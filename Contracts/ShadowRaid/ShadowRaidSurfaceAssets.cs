using System;
using System.IO;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts.ShadowRaid
{
    // Shared, authored HDRP surfaces. No texture processing or per-frame material allocations.
    internal static class ShadowRaidSurfaceAssets
    {
        internal const string BundleName = "y4ngz-shadow-raid-surfaces.bundle";
        private static bool _attempted;
        private static AssetBundle _bundle;
        private static Material _container, _floor, _crate;
        private static GameObject _containerLabel, _crateLabel, _floorWear;

        private static bool Load()
        {
            if (_attempted) return _bundle != null;
            _attempted = true;
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(ShadowRaidSurfaceAssets).Assembly.Location), BundleName);
                if (!File.Exists(path)) return false;
                _bundle = AssetBundle.LoadFromFile(path);
                if (_bundle == null) return false;
                _container = _bundle.LoadAsset<Material>("ShadowRaidContainerWeathered");
                _floor = _bundle.LoadAsset<Material>("ShadowRaidContainerFloor");
                _crate = _bundle.LoadAsset<Material>("ShadowRaidCrateWeathered");
                _containerLabel = _bundle.LoadAsset<GameObject>("ShadowRaidFreightMarking");
                _crateLabel = _bundle.LoadAsset<GameObject>("ShadowRaidDataMarking");
                _floorWear = _bundle.LoadAsset<GameObject>("ShadowRaidFloorWear");
                if (_container == null || _floor == null || _crate == null)
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Surface bundle is incomplete; missing surfaces retain their original materials.");
                else
                    ContractsBootstrap.Log?.LogInfo("[MoonContracts.ShadowRaid] Loaded weathered container and crate surfaces.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Surface bundle unavailable; retaining original materials: {e.Message}");
            }
            return _bundle != null;
        }

        internal static void ApplyContainer(GameObject root)
        {
            if (root == null || !Load()) return;
            Renderer sidewall = null;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null || materials[i].name.IndexOf("HQ Shipping Container", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var replacement = renderer.name.IndexOf("Floor", StringComparison.OrdinalIgnoreCase) >= 0 ? _floor : _container;
                    if (replacement == null) continue;
                    materials[i] = replacement; changed = true;
                }
                if (changed)
                {
                    renderer.sharedMaterials = materials;
                    if (renderer.name == "Sidewall") sidewall = renderer;
                    if (_floorWear != null && renderer.name.StartsWith("Floor", StringComparison.OrdinalIgnoreCase))
                    {
                        string name = "FloorWear_" + renderer.name;
                        if (root.transform.Find(name) == null)
                        {
                            Bounds floorBounds = LocalBounds(root.transform, renderer);
                            var wear = Place(root.transform, _floorWear, name,
                                new Vector3(floorBounds.center.x, floorBounds.max.y + .003f, floorBounds.center.z), Quaternion.identity);
                            wear.transform.localScale = new Vector3(floorBounds.size.x / 3f, 1f, floorBounds.size.z / 2.56f);
                        }
                    }
                }
            }
            // Labels attach only to the known source sidewall, not generated/custom containers.
            if (sidewall != null && _containerLabel != null && root.transform.Find("FreightMarkingLeft") == null)
            {
                Bounds bounds = LocalBounds(root.transform, sidewall);
                Place(root.transform, _containerLabel, "FreightMarkingLeft", new Vector3(bounds.center.x, bounds.center.y + .38f, bounds.min.z - .007f), Quaternion.identity);
                Place(root.transform, _containerLabel, "FreightMarkingRight", new Vector3(bounds.center.x, bounds.center.y + .38f, bounds.max.z + .007f), Quaternion.Euler(0, 180, 0));
            }
        }

        internal static void ApplyCrate(GameObject root)
        {
            if (root == null || !Load() || _crate == null) return;
            Renderer body = null;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                    if (materials[i] != null && materials[i].name == "BlacksiteCrateMat")
                    { materials[i] = _crate; changed = true; }
                if (changed)
                {
                    renderer.sharedMaterials = materials;
                    if (renderer.name == "CrateBody") body = renderer;
                }
            }
            if (body != null && _crateLabel != null && root.transform.Find("DataMarking") == null)
            {
                Bounds bounds = LocalBounds(root.transform, body);
                Place(root.transform, _crateLabel, "DataMarking", new Vector3(bounds.center.x, bounds.center.y, bounds.min.z - .007f), Quaternion.identity);
            }
        }

        private static Bounds LocalBounds(Transform root, Renderer renderer)
        {
            Bounds world = renderer.bounds;
            Bounds local = new Bounds(root.InverseTransformPoint(world.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                local.Encapsulate(root.InverseTransformPoint(world.center + Vector3.Scale(world.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return local;
        }

        private static GameObject Place(Transform parent, GameObject prefab, string name, Vector3 position, Quaternion rotation)
        {
            var label = UnityEngine.Object.Instantiate(prefab, parent, false);
            label.name = name;
            label.transform.localPosition = position;
            label.transform.localRotation = rotation;
            return label;
        }
    }
}