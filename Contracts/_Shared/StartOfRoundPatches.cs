using System.Collections;
using HarmonyLib;
using UnityEngine;

using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
namespace Y4NGZCompany.Contracts._Shared
{
    internal static class StartOfRoundPatches
    {
        [HarmonyPatch(typeof(StartOfRound), "openingDoorsSequence")]
        [HarmonyPostfix]
        static void AfterOpeningDoorsSequence(ref IEnumerator __result)
        {
            __result = WrapWithContractHUD(__result);
        }

        private static IEnumerator WrapWithContractHUD(IEnumerator original)
        {
            // A3: the card's canvas, its procedural children, its CanvasScaler and its
            // typewriter audio load used to be created on the reveal — the single most
            // expensive frame of the round, after the level generates and every contract
            // system initialises. The doors sequence takes roughly fourteen seconds of
            // waits before it sets shipHasLanded, and the manager marks itself
            // DontDestroyOnLoad and hidden in Awake, so building it one yield into that
            // sequence costs nothing visible and takes the whole construction off the
            // reveal frame. It survives the session, so this runs once.
            //
            // #1233 S8: the card itself too. EnsureCardBuilt was left to the reveal, so the
            // session's first reveal still built the whole card (19.04 ms on 27 September, of
            // which the timed steps accounted for 1.4). It is built at alpha 0 under the vanilla
            // HUD container, which exists here, and the reveal then finds it already attached.
            bool warmed = false;
            while (original.MoveNext())
            {
                yield return original.Current;
                if (!warmed)
                {
                    warmed = true;
                    ContractHUDManager.EnsureInstance();
                    ContractHUDManager.Instance?.EnsureCardBuilt();
                }
            }

            yield return new WaitForSeconds(1.5f);

            var start = StartOfRound.Instance;
            if (start == null || start.inShipPhase || start.shipIsLeaving || !start.shipHasLanded)
                yield break;

            ContractHUDManager.EnsureInstance();
            if (ContractHUDManager.Instance != null)
            {
                ContractHUDManager.Instance.ShowContract();
            }
        }

        [HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
        [HarmonyPostfix]
        static void OnShipLeave()
        {
            ShadowRaidContract.RemoveReturnedDrillsFromShip("ship leave");
            // #716/G3: a departure is the boundary before the round-end popup sequence, so
            // anything a previous round parked is stale by definition. Clearing it here is
            // what stops a quit inside last round's delay window from making this round's
            // prefix swallow vanilla's days-left and XP popups.
            MoonContractDelayedPopupLifecycle.CancelAll("ship leave");
            // #540: monster targeting deliberately survives the departure so a Blacksite squad
            // left on the moon keeps engaging what is hunting it. MoonEscalationState's deferred
            // teardown resets it once vanilla's end-of-round sweep clears the map.
            if (ContractHUDManager.Instance != null)
            {
                ContractHUDManager.Instance.Hide();
            }
        }

        [HarmonyPatch(typeof(StartOfRound), "ShipLeaveAutomatically")]
        [HarmonyPostfix]
        static void OnShipLeaveAutomatically()
        {
            ShadowRaidContract.RemoveReturnedDrillsFromShip("automatic ship leave");
            // #716/G3: see OnShipLeave.
            MoonContractDelayedPopupLifecycle.CancelAll("automatic ship leave");
            // #540: monster targeting deliberately survives the departure so a Blacksite squad
            // left on the moon keeps engaging what is hunting it. MoonEscalationState's deferred
            // teardown resets it once vanilla's end-of-round sweep clears the map.
            if (ContractHUDManager.Instance != null)
            {
                ContractHUDManager.Instance.Hide();
            }
        }

        // #951 S11H U3 (c): the host's round start releases the disconnect latch. A prefix, because
        // StartGame's own body is server-only and returns early on a client; the client's release
        // is the SetShipReadyToLand postfix below, which every peer runs.
        [HarmonyPatch(typeof(StartOfRound), "StartGame")]
        [HarmonyPrefix]
        static void BeforeStartGame()
        {
            MoonContractRunGate.NoteRunStarted();
        }

        [HarmonyPatch(typeof(StartOfRound), "SetShipReadyToLand")]
        [HarmonyPostfix]
        static void OnSetShipReadyToLand()
        {
            // #951 S11H U3 (c): the other release point, and the one a client reaches.
            MoonContractRunGate.NoteRunStarted();
            ShadowRaidContract.RemoveReturnedDrillsFromShip("ship ready to land");
            BlacksiteMonsterTargeting.Reset();
            if (ContractHUDManager.Instance != null)
            {
                ContractHUDManager.Instance.Hide(immediate: true);
            }
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        static void OnDisconnect()
        {
            // #611 task 1.3: a disconnect mid-round ends the round with no rollover, so nothing
            // will ever consume an outcome staged for it. Left behind, it would be summed into
            // whatever rollover the next lobby reaches and attributed to a quota it had nothing
            // to do with.
            Y4NGZCore.Modules.Quota.QuotaRolloverOutcomeStaging.Discard();

            // #716/G1: every other lifecycle path resets the contract runtime, but a quit
            // never did — so a disconnect mid-round carried the round's blackout zones,
            // whistleblower state, survey targets and escalation runtime straight into the
            // next lobby. ResetRunState covers MoonEscalationState too, and is idempotent
            // per round (#716/G4), so this is harmless after a ShipLeave that already ran.
            //
            // #951 S11H U3 (c): latched BEFORE the reset. NetworkManager.IsListening is still true
            // on this frame, so without the latch the next RoundManager.Update postfix passed the
            // #944 S11 U6 listening guard and re-rolled the escalation on a level being torn down
            // (Test 3 log 80181-80322, the 133.84 ms frame). Released by StartGame or the ship
            // being made ready to land.
            MoonContractRunGate.NoteDisconnect();
            MoonContractState.ResetRunState();
            // Upgrades #458: the XP report ledger now holds crewmates' shared rows too. A client
            // clears it only at SetShipReadyToLand, so a quit before that carried them into the
            // next lobby's first report as another crew's rows.
            PerformanceReportXpAmounts.Clear("disconnect");
            PerformanceReportXpLedger.Clear("disconnect");
            // #716/G3: a quit inside the round-end popup delay window is the exact case that
            // strands these coroutine handles for the life of the process.
            MoonContractDelayedPopupLifecycle.CancelAll("disconnect");

            BlacksiteMonsterTargeting.Reset();
            if (ContractHUDManager.Instance != null)
            {
                ContractHUDManager.Instance.Hide(immediate: true);
            }
        }
    }
}
