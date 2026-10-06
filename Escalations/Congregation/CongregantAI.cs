using System;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core.Compat;
namespace Y4NGZCompany.Escalations.Congregation
{
    internal enum CultistRole
    {
        Red,
        Black,
        White
    }

    internal enum CultistRitualPhase
    {
        Traveling,
        Phase1Idle,
        Phase2RedPray,
        Phase3AllPray,
        Stare,
        Aggro,
        Phase4Demon,
        Ruined,
        Fleeing
    }

    public sealed class CongregantAI : EnemyAI
    {
        private const float TravelMoveSpeed = 1.65f;
        private const float VisualWalkFullBlendSpeed = 1.35f;
        private const float MaxAnimationMatchedAggroMoveSpeed = 2.25f;
        private const float SeraphimFleeMoveSpeed = 4.6f;
        private const float FootstepMoveThreshold = 0.45f;
        private const float AttackImpactVolume = 0.82f;
        private const float AttackCancelFadeSeconds = 0.12f;
        // DrumSwing (2.000 s, contact at 1.200 s) matches the drum loop at playback speed 1.
        private const float DrumAttackSpeed = 1f;
        private const double DrumBeatSeconds = 2.0;
        private const float DrumBeatNormalizedTime = 36f / 60f;
        private const float DrumPhaseTolerance = 0.02f;
        // NavMeshAgent.OnEnable/Warp log "Failed to create agent because it is not close enough to
        // the NavMesh" for an off-mesh point, so the agent is only enabled or warped onto a sampled hit.
        private const float AgentEnableSampleRadius = 2f;
        private const float AgentRecoverSampleRadius = 14f;
        private const int ScanNodeLayer = 22;
        private const int ExpectedVisualRevision = 12;
        // Vanilla's PlayFootstepServer/PlayFootstepLocal numbers, as WhistleblowerEnemyAI uses them:
        // noise 17 m at 0.4 walking and 22 m at 0.6 running, audible steps at 0.6 and 0.9.
        private const float WalkFootstepNoiseRange = 17f;
        private const float WalkFootstepNoiseLoudness = 0.4f;
        private const float RunFootstepNoiseRange = 22f;
        private const float RunFootstepNoiseLoudness = 0.6f;
        private const float WalkFootstepVolume = 0.6f;
        private const float RunFootstepVolume = 0.9f;
        private const float RunFootstepBlendThreshold = 0.5f;
        // Vanilla's four-player ceiling on simultaneous stepping bodies; see IsAudibleFootstepEmitter.
        private const int MaxAudibleFootstepEmitters = 4;
        private const float LocalAudioSourceHeight = 0.98f;
        // Vanilla tags a footstep the local player witnessed with 6.
        private const int FootstepNoiseId = 6;
        // Clients read the vanilla serverPosition/SmoothDamp path, which lands a 0.35 m step every
        // one or two 0.2 s AI intervals; ten 0.04 s visual ticks average across two of them.
        private const int ReplicatedMoveSampleCount = 10;
        private const float TravelFallbackSampleRadius = 4f;
        private const int TravelRouteSpot = 0;
        private const int TravelRouteSampled = 1;
        private const int TravelRouteNode = 2;
        private const int TravelNodeCandidates = 6;
        // #845 C: the measured strike needs the drummer's root on its spot, so its generic arrival
        // starts a short precise walk-in; one that has not settled in time is warped onto a sampled
        // seat when it is within DrummerSettleMaxWarpMeters, and otherwise drums where it stands.
        private const float DrummerSettleStoppingDistance = 0.05f;
        private const float DrummerSettledDistance = 0.12f;
        private const float DrummerSettleTimeoutSeconds = 3f;
        private const float DrummerSettleMaxWarpMeters = 2f;
        private const float StuckWindowSeconds = 4f;
        private const float StuckMinTravelMeters = 0.5f;
        private const float AggroPathCheckInterval = 0.5f;
        private const float AggroPathEndTolerance = 3f;
        private const float AggroTargetLostGraceSeconds = 3f;
        private static readonly Vector3[] PathCornerBuffer = new Vector3[64];

        // Sidecar animator contract (VisualRevision 12), hashed once. Presence is resolved once per
        // animator in ResolveAnimatorParameters so a set never names a missing parameter or state.
        private static readonly int MoveSpeedHash = Animator.StringToHash("MoveSpeed");
        private static readonly int GaitRateHash = Animator.StringToHash("GaitRate");
        private static readonly int RunBlendHash = Animator.StringToHash("RunBlend");
        private static readonly int PrayingHash = Animator.StringToHash("Praying");
        private static readonly int DeadHash = Animator.StringToHash("Dead");
        private static readonly int FleeingHash = Animator.StringToHash("Fleeing");
        private static readonly int AttackHash = Animator.StringToHash("Attack");
        private static readonly int AttackIndexHash = Animator.StringToHash("AttackIndex");
        private static readonly int AttackSpeedHash = Animator.StringToHash("AttackSpeed");
        private static readonly int DrummingHash = Animator.StringToHash("Drumming");
        private static readonly int HitHash = Animator.StringToHash("Hit");
        private static readonly int VisualRevisionHash = Animator.StringToHash("VisualRevision");
        private static readonly int IdleStateHash = Animator.StringToHash("Idle");
        private static readonly int FleeStateHash = Animator.StringToHash("Flee");
        private static readonly int PrayEnterStateHash = Animator.StringToHash("PrayEnter");
        private static readonly int PrayLoopStateHash = Animator.StringToHash("PrayLoop");
        private static readonly int PrayExitStateHash = Animator.StringToHash("PrayExit");
        private static readonly int DrummingStateHash = Animator.StringToHash("Drumming");
        private static readonly int KnifeInwardStateHash = Animator.StringToHash("KnifeInward");
        private static readonly int KnifeOutwardStateHash = Animator.StringToHash("KnifeOutward");
        private static readonly int AttackNoneStateHash = Animator.StringToHash("None");
        private const string AttackLayerName = "Attack";
        private static bool _visualRevisionLogged;

        private NavMeshAgent _agent;
        private Animator _animator;
        private AudioSource _audioSource;
        private AudioSource _sfxAudioSource;
        // Intrusive peer-local registry: the escalation's Cultists list is populated only by the server.
        internal static CongregantAI LocalChantActors { get; private set; }
        internal CongregantAI NextLocalChantActor { get; private set; }
        private CongregantAI _previousLocalChantActor;
        private bool _chantRegistered;
        private AudioSource _chantSource;
        private bool _chantScheduled;
        private ScanNodeProperties _scanNode;
        private CapsuleCollider _bodyCollider;
        private CultistRole _role;
        private CultistRitualPhase _phase = CultistRitualPhase.Traveling;
        private Vector3 _ritualPoint;
        private Vector3 _ritualSpot;
        private int _index;
        private bool _initialized;
        private bool _runtimeReady;
        private bool _aggroed;
        private bool _deadHandled;
        private PlayerControllerB _targetPlayer;
        private ulong _targetClientId;
        private Vector3 _fleeFromPoint;
        private Vector3 _forcedFleeDirection;
        private float _nextDestinationTime;
        private float _nextAttackTime;
        private int _playerDamageSerial;
        private int _lastAppliedPlayerDamageSerial;
        private float _nextFleeDestinationTime;
        private float _nextVisualTickTime;
        // Server copy of the last SyncStateClientRpc payload; a resend goes out only on a change
        // or when CongregationEscalationState's late-join resync asks for one.
        private bool _hasSentSyncState;
        private bool _syncStateResendRequested;
        private CultistRole _sentRole;
        private CultistRitualPhase _sentPhase;
        private Vector3 _sentRitualPoint;
        private Vector3 _sentRitualSpot;
        private int _sentIndex;
        private bool _sentAggroed;
        private ulong _sentTargetClientId;
        private int _aggroTargetFrame = -1;
        private PlayerControllerB _aggroTargetThisFrame;
        private readonly Vector3[] _replicatedMoveSamplePositions = new Vector3[ReplicatedMoveSampleCount];
        private readonly float[] _replicatedMoveSampleTimes = new float[ReplicatedMoveSampleCount];
        private int _replicatedMoveSampleHead;
        private int _replicatedMoveSampleCount;
        private bool _locomotionOffsetApplied;
        private float _walkCycleOffset;
        private float _footfallPhase;
        private bool _footstepEmitting;
        private int[] _footstepBag = Array.Empty<int>();
        private int _footstepBagCursor;
        private int _lastFootstepClipIndex = -1;
        private bool _hasForcedFleeDirection;
        private bool _seraphimExecutionLocked;
        private bool _killedBySeraphim;
        private bool _agentConfigured;
        private bool _animatorParametersResolved;
        private bool _hasMoveSpeed;
        private bool _hasGaitRate;
        private bool _hasRunBlend;
        private bool _hasPraying;
        private bool _hasDead;
        private bool _hasFleeing;
        private bool _hasAttack;
        private bool _hasAttackIndex;
        private bool _hasAttackSpeed;
        private bool _hasDrumming;
        private bool _hasDrummingState;
        private bool _hasAttackNoneState;
        private int _attackLayer = -1;
        private bool _hasHit;
        private bool _serverRunRequested;
        private readonly NetworkVariable<bool> _runRequested = new NetworkVariable<bool>();
        private float _runProgress;
        private float _runBlend;
        private float _smoothedMoveSpeed;
        private float _lastVisualUpdateTime;
        private float _hitRecoveryUntil;
        private readonly CongregantAttackWindow _attackWindow = new CongregantAttackWindow();
        // Server: the slash the next attack plays; alternates per cultist, seeded from its index.
        private int _nextAttackIndex;
        // Every peer: the swing whose impact sound waits for its contact time (-1 = none).
        private int _pendingImpactIndex = -1;
        private ulong _pendingImpactTarget;
        private bool _pendingImpactArmed;
        private float _pendingImpactUntil;
        // Server-owned drummer flag; clients receive it through SyncStateClientRpc.
        private bool _drumming;
        private bool _sentDrumming;
        // Server: this congregant holds the drum (#845 D). Its ritual spot is the measured drummer
        // spot beside the drum, it faces _ritualFacePoint (drum-local -Z), and it is never stared
        // or aggroed.
        private bool _isDrummer;
        private Vector3 _ritualFacePoint;
        // Every peer: the ceremony's drum pitch, applied locally to the drummer's AttackSpeed.
        private float _drumTempo = 1f;
        // Every peer: the knife or drumstick in the right hand (#845 C), built locally on first use.
        private CongregantHandProp _handProp;
        private bool _hasPrayLoopState;
        // Server: UpdateTravel saw the walk end; cleared whenever a new travel target is resolved.
        private bool _travelArrived;
        // Server: when the drummer's precise settle onto its spot began (-1 = not settling).
        private float _drummerSettleStart = -1f;
        private readonly NavMeshPath _navPath = new NavMeshPath();
        private int _footstepSurfaceIndex;
        private int _lastSurfaceFootstepClip = -1;
        private Vector3 _travelTarget;
        private bool _hasTravelTarget;
        private int _travelRoute;
        private bool _travelFallbackLogged;
        private float _nextTravelResolveTime;
        private bool _wantsMovement;
        private Vector3 _stuckAnchor;
        private float _stuckWindowStart;
        private int _stuckStrikes;
        private PlayerControllerB _aggroPathTarget;
        private float _nextAggroPathCheckTime;
        private float _aggroTargetLostSince = -1f;

        internal CultistRole Role => _role;
        internal bool IsWhiteCultist => _role == CultistRole.White;
        internal bool IsAggroed => _aggroed;
        internal bool IsFleeingFromSeraphim => _phase == CultistRitualPhase.Fleeing;
        internal bool IsSeraphimExecutionLocked => _seraphimExecutionLocked;
        // A pathless agent (StopAgentMotion cleared it) reads remainingDistance 0, so the path
        // clause needs a live path; the arrival latch and the distance clauses cover a cultist
        // already standing where its walk ended.
        internal bool HasReachedRitualSpot => _travelArrived || (!AwaitsDrummerSettle && IsLooselyAtRitualSpot);
        // Generic arrival: close enough for a ring congregant. A drummer not yet drumming only
        // counts once UpdateTravel has settled it onto the exact spot and latched _travelArrived.
        private bool IsLooselyAtRitualSpot
            => (_agent != null && _agent.enabled && _agent.hasPath && ReachedDestination(1.35f))
            || IsNearRitualSpot;
        private bool AwaitsDrummerSettle => _isDrummer && !_drumming;
        private bool IsNearRitualSpot => HorizontalDistance(transform.position, _ritualSpot) <= 1.45f
            || (_hasTravelTarget && HorizontalDistance(transform.position, _travelTarget) <= 1.45f);

        /// <summary>Server: the ceremony's drummer (#845 D).</summary>
        internal bool IsDrummer => _isDrummer;
        /// <summary>Server: the drummer reached the drum and is drumming.</summary>
        internal bool IsDrummingServer => _drumming && !isEnemyDead;
        /// <summary>Server: still walking to its spot; takes the ceremony's phase on arrival.</summary>
        internal bool IsWalkingToRitualSpot => _phase == CultistRitualPhase.Traveling && !HasReachedRitualSpot;
        /// <summary>Server: at its spot in a ceremony phase and not chasing anyone.</summary>
        internal bool IsPresentAtRitual => !isEnemyDead && !_aggroed && !_seraphimExecutionLocked
            && (_phase == CultistRitualPhase.Phase1Idle || _phase == CultistRitualPhase.Phase2RedPray || _phase == CultistRitualPhase.Phase3AllPray);
        /// <summary>Server: in the looping prayer at its spot (PrayEnter does not count yet). A
        /// controller without a PrayLoop state counts the praying phase itself.</summary>
        internal bool IsPrayingForProgress => IsPresentAtRitual && ShouldPrayInCurrentPhase()
            && (!_hasPrayLoopState || (_animator != null && _animator.GetCurrentAnimatorStateInfo(0).shortNameHash == PrayLoopStateHash));
        /// <summary>Every peer: the base layer is in the Drumming state.</summary>
        internal bool IsInDrummingState => _animator != null && _hasDrummingState
            && _animator.GetCurrentAnimatorStateInfo(0).shortNameHash == DrummingStateHash;

        // Vanilla counts stunNormalizedTimer down in EnemyAI.Update on every peer, and every peer
        // sets it locally (StunGrenadeItem.StunExplosion, EnemyAICollisionDetect), so no RPC.
        private bool IsStunned => stunNormalizedTimer > 0f;

        private static float AnimationMatchedAggroMoveSpeed => Mathf.Clamp(
            ContractsBootstrap.CultistAggroMoveSpeed.Value,
            1f,
            MaxAnimationMatchedAggroMoveSpeed);

        internal void InitializeServer(CultistRole role, Vector3 ritualPoint, Vector3 ritualSpot, int index)
        {
            _role = role;
            _ritualPoint = ritualPoint;
            _ritualSpot = ritualSpot;
            _index = index;
            _phase = CultistRitualPhase.Traveling;
            _initialized = true;
            _aggroed = false;
            _targetPlayer = null;
            _targetClientId = 0UL;
            _fleeFromPoint = ritualPoint;
            _forcedFleeDirection = Vector3.zero;
            _hasForcedFleeDirection = false;
            _seraphimExecutionLocked = false;
            _killedBySeraphim = false;
            _nextFleeDestinationTime = 0f;
            _nextAttackIndex = _index & 1;
            _drumming = false;
            _isDrummer = false;
            _ritualFacePoint = ritualPoint;
            _drumTempo = 1f;
            ConfigureAnimationPhaseOffset();
            enemyHP = Mathf.Max(1, ContractsBootstrap.CultistHealth.Value);
            isEnemyDead = false;
            isOutside = true;
            ventAnimationFinished = true;
            EnsureRuntimeComponents();
            ApplyAgentPhaseSettings();
            SnapToNavMesh(transform.position, 6f);
            _hasTravelTarget = false;
            _travelArrived = false;
            _drummerSettleStart = -1f;
            _travelRoute = TravelRouteSpot;
            _travelFallbackLogged = false;
            ResetStuckWatchdog();
            if (TryResolveTravelTarget(TravelRouteSpot))
                SetDestinationThrottled(_travelTarget, 0f);
            ApplyAnimationState(forceIdlePose: false);
            ApplyLocomotionCycleOffset();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Cultist {_index} initialized role={_role} spawn={transform.position}, ritualSpot={_ritualSpot}, ritual={_ritualPoint}.");
        }

        internal void SyncInitialState()
        {
            if (!IsServerAuthority() || NetworkObject == null || !NetworkObject.IsSpawned)
                return;

            SendSyncState();
        }

        /// <summary>Server: rebroadcast the full state on the next tick. Called by the
        /// Congregation late-join resync, which replaces the old 4 s heartbeat as the path that
        /// brings a joining client's cultists up to date.</summary>
        internal void RequestStateResend()
        {
            _syncStateResendRequested = true;
        }

        private void SyncStateIfChanged()
        {
            if (NetworkObject == null || !NetworkObject.IsSpawned)
                return;

            if (_hasSentSyncState
                && !_syncStateResendRequested
                && _sentRole == _role
                && _sentPhase == _phase
                && _sentIndex == _index
                && _sentAggroed == _aggroed
                && _sentTargetClientId == _targetClientId
                && _sentRitualPoint == _ritualPoint
                && _sentRitualSpot == _ritualSpot
                && _sentDrumming == _drumming)
                return;

            SendSyncState();
        }

        private void SendSyncState()
        {
            _hasSentSyncState = true;
            _syncStateResendRequested = false;
            _sentRole = _role;
            _sentPhase = _phase;
            _sentIndex = _index;
            _sentAggroed = _aggroed;
            _sentTargetClientId = _targetClientId;
            _sentRitualPoint = _ritualPoint;
            _sentRitualSpot = _ritualSpot;
            _sentDrumming = _drumming;
            SyncStateClientRpc((int)_role, (int)_phase, _ritualPoint, _ritualSpot, _index, _aggroed, _targetClientId, isEnemyDead, _drumming);
        }

        // Phase-only sync. Records the three fields it carries so SyncStateIfChanged does not follow
        // every phase change with a full-state resend on the next Update.
        private void SendPhaseSync()
        {
            ApplyBodyCollisionState();
            _sentPhase = _phase;
            _sentAggroed = _aggroed;
            _sentTargetClientId = _targetClientId;
            SyncPhaseClientRpc((int)_phase, _aggroed, _targetClientId);
        }

        private void OnEnable()
        {
            if (_chantRegistered)
                return;
            _chantRegistered = true;
            NextLocalChantActor = LocalChantActors;
            if (LocalChantActors != null)
                LocalChantActors._previousLocalChantActor = this;
            LocalChantActors = this;
        }

        private void OnDisable()
        {
            UnregisterLocalChant();
        }

        public override void OnDestroy()
        {
            UnregisterLocalChant();
            base.OnDestroy();
        }

        private void UnregisterLocalChant()
        {
            DestroyLocalChant();
            if (!_chantRegistered)
                return;
            if (_previousLocalChantActor != null)
                _previousLocalChantActor.NextLocalChantActor = NextLocalChantActor;
            else
                LocalChantActors = NextLocalChantActor;
            if (NextLocalChantActor != null)
                NextLocalChantActor._previousLocalChantActor = _previousLocalChantActor;
            _previousLocalChantActor = null;
            NextLocalChantActor = null;
            _chantRegistered = false;
        }

        // State RPCs/phase changes stop at ApplyAnimationState; this also catches local death,
        // and a drummer identity message arriving later in the frame.
        private void LateUpdate()
        {
            StopChantIfIneligible();
        }

        // The replicated phase owns arrival: a server-approved fallback travel target is not
        // replicated, so comparing client position with the original ritual spot is not reliable.
        internal bool CanChantLocally => isActiveAndEnabled && !isEnemyDead && !_deadHandled
            && !_aggroed && !_seraphimExecutionLocked && !_isDrummer && !_drumming
            && !CongregationEscalationState.IsLocalChantDrummer(this)
            && CongregationEscalationState.CanPlayLocalChants
            && (_phase == CultistRitualPhase.Phase1Idle || _phase == CultistRitualPhase.Phase2RedPray
                || _phase == CultistRitualPhase.Phase3AllPray || _phase == CultistRitualPhase.Stare);

        internal bool NeedsLocalChant => !_chantScheduled && CanChantLocally;

        internal bool IsChantScheduled => _chantScheduled;

        internal void StopChantIfIneligible()
        {
            if (CanChantLocally)
                return;
            if (isEnemyDead || _deadHandled)
                DestroyLocalChant();
            else if (_chantScheduled)
            {
                if (_chantSource != null)
                    _chantSource.Stop();
                _chantScheduled = false;
            }
        }

        internal void ScheduleLocalChant(AudioClip clip, double dspStart, int sampleOffset)
        {
            if (!NeedsLocalChant || clip == null)
                return;
            if (_chantSource == null)
            {
                var emitter = new GameObject("CongregantChant");
                emitter.transform.SetParent(transform, false);
                emitter.transform.localPosition = new Vector3(0f, LocalAudioSourceHeight, 0f);
                _chantSource = emitter.AddComponent<AudioSource>();
                CongregationEscalationState.ConfigureCeremonySource(_chantSource, clip, true, CongregationAudioMix.Chant);
                _chantVolume = CongregationAudioMix.Chant.Volume;
                emitter.AddComponent<OccludeAudio>().useReverb = true;
            }
            _chantSource.timeSamples = sampleOffset;
            _chantSource.PlayScheduled(dspStart);
            _chantScheduled = true;
        }

        internal void DestroyLocalChant()
        {
            if (_chantSource != null)
            {
                _chantSource.Stop();
                UnityEngine.Object.Destroy(_chantSource.gameObject);
                _chantSource = null;
            }
            _chantScheduled = false;
            _chantVolume = -1f;
        }

        // #1336: the choir cap's last applied level, so AudioSource.volume is written only on change.
        private float _chantVolume = -1f;

        internal void SetChantVolume(float volume)
        {
            if (_chantSource == null || _chantVolume == volume)
                return;
            _chantSource.volume = volume;
            _chantVolume = volume;
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
            EnsureRuntimeComponents();
            enemyHP = Mathf.Max(enemyHP, Mathf.Max(1, ContractsBootstrap.CultistHealth.Value));
            ventAnimationFinished = true;
            // An outdoor enemy on every peer (PlayerIsTargetable reads it), whatever the EnemyType says.
            isOutside = true;
            // CongregantAI issues its own throttled SetDestination; vanilla DoAIInterval must not
            // re-issue `destination` on top of it or log its off-mesh error every interval.
            moveTowardsDestination = false;
            movingTowardsTargetPlayer = false;
            // Vanilla serverPosition/SmoothDamp is the only transform path (the prefab carries no
            // NetworkTransform); WhistleblowerEnemyAI's values keep a walking body from stepping.
            updatePositionThreshold = 0.35f;
            syncMovementSpeed = 0.13f;
            openDoorSpeedMultiplier = 1f;
            if (!_initialized)
            {
                _ritualPoint = transform.position;
                _ritualSpot = transform.position;
                _ritualFacePoint = transform.position;
            }
            ConfigureAnimationPhaseOffset();
            ApplyAnimationState(forceIdlePose: false);
            ApplyLocomotionCycleOffset();
        }

        public override void Update()
        {
            base.Update();

            if (!_runtimeReady)
                EnsureRuntimeComponents();

            if (IsServerAuthority())
                UpdateRunIntent();

            if (Time.time >= _nextVisualTickTime)
            {
                _nextVisualTickTime = Time.time + 0.04f;
                UpdateAnimationMoveSpeed();
            }

            UpdateAttackImpactAudio();

            if (isEnemyDead)
            {
                HandleDeadState();
                // A body still answers the late-join resend, so a joiner sees it dead.
                if (IsServerAuthority())
                    SyncStateIfChanged();
                return;
            }

            if (!IsServerAuthority())
                return;

            SyncStateIfChanged();
            if (IsStunned)
            {
                _attackWindow.Cancel();
                StopAgentMotion();
                ResetStuckWatchdog();
                return;
            }

            UpdatePendingAttack();
            if (_agent == null)
                return;
            if (!_agent.enabled)
            {
                // A flee order can land while the agent is still off the mesh; keep trying to seat it.
                if (_phase == CultistRitualPhase.Fleeing && !_seraphimExecutionLocked)
                    EnsureAgentReadyForFlee();
                return;
            }

            EnsureOnNavMesh();
            _wantsMovement = false;

            switch (_phase)
            {
                case CultistRitualPhase.Traveling:
                    UpdateTravel();
                    break;
                case CultistRitualPhase.Stare:
                    UpdateStare();
                    break;
                case CultistRitualPhase.Aggro:
                    UpdateAggro();
                    break;
                case CultistRitualPhase.Fleeing:
                    UpdateFlee();
                    break;
                case CultistRitualPhase.Phase1Idle:
                case CultistRitualPhase.Phase2RedPray:
                case CultistRitualPhase.Phase3AllPray:
                case CultistRitualPhase.Phase4Demon:
                    UpdateRitualPose();
                    break;
            }

            UpdateStuckWatchdog();
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

            _attackWindow.Cancel();
            CancelAttackLocal();
            // Vanilla runs HitEnemy exactly once on every peer per hit (the hitter from
            // HitEnemyOnLocalClient, everyone else from HitEnemyClientRpc), so the local trigger is
            // the whole replication.
            TriggerHitLocal();

            try
            {
                base.HitEnemy(force, playerWhoHit, playHitSFX, hitID);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Cultist base HitEnemy skipped: {e.Message}");
            }

            enemyHP -= Mathf.Max(1, force);
            if (enemyHP <= 0 && !isEnemyDead && (IsOwner || IsServerAuthority()))
                KillEnemyOnOwnerClient();
        }

        /// <summary>Server: the Seraphim's execution; the body leaves no loot.</summary>
        internal void KillBySeraphimServer()
        {
            _killedBySeraphim = true;
            KillEnemy(destroy: false);
        }

        public override void KillEnemy(bool destroy = false)
        {
            if (isEnemyDead && _deadHandled)
                return;

            _deadHandled = true;
            try
            {
                // Every peer runs KillEnemy (the owner directly, the rest from vanilla
                // KillEnemyClientRpc), so the state switch stays local instead of another RPC.
                SwitchToBehaviourStateOnLocalClient(4);
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
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Cultist base KillEnemy skipped: {e.Message}");
            }

            isEnemyDead = true;
            _seraphimExecutionLocked = false;
            StopAgentMotion();
            HandleDeadState();
            CancelAttackLocal();
            ApplyDrumState();
            if (IsServerAuthority())
            {
                // Before NotifyCultistKilled, which may hand the drum on: the drop is what this
                // body held. The _deadHandled guard above makes it one drop per death. A Seraphim
                // kill leaves no loot (Lawson, #845, 2026-09-27).
                if (!_killedBySeraphim)
                    DropLootServer();
                CongregationEscalationState.NotifyCultistKilled(this);
            }
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Cultist {_index} killed role={_role}.");
        }

        internal void SetRitualPhaseServer(CultistRitualPhase phase)
        {
            if (isEnemyDead || _aggroed)
                return;

            _phase = phase;
            _targetPlayer = null;
            _targetClientId = 0UL;
            ApplyAgentPhaseSettings();
            StopAgentMotion();
            ApplyAnimationState(forceIdlePose: false);
            SendPhaseSync();
        }

        internal void BeginFleeServer(Vector3 fleeFromPoint, Vector3 preferredDirection)
        {
            if (isEnemyDead)
                return;

            _phase = CultistRitualPhase.Fleeing;
            _aggroed = false;
            _seraphimExecutionLocked = false;
            _targetPlayer = null;
            _targetClientId = 0UL;
            _fleeFromPoint = fleeFromPoint;
            preferredDirection.y = 0f;
            _hasForcedFleeDirection = preferredDirection.sqrMagnitude > 0.25f;
            _forcedFleeDirection = _hasForcedFleeDirection ? preferredDirection.normalized : Vector3.zero;
            ApplyAgentPhaseSettings();
            EnsureAgentReadyForFlee();

            ApplyAnimationState(forceIdlePose: false);
            ApplyLocomotionCycleOffset();
            _nextDestinationTime = 0f;
            PickFleeDestination(force: true);
            SendPhaseSync();
        }

        internal void BeginSeraphimExecutionServer()
        {
            if (isEnemyDead)
                return;

            _phase = CultistRitualPhase.Phase4Demon;
            _aggroed = false;
            _seraphimExecutionLocked = true;
            _targetPlayer = null;
            _targetClientId = 0UL;
            _hasForcedFleeDirection = false;
            _forcedFleeDirection = Vector3.zero;
            StopAgentMotion();
            if (_agent != null)
                _agent.enabled = false;
            ApplyAnimationState(forceIdlePose: true);
            SendPhaseSync();
        }

        internal void SetSeraphimExecutionPositionServer(Vector3 position)
        {
            if (isEnemyDead)
                return;

            if (_agent != null && _agent.enabled)
                _agent.enabled = false;
            transform.position = position;
            FacePosition(_ritualPoint, instant: false);
        }

        internal void BeginStareServer(PlayerControllerB target)
        {
            // The drummer keeps the beat through detection (#845 D).
            if (isEnemyDead || _isDrummer)
                return;

            _phase = CultistRitualPhase.Stare;
            _targetPlayer = target;
            _targetClientId = target != null ? target.actualClientId : 0UL;
            _hasForcedFleeDirection = false;
            ApplyAgentPhaseSettings();
            StopAgentMotion();
            FaceTargetInstant(target);
            ApplyAnimationState(forceIdlePose: true);
            SendPhaseSync();
        }

        internal void BeginAggroServer(PlayerControllerB target)
        {
            if (isEnemyDead || IsWhiteCultist || _isDrummer)
                return;

            _phase = CultistRitualPhase.Aggro;
            _aggroed = true;
            _targetPlayer = target;
            _targetClientId = target != null ? target.actualClientId : 0UL;
            _hasForcedFleeDirection = false;
            _aggroPathTarget = null;
            _nextAggroPathCheckTime = 0f;
            _aggroTargetLostSince = -1f;
            ApplyAgentPhaseSettings();
            ApplyAnimationState(forceIdlePose: false);
            ApplyLocomotionCycleOffset();
            SendPhaseSync();
        }

        internal void ResumeRitualAfterStareServer(CultistRitualPhase phase)
        {
            if (isEnemyDead)
                return;

            _aggroed = false;
            _phase = phase;
            _targetPlayer = null;
            _targetClientId = 0UL;
            _hasForcedFleeDirection = false;
            ApplyAgentPhaseSettings();
            StopAgentMotion();
            ApplyAnimationState(forceIdlePose: false);
            ApplyLocomotionCycleOffset();
            SendPhaseSync();
        }

        /// <summary>Server: back into the ceremony after a stare. At its spot the cultist takes
        /// <paramref name="phase"/> at once; anywhere else it walks back and takes the ceremony's
        /// current phase on arrival.</summary>
        internal void ResumeCeremonyServer(CultistRitualPhase phase)
        {
            if (isEnemyDead)
                return;
            if (IsNearRitualSpot)
                ResumeRitualAfterStareServer(phase);
            else
                BeginWalkToRitualSpotServer();
        }

        /// <summary>Server: walks to the ritual spot in Traveling; UpdateTravel hands the cultist
        /// the ceremony's current phase (or the drum) when it arrives.</summary>
        private void BeginWalkToRitualSpotServer()
        {
            ResumeRitualAfterStareServer(CultistRitualPhase.Traveling);
            ResetStuckWatchdog();
            _hasTravelTarget = false;
            _travelArrived = false;
            _drummerSettleStart = -1f;
            // StopAgentMotion cleared the path; the walk is issued here rather than on the next
            // UpdateTravel so the cultist does not stand for the resolve throttle first.
            if (TryResolveTravelTarget(TravelRouteSpot))
            {
                _nextDestinationTime = 0f;
                SetDestinationThrottled(_travelTarget, 0.4f);
            }
        }

        /// <summary>Server: marks the planned drummer, which walks to the drum spot (#845 D). It
        /// faces <paramref name="facePoint"/> (along drum-local -Z) and is never stared or
        /// aggroed.</summary>
        internal void MarkDrummerServer(Vector3 facePoint)
        {
            _isDrummer = true;
            _ritualFacePoint = facePoint;
        }

        /// <summary>Server: hands the drum to this congregant after the previous drummer died. It
        /// drops whatever it was doing, walks to the drum spot and starts drumming on arrival.</summary>
        internal void AssignDrummerServer(Vector3 drumSpot, Vector3 facePoint)
        {
            if (isEnemyDead || _phase == CultistRitualPhase.Fleeing || _seraphimExecutionLocked)
                return;

            MarkDrummerServer(facePoint);
            _ritualSpot = drumSpot;
            BeginWalkToRitualSpotServer();
        }

        /// <summary>Every peer, no RPC: the ceremony's drum pitch scales the drummer's DrumSwing
        /// speed so the strike stays on the audible beat through the speed-up.</summary>
        internal void SetDrumTempo(float tempo)
        {
            tempo = Mathf.Max(0.1f, tempo);
            if (Mathf.Abs(_drumTempo - tempo) < 0.0005f)
                return;

            _drumTempo = tempo;
            ApplyDrumState();
        }

        /// <summary>Server: the ceremony's drummer hook. Sets the Drumming state on every peer through
        /// the full state sync (late joiners included) and runs DrumSwing at AttackSpeed 1, one beat
        /// every 2.000 s before the ceremony tempo multiplier. Safe to call at any time; aggro,
        /// fleeing, the execution lock and death suspend the drumming locally without clearing the flag.</summary>
        internal void SetDrummingServer(bool drumming)
        {
            if (!IsServerAuthority() || _drumming == drumming)
                return;

            _drumming = drumming;
            ApplyDrumState();
        }

        /// <summary>Every peer: phase-locks the Drumming state to the drum loop's clock (seconds from a
        /// strike, e.g. the drum AudioSource's timeSamples / frequency) without an RPC, so the clip's
        /// beat frame lands on every 2.000 s strike. A lock already within tolerance is left alone,
        /// and prayer, recoil or death are never cut into. True when the Drumming state is locked
        /// (already within tolerance or just re-seated).</summary>
        internal bool AlignDrumPhase(double audioTimeSeconds)
        {
            if (_animator == null || !_hasDrummingState || !IsDrumming
                || double.IsNaN(audioTimeSeconds) || double.IsInfinity(audioTimeSeconds))
                return false;

            double sinceBeat = audioTimeSeconds % DrumBeatSeconds;
            if (sinceBeat < 0d)
                sinceBeat += DrumBeatSeconds;
            float target = Mathf.Repeat((float)(sinceBeat / DrumBeatSeconds) + DrumBeatNormalizedTime, 1f);
            AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(0);
            if (state.shortNameHash == DrummingStateHash && !_animator.IsInTransition(0))
            {
                float error = Mathf.Repeat(state.normalizedTime - target + 0.5f, 1f) - 0.5f;
                if (Mathf.Abs(error) <= DrumPhaseTolerance)
                    return true;
            }
            else if (state.shortNameHash != IdleStateHash)
            {
                return false;
            }

            _animator.Play(DrummingStateHash, 0, target);
            return true;
        }

        private bool IsDrumming => _drumming && !isEnemyDead && !_seraphimExecutionLocked
            && _phase != CultistRitualPhase.Aggro && _phase != CultistRitualPhase.Fleeing;

        private void ApplyDrumState()
        {
            bool drumming = IsDrumming;
            SetBool(DrummingHash, _hasDrumming, drumming);
            // AttackSpeed also times the knife states; only the drummer follows the ceremony tempo.
            SetFloat(AttackSpeedHash, _hasAttackSpeed, drumming ? DrumAttackSpeed * _drumTempo : 1f);
            // Every drumming, role and death change reaches this method on every peer.
            RefreshHandProp();
        }

        /// <summary>What the right hand holds: nothing for white, the drumstick while the synced
        /// drumming flag is set, the knife otherwise. It is also what the body drops.</summary>
        private CongregationPropKind HeldPropKind => _role == CultistRole.White
            ? CongregationPropKind.None
            : _drumming ? CongregationPropKind.Drumstick : CongregationPropKind.Knife;

        /// <summary>Every peer: shows the held prop, hidden on death so the dropped loot does not
        /// duplicate it.</summary>
        private void RefreshHandProp()
        {
            if (_animator == null)
                return;
            if (_handProp == null)
                _handProp = new CongregantHandProp(_animator.transform, gameObject.layer);
            _handProp.Show(isEnemyDead ? CongregationPropKind.None : HeldPropKind);
        }

        /// <summary>Server: leaves the held prop as scrap and sends its rolled value to clients.</summary>
        private void DropLootServer()
        {
            CongregationPropKind kind = HeldPropKind;
            if (kind == CongregationPropKind.None)
                return;
            GrabbableObject loot = CongregationLoot.DropServer(kind, transform.position, transform.eulerAngles.y, isOutside);
            if (loot == null || NetworkObject == null || !NetworkObject.IsSpawned)
                return;
            SyncLootValueClientRpc(new NetworkObjectReference(loot.NetworkObject), loot.scrapValue);
        }

        private void UpdateTravel()
        {
            if (AwaitsDrummerSettle && !_travelArrived
                && (_drummerSettleStart >= 0f || IsLooselyAtRitualSpot)
                && !TrySettleDrummerOnSpot())
                return;

            if (HasReachedRitualSpot)
            {
                _travelArrived = true;
                StopAgentMotion();
                FacePosition(_ritualFacePoint, instant: false);
                if (_isDrummer && !_drumming)
                {
                    SetDrummingServer(true);
                    // Full state first, so a client never applies the phase below (which would
                    // start a prayer) before it knows this congregant drums.
                    if (NetworkObject != null && NetworkObject.IsSpawned)
                        SendSyncState();
                }
                // #845 D: a cultist that arrives after the ceremony started (a late walker, a
                // returned chaser, a new drummer) takes up the ceremony's current phase here.
                if (CongregationEscalationState.TryGetArrivalRitualPhase(out CultistRitualPhase phase))
                    SetRitualPhaseServer(phase);
                return;
            }

            if (!_hasTravelTarget)
            {
                if (Time.time < _nextTravelResolveTime)
                    return;
                _nextTravelResolveTime = Time.time + 1f;
                if (!TryResolveTravelTarget(TravelRouteSpot))
                    return;
                _nextDestinationTime = 0f;
            }

            _wantsMovement = true;
            SetDestinationThrottled(_travelTarget, 0.4f);
        }

        /// <summary>Server: the drummer's walk-in onto the exact spot. The first call routes the
        /// agent to <see cref="_ritualSpot"/> with a near-zero stopping distance; the drummer counts
        /// as settled within <see cref="DrummerSettledDistance"/> or, after
        /// <see cref="DrummerSettleTimeoutSeconds"/>, is warped onto a NavMesh point sampled at the
        /// spot when within <see cref="DrummerSettleMaxWarpMeters"/> of it (farther, it arrived at a
        /// fallback target and drums where it stands). On settling it stops, restores the phase agent
        /// settings, faces the drum's strike direction at once and sends its final position, which
        /// vanilla's 0.35 m threshold would otherwise skip. True once settled.</summary>
        private bool TrySettleDrummerOnSpot()
        {
            if (_drummerSettleStart < 0f)
            {
                _drummerSettleStart = Time.time;
                if (_agent.isOnNavMesh)
                {
                    _agent.stoppingDistance = DrummerSettleStoppingDistance;
                    _agent.autoBraking = true;
                    _agent.isStopped = false;
                    _agent.SetDestination(_ritualSpot);
                }
            }

            float spotOffset = HorizontalDistance(transform.position, _ritualSpot);
            if (spotOffset > DrummerSettledDistance)
            {
                if (Time.time - _drummerSettleStart < DrummerSettleTimeoutSeconds)
                    return false;
                string outcome;
                if (spotOffset > DrummerSettleMaxWarpMeters)
                {
                    outcome = $"{spotOffset:0.0} m away, beyond the {DrummerSettleMaxWarpMeters:0} m warp bound; drumming where it stands";
                }
                else
                {
                    bool warped = TrySampleNavMesh(_ritualSpot, AgentEnableSampleRadius, out Vector3 seat) && _agent.Warp(seat);
                    outcome = warped ? "warp placed it" : "warp failed, drumming";
                }
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Cult] Drummer {_index} did not settle on its spot {_ritualSpot} in {DrummerSettleTimeoutSeconds:0} s; {outcome} at {transform.position}.");
            }

            _drummerSettleStart = -1f;
            _travelArrived = true;
            StopAgentMotion();
            ApplyAgentPhaseSettings();
            FacePosition(_ritualFacePoint, instant: true);
            if (NetworkObject != null && NetworkObject.IsSpawned)
            {
                serverPosition = transform.position;
                UpdateEnemyPositionRpc(serverPosition);
            }
            return true;
        }

        /// <summary>Server: picks where Traveling walks. The ritual spot when a complete path
        /// reaches it; else a NavMesh point within <see cref="TravelFallbackSampleRadius"/> of it;
        /// else the nearest reachable outside AI node to it; else, on a first resolve only, the
        /// partial path toward the spot. <paramref name="firstRoute"/> skips the routes a stuck
        /// cultist already failed on. False when the agent cannot path yet.</summary>
        private bool TryResolveTravelTarget(int firstRoute)
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
                return false;

            bool retryingNode = _hasTravelTarget && _travelRoute == TravelRouteNode;
            Vector3 target = _ritualSpot;
            int route;
            string fallback = null;
            if (firstRoute <= TravelRouteSpot && IsPathComplete(_ritualSpot))
            {
                route = TravelRouteSpot;
            }
            else if (firstRoute <= TravelRouteSampled
                && TrySampleNavMesh(_ritualSpot, TravelFallbackSampleRadius, out target)
                && IsPathComplete(target))
            {
                route = TravelRouteSampled;
                fallback = "NavMesh point near it";
            }
            else if (TryFindReachableOutsideNode(_ritualSpot, retryingNode, out target))
            {
                route = TravelRouteNode;
                fallback = "nearest reachable outside AI node";
            }
            else if (firstRoute <= TravelRouteSpot && HasPartialPath(_ritualSpot))
            {
                target = _ritualSpot;
                route = TravelRouteSpot;
                fallback = "end of the partial path toward it";
            }
            else
            {
                return false;
            }

            _travelTarget = target;
            _travelRoute = route;
            _hasTravelTarget = true;
            _travelArrived = false;
            if (fallback != null && !_travelFallbackLogged)
            {
                _travelFallbackLogged = true;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Cult] Cultist {_index} has no complete path to its ritual spot {_ritualSpot}; travelling to the {fallback} at {target}.");
            }
            return true;
        }

        private bool TryFindReachableOutsideNode(Vector3 near, bool excludeCurrent, out Vector3 position)
        {
            position = near;
            RoundManager round = RoundManager.Instance;
            GameObject[] nodes = round != null ? round.outsideAINodes : null;
            if (nodes == null || nodes.Length == 0)
                return false;

            // Nearest first without sorting: each pass takes the next-nearest node past the last.
            float floor = -1f;
            for (int attempt = 0; attempt < TravelNodeCandidates; attempt++)
            {
                GameObject nearest = null;
                float nearestSqr = float.MaxValue;
                for (int i = 0; i < nodes.Length; i++)
                {
                    GameObject node = nodes[i];
                    if (node == null)
                        continue;
                    float sqr = (node.transform.position - near).sqrMagnitude;
                    if (sqr > floor && sqr < nearestSqr)
                    {
                        nearest = node;
                        nearestSqr = sqr;
                    }
                }

                if (nearest == null)
                    return false;
                floor = nearestSqr;

                if (!TrySampleNavMesh(nearest.transform.position, AgentEnableSampleRadius, out Vector3 candidate))
                    continue;
                if (excludeCurrent && HorizontalDistance(candidate, _travelTarget) < 1f)
                    continue;
                if (IsPathComplete(candidate))
                {
                    position = candidate;
                    return true;
                }
            }

            return false;
        }

        private bool IsPathComplete(Vector3 goal)
        {
            _navPath.ClearCorners();
            return _agent.CalculatePath(goal, _navPath) && _navPath.status == NavMeshPathStatus.PathComplete;
        }

        private bool HasPartialPath(Vector3 goal)
        {
            _navPath.ClearCorners();
            return _agent.CalculatePath(goal, _navPath) && _navPath.status == NavMeshPathStatus.PathPartial;
        }

        /// <summary>Server: drops the chase and walks back to the ritual spot; on arrival
        /// UpdateTravel puts it into the ceremony's current phase, so it resumes its duties.</summary>
        private void ReturnToRitualServer(string reason)
        {
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Cultist {_index} dropped its aggro target ({reason}); returning to the ritual.");
            BeginWalkToRitualSpotServer();
        }

        /// <summary>Server: a cultist that should be moving (Traveling, Aggro, Fleeing) and covers
        /// less than <see cref="StuckMinTravelMeters"/> in <see cref="StuckWindowSeconds"/> repaths;
        /// a second still window takes the next travel fallback, drops the chase, or picks a new
        /// flee point.</summary>
        private void UpdateStuckWatchdog()
        {
            if (!_wantsMovement)
            {
                ResetStuckWatchdog();
                return;
            }

            float now = Time.time;
            if (now - _stuckWindowStart < StuckWindowSeconds)
                return;

            bool moved = HorizontalDistance(transform.position, _stuckAnchor) >= StuckMinTravelMeters;
            _stuckAnchor = transform.position;
            _stuckWindowStart = now;
            if (moved)
            {
                _stuckStrikes = 0;
                return;
            }

            if (++_stuckStrikes == 1)
            {
                RepathCurrentGoal();
                return;
            }

            _stuckStrikes = 0;
            switch (_phase)
            {
                case CultistRitualPhase.Traveling:
                    if (TryResolveTravelTarget(Mathf.Min(_travelRoute + 1, TravelRouteNode)))
                    {
                        _nextDestinationTime = 0f;
                        SetDestinationThrottled(_travelTarget, 0.4f);
                    }
                    break;
                case CultistRitualPhase.Aggro:
                    ReturnToRitualServer("stuck while chasing");
                    break;
                case CultistRitualPhase.Fleeing:
                    RepathCurrentGoal();
                    break;
            }
        }

        private void ResetStuckWatchdog()
        {
            _stuckStrikes = 0;
            _stuckAnchor = transform.position;
            _stuckWindowStart = Time.time;
        }

        private void RepathCurrentGoal()
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
                return;

            _agent.ResetPath();
            _nextDestinationTime = 0f;
            switch (_phase)
            {
                case CultistRitualPhase.Traveling:
                    if (_hasTravelTarget)
                        SetDestinationThrottled(_travelTarget, 0.4f);
                    break;
                case CultistRitualPhase.Aggro:
                    if (_targetPlayer != null)
                        SetDestinationThrottled(_targetPlayer.transform.position, 0.16f);
                    break;
                case CultistRitualPhase.Fleeing:
                    PickFleeDestination(force: true);
                    break;
            }
        }

        private void UpdateRitualPose()
        {
            StopAgentMotion();
            FacePosition(_ritualFacePoint, instant: false);
        }

        private void UpdateStare()
        {
            StopAgentMotion();
            if (_targetPlayer == null || _targetPlayer.isPlayerDead)
                _targetPlayer = FindPlayerByClientId(_targetClientId);
            FaceTargetInstant(_targetPlayer);
        }

        private void UpdateAggro()
        {
            if (IsWhiteCultist)
            {
                ResumeRitualAfterStareServer(CultistRitualPhase.Phase3AllPray);
                return;
            }

            PlayerControllerB target = ResolveAggroTarget();
            if (target == null)
            {
                StopAgentMotion();
                // No targetable, reachable player (indoors, in the ship, ignored or cut off): after
                // a short grace the cultist walks back to the ritual instead of standing guard.
                if (_aggroTargetLostSince < 0f)
                    _aggroTargetLostSince = Time.time;
                else if (Time.time - _aggroTargetLostSince >= AggroTargetLostGraceSeconds)
                    ReturnToRitualServer("no reachable target");
                return;
            }

            _aggroTargetLostSince = -1f;
            if (Time.time < _hitRecoveryUntil || IsAuthoredPrayerPlaying())
            {
                StopAgentMotion();
                return;
            }

            _targetPlayer = target;
            _targetClientId = target.actualClientId;
            float distance = Vector3.Distance(transform.position, target.transform.position);
            // A slash plays on the upper-body layer only, so the chase keeps walking and facing
            // through it; UpdateRunIntent holds the pace at walking speed while it lasts.
            FacePosition(GetPlayerAimPoint(target), instant: false);
            SetDestinationThrottled(target.transform.position, 0.16f);

            float attackRange = AttackReach;
            _wantsMovement = distance > attackRange;
            if (distance > attackRange || Time.time < _nextAttackTime)
                return;

            int attackIndex = _nextAttackIndex;
            _nextAttackIndex = CongregantAttackWindow.NextAttackIndex(attackIndex);
            float duration = CongregantAttackWindow.DurationSeconds(attackIndex);
            _nextAttackTime = Time.time + Mathf.Max(duration, ContractsBootstrap.CultistAttackCooldownSeconds.Value);
            TriggerAttackNetworked(attackIndex, target.actualClientId);
            _attackWindow.Begin(target.actualClientId, Time.time, CongregantAttackWindow.ContactSeconds(attackIndex), duration);
        }

        private bool IsAuthoredPrayerPlaying()
        {
            if (_animator == null) return false;
            int state = _animator.GetCurrentAnimatorStateInfo(0).shortNameHash;
            return state == PrayEnterStateHash || state == PrayLoopStateHash || state == PrayExitStateHash;
        }

        private void UpdatePendingAttack()
        {
            if (isEnemyDead || _phase != CultistRitualPhase.Aggro || IsWhiteCultist || _seraphimExecutionLocked)
            {
                _attackWindow.Cancel();
                return;
            }
            if (!_attackWindow.TryConsume(Time.time, out ulong clientId)) return;
            PlayerControllerB victim = FindPlayerByClientId(clientId);
            if (!IsVictimInReach(victim)) return;
            ApplyAuthoritativePlayerDamage(victim, Mathf.Max(1, ContractsBootstrap.CultistAttackDamage.Value), CauseOfDeath.Stabbing);
        }

        private static float AttackReach => Mathf.Max(0.5f, ContractsBootstrap.CultistAttackRange.Value);

        /// <summary>The server's damage test and every peer's impact-sound test: a live, controlled
        /// victim within attack range and in the cultist's front half.</summary>
        private bool IsVictimInReach(PlayerControllerB victim)
        {
            if (victim == null || victim.isPlayerDead || !victim.isPlayerControlled)
                return false;

            Vector3 offset = victim.transform.position - transform.position;
            Vector3 forward = transform.forward;
            return CongregantAttackWindow.IsInReach(offset.x, offset.y, offset.z, forward.x, forward.z, AttackReach);
        }

        private void UpdateFlee()
        {
            if (!EnsureAgentReadyForFlee())
                return;

            if (Time.time >= _nextFleeDestinationTime || ReachedDestination(1.4f))
                PickFleeDestination(force: false);
        }

        /// <summary>Seats the agent for a flee: enables it only on a sampled NavMesh point and
        /// otherwise leaves it for the next tick, so an off-mesh cultist never enables or warps into
        /// Unity's "not close enough to the NavMesh" warning. Returns true when the agent can path.</summary>
        private bool EnsureAgentReadyForFlee()
        {
            if (_agent == null)
                _agent = agent != null ? agent : GetComponent<NavMeshAgent>();
            if (_agent == null)
                return false;

            if (!_agent.enabled)
            {
                // Tight sample first so a seated cultist does not jump; the wider one keeps the
                // previous 14 m recovery reach for a body dropped off the mesh.
                if (!TrySampleNavMesh(transform.position, AgentEnableSampleRadius, out Vector3 seat)
                    && !TrySampleNavMesh(transform.position, AgentRecoverSampleRadius, out seat))
                    return false;

                transform.position = seat;
                _agent.enabled = true;
            }

            if (!_agent.isOnNavMesh)
                SnapToNavMesh(transform.position, AgentRecoverSampleRadius);
            if (!_agent.isOnNavMesh)
                return false;

            if (_agent.isStopped)
                _agent.isStopped = false;
            return true;
        }

        private void PickFleeDestination(bool force)
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
                return;

            Vector3 away = _hasForcedFleeDirection
                ? _forcedFleeDirection.WithCultY(0f)
                : (transform.position - _fleeFromPoint).WithCultY(0f);
            if (away.sqrMagnitude < 0.25f)
                away = UnityEngine.Random.insideUnitSphere.WithCultY(0f);
            if (away.sqrMagnitude < 0.25f)
                away = transform.forward.WithCultY(0f);
            away.Normalize();

            Vector3 best = Vector3.zero;
            bool found = false;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                float angle = _hasForcedFleeDirection
                    ? UnityEngine.Random.Range(-28f, 28f)
                    : UnityEngine.Random.Range(-58f, 58f);
                float distance = _hasForcedFleeDirection
                    ? UnityEngine.Random.Range(42f, 78f)
                    : UnityEngine.Random.Range(22f, 46f);
                Vector3 direction = Quaternion.Euler(0f, angle, 0f) * away;
                Vector3 candidate = transform.position + direction * distance;
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 12f, NavMesh.AllAreas))
                    continue;

                _navPath.ClearCorners();
                if (!NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, _navPath) || _navPath.status == NavMeshPathStatus.PathInvalid)
                    continue;

                best = hit.position;
                found = true;
                break;
            }

            if (!found)
                best = ResolveNavMeshPosition(transform.position + away * (_hasForcedFleeDirection ? 58f : 28f), 22f);

            if (force)
                _nextDestinationTime = 0f;
            SetDestinationThrottled(best, 0.05f);
            _nextFleeDestinationTime = Time.time + UnityEngine.Random.Range(2.2f, 4.1f);
        }

        /// <summary>Resolved once per frame: UpdateRunIntent and UpdateAggro both ask in the same
        /// server tick.</summary>
        private PlayerControllerB ResolveAggroTarget()
        {
            int frame = Time.frameCount;
            if (_aggroTargetFrame == frame)
                return _aggroTargetThisFrame;

            _aggroTargetFrame = frame;
            _aggroTargetThisFrame = FindAggroTarget();
            return _aggroTargetThisFrame;
        }

        /// <summary>Server: the chase target, path-checked every <see cref="AggroPathCheckInterval"/>
        /// and re-checked for targetability every frame in between.</summary>
        private PlayerControllerB FindAggroTarget()
        {
            if (Time.time < _nextAggroPathCheckTime)
                return IsAggroTargetValid(_aggroPathTarget) ? _aggroPathTarget : null;

            _nextAggroPathCheckTime = Time.time + AggroPathCheckInterval;
            _aggroPathTarget = SelectReachableAggroTarget();
            return _aggroPathTarget;
        }

        /// <summary>Keeps the current target while it is targetable and reachable; otherwise the
        /// nearest player that is both takes over, or nobody.</summary>
        private PlayerControllerB SelectReachableAggroTarget()
        {
            PlayerControllerB current = IsAggroTargetValid(_targetPlayer) ? _targetPlayer : FindPlayerByClientId(_targetClientId);
            if (!IsAggroTargetValid(current))
                current = null;
            else if (CanPathToPlayer(current))
                return current;

            StartOfRound round = StartOfRound.Instance;
            PlayerControllerB[] players = round != null ? round.allPlayerScripts : null;
            if (players == null)
                return null;

            // Nearest first without sorting: each pass takes the next-nearest valid player, so a
            // cut-off nearest player hands over to the next one.
            float floor = -1f;
            for (int pass = 0; pass < players.Length; pass++)
            {
                PlayerControllerB nearest = null;
                float nearestDistance = float.MaxValue;
                foreach (PlayerControllerB player in players)
                {
                    if (player == current || !IsAggroTargetValid(player))
                        continue;
                    float distance = Vector3.Distance(transform.position, player.transform.position);
                    if (distance > floor && distance < nearestDistance)
                    {
                        nearest = player;
                        nearestDistance = distance;
                    }
                }

                if (nearest == null)
                    return null;
                if (CanPathToPlayer(nearest))
                    return nearest;
                floor = nearestDistance;
            }

            return null;
        }

        /// <summary>Vanilla targetability for an outdoor enemy plus the host's mob-ignore switch.
        /// PlayerIsTargetable(cannotBeInShip: true) logs "Targetable A" for every in-ship rejection,
        /// so the ship test runs first and the call passes false: the same test without a log line
        /// per scan.</summary>
        private bool IsAggroTargetValid(PlayerControllerB player)
        {
            return player != null
                && player.isPlayerControlled
                && !player.isPlayerDead
                && !player.isInHangarShipRoom
                && PlayerIsTargetable(player, cannotBeInShip: false, overrideInsideFactoryCheck: false)
                && !MobIgnoreBridge.IsIgnored(player.actualClientId);
        }

        /// <summary>A chase starts or continues only toward a player the agent can reach: a
        /// complete path, or a partial one ending within <see cref="AggroPathEndTolerance"/> of the
        /// player. An agent that cannot path right now is no verdict on the target.</summary>
        private bool CanPathToPlayer(PlayerControllerB player)
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
                return true;

            Vector3 goal = player.transform.position;
            if (!TrySampleNavMesh(goal, AggroPathEndTolerance, out Vector3 onMesh))
                return false;

            _navPath.ClearCorners();
            if (!_agent.CalculatePath(onMesh, _navPath))
                return false;
            if (_navPath.status == NavMeshPathStatus.PathComplete)
                return true;
            if (_navPath.status != NavMeshPathStatus.PathPartial)
                return false;

            int count = _navPath.GetCornersNonAlloc(PathCornerBuffer);
            if (count <= 0)
                return false;
            Vector3 end;
            if (count < PathCornerBuffer.Length)
            {
                end = PathCornerBuffer[count - 1];
            }
            else
            {
                Vector3[] corners = _navPath.corners;
                end = corners[corners.Length - 1];
            }
            return Vector3.Distance(end, goal) <= AggroPathEndTolerance;
        }

        private void EnsureRuntimeComponents()
        {
            if (_agent == null)
                _agent = agent != null ? agent : GetComponent<NavMeshAgent>();
            if (agent == null && _agent != null)
                agent = _agent;

            if (_animator == null)
                _animator = creatureAnimator != null ? creatureAnimator : GetComponentInChildren<Animator>(true);
            if (creatureAnimator == null && _animator != null)
                creatureAnimator = _animator;

            if (_audioSource == null)
            {
                _audioSource = creatureSFX != null ? creatureSFX : GetComponent<AudioSource>();
                if (_audioSource == null)
                    _audioSource = gameObject.AddComponent<AudioSource>();
                _audioSource.playOnAwake = false;
                _audioSource.spatialBlend = 1f;
                _audioSource.rolloffMode = AudioRolloffMode.Linear;
                _audioSource.minDistance = 3f;
                _audioSource.maxDistance = 35f;
                _audioSource.volume = 0f;
                _audioSource.mute = true;
                _audioSource.clip = null;
                _audioSource.Stop();
                _audioSource.priority = 78;
                if (creatureSFX == null)
                    creatureSFX = _audioSource;
            }

            if (_sfxAudioSource == null)
                _sfxAudioSource = EnsureLocalSfxSource();

            ApplyBodyCollisionState();
            EnsureScanNode();

            if (_agent != null && !_agentConfigured)
            {
                _agentConfigured = true;
                _agent.angularSpeed = 720f;
                _agent.acceleration = 12f;
                _agent.radius = 0.42f;
                _agent.height = 2.70f;
                _agent.baseOffset = 0f;
                _agent.updateRotation = true;
                _agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
                // Unity's default, pinned: off-mesh links must not strand a cultist mid-route.
                _agent.autoTraverseOffMeshLink = true;
                ApplyAgentPhaseSettings();
            }

            if (_animator != null && !_animatorParametersResolved)
            {
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                ResolveAnimatorParameters();
            }

            _runtimeReady = _agent != null && _animator != null && _animatorParametersResolved;
        }

        /// <summary>Per-phase agent tuning, applied on each phase change rather than every
        /// runtime-component pass. These properties are plain agent settings, valid while the agent
        /// is disabled, so a flee order that lands before the agent is seated still keeps them.</summary>
        private void ApplyAgentPhaseSettings()
        {
            if (_agent == null)
                return;

            bool aggro = _phase == CultistRitualPhase.Aggro;
            bool fleeing = _phase == CultistRitualPhase.Fleeing;
            _agent.speed = aggro
                ? AnimationMatchedAggroMoveSpeed
                : fleeing
                    ? Mathf.Max(SeraphimFleeMoveSpeed, AnimationMatchedAggroMoveSpeed)
                    : TravelMoveSpeed;
            _agent.stoppingDistance = aggro
                ? Mathf.Max(0.35f, ContractsBootstrap.CultistAttackRange.Value * 0.75f)
                : fleeing
                    ? 0.55f
                    : 0.8f;
            _agent.autoBraking = !aggro && !fleeing;
            _agent.avoidancePriority = Mathf.Clamp(45 + _index, 0, 99);
        }

        /// <summary>One pass over the controller's parameters once the animator is initialized:
        /// records which contract parameters exist (so the per-frame sets never name a missing one)
        /// and logs the sidecar's VisualRevision once per session so a wrong sidecar is visible.</summary>
        private void ResolveAnimatorParameters()
        {
            if (!_animator.isInitialized)
                return;

            _animatorParametersResolved = true;

            int revision = 0;
            foreach (AnimatorControllerParameter parameter in _animator.parameters)
            {
                int hash = parameter.nameHash;
                AnimatorControllerParameterType type = parameter.type;
                if (hash == MoveSpeedHash) _hasMoveSpeed = type == AnimatorControllerParameterType.Float;
                else if (hash == GaitRateHash) _hasGaitRate = type == AnimatorControllerParameterType.Float;
                else if (hash == RunBlendHash) _hasRunBlend = type == AnimatorControllerParameterType.Float;
                else if (hash == PrayingHash) _hasPraying = type == AnimatorControllerParameterType.Bool;
                else if (hash == DeadHash) _hasDead = type == AnimatorControllerParameterType.Bool;
                else if (hash == FleeingHash) _hasFleeing = type == AnimatorControllerParameterType.Bool;
                else if (hash == AttackHash) _hasAttack = type == AnimatorControllerParameterType.Trigger;
                else if (hash == AttackIndexHash) _hasAttackIndex = type == AnimatorControllerParameterType.Int;
                else if (hash == AttackSpeedHash) _hasAttackSpeed = type == AnimatorControllerParameterType.Float;
                else if (hash == DrummingHash) _hasDrumming = type == AnimatorControllerParameterType.Bool;
                else if (hash == HitHash) _hasHit = type == AnimatorControllerParameterType.Trigger;
                else if (hash == VisualRevisionHash && type == AnimatorControllerParameterType.Int) revision = parameter.defaultInt;
            }

            _hasDrummingState = _animator.HasState(0, DrummingStateHash);
            _hasPrayLoopState = _animator.HasState(0, PrayLoopStateHash);
            _attackLayer = _animator.GetLayerIndex(AttackLayerName);
            _hasAttackNoneState = _attackLayer >= 0 && _animator.HasState(_attackLayer, AttackNoneStateHash);
            ApplyDrumState();

            if (_visualRevisionLogged)
                return;
            _visualRevisionLogged = true;
            string message = $"[MoonContracts.Cult] Congregant animator VisualRevision={revision} (expected {ExpectedVisualRevision}).";
            if (revision < ExpectedVisualRevision)
                ContractsBootstrap.Log?.LogWarning(message);
            else
                ContractsBootstrap.Log?.LogInfo(message);
        }

        private void EnsureScanNode()
        {
            if (_scanNode != null)
            {
                ConfigureScanNode(_scanNode);
                return;
            }

            Transform existing = transform.Find("ScanNode") ?? transform.Find("CultistScanNode");
            GameObject scanObject = existing != null
                ? existing.gameObject
                : new GameObject("ScanNode");
            scanObject.name = "ScanNode";
            scanObject.transform.SetParent(transform, false);
            scanObject.transform.localPosition = new Vector3(0f, 1.38f, 0f);
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

            BoxCollider box = scanObject.GetComponent<BoxCollider>();
            if (box == null)
                box = scanObject.AddComponent<BoxCollider>();
            box.enabled = true;
            box.isTrigger = true;
            box.center = Vector3.zero;
            box.size = new Vector3(1.32f, 2.47f, 1.32f);

            _scanNode = scanObject.GetComponent<ScanNodeProperties>();
            if (_scanNode == null)
                _scanNode = scanObject.AddComponent<ScanNodeProperties>();

            ConfigureScanNode(_scanNode);
        }

        private void ConfigureScanNode(ScanNodeProperties scanNode)
        {
            if (scanNode == null)
                return;

            scanNode.gameObject.name = "ScanNode";
            scanNode.gameObject.layer = ResolveLayer("ScanNode", ScanNodeLayer);
            scanNode.headerText = _role == CultistRole.White ? "Cult Leader" : "Cultist";
            scanNode.subText = "The Congregation";
            scanNode.maxRange = 32;
            scanNode.minRange = 1;
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = 1;
            scanNode.creatureScanID = -1;
            scanNode.scrapValue = 0;
        }

        private static int ResolveLayer(string layerName, int fallback)
        {
            int layer = LayerMask.NameToLayer(layerName);
            return layer >= 0 ? layer : fallback;
        }

        private AudioSource EnsureLocalSfxSource()
        {
            Transform child = transform.Find("CultistRuntimeSFX");
            GameObject audioObject;
            if (child == null)
            {
                audioObject = new GameObject("CultistRuntimeSFX");
                audioObject.transform.SetParent(transform, false);
            }
            else
            {
                audioObject = child.gameObject;
            }
            audioObject.transform.localPosition = new Vector3(0f, LocalAudioSourceHeight, 0f);

            var source = audioObject.GetComponent<AudioSource>();
            if (source == null)
                source = audioObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 2.25f;
            source.maxDistance = 42f;
            source.priority = 72;
            source.volume = 1f;
            source.mute = false;
            return source;
        }

        private void ConfigureAnimationPhaseOffset()
        {
            _walkCycleOffset = Mathf.Repeat((_index * 0.173f) + ((int)_role * 0.131f), 1f);
            _locomotionOffsetApplied = false;
            _footfallPhase = _walkCycleOffset;
        }

        /// <summary>Seeds the locomotion cycle once, at the first Traveling state, so the group does
        /// not walk in lockstep. Phase/network updates never restart an authored prayer, recoil or
        /// attack, so it is a no-op once applied or outside Traveling.</summary>
        private void ApplyLocomotionCycleOffset()
        {
            // A late joiner's resend can land on a body killed while Traveling; never replay Idle over Dead.
            if (_animator == null || _locomotionOffsetApplied || _phase != CultistRitualPhase.Traveling || isEnemyDead)
                return;

            _animator.Play(IdleStateHash, 0, _walkCycleOffset);
            _animator.Update(0f);
            _locomotionOffsetApplied = true;
        }

        private void EnsureBehaviourStates()
        {
            if (enemyBehaviourStates != null && enemyBehaviourStates.Length > 0)
                return;

            enemyBehaviourStates = new[]
            {
                new EnemyBehaviourState { name = "Traveling", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Ritual", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Stare", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Aggro", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Dead", IsAnimTrigger = false, parameterString = "Dead", boolValue = true },
            };
        }

        private void ApplyAnimationState(bool forceIdlePose)
        {
            StopChantIfIneligible();
            if (_animator == null)
                return;

            bool praying = ShouldPrayInCurrentPhase() && !_aggroed && !isEnemyDead && !forceIdlePose;
            if (_phase != CultistRitualPhase.Aggro || isEnemyDead)
            {
                _attackWindow.Cancel();
                CancelAttackLocal();
            }
            ApplyDrumState();
            SetBool(FleeingHash, _hasFleeing, _phase == CultistRitualPhase.Fleeing && !isEnemyDead);
            SetBool(PrayingHash, _hasPraying, praying);
            SetBool(DeadHash, _hasDead, isEnemyDead);
            if (forceIdlePose) SetFloat(MoveSpeedHash, _hasMoveSpeed, 0f);
            // The controller owns PrayExit and the continuous flee state.
        }

        private bool ShouldPrayInCurrentPhase()
        {
            // The drummer drums through every phase (#845 D); _drumming reaches clients in the sync.
            if (_drumming)
                return false;
            if (_phase == CultistRitualPhase.Phase3AllPray)
                return true;
            return _phase == CultistRitualPhase.Phase2RedPray && _role == CultistRole.Red;
        }

        private void UpdateRunIntent()
        {
            // A slash no longer plants the cultist: it eases back to a walk and keeps chasing.
            bool swinging = _attackWindow.IsSwinging(Time.time);
            bool allowed = !isEnemyDead && !_seraphimExecutionLocked && !IsStunned
                && Time.time >= _hitRecoveryUntil && !swinging && !IsAuthoredPrayerPlaying();
            bool destination = false;
            float distance = 0;
            if (allowed && _phase == CultistRitualPhase.Aggro && !IsWhiteCultist)
            {
                var target = ResolveAggroTarget();
                destination = target != null;
                if (destination) distance = HorizontalDistance(transform.position, target.transform.position);
            }
            else if (allowed && _phase == CultistRitualPhase.Traveling)
            {
                destination = !HasReachedRitualSpot;
                distance = HorizontalDistance(transform.position, _ritualSpot);
            }

            // Fleeing keeps its own stumble-run clip (the Flee tree at RunBlend 0); the run is for
            // travelling to the ritual and chasing players.
            _serverRunRequested = allowed && CongregantLocomotion.WantsRun(destination, distance, _serverRunRequested,
                _phase == CultistRitualPhase.Aggro ? ContractsBootstrap.CultistAttackRange.Value : 0);
            // A server-owned variable also gives late joiners the current gait without guessing target state.
            if (NetworkObject != null && NetworkObject.IsSpawned && _runRequested.Value != _serverRunRequested)
                _runRequested.Value = _serverRunRequested;

            if (_agent != null && _agent.enabled && (_phase == CultistRitualPhase.Traveling || _phase == CultistRitualPhase.Aggro))
            {
                float walkSpeed = _phase == CultistRitualPhase.Aggro ? AnimationMatchedAggroMoveSpeed : TravelMoveSpeed;
                float speed = Mathf.Lerp(walkSpeed, Mathf.Max(walkSpeed, CongregantLocomotion.RunSpeed), _runBlend);
                _agent.speed = swinging ? Mathf.Min(speed, TravelMoveSpeed) : speed;
            }
        }

        private void UpdateAnimationMoveSpeed()
        {
            if (_animator == null)
                return;

            float speed = 0f;
            bool movingPhase = _phase == CultistRitualPhase.Traveling || _phase == CultistRitualPhase.Aggro || _phase == CultistRitualPhase.Fleeing;
            if (_agent != null && _agent.enabled && _agent.isOnNavMesh && !_agent.isStopped)
                speed = _agent.velocity.WithCultY(0f).magnitude;
            // Clients never run the agent (vanilla SetClientCalculatingAI disables it), so their gait
            // reads the replicated body, sampled every tick so the window is fresh on a phase change.
            float replicatedSpeed = IsServerAuthority() ? 0f : SampleReplicatedMoveSpeed();
            if (movingPhase)
                speed = Mathf.Max(speed, replicatedSpeed);

            float delta = Mathf.Clamp(Time.time - _lastVisualUpdateTime, .001f, .2f);
            _lastVisualUpdateTime = Time.time;
            bool run = movingPhase && !isEnemyDead && !_seraphimExecutionLocked
                && (IsServerAuthority() ? _serverRunRequested : _runRequested.Value);
            _runProgress = CongregantLocomotion.Advance(_runProgress, run, delta);
            _runBlend = CongregantLocomotion.Blend(_runProgress);
            SetFloat(RunBlendHash, _hasRunBlend, _runBlend);
            if (!movingPhase || isEnemyDead) speed = 0f;
            _smoothedMoveSpeed = Mathf.Lerp(_smoothedMoveSpeed, speed, 1f - Mathf.Exp(-delta / .12f));
            speed = _smoothedMoveSpeed;

            float normalizedMoveSpeed = 0f;
            if (speed > 0.05f)
            {
                normalizedMoveSpeed = Mathf.Clamp01(speed / VisualWalkFullBlendSpeed);
                if (speed > 0.25f)
                    normalizedMoveSpeed = Mathf.Max(normalizedMoveSpeed, 0.78f);
            }

            SetFloat(MoveSpeedHash, _hasMoveSpeed, normalizedMoveSpeed);
            SetBool(DeadHash, _hasDead, isEnemyDead);
            float gait = UpdateAnimatorPlaybackSpeed(speed, normalizedMoveSpeed);
            UpdateFootstepAudio(normalizedMoveSpeed, delta, gait);
        }

        /// <summary>Client gait speed: horizontal travel of the replicated body over the last
        /// <see cref="ReplicatedMoveSampleCount"/> visual ticks. Vanilla replication moves it in
        /// 0.35 m serverPosition steps smoothed over 0.13 s, so a one-tick delta alternates between
        /// a burst and a stop; the window reads a walk as a walk.</summary>
        private float SampleReplicatedMoveSpeed()
        {
            Vector3 currentPosition = transform.position;
            float now = Time.time;
            float speed = 0f;
            if (_replicatedMoveSampleCount > 0)
            {
                int oldest = _replicatedMoveSampleCount < ReplicatedMoveSampleCount ? 0 : _replicatedMoveSampleHead;
                float elapsed = now - _replicatedMoveSampleTimes[oldest];
                if (elapsed > 0.001f)
                    speed = HorizontalDistance(currentPosition, _replicatedMoveSamplePositions[oldest]) / elapsed;
            }

            _replicatedMoveSamplePositions[_replicatedMoveSampleHead] = currentPosition;
            _replicatedMoveSampleTimes[_replicatedMoveSampleHead] = now;
            _replicatedMoveSampleHead = (_replicatedMoveSampleHead + 1) % ReplicatedMoveSampleCount;
            if (_replicatedMoveSampleCount < ReplicatedMoveSampleCount)
                _replicatedMoveSampleCount++;

            float maxSampleSpeed = _phase == CultistRitualPhase.Fleeing
                ? SeraphimFleeMoveSpeed + 1.25f
                : CongregantLocomotion.RunSpeed + 1f;
            return Mathf.Clamp(speed, 0f, maxSampleSpeed);
        }

        private float UpdateAnimatorPlaybackSpeed(float worldSpeed, float normalizedMoveSpeed)
        {
            if (_animator == null)
                return 1f;

            // Measured at the 2.62 m runtime height; black's longer legs travel farther.
            float reference = _role == CultistRole.Black
                ? CongregantLocomotion.BlackWalkStrideSpeed : CongregantLocomotion.WalkStrideSpeed;
            bool fleeing = _phase == CultistRitualPhase.Fleeing;
            if (fleeing) reference *= 1.667f;
            float runReference = _role == CultistRole.Black
                ? CongregantLocomotion.BlackRunStrideSpeed
                : _role == CultistRole.White ? CongregantLocomotion.WhiteRunStrideSpeed : CongregantLocomotion.RedRunStrideSpeed;
            float walkSeconds = fleeing ? CongregantLocomotion.FleeClipSeconds : CongregantLocomotion.WalkClipSeconds;
            reference = CongregantLocomotion.GaitReferenceSpeed(reference, walkSeconds, runReference, _runBlend);
            float gait = normalizedMoveSpeed <= .01f || isEnemyDead ? 1f : Mathf.Clamp(worldSpeed / reference, .65f, 3f);
            SetFloat(GaitRateHash, _hasGaitRate, gait);
            return gait;
        }

        /// <summary>Own footfall clock: two steps per clip cycle at the clip's own speed, advanced
        /// by the visual tick. A gated-off tick pauses the clock instead of resetting it, so a
        /// one-tick dip can never double a step.</summary>
        private void UpdateFootstepAudio(float normalizedMoveSpeed, float delta, float gait)
        {
            if (_animator == null || _sfxAudioSource == null || isEnemyDead
                || normalizedMoveSpeed < FootstepMoveThreshold
                || (_phase != CultistRitualPhase.Traveling && _phase != CultistRitualPhase.Aggro && _phase != CultistRitualPhase.Fleeing))
            {
                _footstepEmitting = false;
                return;
            }

            AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(0);
            if (state.shortNameHash != IdleStateHash && state.shortNameHash != FleeStateHash)
            {
                _footstepEmitting = false;
                return;
            }

            _footstepEmitting = true;
            float walkSeconds = _phase == CultistRitualPhase.Fleeing
                ? CongregantLocomotion.FleeClipSeconds
                : CongregantLocomotion.WalkClipSeconds;
            float cycle = CongregantLocomotion.CycleSeconds(walkSeconds, _runBlend);
            if (CongregantLocomotion.AdvanceFootfall(ref _footfallPhase, delta, cycle, gait))
                PlayFootstepLocal();
        }

        /// <summary>One footfall, local on every peer off its own footfall clock (no RPC), in
        /// WhistleblowerEnemyAI's order: the audible noise once, then vanilla's surface step, then
        /// the bundled foley only when the surface has no playable clip.</summary>
        private void PlayFootstepLocal()
        {
            bool running = (_phase == CultistRitualPhase.Fleeing ? 1f : _runBlend) >= RunFootstepBlendThreshold;
            EmitFootstepNoise(running);
            if (!IsAudibleFootstepEmitter())
                return;
            if (TryPlaySurfaceFootstep(running))
                return;

            AudioClip[] clips = ContractAssetLoader.CultistFootstepClips;
            if (clips == null || clips.Length == 0 || _sfxAudioSource == null)
                return;

            int index = NextFootstepClipIndex(clips.Length);
            AudioClip clip = index >= 0 && index < clips.Length ? clips[index] : null;
            if (clip == null)
                return;

            float volume = running ? RunFootstepVolume : WalkFootstepVolume;
            _sfxAudioSource.pitch = UnityEngine.Random.Range(0.93f, 1.07f);
            _sfxAudioSource.PlayOneShot(clip, volume);
            WalkieTalkie.TransmitOneShotAudio(_sfxAudioSource, clip, volume);
            _sfxAudioSource.pitch = 1f;
        }

        /// <summary>Whether this actor is among the <see cref="MaxAudibleFootstepEmitters"/> stepping
        /// congregants nearest the local listener (ties: lower instance id). A whole congregation
        /// stepping at once (6 travelling and 5 fleeing together in the 2026-10-04 run) exceeds
        /// vanilla's four players and reads as clutter; the rest still emit noise.</summary>
        private bool IsAudibleFootstepEmitter()
        {
            StartOfRound round = StartOfRound.Instance;
            AudioListener listener = round != null ? round.audioListener : null;
            if (listener == null)
                return true;

            Vector3 listenerPosition = listener.transform.position;
            float ownDistance = (transform.position - listenerPosition).sqrMagnitude;
            int ownId = GetInstanceID();
            int closer = 0;
            for (CongregantAI actor = LocalChantActors; actor != null; actor = actor.NextLocalChantActor)
            {
                if (actor == this || !actor._footstepEmitting)
                    continue;

                float distance = (actor.transform.position - listenerPosition).sqrMagnitude;
                if (distance < ownDistance || (distance == ownDistance && actor.GetInstanceID() < ownId))
                {
                    closer++;
                    if (closer >= MaxAudibleFootstepEmitters)
                        return false;
                }
            }

            return true;
        }

        /// <summary>Vanilla footstep noise propagation (PlayFootstepServer/PlayFootstepLocal), the
        /// dial other enemies read; issued on every peer like WhistleblowerEnemyAI.EmitFootstepNoise.</summary>
        private void EmitFootstepNoise(bool running)
        {
            RoundManager round = RoundManager.Instance;
            if (round == null)
                return;

            bool insideClosedShip = isInsidePlayerShip
                && StartOfRound.Instance != null
                && StartOfRound.Instance.hangarDoorsClosed;
            round.PlayAudibleNoise(
                transform.position,
                running ? RunFootstepNoiseRange : WalkFootstepNoiseRange,
                running ? RunFootstepNoiseLoudness : WalkFootstepNoiseLoudness,
                0,
                insideClosedShip,
                FootstepNoiseId);
        }

        /// <summary>Surface pick copied from WhistleblowerEnemyAI.TryPlayVanillaFootstep (vanilla
        /// MaskedPlayerEnemy.GetMaterialStandingOn): raycast down, tag-match the collider against
        /// StartOfRound.footstepSurfaces, keep the last surface on a miss.</summary>
        private bool TryPlaySurfaceFootstep(bool running)
        {
            StartOfRound round = StartOfRound.Instance;
            FootstepSurface[] surfaces = round != null ? round.footstepSurfaces : null;
            if (surfaces == null || surfaces.Length == 0 || _sfxAudioSource == null)
                return false;

            if (Physics.Raycast(transform.position + Vector3.up, Vector3.down, out RaycastHit hit, 6f, round.walkableSurfacesMask, QueryTriggerInteraction.Ignore))
            {
                for (int i = 0; i < surfaces.Length; i++)
                {
                    if (hit.collider.CompareTag(surfaces[i].surfaceTag))
                    {
                        _footstepSurfaceIndex = i;
                        break;
                    }
                }
            }

            AudioClip[] clips = surfaces[Mathf.Clamp(_footstepSurfaceIndex, 0, surfaces.Length - 1)].clips;
            if (clips == null || clips.Length == 0)
                return false;

            int clipIndex = UnityEngine.Random.Range(0, clips.Length);
            if (clipIndex == _lastSurfaceFootstepClip && clips.Length > 1)
                clipIndex = (clipIndex + 1) % clips.Length;
            AudioClip clip = null;
            for (int attempt = 0; attempt < clips.Length && clip == null; attempt++)
            {
                int candidate = (clipIndex + attempt) % clips.Length;
                if (clips[candidate] != null)
                {
                    clip = clips[candidate];
                    clipIndex = candidate;
                }
            }
            if (clip == null)
                return false;
            _lastSurfaceFootstepClip = clipIndex;

            // PlayerControllerB's own step loudness: binary 0.6 walking, 0.9 sprinting.
            float volume = running ? RunFootstepVolume : WalkFootstepVolume;
            _sfxAudioSource.pitch = UnityEngine.Random.Range(0.93f, 1.07f);
            _sfxAudioSource.PlayOneShot(clip, volume);
            WalkieTalkie.TransmitOneShotAudio(_sfxAudioSource, clip, volume);
            _sfxAudioSource.pitch = 1f;
            return true;
        }

        private int NextFootstepClipIndex(int clipCount)
        {
            if (clipCount <= 0)
                return -1;

            if (_footstepBag == null || _footstepBag.Length != clipCount || _footstepBagCursor >= _footstepBag.Length)
                RefillFootstepBag(clipCount);

            int index = _footstepBag[Mathf.Clamp(_footstepBagCursor, 0, _footstepBag.Length - 1)];
            _footstepBagCursor++;
            _lastFootstepClipIndex = index;
            return index;
        }

        private void RefillFootstepBag(int clipCount)
        {
            _footstepBag = new int[clipCount];
            for (int i = 0; i < clipCount; i++)
                _footstepBag[i] = i;

            for (int i = 0; i < clipCount; i++)
            {
                int swap = UnityEngine.Random.Range(i, clipCount);
                int value = _footstepBag[i];
                _footstepBag[i] = _footstepBag[swap];
                _footstepBag[swap] = value;
            }

            if (clipCount > 1 && _footstepBag[0] == _lastFootstepClipIndex)
            {
                int value = _footstepBag[0];
                _footstepBag[0] = _footstepBag[1];
                _footstepBag[1] = value;
            }

            _footstepBagCursor = 0;
        }

        private void TriggerAttackNetworked(int attackIndex, ulong targetClientId)
        {
            TriggerAttackLocal(attackIndex, targetClientId);
            if (NetworkObject != null && NetworkObject.IsSpawned && IsServerAuthority())
                TriggerAttackClientRpc(attackIndex, targetClientId);
        }

        /// <summary>Every peer: starts the upper-body slash and arms its impact sound.</summary>
        private void TriggerAttackLocal(int attackIndex, ulong targetClientId)
        {
            if (_animator == null || IsStunned)
                return;

            // AttackIndex first: the Attack layer's AnyState transitions read both on the same update.
            SetInteger(AttackIndexHash, _hasAttackIndex, attackIndex);
            SetTrigger(AttackHash, _hasAttack);
            _pendingImpactIndex = attackIndex;
            _pendingImpactTarget = targetClientId;
            _pendingImpactArmed = false;
            // Clip duration plus slack for the entry transition; a swing never seen expires here.
            _pendingImpactUntil = Time.time + CongregantAttackWindow.DurationSeconds(attackIndex) + 0.5f;
        }

        /// <summary>Every peer: drops a pending impact and an unconsumed Attack trigger, and fades
        /// the Attack layer out of a slash the controller would otherwise finish.</summary>
        private void CancelAttackLocal()
        {
            _pendingImpactIndex = -1;
            if (_animator == null || !_hasAttack)
                return;

            _animator.ResetTrigger(AttackHash);
            if (!_hasAttackNoneState)
                return;
            int state = _animator.GetCurrentAnimatorStateInfo(_attackLayer).shortNameHash;
            if (state == KnifeInwardStateHash || state == KnifeOutwardStateHash)
                _animator.CrossFadeInFixedTime(AttackNoneStateHash, AttackCancelFadeSeconds, _attackLayer);
        }

        /// <summary>Every peer, no RPC: plays the impact the frame this peer's Attack layer crosses the
        /// swing's contact time (normalized, as the footsteps cross their phases), gated by the
        /// server's reach test so a miss stays silent.</summary>
        private void UpdateAttackImpactAudio()
        {
            if (_pendingImpactIndex < 0)
                return;
            if (_animator == null || _attackLayer < 0 || isEnemyDead || Time.time > _pendingImpactUntil)
            {
                _pendingImpactIndex = -1;
                return;
            }

            int stateHash = _pendingImpactIndex == CongregantAttackWindow.KnifeOutwardIndex ? KnifeOutwardStateHash : KnifeInwardStateHash;
            AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(_attackLayer);
            if (state.shortNameHash != stateHash && _animator.IsInTransition(_attackLayer))
                state = _animator.GetNextAnimatorStateInfo(_attackLayer);
            if (state.shortNameHash != stateHash)
                return;

            // Armed once the swing is seen before contact, so a stale state never fires on sight.
            if (state.normalizedTime < CongregantAttackWindow.ContactNormalizedTime(_pendingImpactIndex))
            {
                _pendingImpactArmed = true;
                return;
            }

            bool land = _pendingImpactArmed && !IsStunned && _phase == CultistRitualPhase.Aggro
                && IsVictimInReach(FindPlayerByClientId(_pendingImpactTarget));
            _pendingImpactIndex = -1;
            if (land)
                PlayAttackImpactLocal();
        }

        /// <summary>The single lookup for the slash impact sound: one of the ceremony sidecar's
        /// three body hits per play, never the same twice running (#845 D).</summary>
        private static AudioClip AttackImpactClip => CongregationPropsAssets.RandomHit();

        private void PlayAttackImpactLocal()
        {
            if (_sfxAudioSource == null)
                _sfxAudioSource = EnsureLocalSfxSource();

            AudioClip clip = AttackImpactClip;
            if (clip == null || _sfxAudioSource == null)
                return;

            _sfxAudioSource.pitch = UnityEngine.Random.Range(0.97f, 1.04f);
            _sfxAudioSource.PlayOneShot(clip, AttackImpactVolume);
            _sfxAudioSource.pitch = 1f;
        }

        private void TriggerHitLocal()
        {
            if (_animator == null || IsStunned)
                return;
            // Full-body recoil starts from standing; don't snap a kneeling/fleeing body into it.
            if (ShouldPrayInCurrentPhase() || IsAuthoredPrayerPlaying() || _phase == CultistRitualPhase.Fleeing) return;
            _hitRecoveryUntil = Mathf.Max(_hitRecoveryUntil, Time.time + .8f); // Recoil plus its exit blend stays planted.
            SetTrigger(HitHash, _hasHit);
        }

        private void HandleDeadState()
        {
            ApplyBodyCollisionState();
            StopChantIfIneligible();
            _attackWindow.Cancel();
            StopAgentMotion();
            SetBool(DeadHash, _hasDead, true);
        }

        /// <summary>Client: a late joiner never saw KillEnemyClientRpc, so the full-state resend
        /// carries the death. The local half of vanilla KillEnemy (scan node collider and agent off,
        /// Dead behaviour state) without its RPCs; Update then holds the pose.</summary>
        private void ApplyReplicatedDeath()
        {
            isEnemyDead = true;
            _deadHandled = true;
            _seraphimExecutionLocked = false;
            SwitchToBehaviourStateOnLocalClient(4);
            if (_scanNode != null)
            {
                Collider scanCollider = _scanNode.GetComponent<Collider>();
                if (scanCollider != null)
                    scanCollider.enabled = false;
            }
            if (_agent != null)
                _agent.enabled = false;
            HandleDeadState();
            RefreshHandProp();
        }

        private void SetDestinationThrottled(Vector3 destination, float interval)
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh || Time.time < _nextDestinationTime)
                return;

            _nextDestinationTime = Time.time + Mathf.Max(0.02f, interval);
            _agent.isStopped = false;
            _agent.SetDestination(ResolveNavMeshPosition(destination, 8f));
        }

        private bool ReachedDestination(float distance)
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
                return false;
            if (_agent.pathPending)
                return false;
            return _agent.remainingDistance <= distance;
        }

        private void StopAgentMotion()
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
                return;

            _agent.isStopped = true;
            // ResetPath on an idle agent is not free, and the ritual pose calls this every frame.
            if (_agent.hasPath || _agent.pathPending)
                _agent.ResetPath();
        }

        private void EnsureOnNavMesh()
        {
            if (_agent == null || !_agent.enabled || _agent.isOnNavMesh)
                return;
            SnapToNavMesh(transform.position, 8f);
        }

        /// <summary>Warps only onto a sampled NavMesh point; an unresolved origin is left for the
        /// next tick instead of warping off-mesh, which is what logged "Failed to create agent
        /// because it is not close enough to the NavMesh" every frame after the flee release.</summary>
        private void SnapToNavMesh(Vector3 origin, float maxDistance)
        {
            if (_agent == null || !_agent.enabled)
                return;

            if (TrySampleNavMesh(origin, maxDistance, out Vector3 target))
                _agent.Warp(target);
        }

        private static bool TrySampleNavMesh(Vector3 origin, float maxDistance, out Vector3 position)
        {
            if (NavMesh.SamplePosition(origin, out NavMeshHit hit, maxDistance, NavMesh.AllAreas))
            {
                position = hit.position;
                return true;
            }

            position = origin;
            return false;
        }

        private static Vector3 ResolveNavMeshPosition(Vector3 origin, float maxDistance)
        {
            TrySampleNavMesh(origin, maxDistance, out Vector3 position);
            return position;
        }

        private void FaceTargetInstant(PlayerControllerB target)
        {
            if (target != null)
                FacePosition(GetPlayerAimPoint(target), instant: true);
        }

        private void FacePosition(Vector3 target, bool instant)
        {
            Vector3 flat = (target - transform.position).WithCultY(0f);
            if (flat.sqrMagnitude < 0.01f)
                return;

            Quaternion look = Quaternion.LookRotation(flat.normalized, Vector3.up);
            transform.rotation = instant
                ? look
                : Quaternion.RotateTowards(transform.rotation, look, 360f * Time.deltaTime);
        }

        // Every peer derives physical presence from the existing replicated death/phase state.
        // The execution lock is server-local; Phase4Demon keeps clients and late joiners in sync.
        private void ApplyBodyCollisionState()
        {
            if (_bodyCollider == null)
                _bodyCollider = CongregantCollision.Ensure(this);
            if (_bodyCollider == null)
                return;

            bool active = !isEnemyDead && _phase != CultistRitualPhase.Phase4Demon && !_seraphimExecutionLocked;
            if (_bodyCollider.enabled != active)
                _bodyCollider.enabled = active;
        }

        private void SetBool(int hash, bool present, bool value)
        {
            if (present && _animator != null)
                _animator.SetBool(hash, value);
        }

        private void SetFloat(int hash, bool present, float value)
        {
            if (present && _animator != null)
                _animator.SetFloat(hash, value);
        }

        private void SetInteger(int hash, bool present, int value)
        {
            if (present && _animator != null)
                _animator.SetInteger(hash, value);
        }

        private void SetTrigger(int hash, bool present)
        {
            if (!present || _animator == null)
                return;
            _animator.ResetTrigger(hash);
            _animator.SetTrigger(hash);
        }

        private bool IsServerAuthority()
        {
            if (NetworkManager.Singleton == null)
                return true;
            return NetworkManager.Singleton.IsServer || IsServer;
        }

        private void ApplyAuthoritativePlayerDamage(PlayerControllerB victim, int damage, CauseOfDeath causeOfDeath)
        {
            if (victim == null || damage <= 0 || victim.isPlayerDead || !victim.isPlayerControlled)
                return;

            // PlayerControllerB.DamagePlayer is victim-owner-only (it early-returns unless IsOwner),
            // so the server announces the hit and only the owning client invokes vanilla damage.
            if (!IsServerAuthority())
                return;

            if (NetworkObject == null || !NetworkObject.IsSpawned)
            {
                if (victim.IsOwner)
                    victim.DamagePlayer(damage, hasDamageSFX: true, callRPC: true, causeOfDeath);
                return;
            }

            ApplyAuthoritativePlayerDamageClientRpc(
                victim.actualClientId,
                damage,
                (int)causeOfDeath,
                ++_playerDamageSerial);
        }

        [ClientRpc]
        private void ApplyAuthoritativePlayerDamageClientRpc(ulong victimClientId, int damage, int causeOfDeath, int serial)
        {
            if (serial <= _lastAppliedPlayerDamageSerial)
                return;
            _lastAppliedPlayerDamageSerial = serial;

            PlayerControllerB victim = FindPlayerByClientId(victimClientId);
            if (victim == null || victim.isPlayerDead || !victim.isPlayerControlled || !victim.IsOwner)
                return;

            victim.DamagePlayer(damage, hasDamageSFX: true, callRPC: true, (CauseOfDeath)causeOfDeath);
        }

        private static PlayerControllerB FindPlayerByClientId(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            foreach (PlayerControllerB player in players)
            {
                if (player != null && player.actualClientId == clientId)
                    return player;
            }
            return null;
        }

        private static Vector3 GetPlayerAimPoint(PlayerControllerB player)
        {
            if (player?.gameplayCamera != null)
                return player.gameplayCamera.transform.position;
            return player != null ? player.transform.position + Vector3.up * 1.5f : Vector3.zero;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        [ClientRpc]
        private void SyncStateClientRpc(int role, int phase, Vector3 ritualPoint, Vector3 ritualSpot, int index, bool aggroed, ulong targetClientId, bool dead, bool drumming)
        {
            if (IsServerAuthority())
                return;

            bool identityChanged = _index != index || _role != (CultistRole)role;
            _role = (CultistRole)role;
            _phase = (CultistRitualPhase)phase;
            _ritualPoint = ritualPoint;
            _ritualSpot = ritualSpot;
            _index = index;
            _aggroed = aggroed;
            _targetClientId = targetClientId;
            _targetPlayer = FindPlayerByClientId(targetClientId);
            _drumming = drumming;
            if (identityChanged)
                ConfigureAnimationPhaseOffset();
            EnsureRuntimeComponents();
            if (dead && !isEnemyDead)
                ApplyReplicatedDeath();
            ApplyAgentPhaseSettings();
            ApplyAnimationState(forceIdlePose: _phase == CultistRitualPhase.Stare);
            ApplyLocomotionCycleOffset();
        }

        [ClientRpc]
        private void SyncPhaseClientRpc(int phase, bool aggroed, ulong targetClientId)
        {
            if (IsServerAuthority())
                return;

            _phase = (CultistRitualPhase)phase;
            _aggroed = aggroed;
            _targetClientId = targetClientId;
            _targetPlayer = FindPlayerByClientId(targetClientId);
            EnsureRuntimeComponents();
            ApplyAgentPhaseSettings();
            ApplyAnimationState(forceIdlePose: _phase == CultistRitualPhase.Stare);
            ApplyLocomotionCycleOffset();
        }

        [ClientRpc]
        private void TriggerAttackClientRpc(int attackIndex, ulong targetClientId)
        {
            if (!IsServerAuthority())
                TriggerAttackLocal(attackIndex, targetClientId);
        }

        /// <summary>Clients: the scrap value the server rolled for this congregant's death drop
        /// (SetScrapValue is local; pattern of SyncCargoValueClientRpc). A player joining after the
        /// drop misses it, as it misses vanilla's own scrap value sync.</summary>
        [ClientRpc]
        private void SyncLootValueClientRpc(NetworkObjectReference loot, int value)
        {
            if (IsServerAuthority() || !loot.TryGet(out NetworkObject lootObject))
                return;
            GrabbableObject grabbable = lootObject.GetComponent<GrabbableObject>();
            if (grabbable == null)
                return;
            grabbable.SetScrapValue(value);
            // The server added it in CongregationLoot.DropServer; vanilla keeps the total on every peer.
            if (RoundManager.Instance != null)
                RoundManager.Instance.totalScrapValueInLevel += value;
        }
    }

    internal static class CultistVectorExtensions
    {
        internal static Vector3 WithCultY(this Vector3 value, float y)
        {
            value.y = y;
            return value;
        }
    }
}
