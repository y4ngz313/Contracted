using HarmonyLib;

namespace Y4NGZCompany.Escalations.Congregation
{
    /// <summary>#845 C: StartOfRound.Awake runs on every peer once the game scene (and with it every
    /// vanilla Item) is loaded, and before any item of the session is instantiated, including a
    /// host's saved ship scrap and a client's synced objects.</summary>
    [HarmonyPatch(typeof(StartOfRound), "Awake")]
    internal static class CongregationLootPatches
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            CongregationLoot.HydrateVanillaKnife(warnIfMissing: true);
        }
    }
}
