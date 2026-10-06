using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core.Compat;
namespace Y4NGZCompany.Escalations.Congregation
{
    public sealed class SeraphimEnemyAI : EnemyAI
    {
        private const int AnimAwakening = 0;
        private const int AnimIdle = 1;
        private const int AnimIdleFast = 2;
        private const int AnimAttackStart = 3;
        private const int AnimAttackLoop = 4;
        private const int AnimAttackEnd = 5;
        private const float AttackStartLeadSeconds = 0.55f;
        private const float RiseHandoffSeconds = 0.3f;
        private const float AttackEndSettleSeconds = 0.85f;
        private const float ExecutionHoldSeconds = 3f;
        private const float ExecutionImpactLeadSeconds = 0.12f;
        // The routed kill lands on the victim's client, so isPlayerDead only becomes true on the
        // server one round trip later. Ignore a just-executed player for this long so the seraphim
        // never re-acquires a target it has already killed.
        private const float PendingExecutionSuppressSeconds = 4f;
        // One confirmation window per attempt; two attempts stay inside PendingExecutionSuppressSeconds
        // so the fallback always runs before the suppression lapses on its own.
        private const float ExecutionConfirmSeconds = 1.75f;
        private const float ExecutionLiftMeters = 8f;
        private const float CultistExecutionCooldownSeconds = 0.9f;
        private const float BeamWidth = 0.34f;
        private const float BeamPulseWidth = 0.12f;
        private const float HolyEffectLifetimeSeconds = 4.25f;
        private const float BloodEffectLifetimeSeconds = 5.5f;
        private const float VisualScaleMultiplier = 4f;
        private const float HoverHeightMultiplier = 1.15f;
        private const float FlyingChaseSpeed = 9.5f;
        private const float FlyingReturnSpeed = 4.5f;
        private const float IdleDriftRadius = 5.25f;
        private const float HoverBobMeters = 1.1f;
        private const float FlightGroundProbeHeight = 160f;
        private const float FlightGroundProbeDistance = 360f;
        // Two 360 m raycasts a frame drove most of SeraphimEnemyAI.Update. A sample is reused for
        // this long within this horizontal radius: at FlyingChaseSpeed the body covers under 1 m in
        // the interval, far below the 14-55 m terrain clearance the sample feeds.
        private const float FlightGroundProbeIntervalSeconds = 0.1f;
        private const float FlightGroundProbeReuseRadius = 2f;
        // Terrain under the flight path can rise faster than the chase step climbs; the extra lift is
        // capped at this rate so the body climbs over a ridge instead of teleporting onto it.
        private const float FlightClearanceClimbSpeed = 12f;
        private const int AuraRingSegments = 96;
        // #845 M2: the orbiting aura rings return thinner and dimmer than 8fd334ae's (width 0.085,
        // emission x2.8, and HDRP/Unlit's default white _UnlitColor, which the shared glow recipe
        // never set), so they hint at the body without outshining the central eye.
        private const float AuraRingWidth = 0.04f;
        private const float AuraRingEmission = 1.1f;
        private static readonly Color AuraRingColor = new Color(0.55f, 0.46f, 0.26f, 1f);
        // Rise shake bands, measured from the local player's camera to the ritual point (the vanilla
        // Landmine.SpawnExplosion pattern, widened for a 12 m body breaking the ground).
        private const float RiseShakeBigMeters = 35f;
        private const float RiseShakeLongMeters = 90f;
        private const float RiseShakeSmallMeters = 160f;
        private const float RiseDustMaxLifetimeSeconds = 4.2f;
        private const int SeraphimHealth = 18;
        private const int ScanNodeLayer = 22;
        private const int BeamTargetKindPlayer = 0;
        private const int BeamTargetKindCultist = 1;
        // The scan header is a 12-character window of the contract HUD's scramble stream,
        // short enough for the vanilla scan box (#1336).
        private const int ScanHeaderLength = 12;

        private NavMeshAgent _agent;
        private Animator _animator;
        private AudioSource _voiceSource;
        private AudioSource _beamSource;
        private AudioSource _chantSource;
        private AudioSource _executionImpactSource;
        private LineRenderer _beamLine;
        private static Material _sharedBeamMaterial;
        private static Gradient _sharedBeamGradient;
        private static Material _sharedAuraMaterial;
        private static Gradient _sharedAuraGradient;
        private static Vector3[] _auraRingPointsXY;
        private static Vector3[] _auraRingPointsXZ;
        private static Vector3[] _auraRingPointsYZ;
        private static Material _riseDustMaterial;
        private Transform _visibilityAuraRoot;
        private Animator[] _visualAnimators = Array.Empty<Animator>();
        private Transform[] _riseAnimationRoots = Array.Empty<Transform>();
        private bool _riseRootCorrectionActive;
        private ScanNodeProperties _scanNode;
        private Light _passiveLight;
        private Light _riseLight;
        private GameObject _riseEffectObject;
        private GameObject _riseDustObject;
        private GameObject _beamImpactEffect;
        private GameObject _lastExplosionEffect;
        private GameObject _lastBloodEffect;
        private PlayerControllerB _targetPlayer;
        private CongregantAI _targetCultist;
        private Coroutine _attackRoutine;
        private Vector3 _ritualPoint;
        private Vector3 _riseStartPosition;
        private Vector3 _hoverBasePosition;
        private Quaternion _ritualRotation = Quaternion.identity;
        private double _riseStartNetworkTime;
        private float _scale = 3f;
        private float _hoverHeight = 20f;
        private float _riseDuration = 4.5f;
        private float _nextSearchTime;
        private float _nextAttackReadyTime;
        private float _nextVoiceTime;
        private int _scanHeaderTick = int.MinValue;
        private float _hoverSeed;
        private string _lockedAnimationState = string.Empty;
        private int _lockedAnimationId = -1;
        private int _lockedAnimationShortHash;
        private int _lockedAnimationFullHash;
        private bool _lockedAnimationLoops;
        private double _lockedAnimationStartedAt;
        private int _lockedAnimationRequestedFrame = -1;
        private readonly Dictionary<RuntimeAnimatorController, Dictionary<string, float>> _clipLengthsByController =
            new Dictionary<RuntimeAnimatorController, Dictionary<string, float>>();
        private readonly GroundProbeSample[] _groundProbeSamples = new GroundProbeSample[2];
        private int _groundProbeNextSlot;
        private float _scanNodeConfiguredScale = -1f;
        private static int _groundProbeMask;
        private static bool _groundProbeMaskResolved;
        private bool _serverPositionSynced;
        private bool _compositeVisualPrepared;
        private bool _visualMissingLogged;
        private bool _collisionPrepared;
        private bool _deadHandled;
        private bool _runtimeComponentFailureLogged;
        private ulong _targetClientId;
        private int _playerKillSerial;
        private int _lastAppliedPlayerKillSerial;
        private ulong _pendingExecutionClientId;
        private bool _hasPendingExecution;
        private float _pendingExecutionExpiry;
        private ulong _beamTargetId;
        private int _beamTargetKind = BeamTargetKindPlayer;
        private bool _initialized;
        private bool _rising;
        private bool _beamActive;
        private bool _hasTargetClientId;

        internal static float ResolveVisualScale()
        {
            float configured = ContractsBootstrap.CultSeraphimScale != null ? ContractsBootstrap.CultSeraphimScale.Value : 3f;
            float baseScale = Mathf.Clamp(configured, 1f, 8f);
            return Mathf.Clamp(baseScale * VisualScaleMultiplier, 8f, 18f);
        }

        internal static float ResolveHoverHeight()
        {
            float configured = ContractsBootstrap.CultSeraphimHoverHeight != null ? ContractsBootstrap.CultSeraphimHoverHeight.Value : 20f;
            return Mathf.Clamp(configured * HoverHeightMultiplier, 9f, 90f);
        }

        private static float ResolveAcquisitionRange()
        {
            return Mathf.Max(20f, ContractsBootstrap.CultSeraphimBeamRange != null ? ContractsBootstrap.CultSeraphimBeamRange.Value : 170f);
        }

        private static float ResolveAttackRange()
        {
            float configured = ContractsBootstrap.CultSeraphimAttackRange != null ? ContractsBootstrap.CultSeraphimAttackRange.Value : 42f;
            return Mathf.Clamp(configured, 8f, Mathf.Min(42f, ResolveAcquisitionRange()));
        }

        private float ResolvePlayerAttackStartRange()
        {
            float configured = ResolveAttackRange();
            float visualReachPadding = Mathf.Clamp(_scale * 0.85f, 8f, 20f);
            return Mathf.Min(ResolveAcquisitionRange(), Mathf.Max(configured, Mathf.Min(64f, configured + visualReachPadding)));
        }

        private float ResolvePlayerAttackContinueRange()
        {
            float followThroughPadding = Mathf.Clamp(_scale * 0.45f, 6f, 14f);
            return Mathf.Min(ResolveAcquisitionRange(), ResolvePlayerAttackStartRange() + followThroughPadding);
        }

        // #1336 summon frame: the host pre-warms an inactive body during Phase 3, and every peer
        // builds the rise effects one per frame after the rise starts instead of in its first frame.
        private const double SummonStepWarnMilliseconds = 10.0;
        private static readonly SeraphimRiseEffectStage[] RiseEffectFlushBuffer =
            new SeraphimRiseEffectStage[RiseEffectSchedule.StageCount];
        // This peer's pre-warmed rise effects, inactive until the next PlayRiseEffectsLocal adopts them.
        private static GameObject _pooledRiseEffect;
        private static GameObject _pooledRiseDust;
        private static float _pooledRiseDustRadius;
        private static float _pooledRiseDustEmitSeconds;
        private readonly RiseEffectSchedule _riseEffectSchedule = new RiseEffectSchedule();
        // An adopted effect waits inactive in _riseEffectObject/_riseDustObject, which despawn
        // already destroys, until its stage activates it.
        private bool _riseEffectAdopted;
        private bool _riseDustAdopted;
        // Animators configured on the inactive pre-warmed body bind (Rebind + Update(0)) in the
        // first EnsureRuntimeComponents after activation, where an instantiated body binds them.
        private bool _visualAnimatorRebindPending;
        // #1336 S6: the central eye. Main_eye gazes along its local +Y and, at the rest pose, along
        // the model root's -Z; null on the legacy fullmesh, which keeps the root-offset beam origin.
        private const string MainEyeBonePath = "Ophanim/Root/Ophaim_main_eye/Main_eye";
        private static readonly Vector3 MainEyeLocalGaze = new Vector3(
            SeraphimEyeAim.MainEyeLocalGazeAxis.X, SeraphimEyeAim.MainEyeLocalGazeAxis.Y, SeraphimEyeAim.MainEyeLocalGazeAxis.Z);
        private Transform _eyeBone;
        // The attack target the eye looks at, per peer: PlayBeamCue, BeginBeam, ExplodeBeam and the
        // late-join resync set it; leaving AttackStart/AttackLoop and death clear it.
        private int _aimTargetKind = BeamTargetKindPlayer;
        private ulong _aimTargetId;
        private bool _hasAimTarget;
        private SeraphimEyeAimMode _eyeAimMode;
        private Vector3 _eyeGaze;
        private bool _eyeGazeValid;
        private SeraphimEyeVector _eyeTravelVelocity;
        private Vector3 _eyeLastRootPosition;
        private bool _eyeHasRootPosition;
        private Quaternion _eyeAuthoredLocalRotation;
        private Quaternion _eyeWrittenLocalRotation;
        private bool _eyeRotationWritten;

        internal void InitializeServer(Vector3 ritualPoint, Quaternion ritualRotation)
        {
            _ritualPoint = ritualPoint;
            _ritualRotation = ritualRotation;
            _scale = ResolveVisualScale();
            _hoverHeight = ResolveHoverHeight();
            _riseDuration = ResolveRiseDuration();
            _riseStartPosition = ritualPoint - Vector3.up * Mathf.Max(7f, _scale * 1.05f);
            _hoverBasePosition = ritualPoint + Vector3.up * _hoverHeight;
            _riseStartNetworkTime = GetNetworkTime();
            _hoverSeed = UnityEngine.Random.Range(0f, 100f);
            _targetPlayer = null;
            _targetCultist = null;
            targetPlayer = null;
            _targetClientId = 0UL;
            _hasTargetClientId = false;
            _beamTargetId = 0UL;
            _beamTargetKind = BeamTargetKindPlayer;
            ClearAimTarget();
            _initialized = true;
            _rising = true;
            // Every peer drives the rise from the shared start time; the flag keeps vanilla
            // EnemyAI.Update/DoAIInterval from syncing or smoothing the transform meanwhile.
            inSpecialAnimation = true;
            _serverPositionSynced = false;
            _deadHandled = false;
            enemyHP = SeraphimHealth;
            isEnemyDead = false;
            isOutside = true;
            ventAnimationFinished = true;
            transform.position = _riseStartPosition;
            transform.localScale = Vector3.one * _scale;
            EnsureRuntimeComponentsSafe();
            // After the build: the sidecar's facing offset needs its eye bone.
            transform.rotation = ToRootRotation(ritualRotation);
            StopAgentMotion();
            PlayAnimationLocal(AnimAwakening);
            // The host runs this, not InitializeClientRpc, so its own presentation starts here.
            PlayGlobalChantLocal();
            PlayRiseEffectsLocal();
            _nextSearchTime = Time.time + 0.15f;
            _nextAttackReadyTime = Time.time + _riseDuration + 0.75f;
            _nextVoiceTime = Time.time + Mathf.Max(6f, ContractsBootstrap.CultSeraphimPeriodicVoiceSeconds.Value) * UnityEngine.Random.Range(0.65f, 1.05f);
        }

        internal static float ResolveRiseDuration()
        {
            return Mathf.Clamp(ContractsBootstrap.CultSeraphimRiseDurationSeconds.Value, 1f, 12f);
        }

        /// <summary>Host pre-warm (#1336): builds this inactive, unspawned body's runtime components
        /// during Phase 3 from the values InitializeServer sets again at the summon, so the summon
        /// frame only activates and spawns it. The animators bind after activation.</summary>
        internal void PrepareDormantServer()
        {
            _scale = ResolveVisualScale();
            _hoverHeight = ResolveHoverHeight();
            EnsureRuntimeComponents();
        }

        /// <summary>Instantiates <paramref name="prefab"/> inactive: the asset is deactivated for the
        /// copy and restored after, so no Awake, OnEnable or Start runs on the instance and NGO does
        /// not see it until it is activated and spawned (#1336).</summary>
        internal static GameObject InstantiateInactive(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            bool wasActive = prefab.activeSelf;
            try
            {
                prefab.SetActive(false);
                return Instantiate(prefab, position, rotation);
            }
            finally
            {
                prefab.SetActive(wasActive);
            }
        }

        /// <summary>Host pre-warm (#1336): this peer's rise effects for the coming summon, built
        /// inactive - the HolyEffect copy (its driver and light trim come with activation) and the
        /// rise dust - plus the chant's audio data. The next PlayRiseEffectsLocal adopts them.</summary>
        internal static void PrewarmRiseEffects(Vector3 ritualPoint, float scale, float riseDuration)
        {
            DiscardPrewarmedRiseEffects();
            GameObject effectPrefab = ResolveRiseEffectPrefab();
            if (effectPrefab != null)
            {
                _pooledRiseEffect = InstantiateInactive(effectPrefab, GetRiseEffectPosition(ritualPoint), Quaternion.identity);
                _pooledRiseEffect.name = "SeraphimRisingHolyEffect";
            }

            _pooledRiseDust = BuildRiseDustObject(ritualPoint, scale, riseDuration);
            _pooledRiseDustRadius = ResolveRiseDustRadius(scale);
            _pooledRiseDustEmitSeconds = ResolveRiseDustEmitSeconds(riseDuration);

            AudioClip chant = ContractAssetLoader.SeraphimHolyChantClip;
            if (chant != null && chant.loadState != AudioDataLoadState.Loaded)
                chant.LoadAudioData();
        }

        /// <summary>Destroys the pre-warmed rise effects nobody adopted; never-activated, so no
        /// OnDestroy runs. Idempotent.</summary>
        internal static void DiscardPrewarmedRiseEffects()
        {
            if (_pooledRiseEffect != null)
                Destroy(_pooledRiseEffect);
            _pooledRiseEffect = null;
            if (_pooledRiseDust != null)
                Destroy(_pooledRiseDust);
            _pooledRiseDust = null;
        }

        private static double SummonStepMilliseconds(long startTicks)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0
                   / System.Diagnostics.Stopwatch.Frequency;
        }

        internal void SyncInitialState()
        {
            if (!IsServerAuthority() || NetworkObject == null || !NetworkObject.IsSpawned || !_initialized)
                return;

            InitializeClientRpc(_ritualPoint, _ritualRotation, _scale, _hoverHeight, _riseDuration, _riseStartNetworkTime);
        }

        /// <summary>Server: brings one late joiner up to date - the rise parameters (a joiner after
        /// the rise keeps the live spawn position) and the locked animation. Both handlers are
        /// idempotent, so the retried late-join schedule may call this several times.</summary>
        internal void ResendStateToClient(ulong clientId)
        {
            if (!IsServerAuthority() || NetworkObject == null || !NetworkObject.IsSpawned || !_initialized)
                return;

            var rpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } },
            };
            InitializeClientRpc(_ritualPoint, _ritualRotation, _scale, _hoverHeight, _riseDuration, _riseStartNetworkTime, rpcParams);
            if (_lockedAnimationId >= 0 || isEnemyDead || _hasAimTarget)
                ResyncAnimationClientRpc(_lockedAnimationId, isEnemyDead, _aimTargetKind, _aimTargetId, _hasAimTarget, rpcParams);
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            // #1336: a client builds the whole body in this frame (the host pre-built it), so the
            // build is measured for the next playtest.
            long buildStart = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureRuntimeComponentsSafe();
            if (IsServerAuthority())
                return;

            double buildMilliseconds = SummonStepMilliseconds(buildStart);
            if (buildMilliseconds >= SummonStepWarnMilliseconds)
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Timing] Seraphim client spawn build took {buildMilliseconds:0.00}ms");
        }

        public override void Start()
        {
            EnsureBehaviourStates();
            base.Start();
            // Vanilla never seeds these on a non-owner; until an UpdateEnemyRotationClientRpc
            // arrives, base.Update would lerp a late joiner's body toward world yaw 0.
            if (!IsOwner)
            {
                previousYRotation = transform.eulerAngles.y;
                targetYRotation = transform.eulerAngles.y;
            }
            EnsureRuntimeComponentsSafe();
            enemyHP = Mathf.Max(enemyHP, SeraphimHealth);
            isOutside = true;
            ventAnimationFinished = true;
            // Vanilla sync is the only transform path after the rise; WhistleblowerEnemyAI's values
            // (finer threshold, quicker client catch-up than the EnemyAI defaults).
            updatePositionThreshold = 0.35f;
            syncMovementSpeed = 0.13f;
        }

        public override void OnNetworkDespawn()
        {
            // The last partial sampler window, so a Seraphim despawned between flushes still reports.
            if (_frameCost.IsFinalFlushDue())
                FlushFrameCost();
            StopBeamLocal();
            if (_chantSource != null)
                _chantSource.Stop();
            if (_riseEffectObject != null)
            {
                Destroy(_riseEffectObject);
                _riseEffectObject = null;
            }
            if (_riseDustObject != null)
            {
                Destroy(_riseDustObject);
                _riseDustObject = null;
            }
            base.OnNetworkDespawn();
        }

        // #1336 S3 frame cost: the 10 s sampler, the aura and light throttles, the one-shot
        // renderer bounds switch and the beam's cultist lookup cache.
        private readonly SeraphimFrameCostWindow _frameCost = new SeraphimFrameCostWindow(System.Diagnostics.Stopwatch.Frequency);
        private readonly List<Light> _lightScratch = new List<Light>();
        // Timed and effect lights this Seraphim spawned outside its own hierarchy, for the sampler.
        private readonly List<Light> _transientLights = new List<Light>();
        private readonly LineRenderer[] _auraRings = new LineRenderer[3];
        private Renderer[] _modelRenderers = Array.Empty<Renderer>();
        // The loader's widened box per skinned renderer, captured before the first render.
        private Bounds[] _authoredRendererBounds = Array.Empty<Bounds>();
        private bool[] _authoredRendererBoundsCaptured = Array.Empty<bool>();
        private int _auraSegmentCount;
        private float _auraTickTime = float.NegativeInfinity;
        private bool _auraVisible = true;
        private float _lightPulseTickTime = float.NegativeInfinity;
        private bool _rendererBoundsTightened;
        private ulong _beamCultistLookupId;
        private CongregantAI _beamCultistLookup;
        private bool _beamCultistScanned;

        public override void Update()
        {
            long frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
            base.Update();
            long baseEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureRuntimeComponentsSafe();
            UpdateRiseVisual();
            // The local victim's lift keeps Update timing; the beam line is drawn in LateUpdate.
            if (_beamActive)
                ApplyLocalBeamLift();
            UpdateVisibilityAura();
            UpdateSeraphimLights();
            UpdateScanNodeGlitch();
            MaintainLockedAnimationState();
            TightenRendererBoundsAfterRise();
            long visualEnd = System.Diagnostics.Stopwatch.GetTimestamp();

            long serverTicks = 0;
            if (isEnemyDead)
            {
                StopAgentMotion();
            }
            else if (IsServerAuthority() && _initialized)
            {
                UpdateServerMovement();
                UpdateServerTargeting();
                UpdateServerVoices();
                serverTicks = System.Diagnostics.Stopwatch.GetTimestamp() - visualEnd;
            }

            _frameCost.AddFrame(System.Diagnostics.Stopwatch.GetTimestamp() - frameStart, baseEnd - frameStart, visualEnd - baseEnd, serverTicks);
            if (_frameCost.IsDue(Time.unscaledTime))
                FlushFrameCost();
        }

        /// <summary>After the animators evaluated this frame's pose: the rise Root correction, then
        /// the eye aim on top of the pose, then the beam from the aimed eye (#1336 S6).</summary>
        private void LateUpdate()
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_riseRootCorrectionActive)
                ApplyRiseRootCorrection();
            UpdateEyeAim(Time.deltaTime);
            UpdateBeamVisual();
            _frameCost.AddLateUpdate(System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }

        /// <summary>#1336 S6, every peer, sidecar only: turns Main_eye toward the attack target,
        /// along this peer's own travel, or level along the facing, never more than
        /// <see cref="SeraphimEyeAim.MaxEyeDeflectionDegrees"/> off the facing, on top of the pose the
        /// animator evaluated this frame, so the clip's spin about the gaze and the bone's translation
        /// survive. The weight ramps in after the rise from the replicated rise clock; a dead body
        /// keeps the authored pose. No allocation; no lookup beyond the attack target's.</summary>
        private void UpdateEyeAim(float deltaTime)
        {
            if (_eyeBone == null || !_initialized)
                return;

            // Every clip keys Main_eye, so the animator rewrites it each frame. If it did not, the
            // bone still holds last frame's aimed rotation: aim from the cached authored one instead.
            Quaternion authoredLocal = _eyeBone.localRotation;
            if (_eyeRotationWritten && IsSameRotation(authoredLocal, _eyeWrittenLocalRotation))
            {
                authoredLocal = _eyeAuthoredLocalRotation;
                _eyeBone.localRotation = authoredLocal;
            }
            _eyeAuthoredLocalRotation = authoredLocal;
            _eyeRotationWritten = false;

            // Travel is this peer's own root motion: the host's flight, a client's vanilla sync.
            Vector3 rootPosition = transform.position;
            if (_eyeHasRootPosition)
                _eyeTravelVelocity = SeraphimEyeAim.FilterVelocity(_eyeTravelVelocity, ToEyeVector(rootPosition - _eyeLastRootPosition), deltaTime);
            _eyeLastRootPosition = rootPosition;
            _eyeHasRootPosition = true;

            float weight = SeraphimEyeAim.BlendWeight(GetNetworkTime(), _riseStartNetworkTime, _riseDuration, RiseHandoffSeconds, isEnemyDead);
            if (weight <= 0f)
            {
                // The first applied frame starts from wherever the clip points the eye, in Idle.
                _eyeGazeValid = false;
                _eyeAimMode = SeraphimEyeAimMode.Idle;
                return;
            }

            Quaternion authored = _eyeBone.rotation;
            Vector3 authoredGaze = authored * MainEyeLocalGaze;
            if (!_eyeGazeValid)
            {
                _eyeGaze = authoredGaze;
                _eyeGazeValid = true;
            }

            Vector3 targetPoint = default;
            bool hasTargetPoint = _hasAimTarget
                && (_lockedAnimationId == AnimAttackStart || _lockedAnimationId == AnimAttackLoop)
                && TryResolveTargetPoint(_aimTargetKind, _aimTargetId, out targetPoint);
            SeraphimEyeVector targetDirection = hasTargetPoint ? ToEyeVector(targetPoint - _eyeBone.position) : default;
            SeraphimEyeVector gaze = SeraphimEyeAim.Advance(ToEyeVector(_eyeGaze), _eyeAimMode, hasTargetPoint, targetDirection,
                _eyeTravelVelocity, ToEyeVector(GetFacingForward()), deltaTime, out _eyeAimMode);
            _eyeGaze = new Vector3(gaze.X, gaze.Y, gaze.Z);

            Quaternion aim = Quaternion.FromToRotation(authoredGaze, _eyeGaze);
            if (weight < 1f)
                aim = Quaternion.Slerp(Quaternion.identity, aim, weight);
            _eyeBone.rotation = aim * authored;
            _eyeWrittenLocalRotation = _eyeBone.localRotation;
            _eyeRotationWritten = true;
        }

        private static bool IsSameRotation(Quaternion a, Quaternion b)
        {
            // Exact components: Quaternion == is approximate.
            return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
        }

        private static SeraphimEyeVector ToEyeVector(Vector3 value)
        {
            return new SeraphimEyeVector(value.x, value.y, value.z);
        }

        private void SetAimTarget(int targetKind, ulong targetId)
        {
            if (isEnemyDead)
                return;

            _aimTargetKind = targetKind;
            _aimTargetId = targetId;
            _hasAimTarget = true;
        }

        private void ClearAimTarget()
        {
            _hasAimTarget = false;
            _aimTargetId = 0UL;
            _aimTargetKind = BeamTargetKindPlayer;
        }

        private void ApplyRiseRootCorrection()
        {
            float rootWeight = 0f;
            if (!_rising || _lockedAnimationId != AnimAwakening)
            {
                float release = (float)(GetNetworkTime() - (_riseStartNetworkTime + _riseDuration))
                    / RiseHandoffSeconds - 1f;
                if (release >= 1f)
                {
                    _riseRootCorrectionActive = false;
                    return;
                }
                release = Mathf.Clamp01(release);
                rootWeight = release * release * (3f - 2f * release);
            }

            // The shipped generic rig keeps Root translation despite applyRootMotion=false.
            // Its rotated axes turn the 1.457 m terminal local-Y offset into lateral movement.
            // World rise belongs to GetRisePosition; retain articulation, not any Root translation.
            // Hold through the pose blend, then smoothly return translation to the idle clip.
            for (int i = 0; i < _riseAnimationRoots.Length; i++)
            {
                Transform root = _riseAnimationRoots[i];
                if (root != null)
                    root.localPosition *= rootWeight;
            }
        }

        /// <summary>#1336 S3: off-screen skinning stays on through the rise (it starts underground
        /// and LateUpdate's Root correction moves the pose); afterwards the skinned renderers cull
        /// against the authored box ContractAssetLoader widened. Every peer, late joiners included.</summary>
        private void TightenRendererBoundsAfterRise()
        {
            if (!_initialized || _modelRenderers.Length == 0
                || !SeraphimFrameBudget.ShouldTightenRendererBounds(_rising, _riseRootCorrectionActive, _rendererBoundsTightened))
                return;

            _rendererBoundsTightened = true;
            for (int i = 0; i < _modelRenderers.Length; i++)
            {
                if (!_authoredRendererBoundsCaptured[i])
                    continue;
                if (_modelRenderers[i] is SkinnedMeshRenderer skinned && skinned != null)
                {
                    skinned.localBounds = _authoredRendererBounds[i];
                    skinned.updateWhenOffscreen = false;
                }
            }
        }

        /// <summary>Runs where EnsureCompositeVisual first finds the body, before the instance is
        /// ever rendered. While updateWhenOffscreen is on, Unity overwrites localBounds with each
        /// frame's computed box, so the loader's widened authored box is kept here for the switch.</summary>
        private void SetModelRenderers(Renderer[] renderers)
        {
            _modelRenderers = renderers;
            _authoredRendererBounds = new Bounds[renderers.Length];
            _authoredRendererBoundsCaptured = new bool[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] is SkinnedMeshRenderer skinned && skinned != null)
                {
                    _authoredRendererBounds[i] = skinned.localBounds;
                    _authoredRendererBoundsCaptured[i] = true;
                }
            }
        }

        private void FlushFrameCost()
        {
            CountSeraphimLights(out int lights, out int enabledLights, out int shadowedLights);
            ContractsBootstrap.Log?.LogInfo(
                _frameCost.BuildReport(IsServerAuthority() ? "host" : "client", lights, enabledLights, shadowedLights));
        }

        /// <summary>Flush time only: the lights under this body plus the live timed and effect
        /// lights it spawned; destroyed ones leave the list here.</summary>
        private void CountSeraphimLights(out int lights, out int enabledLights, out int shadowedLights)
        {
            lights = 0;
            enabledLights = 0;
            shadowedLights = 0;
            GetComponentsInChildren(true, _lightScratch);
            for (int i = 0; i < _lightScratch.Count; i++)
                CountLight(_lightScratch[i], ref lights, ref enabledLights, ref shadowedLights);
            _lightScratch.Clear();

            for (int i = _transientLights.Count - 1; i >= 0; i--)
            {
                Light light = _transientLights[i];
                if (light == null)
                {
                    _transientLights.RemoveAt(i);
                    continue;
                }
                if (!light.transform.IsChildOf(transform))
                    CountLight(light, ref lights, ref enabledLights, ref shadowedLights);
            }
        }

        private static void CountLight(Light light, ref int lights, ref int enabledLights, ref int shadowedLights)
        {
            if (light == null)
                return;

            lights++;
            if (light.isActiveAndEnabled)
                enabledLights++;
            if (light.shadows != LightShadows.None)
                shadowedLights++;
        }

        private void RegisterTransientLight(Light light)
        {
            if (light != null && !_transientLights.Contains(light))
                _transientLights.Add(light);
        }

        public override void DoAIInterval()
        {
            base.DoAIInterval();
        }

        public override void OnCollideWithPlayer(Collider other)
        {
        }

        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
        {
            if (isEnemyDead)
                return;

            if (IsServerAuthority())
                PlayRandomVoiceNetworked();

            try
            {
                base.HitEnemy(force, playerWhoHit, playHitSFX, hitID);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Seraphim base HitEnemy skipped: {e.Message}");
            }

            enemyHP -= Mathf.Max(1, force);
            if (enemyHP <= 0 && !isEnemyDead && (IsOwner || IsServerAuthority()))
                KillEnemyOnOwnerClient();
        }

        public override void KillEnemy(bool destroy = false)
        {
            if (isEnemyDead && _deadHandled)
                return;

            _deadHandled = true;
            StopAgentMotion();
            StopBeamLocal();
            ClearAimTarget();
            if (_chantSource != null)
                _chantSource.Stop();

            try
            {
                // Every peer runs KillEnemy (the owner directly, the rest from vanilla
                // KillEnemyClientRpc), so the state switch stays local instead of another RPC.
                SwitchToBehaviourStateOnLocalClient(3);
            }
            catch
            {
            }

            try
            {
                base.KillEnemy(destroy);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Seraphim base KillEnemy skipped: {e.Message}");
            }

            isEnemyDead = true;
            _attackRoutine = null;
            if (IsServerAuthority() && NetworkObject != null && NetworkObject.IsSpawned)
                PlayAnimationClientRpc(AnimAttackEnd);
            else
                PlayAnimationLocal(AnimAttackEnd);
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.Cult] Seraphim enemy killed.");
        }

        /// <summary>Client: a late joiner never saw KillEnemyClientRpc, so the animation resend
        /// carries the death. The local half of vanilla KillEnemy (scan node collider and agent off,
        /// Dead behaviour state) without its RPCs; Update then holds the body still.</summary>
        private void ApplyReplicatedDeath()
        {
            isEnemyDead = true;
            _deadHandled = true;
            _attackRoutine = null;
            StopAgentMotion();
            StopBeamLocal();
            ClearAimTarget();
            if (_chantSource != null)
                _chantSource.Stop();
            SwitchToBehaviourStateOnLocalClient(3);
            if (_scanNode != null)
            {
                Collider scanCollider = _scanNode.GetComponent<Collider>();
                if (scanCollider != null)
                    scanCollider.enabled = false;
            }
            if (_agent != null)
                _agent.enabled = false;
        }

        /// <summary>Every peer moves the body along the same curve from the shared server-time
        /// start, so a client's rise is as smooth as the host's instead of chasing position syncs.</summary>
        private void UpdateRiseVisual()
        {
            if (!_initialized || !_rising)
                return;

            float progress = GetRiseProgress();
            float eased = progress * progress * (3f - 2f * progress);
            transform.position = GetRisePosition(eased);
            transform.rotation = ToRootRotation(_ritualRotation);
            // #1336: at most one queued rise effect per frame; FinishRise runs whatever is left.
            SeraphimRiseEffectStage effectStage = _riseEffectSchedule.TakeNext(Time.frameCount);
            if (effectStage != SeraphimRiseEffectStage.None)
                RunRiseEffectStage(effectStage);
            if (progress >= 1f)
                FinishRise();
        }

        private float GetRiseProgress()
        {
            return Mathf.Clamp01((float)(GetNetworkTime() - _riseStartNetworkTime) / Mathf.Max(0.1f, _riseDuration));
        }

        private void FinishRise()
        {
            _rising = false;
            inSpecialAnimation = false;
            Vector3 hover = GetRisePosition(1f);
            transform.position = hover;
            // Hand the transform to vanilla sync at the hover point: the server's first sync then
            // only fires once the body has moved, and a client's SmoothDamp starts from where it is
            // (unless a server position already arrived, which is newer than the curve's end).
            if (IsServerAuthority() || !_serverPositionSynced)
                serverPosition = hover;
            if (!IsServerAuthority())
                targetYRotation = transform.eulerAngles.y;
            ShakeLocalCameraForRise(hover, riseStart: false);

            if (!isEnemyDead && _lockedAnimationId == AnimAwakening)
                PlayAnimationLocal(AnimIdle, _riseStartNetworkTime + _riseDuration);
            if (IsServerAuthority())
                StopAgentMotion();

            // A peer whose rise ends before every queued effect ran (a late joiner near the end of
            // the rise) still gets them all, after the gameplay hand-off above.
            int owedEffects = _riseEffectSchedule.FlushRemaining(RiseEffectFlushBuffer);
            for (int i = 0; i < owedEffects; i++)
                RunRiseEffectStage(RiseEffectFlushBuffer[i]);
        }

        public override void OnSyncPositionFromServer(Vector3 newPos)
        {
            base.OnSyncPositionFromServer(newPos);
            _serverPositionSynced = true;
        }

        private Vector3 GetRisePosition(float easedProgress)
        {
            float y = Mathf.Lerp(_riseStartPosition.y, _hoverBasePosition.y, Mathf.Clamp01(easedProgress));
            return new Vector3(_ritualPoint.x, y, _ritualPoint.z);
        }

        private void UpdateServerMovement()
        {
            if (_rising)
                return;

            StopAgentMotion();
            if (_attackRoutine != null)
            {
                if (TryResolveCurrentTargetPoint(out Vector3 attackPoint))
                    FacePosition(attackPoint, 120f);
                return;
            }

            CongregantAI cultistTarget = ResolveCurrentCultistTarget();
            if (cultistTarget != null)
            {
                MoveFlyingToward(BuildTargetHoverPosition(GetCultistGroundPosition(cultistTarget)), FlyingChaseSpeed);
                FacePosition(GetCultistAimPoint(cultistTarget), 105f);
                return;
            }

            PlayerControllerB target = ResolveCurrentTarget();
            if (target != null)
            {
                MoveFlyingToward(BuildTargetHoverPosition(GetPlayerGroundPosition(target)), FlyingChaseSpeed);
                FacePosition(GetPlayerAimPoint(target), 105f);
                return;
            }

            MoveFlyingToward(BuildIdleHoverPosition(), FlyingReturnSpeed);
            float time = Time.time + _hoverSeed;
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                ToRootRotation(_ritualRotation * Quaternion.Euler(0f, Mathf.Sin(time * 0.12f) * 18f, 0f)),
                30f * Time.deltaTime);
        }

        private void UpdateServerTargeting()
        {
            if (Time.time < _nextSearchTime)
                return;

            _nextSearchTime = Time.time + 0.35f;
            CongregantAI cultist = FindBestCultistTarget();
            _targetCultist = cultist;
            if (cultist != null)
            {
                _targetPlayer = null;
                targetPlayer = null;
                _targetClientId = 0UL;
                _hasTargetClientId = false;

                if (_rising || _attackRoutine != null || Time.time < _nextAttackReadyTime || !CanAttackCultist(cultist))
                    return;

                _attackRoutine = StartCoroutine(AttackCultistRoutine(cultist));
                return;
            }

            PlayerControllerB best = FindBestTarget();
            ulong previousTarget = _targetClientId;
            bool hadPreviousTarget = _hasTargetClientId;
            _targetPlayer = best;
            targetPlayer = best;
            _targetClientId = best != null ? best.actualClientId : 0UL;
            _hasTargetClientId = best != null;

            if (best != null && (!hadPreviousTarget || _targetClientId != previousTarget))
                PlayRandomVoiceNetworked();

            if (_rising || _attackRoutine != null || Time.time < _nextAttackReadyTime || best == null)
                return;

            if (!CanAttackTarget(best))
                return;

            _attackRoutine = StartCoroutine(AttackPlayerRoutine(best));
        }

        private void UpdateServerVoices()
        {
            if (Time.time < _nextVoiceTime)
                return;

            PlayRandomVoiceNetworked();
            float cadence = Mathf.Max(6f, ContractsBootstrap.CultSeraphimPeriodicVoiceSeconds.Value);
            _nextVoiceTime = Time.time + UnityEngine.Random.Range(cadence * 0.85f, cadence * 1.25f);
        }

        private IEnumerator AttackPlayerRoutine(PlayerControllerB target)
        {
            if (!CanAttackTarget(target))
            {
                _attackRoutine = null;
                yield break;
            }

            StopAgentMotion();
            _targetPlayer = target;
            _targetClientId = target.actualClientId;
            _hasTargetClientId = true;
            FacePosition(GetPlayerAimPoint(target), 1000f);
            PlayRandomVoiceNetworked();
            PlayAnimationClientRpc(AnimAttackStart);
            PlayBeamCueClientRpc(GetPlayerAimPoint(target), BeamTargetKindPlayer, target.actualClientId);

            yield return new WaitForSeconds(AttackStartLeadSeconds);

            if (!CanContinueAttackTarget(target))
            {
                PlayAnimationClientRpc(AnimAttackEnd);
                _nextAttackReadyTime = Time.time + Mathf.Max(2f, ContractsBootstrap.CultSeraphimAttackCooldownSeconds.Value * 0.5f);
                _attackRoutine = null;
                yield break;
            }

            float beamDuration = ExecutionHoldSeconds;
            PlayAnimationClientRpc(AnimAttackLoop);
            BeginBeamClientRpc(BeamTargetKindPlayer, target.actualClientId, beamDuration);

            float beamElapsed = 0f;
            while (beamElapsed < beamDuration)
            {
                if (!CanContinueAttackTarget(target))
                {
                    EndBeamClientRpc();
                    PlayAnimationClientRpc(AnimAttackEnd);
                    _nextAttackReadyTime = Time.time + Mathf.Max(2f, ContractsBootstrap.CultSeraphimAttackCooldownSeconds.Value * 0.5f);
                    _attackRoutine = null;
                    yield break;
                }

                beamElapsed += Time.deltaTime;
                yield return null;
            }

            if (!CanContinueAttackTarget(target))
            {
                EndBeamClientRpc();
                PlayAnimationClientRpc(AnimAttackEnd);
                _nextAttackReadyTime = Time.time + Mathf.Max(2f, ContractsBootstrap.CultSeraphimAttackCooldownSeconds.Value * 0.5f);
                _attackRoutine = null;
                yield break;
            }

            Vector3 impact = GetPlayerAimPoint(target);
            PlayExecutionImpactClientRpc(impact);
            yield return new WaitForSeconds(ExecutionImpactLeadSeconds);

            if (!CanContinueAttackTarget(target))
            {
                EndBeamClientRpc();
                PlayAnimationClientRpc(AnimAttackEnd);
                _nextAttackReadyTime = Time.time + Mathf.Max(2f, ContractsBootstrap.CultSeraphimAttackCooldownSeconds.Value * 0.5f);
                _attackRoutine = null;
                yield break;
            }

            ExplodeBeamClientRpc(BeamTargetKindPlayer, target.actualClientId, impact);
            EndBeamClientRpc();
            KillTarget(target);
            PlayRandomVoiceNetworked();
            PlayAnimationClientRpc(AnimAttackEnd);

            yield return new WaitForSeconds(AttackEndSettleSeconds);

            PlayAnimationClientRpc(UnityEngine.Random.value > 0.35f ? AnimIdleFast : AnimIdle);
            _nextAttackReadyTime = Time.time + Mathf.Max(2f, ContractsBootstrap.CultSeraphimAttackCooldownSeconds.Value);
            _attackRoutine = null;
        }

        private IEnumerator AttackCultistRoutine(CongregantAI target)
        {
            if (!CanAttackCultist(target))
            {
                _attackRoutine = null;
                yield break;
            }

            StopAgentMotion();
            _targetCultist = target;
            target.BeginSeraphimExecutionServer();
            FacePosition(GetCultistAimPoint(target), 1000f);
            PlayRandomVoiceNetworked();
            PlayAnimationClientRpc(AnimAttackStart);
            ulong networkId = GetNetworkObjectId(target);
            PlayBeamCueClientRpc(GetCultistAimPoint(target), BeamTargetKindCultist, networkId);

            yield return new WaitForSeconds(AttackStartLeadSeconds);

            if (!CanAttackCultist(target))
            {
                PlayAnimationClientRpc(AnimAttackEnd);
                _nextAttackReadyTime = Time.time + Mathf.Max(2f, ContractsBootstrap.CultSeraphimAttackCooldownSeconds.Value * 0.5f);
                _attackRoutine = null;
                yield break;
            }

            Vector3 liftStart = target.transform.position;
            float beamDuration = ExecutionHoldSeconds;
            PlayAnimationClientRpc(AnimAttackLoop);
            BeginBeamClientRpc(BeamTargetKindCultist, networkId, beamDuration);

            float beamElapsed = 0f;
            while (beamElapsed < beamDuration)
            {
                if (!IsCultistTargetValid(target))
                {
                    EndBeamClientRpc();
                    PlayAnimationClientRpc(AnimAttackEnd);
                    _nextAttackReadyTime = Time.time + Mathf.Max(2f, ContractsBootstrap.CultSeraphimAttackCooldownSeconds.Value * 0.5f);
                    _attackRoutine = null;
                    yield break;
                }

                float progress = Mathf.Clamp01(beamElapsed / beamDuration);
                target.SetSeraphimExecutionPositionServer(liftStart + Vector3.up * (ExecutionLiftMeters * progress));
                FacePosition(GetCultistAimPoint(target), 240f);
                beamElapsed += Time.deltaTime;
                yield return null;
            }

            if (!IsCultistTargetValid(target))
            {
                EndBeamClientRpc();
                PlayAnimationClientRpc(AnimAttackEnd);
                _nextAttackReadyTime = Time.time + Mathf.Max(2f, ContractsBootstrap.CultSeraphimAttackCooldownSeconds.Value * 0.5f);
                _attackRoutine = null;
                yield break;
            }

            target.SetSeraphimExecutionPositionServer(liftStart + Vector3.up * ExecutionLiftMeters);
            Vector3 impact = GetCultistAimPoint(target);
            PlayExecutionImpactClientRpc(impact);
            yield return new WaitForSeconds(ExecutionImpactLeadSeconds);

            if (!IsCultistTargetValid(target))
            {
                EndBeamClientRpc();
                PlayAnimationClientRpc(AnimAttackEnd);
                _nextAttackReadyTime = Time.time + Mathf.Max(0.5f, CultistExecutionCooldownSeconds);
                _attackRoutine = null;
                yield break;
            }

            ExplodeBeamClientRpc(BeamTargetKindCultist, networkId, impact);
            EndBeamClientRpc();
            KillCultistTarget(target);
            PlayRandomVoiceNetworked();
            PlayAnimationClientRpc(AnimAttackEnd);

            yield return new WaitForSeconds(AttackEndSettleSeconds);

            PlayAnimationClientRpc(UnityEngine.Random.value > 0.35f ? AnimIdleFast : AnimIdle);
            _nextAttackReadyTime = Time.time + Mathf.Max(0.5f, CultistExecutionCooldownSeconds);
            _targetCultist = null;
            _attackRoutine = null;
        }

        private PlayerControllerB FindBestTarget()
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || players.Length == 0)
                return null;

            PlayerControllerB best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (!IsTargetValid(player))
                    continue;

                float distance = HorizontalDistance(transform.position, player.transform.position);
                float score = distance;
                if (player.isInHangarShipRoom)
                    score += 45f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = player;
                }
            }

            return best;
        }

        private CongregantAI FindBestCultistTarget()
        {
            if (!CongregationEscalationState.TryGetNextSeraphimCultistTarget(out CongregantAI cultist))
                return null;
            return IsCultistTargetValid(cultist) ? cultist : null;
        }

        private CongregantAI ResolveCurrentCultistTarget()
        {
            if (IsCultistTargetValid(_targetCultist))
                return _targetCultist;

            CongregantAI best = FindBestCultistTarget();
            _targetCultist = best;
            return best;
        }

        private bool IsCultistTargetValid(CongregantAI cultist)
        {
            return cultist != null
                && !cultist.isEnemyDead
                && cultist.gameObject != null;
        }

        private bool CanAttackCultist(CongregantAI cultist)
        {
            return IsCultistTargetValid(cultist)
                && HorizontalDistance(transform.position, cultist.transform.position) <= ResolveAttackRange();
        }

        private bool TryResolveCurrentTargetPoint(out Vector3 point)
        {
            CongregantAI cultist = ResolveCurrentCultistTarget();
            if (cultist != null)
            {
                point = GetCultistAimPoint(cultist);
                return true;
            }

            PlayerControllerB player = ResolveCurrentTarget();
            if (player != null)
            {
                point = GetPlayerAimPoint(player);
                return true;
            }

            point = Vector3.zero;
            return false;
        }

        private PlayerControllerB ResolveCurrentTarget()
        {
            if (IsTargetValid(_targetPlayer))
                return _targetPlayer;

            if (!_hasTargetClientId)
                return null;

            PlayerControllerB byId = FindPlayerByClientId(_targetClientId);
            if (IsTargetValid(byId))
            {
                _targetPlayer = byId;
                return byId;
            }

            return null;
        }

        private bool IsTargetValid(PlayerControllerB player)
        {
            if (player == null || !player.isPlayerControlled || player.isPlayerDead)
                return false;
            if (IsExecutionPending(player))
                return false;
            // Vanilla targetability for an outdoor enemy, in-ship tested first so the call skips
            // PlayerIsTargetable's per-rejection "Targetable A" log; plus the host's mob-ignore switch.
            if (player.isInHangarShipRoom
                || !PlayerIsTargetable(player, cannotBeInShip: false, overrideInsideFactoryCheck: false)
                || MobIgnoreBridge.IsIgnored(player.actualClientId))
                return false;

            return true;
        }

        private bool IsExecutionPending(PlayerControllerB player)
        {
            if (!_hasPendingExecution || player == null)
                return false;

            if (Time.time >= _pendingExecutionExpiry)
            {
                _hasPendingExecution = false;
                return false;
            }

            return player.actualClientId == _pendingExecutionClientId;
        }

        private bool CanAttackTarget(PlayerControllerB player)
        {
            if (!IsTargetValid(player))
                return false;

            return HorizontalDistance(transform.position, player.transform.position) <= ResolvePlayerAttackStartRange();
        }

        private bool CanContinueAttackTarget(PlayerControllerB player)
        {
            if (!IsTargetValid(player))
                return false;

            return HorizontalDistance(transform.position, player.transform.position) <= ResolvePlayerAttackContinueRange();
        }

        private Vector3 BuildTargetHoverPosition(Vector3 targetGround)
        {
            Vector3 currentGround = transform.position;
            currentGround.y = targetGround.y;

            Vector3 awayFromTarget = currentGround - targetGround;
            awayFromTarget.y = 0f;
            if (awayFromTarget.sqrMagnitude < 0.01f)
            {
                awayFromTarget = -GetFacingForward();
                awayFromTarget.y = 0f;
            }

            float attackRange = ResolveAttackRange();
            float standOff = Mathf.Clamp(
                attackRange * 0.42f,
                Mathf.Min(6f, attackRange * 0.35f),
                Mathf.Max(6f, attackRange * 0.6f));
            Vector3 desired = targetGround + awayFromTarget.normalized * standOff;
            desired.y = targetGround.y + _hoverHeight + GetHoverBob();
            return ApplyFlightClearance(desired);
        }

        private Vector3 BuildIdleHoverPosition()
        {
            float time = Time.time + _hoverSeed;
            Vector3 drift = new Vector3(
                Mathf.Sin(time * 0.18f) * IdleDriftRadius,
                0f,
                Mathf.Cos(time * 0.16f) * IdleDriftRadius);
            Vector3 desired = _ritualPoint + drift;
            desired.y = _ritualPoint.y + _hoverHeight + GetHoverBob();
            return ApplyFlightClearance(desired);
        }

        private void MoveFlyingToward(Vector3 desired, float speed)
        {
            desired = ApplyFlightClearance(desired);
            float step = Mathf.Max(0.1f, speed) * Time.deltaTime;
            Vector3 next = Vector3.MoveTowards(transform.position, desired, step);
            Vector3 cleared = ApplyFlightClearance(next);
            if (cleared.y > next.y)
                next.y = Mathf.MoveTowards(next.y, cleared.y, FlightClearanceClimbSpeed * Time.deltaTime);
            transform.position = next;
        }

        private Vector3 ApplyFlightClearance(Vector3 position)
        {
            if (TrySampleGroundHeight(position, out float groundY))
                position.y = Mathf.Max(position.y, groundY + ResolveTerrainClearance());
            return position;
        }

        /// <summary>Never above the hover height: a larger floor (24.6 m for a 12 m body against a
        /// 23 m hover) pushed every hover target up and read as a jump at the end of the rise.</summary>
        private float ResolveTerrainClearance()
        {
            return Mathf.Min(Mathf.Clamp(Mathf.Max(_hoverHeight, _scale * 2.05f), 14f, 55f), _hoverHeight);
        }

        /// <summary>Flight clearance asks for the ground under the hover target and under the next
        /// step, several times a frame. Each answer is reused for
        /// <see cref="FlightGroundProbeIntervalSeconds"/> near the same spot (one slot per query
        /// point), so the 360 m raycast runs at most every 0.1 s per point. Never reused while rising.</summary>
        private bool TrySampleGroundHeight(Vector3 position, out float groundY)
        {
            float now = Time.time;
            if (!_rising)
            {
                for (int i = 0; i < _groundProbeSamples.Length; i++)
                {
                    GroundProbeSample sample = _groundProbeSamples[i];
                    if (!sample.Valid || now - sample.Time >= FlightGroundProbeIntervalSeconds)
                        continue;
                    float dx = sample.X - position.x;
                    float dz = sample.Z - position.z;
                    if (dx * dx + dz * dz > FlightGroundProbeReuseRadius * FlightGroundProbeReuseRadius)
                        continue;

                    groundY = sample.GroundY;
                    return sample.Hit;
                }
            }

            Vector3 origin = position + Vector3.up * FlightGroundProbeHeight;
            bool hitGround = Physics.Raycast(origin, Vector3.down, out RaycastHit hit, FlightGroundProbeDistance, GetGroundProbeMask(), QueryTriggerInteraction.Ignore);
            groundY = hitGround ? hit.point.y : 0f;

            _groundProbeSamples[_groundProbeNextSlot] = new GroundProbeSample
            {
                Valid = true,
                Time = now,
                X = position.x,
                Z = position.z,
                Hit = hitGround,
                GroundY = groundY,
            };
            _groundProbeNextSlot = (_groundProbeNextSlot + 1) % _groundProbeSamples.Length;
            return hitGround;
        }

        private struct GroundProbeSample
        {
            internal bool Valid;
            internal float Time;
            internal float X;
            internal float Z;
            internal bool Hit;
            internal float GroundY;
        }

        /// <summary>Layer names are project settings, so the mask is built once per session instead
        /// of four <c>LayerMask.NameToLayer</c> calls per probe.</summary>
        private static int GetGroundProbeMask()
        {
            if (_groundProbeMaskResolved)
                return _groundProbeMask;

            int mask = ~0;
            ExcludeLayer(ref mask, "Enemies");
            ExcludeLayer(ref mask, "Player");
            ExcludeLayer(ref mask, "ScanNode");
            ExcludeLayer(ref mask, "Ignore Raycast");
            _groundProbeMask = mask;
            _groundProbeMaskResolved = true;
            return mask;
        }

        private static void ExcludeLayer(ref int mask, string layerName)
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer >= 0)
                mask &= ~(1 << layer);
        }

        private float GetHoverBob()
        {
            return Mathf.Sin((Time.time + _hoverSeed) * 0.31f) * HoverBobMeters;
        }

        private void StopAgentMotion()
        {
            if (_agent == null || !_agent.enabled)
                return;

            // SeraphimAgentEnablePatch keeps vanilla from re-enabling the agent after the rise, so
            // finding it on here means the patch did not apply; the sampler line counts it.
            if (_initialized && !_rising)
                _frameCost.AddAgentToggle();

            try
            {
                if (_agent.isOnNavMesh)
                    _agent.ResetPath();
                _agent.isStopped = true;
                _agent.enabled = false;
            }
            catch
            {
            }
        }

        private static Vector3 GetPlayerGroundPosition(PlayerControllerB player)
        {
            return player != null ? player.transform.position : Vector3.zero;
        }

        private static Vector3 GetCultistGroundPosition(CongregantAI cultist)
        {
            return cultist != null ? cultist.transform.position : Vector3.zero;
        }

        private static Vector3 GetCultistAimPoint(CongregantAI cultist)
        {
            return cultist != null ? cultist.transform.position + Vector3.up * 1.55f : Vector3.zero;
        }

        private static ulong GetNetworkObjectId(NetworkBehaviour behaviour)
        {
            return behaviour != null && behaviour.NetworkObject != null ? behaviour.NetworkObject.NetworkObjectId : 0UL;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private void KillTarget(PlayerControllerB target)
        {
            if (target == null || target.isPlayerDead)
                return;

            // The death force is rolled once on the authority so every machine agrees on the ragdoll.
            Vector3 bodyVelocity = Vector3.up * 24f + UnityEngine.Random.insideUnitSphere * 6f;
            Vector3 positionOffset = Vector3.up * 1.2f;

            // PlayerControllerB.KillPlayer only runs for the victim's owner (it is gated on IsOwner),
            // so the server announces the execution and the owning client performs the vanilla kill.
            if (NetworkObject == null || !NetworkObject.IsSpawned || !IsServerAuthority())
            {
                if (target.IsOwner)
                    ExecutePlayerLocal(target, bodyVelocity, positionOffset);
                return;
            }

            // The kill now completes on the victim's machine, so the server cannot observe
            // isPlayerDead this frame. Suppress re-targeting until the death replicates back.
            _pendingExecutionClientId = target.actualClientId;
            _hasPendingExecution = true;
            _pendingExecutionExpiry = Time.time + PendingExecutionSuppressSeconds;

            ExecutePlayerClientRpc(
                target.actualClientId,
                bodyVelocity,
                positionOffset,
                ++_playerKillSerial);

            // The routed kill only takes effect on the victim's owner, so nothing applies it if that
            // owner lags out or leaves between this decision and the RPC landing — while the execution
            // VFX have already played on every client. Watch the replicated death flag through to a
            // server-visible outcome instead of assuming it (issue #439).
            StartCoroutine(ConfirmExecutionKill(target, target.actualClientId, bodyVelocity, positionOffset));
        }

        // #419 keeps the kill on the victim's machine so vanilla KillPlayer runs where it is allowed
        // to; this only supervises that decision. A plain delivery miss re-issues the RPC once. If the
        // owner left the session the sequence is aborted rather than completed, because vanilla
        // OnPlayerDC already takes that player out of the round (isPlayerControlled=false,
        // isPlayerDead=false, livingPlayers--, items dropped, body teleported to notSpawnedPosition)
        // — a late kill there would decrement livingPlayers a second time and can falsely trip
        // allPlayersDead/ShipLeaveAutomatically.
        private IEnumerator ConfirmExecutionKill(PlayerControllerB victim, ulong victimClientId, Vector3 bodyVelocity, Vector3 positionOffset)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                float deadline = Time.time + ExecutionConfirmSeconds;
                while (Time.time < deadline)
                {
                    if (victim == null)
                    {
                        ClearPendingExecution(victimClientId);
                        yield break;
                    }

                    if (victim.isPlayerDead)
                    {
                        ClearPendingExecution(victimClientId);
                        yield break;
                    }

                    if (!IsExecutionOwnerPresent(victim, victimClientId))
                    {
                        AbortExecution(victimClientId, "victim owner left the session");
                        yield break;
                    }

                    yield return null;
                }

                if (attempt != 0)
                    continue;

                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Cult] Seraphim execution unconfirmed for client {victimClientId} after {ExecutionConfirmSeconds:F2}s; re-issuing the kill.");
                if (NetworkObject != null && NetworkObject.IsSpawned && IsServerAuthority())
                    ExecutePlayerClientRpc(victimClientId, bodyVelocity, positionOffset, ++_playerKillSerial);
            }

            // Still unconfirmed. If ownership already reverted to this machine the owner-gated vanilla
            // path can run right here; otherwise abandon the execution so the seraphim re-acquires a
            // living target instead of leaving it executed-but-alive.
            if (victim != null && !victim.isPlayerDead && victim.isPlayerControlled && victim.IsOwner)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Cult] Seraphim execution still unconfirmed for client {victimClientId}; completing it locally as the current owner.");
                ExecutePlayerLocal(victim, bodyVelocity, positionOffset);
                ClearPendingExecution(victimClientId);
                yield break;
            }

            AbortExecution(victimClientId, "kill never confirmed");
        }

        private static bool IsExecutionOwnerPresent(PlayerControllerB victim, ulong victimClientId)
        {
            if (victim == null)
                return false;
            if (victim.isPlayerDead)
                return true;

            // Vanilla OnPlayerDC flips these before it removes ownership, so they are the earliest
            // server-visible signal that no machine will ever run the routed kill.
            if (!victim.isPlayerControlled || victim.disconnectedMidGame)
                return false;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening || !manager.IsServer)
                return true;

            return victimClientId == manager.LocalClientId || manager.ConnectedClients.ContainsKey(victimClientId);
        }

        private void AbortExecution(ulong victimClientId, string reason)
        {
            ClearPendingExecution(victimClientId);
            if (NetworkObject != null && NetworkObject.IsSpawned && IsServerAuthority())
                ClearExecutionEffectsClientRpc();
            else
                ClearExecutionEffectsLocal();

            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.Cult] Seraphim execution aborted for client {victimClientId} ({reason}); cleared execution effects and released the target lock.");
        }

        private void ClearPendingExecution(ulong victimClientId)
        {
            if (_hasPendingExecution && _pendingExecutionClientId == victimClientId)
                _hasPendingExecution = false;
        }

        private void ClearExecutionEffectsLocal()
        {
            StopBeamLocal();
            if (_lastExplosionEffect != null)
            {
                Destroy(_lastExplosionEffect);
                _lastExplosionEffect = null;
            }

            if (_lastBloodEffect != null)
            {
                Destroy(_lastBloodEffect);
                _lastBloodEffect = null;
            }
        }

        [ClientRpc]
        private void ExecutePlayerClientRpc(ulong victimClientId, Vector3 bodyVelocity, Vector3 positionOffset, int serial)
        {
            if (serial <= _lastAppliedPlayerKillSerial)
                return;
            _lastAppliedPlayerKillSerial = serial;

            PlayerControllerB victim = FindPlayerByClientId(victimClientId);
            if (victim == null || victim.isPlayerDead || !victim.IsOwner)
                return;

            ExecutePlayerLocal(victim, bodyVelocity, positionOffset);
        }

        private static void ExecutePlayerLocal(PlayerControllerB victim, Vector3 bodyVelocity, Vector3 positionOffset)
        {
            try
            {
                victim.KillPlayer(bodyVelocity, true, CauseOfDeath.Blast, 0, positionOffset);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Seraphim KillPlayer skipped, falling back to blast damage: {e.Message}");
                try
                {
                    victim.DamagePlayer(500, hasDamageSFX: true, callRPC: true, CauseOfDeath.Blast);
                }
                catch (Exception damageError)
                {
                    ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Seraphim blast damage skipped: {damageError.Message}");
                }
            }
        }

        private void KillCultistTarget(CongregantAI target)
        {
            if (target == null || target.isEnemyDead)
                return;

            NetworkObject networkObject = target.NetworkObject;
            GameObject targetObject = target.gameObject;
            try
            {
                target.KillBySeraphimServer();
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Seraphim cultist execution skipped: {e.Message}");
            }

            HideExecutedCultist(targetObject);
            try
            {
                if (networkObject != null && networkObject.IsSpawned)
                    networkObject.Despawn(destroy: true);
                else if (targetObject != null)
                    Destroy(targetObject);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Seraphim cultist despawn skipped: {e.Message}");
                if (targetObject != null)
                    Destroy(targetObject);
            }
        }

        private static void HideExecutedCultist(GameObject targetObject)
        {
            if (targetObject == null)
                return;

            foreach (Renderer renderer in targetObject.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null)
                    renderer.enabled = false;
            }

            foreach (Collider collider in targetObject.GetComponentsInChildren<Collider>(true))
            {
                if (collider != null)
                    collider.enabled = false;
            }
        }

        private void EnsureRuntimeComponentsSafe()
        {
            try
            {
                EnsureRuntimeComponents();
            }
            catch (Exception e)
            {
                if (_runtimeComponentFailureLogged)
                    return;

                _runtimeComponentFailureLogged = true;
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Cult] Seraphim runtime component setup failed; summon AI will continue without blocking on visuals: {e}");
            }
        }

        private void EnsureRuntimeComponents()
        {
            EnsureBehaviourStates();
            if (enemyType == null && ContractAssetLoader.SeraphimEnemyType != null)
                enemyType = ContractAssetLoader.SeraphimEnemyType;

            if (_agent == null)
            {
                _agent = GetComponent<NavMeshAgent>();
                if (_agent != null)
                {
                    agent = _agent;
                    _agent.updateRotation = false;
                    _agent.baseOffset = Mathf.Max(0f, _hoverHeight);
                }
            }

            EnsureCompositeVisual();

            if (_animator == null)
            {
                _animator = GetComponentInChildren<Animator>(true);
                if (_animator != null)
                {
                    if (_animator.runtimeAnimatorController == null && ContractAssetLoader.SeraphimController != null)
                        _animator.runtimeAnimatorController = ContractAssetLoader.SeraphimController;
                    _animator.applyRootMotion = false;
                    _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    _riseAnimationRoots = new[] { _animator.transform.Find("Ophanim/Root") };
                }
            }
            if (creatureAnimator == null)
                creatureAnimator = _animator;

            EnsureVisibilityAura();
            EnsureScanNode();
            EnsureFlightCollisionMode();
            EnsureSeraphimLights();

            if (_voiceSource == null)
                _voiceSource = EnsureAudioSource("SeraphimVoiceSource", new Vector3(0f, 2.5f, 0f), spatialBlend: 1f, volume: 0.92f, minDistance: 18f, maxDistance: 170f, priority: 68);

            if (_beamSource == null)
                _beamSource = EnsureAudioSource("SeraphimBeamSource", new Vector3(0f, 2.5f, 0f), spatialBlend: 1f, volume: 0.88f, minDistance: 16f, maxDistance: 150f, priority: 66);

            if (_chantSource == null)
                _chantSource = EnsureAudioSource("SeraphimGlobalChantSource", Vector3.zero, spatialBlend: 0f, volume: 0.92f, minDistance: 1f, maxDistance: 500f, priority: 42);

            if (_executionImpactSource == null)
                _executionImpactSource = EnsureAudioSource("SeraphimExecutionImpactSource", new Vector3(0f, 2.5f, 0f), spatialBlend: 1f, volume: 1f, minDistance: 14f, maxDistance: 170f, priority: 58);

            if (creatureVoice == null)
                creatureVoice = _voiceSource;
            if (creatureSFX == null)
                creatureSFX = _beamSource;
        }

        private void EnsureCompositeVisual()
        {
            if (_compositeVisualPrepared)
            {
                // #1336: a body configured inactive binds its animators once it is active.
                if (_visualAnimatorRebindPending && gameObject.activeInHierarchy)
                {
                    _visualAnimatorRebindPending = false;
                    for (int i = 0; i < _visualAnimators.Length; i++)
                    {
                        if (_visualAnimators[i] != null)
                            RebindVisualAnimator(_visualAnimators[i]);
                    }
                }
                return;
            }

            Animator[] existingAnimators = GetComponentsInChildren<Animator>(true);
            Renderer[] existingRenderers = GetModelRenderers(transform);
            if (existingAnimators.Length > 0 && existingRenderers.Length > 0)
            {
                EnsureVisualHierarchyActive(existingRenderers, existingAnimators);
                _compositeVisualPrepared = true;
                _visualAnimators = existingAnimators;
                SetModelRenderers(existingRenderers);
                ConfigureVisualAnimators(_visualAnimators);
                _animator = _visualAnimators[0];
                creatureAnimator = _animator;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Using animated Seraphim fullmesh visual; baseRenderers={existingRenderers.Length}, animators={_visualAnimators.Length}.");
                return;
            }

            // #816 retired the Ophanim part prefabs (the loader nulls them), so the fullmesh is the
            // only visual; keep retrying in case its hierarchy is still being built, but say so once.
            if (_visualMissingLogged)
                return;
            _visualMissingLogged = true;
            ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Cult] Seraphim fullmesh visual has no animated renderers yet (animators={existingAnimators.Length}, renderers={existingRenderers.Length}); will retry before showing the enemy.");
        }

        private static Renderer[] GetModelRenderers(Transform root)
        {
            if (root == null)
                return Array.Empty<Renderer>();

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return Array.Empty<Renderer>();

            int count = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                // includeInactive: the host pre-warm (#1336) runs this on an inactive body.
                if (renderer == null || renderer.GetComponentInParent<ScanNodeProperties>(true) != null)
                    continue;
                if (renderer is LineRenderer || renderer is ParticleSystemRenderer)
                    continue;
                count++;
            }

            if (count == renderers.Length)
                return renderers;

            Renderer[] filtered = new Renderer[count];
            int index = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.GetComponentInParent<ScanNodeProperties>(true) != null)
                    continue;
                if (renderer is LineRenderer || renderer is ParticleSystemRenderer)
                    continue;
                filtered[index++] = renderer;
            }

            return filtered;
        }

        private void ConfigureVisualAnimators(Animator[] animators)
        {
            if (animators == null)
                return;

            _riseAnimationRoots = new Transform[animators.Length];
            _eyeBone = null;
            // #1336: the host pre-warm configures an inactive body, where Rebind and Update(0) have
            // nothing to bind; EnsureCompositeVisual runs them after activation instead.
            bool bindNow = gameObject.activeInHierarchy;
            _visualAnimatorRebindPending = !bindNow;

            for (int i = 0; i < animators.Length; i++)
            {
                Animator animator = animators[i];
                if (animator == null)
                    continue;

                _riseAnimationRoots[i] = animator.transform.Find("Ophanim/Root");
                // #1336 S6: once per body (Find sees the inactive pre-warmed hierarchy too).
                if (_eyeBone == null)
                    _eyeBone = animator.transform.Find(MainEyeBonePath);

                animator.enabled = true;
                if (animator.runtimeAnimatorController == null && ContractAssetLoader.SeraphimController != null)
                    animator.runtimeAnimatorController = ContractAssetLoader.SeraphimController;
                ConfigureLoopingSeraphimAnimationClips(animator.runtimeAnimatorController);
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.updateMode = AnimatorUpdateMode.Normal;

                if (bindNow)
                    RebindVisualAnimator(animator);
            }
        }

        private static void RebindVisualAnimator(Animator animator)
        {
            try
            {
                animator.Rebind();
                animator.Update(0f);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Seraphim animator rebind skipped: {e.Message}");
            }
        }

        private void EnsureBehaviourStates()
        {
            if (enemyBehaviourStates != null && enemyBehaviourStates.Length >= 4)
                return;

            enemyBehaviourStates = new[]
            {
                new EnemyBehaviourState { name = "Rising", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Chasing", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Attacking", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Dead", IsAnimTrigger = false, parameterString = string.Empty },
            };
        }

        private AudioSource EnsureAudioSource(string childName, Vector3 localPosition, float spatialBlend, float volume, float minDistance, float maxDistance, int priority)
        {
            Transform child = transform.Find(childName);
            GameObject obj;
            if (child == null)
            {
                obj = new GameObject(childName);
                obj.transform.SetParent(transform, false);
                obj.transform.localPosition = localPosition;
            }
            else
            {
                obj = child.gameObject;
            }

            var source = obj.GetComponent<AudioSource>();
            if (source == null)
                source = obj.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = spatialBlend;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.volume = volume;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.priority = priority;
            source.loop = false;
            return source;
        }

        private void EnsureSeraphimLights()
        {
            if (_passiveLight == null)
            {
                _passiveLight = EnsurePointLight(
                    "SeraphimPassiveLight",
                    Vector3.zero,
                    new Color(1f, 0.88f, 0.58f, 1f),
                    48f,
                    3.2f);
            }

            if (_riseLight == null)
            {
                _riseLight = EnsurePointLight(
                    "SeraphimRisingLight",
                    Vector3.down * 0.15f,
                    new Color(0.92f, 0.98f, 1f, 1f),
                    64f,
                    0f);
            }
        }

        private Light EnsurePointLight(string childName, Vector3 localPosition, Color color, float range, float intensity)
        {
            Transform child = transform.Find(childName);
            GameObject obj;
            if (child == null)
            {
                obj = new GameObject(childName);
                obj.transform.SetParent(transform, false);
                obj.transform.localPosition = localPosition;
            }
            else
            {
                obj = child.gameObject;
            }

            Light light = obj.GetComponent<Light>();
            if (light == null)
                light = obj.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = range;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            light.enabled = true;
            return light;
        }

        /// <summary>#1336 S3: each light is written only when a value changes, and the pulse (still
        /// read from Time.time, so the waveform is unchanged) is sampled at most every
        /// LightPulseIntervalSeconds. A light switched off gets no further writes.</summary>
        private void UpdateSeraphimLights()
        {
            if (_passiveLight == null && _riseLight == null)
                return;

            bool active = !isEnemyDead && gameObject.activeInHierarchy;
            float now = Time.time;
            bool pulseDue = SeraphimFrameBudget.IsTickDue(now, _lightPulseTickTime, SeraphimFrameBudget.LightPulseIntervalSeconds);
            if (pulseDue)
                _lightPulseTickTime = now;

            if (_passiveLight != null)
            {
                bool wasEnabled = _passiveLight.enabled;
                if (wasEnabled != active)
                    _passiveLight.enabled = active;
                if (active)
                {
                    SetLightRange(_passiveLight, _rising ? 58f : 42f);
                    if (pulseDue || !wasEnabled)
                    {
                        float pulse = Mathf.Sin((now + _hoverSeed) * 2.1f) * 0.35f;
                        SetLightIntensity(_passiveLight, (_rising ? 5.4f : _beamActive ? 4.8f : 2.7f) + pulse);
                    }
                }
            }

            if (_riseLight != null)
            {
                bool riseActive = active && _rising;
                bool wasEnabled = _riseLight.enabled;
                if (wasEnabled != riseActive)
                    _riseLight.enabled = riseActive;
                if (riseActive && (pulseDue || !wasEnabled))
                {
                    SetLightIntensity(_riseLight, 8.5f + Mathf.Abs(Mathf.Sin((now + _hoverSeed) * 4.8f)) * 3.5f);
                    SetLightRange(_riseLight, 66f + Mathf.Abs(Mathf.Sin(now * 3.2f)) * 12f);
                }
            }
        }

        private static void SetLightRange(Light light, float range)
        {
            if (light.range != range)
                light.range = range;
        }

        private static void SetLightIntensity(Light light, float intensity)
        {
            if (light.intensity != intensity)
                light.intensity = intensity;
        }

        private void EnsureVisualHierarchyActive(Renderer[] renderers, Animator[] animators)
        {
            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null)
                        SetActiveUpToRoot(renderers[i].transform, transform);
                }
            }

            if (animators != null)
            {
                for (int i = 0; i < animators.Length; i++)
                {
                    if (animators[i] != null)
                        SetActiveUpToRoot(animators[i].transform, transform);
                }
            }
        }

        private static void SetActiveUpToRoot(Transform leaf, Transform root)
        {
            Transform current = leaf;
            // The root itself is never toggled: the host pre-warm (#1336) builds the body inactive
            // and only the summon activates it.
            while (current != null)
            {
                if (current == root)
                    return;
                current.gameObject.SetActive(true);
                current = current.parent;
            }
        }

        private void EnsureFlightCollisionMode()
        {
            if (_collisionPrepared)
                return;

            int keptTriggerColliders = 0;
            int disabledModelColliders = 0;
            Collider[] colliders = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null)
                    continue;

                // includeInactive: the host pre-warm (#1336) runs this on an inactive body.
                if (collider.GetComponentInParent<ScanNodeProperties>(true) != null)
                {
                    collider.enabled = true;
                    collider.isTrigger = true;
                    keptTriggerColliders++;
                    continue;
                }

                if (ShouldKeepFlightTrigger(collider))
                {
                    ConfigureFlightTrigger(collider);
                    keptTriggerColliders++;
                    continue;
                }

                collider.enabled = false;
                disabledModelColliders++;
            }

            Rigidbody body = GetComponent<Rigidbody>();
            if (body == null)
                body = gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            _collisionPrepared = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Seraphim flight collision prepared: triggers={keptTriggerColliders}, disabledModelColliders={disabledModelColliders}.");
        }

        private static bool ShouldKeepFlightTrigger(Collider collider)
        {
            if (collider == null)
                return false;

            if (collider.GetComponent<EnemyAICollisionDetect>() != null)
                return true;

            Transform current = collider.transform;
            while (current != null)
            {
                if (string.Equals(current.name, "SeraphimEnemyHitProxy", StringComparison.OrdinalIgnoreCase))
                    return true;
                current = current.parent;
            }

            return false;
        }

        private void ConfigureFlightTrigger(Collider collider)
        {
            collider.enabled = true;
            collider.isTrigger = true;

            if (collider is CapsuleCollider capsule)
            {
                capsule.center = Vector3.zero;
                capsule.radius = Mathf.Clamp(0.12f * _scale, 0.35f, 1.35f);
                capsule.height = Mathf.Clamp(0.32f * _scale, 0.8f, 3.25f);
            }
            else if (collider is SphereCollider sphere)
            {
                sphere.center = Vector3.zero;
                sphere.radius = Mathf.Clamp(0.14f * _scale, 0.4f, 1.65f);
            }
            else if (collider is BoxCollider box)
            {
                box.center = Vector3.zero;
                float size = Mathf.Clamp(0.28f * _scale, 0.8f, 3.5f);
                box.size = Vector3.one * size;
            }

            Rigidbody body = collider.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.isKinematic = true;
                body.useGravity = false;
            }
        }

        /// <summary>Loader hook (#845 F): builds the aura/beam materials, gradients and ring point
        /// arrays once at asset load, so a summon neither calls <c>Shader.Find</c> nor allocates them.</summary>
        internal static void PrewarmSharedVisuals()
        {
            GetSharedAuraMaterial();
            GetSharedBeamMaterial();
            GetSharedAuraGradient();
            GetSharedBeamGradient();
            GetAuraRingPoints(Vector3.forward);
            GetAuraRingPoints(Vector3.up);
            GetAuraRingPoints(Vector3.right);
        }

        private void EnsureVisibilityAura()
        {
            if (_visibilityAuraRoot != null)
                return;

            GameObject aura = new GameObject("SeraphimVisibilityAura");
            aura.transform.SetParent(transform, false);
            aura.transform.localPosition = Vector3.zero;
            aura.transform.localRotation = Quaternion.identity;
            aura.transform.localScale = Vector3.one;
            _visibilityAuraRoot = aura.transform;
            // A rebuilt aura starts shown and re-checks the camera distance on its next frame.
            _auraVisible = true;
            _auraTickTime = float.NegativeInfinity;
            _auraSegmentCount = 0;

            _auraRings[0] = CreateAuraRing("AuraRingXY", Vector3.forward);
            _auraRings[1] = CreateAuraRing("AuraRingXZ", Vector3.up);
            _auraRings[2] = CreateAuraRing("AuraRingYZ", Vector3.right);
        }

        private LineRenderer CreateAuraRing(string name, Vector3 normalAxis)
        {
            if (_visibilityAuraRoot == null)
                return null;

            GameObject ring = new GameObject(name);
            ring.transform.SetParent(_visibilityAuraRoot, false);
            var line = ring.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.loop = true;
            line.numCapVertices = 4;
            line.numCornerVertices = 4;
            line.widthMultiplier = AuraRingWidth;
            line.alignment = LineAlignment.View;
            line.colorGradient = GetSharedAuraGradient();
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.allowOcclusionWhenDynamic = false;
            Material material = GetSharedAuraMaterial();
            if (material != null)
                line.sharedMaterial = material;

            Vector3[] points = GetAuraRingPoints(normalAxis);
            line.positionCount = points.Length;
            line.SetPositions(points);
            _auraSegmentCount += points.Length;
            return line;
        }

        private static Vector3[] GetAuraRingPoints(Vector3 normalAxis)
        {
            if (normalAxis == Vector3.up)
                return _auraRingPointsXZ ?? (_auraRingPointsXZ = BuildAuraRingPoints(normalAxis, 1.02f));
            if (normalAxis == Vector3.right)
                return _auraRingPointsYZ ?? (_auraRingPointsYZ = BuildAuraRingPoints(normalAxis, 0.88f));
            return _auraRingPointsXY ?? (_auraRingPointsXY = BuildAuraRingPoints(normalAxis, 1.16f));
        }

        private static Vector3[] BuildAuraRingPoints(Vector3 normalAxis, float radius)
        {
            var points = new Vector3[AuraRingSegments];
            for (int i = 0; i < points.Length; i++)
            {
                float angle = (i / (float)points.Length) * Mathf.PI * 2f;
                if (normalAxis == Vector3.up)
                    points[i] = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                else if (normalAxis == Vector3.right)
                    points[i] = new Vector3(0f, Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
                else
                    points[i] = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f);
            }

            return points;
        }

        private static Material GetSharedAuraMaterial()
        {
            if (_sharedAuraMaterial == null)
            {
                _sharedAuraMaterial = CreateGlowLineMaterial("SeraphimVisibilityAuraMaterial", AuraRingColor, AuraRingEmission);
                // HDRP/Unlit draws _UnlitColor, not _BaseColor; the beam keeps its white.
                if (_sharedAuraMaterial != null && _sharedAuraMaterial.HasProperty("_UnlitColor"))
                    _sharedAuraMaterial.SetColor("_UnlitColor", AuraRingColor);
            }
            return _sharedAuraMaterial;
        }

        private static Material GetSharedBeamMaterial()
        {
            if (_sharedBeamMaterial == null)
                _sharedBeamMaterial = CreateGlowLineMaterial("SeraphimHolyBeamMaterial", new Color(1f, 0.94f, 0.42f, 0.94f), 2.4f);
            return _sharedBeamMaterial;
        }

        /// <summary>The aura rings and the beam share one recipe; each is built once per session and
        /// kept through asset unloads.</summary>
        private static Material CreateGlowLineMaterial(string name, Color color, float emission)
        {
            Shader shader = Shader.Find("HDRP/Unlit")
                ?? Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Standard");
            if (shader == null)
                return null;

            var material = new Material(shader) { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            if (material.HasProperty("_EmissionColor"))
                material.SetColor("_EmissionColor", color * emission);
            if (material.HasProperty("_EmissiveColor"))
                material.SetColor("_EmissiveColor", color * emission);
            material.EnableKeyword("_EMISSION");
            return material;
        }

        private void EnsureScanNode()
        {
            if (_scanNode != null)
            {
                // Runs every frame from EnsureRuntimeComponents. The node's fields are set at
                // creation and the glitch keeps its header; only the collider follows _scale, which
                // a client learns from InitializeClientRpc after OnNetworkSpawn built the node.
                if (_scanNodeConfiguredScale != _scale)
                {
                    ConfigureSeraphimScanCollider(_scanNode.gameObject);
                    _scanNodeConfiguredScale = _scale;
                }
                return;
            }

            Transform existing = transform.Find("ScanNode") ?? transform.Find("SeraphimScanNode");
            GameObject scanObject = existing != null
                ? existing.gameObject
                : new GameObject("ScanNode");
            scanObject.name = "ScanNode";
            scanObject.transform.SetParent(transform, false);
            scanObject.transform.localPosition = Vector3.up * Mathf.Clamp(_scale * 0.05f, 0.25f, 1.25f);
            scanObject.transform.localRotation = Quaternion.identity;
            scanObject.transform.localScale = Vector3.one;
            scanObject.layer = ResolveLayer("ScanNode", ScanNodeLayer);
            try
            {
                scanObject.tag = "DoNotSet";
            }
            catch
            {
            }

            ConfigureSeraphimScanCollider(scanObject);
            _scanNodeConfiguredScale = _scale;

            _scanNode = scanObject.GetComponent<ScanNodeProperties>();
            if (_scanNode == null)
                _scanNode = scanObject.AddComponent<ScanNodeProperties>();

            ConfigureSeraphimScanNode(_scanNode, preserveHeader: false);
        }

        private void ConfigureSeraphimScanCollider(GameObject scanObject)
        {
            if (scanObject == null)
                return;

            scanObject.transform.localPosition = Vector3.up * Mathf.Clamp(_scale * 0.05f, 0.25f, 1.25f);

            BoxCollider boxCollider = scanObject.GetComponent<BoxCollider>();
            if (boxCollider != null)
                boxCollider.enabled = false;

            SphereCollider collider = scanObject.GetComponent<SphereCollider>();
            if (collider == null)
                collider = scanObject.AddComponent<SphereCollider>();
            collider.enabled = true;
            collider.isTrigger = true;
            collider.center = Vector3.zero;
            collider.radius = Mathf.Clamp(_scale * 0.36f, 3.5f, 8f);
        }

        private static void ConfigureSeraphimScanNode(ScanNodeProperties scanNode, bool preserveHeader)
        {
            if (scanNode == null)
                return;

            scanNode.gameObject.name = "ScanNode";
            scanNode.gameObject.layer = ResolveLayer("ScanNode", ScanNodeLayer);
            if (!preserveHeader || string.IsNullOrWhiteSpace(scanNode.headerText) || string.Equals(scanNode.headerText, "Ophanim", StringComparison.OrdinalIgnoreCase))
                scanNode.headerText = BuildGlitchScanHeader();
            scanNode.subText = "SIGNAL CORRUPT";
            scanNode.maxRange = 220;
            scanNode.minRange = 1;
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = 1;
            scanNode.creatureScanID = -1;
            scanNode.scrapValue = 0;
        }

        private void UpdateScanNodeGlitch()
        {
            if (_scanNode == null)
                return;

            int tick = ScanHeaderTick();
            if (tick == _scanHeaderTick)
                return;

            _scanHeaderTick = tick;
            ConfigureSeraphimScanNode(_scanNode, preserveHeader: true);
            _scanNode.headerText = ContractHudScramble.GetText(tick, ScanHeaderLength);
        }

        // The contract HUD's ascension strip reads the scramble on this same 4 Hz tick
        // (CongregationEscalationState), so the strip and this header share one cached stream.
        private static int ScanHeaderTick() => (int)(Time.unscaledTime * 4f);

        private static string BuildGlitchScanHeader()
        {
            return ContractHudScramble.GetText(ScanHeaderTick(), ScanHeaderLength);
        }

        private void EnsureBeamLine()
        {
            if (_beamLine != null)
                return;

            GameObject obj = new GameObject("SeraphimBeamLine");
            obj.transform.SetParent(transform, false);
            _beamLine = obj.AddComponent<LineRenderer>();
            _beamLine.useWorldSpace = true;
            _beamLine.positionCount = 2;
            _beamLine.enabled = false;
            _beamLine.numCapVertices = 8;
            _beamLine.numCornerVertices = 4;
            _beamLine.textureMode = LineTextureMode.Tile;
            _beamLine.alignment = LineAlignment.View;
            _beamLine.widthMultiplier = BeamWidth;
            _beamLine.colorGradient = GetSharedBeamGradient();
            Material material = GetSharedBeamMaterial();
            if (material != null)
                _beamLine.sharedMaterial = material;
        }

        private static Gradient GetSharedAuraGradient()
        {
            if (_sharedAuraGradient != null)
                return _sharedAuraGradient;

            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.98f, 0.62f, 1f), 0f),
                    new GradientColorKey(new Color(1f, 0.75f, 0.2f, 1f), 0.5f),
                    new GradientColorKey(new Color(0.85f, 1f, 1f, 1f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0.42f, 0f),
                    new GradientAlphaKey(0.86f, 0.48f),
                    new GradientAlphaKey(0.42f, 1f),
                });
            _sharedAuraGradient = gradient;
            return gradient;
        }

        /// <summary>#1336 S3: the rings turn at most every AuraUpdateIntervalSeconds (the angle is
        /// still read from Time.time) and switch off beyond AuraHideDistance of this peer's camera;
        /// a hidden aura is not rotated.</summary>
        private void UpdateVisibilityAura()
        {
            if (_visibilityAuraRoot == null)
                return;

            float now = Time.time;
            if (SeraphimFrameBudget.IsTickDue(now, _auraTickTime, SeraphimFrameBudget.AuraUpdateIntervalSeconds))
            {
                _auraTickTime = now;
                if (TryGetLocalCameraPosition(out Vector3 cameraPosition))
                {
                    float sqrDistance = (cameraPosition - _visibilityAuraRoot.position).sqrMagnitude;
                    bool visible = SeraphimFrameBudget.ResolveAuraVisible(sqrDistance, _auraVisible);
                    if (visible != _auraVisible)
                        SetAuraRingsEnabled(visible);
                }

                if (_auraVisible)
                {
                    float time = now + _hoverSeed;
                    _visibilityAuraRoot.localRotation = Quaternion.Euler(time * 10.5f, time * 16.5f, time * 7.5f);
                    _frameCost.AddAuraUpdate(_auraSegmentCount);
                }
            }

            if (!_auraVisible)
                _frameCost.AddAuraCulledFrame();
        }

        private void SetAuraRingsEnabled(bool visible)
        {
            _auraVisible = visible;
            for (int i = 0; i < _auraRings.Length; i++)
            {
                if (_auraRings[i] != null)
                    _auraRings[i].enabled = visible;
            }
        }

        /// <summary>The camera this peer renders from: the active (spectator or ship) camera, else
        /// the local player's gameplay camera.</summary>
        private static bool TryGetLocalCameraPosition(out Vector3 position)
        {
            StartOfRound round = StartOfRound.Instance;
            Camera camera = round != null ? round.activeCamera : null;
            if (camera == null)
            {
                GameNetworkManager network = GameNetworkManager.Instance;
                PlayerControllerB localPlayer = network != null ? network.localPlayerController : null;
                camera = localPlayer != null ? localPlayer.gameplayCamera : null;
            }

            if (camera == null)
            {
                position = default;
                return false;
            }

            position = camera.transform.position;
            return true;
        }

        private static Gradient GetSharedBeamGradient()
        {
            if (_sharedBeamGradient != null)
                return _sharedBeamGradient;

            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.96f, 0.58f, 1f), 0f),
                    new GradientColorKey(new Color(0.92f, 1f, 1f, 1f), 0.48f),
                    new GradientColorKey(new Color(1f, 0.83f, 0.24f, 1f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0.82f, 0f),
                    new GradientAlphaKey(1f, 0.5f),
                    new GradientAlphaKey(0.86f, 1f),
                });
            _sharedBeamGradient = gradient;
            return gradient;
        }

        /// <summary>LateUpdate, after the eye aim (#1336 S6): the line from this frame's eye to the
        /// target and the impact effect's pose. The local victim's lift stays in Update.</summary>
        private void UpdateBeamVisual()
        {
            if (!_beamActive)
                return;

            EnsureBeamLine();
            Vector3 start = GetBeamOrigin();
            Vector3 end = ResolveBeamTargetPoint(start);
            if (_beamLine != null)
            {
                _beamLine.enabled = true;
                _beamLine.SetPosition(0, start);
                _beamLine.SetPosition(1, end);
                _beamLine.widthMultiplier = BeamWidth + Mathf.Sin(Time.time * 22f) * BeamPulseWidth;
            }

            if (_beamImpactEffect != null)
            {
                _beamImpactEffect.transform.position = end;
                Vector3 direction = (start - end).normalized;
                if (direction.sqrMagnitude > 0.01f)
                    _beamImpactEffect.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
            }
        }

        private void ApplyLocalBeamLift()
        {
            if (_beamTargetKind != BeamTargetKindPlayer)
                return;

            PlayerControllerB target = FindPlayerByClientId(_beamTargetId);
            PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            if (target == null || localPlayer == null || target != localPlayer || localPlayer.isPlayerDead)
                return;

            Vector3 lift = Vector3.up * (ExecutionLiftMeters / ExecutionHoldSeconds) * Time.deltaTime;
            try
            {
                if (localPlayer.thisController != null && localPlayer.thisController.enabled)
                    localPlayer.thisController.Move(lift);
                else
                    localPlayer.transform.position += lift;
            }
            catch
            {
                localPlayer.transform.position += lift;
            }
        }

        private void StartBeamLocal(int targetKind, ulong targetId, float duration)
        {
            _beamTargetKind = targetKind;
            _beamTargetId = targetId;
            _beamActive = true;
            SetAimTarget(targetKind, targetId);
            EnsureBeamLine();
            if (_beamLine != null)
                _beamLine.enabled = true;

            GameObject effectPrefab = ContractAssetLoader.SeraphimExecutionEffectPrefab
                                      ?? ContractAssetLoader.SeraphimHolyEffectPrefab;
            if (_beamImpactEffect == null && effectPrefab != null)
            {
                Vector3 position = ResolveBeamTargetPoint(transform.position + GetFacingForward() * 20f);
                _beamImpactEffect = Instantiate(effectPrefab, position, Quaternion.identity);
                _beamImpactEffect.name = "SeraphimHolyExecutionCross";
                _beamImpactEffect.transform.localScale = Vector3.one * 1.55f;
                DisableHeavyEffectLights(_beamImpactEffect, rangeMultiplier: 0.45f, intensityMultiplier: 0.45f);
                // Lives until StopBeamLocal destroys it, so the driver only starts its particles.
                SeraphimEffectDriver.Attach(_beamImpactEffect, lifetime: 0f);
            }

            PlayBeamCueLocal(GetBeamOrigin());
            StartCoroutine(StopBeamAfter(duration + 0.35f));
        }

        private IEnumerator StopBeamAfter(float seconds)
        {
            yield return new WaitForSeconds(Mathf.Max(0.1f, seconds));
            StopBeamLocal();
        }

        private void StopBeamLocal()
        {
            _beamActive = false;
            _beamTargetId = 0UL;
            _beamTargetKind = BeamTargetKindPlayer;
            if (_beamLine != null)
                _beamLine.enabled = false;
            if (_beamSource != null && _beamSource.isPlaying)
                _beamSource.Stop();
            if (_beamImpactEffect != null)
            {
                Destroy(_beamImpactEffect);
                _beamImpactEffect = null;
            }
        }

        private Vector3 ResolveBeamTargetPoint(Vector3 fallback)
        {
            return TryResolveTargetPoint(_beamTargetKind, _beamTargetId, out Vector3 point) ? point : fallback;
        }

        /// <summary>The beam's and the eye's target point on this peer: a cultist by network object
        /// id (cached per id), a player by client id. No allocation.</summary>
        private bool TryResolveTargetPoint(int targetKind, ulong targetId, out Vector3 point)
        {
            if (targetKind == BeamTargetKindCultist)
            {
                CongregantAI cultist = FindCultistByNetworkObjectId(targetId);
                point = cultist != null ? GetCultistAimPoint(cultist) : default;
                return cultist != null;
            }

            PlayerControllerB player = FindPlayerByClientId(targetId);
            point = player != null ? GetPlayerAimPoint(player) : default;
            return player != null;
        }

        private void PlayGlobalChantLocal()
        {
            EnsureRuntimeComponentsSafe();
            AudioClip clip = ContractAssetLoader.SeraphimHolyChantClip;
            if (clip == null || _chantSource == null)
                return;

            _chantSource.Stop();
            _chantSource.clip = clip;
            _chantSource.spatialBlend = 0f;
            _chantSource.loop = false;
            _chantSource.volume = 0.92f;
            _chantSource.Play();
        }

        private void PlayVoiceLocal(int clipIndex)
        {
            EnsureRuntimeComponentsSafe();
            AudioClip[] clips = ContractAssetLoader.SeraphimVoiceClips;
            if (clips == null || clips.Length == 0 || _voiceSource == null)
                return;

            int index = Mathf.Clamp(clipIndex, 0, clips.Length - 1);
            AudioClip clip = clips[index];
            if (clip == null)
                return;

            _voiceSource.pitch = UnityEngine.Random.Range(0.96f, 1.04f);
            _voiceSource.PlayOneShot(clip, UnityEngine.Random.Range(0.78f, 0.96f));
            _voiceSource.pitch = 1f;
        }

        private void PlayBeamCueLocal(Vector3 worldPosition)
        {
            EnsureRuntimeComponentsSafe();
            AudioClip clip = ContractAssetLoader.SeraphimBeamCueClip;
            if (clip == null || _beamSource == null)
                return;

            _beamSource.transform.position = worldPosition;
            _beamSource.pitch = UnityEngine.Random.Range(0.92f, 1.04f);
            _beamSource.PlayOneShot(clip, 0.82f);
            _beamSource.pitch = 1f;
        }

        private void PlayExecutionImpactLocal(Vector3 worldPosition)
        {
            EnsureRuntimeComponentsSafe();
            AudioClip clip = ContractAssetLoader.SeraphimExecutionImpactClip;
            AudioSource source = _executionImpactSource != null ? _executionImpactSource : _beamSource;
            if (clip == null || source == null)
                return;

            source.transform.position = worldPosition;
            source.pitch = UnityEngine.Random.Range(0.96f, 1.04f);
            source.PlayOneShot(clip, 1f);
            source.pitch = 1f;
        }

        private void PlayExplosionLocal(Vector3 impact)
        {
            StopBeamLocal();
            GameObject holyPrefab = ContractAssetLoader.SeraphimExecutionEffectPrefab
                                    ?? ContractAssetLoader.SeraphimHolyEffectPrefab;
            if (holyPrefab != null)
            {
                _lastExplosionEffect = Instantiate(holyPrefab, impact, Quaternion.identity);
                _lastExplosionEffect.name = "SeraphimHolyExecutionExplosion";
                _lastExplosionEffect.transform.localScale = Vector3.one * 2.75f;
                DisableHeavyEffectLights(_lastExplosionEffect, rangeMultiplier: 0.75f, intensityMultiplier: 0.65f);
                SeraphimEffectDriver.Attach(_lastExplosionEffect, HolyEffectLifetimeSeconds);
            }

            if (ContractAssetLoader.SeraphimBloodEffectPrefab != null)
            {
                _lastBloodEffect = Instantiate(ContractAssetLoader.SeraphimBloodEffectPrefab, impact, Quaternion.identity);
                _lastBloodEffect.name = "SeraphimBloodExecutionBurst";
                _lastBloodEffect.transform.localScale = Vector3.one * 1.85f;
                DisableHeavyEffectLights(_lastBloodEffect, rangeMultiplier: 0.45f, intensityMultiplier: 0.35f);
                SeraphimEffectDriver.Attach(_lastBloodEffect, BloodEffectLifetimeSeconds);
            }

            PlayBeamCueLocal(impact);
            CreateTimedPointLight(
                "SeraphimExecutionBlastLight",
                impact + Vector3.up * 1.2f,
                new Color(1f, 0.86f, 0.48f, 1f),
                28f,
                10.5f,
                1.25f);
        }

        /// <summary>Starts this peer's rise presentation. The shake lands now; the effect objects are
        /// queued for UpdateRiseVisual to build one per frame (#1336), so the frame that starts the
        /// rise - the host's summon, a client's spawn - does not also pay for them.</summary>
        private void PlayRiseEffectsLocal()
        {
            AdoptPrewarmedRiseEffects();
            _riseEffectSchedule.Queue(Time.frameCount);
            ShakeLocalCameraForRise(_ritualPoint, riseStart: true);
        }

        /// <summary>Takes this peer's pre-warmed rise effects (the host's Phase 3 pool); the dust
        /// only while it was built for this summon's scale and rise duration.</summary>
        private void AdoptPrewarmedRiseEffects()
        {
            if (_pooledRiseEffect != null)
            {
                if (_riseEffectObject != null)
                    Destroy(_riseEffectObject);
                _riseEffectObject = _pooledRiseEffect;
                _riseEffectAdopted = true;
                _pooledRiseEffect = null;
            }

            if (_pooledRiseDust != null
                && _pooledRiseDustRadius == ResolveRiseDustRadius(_scale)
                && _pooledRiseDustEmitSeconds == ResolveRiseDustEmitSeconds(_riseDuration))
            {
                if (_riseDustObject != null)
                    Destroy(_riseDustObject);
                _riseDustObject = _pooledRiseDust;
                _riseDustAdopted = true;
                _pooledRiseDust = null;
            }

            DiscardPrewarmedRiseEffects();
        }

        private void RunRiseEffectStage(SeraphimRiseEffectStage stage)
        {
            long stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            switch (stage)
            {
                case SeraphimRiseEffectStage.HolyEffect:
                    StartRiseHolyEffectLocal();
                    break;
                case SeraphimRiseEffectStage.RiseDust:
                    SpawnRiseDustLocal();
                    break;
                case SeraphimRiseEffectStage.BurstLight:
                    CreateTimedPointLight(
                        "SeraphimRisingBurstLight",
                        GetRiseEffectPosition(_ritualPoint) + Vector3.up * 2.5f,
                        new Color(1f, 0.9f, 0.58f, 1f),
                        34f,
                        9.5f,
                        _riseDuration + 0.9f);
                    break;
                default:
                    return;
            }

            double stageMilliseconds = SummonStepMilliseconds(stageStart);
            if (stageMilliseconds >= SummonStepWarnMilliseconds)
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Timing] Seraphim rise effect {stage} took {stageMilliseconds:0.00}ms");
        }

        /// <summary>The HolyEffect over the ritual point: the adopted pre-warmed copy is activated,
        /// otherwise the prefab is instantiated as before.</summary>
        private void StartRiseHolyEffectLocal()
        {
            Vector3 effectPosition = GetRiseEffectPosition(_ritualPoint);
            GameObject effect = _riseEffectAdopted ? _riseEffectObject : null;
            _riseEffectAdopted = false;
            if (effect != null)
            {
                effect.transform.SetPositionAndRotation(effectPosition, Quaternion.identity);
                effect.SetActive(true);
            }
            else
            {
                GameObject effectPrefab = ResolveRiseEffectPrefab();
                if (effectPrefab == null)
                    return;

                if (_riseEffectObject != null)
                    Destroy(_riseEffectObject);
                effect = Instantiate(effectPrefab, effectPosition, Quaternion.identity);
            }

            _riseEffectObject = effect;
            effect.name = "SeraphimRisingHolyEffect";
            effect.transform.localScale = Vector3.one * Mathf.Clamp(_scale * 0.38f, 2.4f, 7.5f);
            DisableHeavyEffectLights(effect, rangeMultiplier: 0.82f, intensityMultiplier: 0.85f);
            SeraphimEffectDriver.Attach(effect, _riseDuration + 2.25f);
        }

        private static GameObject ResolveRiseEffectPrefab()
        {
            return ContractAssetLoader.SeraphimHolyEffectPrefab
                   ?? ContractAssetLoader.SeraphimExecutionEffectPrefab;
        }

        private static Vector3 GetRiseEffectPosition(Vector3 ritualPoint)
        {
            return ritualPoint + Vector3.up * 1.35f;
        }

        /// <summary>Local camera shake banded by distance from the local player's camera, the vanilla
        /// Landmine.SpawnExplosion pattern: Big/Long/Small when the ground breaks, one Small when the
        /// body settles at hover height.</summary>
        private static void ShakeLocalCameraForRise(Vector3 source, bool riseStart)
        {
            HUDManager hud = HUDManager.Instance;
            PlayerControllerB localPlayer = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            if (localPlayer == null && StartOfRound.Instance != null)
                localPlayer = StartOfRound.Instance.localPlayerController;
            if (hud == null || localPlayer == null || localPlayer.isPlayerDead)
                return;

            Vector3 camera = localPlayer.gameplayCamera != null
                ? localPlayer.gameplayCamera.transform.position
                : localPlayer.transform.position;
            float distance = Vector3.Distance(camera, source);
            if (distance >= RiseShakeSmallMeters)
                return;

            if (!riseStart || distance >= RiseShakeLongMeters)
                hud.ShakeCamera(ScreenShakeType.Small);
            else if (distance >= RiseShakeBigMeters)
                hud.ShakeCamera(ScreenShakeType.Long);
            else
                hud.ShakeCamera(ScreenShakeType.Big);
        }

        /// <summary>Ground dust ring plus a debris burst where the body breaks the ground, built from
        /// code-configured particle systems on an existing particle material (no new asset). The
        /// adopted pre-warmed dust (#1336) is activated instead of built.</summary>
        private void SpawnRiseDustLocal()
        {
            GameObject root = _riseDustAdopted ? _riseDustObject : null;
            _riseDustAdopted = false;
            if (root == null)
            {
                root = BuildRiseDustObject(_ritualPoint, _scale, _riseDuration);
                if (root == null)
                    return;

                if (_riseDustObject != null)
                    Destroy(_riseDustObject);
            }

            root.transform.position = _ritualPoint + Vector3.up * 0.25f;
            root.SetActive(true);
            _riseDustObject = root;
            SeraphimEffectDriver.Attach(root, ResolveRiseDustEmitSeconds(_riseDuration) + RiseDustMaxLifetimeSeconds + 0.5f);
        }

        /// <summary>The dust ring and debris burst under an inactive root; null without the vanilla
        /// material.</summary>
        private static GameObject BuildRiseDustObject(Vector3 ritualPoint, float scale, float riseDuration)
        {
            Material material = ResolveRiseDustMaterial();
            if (material == null)
                return null;

            float ringRadius = ResolveRiseDustRadius(scale);
            var root = new GameObject("SeraphimRiseDust");
            root.SetActive(false);
            root.transform.position = ritualPoint + Vector3.up * 0.25f;
            BuildRiseDustRing(root.transform, material, ringRadius, ResolveRiseDustEmitSeconds(riseDuration));
            BuildRiseDebrisBurst(root.transform, material, ringRadius);
            return root;
        }

        private static float ResolveRiseDustRadius(float scale)
        {
            return Mathf.Clamp(scale * 0.55f, 4f, 10f);
        }

        private static float ResolveRiseDustEmitSeconds(float riseDuration)
        {
            return Mathf.Max(1f, riseDuration * 0.8f);
        }

        /// <summary>The vanilla landmine explosion's "Smoke" particle material
        /// (<c>StartOfRound.explosionPrefab</c>); no dust if the vanilla material is unavailable.</summary>
        private static Material ResolveRiseDustMaterial()
        {
            if (_riseDustMaterial != null)
                return _riseDustMaterial;

            GameObject explosion = StartOfRound.Instance != null ? StartOfRound.Instance.explosionPrefab : null;
            _riseDustMaterial = FindParticleMaterial(explosion, "Smoke");
            return _riseDustMaterial;
        }

        private static Material FindParticleMaterial(GameObject root, string objectName)
        {
            if (root == null)
                return null;

            foreach (ParticleSystemRenderer renderer in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                if (renderer == null || renderer.sharedMaterial == null)
                    continue;
                if (objectName == null || string.Equals(renderer.gameObject.name, objectName, StringComparison.Ordinal))
                    return renderer.sharedMaterial;
            }

            return null;
        }

        private static void BuildRiseDustRing(Transform parent, Material material, float radius, float emitSeconds)
        {
            ParticleSystem dust = CreateRiseParticleSystem(parent, "SeraphimRiseDustRing", material);
            var main = dust.main;
            main.duration = emitSeconds;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.4f, RiseDustMaxLifetimeSeconds);
            main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 9f);
            main.startSize = new ParticleSystem.MinMaxCurve(2.5f, 5.5f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.46f, 0.4f, 0.33f, 0.55f),
                new Color(0.32f, 0.29f, 0.25f, 0.42f));
            main.gravityModifier = -0.03f;
            main.maxParticles = 320;

            var emission = dust.emission;
            emission.rateOverTime = 34f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)80) });

            var shape = dust.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = radius;
            shape.radiusThickness = 0.2f;
            shape.arc = 360f;
            shape.rotation = new Vector3(-90f, 0f, 0f);

            var limit = dust.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = new ParticleSystem.MinMaxCurve(1.5f);
            limit.dampen = 0.08f;

            var size = dust.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.6f, 1f, 1.9f));

            SetFadeOverLifetime(dust, peakAt: 0.15f);
        }

        private static void BuildRiseDebrisBurst(Transform parent, Material material, float radius)
        {
            ParticleSystem debris = CreateRiseParticleSystem(parent, "SeraphimRiseDebris", material);
            var main = debris.main;
            main.duration = 1f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.4f, 2.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(9f, 18f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.2f, 0.65f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.2f, 0.16f, 0.12f, 1f),
                new Color(0.33f, 0.27f, 0.2f, 1f));
            main.gravityModifier = 1.8f;
            main.maxParticles = 140;

            var emission = debris.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, (short)90),
                new ParticleSystem.Burst(0.3f, (short)40),
            });

            var shape = debris.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 30f;
            shape.radius = radius * 0.6f;
            shape.rotation = new Vector3(-90f, 0f, 0f);

            SetFadeOverLifetime(debris, peakAt: 0.01f);
        }

        private static ParticleSystem CreateRiseParticleSystem(Transform parent, string name, Material material)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            ParticleSystem system = obj.AddComponent<ParticleSystem>();
            var main = system.main;
            main.playOnAwake = false;
            main.loop = false;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var renderer = obj.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sharedMaterial = material;
            return system;
        }

        private static void SetFadeOverLifetime(ParticleSystem system, float peakAt)
        {
            var colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, Mathf.Clamp(peakAt, 0.01f, 0.9f)),
                    new GradientAlphaKey(0f, 1f),
                });
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);
        }

        private void CreateTimedPointLight(string name, Vector3 position, Color color, float range, float intensity, float lifetime)
        {
            GameObject lightObject = new GameObject(name);
            lightObject.transform.position = position;
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = Mathf.Max(1f, range);
            light.intensity = Mathf.Max(0f, intensity);
            light.shadows = LightShadows.None;
            RegisterTransientLight(light);
            StartCoroutine(FadeAndDestroyLight(light, Mathf.Max(0.1f, lifetime)));
        }

        private IEnumerator FadeAndDestroyLight(Light light, float lifetime)
        {
            if (light == null)
                yield break;

            float startIntensity = light.intensity;
            float elapsed = 0f;
            while (elapsed < lifetime && light != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / lifetime);
                light.intensity = Mathf.Lerp(startIntensity, 0f, t * t);
                yield return null;
            }

            if (light != null)
                Destroy(light.gameObject);
        }

        /// <summary>Effect prefabs bring their own lights: no shadows, dimmed, and registered for the
        /// frame-cost sampler, since they live outside this body's hierarchy.</summary>
        private void DisableHeavyEffectLights(GameObject root, float rangeMultiplier, float intensityMultiplier)
        {
            if (root == null)
                return;

            root.GetComponentsInChildren(true, _lightScratch);
            for (int i = 0; i < _lightScratch.Count; i++)
            {
                Light light = _lightScratch[i];
                if (light == null)
                    continue;
                light.shadows = LightShadows.None;
                light.range *= Mathf.Clamp01(rangeMultiplier);
                light.intensity *= Mathf.Clamp01(intensityMultiplier);
                RegisterTransientLight(light);
            }
            _lightScratch.Clear();
        }

        private void PlayAnimationLocal(int animationId, double? startedAt = null, bool reconstructRisePose = false)
        {
            if (animationId == AnimAwakening &&
                (!_rising || isEnemyDead || GetRiseProgress() >= 1f || _lockedAnimationId == AnimAwakening))
                return;

            EnsureRuntimeComponentsSafe();
            string state = GetAnimationState(animationId);
            if (string.IsNullOrWhiteSpace(state))
                return;

            bool loops = IsLoopingSeraphimAnimation(animationId);
            bool preserveLoopTime = false;
            if (_lockedAnimationLoops && string.Equals(_lockedAnimationState, state, StringComparison.Ordinal))
                preserveLoopTime = loops;

            if (!preserveLoopTime)
                _lockedAnimationStartedAt = animationId == AnimAwakening
                    ? _riseStartNetworkTime : startedAt ?? GetNetworkTime();
            if (!string.Equals(_lockedAnimationState, state, StringComparison.Ordinal))
            {
                _lockedAnimationShortHash = Animator.StringToHash(state);
                _lockedAnimationFullHash = Animator.StringToHash("Base Layer." + state);
            }
            _lockedAnimationState = state;
            _lockedAnimationId = animationId;
            // #1336 S6: the eye keeps an attack target only through the attack's start and loop.
            if (animationId != AnimAttackStart && animationId != AnimAttackLoop)
                ClearAimTarget();
            _lockedAnimationLoops = loops;
            _riseRootCorrectionActive = animationId == AnimAwakening ||
                (IsInitialRiseIdle() && GetNetworkTime() < _lockedAnimationStartedAt + 2f * RiseHandoffSeconds);
            _lockedAnimationRequestedFrame = Time.frameCount;
            bool playedAny = false;
            if (_visualAnimators != null && _visualAnimators.Length > 0)
            {
                for (int i = 0; i < _visualAnimators.Length; i++)
                {
                    Animator animator = _visualAnimators[i];
                    if (animator == null)
                        continue;

                    try
                    {
                        PlayAnimatorState(animator, state, _lockedAnimationStartedAt, loops, reconstructRisePose);
                        playedAny = true;
                    }
                    catch (Exception e)
                    {
                        ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Seraphim part animation '{state}' skipped: {e.Message}");
                    }
                }
            }

            if (playedAny || _animator == null)
                return;

            try
            {
                PlayAnimatorState(_animator, state, _lockedAnimationStartedAt, loops, reconstructRisePose);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Seraphim animation '{state}' skipped: {e.Message}");
            }
        }

        private void MaintainLockedAnimationState()
        {
            // A Play requested this frame (an RPC runs before Update) is not visible in the state
            // info until the animator updates, so re-checking now would replay it at the old time.
            if (string.IsNullOrWhiteSpace(_lockedAnimationState) || _lockedAnimationRequestedFrame == Time.frameCount)
                return;

            bool maintainedAny = false;
            if (_visualAnimators != null && _visualAnimators.Length > 0)
            {
                for (int i = 0; i < _visualAnimators.Length; i++)
                {
                    Animator animator = _visualAnimators[i];
                    if (animator == null)
                        continue;

                    EnsureAnimatorState(animator);
                    maintainedAny = true;
                }
            }

            if (!maintainedAny && _animator != null)
                EnsureAnimatorState(_animator);
        }

        /// <summary>Owns single-play states through their last frame. Authored loops run continuously;
        /// only the legacy fallback's non-looping clips need a once-per-cycle re-seat.</summary>
        private void EnsureAnimatorState(Animator animator)
        {
            if (animator == null || animator.runtimeAnimatorController == null)
                return;

            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            bool inLockedState = (info.shortNameHash == _lockedAnimationShortHash || info.fullPathHash == _lockedAnimationFullHash)
                && !animator.IsInTransition(0);
            if (_lockedAnimationLoops)
            {
                if (IsInitialRiseIdle() && animator.IsInTransition(0))
                {
                    AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
                    if (next.shortNameHash == _lockedAnimationShortHash || next.fullPathHash == _lockedAnimationFullHash)
                        return;
                }
                if (inLockedState && (info.loop || info.normalizedTime < 1f))
                    return;

                PlayAnimatorState(animator, _lockedAnimationState, _lockedAnimationStartedAt, loops: true);
                return;
            }

            if (!inLockedState)
                PlayAnimatorState(animator, _lockedAnimationState, _lockedAnimationStartedAt, loops: false);
            else
                SetSinglePlaySpeed(animator, _lockedAnimationState, info.normalizedTime);
        }

        /// <summary>Normal Play requests evaluate in the animator's update. A joiner inside the
        /// rise handoff privately samples its terminal source pose before starting the same blend.</summary>
        private void PlayAnimatorState(Animator animator, string state, double startedAt, bool loops, bool reconstructRisePose = false)
        {
            if (animator == null || string.IsNullOrWhiteSpace(state) || animator.runtimeAnimatorController == null)
                return;

            animator.enabled = true;
            float clipLength = FindAnimatorClipLength(animator, state);
            float duration = _lockedAnimationId == AnimAwakening ? _riseDuration : clipLength;
            float elapsed = Mathf.Max(0f, (float)(GetNetworkTime() - startedAt));
            if (IsInitialRiseIdle() && elapsed < RiseHandoffSeconds)
            {
                // Keep the clock-owned handoff identical for the sidecar and legacy fallback.
                if (reconstructRisePose)
                {
                    animator.Play(GetAnimationState(AnimAwakening), 0, 0.9999f);
                    animator.Update(0f);
                }
                animator.speed = 1f;
                animator.CrossFadeInFixedTime(state, RiseHandoffSeconds, 0, elapsed, elapsed / RiseHandoffSeconds);
                return;
            }
            float normalizedTime = loops
                ? GetClockDrivenLoopNormalizedTime(animator, state, startedAt)
                : duration > 0.01f ? Mathf.Min(elapsed / duration, 0.9999f) : 0f;
            if (loops)
                animator.speed = 1f;
            else
                SetSinglePlaySpeed(animator, state, normalizedTime);
            animator.Play(state, 0, normalizedTime);
        }

        private bool IsInitialRiseIdle()
        {
            return _initialized && _lockedAnimationId == AnimIdle &&
                _lockedAnimationStartedAt == _riseStartNetworkTime + _riseDuration;
        }

        private void SetSinglePlaySpeed(Animator animator, string state, float normalizedTime)
        {
            float clipLength = FindAnimatorClipLength(animator, state);
            if (clipLength <= 0.01f)
            {
                animator.speed = 1f;
                return;
            }

            float duration = _lockedAnimationId == AnimAwakening ? _riseDuration : clipLength;
            float speed = clipLength / Mathf.Max(0.1f, duration);
            // Stop just before the demonstration controller's exit, including on a long frame.
            // The clock owner chooses the successor; maintenance never replays from that successor.
            if (Time.deltaTime > 0f)
                speed = Mathf.Min(speed, Mathf.Max(0f, 0.9999f - normalizedTime) * clipLength / Time.deltaTime);
            animator.speed = speed;
        }

        private static void ConfigureLoopingSeraphimAnimationClips(RuntimeAnimatorController controller)
        {
            if (controller == null)
                return;

            try
            {
                AnimationClip[] clips = controller.animationClips;
                if (clips == null || clips.Length == 0)
                    return;

                int configured = 0;
                for (int i = 0; i < clips.Length; i++)
                {
                    AnimationClip clip = clips[i];
                    if (clip == null || !IsLoopingSeraphimAnimation(clip.name))
                        continue;

                    if (clip.wrapMode != WrapMode.Loop)
                    {
                        clip.wrapMode = WrapMode.Loop;
                        configured++;
                    }
                }

                if (configured > 0)
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Seraphim animation clips forced to loop: {configured}.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Seraphim loop clip configuration skipped: {e.Message}");
            }
        }

        private float GetClockDrivenLoopNormalizedTime(Animator animator, string state, double loopStartedAt)
        {
            float clipLength = FindAnimatorClipLength(animator, state);
            if (clipLength <= 0.01f)
                return 0f;

            float elapsed = Mathf.Max(0f, (float)(GetNetworkTime() - loopStartedAt));
            return Mathf.Repeat(elapsed / clipLength, 1f);
        }

        /// <summary>Clip lengths by name, read from <c>controller.animationClips</c> (which
        /// allocates a fresh array per read) once per controller instead of once per lookup.</summary>
        private float FindAnimatorClipLength(Animator animator, string state)
        {
            RuntimeAnimatorController controller = animator != null ? animator.runtimeAnimatorController : null;
            if (controller == null || string.IsNullOrWhiteSpace(state))
                return 0f;

            if (!_clipLengthsByController.TryGetValue(controller, out Dictionary<string, float> lengths))
            {
                lengths = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                AnimationClip[] clips = controller.animationClips;
                if (clips != null)
                {
                    for (int i = 0; i < clips.Length; i++)
                    {
                        AnimationClip clip = clips[i];
                        if (clip != null && !lengths.ContainsKey(clip.name))
                            lengths.Add(clip.name, Mathf.Max(0f, clip.length));
                    }
                }
                _clipLengthsByController.Add(controller, lengths);
            }

            return lengths.TryGetValue(state, out float length) ? length : 0f;
        }


        private static bool IsLoopingSeraphimAnimation(int animationId)
        {
            return animationId == AnimIdle ||
                   animationId == AnimIdleFast ||
                   animationId == AnimAttackLoop;
        }

        private static bool IsLoopingSeraphimAnimation(string state)
        {
            return string.Equals(state, GetAnimationState(AnimIdle), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(state, GetAnimationState(AnimIdleFast), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(state, GetAnimationState(AnimAttackLoop), StringComparison.OrdinalIgnoreCase);
        }

        private static string GetAnimationState(int animationId)
        {
            switch (animationId)
            {
                case AnimAwakening:
                    return "Ophanim|awakening_2";
                case AnimIdle:
                    return "Ophanim|idle_rotation_2";
                case AnimIdleFast:
                    return "Ophanim|idle_fast_rotation_2";
                case AnimAttackStart:
                    return "Ophanim|atack_wheel_start_2";
                case AnimAttackLoop:
                    return "Ophanim|atack_wheel_2";
                case AnimAttackEnd:
                    return "Ophanim|atack_wheel_end_2";
                default:
                    return string.Empty;
            }
        }

        private void PlayRandomVoiceNetworked()
        {
            AudioClip[] clips = ContractAssetLoader.SeraphimVoiceClips;
            if (clips == null || clips.Length == 0)
                return;

            int index = UnityEngine.Random.Range(0, clips.Length);
            PlayVoiceClientRpc(index);
        }

        /// <summary>Host: turns the root's yaw toward <paramref name="target"/>; the root stays
        /// upright on host and clients alike (vanilla sync carries yaw only) and the eye supplies
        /// the pitch (#1336 S6).</summary>
        private void FacePosition(Vector3 target, float degreesPerSecond)
        {
            Vector3 direction = target - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f)
                return;

            Quaternion look = ToRootRotation(Quaternion.LookRotation(direction, Vector3.up));
            transform.rotation = Quaternion.RotateTowards(transform.rotation, look, Mathf.Max(1f, degreesPerSecond) * Time.deltaTime);
        }

        /// <summary>The root rotation that makes the model face along <paramref name="facing"/>: the
        /// sidecar model looks along its root's -Z (its eye's rest gaze), the legacy fullmesh along
        /// +Z. Every peer derives it from its own eye bone.</summary>
        private Quaternion ToRootRotation(Quaternion facing)
        {
            return _eyeBone != null ? facing * Quaternion.Euler(0f, SeraphimEyeAim.SidecarFacingYawDegrees, 0f) : facing;
        }

        /// <summary>Where the model faces (see <see cref="ToRootRotation"/>).</summary>
        private Vector3 GetFacingForward()
        {
            return _eyeBone != null ? -transform.forward : transform.forward;
        }

        /// <summary>The central eye, as animated and aimed this frame (#1336 S6); the legacy fullmesh
        /// has no eye bone and keeps its root offset.</summary>
        private Vector3 GetBeamOrigin()
        {
            if (_eyeBone != null)
                return _eyeBone.position;
            return transform.position + transform.up * Mathf.Clamp(_scale * 0.28f, 2f, 6f);
        }

        private static PlayerControllerB FindPlayerByClientId(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && player.actualClientId == clientId)
                    return player;
            }

            return null;
        }

        /// <summary>Runs every beam frame, so the answer is kept per target id: the SpawnedObjects
        /// lookup is a dictionary read, and the scene-wide FindObjectsOfType fallback runs at most
        /// once per id.</summary>
        private CongregantAI FindCultistByNetworkObjectId(ulong networkObjectId)
        {
            if (networkObjectId == 0UL)
                return null;

            if (networkObjectId != _beamCultistLookupId)
            {
                _beamCultistLookupId = networkObjectId;
                _beamCultistLookup = null;
                _beamCultistScanned = false;
            }
            else if (_beamCultistLookup != null)
            {
                return _beamCultistLookup;
            }

            CongregantAI found = null;
            try
            {
                NetworkManager manager = NetworkManager.Singleton;
                var spawned = manager != null && manager.SpawnManager != null ? manager.SpawnManager.SpawnedObjects : null;
                if (spawned != null &&
                    spawned.TryGetValue(networkObjectId, out NetworkObject networkObject) &&
                    networkObject != null)
                {
                    found = networkObject.GetComponent<CongregantAI>();
                }
            }
            catch
            {
            }

            if (found == null && !_beamCultistScanned)
            {
                _beamCultistScanned = true;
                CongregantAI[] cultists = FindObjectsOfType<CongregantAI>();
                for (int i = 0; i < cultists.Length; i++)
                {
                    CongregantAI cultist = cultists[i];
                    if (cultist != null && GetNetworkObjectId(cultist) == networkObjectId)
                    {
                        found = cultist;
                        break;
                    }
                }
            }

            _beamCultistLookup = found;
            return found;
        }

        private static Vector3 GetPlayerAimPoint(PlayerControllerB player)
        {
            if (player == null)
                return Vector3.zero;
            if (player.gameplayCamera != null && player == (GameNetworkManager.Instance?.localPlayerController ?? StartOfRound.Instance?.localPlayerController))
                return player.gameplayCamera.transform.position;
            return player.transform.position + Vector3.up * 1.45f;
        }

        private static double GetNetworkTime()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null && manager.IsListening)
                return manager.ServerTime.Time;
            return Time.time;
        }

        private bool IsServerAuthority()
        {
            if (NetworkManager.Singleton == null)
                return true;
            return NetworkManager.Singleton.IsServer || IsServer;
        }

        [ClientRpc]
        private void InitializeClientRpc(Vector3 ritualPoint, Quaternion ritualRotation, float scale, float hoverHeight, float riseDuration, double riseStartNetworkTime, ClientRpcParams clientRpcParams = default)
        {
            // The host ran InitializeServer with these values and started its own presentation there.
            if (IsServerAuthority() && _initialized)
                return;
            // The late-join resend repeats on a retry schedule; a repeat of the same summon is a no-op.
            if (_initialized && _riseStartNetworkTime == riseStartNetworkTime)
                return;

            _ritualPoint = ritualPoint;
            _ritualRotation = ritualRotation;
            _scale = Mathf.Clamp(scale, 1f, 24f);
            _hoverHeight = Mathf.Clamp(hoverHeight, 6f, 90f);
            _riseDuration = Mathf.Clamp(riseDuration, 1f, 12f);
            _riseStartPosition = ritualPoint - Vector3.up * Mathf.Max(7f, _scale * 1.05f);
            _hoverBasePosition = ritualPoint + Vector3.up * _hoverHeight;
            _riseStartNetworkTime = riseStartNetworkTime;
            _initialized = true;
            // Normally already true: the scale is part of the spawn snapshot (set before Spawn).
            transform.localScale = Vector3.one * _scale;
            EnsureRuntimeComponentsSafe();

            float progress = GetRiseProgress();
            if (progress >= 1f)
            {
                // Joined after the rise: the spawn snapshot holds the live position and vanilla sync
                // keeps it current, so nothing replays and the body is not moved.
                _rising = false;
                inSpecialAnimation = false;
                if (!_serverPositionSynced)
                    serverPosition = transform.position;
                targetYRotation = transform.eulerAngles.y;
                if (!isEnemyDead && (_lockedAnimationId < 0 || _lockedAnimationId == AnimAwakening))
                    PlayAnimationLocal(AnimIdle, _riseStartNetworkTime + _riseDuration, reconstructRisePose: true);
                return;
            }

            _rising = true;
            inSpecialAnimation = true;
            float eased = progress * progress * (3f - 2f * progress);
            transform.position = GetRisePosition(eased);
            transform.rotation = ToRootRotation(ritualRotation);
            PlayGlobalChantLocal();
            if (!isEnemyDead && _lockedAnimationId < 0)
                PlayAnimationLocal(AnimAwakening);
            PlayRiseEffectsLocal();
        }

        private static int ResolveLayer(string layerName, int fallback)
        {
            int layer = LayerMask.NameToLayer(layerName);
            return layer >= 0 ? layer : fallback;
        }

        [ClientRpc]
        private void PlayAnimationClientRpc(int animationId)
        {
            PlayAnimationLocal(animationId);
        }

        /// <summary>Late-join half of the animation lock: plays the server's locked state only when it
        /// is not already the local one, so the retried resend never restarts a playing state. Also
        /// carries <c>isEnemyDead</c>, which no other resend does, and the attack's aim target
        /// (#1336 S6), applied before the early returns so a joiner mid-attack aims its eye.</summary>
        [ClientRpc]
        private void ResyncAnimationClientRpc(int animationId, bool dead, int aimTargetKind, ulong aimTargetId, bool hasAimTarget, ClientRpcParams clientRpcParams = default)
        {
            if (!dead && hasAimTarget)
                SetAimTarget(aimTargetKind, aimTargetId);
            else
                ClearAimTarget();
            if (dead && !isEnemyDead)
                ApplyReplicatedDeath();
            // A server idle resend can arrive before this peer reaches the shared deadline.
            // Combat/death may interrupt the rise, but idle cannot shorten its awakening.
            if (!dead && _rising && _lockedAnimationId == AnimAwakening &&
                (animationId == AnimIdle || animationId == AnimIdleFast))
                return;
            if (animationId == _lockedAnimationId)
                return;

            PlayAnimationLocal(animationId);
        }

        [ClientRpc]
        private void PlayVoiceClientRpc(int clipIndex)
        {
            PlayVoiceLocal(clipIndex);
        }

        /// <summary>Once per attack, at its start: the cue's audio and, from #1336 S6, the target the
        /// eye turns to (the same kind and id BeginBeam sends).</summary>
        [ClientRpc]
        private void PlayBeamCueClientRpc(Vector3 worldPosition, int targetKind, ulong targetId)
        {
            SetAimTarget(targetKind, targetId);
            PlayBeamCueLocal(worldPosition);
        }

        [ClientRpc]
        private void PlayExecutionImpactClientRpc(Vector3 worldPosition)
        {
            PlayExecutionImpactLocal(worldPosition);
        }

        [ClientRpc]
        private void BeginBeamClientRpc(int targetKind, ulong targetId, float duration)
        {
            StartBeamLocal(targetKind, targetId, duration);
        }

        [ClientRpc]
        private void EndBeamClientRpc()
        {
            StopBeamLocal();
        }

        // Broadcast cleanup for an execution that was announced everywhere but never resolved into a
        // real death (issue #439): the beam is stopped and the one-shot execution effects are torn
        // down early instead of being left over a victim that is still standing.
        [ClientRpc]
        private void ClearExecutionEffectsClientRpc()
        {
            ClearExecutionEffectsLocal();
        }

        [ClientRpc]
        private void ExplodeBeamClientRpc(int targetKind, ulong targetId, Vector3 impact)
        {
            _beamTargetKind = targetKind;
            _beamTargetId = targetId;
            SetAimTarget(targetKind, targetId);
            PlayExplosionLocal(impact);
        }

        /// <summary>Stands in for the effect prefabs' own controller scripts (HolyEffect, HolyEffect5,
        /// BloodEffect3), which are missing from the bundle: starts every active particle system,
        /// fades the effect's lights and destroys it after its lifetime. Unity still warns about the
        /// missing script on instantiate; removing that reference needs a bundle edit.</summary>
        private sealed class SeraphimEffectDriver : MonoBehaviour
        {
            private Light[] _lights = Array.Empty<Light>();
            private float[] _startIntensities = Array.Empty<float>();
            private float _lifetime;
            private float _elapsed;

            /// <param name="lifetime">Seconds until the effect is destroyed with its lights faded out;
            /// zero or less only starts the particles and leaves the lifetime to the caller.</param>
            internal static void Attach(GameObject effect, float lifetime)
            {
                if (effect == null)
                    return;

                effect.AddComponent<SeraphimEffectDriver>().Begin(lifetime);
            }

            private void Begin(float lifetime)
            {
                _lifetime = lifetime;
                foreach (ParticleSystem system in GetComponentsInChildren<ParticleSystem>())
                {
                    if (system != null && !system.isPlaying)
                        system.Play(withChildren: false);
                }

                if (lifetime <= 0f)
                {
                    enabled = false;
                    return;
                }

                _lights = GetComponentsInChildren<Light>();
                _startIntensities = new float[_lights.Length];
                for (int i = 0; i < _lights.Length; i++)
                    _startIntensities[i] = _lights[i] != null ? _lights[i].intensity : 0f;
                enabled = _lights.Length > 0;
                Destroy(gameObject, lifetime);
            }

            private void Update()
            {
                _elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(_elapsed / _lifetime);
                float fade = 1f - t * t;
                for (int i = 0; i < _lights.Length; i++)
                {
                    if (_lights[i] != null)
                        _lights[i].intensity = _startIntensities[i] * fade;
                }
            }
        }
    }
}
