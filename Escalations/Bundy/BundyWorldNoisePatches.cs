using System;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Escalations._Shared;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Escalations.Bundy
{
    /// <summary>
    /// Job 3b -- the Harmony half of the world-noise bus. Every hook here does the same three things:
    /// find the active Stranger Bundy, work out where the noise happened and who caused it, and hand
    /// both to <c>OnWorldNoiseEvent</c>. All of the judgement -- attribution, range, priority, doubt --
    /// lives in the bus, so a hook that guesses wrong about the causer is corrected rather than obeyed.
    ///
    /// Each vanilla type gets its own patch class so that a signature drift in a future game build
    /// disables one event instead of the whole bus (see the per-class registration in BundyBootstrap).
    ///
    /// NETWORK AUTHORITY. The bus only acts on the client that owns the Bundy AI, which is the host.
    /// Hooks on ClientRpcs and on shared state-setters therefore work for every player's actions.
    /// The two marked LOCAL-ONLY below fire from local input and so only reach the bus when the host
    /// is the one acting; making them work for remote clients needs an RPC, which is out of 3b's
    /// scope. They are still worth having -- they are free in single-player and for the host -- but
    /// do not read their absence in a client-side log as a bug.
    ///
    /// <c>Report</c> below is also the front door for the systems this mod owns (CCTV station,
    /// ship turret, battery station) -- they need no patch and call it directly, and they rely on
    /// the same guarantee the hooks do: a bus failure never takes down the interaction.
    /// </summary>
    internal static class BundyWorldNoisePatchSupport
    {
        internal static void Report(Vector3 position, BundyWorldNoise meaning, PlayerControllerB causedBy = null)
        {
            try
            {
                if (!MoonEscalationState.TryGetActiveStrangerBundy(out BundyAIBase bundy) || bundy == null)
                    return;

                bundy.OnWorldNoiseEvent(position, meaning, causedBy);
            }
            catch (Exception ex)
            {
                // A perception nicety must never be able to take down a vanilla interaction.
                Bootstrap.BundyPlugin.Log?.LogWarning(
                    $"[Bundy] world noise '{BundyWorldNoiseCatalog.GetLabel(meaning)}' failed: {ex.Message}");
            }
        }

        internal static PlayerControllerB ResolvePlayer(int playerIndex)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || playerIndex < 0 || playerIndex >= players.Length)
                return null;

            return players[playerIndex];
        }

        internal static void LogApplied(string what)
        {
            Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] BundyWorldNoisePatches applied: {what} (3b noise bus).");
        }
    }

    /// <summary>Breaker levers. Runs on every client via the shared setter.</summary>
    [HarmonyPatch]
    internal static class BundyBreakerNoisePatch
    {
        // HarmonyX calls Prepare twice for an attributed class -- once class-level (original ==
        // null) and once per patched method -- so every receipt in this file gates on the
        // class-level call or run logs show each hook "applied" twice. Same pattern in all seven.
        private static bool Prepare(MethodBase original)
        {
            if (original == null)
                BundyWorldNoisePatchSupport.LogApplied("BreakerBox.SwitchBreaker postfix is live");
            return true;
        }

        [HarmonyPatch(typeof(BreakerBox), nameof(BreakerBox.SwitchBreaker))]
        [HarmonyPostfix]
        private static void PostSwitchBreaker(BreakerBox __instance)
        {
            if (__instance == null)
                return;

            // No player argument on this one, so the bus resolves the causer by proximity to the box.
            BundyWorldNoisePatchSupport.Report(__instance.transform.position, BundyWorldNoise.BreakerFlip);
        }
    }

    /// <summary>
    /// Big powered doors. <c>SetDoorOpen</c> is the shared setter both the local path and the
    /// ClientRpc funnel into, so this sees every door on every client.
    /// </summary>
    [HarmonyPatch]
    internal static class BundyBigDoorNoisePatch
    {
        private static bool Prepare(MethodBase original)
        {
            if (original == null)
                BundyWorldNoisePatchSupport.LogApplied("TerminalAccessibleObject.SetDoorOpen postfix is live");
            return true;
        }

        [HarmonyPatch(typeof(TerminalAccessibleObject), nameof(TerminalAccessibleObject.SetDoorOpen))]
        [HarmonyPostfix]
        private static void PostSetDoorOpen(TerminalAccessibleObject __instance, bool open)
        {
            // Only the opening is a player telling him where they are. A door closing behind them is
            // the same sound but usually happens once they are already gone.
            if (__instance == null || !open)
                return;

            BundyWorldNoisePatchSupport.Report(__instance.transform.position, BundyWorldNoise.BigDoorOpen);
        }
    }

    /// <summary>The ship horn. Carries the puller's index, so attribution is exact.</summary>
    [HarmonyPatch]
    internal static class BundyShipHornNoisePatch
    {
        private static bool Prepare(MethodBase original)
        {
            if (original == null)
                BundyWorldNoisePatchSupport.LogApplied("ShipAlarmCord.PullCordClientRpc postfix is live");
            return true;
        }

        [HarmonyPatch(typeof(ShipAlarmCord), nameof(ShipAlarmCord.PullCordClientRpc))]
        [HarmonyPostfix]
        private static void PostPullCord(ShipAlarmCord __instance, int playerPullingCord)
        {
            if (__instance == null)
                return;

            BundyWorldNoisePatchSupport.Report(
                __instance.transform.position,
                BundyWorldNoise.ShipHorn,
                BundyWorldNoisePatchSupport.ResolvePlayer(playerPullingCord));
        }
    }

    /// <summary>
    /// Turret berserk. Carries the triggering player's index. The catalog rates this low-confidence
    /// on purpose: the turret's position is exact but the player it is shooting at is running.
    /// </summary>
    [HarmonyPatch]
    internal static class BundyTurretNoisePatch
    {
        private static bool Prepare(MethodBase original)
        {
            if (original == null)
                BundyWorldNoisePatchSupport.LogApplied("Turret.EnterBerserkModeClientRpc postfix is live");
            return true;
        }

        [HarmonyPatch(typeof(Turret), nameof(Turret.EnterBerserkModeClientRpc))]
        [HarmonyPostfix]
        private static void PostEnterBerserkMode(Turret __instance, int playerWhoTriggered)
        {
            if (__instance == null)
                return;

            BundyWorldNoisePatchSupport.Report(
                __instance.transform.position,
                BundyWorldNoise.TurretFire,
                BundyWorldNoisePatchSupport.ResolvePlayer(playerWhoTriggered));
        }
    }

    /// <summary>
    /// The ship terminal. Fires on the audio ClientRpc rather than on command parsing, because that
    /// is the moment the noise actually exists and it reaches every client.
    /// </summary>
    [HarmonyPatch]
    internal static class BundyTerminalNoisePatch
    {
        private static bool Prepare(MethodBase original)
        {
            if (original == null)
                BundyWorldNoisePatchSupport.LogApplied("Terminal.PlayTerminalAudioClientRpc postfix is live");
            return true;
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.PlayTerminalAudioClientRpc))]
        [HarmonyPostfix]
        private static void PostPlayTerminalAudio(Terminal __instance)
        {
            if (__instance == null)
                return;

            // Whoever is using it is standing at it, so proximity attribution is exactly right here.
            BundyWorldNoisePatchSupport.Report(__instance.transform.position, BundyWorldNoise.TerminalUse);
        }
    }

    /// <summary>LOCAL-ONLY (see the class header). The scan pulse.</summary>
    [HarmonyPatch]
    internal static class BundyScanPingNoisePatch
    {
        private static bool Prepare(MethodBase original)
        {
            if (original == null)
                BundyWorldNoisePatchSupport.LogApplied("HUDManager.PingScan_performed postfix is live (local-only)");
            return true;
        }

        [HarmonyPatch(typeof(HUDManager), "PingScan_performed")]
        [HarmonyPostfix]
        private static void PostPingScan()
        {
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local == null)
                return;

            BundyWorldNoisePatchSupport.Report(local.transform.position, BundyWorldNoise.ScanPing, local);
        }
    }

    /// <summary>
    /// LOCAL-ONLY (see the class header). Walkie transmissions. The weakest contact on the bus by
    /// design -- it is audible everywhere and says almost nothing about where the speaker is, so the
    /// catalog scatters it hard. It is the "he heard you say something, somewhere" channel.
    /// </summary>
    [HarmonyPatch]
    internal static class BundyWalkieNoisePatch
    {
        private static bool Prepare(MethodBase original)
        {
            if (original == null)
                BundyWorldNoisePatchSupport.LogApplied("WalkieTalkie.SetLocalClientSpeaking postfix is live (local-only)");
            return true;
        }

        [HarmonyPatch(typeof(WalkieTalkie), nameof(WalkieTalkie.SetLocalClientSpeaking))]
        [HarmonyPostfix]
        private static void PostSetLocalClientSpeaking(bool speaking)
        {
            // Only the start of a transmission. Without this the hook would re-fire on release and
            // pin him a second time to wherever the player had walked to by then.
            if (!speaking)
                return;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local == null)
                return;

            BundyWorldNoisePatchSupport.Report(local.transform.position, BundyWorldNoise.WalkieTransmit, local);
        }
    }
}
