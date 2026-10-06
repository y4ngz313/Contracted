using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Y4NGZCompany.Core.Compatibility
{
    /// <summary>
    /// Repairs vanilla SandSpider search references before its AI interval dereferences them.
    /// Relocation mods can restore a null inside favorite spot, leaving homeNode null on the
    /// peer that owns the enemy and causing every later DoAIInterval to throw.
    /// </summary>
    [HarmonyPatch(typeof(SandSpiderAI), nameof(SandSpiderAI.DoAIInterval))]
    internal static class SandSpiderSearchStateGuardPatch
    {
        private static readonly HashSet<int> MissingHomeNodeWarnings = new HashSet<int>();

        [HarmonyPrefix]
        private static bool RepairSearchState(SandSpiderAI __instance)
        {
            if (__instance == null || __instance.isEnemyDead)
                return true;

            bool repairedSearch = false;
            bool repairedHome = false;

            if (__instance.patrolHomeBase == null)
            {
                __instance.patrolHomeBase = new AISearchRoutine
                {
                    searchWidth = 17f,
                    searchPrecision = 3f,
                };
                repairedSearch = true;
            }

            int state = __instance.currentBehaviourStateIndex;
            if ((state == 0 || state == 1) && __instance.homeNode == null)
            {
                Transform replacement = __instance.favoriteSpot;
                if (replacement == null)
                {
                    try
                    {
                        replacement = __instance.ChooseClosestNodeToPosition(
                            __instance.transform.position,
                            avoidLineOfSight: false,
                            offset: 2);
                    }
                    catch (Exception ex)
                    {
                        LogMissingHomeNodeOnce(__instance, state, ex.Message);
                        return false;
                    }
                }

                if (replacement == null)
                {
                    LogMissingHomeNodeOnce(__instance, state, "no AI node was available");
                    return false;
                }

                __instance.homeNode = replacement;
                if (__instance.favoriteSpot == null)
                    __instance.favoriteSpot = replacement;
                repairedHome = true;
            }

            if (repairedSearch || repairedHome)
            {
                Plugin.Log?.LogWarning(
                    $"[Compatibility][SandSpider] Repaired search state before DoAIInterval " +
                    $"(enemy='{__instance.name}', state={state}, patrol={repairedSearch}, home={repairedHome}).");
            }

            return true;
        }

        private static void LogMissingHomeNodeOnce(SandSpiderAI spider, int state, string reason)
        {
            int instanceId = spider.GetInstanceID();
            if (!MissingHomeNodeWarnings.Add(instanceId))
                return;

            Plugin.Log?.LogWarning(
                $"[Compatibility][SandSpider] Skipping DoAIInterval for '{spider.name}' in state {state}: " +
                $"homeNode is null and could not be repaired ({reason}).");
        }
    }
}
