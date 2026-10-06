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

namespace Y4NGZCompany.Contracts.Survey
{
    /// <summary>
    /// Builds the Survey Beacon item prefab around the bundled <c>SurveyBeacon</c> visual and
    /// registers it as a network prefab and DawnLib item (#759). Mirrors
    /// <see cref="ShadowRaid.BreachHardDriveItemRegistry"/>. When the visual is missing from
    /// <c>lgucontracts.bundle</c> nothing registers and the survey spawn falls back to the
    /// vanilla Radar Booster path it used before the beacon existed.
    /// </summary>
    internal static class SurveyBeaconItemRegistry
    {
        private const string DawnItemKey = "survey_beacon";
        private const string PrefabName = "LGU_SurveyBeaconItem";
        internal const string ItemName = "Survey Beacon";
        internal const string VisualName = "SurveyBeaconVisual";
        internal const string PickupProxyName = "Pickup";
        internal const string ScreenArrowName = "Screen_Arrow";

        /// <summary>
        /// The v2 beacon (#807) closes to 0.476 x 0.50 x 0.227 m including the mast stub;
        /// anything far past that is a unit slip.
        /// </summary>
        private const float MaximumVisualDimension = 0.65f;
        private const float MinimumPickupWidth = 0.40f;
        private const float MinimumPickupHeight = 0.25f;
        private const float MinimumPickupDepth = 0.18f;
        /// <summary>Body bottom in the item frame; the origin is the body centre.</summary>
        internal const float BodyBottomLocalY = -0.15f;

        private static bool _registrationAttempted;

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

            GameObject visualPrefab = ContractAssetLoader.SurveyBeaconVisualPrefab;
            if (visualPrefab == null)
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.Survey] SurveyBeacon visual was not found in lgucontracts.bundle; " +
                    "survey drops will use the vanilla Radar Booster fallback.");
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
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.Survey] Registered the Survey Beacon item prefab.");
                ContractAssetLoader.LogRegisteredPrefab("[MoonContracts.Survey]", Prefab, registered: true);
                return true;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts.Survey] Failed to register the Survey Beacon item prefab: {e}");
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

            // #773: vanilla items carry no Rigidbody; GrabbableObject moves them to the floor
            // itself and then stops. A simulated body kept falling after that, and its collider
            // is told by GrabbableObject.Start to ignore the Room layer, so the beacon sank
            // through the floor and the grab ray met the room first. Kinematic keeps the
            // propBody field satisfied without simulating.
            Rigidbody body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.mass = 0.9f;
            body.drag = 0.15f;
            body.angularDrag = 4f;

            PhysicsProp prop = root.AddComponent<PhysicsProp>();
            prop.grabbable = true;
            prop.grabbableToEnemies = false;
            prop.isInFactory = false;
            prop.scrapValue = 0;
            prop.rotateObject = false;
            prop.propBody = body;
            prop.originalScale = Vector3.one;
            prop.customGrabTooltip = "Grab survey beacon : [LMB]";

            GameObject visual = UnityEngine.Object.Instantiate(visualPrefab, root.transform, false);
            visual.name = VisualName;
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;
            StripRuntimeComponents(visual);
            OrientScreenForward(visual.transform);
            ClampVisualScale(root.transform, visual);

            BoxCollider collider = CreatePickupCollider(root, visual);
            prop.propColliders = collider != null ? new Collider[] { collider } : Array.Empty<Collider>();
            prop.mainObjectRenderer = FindBodyRenderer(visual);

            SetupScanNode(root, collider);
            return root;
        }

        private static Item BuildItemProperties(GameObject prefab)
        {
            Item item = ScriptableObject.CreateInstance<Item>();
            item.name = "LGU Survey Beacon Item";
            item.itemName = ItemName;
            item.spawnPositionTypes = new List<ItemGroup>();
            // #807: toolbox-sized, carried in front of the chest with both hands. Light, so the
            // survey stays a movement contract.
            item.twoHanded = true;
            item.twoHandedAnimation = true;
            item.grabAnim = "HoldForward";
            item.disableHandsOnWall = true;
            item.canBeGrabbedBeforeGameStart = true;
            item.disallowUtilitySlot = true;
            item.weight = 1.10f;
            item.itemSpawnsOnGround = true;
            item.isConductiveMetal = true;
            item.isScrap = false;
            item.creditsWorth = 0;
            item.minValue = 0;
            item.maxValue = 0;
            item.spawnPrefab = prefab;
            item.requiresBattery = false;
            item.automaticallySetUsingPower = false;
            item.batteryUsage = 0f;
            item.grabAnimationTime = 0.4f;
            item.itemIcon = ContractAssetLoader.SurveyBeaconIconSprite;
            item.syncGrabFunction = true;
            item.syncUseFunction = true;
            item.syncDiscardFunction = true;
            item.toolTips = Array.Empty<string>();
            // The prefab origin is the body centre and the box stands on its base, so the floor
            // offset is half the body height plus a hair. It rests upright.
            item.verticalOffset = -BodyBottomLocalY + 0.005f;
            item.floorYOffset = 0;
            item.allowDroppingAheadOfPlayer = true;
            item.restingRotation = Vector3.zero;
            // SurveyBeaconCarryPose solves the flat carry from both animated wrists after
            // vanilla updates the hand socket. These offsets are optional tuning deltas.
            item.rotationOffset = SurveyHeldPoseTuning.DefaultRotationOffset;
            item.positionOffset = SurveyHeldPoseTuning.DefaultPositionOffset;
            item.meshOffset = false;
            item.canBeInspected = false;
            return item;
        }

        /// <summary>
        /// The arrow sits on the screen face, so the direction from the body centre to the arrow
        /// pivot (flattened on the body's up) is the screen normal whatever axis convention the
        /// FBX import chose. Rotate the visual so that normal is the item's +Z with up kept up;
        /// the held/resting offsets above are written against that frame.
        /// </summary>
        private static void OrientScreenForward(Transform visual)
        {
            Transform arrow = FindChildByName(visual, ScreenArrowName);
            if (arrow == null)
                return;

            Vector3 normal = visual.InverseTransformPoint(arrow.position);
            normal.y = 0f;
            if (normal.sqrMagnitude < 0.0001f)
                return;

            normal.Normalize();
            visual.localRotation = Quaternion.Inverse(Quaternion.LookRotation(normal, Vector3.up));
        }

        private static void ClampVisualScale(Transform root, GameObject visual)
        {
            if (!TryGetLocalRendererBounds(root, visual, out Bounds bounds))
                return;

            float max = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.001f);
            if (max <= MaximumVisualDimension)
                return;

            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.Survey] SurveyBeacon visual spans {max:0.00} m; scaling it down to {MaximumVisualDimension:0.00} m. Check the FBX unit scale.");
            visual.transform.localScale *= MaximumVisualDimension / max;
        }

        private static MeshRenderer FindBodyRenderer(GameObject visual)
        {
            Transform body = FindChildByName(visual.transform, "Body");
            MeshRenderer bodyRenderer = body != null ? body.GetComponent<MeshRenderer>() : null;
            return bodyRenderer != null ? bodyRenderer : visual.GetComponentInChildren<MeshRenderer>(true);
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

            // The authored Pickup box is the grab volume. It is a helper mesh, so hide it once its
            // bounds are read; the arrow/ring/legs stay.
            Transform proxy = FindChildByName(visual.transform, PickupProxyName);
            bool sized = false;
            if (proxy != null)
            {
                // #773: sized from mesh data, not renderer bounds. The prefab is parked under an
                // inactive holder while it is built and renderer bounds are empty there, which
                // produced a zero-size grab box nobody could hover.
                if (TryGetLocalRendererBounds(root.transform, proxy.gameObject, out Bounds proxyBounds)
                    && proxyBounds.size.x > 0.02f && proxyBounds.size.y > 0.02f && proxyBounds.size.z > 0.02f)
                {
                    collider.center = proxyBounds.center;
                    collider.size = proxyBounds.size;
                    sized = true;
                }
                proxy.gameObject.SetActive(false);
            }

            if (!sized && TryGetLocalRendererBounds(root.transform, visual, out Bounds bounds) && bounds.size.sqrMagnitude > 0.0001f)
            {
                collider.center = bounds.center;
                collider.size = new Vector3(
                    Mathf.Max(MinimumPickupWidth, bounds.size.x + 0.03f),
                    Mathf.Max(MinimumPickupHeight, bounds.size.y + 0.03f),
                    Mathf.Max(MinimumPickupDepth, bounds.size.z + 0.03f));
                sized = true;
            }

            if (!sized)
            {
                collider.center = Vector3.zero;
                collider.size = new Vector3(MinimumPickupWidth, MinimumPickupDepth, MinimumPickupHeight);
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
                ? pickupCollider.center + Vector3.up * 0.05f
                : new Vector3(0f, 0.1f, 0f);

            BoxCollider scanCollider = scanObject.AddComponent<BoxCollider>();
            scanCollider.isTrigger = true;
            scanCollider.size = pickupCollider != null
                ? pickupCollider.size + new Vector3(0.08f, 0.08f, 0.08f)
                : new Vector3(0.3f, 0.3f, 0.3f);

            ScanNodeProperties scanNode = scanObject.AddComponent<ScanNodeProperties>();
            scanNode.headerText = ItemName;
            scanNode.subText = "Company survey equipment";
            scanNode.maxRange = 24;
            scanNode.minRange = 1;
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = 0;
            scanNode.creatureScanID = -1;
            scanNode.scrapValue = 0;
        }

        internal static Transform FindChildByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
                return null;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate == null || candidate == root)
                    continue;
                if (string.Equals(candidate.name, name, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Bounds of every mesh under <paramref name="source"/> in <paramref name="root"/>'s local
        /// frame, read from the shared mesh so an inactive hierarchy measures the same as an
        /// active one (#773).
        /// </summary>
        private static bool TryGetLocalRendererBounds(Transform root, GameObject source, out Bounds bounds)
        {
            bounds = default;
            if (root == null || source == null)
                return false;

            MeshFilter[] filters = source.GetComponentsInChildren<MeshFilter>(true);
            bool initialized = false;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                if (filter == null || filter.sharedMesh == null)
                    continue;

                Bounds meshBounds = filter.sharedMesh.bounds;
                Vector3 min = meshBounds.min;
                Vector3 max = meshBounds.max;
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
                    Vector3 local = root.InverseTransformPoint(filter.transform.TransformPoint(corners[corner]));
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
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Survey] Tag '{tag}' was not available for {obj.name}.");
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
                    byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ContractsBootstrap.PLUGIN_GUID + ".Survey." + PrefabName));
                    hash = BitConverter.ToUInt32(bytes, 0);
                }

                FieldInfo field = typeof(NetworkObject).GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? typeof(NetworkObject).GetField("<GlobalObjectIdHash>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                    field.SetValue(networkObject, hash);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Survey] Failed to assign the Survey Beacon NetworkObject hash: {e.Message}");
            }
        }
    }
}
