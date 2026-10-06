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
    internal static class BreachDrillItemRegistry
    {
        private const string DawnItemKey = "breach_drill";
        private const string PrefabName = "LGU_BreachDrillItem";
        private const string ItemName = "Shadow Raid Drill";
        private const float MinimumRootPickupWidth = 1.7f;
        private const float MinimumRootPickupHeight = 1.35f;
        private const float MinimumRootPickupDepth = 2.05f;
        private const float FallbackPickupRootGroundOffset = MinimumRootPickupHeight * 0.5f;
        private const float VisualFloorClearance = 0.03f;

        // --- Resting pose ------------------------------------------------------------------------
        // The visual is centred on the pickup root, its long axis is +Z (the pickup collider
        // minimums are 1.7 x 1.35 x 2.05, i.e. depth dominant) and its authored identity pose is
        // the drilling pose: standing on its base with the bit pointing horizontally forward, which
        // is exactly what AlignToContainer assumes when it only ever yaws the drill into the
        // container. DrillRestingRotation is the x/z euler vanilla forces once the item has settled
        // on the floor (y stays free); Item.verticalOffset lifts the root by half the mesh height.
        //
        // BreachDrillItem centres the drill between HoldForward's animated hands in each view,
        // bit forward. UpdateCarriedRepresentation scales
        // the visual root down while a player carries it so the ~1.5 m body does not swallow the
        // camera (#455). Blind hand-frame offsets (2026-09-02, -0.15/-0.35/0.55: mesh off screen)
        // and a camera-frame pose (2026-09-03: "floats in front of the player") both failed for a
        // full-size body; HeldItemFrameDiagnostics now logs the anchor frame so any future offset
        // is authored from a measurement.
        private static readonly Vector3 DrillRestingRotation = Vector3.zero;
        // ------------------------------------------------------------------------------------------

        private static bool _registrationAttempted;

        internal static GameObject Prefab { get; private set; }
        internal static Item ItemProperties { get; private set; }
        internal static bool Registered { get; private set; }
        internal static float GroundVerticalOffset { get; private set; } = 0.7f;

        /// <summary>Name of the visual root instantiated under the pickup root; the carried representation scales this transform.</summary>
        internal const string VisualRootName = "BreachDrillVisual";

        /// <summary>Full-size renderer bounds of the centred visual in the pickup root's frame (#455: sizes the carried representation).</summary>
        internal static Vector3 VisualSize { get; private set; } = new Vector3(1.7f, 1.35f, 2.05f);

        internal static bool Register()
        {
            if (Registered && Prefab != null && ItemProperties != null)
                return true;
            if (_registrationAttempted)
                return Prefab != null && ItemProperties != null;

            _registrationAttempted = true;

            GameObject visualPrefab = ContractAssetLoader.GetPreparedInteriorSupportPrefab(ContractsBootstrap.ShadowRaidDrillPrefabName.Value);
            if (visualPrefab == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Could not register Shadow Raid drill item; bundled visual '{ContractsBootstrap.ShadowRaidDrillPrefabName.Value}' was not found.");
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
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.ShadowRaid] Registered Shadow Raid drill as a DawnLib item prefab.");
                ContractAssetLoader.LogRegisteredPrefab("[MoonContracts.ShadowRaid]", Prefab, registered: true);
                return true;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts.ShadowRaid] Failed to register Shadow Raid drill item prefab: {e}");
                if (Prefab != null)
                    UnityEngine.Object.DestroyImmediate(Prefab);
                Prefab = null;
                ItemProperties = null;
                return false;
            }
        }

        internal static bool TryGet(out GameObject prefab, out Item itemProperties)
        {
            if (!Register())
            {
                prefab = null;
                itemProperties = null;
                return false;
            }

            prefab = Prefab;
            itemProperties = ItemProperties;
            return prefab != null && itemProperties != null;
        }

        private static GameObject BuildItemPrefab(GameObject visualPrefab)
        {
            GameObject root = ContractAssetLoader.CreateHiddenPrefabRoot(PrefabName);
            root.transform.localScale = Vector3.one;
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
            body.mass = 10f;
            body.drag = 0.12f;
            body.angularDrag = 8f;
            body.constraints = RigidbodyConstraints.FreezeRotation;

            PhysicsProp prop = root.AddComponent<PhysicsProp>();
            prop.grabbable = true;
            prop.grabbableToEnemies = false;
            prop.isInFactory = false;
            prop.scrapValue = 0;
            prop.rotateObject = false;
            prop.propBody = body;
            prop.originalScale = Vector3.one;
            prop.customGrabTooltip = "Grab Shadow Raid drill";

            AudioSource audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.minDistance = 2f;
            audio.maxDistance = 26f;
            audio.volume = 0.28f;

            GameObject visual = UnityEngine.Object.Instantiate(visualPrefab, root.transform, false);
            visual.name = "BreachDrillVisual";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;
            StripNonVisualItemComponents(visual);
            CenterVisualOnPickupRoot(root, visual);

            PromoteAnchor(root.transform, visual.transform, "AttachPoint");
            PromoteAnchor(root.transform, visual.transform, "ActualDrillTip");
            PromoteAnchor(root.transform, visual.transform, "DrillTip");
            PromoteAnchor(root.transform, visual.transform, "SparkOrigin");
            PromoteAnchor(root.transform, visual.transform, "ScreenAnchor");

            BoxCollider rootCollider = CreatePickupCollider(root, visual);
            prop.propColliders = rootCollider != null ? new Collider[] { rootCollider } : Array.Empty<Collider>();
            prop.mainObjectRenderer = visual.GetComponentInChildren<MeshRenderer>(true);

            SetupScanNode(root, rootCollider);

            BreachDrillItem drill = root.AddComponent<BreachDrillItem>();
            drill.InitializeRuntime(MoonContractState.GetShadowRaidDrillDurationSecondsForCurrentMoon(), ItemName);

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Shadow Raid drill item collider ready: layer={root.layer}, tag={root.tag}, center={FormatVector(rootCollider.center)}, size={FormatVector(rootCollider.size)}.");
            return root;
        }

        private static Item BuildItemProperties(GameObject prefab)
        {
            Item item = ScriptableObject.CreateInstance<Item>();
            item.name = "LGU Shadow Raid Drill Item";
            item.itemName = ItemName;
            item.itemIcon = ShadowRaidItemIcons.Drill;
            item.spawnPositionTypes = new List<ItemGroup>();
            item.twoHanded = true;
            item.twoHandedAnimation = true;
            item.grabAnim = "HoldForward";
            item.disableHandsOnWall = true;
            item.canBeGrabbedBeforeGameStart = true;
            item.disallowUtilitySlot = true;
            item.weight = 1.2f;
            item.itemSpawnsOnGround = true;
            item.isConductiveMetal = true;
            item.isScrap = false;
            item.creditsWorth = 0;
            item.minValue = 0;
            item.maxValue = 0;
            item.spawnPrefab = prefab;
            item.requiresBattery = false;
            item.batteryUsage = 0f;
            item.automaticallySetUsingPower = false;
            item.grabAnimationTime = 0.4f;
            item.syncGrabFunction = true;
            item.syncUseFunction = true;
            item.syncDiscardFunction = true;
            item.syncInteractLRFunction = true;
            item.toolTips = new[] { "Drop near Shadow Raid container" };
            item.verticalOffset = GroundVerticalOffset;
            item.floorYOffset = 0;
            item.allowDroppingAheadOfPlayer = true;
            // Resting: the mesh stays centred on the pickup root and verticalOffset is what lifts the
            // root so the drill rests on the floor instead of through it. Carried: zero hand-frame
            // offsets; BreachDrillItem owns both held poses (see the note above).
            item.restingRotation = DrillRestingRotation;
            item.rotationOffset = Vector3.zero;
            item.positionOffset = Vector3.zero;
            item.meshOffset = false;
            item.canBeInspected = true;
            return item;
        }

        // Vanilla scrap keeps its mesh centred on the pickup root and lets Item.verticalOffset raise the
        // root off the floor when the item rests. This used to bake the whole ground offset into the
        // visual's local Y instead, which left the mesh hanging ~1.2m under the hand anchor while held.
        private static void CenterVisualOnPickupRoot(GameObject root, GameObject visual)
        {
            if (root == null || visual == null)
                return;

            if (!TryGetLocalRendererBounds(root.transform, visual, out Bounds bounds))
                return;

            visual.transform.localPosition -= bounds.center;
            if (!TryGetLocalRendererBounds(root.transform, visual, out bounds))
                return;

            // With the mesh centred on the root, the root has to sit half a mesh height (measured in the
            // resting pose, so a tilted restingRotation stays correct) above the floor for the drill to
            // look like it is standing on the ground rather than sunk into it.
            GroundVerticalOffset = Mathf.Max(0.05f, GetRestingHalfHeight(bounds) + VisualFloorClearance);
            VisualSize = bounds.size;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Shadow Raid drill visual centred on pickup root: size={FormatVector(bounds.size)}, " +
                $"center={FormatVector(bounds.center)}, verticalOffset={GroundVerticalOffset:0.00}, " +
                $"restingRotation={FormatVector(DrillRestingRotation)}; carried representation is scaled by BreachDrillItem.UpdateCarriedRepresentation.");
        }

        // Half height of the centred mesh once DrillRestingRotation is applied about the root pivot.
        private static float GetRestingHalfHeight(Bounds localBounds)
        {
            Quaternion resting = Quaternion.Euler(DrillRestingRotation);
            Vector3 extents = localBounds.extents;
            float half = 0f;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? -extents.x : extents.x,
                    (i & 2) == 0 ? -extents.y : extents.y,
                    (i & 4) == 0 ? -extents.z : extents.z);
                half = Mathf.Max(half, Mathf.Abs((resting * corner).y));
            }

            return half;
        }

        private static BoxCollider CreatePickupCollider(GameObject root, GameObject visual)
        {
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider != null)
                    UnityEngine.Object.DestroyImmediate(collider);
            }

            BoxCollider colliderRoot = root.AddComponent<BoxCollider>();
            colliderRoot.isTrigger = false;
            if (TryGetLocalRendererBounds(root.transform, visual, out Bounds bounds))
            {
                colliderRoot.center = bounds.center;
                colliderRoot.size = new Vector3(
                    Mathf.Max(MinimumRootPickupWidth, Mathf.Abs(bounds.size.x) + 0.35f),
                    Mathf.Max(MinimumRootPickupHeight, Mathf.Abs(bounds.size.y) + 0.35f),
                    Mathf.Max(MinimumRootPickupDepth, Mathf.Abs(bounds.size.z) + 0.45f));
            }
            else
            {
                // No renderers to measure: assume a root-centred body of the minimum pickup size, so the
                // collider stays on the pivot and the resting offset is just half that body height.
                GroundVerticalOffset = FallbackPickupRootGroundOffset + VisualFloorClearance;
                colliderRoot.center = Vector3.zero;
                colliderRoot.size = new Vector3(MinimumRootPickupWidth, MinimumRootPickupHeight, MinimumRootPickupDepth);
            }

            colliderRoot.gameObject.layer = ResolvePropsLayer();
            SetTagSafe(colliderRoot.gameObject, "PhysicsProp");
            return colliderRoot;
        }

        private static void SetupScanNode(GameObject root, BoxCollider pickupCollider)
        {
            var scanObject = new GameObject("ScanNode");
            scanObject.transform.SetParent(root.transform, false);
            scanObject.layer = ResolveLayer("ScanNode", 22);
            scanObject.transform.localPosition = pickupCollider != null
                ? pickupCollider.center + Vector3.up * 0.2f
                : new Vector3(0f, 0.75f, 0f);

            BoxCollider scanCollider = scanObject.AddComponent<BoxCollider>();
            scanCollider.isTrigger = true;
            scanCollider.size = pickupCollider != null
                ? pickupCollider.size + new Vector3(0.15f, 0.15f, 0.15f)
                : new Vector3(1.3f, 0.9f, 1.3f);

            ScanNodeProperties scanNode = scanObject.AddComponent<ScanNodeProperties>();
            scanNode.headerText = ItemName;
            scanNode.subText = "drop near Shadow Raid container";
            scanNode.maxRange = 30;
            scanNode.minRange = 1;
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = 0;
            scanNode.creatureScanID = -1;
            scanNode.scrapValue = 0;
        }

        private static void PromoteAnchor(Transform root, Transform visualRoot, string name)
        {
            if (root == null || visualRoot == null || root.Find(name) != null)
                return;

            Transform source = FindDeepChild(visualRoot, name);
            if (source == null)
                return;

            Transform anchor = new GameObject(name).transform;
            anchor.SetParent(root, false);
            anchor.localPosition = root.InverseTransformPoint(source.position);
            anchor.localRotation = Quaternion.Inverse(root.rotation) * source.rotation;
            anchor.localScale = source.lossyScale;
        }

        private static Transform FindDeepChild(Transform root, string name)
        {
            if (root == null)
                return null;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
                    return child;

                Transform nested = FindDeepChild(child, name);
                if (nested != null)
                    return nested;
            }

            return null;
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

        private static void StripNonVisualItemComponents(GameObject root)
        {
            if (root == null)
                return;

            Component[] components = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || component is Transform)
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
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Tag '{tag}' was not available for {obj.name}.");
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
                {
                    field.SetValue(networkObject, hash);
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Drill NetworkObject hash set: {hash}.");
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Failed to assign drill NetworkObject hash: {e.Message}");
            }
        }

        private static string FormatVector(Vector3 value)
        {
            return $"{value.x:0.0},{value.y:0.0},{value.z:0.0}";
        }
    }
}
