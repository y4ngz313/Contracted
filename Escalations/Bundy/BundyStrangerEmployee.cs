using System;
using System.IO;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Escalations.Bundy
{
    /// <summary>#897: optional employee-body presentation, with the existing sack as the absent-safe fallback.</summary>
    internal static class BundyStrangerEmployee
    {
        internal const string SidecarBundleFileName = "y4ngz-stranger-employee.bundle";
        private static bool s_resolved;
        private static GameObject s_prefab;
        private static AssetBundle s_bundle;

        internal static bool TryApplyToInstance(BundyAIBase instance)
        {
            if (instance == null || !BundyStrangerSack.Enabled) return false;
            var existing = instance.GetComponent<BundyStrangerEmployeeMarker>();
            if (existing != null && existing.Root != null) return true;
            GameObject prefab = ResolvePrefab();
            if (prefab == null) return false;
            if (!BundyStrangerEmployeeRig.TryAttach(instance.gameObject, instance.creatureAnimator, prefab, out var marker, out string failure))
            {
                BundyPlugin.Log?.LogWarning("[Bundy] Employee body skipped; retaining the sack fallback: " + failure);
                return false;
            }
            RefreshVisibilityCaches(instance);
            BundyPlugin.Log?.LogInfo($"[Bundy] Employee body attached: renderers={marker.Renderers.Length}, " +
                $"originalRenderersHidden={marker.ReplacedRenderers.Length}, canonicalBones=68, controller unchanged.");
            return true;
        }

        internal static void RefreshVisibilityCaches(BundyAIBase instance)
        {
            var marker = instance != null ? instance.GetComponent<BundyStrangerEmployeeMarker>() : null;
            if (marker == null || marker.Root == null) return;
            instance.skinnedMeshRenderers = BundyStrangerEmployeeRig.FilterVisibilityCache(
                instance.skinnedMeshRenderers ?? instance.GetComponentsInChildren<SkinnedMeshRenderer>(true), marker.Renderers, marker.ReplacedRenderers);
            instance.meshRenderers = BundyStrangerEmployeeRig.FilterVisibilityCache(
                instance.meshRenderers ?? instance.GetComponentsInChildren<MeshRenderer>(true), Array.Empty<MeshRenderer>(), marker.ReplacedRenderers);
            foreach (Renderer original in marker.ReplacedRenderers) if (original != null) original.enabled = false;
        }

        private static GameObject ResolvePrefab()
        {
            if (s_resolved) return s_prefab;
            s_resolved = true;
            try
            {
                s_prefab = BundyPlugin.ModAssets != null
                    ? BundyPlugin.ModAssets.LoadAsset<GameObject>(BundyStrangerEmployeeRig.PrefabName) : null;
                if (s_prefab != null) return s_prefab;
                string directory = Path.GetDirectoryName(typeof(BundyStrangerEmployee).Assembly.Location);
                string path = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, SidecarBundleFileName);
                if (path == null || !File.Exists(path))
                {
                    BundyPlugin.Log?.LogInfo("[Bundy] Employee body bundle is absent; using the existing sack presentation.");
                    return null;
                }
                s_bundle = AssetBundle.LoadFromFile(path);
                s_prefab = s_bundle != null ? s_bundle.LoadAsset<GameObject>(BundyStrangerEmployeeRig.PrefabName) : null;
                if (s_prefab == null)
                    BundyPlugin.Log?.LogWarning("[Bundy] Employee body bundle has no valid named prefab; using the sack fallback.");
                return s_prefab;
            }
            catch (Exception exception)
            {
                BundyPlugin.Log?.LogWarning("[Bundy] Employee body bundle could not load; using the sack fallback. " + exception.Message);
                return null;
            }
        }
    }
}
