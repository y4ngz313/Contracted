using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Escalations._Shared;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Escalations.Bundy
{
    /// <summary>
    /// D4 -- ship teleporter reveal. The teleporter used to be a hard counter to the Stranger hunt:
    /// it yanks the hunted player clean across the map, and with a 14m hearing radius Bundy received
    /// no information at all. The beam is one of the loudest, most recognisable events in the game,
    /// and the locked design says noise feeds him, so an activation is treated as a map-wide noise
    /// event that pins his last-known position to the ship. That also funnels him toward the battery
    /// siege instead of stranding him where the player used to be.
    /// </summary>
    [HarmonyPatch]
    internal static class BundyTeleporterRevealPatch
    {
        // Run 4 could confirm neither that this patch fired nor that it was even applied: nobody
        // teleported during the hunt, and the patch logged nothing on apply. Harmony calls Prepare()
        // once per patch class before patching, so this is the cheapest possible liveness receipt --
        // without it every future run repeats the same ambiguity.
        private static bool Prepare()
        {
            Bootstrap.BundyPlugin.Log?.LogInfo(
                "[Bundy] BundyTeleporterRevealPatch applied: ShipTeleporter.PressTeleportButtonClientRpc " +
                "postfix is live (D4 teleporter reveal).");
            return true;
        }

        // Fires on every client the moment the button is pressed and the beam starts -- i.e. when the
        // noise actually happens, which is a beat before the player lands. The small lead is wanted:
        // he starts moving as the beam fires rather than after the fact.
        [HarmonyPatch(typeof(ShipTeleporter), "PressTeleportButtonClientRpc")]
        [HarmonyPostfix]
        private static void PostPressTeleportButton(ShipTeleporter __instance)
        {
            if (__instance == null || __instance.isInverseTeleporter)
                return;

            if (!MoonEscalationState.TryGetActiveStrangerBundy(out BundyAIBase bundy) || bundy == null)
                return;

            // Vanilla beams up whoever the radar is currently targeting.
            PlayerControllerB teleported = StartOfRound.Instance?.mapScreen?.targetedPlayer;
            if (teleported == null)
                return;

            // The player lands on the teleporter pad, so that is the position worth revealing.
            Vector3 arrival = __instance.teleporterPosition != null
                ? __instance.teleporterPosition.position
                : __instance.transform.position;

            bundy.OnShipTeleporterActivated(teleported, arrival);
        }
    }
}
