using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DunGen;
using GameNetcodeStuff;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Facility.Stash;
namespace Y4NGZCompany.Contracts.Defuse
{
    internal sealed class DefuseDiagnosticSnapshot
    {
        internal readonly string FamilyName;
        internal readonly string DisplayName;
        internal readonly int TotalWires;
        internal readonly string CountSummary;
        internal readonly string WireOrder;
        internal readonly string ProfileBrief;
        internal readonly string Procedure;
        internal readonly int Code;

        internal DefuseDiagnosticSnapshot(
            string familyName,
            string displayName,
            int totalWires,
            string countSummary,
            string wireOrder,
            string profileBrief,
            string procedure,
            int code)
        {
            FamilyName = familyName ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            TotalWires = Mathf.Clamp(totalWires, 0, DefuseRules.MaxWireSlots);
            CountSummary = countSummary ?? string.Empty;
            WireOrder = wireOrder ?? string.Empty;
            ProfileBrief = profileBrief ?? string.Empty;
            Procedure = procedure ?? string.Empty;
            Code = Mathf.Clamp(code, 0, 9999);
        }
    }

    internal static class DefuseContract
    {
        private const string GeneratedPrefabName = "MoonContracts_DefuseBombGeneratedPrefab";
        private const string AuthoringPreviewPrefabName = "P_DeadDrop_BombPreview";
        private const string AsgsBriefcaseBaseName = "Clean ASGS Briefcase Base";
        private const string DefuseModularKitName = "Dead Drop Modular Kit";
        private const float AsgsBriefcaseTinyBoundsThreshold = 0.08f;
        private const float AsgsBriefcaseTargetMaxBounds = 0.60f;
        private const float AsgsBriefcaseMaxRuntimeScaleRepair = 100f;
        private const float AsgsBriefcaseBaseYawOffsetDegrees = 0f;
        private const float BundledBombVisualPitchOffsetDegrees = -90f;

        private const float BundledBombVisualRollOffsetDegrees = 0f;
        private const float DefuseModularKitPitchOffsetDegrees = 0f;
        private const float DefuseModularKitYawOffsetDegrees = 0f;
        private const float DefuseModularKitRollOffsetDegrees = 0f;
        private static GameObject _bombPrefab;
        private static bool _spawnAttempted;
        private static float _nextSpawnAttemptAt;
        private static bool _floorSearchStarted;

        // #716 (WP-B8): per-frame cache for the risk-resolved deadline fraction.
        private static int _deadlineNormalizedFrame = -1;
        private static float _deadlineNormalizedCache = 9f / 16f;
        private static bool _defused;
        private static bool _failed;

        /// <summary>
        /// #688, mirroring Pest Control's #672 latch. Sticky "this client has actually walked up
        /// to the armed case". Objectives are derived locally on every peer, so this is
        /// deliberately per-client: the locate step is a reading aid for the player looking at
        /// the card, not replicated contract state. The bomb spawns server-side right after
        /// landing, so gating on <c>ActiveBomb != null</c> ticked the step before any player had
        /// seen the case.
        ///
        /// It can never stall completion: a solved wire phase or a defusal implies located below,
        /// so a crew that disarms the case without this client ever approaching still finishes.
        /// </summary>
        private static bool _located;

        /// <summary>#1197. The locate row's key, shared with the pity timer's target read.</summary>
        internal const string LocateKey = "defuse.locate";

        /// <summary>#1197. Read-only view of <see cref="_located"/> for the pity timer. It moves on the
        /// same objective poll that ticks the card row, so the two never disagree.</summary>
        internal static bool Located => _located;

        /// <summary>
        /// Distance from the case before it counts as found. Proximity-only, like the pest rig's
        /// ring: a sightline test would leave the step unticked for a player standing over the
        /// case in the dark, which reads as a bug rather than as a rule.
        /// </summary>
        private const float LocateDistanceMeters = 6f;

        private static string _failureReason;
        private static DefuseProfile? _profile;
        private static DefuseDiagnosticSnapshot _diagnostic;
        private static readonly Dictionary<int, Material> GeneratedMaterials = new Dictionary<int, Material>();
        private static Material _briefcaseRuntimeMaterial;
        private static Material _displayRuntimeMaterial;
        private static Shader _visibleRuntimeShader;
        private static bool _loggedMissingRuntimeShader;

        // #337: replacements are cached per source material instance, not per renderer, so a
        // prefab pass and every later instance pass converge on the same HDRP material.
        private static readonly Dictionary<int, Material> HdrpReplacementMaterials = new Dictionary<int, Material>();

        // #337: the authored PBR maps for these surfaces all ship in lgucontracts.bundle, but no
        // material references them - the bundle's *_RuntimeVisible materials were stripped down to
        // a Built-in Unlit/Texture carrying nothing but the albedo. Keyed on the albedo texture
        // rather than the material name because the texture is what actually identifies the
        // surface and it survives a material rename.
        private sealed class BundledSurfaceMaps
        {
            internal string AlbedoTextureName;
            internal string NormalTextureName;
            internal string EmissiveTextureName;
            internal Color EmissiveColor;
            internal float Smoothness;
            internal float Metallic;
        }

        private static readonly BundledSurfaceMaps[] SurfaceMaps =
        {
            // Briefcase shell. The normal map is the whole reason the perforated panel above the
            // marking reads as recessed holes instead of printed rings.
            new BundledSurfaceMaps
            {
                AlbedoTextureName = "T_Briefcase_IED_Color",
                NormalTextureName = "T_Briefcase_IED_Normal_OGL",
                Smoothness = 0.28f,
                Metallic = 0f,
            },
            // Keypad panel. Values lifted from the bundle's own keypad_panelmaterial_standard,
            // which carries all four maps but is itself a Built-in Standard material. Its
            // _EmissionColor is (0, 0.62, 0.547) at HDR intensity 1.5.
            new BundledSurfaceMaps
            {
                AlbedoTextureName = "Panel_albedoo",
                NormalTextureName = "Panel_normale",
                EmissiveTextureName = "Panel_emissive2",
                EmissiveColor = new Color(0f, 0.93f, 0.821f, 1f),
                Smoothness = 0.535f,
                Metallic = 0f,
            },
            new BundledSurfaceMaps
            {
                AlbedoTextureName = "Button_albedo",
                NormalTextureName = "Button_normal",
                Smoothness = 0.4f,
                Metallic = 0f,
            },
        };

        // #337: Lethal Company runs HDRP. A Built-in Render Pipeline shader still *draws* - its
        // single untagged pass is picked up as SRPDefaultUnlit - but it has no DepthForwardOnly,
        // GBuffer, ShadowCaster or MotionVectors pass, so the mesh never reaches the depth prepass
        // and every depth-driven pass (fog, volumetrics, SSAO, contact shadows) treats those pixels
        // as empty space. It also takes no lighting at all, so the surface renders raw albedo at
        // full brightness in a dark facility. That is the reported symptom: the briefcase read as a
        // flat glowing panel that did not sit in the room, right next to HDRP/Lit kit parts that did.
        private static readonly string[] BuiltInPipelineShaderPrefixes =
        {
            "Unlit/", "Standard", "Legacy Shaders/", "Mobile/", "Sprites/", "Particles/",
            "Diffuse", "Specular", "VertexLit", "Reflective/", "Transparent/", "Self-Illumin/",
        };

        private static readonly string[] RenderPipelineShaderPrefixes =
        {
            "HDRP/", "Universal Render Pipeline/", "Shader Graphs/", "TextMeshPro/", "Hidden/",
        };

        internal static DefuseBombController ActiveBomb { get; private set; }

        internal static bool TryGetBlacksiteRetrievalTarget(
            out BlacksiteRetrievalTargetPose target)
        {
            target = default;
            DefuseBombController bomb = ActiveBomb;
            if (bomb == null)
                return false;

            NetworkObject expectedTarget = bomb.GetComponent<NetworkObject>();
            return BlacksiteHardDriveObjectiveService.TryResolveContractTarget(
                bomb.transform,
                expectedTarget,
                "defuse",
                out target);
        }

        internal static void ResetRunState()
        {
            _spawnAttempted = false;
            _nextSpawnAttemptAt = 0f;
            _defused = false;
            _failed = false;
            _located = false;
            _failureReason = null;
            _profile = null;
            _diagnostic = null;
            ActiveBomb = null;
            DefuseElevatedBombPlacer.ResetScanState();
            DefuseFloorBombPlacer.Reset();
            DefuseElevatedBombPlacer.ReleaseReservation();
            _floorSearchStarted = false;
        }

        internal static void RegisterBomb(DefuseBombController bomb)
        {
            if (bomb != null)
                ActiveBomb = bomb;
        }

        internal static void UnregisterBomb(DefuseBombController bomb)
        {
            if (ActiveBomb == bomb)
            {
                ActiveBomb = null;
                DefuseElevatedBombPlacer.ReleaseReservation();
            }
        }

        internal static void UpdateRuntime()
        {
            if (!IsRuntimeActive())
                return;

            EnsureProfile();
            if (_spawnAttempted || ActiveBomb != null)
                return;

            var round = RoundManager.Instance;
            var start = StartOfRound.Instance;
            if (round == null || start == null || !IsServerRuntime() || !start.shipHasLanded || start.inShipPhase || start.shipIsLeaving)
                return;

            if (Time.time < _nextSpawnAttemptAt)
                return;

            _spawnAttempted = true;
            if (!TrySpawnBomb(out bool elevatedScanInProgress))
            {
                _spawnAttempted = false;
                // The spawn path re-runs the full placement ladder; a facility that produced no
                // bomb this attempt will not grow one next frame, so back off well past the frame
                // cadence the way ShadowRaid's procedural container search does. The one
                // exception is a still-running elevated scan: that attempt did no ladder work at
                // all, so it retries on the normal fast cadence until the scan reports complete.
                // That cadence is still throttled: re-entering the ladder every frame costs the
                // scan's whole 8 ms budget per frame for up to the wall-clock valve, which reads
                // as a hitch. A quarter second drains the scan quickly and costs nothing visible.
                if (!elevatedScanInProgress)
                    _nextSpawnAttemptAt = Time.time + 30f;
                else
                    _nextSpawnAttemptAt = Time.time + 0.25f;
            }
        }

        internal static List<MoonContractObjective> GetObjectives()
        {
            bool spawned = ActiveBomb != null;
            bool wireSolved = spawned && ActiveBomb.WirePhaseComplete;
            bool defused = _defused || (spawned && ActiveBomb.IsDefused);
            bool failed = _failed || (spawned && ActiveBomb.IsFailed);
            bool identified = spawned && (ActiveBomb.DiagnosticIdentified || ActiveBomb.CutMask != 0);
            identified |= wireSolved || defused;
            _located |= identified || (spawned && LocalPlayerHasReachedBomb(ActiveBomb));
            int remainingSeconds = GetDeadlineRemainingSeconds();
            // #216: the box shows the countdown itself, not the wall-clock hour the case is
            // set for. A player looking at the HUD mid-round wants "how long have I got",
            // and the deadline hour is already on the terminal's field-support page.
            string timerLabel = defused
                ? "DETONATION \u00B7 SUSPENDED"
                : failed
                    ? "DETONATION \u00B7 CASE LOST"
                    : $"DETONATION \u00B7 {FormatCountdown(remainingSeconds)}";

            return new List<MoonContractObjective>
            {
                // Explicit key: the countdown payload changes every second and must not
                // read as a new objective in the HUD's semantic signature (#520).
                new MoonContractObjective(timerLabel, defused ? 1 : 0, 1, failed, MoonContractObjectiveKind.Status, key: "defuse.timer"),
                new MoonContractObjective("Locate the armed explosive", _located ? 1 : 0, 1, failed, key: LocateKey),
                // #725: plain verbs. The wire order and code both come off the Field Support
                // page, which the briefing already names, so the lines say so instead of
                // "specified" and "input".
                new MoonContractObjective("Ask Field Support on the ship which explosive is armed", identified ? 1 : 0, 1, failed, key: "defuse.identify"),
                new MoonContractObjective("Cut the wires", wireSolved ? 1 : 0, 1, failed, key: "defuse.wires"),
                new MoonContractObjective("Enter the disarm code", defused ? 1 : 0, 1, failed, key: "defuse.keypad"),
            };
        }

        /// <summary>
        /// #688. True once the local player has stood within <see cref="LocateDistanceMeters"/>
        /// of the armed case. Measured against the case's own transform, which replicates with
        /// the networked spawn, so every peer evaluates the same point.
        /// </summary>
        private static bool LocalPlayerHasReachedBomb(DefuseBombController bomb)
        {
            if (bomb == null)
                return false;

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null || player.isPlayerDead || !player.isPlayerControlled)
                return false;

            Vector3 offset = player.transform.position - bomb.transform.position;
            return offset.sqrMagnitude <= LocateDistanceMeters * LocateDistanceMeters;
        }

        internal static void NotifyDefused(DefuseBombController bomb)
        {
            if (bomb != null && ActiveBomb == null)
                ActiveBomb = bomb;

            if (_defused)
                return;

            _defused = true;
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.Defuse] Defuse objective completed.");
        }

        internal static void NotifyFailed(DefuseBombController bomb, string reason)
        {
            if (bomb != null && ActiveBomb == null)
                ActiveBomb = bomb;

            if (_failed)
                return;

            _failed = true;
            _failureReason = string.IsNullOrWhiteSpace(reason) ? "device failed" : reason;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Defuse] Defuse failed ({_failureReason}).");
        }

        internal static string GetFieldSupportStatusText()
        {
            MoonContractType current = MoonContractState.GetCurrentContract();
            if (current != MoonContractType.Defuse)
                return "No Defuse case is attached to the current route.";

            EnsureProfile();

            string status = _failed
                ? "FAILED"
                : _defused
                    ? "DEFUSED"
                    : ActiveBomb != null
                        ? "ACTIVE"
                        : "PENDING SIGNAL";

            string code = _diagnostic != null && _profile.HasValue ? _profile.Value.Code.ToString("D4") : "----";
            string diagnostic = _diagnostic != null ? "COMPLETE" : "PENDING";
            return
                "Defuse case file is active.\n\n" +
                $"Status: {status}\n" +
                $"Deadline: {FormatDeadlineClock()}\n" +
                $"Diagnostic: {diagnostic}\n" +
                $"Briefcase code: {code}\n\n" +
                "Complete the Defuse Diagnostic before any wire is cut.";
        }

        internal static bool IsDeadlineExpired()
        {
            if (_defused || _failed)
                return false;

            TimeOfDay time = TimeOfDay.Instance;
            if (time == null || time.totalTime <= 0f)
                return false;

            return time.currentDayTime >= time.totalTime * GetDeadlineNormalizedTime();
        }

        /// <summary>
        /// The configured deadline fraction, resolved at most once per frame. #716 (WP-B8): the
        /// bomb's Update calls <see cref="IsDeadlineExpired"/> and
        /// <see cref="GetDeadlineRemainingSeconds"/> every frame, and the terminal renders on the
        /// same frame, so the risk-resolved settings struct was rebuilt three or more times a
        /// frame. The balance object exposes no change event, so the cache is keyed on
        /// <c>Time.frameCount</c>: a config reload is picked up on the very next frame.
        /// </summary>
        private static float GetDeadlineNormalizedTime()
        {
            if (_deadlineNormalizedFrame == Time.frameCount)
                return _deadlineNormalizedCache;

            _deadlineNormalizedFrame = Time.frameCount;
            _deadlineNormalizedCache = ContractsBootstrap.DefuseBalance?.GetEffective().DeadlineNormalizedTime ?? (9f / 16f);
            return _deadlineNormalizedCache;
        }

        /// <summary>
        /// Real seconds until detonation, not day-time units.
        /// </summary>
        /// <remarks>
        /// This used to return the raw <c>deadline - currentDayTime</c> difference and render
        /// it as MM:SS, which ticks at the wrong rate on any moon whose day does not run at
        /// 1x. Vanilla drives the clock as
        /// <c>currentDayTime = (globalTime + OffsetFromGlobalTime) * DaySpeedMultiplier</c>
        /// with <c>globalTime += Time.deltaTime * globalTimeSpeedMultiplier</c>, so day-time
        /// units advance at <c>globalTimeSpeedMultiplier * DaySpeedMultiplier</c> per real
        /// second. Dividing by that product is what makes the HUD countdown an honest clock.
        /// </remarks>
        internal static int GetDeadlineRemainingSeconds()
        {
            TimeOfDay time = TimeOfDay.Instance;
            if (time == null || time.totalTime <= 0f)
                return 0;

            float deadline = time.totalTime * GetDeadlineNormalizedTime();
            float remaining = Mathf.Max(0f, deadline - time.currentDayTime);

            float dayTimeUnitsPerSecond = time.globalTimeSpeedMultiplier
                                          * (StartOfRound.Instance?.currentLevel?.DaySpeedMultiplier ?? 1f);
            if (dayTimeUnitsPerSecond > 0.0001f)
                remaining /= dayTimeUnitsPerSecond;

            return Mathf.CeilToInt(remaining);
        }

        /// <summary>
        /// The configured deadline as an in-game clock time. #716 (WP-E12): the terminal used to
        /// print a hard-coded "5:00 PM (mm:ss)" whatever <c>DeadlineNormalizedTime</c> said, which
        /// contradicted both the config and the HUD countdown. The arithmetic is vanilla
        /// <c>HUDManager.GetClockTimeFormatted</c>'s verbatim (06:00 origin, 12-hour + AM/PM), so
        /// the line reads as the same clock the crew sees; it is reproduced here rather than
        /// called so the terminal never mutates the live HUD's shared <c>amPM</c>/<c>newLine</c>
        /// fields.
        /// </summary>
        internal static string FormatDeadlineClock()
        {
            TimeOfDay time = TimeOfDay.Instance;
            if (time == null)
                return "--:--";

            int totalMinutes = (int)(GetDeadlineNormalizedTime() * (60f * time.numberOfHours)) + 360;
            int hour = totalMinutes / 60;
            if (hour >= 24)
                return "12:00 AM";

            string suffix = hour < 12 ? "AM" : "PM";
            if (hour > 12)
                hour %= 12;

            return $"{hour:00}:{totalMinutes % 60:00}".TrimStart('0') + " " + suffix;
        }

        internal static string FormatCountdown(int seconds)
        {
            seconds = Mathf.Max(0, seconds);
            int minutes = seconds / 60;
            int remainder = seconds % 60;
            return $"{minutes:00}:{remainder:00}";
        }

        internal static string GetManualText()
        {
            return DefuseRules.FormatManual();
        }

        internal static string GetDiagnosticInfoText()
        {
            MoonContractType current = MoonContractState.GetCurrentContract();
            if (current != MoonContractType.Defuse)
                return "No Defuse case is attached to the current route.";

            EnsureProfile();
            if (_diagnostic == null)
            {
                return
                    "No Defuse diagnostic has been completed.\n\n" +
                    "Briefcase code: ----\n\n" +
                    "Run Defuse Diagnostic and submit the observed marking, display, wire counts, and left-to-right wire order to unlock the cut procedure and briefcase code.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("Defuse diagnostic complete.");
            sb.AppendLine();
            sb.AppendLine($"Profile: {_diagnostic.ProfileBrief}");
            sb.AppendLine($"Observed marking: {_diagnostic.FamilyName}");
            sb.AppendLine($"Observed display: {_diagnostic.DisplayName}");
            sb.AppendLine($"Wire counts: {_diagnostic.CountSummary}");
            sb.AppendLine($"Wire order: {_diagnostic.WireOrder}");
            sb.AppendLine();
            sb.AppendLine($"Cut procedure: {_diagnostic.Procedure}");
            sb.AppendLine($"Briefcase code: {_diagnostic.Code:D4}");
            sb.AppendLine();
            sb.Append("Run Defuse Diagnostic again to replace this report.");
            return sb.ToString();
        }

        internal static string SubmitDiagnostic(
            string familyName,
            string displayName,
            int redCount,
            int blueCount,
            int yellowCount,
            int greenCount,
            int whiteCount,
            int blackCount,
            int totalWires,
            string wireOrderCsv)
        {
            MoonContractType current = MoonContractState.GetCurrentContract();
            if (current != MoonContractType.Defuse)
                return "DIAGNOSTIC REJECTED\nNo Defuse case is attached to the current route.";

            EnsureProfile();

            if (!TryParseFamily(familyName, out DefuseBombFamily family))
                return "DIAGNOSTIC REJECTED\nUnknown marking sticker.";

            if (!TryParseDisplay(displayName, out DefuseDisplayState display))
                return "DIAGNOSTIC REJECTED\nUnknown display text.";

            int[] counts =
            {
                Mathf.Max(0, redCount),
                Mathf.Max(0, blueCount),
                Mathf.Max(0, yellowCount),
                Mathf.Max(0, greenCount),
                Mathf.Max(0, whiteCount),
                Mathf.Max(0, blackCount),
            };

            int clampedTotal = Mathf.Clamp(totalWires, 0, DefuseRules.MaxWireSlots);
            int submittedTotal = counts.Sum();
            if (submittedTotal != clampedTotal)
                return $"DIAGNOSTIC REJECTED\nWire color counts total {submittedTotal}, but total wires was {clampedTotal}.";

            if (!TryParseWireOrder(wireOrderCsv, clampedTotal, out DefuseWireColor[] wireOrder, out string wireReason))
                return "DIAGNOSTIC REJECTED\n" + wireReason;

            if (!DoCountsMatchOrder(counts, wireOrder, out string countReason))
                return "DIAGNOSTIC REJECTED\n" + countReason;

            List<DefuseArchetype> matches = DefuseRules.Archetypes
                .Where(archetype => DiagnosticMatches(archetype, family, display, counts, wireOrder))
                .ToList();

            if (matches.Count == 0)
                return "DIAGNOSTIC REJECTED\nNo known Defuse profile matches those observations.";

            if (matches.Count > 1)
                return "DIAGNOSTIC REJECTED\nThose observations match multiple Defuse profiles. Re-check the wire order.";

            DefuseArchetype match = matches[0];
            DefuseProfile profile = EnsureProfile();
            _diagnostic = new DefuseDiagnosticSnapshot(
                DefuseRules.GetFamilyName(match.Family),
                DefuseRules.GetDisplayName(match.DisplayState),
                clampedTotal,
                FormatCountSummary(counts),
                FormatWireOrder(wireOrder),
                DefuseRules.FormatArchetypeBrief(match),
                match.Procedure,
                profile.Code);

            if (ActiveBomb != null && ActiveBomb.IsSpawned)
                ActiveBomb.IdentifyDiagnosticServerRpc(Array.IndexOf(DefuseRules.Archetypes, match));
            return "DIAGNOSTIC SAVED\nDiagnostic Info has been updated.";
        }

        internal static string GetActiveProfileSummary()
        {
            EnsureProfile();
            if (!_profile.HasValue)
                return "No active Defuse profile.";

            DefuseArchetype archetype = DefuseRules.GetArchetype(_profile.Value.ArchetypeIndex);
            return $"{DefuseRules.FormatArchetypeBrief(archetype)} / code {_profile.Value.Code:D4}";
        }

        internal static GameObject CreatePlacementPreview()
        {
            try
            {
                ContractAssetLoader.Load();
                GameObject prefab = EnsureBombPrefab();
                if (prefab == null)
                    return null;

                GameObject instance = UnityEngine.Object.Instantiate(prefab);
                if (instance == null)
                    return null;

                instance.name = "DefusePlacementPreview";
                if (!instance.activeSelf)
                    instance.SetActive(true);

                RepairBundledBombVisuals(instance, "placement-preview");
                DefuseBombController controller = instance.GetComponent<DefuseBombController>();
                controller?.ConfigurePlacementPreview(0);
                PreparePlacementPreviewInstance(instance);
                return instance;
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Defuse] Failed to create placement preview: {ex.Message}");
                return null;
            }
        }

        private static bool IsRuntimeActive()
        {
            return MoonContractState.GetCurrentContract() == MoonContractType.Defuse;
        }

        private static bool IsServerRuntime()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null && manager.IsServer)
                return true;

            RoundManager round = RoundManager.Instance;
            if (round != null && round.IsServer)
                return true;

            return NetRole.IsServerOrHost();
        }

        /// <remarks>
        /// #217: the profile is only CACHED once the round's map seed exists. Everything that
        /// varies the bomb — marking, display state, wire layout, code — comes from this one
        /// roll, and <c>ResetRunState</c> clears it from the <c>SetShipReadyToLand</c> postfix
        /// while the ship is still in orbit. A terminal read (field support, diagnostic info)
        /// in that window used to lock the round's profile against a stale-or-zero
        /// <c>randomMapSeed</c>; with <c>StartOfRound.Instance</c> not yet up the seed
        /// degenerates to the constant 7307, which rolls archetype 0 — KILO — every single
        /// time. An uncached roll is still returned so the terminal has something to show.
        /// </remarks>
        private static DefuseProfile EnsureProfile()
        {
            if (_profile.HasValue)
                return _profile.Value;

            DefuseProfile profile = DefuseRules.BuildProfileForCurrentRound();
            if (!DefuseRules.IsRoundSeedAvailable())
            {
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.Defuse] Profile requested before the round seed exists; returning a provisional roll without caching it.");
                return profile;
            }

            _profile = profile;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Defuse] Effective settings: {ContractsBootstrap.DefuseBalance?.BuildAuditLine() ?? "defaults"}; " +
                $"profile={profile.ArchetypeIndex} ({DefuseRules.FormatArchetypeBrief(DefuseRules.GetArchetype(profile.ArchetypeIndex))}).");
            return profile;
        }

        private static bool DiagnosticMatches(
            DefuseArchetype archetype,
            DefuseBombFamily family,
            DefuseDisplayState display,
            int[] counts,
            DefuseWireColor[] wireOrder)
        {
            if (archetype == null || counts == null || wireOrder == null)
                return false;

            if (archetype.Family != family || archetype.DisplayState != display)
                return false;

            if (archetype.WireCount != wireOrder.Length)
                return false;

            for (int i = 0; i < wireOrder.Length; i++)
            {
                if (i >= archetype.Wires.Length || archetype.Wires[i] != wireOrder[i])
                    return false;
            }

            return CountWires(archetype.Wires, archetype.WireCount, DefuseWireColor.Red) == counts[0]
                   && CountWires(archetype.Wires, archetype.WireCount, DefuseWireColor.Blue) == counts[1]
                   && CountWires(archetype.Wires, archetype.WireCount, DefuseWireColor.Yellow) == counts[2]
                   && CountWires(archetype.Wires, archetype.WireCount, DefuseWireColor.Green) == counts[3]
                   && CountWires(archetype.Wires, archetype.WireCount, DefuseWireColor.White) == counts[4]
                   && CountWires(archetype.Wires, archetype.WireCount, DefuseWireColor.Black) == counts[5];
        }

        private static bool TryParseFamily(string value, out DefuseBombFamily family)
        {
            switch (NormalizeDiagnosticToken(value))
            {
                case "KILO":
                    family = DefuseBombFamily.Kilo;
                    return true;
                case "MANTA":
                    family = DefuseBombFamily.Manta;
                    return true;
                case "RUST":
                    family = DefuseBombFamily.Rust;
                    return true;
                case "OMEGA":
                    family = DefuseBombFamily.Omega;
                    return true;
                case "TYPE7":
                    family = DefuseBombFamily.Type7;
                    return true;
                default:
                    family = default;
                    return false;
            }
        }

        private static bool TryParseDisplay(string value, out DefuseDisplayState display)
        {
            switch (NormalizeDiagnosticToken(value))
            {
                case "ARMED":
                    display = DefuseDisplayState.Armed;
                    return true;
                case "ERROR":
                    display = DefuseDisplayState.Error;
                    return true;
                case "LOCKED":
                    display = DefuseDisplayState.Locked;
                    return true;
                default:
                    display = default;
                    return false;
            }
        }

        private static bool TryParseWireOrder(string wireOrderCsv, int totalWires, out DefuseWireColor[] wireOrder, out string reason)
        {
            wireOrder = Array.Empty<DefuseWireColor>();
            reason = string.Empty;

            if (totalWires < 0 || totalWires > DefuseRules.MaxWireSlots)
            {
                reason = "Total wire count is outside the supported range.";
                return false;
            }

            string[] parts = string.IsNullOrWhiteSpace(wireOrderCsv)
                ? Array.Empty<string>()
                : wireOrderCsv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != totalWires)
            {
                reason = $"Wire order listed {parts.Length} wire(s), but total wires was {totalWires}.";
                return false;
            }

            wireOrder = new DefuseWireColor[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!TryParseWireColor(parts[i], out DefuseWireColor color))
                {
                    reason = $"Unknown wire color at slot {i + 1}.";
                    wireOrder = Array.Empty<DefuseWireColor>();
                    return false;
                }

                wireOrder[i] = color;
            }

            return true;
        }

        private static bool TryParseWireColor(string value, out DefuseWireColor color)
        {
            switch (NormalizeDiagnosticToken(value))
            {
                case "RED":
                    color = DefuseWireColor.Red;
                    return true;
                case "BLUE":
                    color = DefuseWireColor.Blue;
                    return true;
                case "YELLOW":
                    color = DefuseWireColor.Yellow;
                    return true;
                case "GREEN":
                    color = DefuseWireColor.Green;
                    return true;
                case "BLACK":
                    color = DefuseWireColor.Black;
                    return true;
                case "WHITE":
                    color = DefuseWireColor.White;
                    return true;
                default:
                    color = default;
                    return false;
            }
        }

        private static bool DoCountsMatchOrder(int[] counts, DefuseWireColor[] wireOrder, out string reason)
        {
            reason = string.Empty;
            if (counts == null || counts.Length < 6 || wireOrder == null)
            {
                reason = "Diagnostic counts were incomplete.";
                return false;
            }

            DefuseWireColor[] colors =
            {
                DefuseWireColor.Red,
                DefuseWireColor.Blue,
                DefuseWireColor.Yellow,
                DefuseWireColor.Green,
                DefuseWireColor.White,
                DefuseWireColor.Black,
            };

            for (int i = 0; i < colors.Length; i++)
            {
                int observed = CountWires(wireOrder, wireOrder.Length, colors[i]);
                if (observed == counts[i])
                    continue;

                reason = $"{DefuseRules.GetWireName(colors[i])} count was {counts[i]}, but wire order contains {observed}.";
                return false;
            }

            return true;
        }

        private static int CountWires(DefuseWireColor[] wires, int wireCount, DefuseWireColor color)
        {
            int count = 0;
            int limit = Mathf.Min(wireCount, wires?.Length ?? 0);
            for (int i = 0; i < limit; i++)
                if (wires[i] == color)
                    count++;
            return count;
        }

        private static string FormatCountSummary(int[] counts)
        {
            if (counts == null || counts.Length < 6)
                return "unknown";

            return $"red {counts[0]}, blue {counts[1]}, yellow {counts[2]}, green {counts[3]}, white {counts[4]}, black {counts[5]}";
        }

        private static string FormatWireOrder(DefuseWireColor[] wireOrder)
        {
            if (wireOrder == null || wireOrder.Length == 0)
                return "none";

            return string.Join(", ", wireOrder.Select(DefuseRules.GetWireName).ToArray());
        }

        private static string NormalizeDiagnosticToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return new string(value
                .Trim()
                .ToUpperInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());
        }

        private static bool TrySpawnBomb(out bool elevatedScanInProgress)
        {
            if (!TryResolveSpawnPose(
                    out Vector3 position,
                    out Quaternion rotation,
                    out string source,
                    out AuthoredInteriorPlacementRecord authoredRecord,
                    out elevatedScanInProgress))
            {
                // An in-flight elevated scan is not a placement failure: it is "not yet", and
                // the caller retries on the normal tick cadence instead of backing off.
                if (!elevatedScanInProgress)
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.Defuse] Could not resolve an interior spawn pose for the Defuse bomb.");
                return false;
            }

            // Every placement tier owns the same claim. Keep it only after a successful spawn.
            bool spawned = false;
            GameObject instance = null;
            try
            {

                GameObject prefab = EnsureBombPrefab();
                if (prefab == null)
                {
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        authoredRecord,
                        "consumer=dead-drop-bomb reason=prefab-unavailable");
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.Defuse] Defuse bomb prefab unavailable.");
                    return false;
                }

                instance = UnityEngine.Object.Instantiate(prefab, position, rotation);
                if (instance == null)
                {
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        authoredRecord,
                        "consumer=dead-drop-bomb reason=instantiate-returned-null");
                    return false;
                }

                instance.name = "DefuseBomb";
                instance.transform.localScale = prefab.transform.localScale * DefuseBalanceConfig.BombScale;
                RepairBundledBombVisuals(instance, "spawned");
                EnsureRuntimeComponents(instance);
                if (!instance.activeSelf)
                    instance.SetActive(true);

                NetworkObject networkObject = instance.GetComponent<NetworkObject>();
                if (networkObject == null)
                {
                    ContractsBootstrap.Log?.LogError("[MoonContracts.Defuse] Defuse bomb prefab has no NetworkObject; the bomb would exist on the host only. Aborting spawn.");
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        authoredRecord,
                        "consumer=dead-drop-bomb reason=prefab-missing-network-object");
                    return false;
                }

                if (!networkObject.IsSpawned)
                {
                    networkObject.Spawn();
                    if (StartOfRound.Instance?.propsContainer != null)
                        networkObject.TrySetParent(StartOfRound.Instance.propsContainer);
                }

                DefuseBombController controller = instance.GetComponent<DefuseBombController>();
                ActiveBomb = controller;
                controller?.ConfigureServer(EnsureProfile());
                AuthoredPlacementRoundReport.RecordSpawned(authoredRecord);
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Defuse] Spawned Defuse bomb via {source} at {FormatVector(position)}.");
                spawned = true;
                return true;
            }
            finally
            {
                if (!spawned)
                {
                    DefuseElevatedBombPlacer.ReleaseReservation();
                    if (instance != null)
                    {
                        var network = instance.GetComponent<NetworkObject>();
                        if (network != null && network.IsSpawned) network.Despawn();
                        else UnityEngine.Object.Destroy(instance);
                    }
                }
            }
        }

        /// <summary>
        /// Plugin init, every peer. The bomb template used to be prepared lazily from the host-only spawn
        /// path, so clients never registered the prefab before NetworkManager started (#454). Idempotent:
        /// <see cref="EnsureBombPrefab"/> latches on <c>_bombPrefab</c>.
        /// </summary>
        internal static void PrepareNetworkPrefab()
        {
            try
            {
                GameObject prefab = EnsureBombPrefab();
                ContractAssetLoader.LogRegisteredPrefab(
                    "[MoonContracts.Defuse]",
                    prefab,
                    ContractAssetLoader.IsInteriorSupportPrefabRegistered(prefab));
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts.Defuse] Failed to prepare the Defuse bomb network prefab at init: {e}");
            }
        }

        private static GameObject EnsureBombPrefab()
        {
            if (_bombPrefab != null)
                return _bombPrefab;

            string assetName = DefuseBalanceConfig.BombPrefabName;
            if (string.IsNullOrWhiteSpace(assetName))
                assetName = AuthoringPreviewPrefabName;

            if (!string.Equals(assetName, "generated", StringComparison.OrdinalIgnoreCase))
            {
                GameObject prefab = ContractAssetLoader.GetPreparedInteriorSupportPrefab(assetName);
                string reason = string.Empty;
                if (prefab != null && IsBundledBombPrefabUsable(prefab, out reason))
                {
                    RepairBundledBombVisuals(prefab, "prefab");
                    EnsureRuntimeComponents(prefab);
                    _bombPrefab = prefab;
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Defuse] Using bundled Defuse bomb prefab '{assetName}'.");
                    return _bombPrefab;
                }

                if (prefab == null)
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Defuse] Bundled bomb prefab '{assetName}' was not found; using generated runtime visual.");
                }
                else
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Defuse] Bundled bomb prefab '{assetName}' is not suitable for runtime ({reason}); using generated runtime visual.");
                }
            }

            _bombPrefab = CreateGeneratedBombPrefab();
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.Defuse] Using generated Defuse runtime visual.");
            return _bombPrefab;
        }

        private static bool IsBundledBombPrefabUsable(GameObject prefab, out string reason)
        {
            reason = string.Empty;
            if (prefab == null)
            {
                reason = "missing prefab";
                return false;
            }

            int rendererCount = prefab.GetComponentsInChildren<Renderer>(includeInactive: true).Length;
            if (rendererCount <= 0)
            {
                reason = "no renderers";
                return false;
            }

            if (!HasAsgsBriefcaseBase(prefab))
            {
                reason = "missing clean ASGS briefcase base";
                return false;
            }

            if (!HasRequiredKeypadButtons(prefab, out reason))
                return false;

            if (!HasTransformNamed(prefab, "DisplayText"))
            {
                reason = "missing keypad DisplayText";
                return false;
            }

            if (!HasRequiredWireAnchors(prefab, out reason))
                return false;

            return true;
        }

        private static GameObject CreateGeneratedBombPrefab()
        {
            // Assembled active: the bounds collider and visual repair passes read renderer bounds, which only
            // resolve on an active hierarchy. The root is parked under the hidden holder at the end instead.
            GameObject root = new GameObject(GeneratedPrefabName);

            CreateBox(root.transform, "CaseBase", new Vector3(0f, 0.13f, 0f), new Vector3(1.15f, 0.25f, 0.72f), new Color(0.045f, 0.044f, 0.048f));
            CreateBox(root.transform, "CaseLid", new Vector3(0f, 0.72f, 0.36f), new Vector3(1.15f, 0.78f, 0.08f), new Color(0.055f, 0.052f, 0.048f));
            CreateBox(root.transform, "CaseLidInterior", new Vector3(0f, 0.72f, 0.407f), new Vector3(1.02f, 0.64f, 0.014f), new Color(0.56f, 0.49f, 0.34f));
            CreateBox(root.transform, "ExplosiveBlockLeft", new Vector3(-0.31f, 0.30f, 0.08f), new Vector3(0.28f, 0.12f, 0.46f), new Color(0.78f, 0.76f, 0.70f));
            CreateBox(root.transform, "ExplosiveBlockRight", new Vector3(0.34f, 0.30f, 0.08f), new Vector3(0.28f, 0.12f, 0.46f), new Color(0.78f, 0.76f, 0.70f));
            CreateBox(root.transform, "ExplosiveBandLeft", new Vector3(-0.31f, 0.372f, 0.08f), new Vector3(0.30f, 0.026f, 0.50f), new Color(0.035f, 0.033f, 0.032f));
            CreateBox(root.transform, "ExplosiveBandRight", new Vector3(0.34f, 0.372f, 0.08f), new Vector3(0.30f, 0.026f, 0.50f), new Color(0.035f, 0.033f, 0.032f));
            CreateBox(root.transform, "DD_StickerSlot_Base_OffWhite", new Vector3(-0.28f, 0.83f, 0.421f), new Vector3(0.38f, 0.13f, 0.012f), new Color(0.93f, 0.89f, 0.73f));

            string[] labels = { "KILO", "MANTA", "RUST", "OMEGA", "TYPE_7" };
            for (int i = 0; i < labels.Length; i++)
            {
                GameObject label = CreateBox(root.transform, "DD_Marking_" + labels[i], new Vector3(-0.28f, 0.825f, 0.431f + i * 0.0005f), new Vector3(0.26f, 0.035f, 0.012f), Color.black);
                if (i != 0)
                    label.SetActive(false);
            }

            CreateBox(root.transform, "Keypad", new Vector3(-0.18f, 0.34f, -0.17f), new Vector3(0.30f, 0.08f, 0.30f), new Color(0.018f, 0.021f, 0.021f));
            CreateBox(root.transform, "DD_DisplayOverlay_Glass", new Vector3(-0.18f, 0.39f, -0.06f), new Vector3(0.22f, 0.018f, 0.075f), new Color(0.02f, 0.38f, 0.12f));
            string[] displays = { "ARMED", "ERROR", "LOCKED" };
            for (int i = 0; i < displays.Length; i++)
            {
                GameObject display = CreateBox(root.transform, "DD_DisplayState_" + displays[i], new Vector3(-0.18f, 0.405f, -0.06f + i * 0.0004f), new Vector3(0.16f, 0.014f, 0.028f), new Color(0.3f, 1f, 0.42f));
                if (i != 0)
                    display.SetActive(false);
            }

            CreateBox(root.transform, "DD_KeypadTerminalBlock", new Vector3(0.11f, 0.38f, -0.18f), new Vector3(0.46f, 0.05f, 0.055f), new Color(0.015f, 0.015f, 0.014f));
            CreateBox(root.transform, "DD_DetonatorBus", new Vector3(0.11f, 0.43f, 0.20f), new Vector3(0.52f, 0.05f, 0.055f), new Color(0.035f, 0.033f, 0.032f));

            for (int i = 0; i < DefuseRules.MaxWireSlots; i++)
            {
                float x = -0.06f + i * 0.085f;
                Color color = DefuseRules.GetWireColor((DefuseWireColor)Mathf.Min(i, 4));
                CreateBox(root.transform, $"DD_WireSlot_{i + 1:00}_Intact", new Vector3(x, 0.44f, 0.01f), new Vector3(0.026f, 0.026f, 0.36f), color);
                GameObject cutA = CreateBox(root.transform, $"DD_WireSlot_{i + 1:00}_CutA", new Vector3(x, 0.44f, -0.08f), new Vector3(0.026f, 0.026f, 0.15f), color);
                GameObject cutB = CreateBox(root.transform, $"DD_WireSlot_{i + 1:00}_CutB", new Vector3(x, 0.44f, 0.12f), new Vector3(0.026f, 0.026f, 0.15f), color);
                cutA.SetActive(false);
                cutB.SetActive(false);
            }

            EnsureRuntimeComponents(root);
            ContractAssetLoader.PrepareRuntimeInteriorSupportPrefab(root, GeneratedPrefabName);
            ContractAssetLoader.ParkHiddenPrefabRoot(root);
            return root;
        }

        private static GameObject CreateBox(Transform parent, string name, Vector3 localPosition, Vector3 localScale, Color color)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent, false);
            box.transform.localPosition = localPosition;
            box.transform.localScale = localScale;
            Renderer renderer = box.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = GetGeneratedMaterial(color, emissive: false);

            Collider collider = box.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.Destroy(collider);

            return box;
        }

        private static Material GetGeneratedMaterial(Color color, bool emissive)
        {
            Color32 packed = color;
            int key = (packed.r << 24) | (packed.g << 16) | (packed.b << 8) | packed.a;
            if (emissive)
                key ^= unchecked((int)0x80000000);

            if (GeneratedMaterials.TryGetValue(key, out Material existing) && existing != null)
                return existing;

            Shader shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            Material material = new Material(shader);
            material.hideFlags = HideFlags.HideAndDontSave;
            material.enableInstancing = true;
            material.color = color;
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            if (material.HasProperty("_UnlitColor"))
                material.SetColor("_UnlitColor", color);
            if (emissive)
            {
                if (material.HasProperty("_EmissionColor"))
                    material.SetColor("_EmissionColor", color);
                if (material.HasProperty("_EmissiveColor"))
                    material.SetColor("_EmissiveColor", color);
            }

            GeneratedMaterials[key] = material;
            return material;
        }

        private static void EnsureRuntimeComponents(GameObject root)
        {
            if (root == null)
                return;

            DefuseBombController bomb = root.GetComponent<DefuseBombController>();
            if (bomb == null)
                bomb = root.AddComponent<DefuseBombController>();

            DefuseInteractor interactor = root.GetComponent<DefuseInteractor>();
            if (interactor == null)
                interactor = root.AddComponent<DefuseInteractor>();
            interactor.Bomb = bomb;
            interactor.InteractRadius = ContractsBootstrap.DefuseBalance?.GetEffective().InteractDistanceMeters ?? 2.8f;

            CompanyStashKeypadInteractor keypad = root.GetComponentInChildren<CompanyStashKeypadInteractor>(true);
            if (keypad == null)
            {
                Transform keypadHost = FindSharedKeypadHost(root) ?? root.transform;
                keypad = keypadHost.gameObject.AddComponent<CompanyStashKeypadInteractor>();
            }
            // The keypad resolves its owner from the parent chain since #393; `bomb` is the
            // ICompanyStashKeypadClient it will find walking up from `keypadHost`.
            keypad.EnsureBuilt();
            keypad.RefreshButtons();

            int keypadButtonNodes = keypad.BuiltButtonCount;
            if (keypadButtonNodes < 11)
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Defuse] Shared keypad bound {keypadButtonNodes}/11 physical button interaction node(s). Expected bttn0-bttn9 plus bttnEnter on the Defuse Keypad.");
            else
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Defuse] Shared keypad bound {keypadButtonNodes}/11 physical button interaction node(s).");

            if (root.GetComponent<BoxCollider>() == null)
                AddBoundsCollider(root);

            // No NetworkObject here: this also runs on the spawned instance, where an added component would
            // leave GlobalObjectIdHash at 0 and the bomb host-only. ContractAssetLoader's interior-support
            // prep owns the NetworkObject, its stable hash and the NetworkTransform, on the prefab (#454).
        }

        private static void RepairBundledBombVisuals(GameObject root, string context)
        {
            if (root == null)
                return;

            // #716 (WP-A4): the repair is latched with a marker component on the root. The prefab
            // is repaired once and Instantiate copies the marker along with the repaired shared
            // materials, local scales and active states, so the spawned bomb and the placement
            // preview no longer redo a ~65-renderer, ~20-property-call-per-material pass on the
            // frame they appear. A generated (non-ASGS) rig latches too: it has nothing to repair,
            // and the marker skips its hierarchy walk on every later clone.
            if (root.GetComponent<DefuseVisualsRepairMarker>() != null)
                return;

            root.AddComponent<DefuseVisualsRepairMarker>();

            if (!HasAsgsBriefcaseBase(root))
                return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            int baseRenderers = 0;
            int baseMeshFilters = 0;
            int baseMeshVertices = 0;
            int materialSlotsRepaired = 0;
            int builtInSlotsRebound = 0;
            int customRenderers = 0;
            string baseMaterialSummary = string.Empty;
            string builtInRepairSummary = string.Empty;
            string baseBoundsSummary = string.Empty;
            string baseScaleRepairSummary;

            CacheVisibleRuntimeShader(root);
            int baseScaleRepairs = RepairTinyAsgsBaseScale(root, renderers, out baseScaleRepairSummary);
            int baseAlignmentRepairs = 0;
            int kitAlignmentRepairs = 0;
            string baseAlignmentSummary = "authored";
            string kitAlignmentSummary = "authored";
            if (baseScaleRepairs > 0)
                RefreshBoundsCollider(root);

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                // #337: run before the base-renderer filter below. The keypad panel and buttons are
                // custom kit renderers, so the ASGS-only repair further down never reached them even
                // though they carry the same stripped Built-in materials the shell does.
                builtInSlotsRebound += RepairBuiltInPipelineMaterials(renderer, ref builtInRepairSummary);
                NormalizeOpaqueMaterials(renderer);
                if (!IsAsgsBriefcaseBaseRenderer(renderer.transform))
                {
                    customRenderers++;
                    continue;
                }

                baseRenderers++;
                ActivatePath(renderer.transform, root.transform);
                renderer.enabled = true;
                try
                {
                    renderer.forceRenderingOff = false;
                    renderer.allowOcclusionWhenDynamic = false;
                }
                catch
                {
                    // Kept for compatibility with older publicized Unity assemblies.
                }

                MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    baseMeshFilters++;
                    baseMeshVertices += meshFilter.sharedMesh.vertexCount;
                }

                if (string.IsNullOrEmpty(baseBoundsSummary))
                    baseBoundsSummary = DescribeBounds(renderer.bounds);

                Material[] materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                    materials = new Material[1];

                if (string.IsNullOrEmpty(baseMaterialSummary))
                    baseMaterialSummary = DescribeMaterials(materials);

                for (int m = 0; m < materials.Length; m++)
                {
                    string materialName = materials[m] != null ? materials[m].name : string.Empty;
                    if (!NeedsRuntimeMaterialReplacement(materials[m]))
                        continue;

                    bool displaySlot = materialName.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0
                                       || materialName.IndexOf("num_pad", StringComparison.OrdinalIgnoreCase) >= 0
                                       || materialName.IndexOf("numpad", StringComparison.OrdinalIgnoreCase) >= 0;
                    Material replacement = displaySlot ? GetDisplayRuntimeMaterial() : GetBriefcaseRuntimeMaterial();
                    if (replacement == null)
                        continue;

                    if (materials[m] != replacement)
                    {
                        materials[m] = replacement;
                        materialSlotsRepaired++;
                    }
                }

                renderer.sharedMaterials = materials;
            }

            if (DefuseBombPresentation.Apply(root))
                RefreshBoundsCollider(root);

            // #716 (WP-F5): a ~500-character diagnostic per spawn belongs behind the debug gate;
            // the one-line Info above it is what a normal log needs.
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Defuse] Visual check {context}: repaired {materialSlotsRepaired} material slot(s) across {renderers.Length} renderer(s).");
            ContractsBootstrap.Log?.LogDebug(
                $"[MoonContracts.Defuse] Visual check {context}: renderers={renderers.Length}, customRenderers={customRenderers}, " +
                $"asgsBaseRenderers={baseRenderers}, asgsBaseMeshes={baseMeshFilters}, asgsBaseVertices={baseMeshVertices}, " +
                $"builtInSlotsRebound={builtInSlotsRebound}, builtInRepair='{(string.IsNullOrEmpty(builtInRepairSummary) ? "none" : builtInRepairSummary)}', " +
                $"materialSlotsRepaired={materialSlotsRepaired}, baseScaleRepairs={baseScaleRepairs}, baseAlignmentRepairs={baseAlignmentRepairs}, " +
                $"kitAlignmentRepairs={kitAlignmentRepairs}, baseScale='{baseScaleRepairSummary}', baseAlignment='{baseAlignmentSummary}', " +
                $"kitAlignment='{kitAlignmentSummary}', baseBounds='{baseBoundsSummary}', baseMaterials='{baseMaterialSummary}'.");
        }

        private static bool HasAsgsBriefcaseBase(GameObject root)
        {
            Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform child = transforms[i];
                if (child != null && child.name.IndexOf(AsgsBriefcaseBaseName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static bool HasRequiredKeypadButtons(GameObject root, out string reason)
        {
            for (int digit = 0; digit <= 9; digit++)
            {
                string name = $"bttn{digit}";
                if (!HasTransformNamed(root, name))
                {
                    reason = $"missing keypad button {name}";
                    return false;
                }
            }

            if (!HasTransformNamed(root, "bttnEnter"))
            {
                reason = "missing keypad button bttnEnter";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool HasRequiredWireAnchors(GameObject root, out string reason)
        {
            for (int slot = 1; slot <= DefuseRules.MaxWireSlots; slot++)
            {
                string start = $"DD_WireSlot_{slot:00}_Anchor_Start";
                string end = $"DD_WireSlot_{slot:00}_Anchor_End";
                if (!HasTransformNameContaining(root, start))
                {
                    reason = $"missing wire anchor {start}";
                    return false;
                }

                if (!HasTransformNameContaining(root, end))
                {
                    reason = $"missing wire anchor {end}";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        private static bool HasTransformNamed(GameObject root, string name)
        {
            return FindTransformNamed(root, name) != null;
        }

        private static Transform FindTransformNamed(GameObject root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
                return null;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform child = transforms[i];
                if (child != null && string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            }

            return null;
        }

        private static bool HasTransformNameContaining(GameObject root, string nameToken)
        {
            if (root == null || string.IsNullOrWhiteSpace(nameToken))
                return false;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform child = transforms[i];
                if (child != null && (child.name ?? string.Empty).IndexOf(nameToken, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static Transform FindSharedKeypadHost(GameObject root)
        {
            if (root == null)
                return null;

            // #716 (WP-A4): one hierarchy walk resolves both names. The two FindTransformNamed
            // calls this replaces each allocated and scanned a full GetComponentsInChildren
            // <Transform> over the bomb rig, on the frame the bomb lands.
            Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: true);
            Transform enter = null;
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform child = transforms[i];
                if (child == null)
                    continue;
                if (string.Equals(child.name, "Defuse Keypad", StringComparison.OrdinalIgnoreCase))
                    return child;
                if (enter == null && string.Equals(child.name, "bttnEnter", StringComparison.OrdinalIgnoreCase))
                    enter = child;
            }

            if (enter == null)
                return null;

            Transform current = enter;
            while (current.parent != null && current.parent != root.transform)
                current = current.parent;
            return current;
        }

        private static int RepairTinyAsgsBaseScale(GameObject root, Renderer[] renderers, out string summary)
        {
            summary = "none";
            if (root == null || renderers == null)
                return 0;

            List<string> repairs = new List<string>();
            HashSet<Transform> repairedAnchors = new HashSet<Transform>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || IsDefuseCustomRenderer(renderer.transform))
                    continue;

                Transform anchor = FindAsgsBaseAnchor(renderer.transform);
                if (anchor == null || repairedAnchors.Contains(anchor))
                    continue;

                Bounds localBounds = ConvertWorldBoundsToLocal(root.transform, renderer.bounds);
                float maxDimension = MaxComponent(localBounds.size);
                if (maxDimension <= 0f || maxDimension >= AsgsBriefcaseTinyBoundsThreshold)
                    continue;

                float scaleFactor = Mathf.Clamp(
                    AsgsBriefcaseTargetMaxBounds / maxDimension,
                    1f,
                    AsgsBriefcaseMaxRuntimeScaleRepair);
                if (scaleFactor <= 1.01f)
                    continue;

                Vector3 previousScale = anchor.localScale;
                anchor.localScale = previousScale * scaleFactor;
                repairedAnchors.Add(anchor);
                repairs.Add($"{anchor.name}:{maxDimension:F4}->{maxDimension * scaleFactor:F3} x{scaleFactor:F1}");
            }

            if (repairs.Count > 0)
                summary = string.Join("; ", repairs.ToArray());
            return repairs.Count;
        }

        private static Transform FindAsgsBaseAnchor(Transform transform)
        {
            Transform current = transform;
            while (current != null)
            {
                string name = current.name ?? string.Empty;
                if (name.IndexOf(AsgsBriefcaseBaseName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return current;

                current = current.parent;
            }

            return null;
        }

        private static int AlignAsgsBriefcaseBase(GameObject root, out string summary)
        {
            summary = "none";
            if (root == null)
                return 0;

            List<string> repairs = new List<string>();
            Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: true);
            Quaternion targetRotation = Quaternion.Euler(
                BundledBombVisualPitchOffsetDegrees,
                AsgsBriefcaseBaseYawOffsetDegrees,
                BundledBombVisualRollOffsetDegrees);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (transform == null || !IsAsgsBriefcaseBaseVisualRoot(transform))
                    continue;

                Vector3 previousEuler = transform.localEulerAngles;
                if (Quaternion.Angle(transform.localRotation, targetRotation) <= 0.1f)
                    continue;

                transform.localRotation = targetRotation;
                Vector3 currentEuler = transform.localEulerAngles;
                repairs.Add(
                    $"{transform.name}:rot=({previousEuler.x:F1},{previousEuler.y:F1},{previousEuler.z:F1})->({currentEuler.x:F1},{currentEuler.y:F1},{currentEuler.z:F1})");
            }

            if (repairs.Count > 0)
                summary = string.Join("; ", repairs.ToArray());
            return repairs.Count;
        }

        private static int AlignDefuseModularKit(GameObject root, out string summary)
        {
            summary = "none";
            if (root == null)
                return 0;

            List<string> repairs = new List<string>();
            Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: true);
            Quaternion targetRotation = Quaternion.Euler(
                DefuseModularKitPitchOffsetDegrees,
                DefuseModularKitYawOffsetDegrees,
                DefuseModularKitRollOffsetDegrees);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (transform == null || !IsDefuseModularKitVisualRoot(transform))
                    continue;

                Vector3 previousEuler = transform.localEulerAngles;
                if (Quaternion.Angle(transform.localRotation, targetRotation) <= 0.1f)
                    continue;

                transform.localRotation = targetRotation;
                Vector3 currentEuler = transform.localEulerAngles;
                repairs.Add(
                    $"{transform.name}:rot=({previousEuler.x:F1},{previousEuler.y:F1},{previousEuler.z:F1})->({currentEuler.x:F1},{currentEuler.y:F1},{currentEuler.z:F1})");
            }

            if (repairs.Count > 0)
                summary = string.Join("; ", repairs.ToArray());
            return repairs.Count;
        }

        private static bool IsAsgsBriefcaseBaseVisualRoot(Transform transform)
        {
            string name = transform != null ? transform.name ?? string.Empty : string.Empty;
            return name.IndexOf(AsgsBriefcaseBaseName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsDefuseModularKitVisualRoot(Transform transform)
        {
            string name = transform != null ? transform.name ?? string.Empty : string.Empty;
            return name.IndexOf(DefuseModularKitName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsDefuseCustomRenderer(Transform transform)
        {
            Transform current = transform;
            while (current != null)
            {
                string name = current.name ?? string.Empty;
                if (name.StartsWith("DD_", StringComparison.OrdinalIgnoreCase)
                    || name.IndexOf(DefuseModularKitName, StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Defuse Keypad", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("KeypadStandard", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static bool IsAsgsBriefcaseBaseRenderer(Transform transform)
        {
            return FindAsgsBaseAnchor(transform) != null;
        }

        private static bool NeedsRuntimeMaterialReplacement(Material material)
        {
            if (material == null)
                return true;

            Shader shader = material.shader;
            string shaderName = shader != null ? shader.name ?? string.Empty : string.Empty;
            return shader == null || shaderName.IndexOf("FallbackError", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// #337: true when the material is bound to a Built-in Render Pipeline shader, which cannot
        /// render correctly under HDRP. TextMeshPro and Hidden/ shaders are deliberately exempt -
        /// rebinding the keypad's text shader would break the display rather than fix it.
        /// </summary>
        private static bool IsBuiltInPipelineMaterial(Material material)
        {
            Shader shader = material != null ? material.shader : null;
            string shaderName = shader != null ? shader.name ?? string.Empty : string.Empty;
            if (shaderName.Length == 0)
                return false;

            for (int i = 0; i < RenderPipelineShaderPrefixes.Length; i++)
            {
                if (shaderName.StartsWith(RenderPipelineShaderPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            for (int i = 0; i < BuiltInPipelineShaderPrefixes.Length; i++)
            {
                if (shaderName.StartsWith(BuiltInPipelineShaderPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// #337: rebinds every Built-in-pipeline material slot on the renderer to an HDRP equivalent
        /// that carries the authored albedo plus the normal/emissive maps the stripped bundle
        /// material dropped. Returns the number of slots repaired.
        /// </summary>
        private static int RepairBuiltInPipelineMaterials(Renderer renderer, ref string repairSummary)
        {
            if (renderer == null)
                return 0;

            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
                return 0;

            // Checked once per renderer rather than per slot: with no HDRP shader available there is
            // nothing to rebind to, and the per-slot path would otherwise log the same warning for
            // every one of the bomb's 65 renderers.
            if (ResolveRuntimeShader() == null)
            {
                if (!_loggedMissingRuntimeShader)
                {
                    _loggedMissingRuntimeShader = true;
                    ContractsBootstrap.Log?.LogWarning(
                        "[MoonContracts.Defuse] No HDRP shader available for material repair; " +
                        "Built-in-pipeline surfaces will keep rendering unlit.");
                }

                return 0;
            }

            int repaired = 0;
            for (int i = 0; i < materials.Length; i++)
            {
                Material source = materials[i];
                if (!IsBuiltInPipelineMaterial(source))
                    continue;

                Material replacement = GetHdrpReplacementMaterial(source);
                if (replacement == null || replacement == source)
                    continue;

                if (string.IsNullOrEmpty(repairSummary))
                    repairSummary = $"{source.name}/{source.shader.name} -> {replacement.shader.name}";

                materials[i] = replacement;
                repaired++;
            }

            if (repaired > 0)
                renderer.sharedMaterials = materials;

            return repaired;
        }

        private static Material GetHdrpReplacementMaterial(Material source)
        {
            if (source == null)
                return null;

            int key = source.GetInstanceID();
            if (HdrpReplacementMaterials.TryGetValue(key, out Material cached) && cached != null)
                return cached;

            Texture albedo = GetFirstMaterialTexture(source, "_MainTex", "_BaseColorMap", "_BaseMap", "_UnlitColorMap");
            BundledSurfaceMaps maps = FindSurfaceMaps(albedo != null ? albedo.name : null);

            Material replacement = CreateRuntimeLitMaterial(
                (source.name ?? "Defuse") + "_HDRP",
                albedo,
                maps != null ? LoadBundleAssetByShortName<Texture2D>(maps.NormalTextureName) : null,
                maps != null ? LoadBundleAssetByShortName<Texture2D>(maps.EmissiveTextureName) : null,
                maps != null ? maps.EmissiveColor : Color.black,
                maps != null ? maps.Smoothness : 0.3f,
                maps != null ? maps.Metallic : 0f,
                albedo != null ? Color.white : GetFirstMaterialColor(source, Color.gray, "_Color", "_BaseColor", "_UnlitColor"));

            HdrpReplacementMaterials[key] = replacement;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Defuse] Rebound Built-in material '{source.name}' ({source.shader?.name}) to " +
                $"'{replacement?.shader?.name ?? "<none>"}': albedo={(albedo != null ? albedo.name : "none")}, " +
                $"normal={(maps != null && !string.IsNullOrEmpty(maps.NormalTextureName) ? maps.NormalTextureName : "none")}, " +
                $"emissive={(maps != null && !string.IsNullOrEmpty(maps.EmissiveTextureName) ? maps.EmissiveTextureName : "none")}.");

            return replacement;
        }

        private static BundledSurfaceMaps FindSurfaceMaps(string albedoTextureName)
        {
            if (string.IsNullOrWhiteSpace(albedoTextureName))
                return null;

            for (int i = 0; i < SurfaceMaps.Length; i++)
            {
                if (string.Equals(SurfaceMaps[i].AlbedoTextureName, albedoTextureName, StringComparison.OrdinalIgnoreCase))
                    return SurfaceMaps[i];
            }

            return null;
        }

        private static Texture GetFirstMaterialTexture(Material material, params string[] propertyNames)
        {
            if (material == null || propertyNames == null)
                return null;

            for (int i = 0; i < propertyNames.Length; i++)
            {
                string propertyName = propertyNames[i];
                if (!material.HasProperty(propertyName))
                    continue;

                Texture texture = material.GetTexture(propertyName);
                if (texture != null)
                    return texture;
            }

            return null;
        }

        private static Color GetFirstMaterialColor(Material material, Color fallback, params string[] propertyNames)
        {
            if (material == null || propertyNames == null)
                return fallback;

            for (int i = 0; i < propertyNames.Length; i++)
            {
                string propertyName = propertyNames[i];
                if (material.HasProperty(propertyName))
                    return material.GetColor(propertyName);
            }

            return fallback;
        }

        private static void ActivatePath(Transform transform, Transform stopAt)
        {
            Transform current = transform;
            while (current != null)
            {
                if (!current.gameObject.activeSelf)
                    current.gameObject.SetActive(true);
                if (current == stopAt)
                    return;
                current = current.parent;
            }
        }

        private static Material GetBriefcaseRuntimeMaterial()
        {
            if (_briefcaseRuntimeMaterial != null)
                return _briefcaseRuntimeMaterial;

            _briefcaseRuntimeMaterial = CreateRuntimeLitMaterial(
                "Defuse_Briefcase_Runtime",
                LoadBundleAssetByShortName<Texture2D>("T_Briefcase_IED_Color"),
                LoadBundleAssetByShortName<Texture2D>("T_Briefcase_IED_Normal_OGL"),
                emissive: null,
                emissiveColor: Color.black,
                smoothness: 0.28f,
                metallic: 0f,
                fallbackColor: new Color(0.09f, 0.075f, 0.055f, 1f));
            return _briefcaseRuntimeMaterial;
        }

        private static Material GetDisplayRuntimeMaterial()
        {
            if (_displayRuntimeMaterial != null)
                return _displayRuntimeMaterial;

            // T_Num_Pad_Display_Emissive really is an emissive map, so it belongs in the emissive
            // slot - unlike the case and keypad normals, which the old shared parameter used to
            // shove into _EmissiveColorMap alongside _NormalMap (#337).
            _displayRuntimeMaterial = CreateRuntimeLitMaterial(
                "Defuse_Display_Runtime",
                LoadBundleAssetByShortName<Texture2D>("T_Num_Pad_Display_Color"),
                normal: null,
                emissive: LoadBundleAssetByShortName<Texture2D>("T_Num_Pad_Display_Emissive"),
                emissiveColor: Color.white,
                smoothness: 0.6f,
                metallic: 0f,
                fallbackColor: new Color(0.05f, 0.28f, 0.10f, 1f));
            return _displayRuntimeMaterial;
        }

        /// <summary>
        /// #337: builds an HDRP-renderable material. HDRP/Lit ignores _NormalMap and
        /// _EmissiveColorMap unless the matching keywords are enabled, so setting the textures
        /// alone - which is all the previous helper did - produced a flat, unlit-looking surface
        /// even when the replacement path ran.
        /// </summary>
        private static Material CreateRuntimeLitMaterial(
            string name,
            Texture albedo,
            Texture normal,
            Texture emissive,
            Color emissiveColor,
            float smoothness,
            float metallic,
            Color fallbackColor)
        {
            Shader shader = ResolveRuntimeShader();
            if (shader == null)
                return null;

            Material material = new Material(shader);
            material.name = name;
            material.hideFlags = HideFlags.HideAndDontSave;
            material.enableInstancing = true;

            SetMaterialTexture(material, albedo, "_BaseColorMap", "_BaseMap", "_MainTex", "_UnlitColorMap");
            SetMaterialColor(material, albedo != null ? Color.white : fallbackColor, "_BaseColor", "_Color", "_UnlitColor");

            if (normal != null)
            {
                SetMaterialTexture(material, normal, "_NormalMap", "_BumpMap");
                SetMaterialFloatIfPresent(material, "_NormalScale", 1f);
                SetMaterialFloatIfPresent(material, "_BumpScale", 1f);
                SetMaterialFloatIfPresent(material, "_NormalMapSpace", 0f);
                material.EnableKeyword("_NORMALMAP");
                material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }

            if (emissive != null || emissiveColor.maxColorComponent > 0f)
            {
                if (emissive != null)
                {
                    SetMaterialTexture(material, emissive, "_EmissiveColorMap", "_EmissionMap");
                    material.EnableKeyword("_EMISSIVE_COLOR_MAP");
                }

                // Intensity is baked into the colour, so HDRP reads _EmissiveColor directly.
                // _EmissiveExposureWeight is deliberately left at HDRP's default, which is what the
                // kit's own DD_Display_Text emissive material uses.
                SetMaterialColor(material, emissiveColor, "_EmissiveColor", "_EmissiveColorLDR", "_EmissionColor");
                SetMaterialFloatIfPresent(material, "_UseEmissiveIntensity", 0f);
                material.EnableKeyword("_EMISSION");
            }

            SetMaterialFloatIfPresent(material, "_Smoothness", Mathf.Clamp01(smoothness));
            SetMaterialFloatIfPresent(material, "_Glossiness", Mathf.Clamp01(smoothness));
            SetMaterialFloatIfPresent(material, "_Metallic", Mathf.Clamp01(metallic));

            NormalizeOpaqueMaterial(material);
            return material;
        }

        private static void CacheVisibleRuntimeShader(GameObject root)
        {
            if (_visibleRuntimeShader != null || root == null)
                return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !IsDefuseCustomRenderer(renderer.transform))
                    continue;

                Material[] materials = renderer.sharedMaterials;
                if (materials == null)
                    continue;

                for (int m = 0; m < materials.Length; m++)
                {
                    Material material = materials[m];
                    Shader shader = material != null ? material.shader : null;
                    if (shader == null)
                        continue;
                    string shaderName = shader.name ?? string.Empty;
                    string materialName = material.name ?? string.Empty;
                    if (shaderName.IndexOf("FallbackError", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    // #337: the kit also contains Built-in-pipeline materials (the keypad panel and
                    // buttons). Caching one of those here would make every repaired material a fresh
                    // Unlit/Texture and silently defeat the whole rebind, so skip them - only an
                    // HDRP shader is a valid donor.
                    if (IsBuiltInPipelineMaterial(material))
                        continue;
                    if (shaderName.IndexOf("TextMesh", StringComparison.OrdinalIgnoreCase) >= 0
                        || shaderName.IndexOf("Text Mesh", StringComparison.OrdinalIgnoreCase) >= 0
                        || materialName.IndexOf("font", StringComparison.OrdinalIgnoreCase) >= 0
                        || materialName.IndexOf("sticker", StringComparison.OrdinalIgnoreCase) >= 0
                        || materialName.IndexOf("marking", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }
                    if (!material.HasProperty("_BaseColor")
                        && !material.HasProperty("_Color")
                        && !material.HasProperty("_UnlitColor"))
                    {
                        continue;
                    }

                    _visibleRuntimeShader = shader;
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Defuse] Using visible kit shader '{shaderName}' for ASGS runtime material repair.");
                    return;
                }
            }
        }

        // #337: the fallback chain used to reach for Unlit/Texture and Standard before HDRP, which
        // is backwards in an HDRP game - those are exactly the Built-in shaders this repair exists
        // to get rid of. HDRP first; the Built-in entries stay only as a last resort ahead of
        // rendering nothing at all.
        private static Shader ResolveRuntimeShader()
        {
            return _visibleRuntimeShader
                   ?? Shader.Find("HDRP/Lit")
                   ?? Shader.Find("HDRP/Unlit")
                   ?? Shader.Find("Universal Render Pipeline/Lit")
                   ?? Shader.Find("Standard")
                   ?? Shader.Find("Unlit/Texture");
        }

        private static void NormalizeOpaqueMaterials(Renderer renderer)
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials == null)
                return;

            for (int i = 0; i < materials.Length; i++)
                NormalizeOpaqueMaterial(materials[i]);
        }

        private static void NormalizeOpaqueMaterial(Material material)
        {
            if (material == null)
                return;

            material.renderQueue = (int)RenderQueue.Geometry;
            material.SetOverrideTag("RenderType", "Opaque");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");

            SetMaterialFloatIfPresent(material, "_Surface", 0f);
            SetMaterialFloatIfPresent(material, "_SurfaceType", 0f);
            SetMaterialFloatIfPresent(material, "_Blend", 0f);
            SetMaterialFloatIfPresent(material, "_BlendMode", 0f);
            SetMaterialFloatIfPresent(material, "_Mode", 0f);
            SetMaterialFloatIfPresent(material, "_AlphaClip", 0f);
            SetMaterialFloatIfPresent(material, "_AlphaCutoffEnable", 0f);
            // #337: this used to force _DoubleSidedEnable=1 and _CullMode=0 (Off) on every material
            // it touched. The briefcase's Built-in shader does not declare either property so it was
            // never affected, but the 64 shared HDRP/Lit kit materials do - and HDRP/Lit binds
            // Cull [_CullMode] directly, so the whole modular kit was rendering with backface
            // culling disabled against an authored value of 2 (Back). _DoubleSidedEnable without the
            // _DOUBLESIDED_ON keyword did nothing either way. Leave both at their authored values.
            SetMaterialFloatIfPresent(material, "_ZWrite", 1f);
            SetMaterialFloatIfPresent(material, "_ZTest", 4f);
            SetMaterialFloatIfPresent(material, "_SrcBlend", (float)BlendMode.One);
            SetMaterialFloatIfPresent(material, "_DstBlend", (float)BlendMode.Zero);
            SetMaterialFloatIfPresent(material, "_AlphaSrcBlend", (float)BlendMode.One);
            SetMaterialFloatIfPresent(material, "_AlphaDstBlend", (float)BlendMode.Zero);
            SetMaterialFloatIfPresent(material, "_TransparentZWrite", 0f);
            ForceMaterialColorAlpha(material, 1f);

            material.SetShaderPassEnabled("TransparentDepthPrepass", false);
            material.SetShaderPassEnabled("TransparentDepthPostpass", false);
            material.SetShaderPassEnabled("TransparentBackface", false);
        }

        private static void SetMaterialFloatIfPresent(Material material, string propertyName, float value)
        {
            if (material != null && material.HasProperty(propertyName))
                material.SetFloat(propertyName, value);
        }

        private static void ForceMaterialColorAlpha(Material material, float alpha)
        {
            if (material == null)
                return;

            ForceMaterialColorAlpha(material, "_BaseColor", alpha);
            ForceMaterialColorAlpha(material, "_Color", alpha);
            ForceMaterialColorAlpha(material, "_UnlitColor", alpha);
        }

        private static void ForceMaterialColorAlpha(Material material, string propertyName, float alpha)
        {
            if (material == null || !material.HasProperty(propertyName))
                return;

            Color color = material.GetColor(propertyName);
            color.a = Mathf.Clamp01(alpha);
            material.SetColor(propertyName, color);
        }

        private static string DescribeMaterials(Material[] materials)
        {
            if (materials == null || materials.Length == 0)
                return "none";

            List<string> parts = new List<string>();
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                string materialName = material != null ? material.name : "null";
                string shaderName = material?.shader != null ? material.shader.name : "null";
                parts.Add($"{materialName}/{shaderName}");
            }

            return string.Join(", ", parts.ToArray());
        }

        private static string DescribeBounds(Bounds bounds)
        {
            Vector3 center = bounds.center;
            Vector3 size = bounds.size;
            return $"center=({center.x:F2},{center.y:F2},{center.z:F2}) size=({size.x:F2},{size.y:F2},{size.z:F2})";
        }

        private static void SetMaterialTexture(Material material, Texture texture, params string[] propertyNames)
        {
            if (material == null || texture == null || propertyNames == null)
                return;

            for (int i = 0; i < propertyNames.Length; i++)
            {
                string propertyName = propertyNames[i];
                if (material.HasProperty(propertyName))
                    material.SetTexture(propertyName, texture);
            }
        }

        private static void SetMaterialColor(Material material, Color color, params string[] propertyNames)
        {
            if (material == null || propertyNames == null)
                return;

            for (int i = 0; i < propertyNames.Length; i++)
            {
                string propertyName = propertyNames[i];
                if (material.HasProperty(propertyName))
                    material.SetColor(propertyName, color);
            }
        }

        private static T LoadBundleAssetByShortName<T>(string assetName) where T : UnityEngine.Object
        {
            AssetBundle bundle = ContractAssetLoader.Bundle;
            if (bundle == null || string.IsNullOrWhiteSpace(assetName))
                return null;

            try
            {
                T direct = bundle.LoadAsset<T>(assetName);
                if (direct != null)
                    return direct;
            }
            catch
            {
                // Some bundle builds require loading by full path.
            }

            string[] assetNames = bundle.GetAllAssetNames();
            for (int i = 0; i < assetNames.Length; i++)
            {
                string bundledName = assetNames[i];
                string shortName = Path.GetFileNameWithoutExtension(bundledName);
                if (!string.Equals(shortName, assetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                T asset = bundle.LoadAsset<T>(bundledName);
                if (asset != null)
                    return asset;
            }

            return null;
        }

        private static void PreparePlacementPreviewInstance(GameObject root)
        {
            if (root == null)
                return;

            DefuseBombController[] controllers = root.GetComponentsInChildren<DefuseBombController>(true);
            for (int i = 0; i < controllers.Length; i++)
                if (controllers[i] != null)
                    controllers[i].enabled = false;

            DefuseInteractor[] interactors = root.GetComponentsInChildren<DefuseInteractor>(true);
            for (int i = 0; i < interactors.Length; i++)
                if (interactors[i] != null)
                    interactors[i].enabled = false;

            CompanyStashKeypadInteractor[] keypads = root.GetComponentsInChildren<CompanyStashKeypadInteractor>(true);
            for (int i = 0; i < keypads.Length; i++)
                if (keypads[i] != null)
                    keypads[i].enabled = false;

            CompanyStashKeypadButton[] keypadButtons = root.GetComponentsInChildren<CompanyStashKeypadButton>(true);
            for (int i = 0; i < keypadButtons.Length; i++)
                if (keypadButtons[i] != null)
                    keypadButtons[i].enabled = false;

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                if (colliders[i] != null)
                    colliders[i].enabled = false;

            NetworkObject[] networkObjects = root.GetComponentsInChildren<NetworkObject>(true);
            for (int i = 0; i < networkObjects.Length; i++)
                if (networkObjects[i] != null)
                    networkObjects[i].enabled = false;

            Unity.Netcode.Components.NetworkTransform[] transforms =
                root.GetComponentsInChildren<Unity.Netcode.Components.NetworkTransform>(true);
            for (int i = 0; i < transforms.Length; i++)
                if (transforms[i] != null)
                    transforms[i].enabled = false;
        }

        private static void AddBoundsCollider(GameObject root)
        {
            BoxCollider collider = root.AddComponent<BoxCollider>();
            ConfigureBoundsCollider(root, collider);
        }

        private static void RefreshBoundsCollider(GameObject root)
        {
            if (root == null)
                return;

            BoxCollider collider = root.GetComponent<BoxCollider>();
            if (collider == null)
            {
                AddBoundsCollider(root);
                return;
            }

            ConfigureBoundsCollider(root, collider);
        }

        private static void ConfigureBoundsCollider(GameObject root, BoxCollider collider)
        {
            if (root == null || collider == null)
                return;

            Bounds? combined = null;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                Bounds local = ConvertWorldBoundsToLocal(root.transform, renderer.bounds);
                combined = combined.HasValue ? Encapsulate(combined.Value, local) : local;
            }

            collider.isTrigger = true;
            if (!combined.HasValue)
            {
                collider.center = new Vector3(0f, 0.35f, 0f);
                collider.size = new Vector3(1.2f, 0.8f, 0.8f);
                return;
            }

            Bounds bounds = combined.Value;
            bounds.Expand(0.16f);
            collider.center = bounds.center;
            collider.size = new Vector3(Mathf.Max(0.6f, bounds.size.x), Mathf.Max(0.35f, bounds.size.y), Mathf.Max(0.45f, bounds.size.z));
        }

        private static bool TryResolveSpawnPose(
            out Vector3 position,
            out Quaternion rotation,
            out string source,
            out AuthoredInteriorPlacementRecord authoredRecord,
            out bool elevatedScanInProgress)
        {
            authoredRecord = null;
            elevatedScanInProgress = false;

            if (!_floorSearchStarted && TrySelectAuthoredBombPose(out AuthoredInteriorPlacementPose canonical, out int canonicalCount))
            {
                position = canonical.WorldPosition;
                rotation = canonical.WorldRotation;
                authoredRecord = canonical.Record;
                string id = canonical.Record != null && !string.IsNullOrWhiteSpace(canonical.Record.objectId)
                    ? canonical.Record.objectId
                    : "bomb";
                source = $"authored-canonical:{id}:{canonical.Record?.flowName}/{canonical.Record?.tileName} ({canonicalCount} compatible)";
                if (FixtureReservationRegistry.TryReserve(DefuseElevatedBombPlacer.ReservationLabel, "defuse-authored",
                    DefuseElevatedBombPlacer.CreateBombFootprint(), position, rotation, out _, out _))
                    return true;
                authoredRecord = null;
            }

            // #548: elevated procedural tier — rest the bomb on a stable prop top inside a
            // proper room before any floor fallback runs. The placer reserves the footprint
            // and applies the shared vertical offset itself.
            // The scan is resumable: while it is still in flight the floor tiers must NOT run,
            // or a large interior would floor-spawn the bomb purely because the elevated search
            // had not finished walking its rooms yet.
            bool elevatedScanComplete = true;
            if (!_floorSearchStarted && DefuseElevatedBombPlacer.TryResolveElevated(
                    out Vector3 elevatedPosition,
                    out Quaternion elevatedRotation,
                    out string elevatedTile,
                    out elevatedScanComplete))
            {
                position = elevatedPosition;
                rotation = elevatedRotation;
                source = $"elevated-prop:{elevatedTile}";
                return true;
            }

            if (!elevatedScanComplete)
            {
                elevatedScanInProgress = true;
                position = Vector3.zero;
                rotation = Quaternion.identity;
                source = "elevated-scan-in-progress";
                return false;
            }

            _floorSearchStarted = true;
            bool resolved = DefuseFloorBombPlacer.TryResolve(out position, out rotation, out source, out bool floorComplete);
            elevatedScanInProgress = !floorComplete;
            return resolved;
        }

        private static bool TrySelectAuthoredBombPose(out AuthoredInteriorPlacementPose selected, out int compatibleCount)
        {
            selected = default;
            compatibleCount = 0;
            if (!AuthoredInteriorPlacementStore.TryResolvePoses(AuthoredInteriorPlacementKinds.DeadDropBomb, out List<AuthoredInteriorPlacementPose> poses) ||
                poses == null ||
                poses.Count == 0)
            {
                return false;
            }

            List<AuthoredInteriorPlacementPose> compatible = new List<AuthoredInteriorPlacementPose>(poses.Count);
            for (int i = 0; i < poses.Count; i++)
            {
                AuthoredInteriorPlacementPose pose = poses[i];
                if (pose.Tile != null
                    && DefusePlacementPolicy.IsSeparated(FixtureReservationRegistry.GetNearestReservationCenterDistance(pose.WorldPosition))
                    && !FixtureReservationRegistry.TryGetConflict(DefuseElevatedBombPlacer.CreateBombFootprint(), pose.WorldPosition, pose.WorldRotation, out _)
                    && InteriorAnchorService.TryGetEntrancePathDistance(pose.WorldPosition, out _))
                    compatible.Add(pose);
            }

            compatibleCount = compatible.Count;
            if (compatible.Count == 0)
                return false;

            compatible.Sort(CompareAuthoredBombPose);
            int index = PositiveModulo(BuildDefuseSelectionSeed("bomb-placement"), compatible.Count);
            selected = compatible[index];
            for (int unusedIndex = 0; unusedIndex < compatible.Count; unusedIndex++)
            {
                if (unusedIndex == index)
                    continue;
                AuthoredPlacementRoundReport.RecordResolvedButUnused(
                    compatible[unusedIndex].Record,
                    "consumer=dead-drop-bomb reason=seed-selection");
            }
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Defuse] Selected authored bomb id='{selected.Record?.objectId ?? "<unknown>"}' from compatible={compatible.Count} index={index}.");
            return true;
        }

        private static int CompareAuthoredBombPose(AuthoredInteriorPlacementPose a, AuthoredInteriorPlacementPose b)
        {
            int id = string.Compare(a.Record?.objectId ?? string.Empty, b.Record?.objectId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            if (id != 0)
                return id;

            int tile = string.Compare(a.Tile?.name ?? string.Empty, b.Tile?.name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            if (tile != 0)
                return tile;

            return string.Compare(a.Record?.timestampUtc ?? string.Empty, b.Record?.timestampUtc ?? string.Empty, StringComparison.Ordinal);
        }

        private static int BuildDefuseSelectionSeed(string salt)
        {
            unchecked
            {
                int seed = StartOfRound.Instance != null ? StartOfRound.Instance.randomMapSeed : Environment.TickCount;
                SelectableLevel level = StartOfRound.Instance?.currentLevel ?? RoundManager.Instance?.currentLevel;
                seed = (seed * 397) ^ StableHash(salt);
                seed = (seed * 397) ^ (level != null ? level.levelID : 0);
                seed = (seed * 397) ^ StableHash(level != null ? level.PlanetName : string.Empty);
                seed = (seed * 397) ^ StableHash(ResolveCurrentDungeonFlowName());
                return seed;
            }
        }

        private static string ResolveCurrentDungeonFlowName()
        {
            try
            {
                DunGen.Graph.DungeonFlow flow = RoundManager.Instance?.dungeonGenerator?.Generator?.DungeonFlow;
                return flow != null && !string.IsNullOrWhiteSpace(flow.name) ? flow.name : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = (int)2166136261;
                if (!string.IsNullOrEmpty(value))
                {
                    for (int i = 0; i < value.Length; i++)
                        hash = (hash ^ value[i]) * 16777619;
                }

                return hash;
            }
        }

        private static int PositiveModulo(int value, int modulus)
        {
            if (modulus <= 0)
                return 0;

            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static Bounds ConvertWorldBoundsToLocal(Transform root, Bounds worldBounds)
        {
            Vector3 min = worldBounds.min;
            Vector3 max = worldBounds.max;
            Vector3[] corners =
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, max.y, max.z),
            };

            Vector3 localMin = root.InverseTransformPoint(corners[0]);
            Vector3 localMax = localMin;
            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 local = root.InverseTransformPoint(corners[i]);
                localMin = Vector3.Min(localMin, local);
                localMax = Vector3.Max(localMax, local);
            }

            return new Bounds((localMin + localMax) * 0.5f, localMax - localMin);
        }

        private static Bounds Encapsulate(Bounds a, Bounds b)
        {
            a.Encapsulate(b.min);
            a.Encapsulate(b.max);
            return a;
        }

        private static float MaxComponent(Vector3 value)
        {
            return Mathf.Max(value.x, Mathf.Max(value.y, value.z));
        }

        private static string FormatVector(Vector3 value)
        {
            return $"{value.x:0.0},{value.y:0.0},{value.z:0.0}";
        }
    }

    /// <summary>
    /// Latch for <c>DefuseContract.RepairBundledBombVisuals</c> (#716, WP-A4). Added to a bomb
    /// root once its bundled visuals have been repaired; <c>Instantiate</c> copies it onto every
    /// clone, so the spawned bomb and the placement preview inherit the verdict instead of
    /// rerunning the renderer/material pass. Deliberately a plain <see cref="MonoBehaviour"/>:
    /// it must not appear in the prefab's NetworkBehaviour ordering.
    /// </summary>
    internal sealed class DefuseVisualsRepairMarker : MonoBehaviour
    {
    }
}
