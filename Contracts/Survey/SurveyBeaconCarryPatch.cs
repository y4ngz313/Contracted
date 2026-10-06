using HarmonyLib;

namespace Y4NGZCompany.Contracts.Survey
{
    // A separate MonoBehaviour LateUpdate can run before vanilla, which then replaces the
    // solved pose with the left-hand socket. Attach to that write directly instead.
    [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.LateUpdate))]
    internal static class SurveyBeaconCarryPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(GrabbableObject __instance)
        {
            if (__instance.isHeld && __instance.TryGetComponent<SurveyDroneItem>(out var beacon))
                beacon.ApplyHeldPoseAfterVanilla(__instance);
        }
    }
}
