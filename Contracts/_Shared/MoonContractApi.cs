using System;
using UnityEngine;

using Y4NGZCompany.Contracts.Defuse;
namespace Y4NGZCompany.Contracts._Shared
{
    public static class MoonContractApi
    {
        public static void RefreshAssignments(string reason)
        {
            MoonContractState.RefreshAssignments(reason);
        }

        public static void ForceRefreshAssignments(string reason)
        {
            MoonContractState.RefreshAssignments(reason, force: true);
        }

        public static bool LockContractForLevel(int levelIndex, string reason)
        {
            return MoonContractState.LockContractForLevel(levelIndex, reason);
        }

        public static bool LockContractForLevelId(int levelId, string reason)
        {
            return MoonContractState.LockContractForLevelId(levelId, reason);
        }

        public static string GetDisplayNameForLevel(int levelIndex)
        {
            return MoonContractState.GetDisplayNameForLevel(levelIndex);
        }

        public static string GetDisplayNameForLevelId(int levelId)
        {
            return MoonContractState.GetDisplayNameForLevelId(levelId);
        }

        public static string GetShortNameForLevel(int levelIndex)
        {
            return MoonContractState.GetShortNameForLevel(levelIndex);
        }

        public static string GetShortNameForLevelId(int levelId)
        {
            return MoonContractState.GetShortNameForLevelId(levelId);
        }

        public static string GetGameplayDescriptionForLevel(int levelIndex)
        {
            return MoonContractState.GetGameplayDescriptionForLevel(levelIndex);
        }

        public static string GetGameplayDescriptionForLevelId(int levelId)
        {
            return MoonContractState.GetGameplayDescriptionForLevelId(levelId);
        }

        public static string GetTitleForLevelId(int levelId)
        {
            return MoonContractCatalog.Get(MoonContractState.GetContractForLevelId(levelId)).Title;
        }

        public static string GetTitleForLevel(int levelIndex)
        {
            return MoonContractCatalog.Get(MoonContractState.GetContractForLevel(levelIndex)).Title;
        }

        public static string GetBriefingForLevelId(int levelId)
        {
            return MoonContractCatalog.Get(MoonContractState.GetContractForLevelId(levelId)).Briefing;
        }

        public static string GetBriefingForLevel(int levelIndex)
        {
            return MoonContractCatalog.Get(MoonContractState.GetContractForLevel(levelIndex)).Briefing;
        }

        public static string GetArtForLevelId(int levelId)
        {
            return MoonContractCatalog.Get(MoonContractState.GetContractForLevelId(levelId)).Art;
        }

        public static string GetArtForLevel(int levelIndex)
        {
            return MoonContractCatalog.Get(MoonContractState.GetContractForLevel(levelIndex)).Art;
        }

        public static string GetFieldSupportStatusText()
        {
            return DefuseContract.GetFieldSupportStatusText();
        }

        public static string GetDefuseManualText()
        {
            return DefuseContract.GetManualText();
        }

        public static string GetDefuseDiagnosticInfoText()
        {
            return DefuseContract.GetDiagnosticInfoText();
        }

        public static string SubmitDefuseDiagnostic(
            string familyName,
            string displayName,
            int redCount,
            int blueCount,
            int yellowCount,
            int greenCount,
            int whiteCount,
            int blackCount,
            int totalWires,
            string wireOrderCsv)
        {
            return DefuseContract.SubmitDiagnostic(
                familyName,
                displayName,
                redCount,
                blueCount,
                yellowCount,
                greenCount,
                whiteCount,
                blackCount,
                totalWires,
                wireOrderCsv);
        }

        [Obsolete("Use GetDefuseManualText.")]
        public static string GetDeadDropManualText()
        {
            return GetDefuseManualText();
        }

        public static string GetDefuseActiveProfileSummary()
        {
            return DefuseContract.GetActiveProfileSummary();
        }

        [Obsolete("Use GetDefuseActiveProfileSummary.")]
        public static string GetDeadDropActiveProfileSummary()
        {
            return GetDefuseActiveProfileSummary();
        }

        public static GameObject CreateDefusePlacementPreview()
        {
            return DefuseContract.CreatePlacementPreview();
        }

        [Obsolete("Use CreateDefusePlacementPreview.")]
        public static GameObject CreateDeadDropPlacementPreview()
        {
            return CreateDefusePlacementPreview();
        }

        public static string GetAssignableContractNameForLevelId(int levelId, string requestedContract)
        {
            return MoonContractState.GetAssignableContractNameForLevelId(levelId, requestedContract);
        }

        public static bool TrySetContractForLevelId(int levelId, string contractName)
        {
            return MoonContractState.TrySetContractForLevelId(levelId, contractName);
        }
    }
}
