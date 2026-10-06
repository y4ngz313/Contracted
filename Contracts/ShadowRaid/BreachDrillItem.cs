using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using GameNetcodeStuff;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.UI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Contracts.ShadowRaid
{
    // NOTE: This is intentionally a plain MonoBehaviour, NOT a NetworkBehaviour. Adding a second custom
    // NetworkBehaviour to the runtime-built drill prefab corrupted Netcode's NetworkBehaviour<->NetworkObject
    // binding (both PhysicsProp and this component resolved to the ship's Props container instead of the
    // drill), which made vanilla PlayerControllerB.GrabObjectServerRpc unable to validate the grab. With only
    // the vanilla PhysicsProp left as the NetworkBehaviour, the drill grabs like any normal item. Drilling
    // state is therefore server-authoritative local state here; cross-client sync rides on the Shadow Raid
    // container (a properly-spawned NetworkBehaviour) — see BreachContainerController.
    // PlayerControllerB.LateUpdate moves the local arm rig after animation evaluation.
    // Finish after both that write and GrabbableObject's socket placement.
    [DefaultExecutionOrder(32000)]
    internal sealed class BreachDrillItem : MonoBehaviour
    {
        private const int ItemFloorRaycastMask = 268437761;

        // --- Carried representation (#455) -------------------------------------------------------
        // Both views follow their own animated wrists instead of a single rolled hand socket.
        // This is the largest axis the carried drill is scaled to while a player holds it, so the
        // body sits between the two-handed carry animation's hands like a crate rather than
        // swallowing the camera. Sized against the Blacksite hard drive (0.62 m), which reads as
        // held with the same zero offsets.
        private const float CarriedVisualMaxDimensionMeters = 0.55f;
        // ------------------------------------------------------------------------------------------
        private const string ProvidedDrillLoopFileName = "dbsound-mechanical-noise-loop-2-250783.mp3";
        private const string ProvidedDoorOpenFileName = "universfield-opening-metal-door-199581.mp3";
        private const string AuthoredRotatingDrillBitName = "RotatingDrillBitVisual";
        private const float BundledSparkEffectScale = 1f;
        private const float SparkContactOutwardOffset = 0.015f;
        // Must not be a multiple of 180° per frame at common framerates: 10800°/s was exactly
        // 180°/frame at 60fps, which makes a two-fluted (180°-symmetric) bit look frozen.
        private const float DrillBitSpinDegreesPerSecond = 1170f;
        private const float RunningVibrationHorizontalAmplitude = 0.0045f;
        private const float RunningVibrationVerticalAmplitude = 0.0025f;
        private const float RunningVibrationForwardAmplitude = 0.0035f;
        private static readonly System.Collections.Generic.List<BreachDrillItem> ActiveDrills = new System.Collections.Generic.List<BreachDrillItem>();
        private static AudioClip _generatedDrillLoopClip;
        private static AudioClip _providedDrillLoopClip;
        private static AudioClip _providedDoorOpenClip;
        private static bool _providedDrillLoopLoadStarted;
        private static bool _providedDrillLoopLoadFailed;
        private static bool _providedDoorOpenLoadStarted;
        private static bool _providedDoorOpenLoadFailed;
        private static Texture2D _runtimeSparkTexture;
        private static Material _drillFallbackMaterial;
        // Lightweight server-authoritative state holders (replaces the former NetworkVariables; the same
        // ".Value" access pattern is kept so the rest of the class is unchanged). Host/server writes these;
        // every machine reads them. Multiplayer replication is handled via the Shadow Raid container, not here.
        private readonly LocalState<bool> _attached = new LocalState<bool>(false);
        private readonly LocalState<bool> _completed = new LocalState<bool>(false);
        private readonly LocalState<float> _finishServerTime = new LocalState<float>(0f);
        private readonly LocalState<ulong> _containerNetworkId = new LocalState<ulong>(0UL);

        private sealed class LocalState<T>
        {
            internal T Value;
            internal LocalState(T value) { Value = value; }
        }

        private static bool IsServerInstance => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        private PhysicsProp _prop;
        private ScanNodeProperties _scanNode;
        private AudioSource _audio;
        private Light _tipLight;
        private ParticleSystem _sparks;
        private GameObject _bundledSparkEffect;
        private ParticleSystem[] _bundledSparkSystems;
        private BreachDrillScreenDisplay _screen;
        private Renderer _screenRenderer;
        private Transform _attachPoint;
        private Transform _drillTip;
        private Transform _sparkOrigin;
        private Transform _rotatingBit;
        private Transform _bodyVisual;
        private Transform _visualRoot;
        private Vector3 _visualRootBaseLocalScale = Vector3.one;
        private Vector3 _visualRootBaseLocalPosition;
        private bool _visualRootBaseLocalScaleCaptured;
        private bool _carriedRepresentationApplied;
        private SkinnedMeshRenderer _carryBodyModel;
        private Transform _carryLeftHand;
        private Transform _carryRightHand;
        private Vector3 _bodyVisualBaseLocalPosition;
        private Vector3 _rotatingBitBaseLocalPosition;
        private Vector3 _rotatingBitSpinAxis = Vector3.forward;
        private BreachContainerController _attachedContainerDirect;
        private bool _bodyVisualBaseLocalPositionCaptured;
        private bool _rotatingBitBaseLocalPositionCaptured;
        private bool _rotatingBitSpinAxisResolved;
        private bool _screenDisplayAttempted;
        private bool _warnedMissingRotatingBit;
        private bool _loggedRotatingBit;
        private float _drillDurationSeconds;
        private string _displayName = "SHADOW RAID DRILL";
        private bool _initialized;
        private bool _wasHeldLastFrame;
        // #455 diagnostic: a pickup schedules one held-state log half a second later, once vanilla
        // has finished parenting the item. Bounded per item so a drill changing hands all round
        // does not flood the log.
        private float _heldStateLogAt;
        private int _heldStateLogsEmitted;
        private const int MaxHeldStateLogs = 3;
        private bool _wasGroundedLastFrame;
        private float _nextLooseFloorCheckAt;
        private float _lastReleasedAt;
        private ulong _lastHolderClientId = ulong.MaxValue;
        private ulong _attachedByClientId = ulong.MaxValue;
        private bool _screenRendererResolveAttempted;
        private Vector3 _lastSparkContact;
        private bool _hasLastSparkContact;
        private bool _loggedVisualState;
        private bool _loggedTipSparkAlignment;
        private bool _loggedDebugHierarchy;
        private int _lastLoggedScreenCountdownBucket = int.MinValue;
        // Steady-state caches. The drill's hierarchy never changes after the runtime visual is built,
        // so the per-frame Update path resolves each part once and only re-resolves when a cached
        // reference has actually gone null (see EnsureRuntimeParts / NeedsRuntimePartResolve).
        private bool _runtimePartsResolved;
        private Renderer[] _visualRenderers;
        private float _nextVisualRepairAt;
        private Collider[] _cachedRootColliders;
        private int _lastPickupColliderCount = -1;
        private bool _pickupColliderStateApplied;
        private bool _lastGrabbableStateApplied;
        private bool _hasLastGrabbableState;
        private Transform _bitTipMeshTransform;
        private Vector3 _bitTipMeshLocalPoint;
        private bool _bitTipMeshResolved;
        private float _nextBitTipResolveAt;
        private const int MaxRotatingBitResolveAttempts = 30;
        private int _rotatingBitResolveAttempts;
        private int _lastScanNodeSeconds = int.MinValue;
        private bool _lastScanNodeAttached;
        private bool _lastScanNodeCompleted;
        private bool _scanNodeStaticsApplied;
        private int _lastScreenSeconds = int.MinValue;
        private string _cachedScreenSecondsText;
        private float _loopFadeOutRemaining;
        private float _loopFadeOutStartVolume;
        private float _audioBaseVolume = 0.36f;
        // Client mirror of the server-authoritative drill state, fed by the Shadow Raid container's
        // replicated attachment fields (see BreachContainerController.AttachedDrillNetworkId).
        private float _nextClientStateSyncAt;
        private ulong _appliedCompletionContainerId;
        private int _appliedCompletionSequence;
        private float _detachGraceUntil;

        /// <summary>
        /// Called by <see cref="BreachContainerController"/> when replicated drill state changes, so a
        /// client applies it on the same frame rather than up to one poll interval later. Every active
        /// drill re-evaluates itself, which keeps this independent of the order individual
        /// NetworkVariables in one batch are applied in.
        /// </summary>
        internal static void NotifyReplicatedContainerStateChanged()
        {
            for (int i = 0; i < ActiveDrills.Count; i++)
            {
                BreachDrillItem drill = ActiveDrills[i];
                if (drill == null)
                    continue;

                drill._nextClientStateSyncAt = 0f;
                drill.SyncAttachmentFromContainerOnClient();
            }
        }

        internal static BreachDrillItem FindActive()
        {
            for (int i = 0; i < ActiveDrills.Count; i++)
            {
                BreachDrillItem drill = ActiveDrills[i];
                if (drill != null && drill.gameObject.scene.IsValid())
                    return drill;
            }

            return null;
        }

        internal static int RemoveReturnedDrillsFromShip(string reason)
        {
            if (!CanServerRemoveReturnedDrills())
                return 0;

            BreachDrillItem[] drills = Resources.FindObjectsOfTypeAll<BreachDrillItem>();
            int removed = 0;
            for (int i = 0; i < drills.Length; i++)
            {
                BreachDrillItem drill = drills[i];
                if (drill == null || drill.gameObject == BreachDrillItemRegistry.Prefab)
                    continue;
                if (!drill.gameObject.scene.IsValid())
                    continue;
                if (!IsReturnedToShip(drill))
                    continue;

                RemoveDrillObject(drill, reason);
                removed++;
            }

            if (removed > 0)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Removed {removed} returned Shadow Raid drill(s) from ship via {reason}.");
            return removed;
        }

        internal static int RemoveAllRuntimeDrills(string reason)
        {
            if (!CanServerRemoveReturnedDrills())
                return 0;

            BreachDrillItem[] drills = Resources.FindObjectsOfTypeAll<BreachDrillItem>();
            int removed = 0;
            for (int i = 0; i < drills.Length; i++)
            {
                BreachDrillItem drill = drills[i];
                if (drill == null || drill.gameObject == BreachDrillItemRegistry.Prefab)
                    continue;
                if (!drill.gameObject.scene.IsValid())
                    continue;

                RemoveDrillObject(drill, reason);
                removed++;
            }

            if (removed > 0)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Removed {removed} inactive Shadow Raid drill(s) via {reason}.");
            return removed;
        }

        private static bool CanServerRemoveReturnedDrills()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null)
                return manager.IsServer;

            return NetRole.IsServerOrHost();
        }

        private static bool IsReturnedToShip(BreachDrillItem drill)
        {
            if (drill == null)
                return false;

            PhysicsProp prop = drill._prop != null ? drill._prop : drill.GetComponent<PhysicsProp>();
            if (prop != null)
            {
                if (prop.isInShipRoom || prop.isInElevator)
                    return true;

                PlayerControllerB holder = FindPlayerHolding(prop);
                if (holder != null)
                    return holder.isInHangarShipRoom || holder.isInElevator;
            }

            return IsPositionInShip(drill.transform.position);
        }

        private static bool IsPositionInShip(Vector3 position)
        {
            StartOfRound start = StartOfRound.Instance;
            if (start == null)
                return false;

            if (start.shipInnerRoomBounds != null && start.shipInnerRoomBounds.bounds.Contains(position))
                return true;
            return start.shipBounds != null && start.shipBounds.bounds.Contains(position);
        }

        private static PlayerControllerB FindPlayerHolding(GrabbableObject item)
        {
            if (item == null)
                return null;
            if (item.playerHeldBy != null)
                return item.playerHeldBy;

            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null)
                    continue;
                if (player.currentlyHeldObjectServer == item)
                    return player;

                GrabbableObject[] slots = player.ItemSlots;
                if (slots == null)
                    continue;
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    if (slots[slot] == item)
                        return player;
                }
            }

            return null;
        }

        private static void RemoveDrillObject(BreachDrillItem drill, string reason)
        {
            if (drill == null)
                return;

            PhysicsProp prop = drill._prop != null ? drill._prop : drill.GetComponent<PhysicsProp>();
            PlayerControllerB holder = FindPlayerHolding(prop);
            try
            {
                if (holder != null && prop != null)
                {
                    if (holder.currentlyHeldObjectServer == prop)
                    {
                        holder.DespawnHeldObject();
                        return;
                    }

                    prop.DestroyObjectInHand(holder);
                }

                NetworkObject networkObject = prop != null && prop.NetworkObject != null
                    ? prop.NetworkObject
                    : drill.GetComponent<NetworkObject>();
                if (networkObject != null && networkObject.IsSpawned)
                    networkObject.Despawn(destroy: true);
                else
                    UnityEngine.Object.Destroy(drill.gameObject);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Failed removing returned drill via {reason}: {e.Message}");
            }
        }

        internal bool Delivered => _initialized;
        internal bool Attached => _attached.Value;
        internal bool Completed => _completed.Value;
        internal float RemainingSeconds
        {
            get
            {
                if (_completed.Value)
                    return 0f;

                double now = NetworkManager.Singleton != null
                    ? NetworkManager.Singleton.ServerTime.Time
                    : Time.timeAsDouble;
                return Mathf.Max(0f, _finishServerTime.Value - (float)now);
            }
        }

        internal void InitializeRuntime(float drillDurationSeconds, string displayName)
        {
            _drillDurationSeconds = Mathf.Max(1f, drillDurationSeconds);
            _displayName = string.IsNullOrWhiteSpace(displayName) ? "SHADOW RAID DRILL" : displayName;
            _prop = GetComponent<PhysicsProp>();
            _scanNode = GetComponentInChildren<ScanNodeProperties>(true);
            EnsureRuntimeParts();
            UpdateScreenLocal();
            UpdateScanNode();
            _initialized = true;
        }

        private void Start()
        {
            if (!_initialized)
                InitializeRuntime(MoonContractState.GetShadowRaidDrillDurationSecondsForCurrentMoon(), "SHADOW RAID DRILL");
            _wasHeldLastFrame = HeldByPlayer();
            _wasGroundedLastFrame = _prop != null && _prop.hasHitGround;

            if (!ActiveDrills.Contains(this))
                ActiveDrills.Add(this);
        }

        private void OnDestroy()
        {
            ActiveDrills.Remove(this);
            _screen?.Dispose();
            _screen = null;
        }

        private void LateUpdate()
        {
            UpdateCarriedRepresentation();
            ApplyHeldPoseAfterVanilla();
        }

        internal void ApplyHeldPoseAfterVanilla()
        {
            // Run after vanilla has placed the item at its socket. Never move an attached drill,
            // a dropped item, or an item owned by a special interaction away from its owner.
            var holder = _prop != null && _prop.isHeld && !_prop.isPocketed ? _prop.playerHeldBy : null;
            if (!_carriedRepresentationApplied || _attached.Value || _completed.Value || holder == null
                || holder.isPlayerDead || holder.inSpecialInteractAnimation
                || _prop.parentObject == null
                || (_prop.parentObject != holder.serverItemHolder && _prop.parentObject != holder.localItemHolder))
            {
                _carryBodyModel = null;
                _carryLeftHand = null;
                _carryRightHand = null;
                return;
            }

            bool firstPerson = _prop.parentObject == holder.localItemHolder;
            var model = firstPerson ? holder.thisPlayerModelArms : holder.thisPlayerModel;
            if (_carryBodyModel != model)
            {
                _carryBodyModel = model;
                _carryLeftHand = null;
                _carryRightHand = null;
                if (model != null)
                    foreach (var bone in model.bones)
                    {
                        if (bone == null) continue;
                        if (bone.name == "hand.L") _carryLeftHand = bone;
                        else if (bone.name == "hand.R") _carryRightHand = bone;
                    }
            }
            // Custom player models without vanilla wrist names retain vanilla's socket pose.
            if (_carryLeftHand == null || _carryRightHand == null)
                return;

            // The local arms travel with camera pitch; keep the machine in the same view frame.
            var cameraFrame = firstPerson && holder.gameplayCamera != null ? holder.gameplayCamera.transform : null;
            Vector3 carryUp = cameraFrame != null ? cameraFrame.up : Vector3.up;
            Vector3 forward = cameraFrame != null ? cameraFrame.forward
                : Vector3.ProjectOnPlane(holder.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.001f)
                return;

            // The authored drill's bit is +Z. Support its lower body between the wrists instead
            // of centring the entire machine on the right hand and inheriting the wrist's roll.
            float halfHeight = BreachDrillItemRegistry.VisualSize.y * ResolveCarriedScaleFactor() * 0.5f;
            Vector3 position = (_carryLeftHand.position + _carryRightHand.position) * 0.5f
                               + carryUp * Mathf.Max(0f, halfHeight - 0.04f);
            transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward, carryUp));
        }

        private void Update()
        {
            if (!_initialized)
                InitializeRuntime(MoonContractState.GetShadowRaidDrillDurationSecondsForCurrentMoon(), "SHADOW RAID DRILL");

            EnsureRuntimeParts();
            if (_prop != null && _prop.playerHeldBy != null)
                _lastHolderClientId = _prop.playerHeldBy.actualClientId;
            HandleDropTransition();
            SyncAttachmentFromContainerOnClient();
            UpdateCarriedRepresentation();

            if (IsServerInstance && !_attached.Value && !_completed.Value)
                TryAutoAttachOnServer();

            if (IsServerInstance && _attached.Value && !_completed.Value && RemainingSeconds <= 0.01f)
                CompleteOnServer();

            UpdateAttachedPose();
            if (!_attached.Value && !_completed.Value && !HeldByPlayer())
            {
                SetGrabbableState(true);
                StabilizeLooseDrillBody();
                if (_prop != null && _prop.hasHitGround)
                {
                    if (!_wasGroundedLastFrame)
                    {
                        RestoreLoosePickupState(markGrounded: true);
                        EnsureLooseRootFloorClearance();
                        SetLooseBodyKinematic();
                        LogLoosePickupState("settle");
                    }
                    else if (Time.time >= _nextLooseFloorCheckAt)
                    {
                        _nextLooseFloorCheckAt = Time.time + 0.5f;
                        EnsureLooseRootFloorClearance();
                    }
                }
            }
            ApplyRunningEffects();
            EnsureScreenDisplay();
            UpdateScreenLocal();
            UpdateScanNode();
            _wasGroundedLastFrame = _prop != null && _prop.hasHitGround;
        }

        /// <summary>
        /// True while any part the running drill depends on is still unresolved. Once every part is
        /// bound the whole resolve pass is skipped, which is what keeps <see cref="Update"/> off the
        /// per-frame hierarchy walks (<c>GetComponentsInChildren</c>, <c>FindChildRecursive</c>).
        /// Optional parts (<c>_sparkOrigin</c>, <c>_rotatingBit</c>) are deliberately not required:
        /// prefabs without them would otherwise re-resolve forever.
        /// </summary>
        private bool NeedsRuntimePartResolve()
        {
            return !_runtimePartsResolved
                   || _attachPoint == null
                   || _drillTip == null
                   || _bodyVisual == null
                   || _audio == null
                   || _tipLight == null
                   || _sparks == null;
        }

        /// <summary>
        /// Retries the parts that <see cref="NeedsRuntimePartResolve"/> deliberately does not require,
        /// so a first-frame miss is not permanent. The rotating bit is the one that matters: without it
        /// there is no spin and no spark/tip alignment, and the authored child can appear a frame or two
        /// after the runtime visual is instantiated. Bounded so a prefab that genuinely has no drill bit
        /// stops paying for the search.
        /// </summary>
        private void RetryOptionalPartResolve()
        {
            if (_rotatingBit != null || _rotatingBitResolveAttempts >= MaxRotatingBitResolveAttempts)
                return;

            _rotatingBitResolveAttempts++;
            _rotatingBit = FindRotatingBitCandidate();
            if (_rotatingBit == null)
                return;

            // A newly bound bit invalidates everything derived from it.
            _rotatingBitSpinAxisResolved = false;
            _rotatingBitBaseLocalPositionCaptured = false;
            _bitTipMeshResolved = false;
            _bitTipMeshTransform = null;
            _nextBitTipResolveAt = 0f;
            _visualRenderers = null;
            CaptureRunningVisualBasePositions();
            if (!_loggedRotatingBit)
            {
                _loggedRotatingBit = true;
                LogResolvedDrillBitTarget(_rotatingBit);
            }
        }

        private void EnsureRuntimeParts()
        {
            if (!NeedsRuntimePartResolve())
            {
                EnsureProvidedDrillLoopLoading();
                EnsureProvidedDoorOpenLoading();
                // Periodic safety net for parts that appear after the first resolve (the screen quad
                // from EnsureScreenDisplay, a late material assignment, an authored drill bit that was
                // not in the hierarchy on the first frame). The cached renderer array is dropped so the
                // repair actually re-collects rather than re-walking a stale list.
                if (Time.time >= _nextVisualRepairAt)
                {
                    _nextVisualRepairAt = Time.time + 2f;
                    _visualRenderers = null;
                    EnsureDrillVisualVisible();
                    RetryOptionalPartResolve();
                }

                return;
            }

            if (_attachPoint == null)
                _attachPoint = FindChildRecursive(transform, "AttachPoint");
            if (_drillTip == null)
                _drillTip = FindChildRecursive(transform, "ActualDrillTip") ?? FindChildRecursive(transform, "DrillTip");
            if (_sparkOrigin == null)
                _sparkOrigin = FindChildRecursive(transform, "SparkOrigin");
            if (_bodyVisual == null)
                _bodyVisual = FindChildRecursive(transform, "Visual");
            EnsureDrillVisualVisible();
            ResolveScreenRendererOnce();
            if (_rotatingBit == null)
            {
                _rotatingBit = FindRotatingBitCandidate();
                _rotatingBitSpinAxisResolved = false;
                if (_rotatingBit != null && !_loggedRotatingBit)
                {
                    _loggedRotatingBit = true;
                    LogResolvedDrillBitTarget(_rotatingBit);
                }
            }
            CaptureRunningVisualBasePositions();

            if (_audio == null)
            {
                _audio = GetComponent<AudioSource>();
                if (_audio == null)
                    _audio = gameObject.AddComponent<AudioSource>();
                _audio.clip = _providedDrillLoopClip != null ? _providedDrillLoopClip : GetOrCreateDrillLoopClip();
                _audio.playOnAwake = false;
                _audio.spatialBlend = 1f;
                _audio.rolloffMode = AudioRolloffMode.Linear;
                _audio.minDistance = 2f;
                _audio.maxDistance = 26f;
                _audio.volume = 0.36f;
                _audio.loop = true;
                _audio.pitch = 0.92f;
            }
            EnsureProvidedDrillLoopLoading();
            EnsureProvidedDoorOpenLoading();

            if (_tipLight == null)
            {
                var lightObject = new GameObject("DrillTipLight");
                lightObject.transform.SetParent(_drillTip != null ? _drillTip : transform, false);
                lightObject.transform.localPosition = Vector3.zero;
                _tipLight = lightObject.AddComponent<Light>();
                _tipLight.type = LightType.Spot;
                _tipLight.range = 1.65f;
                _tipLight.spotAngle = 30f;
                _tipLight.intensity = 0f;
                _tipLight.color = new Color(0.9f, 0.95f, 1f, 1f);
            }

            if (_sparks == null)
            {
                var sparksObject = new GameObject("DrillSparks");
                sparksObject.transform.SetParent(transform, false);
                sparksObject.transform.position = _sparkOrigin != null ? _sparkOrigin.position : transform.position;
                _sparks = sparksObject.AddComponent<ParticleSystem>();
                var sparksRenderer = sparksObject.GetComponent<ParticleSystemRenderer>();
                if (sparksRenderer != null)
                    PrepareMechanicalSparkRenderer(sparksRenderer, replaceMaterial: true);
                var main = _sparks.main;
                main.playOnAwake = false;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.05f, 0.13f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.45f, 1.2f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.018f, 0.055f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.98f, 0.72f, 1f), new Color(1f, 0.54f, 0.08f, 1f));
                main.gravityModifier = new ParticleSystem.MinMaxCurve(0.25f);
                main.maxParticles = 260;
                main.simulationSpace = ParticleSystemSimulationSpace.World;

                var emission = _sparks.emission;
                emission.rateOverTime = 0f;

                var shape = _sparks.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 24f;
                shape.radius = 0.004f;

                var noise = _sparks.noise;
                noise.enabled = false;
            }

            MaybeLogDebugHierarchy();
            // Latch only once every required part actually bound; a prefab still missing one keeps
            // re-resolving until it appears (NeedsRuntimePartResolve reads this flag, so test the
            // references directly here rather than calling back into it).
            _runtimePartsResolved = _attachPoint != null
                                    && _drillTip != null
                                    && _bodyVisual != null
                                    && _audio != null
                                    && _tipLight != null
                                    && _sparks != null;
            _nextVisualRepairAt = Time.time + 2f;
        }

        private void EnsureDrillVisualVisible()
        {
            Renderer[] renderers = _visualRenderers;
            if (renderers == null || ContainsDestroyedRenderer(renderers))
            {
                renderers = GetComponentsInChildren<Renderer>(true);
                _visualRenderers = renderers;
            }

            int usableRenderers = 0;
            bool initializedBounds = false;
            Bounds combinedBounds = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;
                if (renderer is ParticleSystemRenderer)
                    continue;
                if (renderer.GetComponentInParent<ScanNodeProperties>() != null)
                    continue;

                renderer.enabled = true;
                renderer.forceRenderingOff = false;
                renderer.allowOcclusionWhenDynamic = false;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;

                Material[] materials = renderer.sharedMaterials;
                bool repairedMaterial = materials == null || materials.Length == 0;
                if (materials == null || materials.Length == 0)
                {
                    renderer.sharedMaterial = GetOrCreateDrillFallbackMaterial();
                }
                else
                {
                    for (int slot = 0; slot < materials.Length; slot++)
                    {
                        if (materials[slot] != null)
                            continue;

                        materials[slot] = GetOrCreateDrillFallbackMaterial();
                        repairedMaterial = true;
                    }

                    if (repairedMaterial)
                        renderer.sharedMaterials = materials;
                }

                // renderer.bounds forces a bounds recalculation; it only feeds the one-time Info log,
                // so skip it entirely once that log has been emitted.
                if (!_loggedVisualState)
                {
                    if (!initializedBounds)
                    {
                        combinedBounds = renderer.bounds;
                        initializedBounds = true;
                    }
                    else
                        combinedBounds.Encapsulate(renderer.bounds);
                }

                usableRenderers++;
            }

            if (_loggedVisualState)
                return;

            _loggedVisualState = true;
            if (usableRenderers == 0)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Shadow Raid drill visual has no usable renderers on '{name}'.");
                return;
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Shadow Raid drill visual repair on '{name}': renderers={usableRenderers}, " +
                $"boundsCenter={FormatVector(combinedBounds.center)}, boundsSize={FormatVector(combinedBounds.size)}.");
        }

        private static bool ContainsDestroyedRenderer(Renderer[] renderers)
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null)
                    return true;
            }

            return false;
        }

        private static Material GetOrCreateDrillFallbackMaterial()
        {
            if (_drillFallbackMaterial != null)
                return _drillFallbackMaterial;

            Shader shader = Shader.Find("HDRP/Lit")
                            ?? Shader.Find("High Definition Render Pipeline/Lit")
                            ?? Shader.Find("Standard")
                            ?? Shader.Find("Unlit/Color");
            _drillFallbackMaterial = new Material(shader)
            {
                name = "LGU_BreachDrillFallbackMaterial",
                hideFlags = HideFlags.HideAndDontSave
            };

            Color color = new Color(0.18f, 0.20f, 0.22f, 1f);
            SetMaterialColorIfPresent(_drillFallbackMaterial, "_BaseColor", color);
            SetMaterialColorIfPresent(_drillFallbackMaterial, "_Color", color);
            SetMaterialFloatIfPresent(_drillFallbackMaterial, "_Metallic", 0.1f);
            SetMaterialFloatIfPresent(_drillFallbackMaterial, "_Smoothness", 0.34f);
            SetMaterialFloatIfPresent(_drillFallbackMaterial, "_Glossiness", 0.34f);
            _drillFallbackMaterial.renderQueue = 2000;
            _drillFallbackMaterial.SetOverrideTag("RenderType", "Opaque");
            return _drillFallbackMaterial;
        }

        private static Transform FindChildRecursive(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
                return null;

            Transform direct = root.Find(name);
            if (direct != null)
                return direct;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform nested = FindChildRecursive(root.GetChild(i), name);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private void EnsureProvidedDrillLoopLoading()
        {
            if (_audio != null && _providedDrillLoopClip != null && _audio.clip != _providedDrillLoopClip)
            {
                bool wasPlaying = _audio.isPlaying;
                _audio.clip = _providedDrillLoopClip;
                if (wasPlaying)
                    _audio.Play();
                return;
            }

            if (_providedDrillLoopLoadStarted || _providedDrillLoopLoadFailed || !gameObject.activeInHierarchy)
                return;

            string path = ResolveProvidedDrillLoopPath();
            if (string.IsNullOrEmpty(path))
            {
                _providedDrillLoopLoadFailed = true;
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Drill loop audio missing: {ProvidedDrillLoopFileName}");
                return;
            }

            _providedDrillLoopLoadStarted = true;
            StartCoroutine(LoadProvidedDrillLoop(path));
        }

        private void EnsureProvidedDoorOpenLoading()
        {
            if (_providedDoorOpenLoadStarted || _providedDoorOpenLoadFailed || _providedDoorOpenClip != null || !gameObject.activeInHierarchy)
                return;

            string path = ResolveProvidedAudioPath(ProvidedDoorOpenFileName);
            if (string.IsNullOrEmpty(path))
            {
                _providedDoorOpenLoadFailed = true;
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Door opening audio missing: {ProvidedDoorOpenFileName}");
                return;
            }

            _providedDoorOpenLoadStarted = true;
            StartCoroutine(LoadProvidedDoorOpen(path));
        }

        private IEnumerator LoadProvidedDrillLoop(string path)
        {
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _providedDrillLoopLoadFailed = true;
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Failed loading drill loop audio '{path}': {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    _providedDrillLoopLoadFailed = true;
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Drill loop audio decoded null: {path}");
                    yield break;
                }

                clip.name = "LGU_BreachDrillLoop_Provided";
                _providedDrillLoopClip = clip;
                if (_audio != null)
                {
                    bool wasPlaying = _audio.isPlaying;
                    _audio.clip = clip;
                    if (wasPlaying)
                        _audio.Play();
                }

                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Loaded provided drill loop audio from '{path}'.");
            }
        }

        private IEnumerator LoadProvidedDoorOpen(string path)
        {
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _providedDoorOpenLoadFailed = true;
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Failed loading door opening audio '{path}': {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    _providedDoorOpenLoadFailed = true;
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Door opening audio decoded null: {path}");
                    yield break;
                }

                clip.name = "LGU_BreachDoorOpen_Provided";
                _providedDoorOpenClip = clip;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Loaded provided door opening audio from '{path}'.");
            }
        }

        private static string ResolveProvidedDrillLoopPath()
        {
            return ResolveProvidedAudioPath(ProvidedDrillLoopFileName);
        }

        private static string ResolveProvidedAudioPath(string fileName)
        {
            string assemblyDir = Path.GetDirectoryName(typeof(ContractsBootstrap).Assembly.Location) ?? string.Empty;
            string[] pluginRoots =
            {
                assemblyDir,
                Path.Combine(Paths.PluginPath, "Contracted"),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany"),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades"),
                Paths.PluginPath,
                AppDomain.CurrentDomain.BaseDirectory,
            };
            string[] subfolders = { "Breach Prototype", "Blacksite" };

            for (int folderIndex = 0; folderIndex < subfolders.Length; folderIndex++)
            {
                for (int rootIndex = 0; rootIndex < pluginRoots.Length; rootIndex++)
                {
                    string candidate = Path.Combine(pluginRoots[rootIndex], "ContractAudio", subfolders[folderIndex], fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return null;
        }

        private void EnsureBundledSparkEffect()
        {
            if (_bundledSparkEffect != null)
                return;

            GameObject prefab = ContractAssetLoader.BreachDrillSparkEffectPrefab;
            if (prefab == null)
                return;

            try
            {
                _bundledSparkEffect = Instantiate(prefab, transform, false);
                _bundledSparkEffect.name = "DrillBundledSparks";
                _bundledSparkEffect.transform.localPosition = Vector3.zero;
                _bundledSparkEffect.transform.localRotation = Quaternion.identity;
                _bundledSparkEffect.transform.localScale = Vector3.one * BundledSparkEffectScale;

                _bundledSparkSystems = _bundledSparkEffect.GetComponentsInChildren<ParticleSystem>(true);
                for (int i = 0; i < _bundledSparkSystems.Length; i++)
                {
                    ParticleSystem system = _bundledSparkSystems[i];
                    if (system == null)
                        continue;

                    var main = system.main;
                    main.playOnAwake = false;
                    main.loop = true;
                    main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                    main.simulationSpace = ParticleSystemSimulationSpace.World;
                    main.startLifetime = new ParticleSystem.MinMaxCurve(0.06f, 0.16f);
                    main.startSpeed = new ParticleSystem.MinMaxCurve(0.28f, 0.85f);
                    main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.075f);
                    main.gravityModifier = new ParticleSystem.MinMaxCurve(0.45f);
                    main.maxParticles = 120;
                    main.startColor = new ParticleSystem.MinMaxGradient(
                        new Color(1f, 0.96f, 0.72f, 1f),
                        new Color(1f, 0.52f, 0.08f, 1f));
                    var emission = system.emission;
                    emission.enabled = true;
                    emission.rateOverTime = new ParticleSystem.MinMaxCurve(95f);

                    var shape = system.shape;
                    if (shape.enabled)
                    {
                        shape.angle = 18f;
                        shape.radius = 0.004f;
                    }

                    var noise = system.noise;
                    noise.enabled = false;
                    var velocity = system.velocityOverLifetime;
                    velocity.enabled = false;
                    var force = system.forceOverLifetime;
                    force.enabled = false;

                    system.transform.localPosition = Vector3.zero;
                    system.transform.localRotation = Quaternion.identity;
                    system.transform.localScale = Vector3.one;

                    ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
                    PrepareMechanicalSparkRenderer(renderer, replaceMaterial: true);
                    system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }

                Light[] lights = _bundledSparkEffect.GetComponentsInChildren<Light>(true);
                for (int i = 0; i < lights.Length; i++)
                {
                    Light light = lights[i];
                    if (light == null)
                        continue;

                    light.color = new Color(1f, 0.72f, 0.24f, 1f);
                    light.range = Mathf.Max(Mathf.Min(light.range, 2.6f), 1.6f);
                    light.intensity = Mathf.Max(Mathf.Min(light.intensity, 2.4f), 1.6f);
                }

                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Attached bundled drill spark effect '{prefab.name}' with {_bundledSparkSystems.Length} particle system(s).");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Failed attaching bundled drill spark effect: {e.Message}");
                if (_bundledSparkEffect != null)
                    Destroy(_bundledSparkEffect);
                _bundledSparkEffect = null;
                _bundledSparkSystems = null;
            }
        }

        private static void PrepareMechanicalSparkRenderer(ParticleSystemRenderer renderer, bool replaceMaterial, bool preserveMotion = false)
        {
            if (renderer == null)
                return;

            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            if (!preserveMotion)
            {
                renderer.lengthScale = 1f;
                renderer.velocityScale = 0f;
            }
            renderer.cameraVelocityScale = 0f;
            renderer.minParticleSize = 0.004f;
            renderer.maxParticleSize = 0.07f;
            renderer.normalDirection = 1f;
            if (replaceMaterial || renderer.sharedMaterial == null)
                renderer.sharedMaterial = CreateMechanicalSparkMaterial(renderer.sharedMaterial);
        }

        private static Material CreateMechanicalSparkMaterial(Material sourceParticleMaterial)
        {
            Shader shader = Shader.Find("HDRP/Unlit")
                            ?? Shader.Find("Sprites/Default")
                            ?? Shader.Find("Unlit/Transparent")
                            ?? Shader.Find("Unlit/Color")
                            ?? Shader.Find("Standard");
            Material material = new Material(shader);
            material.name = "LGU_BreachDrillSpark";
            material.hideFlags = HideFlags.HideAndDontSave;
            material.renderQueue = 3100;
            Texture sourceTexture = GetOrCreateRuntimeSparkTexture();
            if (sourceTexture != null)
            {
                SetMaterialTextureIfPresent(material, "_BaseColorMap", sourceTexture);
                SetMaterialTextureIfPresent(material, "_MainTex", sourceTexture);
                SetMaterialTextureIfPresent(material, "_UnlitColorMap", sourceTexture);
                SetMaterialTextureIfPresent(material, "_EmissiveColorMap", sourceTexture);
            }

            Color color = new Color(1f, 0.74f, 0.22f, 1f);
            material.color = color;
            SetMaterialColorIfPresent(material, "_Color", color);
            SetMaterialColorIfPresent(material, "_BaseColor", color);
            SetMaterialColorIfPresent(material, "_UnlitColor", color);
            SetMaterialColorIfPresent(material, "_TintColor", color);
            SetMaterialColorIfPresent(material, "_EmissionColor", color * 6f);
            SetMaterialColorIfPresent(material, "_EmissiveColor", color * 6f);
            SetMaterialFloatIfPresent(material, "_SurfaceType", 1f);
            SetMaterialFloatIfPresent(material, "_BlendMode", 1f);
            SetMaterialFloatIfPresent(material, "_ZWrite", 0f);
            SetMaterialFloatIfPresent(material, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            SetMaterialFloatIfPresent(material, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            SetMaterialFloatIfPresent(material, "_AlphaSrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            SetMaterialFloatIfPresent(material, "_AlphaDstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            SetMaterialFloatIfPresent(material, "_CullMode", 0f);
            SetMaterialFloatIfPresent(material, "_TransparentCullMode", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_BLENDMODE_ADD");
            material.DisableKeyword("_ALPHATEST_ON");
            return material;
        }

        private static Texture2D GetOrCreateRuntimeSparkTexture()
        {
            if (_runtimeSparkTexture != null)
                return _runtimeSparkTexture;

            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                name = "LGU_BreachSparkTexture",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            Color[] pixels = new Color[size * size];
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center) / (size * 0.5f);
                    float alpha = Mathf.Clamp01(1f - distance);
                    alpha *= alpha;
                    Color core = Color.Lerp(new Color(1f, 0.42f, 0.05f, alpha), new Color(1f, 0.98f, 0.78f, alpha), alpha);
                    pixels[y * size + x] = core;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            _runtimeSparkTexture = texture;
            return _runtimeSparkTexture;
        }

        private static Texture TryGetMaterialTexture(Material material)
        {
            if (material == null)
                return null;

            string[] textureProperties =
            {
                "_BaseColorMap",
                "_MainTex",
                "_UnlitColorMap",
                "_EmissiveColorMap",
                "_TintMap",
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

        private static Material TryGetInGameParticleMaterial()
        {
            try
            {
                PlayerControllerB player = StartOfRound.Instance != null
                    ? StartOfRound.Instance.localPlayerController
                    : null;
                ParticleSystem blood = player != null ? player.bloodParticle : null;
                ParticleSystemRenderer renderer = blood != null
                    ? blood.GetComponent<ParticleSystemRenderer>()
                    : null;
                return renderer != null ? renderer.sharedMaterial : null;
            }
            catch
            {
                return null;
            }
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

        private static void SetMaterialTextureIfPresent(Material material, string property, Texture texture)
        {
            if (material != null && texture != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        private static bool LooksLikeScreenMaterial(Material material)
        {
            if (material == null)
                return false;

            if (material.name.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            Texture texture = null;
            if (material.HasProperty("_BaseColorMap"))
                texture = material.GetTexture("_BaseColorMap");
            if (texture == null && material.HasProperty("_MainTex"))
                texture = material.GetTexture("_MainTex");
            return texture != null && texture.name.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ResolveScreenRendererOnce()
        {
            // The physical screen mesh never moves once the visual is instantiated, so scan for it
            // only once instead of every frame. Used to bind the countdown RenderTexture in
            // BreachDrillScreenDisplay; if no screen-named renderer exists the display stays disabled.
            if (_screenRenderer != null || _screenRendererResolveAttempted)
                return;

            _screenRendererResolveAttempted = true;
            _screenRenderer = FindScreenRendererCandidate();
        }

        private Renderer FindScreenRendererCandidate()
        {
            Renderer materialFallback = null;
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!IsScreenSearchRenderer(renderer))
                    continue;

                Material[] materials = renderer.sharedMaterials;
                bool hasScreenMaterial = false;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    if (LooksLikeScreenMaterial(materials[slot]))
                    {
                        hasScreenMaterial = true;
                        break;
                    }
                }

                string path = BuildTransformPath(renderer.transform);
                bool namedLikeScreen = path.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0
                                       || path.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0
                                       || path.IndexOf("monitor", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isAuthoredBodyRenderer = string.Equals(renderer.transform.name, "Visual", StringComparison.OrdinalIgnoreCase)
                                               && (path.IndexOf("BreachDrillVisual", StringComparison.OrdinalIgnoreCase) >= 0
                                                   || path.IndexOf("BreachDrill", StringComparison.OrdinalIgnoreCase) >= 0);
                if (hasScreenMaterial && isAuthoredBodyRenderer)
                    return renderer;
                if (hasScreenMaterial && namedLikeScreen)
                    return renderer;
                if (materialFallback == null && hasScreenMaterial)
                    materialFallback = renderer;
            }

            return materialFallback;
        }

        private static bool IsScreenSearchRenderer(Renderer renderer)
        {
            if (renderer == null)
                return false;
            if (renderer is ParticleSystemRenderer)
                return false;
            if (renderer.GetComponentInParent<ScanNodeProperties>() != null)
                return false;

            string path = BuildTransformPath(renderer.transform);
            return path.IndexOf(AuthoredRotatingDrillBitName, StringComparison.OrdinalIgnoreCase) < 0
                   && path.IndexOf("RotatingBit", StringComparison.OrdinalIgnoreCase) < 0
                   && path.IndexOf("DrillBit", StringComparison.OrdinalIgnoreCase) < 0
                   && path.IndexOf("BitVisual", StringComparison.OrdinalIgnoreCase) < 0
                   && path.IndexOf("ActualDrillTip", StringComparison.OrdinalIgnoreCase) < 0
                   && path.IndexOf("DrillTip", StringComparison.OrdinalIgnoreCase) < 0
                   && path.IndexOf("SparkOrigin", StringComparison.OrdinalIgnoreCase) < 0
                   && path.IndexOf("auger", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static string BuildTransformPath(Transform current)
        {
            if (current == null)
                return string.Empty;

            string path = current.name;
            while (current.parent != null)
            {
                current = current.parent;
                path = current.name + "/" + path;
            }

            return path;
        }

        private void TryAutoAttachOnServer()
        {
            if (_prop == null || !_prop.hasBeenHeld || _prop.isHeld || _prop.isPocketed || !_grabbableOnGroundReady() || _completed.Value)
                return;

            if (Time.time - _lastReleasedAt < 0.35f)
                return;

            BreachContainerController container = BreachContainerController.FindNearestClosedContainer(transform.position, ContractsBootstrap.ShadowRaidDrillAttachDistance.Value);
            if (container == null)
                return;

            _attached.Value = true;
            _attachedContainerDirect = container;
            _containerNetworkId.Value = container.NetworkId;
            _finishServerTime.Value = (float)((NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : Time.timeAsDouble) + _drillDurationSeconds);

            SetGrabbableState(false);
            FreezeAttachedBodyLocal();

            AlignToContainer(container);
            _attachedByClientId = _lastHolderClientId;
            // Replicate the attachment through the container so every client runs the same drill.
            container.SetAttachedDrillServer(SelfNetworkId, _finishServerTime.Value);
            PublishDrillProgression(container.NetworkId, completed: false);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Drill attached to container {container.NetworkId} at {container.transform.position}.");
        }

        /// <summary>
        /// Pins the drill body while it is attached. Runs on every peer so a client's copy stops its
        /// own fall lerp instead of drifting away from the pose the server holds it in.
        /// </summary>
        private void FreezeAttachedBodyLocal()
        {
            if (_prop == null)
                return;

            Rigidbody body = _prop.propBody;
            StopDynamicBodyMotion(body);
            _prop.EnablePhysics(enable: false);
            if (body != null)
            {
                body.isKinematic = true;
                body.useGravity = false;
            }
        }

        /// <summary>
        /// NetworkObjectId of this drill, taken from the vanilla PhysicsProp that owns the item's
        /// NetworkObject (this component is intentionally not a NetworkBehaviour). 0 until spawned.
        /// </summary>
        private ulong SelfNetworkId
        {
            get
            {
                if (_prop == null)
                    return 0UL;

                NetworkObject networkObject = _prop.NetworkObject;
                return networkObject != null && networkObject.IsSpawned ? networkObject.NetworkObjectId : 0UL;
            }
        }

        /// <summary>
        /// Mirrors the server's drill attachment onto clients. The drill itself owns no network state,
        /// so the truth lives on the Shadow Raid container (a properly spawned NetworkBehaviour) and is
        /// read back here. Without this, spin, sparks, vibration, loop audio, the physical screen, the
        /// scan node countdown and the HUD DRILL line only ever ran on the host.
        /// </summary>
        private void SyncAttachmentFromContainerOnClient()
        {
            if (IsServerInstance)
                return;

            // Container lookup is a handful of list entries; poll it a few times a second rather than
            // every frame, and let the finish time drive the countdown in between.
            if (Time.time < _nextClientStateSyncAt)
                return;

            _nextClientStateSyncAt = Time.time + 0.1f;
            ulong selfId = SelfNetworkId;
            if (selfId == 0UL)
                return;

            BreachContainerController owner = BreachContainerController.FindContainerDrilledBy(selfId);
            if (owner != null)
            {
                _attachedContainerDirect = owner;
                _containerNetworkId.Value = owner.NetworkId;
                _finishServerTime.Value = owner.AttachedDrillFinishServerTime;
                _completed.Value = false;
                if (!_attached.Value)
                {
                    _attached.Value = true;
                    SetGrabbableState(false);
                    FreezeAttachedBodyLocal();
                }

                _detachGraceUntil = 0f;
                return;
            }

            // Not attached per the server. If a completion was published for this drill that this peer
            // has not applied yet, replay the host's drop exactly once.
            BreachContainerController completedOn = BreachContainerController.FindContainerThatCompletedDrill(selfId);
            if (completedOn != null
                && (completedOn.NetworkId != _appliedCompletionContainerId
                    || completedOn.CompletedDrillSequence != _appliedCompletionSequence))
            {
                float publishedAt = completedOn.CompletedDrillServerTime;
                if (publishedAt <= 0f)
                {
                    // The sequence is visible but the pose payload has not been applied on this peer
                    // yet. Leave the completion UNLATCHED and retry: latching here would consume the
                    // event and the drop would never be replayed.
                    return;
                }

                double now = NetworkManager.Singleton != null
                    ? NetworkManager.Singleton.ServerTime.Time
                    : Time.timeAsDouble;
                // A late joiner arriving long after the drill finished must not teleport an item the
                // crew has since carried elsewhere.
                bool definitivelyStale = (float)now - publishedAt > 5f;
                bool applyPose = !definitivelyStale && !HeldByPlayer();

                _appliedCompletionContainerId = completedOn.NetworkId;
                _appliedCompletionSequence = completedOn.CompletedDrillSequence;
                _detachGraceUntil = 0f;
                ApplyReplicatedCompletionDrop(completedOn, applyPose);
                return;
            }

            if (!_attached.Value)
                return;

            // The attachment cleared. _attachedDrillNetworkId is declared before the completion payload
            // on the container, so NGO raises its OnValueChanged while the completion fields of the same
            // delta are still unread — the clear is always observed first. Give the completion a short
            // grace period to land before treating this as a plain detach; otherwise a real completion
            // would be settled as a pose-less drop.
            if (_detachGraceUntil <= 0f)
            {
                _detachGraceUntil = Time.time + 0.5f;
                return;
            }

            if (Time.time < _detachGraceUntil)
                return;

            // Attachment cleared with no completion behind it (drill removed, contract reset). Hand the
            // item back to normal physics; there is no authored drop pose to replay here.
            _detachGraceUntil = 0f;
            _attached.Value = false;
            _completed.Value = false;
            _finishServerTime.Value = 0f;
            _containerNetworkId.Value = 0UL;
            _attachedContainerDirect = null;
            SetGrabbableState(true);
            RestoreLoosePickupState(markGrounded: true);
        }

        /// <summary>
        /// Replays the server's completion drop on a client: the same pose, fall target and physics
        /// hand-off that <see cref="DropReusableDrillAfterCompletion"/> applies on the host. Runs once
        /// per published completion (container id plus sequence), including for a late joiner. The
        /// caller decides whether the pose is still worth applying (<paramref name="applyPose"/>); a
        /// stale or held drill still settles into the same loose, reusable state without teleporting.
        /// </summary>
        private void ApplyReplicatedCompletionDrop(BreachContainerController container, bool applyPose)
        {
            _attached.Value = false;
            _completed.Value = true;
            _finishServerTime.Value = 0f;
            _containerNetworkId.Value = 0UL;
            _attachedContainerDirect = null;

            // Stop the running effects on this frame rather than on the next poll.
            StopDrillLoopAudio();
            ApplyRunningEffects();
            UpdateScreenLocal();
            UpdateScanNode();

            if (applyPose)
            {
                Vector3 dropPosition = container.CompletedDrillDropPosition;
                Vector3 floorPosition = container.CompletedDrillFloorPosition;
                transform.position = dropPosition;
                transform.rotation = Quaternion.Euler(0f, container.CompletedDrillDropYaw, 0f);
                _lastReleasedAt = Time.time;
                RestoreLoosePickupState(markGrounded: false);
                SetGrabbableState(true);

                if (_prop != null)
                {
                    _prop.hasHitGround = false;
                    _prop.reachedFloorTarget = false;
                    _prop.fallTime = 0f;
                    _prop.startFallingPosition = ToLocalDropPosition(dropPosition);
                    _prop.targetFloorPosition = ToLocalDropPosition(floorPosition);
                    _prop.EnablePhysics(enable: true);
                    if (_prop.propBody != null)
                    {
                        _prop.propBody.isKinematic = false;
                        _prop.propBody.useGravity = true;
                        _prop.propBody.constraints = RigidbodyConstraints.FreezeRotation;
                        _prop.propBody.velocity = Vector3.down * 0.15f;
                        _prop.propBody.angularVelocity = Vector3.zero;
                    }

                    _wasGroundedLastFrame = false;
                }
            }
            else
            {
                SetGrabbableState(true);
                // A delayed completion must never clear a pickup that already succeeded.
                // It would orphan the held item from vanilla's inventory bookkeeping.
                if (!HeldByPlayer())
                    RestoreLoosePickupState(markGrounded: true);
            }

            // The host's own _completed is momentary (CompleteOnServer clears it once the drop is set
            // up); clear it here too so both ends settle on the same "loose and reusable" state.
            _completed.Value = false;
        }

        private bool _grabbableOnGroundReady()
        {
            return _prop != null && _prop.hasHitGround;
        }

        private void UpdateAttachedPose()
        {
            if (!_attached.Value || _completed.Value)
                return;

            BreachContainerController container = ResolveAttachedContainer();
            if (container == null)
                return;

            AlignToContainer(container);
            ApplyRunningVibration(container);
        }

        private void AlignToContainer(BreachContainerController container)
        {
            Transform anchor = _attachPoint != null ? _attachPoint : transform;
            Transform containerAnchor = container.LockAnchor != null ? container.LockAnchor : container.transform;

            Vector3 desiredDirection = -ResolveContainerOutward(container);
            desiredDirection.y = 0f;
            if (desiredDirection.sqrMagnitude < 0.001f)
                desiredDirection = -containerAnchor.forward;
            desiredDirection.y = 0f;
            if (desiredDirection.sqrMagnitude < 0.001f)
                desiredDirection = Vector3.right;
            desiredDirection.Normalize();

            Vector3 localToolAxis = _rotatingBit != null
                ? (_rotatingBit.localRotation * ResolveRotatingBitLocalSpinAxis(_rotatingBit)).normalized
                : (_drillTip != null && _attachPoint != null
                    ? (_drillTip.localPosition - _attachPoint.localPosition).normalized
                    : Vector3.right);
            localToolAxis.y = 0f;
            if (localToolAxis.sqrMagnitude < 0.001f)
                localToolAxis = Vector3.forward;
            localToolAxis.Normalize();

            Quaternion desiredRotation = Quaternion.LookRotation(desiredDirection, Vector3.up) *
                                         Quaternion.Inverse(Quaternion.LookRotation(localToolAxis, Vector3.up));
            transform.rotation = Quaternion.Euler(0f, desiredRotation.eulerAngles.y, 0f);
            PinResolvedBitTipToSparkContact(container, anchor);
        }

        private void PinResolvedBitTipToSparkContact(BreachContainerController container, Transform fallbackAnchor = null)
        {
            if (container == null)
                return;

            Vector3 toolContactWorld = TryGetResolvedBitTipWorld(out Vector3 resolvedBitTipWorld)
                ? resolvedBitTipWorld
                : (_drillTip != null
                    ? _drillTip.position
                    : (fallbackAnchor != null ? fallbackAnchor.position : transform.position));
            Vector3 desiredContactWorld = GetDrillSparkContactPoint(container, out _);
            transform.position += desiredContactWorld - toolContactWorld;
        }

        private void ApplyRunningEffects()
        {
            bool running = _attached.Value && !_completed.Value;
            if (_tipLight != null)
                _tipLight.intensity = running ? 1.15f : 0f;

            if (_tipLight != null && _drillTip != null)
            {
                BreachContainerController container = ResolveAttachedContainer();
                if (container != null)
                {
                    Vector3 target = GetSparkEmitterPoint(container, out _);
                    Vector3 lightDirection = target - _tipLight.transform.position;
                    if (lightDirection.sqrMagnitude > 0.0001f)
                        _tipLight.transform.rotation = Quaternion.LookRotation(lightDirection.normalized, Vector3.up);
                }
            }

            if (_bundledSparkSystems != null && _bundledSparkSystems.Length > 0)
            {
                if (_bundledSparkEffect != null && _bundledSparkEffect.activeSelf != running)
                    _bundledSparkEffect.SetActive(running);

                if (running && _bundledSparkEffect != null)
                {
                    BreachContainerController container = ResolveAttachedContainer();
                    if (container != null)
                        PositionSparkEmitterAtContact(_bundledSparkEffect.transform, container);
                }

                for (int i = 0; i < _bundledSparkSystems.Length; i++)
                {
                    ParticleSystem system = _bundledSparkSystems[i];
                    if (system == null)
                        continue;

                    if (running && !system.isPlaying)
                    {
                        system.Clear(true);
                        system.Play(true);
                        system.Emit(18);
                    }
                    else if (!running && system.isPlaying)
                    {
                        system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    }
                }
            }
            if (_sparks != null)
            {
                var emission = _sparks.emission;
                emission.rateOverTime = running ? 180f : 0f;
                if (running)
                {
                    BreachContainerController container = ResolveAttachedContainer();
                    if (container != null)
                        PositionSparkEmitterAtContact(_sparks.transform, container);
                    _sparks.Emit(3);
                }
                if (running && !_sparks.isPlaying)
                {
                    _sparks.Play();
                    _sparks.Emit(32);
                }
                else if (!running && _sparks.isPlaying)
                    _sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            TickDrillLoopAudio(running);

            if (!running)
                ResetRunningVibration();
            UpdateDrillBitRotation(running);
        }

        private void UpdateDrillBitRotation(bool running)
        {
            if (!running)
                return;

            Transform bit = _rotatingBit;
            if (bit == null)
            {
                if (!_warnedMissingRotatingBit)
                {
                    _warnedMissingRotatingBit = true;
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Drill bit animation skipped because no isolated drill bit transform was found in the prefab.");
                }

                return;
            }

            Vector3 localPosition = bit.localPosition;
            bit.Rotate(ResolveRotatingBitLocalSpinAxis(bit), DrillBitSpinDegreesPerSecond * Time.deltaTime, Space.Self);
            bit.localPosition = localPosition;
        }

        private Transform FindRotatingBitCandidate()
        {
            Transform authored = FindChildByExactName(transform, AuthoredRotatingDrillBitName);
            if (authored == null)
                return null;

            if (!HasUsableRenderer(authored))
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Authored drill bit transform '{GetTransformPath(authored)}' has no renderer; drill bit animation will be invisible.");

            return authored;
        }

        private static bool HasUsableRenderer(Transform target)
        {
            return target != null && target.GetComponentInChildren<Renderer>(true) != null;
        }

        private bool TryGetResolvedBitTipWorld(out Vector3 tipWorld)
        {
            tipWorld = Vector3.zero;
            // Do not re-run the recursive name search every frame, but do not give up on it either:
            // the bounded retry budget is shared with RetryOptionalPartResolve's 2s net.
            Transform bit = _rotatingBit;
            if (bit == null && _rotatingBitResolveAttempts < MaxRotatingBitResolveAttempts)
            {
                _rotatingBitResolveAttempts++;
                bit = _rotatingBit = FindRotatingBitCandidate();
            }

            if (bit == null)
                return false;

            // Resolve the mesh-space tip once it succeeds. The bit spins about its own axis every
            // frame, so the world point still has to be recomputed, but the
            // GetComponentInChildren<MeshFilter> walk and the bounds maths do not. A failed resolve is
            // retried (the mesh can arrive after the transform does) rather than latched off.
            if ((!_bitTipMeshResolved || _bitTipMeshTransform == null) && Time.time >= _nextBitTipResolveAt)
            {
                _nextBitTipResolveAt = Time.time + 2f;
                _bitTipMeshResolved = TryResolveMeshTipLocal(
                    bit,
                    ResolveRotatingBitLocalSpinAxis(bit),
                    out _bitTipMeshTransform,
                    out _bitTipMeshLocalPoint);
            }

            if (_bitTipMeshResolved && _bitTipMeshTransform != null)
            {
                tipWorld = _bitTipMeshTransform.TransformPoint(_bitTipMeshLocalPoint);
                return true;
            }

            Renderer renderer = bit.GetComponentInChildren<Renderer>(true);
            if (renderer == null)
                return false;

            Vector3 axis = bit.TransformDirection(ResolveRotatingBitLocalSpinAxis(bit));
            if (axis.sqrMagnitude < 0.001f && _attachPoint != null && _drillTip != null)
                axis = _drillTip.position - _attachPoint.position;
            if (axis.sqrMagnitude < 0.001f)
                return false;
            axis.Normalize();

            Bounds bounds = renderer.bounds;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
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

            float best = float.NegativeInfinity;
            for (int i = 0; i < corners.Length; i++)
            {
                float score = Vector3.Dot(corners[i], axis);
                if (score > best)
                {
                    best = score;
                    tipWorld = corners[i];
                }
            }

            return true;
        }

        /// <summary>
        /// Resolves the drill-bit tip in the mesh transform's local space. The result is stable for
        /// the life of the item, so callers cache it and only re-run TransformPoint per frame.
        /// </summary>
        private static bool TryResolveMeshTipLocal(Transform bit, Vector3 localAxis, out Transform meshTransform, out Vector3 localTip)
        {
            meshTransform = null;
            localTip = Vector3.zero;
            if (bit == null)
                return false;

            MeshFilter filter = bit.GetComponentInChildren<MeshFilter>(true);
            if (filter != null && filter.sharedMesh != null)
            {
                localTip = GetBoundsTipOnAxis(filter.sharedMesh.bounds, localAxis);
                meshTransform = filter.transform;
                return true;
            }

            SkinnedMeshRenderer skinned = bit.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (skinned != null)
            {
                localTip = GetBoundsTipOnAxis(skinned.localBounds, localAxis);
                meshTransform = skinned.transform;
                return true;
            }

            return false;
        }

        private void LogResolvedDrillBitTarget(Transform bit)
        {
            Renderer renderer = bit != null ? bit.GetComponentInChildren<Renderer>(true) : null;
            Vector3 spinAxis = ResolveRotatingBitLocalSpinAxis(bit);
            string rendererName = renderer != null ? renderer.GetType().Name : "none";
            string boundsText = renderer != null
                ? $"center={FormatVector(renderer.bounds.center)}, size={FormatVector(renderer.bounds.size)}"
                : "bounds=none";
            string tipText = TryGetResolvedBitTipWorld(out Vector3 tipWorld)
                ? FormatVector(tipWorld)
                : "unknown";
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Drill bit animation target resolved to '{GetTransformPath(bit)}', " +
                $"localPos={FormatVector(bit.localPosition)}, spinAxis={FormatVector(spinAxis)}, renderer={rendererName}, {boundsText}, tip={tipText}.");
        }

        private void MaybeLogDebugHierarchy()
        {
            if (_loggedDebugHierarchy || !BlacksiteDebugLogsEnabled())
                return;

            _loggedDebugHierarchy = true;
            Transform[] transforms = GetComponentsInChildren<Transform>(true);
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid][Debug] Drill runtime hierarchy '{name}': transforms={transforms.Length}, renderers={renderers.Length}, " +
                $"scene='{gameObject.scene.name}', rootFullPath='{GetTransformPath(transform)}', " +
                $"rootPos={FormatDebugVector(transform.position)}, rootRot={FormatDebugVector(transform.eulerAngles)}, rootScale={FormatDebugVector(transform.lossyScale)}, " +
                $"attachFullPath='{GetTransformPath(_attachPoint)}', actualTipFullPath='{GetTransformPath(_drillTip)}', sparkOriginFullPath='{GetTransformPath(_sparkOrigin)}', " +
                $"rotatingBitFullPath='{GetTransformPath(_rotatingBit)}', screenRendererFullPath='{(_screenRenderer != null ? GetTransformPath(_screenRenderer.transform) : "null")}'.");

            for (int i = 0; i < transforms.Length; i++)
            {
                Transform child = transforms[i];
                if (child == null)
                    continue;

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.ShadowRaid][Debug] Drill transform[{i}] fullPath='{GetTransformPath(child)}', activeSelf={child.gameObject.activeSelf}, " +
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

                string drillTipDistanceText = _drillTip != null
                    ? $"{Vector3.Distance(renderer.bounds.center, _drillTip.position):0.0000}"
                    : "unknown";
                bool isParticle = renderer is ParticleSystemRenderer;
                bool isScanNode = renderer.GetComponentInParent<ScanNodeProperties>() != null;
                bool isScreen = renderer == _screenRenderer || RendererHasScreenMaterial(renderer);
                bool isCandidateBit = _rotatingBit != null && (renderer.transform == _rotatingBit || renderer.transform.IsChildOf(_rotatingBit));
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.ShadowRaid][Debug] Drill renderer[{i}] fullPath='{GetTransformPath(renderer.transform)}', type={renderer.GetType().Name}, " +
                    $"enabled={renderer.enabled}, forceOff={renderer.forceRenderingOff}, shadow={renderer.shadowCastingMode}, " +
                    $"isScreen={isScreen}, isParticle={isParticle}, isScanNode={isScanNode}, isCandidateBit={isCandidateBit}, " +
                    $"materials=[{FormatMaterialNames(renderer.sharedMaterials)}], " +
                    $"distanceFromDrillTip={drillTipDistanceText}, localBounds={localBoundsText}, worldBounds={FormatDebugBounds(renderer.bounds)}, {meshText}.");
            }

            if (_rotatingBit != null && TryGetResolvedBitTipWorld(out Vector3 bitTipWorld))
            {
                Vector3 actualTipWorld = _drillTip != null ? _drillTip.position : bitTipWorld;
                Vector3 sparkWorld = _sparkOrigin != null ? _sparkOrigin.position : actualTipWorld;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.ShadowRaid][Debug] Drill bit tip check: bitTip={FormatDebugVector(bitTipWorld)}, " +
                    $"actualTip={FormatDebugVector(actualTipWorld)}, sparkOrigin={FormatDebugVector(sparkWorld)}, " +
                    $"distanceToActualTip={Vector3.Distance(bitTipWorld, actualTipWorld):0.0000}, distanceToSparkOrigin={Vector3.Distance(bitTipWorld, sparkWorld):0.0000}.");
            }
        }

        private static Transform FindChildByExactName(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
                return null;

            if (string.Equals(root.name, name, StringComparison.OrdinalIgnoreCase))
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildByExactName(root.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }

        private void ApplyRunningVibration(BreachContainerController container)
        {
            if (!_attached.Value || _completed.Value)
            {
                ResetRunningVibration();
                return;
            }

            CaptureRunningVisualBasePositions();
            Vector3 vibration = CalculateRunningVibrationOffset();
            ApplyLocalVibration(_bodyVisual, _bodyVisualBaseLocalPosition, _bodyVisualBaseLocalPositionCaptured, vibration);
            ApplyLocalVibration(_rotatingBit, _rotatingBitBaseLocalPosition, _rotatingBitBaseLocalPositionCaptured, vibration);
            MaybeLogTipSparkAlignment(container);
        }

        private void CaptureRunningVisualBasePositions()
        {
            if (_bodyVisual != null && !_bodyVisualBaseLocalPositionCaptured)
            {
                _bodyVisualBaseLocalPosition = _bodyVisual.localPosition;
                _bodyVisualBaseLocalPositionCaptured = true;
            }

            if (_rotatingBit != null && !_rotatingBitBaseLocalPositionCaptured)
            {
                _rotatingBitBaseLocalPosition = _rotatingBit.localPosition;
                _rotatingBitBaseLocalPositionCaptured = true;
            }
        }

        private Vector3 CalculateRunningVibrationOffset()
        {
            float t = Time.time;
            return new Vector3(
                Mathf.Sin(t * 91.0f) * RunningVibrationHorizontalAmplitude
                    + Mathf.Sin(t * 153.0f) * RunningVibrationHorizontalAmplitude * 0.35f,
                Mathf.Sin(t * 117.0f) * RunningVibrationVerticalAmplitude,
                Mathf.Sin(t * 83.0f) * RunningVibrationForwardAmplitude
                    + Mathf.Sin(t * 139.0f) * RunningVibrationForwardAmplitude * 0.3f);
        }

        private static void ApplyLocalVibration(Transform target, Vector3 baseLocalPosition, bool hasBaseLocalPosition, Vector3 vibrationOffset)
        {
            if (target == null || !hasBaseLocalPosition)
                return;

            target.localPosition = baseLocalPosition + vibrationOffset;
        }

        private void ResetRunningVibration()
        {
            if (_bodyVisual != null && _bodyVisualBaseLocalPositionCaptured)
                _bodyVisual.localPosition = _bodyVisualBaseLocalPosition;
            if (_rotatingBit != null && _rotatingBitBaseLocalPositionCaptured)
                _rotatingBit.localPosition = _rotatingBitBaseLocalPosition;
        }

        private void MaybeLogTipSparkAlignment(BreachContainerController container)
        {
            if (_loggedTipSparkAlignment || container == null)
                return;
            Vector3 bitTipWorld;
            if (!TryGetResolvedBitTipWorld(out bitTipWorld))
            {
                if (_drillTip == null)
                    return;
                bitTipWorld = _drillTip.position;
            }
            Vector3 authoredTipWorld = _drillTip != null ? _drillTip.position : bitTipWorld;
            float authoredDistance = Vector3.Distance(authoredTipWorld, bitTipWorld);
            if (authoredDistance > 0.025f)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.ShadowRaid] Authored drill tip marker differs from resolved bit mesh tip by {authoredDistance:0.0000}m; aligning to the visible bit mesh tip.");
            }

            if (bitTipWorld == Vector3.zero)
                return;

            Vector3 sparkWorld = GetDrillSparkContactPoint(container, out _);
            float distance = Vector3.Distance(bitTipWorld, sparkWorld);
            _loggedTipSparkAlignment = true;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Drill tip/spark alignment check: bitTip={FormatVector(bitTipWorld)}, " +
                $"authoredTip={FormatVector(authoredTipWorld)}, spark={FormatVector(sparkWorld)}, distance={distance:0.0000}m, " +
                $"rotatingBit='{(_rotatingBit != null ? GetTransformPath(_rotatingBit) : "null")}'.");
        }

        private Transform FindRendererClosestToTip()
        {
            if (_drillTip == null)
                return null;

            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            Transform best = null;
            float bestSqr = 0.18f * 0.18f;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.transform == transform)
                    continue;

                float sqr = (renderer.bounds.center - _drillTip.position).sqrMagnitude;
                if (sqr >= bestSqr)
                    continue;

                bestSqr = sqr;
                best = renderer.transform;
            }

            return best;
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

        private static string FormatVector(Vector3 value)
        {
            return $"{value.x:0.00},{value.y:0.00},{value.z:0.00}";
        }

        private static string FormatDebugVector(Vector3 value)
        {
            return $"{value.x:0.000},{value.y:0.000},{value.z:0.000}";
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

        private static bool RendererHasScreenMaterial(Renderer renderer)
        {
            if (renderer == null)
                return false;

            Material[] materials = renderer.sharedMaterials;
            if (materials == null)
                return false;

            for (int i = 0; i < materials.Length; i++)
            {
                if (LooksLikeScreenMaterial(materials[i]))
                    return true;
            }

            return false;
        }

        private static bool BlacksiteDebugLogsEnabled()
        {
            return ContractsBootstrap.ShadowRaidDebugEnabled != null && ContractsBootstrap.ShadowRaidDebugEnabled.Value;
        }

        private void PositionSparkEmitterAtContact(Transform emitter, BreachContainerController container)
        {
            if (emitter == null || container == null)
                return;

            Vector3 contact = GetSparkEmitterPoint(container, out Vector3 outward);
            bool jumped = _hasLastSparkContact && (contact - _lastSparkContact).sqrMagnitude > 0.0225f;
            _lastSparkContact = contact;
            _hasLastSparkContact = true;
            emitter.position = contact;
            if (outward.sqrMagnitude > 0.001f)
            {
                Vector3 sprayDirection = (outward * 0.95f + Vector3.down * 0.16f).normalized;
                emitter.rotation = Quaternion.LookRotation(sprayDirection, Vector3.up);
            }

            if (jumped)
                ClearSparkParticles();
        }

        private Vector3 GetDrillSparkContactPoint(BreachContainerController container, out Vector3 outward)
        {
            outward = ResolveContainerOutward(container);
            if (container == null)
                return Vector3.zero;

            Transform anchor = container.LockAnchor != null ? container.LockAnchor : container.transform;
            return anchor.position + outward * SparkContactOutwardOffset;
        }

        private Vector3 GetSparkEmitterPoint(BreachContainerController container, out Vector3 outward)
        {
            Vector3 contact = GetDrillSparkContactPoint(container, out outward);
            if (_attached.Value && !_completed.Value && TryGetResolvedBitTipWorld(out Vector3 visibleBitTip))
                return visibleBitTip + outward * 0.006f;

            return contact;
        }

        private Vector3 ResolveRotatingBitLocalSpinAxis(Transform bit)
        {
            if (bit == null)
                return Vector3.forward;

            if (_rotatingBitSpinAxisResolved)
                return _rotatingBitSpinAxis;

            _rotatingBitSpinAxis = ResolveMeshLongLocalAxis(bit);
            _rotatingBitSpinAxisResolved = true;
            return _rotatingBitSpinAxis;
        }

        private static Vector3 ResolveMeshLongLocalAxis(Transform bit)
        {
            Bounds bounds;
            MeshFilter filter = bit != null ? bit.GetComponentInChildren<MeshFilter>(true) : null;
            if (filter != null && filter.sharedMesh != null)
            {
                bounds = filter.sharedMesh.bounds;
                return GetLongestBoundsAxis(bounds);
            }

            SkinnedMeshRenderer skinned = bit != null ? bit.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            if (skinned != null)
            {
                bounds = skinned.localBounds;
                return GetLongestBoundsAxis(bounds);
            }

            return Vector3.forward;
        }

        private static Vector3 GetLongestBoundsAxis(Bounds bounds)
        {
            Vector3 size = bounds.size;
            if (size.x >= size.y && size.x >= size.z)
                return Vector3.right;
            if (size.y >= size.x && size.y >= size.z)
                return Vector3.up;
            return Vector3.forward;
        }

        private static Vector3 GetBoundsTipOnAxis(Bounds bounds, Vector3 axis)
        {
            axis = DominantCardinalAxis(axis);
            Vector3 extents = bounds.extents;
            float reach = Mathf.Abs(axis.x) * extents.x
                          + Mathf.Abs(axis.y) * extents.y
                          + Mathf.Abs(axis.z) * extents.z;
            return bounds.center + axis * reach;
        }

        private static Vector3 DominantCardinalAxis(Vector3 axis)
        {
            if (axis.sqrMagnitude < 0.001f)
                return Vector3.forward;

            axis.Normalize();
            float ax = Mathf.Abs(axis.x);
            float ay = Mathf.Abs(axis.y);
            float az = Mathf.Abs(axis.z);
            if (ax >= ay && ax >= az)
                return axis.x >= 0f ? Vector3.right : Vector3.left;
            if (ay >= ax && ay >= az)
                return axis.y >= 0f ? Vector3.up : Vector3.down;
            return axis.z >= 0f ? Vector3.forward : Vector3.back;
        }

        private void ClearSparkParticles()
        {
            if (_bundledSparkSystems != null)
            {
                for (int i = 0; i < _bundledSparkSystems.Length; i++)
                {
                    ParticleSystem system = _bundledSparkSystems[i];
                    if (system != null)
                        system.Clear(true);
                }
            }

            if (_sparks != null)
                _sparks.Clear(true);
        }

        private static Vector3 ResolveContainerOutward(BreachContainerController container)
        {
            if (container == null)
                return Vector3.forward;

            Transform anchor = container.LockAnchor != null ? container.LockAnchor : container.transform;
            Vector3 outward = anchor.forward;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.001f)
            {
                outward = container.transform.forward;
                outward.y = 0f;
            }
            if (outward.sqrMagnitude < 0.001f)
                return Vector3.forward;
            return outward.normalized;
        }

        private BreachContainerController ResolveAttachedContainer()
        {
            BreachContainerController container = null;
            if (_containerNetworkId.Value != 0UL)
                container = BreachContainerController.FindByNetworkId(_containerNetworkId.Value);
            if (container != null)
                return container;

            if (_attachedContainerDirect != null && _attachedContainerDirect.gameObject != null && _attachedContainerDirect.gameObject.activeInHierarchy)
                return _attachedContainerDirect;

            float searchDistance = Mathf.Max(ContractsBootstrap.ShadowRaidDrillAttachDistance.Value + 1.25f, 3f);
            Vector3 searchPosition = _drillTip != null ? _drillTip.position : transform.position;
            container = BreachContainerController.FindNearestContainer(searchPosition, searchDistance, includeOpen: true);
            if (container != null && IsServerInstance)
            {
                _attachedContainerDirect = container;
                if (container.NetworkId != 0UL && _containerNetworkId.Value != container.NetworkId)
                    _containerNetworkId.Value = container.NetworkId;
            }

            return container;
        }

        private void CompleteOnServer()
        {
            if (_completed.Value)
                return;

            _completed.Value = true;
            _attached.Value = false;
            BreachContainerController container = ResolveAttachedContainer();
            // The attachment is cleared inside DropReusableDrillAfterCompletion, together with the
            // replicated drop pose — clearing it here would leave clients briefly detached with no
            // floor target to fall to.
            PublishDrillProgression(container != null ? container.NetworkId : _containerNetworkId.Value, completed: true);
            StopDrillLoopAudio();
            // The container plays its own door-open one-shot on every peer from StartOpenAnimationLocal
            // (via _openState / OpenContainerLocal). Playing this one as well only doubled the sound on
            // the host, so it now covers the no-container fallback alone.
            if (container == null)
                PlayDoorOpeningSound(null);

            if (container != null)
            {
                container.OpenContainerLocal();
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Drill completed. Opened container {container.NetworkId}.");
            }

            DropReusableDrillAfterCompletion(container);
            _attachedContainerDirect = null;
            _containerNetworkId.Value = 0UL;
            _finishServerTime.Value = 0f;
            _completed.Value = false;
        }

        /// <summary>
        /// Credits the crew member who carried the drill into place; a drill nobody was seen
        /// holding (spawned straight onto the container) pays the whole crew instead.
        /// </summary>
        private void PublishDrillProgression(ulong containerNetworkId, bool completed)
        {
            ulong attributedClientId = _attachedByClientId != ulong.MaxValue ? _attachedByClientId : _lastHolderClientId;
            if (attributedClientId == ulong.MaxValue)
            {
                if (completed)
                    ContractProgressionEvents.PublishShadowRaidDrillCompletedToAll(containerNetworkId, _drillDurationSeconds);
                else
                    ContractProgressionEvents.PublishShadowRaidDrillAttachedToAll(containerNetworkId);
                return;
            }

            PublishDrillProgressionFor(attributedClientId, containerNetworkId, completed);
        }

        private void PublishDrillProgressionFor(ulong playerClientId, ulong containerNetworkId, bool completed)
        {
            if (completed)
                ContractProgressionEvents.PublishShadowRaidDrillCompleted(playerClientId, containerNetworkId, _drillDurationSeconds);
            else
                ContractProgressionEvents.PublishShadowRaidDrillAttached(playerClientId, containerNetworkId);
        }

        private const float DrillLoopFadeOutSeconds = 0.22f;

        /// <summary>
        /// Per-frame volume ramp for the drill loop. A hard <c>Stop()</c> on a mid-cycle motor loop
        /// clicks; this fades the loop out over <see cref="DrillLoopFadeOutSeconds"/> and only stops
        /// the source once the ramp reaches zero. Starting the loop again cancels a pending fade.
        /// </summary>
        private void TickDrillLoopAudio(bool running)
        {
            if (_audio == null)
                return;

            if (running)
            {
                if (_loopFadeOutRemaining > 0f)
                {
                    _loopFadeOutRemaining = 0f;
                    _audio.volume = _audioBaseVolume;
                }

                if (_audio.clip != null && !_audio.isPlaying)
                {
                    _audio.volume = _audioBaseVolume;
                    _audio.Play();
                }

                return;
            }

            if (_loopFadeOutRemaining <= 0f)
            {
                if (_audio.isPlaying)
                    StopDrillLoopAudio();
                return;
            }

            _loopFadeOutRemaining = Mathf.Max(0f, _loopFadeOutRemaining - Time.deltaTime);
            if (_loopFadeOutRemaining <= 0f)
            {
                _audio.Stop();
                _audio.volume = _audioBaseVolume;
                return;
            }

            _audio.volume = _loopFadeOutStartVolume * (_loopFadeOutRemaining / DrillLoopFadeOutSeconds);
        }

        private void StopDrillLoopAudio()
        {
            if (_audio == null || !_audio.isPlaying)
                return;

            if (_loopFadeOutRemaining > 0f)
                return;

            _loopFadeOutStartVolume = _audio.volume > 0f ? _audio.volume : _audioBaseVolume;
            _loopFadeOutRemaining = DrillLoopFadeOutSeconds;
        }

        private void PlayDoorOpeningSound(BreachContainerController container)
        {
            if (_providedDoorOpenClip == null)
            {
                EnsureProvidedDoorOpenLoading();
                return;
            }

            Vector3 soundPosition = transform.position;
            if (container != null)
            {
                Transform anchor = container.LockAnchor != null ? container.LockAnchor : container.transform;
                soundPosition = anchor != null ? anchor.position : container.transform.position;
            }

            GameObject soundObject = new GameObject("BreachDoorOpeningAudio");
            soundObject.transform.position = soundPosition;
            AudioSource source = soundObject.AddComponent<AudioSource>();
            source.clip = _providedDoorOpenClip;
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 2f;
            source.maxDistance = 30f;
            source.volume = 0.78f;
            source.loop = false;
            source.pitch = 1f;
            source.Play();
            Destroy(soundObject, Mathf.Max(0.25f, _providedDoorOpenClip.length + 0.25f));
        }

        private void DropReusableDrillAfterCompletion(BreachContainerController container)
        {
            Vector3 dropPosition = transform.position + Vector3.up * 0.15f;
            Vector3 floorPosition = dropPosition;
            Quaternion dropRotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            if (container != null)
            {
                Vector3 outward = ResolveContainerOutward(container);
                Transform anchor = container.LockAnchor != null ? container.LockAnchor : container.transform;
                ResolveCompletionDropPositions(anchor.position, outward, out dropPosition, out floorPosition);
                dropRotation = Quaternion.LookRotation(outward, Vector3.up);
            }
            else
            {
                ResolveCompletionDropPositions(transform.position, transform.forward, out dropPosition, out floorPosition);
            }

            // Replicate the drop before anything clears the attachment, so a client never sees the
            // attachment go away without the pose that replaces it. This also clears the attachment.
            container?.PublishDrillCompletionServer(
                SelfNetworkId,
                dropPosition,
                floorPosition,
                dropRotation.eulerAngles.y);

            transform.position = dropPosition;
            transform.rotation = dropRotation;
            _lastReleasedAt = Time.time;
            RestoreLoosePickupState(markGrounded: false);
            SetGrabbableState(true);

            if (_prop != null)
            {
                _prop.hasHitGround = false;
                _prop.reachedFloorTarget = false;
                _prop.fallTime = 0f;
                _prop.startFallingPosition = ToLocalDropPosition(dropPosition);
                _prop.targetFloorPosition = ToLocalDropPosition(floorPosition);
                _prop.EnablePhysics(enable: true);
                if (_prop.propBody != null)
                {
                    _prop.propBody.isKinematic = false;
                    _prop.propBody.useGravity = true;
                    _prop.propBody.constraints = RigidbodyConstraints.FreezeRotation;
                    _prop.propBody.velocity = Vector3.down * 0.15f;
                    _prop.propBody.angularVelocity = Vector3.zero;
                }
                _wasGroundedLastFrame = false;
            }

            LogLoosePickupState("completion-drop");
        }

        private void ResolveCompletionDropPositions(Vector3 origin, Vector3 outward, out Vector3 dropPosition, out Vector3 floorPosition)
        {
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.001f)
                outward = transform.forward;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.001f)
                outward = Vector3.forward;
            outward.Normalize();

            float rootOffset = GetLooseRootGroundOffset();
            Vector3 floorProbeBase = origin + outward * 1.15f;
            Vector3 rayStart = floorProbeBase + Vector3.up * 1.8f;
            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, rootOffset + 6f, ItemFloorRaycastMask, QueryTriggerInteraction.Ignore))
            {
                floorPosition = hit.point + Vector3.up * rootOffset;
                dropPosition = floorPosition + Vector3.up * 0.42f;
                return;
            }

            floorPosition = floorProbeBase + Vector3.up * rootOffset;
            dropPosition = floorPosition + Vector3.up * 0.42f;
        }

        private Vector3 ToLocalDropPosition(Vector3 worldPosition)
        {
            return transform.parent != null
                ? transform.parent.InverseTransformPoint(worldPosition)
                : worldPosition;
        }

        private float GetLooseRootGroundOffset()
        {
            float itemOffset = Mathf.Max(BreachDrillItemRegistry.GroundVerticalOffset, _prop?.itemProperties != null ? _prop.itemProperties.verticalOffset : 0f);
            if (!TryCalculateVisibleGroundOffset(out float visualOffset))
                return itemOffset;

            return Mathf.Clamp(visualOffset, 0.25f, Mathf.Max(itemOffset, visualOffset));
        }

        private bool TryCalculateVisibleGroundOffset(out float offset)
        {
            offset = 0f;
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            bool initialized = false;
            Bounds bounds = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;
                if (renderer.GetComponentInParent<ScanNodeProperties>() != null)
                    continue;
                if (renderer.GetComponentInParent<ParticleSystem>() != null)
                    continue;
                if (renderer.name.IndexOf("Screen", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                if (!initialized)
                {
                    bounds = renderer.bounds;
                    initialized = true;
                }
                else
                    bounds.Encapsulate(renderer.bounds);
            }

            if (!initialized)
                return false;

            offset = Mathf.Max(0.05f, transform.position.y - bounds.min.y + 0.03f);
            return true;
        }

        private void EnsureScreenDisplay()
        {
            if (_screenDisplayAttempted)
                return;

            _screenDisplayAttempted = true;
            ResolveScreenRendererOnce();
            if (_screenRenderer == null)
            {
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.ShadowRaid] Drill has no physical screen renderer; countdown screen disabled.");
                return;
            }

            _screen = new BreachDrillScreenDisplay(_screenRenderer);
            if (!_screen.IsValid)
            {
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.ShadowRaid] Drill screen renderer has no screen material slot; countdown screen disabled.");
                _screen = null;
                return;
            }

            // Binding the screen swaps a material on the drill hierarchy. Drop the cached renderer list
            // and run the repair on the next frame so the screen renderer is covered by it.
            _visualRenderers = null;
            _nextVisualRepairAt = 0f;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Drill physical screen bound to '{GetTransformPath(_screenRenderer.transform)}'.");
        }

        private void UpdateScreenLocal()
        {
            if (_screen == null)
                return;

            bool powered = _attached.Value && !_completed.Value;
            string text;
            float progress;
            if (_completed.Value)
            {
                text = "DONE";
                progress = 1f;
            }
            else if (!_attached.Value)
            {
                text = "READY";
                progress = 0f;
            }
            else
            {
                float remaining = RemainingSeconds;
                int seconds = Mathf.CeilToInt(remaining);
                if (seconds != _lastScreenSeconds || _cachedScreenSecondsText == null)
                {
                    _lastScreenSeconds = seconds;
                    _cachedScreenSecondsText = $"{seconds}s";
                }

                text = _cachedScreenSecondsText;
                progress = 1f - Mathf.Clamp01(remaining / Mathf.Max(1f, _drillDurationSeconds));
            }

            _screen.Tick(powered, text, progress);
            MaybeLogScreenCountdown(powered, text, progress);
        }

        private void MaybeLogScreenCountdown(bool powered, string text, float progress)
        {
            if (!powered)
            {
                _lastLoggedScreenCountdownBucket = int.MinValue;
                return;
            }

            int seconds = Mathf.CeilToInt(RemainingSeconds);
            int bucket = seconds / 15;
            if (_lastLoggedScreenCountdownBucket == bucket)
                return;

            _lastLoggedScreenCountdownBucket = bucket;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Drill screen countdown active: text='{text}', remaining={RemainingSeconds:0.0}s, progress={progress:0.00}.");
        }

        private static AudioClip GetOrCreateDrillLoopClip()
        {
            if (_generatedDrillLoopClip != null)
                return _generatedDrillLoopClip;

            const int sampleRate = 22050;
            const int seconds = 1;
            int frames = sampleRate * seconds;
            float[] samples = new float[frames];
            uint noise = 0x1234567u;
            for (int i = 0; i < frames; i++)
            {
                float t = i / (float)sampleRate;
                noise = noise * 1664525u + 1013904223u;
                float grit = ((noise >> 9) / (float)(1u << 23)) * 2f - 1f;
                float motor = Mathf.Sin(2f * Mathf.PI * 74f * t) * 0.32f
                              + Mathf.Sin(2f * Mathf.PI * 148f * t) * 0.18f
                              + Mathf.Sin(2f * Mathf.PI * 296f * t) * 0.08f;
                float chatter = Mathf.Sin(2f * Mathf.PI * 19f * t) * 0.18f;
                samples[i] = Mathf.Clamp((motor + grit * 0.22f) * (0.78f + chatter), -0.85f, 0.85f);
            }

            _generatedDrillLoopClip = AudioClip.Create("LGU_BreachDrillLoop", frames, 1, sampleRate, stream: false);
            _generatedDrillLoopClip.SetData(samples, 0);
            return _generatedDrillLoopClip;
        }

        private void UpdateScanNode()
        {
            if (_scanNode == null)
                return;

            if (!_scanNodeStaticsApplied)
            {
                _scanNodeStaticsApplied = true;
                _scanNode.headerText = _displayName;
                _scanNode.maxRange = 30;
                _scanNode.minRange = 1;
                _scanNode.requiresLineOfSight = false;
                _scanNode.nodeType = 0;
                _scanNode.scrapValue = 0;
            }

            // Only build the subText string when the displayed value actually changes; the drilling
            // line changes once per whole second, not once per frame.
            bool attached = _attached.Value;
            bool completed = _completed.Value;
            int seconds = attached && !completed ? Mathf.CeilToInt(RemainingSeconds) : int.MinValue;
            if (attached == _lastScanNodeAttached
                && completed == _lastScanNodeCompleted
                && seconds == _lastScanNodeSeconds)
            {
                return;
            }

            _lastScanNodeAttached = attached;
            _lastScanNodeCompleted = completed;
            _lastScanNodeSeconds = seconds;
            _scanNode.subText = completed
                ? "Shadow Raid complete"
                : (attached ? $"drilling {seconds}s" : "drop near Shadow Raid container");
        }

        /// <summary>
        /// Scales the carried visual to CarriedVisualMaxDimensionMeters about the centred pickup
        /// root. The carry postfix uses each view's animated skeleton to position the drill
        /// between the wrists. Dropping or attaching restores the full-size mesh before alignment.
        /// </summary>
        private void UpdateCarriedRepresentation()
        {
            Transform visualRoot = ResolveVisualRoot();
            if (visualRoot == null)
                return;

            if (!_visualRootBaseLocalScaleCaptured)
            {
                _visualRootBaseLocalScale = visualRoot.localScale;
                _visualRootBaseLocalPosition = visualRoot.localPosition;
                _visualRootBaseLocalScaleCaptured = true;
            }

            bool carried = _prop != null
                           && !_attached.Value
                           && !_completed.Value
                           && _prop.playerHeldBy != null
                           && (_prop.isHeld || _prop.isPocketed || _prop.parentObject != null);
            if (carried == _carriedRepresentationApplied)
                return;

            _carriedRepresentationApplied = carried;
            float factor = carried ? ResolveCarriedScaleFactor() : 1f;
            // Scale about the pickup root, not about the visual root's own origin: the registry
            // centred the mesh by offsetting the visual root's local position by minus the mesh
            // centre, so that offset has to shrink with the mesh or the carried body drifts off
            // the hand anchor by (1 - factor) times the original centre offset.
            visualRoot.localScale = _visualRootBaseLocalScale * factor;
            visualRoot.localPosition = _visualRootBaseLocalPosition * factor;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Drill carried representation {(carried ? "applied" : "restored")}: scale={factor:0.000}, visualRoot='{visualRoot.name}', fullSize={FormatVector(BreachDrillItemRegistry.VisualSize)}.");
        }

        private Transform ResolveVisualRoot()
        {
            if (_visualRoot != null)
                return _visualRoot;

            _visualRoot = transform.Find(BreachDrillItemRegistry.VisualRootName);
            if (_visualRoot == null && _bodyVisual != null)
            {
                // Fall back to the body mesh's topmost ancestor under the pickup root.
                Transform cursor = _bodyVisual;
                while (cursor.parent != null && cursor.parent != transform)
                    cursor = cursor.parent;
                _visualRoot = cursor;
            }

            return _visualRoot;
        }

        private static float ResolveCarriedScaleFactor()
        {
            Vector3 size = BreachDrillItemRegistry.VisualSize;
            float largest = Mathf.Max(size.x, size.y, size.z);
            if (largest <= 0.001f)
                return 1f;
            return Mathf.Clamp(CarriedVisualMaxDimensionMeters / largest, 0.05f, 1f);
        }

        private void HandleDropTransition()
        {
            bool held = HeldByPlayer();
            if (held && !_wasHeldLastFrame)
                _heldStateLogAt = Time.time + 0.5f;
            else if (!held)
                _heldStateLogAt = 0f;

            if (held && _heldStateLogAt > 0f && Time.time >= _heldStateLogAt)
            {
                _heldStateLogAt = 0f;
                LogHeldState();
            }

            if (_wasHeldLastFrame && !held && !_attached.Value && !_completed.Value)
            {
                _lastReleasedAt = Time.time;
                RestoreLoosePickupState(markGrounded: false);
                _prop?.EnablePhysics(enable: true);
                if (_prop?.propBody != null)
                {
                    _prop.propBody.isKinematic = false;
                    _prop.propBody.useGravity = true;
                    _prop.propBody.constraints = RigidbodyConstraints.FreezeRotation;
                    _prop.propBody.velocity = Vector3.zero;
                    _prop.propBody.angularVelocity = Vector3.zero;
                }
                LogLoosePickupState("drop");
            }

            _wasHeldLastFrame = held;
        }

        private void StabilizeLooseDrillBody()
        {
            if (_prop?.propBody == null || HeldByPlayer())
                return;

            Rigidbody body = _prop.propBody;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            if (body.isKinematic)
                return;
            if (_prop.hasHitGround && body.angularVelocity.sqrMagnitude > 0.0001f)
                body.angularVelocity = Vector3.zero;
        }

        private void SetLooseBodyKinematic()
        {
            if (_prop?.propBody == null || HeldByPlayer())
                return;

            Rigidbody body = _prop.propBody;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            StopDynamicBodyMotion(body);
            body.isKinematic = true;
            body.useGravity = false;
            body.constraints = RigidbodyConstraints.FreezeRotation;
        }

        private static void StopDynamicBodyMotion(Rigidbody body)
        {
            if (body == null || body.isKinematic)
                return;

            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        private void EnsureLooseRootFloorClearance()
        {
            if (_prop == null || HeldByPlayer() || !_prop.hasHitGround)
                return;

            float rootOffset = GetLooseRootGroundOffset();
            Vector3 rayStart = transform.position + Vector3.up * 0.2f;
            if (!Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, rootOffset + 2.5f, ItemFloorRaycastMask, QueryTriggerInteraction.Ignore))
                return;

            float desiredY = hit.point.y + rootOffset;
            bool belowFloorClearance = transform.position.y < desiredY - 0.05f;
            bool recentlyReleasedAndFloating = Time.time - _lastReleasedAt < 4f && transform.position.y > desiredY + 0.22f;
            if (!belowFloorClearance && !recentlyReleasedAndFloating)
                return;

            Vector3 position = transform.position;
            position.y = desiredY;
            transform.position = position;
            _prop.targetFloorPosition = transform.parent != null
                ? transform.parent.InverseTransformPoint(position)
                : position;
            _prop.startFallingPosition = _prop.targetFloorPosition;
            _prop.fallTime = 1f;
            _prop.reachedFloorTarget = true;
        }

        private bool HeldByPlayer()
        {
            return _prop != null && (_prop.isHeld || _prop.isPocketed || _prop.playerHeldBy != null || _prop.parentObject != null);
        }

        private void SetGrabbableState(bool enabled)
        {
            if (_prop == null)
                return;

            // Update() calls this every frame while the drill is loose. Only touch the prop and the
            // collider set when the requested state actually changed.
            bool unchanged = _hasLastGrabbableState
                             && _lastGrabbableStateApplied == enabled
                             && _prop.grabbable == enabled
                             && !_prop.grabbableToEnemies;
            if (unchanged && (!enabled || _pickupColliderStateApplied))
                return;

            _prop.grabbable = enabled;
            _prop.grabbableToEnemies = false;
            _lastGrabbableStateApplied = enabled;
            _hasLastGrabbableState = true;
            if (enabled && !HeldByPlayer())
                EnsurePickupColliderState();
        }

        private void RestoreLoosePickupState(bool markGrounded)
        {
            if (_prop == null)
                return;

            _prop.grabbable = true;
            _prop.grabbableToEnemies = false;
            _prop.isHeld = false;
            _prop.isPocketed = false;
            _prop.heldByPlayerOnServer = false;
            _prop.playerHeldBy = null;
            _prop.parentObject = null;
            _prop.scrapValue = 0;
            if (_prop.itemProperties != null)
                _prop.itemProperties.verticalOffset = BreachDrillItemRegistry.GroundVerticalOffset;
            if (markGrounded)
            {
                _prop.hasHitGround = true;
                _prop.reachedFloorTarget = true;
                _prop.fallTime = Mathf.Max(_prop.fallTime, 1f);
            }

            EnsurePickupColliderState(force: true);
        }

        private void EnsurePickupColliderState(bool force = false)
        {
            if (_prop == null)
                return;

            // The root collider set is fixed once the runtime visual exists, and the layer/tag writes
            // below are idempotent, so re-run only on a real transition or when the cache went stale.
            if (!force
                && _pickupColliderStateApplied
                && _cachedRootColliders != null
                && !ContainsDestroyedCollider(_cachedRootColliders)
                && _prop.propColliders != null
                && _prop.propColliders.Length == _lastPickupColliderCount)
            {
                return;
            }

            Collider[] rootColliders = _cachedRootColliders;
            if (rootColliders == null || ContainsDestroyedCollider(rootColliders))
            {
                rootColliders = GetComponents<Collider>();
                _cachedRootColliders = rootColliders;
            }

            int count = 0;
            for (int i = 0; i < rootColliders.Length; i++)
            {
                Collider collider = rootColliders[i];
                if (collider == null || collider.isTrigger)
                    continue;

                collider.enabled = true;
                collider.gameObject.layer = ResolvePropsLayer();
                SetTagSafe(collider.gameObject, "PhysicsProp");
                count++;
            }

            if (count <= 0)
                return;

            Collider[] propColliders = new Collider[count];
            int index = 0;
            for (int i = 0; i < rootColliders.Length; i++)
            {
                Collider collider = rootColliders[i];
                if (collider == null || collider.isTrigger)
                    continue;

                propColliders[index++] = collider;
            }
            _prop.propColliders = propColliders;
            _lastPickupColliderCount = count;
            _pickupColliderStateApplied = true;
        }

        private static bool ContainsDestroyedCollider(Collider[] colliders)
        {
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] == null)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// #455 diagnostic. One Info line shortly after the drill is picked up, capturing everything
        /// that decides whether the carried mesh can be seen: what vanilla parented it to, where that
        /// anchor sits relative to the holder's camera, and each renderer's state. The carried drill
        /// has been reported invisible in first person while vanilla reports the item as held (input
        /// watchdog: <c>held='Shadow Raid Drill' twoHandedItem=True</c>); this is the evidence the fix
        /// is built on. Reads renderer bounds, so it must stay a one-shot rather than a poll.
        /// </summary>
        private void LogHeldState()
        {
            if (_prop == null || _heldStateLogsEmitted >= MaxHeldStateLogs)
                return;

            _heldStateLogsEmitted++;
            PlayerControllerB holder = _prop.playerHeldBy;
            Transform anchor = _prop.parentObject;
            string anchorKind = "none";
            if (anchor != null)
            {
                if (holder != null && anchor == holder.localItemHolder)
                    anchorKind = "localItemHolder";
                else if (holder != null && anchor == holder.serverItemHolder)
                    anchorKind = "serverItemHolder";
                else
                    anchorKind = anchor.name;
            }

            Camera cam = holder != null ? holder.gameplayCamera : null;
            string cameraRelation = "n/a";
            if (cam != null)
            {
                Vector3 toItem = transform.position - cam.transform.position;
                float forwardDot = toItem.sqrMagnitude > 0.0001f ? Vector3.Dot(cam.transform.forward, toItem.normalized) : 0f;
                cameraRelation = $"dist={toItem.magnitude:0.00}, forwardDot={forwardDot:0.00}, viewport={FormatVector(cam.WorldToViewportPoint(transform.position))}, nearClip={cam.nearClipPlane:0.000}";
            }

            var sb = new StringBuilder(768);
            sb.Append("[MoonContracts.ShadowRaid] Drill held state: holder=")
              .Append(holder != null ? holder.playerUsername : "null")
              .Append(holder != null && holder.IsOwner ? " (local)" : string.Empty)
              .Append(", isHeld=").Append(_prop.isHeld)
              .Append(", isPocketed=").Append(_prop.isPocketed)
              .Append(", heldServer=").Append(_prop.heldByPlayerOnServer)
              .Append(", anchor=").Append(anchorKind)
              .Append(", anchorPos=").Append(anchor != null ? FormatVector(anchor.position) : "null")
              .Append(", itemPos=").Append(FormatVector(transform.position))
              .Append(", parent=").Append(transform.parent != null ? transform.parent.name : "null")
              .Append(", lossyScale=").Append(FormatVector(transform.lossyScale))
              .Append(", layer=").Append(gameObject.layer)
              .Append(", activeInHierarchy=").Append(gameObject.activeInHierarchy)
              .Append(", positionOffset=").Append(_prop.itemProperties != null ? FormatVector(_prop.itemProperties.positionOffset) : "null")
              .Append(", rotationOffset=").Append(_prop.itemProperties != null ? FormatVector(_prop.itemProperties.rotationOffset) : "null")
              .Append(", bodyVisualActive=").Append(_bodyVisual != null && _bodyVisual.gameObject.activeInHierarchy)
              .Append(", camera: ").Append(cameraRelation);

            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            sb.Append(", renderers=").Append(renderers.Length);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;

                Material material = renderer.sharedMaterial;
                sb.Append(" | ").Append(renderer.name)
                  .Append(": enabled=").Append(renderer.enabled)
                  .Append(", active=").Append(renderer.gameObject.activeInHierarchy)
                  .Append(", forceOff=").Append(renderer.forceRenderingOff)
                  .Append(", isVisible=").Append(renderer.isVisible)
                  .Append(", layer=").Append(renderer.gameObject.layer)
                  .Append(", boundsCenter=").Append(FormatVector(renderer.bounds.center))
                  .Append(", boundsSize=").Append(FormatVector(renderer.bounds.size))
                  .Append(", material=").Append(material != null ? material.name : "null")
                  .Append(", shader=").Append(material != null && material.shader != null ? material.shader.name : "null");
            }

            ContractsBootstrap.Log?.LogInfo(sb.ToString());
            HeldItemFrameDiagnostics.Log("[MoonContracts.ShadowRaid] Drill", _prop, ResolveVisualRoot(), "drill");
        }

        private void LogLoosePickupState(string reason)
        {
            if (_prop == null)
                return;

            Collider pickupCollider = null;
            Collider[] colliders = GetComponents<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || collider.isTrigger)
                    continue;

                pickupCollider = collider;
                break;
            }

            BoxCollider box = pickupCollider as BoxCollider;
            string colliderShape = box != null
                ? $", colliderCenter={box.center.x:0.00},{box.center.y:0.00},{box.center.z:0.00}, colliderSize={box.size.x:0.00},{box.size.y:0.00},{box.size.z:0.00}"
                : string.Empty;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Drill loose pickup restored after {reason}: pos={transform.position.x:0.00},{transform.position.y:0.00},{transform.position.z:0.00}, grabbable={_prop.grabbable}, isHeld={_prop.isHeld}, isPocketed={_prop.isPocketed}, heldServer={_prop.heldByPlayerOnServer}, playerHeld={_prop.playerHeldBy != null}, parent={_prop.parentObject?.name ?? "null"}, hasHitGround={_prop.hasHitGround}, collider={(pickupCollider != null ? pickupCollider.name : "null")}, colliderEnabled={(pickupCollider != null && pickupCollider.enabled)}, colliderTag={(pickupCollider != null ? pickupCollider.tag : "null")}, colliderLayer={(pickupCollider != null ? pickupCollider.gameObject.layer : -1)}{colliderShape}.");
        }

        private static int ResolvePropsLayer()
        {
            int props = LayerMask.NameToLayer("Props");
            if (props >= 0)
                return props;

            int interactable = LayerMask.NameToLayer("InteractableObject");
            return interactable >= 0 ? interactable : 6;
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
                // The tag is created by the base game; leave the current tag if another runtime strips it.
            }
        }
    }
}
