using HarmonyLib;

namespace Y4NGZCompany.Escalations.Congregation
{
    /// <summary>#1336 S3: once inSpecialAnimation clears, vanilla EnemyAI.Update calls
    /// SetClientCalculatingAI(true) on the owner every frame, and SeraphimEnemyAI.StopAgentMotion
    /// switched the agent off again, so the host registered and unregistered a NavMeshAgent each
    /// frame. The Seraphim flies by transform and never navigates: a living one keeps the flag
    /// vanilla reads and leaves its agent off. Other enemies and every disable run unchanged.</summary>
    [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.SetClientCalculatingAI))]
    internal static class SeraphimAgentEnablePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(EnemyAI __instance, bool enable)
        {
            if (!(__instance is SeraphimEnemyAI) || !SeraphimFrameBudget.ShouldSuppressAgentEnable(enable, __instance.isEnemyDead))
                return true;

            __instance.isClientCalculatingAI = true;
            return false;
        }
    }
}
