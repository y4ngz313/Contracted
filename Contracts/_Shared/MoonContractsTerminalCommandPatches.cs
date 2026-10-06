using System;
using HarmonyLib;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts._Shared
{
    [HarmonyPatch(typeof(Terminal), "Start")]
    internal static class MoonContractsTerminalCommandStartPatch
    {
        // Last, so keywords other mods add in their own Start postfixes are seen first and
        // keep their word.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Terminal __instance)
        {
            try
            {
                MoonContractsTerminalCommand.Register(__instance);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.TerminalCompat] Could not register the contract list command: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Terminal), "LoadNewNode")]
    internal static class MoonContractsTerminalCommandLoadPatch
    {
        [HarmonyPrefix]
        private static void Prefix(Terminal __instance, TerminalNode node)
            => MoonContractsTerminalCommand.Refresh(__instance, node);
    }

    [HarmonyPatch(typeof(Terminal), "TextPostProcess")]
    internal static class MoonContractsTerminalCommandHelpPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Terminal __instance, TerminalNode node, ref string __result)
            => MoonContractsTerminalCommand.AddHelp(__instance, node, ref __result);
    }
}
