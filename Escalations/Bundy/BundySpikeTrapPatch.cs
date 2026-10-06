using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Escalations.Bundy
{
    /// <summary>
    /// #318 -- spike roof traps used to delete Bundy. SpikeRoofTrap.OnTriggerStay calls
    /// KillEnemyOnOwnerClient on any enemy whose type has canDie, and Bundy sets canDie so the ship
    /// turret and the shotgun register on him. Vanilla KillEnemy then sets isEnemyDead, disables the
    /// agent and drives animator parameters his controller does not have (Dead, KillEnemy), so the
    /// result was a frozen walking corpse rather than a death -- and OnTriggerStay kept refiring it.
    /// He has no death representation yet, so the slam is converted into damage plus a forced
    /// knockdown instead (BundyAIBase.OnSpikeTrapSlam).
    /// </summary>
    [HarmonyPatch]
    internal static class BundySpikeTrapPatch
    {
        // Same liveness receipt as BundyTeleporterRevealPatch: Harmony calls Prepare() once per patch
        // class before patching, so a run where spikes still delete him can be told apart from a run
        // where the patch never applied.
        private static bool Prepare()
        {
            Bootstrap.BundyPlugin.Log?.LogInfo(
                "[Bundy] BundySpikeTrapPatch applied: SpikeRoofTrap.OnTriggerStay prefix is live " +
                "(#318 spike trap knockdown).");
            return true;
        }

        [HarmonyPatch(typeof(SpikeRoofTrap), "OnTriggerStay")]
        [HarmonyPrefix]
        private static bool PreOnTriggerStay(SpikeRoofTrap __instance, Collider other)
        {
            if (__instance == null || other == null)
                return true;

            // Re-check the trap's own gates before doing anything: OnTriggerStay runs for every
            // overlapping collider on every physics frame, and the overwhelming majority of those
            // calls are a retracted or rising trap that vanilla drops immediately.
            if (!__instance.trapActive ||
                !__instance.slammingDown ||
                Time.realtimeSinceStartup - __instance.timeSinceMovingUp < 0.75f)
            {
                return true;
            }

            // Players, dead bodies and every other enemy keep vanilla behaviour untouched -- this
            // patch only ever claims a collider that resolves to Bundy.
            EnemyAICollisionDetect collision = other.gameObject.GetComponent<EnemyAICollisionDetect>();
            if (collision == null || collision.mainScript is not BundyAIBase bundy)
                return true;

            // Suppressed unconditionally, including on non-owners and on a Bundy already flagged
            // dead: vanilla's branch here has no effect worth keeping for him, and letting it run on
            // any client is exactly the instakill this exists to remove.
            if (bundy.IsOwner && !bundy.isEnemyDead)
                bundy.OnSpikeTrapSlam(__instance);

            return false;
        }
    }
}
