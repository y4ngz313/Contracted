using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DunGen;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.BlackoutAudit;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Contracts.Lgu;
using Y4NGZCompany.Contracts.ObjectiveHints;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
using Y4NGZCompany.Escalations._Shared;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Experience.UITheme;
using Y4NGZCompany.Core.Compatibility;
using Y4NGZCore.Modules.Monitor;
namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #802. Hands the destination's Contracted-assigned contract to Lategame Upgrades so LGU
    /// spawns and plays it.
    ///
    /// The apply has to land before the floor generates: LGU's map objects spawn during
    /// generation and <c>ContractObject.Start</c> despawns each one whose type and moon do not
    /// match the manager, so the <c>StartOfRound.StartGame</c> prefix is the last safe point.
    /// The route-change call keeps the manager honest in orbit as well, which is what LGU's own
    /// terminal commands read.
    ///
    /// Host-only throughout: <see cref="LategameUpgradesCompat"/> replicates the assignment to
    /// clients with LGU's own ClientRpc, and clients must not write it themselves.
    /// </summary>
    internal static class MoonContractLguBridge
    {
        /// <summary>Hard stop on the retry, in case the generation signal never arrives.</summary>
        private const float PendingApplyTimeoutSeconds = 30f;

        private static string _pendingPlanet;
        private static string _pendingType;
        private static string _pendingReason;
        private static float _pendingDeadline;

        /// <summary>True once <c>dungeonFinishedGeneratingForAllPlayers</c> has been seen false
        /// since the pending apply began. Until then the flag is the *previous* round's, still
        /// true from the last landing, and honouring it would abort the retry on frame one.</summary>
        private static bool _pendingSawGenerationReset;

        /// <summary>The one assignment this bridge wrote, so a clear can never take an
        /// assignment somebody else paid for. Null when the bridge holds nothing.</summary>
        private static string _ownedPlanet;
        private static string _ownedType;

        /// <summary>
        /// #803. The fixed Contracted-to-LGU contract-type mapping, with no seam and no side
        /// effects, so the selection pool can ask it once per candidate. The five strings are
        /// spelled exactly as LGU spells them in <c>CommandParser.contracts</c>; anything else
        /// LGU silently ignores. Null means "not LGU-backed".
        /// </summary>
        internal static string LguTypeStringFor(MoonContractType contract)
        {
            switch (contract)
            {
                case MoonContractType.LguDataRetrieval:
                    return "Data";
                case MoonContractType.LguExtermination:
                    return "Exterminator";
                case MoonContractType.LguExtraction:
                    return "Extraction";
                case MoonContractType.LguExorcism:
                    return "Exorcism";
                case MoonContractType.LguDefusal:
                    return "Defusal";
                default:
                    return null;
            }
        }

        /// <summary>#803. True when Lategame Upgrades, not Contracted, owns this contract's
        /// spawning and play. #804 and #805 branch on it.</summary>
        internal static bool IsLguBacked(MoonContractType contract) => LguTypeStringFor(contract) != null;

        /// <summary>
        /// #803. The runtime capability a contract needs before it can roll. Only the LGU-backed
        /// directives have one: Lategame Upgrades must be chainloaded, its members must have
        /// resolved, and it must still offer that contract type - LGU deletes disabled types
        /// from its own list, so a host who turned Exorcism off in LGU never sees it here
        /// either. Native Defuse is temporarily unavailable (#992).
        ///
        /// #804: this is the single answer. The eligibility gate in <c>MoonContractState</c> and
        /// the Defuse balance audit line both call it; they used to carry a copy each, and two
        /// copies of a three-clause availability rule is one copy too many.
        /// </summary>
        internal static bool IsContractCapabilityAvailable(MoonContractType contract)
        {
            // #992: keep the implementation and tuning, but suspend all native assignments.
            if (contract == MoonContractType.Defuse)
                return false;

            string lguType = LguTypeStringFor(contract);
            if (lguType == null)
                return true;

            return LategameUpgradesCompat.IsLoaded
                   && LategameUpgradesCompat.IsUsable
                   && LategameUpgradesCompat.IsContractTypeAvailable(lguType);
        }

        /// <summary>
        /// Pushes the destination's assignment into LGU. <paramref name="allowRetry"/> is the
        /// difference between the two callers: the <c>StartGame</c> prefix is the authoritative
        /// apply and keeps retrying until LGU's manager is spawned, while a route change only
        /// refreshes what is already there and does not arm a retry for a landing that may
        /// never happen.
        /// </summary>
        internal static void ApplyCurrentContractToLGU(string reason, bool allowRetry = true)
        {
            ClearPending();
            if (!LategameUpgradesCompat.IsLoaded)
                return;

            if (!LategameUpgradesCompat.IsUsable)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.LGU] Bridge skipped; LGU members did not resolve ({reason}).");
                return;
            }

            if (!NetRole.IsServerOrHost())
                return;

            SelectableLevel destination = StartOfRound.Instance?.currentLevel;
            if (destination == null)
                return;

            string planet = destination.PlanetName;
            string lguType = LguTypeStringFor(MoonContractState.GetContractForLevel(destination));
            bool hasActive = LategameUpgradesCompat.TryGetActiveContract(out string activePlanet, out string activeType);

            if (!string.IsNullOrEmpty(lguType) && LategameUpgradesCompat.IsContractTypeAvailable(lguType))
            {
                // Idempotent on purpose. LGU's SyncContractDetailsClientRpc rewrites its save
                // file on every peer that receives it, so re-sending the assignment the manager
                // already holds would be one disk write per moon the player browses past.
                if (hasActive
                    && string.Equals(activePlanet, planet, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(activeType, lguType, StringComparison.OrdinalIgnoreCase))
                {
                    RecordOwnership(planet, lguType);
                    return;
                }

                if (LategameUpgradesCompat.TryApplyContract(planet, lguType, reason))
                    RecordOwnership(planet, lguType);
                else if (allowRetry)
                    BeginPending(planet, lguType, reason);
                return;
            }

            // Not LGU-backed. Clearing takes something away, so it is allowed only for the one
            // assignment this bridge wrote itself: with the shop suppression off, a crew can
            // buy an LGU contract with their own credits, and a purchase made for this same
            // moon must survive the route. An assignment for anywhere else is LGU's business
            // either way.
            if (!hasActive || !OwnsCurrentAssignment(activePlanet, activeType))
                return;

            if (!string.Equals(activePlanet, planet, StringComparison.OrdinalIgnoreCase))
                return;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.LGU] Clearing the '{activeType}' contract this bridge assigned on {planet}; Contracted's assignment is not LGU-backed ({reason}).");
            if (LategameUpgradesCompat.TryClearContract(reason))
                ForgetOwnership();
        }

        private static void RecordOwnership(string planet, string lguType)
        {
            _ownedPlanet = planet;
            _ownedType = lguType;
        }

        private static void ForgetOwnership()
        {
            _ownedPlanet = null;
            _ownedType = null;
        }

        /// <summary>True when what LGU holds right now is exactly what this bridge last wrote.
        /// Anything else - a shop purchase, a terminal command, a later bridge write that was
        /// overwritten - is not the bridge's to clear.</summary>
        private static bool OwnsCurrentAssignment(string activePlanet, string activeType)
        {
            return _ownedType != null
                && string.Equals(_ownedPlanet, activePlanet, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_ownedType, activeType, StringComparison.OrdinalIgnoreCase);
        }

        private static void BeginPending(string planet, string lguType, string reason)
        {
            _pendingPlanet = planet;
            _pendingType = lguType;
            _pendingReason = reason;
            _pendingDeadline = Time.realtimeSinceStartup + PendingApplyTimeoutSeconds;
            _pendingSawGenerationReset = false;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.LGU] LGU's contract manager was not ready; retrying '{lguType}' on {planet} until the floor finishes generating ({reason}).");
        }

        private static void ClearPending()
        {
            _pendingPlanet = null;
            _pendingType = null;
            _pendingReason = null;
            _pendingDeadline = 0f;
            _pendingSawGenerationReset = false;
        }

        /// <summary>
        /// Bounded retry, driven from the per-frame contract runtime tick. Gives up once the
        /// floor has finished generating - past that point LGU's objects have already been
        /// despawned and a late write would only mislead the terminal - or after
        /// <see cref="PendingApplyTimeoutSeconds"/> if that signal never arrives.
        /// </summary>
        internal static void TickPendingApply()
        {
            if (_pendingType == null)
                return;

            if (!NetRole.IsServerOrHost())
            {
                ClearPending();
                return;
            }

            if (LategameUpgradesCompat.TryApplyContract(_pendingPlanet, _pendingType, _pendingReason + " (retry)"))
            {
                RecordOwnership(_pendingPlanet, _pendingType);
                ClearPending();
                return;
            }

            // The generated flag is latched, not read raw. StartGame's prefix runs while the
            // previous round's dungeonFinishedGeneratingForAllPlayers is still true, so an
            // unlatched read would end the retry on its first frame - before LGU's manager has
            // any chance to spawn. Only a flag that has been observed false since this pending
            // apply began means "this round's generation", and the 30 s deadline still bounds
            // the case where that observation never happens.
            RoundManager round = RoundManager.Instance;
            bool flag = round != null && round.dungeonFinishedGeneratingForAllPlayers;
            if (!flag)
                _pendingSawGenerationReset = true;

            bool generated = flag && _pendingSawGenerationReset;
            if (!generated && Time.realtimeSinceStartup < _pendingDeadline)
                return;

            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.LGU] Gave up assigning '{_pendingType}' on {_pendingPlanet}: LGU's contract manager never became ready "
                + (generated ? "before the floor finished generating" : "within the retry window")
                + $" ({_pendingReason}).");
            ClearPending();
        }
    }

    [HarmonyPatch(typeof(GrabbableObject), "GrabItem")]
    internal static class MoonContractScrapGrabStatsPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(GrabbableObject __instance)
        {
            BreachHardDriveItem drive = __instance != null
                ? __instance.GetComponent<BreachHardDriveItem>()
                : null;
            return drive == null
                || drive.SoldierCarrierNetworkId == 0UL && !drive.Extracted;
        }

        [HarmonyPostfix]
        private static void Postfix(GrabbableObject __instance)
        {
            BreachHardDriveItem drive = __instance != null
                ? __instance.GetComponent<BreachHardDriveItem>()
                : null;
            if (drive != null
                && (drive.SoldierCarrierNetworkId != 0UL || drive.Extracted))
            {
                return;
            }
            MoonContractRoundStats.RecordScrapGrabbed(__instance);
        }
    }

    [HarmonyPatch]
    internal static class BlacksiteSoldierHeldDriveGrabGuardPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(PlayerControllerB),
                "GrabObjectServerRpc",
                new[] { typeof(NetworkObjectReference) });
        }

        private static bool Prefix(NetworkObjectReference grabbedObject)
        {
            NetworkObjectReference reference = grabbedObject;
            if (!reference.TryGet(out NetworkObject networkObject) || networkObject == null)
                return true;
            BreachHardDriveItem drive = networkObject.GetComponent<BreachHardDriveItem>();
            return drive == null
                || drive.SoldierCarrierNetworkId == 0UL && !drive.Extracted;
        }
    }

    [HarmonyPatch(typeof(StartOfRound))]
    internal static class MoonContractStartOfRoundPatches
    {
        [HarmonyPatch("SetShipReadyToLand")]
        [HarmonyPostfix]
        private static void SetShipReadyToLand_Postfix()
        {
            // #1203: before the reset, so the preview's saved ledger is handed back and then
            // cleared with the rest of the round rather than restored over the new one.
            PerformanceReportPreview.CloseIfOpen("ship ready to land");
            MoonContractRoundStats.Reset("ship ready to land");
            MoonContractState.ResetRunState();
            // New orbit: release the previous route's contract lock and roll a
            // fresh assignment table for the day.
            MoonContractState.RefreshAssignments("ship ready to land", force: true, clearLocks: true);
        }

        [HarmonyPatch("ChangeLevel")]
        [HarmonyPostfix]
        private static void ChangeLevel_Postfix()
        {
            MoonContractState.RefreshAssignments("level route changed");
            // Pin the destination's currently-displayed contract on every route
            // path (vanilla `route`, nav mods), not just LGUTerminal's
            // AttemptRoute, so landing reads the same contract the player saw.
            SelectableLevel destination = StartOfRound.Instance?.currentLevel;
            if (destination != null)
                MoonContractState.LockContractForLevelId(destination.levelID, "level route changed");
            // Refresh only: keeps LGU's terminal commands honest in orbit, but does not arm the
            // retry - StartGame is the authoritative apply, and the assignment is idempotent so
            // browsing moons costs no LGU save writes.
            MoonContractLguBridge.ApplyCurrentContractToLGU("route changed", allowRetry: false);
        }

        [HarmonyPatch("StartGame")]
        [HarmonyPrefix]
        private static void StartGame_Prefix()
        {
            PerformanceReportPreview.CloseIfOpen("start game");
            MoonContractRoundStats.Reset("start game");
            MoonContractLguBridge.ApplyCurrentContractToLGU("start game");
            MoonContractState.PrimeCurrentContractForRound();
            MoonContractRoundEndState.CaptureRoundStartContractSnapshot("start game");
            // #451: nothing from the previous round's presentation may survive into
            // this one. Idempotent — the discard path inside the snapshot capture
            // normally covers it, but a landing is the last point at which a stuck
            // HUD is still recoverable without a restart.
            ContractPerformanceReportUi.ForceRestoreRoundEndHudSuppression("start game");
            // Absolute last resort: a takeover interrupted by a scene change can
            // leave claims behind too. Landing is takeover-free by construction, so
            // anything the registry still holds here is a leak, and one log line
            // proves whether there was one.
            // #610: a capability question, not a feature question. "Is anything at least as
            // important as a monitor takeover on screen right now" is answerable by Core with
            // no reference to the takeover's assembly, and reads false in a profile that does
            // not ship one — which is the right answer, not merely a safe one.
            if (!MonitorArbiter.IsAnyHeldAtOrAbove(MonitorPriority.QuotaTakeover))
                HudSuppressionRegistry.ForceRestoreAll("start game");
        }

        [HarmonyPatch("ShipLeave")]
        [HarmonyPostfix]
        private static void ShipLeave_Postfix()
        {
            PerformanceReportPreview.CloseIfOpen("ship leave");
            MoonContractRoundEndState.CaptureCurrentOutcomeIfMissing("ship leave");
            // #540: the outcome above is captured first, so the round is already decided by the
            // time the escalations are told to hold. Their entities stay on the moon until
            // vanilla's own sweep clears it (MoonContractEscalationDepartureSweepPatch below)
            // rather than blinking out on the frame the lever's RPC lands.
            MoonContractState.ResetRunState(deferEntityTeardown: true);
        }

        // #1203: the debug report preview hands its borrowed state back (the XP ledger above
        // all) before any plugin records the real round into it. Upgrades records its ledger
        // snapshot from a FillEndGameStats prefix and finalizes from inside
        // EndOfGameClientRpc, with no order pinned against Company's own prefixes, so these
        // closes run first rather than sharing the round-end prefixes further down. Each is
        // a no-op when no preview is open.
        private const string UpgradesPluginGuid = "com.y4ngz.upgrades";

        [HarmonyPatch("EndOfGameClientRpc")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyBefore(UpgradesPluginGuid)]
        private static void EndOfGameClientRpc_ClosePreview()
        {
            PerformanceReportPreview.CloseIfOpen("end game rpc");
        }

        [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.FillEndGameStats))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyBefore(UpgradesPluginGuid)]
        private static void FillEndGameStats_ClosePreview()
        {
            PerformanceReportPreview.CloseIfOpen("fill endgame stats");
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Disconnect_ClosePreview()
        {
            PerformanceReportPreview.CloseIfOpen("disconnect");
        }
    }

    [HarmonyPatch(typeof(RoundManager), "GenerateNewFloor")]
    internal static class BlackoutDungeonFlowPatch
    {
        private static SelectableLevel _overrideLevel;
        private static IntWithRarity[] _originalFlows;
        /// <summary>Map seed the floor-generation gate line was last logged for (#716/F5).</summary>
        private static int _floorGenerationGateLoggedSeed = int.MinValue;

        [HarmonyPrefix]
        private static void Prefix(RoundManager __instance)
        {
            Restore();

            try
            {
                AuthoredPlacementCoverageReport.WriteReport("before floor generation");
                if (__instance == null || __instance.currentLevel == null)
                    return;

                // Mainframe and Company Stash authored placements are post-generation
                // candidates only. Do not force their tiles into the dungeon flow.

                MoonContractType contract = MoonContractState.GetCurrentContract();
                // #446: this line used to carry only the contract context, which left the
                // generation inputs invisible right where the Session 3 blackout starts. The
                // flow itself is not picked until later inside GenerateNewFloor (vanilla rolls
                // it from currentLevel.dungeonFlowTypes, and LethalLevelLoader's transpiler
                // writes the size scalar after this prefix), so the flow name, final DungeonSize
                // and tile-set/branch counts land in the companion
                // "[MoonContracts.GenWatch] Floor generation parameters resolved" line emitted
                // from DungeonGenerator.Generate. What is already known here is logged here.
                DungeonGenerator generator = __instance.dungeonGenerator != null
                    ? __instance.dungeonGenerator.Generator
                    : null;
                // #716/F5: GenerateNewFloor re-enters on every generation attempt, so this
                // eight-field line used to repeat several times per landing. It stays at Info
                // — it is the anchor for interior investigations — but only once per round,
                // keyed on the map seed that changes with each new round.
                int mapSeed = StartOfRound.Instance != null ? StartOfRound.Instance.randomMapSeed : 0;
                if (mapSeed != _floorGenerationGateLoggedSeed)
                {
                    _floorGenerationGateLoggedSeed = mapSeed;
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts] Floor generation contract gate: roundLevel={MoonContractState.GetLevelDebugName(__instance.currentLevel)}, "
                        + $"{MoonContractState.GetCurrentContractDebugContext()}, "
                        + $"factorySizeMultiplier={__instance.currentLevel.factorySizeMultiplier:0.###}, "
                        + $"mapSizeMultiplier={__instance.mapSizeMultiplier:0.###}, "
                        + $"incomingDungeonSize={(generator != null ? generator.LengthMultiplier.ToString("0.###") : "<no generator>")}, "
                        + $"maxAttemptCount={(generator != null ? generator.MaxAttemptCount.ToString() : "<no generator>")}, "
                        + $"candidateFlows={(__instance.currentLevel.dungeonFlowTypes != null ? __instance.currentLevel.dungeonFlowTypes.Length : 0)}, "
                        + $"softCap={(ContractsBootstrap.DungeonSizeSoftCap != null && ContractsBootstrap.DungeonSizeSoftCap.Value > 0f ? ContractsBootstrap.DungeonSizeSoftCap.Value.ToString("0.###") : "disabled")}.");
                }

                IntWithRarity[] approvedFlows;
                string reason;
                string label;
                if (contract == MoonContractType.BlackoutAudit)
                {
                    if (!MoonContractState.TryBuildBlackoutApprovedDungeonPool(__instance, out approvedFlows, out reason))
                    {
                        ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Blackout Audit could not force an approved interior: {reason}.");
                        return;
                    }

                    label = "Blackout Audit";
                }
                else if (contract == MoonContractType.ShadowRaid)
                {
                    if (!MoonContractState.TryBuildShadowRaidApprovedDungeonPool(__instance, out approvedFlows, out reason))
                    {
                        ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Shadow Raid could not force an authored interior: {reason}.");
                        return;
                    }

                    label = "Shadow Raid";
                }
                else if (contract == MoonContractType.Defuse)
                {
                    if (!MoonContractState.TryBuildDefuseApprovedDungeonPool(__instance, out approvedFlows, out reason))
                    {
                        ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Defuse] Defuse could not force an authored interior: {reason}.");
                        return;
                    }

                    label = "Defuse";
                }
                else
                {
                    return;
                }

                _overrideLevel = __instance.currentLevel;
                _originalFlows = __instance.currentLevel.dungeonFlowTypes;
                __instance.currentLevel.dungeonFlowTypes = approvedFlows;
                if (contract == MoonContractType.ShadowRaid)
                {
                    AuthoredInteriorTileInjectionService.ApplyForApprovedFlows(
                        __instance,
                        approvedFlows,
                        false,
                        AuthoredInteriorPlacementKinds.BlacksiteContainer);
                }
                else if (contract == MoonContractType.Defuse)
                {
                    AuthoredInteriorTileInjectionService.ApplyForApprovedFlows(
                        __instance,
                        approvedFlows,
                        false,
                        AuthoredInteriorPlacementKinds.DeadDropBomb);
                }
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] {label} forcing approved interior pool for {__instance.currentLevel.PlanetName}: {reason}.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts] Contract interior override failed before generation: {e}");
            }
        }

        [HarmonyPostfix]
        private static void Postfix(RoundManager __instance)
        {
            Restore();
            MoonContractState.ForceCurrentBlackoutPowerOff("floor generated");
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
                AuthoredInteriorTileInjectionService.RestoreInjectedRules("generation finalizer");

            Restore();
            return __exception;
        }

        private static void Restore()
        {
            if (_overrideLevel == null)
                return;

            _overrideLevel.dungeonFlowTypes = _originalFlows;
            _overrideLevel = null;
            _originalFlows = null;
        }
    }

    [HarmonyPatch(typeof(RoundManager), "FinishGeneratingLevel")]
    internal static class BreachAuthoredTileInjectionCleanupPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            AuthoredInteriorTileInjectionService.RestoreInjectedRules("finished generating level");
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                AuthoredInteriorTileInjectionService.RestoreInjectedRules("generation finalizer");
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(RoundManager), "SpawnMapObjects")]
    internal static class BlackoutBreakerSpawnMapObjectPatch
    {
        [HarmonyPrefix]
        private static void Prefix(RoundManager __instance)
        {
            try
            {
                if (MoonContractState.TryPrepareBlackoutVanillaBreakerSpawns(__instance, out string reason))
                {
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Blackout Audit prepared vanilla breaker spawns: {reason}.");
                }
                else if (MoonContractState.GetCurrentContract() == MoonContractType.BlackoutAudit)
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Blackout Audit could not prepare vanilla breaker spawns: {reason}.");
                }
            }
            catch (Exception e)
            {
                MoonContractState.RestoreBlackoutVanillaBreakerSpawnCurves(__instance);
                ContractsBootstrap.Log?.LogError($"[MoonContracts] Blackout Audit breaker spawn preparation failed: {e}");
            }
        }

        [HarmonyPostfix]
        private static void Postfix(RoundManager __instance)
        {
            MoonContractState.RestoreBlackoutVanillaBreakerSpawnCurves(__instance);
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(RoundManager __instance, Exception __exception)
        {
            MoonContractState.RestoreBlackoutVanillaBreakerSpawnCurves(__instance);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(RoundManager), "RefreshLightsList")]
    internal static class BlackoutRefreshLightsPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            MoonContractState.ForceCurrentBlackoutPowerOff("lights list refreshed", validateSupport: false);
            MoonContractState.LogInteriorPowerState("lights list refreshed");
        }
    }

    // #292: vanilla rolls an 8% seeded dark start here (SetPowerOffAtStart ->
    // TurnBreakerSwitchesOff). Its own Debug.Log lines never reach LogOutput.log, so a
    // "lights all off" report could not be told apart from a mod bug. This postfix is
    // observation only: it reads the flag vanilla just set and logs which branch rolled.
    [HarmonyPatch(typeof(RoundManager), "SetPowerOffAtStart")]
    internal static class VanillaDarkStartDiagnosticPatch
    {
        [HarmonyPostfix]
        private static void Postfix(RoundManager __instance)
        {
            try
            {
                bool darkStart = __instance != null && __instance.powerOffPermanently;
                int mapSeed = StartOfRound.Instance != null ? StartOfRound.Instance.randomMapSeed : 0;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Vanilla SetPowerOffAtStart rolled {(darkStart ? "DARK START (breaker switches turned off)" : "normal power")} (powerOffPermanently={darkStart}, mapSeed={mapSeed}).");
                MoonContractState.LogInteriorPowerState("vanilla SetPowerOffAtStart");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Vanilla dark-start diagnostic skipped: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(BreakerBox), nameof(BreakerBox.SwitchBreaker))]
    internal static class BlackoutBreakerSwitchPatch
    {
        [HarmonyPostfix]
        private static void Postfix(BreakerBox __instance, bool on)
        {
            if (on)
                MoonContractState.NotifyBreakerPowerRestored(__instance);
        }
    }

    /// <summary>
    /// #406: latches who flipped a breaker switch so the Blackout Audit completion is attributed.
    /// Both hooks are prefixes because the switch's UnityEvent reaches
    /// <c>BreakerBox.SwitchBreaker</c> from inside the patched bodies - a postfix would latch the
    /// actor after the audit had already decided it was complete.
    /// </summary>
    [HarmonyPatch(typeof(AnimatedObjectTrigger), nameof(AnimatedObjectTrigger.TriggerAnimation))]
    internal static class BlackoutBreakerLocalTriggerPatch
    {
        [HarmonyPrefix]
        private static void Prefix(AnimatedObjectTrigger __instance, PlayerControllerB playerWhoTriggered)
        {
            if (playerWhoTriggered == null || !BlackoutBreakerActorHooks.IsBreakerSwitch(__instance))
                return;

            BlackoutBreakerActorLatch.Latch(playerWhoTriggered);
        }
    }

    /// <summary>
    /// The replicated half: this RPC carries the actor index to every machine, so a switch a
    /// remote client flipped is still attributable on the host, which is the only machine allowed
    /// to author a progression event.
    /// </summary>
    [HarmonyPatch(typeof(AnimatedObjectTrigger), "UpdateAnimClientRpc")]
    internal static class BlackoutBreakerReplicatedTriggerPatch
    {
        [HarmonyPrefix]
        private static void Prefix(AnimatedObjectTrigger __instance, int playerWhoTriggered)
        {
            if (playerWhoTriggered < 0 || !BlackoutBreakerActorHooks.IsBreakerSwitch(__instance))
                return;

            BlackoutBreakerActorLatch.LatchByPlayerIndex(playerWhoTriggered);
        }
    }

    internal static class BlackoutBreakerActorHooks
    {
        /// <summary>
        /// Cheap gate first: almost every animated trigger in a run is a door, and only a
        /// Blackout Audit round cares who touched a breaker at all.
        /// </summary>
        internal static bool IsBreakerSwitch(AnimatedObjectTrigger trigger)
        {
            if (trigger == null || MoonContractState.GetCurrentContract() != MoonContractType.BlackoutAudit)
                return false;

            try
            {
                return trigger.GetComponentInParent<BreakerBox>() != null;
            }
            catch
            {
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(Terminal), "TextPostProcess")]
    internal static class MoonContractTerminalTextPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Terminal __instance, ref string __result, TerminalNode node)
        {
            if (string.IsNullOrEmpty(__result))
                return;

            if (IsMoonCatalogueNode(__instance, node))
                AddCatalogueLabels(__instance, ref __result);

            AppendPlanetInfoContract(ref __result, node);
        }

        /// <summary>
        /// Shared output adapter used by vanilla TextPostProcess and optional terminal
        /// replacements that return a fully-rendered catalogue without calling vanilla.
        /// </summary>
        internal static void AddCatalogueLabels(Terminal terminal, ref string text, bool detailRows = false)
        {
            if (string.IsNullOrEmpty(text))
                return;

            try
            {
                MoonContractState.RefreshAssignments("terminal moon catalogue");
                List<MoonContractCatalogueLabel> labels = BuildCatalogueLabels(terminal);
                var screen = terminal?.screenText?.textComponent;
                if (detailRows || screen == null)
                {
                    text = MoonContractCatalogueFormatter.AddLabels(text, labels, detailRows);
                }
                else
                {
                    // TMP's preferred width includes positive margins. Measure content only;
                    // the screen budget below already subtracts those margins once.
                    float margins = Mathf.Max(0f, screen.margin.x) + Mathf.Max(0f, screen.margin.z);
                    text = MoonContractCatalogueFormatter.AddArrangedLabels(text, labels,
                        screen.rectTransform.rect.width - screen.margin.x - screen.margin.z,
                        value => Mathf.Max(0f, screen.GetPreferredValues(value, float.PositiveInfinity, float.PositiveInfinity).x - margins));
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.TerminalCompat] Failed to annotate moon catalogue: {e.Message}");
            }
        }

        private static List<MoonContractCatalogueLabel> BuildCatalogueLabels(Terminal terminal)
        {
            var identities = new List<CatalogueLevelIdentity>();
            SelectableLevel[] startLevels = StartOfRound.Instance?.levels;

            if (startLevels != null)
            {
                foreach (SelectableLevel level in startLevels)
                    AddLevelIdentity(identities, level);
            }

            if (terminal?.moonsCatalogueList != null)
            {
                foreach (SelectableLevel level in terminal.moonsCatalogueList)
                    AddLevelIdentity(identities, level);
            }

            TerminalKeyword routeKeyword = terminal?.terminalNodes?.allKeywords?
                .FirstOrDefault(keyword => keyword != null
                    && keyword.isVerb
                    && string.Equals(keyword.word, "route", StringComparison.OrdinalIgnoreCase));
            if (routeKeyword?.compatibleNouns != null)
            {
                foreach (CompatibleNoun compatibleNoun in routeKeyword.compatibleNouns)
                {
                    TerminalNode routeNode = compatibleNoun?.result;
                    if (routeNode == null || routeNode.buyRerouteToMoon < 0)
                        continue;

                    SelectableLevel level = ResolveRouteLevel(
                        startLevels,
                        identities,
                        routeNode.buyRerouteToMoon);
                    CatalogueLevelIdentity identity = AddLevelIdentity(identities, level);
                    if (identity != null && !string.IsNullOrWhiteSpace(compatibleNoun.noun?.word))
                        identity.Aliases.Add(compatibleNoun.noun.word.Trim());
                }
            }

            return identities.Select(identity => new MoonContractCatalogueLabel(
                MoonContractState.GetDisplayNameForLevel(identity.Level),
                MoonContractState.GetShortNameForLevel(identity.Level),
                identity.Aliases)).ToList();
        }

        private static CatalogueLevelIdentity AddLevelIdentity(
            List<CatalogueLevelIdentity> identities,
            SelectableLevel level)
        {
            if (level == null)
                return null;

            CatalogueLevelIdentity identity = identities
                .FirstOrDefault(candidate => ReferenceEquals(candidate.Level, level));
            if (identity == null)
            {
                identity = new CatalogueLevelIdentity(level);
                identities.Add(identity);
            }

            if (!string.IsNullOrWhiteSpace(level.PlanetName))
            {
                identity.Aliases.Add(level.PlanetName.Trim());
                identity.Aliases.Add(MoonContractCatalogueFormatter.NumberlessMoonName(level.PlanetName));
            }

            return identity;
        }

        private static SelectableLevel ResolveRouteLevel(
            SelectableLevel[] startLevels,
            List<CatalogueLevelIdentity> identities,
            int routeIndex)
        {
            if (startLevels != null && routeIndex >= 0 && routeIndex < startLevels.Length)
                return startLevels[routeIndex];

            SelectableLevel byLevelId = startLevels?
                .FirstOrDefault(level => level != null && level.levelID == routeIndex);
            return byLevelId ?? identities
                .Select(identity => identity.Level)
                .FirstOrDefault(level => level != null && level.levelID == routeIndex);
        }

        private static bool IsMoonCatalogueNode(Terminal terminal, TerminalNode node)
        {
            if (node == null)
                return false;

            string nodeName = node.name ?? string.Empty;
            if (nodeName.IndexOf("moon", StringComparison.OrdinalIgnoreCase) >= 0
                && nodeName.IndexOf("catalog", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            TerminalKeyword moonsKeyword = terminal?.terminalNodes?.allKeywords?
                .FirstOrDefault(keyword => keyword != null
                    && string.Equals(keyword.word, "moons", StringComparison.OrdinalIgnoreCase));
            return ReferenceEquals(moonsKeyword?.specialKeywordResult, node);
        }

        private static void AppendPlanetInfoContract(ref string text, TerminalNode node)
        {
            if (node == null || node.displayPlanetInfo < 0)
                return;

            if (text.IndexOf("Contract:", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            SelectableLevel[] levels = StartOfRound.Instance?.levels;
            SelectableLevel level = null;
            if (levels != null && node.displayPlanetInfo < levels.Length)
                level = levels[node.displayPlanetInfo];
            if (level == null && levels != null)
                level = levels.FirstOrDefault(candidate => candidate != null && candidate.levelID == node.displayPlanetInfo);

            string displayName = level != null
                ? MoonContractState.GetDisplayNameForLevel(level)
                : MoonContractState.GetDisplayNameForLevelId(node.displayPlanetInfo);
            // Upper-case label to sit with the fuel block's labels, and the same bracketed
            // cyan tag the catalogue uses so a contract reads identically on both screens.
            // The idempotency guard above matches "Contract:" case-insensitively, so this
            // still self-guards, and the fuel panel still recognises the line by "contract:".
            text += $"\nCONTRACT: {MoonContractCatalogueFormatter.FormatContractTag(displayName)}\n";
        }

        private sealed class CatalogueLevelIdentity
        {
            internal CatalogueLevelIdentity(SelectableLevel level)
            {
                Level = level;
            }

            internal SelectableLevel Level { get; }
            internal HashSet<string> Aliases { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Retires the escalation entities that <c>ShipLeave_Postfix</c> deliberately left
    /// standing (#540).
    ///
    /// <para>Vanilla reaches this the same way it always did: the ship-leave animation event calls
    /// <c>ElevatorAnimationEvents.ElevatorFullyRunning</c>, which calls
    /// <c>RoundManager.DetectElevatorIsRunning</c>, whose coroutine waits 1.5s and then runs
    /// <c>UnloadSceneObjectsEarly</c> to despawn every <c>EnemyAI</c> on the map. Escalation
    /// entities are all <c>EnemyAI</c> spawned through <c>SpawnEnemyGameObject</c>, so vanilla
    /// already collects them and this postfix only has to retire the props vanilla ignores - the
    /// Blacksite helicopter, the mission drive, the ritual fire - and clear the registries.</para>
    ///
    /// <para><c>UnloadSceneObjectsEarly</c> is server-only, so this never fires on a client; the
    /// <c>EndOfGameClientRpc</c> reset below is what drains a client, and it is also the net if the
    /// sweep never runs at all (host disconnect, aborted departure).</para></summary>
    [HarmonyPatch(typeof(RoundManager), "UnloadSceneObjectsEarly")]
    internal static class MoonContractEscalationDepartureSweepPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            MoonEscalationState.CompleteDeferredTeardown("vanilla end-of-round sweep");
        }
    }

    [HarmonyPatch(typeof(StartOfRound), "EndOfGameClientRpc")]
    internal static class MoonContractEndOfRoundPatch
    {
        [HarmonyPrefix]
        private static void Prefix(StartOfRound __instance, int bodiesInsured, int connectedPlayersOnServer)
        {
            ContractHUDManager.Instance?.Hide(immediate: true);
            ShadowRaidContract.RemoveReturnedDrillsFromShip("end game");
            MoonContractRoundEndState.CaptureCurrentOutcomeIfMissing("end game rpc");
            int totalPlayers = Mathf.Max(1, connectedPlayersOnServer + 1);
            int playersDead = __instance != null
                ? Mathf.Max(0, totalPlayers - __instance.livingPlayers)
                : 0;
            MoonContractRoundEndState.CaptureCrewLosses(playersDead, bodiesInsured, totalPlayers, "end game rpc");
            // This prefix runs before the game's quota-rollover check, so a payout applied here
            // is inside the value that check sees.
            MoonContractRoundEndState.ApplyEndOfRoundEffects(beforeQuotaRolloverCheck: true);
            MoonContractRoundStats.CaptureEndOfRound(playersDead, bodiesInsured, totalPlayers);
            MoonContractState.ResetRunState();
        }
    }

    [HarmonyPatch(typeof(StartOfRound), "ResetShip")]
    internal static class QuotaOverhaulResetShipPatch
    {
        [HarmonyPostfix]
        private static void Postfix(StartOfRound __instance)
        {
            QuotaOverhaulRules.InitializeNewRun(__instance, "ship reset");
        }
    }

    [HarmonyPatch]
    internal static class QuotaOverhaulPlayerCountPatch
    {
        [HarmonyPatch(typeof(StartOfRound), "Start")]
        [HarmonyPostfix]
        private static void Start_Postfix(StartOfRound __instance)
        {
            QuotaOverhaulRules.EnsureSaveStateLoaded(__instance);
            QuotaOverhaulRules.RecordCurrentPlayerCountForFirstQuota();
        }

        [HarmonyPatch(typeof(StartOfRound), "OnClientConnect")]
        [HarmonyPrefix]
        private static void OnClientConnect_Prefix(StartOfRound __instance)
        {
            if (__instance == null)
                return;

            QuotaOverhaulRules.RecordCurrentPlayerCountForFirstQuota(
                Mathf.Max(1, __instance.connectedPlayersAmount + 2),
                "client connect");
        }

        [HarmonyPatch(typeof(StartOfRound), "OnClientDisconnect")]
        [HarmonyPostfix]
        private static void OnClientDisconnect_Postfix()
        {
            QuotaOverhaulRules.RecordCurrentPlayerCountForFirstQuota();
            QuotaOverhaulRules.RecordCurrentPlayerCountForMoon();
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.StartGame))]
        [HarmonyPostfix]
        private static void StartGame_Postfix()
        {
            QuotaOverhaulRules.RecordCurrentPlayerCountForMoon();
        }
    }

    [HarmonyPatch(typeof(TimeOfDay), nameof(TimeOfDay.SetNewProfitQuota))]
    internal static class QuotaOverhaulSetNewProfitQuotaPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(TimeOfDay __instance)
        {
            QuotaOverhaulRules.PrepareNextBaseQuotaAfterCompletion(__instance);
        }

        // Last of the three Y4NGZCompany postfixes on SetNewProfitQuota. Harmony sorts them by
        // priority across owners, so the declared order holds regardless of module init order:
        // ship-systems rollover refill (Priority.First), quota milestone grants
        // (Priority.Normal), then this rewrite of the next quota value.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(TimeOfDay __instance)
        {
            QuotaOverhaulRules.ApplyPreparedRolloverQuota(__instance);
        }
    }

    [HarmonyPatch(typeof(TimeOfDay), nameof(TimeOfDay.SyncNewProfitQuotaClientRpc))]
    internal static class QuotaOverhaulSyncNewProfitQuotaPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(ref int newProfitQuota)
        {
            QuotaOverhaulRules.OverrideSyncedRolloverQuota(ref newProfitQuota);
        }
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.FillEndGameStats))]
    internal static class MoonContractPrePerformanceReportPatch
    {
        private const string CoronerPluginGuid = "com.elitemastereric.coroner";

        [HarmonyPrefix]
        private static void Prefix(HUDManager __instance)
        {
            MoonContractUpgradesXpUiDelayPatch.RetryApplyIfNeeded();
            MoonContractRoundEndState.CaptureCurrentOutcomeIfMissing("fill endgame stats");
            // The rollover check has already happened by the time the end-game stats are
            // filled, so anything finalized here is banked against the next quota. In practice
            // the applied-flags above make this a no-op whenever the end-game RPC ran first;
            // the flag matters for the path where it did not.
            MoonContractRoundEndState.ApplyEndOfRoundEffects(beforeQuotaRolloverCheck: false);
            MoonContractRoundEndState.BeginPerformanceReportDelayIfNeeded(__instance, "fill endgame stats prefix");
        }

        // Coroner rewrites the same playerNotesText slots this report writes, so the snapshot
        // below is only worth anything once Coroner has had its turn. Postfix order between two
        // mods is otherwise arbitrary, and on the losing order the cause of death is captured
        // before it exists and the report shows our note alone.
        [HarmonyPostfix]
        [HarmonyAfter(CoronerPluginGuid)]
        private static void Postfix(HUDManager __instance)
        {
            ContractPerformanceReportUi.CaptureForeignPlayerNotes(__instance);
            MoonContractRoundEndState.QueuePerformanceReportApply(__instance);
        }
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.DisplayDaysLeft))]
    internal static class MoonContractDaysLeftDelayPatch
    {
        private static Coroutine _delayCoroutine;
        private static HUDManager _delayHost;
        private static bool _replaying;
        private static int _queuedDaysLeft;

        // #716/G3, same class as the #451 _tooltipRestoreCoroutine fix: the handle is
        // cleared only from the coroutine's tail, so quitting to the menu inside the delay
        // window leaves it non-null for the life of the process. The next lobby's prefix
        // then sees a "already queued" handle, swallows vanilla's popup and never replays
        // it. Round boundaries and disconnects clear it explicitly.
        internal static void CancelPendingReplay(string reason)
        {
            if (_delayCoroutine == null && !_replaying)
                return;

            MoonContractDelayedPopupLifecycle.StopDelayCoroutine(ref _delayHost, ref _delayCoroutine, "days-left", reason);
            _replaying = false;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(HUDManager __instance, int daysLeft)
        {
            if (_replaying
                || __instance == null
                || daysLeft < 0
                || !ContractPerformanceReportUi.ShouldDelayDaysLeftPopup(__instance))
            {
                return true;
            }

            _queuedDaysLeft = daysLeft;
            if (_delayCoroutine == null)
            {
                _delayHost = __instance;
                _delayCoroutine = __instance.StartCoroutine(ReplayWhenClear(__instance));
            }

            ContractsBootstrap.Log?.LogInfo(
                "[MoonContracts.ReportDiag] Delaying vanilla days-left popup until after performance report/XP: " +
                $"daysLeft={daysLeft}.");
            return false;
        }

        private static IEnumerator ReplayWhenClear(HUDManager hud)
        {
            while (hud != null
                && (ContractPerformanceReportUi.ShouldDelayDaysLeftPopup(hud)
                    || hud.displayingNewQuota))
            {
                yield return null;
            }

            yield return new WaitForSeconds(0.15f);

            if (hud != null)
            {
                _replaying = true;
                try
                {
                    ContractsBootstrap.Log?.LogInfo(
                        "[MoonContracts.ReportDiag] Replaying delayed vanilla days-left popup: " +
                        $"daysLeft={_queuedDaysLeft}.");
                    ContractPerformanceReportUi.HoldDaysLeftPopupSequence(hud);
                    hud.DisplayDaysLeft(_queuedDaysLeft);
                }
                finally
                {
                    _replaying = false;
                }
            }

            _delayCoroutine = null;
            _delayHost = null;
        }
    }

    /// <summary>
    /// #716/G3: shared teardown for the round-end popup delay patches. Each of them parks a
    /// coroutine handle that its own tail is the only thing that clears; a quit or a round
    /// boundary inside that window has to clear it from outside, or the handle outlives the
    /// lobby and the next round's prefix suppresses a popup it will never replay.
    /// </summary>
    internal static class MoonContractDelayedPopupLifecycle
    {
        internal static void StopDelayCoroutine(ref HUDManager host, ref Coroutine handle, string label, string reason)
        {
            if (handle != null)
            {
                MonoBehaviour runner = host != null ? host : HUDManager.Instance;
                if (runner != null)
                {
                    try { runner.StopCoroutine(handle); }
                    catch { }
                }

                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.ReportDiag] Cleared pending {label} popup replay ({reason}).");
            }

            handle = null;
            host = null;
        }

        /// <summary>Clears every parked replay. Safe to call repeatedly and on any peer.</summary>
        internal static void CancelAll(string reason)
        {
            MoonContractDaysLeftDelayPatch.CancelPendingReplay(reason);
            MoonContractUpgradesXpUiDelayPatch.CancelPendingReplay(reason);
            MoonContractPlayerLevelDelayPatch.CancelPendingReplay(reason);
        }
    }

    internal static class MoonContractUpgradesXpUiDelayPatch
    {
        private static MethodInfo _showRoundXpMethod;
        private static MethodInfo _markPresentationWindowReadyMethod;
        private static PropertyInfo _isPresentationActiveProperty;
        private static Coroutine _delayCoroutine;
        private static HUDManager _queuedHud;
        private static object _queuedLog;
        private static bool _replaying;

        private static Harmony _harmony;

        internal static bool IsApplied => _showRoundXpMethod != null;

        internal static bool HasQueuedReplayFor(HUDManager hud)
        {
            return hud != null && _delayCoroutine != null && _queuedHud == hud;
        }

        // #716/G3: see MoonContractDelayedPopupLifecycle. _queuedHud doubles as the
        // coroutine host here, so it is the handle passed to the shared stop.
        internal static void CancelPendingReplay(string reason)
        {
            if (_delayCoroutine == null && !_replaying)
                return;

            MoonContractDelayedPopupLifecycle.StopDelayCoroutine(ref _queuedHud, ref _delayCoroutine, "Y4NGZUpgrades XP/token", reason);
            _queuedLog = null;
            _replaying = false;
        }

        // The initial Apply runs during this plugin's Awake, which can precede
        // the Y4NGZUpgrades assembly load (it depends on us, so it initializes
        // later). Retried at round end, by which point the type is resolvable.
        internal static void RetryApplyIfNeeded()
        {
            if (!IsApplied)
                Apply(_harmony);
        }

        internal static void Apply(Harmony harmony)
        {
            harmony = harmony ?? _harmony;
            if (harmony == null || IsApplied)
                return;

            _harmony = harmony;
            _showRoundXpMethod = AccessTools.Method("Y4NGZUpgrades.ProgressionXpBarUi:ShowRoundXp");
            if (_showRoundXpMethod == null)
            {
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.ReportDiag] Y4NGZUpgrades ShowRoundXp not found; direct XP UI delay patch skipped.");
                return;
            }

            _isPresentationActiveProperty = _showRoundXpMethod.DeclaringType?.GetProperty(
                "IsPresentationActive",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _markPresentationWindowReadyMethod = _showRoundXpMethod.DeclaringType?.GetMethod(
                "MarkPresentationWindowReady",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(HUDManager) },
                null);

            var prefix = AccessTools.Method(typeof(MoonContractUpgradesXpUiDelayPatch), nameof(Prefix));
            harmony.Patch(_showRoundXpMethod, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.ReportDiag] Patched Y4NGZUpgrades ProgressionXpBarUi:ShowRoundXp for performance report sequencing.");
        }

        private static bool Prefix(HUDManager hud, object log)
        {
            ContractPerformanceReportUi.XpPopupGateSnapshot gate =
                ContractPerformanceReportUi.GetXpPopupGateSnapshot(hud);
            ContractsBootstrap.Log?.LogInfo(
                "[MoonContracts.ReportDiag] Y4NGZUpgrades XP/token gate: " +
                $"replaying={_replaying}, hudPresent={hud != null}, logPresent={log != null}, " +
                gate.FormatForLog() + ".");

            if (_replaying
                || hud == null
                || !gate.ShouldDelay)
            {
                return true;
            }

            _queuedHud = hud;
            _queuedLog = log;
            bool alreadyQueued = _delayCoroutine != null;
            if (_delayCoroutine == null)
                _delayCoroutine = hud.StartCoroutine(ReplayWhenReportClear(hud));

            ContractsBootstrap.Log?.LogInfo(
                alreadyQueued
                    ? "[MoonContracts.ReportDiag] Coalesced duplicate delayed Y4NGZUpgrades XP/token UI request."
                    : "[MoonContracts.ReportDiag] Delaying Y4NGZUpgrades XP/token UI until after performance report.");
            return false;
        }

        private static IEnumerator ReplayWhenReportClear(HUDManager hud)
        {
            yield return ContractPerformanceReportUi.WaitForPerformanceReportXpReplayClear(hud, "Y4NGZUpgrades");
            yield return new WaitForSeconds(0.05f);

            if (hud != null && _showRoundXpMethod != null)
            {
                ContractPerformanceReportUi.BeginXpPopupSequence(hud, "Y4NGZUpgrades", _queuedLog);
                yield return ContractPerformanceReportUi.WaitForXpPopupEntrance(hud);
                MarkUpgradesPresentationWindowReady(hud);
                _replaying = true;
                try
                {
                    ContractsBootstrap.Log?.LogInfo("[MoonContracts.ReportDiag] Replaying delayed Y4NGZUpgrades XP/token UI.");
                    _showRoundXpMethod.Invoke(null, new[] { hud, _queuedLog });
                }
                catch (Exception ex)
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ReportDiag] Failed replaying Y4NGZUpgrades XP/token UI: {ex}");
                }
                finally
                {
                    _replaying = false;
                }

                yield return ContractPerformanceReportUi.WaitForXpPopupSequence(hud, IsUpgradesPresentationActive);
                yield return ContractPerformanceReportUi.EndXpPopupSequence(hud);
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.ReportDiag] Delayed Y4NGZUpgrades XP/token UI sequence finished.");
            }

            _queuedHud = null;
            _queuedLog = null;
            _delayCoroutine = null;
        }

        private static void MarkUpgradesPresentationWindowReady(HUDManager hud)
        {
            if (_markPresentationWindowReadyMethod == null
                || !ContractPerformanceReportUi.IsXpPopupEntranceReady(hud))
            {
                return;
            }

            try
            {
                _markPresentationWindowReadyMethod.Invoke(null, new object[] { hud });
            }
            catch
            {
            }
        }

        private static bool IsUpgradesPresentationActive()
        {
            if (_isPresentationActiveProperty == null)
                return false;

            try
            {
                return _isPresentationActiveProperty.GetValue(null, null) is bool active && active;
            }
            catch
            {
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.SetPlayerLevel))]
    internal static class MoonContractPlayerLevelDelayPatch
    {
        private static Coroutine _delayCoroutine;
        private static HUDManager _delayHost;
        private static bool _replaying;
        private static bool _queuedIsDead;
        private static bool _queuedMostProfitable;
        private static bool _queuedAllPlayersDead;

        // #716/G3: see MoonContractDelayedPopupLifecycle.
        internal static void CancelPendingReplay(string reason)
        {
            if (_delayCoroutine == null && !_replaying)
                return;

            MoonContractDelayedPopupLifecycle.StopDelayCoroutine(ref _delayHost, ref _delayCoroutine, "vanilla XP", reason);
            _replaying = false;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter(new[] { "com.y4ngz.upgrades" })]
        private static bool Prefix(HUDManager __instance, bool isDead, bool mostProfitable, bool allPlayersDead)
        {
            ContractPerformanceReportUi.XpPopupGateSnapshot gate =
                ContractPerformanceReportUi.GetXpPopupGateSnapshot(__instance);
            bool upgradesPatchApplied = MoonContractUpgradesXpUiDelayPatch.IsApplied;
            bool upgradesReplayQueued = MoonContractUpgradesXpUiDelayPatch.HasQueuedReplayFor(__instance);
            ContractsBootstrap.Log?.LogInfo(
                "[MoonContracts.ReportDiag] Vanilla XP gate: " +
                $"replaying={_replaying}, hudPresent={__instance != null}, " +
                $"upgradesPatchApplied={upgradesPatchApplied}, upgradesReplayQueued={upgradesReplayQueued}, " +
                $"isDead={isDead}, mostProfitable={mostProfitable}, allPlayersDead={allPlayersDead}, " +
                gate.FormatForLog() + ".");

            if (_replaying
                || __instance == null
                || !gate.ShouldDelay)
            {
                return true;
            }

            if (upgradesPatchApplied)
            {
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.ReportDiag] Vanilla XP delay yielded; Y4NGZUpgrades ShowRoundXp owns XP replay.");
                return true;
            }

            if (upgradesReplayQueued)
            {
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.ReportDiag] Vanilla XP replay skipped; Y4NGZUpgrades XP/token UI replay is already queued.");
                return false;
            }

            _queuedIsDead = isDead;
            _queuedMostProfitable = mostProfitable;
            _queuedAllPlayersDead = allPlayersDead;

            if (_delayCoroutine == null)
            {
                _delayHost = __instance;
                _delayCoroutine = __instance.StartCoroutine(ReplayWhenReportClear(__instance));
            }

            ContractsBootstrap.Log?.LogInfo(
                "[MoonContracts.ReportDiag] Delaying vanilla XP popup until after performance report: " +
                $"isDead={isDead}, mostProfitable={mostProfitable}, allPlayersDead={allPlayersDead}.");
            return false;
        }

        private static IEnumerator ReplayWhenReportClear(HUDManager hud)
        {
            yield return ContractPerformanceReportUi.WaitForPerformanceReportXpReplayClear(hud, "vanilla");
            yield return new WaitForSeconds(0.05f);

            if (hud != null)
            {
                ContractPerformanceReportUi.BeginXpPopupSequence(
                    hud,
                    "vanilla",
                    _queuedIsDead,
                    _queuedMostProfitable,
                    _queuedAllPlayersDead);
                yield return ContractPerformanceReportUi.WaitForXpPopupEntrance(hud);
                _replaying = true;
                try
                {
                    ContractsBootstrap.Log?.LogInfo(
                        "[MoonContracts.ReportDiag] Replaying delayed vanilla XP popup: " +
                        $"isDead={_queuedIsDead}, mostProfitable={_queuedMostProfitable}, allPlayersDead={_queuedAllPlayersDead}.");
                    hud.SetPlayerLevel(_queuedIsDead, _queuedMostProfitable, _queuedAllPlayersDead);
                }
                finally
                {
                    _replaying = false;
                }

                yield return ContractPerformanceReportUi.WaitForXpPopupSequence(hud);
                yield return ContractPerformanceReportUi.EndXpPopupSequence(hud);
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.ReportDiag] Delayed vanilla XP popup sequence finished.");
            }

            _delayCoroutine = null;
            _delayHost = null;
        }
    }

    [HarmonyPatch(typeof(Animator), nameof(Animator.SetTrigger), new[] { typeof(string) })]
    internal static class MoonContractPenaltyAnimationPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Animator __instance, string name)
        {
            if (name == "displayPenalty"
                && HUDManager.Instance != null
                && __instance == HUDManager.Instance.endgameStatsAnimator
                && MoonContractRoundEndState.ShouldMergeWithPenaltyPopup())
            {
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Vanilla penalty popup allowed; merged contract report will overlay if ApplyPenalty completes.");
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.ApplyPenalty))]
    internal static class MoonContractMergedPenaltyPatch
    {
        private sealed class PenaltyPatchState
        {
            internal bool Merge;
            internal int CreditsBefore;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(int playersDead, out PenaltyPatchState __state)
        {
            var terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
            __state = new PenaltyPatchState
            {
                Merge = MoonContractRoundEndState.ShouldMergeWithPenaltyPopup(playersDead),
                CreditsBefore = terminal != null ? terminal.groupCredits : 0,
            };

            if (__state.Merge)
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Captured pre-penalty credits for merged contract/fines report.");
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(HUDManager __instance, int playersDead, int bodiesInsured, PenaltyPatchState __state)
        {
            if (__state == null || !__state.Merge)
                return;

            var terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
            int creditsAfter = terminal != null ? terminal.groupCredits : __state.CreditsBefore;
            string penaltyAddition = __instance?.statsUIElements?.penaltyAddition?.text
                ?? $"{playersDead} casualties";
            string penaltyTotal = __instance?.statsUIElements?.penaltyTotal?.text
                ?? $"DUE: ${Mathf.Max(0, __state.CreditsBefore - creditsAfter)}";

            ContractsBootstrap.Log?.LogInfo("[MoonContracts] ApplyPenalty completed; queueing merged contract/fines report.");
            MoonContractRoundEndState.QueueMergedPenaltyNotification(
                __instance,
                playersDead,
                bodiesInsured,
                __state.CreditsBefore,
                creditsAfter,
                penaltyAddition,
                penaltyTotal);
        }
    }

    [HarmonyPatch(typeof(StartOfRound), "AllPlayersHaveRevivedClientRpc")]
    internal static class MoonContractShipNotificationPatch
    {
        [HarmonyPostfix]
        private static void Postfix(StartOfRound __instance)
        {
            MoonContractRoundEndState.QueueShipNotification(__instance);
        }
    }

    [HarmonyPatch(typeof(ItemDropship), "OpenShipDoorsOnServer")]
    internal static class SurveyDropshipOpenPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ItemDropship __instance)
        {
            try
            {
                ShadowRaidContract.PrepareDropshipDoorOpen(__instance);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts] Dropship door-open preparation failed: {e}");
            }
        }

        [HarmonyPostfix]
        private static void Postfix(ItemDropship __instance)
        {
            try
            {
                MoonContractState.TrySpawnSurveyDronesFromDropship(__instance);
                ShadowRaidContract.TrySpawnDrillFromDropship(__instance);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts] Survey dropship spawn failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(RoundManager), "Update")]
    internal static class MoonContractRuntimePatch
    {
        /// <summary>Diagnostic timing for the 2026-07-31 investigation: profiler hitch lines show
        /// a single ~25 ms RoundManager.Update call about once per second, but method-level data
        /// cannot say whether this chain owns it. When one postfix pass exceeds this threshold,
        /// the per-step breakdown below names the owner — or exonerates the chain entirely.</summary>
        private const double SlowPassLogThresholdMilliseconds = 5.0;
        /// <summary>Threshold used when the diagnostics toggle is off. 5 ms is an
        /// investigation threshold, not a problem threshold: it fires on passes a player
        /// would never notice. One frame at 60 fps is the point where a pass is a real
        /// problem, so that is what an ordinary session reports on.</summary>
        private const double SlowPassPlayThresholdMilliseconds = 16.0;
        /// <summary>Rate limit so a persistently slow step reports, not spams.</summary>
        private const float SlowPassLogIntervalSeconds = 5f;
        /// <summary>Steps under this are noise and stay out of the reported line.</summary>
        private const double StepReportThresholdMilliseconds = 0.5;

        private static readonly string[] StepNames =
        {
            "MoonContractState.UpdateActiveContractMechanics",
            "MoonEscalationState.UpdateRuntime",
            "CodeRebirthCrashedShipObjectiveState.UpdateRuntime",
            "ShipPowerEscalationObjectiveState.UpdateRuntime",
            "BlacksiteMonsterTargeting.UpdateRuntime",
            "InteriorInjectionService.UpdateRuntime",
            "ShadowRaidContract.UpdateRuntime",
            "MoonContractRoundStats.UpdateRuntime",
            "AuthoredPlacementRoundReport.FlushHostIfReady",
            "SideObjectiveRuntime.UpdateRuntime",
            "ObjectiveHintRuntime.UpdateRuntime",
        };

        // Sized from StepNames so adding a step cannot silently desync the two.
        private static readonly double[] StepMilliseconds = new double[StepNames.Length];
        private static float _nextSlowPassLogAt;
        private static bool _patchOwnersLogged;
        /// <summary>Landed-edge tracking for the A1 landing-hitch instrumentation.</summary>
        private static bool _shipWasLanded;
        /// <summary>#716/A4: frames the contract mechanics chain stands aside for after
        /// <c>shipHasLanded</c> flips. Vanilla stacks its own landing work — several
        /// <c>ES3.Save</c> calls among it — on that exact frame, and the contract runtime
        /// transition (Blackout zone init, the Whistleblower guard sweep, first-frame
        /// contract spawns) piled onto the same one. Nothing in the chain or its consumers
        /// reads a mechanics result before the next frame: the objective queries the HUD
        /// runs call their own Ensure* entry points, and the other steps in this
        /// postfix are independent subsystems.</summary>
        private const int LandedTransitionDeferFrames = 2;
        /// <summary>Frames still to skip before the mechanics step resumes. Counts down on
        /// each postfix after the landed edge; the pass that takes it to zero is the one
        /// that runs the chain, and that pass is what the landed-frame report describes.</summary>
        private static int _landedDeferFramesRemaining;
        /// <summary>Frame the HUD reveal ran on, published by <see cref="ContractHUDManager"/>.
        /// The reveal is driven from a coroutine, so it can land either side of this postfix
        /// within its frame; the report treats that frame and the next one as the reveal.</summary>
        private static int _revealFrame = -1000;

        /// <summary>Called by the HUD as it reveals the contract card, so the pass covering
        /// the reveal frame escapes the rate limit that used to silence exactly it.</summary>
        internal static void MarkRevealFrame()
        {
            _revealFrame = Time.frameCount;
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            // #944 S11 U6: the whole runtime pass stands down while netcode is not listening.
            // Run 1 line 71954 threw NotListeningException out of PayloadContract.TrySpawnCart on
            // the teardown frame: RoundManager.Update still ticks after NetworkManager has stopped,
            // and every step below can reach a spawn, a despawn or an RPC. This is the same guard
            // ContractProgressionNetwork.EnsureNetworkHandlers uses (ContractProgressionNetwork.cs:67):
            // Singleton null-check plus IsListening, nothing weaker. Not a shutdown-only check -
            // it is also false before the session starts, which is the other frame range where the
            // chain has nothing to act on.
            //
            // #951 S11H U3 (c): and it stands down while a disconnect is in flight. IsListening is
            // still true on the frame GameNetworkManager.Disconnect resets the run state, which is
            // exactly when the chain has the most to break: Test 3's 133.84 ms frame re-rolled
            // Blacksite and re-ran interior injection on a level already being torn down (log
            // 80181-80322). The rule itself is MoonContractRunGate, pinned by
            // BlacksiteTierTwoThreeRegression.
            NetworkManager network = NetworkManager.Singleton;
            if (!MoonContractRunGate.ShouldRunRuntimePass(network != null && network.IsListening, MoonContractRunGate.Disconnecting))
                return;

            LogRoundManagerUpdatePatchOwnersOnce();
            // #802: outside the timed step chain on purpose - it is a no-op on every frame
            // but the few between StartGame and the floor finishing generation, and giving it
            // a timed step of its own would add a slow-pass report column for a line that never
            // costs anything.
            MoonContractLguBridge.TickPendingApply();

            var round = StartOfRound.Instance;
            bool landed = round != null && round.shipHasLanded;
            if (landed != _shipWasLanded)
            {
                _shipWasLanded = landed;
                // +1 because the counter also covers the pass that resumes the chain: the
                // landed frame and the one after it are skipped, and landed + 2 runs it.
                _landedDeferFramesRemaining = landed ? LandedTransitionDeferFrames + 1 : 0;
                if (!landed)
                    _revealFrame = -1000;
            }

            // #716/A4: the landed frame and the one after it run the rest of the chain but
            // leave the contract mechanics alone, so the transition lands clear of vanilla's
            // landing burst. "Landed-frame" in the report still means the frame the contract
            // transition actually ran on — now landed + LandedTransitionDeferFrames — so the
            // line keeps measuring the pass that owns the transition cost.
            bool runMechanics = true;
            bool landedFrame = false;
            if (_landedDeferFramesRemaining > 0)
            {
                _landedDeferFramesRemaining--;
                if (_landedDeferFramesRemaining > 0)
                    runMechanics = false;
                else
                    landedFrame = true;
            }

            int sinceReveal = Time.frameCount - _revealFrame;
            bool revealFrame = sinceReveal >= 0 && sinceReveal <= 1;

            long passStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // Execution order and failure semantics are unchanged from the untimed version:
                // the steps run in the same sequence inside one try, so a throwing step still
                // skips the rest of the pass and logs the same error.
                long t0 = passStart;
                if (runMechanics)
                    MoonContractState.UpdateActiveContractMechanics();
                t0 = MarkStep(0, t0);
                MoonEscalationState.UpdateRuntime();
                t0 = MarkStep(1, t0);
                CodeRebirthCrashedShipObjectiveState.UpdateRuntime();
                t0 = MarkStep(2, t0);
                ShipPowerEscalationObjectiveState.UpdateRuntime();
                t0 = MarkStep(3, t0);
                BlacksiteMonsterTargeting.UpdateRuntime();
                t0 = MarkStep(4, t0);
                InteriorInjectionService.UpdateRuntime();
                t0 = MarkStep(5, t0);
                // #393: InteriorSupportSpawner.UpdateRuntime moved out with the spawner and
                // now runs from LethalCCTV's own RoundManager.Update postfix, so it is no
                // longer one of this chain's timed steps.
                ShadowRaidContract.UpdateRuntime();
                t0 = MarkStep(6, t0);
                MoonContractRoundStats.UpdateRuntime();
                t0 = MarkStep(7, t0);
                AuthoredPlacementRoundReport.FlushHostIfReady();
                t0 = MarkStep(8, t0);
                // #1195: Wesley's Interiors side objective - host poll, local sight check, replay.
                SideObjectiveRuntime.UpdateRuntime();
                t0 = MarkStep(9, t0);
                // #1197: the local objective pity timer - reads the locate row, sends nothing.
                ObjectiveHintRuntime.UpdateRuntime();
                MarkStep(10, t0);
            }
            catch (Exception e)
            {
                // #716/G5: this is a per-frame path. A fault that reproduces every frame used
                // to write a full stack trace 60 times a second, which buries the first
                // occurrence — the only one that carries the state that caused it.
                MoonContractCatchReporter.Report(
                    "runtime-update",
                    "[MoonContracts] Runtime contract update failed",
                    e,
                    includeStackOnFirst: true);
            }

            ReportIfSlow(ToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - passStart), landedFrame, revealFrame);
        }

        private static long MarkStep(int index, long stepStart)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            StepMilliseconds[index] = ToMilliseconds(now - stepStart);
            return now;
        }

        private static double ToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static void ReportIfSlow(double passMilliseconds, bool landedFrame, bool revealFrame)
        {
            bool diagnostics = ContractsBootstrap.PerformanceTimingLogging?.Value == true;
            double threshold = diagnostics
                ? SlowPassLogThresholdMilliseconds
                : SlowPassPlayThresholdMilliseconds;
            bool overThreshold = passMilliseconds >= threshold;

            // A1: the landed frame and the reveal frame are the two frames the landing-hitch
            // investigation actually needs, and the 5 s rate limit silenced exactly them —
            // the landed frame armed the limit and the reveal 1.5 s later fell inside it. Both
            // are exempt from the limit, and the landed frame reports once per round even when
            // it is under threshold so the baseline is on record rather than inferred from
            // silence. Neither line is on the diagnostics toggle: they are once per round.
            if (landedFrame || (revealFrame && overThreshold))
            {
                if (revealFrame)
                    _revealFrame = -1000;
                EmitPass(passMilliseconds, landedFrame ? "Landed-frame" : "Reveal-frame", warn: false);
                return;
            }

            if (!overThreshold || Time.unscaledTime < _nextSlowPassLogAt)
            {
                ClearStepTimes();
                return;
            }

            _nextSlowPassLogAt = Time.unscaledTime + SlowPassLogIntervalSeconds;
            EmitPass(passMilliseconds, "Slow", warn: diagnostics);
        }

        private static void EmitPass(double passMilliseconds, string kind, bool warn)
        {
            var line = new System.Text.StringBuilder(256);
            line.Append("[MoonContracts.Timing] ");
            line.Append(kind);
            line.Append(" contract runtime pass ");
            line.Append(passMilliseconds.ToString("0.00"));
            line.Append("ms:");
            for (int i = 0; i < StepMilliseconds.Length; i++)
            {
                if (StepMilliseconds[i] < StepReportThresholdMilliseconds)
                    continue;
                line.Append(' ');
                line.Append(StepNames[i]);
                line.Append('=');
                line.Append(StepMilliseconds[i].ToString("0.00"));
                line.Append("ms");
                // #503: the contract mechanics step owns a whole chain of its own, so when it
                // is the reported step its nested breakdown is inlined right behind it.
                if (i == 0)
                {
                    line.Append(" [");
                    MoonContractState.AppendMechanicsBreakdown(line, StepReportThresholdMilliseconds);
                    line.Append(" ]");
                }
            }

            if (warn)
                ContractsBootstrap.Log?.LogWarning(line.ToString());
            else
                ContractsBootstrap.Log?.LogInfo(line.ToString());
            ClearStepTimes();
        }

        private static void ClearStepTimes()
        {
            for (int i = 0; i < StepMilliseconds.Length; i++)
                StepMilliseconds[i] = 0.0;
            MoonContractState.ClearMechanicsStepTimes();
        }

        /// <summary>One-time census of every Harmony patch attached to RoundManager.Update. If a
        /// slow pass never reports while the profiler still shows ~25 ms RoundManager.Update
        /// calls, the stall belongs to vanilla or to one of the owners named here.</summary>
        private static void LogRoundManagerUpdatePatchOwnersOnce()
        {
            if (_patchOwnersLogged)
                return;

            _patchOwnersLogged = true;
            try
            {
                MethodBase update = AccessTools.Method(typeof(RoundManager), "Update");
                Patches patches = update != null ? Harmony.GetPatchInfo(update) : null;
                if (patches == null)
                {
                    ContractsBootstrap.Log?.LogInfo("[MoonContracts.Timing] No Harmony patch info for RoundManager.Update.");
                    return;
                }

                string owners =
                    "prefixes[" + string.Join(", ", patches.Prefixes.Select(p => p.owner)) + "] " +
                    "postfixes[" + string.Join(", ", patches.Postfixes.Select(p => p.owner)) + "] " +
                    "transpilers[" + string.Join(", ", patches.Transpilers.Select(p => p.owner)) + "]";
                // A census is diagnostic detail, not a fault, so it only reaches the console
                // at Warning while the diagnostics toggle is on.
                string census = $"[MoonContracts.Timing] RoundManager.Update patch owners: {owners}";
                if (ContractsBootstrap.PerformanceTimingLogging?.Value == true)
                    ContractsBootstrap.Log?.LogWarning(census);
                else
                    ContractsBootstrap.Log?.LogDebug(census);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Timing] Patch owner census failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.HitEnemy))]
    internal static class MoonContractEnemyHitStatsPatch
    {
        [HarmonyPostfix]
        private static void Postfix(EnemyAI __instance, PlayerControllerB playerWhoHit)
        {
            try
            {
                MoonContractRoundStats.RecordEnemyHit(__instance, playerWhoHit);
            }
            catch (Exception e)
            {
                // #716/G5: one hit per swing, so a persistent fault repeats indefinitely at
                // Debug and is never seen. Surface the first one.
                MoonContractCatchReporter.Report(
                    "enemy-hit-stats",
                    "[MoonContracts.ReportStats] Enemy hit stat capture failed",
                    e);
            }
        }
    }

    [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.KillEnemy))]
    internal static class MoonContractEnemyKillStatsPatch
    {
        [HarmonyPostfix]
        private static void Postfix(EnemyAI __instance)
        {
            try
            {
                MoonContractRoundStats.NotifyEnemyKilled(__instance);
                // #804: the extermination cull is counted off this same postfix. Vanilla drives
                // KillEnemy through KillEnemyClientRpc on every peer, so each machine arrives at
                // the same count without any Contracted networking. It filters on the contract
                // and the species itself, so this is a per-kill dictionary miss on every other
                // round.
                LguContractRuntime.NotifyEnemyKilled(__instance);
            }
            catch (Exception e)
            {
                // #716/G5: see the hit-stat postfix above.
                MoonContractCatchReporter.Report(
                    "enemy-kill-stats",
                    "[MoonContracts.ReportStats] Enemy kill stat capture failed",
                    e);
            }
        }
    }

    // #716/B3: WhistleblowerEnemySpawnPatch used to sit here as a second
    // RoundManager.Update postfix calling MoonContractState.EnsureWhistleblowerTarget.
    // MoonContractRuntimePatch already reaches the same call every frame through
    // UpdateActiveContractMechanics -> UpdateWhistleblowerContract, so this postfix only
    // ever added a duplicate spawned-enemy scan per frame — and, being ungated, ran the
    // whole placement search during descent and in orbit. Removed; the mechanics branch is
    // the single owner.

    [HarmonyPatch]
    internal static class WhistleblowerEnemySpawnGuardPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(RoundManager),
                "SpawnEnemyGameObject",
                new[] { typeof(Vector3), typeof(float), typeof(int), typeof(EnemyType) });
        }

        private static bool Prefix(EnemyType enemyType, ref NetworkObjectReference __result)
        {
            if (MoonContractState.ShouldAllowWhistleblowerSpawn(enemyType))
                return true;

            __result = default;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class WhistleblowerEnemyIndexSpawnGuardPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo method = AccessTools.Method(
                typeof(RoundManager),
                "SpawnEnemyGameObject",
                new[] { typeof(Vector3), typeof(float), typeof(int) });

            if (method != null)
                yield return method;
            else
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Could not find RoundManager.SpawnEnemyGameObject(Vector3,float,int); Whistleblower index spawn guard was not patched.");
        }

        private static bool Prefix(RoundManager __instance, int enemyIndex)
        {
            EnemyType enemyType = MoonContractState.ResolveLevelEnemyType(__instance, enemyIndex);
            return MoonContractState.ShouldAllowWhistleblowerSpawn(enemyType);
        }
    }
}
