using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Contracts.ShadowRaid
{
    internal sealed class BreachContainerController : NetworkBehaviour
    {
        private const string RuntimePrefabKey = "BreachContainerVault";
        private const string RuntimeVisualName = "ShippingContainerVisual";
        private const float BaseWidth = 2.6f;
        // #1143: the shell, collider, lock-anchor and interior-test height. Raised from the
        // authored 2.58 m because the roof clipped the top of player and Blacksite soldier heads
        // (agent height 2.65 m); every consumer reads it through ResolveContainerDimensions.
        private const float BaseHeight = 2.84f;
        // #1142: how far each wall carve extends below the container origin. See ApplyShellCarve.
        private const float CarveSink = 0.5f;
        // #1143: the height the bundled container meshes were authored at. The visual root is its
        // only consumer — it is scaled on Y by BaseHeight / AuthoredVisualHeight so the mesh matches
        // the shell. No other height literal exists: colliders, lock anchor, interior test, the
        // generated fallback visual and the cache diagnostics all follow BaseHeight.
        private const float AuthoredVisualHeight = 2.58f;
        private const float TenFootLength = 3.05f;
        private const float TwentyFootLength = 6.1f;
        private const float FortyFootLength = 12.2f;
        private const float ShellThickness = 0.18f;
        private const float DoorOpenDuration = 1.15f;
        private const string EmbeddedCacheName = "EmbeddedBlacksiteCargoCrate";
        private const float EmbeddedCacheBackClearance = 1.02f;
        private const float EmbeddedCacheMinimumDoorClearance = 0.58f;
        private const float EmbeddedCacheFloorClearance = 0.14f;
        private const float EmbeddedCacheApproximateHalfWidth = 0.64f;
        private const float EmbeddedCacheSideWallClearance = 0.18f;
        private const float EmbeddedCacheSideSign = -1f;
        private const float DrillLockAnchorHorizontalOffset = 0f;
        private const string EmbeddedCacheDiagnosticsRootName = "EmbeddedCacheDiagnostics";
        private const float EmbeddedCacheDiagnosticWidth = 1.2f;
        private const float EmbeddedCacheDiagnosticHeight = 0.566f;
        private const float EmbeddedCacheDiagnosticLength = 0.788f;
        private const float EmbeddedCacheDiagnosticEdgeThickness = 0.035f;

        private static readonly List<BreachContainerController> ActiveContainers = new List<BreachContainerController>();
        private static GameObject _runtimePrefab;
        private static bool _runtimePrefabRegistered;
        private static bool _loggedContainerChoice;
        private static Material _generatedContainerMaterial;
        private static Material _generatedContainerDoorMaterial;
        private static Material _embeddedCacheDiagnosticMaterial;
        private static Material _embeddedCacheDiagnosticDoorMaterial;
        private static Material _embeddedCacheDiagnosticDepthMaterial;
        private static Material _embeddedCacheDiagnosticBackMaterial;
        private static readonly Dictionary<Material, Material> RuntimeContainerMaterials = new Dictionary<Material, Material>();

        private readonly NetworkVariable<bool> _openState = new NetworkVariable<bool>(false);
        private readonly NetworkVariable<int> _sizeIndexState = new NetworkVariable<int>(1);
        // BreachDrillItem is deliberately a plain MonoBehaviour (see the note at the top of that file),
        // so it cannot own NetworkVariables. The container is the properly spawned NetworkBehaviour of
        // the pair, so it carries the drill's attachment state for every peer. NetworkVariables rather
        // than a ClientRpc so a late joiner receives the running drill on spawn.
        private readonly NetworkVariable<ulong> _attachedDrillNetworkId = new NetworkVariable<ulong>(0UL);
        private readonly NetworkVariable<float> _attachedDrillFinishServerTime = new NetworkVariable<float>(0f);
        // Completion drop pose. The drill writes its own transform, hasHitGround, startFallingPosition
        // and targetFloorPosition as plain local fields when it finishes, so without these a client
        // would clear the attachment and re-enable gravity with no floor target — the drill would hang
        // against the doors or free-fall while the host's copy teleported 1.15m out onto the floor.
        // The sequence counter makes a re-used drill's second completion a distinct event.
        //
        // DECLARATION ORDER IS LOAD-BEARING. NGO packs a behaviour's dirty NetworkVariables into one
        // delta in field declaration order and raises OnValueChanged synchronously as each field is
        // read, so a handler sees only the fields declared *before* its own. _completedDrillSequence is
        // therefore declared LAST and is the only field the push handler subscribes to: when it fires,
        // the whole payload above it (id, positions, yaw, server time) has already been applied. Do not
        // move it, and do not subscribe a handler to any field above it.
        private readonly NetworkVariable<ulong> _completedDrillNetworkId = new NetworkVariable<ulong>(0UL);
        private readonly NetworkVariable<Vector3> _completedDrillDropPosition = new NetworkVariable<Vector3>(Vector3.zero);
        private readonly NetworkVariable<Vector3> _completedDrillFloorPosition = new NetworkVariable<Vector3>(Vector3.zero);
        private readonly NetworkVariable<float> _completedDrillDropYaw = new NetworkVariable<float>(0f);
        private readonly NetworkVariable<float> _completedDrillServerTime = new NetworkVariable<float>(0f);
        private readonly NetworkVariable<int> _completedDrillSequence = new NetworkVariable<int>(0);
        private readonly List<Collider> _doorColliders = new List<Collider>();

        private NetworkObject _networkObject;
        private Rigidbody _kinematicBody;
        private string _runtimePartsBuiltForSize;
        private Transform _visualRoot;
        private Transform _lockAnchor;
        private Transform _lootAnchor;
        private ShadowRaidCargoCrate _embeddedCache;
        private NetworkObject _embeddedCacheNetworkObject;
        private Transform _leftHinge;
        private Transform _rightHinge;
        private Transform _shellRoot;
        private BoxCollider _doorBlocker;
        private NavMeshObstacle _doorBlockerObstacle;
        private readonly List<Renderer> _fallbackDoorRenderers = new List<Renderer>();
        private Quaternion _leftClosedRotation;
        private Quaternion _rightClosedRotation;
        private Quaternion _leftOpenRotation;
        private Quaternion _rightOpenRotation;
        private bool _opening;
        private bool _openLocal;
        private float _openingProgress;
        private AudioSource _doorAudioSource;
        private bool _playedOpenSound;
        private string _sizeName = "medium";
        private string _visualSizeName = string.Empty;
        // #949 S11H U1: the round this container was built in. Stamped in Awake, which runs on
        // every peer, and every peer resets run state at least once per round transition
        // (MoonContractEndOfRoundPatch on EndOfGameClientRpc and
        // MoonContractStartOfRoundPatches on SetShipReadyToLand both run everywhere), so a
        // client answers the liveness question from its own copy without needing the host's
        // spawn list.
        private int _roundToken;
        private bool _loggedEmbeddedCacheDiagnostics;
        private bool _loggedEmbeddedCacheDiagnosticsWithCache;

        internal ulong NetworkId => _networkObject != null ? _networkObject.NetworkObjectId : 0UL;
        internal bool IsOpen => _openLocal || _openState.Value;
        internal Transform LockAnchor => _lockAnchor != null ? _lockAnchor : transform;
        internal Transform LootAnchor => _lootAnchor != null ? _lootAnchor : transform;
        internal string SizeName => _sizeName;
        internal ShadowRaidCargoCrate EmbeddedCache => _embeddedCache;

        internal bool TryGetEmbeddedHardDriveTarget(
            out ShadowRaidCargoCrate cache,
            out BreachHardDriveItem drive)
        {
            cache = _embeddedCache;
            drive = null;
            // #949 S11H U1: a crate answers the same liveness question its container does, so a
            // cache left over from an earlier round is never offered as a retrieval target.
            return cache != null && cache.IsLiveThisRound && (
                cache.TryGetContainedHardDrive(out drive)
                || cache.ContainedItemNetworkId == 0UL);
        }

        internal bool ContainsWorldPointInsideContainer(Vector3 worldPoint)
        {
            ResolveContainerDimensions(_sizeName, out float width, out float height, out float length);
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            float sidePadding = ShellThickness * 0.35f;
            return local.x > -width * 0.5f + sidePadding
                   && local.x < width * 0.5f - sidePadding
                   && local.y > -0.35f
                   && local.y < height + 0.35f
                   && local.z < 0.35f
                   && local.z > -length - 0.15f;
        }

        private void Awake()
        {
            // #949 S11H U1: stamp the round before joining the registry, so no reader can ever
            // see this container with the default token of 0.
            _roundToken = ShadowRaidRoundRegistry.CurrentRound;
            ResolveRuntimeReferences();
            if (!ActiveContainers.Contains(this))
                ActiveContainers.Add(this);
        }

        private void Start()
        {
            EnsureRuntimeParts();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _openState.OnValueChanged += OnOpenStateChanged;
            _sizeIndexState.OnValueChanged += OnSizeStateChanged;
            _attachedDrillNetworkId.OnValueChanged += OnAttachedDrillStateChanged;
            _completedDrillSequence.OnValueChanged += OnCompletedDrillStateChanged;
            ApplyContainerSize(IndexToSizeName(_sizeIndexState.Value), updateNetwork: false);
            if (_openState.Value)
                StartOpenAnimationLocal();

            // Late joiner: the drill may already be attached or already finished on this container.
            if (!IsServer && (_attachedDrillNetworkId.Value != 0UL || _completedDrillSequence.Value > 0))
                BreachDrillItem.NotifyReplicatedContainerStateChanged();
        }

        public override void OnNetworkDespawn()
        {
            _openState.OnValueChanged -= OnOpenStateChanged;
            _sizeIndexState.OnValueChanged -= OnSizeStateChanged;
            _attachedDrillNetworkId.OnValueChanged -= OnAttachedDrillStateChanged;
            _completedDrillSequence.OnValueChanged -= OnCompletedDrillStateChanged;
            base.OnNetworkDespawn();
        }

        public override void OnDestroy()
        {
            ActiveContainers.Remove(this);
            base.OnDestroy();
        }

        private void Update()
        {
            if (!_opening)
                return;

            _openingProgress = Mathf.MoveTowards(_openingProgress, 1f, Time.deltaTime / DoorOpenDuration);
            if (_leftHinge != null)
                _leftHinge.localRotation = Quaternion.Slerp(_leftClosedRotation, _leftOpenRotation, _openingProgress);
            if (_rightHinge != null)
                _rightHinge.localRotation = Quaternion.Slerp(_rightClosedRotation, _rightOpenRotation, _openingProgress);

            if (_openingProgress >= 0.999f)
            {
                _opening = false;
                SetFallbackDoorRenderersVisible(false);
            }
        }

        internal void ConfigureRuntime(float requestedScale, float _)
        {
            string sizeName = requestedScale <= 0.6f
                ? "small"
                : requestedScale >= 0.9f ? "large" : "medium";
            ConfigureRuntime(sizeName);
        }

        internal void ConfigureRuntime(string sizeName)
        {
            ApplyContainerSize(sizeName, updateNetwork: true);
        }

        internal void OpenContainerLocal()
        {
            if (IsServer && !_openState.Value)
                _openState.Value = true;

            StartOpenAnimationLocal();
        }

        internal bool TryOpenFromBlacksiteOverrideServer(
            BlacksiteRetrievalMissionController mission)
        {
            if (!IsServer
                || !IsSpawned
                || mission == null
                || !mission.IsServer
                || mission.IsTerminal
                || mission.Contract != MoonContractType.ShadowRaid)
            {
                return false;
            }

            ResolveRuntimeReferences();
            if (NetworkId == 0UL || mission.ExpectedTargetNetworkId != NetworkId)
                return false;

            OpenContainerLocal();
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Shipping-container override accepted: " +
                $"containerNetId={NetworkId}, missionNetId={mission.MissionNetworkId}.");
            return IsOpen;
        }

        private void OnOpenStateChanged(bool _, bool isOpen)
        {
            if (isOpen)
                StartOpenAnimationLocal();
        }

        private void OnSizeStateChanged(int _, int newValue)
        {
            ApplyContainerSize(IndexToSizeName(newValue), updateNetwork: false);
        }

        private void ApplyContainerSize(string sizeName, bool updateNetwork)
        {
            _sizeName = NormalizeContainerSizeName(sizeName);
            if (updateNetwork && IsServer && IsSpawned)
            {
                int nextIndex = SizeNameToIndex(_sizeName);
                if (_sizeIndexState.Value != nextIndex)
                    _sizeIndexState.Value = nextIndex;
            }

            EnsureRuntimeParts();
        }

        private void StartOpenAnimationLocal()
        {
            if (_openLocal)
                return;

            EnsureRuntimeParts();
            ApplyDoorClosedPose();
            _openLocal = true;
            PlayOpenSoundLocal();
            for (int i = 0; i < _doorColliders.Count; i++)
            {
                Collider collider = _doorColliders[i];
                if (collider != null)
                    collider.enabled = false;
            }

            // #1142: the doorway carve follows the collider it belongs to, off the same replicated
            // open state, so the host, a client that saw _openState change and a late joiner whose
            // OnNetworkSpawn replays the open animation all stop carving the doorway together. The
            // interior floor cells are only reachable through the door once this obstacle is off.
            if (_doorBlockerObstacle != null)
                _doorBlockerObstacle.enabled = false;

            _opening = true;
            _openingProgress = 0f;
            if (_leftHinge == null && _rightHinge == null)
                SetFallbackDoorRenderersVisible(false);
        }

        private void PlayOpenSoundLocal()
        {
            if (_playedOpenSound)
                return;

            _playedOpenSound = true;
            AudioClip clip = ContractAssetLoader.BreachContainerDoorOpenClip;
            if (clip == null)
                return;

            if (_doorAudioSource == null)
            {
                _doorAudioSource = GetComponent<AudioSource>();
                if (_doorAudioSource == null)
                    _doorAudioSource = gameObject.AddComponent<AudioSource>();

                _doorAudioSource.playOnAwake = false;
                _doorAudioSource.spatialBlend = 1f;
                _doorAudioSource.rolloffMode = AudioRolloffMode.Linear;
                _doorAudioSource.minDistance = 2f;
                _doorAudioSource.maxDistance = 30f;
                _doorAudioSource.volume = 0.82f;
                _doorAudioSource.loop = false;
            }

            _doorAudioSource.pitch = 1f;
            _doorAudioSource.PlayOneShot(clip, 0.92f);
        }

        /// <summary>
        /// Builds the container's runtime parts. Callers hit this three to four times per container on
        /// the spawn frame (Start, OnNetworkSpawn size apply, ConfigureRuntime, the open animation, the
        /// cache embed), and the tail — collider rebuild, embedded-cache search, per-slot materials and
        /// door pivots — is the expensive half. It now runs once per container per size; pass
        /// <paramref name="force"/> when a newly spawned child (the embedded cache) has to be picked up.
        /// </summary>
        private void EnsureRuntimeParts(bool force = false)
        {
            ResolveRuntimeReferences();
            EnsureVisualForCurrentSize();

            if (_lockAnchor == null)
                _lockAnchor = EnsureChildTransform("LockAnchor");
            if (_lootAnchor == null)
                _lootAnchor = EnsureChildTransform("LootAnchor");

            if (_kinematicBody == null)
            {
                _kinematicBody = GetComponent<Rigidbody>();
                if (_kinematicBody == null)
                    _kinematicBody = gameObject.AddComponent<Rigidbody>();
                _kinematicBody.isKinematic = true;
                _kinematicBody.useGravity = false;
            }

            bool sizeChanged = !string.Equals(_runtimePartsBuiltForSize, _sizeName, System.StringComparison.Ordinal);
            if (!force && !sizeChanged && _runtimePartsBuiltForSize != null && _shellRoot != null)
                return;

            _runtimePartsBuiltForSize = _sizeName;
            RebuildAnchorsAndColliders();
            CaptureDoorPivots();
        }

        private void EnsureVisualForCurrentSize()
        {
            GameObject source = ContractAssetLoader.GetBreachContainerVisualPrefab(_sizeName);
            if (source == null)
                source = ContractAssetLoader.BreachContainerVisualPrefab;

            if (source == null)
            {
                CreateGeneratedContainerVisual();
                return;
            }

            bool wrongVariant = _visualRoot != null &&
                                !string.Equals(_visualSizeName, _sizeName, System.StringComparison.OrdinalIgnoreCase);
            if (wrongVariant)
            {
                DestroyUnityObject(_visualRoot.gameObject);
                _visualRoot = null;
                _visualSizeName = string.Empty;
                _leftHinge = null;
                _rightHinge = null;
            }

            if (_visualRoot != null)
                return;

            GameObject visual = Instantiate(source, transform, false);
            visual.name = RuntimeVisualName;
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            // #1143: the bundled meshes are authored at AuthoredVisualHeight, so the taller shell
            // would leave the mesh short. The scale is Y only, which is the axis the door leaves
            // rotate about (identity closed, the authored open pose or Euler(0, +/-105, 0) open):
            // a scale along the rotation axis keeps the uniformly scaled XZ plane the leaves sweep
            // through unsheared, so the swing is safe.
            ResolveContainerDimensions(_sizeName, out _, out float visualHeight, out _);
            visual.transform.localScale = new Vector3(1f, visualHeight / AuthoredVisualHeight, 1f);
            StripNestedRuntimeComponents(visual);
            PrepareVisualForRuntime(visual);
            _visualRoot = visual.transform;
            _visualSizeName = _sizeName;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Shadow Raid container visual instantiated: size={_sizeName}, prefab='{source.name}'.");
        }

        private void CreateGeneratedContainerVisual()
        {
            bool wrongVariant = _visualRoot != null &&
                                !string.Equals(_visualSizeName, _sizeName, System.StringComparison.OrdinalIgnoreCase);
            if (wrongVariant)
            {
                DestroyUnityObject(_visualRoot.gameObject);
                _visualRoot = null;
                _visualSizeName = string.Empty;
                _leftHinge = null;
                _rightHinge = null;
            }

            if (_visualRoot != null)
                return;

            ResolveContainerDimensions(_sizeName, out float width, out float height, out float length);
            var root = new GameObject(RuntimeVisualName);
            root.transform.SetParent(transform, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            float centerZ = -length * 0.5f;
            CreateGeneratedVisualPart(root.transform, "ContainerBody",
                new Vector3(0f, height * 0.5f, centerZ),
                new Vector3(width, height, length),
                GeneratedContainerMaterial);
            CreateGeneratedVisualPart(root.transform, "FrontDoors",
                new Vector3(0f, height * 0.5f, 0.03f),
                new Vector3(width * 0.98f, height * 0.96f, 0.09f),
                GeneratedContainerDoorMaterial);
            CreateGeneratedVisualPart(root.transform, "DoorSeam",
                new Vector3(0f, height * 0.5f, 0.09f),
                new Vector3(0.04f, height * 0.88f, 0.06f),
                GeneratedContainerMaterial);
            CreateGeneratedVisualPart(root.transform, "LockPlate",
                new Vector3(0.23f, height * 0.53f, 0.12f),
                new Vector3(0.26f, 0.38f, 0.08f),
                GeneratedContainerMaterial);

            for (int i = 0; i < 7; i++)
            {
                float t = (i + 1f) / 8f;
                float z = Mathf.Lerp(-0.35f, -length + 0.35f, t);
                CreateGeneratedVisualPart(root.transform, "RibL_" + i,
                    new Vector3(-width * 0.505f, height * 0.5f, z),
                    new Vector3(0.055f, height * 0.92f, 0.08f),
                    GeneratedContainerDoorMaterial);
                CreateGeneratedVisualPart(root.transform, "RibR_" + i,
                    new Vector3(width * 0.505f, height * 0.5f, z),
                    new Vector3(0.055f, height * 0.92f, 0.08f),
                    GeneratedContainerDoorMaterial);
            }

            _visualRoot = root.transform;
            _visualSizeName = _sizeName;
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.ShadowRaid] Bundled shipping container visual missing; generated procedural {_sizeName} container visual length={length:0.00}m.");
        }

        /// <summary>
        /// Binds the runtime child references. Called from hot paths (the crate cache poll runs this
        /// per container several times a second through <see cref="IsLiveRuntimeContainer"/>), so each
        /// lookup is skipped once its field is bound. Unity's overloaded == turns a destroyed object
        /// back into null, so a reference that really went away self-heals on the next call.
        /// </summary>
        private void ResolveRuntimeReferences()
        {
            if (_networkObject == null)
                _networkObject = GetComponent<NetworkObject>();
            if (_visualRoot == null)
                _visualRoot = transform.Find(RuntimeVisualName);
            if (_lockAnchor == null)
                _lockAnchor = transform.Find("LockAnchor");
            if (_lootAnchor == null)
                _lootAnchor = transform.Find("LootAnchor");
            if (_embeddedCache == null)
            {
                _embeddedCache = GetComponentInChildren<ShadowRaidCargoCrate>(true);
                _embeddedCacheNetworkObject = null;
            }

            if (_embeddedCacheNetworkObject == null && _embeddedCache != null)
                _embeddedCacheNetworkObject = _embeddedCache.GetComponent<NetworkObject>();
            if (_shellRoot == null)
                _shellRoot = transform.Find("RuntimeShell");
            if (_doorBlocker == null)
            {
                _doorBlocker = transform.Find("RuntimeShell/DoorBlocker")?.GetComponent<BoxCollider>()
                               ?? transform.Find("DoorBlocker")?.GetComponent<BoxCollider>();
            }

            // #1142: the carve rides the same GameObject as the blocker collider, so a rebound
            // blocker brings its obstacle back with it and the open-state toggle can never no-op.
            if (_doorBlockerObstacle == null && _doorBlocker != null)
                _doorBlockerObstacle = _doorBlocker.GetComponent<NavMeshObstacle>();
        }

        private Transform EnsureChildTransform(string name)
        {
            Transform child = transform.Find(name);
            if (child != null)
                return child;

            var obj = new GameObject(name);
            obj.transform.SetParent(transform, false);
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.identity;
            return obj.transform;
        }

        private void RebuildAnchorsAndColliders()
        {
            ResolveContainerDimensions(_sizeName, out float width, out float height, out float length);
            float frontZ = 0f;
            float backZ = -length;
            float centerZ = (frontZ + backZ) * 0.5f;

            if (_lockAnchor != null)
            {
                _lockAnchor.localPosition = new Vector3(DrillLockAnchorHorizontalOffset, height * 0.52f, frontZ + 0.13f);
                _lockAnchor.localRotation = Quaternion.identity;
            }

            if (_lootAnchor != null)
            {
                _lootAnchor.localPosition = GetEmbeddedCacheLocalPosition(width, length);
                _lootAnchor.localRotation = Quaternion.identity;
            }

            ResolveEmbeddedCacheByPosition(width, length);
            PositionEmbeddedCache(width, length);
            RebuildShellColliders(width, height, length, centerZ, backZ, frontZ);
            RefreshEmbeddedCacheDiagnostics(width, height, length);
        }

        internal ShadowRaidCargoCrate EnsureEmbeddedCacheServer(out NetworkObject cacheNetworkObject)
        {
            // Forced: this is the path that has to notice a cache child that spawned after the last build.
            EnsureRuntimeParts(force: true);
            cacheNetworkObject = _embeddedCacheNetworkObject;
            if (_embeddedCache != null)
                return _embeddedCache;

            GameObject prefab = ContractAssetLoader.BlacksiteCratePrefab;
            if (prefab == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Cannot embed military cache because BlacksiteCargoCrate prefab is missing.");
                return null;
            }

            ResolveContainerDimensions(_sizeName, out float width, out _, out float length);
            Vector3 targetPosition = transform.TransformPoint(GetEmbeddedCacheLocalPosition(width, length));
            Quaternion targetRotation = GetEmbeddedCacheWorldRotation();
            GameObject spawned = Instantiate(prefab, targetPosition, targetRotation);
            if (spawned == null)
                return null;

            spawned.name = EmbeddedCacheName;
            spawned.SetActive(true);
            _embeddedCache = spawned.GetComponent<ShadowRaidCargoCrate>();
            _embeddedCacheNetworkObject = spawned.GetComponent<NetworkObject>();

            if (_embeddedCacheNetworkObject != null && !_embeddedCacheNetworkObject.IsSpawned && IsServer)
                _embeddedCacheNetworkObject.Spawn();

            PositionEmbeddedCache(width, length);
            cacheNetworkObject = _embeddedCacheNetworkObject;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Embedded military cache into shipping container {NetworkId}.");
            return _embeddedCache;
        }

        private static Vector3 GetEmbeddedCacheLocalPosition(float width, float length)
        {
            float depth = Mathf.Max(EmbeddedCacheMinimumDoorClearance, length - EmbeddedCacheBackClearance);
            float sideOffset = Mathf.Max(0f, width * 0.5f - EmbeddedCacheApproximateHalfWidth - EmbeddedCacheSideWallClearance);
            return new Vector3(sideOffset * EmbeddedCacheSideSign, EmbeddedCacheFloorClearance, -depth);
        }

        private void PositionEmbeddedCache(float width, float length)
        {
            if (_embeddedCache == null)
                return;

            Transform cacheTransform = _embeddedCache.transform;
            cacheTransform.position = transform.TransformPoint(GetEmbeddedCacheLocalPosition(width, length));
            cacheTransform.rotation = GetEmbeddedCacheWorldRotation();
            cacheTransform.localScale = Vector3.one;
            SetEmbeddedCacheNetworkVisualVisible(true);

            ResolveContainerDimensions(_sizeName, out _, out float height, out _);
            RefreshEmbeddedCacheDiagnostics(width, height, length);
        }

        private Quaternion GetEmbeddedCacheWorldRotation()
        {
            return transform.rotation;
        }

        private void ResolveEmbeddedCacheByPosition(float width, float length)
        {
            if (_embeddedCache != null)
                return;

            Vector3 expectedPosition = transform.TransformPoint(GetEmbeddedCacheLocalPosition(width, length));
            ShadowRaidCargoCrate best = null;
            float bestSqr = 1.25f * 1.25f;
            ShadowRaidCargoCrate[] crates = FindObjectsOfType<ShadowRaidCargoCrate>();
            for (int i = 0; i < crates.Length; i++)
            {
                ShadowRaidCargoCrate crate = crates[i];
                if (crate == null || !crate.gameObject.activeInHierarchy)
                    continue;

                float sqr = (crate.transform.position - expectedPosition).sqrMagnitude;
                if (sqr >= bestSqr)
                    continue;

                bestSqr = sqr;
                best = crate;
            }

            if (best == null)
                return;

            _embeddedCache = best;
            _embeddedCacheNetworkObject = best.GetComponent<NetworkObject>();
        }

        private void SetEmbeddedCacheNetworkVisualVisible(bool visible)
        {
            if (_embeddedCache == null)
                return;

            _embeddedCache.SuppressVisualRenderers = !visible;
            Renderer[] renderers = _embeddedCache.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                renderer.enabled = visible;
                renderer.forceRenderingOff = !visible;
            }
        }

        private void RefreshEmbeddedCacheDiagnostics(float width, float height, float length)
        {
            Transform root = transform.Find(EmbeddedCacheDiagnosticsRootName);
            if (root != null)
                DestroyUnityObject(root.gameObject);

            Vector3 cacheRootLocal = GetEmbeddedCacheLocalPosition(width, length);
            if (BlacksiteDebugDiagnosticsEnabled())
                LogEmbeddedCacheDiagnostics(width, height, length, cacheRootLocal);
        }

        private void LogEmbeddedCacheDiagnostics(float width, float height, float length, Vector3 cacheRootLocal)
        {
            bool hasCache = _embeddedCache != null;
            if (hasCache)
            {
                if (_loggedEmbeddedCacheDiagnosticsWithCache)
                    return;
                _loggedEmbeddedCacheDiagnosticsWithCache = true;
            }
            else
            {
                if (_loggedEmbeddedCacheDiagnostics)
                    return;
                _loggedEmbeddedCacheDiagnostics = true;
            }

            int rendererCount = 0;
            int visibleRendererCount = 0;
            Bounds cacheBounds = default;
            bool hasBounds = false;
            if (_embeddedCache != null)
            {
                Renderer[] renderers = _embeddedCache.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null || renderer.GetComponentInParent<ScanNodeProperties>() != null)
                        continue;

                    rendererCount++;
                    if (renderer.enabled && !renderer.forceRenderingOff)
                        visibleRendererCount++;
                    if (!hasBounds)
                    {
                        cacheBounds = renderer.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        cacheBounds.Encapsulate(renderer.bounds);
                    }
                }
            }

            Vector3 targetWorld = transform.TransformPoint(cacheRootLocal);
            Vector3 actualWorld = _embeddedCache != null ? _embeddedCache.transform.position : Vector3.zero;
            Vector3 actualLocal = _embeddedCache != null ? transform.InverseTransformPoint(actualWorld) : Vector3.zero;
            float targetDelta = _embeddedCache != null ? Vector3.Distance(actualWorld, targetWorld) : 0f;
            string cacheText = _embeddedCache != null
                ? $"cache='{_embeddedCache.name}', cacheLocal={FormatVector(actualLocal)}, cacheWorld={FormatVector(actualWorld)}, targetDelta={targetDelta:0.000}m"
                : "cache=none";
            string boundsText = hasBounds
                ? $"boundsCenter={FormatVector(cacheBounds.center)}, boundsSize={FormatVector(cacheBounds.size)}"
                : "bounds=none";

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Embedded cache placement diagnostic: container={NetworkId}, size={_sizeName}, " +
                $"dimensions={width:0.00}x{height:0.00}x{length:0.00}, backClearance={EmbeddedCacheBackClearance:0.00}, " +
                $"clampedDepth={-cacheRootLocal.z:0.00}, floorClearance={cacheRootLocal.y:0.00}, targetLocal={FormatVector(cacheRootLocal)}, " +
                $"targetWorld={FormatVector(targetWorld)}, doorWorld={FormatVector(transform.TransformPoint(Vector3.zero))}, " +
                $"backWorld={FormatVector(transform.TransformPoint(new Vector3(0f, 0f, -length)))}, {cacheText}, " +
                $"renderers={visibleRendererCount}/{rendererCount}, {boundsText}.");
        }

        private static void UpdateDiagnosticBox(Transform parent, string prefix, Vector3 center, Vector3 size, Material material)
        {
            float t = EmbeddedCacheDiagnosticEdgeThickness;
            Vector3 half = size * 0.5f;

            UpdateDiagnosticCube(parent, prefix + "_BottomFrontX", center + new Vector3(0f, -half.y, half.z), new Vector3(size.x, t, t), material);
            UpdateDiagnosticCube(parent, prefix + "_BottomBackX", center + new Vector3(0f, -half.y, -half.z), new Vector3(size.x, t, t), material);
            UpdateDiagnosticCube(parent, prefix + "_TopFrontX", center + new Vector3(0f, half.y, half.z), new Vector3(size.x, t, t), material);
            UpdateDiagnosticCube(parent, prefix + "_TopBackX", center + new Vector3(0f, half.y, -half.z), new Vector3(size.x, t, t), material);

            UpdateDiagnosticCube(parent, prefix + "_BottomLeftZ", center + new Vector3(-half.x, -half.y, 0f), new Vector3(t, t, size.z), material);
            UpdateDiagnosticCube(parent, prefix + "_BottomRightZ", center + new Vector3(half.x, -half.y, 0f), new Vector3(t, t, size.z), material);
            UpdateDiagnosticCube(parent, prefix + "_TopLeftZ", center + new Vector3(-half.x, half.y, 0f), new Vector3(t, t, size.z), material);
            UpdateDiagnosticCube(parent, prefix + "_TopRightZ", center + new Vector3(half.x, half.y, 0f), new Vector3(t, t, size.z), material);

            UpdateDiagnosticCube(parent, prefix + "_FrontLeftY", center + new Vector3(-half.x, 0f, half.z), new Vector3(t, size.y, t), material);
            UpdateDiagnosticCube(parent, prefix + "_FrontRightY", center + new Vector3(half.x, 0f, half.z), new Vector3(t, size.y, t), material);
            UpdateDiagnosticCube(parent, prefix + "_BackLeftY", center + new Vector3(-half.x, 0f, -half.z), new Vector3(t, size.y, t), material);
            UpdateDiagnosticCube(parent, prefix + "_BackRightY", center + new Vector3(half.x, 0f, -half.z), new Vector3(t, size.y, t), material);
        }

        private static void UpdateDiagnosticCube(Transform parent, string name, Vector3 localPosition, Vector3 localScale, Material material)
        {
            Transform existing = parent.Find(name);
            GameObject obj = existing != null ? existing.gameObject : GameObject.CreatePrimitive(PrimitiveType.Cube);
            obj.name = name;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = localPosition;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = localScale;

            Collider collider = obj.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                DestroyUnityObject(collider);
            }

            Renderer renderer = obj.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.enabled = true;
                renderer.forceRenderingOff = false;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.allowOcclusionWhenDynamic = false;
            }
        }

        private static bool BlacksiteDebugDiagnosticsEnabled()
        {
            return ContractsBootstrap.ShadowRaidDebugEnabled != null && ContractsBootstrap.ShadowRaidDebugEnabled.Value;
        }

        private void RebuildShellColliders(float width, float height, float length, float centerZ, float backZ, float frontZ)
        {
            if (_shellRoot == null)
            {
                var shell = new GameObject("RuntimeShell");
                shell.transform.SetParent(transform, false);
                shell.transform.localPosition = Vector3.zero;
                shell.transform.localRotation = Quaternion.identity;
                _shellRoot = shell.transform;
            }

            float halfWidth = width * 0.5f;
            float halfHeight = height * 0.5f;
            float wallHeight = Mathf.Max(0.2f, height);
            float wallY = halfHeight;

            // #1142: the three walls and the closed door carve the navmesh, so a route past a
            // container goes around it instead of reporting PathComplete straight through the back
            // wall. Floor and Roof never carve: each wall box is only ShellThickness (0.18 m) thick
            // and sits centred on the shell line, so it eats 0.09 m of the footprint and the
            // interior floor cells between the walls stay walkable. That interior is cut off from
            // the outside mesh until the DoorBlocker obstacle goes off with the doors.
            CreateShellBox("LeftWall", new Vector3(-halfWidth, wallY, centerZ), new Vector3(ShellThickness, wallHeight, length), carve: true);
            CreateShellBox("RightWall", new Vector3(halfWidth, wallY, centerZ), new Vector3(ShellThickness, wallHeight, length), carve: true);
            CreateShellBox("BackWall", new Vector3(0f, wallY, backZ), new Vector3(width, wallHeight, ShellThickness), carve: true);
            CreateShellBox("Floor", new Vector3(0f, -ShellThickness * 0.5f, centerZ), new Vector3(width, ShellThickness, length), carve: false);
            CreateShellBox("Roof", new Vector3(0f, height, centerZ), new Vector3(width, ShellThickness, length), carve: false);
            _doorBlocker = CreateShellBox("DoorBlocker", new Vector3(0f, wallY, frontZ), new Vector3(width, wallHeight, ShellThickness), carve: true);
            _doorBlocker.enabled = !IsOpen;
            // #1142: the obstacle starts from the same replicated open state as its collider above,
            // so a client, a late joiner and a container rebuilt for a new size all agree, and a
            // round reset (a fresh container with the default closed _openState) carves again.
            _doorBlockerObstacle = _doorBlocker.GetComponent<NavMeshObstacle>();
            if (_doorBlockerObstacle != null)
                _doorBlockerObstacle.enabled = !IsOpen;

            CollectDoorColliders();
        }

        private BoxCollider CreateShellBox(string name, Vector3 localPosition, Vector3 size, bool carve)
        {
            Transform child = _shellRoot.Find(name);
            if (child == null)
            {
                var obj = new GameObject(name);
                obj.transform.SetParent(_shellRoot, false);
                child = obj.transform;
            }

            child.localPosition = localPosition;
            child.localRotation = Quaternion.identity;
            var collider = child.GetComponent<BoxCollider>();
            if (collider == null)
                collider = child.gameObject.AddComponent<BoxCollider>();
            collider.isTrigger = false;
            collider.center = Vector3.zero;
            collider.size = size;
            ApplyShellCarve(child.gameObject, size, carve);
            return collider;
        }

        /// <summary>
        /// #1142: keeps a shell box's carving obstacle in step with its collider. Every rebuild comes
        /// through here, so a resize re-sizes the obstacle rather than leaving a stale hole in the
        /// mesh, and a box that stops carving loses its obstacle outright. The shell boxes are never
        /// scaled (<see cref="CreateShellBox"/> writes position and rotation only) and the container
        /// root stands at scale one, so the collider's size is the obstacle's size in metres.
        /// </summary>
        private static void ApplyShellCarve(GameObject box, Vector3 size, bool carve)
        {
            try
            {
                var obstacle = box.GetComponent<NavMeshObstacle>();
                if (!carve)
                {
                    if (obstacle != null)
                        DestroyUnityObject(obstacle);
                    return;
                }

                if (obstacle == null)
                    obstacle = box.AddComponent<NavMeshObstacle>();
                obstacle.shape = NavMeshObstacleShape.Box;
                // The container origin is TileClearanceGrid.DominantFloorY, a 0.25 m bucket, so the
                // shell can stand up to 0.125 m above the surface the navmesh was baked on (the
                // 2026-09-19 placements sat 0.05 m above it). A carve that does not reach the mesh
                // carves nothing, silently, so the volume is sunk 0.5 m into the floor slab: the
                // Floor box never carves, and nothing walks under a container.
                obstacle.center = new Vector3(0f, -CarveSink * 0.5f, 0f);
                obstacle.size = new Vector3(size.x, size.y + CarveSink, size.z);
                obstacle.carving = true;
                // The container is placed once and never moves, so the stationary carve is the cheap
                // one: it cuts 0.5 s after the spawn transform settles and never churns after that.
                obstacle.carveOnlyStationary = true;
                // Enabled state is not written here: AddComponent yields an enabled obstacle, and
                // the DoorBlocker's enabled flag has exactly one writer, !IsOpen in
                // RebuildShellColliders and StartOpenAnimationLocal.
            }
            catch (System.Exception e)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.ShadowRaid] Container shell '{box.name}' could not carry a NavMeshObstacle: {e.Message}");
            }
        }

        private void CaptureDoorPivots()
        {
            if (_visualRoot == null)
                return;

            _leftHinge = FindChildByName(_visualRoot, "Hinge L");
            _rightHinge = FindChildByName(_visualRoot, "Hinge R");
            if (_leftHinge == null)
                _leftHinge = FindDoorTransform(_visualRoot, left: true);
            if (_rightHinge == null)
                _rightHinge = FindDoorTransform(_visualRoot, left: false);
            if (_rightHinge == _leftHinge)
                _rightHinge = null;

            if (_leftHinge != null)
            {
                Quaternion authoredOpenRotation = _leftHinge.localRotation;
                _leftClosedRotation = Quaternion.identity;
                _leftOpenRotation = LooksLikeAuthoredOpenRotation(authoredOpenRotation)
                    ? authoredOpenRotation
                    : _leftClosedRotation * Quaternion.Euler(0f, -105f, 0f);
            }

            if (_rightHinge != null)
            {
                Quaternion authoredOpenRotation = _rightHinge.localRotation;
                _rightClosedRotation = Quaternion.identity;
                _rightOpenRotation = LooksLikeAuthoredOpenRotation(authoredOpenRotation)
                    ? authoredOpenRotation
                    : _rightClosedRotation * Quaternion.Euler(0f, 105f, 0f);
            }

            if (!IsOpen && !_opening)
            {
                ApplyDoorClosedPose();
                SetFallbackDoorRenderersVisible(true);
            }

            CollectDoorColliders();
            CollectFallbackDoorRenderers();
        }

        private void ApplyDoorClosedPose()
        {
            if (_leftHinge != null)
                _leftHinge.localRotation = _leftClosedRotation;
            if (_rightHinge != null)
                _rightHinge.localRotation = _rightClosedRotation;
        }

        private static bool LooksLikeAuthoredOpenRotation(Quaternion rotation)
        {
            return Quaternion.Angle(Quaternion.identity, rotation) > 8f;
        }

        private void CollectDoorColliders()
        {
            _doorColliders.Clear();
            if (_doorBlocker != null)
                _doorColliders.Add(_doorBlocker);
        }

        private void CollectFallbackDoorRenderers()
        {
            _fallbackDoorRenderers.Clear();
            if (_visualRoot == null)
                return;

            Renderer[] renderers = _visualRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                string rendererName = renderer.transform.name;
                if (RendererNameLooksLikeDoorPart(rendererName)
                    || RendererLooksLikeFrontDoor(renderer))
                {
                    _fallbackDoorRenderers.Add(renderer);
                }
            }
        }

        private bool RendererLooksLikeFrontDoor(Renderer renderer)
        {
            if (renderer == null)
                return false;

            ResolveContainerDimensions(_sizeName, out float width, out float height, out float length);
            if (RendererNameLooksStructural(renderer.transform.name))
                return false;

            Vector3 localCenter = transform.InverseTransformPoint(renderer.bounds.center);
            Vector3 localSize = transform.InverseTransformVector(renderer.bounds.size);
            localSize = new Vector3(Mathf.Abs(localSize.x), Mathf.Abs(localSize.y), Mathf.Abs(localSize.z));

            bool nearFront = localCenter.z > -Mathf.Max(0.35f, length * 0.16f);
            bool doorSized = localSize.x > width * 0.22f && localSize.y > height * 0.25f;
            bool notWholeContainer = localSize.z < length * 0.42f;
            return nearFront && doorSized && notWholeContainer;
        }

        private static bool RendererNameLooksLikeDoorPart(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || RendererNameLooksStructural(name))
                return false;

            return name.IndexOf("door", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("hinge", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("handle", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("lock", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("rod", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool RendererNameLooksStructural(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            return name.IndexOf("roof", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("floor", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("sidewall", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("side wall", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("wall", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("frame", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SetFallbackDoorRenderersVisible(bool visible)
        {
            for (int i = 0; i < _fallbackDoorRenderers.Count; i++)
            {
                Renderer renderer = _fallbackDoorRenderers[i];
                if (renderer != null)
                    renderer.enabled = visible;
            }
        }

        /// <summary>
        /// Plugin init, every peer. The container template used to be built and registered lazily inside the
        /// host-gated spawn path, so clients never registered the prefab before NetworkManager started (#454).
        /// Idempotent: <see cref="GetOrCreateRuntimeContainerPrefab"/> latches on <c>_runtimePrefab</c>.
        /// </summary>
        internal static bool PrepareNetworkPrefab()
        {
            try
            {
                GameObject prefab = GetOrCreateRuntimeContainerPrefab(ContractsBootstrap.ShadowRaidContainerPrefabName.Value);
                ContractAssetLoader.LogRegisteredPrefab("[MoonContracts.ShadowRaid]", prefab, _runtimePrefabRegistered);
                return prefab != null;
            }
            catch (System.Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts.ShadowRaid] Failed to prepare the Shadow Raid container network prefab at init: {e}");
                return false;
            }
        }

        internal static bool TryGetContainerPrefab(string preferredAssetName, out GameObject prefab)
        {
            prefab = null;
            if (!_loggedContainerChoice)
            {
                _loggedContainerChoice = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.ShadowRaid] Using bundled modular shipping container visuals for Shadow Raid containers.");
            }

            prefab = GetOrCreateRuntimeContainerPrefab(preferredAssetName);
            return prefab != null;
        }

        internal static BreachContainerController FindNearestClosedContainer(Vector3 position, float maxDistance)
        {
            return FindNearestContainer(position, maxDistance, includeOpen: false);
        }

        internal static BreachContainerController FindNearestContainer(Vector3 position, float maxDistance, bool includeOpen)
        {
            BreachContainerController best = null;
            float bestDistance = maxDistance * maxDistance;
            for (int i = 0; i < ActiveContainers.Count; i++)
            {
                BreachContainerController container = ActiveContainers[i];
                if (!IsLiveRuntimeContainer(container) || !includeOpen && container.IsOpen)
                    continue;

                Vector3 anchorPosition = container.LockAnchor != null ? container.LockAnchor.position : container.transform.position;
                float sqr = (anchorPosition - position).sqrMagnitude;
                if (sqr >= bestDistance)
                    continue;

                bestDistance = sqr;
                best = container;
            }

            return best;
        }

        /// <summary>
        /// NetworkObjectId of the drill currently attached to this container, or 0 when none is.
        /// Replicated, so every peer (including a late joiner) reads the same value.
        /// </summary>
        internal ulong AttachedDrillNetworkId => _attachedDrillNetworkId.Value;

        /// <summary>Server time at which the attached drill finishes; 0 when no drill is attached.</summary>
        internal float AttachedDrillFinishServerTime => _attachedDrillFinishServerTime.Value;

        /// <summary>Server-only. Publishes the drill attachment so clients can run the same visuals.</summary>
        internal void SetAttachedDrillServer(ulong drillNetworkId, float finishServerTime)
        {
            if (!IsServer || !IsSpawned)
                return;

            if (_attachedDrillNetworkId.Value != drillNetworkId)
                _attachedDrillNetworkId.Value = drillNetworkId;
            if (!Mathf.Approximately(_attachedDrillFinishServerTime.Value, finishServerTime))
                _attachedDrillFinishServerTime.Value = finishServerTime;
        }

        /// <summary>Server-only. Clears the replicated drill attachment (completion, removal, reset).</summary>
        internal void ClearAttachedDrillServer()
        {
            SetAttachedDrillServer(0UL, 0f);
        }

        /// <summary>NetworkObjectId of the drill whose completion drop is published here, or 0.</summary>
        internal ulong CompletedDrillNetworkId => _completedDrillNetworkId.Value;

        /// <summary>Monotonic id for the published completion; a re-used drill increments it again.</summary>
        internal int CompletedDrillSequence => _completedDrillSequence.Value;

        internal Vector3 CompletedDrillDropPosition => _completedDrillDropPosition.Value;

        internal Vector3 CompletedDrillFloorPosition => _completedDrillFloorPosition.Value;

        internal float CompletedDrillDropYaw => _completedDrillDropYaw.Value;

        /// <summary>Server time at which the completion was published; used to reject stale poses.</summary>
        internal float CompletedDrillServerTime => _completedDrillServerTime.Value;

        /// <summary>
        /// Server-only. Publishes the finished drill's drop pose and clears the attachment in one step,
        /// so a client never observes the attachment clearing without the pose that goes with it.
        /// </summary>
        internal void PublishDrillCompletionServer(
            ulong drillNetworkId,
            Vector3 dropPosition,
            Vector3 floorPosition,
            float dropYaw)
        {
            if (!IsServer || !IsSpawned)
                return;

            _completedDrillNetworkId.Value = drillNetworkId;
            _completedDrillDropPosition.Value = dropPosition;
            _completedDrillFloorPosition.Value = floorPosition;
            _completedDrillDropYaw.Value = dropYaw;
            _completedDrillServerTime.Value = (float)(NetworkManager.Singleton != null
                ? NetworkManager.Singleton.ServerTime.Time
                : Time.timeAsDouble);
            _completedDrillSequence.Value = _completedDrillSequence.Value + 1;
            ClearAttachedDrillServer();
        }

        /// <summary>
        /// Finds the container that published the given drill's completion drop, per replicated state.
        /// </summary>
        internal static BreachContainerController FindContainerThatCompletedDrill(ulong drillNetworkId)
        {
            if (drillNetworkId == 0UL)
                return null;

            for (int i = 0; i < ActiveContainers.Count; i++)
            {
                BreachContainerController container = ActiveContainers[i];
                if (container != null
                    && container.CompletedDrillSequence > 0
                    && container.CompletedDrillNetworkId == drillNetworkId)
                {
                    return container;
                }
            }

            return null;
        }

        /// <summary>
        /// Pushes replicated drill state straight into the drills instead of waiting for their poll, so
        /// spin, sparks and loop audio stop on the same frame the host opens the container. Every active
        /// drill re-evaluates its own state, which keeps this immune to the order the individual
        /// NetworkVariables in a batch land in.
        /// </summary>
        private void OnAttachedDrillStateChanged(ulong previous, ulong current)
        {
            NotifyDrillsOfReplicatedState();
        }

        private void OnCompletedDrillStateChanged(int previous, int current)
        {
            NotifyDrillsOfReplicatedState();
        }

        private void NotifyDrillsOfReplicatedState()
        {
            if (IsServer)
                return;

            BreachDrillItem.NotifyReplicatedContainerStateChanged();
        }

        /// <summary>
        /// Finds the container that the given drill NetworkObjectId is attached to, per the replicated
        /// state. Clients use this to mirror the server's attachment without owning any network state.
        /// </summary>
        internal static BreachContainerController FindContainerDrilledBy(ulong drillNetworkId)
        {
            if (drillNetworkId == 0UL)
                return null;

            for (int i = 0; i < ActiveContainers.Count; i++)
            {
                BreachContainerController container = ActiveContainers[i];
                if (container != null && container.AttachedDrillNetworkId == drillNetworkId)
                    return container;
            }

            return null;
        }

        internal static BreachContainerController FindByNetworkId(ulong networkObjectId)
        {
            if (networkObjectId == 0UL)
                return null;

            for (int i = 0; i < ActiveContainers.Count; i++)
            {
                BreachContainerController container = ActiveContainers[i];
                if (container != null && container.NetworkId == networkObjectId)
                    return container;
            }

            return null;
        }

        internal static bool HasCompletedDrill()
        {
            for (int i = 0; i < ActiveContainers.Count; i++)
            {
                BreachContainerController container = ActiveContainers[i];
                if (IsLiveRuntimeContainer(container) && container.CompletedDrillSequence > 0)
                    return true;
            }
            return false;
        }

        internal static List<BreachContainerController> GetActiveContainersSnapshot()
        {
            var containers = new List<BreachContainerController>();
            for (int i = 0; i < ActiveContainers.Count; i++)
            {
                BreachContainerController container = ActiveContainers[i];
                if (IsLiveRuntimeContainer(container))
                    containers.Add(container);
            }

            return containers;
        }

        private static bool IsLiveRuntimeContainer(BreachContainerController container)
        {
            if (container == null || !container.gameObject.activeInHierarchy)
                return false;

            // #949 S11H U1: active and spawned used to be the whole test, which is why an
            // Assurance container was still a valid Shadow Raid container on March. A
            // container belongs to the round it was built in and to no other.
            if (container._roundToken != ShadowRaidRoundRegistry.CurrentRound)
                return false;

            container.ResolveRuntimeReferences();
            return container._networkObject != null && container._networkObject.IsSpawned;
        }

        /// <summary>
        /// #949 S11H U1: drops the round's containers from the static registry at run reset.
        /// Called from ShadowRaidContract.ResetRunState, where the objects themselves are being
        /// despawned, and on every peer so a client's list does not keep a destroyed instance
        /// alive until its OnDestroy runs. The filter is the token rather than the whole list,
        /// so a container that somehow registered after the token advanced is left alone; in
        /// practice at reset time every registered container belongs to the round being ended.
        /// OnDestroy removal stays as the belt.
        /// </summary>
        internal static int PurgeRound()
        {
            int removed = 0;
            for (int i = ActiveContainers.Count - 1; i >= 0; i--)
            {
                BreachContainerController container = ActiveContainers[i];
                if (container != null && container._roundToken > ShadowRaidRoundRegistry.CurrentRound)
                    continue;

                ActiveContainers.RemoveAt(i);
                removed++;
            }

            return removed;
        }

        internal static string NormalizeContainerSizeName(string sizeName)
        {
            if (string.IsNullOrWhiteSpace(sizeName))
                return "medium";

            switch (sizeName.Trim().ToLowerInvariant())
            {
                case "s":
                case "small":
                case "50":
                case "0.5":
                case "10":
                    return "small";
                case "l":
                case "large":
                case "100":
                case "1":
                case "40":
                    return "large";
                default:
                    return "medium";
            }
        }

        internal static float ResolveContainerScale(string sizeName)
        {
            return 1f;
        }

        internal static void ResolveContainerDimensions(string sizeName, out float width, out float height, out float length)
        {
            width = BaseWidth;
            height = BaseHeight;
            switch (NormalizeContainerSizeName(sizeName))
            {
                case "small":
                    length = TenFootLength;
                    break;
                case "large":
                    length = FortyFootLength;
                    break;
                default:
                    length = TwentyFootLength;
                    break;
            }
        }

        private static GameObject GetOrCreateRuntimeContainerPrefab(string preferredAssetName)
        {
            if (_runtimePrefab != null)
                return _runtimePrefab;

            GameObject visualPrefab = ContractAssetLoader.GetBreachContainerVisualPrefab("medium")
                                      ?? ContractAssetLoader.BreachContainerVisualPrefab;
            if (visualPrefab == null)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.ShadowRaid] Shipping container prefab '{preferredAssetName}' was not found in lgucontracts.bundle. Runtime will use a generated procedural container visual.");
            }

            GameObject prefab = ContractAssetLoader.CreateHiddenPrefabRoot("LGU_BreachContainerVaultPrefab");

            var networkObject = prefab.AddComponent<NetworkObject>();
            networkObject.SynchronizeTransform = true;
            prefab.AddComponent<NetworkTransform>();

            var body = prefab.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            prefab.AddComponent<BreachContainerController>();

            _runtimePrefabRegistered = ContractAssetLoader.PrepareRuntimeInteriorSupportPrefab(prefab, RuntimePrefabKey);
            _runtimePrefab = prefab;
            return _runtimePrefab;
        }

        private static void CreateGeneratedVisualPart(Transform parent, string name, Vector3 localPosition, Vector3 localScale, Material material)
        {
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localRotation = Quaternion.identity;
            part.transform.localScale = localScale;

            Collider collider = part.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                DestroyUnityObject(collider);
            }

            Renderer renderer = part.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = material;
        }

        private static void PrepareVisualForRuntime(GameObject root)
        {
            if (root == null)
                return;

            SetLayerRecursive(root, 0);
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            int materialSlots = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                renderer.enabled = true;
                renderer.forceRenderingOff = false;
                renderer.gameObject.layer = 0;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;

                Material[] materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                {
                    renderer.sharedMaterial = GetRuntimeContainerMaterial(null, renderer.name, 0);
                    materialSlots++;
                    continue;
                }

                for (int slot = 0; slot < materials.Length; slot++)
                {
                    materials[slot] = GetRuntimeContainerMaterial(materials[slot], renderer.name, slot);
                    materialSlots++;
                }

                renderer.sharedMaterials = materials;
            }

            ShadowRaidSurfaceAssets.ApplyContainer(root);

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Prepared shipping container runtime visual with {renderers.Length} renderer(s) and {materialSlots} material slot(s).");
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            if (root == null)
                return;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null)
                    transforms[i].gameObject.layer = layer;
            }
        }

        private static Material GeneratedContainerMaterial
        {
            get
            {
                if (_generatedContainerMaterial == null)
                    _generatedContainerMaterial = CreateGeneratedMaterial("LGU_BreachGeneratedContainer_Mat", new Color(0.06f, 0.17f, 0.18f, 1f));
                return _generatedContainerMaterial;
            }
        }

        private static Material GeneratedContainerDoorMaterial
        {
            get
            {
                if (_generatedContainerDoorMaterial == null)
                    _generatedContainerDoorMaterial = CreateGeneratedMaterial("LGU_BreachGeneratedContainerDoor_Mat", new Color(0.10f, 0.26f, 0.28f, 1f));
                return _generatedContainerDoorMaterial;
            }
        }

        private static Material EmbeddedCacheDiagnosticMaterial
        {
            get
            {
                if (_embeddedCacheDiagnosticMaterial == null)
                    _embeddedCacheDiagnosticMaterial = CreateDiagnosticMaterial("LGU_EmbeddedCacheDiagnostic_Cache", new Color(0f, 0.85f, 1f, 1f));
                return _embeddedCacheDiagnosticMaterial;
            }
        }

        private static Material EmbeddedCacheDiagnosticDoorMaterial
        {
            get
            {
                if (_embeddedCacheDiagnosticDoorMaterial == null)
                    _embeddedCacheDiagnosticDoorMaterial = CreateDiagnosticMaterial("LGU_EmbeddedCacheDiagnostic_Door", new Color(1f, 0.95f, 0.08f, 1f));
                return _embeddedCacheDiagnosticDoorMaterial;
            }
        }

        private static Material EmbeddedCacheDiagnosticDepthMaterial
        {
            get
            {
                if (_embeddedCacheDiagnosticDepthMaterial == null)
                    _embeddedCacheDiagnosticDepthMaterial = CreateDiagnosticMaterial("LGU_EmbeddedCacheDiagnostic_Depth", new Color(0.1f, 1f, 0.15f, 1f));
                return _embeddedCacheDiagnosticDepthMaterial;
            }
        }

        private static Material EmbeddedCacheDiagnosticBackMaterial
        {
            get
            {
                if (_embeddedCacheDiagnosticBackMaterial == null)
                    _embeddedCacheDiagnosticBackMaterial = CreateDiagnosticMaterial("LGU_EmbeddedCacheDiagnostic_Back", new Color(1f, 0.1f, 0.1f, 1f));
                return _embeddedCacheDiagnosticBackMaterial;
            }
        }

        private static Material CreateGeneratedMaterial(string name, Color color)
        {
            Shader shader = ResolveRuntimeVisualShader();
            Material material = new Material(shader) { name = name, hideFlags = HideFlags.HideAndDontSave };
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            ForceOpaque(material);
            return material;
        }

        private static Material CreateDiagnosticMaterial(string name, Color color)
        {
            Material material = CreateGeneratedMaterial(name, color);
            Color emissive = new Color(color.r * 1.75f, color.g * 1.75f, color.b * 1.75f, 1f);
            SetColorIfPresent(material, "_EmissionColor", emissive);
            SetColorIfPresent(material, "_EmissiveColor", emissive);
            material.EnableKeyword("_EMISSION");
            material.EnableKeyword("_MATERIAL_FEATURE_EMISSION");
            return material;
        }

        private static Material GetRuntimeContainerMaterial(Material source, string rendererName, int slot)
        {
            if (source != null && RuntimeContainerMaterials.TryGetValue(source, out Material cached))
                return cached;

            Shader shader = ResolveRuntimeVisualShader();
            if (shader == null)
                return source != null ? source : GeneratedContainerMaterial;

            string sourceName = source != null ? source.name : "Fallback";
            var material = new Material(shader)
            {
                name = "LGU_BreachContainerRuntime_" + sourceName,
                hideFlags = HideFlags.HideAndDontSave
            };

            Texture baseColor = GetTextureIfPresent(source, "_BaseColorMap")
                                ?? GetTextureIfPresent(source, "_MainTex")
                                ?? GetTextureIfPresent(source, "_BaseMap");
            if (baseColor != null)
            {
                SetTextureIfPresent(material, "_BaseColorMap", baseColor);
                SetTextureIfPresent(material, "_MainTex", baseColor);
                SetTextureIfPresent(material, "_BaseMap", baseColor);
                SetColorIfPresent(material, "_BaseColor", Color.white);
                SetColorIfPresent(material, "_Color", Color.white);
            }
            else
            {
                Color color = InferContainerColor(sourceName, rendererName, slot);
                SetColorIfPresent(material, "_BaseColor", color);
                SetColorIfPresent(material, "_Color", color);
            }

            Texture normal = GetTextureIfPresent(source, "_NormalMap")
                             ?? GetTextureIfPresent(source, "_BumpMap");
            if (normal != null)
            {
                SetTextureIfPresent(material, "_NormalMap", normal);
                SetTextureIfPresent(material, "_BumpMap", normal);
                material.EnableKeyword("_NORMALMAP");
                material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }

            Texture metallic = GetTextureIfPresent(source, "_MetallicGlossMap")
                               ?? GetTextureIfPresent(source, "_MaskMap");
            if (metallic != null)
            {
                SetTextureIfPresent(material, "_MetallicGlossMap", metallic);
                SetTextureIfPresent(material, "_MaskMap", metallic);
            }

            Texture occlusion = GetTextureIfPresent(source, "_OcclusionMap");
            if (occlusion != null)
                SetTextureIfPresent(material, "_OcclusionMap", occlusion);

            SetFloatIfPresent(material, "_Metallic", 0f);
            SetFloatIfPresent(material, "_Smoothness", 0.34f);
            SetFloatIfPresent(material, "_SmoothnessRemapMax", 0.34f);
            SetFloatIfPresent(material, "_Glossiness", 0.34f);
            SetColorIfPresent(material, "_EmissionColor", Color.black);
            SetColorIfPresent(material, "_EmissiveColor", Color.black);
            ForceOpaque(material);

            if (source != null)
                RuntimeContainerMaterials[source] = material;

            return material;
        }

        private static Shader ResolveRuntimeVisualShader()
        {
            return Shader.Find("HDRP/Lit")
                   ?? Shader.Find("High Definition Render Pipeline/Lit")
                   ?? Shader.Find("HDRenderPipeline/Lit")
                   ?? Shader.Find("Universal Render Pipeline/Lit")
                   ?? Shader.Find("Standard")
                   ?? Shader.Find("Unlit/Color");
        }

        private static Color InferContainerColor(string materialName, string rendererName, int slot)
        {
            string haystack = ((materialName ?? string.Empty) + " " + (rendererName ?? string.Empty)).ToLowerInvariant();
            if (haystack.Contains("rust"))
                return new Color(0.42f, 0.18f, 0.09f, 1f);
            if (haystack.Contains("orange"))
                return new Color(0.63f, 0.28f, 0.08f, 1f);
            if (haystack.Contains("red"))
                return new Color(0.45f, 0.08f, 0.06f, 1f);
            if (haystack.Contains("blue"))
                return new Color(0.48f, 0.07f, 0.035f, 1f);
            if (haystack.Contains("floor") || haystack.Contains("roof") || slot > 0)
                return new Color(0.13f, 0.15f, 0.14f, 1f);

            return new Color(0.42f, 0.08f, 0.045f, 1f);
        }

        private static Transform FindDoorTransform(Transform root, bool left)
        {
            if (root == null)
                return null;

            Transform best = null;
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate == null || candidate == root)
                    continue;

                string name = candidate.name.ToLowerInvariant();
                if (name.IndexOf("door") < 0)
                    continue;

                bool isLeft = name.Contains("left") || name.Contains("_l") || name.Contains(" l") || name.EndsWith("l");
                bool isRight = name.Contains("right") || name.Contains("_r") || name.Contains(" r") || name.EndsWith("r");
                if (left && isLeft)
                    return candidate;
                if (!left && isRight)
                    return candidate;

                best = best ?? candidate;
            }

            return best;
        }

        private static string BuildTransformPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string path = transform.name;
            Transform current = transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"{value.x:0.00},{value.y:0.00},{value.z:0.00}";
        }

        private static Texture GetTextureIfPresent(Material material, string property)
        {
            return material != null && material.HasProperty(property) ? material.GetTexture(property) : null;
        }

        private static void SetTextureIfPresent(Material material, string property, Texture texture)
        {
            if (material != null && texture != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        private static void SetColorIfPresent(Material material, string property, Color color)
        {
            if (material != null && material.HasProperty(property))
                material.SetColor(property, color);
        }

        private static void SetFloatIfPresent(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private static void ForceOpaque(Material material)
        {
            if (material == null)
                return;

            material.renderQueue = 2000;
            material.SetOverrideTag("RenderType", "Opaque");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            SetFloatIfPresent(material, "_SurfaceType", 0f);
            SetFloatIfPresent(material, "_BlendMode", 0f);
            SetFloatIfPresent(material, "_AlphaCutoffEnable", 0f);
            SetFloatIfPresent(material, "_ZWrite", 1f);
            SetFloatIfPresent(material, "_SrcBlend", (float)BlendMode.One);
            SetFloatIfPresent(material, "_DstBlend", (float)BlendMode.Zero);
            SetFloatIfPresent(material, "_AlphaSrcBlend", (float)BlendMode.One);
            SetFloatIfPresent(material, "_AlphaDstBlend", (float)BlendMode.Zero);
        }

        private static int SizeNameToIndex(string sizeName)
        {
            switch (NormalizeContainerSizeName(sizeName))
            {
                case "small":
                    return 0;
                case "large":
                    return 2;
                default:
                    return 1;
            }
        }

        private static string IndexToSizeName(int index)
        {
            switch (index)
            {
                case 0:
                    return "small";
                case 2:
                    return "large";
                default:
                    return "medium";
            }
        }

        private static Transform FindChildByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
                return null;

            if (string.Equals(root.name, name, System.StringComparison.OrdinalIgnoreCase))
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildByName(root.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static void StripNestedRuntimeComponents(GameObject root)
        {
            if (root == null)
                return;

            Component[] components = root.GetComponentsInChildren<Component>(true);
            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null || component is Transform)
                    continue;

                if (component is NetworkObject ||
                    component is NetworkBehaviour ||
                    component is NetworkTransform ||
                    component is Rigidbody ||
                    component is AudioSource ||
                    component is Collider)
                {
                    Object.DestroyImmediate(component);
                }
            }
        }

        private static void DestroyUnityObject(Object obj)
        {
            if (obj == null)
                return;

            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }
    }
}
