using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core.Compat;
namespace Y4NGZCompany.Contracts.ShadowRaid
{
    internal static class BreachHardDriveItemRegistry
    {
        private const string DawnItemKey = "breach_hard_drive";
        private const string PrefabName = "LGU_BreachHardDriveItem";
        private const string ItemName = "Blacksite Hard Drive";
        private const float TargetMaxVisualDimension = 0.62f;
        private const float MinimumPickupWidth = 0.52f;
        private const float MinimumPickupHeight = 0.24f;
        private const float MinimumPickupDepth = 0.38f;

        /// <summary>Gap between the resting mesh's bottom face and the floor.</summary>
        private const float RestingFloorClearance = 0.015f;

        private static bool _registrationAttempted;

        /// <summary>Half the centred mesh height plus clearance: what Item.verticalOffset lifts the root by at rest (#789).</summary>
        internal static float GroundVerticalOffset { get; private set; } = 0.12f;

        internal const int ScrapValue = 200;
        internal static GameObject Prefab { get; private set; }
        internal static Item ItemProperties { get; private set; }
        internal static bool Registered { get; private set; }

        internal static bool Register()
        {
            if (Registered && Prefab != null && ItemProperties != null)
                return true;
            if (_registrationAttempted)
                return Prefab != null && ItemProperties != null;

            _registrationAttempted = true;

            GameObject visualPrefab = ContractAssetLoader.BreachHardDriveVisualPrefab;
            if (visualPrefab == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Blacksite] Could not register hard drive item; bundled BreachHardDrive visual was not found.");
                return false;
            }

            try
            {
                Prefab = BuildItemPrefab(visualPrefab);
                ItemProperties = BuildItemProperties(Prefab);

                PhysicsProp prop = Prefab.GetComponent<PhysicsProp>();
                prop.itemProperties = ItemProperties;
                ItemProperties.spawnPrefab = Prefab;

                DawnLibCompat.FixMixerGroups(Prefab);
                DawnLibCompat.RegisterNetworkPrefab(Prefab);
                DawnLibCompat.RegisterItem(DawnItemKey, ItemProperties);

                Registered = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.Blacksite] Registered the shared Blacksite hard drive as a DawnLib item prefab.");
                ContractAssetLoader.LogRegisteredPrefab("[MoonContracts.Blacksite]", Prefab, registered: true);
                return true;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts.Blacksite] Failed to register hard drive item prefab: {e}");
                if (Prefab != null)
                    UnityEngine.Object.DestroyImmediate(Prefab);
                Prefab = null;
                ItemProperties = null;
                return false;
            }
        }

        internal static bool TryGet(out Item itemProperties)
        {
            itemProperties = null;
            if (!Register())
                return false;

            itemProperties = ItemProperties;
            return itemProperties != null && itemProperties.spawnPrefab != null;
        }

        private static GameObject BuildItemPrefab(GameObject visualPrefab)
        {
            GameObject root = ContractAssetLoader.CreateHiddenPrefabRoot(PrefabName);
            SetTagSafe(root, "PhysicsProp");
            root.layer = ResolvePropsLayer();

            NetworkObject networkObject = root.AddComponent<NetworkObject>();
            networkObject.SynchronizeTransform = true;
            networkObject.SceneMigrationSynchronization = true;
            networkObject.SpawnWithObservers = true;
            networkObject.DontDestroyWithOwner = true;
            networkObject.AutoObjectParentSync = true;
            AssignStableNetworkHash(networkObject);

            Rigidbody body = root.AddComponent<Rigidbody>();
            body.isKinematic = false;
            body.useGravity = true;
            body.mass = 1.1f;
            body.drag = 0.15f;
            body.angularDrag = 4f;

            // Vanilla PocketItem always dereferences an AudioSource on the item root.
            // Missing it aborts the drive-to-drill inventory switch halfway through.
            AudioSource audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.minDistance = 1f;
            audio.maxDistance = 12f;

            PhysicsProp prop = root.AddComponent<PhysicsProp>();
            prop.grabbable = true;
            prop.grabbableToEnemies = false;
            prop.isInFactory = false;
            prop.scrapValue = ScrapValue;
            prop.rotateObject = false;
            prop.propBody = body;
            prop.originalScale = Vector3.one;
            prop.customGrabTooltip = "Grab hard drive";

            GameObject visual = UnityEngine.Object.Instantiate(visualPrefab, root.transform, false);
            visual.name = "BreachHardDriveVisual";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;
            StripRuntimeComponents(visual);
            NormalizeVisual(root.transform, visual);

            BoxCollider collider = CreatePickupCollider(root, visual);
            prop.propColliders = collider != null ? new Collider[] { collider } : Array.Empty<Collider>();
            prop.mainObjectRenderer = visual.GetComponentInChildren<MeshRenderer>(true);

            SetupScanNode(root, collider);
            root.AddComponent<BreachHardDriveItem>();

            return root;
        }

        private static Item BuildItemProperties(GameObject prefab)
        {
            Item item = ScriptableObject.CreateInstance<Item>();
            item.name = "LGU Blacksite Hard Drive Item";
            item.itemName = ItemName;
            item.itemIcon = ShadowRaidItemIcons.HardDrive;
            item.spawnPositionTypes = new List<ItemGroup>();
            item.twoHanded = false;
            item.twoHandedAnimation = false;
            item.canBeGrabbedBeforeGameStart = true;
            item.disallowUtilitySlot = true;
            item.weight = 1.03f;
            item.itemSpawnsOnGround = true;
            item.isConductiveMetal = true;
            item.isScrap = true;
            item.creditsWorth = ScrapValue;
            item.minValue = ScrapValue;
            item.maxValue = ScrapValue;
            item.spawnPrefab = prefab;
            item.requiresBattery = false;
            item.grabAnimationTime = 0.25f;
            item.syncGrabFunction = true;
            item.syncUseFunction = true;
            item.syncDiscardFunction = true;
            item.toolTips = Array.Empty<string>();
            item.verticalOffset = GroundVerticalOffset;
            item.floorYOffset = 0;
            item.allowDroppingAheadOfPlayer = true;
            item.restingRotation = Vector3.zero;
            item.rotationOffset = Vector3.zero;
            item.positionOffset = Vector3.zero;
            item.meshOffset = false;
            item.canBeInspected = true;
            return item;
        }

        // #789: the mesh is centred on the pickup root, the way vanilla scrap is, and
        // Item.verticalOffset lifts the root off the floor at rest. It used to sit entirely above
        // the root (bottom face at +0.015 m), which put the whole drive a half-height above the
        // hand anchor while held — the "floated" read — and the same offset above a soldier's
        // carry anchor.
        private static void NormalizeVisual(Transform root, GameObject visual)
        {
            if (root == null || visual == null || !TryGetLocalRendererBounds(root, visual, out Bounds bounds))
                return;

            float max = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.001f);
            visual.transform.localScale *= TargetMaxVisualDimension / max;
            if (!TryGetLocalRendererBounds(root, visual, out bounds))
                return;

            visual.transform.localPosition -= bounds.center;
            if (!TryGetLocalRendererBounds(root, visual, out bounds))
                return;

            GroundVerticalOffset = Mathf.Max(0.02f, bounds.extents.y + RestingFloorClearance);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Hard drive visual centred on pickup root: size={bounds.size.x:0.00},{bounds.size.y:0.00},{bounds.size.z:0.00}, " +
                $"center={bounds.center.x:0.00},{bounds.center.y:0.00},{bounds.center.z:0.00}, verticalOffset={GroundVerticalOffset:0.00}.");
        }

        private static BoxCollider CreatePickupCollider(GameObject root, GameObject visual)
        {
            foreach (Collider existing in root.GetComponentsInChildren<Collider>(true))
            {
                if (existing != null)
                    UnityEngine.Object.DestroyImmediate(existing);
            }

            BoxCollider collider = root.AddComponent<BoxCollider>();
            collider.isTrigger = false;
            if (TryGetLocalRendererBounds(root.transform, visual, out Bounds bounds))
            {
                collider.center = bounds.center;
                collider.size = new Vector3(
                    Mathf.Max(MinimumPickupWidth, bounds.size.x + 0.05f),
                    Mathf.Max(MinimumPickupHeight, bounds.size.y + 0.04f),
                    Mathf.Max(MinimumPickupDepth, bounds.size.z + 0.05f));
            }
            else
            {
                collider.center = new Vector3(0f, MinimumPickupHeight * 0.5f, 0f);
                collider.size = new Vector3(MinimumPickupWidth, MinimumPickupHeight, MinimumPickupDepth);
            }

            collider.gameObject.layer = ResolvePropsLayer();
            SetTagSafe(collider.gameObject, "PhysicsProp");
            return collider;
        }

        private static void SetupScanNode(GameObject root, BoxCollider pickupCollider)
        {
            var scanObject = new GameObject("ScanNode");
            scanObject.transform.SetParent(root.transform, false);
            scanObject.layer = ResolveLayer("ScanNode", 22);
            scanObject.transform.localPosition = pickupCollider != null
                ? pickupCollider.center + Vector3.up * 0.08f
                : new Vector3(0f, 0.18f, 0f);

            BoxCollider scanCollider = scanObject.AddComponent<BoxCollider>();
            scanCollider.isTrigger = true;
            scanCollider.size = pickupCollider != null
                ? pickupCollider.size + new Vector3(0.08f, 0.08f, 0.08f)
                : new Vector3(0.42f, 0.24f, 0.36f);

            ScanNodeProperties scanNode = scanObject.AddComponent<ScanNodeProperties>();
            scanNode.headerText = ItemName;
            scanNode.subText = "contract data";
            scanNode.maxRange = 24;
            scanNode.minRange = 1;
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = 0;
            scanNode.creatureScanID = -1;
            scanNode.scrapValue = ScrapValue;
        }

        private static bool TryGetLocalRendererBounds(Transform root, GameObject source, out Bounds bounds)
        {
            bounds = default;
            if (root == null || source == null)
                return false;

            Renderer[] renderers = source.GetComponentsInChildren<Renderer>(true);
            bool initialized = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                Bounds world = renderer.bounds;
                Vector3 min = world.min;
                Vector3 max = world.max;
                Vector3[] corners =
                {
                    new Vector3(min.x, min.y, min.z),
                    new Vector3(min.x, min.y, max.z),
                    new Vector3(min.x, max.y, min.z),
                    new Vector3(min.x, max.y, max.z),
                    new Vector3(max.x, min.y, min.z),
                    new Vector3(max.x, min.y, max.z),
                    new Vector3(max.x, max.y, min.z),
                    new Vector3(max.x, max.y, max.z),
                };

                for (int corner = 0; corner < corners.Length; corner++)
                {
                    Vector3 local = root.InverseTransformPoint(corners[corner]);
                    if (!initialized)
                    {
                        bounds = new Bounds(local, Vector3.zero);
                        initialized = true;
                    }
                    else
                    {
                        bounds.Encapsulate(local);
                    }
                }
            }

            return initialized;
        }

        private static void StripRuntimeComponents(GameObject root)
        {
            if (root == null)
                return;

            Component[] components = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || component is Transform || component is Renderer || component is MeshFilter)
                    continue;

                if (component is NetworkObject
                    || component is NetworkBehaviour
                    || component is NetworkTransform
                    || component is Rigidbody
                    || component is Collider
                    || component is AudioSource
                    || component is ScanNodeProperties)
                {
                    UnityEngine.Object.DestroyImmediate(component);
                }
            }
        }

        private static int ResolvePropsLayer()
        {
            return ResolveLayer("Props", ResolveLayer("InteractableObject", 6));
        }

        private static int ResolveLayer(string name, int fallback)
        {
            int layer = LayerMask.NameToLayer(name);
            return layer >= 0 ? layer : fallback;
        }

        private static void SetTagSafe(GameObject obj, string tag)
        {
            if (obj == null)
                return;

            try
            {
                obj.tag = tag;
            }
            catch
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Tag '{tag}' was not available for {obj.name}.");
            }
        }

        private static void AssignStableNetworkHash(NetworkObject networkObject)
        {
            if (networkObject == null)
                return;

            try
            {
                uint hash;
                using (MD5 md5 = MD5.Create())
                {
                    byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ContractsBootstrap.PLUGIN_GUID + ".Blacksite." + PrefabName));
                    hash = BitConverter.ToUInt32(bytes, 0);
                }

                FieldInfo field = typeof(NetworkObject).GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? typeof(NetworkObject).GetField("<GlobalObjectIdHash>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                    field.SetValue(networkObject, hash);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Failed to assign hard drive NetworkObject hash: {e.Message}");
            }
        }
    }
}
