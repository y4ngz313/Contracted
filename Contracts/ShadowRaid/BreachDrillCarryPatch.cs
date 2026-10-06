using HarmonyLib;

namespace Y4NGZCompany.Contracts.ShadowRaid
{
    // Apply directly after vanilla writes the hand socket, as SurveyBeaconCarryPatch does.
    // A runtime MonoBehaviour's independent LateUpdate can be overwritten by that write.
    [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.LateUpdate))]
    internal static class BreachDrillCarryPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(GrabbableObject __instance)
        {
            if (__instance.isHeld && __instance.TryGetComponent<BreachDrillItem>(out var drill))
                drill.ApplyHeldPoseAfterVanilla();
        }
    }
}
