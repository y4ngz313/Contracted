using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.Bundy
{
    internal static class BundyPairedActionCatalog
    {
        private const string MetadataAssetName = "bundy_paired_actions";

        private static readonly Dictionary<string, BundyPairedActionDefinition> ActionsById =
            new Dictionary<string, BundyPairedActionDefinition>(StringComparer.Ordinal);

        internal static int ActionCount => ActionsById.Count;

        internal static void Register(AssetBundle bundle)
        {
            ActionsById.Clear();

            string metadataJson = null;
            if (bundle != null)
            {
                TextAsset metadata = bundle.LoadAsset<TextAsset>(MetadataAssetName);
                if (metadata == null)
                {
                    foreach (string assetName in bundle.GetAllAssetNames())
                    {
                        if (assetName.EndsWith("/" + MetadataAssetName + ".json", StringComparison.OrdinalIgnoreCase))
                        {
                            metadata = bundle.LoadAsset<TextAsset>(assetName);
                            break;
                        }
                    }
                }

                if (metadata != null)
                    metadataJson = metadata.text;
            }

            // The clean-probe bundle ships animation-only, so the paired timing/anchor metadata
            // (harvested from the legacy bundle) is deployed as a loose JSON next to the plugin.
            if (string.IsNullOrEmpty(metadataJson))
                metadataJson = TryLoadLooseMetadataJson();

            if (string.IsNullOrEmpty(metadataJson))
            {
                LogWarning($"Paired action catalog missing from bundle and loose file: {MetadataAssetName}.");
                return;
            }

            BundyPairedActionCatalogData catalog;
            try
            {
                catalog = JsonUtility.FromJson<BundyPairedActionCatalogData>(metadataJson);
            }
            catch (Exception e)
            {
                LogWarning($"Paired action catalog parse failed: {e.GetType().Name}: {e.Message}");
                return;
            }

            if (catalog == null || catalog.actions == null || catalog.actions.Length == 0)
            {
                LogWarning("Paired action catalog is empty.");
                return;
            }

            int warningCount = 0;
            for (int i = 0; i < catalog.actions.Length; i++)
            {
                BundyPairedActionDefinition action = catalog.actions[i];
                if (action == null || string.IsNullOrEmpty(action.id))
                    continue;

                ActionsById[action.id] = action;
                warningCount += action.warnings != null ? action.warnings.Length : 0;
            }

            LogInfo(
                $"Paired action catalog loaded: actions={ActionsById.Count}, " +
                $"schema={catalog.schemaVersion}, generatedAtUtc={catalog.generatedAtUtc}, warnings={warningCount}.");
        }

        internal static bool TryGet(string actionId, out BundyPairedActionDefinition action)
        {
            if (string.IsNullOrEmpty(actionId))
            {
                action = null;
                return false;
            }

            return ActionsById.TryGetValue(actionId, out action);
        }

        private static string TryLoadLooseMetadataJson()
        {
            try
            {
                string assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(assemblyDir))
                    return null;

                string path = Path.Combine(assemblyDir, MetadataAssetName + ".json");
                if (!File.Exists(path))
                    return null;

                string json = File.ReadAllText(path);
                LogInfo($"Paired action catalog loaded from loose file: {path}");
                return json;
            }
            catch (Exception e)
            {
                LogWarning($"Paired action catalog loose file load failed: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        private static void LogInfo(string message)
        {
            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogInfo("[Bundy] " + message);
        }

        private static void LogWarning(string message)
        {
            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogWarning("[Bundy] " + message);
        }
    }

#pragma warning disable 0649

    [Serializable]
    internal sealed class BundyPairedActionCatalogData
    {
        public int schemaVersion;
        public string generatedAtUtc;
        public BundyPairedActionDefinition[] actions;
    }

    [Serializable]
    internal sealed class BundyPairedActionDefinition
    {
        public string id;
        public string variant;
        public string kind;
        public int entryIndex;
        public int bodyIndex;
        public string anchorMode;
        public string[] attackerSequence;
        public string[] victimSequence;
        public string[] victimClipIds;
        public float duration;
        public float attackerDuration;
        public float victimDuration;
        public int damageClipIndex;
        public float damageNormalizedTime;
        public int releaseClipIndex;
        public float releaseNormalizedTime;
        public BundyPairedActionSample[] samples;
        public string[] warnings;
    }

    [Serializable]
    internal sealed class BundyPairedActionSample
    {
        public float normalizedTime;
        public float time;
        public BundyVector3Data attackerAnchor;
        public BundyVector3Data victimAnchor;
        public BundyVector3Data anchorDelta;
        public float horizontalDistance;
        public float facingDot;
    }

    [Serializable]
    internal sealed class BundyVector3Data
    {
        public float x;
        public float y;
        public float z;
    }

#pragma warning restore 0649
}
