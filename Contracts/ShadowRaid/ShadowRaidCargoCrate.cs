using System;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Escalations._Shared;
namespace Y4NGZCompany.Contracts.ShadowRaid
{
    internal sealed class ShadowRaidCargoCrate : NetworkBehaviour
    {
        private const int InteractLayer = 9;
        private const int ScanNodeLayer = 22;
        private const float ContainedCacheReachRange = 3.15f;
        private const float DefaultCacheReachRange = 2.35f;
        private const float BreachContainerAssociationRange = 3.0f;
        private const float FallbackOpenDuration = 1.1f;
        private const float RefreshInterval = 0.125f;
        private const float ContainedItemRefreshInterval = 0.25f;
        private const float ManualInteractCooldown = 0.35f;
        private const float ManualInteractFacingDot = 0.42f;
        private const float CacheOpenHoldSeconds = 10f;
        private const float RuntimeComponentsFastRetryDuration = 5f;
        private const float RuntimeComponentsSlowRetryInterval = 1f;

        private readonly NetworkVariable<bool> _retrievalCargo = new NetworkVariable<bool>();
        internal void ConfigureRetrievalCargoServer() { if (IsServer) _retrievalCargo.Value = true; }

        private readonly NetworkVariable<bool> _openedState = new NetworkVariable<bool>(false);
        private readonly NetworkVariable<ulong> _containedItemNetworkId = new NetworkVariable<ulong>(0UL);
        private readonly NetworkVariable<bool> _containedItemUnlocked = new NetworkVariable<bool>(false);

        private InteractTrigger _trigger;
        private ScanNodeProperties _scanNode;
        private AudioSource _audioSource;
        private NetworkObject _networkObject;
        private Transform _itemAnchor;
        private Transform _lidPivot;
        private Animator _animator;
        private Animation _legacyAnimation;
        private Collider[] _interactionColliders;
        private Renderer[] _crateVisualRenderers;
        private GameObject _containedItemLookupObject;
        private Quaternion _lidClosedRotation;
        private Quaternion _lidOpenRotation;
        private ulong _containedItemLookupNetworkId;
        private ulong _containedItemLockRefreshDoneNetworkId;
        private bool _suppressVisualRenderers;
        private bool _opened;
        private bool _listenerBound;
        private bool _lidCaptured;
        private bool _runtimeComponentsResolved;
        private bool _animationComponentsResolved;
        private bool _lidPivotResolved;
        private bool _fallbackOpening;
        private bool _loggedVisualState;
        private bool _loggedDebugHierarchy;
        private bool _loggedOpenAnimation;
        private bool _loggedContainedItemReveal;
        private bool _loggedManualInteractFallback;
        private bool _containedItemLookupSceneScanAttempted;
        private bool _containedItemLookupMissing;
        private bool _containedItemLockRefreshDone;
        private bool _crateVisualOk;
        private bool _crateVisualOpenedState;
        private bool _crateVisualSuppressedState;
        private bool _runtimeComponentFailureWindowStarted;
        private bool _loggedRuntimeComponentResolutionWarning;
        private int _runtimeComponentResolveFailureCount;
        // #949 S11H U1: the round this crate was built in. See IsLiveThisRound.
        private int _roundToken;
        private float _fallbackOpenProgress;
        private float _nextInteractionRefreshAt;
        private float _nextCrateVisualRefreshAt;
        private float _nextContainedItemRefreshAt;
        private float _manualInteractCooldownUntil;
        private float _manualInteractHoldStartedAt;
        private float _firstRuntimeComponentFailureAt;
        private float _nextRuntimeComponentRetryAt;

        internal ulong NetworkId => _networkObject != null ? _networkObject.NetworkObjectId : 0UL;
        // #949 S11H U1: the crate carries the same round token its container does, so
        // BreachContainerController.TryGetEmbeddedHardDriveTarget answers one question rather
        // than two. Stamped in Awake on every peer; nothing about the crate's deck, its audio
        // or its interaction changes with it.
        internal bool IsLiveThisRound => _roundToken == ShadowRaidRoundRegistry.CurrentRound;
        internal bool Opened => _opened || _openedState.Value;
        internal bool IsRetrievalCargo => _retrievalCargo.Value;
        internal ulong ContainedItemNetworkId => _containedItemNetworkId.Value;
        internal Vector3 InteractionPosition => _trigger != null
            ? _trigger.transform.position
            : transform.position + Vector3.up * 0.42f;
        internal bool SuppressVisualRenderers
        {
            get => _suppressVisualRenderers;
            set
            {
                if (_suppressVisualRenderers == value)
                    return;

                _suppressVisualRenderers = value;
                ResetCrateVisualRefreshState();
            }
        }

        private void Awake()
        {
            // #949 S11H U1: stamped before anything can read it. OnEnable is not the hook: a
            // crate that is disabled and re-enabled inside its own round is the same crate.
            _roundToken = ShadowRaidRoundRegistry.CurrentRound;
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            ResetContainedItemPerformanceState();
            ResetCrateVisualRefreshState();
            ResetRuntimeComponentRetryState();
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ResetContainedItemPerformanceState();
            ResetCrateVisualRefreshState();
            ResetRuntimeComponentRetryState();
        }

        private void OnSceneLoaded(Scene _, LoadSceneMode __)
        {
            ResetContainedItemPerformanceState();
            ResetCrateVisualRefreshState();
            ResetRuntimeComponentRetryState();
        }

        private void ResetContainedItemPerformanceState()
        {
            ResetContainedItemResolutionState();
            ResetContainedItemLockRefreshDone();
            _nextContainedItemRefreshAt = 0f;
        }

        private void ResetContainedItemResolutionState()
        {
            _containedItemLookupNetworkId = 0UL;
            _containedItemLookupObject = null;
            _containedItemLookupSceneScanAttempted = false;
            _containedItemLookupMissing = false;
        }

        private void ResetContainedItemLockRefreshDone()
        {
            _containedItemLockRefreshDone = false;
            _containedItemLockRefreshDoneNetworkId = 0UL;
        }

        private void ResetCrateVisualRefreshState()
        {
            _crateVisualOk = false;
            _crateVisualRenderers = null;
            _crateVisualOpenedState = Opened;
            _crateVisualSuppressedState = SuppressVisualRenderers;
            _nextCrateVisualRefreshAt = 0f;
            _runtimeComponentsResolved = false;
        }

        private void ResetRuntimeComponentRetryState()
        {
            _runtimeComponentFailureWindowStarted = false;
            _runtimeComponentResolveFailureCount = 0;
            _loggedRuntimeComponentResolutionWarning = false;
            _firstRuntimeComponentFailureAt = 0f;
            _nextRuntimeComponentRetryAt = 0f;
        }

        private bool ShouldSkipRuntimeComponentRetry()
        {
            if (!_runtimeComponentFailureWindowStarted)
                return false;
            if (Time.unscaledTime - _firstRuntimeComponentFailureAt < RuntimeComponentsFastRetryDuration)
                return false;

            return Time.unscaledTime < _nextRuntimeComponentRetryAt;
        }

        private void RegisterRuntimeComponentResolutionFailure()
        {
            float now = Time.unscaledTime;
            if (!_runtimeComponentFailureWindowStarted)
            {
                _runtimeComponentFailureWindowStarted = true;
                _firstRuntimeComponentFailureAt = now;
            }

            _runtimeComponentResolveFailureCount++;
            if (now - _firstRuntimeComponentFailureAt < RuntimeComponentsFastRetryDuration)
                return;

            _nextRuntimeComponentRetryAt = now + RuntimeComponentsSlowRetryInterval;
            if (!_loggedRuntimeComponentResolutionWarning)
                LogRuntimeComponentResolutionWarning();
        }

        private void LogRuntimeComponentResolutionWarning()
        {
            _loggedRuntimeComponentResolutionWarning = true;
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.Blacksite] Military cache runtime component resolution incomplete after {RuntimeComponentsFastRetryDuration:0.#}s " +
                $"for '{name}'; retrying once per second. Missing: {GetRuntimeComponentFailureSummary()}.");
        }

        private string GetRuntimeComponentFailureSummary()
        {
            List<string> missing = new List<string>();
            if (_networkObject == null)
                missing.Add(nameof(_networkObject));
            if (_audioSource == null)
                missing.Add(nameof(_audioSource));
            if (!_animationComponentsResolved)
                missing.Add(nameof(_animationComponentsResolved));
            if (_itemAnchor == null)
                missing.Add(nameof(_itemAnchor));
            if (!_lidPivotResolved)
                missing.Add(nameof(_lidPivotResolved));
            if (_trigger == null)
                missing.Add(nameof(_trigger));
            if (_scanNode == null)
                missing.Add(nameof(_scanNode));
            if (!_listenerBound)
                missing.Add(nameof(_listenerBound));

            return missing.Count > 0 ? string.Join(", ", missing) : "none";
        }

        private void Start()
        {
            EnsureRuntimeComponents();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _networkObject = NetworkObject;
            _openedState.OnValueChanged += OnOpenedStateChanged;
            _containedItemNetworkId.OnValueChanged += OnContainedItemChanged;
            _containedItemUnlocked.OnValueChanged += OnContainedItemUnlockedChanged;

            if (_openedState.Value)
                ApplyOpenedLocal();
            ApplyContainedItemLockLocal();
        }

        public override void OnNetworkDespawn()
        {
            _openedState.OnValueChanged -= OnOpenedStateChanged;
            _containedItemNetworkId.OnValueChanged -= OnContainedItemChanged;
            _containedItemUnlocked.OnValueChanged -= OnContainedItemUnlockedChanged;
            ResetContainedItemPerformanceState();
            ResetRuntimeComponentRetryState();
            base.OnNetworkDespawn();
        }

        private void Update()
        {
            EnsureRuntimeComponents();
            if (Time.unscaledTime >= _nextInteractionRefreshAt)
            {
                _nextInteractionRefreshAt = Time.unscaledTime + RefreshInterval;
                RefreshInteractionAvailabilityLocal();
            }

            UpdateFallbackOpenAnimation();
            TryHandleLocalInteractFallback();

            if (ShouldRefreshContainedItemLock() && Time.time >= _nextContainedItemRefreshAt)
            {
                _nextContainedItemRefreshAt = Time.time + ContainedItemRefreshInterval;
                ApplyContainedItemLockLocal();
            }
        }

        internal void OpenFromServer(PlayerControllerB opener = null)
        {
            if (Opened)
                return;

            if (opener != null && !PlayerCanReachCache(opener))
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Military cache open rejected: crateNetId={NetworkId}, " +
                    $"opener='{opener.playerUsername}', reason=blocked-or-out-of-reach.");
                return;
            }

            if (IsServerRuntime() && IsSpawned && !_openedState.Value)
                _openedState.Value = true;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Military cache open accepted: crateNetId={NetworkId}, " +
                $"opener='{(opener != null ? opener.playerUsername : "server")}', containedNetId={_containedItemNetworkId.Value}.");
            ApplyOpenedLocal();
            RevealContainedItemServer();
            MoonEscalationState.NotifyBlacksiteCrateOpened(this);
        }

        internal bool TryOpenFromBlacksiteOverrideServer(
            BlacksiteRetrievalMissionController mission)
        {
            if (!IsServerRuntime()
                || !IsSpawned
                || mission == null
                || !mission.IsServer
                || mission.IsTerminal
                || mission.Contract != MoonContractType.ShadowRaid)
            {
                return false;
            }

            EnsureRuntimeComponents();
            if (NetworkId == 0UL || mission.SecondaryTargetNetworkId != NetworkId)
                return false;

            BreachContainerController container = _retrievalCargo.Value ? null : FindAssociatedBreachContainer();
            if (container == null
                || !container.IsOpen
                || mission.ExpectedTargetNetworkId != container.NetworkId)
            {
                return false;
            }

            OpenFromServer();
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Internal-cache override accepted: " +
                $"cacheNetId={NetworkId}, missionNetId={mission.MissionNetworkId}.");
            return Opened;
        }

        internal bool ConfigureRandomContainedRewardServer()
        {
            Item reward = ChooseRewardItem();
            if (reward == null)
            {
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.Blacksite] Military cache configured empty.");
                ForceInteractionRefreshLocal();
                return true;
            }

            return ConfigureContainedItemServer(reward, objectiveHardDrive: false);
        }

        internal bool ConfigureContainedItemServer(Item item, bool objectiveHardDrive)
        {
            if (!IsServerRuntime())
                return false;
            if (item?.spawnPrefab == null)
                return false;

            EnsureRuntimeComponents();
            if (_containedItemNetworkId.Value != 0UL)
            {
                GameObject existing = ResolveContainedItemGameObject(_containedItemNetworkId.Value);
                if (ContainedItemMatches(existing, item))
                {
                    if (objectiveHardDrive)
                        ConfigureContainedHardDriveObjectiveServer(existing);
                    ApplyContainedItemLockLocal();
                    return true;
                }

                if (!objectiveHardDrive)
                    return true;

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Replacing stale military cache item '{GetContainedItemDebugName()}' with objective '{item.itemName}'.");
                RemoveContainedItemServer(existing);
            }

            Transform anchor = GetItemAnchor();
            GameObject spawned = Instantiate(item.spawnPrefab, anchor.position, anchor.rotation, StartOfRound.Instance?.propsContainer);
            if (spawned == null)
                return false;

            if (!spawned.activeSelf)
                spawned.SetActive(true);

            PrepareContainedItem(spawned, item, unlocked: false);

            NetworkObject networkObject = spawned.GetComponent<NetworkObject>();
            if (networkObject != null && !networkObject.IsSpawned)
            {
                networkObject.Spawn();
                if (StartOfRound.Instance?.propsContainer != null)
                    networkObject.TrySetParent(StartOfRound.Instance.propsContainer);
            }

            if (networkObject == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Contained reward '{item.itemName}' has no NetworkObject.");
                return false;
            }

            _containedItemNetworkId.Value = networkObject.NetworkObjectId;
            _containedItemUnlocked.Value = false;
            if (objectiveHardDrive)
                ConfigureContainedHardDriveObjectiveServer(spawned);
            ApplyContainedItemLockLocal();
            ForceInteractionRefreshLocal();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Military cache contains '{item.itemName}'{(objectiveHardDrive ? " (objective)" : string.Empty)}.");
            return true;
        }

        internal bool TryGetContainedHardDrive(out BreachHardDriveItem drive)
        {
            drive = null;
            GameObject itemObject = ResolveContainedItemGameObject(_containedItemNetworkId.Value);
            if (itemObject == null)
                return false;

            drive = itemObject.GetComponent<BreachHardDriveItem>();
            return drive != null;
        }

        private static void ConfigureContainedHardDriveObjectiveServer(GameObject itemObject)
        {
            BreachHardDriveItem drive = itemObject != null
                ? itemObject.GetComponent<BreachHardDriveItem>()
                : null;
            drive?.ConfigureObjectiveServer(
                MoonContractType.ShadowRaid,
                missionNetworkId: 0UL,
                eventCreated: false);
        }

        internal void ApplyOpenedLocal()
        {
            if (_opened)
                return;

            _opened = true;
            ResetContainedItemLockRefreshDone();
            ResetCrateVisualRefreshState();
            ForceInteractionRefreshLocal();
            EnsureRuntimeComponents();

            if (IsServerRuntime() && IsSpawned && !_openedState.Value)
                _openedState.Value = true;

            if (_trigger != null)
            {
                _trigger.interactable = false;
                _trigger.hoverTip = "[ Cache opened ]";
                _trigger.disabledHoverTip = "[ Cache opened ]";
                SetInteractionColliderEnabled(false);
            }

            if (_scanNode != null)
                _scanNode.subText = "cache opened";

            StartOpenVisualLocal();
            PlayOpenSound();
            ApplyContainedItemLockLocal();
        }

        internal static ShadowRaidCargoCrate FindByNetworkId(ulong networkObjectId)
        {
            if (networkObjectId == 0UL)
                return null;

            var networkManager = NetworkManager.Singleton;
            if (networkManager?.SpawnManager?.SpawnedObjects != null
                && networkManager.SpawnManager.SpawnedObjects.TryGetValue(networkObjectId, out NetworkObject networkObject)
                && networkObject != null)
            {
                return networkObject.GetComponent<ShadowRaidCargoCrate>();
            }

            foreach (ShadowRaidCargoCrate crate in FindObjectsOfType<ShadowRaidCargoCrate>())
            {
                if (crate != null && crate.NetworkId == networkObjectId)
                    return crate;
            }

            return null;
        }

        internal static Item ChooseRewardItemForBlacksite()
        {
            return ChooseRewardItem();
        }

        private void OnOpenedStateChanged(bool _, bool isOpen)
        {
            ResetContainedItemLockRefreshDone();
            ResetCrateVisualRefreshState();
            ForceInteractionRefreshLocal();
            if (isOpen)
                ApplyOpenedLocal();
        }

        private void OnContainedItemChanged(ulong _, ulong __)
        {
            ResetContainedItemPerformanceState();
            ApplyContainedItemLockLocal();
            ForceInteractionRefreshLocal();
        }

        private void OnContainedItemUnlockedChanged(bool _, bool __)
        {
            ResetContainedItemLockRefreshDone();
            _nextContainedItemRefreshAt = 0f;
            ApplyContainedItemLockLocal();
        }

        private void EnsureRuntimeComponents()
        {
            if (_runtimeComponentsResolved)
                return;
            if (ShouldSkipRuntimeComponentRetry())
                return;

            if (_networkObject == null)
                _networkObject = GetComponent<NetworkObject>();
            if (_audioSource == null)
            {
                _audioSource = GetComponent<AudioSource>();
                if (_audioSource == null)
                    _audioSource = gameObject.AddComponent<AudioSource>();
                _audioSource.playOnAwake = false;
                _audioSource.spatialBlend = 1f;
                _audioSource.rolloffMode = AudioRolloffMode.Linear;
                _audioSource.minDistance = 2f;
                _audioSource.maxDistance = 24f;
                _audioSource.volume = 0.75f;
            }

            if (!_animationComponentsResolved)
            {
                _animator = GetComponentInChildren<Animator>(true);
                _legacyAnimation = GetComponentInChildren<Animation>(true);
                _animationComponentsResolved = true;
            }

            if (ShouldRefreshCrateVisual() && Time.unscaledTime >= _nextCrateVisualRefreshAt)
            {
                _nextCrateVisualRefreshAt = Time.unscaledTime + RefreshInterval;
                EnsureCrateVisualVisible();
            }

            if (_itemAnchor == null)
                _itemAnchor = FindOrCreateItemAnchor();
            if (!_lidPivotResolved)
            {
                _lidPivot = FindChildByNameToken(transform, "lid")
                            ?? FindChildByNameToken(transform, "cover")
                            ?? FindChildByNameToken(transform, "top")
                            ?? FindChildByNameToken(transform, "door");
                _lidPivotResolved = true;
            }

            CaptureFallbackLidPose();

            if (_trigger == null)
                BuildInteraction();
            ApplyInteractionIcons();
            if (_scanNode == null)
                BuildScanNode();
            if (_trigger != null && !_listenerBound)
            {
                _trigger.onInteract.RemoveListener(OnInteract);
                _trigger.onInteract.AddListener(OnInteract);
                _listenerBound = true;
            }

            _runtimeComponentsResolved = _networkObject != null
                && _audioSource != null
                && _animationComponentsResolved
                && _itemAnchor != null
                && _lidPivotResolved
                && _trigger != null
                && _scanNode != null
                && _listenerBound;
            if (_runtimeComponentsResolved)
                ResetRuntimeComponentRetryState();
            else
                RegisterRuntimeComponentResolutionFailure();
        }

        private void TryHandleLocalInteractFallback()
        {
            if (Opened || Time.time < _manualInteractCooldownUntil)
                return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.eKey.isPressed)
            {
                _manualInteractHoldStartedAt = 0f;
                return;
            }

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                                       ?? StartOfRound.Instance?.localPlayerController;
            if (player == null || player.isPlayerDead || !player.isPlayerControlled)
                return;

            if (!PlayerCanReachCache(player) || !PlayerIsFacingCache(player))
            {
                _manualInteractHoldStartedAt = 0f;
                return;
            }

            if (_manualInteractHoldStartedAt <= 0f)
                _manualInteractHoldStartedAt = Time.time;
            if (Time.time - _manualInteractHoldStartedAt < CacheOpenHoldSeconds)
                return;

            _manualInteractCooldownUntil = Time.time + ManualInteractCooldown;
            _manualInteractHoldStartedAt = 0f;
            if (!_loggedManualInteractFallback)
            {
                _loggedManualInteractFallback = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.Blacksite] Military cache accepted local held-E fallback interaction.");
            }

            OnInteract(player);
        }

        private bool PlayerIsFacingCache(PlayerControllerB player)
        {
            Transform camera = player?.gameplayCamera != null ? player.gameplayCamera.transform : player?.transform;
            if (camera == null)
                return false;

            Vector3 target = _trigger != null
                ? _trigger.transform.position
                : transform.position + Vector3.up * 0.42f;
            Vector3 toTarget = target - camera.position;
            if (toTarget.sqrMagnitude < 0.05f)
                return true;

            return Vector3.Dot(camera.forward, toTarget.normalized) >= ManualInteractFacingDot;
        }

        private void BuildInteraction()
        {
            Transform existing = transform.Find("InteractionTrigger");
            GameObject triggerObject = existing != null
                ? existing.gameObject
                : new GameObject("InteractionTrigger");
            triggerObject.transform.SetParent(transform, false);
            Bounds bounds = CalculateRendererBoundsLocal();
            bool hasBounds = bounds.size.sqrMagnitude > 0.001f;
            triggerObject.transform.localPosition = hasBounds
                ? bounds.center + new Vector3(0f, 0.08f, 0.06f)
                : new Vector3(0f, 0.5f, 0.06f);
            triggerObject.layer = InteractLayer;
            SetTagSafe(triggerObject, "InteractTrigger");

            BoxCollider collider = triggerObject.GetComponent<BoxCollider>();
            if (collider == null)
                collider = triggerObject.AddComponent<BoxCollider>();
            collider.center = Vector3.zero;
            collider.size = hasBounds
                ? new Vector3(
                    Mathf.Max(0.85f, bounds.size.x + 0.12f),
                    Mathf.Max(0.72f, bounds.size.y + 0.16f),
                    Mathf.Max(0.72f, bounds.size.z + 0.12f))
                : new Vector3(0.95f, 0.72f, 0.8f);
            collider.isTrigger = true;

            _trigger = triggerObject.GetComponent<InteractTrigger>();
            if (_trigger == null)
                _trigger = triggerObject.AddComponent<InteractTrigger>();
            _interactionColliders = null;

            _trigger.interactable = !Opened;
            _trigger.oneHandedItemAllowed = true;
            _trigger.twoHandedItemAllowed = true;
            _trigger.holdInteraction = true;
            _trigger.timeToHold = CacheOpenHoldSeconds;
            _trigger.timeToHoldSpeedMultiplier = 1f;
            _trigger.interactCooldown = true;
            _trigger.cooldownTime = 0.65f;
            _trigger.hoverTip = "Open military cache : [E]";
            _trigger.holdTip = "Opening military cache";
            _trigger.disabledHoverTip = "[ Cache opened ]";
            _trigger.disableTriggerMesh = true;
            ApplyInteractionIcons();
            if (_trigger.onInteract == null)
                _trigger.onInteract = new InteractEvent();
            if (_trigger.onInteractEarly == null)
                _trigger.onInteractEarly = new InteractEvent();
            if (_trigger.onInteractEarlyOtherClients == null)
                _trigger.onInteractEarlyOtherClients = new InteractEvent();
            if (_trigger.onStopInteract == null)
                _trigger.onStopInteract = new InteractEvent();
            if (_trigger.onCancelAnimation == null)
                _trigger.onCancelAnimation = new InteractEvent();
            if (_trigger.holdingInteractEvent == null)
                _trigger.holdingInteractEvent = new InteractEventFloat();
        }

        private void ForceInteractionRefreshLocal()
        {
            _nextInteractionRefreshAt = 0f;
        }

        private void RefreshInteractionAvailabilityLocal()
        {
            if (_trigger == null)
                return;

            ApplyInteractionIcons();
            if (Opened)
            {
                if (_trigger.interactable)
                    _trigger.interactable = false;
                if (!string.Equals(_trigger.hoverTip, "[ Cache opened ]", StringComparison.Ordinal))
                    _trigger.hoverTip = "[ Cache opened ]";
                if (!string.Equals(_trigger.disabledHoverTip, "[ Cache opened ]", StringComparison.Ordinal))
                    _trigger.disabledHoverTip = "[ Cache opened ]";
                SetInteractionColliderEnabled(false);
                return;
            }

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                                       ?? StartOfRound.Instance?.localPlayerController;
            bool reachable = player == null || PlayerCanReachCache(player);
            if (_trigger.interactable != reachable)
                _trigger.interactable = reachable;
            if (!string.Equals(_trigger.hoverTip, "Open military cache : [E]", StringComparison.Ordinal))
                _trigger.hoverTip = "Open military cache : [E]";
            string disabledHoverTip = reachable ? "[ Cache opened ]" : "Move inside the opened container";
            if (!string.Equals(_trigger.disabledHoverTip, disabledHoverTip, StringComparison.Ordinal))
                _trigger.disabledHoverTip = disabledHoverTip;
            SetInteractionColliderEnabled(reachable);
        }

        private void SetInteractionColliderEnabled(bool enabled)
        {
            if (_trigger == null)
                return;

            Collider[] colliders = GetInteractionColliders();
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider != null && collider.enabled != enabled)
                    collider.enabled = enabled;
            }
        }

        private Collider[] GetInteractionColliders()
        {
            if (_trigger == null)
                return Array.Empty<Collider>();

            if (_interactionColliders == null || _interactionColliders.Length == 0 || ContainsMissingCollider(_interactionColliders))
                _interactionColliders = _trigger.GetComponentsInChildren<Collider>(true);

            return _interactionColliders;
        }

        private static bool ContainsMissingCollider(Collider[] colliders)
        {
            if (colliders == null)
                return true;

            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] == null)
                    return true;
            }

            return false;
        }

        private void ApplyInteractionIcons()
        {
            if (_trigger == null || _trigger.hoverIcon != null)
                return;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                                       ?? StartOfRound.Instance?.localPlayerController;
            if (player?.grabItemIcon == null)
                return;

            _trigger.hoverIcon = player.grabItemIcon;
            _trigger.disabledHoverIcon = player.grabItemIcon;
        }

        private void BuildScanNode()
        {
            Transform existing = transform.Find("BlacksiteCacheScanNode");
            GameObject scanObject = existing != null
                ? existing.gameObject
                : new GameObject("BlacksiteCacheScanNode");
            scanObject.transform.SetParent(transform, false);
            scanObject.transform.localPosition = new Vector3(0f, 0.95f, 0f);
            scanObject.layer = ScanNodeLayer;

            SphereCollider collider = scanObject.GetComponent<SphereCollider>();
            if (collider == null)
                collider = scanObject.AddComponent<SphereCollider>();
            collider.radius = 0.42f;
            collider.isTrigger = true;

            _scanNode = scanObject.GetComponent<ScanNodeProperties>();
            if (_scanNode == null)
                _scanNode = scanObject.AddComponent<ScanNodeProperties>();
            _scanNode.headerText = "MILITARY CACHE";
            _scanNode.subText = Opened ? "cache opened" : "sealed cargo";
            _scanNode.maxRange = 28;
            _scanNode.minRange = 1;
            _scanNode.requiresLineOfSight = false;
            _scanNode.nodeType = 0;
            _scanNode.scrapValue = 0;
        }

        private void OnInteract(PlayerControllerB player)
        {
            if (Opened)
                return;

            if (!PlayerCanReachCache(player))
            {
                HUDManager.Instance?.DisplayTip("MILITARY CACHE", "Move inside the opened container.", isWarning: true);
                return;
            }

            if (IsServerRuntime())
            {
                OpenFromServer(player);
                return;
            }

            MoonEscalationState.RequestOpenBlacksiteCrate(this);
        }

        private bool PlayerCanReachCache(PlayerControllerB player)
        {
            if (player == null)
                return false;

            Vector3 origin = GetPlayerReachOrigin(player);
            float coarseReach = DefaultCacheReachRange + 3f;
            if ((transform.position - origin).sqrMagnitude > coarseReach * coarseReach)
                return false;

            BreachContainerController container = _retrievalCargo.Value ? null : FindAssociatedBreachContainer();
            if (container != null && !container.IsOpen)
                return false;

            Vector3 bodyPoint = player.transform.position + Vector3.up * 0.65f;
            if (container != null
                && !container.ContainsWorldPointInsideContainer(origin)
                && !container.ContainsWorldPointInsideContainer(bodyPoint))
            {
                return false;
            }

            Vector3 target = _trigger != null
                ? _trigger.transform.position
                : transform.position + Vector3.up * 0.42f;
            float reach = container != null ? ContainedCacheReachRange : DefaultCacheReachRange;
            if ((target - origin).sqrMagnitude > reach * reach)
                return false;

            int reachMask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : ~0;
            if (Physics.Linecast(origin, target, out RaycastHit hit, reachMask, QueryTriggerInteraction.Ignore)
                && !IsAllowedReachHit(hit.collider, player))
            {
                return false;
            }

            return true;
        }

        private BreachContainerController FindAssociatedBreachContainer()
        {
            List<BreachContainerController> containers = BreachContainerController.GetActiveContainersSnapshot();
            BreachContainerController best = null;
            float bestSqr = BreachContainerAssociationRange * BreachContainerAssociationRange;
            Vector3 position = transform.position;

            for (int i = 0; i < containers.Count; i++)
            {
                BreachContainerController container = containers[i];
                if (container == null)
                    continue;

                Vector3 anchorPosition = container.LootAnchor != null
                    ? container.LootAnchor.position
                    : container.transform.position;
                float sqr = (anchorPosition - position).sqrMagnitude;
                if (sqr >= bestSqr)
                    continue;

                bestSqr = sqr;
                best = container;
            }

            return best;
        }

        private static Vector3 GetPlayerReachOrigin(PlayerControllerB player)
        {
            if (player?.gameplayCamera != null)
                return player.gameplayCamera.transform.position;
            return player != null ? player.transform.position + Vector3.up * 0.65f : Vector3.zero;
        }

        private bool IsAllowedReachHit(Collider collider, PlayerControllerB player)
        {
            if (collider == null)
                return true;

            Transform hitTransform = collider.transform;
            if (hitTransform == null)
                return true;

            if (hitTransform == transform || hitTransform.IsChildOf(transform))
                return true;

            ulong containedId = _containedItemNetworkId.Value;
            if (containedId != 0UL)
            {
                GameObject contained = ResolveContainedItemGameObject(containedId);
                if (contained != null && (hitTransform == contained.transform || hitTransform.IsChildOf(contained.transform)))
                    return true;
            }

            if (player != null && (hitTransform == player.transform || hitTransform.IsChildOf(player.transform)))
                return true;

            return false;
        }

        private static bool ContainedItemMatches(GameObject itemObject, Item item)
        {
            if (itemObject == null || item == null)
                return false;

            GrabbableObject grabbable = itemObject.GetComponent<GrabbableObject>();
            Item existing = grabbable?.itemProperties;
            if (existing == null)
                return false;

            if (ReferenceEquals(existing, item))
                return true;

            return string.Equals(existing.itemName, item.itemName, StringComparison.OrdinalIgnoreCase);
        }

        private void RemoveContainedItemServer(GameObject itemObject)
        {
            _containedItemNetworkId.Value = 0UL;
            _containedItemUnlocked.Value = false;

            if (itemObject == null)
                return;

            NetworkObject networkObject = itemObject.GetComponent<NetworkObject>();
            if (networkObject != null && networkObject.IsSpawned)
            {
                networkObject.Despawn(destroy: true);
                return;
            }

            Destroy(itemObject);
        }

        private void RevealContainedItemServer()
        {
            if (!IsServerRuntime())
                return;

            if (IsSpawned && !_containedItemUnlocked.Value)
                _containedItemUnlocked.Value = true;

            ApplyContainedItemLockLocal();
            if (!_loggedContainedItemReveal)
            {
                _loggedContainedItemReveal = true;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Military cache revealed contained item '{GetContainedItemDebugName()}' " +
                    $"(netId={_containedItemNetworkId.Value}).");
            }
        }

        private void PrepareContainedItem(GameObject itemObject, Item item, bool unlocked)
        {
            if (itemObject == null)
                return;

            GrabbableObject grabbable = itemObject.GetComponent<GrabbableObject>();
            if (grabbable != null)
            {
                grabbable.itemProperties = item;
                grabbable.isInFactory = true;
                grabbable.fallTime = 0f;
                grabbable.hasHitGround = true;
                grabbable.reachedFloorTarget = true;
                grabbable.startFallingPosition = itemObject.transform.localPosition;
                grabbable.targetFloorPosition = itemObject.transform.localPosition;

                if (IsBlacksiteHardDriveItem(itemObject, item))
                {
                    grabbable.SetScrapValue(BreachHardDriveItemRegistry.ScrapValue);
                    AddContainedScrapValueToLevel(BreachHardDriveItemRegistry.ScrapValue);
                }
                else if (item.isScrap)
                {
                    int min = Mathf.Max(50, item.minValue);
                    int max = Mathf.Max(min + 15, item.maxValue);
                    int value = Mathf.RoundToInt(UnityEngine.Random.Range(min, max) * Mathf.Max(1f, RoundManager.Instance != null ? RoundManager.Instance.scrapValueMultiplier : 1f));
                    grabbable.SetScrapValue(value);
                    AddContainedScrapValueToLevel(grabbable.scrapValue);
                }
            }

            SetContainedItemUsability(itemObject, unlocked);
        }

        private void ApplyContainedItemLockLocal()
        {
            if (!ShouldRefreshContainedItemLock())
                return;

            ulong id = _containedItemNetworkId.Value;
            GameObject itemObject = ResolveContainedItemGameObject(id);
            if (itemObject == null)
                return;

            bool unlocked = Opened || _containedItemUnlocked.Value;
            bool keepDisplayedInCache = unlocked && ShouldKeepContainedItemDisplayed(itemObject);
            if (!unlocked || keepDisplayedInCache)
            {
                Transform anchor = GetItemAnchor();
                itemObject.transform.position = anchor.position;
                itemObject.transform.rotation = anchor.rotation;
            }

            SetContainedItemUsability(itemObject, unlocked, keepDisplayedInCache);
            _containedItemLockRefreshDone = true;
            _containedItemLockRefreshDoneNetworkId = id;
        }

        private bool ShouldRefreshContainedItemLock()
        {
            ulong id = _containedItemNetworkId.Value;
            return id != 0UL
                   && (!_containedItemLockRefreshDone || _containedItemLockRefreshDoneNetworkId != id);
        }

        private GameObject ResolveContainedItemGameObject(ulong networkObjectId)
        {
            if (networkObjectId == 0UL)
                return null;

            if (_containedItemLookupNetworkId != networkObjectId)
            {
                _containedItemLookupNetworkId = networkObjectId;
                _containedItemLookupObject = null;
                _containedItemLookupSceneScanAttempted = false;
                _containedItemLookupMissing = false;
            }

            if (_containedItemLookupObject != null)
            {
                NetworkObject cachedNetworkObject = _containedItemLookupObject.GetComponent<NetworkObject>();
                if (cachedNetworkObject != null && cachedNetworkObject.NetworkObjectId == networkObjectId)
                    return _containedItemLookupObject;

                _containedItemLookupObject = null;
            }

            GameObject spawnedObject = FindNetworkObjectGameObject(networkObjectId, allowSceneScan: false);
            if (spawnedObject != null)
            {
                _containedItemLookupObject = spawnedObject;
                _containedItemLookupMissing = false;
                return spawnedObject;
            }

            if (_containedItemLookupMissing || _containedItemLookupSceneScanAttempted)
            {
                _containedItemLookupMissing = true;
                return null;
            }

            _containedItemLookupSceneScanAttempted = true;
            GameObject sceneObject = FindNetworkObjectGameObject(networkObjectId, allowSceneScan: true);
            if (sceneObject != null)
            {
                _containedItemLookupObject = sceneObject;
                _containedItemLookupMissing = false;
                return sceneObject;
            }

            _containedItemLookupMissing = true;
            return null;
        }

        private static bool ShouldKeepContainedItemDisplayed(GameObject itemObject)
        {
            GrabbableObject grabbable = itemObject != null ? itemObject.GetComponent<GrabbableObject>() : null;
            return grabbable != null
                   && !grabbable.hasBeenHeld
                   && !grabbable.isHeld
                   && !grabbable.isPocketed
                   && !grabbable.heldByPlayerOnServer
                   && grabbable.playerHeldBy == null;
        }

        private static bool HasContainedItemBeenTaken(GrabbableObject grabbable)
        {
            return grabbable != null
                   && (grabbable.hasBeenHeld
                       || grabbable.isHeldByEnemy
                   || grabbable.isHeld
                       || grabbable.isPocketed
                       || grabbable.heldByPlayerOnServer
                       || grabbable.playerHeldBy != null);
        }

        private static bool IsBlacksiteHardDriveItem(GameObject itemObject, Item item)
        {
            if (itemObject != null && itemObject.GetComponent<BreachHardDriveItem>() != null)
                return true;
            if (ReferenceEquals(item, BreachHardDriveItemRegistry.ItemProperties))
                return true;

            return string.Equals(item?.itemName, "Shadow Raid Hard Drive", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(item?.itemName, "Blacksite Hard Drive", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddContainedScrapValueToLevel(int value)
        {
            if (RoundManager.Instance != null)
                RoundManager.Instance.totalScrapValueInLevel += Mathf.Max(0, value);
        }

        private bool ShouldRefreshCrateVisual()
        {
            if (!_crateVisualOk)
                return true;
            if (_crateVisualOpenedState != Opened || _crateVisualSuppressedState != SuppressVisualRenderers)
                return true;
            if (ContainsMissingRenderer(_crateVisualRenderers))
            {
                _crateVisualOk = false;
                _crateVisualRenderers = null;
                return true;
            }

            return false;
        }

        private Renderer[] GetCrateVisualRenderers()
        {
            if (_crateVisualRenderers == null || _crateVisualRenderers.Length == 0 || ContainsMissingRenderer(_crateVisualRenderers))
                _crateVisualRenderers = GetComponentsInChildren<Renderer>(true);
            return _crateVisualRenderers;
        }

        private static bool ContainsMissingRenderer(Renderer[] renderers)
        {
            if (renderers == null)
                return true;

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null)
                    return true;
            }

            return false;
        }

        private bool SetContainedItemUsability(GameObject itemObject, bool unlocked, bool keepDisplayedInCache = false)
        {
            if (itemObject == null)
                return false;

            SetContainedItemVisible(itemObject, unlocked);

            GrabbableObject grabbable = itemObject.GetComponent<GrabbableObject>();
            bool held = grabbable != null
                        && (grabbable.isHeld
                            || grabbable.isPocketed
                            || grabbable.heldByPlayerOnServer
                            || grabbable.playerHeldBy != null);
            bool taken = unlocked && HasContainedItemBeenTaken(grabbable);
            if (taken && grabbable != null && grabbable.isHeldByEnemy) return true;
            if (grabbable != null)
            {
                grabbable.grabbable = unlocked;
                grabbable.grabbableToEnemies = unlocked;
                grabbable.isInFactory = true;
                if (!taken)
                {
                    grabbable.hasHitGround = true;
                    grabbable.reachedFloorTarget = true;
                    grabbable.fallTime = 0f;
                    grabbable.targetFloorPosition = itemObject.transform.localPosition;
                    grabbable.startFallingPosition = grabbable.targetFloorPosition;
                    if (!held)
                    {
                        try
                        {
                            grabbable.EnablePhysics(unlocked);
                        }
                        catch
                        {
                            // Some modded GrabbableObject subclasses are picky about early physics toggles.
                        }
                    }
                }
            }

            Rigidbody body = itemObject.GetComponent<Rigidbody>();
            if (body != null && !held && !taken)
            {
                // EnablePhysics(false) may already have made the reward kinematic. Unity warns
                // if velocity is assigned in that state, so only clear it while the body is
                // dynamic. ApplyContainedItemLockLocal stops polling after this state converges.
                if (unlocked)
                {
                    if (body.isKinematic)
                        body.isKinematic = false;
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                else if (!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.isKinematic = true;
                }

                body.useGravity = unlocked && !keepDisplayedInCache;
            }

            Collider[] colliders = itemObject.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider != null)
                    collider.enabled = unlocked;
            }

            return taken;
        }

        private void EnsureCrateVisualVisible()
        {
            Renderer[] renderers = GetCrateVisualRenderers();
            if (SuppressVisualRenderers)
            {
                int suppressedRenderers = SetAuthoredRenderersSuppressed(renderers);
                if (suppressedRenderers > 0)
                {
                    _crateVisualOk = true;
                    _crateVisualOpenedState = Opened;
                    _crateVisualSuppressedState = SuppressVisualRenderers;
                }
                return;
            }

            Bounds authoredBounds = CalculateRendererBoundsLocal(renderers);
            int usableRenderers = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.GetComponentInParent<ScanNodeProperties>() != null)
                    continue;

                SetRendererEnabledIfDifferent(renderer, true);
                SetRendererForceOffIfDifferent(renderer, false);
                SetRendererOcclusionIfDifferent(renderer, false);
                SetRendererShadowCastingIfDifferent(renderer, UnityEngine.Rendering.ShadowCastingMode.On);
                SetRendererReceiveShadowsIfDifferent(renderer, true);
                RepairCrateMaterialIfNeeded(renderer);
                usableRenderers++;

                if (renderer is SkinnedMeshRenderer skinned)
                {
                    if (!skinned.updateWhenOffscreen)
                        skinned.updateWhenOffscreen = true;
                    Bounds bounds = skinned.localBounds;
                    float largestAxis = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                    SetSkinnedLocalBoundsIfDifferent(skinned, new Bounds(bounds.center, Vector3.one * Mathf.Max(2.5f, largestAxis)));
                }
            }

            if (usableRenderers > 0)
            {
                _crateVisualOk = true;
                _crateVisualOpenedState = Opened;
                _crateVisualSuppressedState = SuppressVisualRenderers;
            }

            if (!_loggedVisualState)
            {
                _loggedVisualState = true;
                bool hasCrateChild = FindChildByNameToken(transform, "crate") != null
                                     || GetComponentInChildren<MeshRenderer>(true) != null
                                     || GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Military cache visual check '{name}': rendererCount={renderers.Length}, usable={usableRenderers}, " +
                    $"active={gameObject.activeInHierarchy}, children={transform.childCount}, hasCrateChild={hasCrateChild}, " +
                    $"position={FormatVector(transform.position)}, scale={FormatVector(transform.lossyScale)}, localBounds={FormatVector(authoredBounds.size)}, " +
                    $"authoredOnly=true.");
                LogCrateRendererDetails(renderers);
            }

            MaybeLogDebugHierarchy(authoredBounds, usableRenderers);

            if (usableRenderers == 0)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Blacksite] Military cache prefab has no usable authored renderers at runtime. Rebuild/redeploy lgucontracts.bundle or repair the BlacksiteCargoCrate prefab.");
            }
        }

        private void LogCrateRendererDetails(Renderer[] renderers)
        {
            if (renderers == null)
                return;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.GetComponentInParent<ScanNodeProperties>() != null)
                    continue;

                Mesh mesh = null;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null)
                    mesh = filter.sharedMesh;
                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null)
                    mesh = skinned.sharedMesh;

                string materialText = string.Empty;
                Material[] materials = renderer.sharedMaterials;
                if (materials != null)
                {
                    string[] parts = new string[materials.Length];
                    for (int slot = 0; slot < materials.Length; slot++)
                    {
                        Material material = materials[slot];
                        if (material == null)
                        {
                            parts[slot] = "null";
                            continue;
                        }

                        Texture texture = TryGetMaterialTexture(material);
                        string shaderName = material.shader != null ? material.shader.name : "null";
                        bool supported = material.shader != null && material.shader.isSupported;
                        parts[slot] = $"{material.name}(shader='{shaderName}', supported={supported}, tex={(texture != null ? texture.name : "none")})";
                    }
                    materialText = string.Join(" | ", parts);
                }

                string boneText = string.Empty;
                if (skinned != null)
                {
                    int boneCount = skinned.bones != null ? skinned.bones.Length : 0;
                    int nullBones = 0;
                    if (skinned.bones != null)
                    {
                        for (int b = 0; b < skinned.bones.Length; b++)
                        {
                            if (skinned.bones[b] == null)
                                nullBones++;
                        }
                    }
                    boneText = $", rootBone={(skinned.rootBone != null ? skinned.rootBone.name : "null")}, bones={boneCount}, nullBones={nullBones}, updateWhenOffscreen={skinned.updateWhenOffscreen}";
                }

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Cache renderer detail '{GetTransformPath(renderer.transform)}': type={renderer.GetType().Name}, " +
                    $"enabled={renderer.enabled}, forceOff={renderer.forceRenderingOff}, mesh={(mesh != null ? mesh.name : "null")}, " +
                    $"verts={(mesh != null ? mesh.vertexCount : 0)}{boneText}, materials=[{materialText}].");
            }
        }

        private void MaybeLogDebugHierarchy(Bounds authoredBounds, int usableRenderers)
        {
            if (_loggedDebugHierarchy || !BlacksiteDebugLogsEnabled())
                return;

            _loggedDebugHierarchy = true;
            Transform[] transforms = GetComponentsInChildren<Transform>(true);
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            NetworkObject networkObject = _networkObject != null ? _networkObject : NetworkObject;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite][Debug] Military cache runtime hierarchy '{name}': transforms={transforms.Length}, renderers={renderers.Length}, usableRenderers={usableRenderers}, " +
                $"networkId={(networkObject != null ? networkObject.NetworkObjectId : 0UL)}, opened={Opened}, suppressVisuals={SuppressVisualRenderers}, " +
                $"containedItem='{GetContainedItemDebugName()}', scene='{gameObject.scene.name}', rootFullPath='{GetTransformPath(transform)}', " +
                $"rootPos={FormatDebugVector(transform.position)}, rootRot={FormatDebugVector(transform.eulerAngles)}, " +
                $"rootScale={FormatDebugVector(transform.lossyScale)}, localAuthoredBounds={FormatDebugBounds(authoredBounds)}.");

            for (int i = 0; i < transforms.Length; i++)
            {
                Transform child = transforms[i];
                if (child == null)
                    continue;

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite][Debug] Cache transform[{i}] fullPath='{GetTransformPath(child)}', activeSelf={child.gameObject.activeSelf}, " +
                    $"localPos={FormatDebugVector(child.localPosition)}, localEuler={FormatDebugVector(child.localEulerAngles)}, localScale={FormatDebugVector(child.localScale)}, " +
                    $"worldPos={FormatDebugVector(child.position)}, worldEuler={FormatDebugVector(child.eulerAngles)}.");
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                if (mesh == null && skinned != null)
                    mesh = skinned.sharedMesh;

                string localBoundsText = mesh != null
                    ? FormatDebugBounds(skinned != null ? skinned.localBounds : mesh.bounds)
                    : "none";
                string meshText = mesh != null
                    ? $"mesh='{mesh.name}', vertices={mesh.vertexCount}, submeshes={mesh.subMeshCount}, meshBounds={FormatDebugBounds(mesh.bounds)}"
                    : "mesh=none";
                if (skinned != null)
                    meshText += $", updateWhenOffscreen={skinned.updateWhenOffscreen}";

                bool isParticle = renderer is ParticleSystemRenderer;
                bool isScanNode = renderer.GetComponentInParent<ScanNodeProperties>() != null;
                bool isScreen = RendererLooksScreenLike(renderer);
                bool isCandidateBit = false;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite][Debug] Cache renderer[{i}] fullPath='{GetTransformPath(renderer.transform)}', type={renderer.GetType().Name}, " +
                    $"enabled={renderer.enabled}, forceOff={renderer.forceRenderingOff}, shadow={renderer.shadowCastingMode}, " +
                    $"isScreen={isScreen}, isParticle={isParticle}, isScanNode={isScanNode}, isCandidateBit={isCandidateBit}, " +
                    $"materials=[{FormatMaterialNames(renderer.sharedMaterials)}], " +
                    $"localBounds={localBoundsText}, worldBounds={FormatDebugBounds(renderer.bounds)}, {meshText}.");
            }
        }

        private static int SetAuthoredRenderersSuppressed(Renderer[] renderers)
        {
            if (renderers == null)
                return 0;

            int suppressedRenderers = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.GetComponentInParent<ScanNodeProperties>() != null)
                    continue;

                SetRendererEnabledIfDifferent(renderer, false);
                SetRendererForceOffIfDifferent(renderer, true);
                suppressedRenderers++;
            }

            return suppressedRenderers;
        }

        private static void SetRendererEnabledIfDifferent(Renderer renderer, bool enabled)
        {
            if (renderer != null && renderer.enabled != enabled)
                renderer.enabled = enabled;
        }

        private static void SetRendererForceOffIfDifferent(Renderer renderer, bool forceRenderingOff)
        {
            if (renderer != null && renderer.forceRenderingOff != forceRenderingOff)
                renderer.forceRenderingOff = forceRenderingOff;
        }

        private static void SetRendererOcclusionIfDifferent(Renderer renderer, bool allowOcclusionWhenDynamic)
        {
            if (renderer != null && renderer.allowOcclusionWhenDynamic != allowOcclusionWhenDynamic)
                renderer.allowOcclusionWhenDynamic = allowOcclusionWhenDynamic;
        }

        private static void SetRendererShadowCastingIfDifferent(Renderer renderer, UnityEngine.Rendering.ShadowCastingMode shadowCastingMode)
        {
            if (renderer != null && renderer.shadowCastingMode != shadowCastingMode)
                renderer.shadowCastingMode = shadowCastingMode;
        }

        private static void SetRendererReceiveShadowsIfDifferent(Renderer renderer, bool receiveShadows)
        {
            if (renderer != null && renderer.receiveShadows != receiveShadows)
                renderer.receiveShadows = receiveShadows;
        }

        private static void SetSkinnedLocalBoundsIfDifferent(SkinnedMeshRenderer skinned, Bounds bounds)
        {
            if (skinned != null && !BoundsApproximately(skinned.localBounds, bounds))
                skinned.localBounds = bounds;
        }

        private static bool BoundsApproximately(Bounds left, Bounds right)
        {
            return VectorApproximately(left.center, right.center)
                   && VectorApproximately(left.size, right.size);
        }

        private static bool VectorApproximately(Vector3 left, Vector3 right)
        {
            return Mathf.Approximately(left.x, right.x)
                   && Mathf.Approximately(left.y, right.y)
                   && Mathf.Approximately(left.z, right.z);
        }

        private static void SetContainedItemVisible(GameObject itemObject, bool visible)
        {
            Renderer[] renderers = itemObject.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                renderer.enabled = visible;
                renderer.forceRenderingOff = false;
                renderer.allowOcclusionWhenDynamic = false;
                if (visible && renderer is SkinnedMeshRenderer skinned)
                {
                    skinned.updateWhenOffscreen = true;
                    Bounds bounds = skinned.localBounds;
                    float largestAxis = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                    skinned.localBounds = new Bounds(bounds.center, Vector3.one * Mathf.Max(1.2f, largestAxis));
                }
            }
        }

        private Transform GetItemAnchor()
        {
            if (_itemAnchor == null)
                _itemAnchor = FindOrCreateItemAnchor();
            return _itemAnchor != null ? _itemAnchor : transform;
        }

        private string GetContainedItemDebugName()
        {
            GameObject itemObject = ResolveContainedItemGameObject(_containedItemNetworkId.Value);
            if (itemObject == null)
                return "unknown";

            GrabbableObject grabbable = itemObject.GetComponent<GrabbableObject>();
            if (grabbable?.itemProperties != null && !string.IsNullOrWhiteSpace(grabbable.itemProperties.itemName))
                return grabbable.itemProperties.itemName;

            return itemObject.name;
        }

        private Transform FindOrCreateItemAnchor()
        {
            Transform existing = transform.Find("ItemAnchor");
            if (existing != null)
            {
                PositionItemAnchor(existing);
                return existing;
            }

            var anchor = new GameObject("ItemAnchor").transform;
            anchor.SetParent(transform, false);
            PositionItemAnchor(anchor);
            return anchor;
        }

        private void PositionItemAnchor(Transform anchor)
        {
            if (anchor == null)
                return;

            Bounds bounds = CalculateRendererBoundsLocal();
            anchor.localPosition = bounds.size.sqrMagnitude > 0.001f
                ? new Vector3(bounds.center.x, bounds.min.y + 0.11f, bounds.center.z)
                : new Vector3(0f, 0.11f, 0f);
            anchor.localRotation = Quaternion.identity;
        }

        private void StartOpenVisualLocal()
        {
            bool playedAuthoredClip = PlayAuthoredOpenAnimation();
            if (_lidPivot != null && !playedAuthoredClip)
                StartFallbackOpenAnimation();
        }

        private void StartFallbackOpenAnimation()
        {
            if (_lidPivot != null)
            {
                CaptureFallbackLidPose();
                _fallbackOpening = true;
                _fallbackOpenProgress = 0f;
                if (!_loggedOpenAnimation)
                {
                    _loggedOpenAnimation = true;
                    ContractsBootstrap.Log?.LogInfo("[MoonContracts.Blacksite] Playing fallback military cache lid open animation.");
                }
            }
        }

        private bool PlayAuthoredOpenAnimation()
        {
            if (_legacyAnimation != null)
            {
                _legacyAnimation.enabled = true;
                _legacyAnimation.cullingType = AnimationCullingType.AlwaysAnimate;
                AnimationState bestState = null;
                int bestScore = int.MinValue;
                foreach (AnimationState state in _legacyAnimation)
                {
                    if (state == null)
                        continue;

                    state.wrapMode = WrapMode.Once;
                    int score = ScoreOpenAnimationState(state);
                    if (bestState == null || score > bestScore || (score == bestScore && state.length > bestState.length))
                    {
                        bestState = state;
                        bestScore = score;
                    }
                }

                if (bestState != null)
                {
                    bestState.enabled = true;
                    bestState.time = 0f;
                    bestState.speed = 1f;
                    _legacyAnimation.clip = bestState.clip;
                    _legacyAnimation.Play(bestState.name, PlayMode.StopAll);
                    if (!_loggedOpenAnimation)
                    {
                        _loggedOpenAnimation = true;
                        ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Playing authored military cache open animation '{bestState.name}'.");
                    }
                    return true;
                }
            }

            if (_animator != null && _animator.runtimeAnimatorController != null)
            {
                if (AnimatorHasTrigger(_animator, "Open"))
                {
                    _animator.enabled = true;
                    _animator.SetTrigger("Open");
                    if (!_loggedOpenAnimation)
                    {
                        _loggedOpenAnimation = true;
                        ContractsBootstrap.Log?.LogInfo("[MoonContracts.Blacksite] Playing animator military cache open trigger 'Open'.");
                    }
                    return true;
                }
            }

            return false;
        }

        private static bool AnimatorHasTrigger(Animator animator, string triggerName)
        {
            if (animator == null || string.IsNullOrWhiteSpace(triggerName))
                return false;

            try
            {
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    AnimatorControllerParameter parameter = parameters[i];
                    if (parameter != null
                        && parameter.type == AnimatorControllerParameterType.Trigger
                        && string.Equals(parameter.name, triggerName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static int ScoreOpenAnimationState(AnimationState state)
        {
            if (state == null)
                return int.MinValue;

            string name = ((state.name ?? string.Empty) + " " + (state.clip != null ? state.clip.name : string.Empty)).ToLowerInvariant();
            int score = 0;
            if (name.Contains("open") || name.Contains("door") || name.Contains("lid"))
                score += 12;
            if (name.Contains("armature"))
                score += 8;
            if (name.Contains("crate") || name.Contains("cache"))
                score += 4;
            if (name.Contains("circle"))
                score -= 8;
            return score;
        }

        private void CaptureFallbackLidPose()
        {
            if (_lidCaptured || _lidPivot == null || _lidPivot == transform)
                return;

            _lidClosedRotation = _lidPivot.localRotation;
            _lidOpenRotation = _lidClosedRotation * Quaternion.Euler(-82f, 0f, 0f);
            _lidCaptured = true;
        }

        private void UpdateFallbackOpenAnimation()
        {
            if (!_fallbackOpening || _lidPivot == null)
                return;

            _fallbackOpenProgress = Mathf.MoveTowards(_fallbackOpenProgress, 1f, Time.deltaTime / FallbackOpenDuration);
            _lidPivot.localRotation = Quaternion.Slerp(_lidClosedRotation, _lidOpenRotation, _fallbackOpenProgress);
            if (_fallbackOpenProgress >= 0.999f)
                _fallbackOpening = false;
        }

        private static Transform FindChildByNameToken(Transform root, string token)
        {
            if (root == null || string.IsNullOrWhiteSpace(token))
                return null;

            if (root.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildByNameToken(root.GetChild(i), token);
                if (found != null)
                    return found;
            }

            return null;
        }

        private Bounds CalculateRendererBoundsLocal()
        {
            return CalculateRendererBoundsLocal(null);
        }

        private Bounds CalculateRendererBoundsLocal(Renderer[] renderers)
        {
            if (renderers == null)
                renderers = GetComponentsInChildren<Renderer>(true);

            bool initialized = false;
            Bounds bounds = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;
                if (renderer.GetComponentInParent<ScanNodeProperties>() != null)
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
                    Vector3 local = transform.InverseTransformPoint(corners[corner]);
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

            return initialized ? bounds : new Bounds(Vector3.zero, Vector3.one * 0.4f);
        }

        private static GameObject FindNetworkObjectGameObject(ulong networkObjectId, bool allowSceneScan = true)
        {
            if (networkObjectId == 0UL)
                return null;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager?.SpawnManager?.SpawnedObjects != null
                && manager.SpawnManager.SpawnedObjects.TryGetValue(networkObjectId, out NetworkObject networkObject)
                && networkObject != null)
            {
                return networkObject.gameObject;
            }

            if (!allowSceneScan)
                return null;

            foreach (NetworkObject candidate in FindObjectsOfType<NetworkObject>())
            {
                if (candidate != null && candidate.NetworkObjectId == networkObjectId)
                    return candidate.gameObject;
            }

            return null;
        }

        private static Item ChooseRewardItem()
        {
            var items = StartOfRound.Instance?.allItemsList?.itemsList;
            if (items == null || items.Count == 0)
                return null;

            if (UnityEngine.Random.value < 0.26f)
                return null;

            List<Item> weaponCandidates = items
                .Where(item => IsSpawnable(item) && IsWeaponLike(item))
                .ToList();
            List<Item> valuableCandidates = items
                .Where(item => IsSpawnable(item) && item.isScrap && IsValuableLike(item))
                .ToList();
            List<Item> fallbackScrap = items
                .Where(item => IsSpawnable(item) && item.isScrap)
                .OrderByDescending(item => item.maxValue)
                .Take(24)
                .ToList();

            bool preferWeapon = UnityEngine.Random.value < 0.55f;
            if (preferWeapon && weaponCandidates.Count > 0)
                return weaponCandidates[UnityEngine.Random.Range(0, weaponCandidates.Count)];
            if (valuableCandidates.Count > 0)
                return valuableCandidates[UnityEngine.Random.Range(0, valuableCandidates.Count)];
            if (weaponCandidates.Count > 0)
                return weaponCandidates[UnityEngine.Random.Range(0, weaponCandidates.Count)];
            if (fallbackScrap.Count > 0)
                return fallbackScrap[UnityEngine.Random.Range(0, fallbackScrap.Count)];

            return items.FirstOrDefault(IsSpawnable);
        }

        private static bool IsSpawnable(Item item)
        {
            return item != null
                && item.spawnPrefab != null
                && item.spawnPrefab.GetComponent<GrabbableObject>() != null;
        }

        private static bool IsWeaponLike(Item item)
        {
            string name = item?.itemName ?? string.Empty;
            return Contains(name, "gun")
                || Contains(name, "rifle")
                || Contains(name, "shotgun")
                || Contains(name, "revolver")
                || Contains(name, "pistol")
                || Contains(name, "smg")
                || Contains(name, "ammo")
                || Contains(name, "grenade")
                || Contains(name, "weapon")
                || Contains(name, "g15")
                || Contains(name, "boom");
        }

        private static bool IsValuableLike(Item item)
        {
            string name = item?.itemName ?? string.Empty;
            return Contains(name, "gold")
                || Contains(name, "bar")
                || Contains(name, "cash")
                || Contains(name, "diamond")
                || Contains(name, "ingot")
                || Contains(name, "coin")
                || Contains(name, "gem")
                || item.maxValue >= 95;
        }

        private static bool Contains(string value, string fragment)
        {
            return value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void PlayOpenSound()
        {
            if (_audioSource == null)
                return;

            // #943 S11 U5: a crate lid opening is an opening sound. This played vanilla's
            // shutDoorMetal - a metal door being SHUT - dropped to pitch 0.72, which is why the
            // cache read as a wrong, slowed-down thud. StartOfRound.creakOpenDoorMetal is the
            // game's own metal-latch-opening cue for exactly this, and it needs no pitch shift.
            AudioClip clip = StartOfRound.Instance != null && StartOfRound.Instance.creakOpenDoorMetal != null && StartOfRound.Instance.creakOpenDoorMetal.Length > 0
                ? StartOfRound.Instance.creakOpenDoorMetal.FirstOrDefault(sound => sound != null)
                : null;
            if (clip != null)
            {
                _audioSource.pitch = 1f;
                _audioSource.PlayOneShot(clip, 0.42f);
            }
        }

        private static bool IsServerRuntime()
        {
            return RoundManager.Instance != null && RoundManager.Instance.IsServer;
        }

        private static void SetMaterialColorIfPresent(Material material, string property, Color color)
        {
            if (material != null && material.HasProperty(property))
                material.SetColor(property, color);
        }

        private static void SetMaterialFloatIfPresent(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private static void RepairCrateMaterialIfNeeded(Renderer renderer)
        {
            if (renderer == null)
                return;

            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
                materials = new Material[1];

            bool changed = false;
            for (int i = 0; i < materials.Length; i++)
            {
                if (!LooksLikeBrokenCrateMaterial(materials[i]))
                    continue;

                materials[i] = CreateRuntimeCrateMaterial(materials[i]);
                changed = true;
            }

            if (changed)
                renderer.sharedMaterials = materials;
        }

        private static bool LooksLikeBrokenCrateMaterial(Material material)
        {
            if (material == null || material.shader == null)
                return true;
            if (material.shader.name.IndexOf("InternalError", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (TryGetMaterialTexture(material) != null)
                return false;

            string name = material.name ?? string.Empty;
            Color color = GetMaterialColor(material);
            bool crateLike = name.IndexOf("crate", StringComparison.OrdinalIgnoreCase) >= 0
                             || name.IndexOf("cargo", StringComparison.OrdinalIgnoreCase) >= 0
                             || name.IndexOf("military", StringComparison.OrdinalIgnoreCase) >= 0;
            bool placeholderWhite = color.r > 0.86f && color.g > 0.86f && color.b > 0.86f;
            return crateLike && placeholderWhite;
        }

        private static Material CreateRuntimeCrateMaterial(Material source)
        {
            Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
            Material material = new Material(shader)
            {
                name = "LGU_RuntimeMilitaryCacheMaterial",
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = 2000,
            };
            material.SetOverrideTag("RenderType", "Opaque");

            Texture texture = TryGetMaterialTexture(source);
            if (texture != null)
            {
                SetMaterialTextureIfPresent(material, "_BaseColorMap", texture);
                SetMaterialTextureIfPresent(material, "_MainTex", texture);
            }

            Color color = new Color(0.22f, 0.31f, 0.23f, 1f);
            material.color = color;
            SetMaterialColorIfPresent(material, "_BaseColor", color);
            SetMaterialColorIfPresent(material, "_Color", color);
            SetMaterialFloatIfPresent(material, "_SurfaceType", 0f);
            SetMaterialFloatIfPresent(material, "_BlendMode", 0f);
            SetMaterialFloatIfPresent(material, "_ZWrite", 1f);
            SetMaterialFloatIfPresent(material, "_Metallic", 0.04f);
            SetMaterialFloatIfPresent(material, "_Smoothness", 0.32f);
            SetMaterialFloatIfPresent(material, "_Glossiness", 0.32f);
            return material;
        }

        private static Texture TryGetMaterialTexture(Material material)
        {
            if (material == null)
                return null;

            string[] textureProperties =
            {
                "_BaseColorMap",
                "_MainTex",
                "_BaseMap",
                "_DiffuseMap",
            };
            for (int i = 0; i < textureProperties.Length; i++)
            {
                string property = textureProperties[i];
                if (!material.HasProperty(property))
                    continue;

                Texture texture = material.GetTexture(property);
                if (texture != null)
                    return texture;
            }

            return null;
        }

        private static Color GetMaterialColor(Material material)
        {
            if (material == null)
                return Color.white;
            if (material.HasProperty("_BaseColor"))
                return material.GetColor("_BaseColor");
            if (material.HasProperty("_Color"))
                return material.GetColor("_Color");
            return material.color;
        }

        private static void SetMaterialTextureIfPresent(Material material, string property, Texture texture)
        {
            if (material != null && texture != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        private static string FormatVector(Vector3 vector)
        {
            return $"{vector.x:0.0},{vector.y:0.0},{vector.z:0.0}";
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return "null";

            string path = transform.name;
            Transform current = transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private static string FormatDebugVector(Vector3 vector)
        {
            return $"{vector.x:0.000},{vector.y:0.000},{vector.z:0.000}";
        }

        private static string FormatDebugBounds(Bounds bounds)
        {
            return $"center={FormatDebugVector(bounds.center)}, size={FormatDebugVector(bounds.size)}, extents={FormatDebugVector(bounds.extents)}";
        }

        private static string FormatMaterialNames(Material[] materials)
        {
            if (materials == null || materials.Length == 0)
                return string.Empty;

            string[] names = new string[materials.Length];
            for (int i = 0; i < materials.Length; i++)
                names[i] = materials[i] != null ? materials[i].name : "null";
            return string.Join(", ", names);
        }

        private static bool RendererLooksScreenLike(Renderer renderer)
        {
            if (renderer == null)
                return false;

            string path = GetTransformPath(renderer.transform);
            if (path.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("monitor", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            Material[] materials = renderer.sharedMaterials;
            if (materials == null)
                return false;

            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null)
                    continue;
                if (material.name.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0
                    || material.name.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0
                    || material.name.IndexOf("monitor", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                Texture texture = TryGetMaterialTexture(material);
                if (texture != null
                    && (texture.name.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0
                        || texture.name.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0
                        || texture.name.IndexOf("monitor", StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            }

            return false;
        }

        private static bool BlacksiteDebugLogsEnabled()
        {
            return ContractsBootstrap.ShadowRaidDebugEnabled != null && ContractsBootstrap.ShadowRaidDebugEnabled.Value;
        }

        private static void SetTagSafe(GameObject obj, string tag)
        {
            try
            {
                obj.tag = tag;
            }
            catch
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Tag '{tag}' was not available for cache interaction.");
            }
        }
    }
}
