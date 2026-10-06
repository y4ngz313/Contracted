using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;

namespace Y4NGZCompany.Escalations.Congregation
{
    /// <summary>
    /// #845 C: what a killed congregant leaves behind. Red and black drop a long knife (a vanilla
    /// <see cref="KnifeItem"/> weapon sold as scrap), the drummer its drumstick (scrap only). A
    /// congregant the Seraphim kills leaves nothing.
    ///
    /// <b>Registration is symmetric.</b> Both network prefabs are built at plugin load on every peer.
    /// The knife is always registered; the drumstick only when the props sidecar carries its model,
    /// and host and client ship the same sidecar. The vanilla knife parts (icon, handling and blade
    /// sounds, blood particle, interim mesh) are not loaded at plugin load, so they are borrowed at
    /// <c>StartOfRound.Awake</c> on every peer, before any item of a session is instantiated. They
    /// add no NetworkBehaviour, so a peer that failed to borrow them still matches the host.
    /// </summary>
    internal static class CongregationLoot
    {
        // Scrap value ranges, rolled by the server per drop. The drumstick's is a first guess to tune.
        private const int KnifeMinValue = 30;
        private const int KnifeMaxValue = 60;
        private const int DrumstickMinValue = 15;
        private const int DrumstickMaxValue = 30;
        private const int PropsLayer = 6;
        private const int ScanNodeLayer = 22;
        private const string VanillaKnifeAssetName = "Knife";
        // The vanilla KnifeItem pickup box, in its prefab root's 0.0454 scale, converted to metres.
        private static readonly Vector3 VanillaKnifePickupCenter = new Vector3(0f, -0.006f, 0.2285f);
        private static readonly Vector3 VanillaKnifePickupSize = new Vector3(0.345f, 0.276f, 0.792f);
        private static readonly Vector3 DrumstickFallbackPickupSize = new Vector3(0.06f, 0.06f, 0.45f);
        private static readonly Vector3 MinimumPickupSize = new Vector3(0.06f, 0.06f, 0.06f);

        private static bool _registered;
        private static GameObject _knifePrefab;
        private static Item _knifeItem;
        private static KnifeItem _knife;
        private static AudioSource _knifeRootAudio;
        // Carries the vanilla prefab root's scale, so the borrowed blood particle and interim mesh
        // keep their authored local poses.
        private static Transform _vanillaKnifeFrame;
        private static bool _knifeHasCustomModel;
        private static bool _knifeHydrated;
        private static bool _knifeMissingWarned;
        private static GameObject _drumstickPrefab;
        private static Item _drumstickItem;

        /// <summary>Plugin load, every peer: builds and registers the loot network prefabs.</summary>
        internal static void Register()
        {
            if (_registered)
                return;
            _registered = true;

            RegisterKnife();
            RegisterDrumstick();
            HydrateVanillaKnife(warnIfMissing: false);
        }

        /// <summary>Every peer at StartOfRound.Awake: borrows the vanilla knife's parts once. A
        /// missing vanilla knife is one warning; the server then drops no knife.</summary>
        internal static void HydrateVanillaKnife(bool warnIfMissing)
        {
            if (_knifeHydrated || _knifePrefab == null)
                return;

            Item vanilla = FindVanillaKnifeItem();
            KnifeItem source = vanilla != null ? vanilla.spawnPrefab.GetComponent<KnifeItem>() : null;
            if (source == null)
            {
                if (warnIfMissing && !_knifeMissingWarned)
                {
                    _knifeMissingWarned = true;
                    ContractsBootstrap.Log?.LogWarning(
                        "[MoonContracts.Cult] No vanilla KnifeItem found; congregants drop no long knife this session.");
                }
                return;
            }

            _knifeHydrated = true;
            _knifeItem.itemIcon = vanilla.itemIcon;
            _knifeItem.grabSFX = vanilla.grabSFX;
            _knifeItem.dropSFX = vanilla.dropSFX;
            _knifeItem.pocketSFX = vanilla.pocketSFX;
            _knifeItem.throwSFX = vanilla.throwSFX;
            _knife.swingSFX = source.swingSFX;
            _knife.hitSFX = source.hitSFX;

            Transform vanillaRoot = source.transform;
            _vanillaKnifeFrame.localScale = vanillaRoot.localScale;
            AudioSource vanillaRootAudio = source.GetComponent<AudioSource>();
            if (vanillaRootAudio != null && vanillaRootAudio.outputAudioMixerGroup != null)
                _knifeRootAudio.outputAudioMixerGroup = vanillaRootAudio.outputAudioMixerGroup;
            if (source.knifeAudio != null && source.knifeAudio.outputAudioMixerGroup != null)
                _knife.knifeAudio.outputAudioMixerGroup = source.knifeAudio.outputAudioMixerGroup;

            // KnifeItem.HitKnife plays bloodParticle on every damaging stab, so it must not be null.
            if (source.bloodParticle != null)
            {
                GameObject blood = Object.Instantiate(source.bloodParticle.gameObject, _vanillaKnifeFrame, false);
                blood.name = source.bloodParticle.gameObject.name;
                _knife.bloodParticle = blood.GetComponent<ParticleSystem>();
            }

            // Interim model until the sidecar carries CongregationKnife: the vanilla knife mesh,
            // which sits on the vanilla prefab root itself.
            bool interim = false;
            MeshFilter vanillaMesh = source.GetComponent<MeshFilter>();
            MeshRenderer vanillaRenderer = source.GetComponent<MeshRenderer>();
            if (!_knifeHasCustomModel && vanillaMesh != null && vanillaRenderer != null)
            {
                var model = new GameObject("InterimVanillaKnifeModel") { layer = PropsLayer };
                model.transform.SetParent(_vanillaKnifeFrame, false);
                model.AddComponent<MeshFilter>().sharedMesh = vanillaMesh.sharedMesh;
                model.AddComponent<MeshRenderer>().sharedMaterials = vanillaRenderer.sharedMaterials;
                interim = true;
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Cult] Long knife loot borrowed the vanilla '{vanilla.name}' parts; model="
                + (_knifeHasCustomModel ? CongregationPropsAssets.KnifeName : interim ? "interim vanilla knife" : "none") + ".");
        }

        /// <summary>Server only: spawns one loot item at a reachable point near <paramref name="origin"/>,
        /// rolls its scrap value and adds it to the level total. Null when the kind has no registered
        /// or usable prefab; the caller syncs the value to clients.</summary>
        internal static GrabbableObject DropServer(CongregationPropKind kind, Vector3 origin, float yaw, bool outside)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer)
                return null;

            GameObject prefab;
            Item item;
            if (kind == CongregationPropKind.Knife && _knifeHydrated)
            {
                prefab = _knifePrefab;
                item = _knifeItem;
            }
            else if (kind == CongregationPropKind.Drumstick)
            {
                prefab = _drumstickPrefab;
                item = _drumstickItem;
            }
            else
            {
                return null;
            }
            if (prefab == null || item == null)
                return null;

            Transform props = StartOfRound.Instance != null ? StartOfRound.Instance.propsContainer : null;
            Vector3 point = BlacksiteDriveCarrier.ResolveReachableDropPoint(origin);
            GameObject spawned = Object.Instantiate(prefab, point, Quaternion.Euler(0f, yaw, 0f), props);
            spawned.SetActive(true);
            GrabbableObject grabbable = spawned.GetComponent<GrabbableObject>();
            grabbable.isInFactory = !outside;
            grabbable.startFallingPosition = spawned.transform.localPosition;
            grabbable.targetFloorPosition = grabbable.startFallingPosition;
            grabbable.fallTime = 0f;
            spawned.GetComponent<NetworkObject>().Spawn();

            int value = Random.Range(item.minValue, item.maxValue + 1);
            grabbable.SetScrapValue(value);
            if (RoundManager.Instance != null)
                RoundManager.Instance.totalScrapValueInLevel += value;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Congregant dropped {item.itemName} worth {value} at {point}.");
            return grabbable;
        }

        private static void RegisterKnife()
        {
            GameObject root = CreateLootRoot("CongregationKnifeLoot", "LongKnife", out AudioSource rootAudio);
            _knifeRootAudio = rootAudio;
            var bladeAudioObject = new GameObject("KnifeAudio") { layer = PropsLayer };
            bladeAudioObject.transform.SetParent(root.transform, false);
            AudioSource bladeAudio = bladeAudioObject.AddComponent<AudioSource>();
            ConfigureAudio(bladeAudio, 5f, 17f);
            _vanillaKnifeFrame = new GameObject("VanillaKnifeFrame") { layer = PropsLayer }.transform;
            _vanillaKnifeFrame.SetParent(root.transform, false);

            // The vanilla Knife item's handling numbers; its asset references are borrowed at
            // StartOfRound.Awake because the vanilla item is not loaded yet at plugin load.
            Item item = ScriptableObject.CreateInstance<Item>();
            item.name = "CongregationLongKnifeItem";
            item.itemName = "Congregation long knife";
            item.spawnPrefab = root;
            item.spawnPositionTypes = new List<ItemGroup>();
            item.isScrap = true;
            item.minValue = KnifeMinValue;
            item.maxValue = KnifeMaxValue;
            item.creditsWorth = 0;
            item.weight = 1f;
            item.itemSpawnsOnGround = true;
            item.twoHanded = false;
            item.twoHandedAnimation = false;
            item.disableHandsOnWall = true;
            item.holdButtonUse = false;
            item.isConductiveMetal = true;
            item.requiresBattery = false;
            item.grabAnim = "HoldKnife";
            item.syncGrabFunction = false;
            item.syncUseFunction = true;
            item.syncDiscardFunction = false;
            item.syncInteractLRFunction = false;
            item.isDefensiveWeapon = true;
            item.toolTips = new[] { "Stab : [RMB]" };
            item.verticalOffset = 0.07f;
            item.restingRotation = new Vector3(0f, 90f, -90f);
            item.rotationOffset = new Vector3(-20f, -87.5f, -853.5f);
            item.positionOffset = new Vector3(-0.03f, 0.16f, 0f);
            item.meshOffset = false;

            KnifeItem knife = root.AddComponent<KnifeItem>();
            knife.itemProperties = item;
            knife.grabbable = true;
            knife.grabbableToEnemies = true;
            knife.knifeAudio = bladeAudio;
            knife.knifeHitForce = 1;

            // A bundled model follows the vanilla knife's frame (pivot at the grip, +Z to the tip),
            // so the vanilla hold offsets above apply to it unchanged.
            GameObject model = InstantiateModel(CongregationPropsAssets.Knife, root.transform, "LongKnifeModel");
            _knifeHasCustomModel = model != null;
            BoxCollider pickup = root.AddComponent<BoxCollider>();
            FitPickupBox(root.transform, model, pickup, VanillaKnifePickupCenter, VanillaKnifePickupSize);
            AddScanNode(root.transform, pickup, item.itemName);

            _knifePrefab = root;
            _knifeItem = item;
            _knife = knife;
            RegisterPrefab(root, item, "congregation_long_knife");
        }

        private static void RegisterDrumstick()
        {
            // No vanilla drumstick to borrow, so without the sidecar model there is no drumstick
            // loot; host and client ship the same sidecar, which keeps the registration symmetric.
            GameObject visual = CongregationPropsAssets.Drumstick;
            if (visual == null)
            {
                ContractsBootstrap.Log?.LogInfo(
                    "[MoonContracts.Cult] Drumstick loot not registered: the props sidecar has no "
                    + CongregationPropsAssets.DrumstickName + "; the drummer drops nothing.");
                return;
            }

            GameObject root = CreateLootRoot("CongregationDrumstickLoot", "Drumstick", out _);
            Item item = ScriptableObject.CreateInstance<Item>();
            item.name = "CongregationDrumstickItem";
            item.itemName = "Congregation drumstick";
            item.spawnPrefab = root;
            item.spawnPositionTypes = new List<ItemGroup>();
            item.isScrap = true;
            item.minValue = DrumstickMinValue;
            item.maxValue = DrumstickMaxValue;
            item.creditsWorth = 0;
            item.weight = 1f;
            item.itemSpawnsOnGround = true;
            item.twoHanded = false;
            item.requiresBattery = false;
            item.isConductiveMetal = false;

            PhysicsProp prop = root.AddComponent<PhysicsProp>();
            prop.itemProperties = item;
            prop.grabbable = true;
            prop.grabbableToEnemies = true;

            GameObject model = InstantiateModel(visual, root.transform, "DrumstickModel");
            BoxCollider pickup = root.AddComponent<BoxCollider>();
            FitPickupBox(root.transform, model, pickup, new Vector3(0f, 0f, DrumstickFallbackPickupSize.z * 0.5f), DrumstickFallbackPickupSize);
            AddScanNode(root.transform, pickup, item.itemName);

            _drumstickPrefab = root;
            _drumstickItem = item;
            RegisterPrefab(root, item, "congregation_drumstick");
        }

        private static GameObject CreateLootRoot(string name, string hashKey, out AudioSource audio)
        {
            GameObject root = ContractAssetLoader.CreateHiddenPrefabRoot(name);
            root.layer = PropsLayer;
            root.tag = "PhysicsProp";
            NetworkObject network = root.AddComponent<NetworkObject>();
            ContractAssetLoader.AssignStableNetworkHash(network, "Congregation", hashKey);
            Rigidbody body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            // GrabbableObject plays its drop, pocket and clink sounds on the root AudioSource.
            audio = root.AddComponent<AudioSource>();
            ConfigureAudio(audio, 3f, 27f);
            return root;
        }

        private static void ConfigureAudio(AudioSource audio, float minDistance, float maxDistance)
        {
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.minDistance = minDistance;
            audio.maxDistance = maxDistance;
        }

        private static GameObject InstantiateModel(GameObject visual, Transform root, string name)
        {
            if (visual == null)
                return null;

            GameObject model = Object.Instantiate(visual, root, false);
            model.name = name;
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            foreach (Collider collider in model.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(collider);
            foreach (Transform child in model.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = PropsLayer;
            return model;
        }

        /// <summary>Sizes the pickup box to the model's meshes in root space, or to the fallback
        /// when there is no model yet. Mesh bounds, not renderer bounds: the template is inactive.</summary>
        private static void FitPickupBox(Transform root, GameObject model, BoxCollider box, Vector3 fallbackCenter, Vector3 fallbackSize)
        {
            bool any = false;
            Bounds bounds = default;
            if (model != null)
            {
                Matrix4x4 toRoot = root.worldToLocalMatrix;
                foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
                {
                    Mesh mesh = filter.sharedMesh;
                    if (mesh == null)
                        continue;
                    Matrix4x4 toRootFromMesh = toRoot * filter.transform.localToWorldMatrix;
                    Bounds local = mesh.bounds;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 sign = new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f);
                        Vector3 point = toRootFromMesh.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, sign));
                        if (any)
                        {
                            bounds.Encapsulate(point);
                        }
                        else
                        {
                            bounds = new Bounds(point, Vector3.zero);
                            any = true;
                        }
                    }
                }
            }

            box.center = any ? bounds.center : fallbackCenter;
            box.size = any ? Vector3.Max(bounds.size, MinimumPickupSize) : fallbackSize;
        }

        private static void AddScanNode(Transform root, BoxCollider pickup, string header)
        {
            var scan = new GameObject("ScanNode") { layer = ScanNodeLayer };
            scan.transform.SetParent(root, false);
            scan.transform.localPosition = pickup.center;
            BoxCollider scanCollider = scan.AddComponent<BoxCollider>();
            scanCollider.isTrigger = true;
            scanCollider.size = pickup.size;
            ScanNodeProperties node = scan.AddComponent<ScanNodeProperties>();
            node.headerText = header;
            node.subText = "Value: ???";
            node.nodeType = 2;
            node.minRange = 1;
            node.maxRange = 13;
            node.requiresLineOfSight = true;
        }

        private static void RegisterPrefab(GameObject root, Item item, string itemKey)
        {
            DawnLibCompat.FixMixerGroups(root);
            DawnLibCompat.RegisterNetworkPrefab(root);
            DawnLibCompat.RegisterItem(itemKey, item);
            ContractAssetLoader.LogRegisteredPrefab("[MoonContracts.Cult]", root, registered: true);
        }

        /// <summary>The vanilla Kitchen knife item (asset "Knife"), else any other loaded item
        /// whose prefab is a KnifeItem; never this module's own long knife.</summary>
        private static Item FindVanillaKnifeItem()
        {
            Item fallback = null;
            foreach (Item candidate in Resources.FindObjectsOfTypeAll<Item>())
            {
                if (candidate == null || candidate == _knifeItem || candidate.spawnPrefab == null
                    || candidate.spawnPrefab == _knifePrefab || candidate.spawnPrefab.GetComponent<KnifeItem>() == null)
                    continue;
                if (candidate.name == VanillaKnifeAssetName)
                    return candidate;
                if (fallback == null)
                    fallback = candidate;
            }
            return fallback;
        }
    }
}
