using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using DunGen;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.BlackoutAudit;
using Y4NGZCompany.Contracts.Survey;
using Y4NGZCompany.Contracts.Whistleblower;
using Y4NGZCompany.Contracts.ContainmentBreach;
using Y4NGZCompany.Contracts.Defuse;
using Y4NGZCompany.Contracts.Payload;
using Y4NGZCompany.Contracts.WasteDisposal;
using Y4NGZCompany.Contracts.PestControl;
using Y4NGZCompany.Contracts.Lgu;
using Y4NGZCompany.Contracts.ObjectiveHints;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Escalations._Shared;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Core.Compatibility;
namespace Y4NGZCompany.Contracts._Shared
{
    internal static class MoonContractState
    {
        private static readonly MoonContractType[] ContractPool =
        {
            MoonContractType.None,
            MoonContractType.None,
            MoonContractType.Whistleblower,
            MoonContractType.BlackoutAudit,
            MoonContractType.ShadowRaid,
            MoonContractType.ContainmentBreach,
            MoonContractType.Survey,
            MoonContractType.Defuse,
            MoonContractType.Payload,
            MoonContractType.PestControl,
            MoonContractType.WasteDisposal
        };

        private static readonly Dictionary<int, MoonContractType> Assignments =
            new Dictionary<int, MoonContractType>();
        private static readonly Dictionary<int, MoonContractType> LockedAssignments =
            new Dictionary<int, MoonContractType>();
        /// <summary>#716/F5: previous roll, kept so the "Refreshed assignments" line can be
        /// Info only when the assignment set actually moved. Reused, never re-allocated.</summary>
        private static readonly Dictionary<int, MoonContractType> PreviousAssignments =
            new Dictionary<int, MoonContractType>();
        private const string AssignmentSnapshotMessage = "Y4NGZCompany.ContractAssignments.v2";
        private const string BlackoutZoneSnapshotMessage = "Y4NGZCompany.BlackoutZones.v1";
        // #172 item 4: the ambient surge is scheduled on the host and broadcast, so every
        // machine pulses the same zone at the same moment with the same stutter.
        private const string BlackoutSurgeMessage = "Y4NGZCompany.BlackoutSurge.v1";
        // #435: latch the CustomMessagingManager, not the NetworkManager. NGO builds a fresh
        // messaging manager on every StartHost/StartClient while NetworkManager.Singleton lives
        // for the whole process, so a NetworkManager-keyed guard skips registration entirely in
        // the second lobby of one session and no snapshot ever arrives.
        private static CustomMessagingManager _assignmentMessaging;
        private static float _nextAssignmentSnapshotAt;
        private static Dictionary<string, List<string>> _constellationMoonMap;
        private static DateTime _constellationMoonMapWriteTimeUtc = DateTime.MinValue;

        private static int _assignmentCycleKey = int.MinValue;
        private static int _assignmentSeed = int.MinValue;
        private static MoonContractType _lastRuntimeContract = MoonContractType.None;
        /// <summary>Vanilla's powered-light animator bool. Hashed once so the 1 Hz zone
        /// re-apply does not pay a string lookup per fixture (#716/B4).</summary>
        private static readonly int BlackoutFixturePoweredParameter = Animator.StringToHash("on");
        private const float WhistleblowerGuardSweepIntervalSeconds = 2f;
        private static float _nextWhistleblowerGuardSweepAt;
        private static readonly List<EnemyAI> WhistleblowerEnemySweepBuffer = new List<EnemyAI>();
        private static WhistleblowerEnemyAI _whistleblowerEnemy;
        private static int _whistleblowerEscapeCount;
        private static bool _whistleblowerNeutralized;
        /// <summary>
        /// #964 S14 U2: latched once a Blacksite ship has departed with the witness aboard. It is
        /// the contract's first real failure and it is one-way for the round: nothing un-extracts
        /// a man who has left the moon. Stage 14 U4 reads it to build the failed card row; this
        /// unit only raises it.
        ///
        /// #1076: it used to be raised ONLY by the host, in NotifyWhistleblowerExtractedServer, so
        /// a client's card never showed the failure at all. The host still raises it there (and
        /// logs the custody terminal); every other peer raises it from the
        /// BlacksiteObjectiveDeparted progression event that NotifyBlacksiteObjectiveDepartedServer
        /// publishes in the same departure branch, and a late joiner from that event's round-keyed
        /// replay (#406). One-way per round; cleared by ResetRunState.
        /// </summary>
        private static bool _whistleblowerExtracted;
        /// <summary>
        /// #1076: the departure tokens this round's host has already put on the progression
        /// channel. This is the publish dedupe, kept SEPARATE from the three contract latches on
        /// purpose: the witness latch is raised by NotifyWhistleblowerExtractedServer one line
        /// before the departure terminal runs, so a dedupe that read the latch would never publish
        /// the witness at all (the reworked #1076's first cut did exactly that).
        /// </summary>
        private static readonly HashSet<string> _blacksiteDepartedPublished = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>
        /// #964 S14 U2: the live witness, or null. BlacksiteObjectiveManifestController.Discover
        /// needs him to register the escort objective at all, and there was no accessor: the field
        /// is private and every existing reader is inside this class.
        /// </summary>
        internal static WhistleblowerEnemyAI ActiveWhistleblower => _whistleblowerEnemy;
        /// <summary>#964 S14 U2: whether the Blacksite ship left with the witness aboard.</summary>
        internal static bool WhistleblowerExtracted => _whistleblowerExtracted;
        // #716/B3: the spawned-enemy scan used to run twice per frame with a GetComponent
        // plus a GetComponentInChildren per live enemy. It only has to notice a spawn, so
        // it runs on a 0.5 s window and remembers the enemies it has already rejected.
        private const float WhistleblowerEnemyScanIntervalSeconds = 0.5f;
        private static float _nextWhistleblowerEnemyScanAt;
        private static readonly HashSet<int> WhistleblowerScanRejectedEnemies = new HashSet<int>();
        private static bool _blackoutInitialized;
        /// <summary>#716/B16: latched once an interior is confirmed to expose powered
        /// lights with no powered-light animators, so RefreshLightsList is not re-entered
        /// on every pass for the rest of the round.</summary>
        private static bool _poweredLightAnimatorsAbsentThisRound;
        private static BlackoutAuditLever _blackoutLever;
        private static BreakerBox _blackoutBreaker;
        private const string BlackoutFallbackBreakerNamePrefix = "BlackoutAuditFallbackBreakerBox";
        private const float BlackoutBreakerDefaultMountHeight = 1.15f;
        private const float BlackoutBreakerDefaultWallInset = 0.22f;
        private const float BlackoutBreakerMaxWallProbeDistance = 2.4f;
        private const float BlackoutBreakerMarkerMatchRadius = 2f;
        private const float BlackoutBreakerMaxPlanFloorGap = 1.25f;
        private static bool _blackoutCompleted;
        private static bool _blackoutUnsupportedThisRound;
        private static bool _blackoutPowerForced;
        private static bool _blackoutForceInProgress;
        private static float _blackoutNextPowerForceTime;
        private static float _blackoutNextLightAuditTime;
        private static readonly List<BlackoutBreakerZone> BlackoutBreakerZones = new List<BlackoutBreakerZone>();
        private static readonly List<BlackoutPoweredFixture> BlackoutPoweredFixtures = new List<BlackoutPoweredFixture>();
        private static readonly List<BlackoutBreakerSpawnCurveOverride> BlackoutBreakerSpawnCurveOverrides =
            new List<BlackoutBreakerSpawnCurveOverride>();
        private static IndoorMapHazard[] _blackoutOriginalIndoorMapHazards;
        private static SelectableLevel _blackoutOriginalIndoorMapHazardsLevel;
        private static float _blackoutZoneEnforceUntil;
        private static float _blackoutNextAmbientPulseTime;
        private static float _blackoutAmbientPulseEndTime;
        private static int _blackoutAmbientPulseZoneIndex = -1;
        private static int _blackoutOriginalIncreasedMapHazardSpawnRateIndex = int.MinValue;
        private static int _blackoutBreakerMapObjectMarkerCount = -1;
        private static int _blackoutBreakerSyncedSourceCount = -1;
        private static readonly Dictionary<Light, BlackoutNightVisionState> BlackoutSuppressedNightVision =
            new Dictionary<Light, BlackoutNightVisionState>();
        // #290: at audit start the NightVision sweep walked all 32 player slots and the
        // light audit walked every scene Light in the same frame, which the 2026-08-05
        // profiler measured as a single 1,150 ms UpdateActiveContractMechanics call.
        // Both sweeps are now cursor-sliced across RoundManager.Update passes; the end
        // state is unchanged, only the per-frame cost is bounded.
        private const int BlackoutNightVisionSlotsPerPass = 4;
        private const int BlackoutLightAuditLightsPerPass = 48;
        private const int BlackoutLightAuditSummaryExamples = 3;
        private const int BlackoutLightAuditVerboseDetailLines = 12;
        private static int _blackoutNightVisionSweepCursor;
        private static Light[] _blackoutLightAuditPending;
        private static int _blackoutLightAuditCursor;
        private static Bounds[] _blackoutLightAuditTileBounds;
        private static HashSet<Light> _blackoutLightAuditTrackedLights;
        private static List<BlackoutLightAuditRecord> _blackoutLightAuditSuspects;
        private static Transform _blackoutLightAuditPlayerTransform;
        private static Transform _blackoutLightAuditCameraTransform;
        private static int _blackoutLightAuditEnabledCount;
        private static int _blackoutLightAuditInsideDungeonCount;
        private static int _blackoutLightAuditUntrackedCount;
        private static int _salvageKnownTotalValue;
        private static bool _containmentStarted;
        private static bool _containmentCompleted;
        private static float _containmentInsideSeconds;
        private static float _containmentNextWaveTime;
        private static float _containmentNextUpdateTime;
        private static float _containmentLastUpdateTime;
        private static float _containmentEntryTime;
        private static float _containmentLockdownWarningTime;
        private static float _containmentLockdownStartTime;
        private static float _containmentNextPowerRestoreTime;
        private static BreakerBox _containmentBreakerBox;
        // Fixed length of the wave-start alarm pulse (blackout + fixture/lens
        // strobe); presentation only, never detection-driven.
        private const float ContainmentWaveAlarmPulseSeconds = 7f;
        private static int _containmentWave;
        // #705: a wave's enemies spawn one per containment tick instead of all in the
        // wave-start frame; SpawnEnemyGameObject on a modded enemy prefab was the owner
        // of 20-60ms ContractBranch stalls.
        private static int _containmentPendingWaveSpawns;
        private static int _containmentPendingWaveNumber;
        private static int _containmentPendingWaveSpawned;
        // #406: highest wave already credited as survived, so a wave pays exactly once.
        private static int _containmentCreditedWave;
        private static System.Random _containmentRandom;
        private static List<ContainmentEnemyCandidate> _containmentEnemyPool;
        private static List<EnemyVent> _containmentVents;
        private static bool _containmentLockdownLinePlayed;
        private static bool _containmentLockedDown;
        // True while the Containment Breach contract itself is holding the
        // lockdown gates closed. Other lockdown users (protocol drill, CCTV
        // alarm) must not release the shared gates while this holds.
        internal static bool ContainmentLockdownHeld => _containmentLockedDown && !_containmentCompleted;
        private static bool _containmentEndSequencePlayed;
        private static bool _containmentPowerRestored;
        private static List<SurveyTarget> _surveyTargets;
        private static bool _surveyTargetsInitialized;
        private static bool _surveyDeliveryRequested;
        private static bool _surveyDronesDelivered;
        private static bool _surveyBeaconGrabbed;
        private static Item _surveyDroneItemProperties;
        private static MethodInfo _surveyDropshipLandMethod;

        internal static void RefreshAssignments(string reason, bool force = false, bool clearLocks = false)
        {
            TryRegisterAssignmentSnapshotHandler();
            var start = StartOfRound.Instance;
            if (start == null || start.levels == null)
                return;

            // Connected clients never roll locally: the host snapshot
            // (OnAssignmentSnapshot) is authoritative. A local roll would use this
            // machine's own map seed, quota state, and unsynced weight config, so the
            // terminal could list a contract the host never assigned — and the host's
            // next snapshot would silently replace it after routing.
            if (IsAssignmentAuthorityRemote())
                return;

            int cycleKey = BuildCycleKey(start);
            if (!force && Assignments.Count > 0)
            {
                if (!start.inShipPhase)
                    return;
                if (_assignmentCycleKey == cycleKey)
                    return;
            }

            // Locked (routed-to) contracts survive re-rolls so the contract the
            // terminal listed at route time is still the one active on landing,
            // even though the cycle key shifts with quota/day state and the map
            // seed changes at StartGame. Locks are wiped only when a new orbit
            // begins (clearLocks, from SetShipReadyToLand).
            if (clearLocks)
                LockedAssignments.Clear();

            PreviousAssignments.Clear();
            foreach (var entry in Assignments)
                PreviousAssignments[entry.Key] = entry.Value;

            Assignments.Clear();
            _assignmentCycleKey = cycleKey;
            _assignmentSeed = BuildAssignmentSeed(start, cycleKey);

            for (int i = 0; i < start.levels.Length; i++)
            {
                var level = start.levels[i];
                if (level == null)
                    continue;

                MoonContractType contract = PickContractForLevel(level, _assignmentSeed);
                Assignments[i] = contract;

                if (level.levelID >= 0 && level.levelID != i && !Assignments.ContainsKey(level.levelID))
                    Assignments[level.levelID] = contract;
            }

            // Keep Assignments in agreement with surviving locks so snapshots,
            // logs, and direct Assignments readers all report the locked value.
            foreach (var locked in LockedAssignments)
                Assignments[locked.Key] = locked.Value;

            // #716/F5: a re-roll that lands on the same assignment set is not news. The
            // terminal catalogue drives this on every render, so the unchanged case drops to
            // Debug and only a real change stays at Info.
            string refreshed = $"[MoonContracts] Refreshed assignments ({reason}, cycle={cycleKey}, seed={_assignmentSeed}, mapSeed={start.randomMapSeed}).";
            if (AssignmentsChangedFromPrevious())
                ContractsBootstrap.Log?.LogInfo(refreshed);
            else
                ContractsBootstrap.Log?.LogDebug(refreshed);
            LogAssignmentSnapshot(start, reason);
            if (NetworkManager.Singleton?.IsServer == true)
                SendAssignmentSnapshot();
            MoonContractShipMonitor.RefreshCurrent();
        }

        private static bool AssignmentsChangedFromPrevious()
        {
            if (PreviousAssignments.Count != Assignments.Count)
                return true;

            foreach (var entry in Assignments)
            {
                if (!PreviousAssignments.TryGetValue(entry.Key, out MoonContractType previous) || previous != entry.Value)
                    return true;
            }

            return false;
        }

        private static void LogAssignmentSnapshot(StartOfRound start, string reason)
        {
            if (start?.levels == null)
                return;

            var entries = new List<string>();
            for (int i = 0; i < start.levels.Length; i++)
            {
                SelectableLevel level = start.levels[i];
                if (level == null)
                    continue;

                Assignments.TryGetValue(i, out MoonContractType contract);
                if (contract != MoonContractType.None || ReferenceEquals(level, start.currentLevel))
                    entries.Add($"{i}:{FormatLevelForLog(level)}={contract}");
            }

            string activeAssignments = entries.Count > 0
                ? string.Join("; ", entries)
                : "no non-salvage assignments";

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Assignment snapshot ({reason}): {GetCurrentContractDebugContext()}; assignments={activeAssignments}");
        }

        /// <summary>Resets the round's contract runtime. <paramref name="deferEntityTeardown"/> is
        /// passed straight through to the escalations (#540): at ship-leave their spawned entities
        /// stay standing until vanilla's own end-of-round sweep clears the moon, while every flag,
        /// timer and ledger here still resets on the spot.</summary>
        // #716/G4: round end reaches this method two to four times (ShipLeave,
        // ShipLeaveAutomatically, SetShipReadyToLand, EndOfGameClientRpc, and now Disconnect),
        // and every pass re-runs ResetBlackoutRuntimeState -> RestoreBlackoutPower over the
        // whole fixture list and re-tears-down every escalation. Only the first pass of a
        // round has anything to do. The guard is keyed on the round's identity rather than a
        // bare flag so a lobby that never reaches PrimeCurrentContractForRound still recovers:
        // a new seed or level is by itself enough to release it.
        //
        // The deferral mode is part of the key, because within one round the two calls are not
        // interchangeable. MoonContractPatches' ShipLeave_Postfix calls
        // ResetRunState(deferEntityTeardown: true), which sets MoonEscalationState's
        // _departureTeardownPending and deliberately leaves the escalation entities standing
        // (#540); the EndOfGameClientRpc prefix then calls ResetRunState() with defer=false,
        // and THAT call is the only teardown a client ever gets - vanilla's
        // UnloadSceneObjectsEarly sweep, which MoonContractEscalationDepartureSweepPatch rides,
        // is server-only - as well as the safety net on the host when the sweep never runs
        // (host disconnect, aborted departure). Collapsing it into the deferred pass would
        // strand a client's escalation entities for the rest of the session.
        //
        // So: a defer=false pass always runs after a defer=true one for the same round, and
        // clears the mirrored pending flag; a defer=true pass after a defer=false one is the
        // no-op. _runResetDeferralPending mirrors MoonEscalationState._departureTeardownPending
        // (which is private to that type), and the guard never skips while it is set.
        private static long _lastRunResetRoundKey = long.MinValue;
        private static bool _runResetDeferralPending;

        private static long GetRunResetRoundKey()
        {
            StartOfRound start = StartOfRound.Instance;
            int seed = start != null ? start.randomMapSeed : 0;
            int level = start?.currentLevel != null ? start.currentLevel.levelID : -1;
            return ((long)seed << 32) ^ (uint)level;
        }

        /// <summary>Releases the <see cref="ResetRunState"/> per-round guard. Called when a
        /// round starts, so the next round end resets for real.</summary>
        private static void ReleaseRunResetGuard()
        {
            _lastRunResetRoundKey = long.MinValue;
            _runResetDeferralPending = false;
        }

        internal static void ResetRunState(bool deferEntityTeardown = false)
        {
            // #1197: the objective hint is local presentation, so it is torn down on every reset
            // call, ahead of the once-per-round dedupe below: an open blink window must never
            // outlive the round, a disconnect or a lobby change.
            ObjectiveHintRuntime.ResetRunState();
            _locateTileBoundsCount = 0;
            _locateTileBoundsDungeon = null;
            long roundKey = GetRunResetRoundKey();
            // A non-deferred pass owes the escalations an entity teardown that the deferred
            // pass explicitly did not do, so it is never skipped while that debt is open.
            bool owesEntityTeardown = !deferEntityTeardown && _runResetDeferralPending;
            if (roundKey == _lastRunResetRoundKey && !owesEntityTeardown)
            {
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts] Contract run state already reset for this round (deferEntityTeardown={deferEntityTeardown}); skipping duplicate reset.");
                return;
            }

            _lastRunResetRoundKey = roundKey;
            _runResetDeferralPending = deferEntityTeardown;
            // #406: the progression replay ledger is deliberately NOT cleared here. This method is
            // called from ShipLeave_Postfix immediately after the round's ContractCompleted /
            // ContractFailed events are published, so clearing it here would delete exactly the
            // events a client is most likely to still be missing. The ledger is keyed by round id
            // inside ContractProgressionNetwork and expires on the next round's first event.
            BlackoutBreakerActorLatch.Clear();
            _containmentCreditedWave = 0;
            MoonEscalationState.ResetRunState(deferEntityTeardown);
            PayloadContract.ResetRunState();
            WasteDisposalContract.ResetRunState();
            PestControlContract.ResetRunState();
            _lastRuntimeContract = MoonContractType.None;
            _nextWhistleblowerGuardSweepAt = 0f;
            WhistleblowerEnemySweepBuffer.Clear();
            WhistleblowerScanRejectedEnemies.Clear();
            _nextWhistleblowerEnemyScanAt = 0f;
            ClearWhistleblowerEnemyReference();
            _whistleblowerEscapeCount = 0;
            _whistleblowerNeutralized = false;
            // #964 S14 U2: the extraction latch dies with the run it belongs to, beside the two
            // latches it sits next to.
            _whistleblowerExtracted = false;
            _blacksiteDepartedPublished.Clear();
            ResetBlackoutRuntimeState("run reset");
            _salvageKnownTotalValue = 0;
            _containmentStarted = false;
            _containmentCompleted = false;
            _containmentInsideSeconds = 0f;
            _containmentNextWaveTime = 0f;
            _containmentNextUpdateTime = 0f;
            _containmentLastUpdateTime = 0f;
            _containmentEntryTime = 0f;
            _containmentLockdownWarningTime = 0f;
            _containmentLockdownStartTime = 0f;
            _containmentNextPowerRestoreTime = 0f;
            _containmentBreakerBox = null;
            _containmentWave = 0;
            _containmentPendingWaveSpawns = 0;
            _containmentPendingWaveNumber = 0;
            _containmentPendingWaveSpawned = 0;
            _containmentRandom = null;
            _containmentEnemyPool = null;
            _containmentVents = null;
            _containmentLockdownLinePlayed = false;
            _containmentLockedDown = false;
            _containmentEndSequencePlayed = false;
            _containmentPowerRestored = false;
            ContainmentWaveHunter.ResetRunState();
            ContainmentBreachDirector.ResetDirector();
            // #393: the CCTV round reset that used to run here moved into LethalCCTV's own
            // CctvRoundLifecyclePatches, which hooks the same vanilla moments this method is
            // called from. Reaching across for it would be a hard reference to an optional
            // plugin, and a reflective call would only re-time work that plugin already does.
            InteriorAnchorService.ResetRunState();
            InteriorInjectionService.ResetRunState();
            ShadowRaidContract.ResetRunState();
            AuthoredInteriorTileInjectionService.ResetRunState();
            AuthoredPlacementRoundReport.ResetRound();
            _cachedEntrances = null;
            ResetSurveyTargetScan();
            ResetWhistleblowerPlacementScan();
            _surveyTargets = null;
            _surveyTargetsInitialized = false;
            _surveyDeliveryRequested = false;
            _surveyDronesDelivered = false;
            _surveyBeaconGrabbed = false;
            _surveyDroneItemProperties = null;
            DefuseContract.ResetRunState();
            // #804: clears the LGU objective latches, the cached LGU components and the
            // hoarding-bug cull count. A component cached across a round would be a destroyed
            // Unity object that only the overloaded null operator rejects.
            LguContractRuntime.ResetRunState();
            ContractSpatialAudio.ResetRunState();
            // #929: the scaled shot copies belong to the round whose clips they were built from.
            Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteAudioPolicy.ResetRunState();
            CodeRebirthCrashedShipObjectiveState.ResetRunState();
            // #1195: the Wesley side objective, its reveal latch and its cached controller and props.
            SideObjectiveState.ResetRunState();
            ShipPowerEscalationObjectiveState.ResetRunState();
        }

        private static void ResetBlackoutRuntimeState(string reason)
        {
            bool hadBlackoutRuntimeState = _blackoutPowerForced
                || _blackoutInitialized
                || _blackoutBreaker != null
                || _blackoutLever != null
                || BlackoutBreakerZones.Count > 0
                || BlackoutPoweredFixtures.Count > 0
                || BlackoutSuppressedNightVision.Count > 0;

            if (_blackoutPowerForced || _blackoutInitialized || _blackoutBreaker != null)
            {
                RestoreBlackoutPower();
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout Audit restored facility power during {reason}.");
            }
            else
            {
                RestoreBlackoutPlayerNightVision();
            }

            DestroyBlackoutLever();
            ClearBlackoutZoneState(destroySpawnedBreakers: true);
            // #716/A4: a sliced build or a parked client rebuild must not survive the round.
            ResetBlackoutFixtureBuild();
            ClearBlackoutSnapshotRebuild();
            _blackoutBreaker = null;
            _blackoutInitialized = false;
            _poweredLightAnimatorsAbsentThisRound = false;
            _blackoutCompleted = false;
            _blackoutUnsupportedThisRound = false;
            _blackoutPowerForced = false;
            _blackoutForceInProgress = false;
            _blackoutNextPowerForceTime = 0f;
            _blackoutNextLightAuditTime = 0f;
            _blackoutNightVisionSweepCursor = 0;
            ResetBlackoutLightAuditPass();
            _blackoutZoneEnforceUntil = 0f;
            // #172 item 4: the surge timer used to survive the reset, so a client rebuild
            // started with a stale deadline and fired its first pulse immediately.
            _blackoutNextAmbientPulseTime = 0f;
            _blackoutAmbientPulseStartTime = 0f;
            _blackoutAmbientPulseEndTime = 0f;
            _blackoutAmbientPulseZoneIndex = -1;
            _blackoutBreakerMapObjectMarkerCount = -1;
            _blackoutBreakerSyncedSourceCount = -1;
            RestoreBlackoutVanillaBreakerSpawnCurves(RoundManager.Instance);

            if (hadBlackoutRuntimeState)
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Blackout Audit runtime state cleared ({reason}).");
        }

        internal static MoonContractType GetCurrentContract()
        {
            var start = StartOfRound.Instance;
            if (start == null || start.currentLevel == null)
                return MoonContractType.None;
            return GetContractForLevel(start.currentLevel);
        }

        /// <summary>
        /// #1075: is this peer's reading of the round's contract final, as opposed to merely
        /// untold? <see cref="GetCurrentContract"/> returns <see cref="MoonContractType.None"/> for
        /// both "this round has no contract" and "this machine has not been told yet", and a caller
        /// that may commit something on a no-contract round (Blacksite's selection gate) has to
        /// tell them apart.
        ///
        /// The answer is read from the two dictionaries <see cref="GetContractForLevel"/> already
        /// reads, so it can never disagree with the value that method returns and no new state is
        /// introduced. A peer that receives assignments from the host
        /// (<see cref="IsAssignmentAuthorityRemote"/>) is resolved only once the host snapshot has
        /// covered the current level - that snapshot is exactly what fills these dictionaries on a
        /// client, and a late joiner therefore reads false until it arrives. The host and solo play
        /// roll the table themselves (GetContractForLevel falls back to a local roll), so a current
        /// level is by itself enough there.
        /// </summary>
        internal static bool IsCurrentContractResolved()
        {
            var start = StartOfRound.Instance;
            SelectableLevel level = start?.currentLevel;
            if (level == null)
                return false;

            int key = GetAssignmentKey(level);
            if (key >= 0 && (LockedAssignments.ContainsKey(key) || Assignments.ContainsKey(key)))
                return true;

            return !IsAssignmentAuthorityRemote();
        }

        internal static char GetCurrentRiskTier()
        {
            return GetRiskTier(StartOfRound.Instance?.currentLevel);
        }

        // #694: the shared risk-tier table can be overridden per contract from that
        // contract's own config section (-1 keeps the table value).
        internal static float GetContractRewardFractionForCurrentMoon()
        {
            return GetRewardPenaltyFraction(
                ContractsBootstrap.ContractSelection?.Get(GetCurrentContract())?.RewardPercentOverride?.Value ?? -1);
        }

        internal static float GetContractPenaltyFractionForCurrentMoon()
        {
            return GetRewardPenaltyFraction(
                ContractsBootstrap.ContractSelection?.Get(GetCurrentContract())?.FailurePenaltyPercentOverride?.Value ?? -1);
        }

        private static float GetRewardPenaltyFraction(int overridePercent)
        {
            int percent = overridePercent >= 0
                ? overridePercent
                : ContractConfigRules.GetTierInt(
                    ContractConfigRules.ParseTierIntMap(ContractsBootstrap.ContractRewardPenaltyPercentByTier?.Value),
                    GetCurrentRiskTier(),
                    25);
            return Mathf.Clamp(percent, 0, 100) / 100f;
        }

        internal static int GetSurveyDroneCountForCurrentMoon()
        {
            return Mathf.Clamp(ContractsBootstrap.SurveyBalance.DroneCount(GetCurrentRiskTier()), 1, 8);
        }

        internal static int GetBlackoutBreakerCountForCurrentMoon()
        {
            return Mathf.Clamp(ContractsBootstrap.BlackoutBalance.ForRisk(GetCurrentRiskTier()).BreakerCount.Value, 1, 3);
        }

        private static int GetBlackoutAmbientFlickerIntervalSecondsForCurrentMoon()
        {
            return Mathf.Clamp(ContractsBootstrap.BlackoutBalance.ForRisk(GetCurrentRiskTier()).AmbientFlickerIntervalSeconds.Value, 20, 180);
        }

        internal static bool TryPrepareBlackoutVanillaBreakerSpawns(RoundManager round, out string reason)
        {
            RestoreBlackoutVanillaBreakerSpawnCurves(round);
            reason = "not a Blackout Audit round";
            if (round?.currentLevel == null)
            {
                reason = "round/current level unavailable";
                return false;
            }

            if (GetCurrentContract() != MoonContractType.BlackoutAudit)
                return false;

            int targetCount = GetBlackoutBreakerCountForCurrentMoon();
            RecordBlackoutBreakerSpawnSourceCounts();

            IndoorMapHazard[] hazards = round.currentLevel.indoorMapHazards ?? Array.Empty<IndoorMapHazard>();

            int hazardIndex = -1;
            IndoorMapHazard breakerHazard = null;
            for (int i = 0; i < hazards.Length; i++)
            {
                IndoorMapHazard hazard = hazards[i];
                if (!IsBreakerBoxHazard(hazard))
                    continue;

                hazardIndex = i;
                breakerHazard = hazard;
                break;
            }

            if (breakerHazard == null)
            {
                if (!TryCreateBlackoutBreakerHazardFromSpawnMarkers(round, out breakerHazard, out reason))
                    return false;

                _blackoutOriginalIndoorMapHazards = round.currentLevel.indoorMapHazards;
                _blackoutOriginalIndoorMapHazardsLevel = round.currentLevel;
                hazards = hazards.Concat(new[] { breakerHazard }).ToArray();
                round.currentLevel.indoorMapHazards = hazards;
                hazardIndex = hazards.Length - 1;
            }

            BlackoutBreakerSpawnCurveOverrides.Add(new BlackoutBreakerSpawnCurveOverride
            {
                Hazard = breakerHazard,
                OriginalCurve = breakerHazard.numberToSpawn
            });
            breakerHazard.numberToSpawn = AnimationCurve.Constant(0f, 1f, targetCount);

            if (round.increasedMapHazardSpawnRateIndex == hazardIndex)
            {
                _blackoutOriginalIncreasedMapHazardSpawnRateIndex = round.increasedMapHazardSpawnRateIndex;
                round.increasedMapHazardSpawnRateIndex = -1;
            }

            reason = $"forcing vanilla breaker hazard count to {targetCount}";
            return true;
        }

        private static void RecordBlackoutBreakerSpawnSourceCounts()
        {
            _blackoutBreakerMapObjectMarkerCount = CountBlackoutBreakerMapObjectMarkers();
            _blackoutBreakerSyncedSourceCount = CountBlackoutBreakerSyncedSources();
        }

        private static int CountBlackoutBreakerMapObjectMarkers()
        {
            RandomMapObject[] markers = UnityEngine.Object.FindObjectsOfType<RandomMapObject>();
            if (markers == null || markers.Length == 0)
                return 0;

            int count = 0;
            foreach (RandomMapObject marker in markers)
            {
                if (marker?.spawnablePrefabs == null)
                    continue;

                if (marker.spawnablePrefabs.Any(IsBreakerBoxPrefab))
                    count++;
            }

            return count;
        }

        private static int CountBlackoutBreakerSyncedSources()
        {
            return CollectBlackoutBreakerSyncedMarkers().Count;
        }

        private static List<SpawnSyncedObject> CollectBlackoutBreakerSyncedMarkers()
        {
            var markers = new List<SpawnSyncedObject>();
            SpawnSyncedObject[] sources = UnityEngine.Object.FindObjectsOfType<SpawnSyncedObject>();
            if (sources == null)
                return markers;

            foreach (SpawnSyncedObject source in sources)
            {
                if (source != null && IsBreakerBoxPrefab(source.spawnPrefab))
                    markers.Add(source);
            }

            return markers;
        }

        /// <summary>
        /// Per-marker detail for the capability audit: vanilla spawns one breaker per synced
        /// marker, so a marker with no live breaker beside it is a mount the mod can claim.
        /// Logging the pairing is what proves (or disproves) that 1:1 relationship on flows we
        /// have never captured.
        /// </summary>
        private static string DescribeBlackoutBreakerSyncedMarkers(IReadOnlyList<BreakerBox> liveBreakers)
        {
            List<SpawnSyncedObject> markers = CollectBlackoutBreakerSyncedMarkers();
            if (markers.Count == 0)
                return "<none>";

            var parts = new List<string>(markers.Count);
            for (int i = 0; i < markers.Count; i++)
            {
                Transform marker = markers[i]?.transform;
                if (marker == null)
                    continue;

                Vector3 position = marker.position;
                bool claimed = liveBreakers != null
                    && HasBlackoutBreakerNear(liveBreakers, position, BlackoutBreakerMarkerMatchRadius);
                Tile tile = FindBlackoutTileForPosition(position);
                parts.Add($"[{i}] pos=({position.x:0.0},{position.y:0.0},{position.z:0.0}) tile='{(tile != null ? tile.name : "<none>")}' live={claimed}");
            }

            return parts.Count > 0 ? string.Join(" ", parts) : "<none>";
        }

        private static bool HasBlackoutBreakerNear(IReadOnlyList<BreakerBox> breakers, Vector3 position, float radius)
        {
            if (breakers == null)
                return false;

            float radiusSqr = radius * radius;
            for (int i = 0; i < breakers.Count; i++)
            {
                BreakerBox breaker = breakers[i];
                if (breaker == null || breaker.transform == null)
                    continue;
                if ((breaker.transform.position - position).sqrMagnitude <= radiusSqr)
                    return true;
            }

            return false;
        }

        private static void LogBlackoutBreakerCapabilityAudit(
            int targetCount,
            int vanillaCount,
            int actualCount,
            int effectiveCount,
            int fallbackSpawned,
            IReadOnlyList<BreakerBox> breakers)
        {
            // #716/A4: three Info lines nobody reads during play, each of which pays for
            // itself in work: a RandomMapObject scan, a SpawnSyncedObject scan, a
            // FindBlackoutTileForPosition tile walk and a TryResolveFloorPosition raycast per
            // breaker and per marker — on the landed frame, on every machine. The whole audit
            // now costs nothing unless the diagnostics switch asked for it.
            if (ContractsBootstrap.PerformanceTimingLogging?.Value != true)
                return;

            int markerCount = _blackoutBreakerMapObjectMarkerCount >= 0
                ? _blackoutBreakerMapObjectMarkerCount
                : CountBlackoutBreakerMapObjectMarkers();
            int syncedSourceCount = _blackoutBreakerSyncedSourceCount >= 0
                ? _blackoutBreakerSyncedSourceCount
                : CountBlackoutBreakerSyncedSources();
            string flowName = GetCurrentBlackoutDungeonFlowName();

            ContractsBootstrap.Log?.LogDebug(
                $"[MoonContracts] Blackout breaker capability audit: {GetCurrentContractDebugContext()}, flow='{flowName}', target={targetCount}, mapObjectMarkers={markerCount}, syncedSources={syncedSourceCount}, vanillaBreakers={vanillaCount}, fallbackSpawned={fallbackSpawned}, actualBreakers={actualCount}, effectiveZones={effectiveCount}, supportsTwo={effectiveCount >= 2}, supportsThree={effectiveCount >= 3}.");
            ContractsBootstrap.Log?.LogDebug(
                $"[MoonContracts] Blackout breaker synced markers: {DescribeBlackoutBreakerSyncedMarkers(breakers)}.");
            ContractsBootstrap.Log?.LogDebug(
                $"[MoonContracts] Blackout breaker boxes: {DescribeBlackoutBreakers(breakers)}.");
        }

        private static string DescribeBlackoutBreakers(IReadOnlyList<BreakerBox> breakers)
        {
            if (breakers == null || breakers.Count == 0)
                return "<none>";

            var parts = new List<string>(breakers.Count);
            for (int i = 0; i < breakers.Count; i++)
            {
                BreakerBox breaker = breakers[i];
                if (breaker == null || breaker.transform == null)
                    continue;

                Vector3 position = breaker.transform.position;
                Tile tile = FindBlackoutTileForPosition(position);
                bool spawnedByBlackout = breaker.name != null
                    && breaker.name.StartsWith(BlackoutFallbackBreakerNamePrefix, StringComparison.Ordinal);
                string floor = InteriorAnchorService.TryResolveFloorPosition(position + Vector3.up * 0.75f, out Vector3 floorPoint)
                    ? $"{position.y - floorPoint.y:0.00}"
                    : "<no-floor>";
                parts.Add($"[{i}] name='{breaker.name}' pos=({position.x:0.0},{position.y:0.0},{position.z:0.0}) tile='{(tile != null ? tile.name : "<none>")}' aboveFloor={floor} modSpawned={spawnedByBlackout}");
            }

            return parts.Count > 0 ? string.Join(" ", parts) : "<none>";
        }

        private static string GetCurrentBlackoutDungeonFlowName()
        {
            RoundManager round = RoundManager.Instance;
            if (round != null && round.currentDungeonType >= 0)
                return GetDungeonFlowName(round, round.currentDungeonType);

            try
            {
                return round?.dungeonGenerator?.Generator?.DungeonFlow?.name ?? "<unknown>";
            }
            catch
            {
                return "<unknown>";
            }
        }

        internal static void RestoreBlackoutVanillaBreakerSpawnCurves(RoundManager round = null)
        {
            for (int i = 0; i < BlackoutBreakerSpawnCurveOverrides.Count; i++)
            {
                BlackoutBreakerSpawnCurveOverride restore = BlackoutBreakerSpawnCurveOverrides[i];
                if (restore?.Hazard == null)
                    continue;

                restore.Hazard.numberToSpawn = restore.OriginalCurve;
            }

            BlackoutBreakerSpawnCurveOverrides.Clear();
            if (_blackoutOriginalIndoorMapHazardsLevel != null
                && round?.currentLevel == _blackoutOriginalIndoorMapHazardsLevel)
            {
                round.currentLevel.indoorMapHazards = _blackoutOriginalIndoorMapHazards;
            }

            _blackoutOriginalIndoorMapHazards = null;
            _blackoutOriginalIndoorMapHazardsLevel = null;
            if (round != null && _blackoutOriginalIncreasedMapHazardSpawnRateIndex != int.MinValue)
            {
                round.increasedMapHazardSpawnRateIndex = _blackoutOriginalIncreasedMapHazardSpawnRateIndex;
                _blackoutOriginalIncreasedMapHazardSpawnRateIndex = int.MinValue;
            }
        }

        private static bool TryCreateBlackoutBreakerHazardFromSpawnMarkers(
            RoundManager round,
            out IndoorMapHazard hazard,
            out string reason)
        {
            hazard = null;
            reason = "no breaker-capable vanilla map-object markers found";
            GameObject prefab = FindBlackoutBreakerSpawnPrefab();
            if (prefab == null)
                return false;

            var hazardType = ScriptableObject.CreateInstance<IndoorMapHazardType>();
            hazardType.name = "BlackoutAuditTemporaryBreakerHazard";
            hazardType.prefabToSpawn = prefab;
            if (!TryCopyBlackoutBreakerHazardTemplate(hazardType)
                && !TryCopyBlackoutBreakerSpawnableMapObjectTemplate(round?.currentLevel, prefab, hazardType))
            {
                ApplyDefaultBlackoutBreakerHazardFlags(hazardType);
            }

            hazard = new IndoorMapHazard
            {
                hazardType = hazardType,
                numberToSpawn = AnimationCurve.Constant(0f, 1f, 0f)
            };
            reason = $"temporary vanilla breaker hazard from spawn marker prefab '{prefab.name}'";
            return true;
        }

        private static GameObject FindBlackoutBreakerSpawnPrefab()
        {
            RandomMapObject[] markers = UnityEngine.Object.FindObjectsOfType<RandomMapObject>();
            if (markers != null)
            {
                foreach (RandomMapObject marker in markers)
                {
                    if (marker?.spawnablePrefabs == null)
                        continue;

                    foreach (GameObject prefab in marker.spawnablePrefabs)
                    {
                        if (IsBreakerBoxPrefab(prefab))
                            return prefab;
                    }
                }
            }

            SelectableLevel level = RoundManager.Instance?.currentLevel;
            if (level?.spawnableMapObjects != null)
            {
                foreach (SpawnableMapObject mapObject in level.spawnableMapObjects)
                {
                    if (IsBreakerBoxPrefab(mapObject?.prefabToSpawn))
                        return mapObject.prefabToSpawn;
                }
            }

            return null;
        }

        private static bool TryCopyBlackoutBreakerHazardTemplate(IndoorMapHazardType destination)
        {
            IndoorMapHazardType[] templates = Resources.FindObjectsOfTypeAll<IndoorMapHazardType>();
            if (templates == null)
                return false;

            foreach (IndoorMapHazardType template in templates)
            {
                if (template == null || !IsBreakerBoxPrefab(template.prefabToSpawn))
                    continue;

                CopyBlackoutBreakerHazardFlags(destination, template);
                return true;
            }

            return false;
        }

        private static bool TryCopyBlackoutBreakerSpawnableMapObjectTemplate(
            SelectableLevel level,
            GameObject prefab,
            IndoorMapHazardType destination)
        {
            if (level?.spawnableMapObjects == null)
                return false;

            foreach (SpawnableMapObject template in level.spawnableMapObjects)
            {
                if (template == null || template.prefabToSpawn != prefab)
                    continue;

                destination.spawnFacingAwayFromWall = template.spawnFacingAwayFromWall;
                destination.spawnFacingWall = template.spawnFacingWall;
                destination.spawnWithBackToWall = template.spawnWithBackToWall;
                destination.spawnWithBackFlushAgainstWall = template.spawnWithBackFlushAgainstWall;
                destination.requireDistanceBetweenSpawns = template.requireDistanceBetweenSpawns;
                destination.disallowSpawningNearEntrances = template.disallowSpawningNearEntrances;
                return true;
            }

            return false;
        }

        private static void CopyBlackoutBreakerHazardFlags(IndoorMapHazardType destination, IndoorMapHazardType source)
        {
            destination.spawnFacingAwayFromWall = source.spawnFacingAwayFromWall;
            destination.spawnFacingWall = source.spawnFacingWall;
            destination.spawnWithBackToWall = source.spawnWithBackToWall;
            destination.spawnWithBackFlushAgainstWall = source.spawnWithBackFlushAgainstWall;
            destination.requireDistanceBetweenSpawns = source.requireDistanceBetweenSpawns;
            destination.disallowSpawningNearEntrances = source.disallowSpawningNearEntrances;
            destination.allowInMineshaft = source.allowInMineshaft;
        }

        private static void ApplyDefaultBlackoutBreakerHazardFlags(IndoorMapHazardType hazardType)
        {
            hazardType.spawnFacingAwayFromWall = true;
            hazardType.spawnFacingWall = false;
            hazardType.spawnWithBackToWall = true;
            hazardType.spawnWithBackFlushAgainstWall = true;
            hazardType.requireDistanceBetweenSpawns = true;
            hazardType.disallowSpawningNearEntrances = true;
            hazardType.allowInMineshaft = true;
        }

        private static bool IsBreakerBoxHazard(IndoorMapHazard hazard)
        {
            GameObject prefab = hazard?.hazardType?.prefabToSpawn;
            return IsBreakerBoxPrefab(prefab);
        }

        private static bool IsBreakerBoxPrefab(GameObject prefab)
        {
            return prefab != null
                && (prefab.GetComponent<BreakerBox>() != null
                    || prefab.GetComponentInChildren<BreakerBox>(true) != null);
        }

        internal static float GetShadowRaidDrillDurationSecondsForCurrentMoon()
        {
            return Mathf.Clamp(ContractsBootstrap.ShadowRaidBalance.ForRisk(GetCurrentRiskTier()).DrillDurationSeconds.Value, 5f, 300f);
        }

        internal static int GetShadowRaidContainerCountForCurrentMoon(int availableCount)
        {
            if (availableCount <= 0)
                return 0;

            int count = ContractsBootstrap.ShadowRaidBalance.ForRisk(GetCurrentRiskTier()).ContainerCount.Value;
            return Mathf.Clamp(count, 1, availableCount);
        }

        internal static int GetContainmentWaveCountForCurrentMoon()
        {
            return Mathf.Clamp(ContractsBootstrap.ContainmentBalance.ForRisk(GetCurrentRiskTier()).WaveCount.Value, 1, 8);
        }

        internal static int GetContainmentWaveDurationSecondsForCurrentMoon()
        {
            return Mathf.Clamp(ContractsBootstrap.ContainmentBalance.ForRisk(GetCurrentRiskTier()).WaveDurationSeconds.Value, 15, 300);
        }

        internal static char GetRiskTier(SelectableLevel level)
        {
            return ContractConfigRules.ResolveRiskTier(level?.riskLevel ?? string.Empty);
        }

        internal static string GetCurrentContractDebugContext()
        {
            var start = StartOfRound.Instance;
            SelectableLevel currentLevel = start?.currentLevel;
            MoonContractType contract = currentLevel != null ? GetContractForLevel(currentLevel) : MoonContractType.None;
            return $"current={FormatLevelForLog(currentLevel)}, contract={contract}, display='{MoonContractCatalog.Get(contract).DisplayName}'";
        }

        internal static string GetLevelDebugName(SelectableLevel level)
        {
            return FormatLevelForLog(level);
        }

        internal static MoonContractType GetContractForLevel(int levelIndex)
        {
            var start = StartOfRound.Instance;
            if (start == null || start.levels == null || levelIndex < 0 || levelIndex >= start.levels.Length)
                return MoonContractType.None;
            return GetContractForLevel(start.levels[levelIndex]);
        }

        internal static MoonContractType GetContractForLevelId(int levelId)
        {
            SelectableLevel level = FindLevelById(levelId);
            if (level != null)
                return GetContractForLevel(level);

            if (Assignments.Count == 0)
                RefreshAssignments("lazy level id lookup", force: true);

            if (Assignments.TryGetValue(levelId, out var assigned))
                return assigned;

            return MoonContractType.None;
        }

        internal static MoonContractType GetContractForLevel(SelectableLevel level)
        {
            if (level == null)
                return MoonContractType.None;

            if (ContractsBootstrap.ShadowRaidForceAllMoonsContract != null
                && ContractsBootstrap.ShadowRaidForceAllMoonsContract.Value
                && CanReceiveContract(level))
            {
                return MoonContractType.ShadowRaid;
            }

            if (ContractsBootstrap.DefuseBalance != null
                && ContractsBootstrap.DefuseBalance.ForceAllMoons
                && CanReceiveContract(level))
            {
                return MoonContractType.Defuse;
            }

            if (Assignments.Count == 0)
                RefreshAssignments("lazy init", force: true);

            int key = GetAssignmentKey(level);
            if (key >= 0 && LockedAssignments.TryGetValue(key, out var locked))
                return locked;

            if (key >= 0 && Assignments.TryGetValue(key, out var assigned))
                return assigned;

            // Clients must not invent a contract for a level the host snapshot did not
            // cover; an ad-hoc local roll here can disagree with the host every frame.
            if (IsAssignmentAuthorityRemote())
                return MoonContractType.None;

            int seed = _assignmentSeed != int.MinValue
                ? _assignmentSeed
                : BuildAssignmentSeed(StartOfRound.Instance, BuildCycleKey(StartOfRound.Instance));
            return PickContractForLevel(level, seed);
        }

        // True on machines that receive assignments from the host instead of rolling
        // them: any connected non-server peer. Solo play and hosting both run the
        // embedded server, so this is false there.
        private static bool IsAssignmentAuthorityRemote()
        {
            NetworkManager network = NetworkManager.Singleton;
            return network != null && network.IsListening && !network.IsServer;
        }

        internal static bool LockContractForLevel(int levelIndex, string reason)
        {
            var start = StartOfRound.Instance;
            if (start == null || start.levels == null || levelIndex < 0 || levelIndex >= start.levels.Length)
                return false;

            return LockContractForLevel(start.levels[levelIndex], reason, $"levelIndex={levelIndex}");
        }

        internal static bool LockContractForLevelId(int levelId, string reason)
        {
            SelectableLevel level = FindLevelById(levelId);
            if (level == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Could not lock contract for levelId={levelId}; level was not found ({reason}).");
                return false;
            }

            return LockContractForLevel(level, reason, $"levelId={levelId}");
        }

        private static bool LockContractForLevel(SelectableLevel level, string reason, string source)
        {
            if (level == null)
                return false;

            int key = GetAssignmentKey(level);
            if (key < 0)
                return false;

            MoonContractType contract = GetContractForLevel(level);
            Assignments[key] = contract;
            LockedAssignments[key] = contract;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Locked contract for {FormatLevelForLog(level)} at key={key}: {contract} ({source}; {reason}).");
            return true;
        }

        internal static string GetAssignableContractNameForLevelId(int levelId, string requestedContract)
        {
            MoonContractType requested = ParseContractName(requestedContract);
            SelectableLevel level = FindLevelById(levelId);
            return IsContractAssignableForLevel(level, requested)
                ? requested.ToString()
                : MoonContractType.None.ToString();
        }

        internal static bool TrySetContractForLevelId(int levelId, string contractName)
        {
            MoonContractType requested = ParseContractName(contractName);
            string assignableName = GetAssignableContractNameForLevelId(levelId, contractName);
            MoonContractType assignable = ParseContractName(assignableName);
            SelectableLevel level = FindLevelById(levelId);
            int key = level != null ? GetAssignmentKey(level) : levelId;
            if (key < 0)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Ignored external contract assignment for invalid levelId={levelId}: requested={requested}, caller={GetExternalContractAssignmentCaller()}.");
                return false;
            }

            if (LockedAssignments.TryGetValue(key, out MoonContractType locked))
            {
                Assignments[key] = locked;
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Ignored external contract assignment for locked {FormatLevelForLog(level)} at key={key}: requested={requested}, assignable={assignable}, locked={locked}, levelId={levelId}, caller={GetExternalContractAssignmentCaller()}.");
                return locked == requested;
            }

            StartOfRound start = StartOfRound.Instance;
            if (start != null && !start.inShipPhase && level != null && ReferenceEquals(level, start.currentLevel))
            {
                MoonContractType current = GetContractForLevel(level);
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Ignored external contract assignment for current {FormatLevelForLog(level)} at key={key} while round is underway: requested={requested}, assignable={assignable}, current={current}, levelId={levelId}, caller={GetExternalContractAssignmentCaller()}.");
                return current == requested;
            }

            Assignments[key] = assignable;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] External contract assignment accepted for {FormatLevelForLog(level)} at key={key}: requested={requested}, assigned={assignable}, levelId={levelId}, caller={GetExternalContractAssignmentCaller()}.");
            if (start != null && level != null && ReferenceEquals(level, start.currentLevel))
                MoonContractShipMonitor.RefreshCurrent();
            return assignable == requested;
        }

        internal static string GetDisplayNameForLevel(int levelIndex)
        {
            var start = StartOfRound.Instance;
            if (start == null || start.levels == null || levelIndex < 0 || levelIndex >= start.levels.Length)
                return MoonContractCatalog.Get(MoonContractType.None).DisplayName;

            return GetDisplayNameForLevel(start.levels[levelIndex]);
        }

        internal static string GetDisplayNameForLevelId(int levelId)
        {
            SelectableLevel level = FindLevelById(levelId);
            if (level != null)
                return GetDisplayNameForLevel(level);

            return MoonContractCatalog.Get(GetContractForLevelId(levelId)).DisplayName;
        }

        internal static string GetDisplayNameForLevel(SelectableLevel level)
        {
            MoonContractType contract = GetContractForLevel(level);
            if (contract == MoonContractType.None && ShouldDisplayLiteralNone(level))
                return "None";

            return MoonContractCatalog.Get(contract).DisplayName;
        }

        internal static string GetShortNameForLevel(int levelIndex)
        {
            var start = StartOfRound.Instance;
            if (start == null || start.levels == null || levelIndex < 0 || levelIndex >= start.levels.Length)
                return MoonContractCatalog.Get(MoonContractType.None).ShortName;

            return GetShortNameForLevel(start.levels[levelIndex]);
        }

        internal static string GetShortNameForLevelId(int levelId)
        {
            SelectableLevel level = FindLevelById(levelId);
            if (level != null)
                return GetShortNameForLevel(level);

            return MoonContractCatalog.Get(GetContractForLevelId(levelId)).ShortName;
        }

        internal static string GetShortNameForLevel(SelectableLevel level)
        {
            MoonContractType contract = GetContractForLevel(level);
            if (contract == MoonContractType.None && ShouldDisplayLiteralNone(level))
                return "None";

            return MoonContractCatalog.Get(contract).ShortName;
        }

        internal static string GetGameplayDescriptionForLevel(int levelIndex)
        {
            var start = StartOfRound.Instance;
            if (start == null || start.levels == null || levelIndex < 0 || levelIndex >= start.levels.Length)
                return MoonContractCatalog.Get(MoonContractType.None).GameplayDescription;

            return MoonContractCatalog.Get(GetContractForLevel(start.levels[levelIndex])).GameplayDescription;
        }

        internal static string GetGameplayDescriptionForLevelId(int levelId)
        {
            SelectableLevel level = FindLevelById(levelId);
            if (level != null)
                return MoonContractCatalog.Get(GetContractForLevel(level)).GameplayDescription;

            return MoonContractCatalog.Get(GetContractForLevelId(levelId)).GameplayDescription;
        }

        internal static int GetShipScrapValue()
        {
            int total = 0;
            var scrap = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            foreach (var item in scrap)
            {
                if (item == null || item.itemProperties == null || !item.itemProperties.isScrap)
                    continue;
                if (item.isInShipRoom || item.isInElevator)
                    total += Mathf.Max(0, item.scrapValue);
            }
            return total;
        }

        internal static int GetKnownSalvageTotalValue()
        {
            int shipValue = GetShipScrapValue();
            int interiorValue = GetInteriorScrapValue();
            int observedTotal = Mathf.Max(0, shipValue + interiorValue);
            _salvageKnownTotalValue = Mathf.Max(_salvageKnownTotalValue, observedTotal);
            return Mathf.Max(1, _salvageKnownTotalValue);
        }

        internal static bool CurrentLevelSupportsSalvageContract()
        {
            return CanReceiveContract(StartOfRound.Instance?.currentLevel);
        }

        private static List<MoonContractObjective> GetSalvageObjectives()
        {
            if (!CurrentLevelSupportsSalvageContract())
                return new List<MoonContractObjective>();

            int shipValue = GetShipScrapValue();
            int total = GetKnownSalvageTotalValue();

            return new List<MoonContractObjective>
            {
                new MoonContractObjective(
                    "Collect the scrap",
                    shipValue,
                    total,
                    kind: MoonContractObjectiveKind.Status,
                    key: "salvage.remaining")
            };
        }

        private static int GetInteriorScrapValue()
        {
            int total = 0;
            var scrap = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            foreach (var item in scrap)
            {
                if (item == null || item.itemProperties == null || !item.itemProperties.isScrap)
                    continue;
                if (item.isInFactory && !item.isInShipRoom && !item.isInElevator)
                    total += Mathf.Max(0, item.scrapValue);
            }
            return total;
        }

        internal static List<MoonContractObjective> GetCurrentObjectives()
        {
            return GetObjectives(GetCurrentContract());
        }

        /// <summary>
        /// Allocation-free signature used by event-driven presentation caches.
        /// It changes whenever the per-moon assignment or lock table changes,
        /// without exposing either mutable dictionary.
        /// </summary>
        internal static int GetAssignmentStateToken()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + Assignments.Count;
                foreach (KeyValuePair<int, MoonContractType> pair in Assignments)
                {
                    hash = hash * 31 + pair.Key;
                    hash = hash * 31 + (int)pair.Value;
                }
                hash = hash * 31 + LockedAssignments.Count;
                foreach (KeyValuePair<int, MoonContractType> pair in LockedAssignments)
                    hash = hash * 31 + pair.Key * 31 + (int)pair.Value;
                return hash;
            }
        }

        internal static IReadOnlyList<MoonContractObjectiveMarker> GetCctvObjectiveMarkers()
        {
            var markers = new List<MoonContractObjectiveMarker>();
            // Escalation cargo is relevant even alongside a different contract.
            foreach (ShadowRaidCargoCrate crate in UnityEngine.Object.FindObjectsOfType<ShadowRaidCargoCrate>())
            {
                if (crate == null || !crate.IsLiveThisRound || crate.Opened || crate.SuppressVisualRenderers) continue;
                if (!crate.IsRetrievalCargo && GetCurrentContract() != MoonContractType.ShadowRaid) continue;
                AddCctvMarker(markers, "BLACKSITE CRATE", crate.InteractionPosition, 0.8f, 0, target: crate);
            }
            MoonContractType currentContract = GetCurrentContract();
            bool allowPreRuntime = currentContract == MoonContractType.ShadowRaid;
            if (!allowPreRuntime && !IsContractRuntimeActive())
                return markers;

            switch (currentContract)
            {
                case MoonContractType.Whistleblower:
                    AddWhistleblowerCctvMarkers(markers);
                    break;
                case MoonContractType.BlackoutAudit:
                    AddBlackoutCctvMarkers(markers);
                    break;
                case MoonContractType.ContainmentBreach:
                    // #542 — deliberately emits nothing. Every other contract in
                    // this switch has a physical thing to walk to: a breaker, a
                    // bomb, a cart, an incinerator. ContainmentBreach has none.
                    // Its objectives are timed survival waves (see
                    // GetContainmentBreachObjectives), scored on how long the crew
                    // stays inside a sealed facility, and the lockdown gates
                    // ContainmentBreachDirector prepares are the boundary of the
                    // fight rather than a destination in it. A blip here would
                    // have to be invented, and an invented objective marker is
                    // worse than none. This case exists so the absence reads as a
                    // decision instead of an oversight; if the contract ever gains
                    // a real target object, add AddContainmentBreachCctvMarkers
                    // here alongside the others.
                    break;
                case MoonContractType.ShadowRaid:
                    AddShadowRaidCctvMarkers(markers);
                    break;
                case MoonContractType.Survey:
                    AddSurveyCctvMarkers(markers);
                    break;
                case MoonContractType.Defuse:
                    AddDefuseCctvMarkers(markers);
                    break;
                case MoonContractType.Payload:
                    AddPayloadCctvMarkers(markers);
                    break;
                case MoonContractType.WasteDisposal:
                    AddWasteDisposalCctvMarkers(markers);
                    break;
                case MoonContractType.PestControl:
                    AddPestControlCctvMarkers(markers);
                    break;
                // #804: one blip per LGU-backed directive, on the object LGU spawned - the
                // laptop, the nest, the scavenger, the pentagram, the device.
                case MoonContractType.LguDataRetrieval:
                case MoonContractType.LguExtermination:
                case MoonContractType.LguExtraction:
                case MoonContractType.LguExorcism:
                case MoonContractType.LguDefusal:
                    AddLguCctvMarkers(markers, currentContract);
                    break;
            }

            return markers;
        }

        private const string BlackoutLocateKey = "blackout.locate";

        /// <summary>#1197. DunGen tile bounds for the pity timer's inside test. Filled by
        /// <see cref="PrepareLocateTileBounds"/> once per generated dungeon, outside the collector, into
        /// a buffer kept across rounds; only a dungeon larger than any before it grows the buffer.</summary>
        private static Bounds[] _locateTileBounds = new Bounds[256];
        private static int _locateTileBoundsCount;
        private static Dungeon _locateTileBoundsDungeon;

        /// <summary>
        /// #1197. Snapshots the current dungeon's tile bounds for <see cref="CollectLocateTargets"/>
        /// when the dungeon changed since the last snapshot; otherwise a reference compare. The
        /// objective hint calls it before each sample, outside the collector, so the collector itself
        /// never allocates; the only allocation is a buffer growth for a dungeon with more than 256
        /// tiles (or more than any before it this session), once per such dungeon.
        /// </summary>
        internal static void PrepareLocateTileBounds()
        {
            Dungeon dungeon = ResolveCurrentDungeon();
            if (dungeon == null || dungeon == _locateTileBoundsDungeon)
                return;

            _locateTileBoundsDungeon = dungeon;
            _locateTileBoundsCount = 0;
            var tiles = dungeon.AllTiles;
            if (tiles == null)
                return;

            int count = tiles.Count;
            if (_locateTileBounds.Length < count)
                _locateTileBounds = new Bounds[Mathf.NextPowerOfTwo(count)];
            for (int i = 0; i < count; i++)
            {
                Tile tile = tiles[i];
                if (tile != null)
                    _locateTileBounds[_locateTileBoundsCount++] = tile.Bounds;
            }
        }

        private static Dungeon ResolveCurrentDungeon()
        {
            RoundManager round = RoundManager.Instance;
            RuntimeDungeon generator = round != null ? round.dungeonGenerator : null;
            return generator != null && generator.Generator != null ? generator.Generator.CurrentDungeon : null;
        }

        /// <summary>
        /// #1197. The current contract's locate row, for the objective pity timer. Reads the same
        /// target sources <see cref="GetCctvObjectiveMarkers"/> uses, but allocates nothing, runs no
        /// scene search and calls no Ensure* entry point: it only reads what the contract runtime
        /// and the card's own objective poll already maintain.
        ///
        /// <para>Returns false when the current contract has no locate row the timer may use:
        /// no contract, Survey, Shadow Raid, Containment Breach, a failed or finished contract, a
        /// Blackout without breaker zones. Wesley side objectives are never read here. Otherwise
        /// <paramref name="rowKey"/> is the row's #520 key, <paramref name="rowFound"/> whether the
        /// row reads found, <paramref name="foundCount"/> its found progress (found zones for
        /// Blackout, 0 or 1 elsewhere), and <paramref name="insideTargets"/> receives every
        /// unfound target inside the facility. Inside is the replicated entity flag where one
        /// exists (Whistleblower <c>IsInsideFacility</c>; Payload's <c>IsOutside</c> side, and the
        /// tile test as well, because that side only moves when the cart crosses an entrance) and
        /// a test against the tile bounds <see cref="PrepareLocateTileBounds"/> took everywhere
        /// else. Without a snapshot for the current dungeon nothing reads as inside.</para>
        /// </summary>
        internal static bool CollectLocateTargets(List<Component> insideTargets, out string rowKey, out bool rowFound, out int foundCount)
        {
            rowKey = null;
            rowFound = false;
            foundCount = 0;
            if (insideTargets == null)
                return false;
            insideTargets.Clear();
            if (!IsContractRuntimeActive())
                return false;

            MoonContractType contract = GetCurrentContract();
            switch (contract)
            {
                case MoonContractType.Defuse:
                {
                    DefuseBombController bomb = DefuseContract.ActiveBomb;
                    if (bomb == null || bomb.IsDefused || bomb.IsFailed)
                        return false;
                    rowKey = DefuseContract.LocateKey;
                    rowFound = DefuseContract.Located;
                    AddTileInsideTarget(insideTargets, bomb, rowFound);
                    break;
                }
                case MoonContractType.WasteDisposal:
                {
                    WasteDisposalIncineratorController incinerator = WasteDisposalContract.ActiveIncinerator;
                    if (incinerator == null || incinerator.IsComplete)
                        return false;
                    rowKey = WasteDisposalContract.LocateKey;
                    rowFound = WasteDisposalContract.Located;
                    AddTileInsideTarget(insideTargets, incinerator, rowFound);
                    break;
                }
                case MoonContractType.PestControl:
                {
                    PestControlTrapController trap = PestControlContract.ActiveTrap;
                    if (trap == null || trap.FootprintMeters <= 0f || trap.CapturedTotal > 0)
                        return false;
                    rowKey = PestControlContract.LocateKey;
                    rowFound = PestControlContract.Located;
                    AddTileInsideTarget(insideTargets, trap, rowFound);
                    break;
                }
                case MoonContractType.Payload:
                {
                    PayloadCartController cart = PayloadContract.ActiveCart;
                    if (cart == null || cart.IsDelivered || cart.IsFailed)
                        return false;
                    rowKey = PayloadContract.LocateKey;
                    // The row's own term: the replicated one-way located flag, or already out. The
                    // cart's own two-way side flag moves only when the cart crosses an entrance, so an
                    // unfound cart is still tested against the tiles before it counts as inside.
                    rowFound = cart.IsLocated || cart.IsOutside;
                    AddTileInsideTarget(insideTargets, cart, rowFound);
                    break;
                }
                case MoonContractType.BlackoutAudit:
                    if (!CollectBlackoutLocateTargets(insideTargets, out foundCount))
                        return false;
                    rowKey = BlackoutLocateKey;
                    rowFound = foundCount >= BlackoutBreakerZones.Count;
                    return true;
                case MoonContractType.Whistleblower:
                {
                    WhistleblowerEnemyAI witness = _whistleblowerEnemy;
                    if (witness == null || witness.Neutralized || _whistleblowerNeutralized || _whistleblowerExtracted)
                        return false;
                    rowKey = WhistleblowerObjectiveLines.LocateKey;
                    // The row reads Neutralize, not Locate, while he is in sight or held. It reads
                    // Locate again once he slips away, which is a new unfound objective.
                    rowFound = witness.IsObservedRecently || witness.IsInCustody;
                    if (!rowFound && witness.IsInsideFacility)
                        insideTargets.Add(witness);
                    break;
                }
                case MoonContractType.LguDataRetrieval:
                case MoonContractType.LguExtermination:
                case MoonContractType.LguExtraction:
                case MoonContractType.LguExorcism:
                case MoonContractType.LguDefusal:
                {
                    if (!LguContractRuntime.TryGetLocateState(contract, out rowKey, out rowFound, out Component target))
                        return false;
                    if (target != null && target.gameObject.activeInHierarchy)
                        AddTileInsideTarget(insideTargets, target, rowFound);
                    break;
                }
                default:
                    // Survey, Shadow Raid and Containment Breach carry no locate row (#1197).
                    return false;
            }

            foundCount = rowFound ? 1 : 0;
            return true;
        }

        /// <summary>#1197. Every unrestored, unfound breaker inside the facility. Reads the zone latches
        /// the card's Blackout poll maintains; it never advances them.</summary>
        private static bool CollectBlackoutLocateTargets(List<Component> insideTargets, out int foundCount)
        {
            foundCount = 0;
            if (_blackoutUnsupportedThisRound || _blackoutCompleted || BlackoutBreakerZones.Count == 0)
                return false;

            for (int i = 0; i < BlackoutBreakerZones.Count; i++)
            {
                BlackoutBreakerZone zone = BlackoutBreakerZones[i];
                if (zone == null)
                    continue;
                if (zone.Found || zone.Restored)
                {
                    foundCount++;
                    continue;
                }
                if (zone.Breaker != null && IsInsideFacilityTiles(zone.Breaker.transform.position))
                    insideTargets.Add(zone.Breaker);
            }

            return true;
        }

        private static void AddTileInsideTarget(List<Component> insideTargets, Component target, bool rowFound)
        {
            if (!rowFound && target != null && IsInsideFacilityTiles(target.transform.position))
                insideTargets.Add(target);
        }

        /// <summary>#1197. Inside the generated dungeon's tile bounds, XYZ with the Blackout helpers'
        /// 0.4 m slack. The facility sits far below the moon surface, so the height test is what
        /// keeps an outdoor object over the facility's footprint outside. Reads the snapshot only:
        /// one taken for another dungeon, or none, reads as outside.</summary>
        private static bool IsInsideFacilityTiles(Vector3 position)
        {
            Dungeon dungeon = ResolveCurrentDungeon();
            if (dungeon == null || dungeon != _locateTileBoundsDungeon)
                return false;

            for (int i = 0; i < _locateTileBoundsCount; i++)
            {
                Bounds bounds = _locateTileBounds[i];
                if (position.x >= bounds.min.x - 0.4f && position.x <= bounds.max.x + 0.4f
                    && position.z >= bounds.min.z - 0.4f && position.z <= bounds.max.z + 0.4f
                    && position.y >= bounds.min.y - 0.4f && position.y <= bounds.max.y + 0.4f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Text and key of the shared closing objective every contract ends on.</summary>
        internal const string ReturnObjectiveText = "Return to Orbit";
        internal const string ReturnObjectiveKey = "contract.return";

        /// <summary>
        /// The contract's objective list plus the shared closing line. Every contract ends on
        /// <see cref="ReturnObjectiveText"/> (Lawson's 2026-09-04 decision under #725): it
        /// completes once the rest of the list is complete and the ship is on its way out,
        /// which is the same <c>shipIsLeaving</c> gate the round-end outcome capture runs on.
        /// Survey and Pest Control used to carry their own departure rows in two different
        /// wordings and the card appended a third; this is the one place that line lives now.
        /// Salvage (no contract) has its own scrap telemetry and takes no closing line.
        /// </summary>
        internal static List<MoonContractObjective> GetObjectives(MoonContractType contractType)
        {
            List<MoonContractObjective> objectives = GetContractObjectives(contractType);
            if (contractType == MoonContractType.None || objectives == null || objectives.Count == 0)
                return objectives;

            bool othersComplete = true;
            for (int i = 0; i < objectives.Count; i++)
                othersComplete &= objectives[i].IsComplete;

            StartOfRound start = StartOfRound.Instance;
            bool departed = othersComplete && start != null && start.shipIsLeaving;
            objectives.Add(new MoonContractObjective(ReturnObjectiveText, departed ? 1 : 0, 1, key: ReturnObjectiveKey));
            return objectives;
        }

        private static List<MoonContractObjective> GetContractObjectives(MoonContractType contractType)
        {
            switch (contractType)
            {
                case MoonContractType.Whistleblower:
                    return GetWhistleblowerObjectives();
                case MoonContractType.BlackoutAudit:
                    return GetBlackoutAuditObjectives();
                case MoonContractType.ShadowRaid:
                    return GetShadowRaidObjectives();
                case MoonContractType.ContainmentBreach:
                    return GetContainmentBreachObjectives();
                case MoonContractType.Survey:
                    return GetSurveyObjectives();
                case MoonContractType.Defuse:
                    return DefuseContract.GetObjectives();
                case MoonContractType.Payload:
                    return PayloadContract.GetObjectives();
                case MoonContractType.WasteDisposal:
                    return WasteDisposalContract.GetObjectives();
                case MoonContractType.PestControl:
                    return PestControlContract.GetObjectives();
                // #804: the LGU-backed directives derive every line from state Lategame Upgrades
                // has already replicated. They must not fall through to the Salvage objectives
                // below - that would put "Recover valuable scrap" on a contract that is not
                // about scrap.
                case MoonContractType.LguDataRetrieval:
                case MoonContractType.LguExtermination:
                case MoonContractType.LguExtraction:
                case MoonContractType.LguExorcism:
                case MoonContractType.LguDefusal:
                    return LguContractRuntime.GetObjectives(contractType);
                default:
                    return GetSalvageObjectives();
            }
        }

        internal static bool IsCurrentContractComplete()
        {
            var objectives = GetCurrentObjectives();
            if (objectives.Count == 0)
                return true;

            foreach (var objective in objectives)
            {
                if (!objective.IsComplete)
                    return false;
            }
            return true;
        }

        internal static bool IsCurrentContractFailed()
        {
            var objectives = GetCurrentObjectives();
            foreach (var objective in objectives)
            {
                if (objective.IsFailed)
                    return true;
            }

            return false;
        }

        internal static (int Complete, int Total) GetObjectiveSummary()
        {
            int complete = 0;
            int total = 0;
            var objectives = GetCurrentObjectives();

            foreach (var objective in objectives)
            {
                total += Mathf.Max(1, objective.Total);
                complete += Mathf.Clamp(objective.Complete, 0, Mathf.Max(1, objective.Total));
            }

            return (complete, total);
        }

        internal static void UpdateActiveContractMechanics()
        {
            long step = System.Diagnostics.Stopwatch.GetTimestamp();
            TryRegisterAssignmentSnapshotHandler();
            step = MarkMechanicsStep(MechanicsStepRegisterHandler, step);
            // #406: TryRegisterAssignmentSnapshotHandler short-circuits once its own messaging
            // manager is latched, so the progression channel needs its own tick to retry the
            // replay request a client's first attempt may have raced. This is the only call
            // site: #503 removed the duplicate inside TryRegisterAssignmentSnapshotHandler,
            // which could only fire on a messaging-manager change this line already covers.
            // #1076: the departure subscriber goes up BEFORE the transport, so a replayed
            // departure event cannot land on a peer that has nothing listening for it. Both are
            // idempotent and both run on clients, which is the point of this pair of lines.
            EnsureBlacksiteDepartureHandler();
            ContractProgressionNetwork.EnsureNetworkHandlers();
            step = MarkMechanicsStep(MechanicsStepProgressionHandlers, step);
            // #438: runs above the runtime-active gate below - the round-end report has to be
            // retried while the crew is in orbit, when no contract runtime is active.
            MoonContractRoundEndState.TickPendingRoundEndBroadcast();
            step = MarkMechanicsStep(MechanicsStepRoundEndBroadcast, step);
            if (NetworkManager.Singleton?.IsServer == true && Time.realtimeSinceStartup >= _nextAssignmentSnapshotAt)
            {
                _nextAssignmentSnapshotAt = Time.realtimeSinceStartup + 5f;
                SendAssignmentSnapshot();
                SendBlackoutZoneSnapshot();
                step = MarkMechanicsStep(MechanicsStepSnapshotBroadcast, step);
            }
            if (!IsContractRuntimeActive())
            {
                if (HasInactiveBlackoutState())
                    ClearInactiveBlackoutState("contract runtime inactive");
                _lastRuntimeContract = MoonContractType.None;
                _nextWhistleblowerGuardSweepAt = 0f;
                _mechanicsBranchLabel = null;
                return;
            }

            MoonContractType currentContract = GetCurrentContract();
            HandleRuntimeContractTransition(currentContract);
            step = MarkMechanicsStep(MechanicsStepContractTransition, step);
            // The branch cost is meaningless without knowing which contract paid it, so the
            // active contract's name rides along into the slow-pass line.
            _mechanicsBranchLabel = currentContract.ToString();

            switch (currentContract)
            {
                case MoonContractType.BlackoutAudit:
                    UpdateBlackoutAudit();
                    break;
                case MoonContractType.ShadowRaid:
                    break;
                case MoonContractType.Whistleblower:
                    UpdateWhistleblowerContract();
                    break;
                case MoonContractType.ContainmentBreach:
                    UpdateContainmentBreach();
                    break;
                case MoonContractType.Survey:
                    UpdateSurvey();
                    break;
                case MoonContractType.Defuse:
                    DefuseContract.UpdateRuntime();
                    break;
                case MoonContractType.Payload:
                    PayloadContract.UpdateRuntime();
                    break;
                case MoonContractType.WasteDisposal:
                    WasteDisposalContract.UpdateRuntime();
                    break;
                case MoonContractType.PestControl:
                    PestControlContract.UpdateRuntime();
                    break;
                // #804: LGU drives the contract itself; Contracted's tick only reads it. It runs
                // on clients too, because every objective on these cards is derived locally.
                case MoonContractType.LguDataRetrieval:
                case MoonContractType.LguExtermination:
                case MoonContractType.LguExtraction:
                case MoonContractType.LguExorcism:
                case MoonContractType.LguDefusal:
                    LguContractRuntime.UpdateRuntime(currentContract);
                    break;
            }

            MarkMechanicsStep(MechanicsStepContractBranch, step);
        }

        // #503: the 2026-08-14 log recorded a 26 ms single-frame spike attributed only to
        // "MoonContractState.UpdateActiveContractMechanics" - the same shape as the #290
        // BlackoutAudit incident, where the owner stayed hidden until the pass was broken
        // into named steps. These nested steps are recorded unconditionally (two timestamp
        // reads per step) and are only formatted when the outer harness in
        // MoonContractRuntimePatch already decided the pass was slow.
        private const int MechanicsStepRegisterHandler = 0;
        private const int MechanicsStepProgressionHandlers = 1;
        private const int MechanicsStepRoundEndBroadcast = 2;
        private const int MechanicsStepSnapshotBroadcast = 3;
        private const int MechanicsStepContractTransition = 4;
        private const int MechanicsStepContractBranch = 5;

        private static readonly string[] MechanicsStepNames =
        {
            "TryRegisterAssignmentSnapshotHandler",
            "ContractProgressionNetwork.EnsureNetworkHandlers",
            "TickPendingRoundEndBroadcast",
            "AssignmentSnapshotBroadcast",
            "HandleRuntimeContractTransition",
            "ContractBranch",
        };

        // Sized from MechanicsStepNames so adding a step cannot silently desync the two.
        private static readonly double[] MechanicsStepMilliseconds = new double[MechanicsStepNames.Length];
        private static string _mechanicsBranchLabel;

        private static long MarkMechanicsStep(int index, long stepStart)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            MechanicsStepMilliseconds[index] =
                (now - stepStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return now;
        }

        /// <summary>Appends the per-step breakdown of the last mechanics pass. Called only from
        /// the slow-pass report, and only when this step was itself over the report threshold.</summary>
        internal static void AppendMechanicsBreakdown(System.Text.StringBuilder line, double stepThresholdMilliseconds)
        {
            for (int i = 0; i < MechanicsStepMilliseconds.Length; i++)
            {
                if (MechanicsStepMilliseconds[i] < stepThresholdMilliseconds)
                    continue;
                line.Append(' ');
                line.Append(MechanicsStepNames[i]);
                if (i == MechanicsStepContractBranch && !string.IsNullOrEmpty(_mechanicsBranchLabel))
                {
                    line.Append('(');
                    line.Append(_mechanicsBranchLabel);
                    line.Append(')');
                }
                line.Append('=');
                line.Append(MechanicsStepMilliseconds[i].ToString("0.00"));
                line.Append("ms");
            }
        }

        internal static void ClearMechanicsStepTimes()
        {
            for (int i = 0; i < MechanicsStepMilliseconds.Length; i++)
                MechanicsStepMilliseconds[i] = 0.0;
        }

        private static void TryRegisterAssignmentSnapshotHandler()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsListening || network.CustomMessagingManager == null) return;
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (ReferenceEquals(messaging, _assignmentMessaging)) return;
            if (_assignmentMessaging != null)
            {
                try { _assignmentMessaging.UnregisterNamedMessageHandler(AssignmentSnapshotMessage); } catch { }
                try { _assignmentMessaging.UnregisterNamedMessageHandler(BlackoutZoneSnapshotMessage); } catch { }
                try { _assignmentMessaging.UnregisterNamedMessageHandler(BlackoutSurgeMessage); } catch { }
            }
            messaging.RegisterNamedMessageHandler(AssignmentSnapshotMessage, OnAssignmentSnapshot);
            messaging.RegisterNamedMessageHandler(BlackoutZoneSnapshotMessage, OnBlackoutZoneSnapshot);
            messaging.RegisterNamedMessageHandler(BlackoutSurgeMessage, OnBlackoutSurge);
            // #503: ContractProgressionNetwork.EnsureNetworkHandlers() used to run here too.
            // UpdateActiveContractMechanics calls it unconditionally on every pass immediately
            // after this method returns, so this call could never observe a state the caller
            // had not already handled in the same frame.
            QuotaSyncNetwork.EnsureNetworkHandlers();
            _assignmentMessaging = messaging;
            _nextAssignmentSnapshotAt = 0f;
        }

        private static void SendAssignmentSnapshot()
        {
            StartOfRound start = StartOfRound.Instance;
            CustomMessagingManager manager = NetworkManager.Singleton?.CustomMessagingManager;
            if (start?.levels == null || manager == null || NetworkManager.Singleton?.IsServer != true) return;
            using (var writer = new FastBufferWriter(
                sizeof(int) + start.levels.Length * (sizeof(int) * 2 + sizeof(byte)) + LguProgressTrailerMaxBytes,
                Allocator.Temp))
            {
                writer.WriteValueSafe(start.levels.Length);
                for (int i = 0; i < start.levels.Length; i++)
                {
                    writer.WriteValueSafe(i);
                    writer.WriteValueSafe((int)(Assignments.TryGetValue(i, out MoonContractType contract) ? contract : MoonContractType.None));
                    writer.WriteValueSafe(LockedAssignments.ContainsKey(i) ? (byte)1 : (byte)0);
                }
                WriteLguProgressTrailer(writer);
                manager.SendNamedMessageToAll(AssignmentSnapshotMessage, writer);
            }
        }

        /// <summary>
        /// #804. Worst-case size of the LGU progress trailer: the present-marker, the three
        /// identity ints, the four state bytes, the has-serial marker, and a length-prefixed
        /// serial. LGU's serials are six characters, so 64 bytes is a wide margin - and it has
        /// to be a margin, because a <c>FastBufferWriter</c> built with a single capacity cannot
        /// grow past it.
        /// </summary>
        private const int LguProgressTrailerMaxBytes = 1 + sizeof(int) * 3 + 5 + 64;

        /// <summary>
        /// #804. The round the trailer is stamped with, spelled exactly as the progression
        /// ledger spells its round id: the map seed and the level. Both are host-owned values
        /// the vanilla start sequence replicates before anyone lands, so the host and every
        /// client derive the same pair without Contracted owning a counter of its own.
        /// </summary>
        private static void GetLguTrailerRound(out int seed, out int levelId)
        {
            StartOfRound start = StartOfRound.Instance;
            seed = start != null ? start.randomMapSeed : 0;
            levelId = start?.currentLevel != null ? start.currentLevel.levelID : -1;
        }

        /// <summary>
        /// #804. Appends this round's LGU objective progress so a late joiner, who missed every
        /// one of LGU's progress ClientRpcs, still gets a card that reflects the round.
        ///
        /// Appended rather than versioned, the way <c>QuotaRoundEndReport</c> appends
        /// <c>QuotaOverhaulEnabled</c> (#748). A host without this build simply stops writing
        /// after the level entries and the reader below finds no bytes left; a client without it
        /// reads the entries it knows and ignores the tail. Neither direction needs a version
        /// number, because the trailer is only ever read after a complete level table.
        ///
        /// The contract type and the round stamp lead the payload. The snapshot rebroadcasts
        /// every five seconds and the payload only ever raises progress, so without an identity
        /// a trailer written at the end of one round would fold into the next one - or into a
        /// different directive - and close objectives nobody had touched.
        /// </summary>
        private static void WriteLguProgressTrailer(FastBufferWriter writer)
        {
            MoonContractType current = GetCurrentContract();
            if (!LguContractRuntime.HasSnapshotPayload(current))
            {
                writer.WriteValueSafe((byte)0);
                return;
            }

            LguContractRuntime.GetSnapshotPayload(
                out byte flags,
                out byte demonIndex,
                out byte ritualRemaining,
                out byte wiresRemaining,
                out string serial);
            GetLguTrailerRound(out int roundSeed, out int roundLevelId);

            writer.WriteValueSafe((byte)1);
            writer.WriteValueSafe((int)current);
            writer.WriteValueSafe(roundSeed);
            writer.WriteValueSafe(roundLevelId);
            writer.WriteValueSafe(flags);
            writer.WriteValueSafe(demonIndex);
            writer.WriteValueSafe(ritualRemaining);
            writer.WriteValueSafe(wiresRemaining);
            writer.WriteValueSafe(string.IsNullOrEmpty(serial) ? (byte)0 : (byte)1);
            if (!string.IsNullOrEmpty(serial))
                writer.WriteValueSafe(serial);
        }

        /// <summary>
        /// #804. Reads the trailer if the host sent one. Nothing is cleared on a miss: the
        /// payload only ever adds progress this peer has not seen for itself.
        ///
        /// A trailer stamped with another contract or another round is dropped outright. A host
        /// on an older build writes the level table and stops, or writes a trailer without the
        /// stamp; both leave fewer bytes here than the header needs, so the guarded read simply
        /// finds nothing and this peer keeps deriving its card locally.
        /// </summary>
        private static void ReadLguProgressTrailer(FastBufferReader reader)
        {
            if (!reader.TryBeginRead(sizeof(byte)))
                return;

            reader.ReadByte(out byte present);
            if (present == 0)
                return;

            if (!reader.TryBeginRead(sizeof(int) * 3 + sizeof(byte) * 5))
                return;

            reader.ReadValueSafe(out int contractValue);
            reader.ReadValueSafe(out int roundSeed);
            reader.ReadValueSafe(out int roundLevelId);
            reader.ReadByte(out byte flags);
            reader.ReadByte(out byte demonIndex);
            reader.ReadByte(out byte ritualRemaining);
            reader.ReadByte(out byte wiresRemaining);
            reader.ReadByte(out byte hasSerial);

            string serial = null;
            if (hasSerial != 0)
                reader.ReadValueSafe(out serial);

            MoonContractType current = GetCurrentContract();
            if (current == MoonContractType.None || contractValue != (int)current)
                return;

            GetLguTrailerRound(out int seed, out int levelId);
            if (!LguProgressRules.IsTrailerForCurrentRound(roundSeed, seed, roundLevelId, levelId))
                return;

            LguContractRuntime.ApplySnapshotPayload(flags, demonIndex, ritualRemaining, wiresRemaining, serial);
        }

        private static void OnAssignmentSnapshot(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId) return;
            // The server's own assignments are authoritative; applying its own broadcast
            // would wipe LockedAssignments and reopen locked contracts to overwrites.
            if (NetworkManager.Singleton?.IsServer == true) return;
            reader.ReadValueSafe(out int count);
            StartOfRound start = StartOfRound.Instance;
            if (count < 0 || count > 1024) return;
            Assignments.Clear();
            LockedAssignments.Clear();
            for (int entry = 0; entry < count; entry++)
            {
                reader.ReadValueSafe(out int index);
                reader.ReadValueSafe(out int contractValue);
                reader.ReadValueSafe(out byte lockedFlag);
                if (index < 0 || !Enum.IsDefined(typeof(MoonContractType), contractValue)) continue;
                MoonContractType contract = (MoonContractType)contractValue;
                Assignments[index] = contract;
                if (lockedFlag != 0)
                    LockedAssignments[index] = contract;
                SelectableLevel level = start?.levels != null && index < start.levels.Length ? start.levels[index] : null;
                if (level != null && level.levelID >= 0 && level.levelID != index)
                {
                    Assignments[level.levelID] = contract;
                    if (lockedFlag != 0)
                        LockedAssignments[level.levelID] = contract;
                }
            }
            ReadLguProgressTrailer(reader);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Applied host assignment snapshot ({count} level entries).");
            MoonContractShipMonitor.RefreshCurrent();
        }

        // Blackout zone membership and restore state are computed host-side and
        // pushed over the same weave-free named-message channel as assignments;
        // clients only ever apply. Light writes stay local: MaintainBlackoutPowerOff
        // re-applies zone power every ~1s on every machine once the flags agree.
        private static void SendBlackoutZoneSnapshot()
        {
            if (BlackoutBreakerZones.Count == 0 && !_blackoutCompleted)
                return;
            CustomMessagingManager manager = NetworkManager.Singleton?.CustomMessagingManager;
            if (manager == null || NetworkManager.Singleton?.IsServer != true)
                return;

            using (var writer = new FastBufferWriter(
                sizeof(byte) + sizeof(int) + BlackoutBreakerZones.Count * (sizeof(ulong) + sizeof(byte)), Allocator.Temp))
            {
                writer.WriteValueSafe(_blackoutCompleted ? (byte)1 : (byte)0);
                writer.WriteValueSafe(BlackoutBreakerZones.Count);
                foreach (BlackoutBreakerZone zone in BlackoutBreakerZones)
                {
                    NetworkObject networkObject = zone?.Breaker != null
                        ? zone.Breaker.GetComponentInParent<NetworkObject>()
                        : null;
                    writer.WriteValueSafe(networkObject != null ? networkObject.NetworkObjectId : 0UL);
                    writer.WriteValueSafe(zone != null && zone.Restored ? (byte)1 : (byte)0);
                }
                manager.SendNamedMessageToAll(BlackoutZoneSnapshotMessage, writer);
            }
        }

        private static void OnBlackoutZoneSnapshot(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId) return;
            if (NetworkManager.Singleton?.IsServer == true) return;

            reader.ReadValueSafe(out byte completedFlag);
            reader.ReadValueSafe(out int count);
            if (count < 0 || count > 16) return;

            var spawned = NetworkManager.Singleton?.SpawnManager?.SpawnedObjects;
            var breakers = new List<BreakerBox>(count);
            var restoredFlags = new List<bool>(count);
            bool resolvedAll = true;
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out ulong networkObjectId);
                reader.ReadValueSafe(out byte restoredFlag);
                BreakerBox breaker = null;
                if (networkObjectId != 0UL && spawned != null
                    && spawned.TryGetValue(networkObjectId, out NetworkObject networkObject)
                    && networkObject != null)
                {
                    breaker = networkObject.GetComponentInChildren<BreakerBox>(true);
                }
                if (breaker == null)
                    resolvedAll = false;
                breakers.Add(breaker);
                restoredFlags.Add(restoredFlag != 0);
            }

            if (completedFlag != 0)
            {
                if (!_blackoutCompleted)
                    CompleteBlackoutAudit("host zone snapshot");
                return;
            }

            if (count == 0 || GetCurrentContract() != MoonContractType.BlackoutAudit
                || _blackoutCompleted || _blackoutUnsupportedThisRound)
                return;
            // A breaker the host knows about has not replicated here yet; the next
            // periodic snapshot retries rather than building a diverged zone list.
            if (!resolvedAll)
                return;

            bool matches = BlackoutBreakerZones.Count == count;
            if (matches)
            {
                for (int i = 0; i < count; i++)
                {
                    if (!ReferenceEquals(BlackoutBreakerZones[i].Breaker, breakers[i]))
                    {
                        matches = false;
                        break;
                    }
                }
            }

            if (!matches)
            {
                // #716/A4: the rebuild runs the same sliced fixture build the host-side init
                // uses, so a mismatching client pays the same metered cost instead of a
                // second full Light-versus-every-tile sweep in one frame. The restore flags
                // ride along with the request and are applied when the build lands.
                _blackoutSnapshotRebuildBreakers = breakers;
                _blackoutSnapshotRebuildRestored = restoredFlags;
                PumpBlackoutSnapshotRebuild();
                return;
            }

            bool restoredChanged = false;
            List<BlackoutBreakerZone> newlyRestored = null;
            for (int i = 0; i < BlackoutBreakerZones.Count && i < restoredFlags.Count; i++)
            {
                if (BlackoutBreakerZones[i].Restored != restoredFlags[i])
                {
                    BlackoutBreakerZones[i].Restored = restoredFlags[i];
                    restoredChanged = true;
                    if (restoredFlags[i])
                        (newlyRestored ??= new List<BlackoutBreakerZone>()).Add(BlackoutBreakerZones[i]);
                }
            }

            if (restoredChanged || !matches)
            {
                ApplyBlackoutZonePowerState();
                // #775: clients see the restore only through this snapshot, so the
                // power-on flicker has to start here or they never get one.
                if (newlyRestored != null && !_blackoutCompleted)
                {
                    foreach (BlackoutBreakerZone zone in newlyRestored)
                        BeginBlackoutRestoreFlicker(zone);
                }
                MarkBlackoutZoneStateDirty(1.25f);
                if (restoredChanged)
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout zone restore flags applied from host snapshot: {BlackoutBreakerZones.Count(z => z.Restored)}/{BlackoutBreakerZones.Count} restored.");
            }
        }

        // #716/A4: a client zone rebuild parked until the sliced fixture build finishes.
        private static List<BreakerBox> _blackoutSnapshotRebuildBreakers;
        private static List<bool> _blackoutSnapshotRebuildRestored;

        private static bool IsBlackoutSnapshotRebuildPending => _blackoutSnapshotRebuildBreakers != null;

        private static void ClearBlackoutSnapshotRebuild()
        {
            _blackoutSnapshotRebuildBreakers = null;
            _blackoutSnapshotRebuildRestored = null;
        }

        /// <summary>Advances a parked client zone rebuild by one slice. Returns true while the
        /// rebuild owns the fixture build, so the normal init pass stands aside.</summary>
        private static bool PumpBlackoutSnapshotRebuild()
        {
            if (!IsBlackoutSnapshotRebuildPending)
                return false;

            List<BreakerBox> breakers = _blackoutSnapshotRebuildBreakers;
            if (_blackoutCompleted || _blackoutUnsupportedThisRound
                || GetCurrentContract() != MoonContractType.BlackoutAudit
                || breakers.Count == 0
                || breakers.Any(breaker => breaker == null))
            {
                // A breaker despawned mid-rebuild, or the contract moved on: drop the request
                // rather than registering a diverged zone list. The next snapshot retries.
                ResetBlackoutFixtureBuild();
                ClearBlackoutSnapshotRebuild();
                return false;
            }

            if (!IsBlackoutFixtureBuildInProgress)
                ClearBlackoutZoneState(destroySpawnedBreakers: false);

            switch (PumpBlackoutPoweredFixtureBuild())
            {
                case BlackoutFixtureBuildResult.Pending:
                    return true;
                case BlackoutFixtureBuildResult.Failed:
                    ClearBlackoutSnapshotRebuild();
                    return false;
            }

            List<bool> restoredFlags = _blackoutSnapshotRebuildRestored;
            ClearBlackoutSnapshotRebuild();

            for (int i = 0; i < breakers.Count; i++)
                RegisterBlackoutBreakerZone(breakers[i], i, spawnedByBlackout: false);
            AssignBlackoutFixturesToZones();
            _blackoutInitialized = true;
            _blackoutBreaker = BlackoutBreakerZones.Count > 0 ? BlackoutBreakerZones[0].Breaker : null;

            for (int i = 0; i < BlackoutBreakerZones.Count && restoredFlags != null && i < restoredFlags.Count; i++)
                BlackoutBreakerZones[i].Restored = restoredFlags[i];

            ApplyBlackoutZonePowerState();
            MarkBlackoutZoneStateDirty(1.25f);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout zones rebuilt from host snapshot ({breakers.Count} zone(s), {BlackoutPoweredFixtures.Count} fixture(s)).");
            return false;
        }

        private static void HandleRuntimeContractTransition(MoonContractType currentContract)
        {
            bool runtimeContractChanged = _lastRuntimeContract != currentContract;
            if (runtimeContractChanged)
            {
                if ((_lastRuntimeContract == MoonContractType.BlackoutAudit || currentContract != MoonContractType.BlackoutAudit)
                    && HasInactiveBlackoutState())
                {
                    ClearInactiveBlackoutState($"contract changed from {_lastRuntimeContract} to {currentContract}");
                }
                _lastRuntimeContract = currentContract;
            }
            else if (currentContract != MoonContractType.BlackoutAudit && HasInactiveBlackoutState())
            {
                ClearInactiveBlackoutState($"active contract is {currentContract}");
            }

            if (ShouldRunWhistleblowerGuardSweep(currentContract, runtimeContractChanged))
            {
                RemoveWhistleblowerFromLevelSpawnPools(currentContract);
                RemoveNonContractWhistleblowers(currentContract);
            }
        }

        private static bool ShouldRunWhistleblowerGuardSweep(MoonContractType currentContract, bool runtimeContractChanged)
        {
            if (currentContract == MoonContractType.Whistleblower)
                return false;

            float now = Time.unscaledTime;
            if (!runtimeContractChanged && now < _nextWhistleblowerGuardSweepAt)
                return false;

            _nextWhistleblowerGuardSweepAt = now + WhistleblowerGuardSweepIntervalSeconds;
            return true;
        }

        private static bool HasInactiveBlackoutState()
        {
            return _blackoutPowerForced
                || _blackoutInitialized
                || _blackoutBreaker != null
                || _blackoutLever != null
                || BlackoutSuppressedNightVision.Count > 0;
        }

        private static void ClearInactiveBlackoutState(string reason)
        {
            if (!HasInactiveBlackoutState())
                return;

            ResetBlackoutRuntimeState(reason);
        }

        private static bool IsContractRuntimeActive()
        {
            var start = StartOfRound.Instance;
            return start != null
                && !start.inShipPhase
                && !start.shipIsLeaving
                && start.shipHasLanded;
        }

        private static List<MoonContractObjective> GetBlackoutAuditObjectives()
        {
            if (_blackoutUnsupportedThisRound)
            {
                return new List<MoonContractObjective>
                {
                    // #725: Total 1, never 0. A zero total reads as "already complete" to the
                    // card, so the fallback used to render ticked from the first frame.
                    // "Scrap" is the word the rest of the card uses; the shared closing
                    // objective covers the extraction.
                    new MoonContractObjective("Collect the scrap", 0, 1, key: "blackout.fallback")
                };
            }

            if (!IsBlackoutRuntimeSupported(allowFlowPrediction: false))
            {
                DisableBlackoutForCurrentRound("generated interior has no switchable powered lights");
                return new List<MoonContractObjective>
                {
                    new MoonContractObjective("Collect the scrap", 0, 1, key: "blackout.fallback")
                };
            }

            EnsureBlackoutAudit();
            bool complete = IsBlackoutAuditComplete();
            int breakerTotal = BlackoutBreakerZones.Count;
            // Locate and Reset share the card once the first breaker is found.
            int found = GetBlackoutFoundBreakerCount();
            int restored = complete ? Mathf.Max(1, breakerTotal) : GetBlackoutRestoredBreakerCount();
            var objectives = new List<MoonContractObjective>();
            if (breakerTotal > 1)
            {
                objectives.Add(new MoonContractObjective("Locate the breaker boxes", found, breakerTotal, key: BlackoutLocateKey));
                objectives.Add(new MoonContractObjective("Reset the breaker boxes", restored, breakerTotal, key: "blackout.breakers"));
            }
            else
            {
                objectives.Add(new MoonContractObjective("Locate the breaker box", found > 0 ? 1 : 0, 1, key: BlackoutLocateKey));
                objectives.Add(new MoonContractObjective("Reset the breaker box", complete ? 1 : 0, 1, key: "blackout.breakers"));
            }

            return objectives;
        }

        private static void EnsureBlackoutAudit()
        {
            if (_blackoutCompleted || _blackoutUnsupportedThisRound)
                return;

            if (!IsBlackoutRuntimeSupported(allowFlowPrediction: false))
            {
                DisableBlackoutForCurrentRound("generated interior has no switchable powered lights");
                return;
            }

            if (_blackoutInitialized && (BlackoutBreakerZones.Count > 0 || _blackoutBreaker != null || _blackoutLever != null))
                return;

            if (RoundManager.Instance == null)
                return;

            switch (TryInitializeBlackoutZones(out string reason))
            {
                case BlackoutFixtureBuildResult.Pending:
                    // The fixture sweep is still being metered out; nothing to disable and
                    // nothing to announce until it lands.
                    return;
                case BlackoutFixtureBuildResult.Ready:
                    _blackoutInitialized = true;
                    ForceBlackoutPowerOff(playEffects: false, syncBreaker: true);
                    ApplyBlackoutZonePowerState();
                    MarkBlackoutZoneStateDirty();
                    SendBlackoutZoneSnapshot();
                    ContractsBootstrap.Log?.LogInfo("[MoonContracts] Blackout Audit initialized breaker zones and forced facility power offline.");
                    return;
                default:
                    DisableBlackoutForCurrentRound(reason);
                    return;
            }
        }

        private static BlackoutFixtureBuildResult TryInitializeBlackoutZones(out string reason)
        {
            reason = string.Empty;
            if (BlackoutBreakerZones.Count > 0 && BlackoutPoweredFixtures.Count > 0)
                return BlackoutFixtureBuildResult.Ready;

            var round = RoundManager.Instance;
            if (round == null)
            {
                reason = "round manager unavailable";
                return BlackoutFixtureBuildResult.Failed;
            }

            // #716/A4: only the opening pass clears; later passes are resuming a build that
            // already owns the (empty) zone list.
            if (!IsBlackoutFixtureBuildInProgress)
            {
                ClearBlackoutZoneState(destroySpawnedBreakers: false);
                EnsurePoweredLightListsRefreshed(round);
            }

            switch (PumpBlackoutPoweredFixtureBuild())
            {
                case BlackoutFixtureBuildResult.Pending:
                    return BlackoutFixtureBuildResult.Pending;
                case BlackoutFixtureBuildResult.Failed:
                    reason = "no powered-light fixtures could be grouped";
                    return BlackoutFixtureBuildResult.Failed;
            }

            int targetCount = Mathf.Clamp(GetBlackoutBreakerCountForCurrentMoon(), 1, 3);
            Vector3 entrance = GetMainEntranceReferencePosition() ?? RoundManager.FindMainEntrancePosition();
            var existingBreakers = UnityEngine.Object.FindObjectsOfType<BreakerBox>()
                .Where(breaker => breaker != null && breaker.gameObject != null && breaker.gameObject.scene.IsValid())
                .OrderByDescending(breaker => (breaker.transform.position - entrance).sqrMagnitude)
                .ThenBy(breaker => breaker.name, StringComparer.Ordinal)
                .ToList();

            // Modded interiors (e.g. flows without the vanilla breaker markers) can generate
            // zero breaker boxes. That must not sink the contract: the fallback placer can
            // spawn every breaker from the prefab, so only fail after it has had its chance.
            int vanillaBreakerCount = existingBreakers.Count;
            int fallbackSpawned = TrySpawnBlackoutFallbackBreakers(existingBreakers, targetCount);
            int effectiveTargetCount = Mathf.Min(targetCount, existingBreakers.Count);
            LogBlackoutBreakerCapabilityAudit(targetCount, vanillaBreakerCount, existingBreakers.Count, effectiveTargetCount, fallbackSpawned, existingBreakers);
            if (existingBreakers.Count == 0)
            {
                reason = $"vanilla spawned 0/{targetCount} breaker boxes and fallback placement spawned none";
                return BlackoutFixtureBuildResult.Failed;
            }
            if (effectiveTargetCount < targetCount)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Blackout Audit clamped breaker zones to {effectiveTargetCount}/{targetCount}; vanilla/authored generation did not provide more valid breaker boxes.");
            }

            // A zone with zero fixtures makes its breaker a no-op restore. With fewer
            // fixtures than zones that is unavoidable, so merge down: fewer zones,
            // every one guaranteed at least one fixture by the rebalance pass.
            if (BlackoutPoweredFixtures.Count < effectiveTargetCount)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Blackout Audit merged breaker zones {effectiveTargetCount} -> {BlackoutPoweredFixtures.Count}; only {BlackoutPoweredFixtures.Count} powered fixture(s) exist to assign.");
                effectiveTargetCount = BlackoutPoweredFixtures.Count;
            }

            for (int i = 0; i < effectiveTargetCount; i++)
            {
                BreakerBox breaker = existingBreakers[i];
                if (breaker == null)
                    continue;

                RegisterBlackoutBreakerZone(breaker, i, spawnedByBlackout: i >= vanillaBreakerCount);
            }

            if (BlackoutBreakerZones.Count == 0)
            {
                reason = "no breaker boxes could be registered";
                return BlackoutFixtureBuildResult.Failed;
            }

            AssignBlackoutFixturesToZones();
            if (BlackoutBreakerZones.All(zone => zone.Fixtures.Count == 0))
            {
                reason = "no powered-light fixtures could be assigned";
                return BlackoutFixtureBuildResult.Failed;
            }

            _blackoutBreaker = BlackoutBreakerZones[0].Breaker;
            ScheduleNextBlackoutAmbientFlicker();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout Audit initialized {BlackoutBreakerZones.Count} breaker zone(s) for {BlackoutPoweredFixtures.Count} powered fixture(s).");
            return BlackoutFixtureBuildResult.Ready;
        }

        private static int TrySpawnBlackoutFallbackBreakers(List<BreakerBox> breakers, int targetCount)
        {
            // breakers may be empty: interiors without vanilla breaker markers rely on this
            // method to spawn every breaker from the prefab.
            if (breakers == null || breakers.Count >= targetCount)
                return 0;

            var round = RoundManager.Instance;
            if (round == null || !round.IsServer)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Blackout breaker fallback placement skipped: server-only spawn unavailable.");
                return 0;
            }

            GameObject prefab = FindBlackoutFallbackBreakerPrefab(breakers);
            if (prefab == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Blackout breaker fallback placement skipped: no vanilla breaker prefab/template available.");
                return 0;
            }

            // Authored vanilla spawn markers beat anything the procedural placer can
            // synthesise, so harvest any marker the vanilla pass left unused before falling
            // back to generated poses. On every flow captured so far each marker already
            // carries its own breaker (markers and spawned boxes run 1:1), which makes this a
            // no-op — but when a marker does go unused it is a free, guaranteed-valid mount.
            int spawned = TrySpawnBlackoutBreakersAtUnusedVanillaMarkers(breakers, targetCount);

            BreakerBox mountTemplate = SelectBlackoutFallbackMountTemplate(breakers);
            var excludedTiles = new HashSet<Tile>();
            foreach (BreakerBox breaker in breakers)
            {
                Tile tile = FindBlackoutTileForPosition(breaker.transform.position);
                if (tile != null)
                    excludedTiles.Add(tile);
            }

            int attempts = Mathf.Clamp((targetCount - breakers.Count) * 6, 6, 18);
            for (int attempt = 0; attempt < attempts && breakers.Count < targetCount; attempt++)
            {
                var request = new InteriorPlacementRequest
                {
                    Role = InteriorPlacementRole.GenericWallFixture,
                    Footprint = CreateBlackoutBreakerFootprint(),
                    ExcludedTiles = excludedTiles,
                    MinEntranceDistance = 12f,
                    AllowEntranceRoom = false,
                    RequireWallContact = true,
                    RequireFloorContact = false,
                    RequireBodyClearance = true,
                    RequireReachableInteractionPoint = true,
                    DisallowHallway = true,
                    VariantIndex = attempt + breakers.Count * 3,
                    DebugLabel = $"Blackout breaker fallback {breakers.Count + 1}/{targetCount}"
                };

                if (!InteriorPlacementService.TryBuildPlan(request, out InteriorPlacementPlan plan) || plan?.Pose.Tile == null)
                    break;

                BreakerBox breaker = SpawnBlackoutFallbackBreakerAtPose(
                    prefab,
                    mountTemplate,
                    plan.Pose,
                    breakers.Count,
                    out BlackoutBreakerRuntimeMountPose mountPose,
                    out string rejectReason);
                if (breaker == null)
                {
                    // The plan pose passed the placement service but the real mount pose did
                    // not survive its own re-validation. Retire the tile and try another one
                    // rather than mounting a breaker the player can never reach.
                    excludedTiles.Add(plan.Pose.Tile);
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts] Blackout breaker fallback pose rejected on tile='{plan.Pose.TileName}' source={plan.Pose.Source} tier={plan.FallbackTier} planPos=({plan.Pose.Position.x:0.00},{plan.Pose.Position.y:0.00},{plan.Pose.Position.z:0.00}): {rejectReason}.");
                    continue;
                }

                breakers.Add(breaker);
                excludedTiles.Add(plan.Pose.Tile);
                spawned++;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Blackout breaker fallback placement spawned {spawned}/{targetCount} at tile='{plan.Pose.TileName}' source={plan.Pose.Source} mountSource={mountPose.Source} tier={plan.FallbackTier} pos=({mountPose.Position.x:0.00},{mountPose.Position.y:0.00},{mountPose.Position.z:0.00}) euler=({mountPose.Rotation.eulerAngles.x:0.0},{mountPose.Rotation.eulerAngles.y:0.0},{mountPose.Rotation.eulerAngles.z:0.0}) forward=({mountPose.Forward.x:0.00},{mountPose.Forward.y:0.00},{mountPose.Forward.z:0.00}) height={mountPose.MountHeight:0.00} inset={mountPose.WallInset:0.00}.");
            }

            if (breakers.Count < targetCount)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Blackout breaker fallback placement found {breakers.Count}/{targetCount} total breaker boxes after {spawned} fallback spawn(s).");
            }

            return spawned;
        }

        private static FixtureFootprint CreateBlackoutBreakerFootprint()
        {
            return new FixtureFootprint(
                Vector3.forward,
                Vector3.back,
                Vector3.up,
                new Vector3(1.2f, 1.35f, 0.55f),
                0.16f,
                0.05f,
                1.2f,
                1.1f);
        }

        private static BreakerBox SelectBlackoutFallbackMountTemplate(IReadOnlyList<BreakerBox> breakers)
        {
            if (breakers == null)
                return null;

            for (int i = 0; i < breakers.Count; i++)
            {
                BreakerBox breaker = breakers[i];
                if (breaker == null || breaker.gameObject == null)
                    continue;
                if (breaker.name != null && breaker.name.StartsWith(BlackoutFallbackBreakerNamePrefix, StringComparison.Ordinal))
                    continue;
                return breaker;
            }

            for (int i = 0; i < breakers.Count; i++)
            {
                BreakerBox breaker = breakers[i];
                if (breaker != null && breaker.gameObject != null)
                    return breaker;
            }

            return null;
        }

        private static bool BuildBlackoutBreakerRuntimeMountPose(
            GameObject prefab,
            BreakerBox templateBreaker,
            PlacementPose pose,
            out BlackoutBreakerRuntimeMountPose mountPose,
            out string rejectReason)
        {
            mountPose = default;
            rejectReason = string.Empty;
            if (prefab == null || pose.Tile == null)
            {
                rejectReason = "prefab/tile unavailable";
                return false;
            }

            Vector3 targetForward = SafeBlackoutHorizontalAxis(pose.Forward, pose.WallNormal);
            if (targetForward.sqrMagnitude < 1e-6f)
                targetForward = SafeBlackoutHorizontalAxis(pose.Rotation * Vector3.forward, Vector3.forward);

            Transform templateTransform = templateBreaker != null && templateBreaker.transform != null
                ? templateBreaker.transform
                : prefab.transform;
            Quaternion templateRotation = templateTransform != null ? templateTransform.rotation : Quaternion.identity;

            Vector3 templateForward;
            float wallInset;
            string source;
            if (FindBlackoutMountedWallNormal(templateBreaker, out templateForward, out wallInset))
            {
                source = $"vanilla:{templateBreaker.name}";
            }
            else
            {
                // The vanilla BreakerBox is authored mesh-Z-up: transform.forward
                // points at the ceiling and the box FACE is -transform.up. Verified
                // across two live runs: aligning +up to the wall normal spawned
                // every box back-to-player (2026-07-30 run 2), so the out-of-wall
                // axis is the negated up-axis.
                Vector3 horizontalOut = -(templateRotation * Vector3.up);
                horizontalOut.y = 0f;
                Vector3 horizontalForward = templateRotation * Vector3.forward;
                horizontalForward.y = 0f;
                templateForward = horizontalForward.sqrMagnitude >= horizontalOut.sqrMagnitude
                    ? SafeBlackoutHorizontalAxis(horizontalForward, targetForward)
                    : SafeBlackoutHorizontalAxis(horizontalOut, targetForward);
                wallInset = BlackoutBreakerDefaultWallInset;
                source = "fallback-template-axis";
            }

            // Yaw-only alignment: rotate the template's authored orientation about the
            // world up axis until its out-of-wall direction meets targetForward.
            // FromToRotation between two horizontal vectors picks an arbitrary
            // rotation axis when they are near-opposite, which can pitch or roll the
            // breaker; a yaw can only ever spin it in place.
            float yawDelta = Vector3.SignedAngle(templateForward, targetForward, Vector3.up);
            Quaternion rotation = Quaternion.AngleAxis(yawDelta, Vector3.up) * templateRotation;
            float mountHeight = ComputeBlackoutBreakerMountHeight(templateBreaker);
            float clampedWallInset = Mathf.Clamp(wallInset, 0.08f, 0.65f);
            float planWallInset = CreateBlackoutBreakerFootprint().BackPlaneOffset;

            // A floor probe that misses means the plan pose has nothing underneath it — it is
            // over a void or, on an irregular tile, past the mesh on the tile's AABB face. The
            // probe used to answer "the floor is exactly at my origin" in that case, and the
            // mount height was then stacked on top of that fiction, which is how a breaker
            // reached the skybox. Treat the miss as a rejected candidate.
            if (!InteriorAnchorService.TryResolveFloorPosition(pose.Position + Vector3.up * 0.75f, out Vector3 floor))
            {
                rejectReason = "plan-pose-floor-resolve-miss";
                return false;
            }

            float planFloorGap = pose.Position.y - floor.y;
            if (planFloorGap < -0.6f || planFloorGap > BlackoutBreakerMaxPlanFloorGap)
            {
                // The plan pose is generated at floor level, so a large gap means the probe
                // resolved some other surface (a different storey, a catwalk below).
                rejectReason = $"plan-pose-floor-gap={planFloorGap:0.00}";
                return false;
            }

            Vector3 position = pose.Position + targetForward * (clampedWallInset - planWallInset);
            position.y = floor.y + mountHeight;

            mountPose = new BlackoutBreakerRuntimeMountPose(
                position,
                rotation,
                targetForward,
                mountHeight,
                clampedWallInset,
                source);

            if (!ValidateBlackoutBreakerMountPose(pose, mountPose, out rejectReason))
            {
                mountPose = default;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Re-validates the pose the breaker is actually mounted at. The placement service
        /// validates its plan pose, but the mount pose above then moves that point to the
        /// template's wall inset and mount height — so the validated pose and the spawned pose
        /// are not the same point, and nothing used to check the one that ships.
        /// </summary>
        private static bool ValidateBlackoutBreakerMountPose(
            PlacementPose pose,
            BlackoutBreakerRuntimeMountPose mountPose,
            out string reason)
        {
            Vector3 position = mountPose.Position;
            Vector3 forward = mountPose.Forward;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;

            if (pose.Tile != null)
            {
                Bounds bounds = pose.Tile.Bounds;
                if (position.x < bounds.min.x - 0.4f
                    || position.x > bounds.max.x + 0.4f
                    || position.z < bounds.min.z - 0.4f
                    || position.z > bounds.max.z + 0.4f)
                {
                    reason = "mount-outside-tile-bounds";
                    return false;
                }
            }

            if (!Physics.Raycast(
                    position,
                    Vector3.down,
                    out RaycastHit floorHit,
                    mountPose.MountHeight + 1.5f,
                    mask,
                    QueryTriggerInteraction.Ignore))
            {
                reason = "no-floor-under-mount";
                return false;
            }

            if (!Physics.Raycast(
                    position,
                    -forward,
                    out RaycastHit wallHit,
                    mountPose.WallInset + 0.75f,
                    mask,
                    QueryTriggerInteraction.Ignore))
            {
                reason = "no-wall-behind-mount";
                return false;
            }

            // One center ray accepts a thin partition edge or a pillar corner as "a
            // wall" and lets the box overhang void on both sides. Every corner of the
            // box face must find backing within the same distance for the surface to
            // actually carry the mount.
            Vector3 wallRight = Vector3.Cross(Vector3.up, forward).normalized;
            float wallProbeReach = mountPose.WallInset + 0.75f;
            foreach (Vector3 cornerOffset in new[]
                     {
                         wallRight * 0.3f + Vector3.up * 0.35f,
                         wallRight * 0.3f - Vector3.up * 0.35f,
                         -wallRight * 0.3f + Vector3.up * 0.35f,
                         -wallRight * 0.3f - Vector3.up * 0.35f
                     })
            {
                if (!Physics.Raycast(
                        position + cornerOffset,
                        -forward,
                        wallProbeReach,
                        mask,
                        QueryTriggerInteraction.Ignore))
                {
                    reason = $"wall-coverage-miss offset=({cornerOffset.x:0.##},{cornerOffset.y:0.##},{cornerOffset.z:0.##})";
                    return false;
                }
            }

            // The player stands in front of the box to flip switches. A mount on a
            // ledge over a pit has floor under the wall but nothing under the player.
            Vector3 standPoint = position + forward * 0.6f;
            if (!Physics.Raycast(
                    standPoint,
                    Vector3.down,
                    out RaycastHit standFloorHit,
                    mountPose.MountHeight + 1.5f,
                    mask,
                    QueryTriggerInteraction.Ignore))
            {
                reason = "no-floor-under-approach";
                return false;
            }

            if (!NavMesh.SamplePosition(standFloorHit.point, out NavMeshHit standNavHit, 1.0f, NavMesh.AllAreas))
            {
                reason = "approach-floor-off-navmesh";
                return false;
            }

            // Navmesh under the standing spot proves surface, not reachability — a
            // sealed shaft or the far side of a wall can carry its own island of
            // navmesh. Require an actual complete path from the main entrance.
            Vector3? entrance = GetMainEntranceReferencePosition();
            if (entrance.HasValue
                && NavMesh.SamplePosition(entrance.Value, out NavMeshHit entranceNavHit, 4f, NavMesh.AllAreas))
            {
                var approachPath = new NavMeshPath();
                if (!NavMesh.CalculatePath(entranceNavHit.position, standNavHit.position, NavMesh.AllAreas, approachPath)
                    || approachPath.status != NavMeshPathStatus.PathComplete)
                {
                    reason = "approach-unreachable-from-entrance";
                    return false;
                }
            }

            // Buried in geometry and unreachable approach are the same symptom: solid volume
            // where a player has to stand to flip the switches.
            if (Physics.CheckBox(
                    position + forward * 0.6f,
                    new Vector3(0.45f, 0.5f, 0.4f),
                    mountPose.Rotation,
                    mask,
                    QueryTriggerInteraction.Ignore))
            {
                reason = "mount-approach-blocked";
                return false;
            }

            if (!NavMesh.SamplePosition(floorHit.point, out NavMeshHit navHit, 2.5f, NavMesh.AllAreas))
            {
                reason = "mount-floor-off-navmesh";
                return false;
            }

            reason = $"ok wall={wallHit.distance:0.00} floor={floorHit.distance:0.00} nav={Vector3.Distance(navHit.position, floorHit.point):0.00}";
            return true;
        }

        private static bool FindBlackoutMountedWallNormal(BreakerBox breaker, out Vector3 wallNormal, out float wallDistance)
        {
            wallNormal = Vector3.zero;
            wallDistance = BlackoutBreakerDefaultWallInset;
            if (breaker == null || breaker.transform == null)
                return false;

            Vector3 origin = ResolveBlackoutBreakerWallProbeOrigin(breaker);
            List<Vector3> directions = BuildBlackoutBreakerWallProbeDirections(breaker.transform);
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            float bestDistance = float.MaxValue;
            Vector3 bestNormal = Vector3.zero;

            for (int i = 0; i < directions.Count; i++)
            {
                Vector3 direction = directions[i];
                if (direction.sqrMagnitude < 1e-6f)
                    continue;

                RaycastHit[] hits = Physics.RaycastAll(
                    origin,
                    direction,
                    BlackoutBreakerMaxWallProbeDistance,
                    mask,
                    QueryTriggerInteraction.Ignore);
                if (hits == null || hits.Length == 0)
                    continue;

                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                for (int h = 0; h < hits.Length; h++)
                {
                    RaycastHit hit = hits[h];
                    if (hit.collider == null || hit.collider.transform == null)
                        continue;
                    if (hit.collider.transform == breaker.transform || hit.collider.transform.IsChildOf(breaker.transform))
                        continue;

                    Vector3 normal = hit.normal;
                    normal.y = 0f;
                    if (normal.sqrMagnitude < 0.05f)
                        continue;

                    normal.Normalize();
                    if (Vector3.Dot(normal, -direction) < 0.25f)
                        continue;

                    if (hit.distance < bestDistance)
                    {
                        bestDistance = hit.distance;
                        bestNormal = normal;
                    }

                    break;
                }
            }

            if (bestDistance == float.MaxValue)
                return false;

            wallNormal = bestNormal;
            wallDistance = bestDistance;
            return true;
        }

        private static float ComputeBlackoutBreakerMountHeight(BreakerBox templateBreaker)
        {
            if (templateBreaker != null
                && templateBreaker.transform != null
                && InteriorAnchorService.TryResolveFloorPosition(
                    templateBreaker.transform.position + Vector3.up * 0.75f,
                    out Vector3 floor))
            {
                float rootHeight = templateBreaker.transform.position.y - floor.y;
                if (rootHeight >= 0.45f && rootHeight <= 2.25f)
                    return rootHeight;

                if (TryGetBlackoutBreakerWorldBounds(templateBreaker.gameObject, out Bounds bounds))
                {
                    float centerHeight = bounds.center.y - floor.y;
                    if (centerHeight >= 0.65f && centerHeight <= 2.1f)
                        return centerHeight;
                }
            }

            return BlackoutBreakerDefaultMountHeight;
        }

        private static Vector3 ResolveBlackoutBreakerWallProbeOrigin(BreakerBox breaker)
        {
            if (breaker != null && breaker.gameObject != null && TryGetBlackoutBreakerWorldBounds(breaker.gameObject, out Bounds bounds))
            {
                Vector3 floor = InteriorAnchorService.ResolveFloorPosition(breaker.transform.position + Vector3.up * 0.75f);
                Vector3 origin = bounds.center;
                origin.y = Mathf.Clamp(origin.y, floor.y + 0.75f, floor.y + 1.65f);
                return origin;
            }

            return breaker != null && breaker.transform != null
                ? breaker.transform.position + Vector3.up * 0.95f
                : Vector3.zero;
        }

        private static bool TryGetBlackoutBreakerWorldBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            if (root == null)
                return false;

            bool hasBounds = false;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (hasBounds)
                return true;

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null)
                    continue;

                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            return hasBounds;
        }

        private static List<Vector3> BuildBlackoutBreakerWallProbeDirections(Transform transform)
        {
            var directions = new List<Vector3>(10);
            if (transform != null)
            {
                AddBlackoutBreakerWallProbeDirection(directions, transform.forward);
                AddBlackoutBreakerWallProbeDirection(directions, -transform.forward);
                AddBlackoutBreakerWallProbeDirection(directions, transform.right);
                AddBlackoutBreakerWallProbeDirection(directions, -transform.right);
                AddBlackoutBreakerWallProbeDirection(directions, transform.up);
                AddBlackoutBreakerWallProbeDirection(directions, -transform.up);
            }

            AddBlackoutBreakerWallProbeDirection(directions, Vector3.forward);
            AddBlackoutBreakerWallProbeDirection(directions, Vector3.back);
            AddBlackoutBreakerWallProbeDirection(directions, Vector3.right);
            AddBlackoutBreakerWallProbeDirection(directions, Vector3.left);
            return directions;
        }

        private static void AddBlackoutBreakerWallProbeDirection(List<Vector3> directions, Vector3 direction)
        {
            if (directions == null)
                return;

            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-6f)
                return;

            direction.Normalize();
            for (int i = 0; i < directions.Count; i++)
            {
                if (Vector3.Dot(directions[i], direction) > 0.96f)
                    return;
            }

            directions.Add(direction);
        }

        private static Vector3 SafeBlackoutHorizontalAxis(Vector3 preferred, Vector3 fallback)
        {
            preferred.y = 0f;
            if (preferred.sqrMagnitude >= 1e-6f)
                return preferred.normalized;

            fallback.y = 0f;
            if (fallback.sqrMagnitude >= 1e-6f)
                return fallback.normalized;

            return Vector3.forward;
        }

        private static GameObject FindBlackoutFallbackBreakerPrefab(IReadOnlyList<BreakerBox> existingBreakers)
        {
            BreakerBox[] prefabCandidates = Resources.FindObjectsOfTypeAll<BreakerBox>();
            if (prefabCandidates != null)
            {
                foreach (BreakerBox candidate in prefabCandidates)
                {
                    if (candidate == null || candidate.gameObject == null)
                        continue;
                    if (candidate.gameObject.scene.IsValid())
                        continue;
                    if (candidate.breakerSwitches == null || candidate.breakerSwitches.Length == 0)
                        continue;

                    return candidate.gameObject;
                }
            }

            GameObject mapObjectPrefab = FindBlackoutBreakerSpawnPrefab();
            if (mapObjectPrefab != null)
                return mapObjectPrefab;

            if (existingBreakers != null)
            {
                foreach (BreakerBox breaker in existingBreakers)
                {
                    if (breaker != null && breaker.gameObject != null)
                        return breaker.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// Claims any vanilla breaker spawn marker the vanilla pass left without a breaker.
        /// Authored marker poses are exact, so they beat every generated candidate.
        /// </summary>
        private static int TrySpawnBlackoutBreakersAtUnusedVanillaMarkers(List<BreakerBox> breakers, int targetCount)
        {
            if (breakers == null || breakers.Count >= targetCount)
                return 0;

            List<SpawnSyncedObject> markers = CollectBlackoutBreakerSyncedMarkers();
            if (markers.Count == 0)
                return 0;

            int spawned = 0;
            foreach (SpawnSyncedObject marker in markers)
            {
                if (breakers.Count >= targetCount)
                    break;
                if (marker == null || marker.transform == null || marker.spawnPrefab == null)
                    continue;

                Vector3 position = marker.transform.position;
                if (HasBlackoutBreakerNear(breakers, position, BlackoutBreakerMarkerMatchRadius))
                    continue;

                BreakerBox breaker = InstantiateBlackoutBreaker(
                    marker.spawnPrefab,
                    position,
                    marker.transform.rotation,
                    breakers.Count);
                if (breaker == null)
                    continue;

                breakers.Add(breaker);
                spawned++;
                Tile tile = FindBlackoutTileForPosition(position);
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Blackout breaker claimed unused vanilla marker {spawned} pos=({position.x:0.00},{position.y:0.00},{position.z:0.00}) tile='{(tile != null ? tile.name : "<none>")}'.");
            }

            return spawned;
        }

        private static BreakerBox SpawnBlackoutFallbackBreakerAtPose(
            GameObject prefab,
            BreakerBox mountTemplate,
            PlacementPose pose,
            int index,
            out BlackoutBreakerRuntimeMountPose mountPose,
            out string rejectReason)
        {
            mountPose = default;
            rejectReason = string.Empty;
            if (prefab == null)
            {
                rejectReason = "no prefab";
                return null;
            }
            if (!BuildBlackoutBreakerRuntimeMountPose(prefab, mountTemplate, pose, out mountPose, out rejectReason))
                return null;

            // Claim the volume so a later fixture cannot be planned into the same wall, and so a
            // second fallback breaker cannot stack on this one.
            if (!FixtureReservationRegistry.TryReserve(
                    $"blackout-breaker-{index + 1}",
                    "blackout-audit-fallback",
                    CreateBlackoutBreakerFootprint(),
                    mountPose.Position,
                    mountPose.Rotation,
                    out _,
                    out FixtureReservationConflict conflict))
            {
                rejectReason = $"reservation-conflict existing-label='{conflict.ExistingLabel}' existing-source='{conflict.ExistingSource}'";
                mountPose = default;
                return null;
            }

            BreakerBox spawnedBreaker = InstantiateBlackoutBreaker(prefab, mountPose.Position, mountPose.Rotation, index, mountPose.Forward);
            if (spawnedBreaker == null)
            {
                FixtureReservationRegistry.Release($"blackout-breaker-{index + 1}");
                rejectReason = "instantiate-failed";
                mountPose = default;
            }

            return spawnedBreaker;
        }

        private static BreakerBox InstantiateBlackoutBreaker(
            GameObject prefab,
            Vector3 position,
            Quaternion rotation,
            int index,
            Vector3? faceDirection = null)
        {
            if (prefab == null)
                return null;

            GameObject instance = null;
            try
            {
                Transform parent = GetBlackoutMapPropsParent();
                instance = UnityEngine.Object.Instantiate(prefab);
                if (instance == null)
                    return null;

                instance.name = $"{BlackoutFallbackBreakerNamePrefix}_{index + 1}";
                instance.transform.SetPositionAndRotation(position, rotation);
                BreakerBox breaker = instance.GetComponent<BreakerBox>() ?? instance.GetComponentInChildren<BreakerBox>(true);
                if (breaker == null)
                {
                    UnityEngine.Object.Destroy(instance);
                    return null;
                }

                // Closed-loop facing: rotation math has to guess which template axis
                // faces out of the wall, and both guesses so far shipped a wrong-facing
                // box. Measure the spawned instance's real face (the switch levers sit
                // on the face side of the box) and yaw-correct the difference before
                // the transform is captured for the network spawn below.
                if (faceDirection.HasValue)
                {
                    CorrectBlackoutBreakerFacing(instance.transform, breaker, faceDirection.Value);
                    position = instance.transform.position;
                    rotation = instance.transform.rotation;
                }

                breaker.roundManager = RoundManager.Instance;
                try
                {
                    if (breaker.breakerSwitches != null && breaker.breakerSwitches.Length > 0)
                        breaker.SetSwitchesOff();
                }
                catch (Exception e)
                {
                    ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Blackout fallback breaker switch preparation skipped: {e.Message}");
                }

                breaker.leversSwitchedOff = Mathf.Max(1, breaker.leversSwitchedOff);
                breaker.isPowerOn = false;
                if (breaker.breakerBoxHum != null && breaker.breakerBoxHum.isPlaying)
                    breaker.breakerBoxHum.Stop();

                var networkObject = instance.GetComponent<NetworkObject>() ?? instance.GetComponentInChildren<NetworkObject>(true);
                if (networkObject != null && RoundManager.Instance != null && RoundManager.Instance.IsServer && !networkObject.IsSpawned)
                {
                    networkObject.SynchronizeTransform = true;
                    networkObject.Spawn(destroyWithScene: true);
                }
                else if (networkObject == null)
                {
                    // Vanilla spawns breaker boxes through SpawnSyncedObject, which network-spawns
                    // each one, so the prefab is expected to carry a NetworkObject. Without it the
                    // box exists on the host only and every client sees an unreachable objective.
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts] Blackout breaker '{instance.name}' has no NetworkObject; it will be host-only and invisible to clients.");
                }

                instance.transform.SetPositionAndRotation(position, rotation);
                if (networkObject == null && parent != null)
                    instance.transform.SetParent(parent, true);

                return breaker;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Blackout breaker spawn failed: {e.Message}");
                if (instance != null)
                    UnityEngine.Object.Destroy(instance);
                return null;
            }
        }

        // Only ever flips 180 degrees, decided by physics: the wall must be behind
        // the box and open space in front. An earlier version measured the face
        // from the switch-lever offset and applied a free yaw — the levers sit in
        // a column offset to the SIDE of the box, so the lateral component
        // dominated and boxes yawed ~90 degrees out of their walls.
        private static void CorrectBlackoutBreakerFacing(Transform root, BreakerBox breaker, Vector3 targetForward)
        {
            targetForward.y = 0f;
            if (root == null || breaker == null || targetForward.sqrMagnitude < 1e-6f)
                return;
            targetForward.Normalize();

            if (!TryGetBlackoutBreakerWorldBounds(root.gameObject, out Bounds bounds))
                return;

            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            Vector3 center = bounds.center;
            bool backBlocked = BlackoutFacingRayHits(center, -targetForward, 0.9f, root, mask);
            bool frontBlocked = BlackoutFacingRayHits(center, targetForward, 0.9f, root, mask);

            if (backBlocked && !frontBlocked)
            {
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Blackout breaker facing verified: wall behind, clear ahead.");
                return;
            }

            if (frontBlocked && !backBlocked)
            {
                Vector3 pivot = new Vector3(center.x, root.position.y, center.z);
                root.RotateAround(pivot, Vector3.up, 180f);
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Blackout breaker facing flipped 180 deg: wall was ahead of the face, open space behind.");
                return;
            }

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout breaker facing left as spawned (backBlocked={backBlocked}, frontBlocked={frontBlocked}).");
        }

        private static bool BlackoutFacingRayHits(Vector3 origin, Vector3 direction, float distance, Transform ignoreRoot, int mask)
        {
            RaycastHit[] hits = Physics.RaycastAll(origin, direction, distance, mask, QueryTriggerInteraction.Ignore);
            if (hits == null)
                return false;

            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null || hit.collider.transform == null)
                    continue;
                Transform hitTransform = hit.collider.transform;
                if (hitTransform == ignoreRoot || hitTransform.IsChildOf(ignoreRoot))
                    continue;
                return true;
            }

            return false;
        }

        private static Transform GetBlackoutMapPropsParent()
        {
            GameObject container = RoundManager.Instance?.mapPropsContainer;
            if (container == null)
                container = GameObject.FindGameObjectWithTag("MapPropsContainer");
            return container != null ? container.transform : null;
        }

        private static Tile FindBlackoutTileForPosition(Vector3 position)
        {
            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null)
                return null;

            foreach (Tile tile in dungeon.AllTiles)
            {
                if (tile == null)
                    continue;

                Bounds bounds = tile.Bounds;
                if (position.x >= bounds.min.x - 0.4f
                    && position.x <= bounds.max.x + 0.4f
                    && position.z >= bounds.min.z - 0.4f
                    && position.z <= bounds.max.z + 0.4f)
                {
                    return tile;
                }
            }

            return null;
        }

        /// <summary>#716/A4: the fixture build is the expensive half of the blackout zone
        /// init, and it runs on the landed frame on every machine. It is now resumable: the
        /// vanilla powered-light lists are folded in on the opening pass, and the untagged
        /// interior sweep — one <c>FindObjectsOfType&lt;Light&gt;</c> tested against every
        /// dungeon tile — is metered out over subsequent passes against one Light snapshot
        /// and one tile-bounds snapshot taken at the start of the build. Same inputs, same
        /// iteration order, same fixture list; only the frame it finishes on moves.</summary>
        private enum BlackoutFixtureBuildResult
        {
            Pending,
            Ready,
            Failed
        }

        private const int BlackoutFixtureSweepLightsPerPass = 64;
        private static Light[] _blackoutFixtureSweepLights;
        private static Bounds[] _blackoutFixtureSweepTileBounds;
        private static HashSet<Light> _blackoutFixtureSweepSeenLights;
        private static int _blackoutFixtureSweepCursor;
        private static int _blackoutFixtureSweepAdded;

        private static void ResetBlackoutFixtureBuild()
        {
            _blackoutFixtureSweepLights = null;
            _blackoutFixtureSweepTileBounds = null;
            _blackoutFixtureSweepSeenLights = null;
            _blackoutFixtureSweepCursor = 0;
            _blackoutFixtureSweepAdded = 0;
        }

        private static bool IsBlackoutFixtureBuildInProgress => _blackoutFixtureSweepLights != null;

        private static BlackoutFixtureBuildResult PumpBlackoutPoweredFixtureBuild()
        {
            var round = RoundManager.Instance;
            if (round == null)
            {
                ResetBlackoutFixtureBuild();
                return BlackoutFixtureBuildResult.Failed;
            }

            if (!IsBlackoutFixtureBuildInProgress)
            {
                if (!BeginBlackoutPoweredFixtureBuild(round))
                {
                    ResetBlackoutFixtureBuild();
                    return BlackoutFixtureBuildResult.Failed;
                }

                return BlackoutFixtureBuildResult.Pending;
            }

            Light[] lights = _blackoutFixtureSweepLights;
            int end = Mathf.Min(lights.Length, _blackoutFixtureSweepCursor + BlackoutFixtureSweepLightsPerPass);
            for (; _blackoutFixtureSweepCursor < end; _blackoutFixtureSweepCursor++)
                ExamineBlackoutFixtureSweepLight(lights[_blackoutFixtureSweepCursor]);

            if (_blackoutFixtureSweepCursor < lights.Length)
                return BlackoutFixtureBuildResult.Pending;

            if (_blackoutFixtureSweepAdded > 0)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout fixture sweep added {_blackoutFixtureSweepAdded} untagged interior light(s) beyond the vanilla PoweredLight lists.");

            ResetBlackoutFixtureBuild();
            return BlackoutPoweredFixtures.Count > 0
                ? BlackoutFixtureBuildResult.Ready
                : BlackoutFixtureBuildResult.Failed;
        }

        private static void ExamineBlackoutFixtureSweepLight(Light light)
        {
            // Interiors like CIDOM light most of their tiles with untagged Lights the
            // vanilla PoweredLight lists never see — 51 of them stayed lit through a
            // full blackout on Deadlock. Sweep every other enabled light inside the
            // dungeon into its own fixture. Swept fixtures carry no Animator on
            // purpose: their parent animators are not lighting rigs, and a stray
            // SetBool("on") could drive anything.
            if (light == null || _blackoutFixtureSweepSeenLights.Contains(light))
                return;
            if (!light.enabled || !light.gameObject.activeInHierarchy || light.intensity <= 0.0001f)
                return;
            if (light.CompareTag("IndirectLightSource"))
                return;
            if (!IsPositionInsideBlackoutTileBounds(light.transform.position, _blackoutFixtureSweepTileBounds))
                return;
            if (IsBlackoutSweepExemptLight(light))
                return;

            _blackoutFixtureSweepSeenLights.Add(light);
            var fixture = new BlackoutPoweredFixture
            {
                Root = light.gameObject,
                Animator = null,
                Position = light.transform.position
            };
            fixture.Lights.Add(light);
            BlackoutPoweredFixtures.Add(fixture);
            _blackoutFixtureSweepAdded++;
        }

        private static bool BeginBlackoutPoweredFixtureBuild(RoundManager round)
        {
            BlackoutPoweredFixtures.Clear();

            var seenRoots = new HashSet<GameObject>();
            var seenLights = new HashSet<Light>();
            if (round.allPoweredLightsAnimators != null)
            {
                foreach (Animator animator in round.allPoweredLightsAnimators)
                {
                    if (animator == null || animator.gameObject == null || !seenRoots.Add(animator.gameObject))
                        continue;

                    var fixture = new BlackoutPoweredFixture
                    {
                        Root = animator.gameObject,
                        Animator = animator,
                        Position = animator.transform.position
                    };

                    Light[] lights = animator.gameObject.GetComponentsInChildren<Light>(true);
                    foreach (Light light in lights)
                    {
                        if (light == null || light.CompareTag("IndirectLightSource") || !seenLights.Add(light))
                            continue;
                        fixture.Lights.Add(light);
                    }

                    BlackoutPoweredFixtures.Add(fixture);
                }
            }

            if (round.allPoweredLights != null)
            {
                foreach (Light light in round.allPoweredLights)
                {
                    if (light == null || seenLights.Contains(light))
                        continue;

                    seenLights.Add(light);
                    var fixture = new BlackoutPoweredFixture
                    {
                        Root = light.gameObject,
                        Animator = light.GetComponentInParent<Animator>(),
                        Position = light.transform.position
                    };
                    fixture.Lights.Add(light);
                    BlackoutPoweredFixtures.Add(fixture);
                }
            }

            // One Light scan and one tile-bounds snapshot for the whole build; the sweep
            // itself is metered out by PumpBlackoutPoweredFixtureBuild.
            _blackoutFixtureSweepSeenLights = seenLights;
            _blackoutFixtureSweepTileBounds = SnapshotBlackoutDungeonTileBounds();
            _blackoutFixtureSweepLights = UnityEngine.Object.FindObjectsOfType<Light>() ?? Array.Empty<Light>();
            _blackoutFixtureSweepCursor = 0;
            _blackoutFixtureSweepAdded = 0;
            return true;
        }

        private static bool IsBlackoutSweepExemptLight(Light light)
        {
            // Breaker indicator lights are the beacon players search for in the dark,
            // and the apparatus glow is the facility's power source, not a consumer.
            if (light.GetComponentInParent<BreakerBox>() != null)
                return true;
            if (light.GetComponentInParent<LungProp>() != null)
                return true;
            if (light.GetComponentInParent<PlayerControllerB>() != null)
                return true;

            string path = GetTransformPath(light.transform);
            return ContainsIgnoreCase(path, "LethalCCTV")
                || ContainsIgnoreCase(path, "CCTV")
                || ContainsIgnoreCase(path, "NightVisionFillLight")
                || ContainsIgnoreCase(path, "helmet")
                || ContainsIgnoreCase(path, "nightvision")
                || ContainsIgnoreCase(path, "night vision")
                || ContainsIgnoreCase(path, "flashlight")
                || ContainsIgnoreCase(path, "glow");
        }

        private static void RegisterBlackoutBreakerZone(BreakerBox breaker, int index, bool spawnedByBlackout)
        {
            if (breaker == null)
                return;

            breaker.roundManager = RoundManager.Instance;
            BlackoutBreakerZones.Add(new BlackoutBreakerZone
            {
                Index = index,
                Breaker = breaker,
                Position = breaker.transform.position,
                Restored = breaker.isPowerOn && breaker.leversSwitchedOff <= 0,
                SpawnedByBlackout = spawnedByBlackout
            });
        }

        private static void AssignBlackoutFixturesToZones()
        {
            foreach (BlackoutBreakerZone zone in BlackoutBreakerZones)
                zone.Fixtures.Clear();

            // #172 item 2: zones follow the dungeon's tile graph, not straight-line distance.
            // Straight-line put a corridor one wall away from breaker A into breaker B's zone
            // when B happened to be nearer through the wall, so a restore lit rooms the crew
            // could not see from the breaker they had just pulled. Each fixture now belongs to
            // the breaker that is fewest tile hops away; straight-line only breaks ties, and
            // is the whole rule when no dungeon graph is available.
            BlackoutTileGraph graph = BlackoutTileGraph.TryBuild(BlackoutBreakerZones);
            foreach (BlackoutPoweredFixture fixture in BlackoutPoweredFixtures)
            {
                BlackoutBreakerZone nearest = null;
                int bestHops = int.MaxValue;
                float bestDistance = float.MaxValue;
                foreach (BlackoutBreakerZone zone in BlackoutBreakerZones)
                {
                    int hops = graph != null ? graph.HopsFromZone(zone.Index, fixture.Position) : 0;
                    float distance = (fixture.Position - zone.Position).sqrMagnitude;
                    if (hops < bestHops || (hops == bestHops && distance < bestDistance))
                    {
                        bestHops = hops;
                        bestDistance = distance;
                        nearest = zone;
                    }
                }

                if (nearest == null)
                    continue;

                fixture.ZoneIndex = nearest.Index;
                nearest.Fixtures.Add(fixture);
            }

            if (BlackoutBreakerZones.Count > 1)
            {
                // Fixture count >= zone count is guaranteed at init (zones merge down
                // otherwise), so a donor with >= 2 fixtures always exists while any
                // zone is empty. Take the fixture nearest to the starved zone across
                // every donor, not just the largest one, so the donated fixture is
                // the best spatial fit available.
                foreach (BlackoutBreakerZone empty in BlackoutBreakerZones.Where(zone => zone.Fixtures.Count == 0).ToList())
                {
                    BlackoutBreakerZone donor = null;
                    BlackoutPoweredFixture fixture = null;
                    float bestDistance = float.MaxValue;
                    foreach (BlackoutBreakerZone candidateZone in BlackoutBreakerZones)
                    {
                        if (candidateZone.Fixtures.Count < 2)
                            continue;
                        foreach (BlackoutPoweredFixture candidate in candidateZone.Fixtures)
                        {
                            float distance = (candidate.Position - empty.Position).sqrMagnitude;
                            if (distance < bestDistance)
                            {
                                bestDistance = distance;
                                donor = candidateZone;
                                fixture = candidate;
                            }
                        }
                    }

                    if (donor == null || fixture == null)
                        break;

                    donor.Fixtures.Remove(fixture);
                    fixture.ZoneIndex = empty.Index;
                    empty.Fixtures.Add(fixture);
                }
            }

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout zone fixture assignment ({(graph != null ? graph.Method + ", " + graph.TileCount + " tiles" : "straight-line")}): {string.Join(", ", BlackoutBreakerZones.Select(zone => $"zone {zone.Index + 1}={zone.Fixtures.Count}"))} (total {BlackoutPoweredFixtures.Count}).");
        }

        private static void ApplyBlackoutZonePowerState()
        {
            if (BlackoutBreakerZones.Count == 0)
                return;

            float now = Time.time;
            bool pulseActive = _blackoutAmbientPulseZoneIndex >= 0 && now < _blackoutAmbientPulseEndTime;
            if (_blackoutAmbientPulseZoneIndex >= 0 && !pulseActive)
                _blackoutAmbientPulseZoneIndex = -1;
            // #172 item 4: a surge is a stutter, not a steady second of light. The zone is
            // sampled against a fixed on/off pattern over the pulse so it reads as a grid
            // trying and failing to hold, which is what makes it noticeable from a corridor.
            bool pulseLit = pulseActive && IsBlackoutSurgeLit(now - _blackoutAmbientPulseStartTime);

            foreach (BlackoutBreakerZone zone in BlackoutBreakerZones)
            {
                bool on = _blackoutCompleted
                    || zone.Restored
                    || (pulseLit && zone.Index == _blackoutAmbientPulseZoneIndex);
                foreach (BlackoutPoweredFixture fixture in zone.Fixtures)
                    SetBlackoutFixturePowered(fixture, on);
                UpdateBlackoutBreakerDistressEffect(zone, distressed: !_blackoutCompleted && !zone.Restored);
            }
        }

        // Runs from ApplyBlackoutZonePowerState (~1s cadence on every machine), so
        // effects self-attach after client zone rebuilds and self-clear on restore.
        private static void UpdateBlackoutBreakerDistressEffect(BlackoutBreakerZone zone, bool distressed)
        {
            if (zone?.Breaker == null)
                return;

            if (zone.DistressEffect == null)
            {
                if (!distressed)
                    return;
                ResolveBlackoutBreakerDistressAnchor(zone.Breaker, out Vector3 anchor, out Vector3 faceForward);
                zone.DistressEffect = BlackoutBreakerDistressEffect.Attach(zone.Breaker, anchor, faceForward);
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Blackout breaker zone {zone.Index + 1} distress anchored at {anchor:F2} facing {faceForward:F2} (pivot {zone.Breaker.transform.position:F2}).");
            }

            zone.DistressEffect?.SetDistressed(distressed);
        }

        /// <summary>
        /// #775. Where the distress smoke and sparks leave the box: the renderer bounds
        /// give the real panel regardless of where the Z-up prefab keeps its pivot, the
        /// mounted-wall probe gives the direction the panel faces, and the emitter sits a
        /// few centimetres off the face in the upper third where the lever bank is.
        /// </summary>
        private static void ResolveBlackoutBreakerDistressAnchor(BreakerBox breaker, out Vector3 anchor, out Vector3 faceForward)
        {
            faceForward = ResolveBlackoutBreakerFaceForward(breaker);
            Vector3 pivot = breaker.transform.position;
            // The mount pose puts the pivot 13 cm off the wall at lever height, so the
            // panel face is about a box depth ahead of it and the lever bank a little above.
            Vector3 fallback = pivot + faceForward * BlackoutBreakerDistressFallbackForward + Vector3.up * BlackoutBreakerDistressFallbackUp;

            // Second pass on #775: the whole-hierarchy bounds put the emitter 0.8 m out in
            // the room. The prefab carries renderers that are not the box (disabled trigger
            // meshes, the door's swing envelope), so only enabled, box-sized mesh renderers
            // count, and the result is clamped to the box footprint around the pivot.
            if (!TryGetBlackoutBreakerBodyBounds(breaker.gameObject, out Bounds bounds, out string used))
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Blackout breaker distress anchor: no box-sized renderer under '{breaker.name}'; using pivot fallback.");
                anchor = fallback;
                return;
            }

            Vector3 extents = bounds.extents;
            float depth = Mathf.Abs(extents.x * faceForward.x) + Mathf.Abs(extents.z * faceForward.z);
            Vector3 candidate = bounds.center
                + faceForward * (depth + 0.03f)
                + Vector3.up * (extents.y * 0.45f);

            Vector3 delta = candidate - pivot;
            float forwardOffset = Mathf.Clamp(Vector3.Dot(delta, faceForward), 0.05f, BlackoutBreakerDistressMaxForward);
            Vector3 lateralAxis = Vector3.Cross(Vector3.up, faceForward).normalized;
            float lateralOffset = Mathf.Clamp(Vector3.Dot(delta, lateralAxis), -BlackoutBreakerDistressMaxLateral, BlackoutBreakerDistressMaxLateral);
            float upOffset = Mathf.Clamp(delta.y, -BlackoutBreakerDistressMaxVertical, BlackoutBreakerDistressMaxVertical);
            anchor = pivot + faceForward * forwardOffset + lateralAxis * lateralOffset + Vector3.up * upOffset;
            ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Blackout breaker distress anchor from renderers [{used}] bounds center {bounds.center:F2} size {bounds.size:F2}; candidate {candidate:F2} clamped to {anchor:F2}.");
        }

        private const float BlackoutBreakerDistressFallbackForward = 0.2f;
        private const float BlackoutBreakerDistressFallbackUp = 0.25f;
        private const float BlackoutBreakerDistressMaxForward = 0.4f;
        private const float BlackoutBreakerDistressMaxLateral = 0.3f;
        private const float BlackoutBreakerDistressMaxVertical = 0.5f;
        private const float BlackoutBreakerBodyRendererMaxSize = 1.6f;

        /// <summary>Bounds of the breaker's visible body only: enabled mesh renderers on
        /// active objects whose own bounds are no larger than a breaker box.</summary>
        private static bool TryGetBlackoutBreakerBodyBounds(GameObject root, out Bounds bounds, out string used)
        {
            bounds = default;
            used = string.Empty;
            if (root == null)
                return false;

            bool hasBounds = false;
            var names = new List<string>(4);
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                    continue;

                Bounds own = renderer.bounds;
                Vector3 size = own.size;
                if (size.x > BlackoutBreakerBodyRendererMaxSize || size.y > BlackoutBreakerBodyRendererMaxSize || size.z > BlackoutBreakerBodyRendererMaxSize)
                    continue;

                if (!hasBounds)
                {
                    bounds = own;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(own);
                }

                if (names.Count < 6)
                    names.Add($"{renderer.name}:{size.x:0.00}x{size.y:0.00}x{size.z:0.00}");
            }

            used = string.Join(", ", names);
            return hasBounds;
        }

        private static Vector3 ResolveBlackoutBreakerFaceForward(BreakerBox breaker)
        {
            if (FindBlackoutMountedWallNormal(breaker, out Vector3 wallNormal, out _))
                return wallNormal;

            // No wall found: fall back to the prefab's own out-of-wall axis. The vanilla
            // mesh is authored Z-up, so its outward face is -up in local space; the
            // horizontal forward only wins when the box was re-authored upright.
            Quaternion rotation = breaker.transform.rotation;
            Vector3 horizontalOut = -(rotation * Vector3.up);
            horizontalOut.y = 0f;
            Vector3 horizontalForward = rotation * Vector3.forward;
            horizontalForward.y = 0f;
            Vector3 pick = horizontalForward.sqrMagnitude >= horizontalOut.sqrMagnitude ? horizontalForward : horizontalOut;
            return pick.sqrMagnitude > 1e-4f ? pick.normalized : Vector3.forward;
        }

        private static void SetBlackoutFixturePowered(BlackoutPoweredFixture fixture, bool on)
        {
            if (fixture == null)
                return;

            // #716/B4: the zone re-apply runs at 1 Hz (0.1 s while a zone is being
            // enforced) and used to rewrite every fixture whether or not anything had
            // changed. The applied state is read back off the fixture itself rather than
            // cached beside it, because reasserting over an outside change — a vanilla
            // breaker flip, ship power, another mod — is exactly what this loop exists for,
            // and a desired-state cache would make it stop doing that.
            Animator animator = fixture.Animator;
            if (animator != null && animator.GetBool(BlackoutFixturePoweredParameter) != on)
                animator.SetBool(BlackoutFixturePoweredParameter, on);

            List<Light> lights = fixture.Lights;
            for (int i = 0; i < lights.Count; i++)
            {
                Light light = lights[i];
                if (light != null && light.enabled != on)
                    light.enabled = on;
            }
        }

        private static void TriggerBlackoutZoneFlicker(BlackoutBreakerZone zone)
        {
            if (zone == null)
                return;

            foreach (BlackoutPoweredFixture fixture in zone.Fixtures)
            {
                if (fixture?.Animator != null)
                    fixture.Animator.SetTrigger("Flicker");
            }
        }

        // #775. A restored zone used to fire the Flicker trigger in the same frame its
        // fixtures went from off to on, and clients restoring off the host snapshot got
        // no trigger at all, so the wing simply snapped on. Vanilla only ever flickers
        // lights that are already lit (RoundManager.FlickerPoweredLights walks the
        // animators in four staggered batches 50 ms apart), so the restore now powers
        // the zone first and replays that stagger a beat later on every machine.
        private const float BlackoutRestoreFlickerDelaySeconds = 0.2f;
        private const float BlackoutRestoreFlickerBatchSeconds = 0.05f;
        private const int BlackoutRestoreFlickerBatches = 4;
        private static readonly List<int> _blackoutRestoreFlickerZones = new List<int>();
        private static float _blackoutRestoreFlickerNextTime;
        private static int _blackoutRestoreFlickerBatch;

        private static void BeginBlackoutRestoreFlicker(BlackoutBreakerZone zone)
        {
            if (zone == null || zone.Fixtures.Count == 0 || _blackoutRestoreFlickerZones.Contains(zone.Index))
                return;

            if (_blackoutRestoreFlickerZones.Count == 0)
            {
                _blackoutRestoreFlickerBatch = 0;
                _blackoutRestoreFlickerNextTime = Time.time + BlackoutRestoreFlickerDelaySeconds;
            }

            _blackoutRestoreFlickerZones.Add(zone.Index);
        }

        private static void ClearBlackoutRestoreFlicker()
        {
            _blackoutRestoreFlickerZones.Clear();
            _blackoutRestoreFlickerBatch = 0;
            _blackoutRestoreFlickerNextTime = 0f;
        }

        private static void PumpBlackoutRestoreFlicker()
        {
            if (_blackoutRestoreFlickerZones.Count == 0 || Time.time < _blackoutRestoreFlickerNextTime)
                return;

            int batch = _blackoutRestoreFlickerBatch;
            foreach (int zoneIndex in _blackoutRestoreFlickerZones)
            {
                BlackoutBreakerZone zone = BlackoutBreakerZones.FirstOrDefault(z => z != null && z.Index == zoneIndex);
                if (zone == null)
                    continue;

                // Same slicing as vanilla: batch n covers the fixtures from the previous
                // batch's end up to count / (batches - n), so the last batch reaches the end.
                int count = zone.Fixtures.Count;
                int from = batch == 0 ? 0 : count / (BlackoutRestoreFlickerBatches - batch + 1);
                int to = count / (BlackoutRestoreFlickerBatches - batch);
                for (int i = from; i < to && i < count; i++)
                {
                    Animator animator = zone.Fixtures[i]?.Animator;
                    if (animator != null)
                        animator.SetTrigger("Flicker");
                }
            }

            _blackoutRestoreFlickerBatch++;
            if (_blackoutRestoreFlickerBatch >= BlackoutRestoreFlickerBatches)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Blackout restore flicker played for zone(s) {string.Join(", ", _blackoutRestoreFlickerZones.Select(i => i + 1))}.");
                ClearBlackoutRestoreFlicker();
                return;
            }

            _blackoutRestoreFlickerNextTime = Time.time + BlackoutRestoreFlickerBatchSeconds;
        }

        private static void MarkBlackoutZoneStateDirty(float seconds = 0.75f)
        {
            _blackoutZoneEnforceUntil = Mathf.Max(_blackoutZoneEnforceUntil, Time.time + Mathf.Max(0.1f, seconds));
            _blackoutNextPowerForceTime = 0f;
        }

        private static void ScheduleNextBlackoutAmbientFlicker()
        {
            if (BlackoutBreakerZones.Count == 0)
                return;

            int total = Mathf.Max(1, BlackoutBreakerZones.Count);
            int restored = BlackoutBreakerZones.Count(zone => zone.Restored);
            float progress = Mathf.Clamp01((float)restored / total);
            float interval = GetBlackoutAmbientFlickerIntervalSecondsForCurrentMoon();
            interval *= Mathf.Lerp(1f, 0.65f, progress);
            interval *= UnityEngine.Random.Range(0.9f, 1.1f);
            _blackoutNextAmbientPulseTime = Time.time + Mathf.Max(20f, interval);
        }

        // #172 item 4. Surge stutter pattern in seconds from pulse start: lit, dark, lit,
        // dark, lit-until-end. Three visible strikes inside two seconds, so a player who
        // glances up during any of them sees the wing try to come back.
        private static readonly float[] BlackoutSurgePatternEdges = { 0.22f, 0.34f, 0.80f, 0.95f };
        private static float _blackoutAmbientPulseStartTime;
        private const float BlackoutSurgePlayerBiasRadius = 14f;
        private const float BlackoutSurgePlayerBiasChance = 0.75f;

        private static bool IsBlackoutSurgeLit(float elapsed)
        {
            // Segment index 0 lit, 1 dark, 2 lit, 3 dark, 4 lit.
            int segment = 0;
            for (int i = 0; i < BlackoutSurgePatternEdges.Length; i++)
            {
                if (elapsed >= BlackoutSurgePatternEdges[i])
                    segment = i + 1;
            }

            return (segment & 1) == 0;
        }

        /// <summary>
        /// #172 item 4. Host-only scheduling. The old version ran on every machine with its
        /// own random state, so host and clients surged different zones at different times
        /// and no two players ever saw the same flicker. Clients now only expire the pulse
        /// the host told them about.
        /// </summary>
        private static void UpdateBlackoutAmbientFlicker()
        {
            if (BlackoutBreakerZones.Count == 0 || _blackoutCompleted)
                return;

            float now = Time.time;
            if (_blackoutAmbientPulseZoneIndex >= 0)
            {
                if (now >= _blackoutAmbientPulseEndTime)
                {
                    _blackoutAmbientPulseZoneIndex = -1;
                    ApplyBlackoutZonePowerState();
                }
                return;
            }

            if (NetworkManager.Singleton?.IsServer != true)
                return;

            if (now < _blackoutNextAmbientPulseTime)
                return;

            var darkZones = BlackoutBreakerZones.Where(zone => !zone.Restored && zone.Fixtures.Count > 0).ToList();
            if (darkZones.Count == 0)
                return;

            // Bias toward a wing with a player in it: the surge is meant to be seen, and a
            // pulse in an empty wing is a log line and nothing else.
            BlackoutBreakerZone zone = null;
            if (UnityEngine.Random.value < BlackoutSurgePlayerBiasChance)
            {
                var occupied = darkZones.Where(IsBlackoutZoneOccupied).ToList();
                if (occupied.Count > 0)
                    zone = occupied[UnityEngine.Random.Range(0, occupied.Count)];
            }
            zone ??= darkZones[UnityEngine.Random.Range(0, darkZones.Count)];

            float duration = UnityEngine.Random.Range(1.4f, 2.2f);
            BeginBlackoutSurge(zone.Index, duration, "host schedule");
            SendBlackoutSurge(zone.Index, duration);
            ScheduleNextBlackoutAmbientFlicker();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout ambient surge pulsed zone {zone.Index + 1}/{BlackoutBreakerZones.Count} ({zone.Fixtures.Count} fixture(s)) for {duration:0.##}s; next window ~{_blackoutNextAmbientPulseTime - now:0}s.");
        }

        private static bool IsBlackoutZoneOccupied(BlackoutBreakerZone zone)
        {
            var players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || zone == null)
                return false;

            float radiusSq = BlackoutSurgePlayerBiasRadius * BlackoutSurgePlayerBiasRadius;
            foreach (var player in players)
            {
                if (player == null || !player.isPlayerControlled || player.isPlayerDead || !player.isInsideFactory)
                    continue;

                Vector3 position = player.transform.position;
                foreach (BlackoutPoweredFixture fixture in zone.Fixtures)
                {
                    if ((fixture.Position - position).sqrMagnitude <= radiusSq)
                        return true;
                }
            }

            return false;
        }

        /// <summary>Starts a surge locally: same call on the host after scheduling and on a
        /// client after the message lands. The zone's fixtures get the animator flicker and
        /// the breaker plays its switch clip as the audible cue the old surge never had.</summary>
        private static void BeginBlackoutSurge(int zoneIndex, float duration, string source)
        {
            BlackoutBreakerZone zone = BlackoutBreakerZones.FirstOrDefault(z => z != null && z.Index == zoneIndex);
            if (zone == null || zone.Restored || _blackoutCompleted)
                return;

            float now = Time.time;
            _blackoutAmbientPulseZoneIndex = zone.Index;
            _blackoutAmbientPulseStartTime = now;
            _blackoutAmbientPulseEndTime = now + Mathf.Clamp(duration, 0.5f, 4f);
            TriggerBlackoutZoneFlicker(zone);
            ApplyBlackoutZonePowerState();
            MarkBlackoutZoneStateDirty(duration + 0.2f);

            BreakerBox breaker = zone.Breaker;
            if (breaker != null && breaker.thisAudioSource != null && breaker.switchPowerSFX != null)
                breaker.thisAudioSource.PlayOneShot(breaker.switchPowerSFX, 0.9f);

            if (!string.Equals(source, "host schedule", StringComparison.Ordinal))
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Blackout ambient surge applied from {source}: zone {zone.Index + 1}/{BlackoutBreakerZones.Count} for {duration:0.##}s.");
        }

        private static void SendBlackoutSurge(int zoneIndex, float duration)
        {
            CustomMessagingManager manager = NetworkManager.Singleton?.CustomMessagingManager;
            if (manager == null || NetworkManager.Singleton?.IsServer != true)
                return;

            using (var writer = new FastBufferWriter(sizeof(int) + sizeof(float), Allocator.Temp))
            {
                writer.WriteValueSafe(zoneIndex);
                writer.WriteValueSafe(duration);
                manager.SendNamedMessageToAll(BlackoutSurgeMessage, writer);
            }
        }

        private static void OnBlackoutSurge(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId) return;
            if (NetworkManager.Singleton?.IsServer == true) return;

            reader.ReadValueSafe(out int zoneIndex);
            reader.ReadValueSafe(out float duration);
            if (GetCurrentContract() != MoonContractType.BlackoutAudit || _blackoutUnsupportedThisRound)
                return;

            BeginBlackoutSurge(zoneIndex, duration, "host surge message");
        }

        private static void UpdateBlackoutAudit()
        {
            if (_blackoutCompleted || _blackoutUnsupportedThisRound)
                return;

            if (!IsBlackoutRuntimeSupported(allowFlowPrediction: false))
            {
                DisableBlackoutForCurrentRound("generated interior has no switchable powered lights");
                return;
            }

            // #716/A4: a parked client rebuild owns the sliced fixture build while it runs;
            // the normal init pass must not start a second one over the same state.
            if (PumpBlackoutSnapshotRebuild())
                return;

            EnsureBlackoutAudit();
            if (_blackoutCompleted || _blackoutUnsupportedThisRound)
                return;

            MaintainBlackoutPowerOff();
            MaintainBlackoutPlayerNightVisionOff();
            PumpBlackoutRestoreFlicker();
            UpdateBlackoutAmbientFlicker();
            AuditBlackoutLocalLights();
        }

        private static void MaintainBlackoutPlayerNightVisionOff()
        {
            PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;

            // The local player is swept every pass: its NightVision is the one light
            // that would visibly defeat the blackout on this machine, so it never waits
            // for its slot in the sliced walk below.
            if (localPlayer != null)
                SuppressBlackoutNightVision(localPlayer.nightVision);

            var players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || players.Length == 0)
            {
                _blackoutNightVisionSweepCursor = 0;
                return;
            }

            // #290: the remaining slots are walked a few per pass with a wrapping
            // cursor instead of all 32 in the audit's first frame. The cursor keeps
            // wrapping while the audit runs, so every slot is still swept (and
            // re-swept for players who join or respawn) within a handful of frames.
            int slots = Mathf.Min(BlackoutNightVisionSlotsPerPass, players.Length);
            for (int i = 0; i < slots; i++)
            {
                if (_blackoutNightVisionSweepCursor >= players.Length)
                    _blackoutNightVisionSweepCursor = 0;

                PlayerControllerB player = players[_blackoutNightVisionSweepCursor++];
                if (player == null || player == localPlayer)
                    continue;

                SuppressBlackoutNightVision(player.nightVision);
            }
        }

        private static void SuppressBlackoutNightVision(Light nightVision)
        {
            if (nightVision == null)
                return;

            if (!BlackoutSuppressedNightVision.ContainsKey(nightVision))
            {
                BlackoutSuppressedNightVision[nightVision] = new BlackoutNightVisionState(nightVision);
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Blackout Audit suppressed player NightVision light '{GetTransformPath(nightVision.transform)}' (intensity={nightVision.intensity:0.###}, range={nightVision.range:0.###}, enabled={nightVision.enabled}).");
            }

            nightVision.enabled = false;
            nightVision.intensity = 0f;
            nightVision.range = 0f;
        }

        private static void RestoreBlackoutPlayerNightVision()
        {
            if (BlackoutSuppressedNightVision.Count == 0)
                return;

            int restored = 0;
            foreach (var pair in BlackoutSuppressedNightVision.ToList())
            {
                Light light = pair.Key;
                if (light == null)
                    continue;

                BlackoutNightVisionState state = pair.Value;
                light.intensity = state.Intensity;
                light.range = state.Range;
                light.enabled = state.Enabled;
                restored++;
            }

            BlackoutSuppressedNightVision.Clear();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout Audit restored {restored} player NightVision light(s).");
        }

        // #290: one full sweep (FindObjectsOfType<Light> plus a per-light test against
        // every dungeon tile) measured 20-24 ms and ran inside the same frame as audit
        // setup. The sweep now runs as a sliced pass: it snapshots its inputs once,
        // examines a bounded number of lights per RoundManager.Update, and reports when
        // the cursor reaches the end. The 2 s window is measured from pass completion.
        private static void AuditBlackoutLocalLights()
        {
            // #716/B4: this is a diagnostic, not gameplay. Every 2 s it ran
            // FindObjectsOfType<Light> and then, per light, a transform-path string, eight
            // IndexOf calls and three GetComponentInParent, to produce one Info line nobody
            // reads during play. It now runs only while a diagnostics switch is on.
            if (ContractsBootstrap.BlackoutLightAuditVerboseLogging?.Value != true
                && ContractsBootstrap.PerformanceTimingLogging?.Value != true)
            {
                if (_blackoutLightAuditPending != null)
                    ResetBlackoutLightAuditPass();
                return;
            }

            if (_blackoutLightAuditPending == null)
            {
                if (Time.time < _blackoutNextLightAuditTime)
                    return;

                if (!TryBeginBlackoutLightAuditPass())
                {
                    _blackoutNextLightAuditTime = Time.time + 2f;
                    return;
                }
            }

            // The audit can end (death, exit, contract completion) mid-slice; drop the
            // partial pass rather than reporting against a stale viewpoint.
            if (_blackoutLightAuditPlayerTransform == null || _blackoutLightAuditCameraTransform == null)
            {
                ResetBlackoutLightAuditPass();
                _blackoutNextLightAuditTime = Time.time + 2f;
                return;
            }

            Light[] lights = _blackoutLightAuditPending;
            int end = Mathf.Min(lights.Length, _blackoutLightAuditCursor + BlackoutLightAuditLightsPerPass);
            for (; _blackoutLightAuditCursor < end; _blackoutLightAuditCursor++)
                ExamineBlackoutLightAuditCandidate(lights[_blackoutLightAuditCursor]);

            if (_blackoutLightAuditCursor < lights.Length)
                return;

            ReportBlackoutLightAuditPass();
            ResetBlackoutLightAuditPass();
            _blackoutNextLightAuditTime = Time.time + 2f;
        }

        private static bool TryBeginBlackoutLightAuditPass()
        {
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (player == null || player.isPlayerDead || !player.isInsideFactory)
                return false;

            Transform playerTransform = player.transform;
            Transform cameraTransform = player.gameplayCamera != null
                ? player.gameplayCamera.transform
                : playerTransform;
            if (playerTransform == null || cameraTransform == null)
                return false;

            _blackoutLightAuditPlayerTransform = playerTransform;
            _blackoutLightAuditCameraTransform = cameraTransform;
            _blackoutLightAuditPending = UnityEngine.Object.FindObjectsOfType<Light>();
            _blackoutLightAuditCursor = 0;
            _blackoutLightAuditEnabledCount = 0;
            _blackoutLightAuditInsideDungeonCount = 0;
            _blackoutLightAuditUntrackedCount = 0;
            _blackoutLightAuditSuspects = new List<BlackoutLightAuditRecord>();

            // Lights the blackout system owns: legitimately on when their zone is
            // restored. Anything else enabled inside dungeon bounds is leakage.
            _blackoutLightAuditTrackedLights = new HashSet<Light>();
            foreach (BlackoutPoweredFixture fixture in BlackoutPoweredFixtures)
            {
                foreach (Light tracked in fixture.Lights)
                {
                    if (tracked != null)
                        _blackoutLightAuditTrackedLights.Add(tracked);
                }
            }

            // Tile bounds are snapshotted once per pass instead of re-walking
            // Dungeon.AllTiles for every light, which was the bulk of the sweep cost.
            _blackoutLightAuditTileBounds = SnapshotBlackoutDungeonTileBounds();
            return true;
        }

        private static void ResetBlackoutLightAuditPass()
        {
            _blackoutLightAuditPending = null;
            _blackoutLightAuditCursor = 0;
            _blackoutLightAuditTileBounds = null;
            _blackoutLightAuditTrackedLights = null;
            _blackoutLightAuditSuspects = null;
            _blackoutLightAuditPlayerTransform = null;
            _blackoutLightAuditCameraTransform = null;
            _blackoutLightAuditEnabledCount = 0;
            _blackoutLightAuditInsideDungeonCount = 0;
            _blackoutLightAuditUntrackedCount = 0;
        }

        private static void ExamineBlackoutLightAuditCandidate(Light light)
        {
            if (light == null || !light.enabled || !light.gameObject.activeInHierarchy || light.intensity <= 0.0001f)
                return;

            Transform playerTransform = _blackoutLightAuditPlayerTransform;
            Transform cameraTransform = _blackoutLightAuditCameraTransform;

            _blackoutLightAuditEnabledCount++;
            Transform lightTransform = light.transform;
            float playerDistance = Vector3.Distance(playerTransform.position, lightTransform.position);
            float cameraDistance = Vector3.Distance(cameraTransform.position, lightTransform.position);
            float viewDot = light.type == LightType.Spot
                ? Vector3.Dot(lightTransform.forward.normalized, cameraTransform.forward.normalized)
                : 0f;
            string path = GetTransformPath(lightTransform);
            bool cctv = ContainsIgnoreCase(path, "LethalCCTV")
                || ContainsIgnoreCase(path, "CCTV")
                || ContainsIgnoreCase(path, "NightVisionFillLight");
            bool playerAttached = lightTransform.IsChildOf(playerTransform);
            bool cameraAttached = lightTransform.IsChildOf(cameraTransform);
            bool namedPlayerLight = ContainsIgnoreCase(path, "helmet")
                || ContainsIgnoreCase(path, "nightvision")
                || ContainsIgnoreCase(path, "night vision")
                || ContainsIgnoreCase(path, "flashlight")
                || ContainsIgnoreCase(path, "glow");
            bool viewAligned = light.type == LightType.Spot && cameraDistance <= 2.5f && viewDot >= 0.9f;
            bool nearby = playerDistance <= 18f || cameraDistance <= 18f;

            bool insideDungeon = IsPositionInsideBlackoutTileBounds(lightTransform.position, _blackoutLightAuditTileBounds);
            if (insideDungeon)
                _blackoutLightAuditInsideDungeonCount++;
            bool interiorUntracked = insideDungeon
                && _blackoutLightAuditTrackedLights?.Contains(light) != true
                && !cctv && !playerAttached && !cameraAttached && !namedPlayerLight
                && !IsBlackoutSweepExemptLight(light);
            if (interiorUntracked)
                _blackoutLightAuditUntrackedCount++;

            if (!interiorUntracked && !cctv && !playerAttached && !cameraAttached && !namedPlayerLight && !viewAligned && !nearby)
                return;

            _blackoutLightAuditSuspects.Add(new BlackoutLightAuditRecord(
                light,
                path,
                BuildBlackoutLightTags(cctv, playerAttached, cameraAttached, namedPlayerLight, viewAligned, nearby, interiorUntracked),
                playerDistance,
                cameraDistance,
                viewDot,
                GetBlackoutLightPriority(cctv, playerAttached, cameraAttached, namedPlayerLight, viewAligned, nearby, interiorUntracked)));
        }

        // #290: the sweep used to emit one Info line per suspect on every pass, which
        // added up to a 2,036 ms hitch right after the audit started. A pass now reports
        // exactly one line with the counts plus a few notable suspects; the full per-light
        // dump stays available behind the BlackoutLightAuditVerboseLogging toggle.
        private static void ReportBlackoutLightAuditPass()
        {
            List<BlackoutLightAuditRecord> suspects = _blackoutLightAuditSuspects;
            if (suspects == null || suspects.Count == 0)
            {
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.BlackoutLightAudit] No enabled light suspects found. Enabled scene lights={_blackoutLightAuditEnabledCount}, inside dungeon={_blackoutLightAuditInsideDungeonCount}, interior untracked={_blackoutLightAuditUntrackedCount}.");
                return;
            }

            var ordered = suspects
                .OrderByDescending(record => record.Priority)
                .ThenBy(record => Mathf.Min(record.PlayerDistance, record.CameraDistance))
                .ToList();

            string notable = string.Join("; ", ordered
                .Take(BlackoutLightAuditSummaryExamples)
                .Select(record => $"{record.Tags}:'{(record.Light != null ? record.Light.name : "<destroyed>")}'@{Mathf.Min(record.PlayerDistance, record.CameraDistance):0.#}m")
                .ToArray());

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.BlackoutLightAudit] {suspects.Count} suspect light(s), enabled scene lights={_blackoutLightAuditEnabledCount}, inside dungeon={_blackoutLightAuditInsideDungeonCount}, interior untracked={_blackoutLightAuditUntrackedCount}, player={FormatVector(_blackoutLightAuditPlayerTransform.position)}; top: {notable}.");

            if (ContractsBootstrap.BlackoutLightAuditVerboseLogging?.Value != true)
                return;

            foreach (BlackoutLightAuditRecord record in ordered.Take(BlackoutLightAuditVerboseDetailLines))
            {
                Light light = record.Light;
                if (light == null)
                    continue;

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.BlackoutLightAudit] tags={record.Tags}; name='{light.name}'; path='{record.Path}'; type={light.type}; intensity={light.intensity:0.###}; range={light.range:0.###}; spot={light.spotAngle:0.#}; pDist={record.PlayerDistance:0.##}; camDist={record.CameraDistance:0.##}; viewDot={record.ViewDot:0.###}; layer={light.gameObject.layer}; mask={light.cullingMask}");
            }
        }

        private static Bounds[] SnapshotBlackoutDungeonTileBounds()
        {
            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null)
                return new Bounds[0];

            var bounds = new List<Bounds>();
            foreach (Tile tile in dungeon.AllTiles)
            {
                if (tile != null)
                    bounds.Add(tile.Bounds);
            }

            return bounds.ToArray();
        }

        private static bool IsPositionInsideBlackoutTileBounds(Vector3 position, Bounds[] tileBounds)
        {
            if (tileBounds == null)
                return false;

            for (int i = 0; i < tileBounds.Length; i++)
            {
                Bounds bounds = tileBounds[i];
                if (position.x >= bounds.min.x - 0.4f && position.x <= bounds.max.x + 0.4f
                    && position.z >= bounds.min.z - 0.4f && position.z <= bounds.max.z + 0.4f
                    && position.y >= bounds.min.y - 0.4f && position.y <= bounds.max.y + 0.4f)
                {
                    return true;
                }
            }

            return false;
        }

        private static int GetBlackoutLightPriority(
            bool cctv,
            bool playerAttached,
            bool cameraAttached,
            bool namedPlayerLight,
            bool viewAligned,
            bool nearby,
            bool interiorUntracked)
        {
            int priority = 0;
            if (interiorUntracked)
                priority += 90;
            if (viewAligned)
                priority += 80;
            if (cameraAttached)
                priority += 70;
            if (playerAttached)
                priority += 60;
            if (cctv)
                priority += 50;
            if (namedPlayerLight)
                priority += 40;
            if (nearby)
                priority += 5;
            return priority;
        }

        private static string BuildBlackoutLightTags(
            bool cctv,
            bool playerAttached,
            bool cameraAttached,
            bool namedPlayerLight,
            bool viewAligned,
            bool nearby,
            bool interiorUntracked)
        {
            var tags = new List<string>();
            if (interiorUntracked)
                tags.Add("interior-untracked");
            if (cctv)
                tags.Add("cctv");
            if (playerAttached)
                tags.Add("player-child");
            if (cameraAttached)
                tags.Add("camera-child");
            if (namedPlayerLight)
                tags.Add("named-player-light");
            if (viewAligned)
                tags.Add("view-aligned-spot");
            if (nearby)
                tags.Add("nearby");
            return tags.Count > 0 ? string.Join(",", tags) : "none";
        }

        private static bool ContainsIgnoreCase(string value, string fragment)
        {
            return !string.IsNullOrEmpty(value)
                && !string.IsNullOrEmpty(fragment)
                && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            var parts = new List<string>();
            Transform current = transform;
            while (current != null && parts.Count < 12)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            string path = string.Join("/", parts);
            return current != null ? ".../" + path : path;
        }

        private static bool HasPlayerFoundBlackoutBreaker()
        {
            if (BlackoutBreakerZones.Count > 0)
                return GetBlackoutFoundBreakerCount() > 0;

            if (_blackoutBreaker == null)
                _blackoutBreaker = UnityEngine.Object.FindObjectOfType<BreakerBox>();
            if (_blackoutBreaker == null)
                return _blackoutLever != null;

            var players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return false;

            Vector3 target = _blackoutBreaker.transform.position;
            foreach (var player in players)
            {
                if (player == null || !player.isPlayerControlled || player.isPlayerDead || !player.isInsideFactory)
                    continue;
                if (Vector3.Distance(player.transform.position, target) <= 7f)
                    return true;
            }

            return false;
        }

        private static bool IsBlackoutAuditComplete()
        {
            if (_blackoutUnsupportedThisRound)
                return false;
            if (_blackoutCompleted)
                return true;

            if (BlackoutBreakerZones.Count > 0)
            {
                SynchronizeBlackoutBreakerZonesFromBreakers();
                if (AllBlackoutBreakerZonesRestored())
                {
                    CompleteBlackoutAudit("breaker boxes");
                    return true;
                }

                return false;
            }

            if (_blackoutLever != null && _blackoutLever.Pulled)
            {
                CompleteBlackoutAudit("audit lever");
                return true;
            }

            if (_blackoutBreaker != null && (_blackoutBreaker.isPowerOn || _blackoutBreaker.leversSwitchedOff <= 0))
            {
                CompleteBlackoutAudit("breaker box");
                return true;
            }

            return false;
        }

        /// <summary>
        /// #406: every completion path resolves its actor the same way - through the breaker
        /// trigger latch, which is the only blackout actor that reaches the host when the player
        /// responsible is a remote client. Per-player credit for this contract is therefore
        /// guaranteed on the breaker paths only; every other path falls back to the crew-wide
        /// fan-out, which is what this contract did for all paths before #406.
        /// </summary>
        internal static void CompleteBlackoutAudit(string source)
        {
            CompleteBlackoutAudit(source, BlackoutBreakerActorLatch.ResolveActorClientId());
        }

        internal static void CompleteBlackoutAudit(string source, ulong actorClientId)
        {
            if (GetCurrentContract() != MoonContractType.BlackoutAudit || _blackoutCompleted || _blackoutUnsupportedThisRound)
                return;

            _blackoutCompleted = true;
            // Drop any in-flight sliced sweep: the audit is over, so a partial pass has
            // nothing left to report.
            _blackoutNightVisionSweepCursor = 0;
            ResetBlackoutLightAuditPass();
            RestoreBlackoutPower(source);
            SendBlackoutZoneSnapshot();
            ContractProgressionEvents.PublishBlackoutAuditRestored(source, actorClientId);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Blackout Audit completed by {source} "
                + (ContractProgressionEventIds.IsAttributable(actorClientId)
                    ? $"(credited to client {actorClientId})."
                    : "(no identifiable actor; credited crew-wide)."));
        }

        internal static void NotifyBreakerPowerRestored(BreakerBox breaker)
        {
            if (breaker == null || _blackoutUnsupportedThisRound || GetCurrentContract() != MoonContractType.BlackoutAudit)
                return;
            if (BlackoutBreakerZones.Count > 0)
            {
                MarkBlackoutBreakerZoneRestored(breaker);
                return;
            }

            if (breaker.leversSwitchedOff > 0 && !breaker.isPowerOn)
                return;

            _blackoutBreaker = breaker;
            // #794: the lone breaker is zone 1/1 for the report's per-breaker line.
            ContractProgressionEvents.PublishBlackoutBreakerRestored(0, 1, 1, BlackoutBreakerActorLatch.ResolveActorClientId());
            CompleteBlackoutAudit("single breaker box");
        }

        private static BlackoutBreakerZone FindBlackoutBreakerZone(BreakerBox breaker)
        {
            if (breaker == null)
                return null;

            return BlackoutBreakerZones.FirstOrDefault(zone => zone.Breaker == breaker);
        }

        private static void SynchronizeBlackoutBreakerZonesFromBreakers()
        {
            foreach (BlackoutBreakerZone zone in BlackoutBreakerZones)
            {
                if (zone?.Breaker == null || zone.Restored)
                    continue;
                if (zone.Breaker.leversSwitchedOff <= 0)
                    zone.Restored = true;
            }
        }

        private static bool AllBlackoutBreakerZonesRestored()
        {
            return BlackoutBreakerZones.Count > 0 && BlackoutBreakerZones.All(zone => zone.Restored);
        }

        private static void MarkBlackoutBreakerZoneRestored(BreakerBox breaker)
        {
            BlackoutBreakerZone zone = FindBlackoutBreakerZone(breaker);
            if (zone == null)
                return;
            if (breaker.leversSwitchedOff > 0)
            {
                // #172: this silent return is the "I flipped it and nothing happened" case.
                // Every lever on the box must be up; say so in the log so a report can be
                // told apart from a zone that restored nothing.
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout breaker zone {zone.Index + 1}/{BlackoutBreakerZones.Count} switched on with {breaker.leversSwitchedOff} lever(s) still down; waiting for the rest.");
                return;
            }
            if (zone.Restored)
            {
                ApplyBlackoutZonePowerState();
                return;
            }

            zone.Restored = true;
            int restoredZones = BlackoutBreakerZones.Count(z => z != null && z.Restored);
            MoonContractRoundStats.RecordBlackoutBreakerRestored(restoredZones, BlackoutBreakerZones.Count);
            // #794: per-zone credit to whoever flipped this box, so a partial restore on a
            // failed round still reads back on the report and can be paid. Resolved from the
            // same latch the completion publish below reads for the final zone.
            ContractProgressionEvents.PublishBlackoutBreakerRestored(
                zone.Index,
                BlackoutBreakerZones.Count,
                restoredZones,
                BlackoutBreakerActorLatch.ResolveActorClientId());
            zone.Breaker.isPowerOn = true;
            if (zone.Breaker.breakerBoxHum != null && !zone.Breaker.breakerBoxHum.isPlaying)
                zone.Breaker.breakerBoxHum.Play();

            if (AllBlackoutBreakerZonesRestored())
            {
                _blackoutCompleted = true;
                RestoreBlackoutPower("breaker boxes");
                SendBlackoutZoneSnapshot();
                // #406: the actor is the player who just flipped this final breaker; the latch was
                // set from the switch trigger on the way in, on the host even for a remote flip.
                ulong finalBreakerActor = BlackoutBreakerActorLatch.ResolveActorClientId();
                ContractProgressionEvents.PublishBlackoutAuditRestored("breaker boxes", finalBreakerActor);
                ContractsBootstrap.Log?.LogInfo(
                    "[MoonContracts] Blackout Audit completed by restoring all breaker boxes "
                    + (ContractProgressionEventIds.IsAttributable(finalBreakerActor)
                        ? $"(credited to client {finalBreakerActor})."
                        : "(no identifiable actor; credited crew-wide)."));
                return;
            }

            ApplyBlackoutZonePowerState();
            BeginBlackoutRestoreFlicker(zone);
            MarkBlackoutZoneStateDirty(1.25f);
            ScheduleNextBlackoutAmbientFlicker();
            SendBlackoutZoneSnapshot();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout breaker zone {zone.Index + 1}/{BlackoutBreakerZones.Count} restored; powered {zone.Fixtures.Count} fixture(s).");
        }

        private static int GetBlackoutFoundBreakerCount()
        {
            var players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || BlackoutBreakerZones.Count == 0)
                return 0;

            foreach (BlackoutBreakerZone zone in BlackoutBreakerZones)
            {
                if (zone.Found || zone.Breaker == null)
                    continue;

                Vector3 target = zone.Breaker.transform.position;
                foreach (var player in players)
                {
                    if (player == null || !player.isPlayerControlled || player.isPlayerDead || !player.isInsideFactory)
                        continue;
                    if (Vector3.Distance(player.transform.position, target) <= 7f)
                    {
                        zone.Found = true;
                        break;
                    }
                }
            }

            return BlackoutBreakerZones.Count(zone => zone.Found || zone.Restored);
        }

        /// <summary>
        /// #725. Share of powered fixtures currently sitting in a restored zone, 0-100. Ambient
        /// pulses are deliberately excluded so the readout does not twitch every surge; it only
        /// moves when a breaker comes back.
        /// </summary>
        private static int GetBlackoutGridOutputPercent(bool complete)
        {
            if (complete)
                return 100;

            int total = 0;
            int restored = 0;
            foreach (BlackoutBreakerZone zone in BlackoutBreakerZones)
            {
                int count = zone.Fixtures.Count;
                total += count;
                if (zone.Restored)
                    restored += count;
            }

            if (total <= 0)
                return 0;

            return Mathf.Clamp(Mathf.RoundToInt(restored * 100f / total), 0, 100);
        }

        private static int GetBlackoutRestoredBreakerCount()
        {
            SynchronizeBlackoutBreakerZonesFromBreakers();
            return BlackoutBreakerZones.Count(zone => zone.Restored);
        }

        /// <summary>#716/A4: entrances do not move once the dungeon is generated, so the
        /// blackout build's repeated <c>FindObjectsOfType&lt;EntranceTeleport&gt;</c> is one
        /// scan per round. Re-scanned if any cached entry has been destroyed (a scene change
        /// between rounds), and dropped when a round starts.</summary>
        private static EntranceTeleport[] _cachedEntrances;

        private static EntranceTeleport[] GetCachedEntrances()
        {
            if (_cachedEntrances != null)
            {
                bool stale = _cachedEntrances.Length == 0;
                for (int i = 0; !stale && i < _cachedEntrances.Length; i++)
                {
                    if (_cachedEntrances[i] == null)
                        stale = true;
                }

                if (!stale)
                    return _cachedEntrances;
            }

            _cachedEntrances = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>();
            return _cachedEntrances;
        }

        private static Vector3? GetMainEntranceReferencePosition()
        {
            var entrances = GetCachedEntrances();
            if (entrances == null || entrances.Length == 0)
                return null;

            EntranceTeleport mainEntrance = entrances
                .Where(entrance => entrance != null && entrance.entranceId == 0 && !entrance.isEntranceToBuilding)
                .OrderBy(entrance => entrance.transform.position.sqrMagnitude)
                .FirstOrDefault();

            if (mainEntrance == null)
            {
                mainEntrance = entrances
                    .Where(entrance => entrance != null && entrance.entranceId == 0)
                    .OrderBy(entrance => entrance.transform.position.sqrMagnitude)
                    .FirstOrDefault();
            }

            if (mainEntrance == null)
                return null;

            return mainEntrance.entrancePoint != null
                ? mainEntrance.entrancePoint.position
                : mainEntrance.transform.position;
        }

        internal static void PrimeCurrentContractForRound()
        {
            // #716/G4: a round is starting, so the previous round's reset guard is spent.
            ReleaseRunResetGuard();
            _cachedEntrances = null;
            MoonContractType currentContract = GetCurrentContract();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Priming round contract: {GetCurrentContractDebugContext()}.");

            switch (currentContract)
            {
                case MoonContractType.BlackoutAudit:
                    _blackoutNextPowerForceTime = 0f;
                    if (IsContractRuntimeActive())
                        UpdateBlackoutAudit();
                    break;
                case MoonContractType.ContainmentBreach:
                    if (IsContractRuntimeActive())
                    {
                        EnsureContainmentInteriorPowerOn("round prime");
                        ContainmentBreachDirector.EnsureLockdownGatesPrepared();
                    }
                    break;
                case MoonContractType.Whistleblower:
                    if (IsContractRuntimeActive())
                        UpdateWhistleblowerContract();
                    break;
            }
        }

        private static void MaintainBlackoutPowerOff()
        {
            if (BlackoutBreakerZones.Count > 0)
            {
                float now = Time.time;
                if (now < _blackoutNextPowerForceTime && now >= _blackoutZoneEnforceUntil)
                    return;

                ApplyBlackoutZonePowerState();
                _blackoutPowerForced = true;
                _blackoutNextPowerForceTime = now + (now < _blackoutZoneEnforceUntil ? 0.1f : 1f);
                return;
            }

            if (Time.time < _blackoutNextPowerForceTime)
                return;

            ForceBlackoutPowerOff(playEffects: !_blackoutPowerForced, syncBreaker: !_blackoutPowerForced);
            _blackoutPowerForced = true;
            _blackoutNextPowerForceTime = Time.time + 1f;
        }

        internal static void ForceCurrentBlackoutPowerOff(string reason, bool validateSupport = true)
        {
            MoonContractType currentContract = GetCurrentContract();
            if (currentContract != MoonContractType.BlackoutAudit)
            {
                ClearInactiveBlackoutState($"blackout force skipped during {reason}; active contract is {currentContract}");
                return;
            }

            if (_blackoutCompleted || _blackoutUnsupportedThisRound)
                return;
            if (validateSupport && !IsBlackoutRuntimeSupported(allowFlowPrediction: false))
                return;

            if (BlackoutBreakerZones.Count > 0)
            {
                ApplyBlackoutZonePowerState();
                MarkBlackoutZoneStateDirty();
                _blackoutPowerForced = true;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout zone power re-applied ({reason}): {GetCurrentContractDebugContext()}.");
                return;
            }

            ForceBlackoutPowerOff(playEffects: false, syncBreaker: !_blackoutPowerForced);
            _blackoutPowerForced = true;
            _blackoutNextPowerForceTime = Time.time + 1f;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout power forced off ({reason}): {GetCurrentContractDebugContext()}.");
        }

        internal static void LogInteriorPowerState(string reason)
        {
            try
            {
                var round = RoundManager.Instance;
                if (round == null)
                    return;

                int poweredLights = 0;
                int enabledLights = 0;
                if (round.allPoweredLights != null)
                {
                    poweredLights = round.allPoweredLights.Count;
                    for (int i = 0; i < round.allPoweredLights.Count; i++)
                    {
                        Light light = round.allPoweredLights[i];
                        if (light != null && light.enabled)
                            enabledLights++;
                    }
                }

                // #292: Light.enabled stays true when vanilla darkens the interior through
                // the fixture animators' "on" bool, so an enabled-light count alone cannot
                // tell dark-via-animator apart from lit. Count the animator state too.
                int poweredAnimators = 0;
                int animatorsOn = 0;
                if (round.allPoweredLightsAnimators != null)
                {
                    poweredAnimators = round.allPoweredLightsAnimators.Count;
                    for (int i = 0; i < round.allPoweredLightsAnimators.Count; i++)
                    {
                        Animator animator = round.allPoweredLightsAnimators[i];
                        if (animator == null)
                            continue;

                        try
                        {
                            if (animator.GetBool("on"))
                                animatorsOn++;
                        }
                        catch
                        {
                        }
                    }
                }

                BreakerBox breaker = UnityEngine.Object.FindObjectOfType<BreakerBox>();
                string breakerState = breaker != null
                    ? $"breakerOn={breaker.isPowerOn}, leversOff={breaker.leversSwitchedOff}"
                    : "breaker=<none>";

                string flowName = "<unknown>";
                try
                {
                    flowName = round.dungeonGenerator?.Generator?.DungeonFlow?.name ?? "<unknown>";
                }
                catch
                {
                }

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Interior power audit ({reason}): {GetCurrentContractDebugContext()}, flow='{flowName}', poweredLights={enabledLights}/{poweredLights}, poweredAnimatorsOn={animatorsOn}/{poweredAnimators}, {breakerState}.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Interior power audit skipped ({reason}): {e.Message}");
            }
        }

        private static void ForceBlackoutPowerOff(bool playEffects, bool syncBreaker)
        {
            if (_blackoutForceInProgress)
                return;

            _blackoutForceInProgress = true;
            try
            {
                if (syncBreaker)
                {
                    IEnumerable<BreakerBox> breakers = BlackoutBreakerZones.Count > 0
                        ? BlackoutBreakerZones.Select(zone => zone.Breaker)
                        : new[] { UnityEngine.Object.FindObjectOfType<BreakerBox>() };
                    foreach (BreakerBox breaker in breakers)
                    {
                        if (breaker == null)
                            continue;

                        _blackoutBreaker = breaker;
                        try
                        {
                            if (breaker.breakerSwitches != null && breaker.breakerSwitches.Length > 3)
                                breaker.SetSwitchesOff();
                            breaker.leversSwitchedOff = Mathf.Max(1, breaker.leversSwitchedOff);
                            breaker.isPowerOn = false;
                            BlackoutBreakerZone zone = FindBlackoutBreakerZone(breaker);
                            if (zone != null)
                                zone.Restored = false;
                        }
                        catch (Exception e)
                        {
                            ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Breaker sync skipped during blackout init: {e.Message}");
                        }
                    }
                }

                var round = RoundManager.Instance;
                if (round == null)
                    return;

                EnsurePoweredLightListsRefreshed(round);

                if (playEffects && round.IsServer)
                    round.SwitchPower(on: false);
                else if (playEffects)
                    round.TurnOnAllLights(on: false);

                SetPoweredLightAnimators(on: false);
                SetPoweredLights(on: false);
                if (BlackoutBreakerZones.Count > 0)
                    ApplyBlackoutZonePowerState();

                if (playEffects)
                    round.FlickerLights(flickerFlashlights: true);
            }
            finally
            {
                _blackoutForceInProgress = false;
            }
        }

        private static void RestoreBlackoutPower(string source = null)
        {
            _blackoutPowerForced = false;
            _blackoutNextPowerForceTime = 0f;
            _blackoutZoneEnforceUntil = 0f;
            _blackoutAmbientPulseZoneIndex = -1;
            RestoreBlackoutPlayerNightVision();

            IEnumerable<BreakerBox> breakers = BlackoutBreakerZones.Count > 0
                ? BlackoutBreakerZones.Select(zone => zone.Breaker)
                : new[] { UnityEngine.Object.FindObjectOfType<BreakerBox>() };
            foreach (BreakerBox breaker in breakers)
            {
                if (breaker == null)
                    continue;
                breaker.leversSwitchedOff = 0;
                breaker.isPowerOn = true;
                if (breaker.breakerBoxHum != null && !breaker.breakerBoxHum.isPlaying)
                    breaker.breakerBoxHum.Play();
            }
            foreach (BlackoutBreakerZone zone in BlackoutBreakerZones)
            {
                zone.Restored = true;
                UpdateBlackoutBreakerDistressEffect(zone, distressed: false);
            }

            var round = RoundManager.Instance;
            if (round == null)
                return;

            if (round.IsServer)
                round.SwitchPower(on: true);
            else
                round.TurnOnAllLights(on: true);

            SetPoweredLightAnimators(on: true);
            SetPoweredLights(on: true);
            round.FlickerLights(flickerFlashlights: true);
            if (!string.IsNullOrWhiteSpace(source))
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout Audit restored full facility power via {source}.");
        }

        /// <summary>Round-lifetime BreakerBox cache for the Containment power keep-alive
        /// (#716/B5). Unity's overloaded null check covers destruction, so a destroyed
        /// breaker falls back to one fresh scan.</summary>
        private static BreakerBox ResolveContainmentBreakerBox()
        {
            if (_containmentBreakerBox == null)
                _containmentBreakerBox = UnityEngine.Object.FindObjectOfType<BreakerBox>();
            return _containmentBreakerBox;
        }

        private static void EnsureContainmentInteriorPowerOn(string reason)
        {
            if (GetCurrentContract() != MoonContractType.ContainmentBreach)
                return;

            // While the CCTV security alarm (or a protocol drill) blackout holds the
            // interior lights off, this 1 Hz restore loop used to fight it: the loop
            // snapped every powered light back on and the blackout slammed it off
            // again, reading as facility-wide flicker with the vanilla zap SFX on the
            // alarm beat. The blackout restores power itself when it ends, so stand
            // down while it is holding the lights. Both sides run locally on every
            // machine off the replicated alarm state, so no extra sync is needed.
            if (LethalCctvCompat.SecurityLightPulseActive)
                return;

            float now = Time.time;
            if (now < _containmentNextPowerRestoreTime)
                return;

            _containmentNextPowerRestoreTime = now + 1f;

            var round = RoundManager.Instance;
            if (round == null)
                return;

            EnsurePoweredLightListsRefreshed(round);

            int poweredLights = round.allPoweredLights != null ? round.allPoweredLights.Count : 0;
            int enabledLights = 0;
            if (round.allPoweredLights != null)
            {
                for (int i = 0; i < round.allPoweredLights.Count; i++)
                {
                    Light light = round.allPoweredLights[i];
                    if (light != null && light.enabled)
                        enabledLights++;
                }
            }

            // #716/B5: FindObjectOfType<BreakerBox> ran every second for the whole inside
            // phase. There is one breaker per interior and it lives as long as the round,
            // so it is resolved once and re-resolved only if it is destroyed.
            BreakerBox breaker = ResolveContainmentBreakerBox();
            bool breakerOff = breaker != null && (!breaker.isPowerOn || breaker.leversSwitchedOff > 0);
            bool lightsOff = poweredLights > 0 && enabledLights < poweredLights;

            // The animator sweep only decides whether to act; if the breaker or the light
            // list already says power is off, the answer is settled and the sweep is skipped.
            bool hasAnimatorOff = false;
            if (!breakerOff && !lightsOff && round.allPoweredLightsAnimators != null)
            {
                for (int i = 0; i < round.allPoweredLightsAnimators.Count; i++)
                {
                    Animator animator = round.allPoweredLightsAnimators[i];
                    if (animator != null && !animator.GetBool(BlackoutFixturePoweredParameter))
                    {
                        hasAnimatorOff = true;
                        break;
                    }
                }
            }

            if (!breakerOff && !lightsOff && !hasAnimatorOff)
                return;

            if (breaker != null)
            {
                breaker.leversSwitchedOff = 0;
                breaker.isPowerOn = true;
                if (breaker.breakerBoxHum != null && !breaker.breakerBoxHum.isPlaying)
                    breaker.breakerBoxHum.Play();
            }

            if (round.IsServer)
                round.SwitchPower(on: true);
            else
                round.TurnOnAllLights(on: true);

            SetPoweredLightAnimators(on: true);
            SetPoweredLights(on: true);

            string message =
                $"[MoonContracts] Containment power restored ({reason}): lights={enabledLights}/{poweredLights}, breakerOff={breakerOff}, animatorOff={hasAnimatorOff}.";
            if (_containmentPowerRestored)
                ContractsBootstrap.Log?.LogDebug(message);
            else
                ContractsBootstrap.Log?.LogInfo(message);
            _containmentPowerRestored = true;
        }

        private static void SetPoweredLightAnimators(bool on)
        {
            var round = RoundManager.Instance;
            if (round?.allPoweredLightsAnimators == null)
                return;

            foreach (var animator in round.allPoweredLightsAnimators)
            {
                if (animator != null)
                    animator.SetBool("on", on);
            }
        }

        /// <summary>#716/B16: refreshes vanilla's powered-light lists when they are empty,
        /// at most once per round on interiors that simply have no powered-light animators.
        /// Those interiors used to re-enter <c>RefreshLightsList</c> on every Blackout pass,
        /// and its Harmony postfix then ran <c>ForceCurrentBlackoutPowerOff</c> plus
        /// <c>LogInteriorPowerState</c> once per frame for the whole round.</summary>
        private static void EnsurePoweredLightListsRefreshed(RoundManager round)
        {
            if (round == null)
                return;

            bool animatorsEmpty = round.allPoweredLightsAnimators == null || round.allPoweredLightsAnimators.Count == 0;
            bool lightsEmpty = round.allPoweredLights == null || round.allPoweredLights.Count == 0;
            if (!animatorsEmpty && !lightsEmpty)
                return;
            if (_poweredLightAnimatorsAbsentThisRound)
                return;

            round.RefreshLightsList();

            // Latch only once the refresh has actually produced lights. Both lists empty
            // means the dungeon is not built yet, and a later refresh can still fill them.
            animatorsEmpty = round.allPoweredLightsAnimators == null || round.allPoweredLightsAnimators.Count == 0;
            int lightCount = round.allPoweredLights != null ? round.allPoweredLights.Count : 0;
            if (!animatorsEmpty || lightCount == 0)
                return;

            _poweredLightAnimatorsAbsentThisRound = true;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Interior exposes {lightCount} powered light(s) with no powered-light animators; suppressing further RefreshLightsList calls for this round.");
        }

        private static void SetPoweredLights(bool on)
        {
            var round = RoundManager.Instance;
            if (round?.allPoweredLights == null)
                return;

            foreach (var light in round.allPoweredLights)
            {
                if (light != null)
                    light.enabled = on;
            }
        }

        private static bool IsBlackoutRuntimeSupported(bool allowFlowPrediction)
        {
            var round = RoundManager.Instance;
            if (round == null)
                return false;

            EnsurePoweredLightListsRefreshed(round);

            // #290: this runs every UpdateBlackoutAudit pass. Only "is there at least
            // one live entry" matters, so stop at the first hit instead of counting the
            // whole list twice (each element costs a native Unity liveness check).
            if (HasLive(round.allPoweredLightsAnimators) || HasLive(round.allPoweredLights))
                return true;

            if (allowFlowPrediction && round.currentDungeonType >= 0 && IsDungeonFlowBlackoutCapable(round.currentDungeonType, out _))
                return true;

            return false;
        }

        private static bool HasLive<T>(IEnumerable<T> objects) where T : UnityEngine.Object
        {
            if (objects == null)
                return false;

            foreach (var obj in objects)
            {
                if (obj != null)
                    return true;
            }

            return false;
        }

        private static void DisableBlackoutForCurrentRound(string reason)
        {
            if (_blackoutUnsupportedThisRound)
                return;

            _blackoutUnsupportedThisRound = true;
            _blackoutCompleted = false;
            _blackoutPowerForced = false;
            _blackoutNightVisionSweepCursor = 0;
            ResetBlackoutLightAuditPass();
            DestroyBlackoutLever();
            RestoreBlackoutPower();
            ClearBlackoutZoneState(destroySpawnedBreakers: true);
            ResetBlackoutFixtureBuild();
            ClearBlackoutSnapshotRebuild();
            _blackoutBreaker = null;

            var level = StartOfRound.Instance?.currentLevel;
            int assignmentKey = GetAssignmentKey(level);
            if (assignmentKey >= 0)
            {
                Assignments[assignmentKey] = MoonContractType.None;
                LockedAssignments.Remove(assignmentKey);
            }
            if (level != null && level.levelID >= 0 && level.levelID != assignmentKey)
            {
                Assignments[level.levelID] = MoonContractType.None;
                LockedAssignments.Remove(level.levelID);
            }

            MoonContractRoundEndState.ClearRoundStartContractSnapshot("blackout unsupported");
            ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Blackout Audit disabled for this round: {reason}.");
        }

        /// <summary>
        /// Replaces an unsupported active directive with Salvage before round-end evaluation.
        /// Callers must prove the expected contract still owns the current moon so an
        /// asynchronous placement failure cannot overwrite a newer route.
        /// </summary>
        internal static bool DisableCurrentContractForRound(MoonContractType expectedContract, string reason)
        {
            SelectableLevel level = StartOfRound.Instance?.currentLevel;
            if (level == null || GetContractForLevel(level) != expectedContract)
                return false;

            int assignmentKey = GetAssignmentKey(level);
            if (assignmentKey >= 0)
            {
                Assignments[assignmentKey] = MoonContractType.None;
                LockedAssignments.Remove(assignmentKey);
            }
            if (level.levelID >= 0 && level.levelID != assignmentKey)
            {
                Assignments[level.levelID] = MoonContractType.None;
                LockedAssignments.Remove(level.levelID);
            }

            _nextAssignmentSnapshotAt = 0f;
            MoonContractRoundEndState.ClearRoundStartContractSnapshot($"{expectedContract} unsupported");
            MoonContractShipMonitor.RefreshCurrent();

            // The contract card reveals on landing, which is before an asynchronous placement
            // resolver can report failure. Without this the HUD keeps showing the cancelled
            // directive and its objectives — "RIG THE TRAP" for a trap that never spawned. The
            // reveal path re-reads the current contract, and RefreshAssignments cannot re-roll
            // mid-round (it returns early once assignments exist and the ship has landed), so
            // this cannot resurrect the contract we just disabled.
            ContractHUDManager hud = ContractHUDManager.Instance;
            if (hud != null)
            {
                if (GetCurrentContract() == MoonContractType.None && !CurrentLevelSupportsSalvageContract())
                    hud.Hide();
                else
                    hud.ShowContract();
            }
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts] {MoonContractCatalog.Get(expectedContract).DisplayName} disabled for this round: {reason}. Falling back to Salvage.");
            return true;
        }

        private static void DestroyBlackoutLever()
        {
            if (_blackoutLever == null)
                return;

            UnityEngine.Object.Destroy(_blackoutLever.gameObject);
            _blackoutLever = null;
        }

        private static void ClearBlackoutZoneState(bool destroySpawnedBreakers)
        {
            foreach (BlackoutBreakerZone zone in BlackoutBreakerZones)
            {
                if (zone?.DistressEffect == null)
                    continue;

                try
                {
                    UnityEngine.Object.Destroy(zone.DistressEffect.gameObject);
                }
                catch (Exception e)
                {
                    ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Failed destroying breaker distress effect: {e.Message}");
                }
                zone.DistressEffect = null;
            }

            if (destroySpawnedBreakers)
            {
                foreach (BlackoutBreakerZone zone in BlackoutBreakerZones)
                {
                    if (zone == null || !zone.SpawnedByBlackout || zone.Breaker == null)
                        continue;

                    try
                    {
                        FixtureReservationRegistry.Release($"blackout-breaker-{zone.Index + 1}");
                        UnityEngine.Object.Destroy(zone.Breaker.gameObject);
                    }
                    catch (Exception e)
                    {
                        ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Failed destroying spawned blackout breaker: {e.Message}");
                    }
                }
            }

            BlackoutBreakerZones.Clear();
            BlackoutPoweredFixtures.Clear();
            _blackoutZoneEnforceUntil = 0f;
            _blackoutNextAmbientPulseTime = 0f;
            _blackoutAmbientPulseEndTime = 0f;
            _blackoutAmbientPulseZoneIndex = -1;
            ClearBlackoutRestoreFlicker();
        }

        private static void ClearWhistleblowerEnemyReference()
        {
            _whistleblowerEnemy = null;
        }

        private static List<MoonContractObjective> GetContainmentBreachObjectives()
        {
            int required = GetContainmentRequiredSeconds();
            int interval = GetContainmentWaveDurationSecondsForCurrentMoon();
            int totalWaves = GetContainmentTotalWaves();
            var objectives = new List<MoonContractObjective>();
            for (int wave = 1; wave <= totalWaves; wave++)
            {
                int target = Mathf.Min(required, wave * interval);
                bool complete = _containmentCompleted || _containmentInsideSeconds >= target;
                int remaining = Mathf.Max(0, Mathf.CeilToInt(target - _containmentInsideSeconds));
                string text = $"Survive wave {wave}";
                if (!complete) text += $" \u00B7 {FormatCountdown(remaining)}";
                objectives.Add(new MoonContractObjective(text, complete ? 1 : 0, 1,
                    key: $"containment.wave.{wave}"));
            }
            return objectives;
        }

        private static List<MoonContractObjective> GetShadowRaidObjectives()
        {
            bool delivered = ShadowRaidContract.DrillDelivered;
            bool attached = ShadowRaidContract.DrillAttached;
            bool breached = ShadowRaidContract.DrillCompleted;
            bool hardDriveFound = ShadowRaidContract.HardDriveFound;
            int remaining = Mathf.CeilToInt(ShadowRaidContract.DrillRemainingSeconds);

            // #725: the drill run is the whole middle of this contract and used to be a single
            // countdown between "get the drill" and "get the drive". Mounting and holding the
            // container are real steps the crew performs, so they are milestones.
            bool mounted = attached || breached || hardDriveFound;
            bool held = breached || hardDriveFound;
            // #1076: the Blacksite squad's hold on the drive, from the replicated manifest
            // snapshots, and the one-way departure latch. Both read the same on host and clients.
            var possession = ShadowRaidContract.RefreshBlacksitePossession();
            bool driveExtracted = ShadowRaidContract.DriveExtracted;
            bool squadHolds = !driveExtracted && possession.IsHeld;
            bool squadAboard = squadHolds && possession.IsAboard;
            var objectives = new List<MoonContractObjective>();

            // #1076: this contract had no failure path at all - the card could not resolve once the
            // drive left the moon. The lost row leads the list the way PayloadContract's does, is
            // Failed so IsCurrentContractFailed's scan finds it, and is Lost so it renders dim.
            // #1233 D6: its copy is the two words the belt's terminal status uses; the card strikes
            // the title and writes no voided line for this row (ContractHudObjectivePresentation).
            if (driveExtracted)
                objectives.Add(new MoonContractObjective("DRIVE LOST", 0, 1, true, MoonContractObjectiveKind.Status, key: "shadow.blacksite", lost: true));

            // #1076, amended by the 2026-09-16 playtest: possession leads the drill rows so the card
            // selects this row while the squad has it. It is emitted only while it does, so
            // recovering the drive reverts the card by itself. The status row that used to compose
            // beside it is gone: the belt already says DRIVE TAKEN / HAULING THE DRIVE / DRIVE
            // ABOARD, so the action row's lethal flag is the only card-side danger tint left.
            if (squadHolds)
            {
                objectives.Add(new MoonContractObjective(
                    "Recover the hard drive from the Blacksite team", 0, 1, false, key: "shadow.blacksite.recover", lethal: squadAboard));
            }

            objectives.Add(new MoonContractObjective("Collect the drill from the drop ship", delivered || mounted ? 1 : 0, 1, driveExtracted, key: "shadow.drill.collect"));
            objectives.Add(new MoonContractObjective("Drop the drill in front of the container", mounted ? 1 : 0, 1, driveExtracted, key: "shadow.drill.mount"));
            objectives.Add(new MoonContractObjective("Wait for the drill to finish", held ? 1 : 0, 1, driveExtracted, key: "shadow.drill.hold"));
            objectives.Add(new MoonContractObjective("Recover the hard drive", hardDriveFound ? 1 : 0, 1, driveExtracted, key: "shadow.drive"));

            // #1076: the timer sits directly above the row it counts down for. It used to be a
            // literal index 2, which was that row's position in a fixed four-row list; the list can
            // now start with the lost row or the possession row, so the slot is looked up.
            if (attached && !held && remaining > 0)
                objectives.Insert(IndexOfObjectiveKey(objectives, "shadow.drill.hold"), new MoonContractObjective(
                    $"DRILL \u00B7 {FormatCountdown(remaining)}",
                    0,
                    1,
                    kind: MoonContractObjectiveKind.Status,
                    // Explicit key: per-second countdown stays out of the signature (#520).
                    key: "shadow.drill.timer"));

            return objectives;
        }

        /// <summary>
        /// #1076: the position of the row with <paramref name="key"/>, or the end of the list when
        /// it is not there. Used to place a status row beside the row it belongs to rather than at
        /// a literal index that the rows above it can move.
        /// </summary>
        private static int IndexOfObjectiveKey(List<MoonContractObjective> objectives, string key)
        {
            for (int i = 0; i < objectives.Count; i++)
                if (string.Equals(objectives[i].SignatureKey, key, StringComparison.Ordinal))
                    return i;
            return objectives.Count;
        }

        private static List<MoonContractObjective> GetSurveyObjectives()
        {
            EnsureSurveyTargets();
            EnsureSurveyDelivery();

            // #774: beacons are interchangeable. Any beacon plants any open site, so the HUD
            // counts sites rather than naming beacons. The departure line is the shared
            // closing objective appended by GetObjectives, not a Survey row.
            int siteCount = _surveyTargets != null && _surveyTargets.Count > 0
                ? _surveyTargets.Count
                : GetSurveyDroneCountForCurrentMoon();
            int placedCount = _surveyTargets != null ? _surveyTargets.Count(target => target.Placed) : 0;
            bool grabbed = _surveyBeaconGrabbed || placedCount > 0;

            return new List<MoonContractObjective>
            {
                // "Collect": the beacons are supplied equipment, so the verb follows the
                // contract-copy table rather than "Grab".
                new MoonContractObjective("Collect the survey drones from the drop ship", grabbed ? 1 : 0, 1, key: "survey.collect"),
                new MoonContractObjective("Plant the beacons", placedCount, siteCount, key: "survey.plant"),
            };
        }

        private static void UpdateSurvey()
        {
            EnsureSurveyTargets();
            EnsureSurveyDelivery();
        }

        private static void UpdateWhistleblowerContract()
        {
            EnsureWhistleblowerTarget();
        }

        private static void AddWhistleblowerCctvMarkers(List<MoonContractObjectiveMarker> markers)
        {
            EnsureWhistleblowerTarget();
            if (_whistleblowerEnemy == null || _whistleblowerNeutralized)
                return;

            // #966 S14 U4: colorKind 0 while a Blacksite escort has him, 1 otherwise. Zero is the
            // hostile tier the attached payload cart already uses (AddPayloadCctvMarkers passes
            // `cart.IsAttached ? 0 : 1`), so a witness being marched out reads on the monitor the
            // same way a cart under a soldier's hands does. There is no MarkerPriority symbol: the
            // plan's "marker priority 0" is this argument (orchestrator decision 3).
            AddCctvMarker(
                markers,
                "WHISTLEBLOWER",
                _whistleblowerEnemy.MarkerPosition,
                1.8f,
                _whistleblowerEnemy.IsInCustody ? 0 : 1, target: _whistleblowerEnemy);
        }

        private static void AddBlackoutCctvMarkers(List<MoonContractObjectiveMarker> markers)
        {
            EnsureBlackoutAudit();
            if (BlackoutBreakerZones.Count > 0 && !_blackoutCompleted)
            {
                foreach (BlackoutBreakerZone zone in BlackoutBreakerZones)
                {
                    if (zone?.Breaker == null || zone.Restored)
                        continue;
                    AddCctvMarker(markers, $"BREAKER {zone.Index + 1}", zone.Breaker.transform.position + Vector3.up * 1.0f, 1.45f, 0, target: zone.Breaker);
                }

                return;
            }

            if (_blackoutBreaker != null && !_blackoutCompleted)
            {
                AddCctvMarker(markers, "BREAKER BOX", _blackoutBreaker.transform.position + Vector3.up * 1.0f, 1.45f, 0, target: _blackoutBreaker);
                return;
            }

            if (_blackoutLever == null || _blackoutLever.Pulled)
                return;

            AddCctvMarker(markers, "AUDIT LEVER", _blackoutLever.transform.position + Vector3.up * 1.0f, 1.35f, 0, target: _blackoutLever);
        }

        private static void AddShadowRaidCctvMarkers(List<MoonContractObjectiveMarker> markers)
        {
            ShadowRaidContract.AddObjectiveMarkers(markers);
        }

        private static void AddSurveyCctvMarkers(List<MoonContractObjectiveMarker> markers)
        {
            EnsureSurveyTargets();
            if (_surveyTargets == null)
                return;

            for (int i = 0; i < _surveyTargets.Count; i++)
            {
                SurveyTarget target = _surveyTargets[i];
                if (target.Placed)
                    continue;

                AddCctvMarker(markers, $"SURVEY {i + 1}", target.Position + Vector3.up * 0.05f, target.Radius, 0, isArea: true, key: $"survey:{i}");
            }
        }

        private static void AddDefuseCctvMarkers(List<MoonContractObjectiveMarker> markers)
        {
            DefuseBombController bomb = DefuseContract.ActiveBomb;
            if (bomb == null || bomb.IsDefused || bomb.IsFailed)
                return;

            AddCctvMarker(markers, "DEFUSE", bomb.transform.position + Vector3.up * 0.45f, 1.4f, bomb.WirePhaseComplete ? 0 : 1, target: bomb);
        }

        private static void AddPayloadCctvMarkers(List<MoonContractObjectiveMarker> markers)
        {
            PayloadCartController cart = PayloadContract.ActiveCart;
            if (cart == null || cart.IsDelivered)
                return;

            AddCctvMarker(markers, "PAYLOAD", cart.MarkerPosition, 1.6f, cart.IsAttached ? 0 : 1, target: cart);
        }

        private static void AddWasteDisposalCctvMarkers(List<MoonContractObjectiveMarker> markers)
        {
            WasteDisposalIncineratorController incinerator = WasteDisposalContract.ActiveIncinerator;
            if (incinerator == null || incinerator.IsComplete)
                return;

            int colorKind = incinerator.StagedValue >= incinerator.RequiredValue ? 0 : 1;
            AddCctvMarker(markers, "INCINERATOR", incinerator.MarkerPosition, 1.8f, colorKind, target: incinerator);
        }

        private static void AddPestControlCctvMarkers(List<MoonContractObjectiveMarker> markers)
        {
            PestControlTrapController trap = PestControlContract.ActiveTrap;
            if (trap == null || trap.CapturedTotal > 0)
                return;

            AddCctvMarker(markers, "PEST CONTROL RIG", trap.MarkerPosition, 1.8f, trap.SpecimenLured ? 0 : 1, target: trap);
        }

        /// <summary>#804. The Lategame Upgrades-backed directives all blip one object. The
        /// runtime owns the label and the position because it also owns the cached component;
        /// this method only decides how it renders.</summary>
        private static void AddLguCctvMarkers(List<MoonContractObjectiveMarker> markers, MoonContractType contract)
        {
            if (!LguContractRuntime.TryGetCctvMarker(contract, out string label, out Vector3 position, out bool isComplete, out Component target))
                return;

            bool area = contract == MoonContractType.LguExorcism;
            if (area && target != null) position = target.transform.position + Vector3.up * 0.05f;
            AddCctvMarker(markers, label, position, 1.6f, isComplete ? 0 : 1, isComplete, target, isArea: area);
        }

        private static void AddCctvMarker(
            List<MoonContractObjectiveMarker> markers,
            string label,
            Vector3 position,
            float radius,
            int colorKind,
            bool isComplete = false, Component target = null, bool isArea = false, string key = null)
        {
            if (markers == null)
                return;
            if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z))
                return;

            markers.Add(new MoonContractObjectiveMarker
            {
                Key = key ?? (target != null ? "object:" + target.GetInstanceID() : label),
                Target = target,
                IsArea = isArea,
                Label = string.IsNullOrWhiteSpace(label) ? "OBJECTIVE" : label,
                Position = position,
                Radius = Mathf.Clamp(radius, 0.35f, isArea ? 30f : 4f),
                ColorKind = colorKind,
                IsComplete = isComplete,
            });
        }

        private static void UpdateContainmentBreach()
        {
            if (_containmentCompleted)
                return;

            float now = Time.time;
            if (now < _containmentNextUpdateTime)
                return;

            _containmentNextUpdateTime = now + 0.25f;
            ContainmentWaveHunter.Tick();
            if (_containmentLastUpdateTime <= 0f)
                _containmentLastUpdateTime = now;

            float elapsed = Mathf.Max(0f, now - _containmentLastUpdateTime);
            _containmentLastUpdateTime = now;

            ContainmentBreachDirector.EnsureLockdownGatesPrepared();
            EnsureContainmentInteriorPowerOn("containment update");

            bool playerInside = AnyPlayerInsideFactory();
            if (!_containmentStarted && !playerInside)
                return;

            if (!_containmentStarted)
            {
                _containmentStarted = true;
                _containmentEntryTime = now;
                _containmentNextWaveTime = 0f;
                _containmentRandom = new System.Random((StartOfRound.Instance?.randomMapSeed ?? 0) ^ 918273);

                var round = RoundManager.Instance;
                if (round != null)
                {
                    round.currentMaxInsidePower = Mathf.Max(
                        round.currentMaxInsidePower,
                        round.currentLevel.maxEnemyPowerCount +
                        ContractsBootstrap.ContainmentBalance.ForRisk(GetCurrentRiskTier()).EnemiesPerWave.Value * GetContainmentTotalWaves());
                    round.cannotSpawnMoreInsideEnemies = false;
                    CacheContainmentSpawnData(round);
                }

                float entryStart = ContainmentBreachDirector.PlayVoice(ContainmentVoiceLine.Entry);
                float countdownStart = ContainmentBreachDirector.PlayVoice(ContainmentVoiceLine.Lockdown);
                _containmentLockdownWarningTime = countdownStart;
                _containmentLockdownStartTime = countdownStart + 10f;
                ContainmentBreachDirector.PrepareLockdownGates();
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Containment Breach entry sequence started; countdown scheduled for {Mathf.Max(0f, countdownStart - now):0.00}s from now and lockdown for {Mathf.Max(0f, _containmentLockdownStartTime - now):0.00}s from now.");
            }

            if (!_containmentLockdownLinePlayed && now >= _containmentLockdownWarningTime)
            {
                _containmentLockdownLinePlayed = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Containment Breach lockdown countdown started.");
            }

            if (!_containmentLockedDown && now >= _containmentLockdownStartTime)
            {
                _containmentLockedDown = true;
                _containmentNextWaveTime = Time.time;
                ContainmentBreachDirector.BeginLockdown();
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Containment Breach lockdown engaged.");
            }

            if (!_containmentLockedDown)
                return;

            if (playerInside)
                _containmentInsideSeconds += elapsed;

            if (_containmentInsideSeconds >= GetContainmentRequiredSeconds())
            {
                CompleteContainmentBreach();
                return;
            }

            if (_containmentWave < GetContainmentTotalWaves() && Time.time >= _containmentNextWaveTime)
            {
                // #406: a wave is survived the moment the next one starts. Credit the wave that
                // just closed before spawning its successor.
                CreditContainmentWavesUpTo(_containmentWave);
                _containmentWave++;
                SpawnContainmentWave(_containmentWave);
                _containmentNextWaveTime = Time.time + GetContainmentWaveDurationSecondsForCurrentMoon();
            }

            SpawnPendingContainmentWaveEnemy();
        }

        /// <summary>
        /// Publishes one ContainmentBreachWaveSurvived per wave that has closed and not yet been
        /// credited. Server-authoritative through <c>BroadcastServer</c>; a client's copy of this
        /// tick publishes nothing and receives the host's mirror instead.
        /// </summary>
        private static void CreditContainmentWavesUpTo(int lastClosedWave)
        {
            int total = GetContainmentTotalWaves();
            for (int wave = _containmentCreditedWave + 1; wave <= Mathf.Min(lastClosedWave, total); wave++)
                ContractProgressionEvents.PublishContainmentBreachWaveSurvived(wave, total);

            if (lastClosedWave > _containmentCreditedWave)
                _containmentCreditedWave = Mathf.Min(lastClosedWave, total);
        }

        private static void CompleteContainmentBreach()
        {
            _containmentCompleted = true;
            _containmentPendingWaveSpawns = 0;
            // The final wave closes with the contract itself, so it is credited here rather than
            // by a successor that never spawns.
            CreditContainmentWavesUpTo(_containmentWave);
            if (_containmentEndSequencePlayed)
                return;

            _containmentEndSequencePlayed = true;
            ContainmentWaveHunter.ClearTrackedEnemies();
            ContainmentBreachDirector.PlayVoice(ContainmentVoiceLine.End);
            ContainmentBreachDirector.IgniteInteriorEnemies();
            ContainmentBreachDirector.EndLockdown();
            ContractsBootstrap.Log?.LogInfo("[MoonContracts] Containment Breach complete; lockdown released.");
        }

        private static int GetContainmentTotalWaves()
        {
            return GetContainmentWaveCountForCurrentMoon();
        }

        private static int GetContainmentRequiredSeconds()
        {
            return Mathf.Max(1, GetContainmentTotalWaves() * GetContainmentWaveDurationSecondsForCurrentMoon());
        }

        private static void SpawnContainmentWave(int waveNumber)
        {
            // Wave-start alarm pulse: the normal CCTV alarm presentation (facility
            // blackout, alarm fixtures + camera lenses strobing on the shared
            // clock) time-boxed to 7 s, ending in the pulse's smooth restore. Runs
            // here on every machine — the same deterministic local path that plays
            // the wave voice lines — so no extra sync is needed. Detection stays
            // suppressed for the whole contract; this is presentation only.
            LethalCctvCompat.BeginContainmentWavePulse(ContainmentWaveAlarmPulseSeconds);

            switch (Mathf.Clamp(waveNumber, 1, 3))
            {
                case 1:
                    ContainmentBreachDirector.PlayVoice(ContainmentVoiceLine.Wave1);
                    ContainmentBreachDirector.PlayLockdownSlam();
                    break;
                case 2:
                    ContainmentBreachDirector.PlayVoice(ContainmentVoiceLine.Wave2);
                    break;
                default:
                    ContainmentBreachDirector.PlayVoice(ContainmentVoiceLine.Wave3);
                    break;
            }

            var round = RoundManager.Instance;
            if (round == null || !round.IsServer || round.currentLevel?.Enemies == null || round.currentLevel.Enemies.Count == 0)
                return;

            CacheContainmentSpawnData(round);
            if (_containmentEnemyPool == null || _containmentEnemyPool.Count == 0)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts] Containment Breach wave {waveNumber} skipped because the moon has no valid native indoor enemy entries.");
                return;
            }
            if (_containmentVents == null || _containmentVents.Count == 0)
                return;

            // #705: the wave only queues its spawns here. SpawnPendingContainmentWaveEnemy
            // drains one enemy per containment tick, so a wave start pays the alarm/voice
            // presentation and nothing else in its frame.
            _containmentPendingWaveSpawns = Mathf.Max(1, ContractsBootstrap.ContainmentBalance.ForRisk(GetCurrentRiskTier()).EnemiesPerWave.Value);
            _containmentPendingWaveNumber = waveNumber;
            _containmentPendingWaveSpawned = 0;
        }

        private static readonly List<EnemyAI> ContainmentWaveSpawnBuffer = new List<EnemyAI>(4);

        private static void SpawnPendingContainmentWaveEnemy()
        {
            if (_containmentPendingWaveSpawns <= 0)
                return;

            var round = RoundManager.Instance;
            if (round == null || !round.IsServer)
            {
                _containmentPendingWaveSpawns = 0;
                return;
            }

            _containmentPendingWaveSpawns--;
            var vent = PickContainmentVent(round);
            int enemyIndex = PickContainmentEnemyIndex();
            if (vent != null && vent.floorNode != null && enemyIndex >= 0)
            {
                ContainmentWaveSpawnBuffer.Clear();
                int beforeCount = round.SpawnedEnemies.Count;
                round.SpawnEnemyGameObject(vent.floorNode.position, vent.floorNode.eulerAngles.y, enemyIndex);
                CaptureContainmentEnemies(round, beforeCount, ContainmentWaveSpawnBuffer);
                ContainmentWaveHunter.RegisterSpawnedEnemies(ContainmentWaveSpawnBuffer);
                _containmentPendingWaveSpawned += ContainmentWaveSpawnBuffer.Count;
            }

            if (_containmentPendingWaveSpawns <= 0)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Containment Breach wave {_containmentPendingWaveNumber} spawned {_containmentPendingWaveSpawned} enemy/enemies from the native weighted indoor pool.");
        }

        private static void CacheContainmentSpawnData(RoundManager round)
        {
            if (round == null)
                return;

            if (_containmentEnemyPool == null)
                _containmentEnemyPool = ResolveContainmentEnemyPool(round);

            if (_containmentVents == null)
            {
                _containmentVents = round.allEnemyVents == null
                    ? new List<EnemyVent>()
                    : round.allEnemyVents
                        .Where(vent => vent != null && vent.floorNode != null)
                        .ToList();
            }
        }

        private static List<ContainmentEnemyCandidate> ResolveContainmentEnemyPool(RoundManager round)
        {
            var pool = new List<ContainmentEnemyCandidate>();
            for (int i = 0; i < round.currentLevel.Enemies.Count; i++)
            {
                var entry = round.currentLevel.Enemies[i];
                EnemyType enemy = entry?.enemyType;
                if (entry == null
                    || entry.rarity <= 0
                    || enemy == null
                    || enemy.spawningDisabled
                    || enemy.enemyPrefab == null)
                {
                    continue;
                }

                pool.Add(new ContainmentEnemyCandidate(i, entry.rarity));
            }

            return pool;
        }

        private static int PickContainmentEnemyIndex()
        {
            if (_containmentEnemyPool == null || _containmentEnemyPool.Count == 0 || _containmentRandom == null)
                return -1;

            int totalRarity = 0;
            for (int i = 0; i < _containmentEnemyPool.Count; i++)
                totalRarity += _containmentEnemyPool[i].Rarity;
            if (totalRarity <= 0)
                return -1;

            int roll = _containmentRandom.Next(totalRarity);
            for (int i = 0; i < _containmentEnemyPool.Count; i++)
            {
                ContainmentEnemyCandidate candidate = _containmentEnemyPool[i];
                if (roll < candidate.Rarity)
                    return candidate.EnemyIndex;

                roll -= candidate.Rarity;
            }

            return _containmentEnemyPool[_containmentEnemyPool.Count - 1].EnemyIndex;
        }

        private static EnemyVent PickContainmentVent(RoundManager round)
        {
            if (_containmentVents == null || _containmentVents.Count == 0)
                return null;

            return _containmentVents[_containmentRandom.Next(_containmentVents.Count)];
        }

        private static void CaptureContainmentEnemies(RoundManager round, int beforeCount, List<EnemyAI> captured)
        {
            if (round.SpawnedEnemies == null || round.SpawnedEnemies.Count <= beforeCount)
                return;

            for (int i = beforeCount; i < round.SpawnedEnemies.Count; i++)
            {
                var enemy = round.SpawnedEnemies[i];
                if (enemy == null)
                    continue;

                captured.Add(enemy);
            }
        }

        private struct ContainmentEnemyCandidate
        {
            internal readonly int EnemyIndex;
            internal readonly int Rarity;

            internal ContainmentEnemyCandidate(int enemyIndex, int rarity)
            {
                EnemyIndex = enemyIndex;
                Rarity = rarity;
            }
        }

        internal static void EnsureWhistleblowerTarget()
        {
            if (GetCurrentContract() != MoonContractType.Whistleblower)
                return;
            if (_whistleblowerNeutralized)
                return;
            // #716/B3: nothing below is meaningful before the ship is down, and the
            // placement search underneath hits GameObject.FindGameObjectsWithTag("AINode")
            // — which used to run every frame of the descent from the CCTV marker caller.
            var start = StartOfRound.Instance;
            if (start == null || !start.shipHasLanded || start.inShipPhase)
                return;

            var round = RoundManager.Instance;
            bool canPlace = round != null
                && round.IsServer
                && WhistleblowerEnemyRegistry.Registered
                && WhistleblowerEnemyRegistry.EnemyType != null;

            // #716/A4 follow-up: the sliced placement scan has to advance on its own clock.
            // Everything below this point is behind the 0.5 s enemy-scan window, so leaving
            // the pump down there would hand it one 1.5 ms slice per half second — minutes
            // to resolve a few hundred AI nodes, against one landed frame before slicing.
            // The pump only measures and latches; it never spawns, so it cannot race the
            // spawn decision below, which still requires a fresh "no whistleblower exists"
            // verdict from the throttled scan. Once latched, the call below is a lookup.
            if (canPlace)
                TryGetWhistleblowerSpawnPlacement(out _, out _);

            if (TryCacheActiveWhistleblowerEnemy(out bool scanned))
                return;
            // The scan is throttled; without fresh evidence that no whistleblower exists,
            // spawning would risk a duplicate on the frames between scan windows.
            if (!scanned)
                return;

            if (!canPlace)
                return;
            if (!TryGetWhistleblowerSpawnPlacement(out Vector3 position, out Quaternion rotation))
                return;

            var spawned = round.SpawnEnemyGameObject(position, rotation.eulerAngles.y, -1, WhistleblowerEnemyRegistry.EnemyType);
            if (spawned.TryGet(out NetworkObject networkObject))
                _whistleblowerEnemy = networkObject.GetComponent<WhistleblowerEnemyAI>()
                    ?? networkObject.GetComponentInChildren<WhistleblowerEnemyAI>();

            if (_whistleblowerEnemy != null)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Whistleblower enemy spawned at {FormatVector(position)}.");
            else
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Whistleblower spawn returned no WhistleblowerEnemyAI component.");

            TrySpawnWhistleblowerEntranceShovelsServer();
        }

        // #765: the contract hands the crew its tools. Two vanilla shovels land on the
        // navmesh just inside the interior main entrance, once per round, host-spawned and
        // replicated like any other prop. A missing shovel item or entrance only logs.
        private const int WhistleblowerEntranceShovelCount = 2;
        private static bool _whistleblowerShovelsSpawned;

        private static void TrySpawnWhistleblowerEntranceShovelsServer()
        {
            if (_whistleblowerShovelsSpawned)
                return;

            var round = RoundManager.Instance;
            var start = StartOfRound.Instance;
            if (round == null || !round.IsServer || start == null)
                return;

            _whistleblowerShovelsSpawned = true;

            Item shovel = start.allItemsList?.itemsList?.FirstOrDefault(item =>
                item != null
                && item.spawnPrefab != null
                && string.Equals(item.itemName, "Shovel", StringComparison.OrdinalIgnoreCase));
            if (shovel == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Whistleblower entrance shovels skipped: no vanilla Shovel item in allItemsList.");
                return;
            }

            EntranceTeleport entrance = UnityEngine.Object
                .FindObjectsByType<EntranceTeleport>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(candidate => candidate != null && !candidate.isEntranceToBuilding)
                .OrderBy(candidate => candidate.entranceId)
                .FirstOrDefault();
            if (entrance == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Whistleblower entrance shovels skipped: no interior EntranceTeleport found.");
                return;
            }

            Transform anchor = entrance.entrancePoint != null ? entrance.entrancePoint : entrance.transform;
            int spawned = 0;
            for (int i = 0; i < WhistleblowerEntranceShovelCount; i++)
            {
                float side = i % 2 == 0 ? -1f : 1f;
                Vector3 wanted = anchor.position + anchor.forward * 1.4f + anchor.right * (0.7f * side);
                if (NavMesh.SamplePosition(wanted, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                    wanted = hit.position;
                else if (NavMesh.SamplePosition(anchor.position, out hit, 4f, NavMesh.AllAreas))
                    wanted = hit.position + anchor.right * (0.5f * side);

                GameObject obj = UnityEngine.Object.Instantiate(
                    shovel.spawnPrefab,
                    wanted + Vector3.up * 0.15f,
                    Quaternion.Euler(shovel.restingRotation),
                    start.propsContainer);
                if (obj == null)
                    continue;
                if (!obj.activeSelf)
                    obj.SetActive(true);

                GrabbableObject grabbable = obj.GetComponent<GrabbableObject>();
                if (grabbable != null)
                {
                    grabbable.fallTime = 0f;
                    grabbable.isInFactory = true;
                }

                NetworkObject netObj = obj.GetComponent<NetworkObject>();
                if (netObj == null)
                {
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts] Whistleblower entrance shovel prefab has no NetworkObject; destroying the local copy.");
                    UnityEngine.Object.Destroy(obj);
                    continue;
                }

                if (!netObj.IsSpawned)
                {
                    netObj.Spawn();
                    if (start.propsContainer != null)
                        netObj.TrySetParent(start.propsContainer);
                }

                spawned++;
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Whistleblower entrance shovels: {spawned}/{WhistleblowerEntranceShovelCount} spawned at entrance {entrance.entranceId} ({FormatVector(anchor.position)}).");
        }

        internal static bool ShouldAllowWhistleblowerSpawn(EnemyType enemyType)
        {
            if (!IsWhistleblowerEnemyType(enemyType))
                return true;

            bool allowed = GetCurrentContract() == MoonContractType.Whistleblower;
            if (!allowed)
            {
                ClearWhistleblowerEnemyReference();
                RemoveWhistleblowerFromLevelSpawnPools($"blocked spawn while current contract is {GetCurrentContract()}");
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Blocked Whistleblower spawn while current contract is {GetCurrentContract()}.");
            }

            return allowed;
        }

        internal static EnemyType ResolveLevelEnemyType(RoundManager round, int enemyIndex)
        {
            if (round?.currentLevel?.Enemies == null)
                return null;
            if (enemyIndex < 0 || enemyIndex >= round.currentLevel.Enemies.Count)
                return null;

            return round.currentLevel.Enemies[enemyIndex]?.enemyType;
        }

        private static bool IsWhistleblowerEnemyType(EnemyType enemyType)
        {
            if (enemyType == null)
                return false;
            if (WhistleblowerEnemyRegistry.EnemyType != null && enemyType == WhistleblowerEnemyRegistry.EnemyType)
                return true;
            return string.Equals(enemyType.enemyName, WhistleblowerEnemyRegistry.EnemyName, StringComparison.OrdinalIgnoreCase);
        }

        private static void RemoveWhistleblowerFromLevelSpawnPools(
            string reason,
            MoonContractType activeContract,
            bool useActiveContractReason)
        {
            SelectableLevel level = RoundManager.Instance?.currentLevel ?? StartOfRound.Instance?.currentLevel;
            if (level?.Enemies == null || level.Enemies.Count == 0)
                return;

            int removed = level.Enemies.RemoveAll(entry => entry != null && IsWhistleblowerEnemyType(entry.enemyType));
            if (removed > 0)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts] Removed {removed} Whistleblower entr{(removed == 1 ? "y" : "ies")} from indoor spawn pool ({FormatWhistleblowerGuardReason(reason, activeContract, useActiveContractReason)}).");
            }
        }

        private static void RemoveWhistleblowerFromLevelSpawnPools(MoonContractType activeContract)
        {
            RemoveWhistleblowerFromLevelSpawnPools(null, activeContract, useActiveContractReason: true);
        }

        private static void RemoveWhistleblowerFromLevelSpawnPools(string reason)
        {
            RemoveWhistleblowerFromLevelSpawnPools(reason, MoonContractType.None, useActiveContractReason: false);
        }

        private static void RemoveNonContractWhistleblowers(
            string reason,
            MoonContractType activeContract,
            bool useActiveContractReason)
        {
            var round = RoundManager.Instance;
            if (round == null || !round.IsServer || round.SpawnedEnemies == null)
                return;

            WhistleblowerEnemySweepBuffer.Clear();
            WhistleblowerEnemySweepBuffer.AddRange(round.SpawnedEnemies);
            string formattedReason = null;

            try
            {
                for (int i = 0; i < WhistleblowerEnemySweepBuffer.Count; i++)
                {
                    EnemyAI enemy = WhistleblowerEnemySweepBuffer[i];
                    if (enemy == null)
                        continue;

                    EnemyType enemyType = enemy.enemyType;
                    if (enemyType != null && !IsWhistleblowerEnemyType(enemyType))
                        continue;

                    var whistleblower = enemy as WhistleblowerEnemyAI
                        ?? enemy.GetComponent<WhistleblowerEnemyAI>()
                        ?? enemy.GetComponentInChildren<WhistleblowerEnemyAI>();
                    if (whistleblower == null)
                        continue;

                    if (!IsWhistleblowerEnemyType(whistleblower.enemyType))
                        continue;

                    NetworkObject networkObject = whistleblower.NetworkObject;
                    if (networkObject != null && networkObject.IsSpawned)
                        networkObject.Despawn(destroy: true);
                    else
                        UnityEngine.Object.Destroy(whistleblower.gameObject);

                    if (_whistleblowerEnemy == whistleblower)
                        ClearWhistleblowerEnemyReference();

                    formattedReason ??= FormatWhistleblowerGuardReason(reason, activeContract, useActiveContractReason);
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Removed non-contract Whistleblower ({formattedReason}).");
                }
            }
            finally
            {
                WhistleblowerEnemySweepBuffer.Clear();
            }
        }

        private static void RemoveNonContractWhistleblowers(MoonContractType activeContract)
        {
            RemoveNonContractWhistleblowers(null, activeContract, useActiveContractReason: true);
        }

        private static string FormatWhistleblowerGuardReason(
            string reason,
            MoonContractType activeContract,
            bool useActiveContractReason)
        {
            if (!string.IsNullOrWhiteSpace(reason))
                return reason;
            return useActiveContractReason ? $"active contract is {activeContract}" : "unspecified reason";
        }

        /// <param name="scanned">True when the spawned-enemy list was actually walked on
        /// this call. False means the 0.5 s scan window has not reopened, so a false return
        /// is "unknown", not "no whistleblower exists".</param>
        private static bool TryCacheActiveWhistleblowerEnemy(out bool scanned)
        {
            scanned = false;
            if (_whistleblowerEnemy != null && !_whistleblowerEnemy.isEnemyDead)
            {
                _whistleblowerEscapeCount = Mathf.Max(_whistleblowerEscapeCount, _whistleblowerEnemy.EscapeCount);
                return true;
            }

            if (_whistleblowerEnemy != null && _whistleblowerEnemy.Neutralized)
                _whistleblowerNeutralized = true;

            _whistleblowerEnemy = null;

            // #716/B3: two callers per frame each walked every spawned enemy with a
            // GetComponent plus a GetComponentInChildren. Only a newly spawned enemy can
            // change the answer, so the walk runs on a window and skips the enemies a
            // previous window already ruled out.
            float now = Time.time;
            if (now < _nextWhistleblowerEnemyScanAt)
                return false;
            _nextWhistleblowerEnemyScanAt = now + WhistleblowerEnemyScanIntervalSeconds;
            scanned = true;

            var spawned = RoundManager.Instance?.SpawnedEnemies;
            if (spawned == null)
                return false;

            foreach (EnemyAI enemy in spawned)
            {
                if (enemy == null)
                    continue;
                int enemyId = enemy.GetInstanceID();
                if (WhistleblowerScanRejectedEnemies.Contains(enemyId))
                    continue;

                var whistleblower = enemy as WhistleblowerEnemyAI
                    ?? enemy.GetComponent<WhistleblowerEnemyAI>()
                    ?? enemy.GetComponentInChildren<WhistleblowerEnemyAI>();
                if (whistleblower == null)
                {
                    // A plain enemy never grows a WhistleblowerEnemyAI later, so this is
                    // a permanent rejection. A dead one is not cached: it can still be the
                    // whistleblower and the escape/neutralised bookkeeping below needs it.
                    WhistleblowerScanRejectedEnemies.Add(enemyId);
                    continue;
                }

                if (whistleblower.isEnemyDead)
                    continue;

                if (WhistleblowerEnemyRegistry.EnemyType != null && whistleblower.enemyType != WhistleblowerEnemyRegistry.EnemyType)
                    continue;

                _whistleblowerEnemy = whistleblower;
                _whistleblowerEscapeCount = Mathf.Max(_whistleblowerEscapeCount, whistleblower.EscapeCount);
                _whistleblowerNeutralized = _whistleblowerNeutralized || whistleblower.Neutralized;
                return true;
            }

            return false;
        }

        internal static void NotifyWhistleblowerEscaped(WhistleblowerEnemyAI enemy, int escapeCount)
        {
            if (enemy == null || GetCurrentContract() != MoonContractType.Whistleblower)
                return;
            if (_whistleblowerEnemy != null && _whistleblowerEnemy != enemy)
                return;

            _whistleblowerEnemy = enemy;
            int previous = _whistleblowerEscapeCount;
            _whistleblowerEscapeCount = Mathf.Clamp(Mathf.Max(_whistleblowerEscapeCount, escapeCount), 0, 4);
            if (_whistleblowerEscapeCount != previous)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Whistleblower escape {_whistleblowerEscapeCount}/4 confirmed.");
        }

        internal static void NotifyWhistleblowerNeutralized(WhistleblowerEnemyAI enemy)
        {
            if (enemy == null || GetCurrentContract() != MoonContractType.Whistleblower)
                return;
            if (_whistleblowerEnemy != null && _whistleblowerEnemy != enemy)
                return;

            _whistleblowerEnemy = enemy;
            _whistleblowerEscapeCount = Mathf.Max(_whistleblowerEscapeCount, enemy.EscapeCount);
            bool alreadyNeutralized = _whistleblowerNeutralized;
            _whistleblowerNeutralized = true;
            if (!alreadyNeutralized)
            {
                ulong killerClientId = enemy.LastAttackerClientId;
                if (killerClientId == ulong.MaxValue)
                    ContractProgressionEvents.PublishWhistleblowerNeutralizedToAll(enemy.NetworkObjectId);
                else
                    ContractProgressionEvents.PublishWhistleblowerNeutralized(killerClientId, enemy.NetworkObjectId);

                // #944 S11 U6: the single emitter for this line, and it now sits on the transition
                // rather than on every call. Outside this block the method is re-entered by the
                // second caller (WhistleblowerEnemyAI.cs:1549) and by repeat notifications, which is
                // half of the run 1 lines 35899-35900 pair; the other half was a duplicate emitter
                // in WhistleblowerEnemyAI itself, now removed.
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Whistleblower neutralized.");
            }
        }

        /// <summary>
        /// #964 S14 U2 owns this method. Pre-declared by #966 S14 U4 so the latch it raises and
        /// the reads that consume it merge as one hunk. Raised by the manifest's departure
        /// confirmation when a Blacksite ship leaves with the witness loaded: that is the failure
        /// the catalog has promised since #694 and the only thing that voids this contract.
        /// The one-line call to <see cref="LogWitnessExtractedServer"/> is #966 S14 U4's and is
        /// listed as such in that unit's report.
        /// </summary>
        internal static void NotifyWhistleblowerExtractedServer(WhistleblowerEnemyAI enemy)
        {
            if (GetCurrentContract() != MoonContractType.Whistleblower)
                return;
            if (_whistleblowerExtracted)
                return;

            _whistleblowerExtracted = true;
            LogWitnessExtractedServer(enemy);
            ContractsBootstrap.Log?.LogInfo("[MoonContracts] Whistleblower extracted by the recovery team; contract voided.");
        }

        /// <summary>
        /// #1076: the one departure terminal for all three contracts whose objective the squad can
        /// carry off the moon, called once per extracted record from
        /// <c>BlacksiteObjectiveManifestController.ConfirmDepartureServer</c>.
        ///
        /// Server only. It raises the host's own latch DIRECTLY - it must not depend on its own
        /// subscriber being registered - and then puts the fact on the progression channel for
        /// everybody else. That channel is the only carrier that works here: the whole departure is
        /// one synchronous chain (<c>BlacksiteShipController.Update</c> -> this ->
        /// <c>MoonEscalationState.NotifyBlacksiteShipDeparted</c> -> <c>TryDespawnObject</c>), so
        /// the ship, the mission that is a component of it, the cart, the witness and the drive are
        /// all despawned before NGO's PostLateUpdate flush; any NetworkVariable, NetworkList or
        /// object RPC written here is dropped and only the destroy message reaches the client.
        /// </summary>
        internal static void NotifyBlacksiteObjectiveDepartedServer(
            Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteObjectiveKind kind)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening
                && !NetworkManager.Singleton.IsServer)
            {
                return;
            }

            string token = BlacksiteDepartureToken(kind);
            // Loot is scrap a raider pocketed on the way past; it voids no contract.
            if (token == null)
                return;
            // Raise the host's own latch (a no-op when the witness path already raised it one
            // line earlier in ConfirmDepartureServer), then publish exactly once per token per
            // round. The dedupe is its own set, never the latch: see _blacksiteDepartedPublished.
            NoteBlacksiteObjectiveDeparted(token);
            if (!_blacksiteDepartedPublished.Add(token))
                return;

            ContractProgressionEvents.PublishBlacksiteObjectiveDeparted(token, BlacksiteDepartureContract(token));
        }

        /// <summary>
        /// #1076: the wire token for the three objective kinds a departure can void a contract
        /// with. The enum name is the token, so the two cannot drift; every other kind answers
        /// null and is ignored.
        /// </summary>
        private static string BlacksiteDepartureToken(
            Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteObjectiveKind kind)
        {
            switch (kind)
            {
                case Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteObjectiveKind.Payload:
                case Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteObjectiveKind.Witness:
                case Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteObjectiveKind.ShadowDrive:
                    return kind.ToString();
                default:
                    return null;
            }
        }

        private static string BlacksiteDepartureContract(string token)
        {
            return token == "Payload" ? "Payload"
                : token == "Witness" ? "Whistleblower"
                : "ShadowRaid";
        }

        /// <summary>
        /// #1076: raise the per-peer one-way latch for one objective kind. Returns true on the
        /// transition only. Identical on host and client, because the host reaches it from
        /// <see cref="NotifyBlacksiteObjectiveDepartedServer"/> and every other peer reaches it
        /// from the progression subscriber below - including a late joiner, whose copy arrives on
        /// the round-keyed replay (#406) rather than live.
        /// </summary>
        private static bool NoteBlacksiteObjectiveDeparted(string token)
        {
            switch (token)
            {
                case "Witness":
                    if (_whistleblowerExtracted)
                        return false;
                    _whistleblowerExtracted = true;
                    return true;
                case "Payload":
                    return PayloadContract.NoteBlacksiteDeparted();
                case "ShadowDrive":
                    if (!ShadowRaidContract.NoteDriveDeparted())
                        return false;
                    // The hard drive's extraction tip. It is raised HERE rather than from a
                    // ClientRpc on the mission so that the tip and the card row can never
                    // disagree: both come off this one transition, on every peer, and a tip
                    // announcing a departure the card had not registered was exactly the
                    // contradiction the old carrier produced. The cart keeps its own shipped tip
                    // (PayloadCartController.Blacksite.cs), which is why this is the only one.
                    HUDManager.Instance?.DisplayTip("Hard drive extracted", "Blacksite departed with the hard drive.", true);
                    return true;
                default:
                    return false;
            }
        }

        private static bool _blacksiteDepartureHandlerRegistered;

        /// <summary>
        /// #1076: subscribe once per session to the progression funnel. The handler is a static
        /// method on a static class, so there is nothing to unsubscribe and nothing to leak; the
        /// latches it raises are the per-round state and they are cleared by ResetRunState.
        /// </summary>
        private static void EnsureBlacksiteDepartureHandler()
        {
            if (_blacksiteDepartureHandlerRegistered)
                return;

            _blacksiteDepartureHandlerRegistered = true;
            ContractProgressionEvents.Published += OnContractProgressionEventPublished;
        }

        private static void OnContractProgressionEventPublished(ContractProgressionEvent progressionEvent)
        {
            if (progressionEvent == null
                || progressionEvent.Kind != ContractProgressionEventKind.BlacksiteObjectiveDeparted)
            {
                return;
            }

            // The player id is deliberately ignored. A departure belongs to the crew, so the
            // publisher fans one copy out per participant; a peer that joined afterwards receives
            // copies addressed to players it never shared a lobby with, and any one of them is the
            // same fact.
            //
            // The ROUND is not ignored. The #406 ledger replays this round's events to a joiner,
            // and the only late-join window the game has is orbit, after the round has ended: the
            // ledger still names the ended round until the next round's first event, so a joiner
            // would otherwise inherit the departure (and the drive tip) and open the next moon's
            // card already voided. Same fold-over the LGU trailer guards with its round stamp.
            // A copy is honoured only while the round it was minted for is the live one.
            StartOfRound start = StartOfRound.Instance;
            bool roundLive = start != null && start.shipHasLanded && !start.inShipPhase;
            if (!roundLive || !ContractProgressionEvents.IsBlacksiteDepartureForCurrentRound(progressionEvent.EventId))
                return;
            NoteBlacksiteObjectiveDeparted(progressionEvent.SourceLabel);
        }

        /// <summary>
        /// #966 S14 U4. The terminal line of the <c>[UAT][Custody]</c> vocabulary. <c>aboard</c> is
        /// emitted when the witness's escort completes his board crossing (#1219 C2); <c>extracted</c> is
        /// emitted here, once, on the transition that voids the contract. Not rate-gated, because
        /// it can happen at most once per round by construction.
        /// </summary>
        private static void LogWitnessExtractedServer(WhistleblowerEnemyAI enemy)
        {
            Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteDiagnostics.Log(
                "Custody",
                $"event=extracted, witness={(enemy != null ? enemy.NetworkObjectId : 0UL)}, member=-1");
        }

        private static List<MoonContractObjective> GetWhistleblowerObjectives()
        {
            EnsureWhistleblowerTarget();
            if (_whistleblowerEnemy != null)
            {
                _whistleblowerEscapeCount = Mathf.Max(_whistleblowerEscapeCount, _whistleblowerEnemy.EscapeCount);
                _whistleblowerNeutralized = _whistleblowerNeutralized || _whistleblowerEnemy.Neutralized;
            }

            // #765 verdict round 2: "Neutralize" while any player has him in sight (held a few
            // seconds so turning away does not flip it), "Locate" again once he has slipped out of
            // sight, until he is dead. #725: the briefing calls him the witness, so the card does.
            // #966 S14 U4: the row policy itself now lives in the Unity-free
            // WhistleblowerObjectiveLines so the card is pinned headlessly, and the escort adds the
            // one reading the shipped single row had no way to say: that the contract has voided
            // when their ship leaves with him. Where he is while an escort holds him is the belt's.
            bool inSight = _whistleblowerEnemy != null && _whistleblowerEnemy.IsObservedRecently;
            bool inCustody = _whistleblowerEnemy != null && _whistleblowerEnemy.IsInCustody;
            // Outside and still held is the failure clock running. The belt calls it EXTRACTING
            // WITNESS; on the card it only turns the action row lethal.
            bool beingExtracted = inCustody && !_whistleblowerEnemy.IsInsideFacility;
            // #1076: aboard-their-ship comes from the replicated manifest snapshots, so it reads
            // the same on host and clients and reverts if the crew takes him back. Departure does
            // NOT come from there - the latch is raised by the progression channel, because every
            // object that could carry the fact is despawned in the departure's own call stack.
            bool aboard = !_whistleblowerExtracted
                && Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksitePossessionReader
                    .Read(Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteObjectiveKind.Witness).IsAboard;
            return WhistleblowerObjectiveLines.Build(
                inSight, inCustody, beingExtracted, _whistleblowerNeutralized, _whistleblowerExtracted, aboard);
        }

        private static bool TryGetWhistleblowerSpawnPlacement(out Vector3 position, out Quaternion rotation)
        {
            // The scan runs once per round and is pumped every frame from
            // EnsureWhistleblowerTarget; once it resolves, the answer is latched so the
            // pump and the throttled spawn decision read the same placement and neither
            // pays for it twice.
            if (_whistleblowerPlacementResolved)
            {
                position = _whistleblowerPlacementPosition;
                rotation = _whistleblowerPlacementRotation;
                return true;
            }

            position = Vector3.zero;
            rotation = Quaternion.identity;

            // #716/A4: one CalculatePath per inside AI node per entrance, a whole-dungeon tile
            // walk per node for the depth term, and three LINQ sorts over the result — all on
            // the host's landed frame. The scan is now metered by a per-frame millisecond
            // budget, keeps only the best candidates through a single insertion pass instead
            // of sorting the full node set, and reuses one tile snapshot for every node.
            if (!AdvanceWhistleblowerCandidateScan(out List<WhistleblowerSpawnCandidate> allCandidates, out List<Vector3> entrancePoints))
                return false;

            if (allCandidates.Count == 0)
                return false;

            WhistleblowerBalanceConfig.RiskConfig risk = ContractsBootstrap.WhistleblowerBalance.ForRisk(GetCurrentRiskTier());
            float minPath = Mathf.Max(0f, risk.MinSpawnPathDistance.Value);
            float minHorizontal = Mathf.Max(0f, risk.MinSpawnHorizontalDistance.Value);
            float minDepth = Mathf.Clamp01(risk.MinSpawnTileDepth.Value);
            float topScorePercent = Mathf.Clamp(risk.TopSpawnScorePercent.Value, 0.1f, 1f);

            var distanceFiltered = allCandidates
                .Where(candidate => candidate.PathDistance >= minPath && candidate.StraightDistance >= minHorizontal)
                .ToList();
            if (distanceFiltered.Count == 0)
                distanceFiltered = allCandidates
                    .Where(candidate => candidate.PathDistance >= minPath * 0.75f || candidate.StraightDistance >= minHorizontal * 0.75f || candidate.TileDepth >= minDepth)
                    .ToList();
            if (distanceFiltered.Count == 0)
                distanceFiltered = allCandidates;

            float bestScore = distanceFiltered[0].Score;
            float deepFloor = Mathf.Max(minPath, bestScore * topScorePercent);
            var candidates = distanceFiltered
                .Where(candidate =>
                    candidate.Score >= deepFloor
                    && (candidate.PathDistance >= minPath || candidate.StraightDistance >= minHorizontal || candidate.TileDepth >= minDepth))
                .OrderByDescending(candidate => candidate.Score)
                .ToList();

            if (candidates.Count == 0)
                candidates = distanceFiltered.Take(Math.Min(8, distanceFiltered.Count)).ToList();

            foreach (var candidate in candidates)
            {
                Vector3 candidatePosition = candidate.Position + Vector3.up * 0.04f;
                if (NavMesh.SamplePosition(candidatePosition, out NavMeshHit hit, 8f, NavMesh.AllAreas))
                    candidatePosition = hit.position;

                position = candidatePosition;
                Vector3 away = candidatePosition - InteriorAnchorService.GetNearestEntrancePoint(entrancePoints, candidatePosition);
                away.y = 0f;
                rotation = away.sqrMagnitude > 0.01f
                    ? Quaternion.LookRotation(away.normalized, Vector3.up)
                    : Quaternion.identity;
                _whistleblowerPlacementPosition = position;
                _whistleblowerPlacementRotation = rotation;
                _whistleblowerPlacementResolved = true;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Whistleblower deep spawn selected path={candidate.PathDistance:0.0}, horizontal={candidate.StraightDistance:0.0}, tileDepth={candidate.TileDepth:0.00}, score={candidate.Score:0.0}, candidates={candidates.Count}/{allCandidates.Count}.");
                return true;
            }

            return false;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>#716/A4: one candidate row for the whistleblower spawn pick. It used to be
        /// an anonymous type built by two chained Selects; it has to be a named type now so a
        /// sliced scan can carry rows across frames.</summary>
        private readonly struct WhistleblowerSpawnCandidate
        {
            internal WhistleblowerSpawnCandidate(
                GameObject node,
                Vector3 position,
                float pathDistance,
                float straightDistance,
                float tileDepth)
            {
                Node = node;
                Position = position;
                PathDistance = pathDistance;
                StraightDistance = straightDistance;
                TileDepth = tileDepth;
                Score = pathDistance + straightDistance * 0.65f + tileDepth * 180f;
            }

            internal GameObject Node { get; }
            internal Vector3 Position { get; }
            internal float PathDistance { get; }
            internal float StraightDistance { get; }
            internal float TileDepth { get; }
            internal float Score { get; }
        }

        // Only the highest-scoring candidates can ever win: every downstream branch either
        // filters then takes the best by score, or falls back to the top eight. Keeping the
        // best 96 by insertion replaces three full sorts over the whole node set (interiors
        // routinely generate several hundred AI nodes) with one bounded pass.
        private const int WhistleblowerCandidateRetainCount = 96;
        private const float WhistleblowerScanBudgetMilliseconds = 1.5f;
        private static GameObject[] _whistleblowerScanNodes;
        private static List<WhistleblowerSpawnCandidate> _whistleblowerScanBest;
        private static List<Vector3> _whistleblowerScanEntrances;
        private static Tile[] _whistleblowerScanTiles;
        private static int _whistleblowerScanCursor;
        private static bool _whistleblowerPlacementResolved;
        private static Vector3 _whistleblowerPlacementPosition;
        private static Quaternion _whistleblowerPlacementRotation = Quaternion.identity;

        private static void ResetWhistleblowerPlacementScan()
        {
            _whistleblowerShovelsSpawned = false;
            _whistleblowerScanNodes = null;
            _whistleblowerScanBest = null;
            _whistleblowerScanEntrances = null;
            _whistleblowerScanTiles = null;
            _whistleblowerScanCursor = 0;
            _whistleblowerPlacementResolved = false;
            _whistleblowerPlacementPosition = Vector3.zero;
            _whistleblowerPlacementRotation = Quaternion.identity;
        }

        /// <summary>Advances the candidate scan by one budgeted slice. Returns true only on
        /// the pass that completes it.</summary>
        private static bool AdvanceWhistleblowerCandidateScan(
            out List<WhistleblowerSpawnCandidate> candidates,
            out List<Vector3> entrancePoints)
        {
            candidates = null;
            entrancePoints = _whistleblowerScanEntrances;

            if (_whistleblowerScanNodes == null)
            {
                var round = RoundManager.Instance;
                GameObject[] nodes = round?.insideAINodes;
                if (nodes == null || nodes.Length == 0 || nodes.All(node => node == null))
                    nodes = GameObject.FindGameObjectsWithTag("AINode");
                if (nodes == null || nodes.Length == 0)
                    return false;

                _whistleblowerScanNodes = nodes;
                _whistleblowerScanBest = new List<WhistleblowerSpawnCandidate>(WhistleblowerCandidateRetainCount + 1);
                _whistleblowerScanEntrances = InteriorAnchorService.GetInteriorEntrancePositions();
                _whistleblowerScanTiles = SnapshotBlackoutDungeonTiles();
                _whistleblowerScanCursor = 0;
                entrancePoints = _whistleblowerScanEntrances;
            }

            long budgetTicks = (long)(WhistleblowerScanBudgetMilliseconds
                * (System.Diagnostics.Stopwatch.Frequency / 1000.0));
            long sliceStart = System.Diagnostics.Stopwatch.GetTimestamp();

            while (_whistleblowerScanCursor < _whistleblowerScanNodes.Length)
            {
                GameObject node = _whistleblowerScanNodes[_whistleblowerScanCursor++];
                if (node != null)
                {
                    Vector3 nodePosition = node.transform.position;
                    Tile nodeTile = FindNearestTile(nodePosition, _whistleblowerScanTiles);
                    // #874: a node inside a padded cell never becomes the witness spawn.
                    if (!InteriorPlacementService.IsTileExcludedFromPlacement(nodeTile))
                    {
                        InsertWhistleblowerCandidate(new WhistleblowerSpawnCandidate(
                            node,
                            InteriorAnchorService.ResolveFloorPosition(nodePosition),
                            InteriorAnchorService.EstimateNearestPathDistance(_whistleblowerScanEntrances, nodePosition),
                            InteriorAnchorService.EstimateNearestHorizontalDistance(_whistleblowerScanEntrances, nodePosition),
                            nodeTile != null ? InteriorAnchorService.GetTileDepth(nodeTile) : 0f));
                    }
                }

                // At least one node per pass, then stop as soon as the budget is spent.
                if (System.Diagnostics.Stopwatch.GetTimestamp() - sliceStart >= budgetTicks)
                    break;
            }

            if (_whistleblowerScanCursor < _whistleblowerScanNodes.Length)
                return false;

            candidates = _whistleblowerScanBest;
            entrancePoints = _whistleblowerScanEntrances;
            _whistleblowerScanNodes = null;
            _whistleblowerScanBest = null;
            _whistleblowerScanTiles = null;
            _whistleblowerScanCursor = 0;
            return true;
        }

        /// <summary>Descending-score insertion into the retained best-N list.</summary>
        private static void InsertWhistleblowerCandidate(WhistleblowerSpawnCandidate candidate)
        {
            List<WhistleblowerSpawnCandidate> best = _whistleblowerScanBest;
            if (best.Count >= WhistleblowerCandidateRetainCount
                && candidate.Score <= best[best.Count - 1].Score)
            {
                return;
            }

            int index = best.Count;
            while (index > 0 && best[index - 1].Score < candidate.Score)
                index--;

            best.Insert(index, candidate);
            if (best.Count > WhistleblowerCandidateRetainCount)
                best.RemoveAt(best.Count - 1);
        }

        private static Tile[] SnapshotBlackoutDungeonTiles()
        {
            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null)
                return Array.Empty<Tile>();

            var tiles = new List<Tile>(dungeon.AllTiles.Count);
            foreach (Tile tile in dungeon.AllTiles)
            {
                if (tile != null)
                    tiles.Add(tile);
            }

            return tiles.ToArray();
        }

        private static Tile FindNearestTile(Vector3 position, Tile[] tiles)
        {
            if (tiles == null || tiles.Length == 0)
                return null;

            Tile bestTile = null;
            float bestDistance = float.MaxValue;
            foreach (Tile tile in tiles)
            {
                if (tile == null)
                    continue;

                Vector3 closest = tile.Bounds.ClosestPoint(position);
                float distance = Vector3.SqrMagnitude(closest - position);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestTile = tile;
            }

            return bestTile;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"{value.x:0.0},{value.y:0.0},{value.z:0.0}";
        }

        private static void EnsureSurveyTargets()
        {
            if (_surveyTargetsInitialized || GetCurrentContract() != MoonContractType.Survey)
                return;

            var dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null || dungeon.AllTiles.Count == 0)
                return;

            // #550: depth is exactly the wrong sort key on its own. A sealed DunGen tile carries
            // its own NavMesh island, scores as pleasingly deep, and sorts to the front of this
            // list — which makes it the "deep route room" the crew is then asked to walk a drone
            // into. Gate the candidate pool on the tile-level reachability verdict; the area test
            // runs first because it is arithmetic and the verdict can cost a path query.
            //
            // #716/A4: that verdict is one NavMesh.CalculatePath per qualifying tile per
            // entrance, up to three probes each, and it used to run for every tile in the
            // dungeon on the landed frame on every machine. It is metered out here instead.
            // The pick stays byte-identical: qualifying tiles are appended in AllTiles order
            // exactly as the Where produced them, and the stable OrderByDescending below sees
            // the same sequence it saw before. Replicating a host-side pick was considered and
            // rejected — the pick is already deterministic from the seeded dungeon, so a new
            // network message would buy nothing but a late-join failure mode.
            if (!AdvanceSurveyTileScan(dungeon, out List<Tile> allTiles))
                return;
            // Unchanged from before slicing: no qualifying tile means retry on a later
            // frame (the interior can still be settling). The retry now costs one budgeted
            // slice per frame instead of a whole-dungeon path sweep.
            if (allTiles.Count == 0)
                return;

            var mainPath = dungeon.MainPathTiles != null
                ? new HashSet<Tile>(dungeon.MainPathTiles.Where(tile => tile != null))
                : new HashSet<Tile>();
            var selected = new HashSet<Tile>();
            var targets = new List<SurveyTarget>();
            int targetCount = GetSurveyDroneCountForCurrentMoon();

            Tile deepTile = allTiles.FirstOrDefault();
            AddSurveyTarget(targets, selected, deepTile, "deep route room");

            if (targets.Count < targetCount)
            {
                Tile sideTile = allTiles
                    .Where(tile => !selected.Contains(tile) && !mainPath.Contains(tile))
                    .OrderByDescending(InteriorAnchorService.GetTileDepth)
                    .ThenByDescending(InteriorAnchorService.GetTileArea)
                    .FirstOrDefault();
                AddSurveyTarget(targets, selected, sideTile, "side chamber");
            }

            if (targets.Count < targetCount)
            {
                float baseFloor = targets.Count > 0 ? targets[0].FloorY : (deepTile != null ? deepTile.Bounds.center.y : 0f);
                Tile verticalTile = allTiles
                    .Where(tile => !selected.Contains(tile) && Mathf.Abs(tile.Bounds.center.y - baseFloor) >= 6f)
                    .OrderByDescending(tile => Mathf.Abs(tile.Bounds.center.y - baseFloor))
                    .ThenByDescending(InteriorAnchorService.GetTileDepth)
                    .FirstOrDefault();
                string verticalLabel = verticalTile != null && verticalTile.Bounds.center.y > baseFloor
                    ? "upper floor room"
                    : "lower floor room";
                AddSurveyTarget(targets, selected, verticalTile, verticalLabel);
            }

            foreach (var tile in allTiles.Where(tile => !selected.Contains(tile)).OrderByDescending(InteriorAnchorService.GetTileArea))
            {
                if (targets.Count >= targetCount)
                    break;
                AddSurveyTarget(targets, selected, tile, targets.Count == 2 ? "large survey room" : $"survey room {targets.Count + 1}");
            }

            if (targets.Count == 0)
                return;

            while (targets.Count < targetCount)
            {
                var clone = targets[targets.Count % targets.Count];
                clone.Label = $"backup survey point {targets.Count + 1}";
                clone.Position += new Vector3(1.5f * targets.Count, 0f, -1.5f * targets.Count);
                targets.Add(clone);
            }

            _surveyTargets = targets;
            _surveyTargetsInitialized = true;

            ContractsBootstrap.Log?.LogInfo(
                "[MoonContracts] Survey targets selected: " +
                string.Join(", ", _surveyTargets.Select(target => target.Label)));
        }

        // #716/A4: sliced survey tile qualification. Eight tiles per frame keeps the worst
        // observed per-tile cost (three CalculatePath probes per entrance) inside a fraction
        // of a frame; a 150-tile interior resolves in ~19 frames, roughly 0.3s at 60fps,
        // entirely inside the walk from the ship to the entrance.
        private const int SurveyTileScanTilesPerPass = 8;
        private static List<Tile> _surveyScanSource;
        private static List<Tile> _surveyScanQualified;
        private static List<Vector3> _surveyScanEntrances;
        private static int _surveyScanCursor;

        private static void ResetSurveyTargetScan()
        {
            _surveyScanSource = null;
            _surveyScanQualified = null;
            _surveyScanEntrances = null;
            _surveyScanCursor = 0;
        }

        /// <summary>Advances the qualifying-tile scan by one slice. Returns true only on the
        /// pass that completes it, handing back the depth-sorted candidate list the eager
        /// LINQ chain used to produce in one frame.</summary>
        private static bool AdvanceSurveyTileScan(Dungeon dungeon, out List<Tile> qualified)
        {
            qualified = null;
            if (_surveyScanSource == null)
            {
                _surveyScanSource = new List<Tile>(dungeon.AllTiles);
                _surveyScanQualified = new List<Tile>();
                _surveyScanEntrances = InteriorAnchorService.GetInteriorEntrancePositions();
                _surveyScanCursor = 0;
            }

            int end = Mathf.Min(_surveyScanSource.Count, _surveyScanCursor + SurveyTileScanTilesPerPass);
            for (; _surveyScanCursor < end; _surveyScanCursor++)
            {
                Tile tile = _surveyScanSource[_surveyScanCursor];
                if (tile == null || InteriorPlacementService.IsTileExcludedFromPlacement(tile))
                    continue;
                if (InteriorAnchorService.GetTileArea(tile) < 24f)
                    continue;
                if (!InteriorAnchorService.IsTileReachableFromEntrance(tile, _surveyScanEntrances))
                    continue;
                _surveyScanQualified.Add(tile);
            }

            if (_surveyScanCursor < _surveyScanSource.Count)
                return false;

            // OrderByDescending is stable, so appending in AllTiles order above reproduces
            // the pre-slicing ordering exactly.
            qualified = _surveyScanQualified
                .OrderByDescending(InteriorAnchorService.GetTileDepth)
                .ToList();
            ResetSurveyTargetScan();
            return true;
        }

        private static void AddSurveyTarget(List<SurveyTarget> targets, HashSet<Tile> selected, Tile tile, string label)
        {
            if (tile == null || targets == null || selected == null || selected.Contains(tile))
                return;

            selected.Add(tile);
            Bounds bounds = tile.Bounds;
            float radius = Mathf.Clamp(Mathf.Max(bounds.size.x, bounds.size.z) * 0.42f, 5f, 14f);
            targets.Add(new SurveyTarget
            {
                Label = label,
                Position = bounds.center,
                Radius = radius,
                FloorY = bounds.center.y,
                Placed = false,
            });
        }

        private static void EnsureSurveyDelivery()
        {
            if (_surveyDronesDelivered || _surveyDeliveryRequested || !_surveyTargetsInitialized)
                return;
            if (GetCurrentContract() != MoonContractType.Survey)
                return;
            if (StartOfRound.Instance == null || !StartOfRound.Instance.shipHasLanded)
                return;

            var dropship = UnityEngine.Object.FindObjectOfType<ItemDropship>();
            if (dropship == null)
            {
                if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer)
                    SpawnSurveyDronesAtPositions(null, StartOfRound.Instance.propsContainer);
                _surveyDeliveryRequested = true;
                return;
            }

            // #716/B2: a client has no delivery to request — the host owns the dropship —
            // but the old early return left the poll unlatched, so this ran
            // FindObjectOfType<ItemDropship> every RoundManager.Update plus twice per HUD
            // poll for the whole round. Latch here: the client's drones arrive through the
            // host's spawn/snapshot path, which does not read this flag.
            if (!dropship.IsServer)
            {
                _surveyDeliveryRequested = true;
                return;
            }

            _surveyDeliveryRequested = true;

            var terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
            bool existingDelivery = dropship.deliveringOrder
                || dropship.shipLanded
                || (terminal != null && terminal.orderedItemsFromTerminal != null && terminal.orderedItemsFromTerminal.Count > 0)
                || (terminal != null && terminal.orderedVehicleFromTerminal != -1);

            if (existingDelivery)
            {
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Survey beacons will be added to the active dropship delivery.");
                return;
            }

            try
            {
                if (_surveyDropshipLandMethod == null)
                    _surveyDropshipLandMethod = AccessTools.Method(typeof(ItemDropship), "LandShipOnServer");

                _surveyDropshipLandMethod?.Invoke(dropship, null);
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Survey dropship delivery requested.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Survey dropship request failed; spawning drones in ship: {e.Message}");
                SpawnSurveyDronesAtPositions(null, StartOfRound.Instance.propsContainer);
            }
        }

        internal static void TrySpawnSurveyDronesFromDropship(ItemDropship dropship)
        {
            if (dropship == null || !dropship.IsServer)
                return;
            if (GetCurrentContract() != MoonContractType.Survey || _surveyDronesDelivered)
                return;

            EnsureSurveyTargets();
            if (!_surveyTargetsInitialized)
                return;

            SpawnSurveyDronesAtPositions(dropship.itemSpawnPositions, StartOfRound.Instance?.propsContainer);
        }

        private static void SpawnSurveyDronesAtPositions(IList<Transform> spawnPositions, Transform parent)
        {
            if (_surveyDronesDelivered)
                return;

            // #759: the authored Survey Beacon when the bundle has it; otherwise the renamed
            // vanilla Radar Booster the contract shipped with.
            Item surveyItem;
            Item baseItem;
            if (SurveyBeaconItemRegistry.TryGet(out Item beaconItem))
            {
                surveyItem = beaconItem;
                baseItem = beaconItem;
            }
            else
            {
                baseItem = FindSurveyDroneBaseItem();
                if (baseItem?.spawnPrefab == null)
                {
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts] Survey could not find a grabbable base item for beacon delivery.");
                    return;
                }

                surveyItem = GetSurveyDroneItemProperties(baseItem);
            }
            Vector3 fallback = StartOfRound.Instance != null
                ? StartOfRound.Instance.elevatorTransform.position + StartOfRound.Instance.elevatorTransform.forward * 1.5f
                : Vector3.zero;

            int droneCount = _surveyTargets != null && _surveyTargets.Count > 0
                ? _surveyTargets.Count
                : GetSurveyDroneCountForCurrentMoon();
            float centerOffset = (droneCount - 1) * 0.5f;
            for (int i = 0; i < droneCount; i++)
            {
                Vector3 position = fallback + new Vector3((i - centerOffset) * 0.8f, 0.4f, 0f);
                if (spawnPositions != null && spawnPositions.Count > 0 && spawnPositions[i % spawnPositions.Count] != null)
                    position = spawnPositions[i % spawnPositions.Count].position + new Vector3((i - centerOffset) * 0.18f, 0f, 0f);

                GameObject drone = UnityEngine.Object.Instantiate(
                    baseItem.spawnPrefab,
                    position,
                    Quaternion.identity,
                    parent);
                PrepareSurveyDroneObject(drone, i, surveyItem);

                var networkObject = drone.GetComponent<NetworkObject>();
                if (networkObject != null && !networkObject.IsSpawned)
                {
                    networkObject.Spawn();
                    if (parent != null)
                        networkObject.TrySetParent(parent);
                }
            }

            _surveyDronesDelivered = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Survey beacons delivered ({(surveyItem == _surveyDroneItemProperties ? "Radar Booster fallback" : "authored beacon")}).");
        }

        private static Item FindSurveyDroneBaseItem()
        {
            var items = StartOfRound.Instance?.allItemsList?.itemsList;
            if (items == null || items.Count == 0)
                return null;

            Item Match(Func<Item, bool> predicate)
            {
                return items.FirstOrDefault(item =>
                    item != null
                    && item.spawnPrefab != null
                    && item.spawnPrefab.GetComponent<GrabbableObject>() != null
                    && predicate(item));
            }

            return Match(item => !item.isScrap && NameContains(item, "radar"))
                ?? Match(item => !item.isScrap && NameContains(item, "walkie"))
                ?? Match(item => !item.isScrap && NameContains(item, "flashlight"))
                ?? Match(item => !item.isScrap)
                ?? Match(_ => true);
        }

        private static Item GetSurveyDroneItemProperties(Item baseItem)
        {
            if (_surveyDroneItemProperties != null)
                return _surveyDroneItemProperties;

            _surveyDroneItemProperties = UnityEngine.Object.Instantiate(baseItem);
            _surveyDroneItemProperties.name = "LGU Survey Beacon Item (fallback)";
            _surveyDroneItemProperties.itemName = "Survey Beacon";
            _surveyDroneItemProperties.isScrap = false;
            _surveyDroneItemProperties.creditsWorth = 0;
            _surveyDroneItemProperties.minValue = 0;
            _surveyDroneItemProperties.maxValue = 0;
            _surveyDroneItemProperties.twoHanded = false;
            _surveyDroneItemProperties.twoHandedAnimation = false;
            _surveyDroneItemProperties.disallowUtilitySlot = true;
            _surveyDroneItemProperties.canBeGrabbedBeforeGameStart = true;
            _surveyDroneItemProperties.requiresBattery = false;
            _surveyDroneItemProperties.automaticallySetUsingPower = false;
            _surveyDroneItemProperties.batteryUsage = 0f;
            _surveyDroneItemProperties.spawnPrefab = baseItem.spawnPrefab;
            return _surveyDroneItemProperties;
        }

        private static bool NameContains(Item item, string fragment)
        {
            return item?.itemName != null
                && item.itemName.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void PrepareSurveyDroneObject(GameObject drone, int index, Item itemProperties)
        {
            if (drone == null)
                return;

            drone.name = $"LGU Survey Beacon {index + 1}";

            var grabbable = drone.GetComponent<GrabbableObject>();
            if (grabbable != null)
            {
                if (itemProperties != null)
                    grabbable.itemProperties = itemProperties;
                grabbable.grabbable = true;
                grabbable.grabbableToEnemies = false;
                grabbable.scrapValue = 0;
                // #716/E7: vanilla grab tooltips end in the bound interact key, and the
                // HUD substitutes the real binding for the literal "[LMB]" token. Without
                // the suffix this drone is the only grabbable in the facility that never
                // tells the player which key picks it up.
                grabbable.customGrabTooltip = "Grab survey beacon : [LMB]";
                grabbable.isInFactory = false;
            }

            var scan = drone.GetComponentInChildren<ScanNodeProperties>();
            if (scan != null)
            {
                scan.headerText = "Survey Beacon";
                scan.subText = "Company survey equipment";
                scan.scrapValue = 0;
            }

            var survey = drone.GetComponent<SurveyDroneItem>();
            if (survey == null)
                survey = drone.AddComponent<SurveyDroneItem>();
            survey.Initialize(index);
        }

        internal static bool TryGetSurveyTarget(int index, out Vector3 position, out float radius, out float floorY, out string label, out bool placed)
        {
            EnsureSurveyTargets();

            if (_surveyTargets != null && index >= 0 && index < _surveyTargets.Count)
            {
                var target = _surveyTargets[index];
                position = target.Position;
                radius = target.Radius;
                floorY = target.FloorY;
                label = target.Label;
                placed = target.Placed;
                return true;
            }

            position = Vector3.zero;
            radius = 0f;
            floorY = 0f;
            label = string.Empty;
            placed = false;
            return false;
        }

        internal static void NoteSurveyBeaconGrabbed()
        {
            _surveyBeaconGrabbed = true;
        }

        /// <summary>
        /// #774: the survey site that contains <paramref name="position"/> (flat radius, same
        /// floor) whose placed flag equals <paramref name="placed"/>. The nearest wins when
        /// sites overlap.
        /// </summary>
        internal static bool TryFindSurveyTargetAt(Vector3 position, bool placed, out int targetIndex)
        {
            EnsureSurveyTargets();
            targetIndex = -1;
            if (_surveyTargets == null)
                return false;

            float best = float.MaxValue;
            Vector2 flatPosition = new Vector2(position.x, position.z);
            for (int i = 0; i < _surveyTargets.Count; i++)
            {
                SurveyTarget target = _surveyTargets[i];
                if (target.Placed != placed)
                    continue;

                float flat = Vector2.Distance(flatPosition, new Vector2(target.Position.x, target.Position.z));
                if (flat > Mathf.Max(2f, target.Radius) || Mathf.Abs(position.y - target.FloorY) > 7f || flat >= best)
                    continue;

                best = flat;
                targetIndex = i;
            }

            return targetIndex >= 0;
        }

        /// <summary>
        /// #774: the open site a beacon should guide toward from <paramref name="from"/>. Flat
        /// distance plus a vertical penalty, so a same-floor site beats a closer one on another
        /// floor.
        /// </summary>
        internal static bool TryGetNearestOpenSurveyTarget(Vector3 from, out int targetIndex)
        {
            EnsureSurveyTargets();
            targetIndex = -1;
            if (_surveyTargets == null)
                return false;

            float best = float.MaxValue;
            for (int i = 0; i < _surveyTargets.Count; i++)
            {
                SurveyTarget target = _surveyTargets[i];
                if (target.Placed)
                    continue;

                float flat = Vector2.Distance(new Vector2(from.x, from.z), new Vector2(target.Position.x, target.Position.z));
                float score = flat + Mathf.Abs(from.y - target.FloorY) * 2.5f;
                if (score >= best)
                    continue;

                best = score;
                targetIndex = i;
            }

            return targetIndex >= 0;
        }

        internal static bool IsSurveyDroneInTarget(int index, Vector3 position)
        {
            if (!TryGetSurveyTarget(index, out Vector3 target, out float radius, out float floorY, out _, out bool placed) || placed)
                return false;

            Vector2 flatPosition = new Vector2(position.x, position.z);
            Vector2 flatTarget = new Vector2(target.x, target.z);
            return Vector2.Distance(flatPosition, flatTarget) <= Mathf.Max(2f, radius)
                && Mathf.Abs(position.y - floorY) <= 7f;
        }

        internal static void MarkSurveyDronePlaced(int index)
        {
            MarkSurveyDronePlaced(index, ulong.MaxValue);
        }

        /// <summary>
        /// Every peer detects the drone settling into its target independently, so the mark stays
        /// local while the progression event is published host-side once and mirrored out.
        /// </summary>
        internal static void MarkSurveyDronePlaced(int index, ulong placedByClientId)
        {
            if (_surveyTargets == null || index < 0 || index >= _surveyTargets.Count)
                return;

            var target = _surveyTargets[index];
            if (target.Placed)
                return;

            target.Placed = true;
            _surveyTargets[index] = target;
            int placedCount = _surveyTargets.Count(targetInfo => targetInfo.Placed);
            MoonContractRoundStats.RecordSurveyDronePlaced(placedCount, _surveyTargets.Count);
            if (placedByClientId == ulong.MaxValue)
            {
                // Crew-wide payout: nobody is known to have placed this drone, so every id carries
                // the '.crew.' segment and a consumer counting personal placements can reject it.
                ContractProgressionEvents.PublishSurveyDronePlacedToAll(index, placedCount, target.Label);
            }
            else
            {
                ContractProgressionEvents.PublishSurveyDronePlaced(placedByClientId, index, placedCount, target.Label);
            }

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Survey beacon {index + 1} deployed in {target.Label}.");
        }

        private static bool AnyPlayerInsideFactory()
        {
            var players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || players.Length == 0)
                return false;

            foreach (var player in players)
            {
                if (player != null && player.isPlayerControlled && !player.isPlayerDead && player.isInsideFactory)
                    return true;
            }
            return false;
        }

        private static string FormatCountdown(int seconds)
        {
            seconds = Mathf.Max(0, seconds);
            int minutes = seconds / 60;
            int remainder = seconds % 60;
            return $"{minutes:00}:{remainder:00}";
        }

        private static MoonContractType PickContractForLevel(SelectableLevel level, int cycleKey)
        {
            if (!CanReceiveContract(level))
                return MoonContractType.None;

            if (ContractsBootstrap.ShadowRaidForceAllMoonsContract != null && ContractsBootstrap.ShadowRaidForceAllMoonsContract.Value)
            {
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Forcing Shadow Raid contract on {level.PlanetName}.");
                return MoonContractType.ShadowRaid;
            }

            if (ContractsBootstrap.DefuseBalance != null && ContractsBootstrap.DefuseBalance.ForceAllMoons)
            {
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Defuse] Forcing Defuse contract on {level.PlanetName}.");
                return MoonContractType.Defuse;
            }

            int seed = cycleKey ^ (level.levelID * 73856093) ^ StableHash(level.PlanetName);
            var random = new System.Random(seed);
            var weightedPool = BuildWeightedContractPool(level);
            if (weightedPool.Count == 0)
                return MoonContractType.None;

            int totalWeight = weightedPool.Sum(entry => entry.Value);
            if (totalWeight <= 0)
                return MoonContractType.None;

            int roll = random.Next(0, totalWeight);
            int cursor = 0;
            for (int i = 0; i < weightedPool.Count; i++)
            {
                cursor += weightedPool[i].Value;
                if (roll < cursor)
                    return weightedPool[i].Key;
            }

            return weightedPool[weightedPool.Count - 1].Key;
        }

        private static bool IsContractAssignableForLevel(SelectableLevel level, MoonContractType contract)
        {
            if (contract == MoonContractType.None)
                return true;
            if (!CanReceiveContract(level))
                return false;
            ContractEligibilityDecision eligibility = EvaluateContractEligibility(level, contract, includeWeight: false);
            if (!eligibility.Allowed)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Eligibility] {contract} rejected on '{level?.PlanetName}': {eligibility.Reason} ({eligibility.Detail}).");
                return false;
            }
            if (contract == MoonContractType.BlackoutAudit)
                return IsBlackoutAssignableForLevel(level);
            // #992: native Defuse is suspended by the shared capability gate above.
            // LGU Defusal follows ordinary eligibility, regardless of the saved preference.
            return true;
        }

        private static List<KeyValuePair<MoonContractType, int>> BuildWeightedContractPool(SelectableLevel level)
        {
            Dictionary<MoonContractType, int> weights = BuildConfiguredContractWeights(level);
            var weightedPool = new List<KeyValuePair<MoonContractType, int>>();
            foreach (var pair in weights)
            {
                if (pair.Value <= 0)
                    continue;
                if (!IsContractAssignableForLevel(level, pair.Key))
                    continue;

                weightedPool.Add(new KeyValuePair<MoonContractType, int>(pair.Key, pair.Value));
            }

            return weightedPool;
        }

        private static Dictionary<MoonContractType, int> BuildConfiguredContractWeights(SelectableLevel level)
        {
            Dictionary<MoonContractType, int> weights = ContractsBootstrap.ContractSelection != null
                ? ContractsBootstrap.ContractSelection.BuildBaseWeights()
                : ContractConfigRules.ParseContractWeights(ContractConfigRules.DefaultContractWeights);
            if (weights.Count == 0)
                weights = ContractPool.GroupBy(type => type).ToDictionary(group => group.Key, group => group.Count());

            // Additive migration: a DefaultContractWeights config saved before Payload existed won't
            // list it, and BepInEx keeps the stale saved value over the new code default — which would
            // silently exclude Payload from the pool. Default it to enabled unless explicitly set (incl. =0).
            if (!weights.ContainsKey(MoonContractType.Payload))
                weights[MoonContractType.Payload] = 1;
            if (!weights.ContainsKey(MoonContractType.PestControl))
                weights[MoonContractType.PestControl] = 1;

            if (!weights.ContainsKey(MoonContractType.WasteDisposal))
                weights[MoonContractType.WasteDisposal] = 1;

            // #803: same additive migration for the five LGU-backed directives. A weight string
            // saved before they existed must not silently exclude them; the LGU capability gate
            // in IsContractCapabilityAvailable, not a zero weight, is what keeps them out of the
            // pool for a profile without that mod.
            if (!weights.ContainsKey(MoonContractType.LguDataRetrieval))
                weights[MoonContractType.LguDataRetrieval] = 1;
            if (!weights.ContainsKey(MoonContractType.LguExtermination))
                weights[MoonContractType.LguExtermination] = 1;
            if (!weights.ContainsKey(MoonContractType.LguExtraction))
                weights[MoonContractType.LguExtraction] = 1;
            if (!weights.ContainsKey(MoonContractType.LguExorcism))
                weights[MoonContractType.LguExorcism] = 1;
            if (!weights.ContainsKey(MoonContractType.LguDefusal))
                weights[MoonContractType.LguDefusal] = 1;

            ApplyConstellationWeightOverrides(level, weights);
            ApplyMoonWeightOverrides(level, weights);
            return weights;
        }

        private static void ApplyMoonWeightOverrides(SelectableLevel level, Dictionary<MoonContractType, int> weights)
        {
            if (level == null || weights == null)
                return;

            Dictionary<string, Dictionary<MoonContractType, int>> overrides =
                ContractConfigRules.ParseScopedContractWeights(ContractsBootstrap.ContractMoonWeights?.Value);
            foreach (var pair in overrides)
            {
                if (!ContractConfigRules.NameMatches(level.PlanetName, pair.Key))
                    continue;

                ApplyWeightOverrides(weights, pair.Value);
            }
        }

        private static void ApplyConstellationWeightOverrides(SelectableLevel level, Dictionary<MoonContractType, int> weights)
        {
            if (level == null || weights == null)
                return;

            Dictionary<string, Dictionary<MoonContractType, int>> overrides =
                ContractConfigRules.ParseScopedContractWeights(ContractsBootstrap.ContractConstellationWeights?.Value);
            if (overrides.Count == 0)
                return;

            IReadOnlyList<string> constellations = GetConstellationKeysForLevel(level);
            for (int i = 0; i < constellations.Count; i++)
            {
                string constellation = constellations[i];
                foreach (var pair in overrides)
                {
                    if (!ContractConfigRules.NameMatches(constellation, pair.Key))
                        continue;

                    ApplyWeightOverrides(weights, pair.Value);
                }
            }
        }

        private static void ApplyWeightOverrides(
            Dictionary<MoonContractType, int> weights,
            Dictionary<MoonContractType, int> overrides)
        {
            if (weights == null || overrides == null)
                return;

            foreach (var pair in overrides)
                weights[pair.Key] = Mathf.Max(0, pair.Value);
        }

        private static ContractEligibilityDecision EvaluateContractEligibility(
            SelectableLevel level,
            MoonContractType contract,
            string interiorName = null,
            bool includeWeight = true)
        {
            if (level == null || contract == MoonContractType.None)
                return new ContractEligibilityDecision(true, ContractEligibilityReason.Allowed);

            ContractSelectionProfile profile = ContractsBootstrap.ContractSelection?.Get(contract);
            IReadOnlyList<string> constellations = GetConstellationKeysForLevel(level);
            string constellationPath = Path.Combine(Paths.ConfigPath, "com.github.darmuh.LethalConstellations.cfg");
            int completedQuotas = TimeOfDay.Instance != null ? Math.Max(0, TimeOfDay.Instance.timesFulfilledQuota) : 0;
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            if (!Y4NGZCore.Modules.Quota.QuotaUnlockGates.IsFeatureUnlocked(
                    Y4NGZCore.Modules.Quota.QuotaUnlockCategories.Assignment,
                    contract.ToString(),
                    completedQuotas))
                return new ContractEligibilityDecision(false, ContractEligibilityReason.QuotaLocked, "quota progression milestone has not unlocked this contract");
#endif
            return ContractEligibilityService.Evaluate(
                profile,
                completedQuotas,
                level.PlanetName,
                constellations,
                File.Exists(constellationPath),
                interiorName,
                capabilityAvailable: MoonContractLguBridge.IsContractCapabilityAvailable(contract),
                includeWeight: includeWeight);
        }

        private static IReadOnlyList<string> GetConstellationKeysForLevel(SelectableLevel level)
        {
            if (level == null)
                return Array.Empty<string>();

            Dictionary<string, List<string>> map = LoadConstellationMoonMap();
            if (map.Count == 0)
                return Array.Empty<string>();

            var result = new List<string>();
            foreach (var pair in map)
            {
                List<string> moons = pair.Value;
                if (moons == null || moons.Count == 0)
                    continue;

                if (moons.Any(moon => ContractConfigRules.NameMatches(level.PlanetName, moon)))
                    result.Add(pair.Key);
            }

            return result;
        }

        private static Dictionary<string, List<string>> LoadConstellationMoonMap()
        {
            string path = Path.Combine(Paths.ConfigPath, "com.github.darmuh.LethalConstellations.cfg");
            DateTime writeTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_constellationMoonMap != null && writeTime == _constellationMoonMapWriteTimeUtc)
                return _constellationMoonMap;

            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(path))
                {
                    foreach (string rawLine in File.ReadAllLines(path))
                    {
                        string line = rawLine.Trim();
                        if (!line.StartsWith("ManualSetupListing", StringComparison.OrdinalIgnoreCase))
                            continue;

                        int equals = line.IndexOf('=');
                        if (equals < 0 || equals >= line.Length - 1)
                            continue;

                        string value = line.Substring(equals + 1).Trim();
                        foreach (string segment in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int colon = segment.IndexOf(':');
                            if (colon < 0 || colon >= segment.Length - 1)
                                continue;

                            string constellation = segment.Substring(0, colon).Trim();
                            var moons = segment.Substring(colon + 1)
                                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(moon => moon.Trim())
                                .Where(moon => !string.IsNullOrWhiteSpace(moon))
                                .ToList();

                            if (string.IsNullOrWhiteSpace(constellation) || moons.Count == 0)
                                continue;

                            result[ContractConfigRules.NormalizeLookupKey(constellation)] = moons;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Failed to read LethalConstellations config for contract weights: {e.Message}");
            }

            _constellationMoonMap = result;
            _constellationMoonMapWriteTimeUtc = writeTime;
            return _constellationMoonMap;
        }

        private static SelectableLevel FindLevelById(int levelId)
        {
            var levels = StartOfRound.Instance?.levels;
            if (levels == null)
                return null;

            foreach (var level in levels)
            {
                if (level != null && level.levelID == levelId)
                    return level;
            }

            if (levelId >= 0 && levelId < levels.Length && levels[levelId] != null)
                return levels[levelId];

            return null;
        }

        private static int GetAssignmentKey(SelectableLevel level)
        {
            int index = FindLevelIndex(level);
            if (index >= 0)
                return index;

            return level != null ? level.levelID : -1;
        }

        private static int FindLevelIndex(SelectableLevel level)
        {
            var levels = StartOfRound.Instance?.levels;
            if (level == null || levels == null)
                return -1;

            for (int i = 0; i < levels.Length; i++)
            {
                if (ReferenceEquals(levels[i], level))
                    return i;
            }

            return -1;
        }

        private static string FormatLevelForLog(SelectableLevel level)
        {
            if (level == null)
                return "<none>";

            string planet = string.IsNullOrWhiteSpace(level.PlanetName)
                ? level.name ?? "<unnamed>"
                : level.PlanetName;

            return $"{planet} (index={FindLevelIndex(level)}, levelID={level.levelID}, asset='{level.name ?? "<unnamed>"}')";
        }

        private static string GetExternalContractAssignmentCaller()
        {
            try
            {
                var stack = new System.Diagnostics.StackTrace(2, false);
                var frames = stack.GetFrames();
                if (frames == null)
                    return "unknown";

                foreach (var frame in frames)
                {
                    MethodBase method = frame?.GetMethod();
                    Type type = method?.DeclaringType;
                    string typeName = type?.FullName ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(typeName))
                        continue;
                    if (typeName.StartsWith("LGUContractHUD.", StringComparison.Ordinal)
                        || typeName.StartsWith("Y4NGZCompany.Contracts.", StringComparison.Ordinal)
                        || typeName.StartsWith("Y4NGZCompany.Escalations.", StringComparison.Ordinal)
                        || typeName.StartsWith("Y4NGZCompany.Facility.", StringComparison.Ordinal)
                        || typeName.StartsWith("System.", StringComparison.Ordinal)
                        || typeName.StartsWith("HarmonyLib.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return $"{typeName}.{method.Name}";
                }
            }
            catch
            {
            }

            return "unknown";
        }

        private static MoonContractType ParseContractName(string contractName)
        {
            // #804: the save-file spellings live in ContractConfigRules now, beside the
            // config-weight spellings they have to agree with, where a pure-BCL regression
            // suite can hold the two to each other.
            string normalized = ContractConfigRules.NormalizeContractName(contractName);
            if (string.IsNullOrWhiteSpace(normalized))
                return MoonContractType.None;
            return Enum.TryParse(normalized, ignoreCase: true, out MoonContractType parsed)
                ? parsed
                : MoonContractType.None;
        }

        internal static bool TryBuildBlackoutApprovedDungeonPool(RoundManager round, out IntWithRarity[] pool, out string reason)
        {
            pool = null;
            reason = "no RoundManager/current level";
            if (round?.currentLevel == null || round.dungeonFlowTypes == null || round.dungeonFlowTypes.Length == 0)
                return false;

            var entries = new List<IntWithRarity>();
            var usedIds = new HashSet<int>();
            IntWithRarity[] original = round.currentLevel.dungeonFlowTypes;

            if (original == null || original.Length == 0)
            {
                AddBlackoutDungeonFlowEntry(round, entries, usedIds, 0, 300, null);
            }
            else
            {
                foreach (var entry in original)
                {
                    if (entry == null)
                        continue;

                    AddBlackoutDungeonFlowEntry(
                        round,
                        entries,
                        usedIds,
                        entry.id,
                        Mathf.Max(1, entry.rarity),
                        entry.overrideLevelAmbience);
                }
            }

            bool usedMoonNativePool = entries.Count > 0;
            if (!usedMoonNativePool)
            {
                for (int i = 0; i < round.dungeonFlowTypes.Length; i++)
                    AddBlackoutDungeonFlowEntry(round, entries, usedIds, i, 300, null);
            }

            if (entries.Count == 0)
            {
                reason = "no blackout-approved dungeon flows were found";
                return false;
            }

            string filterReason;
            entries = FilterDungeonFlowsForContract(round, MoonContractType.BlackoutAudit, entries, out filterReason);
            if (entries.Count == 0)
            {
                reason = $"all blackout-approved dungeon flows were filtered by contract interior config: {filterReason}";
                return false;
            }

            pool = entries.ToArray();
            string source = usedMoonNativePool ? "moon-native approved flow(s)" : "global approved fallback flow(s)";
            reason = $"{source}: {string.Join(", ", entries.Select(entry => GetDungeonFlowName(round, entry.id)))}";
            if (!string.IsNullOrWhiteSpace(filterReason))
                reason += $"; {filterReason}";
            return true;
        }

        internal static bool TryBuildShadowRaidApprovedDungeonPool(RoundManager round, out IntWithRarity[] pool, out string reason)
        {
            pool = null;
            reason = "no RoundManager/current level";
            if (round?.currentLevel == null || round.dungeonFlowTypes == null || round.dungeonFlowTypes.Length == 0)
                return false;

            IReadOnlyList<string> configuredFlows =
                AuthoredInteriorPlacementStore.GetConfiguredFlowNames(
                    AuthoredInteriorPlacementKinds.BlacksiteContainer);
            if (configuredFlows == null || configuredFlows.Count == 0)
            {
                reason = "no authored Shadow Raid placement flows are configured";
                return false;
            }

            var entries = new List<IntWithRarity>();
            var usedIds = new HashSet<int>();
            Dictionary<int, IntWithRarity> nativeEntries = BuildNativeDungeonFlowEntryMap(round.currentLevel.dungeonFlowTypes);

            for (int i = 0; i < round.dungeonFlowTypes.Length; i++)
            {
                nativeEntries.TryGetValue(i, out IntWithRarity nativeEntry);
                AddBreachDungeonFlowEntry(
                    round,
                    entries,
                    usedIds,
                    i,
                    nativeEntry != null ? Mathf.Max(1, nativeEntry.rarity) : 300,
                    nativeEntry?.overrideLevelAmbience,
                    configuredFlows);
            }

            if (entries.Count == 0)
            {
                reason = $"no dungeon flow matched authored Shadow Raid placement flow(s): {string.Join(", ", configuredFlows)}";
                return false;
            }

            string filterReason;
            entries = FilterDungeonFlowsForContract(round, MoonContractType.ShadowRaid, entries, out filterReason);
            if (entries.Count == 0)
            {
                reason = $"all authored Shadow Raid dungeon flows were filtered by contract interior config: {filterReason}";
                return false;
            }

            pool = entries.ToArray();
            int nativeCount = entries.Count(entry => nativeEntries.ContainsKey(entry.id));
            reason = $"authored Shadow Raid flow(s) native={nativeCount}, globalFallback={entries.Count - nativeCount}: {string.Join(", ", entries.Select(entry => GetDungeonFlowName(round, entry.id)))}";
            if (!string.IsNullOrWhiteSpace(filterReason))
                reason += $"; {filterReason}";
            return true;
        }

        internal static bool TryBuildDefuseApprovedDungeonPool(RoundManager round, out IntWithRarity[] pool, out string reason)
        {
            pool = null;
            reason = "no RoundManager/current level";
            if (round?.currentLevel == null || round.dungeonFlowTypes == null || round.dungeonFlowTypes.Length == 0)
                return false;

            IReadOnlyList<string> configuredFlows =
                AuthoredInteriorPlacementStore.GetConfiguredFlowNames(
                    AuthoredInteriorPlacementKinds.DeadDropBomb);
            if (configuredFlows == null || configuredFlows.Count == 0)
            {
                reason = "no authored Defuse placement flows are configured";
                return false;
            }

            var entries = new List<IntWithRarity>();
            var usedIds = new HashSet<int>();
            Dictionary<int, IntWithRarity> nativeEntries = BuildNativeDungeonFlowEntryMap(round.currentLevel.dungeonFlowTypes);

            foreach (var pair in nativeEntries)
            {
                IntWithRarity nativeEntry = pair.Value;
                AddBreachDungeonFlowEntry(
                    round,
                    entries,
                    usedIds,
                    pair.Key,
                    nativeEntry != null ? Mathf.Max(1, nativeEntry.rarity) : 1,
                    nativeEntry?.overrideLevelAmbience,
                    configuredFlows);
            }

            if (entries.Count == 0)
            {
                reason = $"no native dungeon flow matched authored Defuse placement flow(s): {string.Join(", ", configuredFlows)}; leaving moon interior pool unchanged";
                return false;
            }

            string filterReason;
            entries = FilterDungeonFlowsForContract(round, MoonContractType.Defuse, entries, out filterReason);
            if (entries.Count == 0)
            {
                reason = $"all native authored Defuse dungeon flows were filtered by contract interior config: {filterReason}; leaving moon interior pool unchanged";
                return false;
            }

            pool = entries.ToArray();
            reason = $"native authored Defuse flow(s): {string.Join(", ", entries.Select(entry => GetDungeonFlowName(round, entry.id)))}";
            if (!string.IsNullOrWhiteSpace(filterReason))
                reason += $"; {filterReason}";
            return true;
        }

        private static IReadOnlyList<string> CombineConfiguredFlowNames(params IReadOnlyList<string>[] sources)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sources == null)
                return result;

            for (int i = 0; i < sources.Length; i++)
            {
                IReadOnlyList<string> source = sources[i];
                if (source == null)
                    continue;

                for (int j = 0; j < source.Count; j++)
                {
                    string flow = source[j];
                    if (string.IsNullOrWhiteSpace(flow))
                        continue;

                    if (seen.Add(flow))
                        result.Add(flow);
                }
            }

            return result;
        }

        private static List<IntWithRarity> FilterDungeonFlowsForContract(
            RoundManager round,
            MoonContractType contract,
            List<IntWithRarity> entries,
            out string reason)
        {
            reason = string.Empty;
            if (entries == null || entries.Count == 0 || contract == MoonContractType.None)
                return entries ?? new List<IntWithRarity>();

            ContractSelectionProfile profile = ContractsBootstrap.ContractSelection?.Get(contract);
            bool hasWhitelist = profile != null && profile.InteriorAllows.Count > 0;

            var kept = new List<IntWithRarity>();
            var filtered = new List<string>();
            for (int i = 0; i < entries.Count; i++)
            {
                IntWithRarity entry = entries[i];
                if (entry == null)
                    continue;

                string flowName = GetDungeonFlowName(round, entry.id);
                SelectableLevel level = StartOfRound.Instance?.currentLevel;
                ContractEligibilityDecision eligibility = EvaluateContractEligibility(level, contract, flowName, includeWeight: false);
                if (!eligibility.Allowed)
                {
                    filtered.Add(flowName);
                    continue;
                }

                kept.Add(entry);
            }

            if (filtered.Count > 0 || hasWhitelist)
            {
                reason = $"interior config kept {kept.Count}/{entries.Count}";
                if (filtered.Count > 0)
                    reason += $" (filtered: {string.Join(", ", filtered.Distinct(StringComparer.OrdinalIgnoreCase))})";
            }

            return kept;
        }

        private static Dictionary<int, IntWithRarity> BuildNativeDungeonFlowEntryMap(IntWithRarity[] entries)
        {
            var result = new Dictionary<int, IntWithRarity>();
            if (entries == null)
                return result;

            for (int i = 0; i < entries.Length; i++)
            {
                IntWithRarity entry = entries[i];
                if (entry == null || entry.id < 0 || result.ContainsKey(entry.id))
                    continue;

                result.Add(entry.id, entry);
            }

            return result;
        }

        private static void AddBlackoutDungeonFlowEntry(
            RoundManager round,
            List<IntWithRarity> entries,
            HashSet<int> usedIds,
            int dungeonTypeId,
            int rarity,
            LevelAmbienceLibrary ambience)
        {
            if (round == null || usedIds.Contains(dungeonTypeId))
                return;

            if (!IsDungeonFlowBlackoutCapable(dungeonTypeId, out _))
                return;

            usedIds.Add(dungeonTypeId);
            entries.Add(new IntWithRarity(dungeonTypeId, Mathf.Max(1, rarity), ambience));
        }

        private static void AddBreachDungeonFlowEntry(
            RoundManager round,
            List<IntWithRarity> entries,
            HashSet<int> usedIds,
            int dungeonTypeId,
            int rarity,
            LevelAmbienceLibrary ambience,
            IReadOnlyList<string> configuredFlows)
        {
            if (round == null || usedIds.Contains(dungeonTypeId) || configuredFlows == null)
                return;
            if (round.dungeonFlowTypes == null || dungeonTypeId < 0 || dungeonTypeId >= round.dungeonFlowTypes.Length)
                return;

            string flowName = round.dungeonFlowTypes[dungeonTypeId]?.dungeonFlow?.name;
            if (string.IsNullOrWhiteSpace(flowName))
                return;

            bool configured = false;
            for (int i = 0; i < configuredFlows.Count; i++)
            {
                if (string.Equals(configuredFlows[i], flowName, StringComparison.OrdinalIgnoreCase))
                {
                    configured = true;
                    break;
                }
            }

            if (!configured)
                return;

            usedIds.Add(dungeonTypeId);
            entries.Add(new IntWithRarity(dungeonTypeId, Mathf.Max(1, rarity), ambience));
        }

        private static string GetDungeonFlowName(RoundManager round, int dungeonTypeId)
        {
            if (round?.dungeonFlowTypes == null || dungeonTypeId < 0 || dungeonTypeId >= round.dungeonFlowTypes.Length)
                return $"#{dungeonTypeId}";

            string name = round.dungeonFlowTypes[dungeonTypeId]?.dungeonFlow?.name;
            return string.IsNullOrWhiteSpace(name) ? $"#{dungeonTypeId}" : $"{name}#{dungeonTypeId}";
        }

        private static bool IsBlackoutAssignableForLevel(SelectableLevel level)
        {
            if (level == null)
                return false;

            if (!TryPredictDungeonType(level, out int dungeonTypeId))
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Blackout Audit unavailable on {level.PlanetName}: could not predict dungeon type.");
                return false;
            }

            bool capable = IsDungeonFlowBlackoutCapable(dungeonTypeId, out string reason);
            if (!capable)
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Blackout Audit unavailable on {level.PlanetName}: {reason}.");
            return capable;
        }

        private static bool TryPredictDungeonType(SelectableLevel level, out int dungeonTypeId)
        {
            dungeonTypeId = 0;
            if (level == null)
                return false;

            if (level.dungeonFlowTypes == null || level.dungeonFlowTypes.Length == 0)
                return true;

            int[] weights = level.dungeonFlowTypes.Select(flow => flow != null ? flow.rarity : 0).ToArray();
            int index = GetRandomWeightedIndex(weights, new System.Random(StartOfRound.Instance?.randomMapSeed ?? 0));
            if (index < 0 || index >= level.dungeonFlowTypes.Length || level.dungeonFlowTypes[index] == null)
                return false;

            dungeonTypeId = level.dungeonFlowTypes[index].id;
            return true;
        }

        private static int GetRandomWeightedIndex(int[] weights, System.Random random)
        {
            if (weights == null || weights.Length == 0)
                return -1;
            if (weights.Length == 1)
                return 0;

            int total = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i] >= 0)
                    total += weights[i];
            }

            if (total <= 0)
                return random.Next(0, weights.Length);

            float roll = (float)random.NextDouble();
            float cursor = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i] <= 0)
                    continue;

                cursor += weights[i] / (float)total;
                if (cursor >= roll)
                    return i;
            }

            return random.Next(0, weights.Length);
        }

        private static bool IsDungeonFlowBlackoutCapable(int dungeonTypeId, out string reason)
        {
            reason = "unknown dungeon flow";
            var round = RoundManager.Instance;
            if (round?.dungeonFlowTypes == null || dungeonTypeId < 0 || dungeonTypeId >= round.dungeonFlowTypes.Length)
                return false;

            var mapType = round.dungeonFlowTypes[dungeonTypeId];
            var flow = mapType?.dungeonFlow;
            if (flow == null)
                return false;

            var stats = new BlackoutFlowStats();
            ScanDungeonFlowForPoweredLights(flow, stats);

            reason = $"{flow.name} has {stats.PoweredLightPrefabs} powered light prefab(s), {stats.PoweredLightAnimators} animator(s), {stats.PoweredLights} light(s)";
            return stats.PoweredLightPrefabs > 0
                && (stats.PoweredLightAnimators > 0 || stats.PoweredLights > 0);
        }

        private static void ScanDungeonFlowForPoweredLights(DunGen.Graph.DungeonFlow flow, BlackoutFlowStats stats)
        {
            if (flow == null)
                return;

            if (flow.Lines != null)
            {
                foreach (var line in flow.Lines)
                {
                    if (line?.DungeonArchetypes == null)
                        continue;

                    foreach (var archetype in line.DungeonArchetypes)
                        ScanArchetypeForPoweredLights(archetype, stats);
                }
            }

            if (flow.TileInjectionRules != null)
            {
                foreach (var rule in flow.TileInjectionRules)
                {
                    if (rule != null)
                        ScanTileSetForPoweredLights(rule.TileSet, stats);
                }
            }
        }

        private static void ScanArchetypeForPoweredLights(DungeonArchetype archetype, BlackoutFlowStats stats)
        {
            if (archetype == null)
                return;

            ScanTileSetsForPoweredLights(archetype.TileSets, stats);
            ScanTileSetsForPoweredLights(archetype.BranchStartTileSets, stats);
            ScanTileSetsForPoweredLights(archetype.BranchCapTileSets, stats);
        }

        private static void ScanTileSetsForPoweredLights(IEnumerable<TileSet> tileSets, BlackoutFlowStats stats)
        {
            if (tileSets == null)
                return;

            foreach (var tileSet in tileSets)
                ScanTileSetForPoweredLights(tileSet, stats);
        }

        private static void ScanTileSetForPoweredLights(TileSet tileSet, BlackoutFlowStats stats)
        {
            if (tileSet?.TileWeights?.Weights == null)
                return;

            foreach (var chance in tileSet.TileWeights.Weights)
            {
                GameObject prefab = chance?.Value;
                if (prefab == null || !stats.SeenPrefabs.Add(prefab))
                    continue;

                CountPrefabPoweredLights(prefab, stats);
            }
        }

        private static void CountPrefabPoweredLights(GameObject prefab, BlackoutFlowStats stats)
        {
            if (prefab == null)
                return;

            bool prefabHasPoweredLight = false;
            Transform[] children = prefab.GetComponentsInChildren<Transform>(true);
            foreach (var child in children)
            {
                if (child == null || !HasTag(child.gameObject, "PoweredLight"))
                    continue;

                prefabHasPoweredLight = true;
                if (child.GetComponentInChildren<Animator>(true) != null)
                    stats.PoweredLightAnimators++;

                var lights = child.GetComponentsInChildren<Light>(true);
                foreach (var light in lights)
                {
                    if (light != null && !HasTag(light.gameObject, "IndirectLightSource"))
                        stats.PoweredLights++;
                }
            }

            if (prefabHasPoweredLight)
                stats.PoweredLightPrefabs++;
        }

        private static bool HasTag(GameObject obj, string tag)
        {
            if (obj == null)
                return false;

            try
            {
                return obj.CompareTag(tag);
            }
            catch
            {
                return false;
            }
        }

        private static bool CanReceiveContract(SelectableLevel level)
        {
            if (level == null || !level.spawnEnemiesAndScrap)
                return false;

            string name = level.PlanetName ?? string.Empty;
            if (name.IndexOf("Gordion", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (name.IndexOf("Company", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (name.IndexOf("Shutter", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (name.IndexOf("Liquidation", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            return true;
        }

        private static bool ShouldDisplayLiteralNone(SelectableLevel level)
        {
            string name = level?.PlanetName ?? string.Empty;
            return name.IndexOf("Gordion", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Company", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Shutter", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int BuildCycleKey(StartOfRound start)
        {
            if (start == null)
                return 0;

            int quota = TimeOfDay.Instance != null ? TimeOfDay.Instance.timesFulfilledQuota : 0;
            int daysLeft = TimeOfDay.Instance != null ? TimeOfDay.Instance.daysUntilDeadline : 0;
            int quotaTarget = TimeOfDay.Instance != null ? TimeOfDay.Instance.profitQuota : 0;

            unchecked
            {
                int seed = 1729;
                seed = (seed * 397) ^ quota;
                seed = (seed * 397) ^ daysLeft;
                seed = (seed * 397) ^ quotaTarget;
                return seed;
            }
        }

        private static int BuildAssignmentSeed(StartOfRound start, int cycleKey)
        {
            unchecked
            {
                int seed = cycleKey;
                seed = (seed * 397) ^ (start != null ? start.randomMapSeed : 0);

                string saveName = GameNetworkManager.Instance != null
                    ? GameNetworkManager.Instance.currentSaveFileName
                    : null;
                seed = (seed * 397) ^ StableHash(saveName);

                return seed;
            }
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = 23;
                if (value == null)
                    return hash;

                for (int i = 0; i < value.Length; i++)
                    hash = hash * 31 + value[i];

                return hash;
            }
        }
    }
}
