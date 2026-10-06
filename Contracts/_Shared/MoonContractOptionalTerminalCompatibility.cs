using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Reflection-only adapters for terminal mods that render their own moon catalogue and
    /// therefore never call <see cref="Terminal.TextPostProcess"/>. Keeping these links soft
    /// means Contracted does not acquire a load-order or assembly dependency on either mod.
    /// </summary>
    internal static class MoonContractOptionalTerminalCompatibility
    {
        private static readonly string[] CatalogueTypeNames =
        {
            "TerminalUtils.Nodes.MoonCatalogue",
            "TerminalFormatter.Nodes.Moons",
        };

        private static readonly HashSet<MethodBase> PatchedMethods = new HashSet<MethodBase>();
        private static Harmony _harmony;

        internal static void Initialize(Harmony harmony)
        {
            _harmony = harmony;
            EnsurePatched();
        }

        internal static void EnsurePatched()
        {
            if (_harmony == null)
                return;

            foreach (string typeName in CatalogueTypeNames)
            {
                try
                {
                    Type targetType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(assembly => assembly.GetType(typeName, throwOnError: false))
                        .FirstOrDefault(type => type != null);
                    MethodInfo getNodeText = targetType?.GetMethod(
                        "GetNodeText",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        binder: null,
                        types: new[] { typeof(TerminalNode) },
                        modifiers: null);
                    if (targetType == null) continue;
                    if (getNodeText == null || getNodeText.ReturnType != typeof(string))
                    {
                        ContractsBootstrap.Log?.LogWarning($"[TerminalCompat] Unsupported catalogue signature: {typeName}.GetNodeText(TerminalNode). Detail rows unavailable.");
                        continue;
                    }
                    if (PatchedMethods.Contains(getNodeText)) continue;

                    var postfix = new HarmonyMethod(AccessTools.Method(
                        typeof(MoonContractOptionalTerminalCompatibility),
                        nameof(CataloguePostfix))) { priority = Priority.Last };
                    _harmony.Patch(getNodeText, postfix: postfix);
                    PatchedMethods.Add(getNodeText);
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.TerminalCompat] Added contract detail-row adapter for {typeName}.GetNodeText.");
                }
                catch (Exception e)
                {
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.TerminalCompat] Could not patch {typeName}: {e.Message}");
                }
            }
        }

        private static void CataloguePostfix(ref string __result)
        {
            Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
            MoonContractTerminalTextPatch.AddCatalogueLabels(terminal, ref __result, detailRows: true);
        }
    }

    [HarmonyPatch(typeof(Terminal), "Start")]
    internal static class MoonContractOptionalTerminalCompatibilityStartPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Terminal __instance)
        {
            // Optional plugins are normally loaded before Contracted because of soft
            // dependencies, but retry at the first live terminal for unknown load orders.
            MoonContractOptionalTerminalCompatibility.EnsurePatched();
            MoonsPlusContractLabelPatch.EnsurePatched(__instance);
        }
    }
}
