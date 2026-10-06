using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Escalations._Shared;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteSoldierRole
    {
        SquadMember = 0
    }

    internal enum BlacksiteAudioCue
    {
        SingleShot,
        BurstShot,
        Grenade,
        Footstep,
        VoiceAffirmative,
        VoiceContact,
        VoiceCommand,
        VoiceDeploy,
        VoiceHit,
        VoiceRetreat
    }

    public sealed partial class BlacksiteSoldierAI : EnemyAI, IVisibleThreat
    {
        private const float VisionDotThreshold = 0.64f;
        private const float AlertedVisionDotThreshold = 0.35f;
        private const float ProximitySenseRange = 2.5f;
        // #874 S11 U2: FootstepHearingDotThreshold 0.22 and VoiceHearingDotThreshold -0.05 were
        // deleted here. Both were authored for a directional hearing test that was never written
        // and neither had a single reader (review-blacksite-ai W19). Directional hearing is I1 and
        // belongs in a BlacksiteHearingPolicy with its own derivation, not in two orphan constants
        // that read as if the cone already existed.
        private const float MaxAggroRange = 22f;
        private const float SprintNoiseRange = 15f;
        private const float WalkNoiseRange = 8f;
        private const float CrouchNoiseRange = 5f;
        private const float VoiceNoiseRange = 15f;
        private const float EnemyVisionRange = 15f;
        private const float SquadStimulusRadius = 20f;
        private const float MaxStimulusOffset = 24f;
        private const float StimulusDebounceSeconds = 1f;
        private const float BurstFireChance = 0.45f;
        private const float SupportBurstFireChance = 0.7f;
        // #937: ten hit points, Lawson's 2026-09-10 balance call (was 4).
        // #1065 WP4: twenty, Lawson's 2026-09-15 call, and the number itself now lives in
        // BlacksiteCombatPolicy beside the rifle's damage per round so the two halves of the
        // exchange rate are one Unity-free fact BlacksiteTierOneRegression can pin. The forwarder
        // keeps this name private to the file, so the spawn-state call site below is unchanged.
        // There is NO BepInEx config entry for soldier health, so this default is the live value on
        // every existing profile - nothing has to migrate.
        private const int SoldierHealth = BlacksiteCombatPolicy.SoldierHealth;
        // #874 S5.4: unchanged value, read from the Unity-free column so the far-side hold the
        // service model excludes and the hold the runtime actually takes cannot drift apart.
        private const float EntrancePostTeleportSettleSeconds = BlacksiteEntranceColumn.FarSideSettleSeconds;
        private const float VanillaDoorOpenSpeedMultiplier = 1.5f;
        private const float FootstepAudioMinInterval = 0.14f;
        private const float HitReactionCooldownSeconds = 0.65f;
        private const float FootstepCyclePointA = 0.18f;
        private const float FootstepCyclePointB = 0.68f;
        private const float AnimationFloatDampTime = BlacksiteStancePolicy.AnimationFloatDampSeconds;
        private const float CrouchEnterSpeedThreshold = BlacksiteCombatPolicy.CrouchEnterSpeedThreshold;
        private const float CrouchMinimumHoldSeconds = BlacksiteCombatPolicy.CrouchMinimumHoldSeconds;
        private const float CrouchExitSpeedThreshold = BlacksiteCombatPolicy.CrouchExitSpeedThreshold;
        private const float CrouchFastMoveExitDelaySeconds = BlacksiteCombatPolicy.CrouchFastMoveExitDelaySeconds;
        private const float CrouchReentryCooldownSeconds = BlacksiteCombatPolicy.CrouchReentryCooldownSeconds;
        // #956 S12A U3: the four movement constants live in BlacksiteMotionPolicy now, beside the
        // arbitration that reads them, so the Unity-free regression measures the same numbers the
        // agent is commanded with. None of the values changed.
        private const float CrouchedMoveSpeedMultiplier = BlacksiteMotionPolicy.CrouchedMoveSpeedMultiplier;
        private const float ApproachSlowdownDistance = BlacksiteMotionPolicy.ApproachSlowdownDistance;
        private const float ApproachWalkDistance = BlacksiteMotionPolicy.ApproachWalkDistance;
        private const float WalkApproachSpeed = BlacksiteMotionPolicy.WalkApproachSpeed;
        private const byte AimingPoseFlag = 1 << 0;
        private const byte CrouchedPoseFlag = 1 << 1;
        private const byte StancePoseMask = 3 << 2;
        private const int StancePoseShift = 2;
        private const byte BoardedPoseFlag = 1 << 4;
        // #873: the ladder and rope climb, replicated so every peer plays the pose.
        private const byte ClimbingPoseFlag = 1 << 6;
        // #991: the last free bit of the replicated pose byte. A jumping member is not climbing,
        // and the two jump clips are its own states, so it needs its own bit.
        private const byte JumpingPoseFlag = 1 << 7;
        private const float FacingTurnDegreesPerSecond = BlacksiteStancePolicy.FacingTurnDegreesPerSecond;
        private const float FacingRequestHoldSeconds = 0.5f;
        private const float RigBindRetryInterval = 1f;
        private const int MaximumRigBindAttempts = 5;

        // #316: how far back along the tracer a peer starts its impact-surface probe. Long enough to
        // clear the contact point's own geometry, short enough that it cannot find a different wall.
        private const float HostedImpactProbeBack = 0.4f;
        private const float ClientVelocitySmoothingSeconds = 0.2f;
        private const float AgentSyncMovementSpeed = 0.13f;
        private const float AgentUpdatePositionThreshold = 0.35f;
        private const int ScanNodeLayer = 22;
        private const string MoveSpeedParameter = "MoveSpeed";
        private const string PlanarVelocityXParameter = "PlanarVelocityX";
        private const string PlanarVelocityYParameter = "PlanarVelocityY";
        private const string TurnRateParameter = "TurnRate";
        private const string GroundedParameter = "Grounded";
        private const string StanceParameter = "Stance";
        private const string IdentitySeedParameter = "IdentitySeed";
        private const string AnimationPhaseParameter = "AnimationPhase";
        private const string FireParameter = "Fire";
        private const string HitParameter = "Hit";
        private const string ReloadParameter = "Reload";
        private const string RadioParameter = "Radio";
        private const string SignalParameter = "Signal";
        private const string GrenadeParameter = "Grenade";
        private const string DeadParameter = "Dead";
        private const string MoveXParameter = "MoveX";
        private const string MoveZParameter = "MoveZ";
        private const string SpeedParameter = "Speed";
        private const string AimingParameter = "Aiming";
        private const string CrouchedParameter = "Crouched";
        private const string TurnAngleParameter = "TurnAngle";
        private const string DeathVariantParameter = "DeathVariant";
        private const string ClimbingParameter = "Climbing";

        private static readonly int MoveSpeedHash = Animator.StringToHash(MoveSpeedParameter);
        private static readonly int PlanarVelocityXHash = Animator.StringToHash(PlanarVelocityXParameter);
        private static readonly int PlanarVelocityYHash = Animator.StringToHash(PlanarVelocityYParameter);
        private static readonly int TurnRateHash = Animator.StringToHash(TurnRateParameter);
        private static readonly int GroundedHash = Animator.StringToHash(GroundedParameter);
        private static readonly int StanceHash = Animator.StringToHash(StanceParameter);
        private static readonly int IdentitySeedHash = Animator.StringToHash(IdentitySeedParameter);
        private static readonly int AnimationPhaseHash = Animator.StringToHash(AnimationPhaseParameter);
        private static readonly int FireHash = Animator.StringToHash(FireParameter);
        private static readonly int HitHash = Animator.StringToHash(HitParameter);
        private static readonly int ReloadHash = Animator.StringToHash(ReloadParameter);
        private static readonly int SignalHash = Animator.StringToHash(SignalParameter);
        private static readonly int RadioHash = Animator.StringToHash(RadioParameter);
        private static readonly int GrenadeHash = Animator.StringToHash(GrenadeParameter);
        private static readonly int DeadHash = Animator.StringToHash(DeadParameter);
        private static readonly int MoveXHash = Animator.StringToHash(MoveXParameter);
        private static readonly int MoveZHash = Animator.StringToHash(MoveZParameter);
        private static readonly int SpeedHash = Animator.StringToHash(SpeedParameter);
        private static readonly int AimingHash = Animator.StringToHash(AimingParameter);
        private static readonly int CrouchedHash = Animator.StringToHash(CrouchedParameter);
        private static readonly int TurnAngleHash = Animator.StringToHash(TurnAngleParameter);
        private static readonly int HandsUnavailableHash = Animator.StringToHash("HandsUnavailable");
        private static readonly int DeathVariantHash = Animator.StringToHash(DeathVariantParameter);
        private static readonly int ClimbRateHash = Animator.StringToHash("ClimbRate");
        private static readonly int ClimbingHash = Animator.StringToHash(ClimbingParameter);
        private static readonly int StandingPatrolStateHash = Animator.StringToHash("StandingPatrol");
        private static readonly int StandingAimedStateHash = Animator.StringToHash("StandingAimed");
        private static readonly int CrouchedLocomotionStateHash = Animator.StringToHash("CrouchedLocomotion");
        // #1219 F2: the two full-body fire states whose single clip plants the legs.
        private static readonly int StandingFireStateHash = Animator.StringToHash("StandingFire");
        private static readonly int CrouchFireStateHash = Animator.StringToHash("CrouchFire");
        // #874 Q7: the rest/aimed family the stance owner presents (BlacksiteStancePolicy).
        private static readonly int[] StanceOwnedStateHashes = HashStates(BlacksiteStancePolicy.StanceOwnedStateNames);
        private static readonly int[] RestStanceStateHashes = HashStates(BlacksiteStancePolicy.RestStanceStateNames);
        // #874 (2026-09-25): the rest idle variant has no authored entry; DriveRestIdleVariant enters it.
        private static readonly int RestIdleVariantStateHash = Animator.StringToHash("RestIdleVariant");
        private static readonly int LocomotionStateHash = Animator.StringToHash("Locomotion");
        // #874 S5.2 (#873): the state the traversal presentation asks the bound controller for.
        private static readonly int RifleLadderClimbStateHash = Animator.StringToHash(BlacksiteTraversalPresentation.RifleLadderClimb);
        private static readonly int RifleLadderTopOutStateHash = Animator.StringToHash(BlacksiteTraversalPresentation.RifleLadderTopOut);

        private readonly NetworkVariable<BlacksiteSoldierSpawnState> _spawnState =
            new NetworkVariable<BlacksiteSoldierSpawnState>(
                default,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<BlacksiteWeaponSnapshot> _weaponState =
            new NetworkVariable<BlacksiteWeaponSnapshot>(
                default,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<byte> _poseFlags =
            new NetworkVariable<byte>(
                0,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<byte> _ladderPhase = new NetworkVariable<byte>();
        internal BlacksiteLadderPhase LadderPhase => (BlacksiteLadderPhase)_ladderPhase.Value;

        // The blend normalization and stride clock must use the same role speed on every peer.
        // Urgency is host-only knowledge; clients must not infer it from their local AI state.
        private readonly NetworkVariable<float> _animationRoleSpeed =
            new NetworkVariable<float>(BlacksiteLocomotionSpeedTable.NormalRoleSpeed, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private float AnimationRoleMoveSpeed => _animationRoleSpeed.Value;

        // #874 Q7: the in-place turn being presented, as the controller's normalised TurnAngle
        // (BlacksiteStancePolicy.TurnAngle). The facing error behind it is host-only knowledge,
        // so the host latches the value and every peer writes the same parameter from it.
        private readonly NetworkVariable<float> _turnAngle =
            new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        // Host: the signed yaw error of the last facing request and the frame it was made on.
        private float _facingYawError;
        private int _facingYawFrame = int.MinValue;
        // The stance ApplyStance wrote last, for the change-gated [UAT][Stance] line.
        private BlacksiteStance _stance;
        private bool _hasStance;
        // The one normalised planar speed (BlacksiteLocomotionSpeedTable.Blend), for the gesture gate.
        private float _normalizedMoveSpeed;
        // #874 (2026-09-25): when that speed last fell below StationaryBlend (-1 while moving), so an
        // idle turn waits for the body to settle. 0 until the first sample: a member with no
        // animator never samples and must still be allowed to turn.
        private float _stationarySince;
        // #874 (2026-09-25): how long the member has stood still in StandingPatrol (-1 = not), peer-local.
        private float _idleVariantStillSince = -1f;
        // The controller the rest-stance states were resolved on, and whether it carries them all.
        private RuntimeAnimatorController _restStanceController;
        private bool _restStanceAuthored;
        // One warning per controller asset per session, however many members bind it.
        private static RuntimeAnimatorController _restStanceReportedController;
        // #1219 E4: where the bound rifle controller's StandingPatrol tree carries the patrol stroll,
        // probed with the rest-stance states (BlacksiteStrollCapabilityProbe), for _restStanceController.
        private BlacksiteStrollCapability _strollCapability = BlacksiteStrollCapability.None;
        private float _strollRawMoveX, _strollRawMoveZ;

        // #315: where the soldier is aiming, so every peer pitches the torso and turns the head
        // toward the same point. Published at most AimPointSyncInterval while the aiming flag is
        // set; zero means "no point" and the aim pose stays level.
        private readonly NetworkVariable<Vector3> _aimPoint =
            new NetworkVariable<Vector3>(
                Vector3.zero,
                NetworkVariableReadPermission.Everyone,
                NetworkVariableWritePermission.Server);
        private const float AimPointSyncInterval = 0.1f;
        private const float AimPointSyncDistance = 0.05f;
        private float _nextAimPointSyncTime;
        private BlacksiteSoldierAimPose _aimPose;
        private bool _strideCalibrated;

        private bool _hasLastGoodNavPosition => Navigator.HasLastGoodPosition;
        private Vector3 _lastGoodNavPosition => Navigator.LastGoodPosition;
        private BlacksiteNavigator _navigator;
        internal BlacksiteNavigator Navigator => _navigator ?? (_navigator = new BlacksiteNavigator(this));
        // #1017 S3: the every-frame decision core. Lazy for the same reason the navigator is - it is
        // host-side state a client never constructs - and every path that only wants to CLEAR it
        // uses `_brain?.` so a despawn never builds one.
        private BlacksiteSoldierBrain _brain;
        internal BlacksiteSoldierBrain Brain => _brain ?? (_brain = new BlacksiteSoldierBrain(this));
        private NavMeshAgent _agent => _navigator?.Agent;
        private Animator _animator;
        private AudioSource _audioSource;
        private AudioSource _voiceSource;
        private AudioSource _footstepSource;
        private AudioLowPassFilter _footstepLowPass;
        private BlacksiteSoldierRigBinding _rigBinding;
        private BlacksiteInteractionCoordinator _interactionCoordinator => _navigator?.Interactions;
        private BlacksiteDriveCarrier _driveCarrier;
        private BlacksiteCombatController _combatController;
        private BlacksiteSquadDirector _squadDirector;
        private BlacksiteSoldierWeaponVisual _weaponVisual;
        private float _nextHostedWeaponAttachTime;
        private Light _muzzleLight;
        private Light _helmetLight;
        // #813: the prefab's authored intensity is the full-power reference the squad rule scales.
        private Light _helmetLightBaseSource;
        private float _helmetLightBaseIntensity;
        // #1305 F1: the prefab's authored helmet shadow mode, restored when the shadow A/B toggle returns on.
        private LightShadows _helmetLightBaseShadows;
        private bool _helmetLightShadowsForcedOff;
        private Renderer _muzzleFlashRenderer;
        private Transform _muzzleAnchor;
        private Transform _headAnchor;
        private Transform _driveCarryAnchor;
        private Transform _magazineVisual;
        private ScanNodeProperties _scanNode;
        private BlacksiteSoldierRole _role = BlacksiteSoldierRole.SquadMember;
        private BlacksiteSoldierTacticalRole _tacticalRole = BlacksiteSoldierTacticalRole.Rifleman;
        private Vector3 _shipPosition;
        private Vector3 _outsideMainEntrance;
        private Vector3 _insideMainEntrance;
        private Vector3 _outsideFireExit;
        private Vector3 _lastPosition;
        private int _index;
        private float _nextFootstepTime;
        private float _nextRoleCueTime;
        private int _entranceGeneration;
        private int _chosenEntranceId;
        private float _entranceSettleUntil;
        private bool _entranceSettleLogPending;
        private float _muzzleLightOffTime;
        private float _muzzleVisualOffTime;
        private float _lastFootstepCycle = -1f;
        private float _lastFootstepAudioAt;
        private float _nextHitReactionAt;
        private int _lastFootstepClipIndex = -1;
        private int _lastGearClipIndex = -1;
        private bool _initialized;
        private bool _sceneComponentsDisabled;
        private bool _runtimeComponentsReady;
        private bool _attachmentsResolved;
        private bool _rigFailureLogged;
        private int _rigBindFailureCount;
        private bool _animationLogged;
        private bool _animatorKickstarted;
        private bool _footstepEventsEnabled;
        private bool _nextLegacyStepIsLeft = true;
        private bool _serverCrouched;
        private bool _extractingToShip;
        private bool _boardedShip;
        // #1219 A3: host-only. Why the director refused this member's boarding at the link start or
        // at his completed board crossing (BlacksiteSquadDirector.NotifyMemberBoarded's term), and
        // the last reason logged.
        private string _boardingRefusal, _boardingRefusalReported;
        private float _nextBoardingRefusalLog;
        // #1219 A3, #1293 R3: the decision the refusal was last written in; it counts for one more.
        private int _boardingRefusalDecision;
        /// <summary>
        /// #1219 A3/C2: the refusal of a member standing at the passage link start (or on the deck
        /// after a refused board crossing); null while walking, boarded, or not extracting. The
        /// boarding hold names it as <c>refused:&lt;r&gt;</c>; every term is a wait, and the motion
        /// monitor does not read it. It counts only while UpdateExtractionRoute keeps renewing it:
        /// when the brain picks a behaviour that does not run the squad order (Hazard,
        /// IdleWithGoal), nothing clears it or _extractingToShip, so without the
        /// expiry the old refusal would be named at that behaviour's goal.
        /// </summary>
        internal string BoardingRefusal => _extractingToShip && !_boardedShip
            && _brain != null && _brain.Decisions - _boardingRefusalDecision <= 1
            ? _boardingRefusal : null;
        private bool _boardedVisualsApplied;
        private bool _deployCuePlayed;
        private bool _deadHandled;
        private bool _deadStateApplied;
        private bool _networkStateSubscribed;
        private bool _weaponStateSubscribed;
        private bool _poseFlagsSubscribed;
        private bool _teardownFailureLogged;
        private int _playerDamageSerial;
        private int _lastAppliedPlayerDamageSerial;
        private float _currentMoveSpeed;
        private float _lastAnimationSampleTime;
        private float _lastAnimationYaw;
        private Vector3 _smoothedPlanarVelocity;
        private Vector3 _facingTarget;
        private bool _hasFacingTarget;
        private float _facingTargetExpiry;
        // #874 (2026-09-25): whether the current request's hold has been extended for a settle or a
        // rest-rate turn. Once per request: extending on every frame (FacingHoldFor never returns
        // less than its margin) would renew a finished look forever and stall the idle sweep.
        private bool _facingHoldExtended;
        private bool _motionHeld => Navigator.MotionHeld;
        private float _nextRigBindAttemptTime;
        private bool _rigContractFailedHard;
        private readonly Dictionary<int, AnimatorControllerParameterType> _animatorParameters =
            new Dictionary<int, AnimatorControllerParameterType>();
        private RuntimeAnimatorController _cachedParameterController;
        private bool _parameterCacheValid;
        // #874 S5.2 (#873): a state test, cached per controller the same way the parameter layout is.
        // Animator.HasState allocates nothing but walks the controller, and the traversal writers ask
        // it every frame of a climb.
        private readonly Dictionary<int, bool> _animatorStates = new Dictionary<int, bool>();
        private bool _ladderPresentationReported;
        private float _crouchEnteredAt;
        private float _crouchFastMoveStartedAt = float.NegativeInfinity;
        private float _crouchReentryAllowedAt;
        private float _movementAcceleration = 18f;
        private float _animationPhaseOffset;
        private int _animationSeed;
        private ushort _lastAppliedReactionSequence;
        // The clock the player-hearing branch gates on: a member that has just reacted to a
        // stimulus does not immediately react to another. Its ONE reader is PerceiveServer's hearing.
        private float _lastStimulusTime = float.NegativeInfinity;
        // #1233 S8: the squad's own noise is not a stimulus. A soldier's footsteps (12 m, loudness
        // 0.5) and gunfire (24-32 m) go out through RoundManager.PlayAudibleNoise so every vanilla
        // monster hears them, and that call dispatches DetectNoise synchronously to every squadmate
        // in range. The emitter names itself for the length of the call and DetectNoise drops the
        // noise at the door: no reaction, no replicated stamp, no director relay. (#874 S11 U2 had
        // kept the reaction on a second clock; the 27 September log carried 1,880 of those
        // reactions, one per member every 1.5 s, each a NetworkVariable write and a squad relay.)
        // Exact, allocation-free and host-side only, like every emitter.
        private static BlacksiteSoldierAI _squadNoiseInFlight;
        internal readonly struct SquadNoiseScope : IDisposable
        {
            private readonly BlacksiteSoldierAI _previous;
            internal SquadNoiseScope(BlacksiteSoldierAI source)
            { _previous = _squadNoiseInFlight; _squadNoiseInFlight = source; }
            public void Dispose() => _squadNoiseInFlight = _previous;
        }
        internal static SquadNoiseScope AnnounceSquadNoise(BlacksiteSoldierAI source) => new SquadNoiseScope(source);
        private BlacksiteSoldierReactionKind _lastStimulusKind;
        private float _burstCadenceCommittedUntil = float.NegativeInfinity;
        private bool _burstCadenceCommitment;
        private readonly NavMeshPath _navPath = new NavMeshPath();
        private bool _diagnosticOrderInitialized;
        private BlacksiteSquadOrderKind _lastDiagnosticOrderKind;
        private BlacksiteFormationKind _lastDiagnosticFormation;
        private bool _lastDiagnosticInteractionOperator;

        private static AudioClip _shotClip;
        // #929: the weapon source's facility routing, applied once per bound source and ticked
        // with the rest of the per-frame presentation.
        private ContractAudioPropagation _weaponPropagation;
        private AudioSource _weaponAudioRouted;
        private static FieldInfo _voicePlayerStateField;
        private static bool _voicePlayerStateFieldResolved;
        private static Type _voiceSpeakingStateType;
        private static Func<object, bool> _voiceSpeakingAccessor;
        private static PropertyInfo _voiceSpeakingProperty;
        private static FieldInfo _voiceSpeakingField;
        private static readonly string[] VoiceSpeakingMemberNames =
        {
            "IsSpeaking",
            "isSpeaking",
            "Speaking",
            "speaking"
        };
        private static readonly Dictionary<ulong, Vector3> LastPlayerPositions = new Dictionary<ulong, Vector3>();
        private static readonly HashSet<int> FootstepEventClipIds = new HashSet<int>();
        private static readonly HashSet<int> CombatEventClipIds = new HashSet<int>();

        internal bool InitializeServer(
            BlacksiteRetrievalMissionController mission,
            Vector3 shipPosition,
            Vector3 outsideMainEntrance,
            Vector3 insideMainEntrance,
            Vector3 outsideFireExit,
            int index,
            int formationSlot,
            Vector3 formationOffset,
            Vector3[] interiorClearRoute = null)
        {
            if (!IsServerAuthority()
                || NetworkObject == null
                || !NetworkObject.IsSpawned
                || mission == null
                || mission.NetworkObject == null
                || !mission.NetworkObject.IsSpawned
                || !mission.IsInitialized
                || mission.IsTerminal
                || !BlacksiteRetrievalRules.IsContractEligible(mission.Contract))
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Blacksite] Soldier initialization rejected without an eligible active mission and native network spawn.");
                return false;
            }

            _role = BlacksiteSoldierRole.SquadMember;

            _shipPosition = shipPosition;
            _outsideMainEntrance = outsideMainEntrance;
            _insideMainEntrance = insideMainEntrance;
            _outsideFireExit = outsideFireExit;
            _index = index;
            _squadDirector = mission.SquadDirector;
            _tacticalRole = _squadDirector.GetRoleForMember(index);
            _animationSeed = BlacksiteRetrievalTuning.CreateAnimationSeed(mission.MissionNetworkId, index);
            RefreshDeathSfx();                                                            // #1066 U8
            _movementAcceleration = BlacksiteRetrievalTuning.GetMovementAcceleration(_animationSeed);
            _animationPhaseOffset = BlacksiteRetrievalTuning.GetAnimationPhase(_animationSeed);
            _deadHandled = false;
            _deadStateApplied = false;
            _serverCrouched = false;
            _crouchEnteredAt = 0f;
            _crouchFastMoveStartedAt = float.NegativeInfinity;
            _crouchReentryAllowedAt = 0f;
            if (_poseFlags.Value != 0)
                _poseFlags.Value = 0;
            // #874 Q7: no in-place turn and no stance survive into a new deployment.
            if (_turnAngle.Value != 0f) _turnAngle.Value = 0f;
            _facingYawFrame = int.MinValue;
            _hasStance = false;
            _hasPatrolLook = false; // #874 (2026-09-25): the look belongs to one arrival.
            if (_ladderPhase.Value != 0) _ladderPhase.Value = 0;
            if (_traversalKind.Value != 0) _traversalKind.Value = 0;
            if (_ropeDirection.Value != 0) _ropeDirection.Value = 0;
            // #874 S5.2: the presentation line is one per member per round.
            _ladderPresentationReported = false;
            _jumpPresentationReported = false;
            // Initialize the per-deployment presentation state beside the replicated traversal.
            RestoreGuardTraversalController();
            _guardTraversalMissingKinds = 0;
            if (_crossingPlaybackSpeed.Value != 1f) _crossingPlaybackSpeed.Value = 1f;
            isEnemyDead = false;
            isOutside = true;
            // #874 S4: a redeployed body is the agent's again, and the deployment stagger is one
            // dwell, taken once at spawn.
            Navigator.EndSeat(transform.position);
            Navigator.Dwell(GetDeploymentStaggerSeconds(), "deploy");
            _nextRoleCueTime = Time.time + UnityEngine.Random.Range(1f, 3f);

            _spawnState.Value = BlacksiteSoldierSpawnState.Create(
                mission.MissionNetworkId,
                index,
                _role,
                _tacticalRole,
                formationSlot,
                formationOffset,
                _animationSeed,
                new NetworkObjectReference(mission.NetworkObject),
                SoldierHealth,
                BlacksiteDeploymentPolicy.Equipment(
                    BlacksiteDeploymentPolicy.Duty(index, mission.MemberCount),
                    BlacksiteUpgradesWeaponBridge.IsAvailable()));
            ApplySpawnState(_spawnState.Value, applyReaction: false);

            EnsureRuntimeComponents();
            EnsureCombatController();
            _combatController.ConfigureServerLoadout();
            SnapToNavMesh(transform.position, 5f);
            mission.SquadDirector.RegisterMember(this);
            // #1293 R3: a (re)deployed body inherits no decision or intent and owes its first decision.
            _brain?.Reset();
            _intent = default;
            _passCombatTarget = false;
            RequestDecision(BlacksiteDecisionCause.Spawned);
            PlayDeployCue();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Soldier {_index} initialized squad={mission.MissionNetworkId}, role={_tacticalRole}, formation={formationSlot}, seed={_animationSeed}, health={enemyHP}, start={transform.position}, ship={_shipPosition}, outsideMain={_outsideMainEntrance}, insideMain={_insideMainEntrance}.");
            return true;
        }

        internal ulong SquadId => _spawnState.Value.SquadId;
        internal int MemberIndex => _spawnState.Value.MemberIndex;
        internal int FormationSlot => _spawnState.Value.FormationSlot;
        internal Vector3 FormationOffset => _spawnState.Value.FormationOffset;
        internal int AnimationSeed => _spawnState.Value.AnimationSeed;
        internal int ReplicatedHealth => _spawnState.Value.Health;
        internal bool ReplicatedDead => _spawnState.Value.IsDead;
        internal bool ReplicatedStunned => _spawnState.Value.IsStunned;
        internal BlacksiteWeaponSnapshot WeaponSnapshot => _weaponState.Value;
        internal BlacksiteSoldierRole Role => _role;
        internal BlacksiteSoldierTacticalRole TacticalRole => _tacticalRole;
        internal ulong MissionNetworkId => _spawnState.Value.IsConfigured
            ? _spawnState.Value.MissionReference.NetworkObjectId
            : 0UL;
        internal bool HasReplicatedIdentity => _spawnState.Value.IsConfigured;
        internal BlacksiteSoldierReactionKind LatestReaction => _spawnState.Value.Reaction;
        internal bool HasBoardedShip => isEnemyDead || _boardedShip;
        // #874 S2.4: resolved only from facts every peer has - the replicated spawn state, the
        // replicated pose flags, and the capture/consumption the vanilla-monster RPC applies on
        // every peer. Precedence: consumed, dead, captured, boarded, traversing, active.
        internal BlacksiteLifecycleState EffectiveLifecycle => BlacksiteLifecycle.Resolve(
            _monsterConsumed,
            isEnemyDead || ReplicatedDead,
            _monsterCaptured,
            (_poseFlags.Value & BoardedPoseFlag) != 0 || _boardedVisualsApplied,
            (_poseFlags.Value & ClimbingPoseFlag) != 0);
        internal bool AllowsCombatLifecycle => BlacksiteLifecycle.AllowsCombat(EffectiveLifecycle);
        internal bool ShouldUseInteriorInteractionHelpers => _initialized && !_boardedShip && (!IsShipGuard || !isOutside);
        internal bool IsWorldInteractionInterrupted => !IsServerAuthority()
            || isEnemyDead
            || ReplicatedDead
            || ReplicatedStunned
            || stunNormalizedTimer > 0f;
        // #874 S4: the `combat` stall owner - combat intent. A pure read. #1271 S3a: a Coil-Head
        // watcher walks its duty, so the watch alone no longer exempts it from the motion monitor.
        internal bool InCombat => _combatController != null && _combatController.HasCombatIntent;
        internal bool IsSeated => Navigator.BodyWriter == BlacksiteBodyWriter.Seated;
        internal bool IsCombatPositionHoldActive => Navigator.InDwell && InCombat;
        // #1293 R1: the contact this member engages is still a row of the squad table, or something
        // hit it inside the immediate-attacker window.
        internal bool IsCombatEngaged => _combatController != null && _combatController.HoldsTargetRow;
        internal bool IsRifleGuard => _spawnState.Value.Equipment != 0 ? _spawnState.Value.Equipment == 2 : TryGetMission(out var mission)
            && BlacksiteDeploymentPolicy.Duty(MemberIndex, mission.MemberCount) == BlacksiteDeploymentDuty.ShipGuard;
        internal bool IsShipGuard => IsRifleGuard && TryGetMission(out var mission) && !mission.Objectives.GuardCalledIn(NetworkObjectId);
        /// #1233 S4 (rule 1): aboard the mission's ship now, by live body position; the ship is the one judge.
        internal bool IsAboardShip => TryGetMission(out var mission) && mission.Objectives.Ship != null && mission.Objectives.Ship.Aboard(transform.position);
        internal bool HasEnteredFacility { get; private set; }
        internal bool IsRequestingWorldInteraction(BlacksiteInteractionTask task) => _interactionCoordinator != null && _interactionCoordinator.IsRequesting(task);
        internal bool IsObjectiveCombatActive => _combatController != null && _combatController.HasCombatIntent;
        internal bool IsFacilityTraversalActive => _interactionCoordinator != null && _interactionCoordinator.HasActiveTask
            && (_interactionCoordinator.ActiveKind == BlacksiteInteractionKind.Elevator || _interactionCoordinator.IsEntranceTraversalActive);
        internal bool IsObjectiveCarrier => TryGetMission(out var mission) && mission.Objectives.IsCarrier(NetworkObjectId);
        internal bool HasOccupiedCargoHands => TryGetMission(out var mission) && mission.Objectives.OccupiesHands(NetworkObjectId);
        internal bool IsPushingPayload => TryGetMission(out var mission) && mission.Objectives.IsPushing(NetworkObjectId);
        // #965 S14 U3: the witness escort's mirror of IsPushingPayload, one line above.
        //
        // Stage 14 U2 owns BlacksiteObjectiveManifestController.IsEscortingWitness(ulong) and this
        // property becomes the same one-line delegation the cart uses the moment U2 lands. Until
        // then the resolution below stands in for it and needs no objective KIND: a Carried record
        // whose carrier is this soldier and whose object is a witness this soldier holds is an
        // escort by every definition either unit uses.
        internal bool IsEscortingWitness => TryGetEscortedWitness(out _);
        /// <summary>
        /// #990 S16 D2: this member holds the Witness RESERVATION and the squad can currently see
        /// the target. The board answers both halves - it owns the record and it owns the knowledge
        /// model - so this is the one-line delegation IsPushingPayload and IsEscortingWitness are.
        /// </summary>
        internal bool IsPursuingWitnessInSight
            => TryGetMission(out var mission) && mission.Objectives != null
               && mission.Objectives.IsPursuingWitnessInSight(NetworkObjectId);
        internal bool HasActiveWorldInteraction => _interactionCoordinator != null
            && _interactionCoordinator.HasActiveTask;
        // #1271 S8: IsVoiceCapable is everything but the boarded test, for the lines spoken aboard
        // (BlacksiteVoiceEventRules.IsSpokenAboard); every other line still wants an unboarded member.
        internal bool IsVoiceCapable => _spawnState.Value.IsConfigured
            && !isEnemyDead
            && !ReplicatedDead
            && !ReplicatedStunned
            && !HasActiveWorldInteraction
            && WeaponSnapshot.State != BlacksiteWeaponState.FiringBurst
            && WeaponSnapshot.State != BlacksiteWeaponState.GrenadeCommitted;
        internal bool IsEligibleForSquadVoice => IsVoiceCapable && !_boardedShip;
        internal bool IsServerAuthorityForSquad => IsServerAuthority();
        internal Vector3 CombatMuzzlePosition
        {
            get
            {
                ResolveAttachmentAnchors();
                return _muzzleAnchor != null
                    ? _muzzleAnchor.position
                    : GetEyePosition() + transform.forward * 0.45f;
            }
        }
        internal Transform DriveCarryAnchor
        {
            get
            {
                ResolveAttachmentAnchors();
                return _driveCarryAnchor != null ? _driveCarryAnchor : transform;
            }
        }

        internal void ApplyRigBinding(BlacksiteSoldierRigBinding binding)
        {
            if (binding == null || !binding.IsValid)
                return;

            _strollRawMoveX = _strollRawMoveZ = 0f;
            _restStanceController = null;
            _strollCapability = BlacksiteStrollCapability.None;
            _rigBinding = binding;
            _animator = binding.Animator;
            InvalidateAnimatorParameterCache();
            _audioSource = binding.WeaponSource;
            ConfigureWeaponAudio();
            _voiceSource = binding.VoiceSource;
            _footstepSource = binding.FootstepSource;
            _headAnchor = binding.Head;
            _muzzleAnchor = binding.Muzzle;
            _driveCarryAnchor = binding.CarryAnchor;
            _magazineVisual = binding.Magazine;
            _helmetLight = binding.HelmetLight;
            if (_helmetLight != null && _helmetLightBaseSource != _helmetLight)
            {
                // Capture once per light instance, before any power scaling has touched it; a
                // re-bind to the same light must not read back an already halved value.
                _helmetLightBaseSource = _helmetLight;
                _helmetLightBaseIntensity = _helmetLight.intensity;
                _helmetLightBaseShadows = _helmetLight.shadows;
                _helmetLightShadowsForcedOff = false;
                _helmetLight.range *= BlacksiteHelmetLightPolicy.RangeMultiplier;
                _helmetLight.spotAngle = Mathf.Min(100f, _helmetLight.spotAngle * 1.2f);
            }
            _muzzleLight = binding.MuzzleLight;
            _muzzleFlashRenderer = binding.MuzzleFlash;
            _footstepLowPass = _footstepSource != null
                ? _footstepSource.GetComponent<AudioLowPassFilter>()
                : null;
            creatureAnimator = _animator;
            creatureSFX = _audioSource;
            creatureVoice = _voiceSource;
            _attachmentsResolved = true;

            // #316: the hosted MX16A4 is built on every peer from local data the moment the rig is
            // known, and it re-seats the muzzle anchor onto the model's real barrel - so this has to
            // run before the muzzle light and flash renderer are parented to that anchor.
            //
            // A corpse never gets one. HandleDeadState destroys the model exactly once (it is latched
            // behind _deadStateApplied), so a rig that binds late - a late-joiner, or a retry that
            // lands after death - would arm a body that nothing will ever disarm.
            TryAttachHostedWeapon();
        }

        /// <summary>
        /// #316: give the hosted MX16A4 a chance to attach. Called on every rig bind, on every
        /// runtime-component pass, and from <see cref="Update"/> until the visual settles, because
        /// the first attempt can legitimately miss - the bridge throttles its type lookup to one
        /// every couple of seconds, and a whole squad binds in the same frame. The visual latches
        /// itself once the answer is definitive, so this stops costing anything the moment the weapon
        /// is on or the pack is established as absent.
        ///
        /// #1233 S8: nothing is built before the replicated equipment says what this member
        /// carries. The host's rig binds inside SpawnEnemyGameObject, one call before
        /// InitializeServer writes the state, and every baton raider (five of six) used to build a
        /// hosted rifle there that its first Update then destroyed; a rifle guard now gets its rifle
        /// from <see cref="BindEquipment"/> on the same frame instead.
        /// </summary>
        private void TryAttachHostedWeapon()
        {
            if (_rigBinding == null || !_rigBinding.IsValid)
                return;
            if (isEnemyDead || _deadHandled || !_spawnState.Value.IsConfigured || UsesBaton)
                return;
            if (_weaponVisual != null && _weaponVisual.HasModel)
                return;
            if (Time.time < _nextHostedWeaponAttachTime)
                return;
            _nextHostedWeaponAttachTime = Time.time + RigBindRetryInterval;

            _weaponVisual = _weaponVisual ?? new BlacksiteSoldierWeaponVisual(this);
            _weaponVisual.TryAttach(_rigBinding);
        }

        /// <summary>
        /// #1016 S2: this member's mission resource broker, or null off the server or before the
        /// mission exists. Every wait site that has a soldier reaches the one claim ledger through
        /// here rather than threading a broker through the navigator's constructors.
        /// </summary>
        internal BlacksiteResourceBroker ResourceBroker
            => TryGetMission(out BlacksiteRetrievalMissionController mission) && mission.IsServer
                ? mission.Blackboard.Broker
                : null;

        internal bool TryGetMission(out BlacksiteRetrievalMissionController mission) => TryGetMission(out mission, false);

        // #872: ignoreTransportLoss is used by the stranded re-attach check, which must see the
        // mission while the member is still detached from it, and (#1293 R4) by the elevator link,
        // whose one lane lives in that mission's broker for survivors and squad alike.
        internal bool TryGetMission(out BlacksiteRetrievalMissionController mission, bool ignoreTransportLoss)
        {
            mission = null;
            if ((_transportLost && !ignoreTransportLoss) || NetworkManager.Singleton == null
                || NetworkObject == null
                || !NetworkObject.IsSpawned
                || !_spawnState.Value.IsConfigured
                || !_spawnState.Value.MissionReference.TryGet(out NetworkObject missionObject)
                || missionObject == null
                || !missionObject.IsSpawned)
            {
                return false;
            }

            mission = missionObject.GetComponent<BlacksiteRetrievalMissionController>();
            return mission != null;
        }

        // #965 S14 U3. Resolved once per frame: custody is written on the server tick, so the
        // answer cannot change between two asks inside one frame.
        private Y4NGZCompany.Contracts.Whistleblower.WhistleblowerEnemyAI _escortedWitness;
        private int _escortedWitnessFrame = -1;

        /// <summary>
        /// #965 S14 U3. The witness this soldier holds, resolved from U1's replicated escort id.
        ///
        /// The brief's stand-in for U2's <c>Objectives.IsEscortingWitness(ulong)</c> was a scan of
        /// the manifest's Carried records. This resolves the same fact from the other end, because
        /// the record is the board's bookkeeping of the custody id and the custody id is what the
        /// board would be reconciled against: <c>IsEscortedBy</c> is true only while the witness's
        /// own server-written <c>_custodyEscort</c> names this soldier. It also needs neither a
        /// spawned NetworkObject nor the SpawnManager, so the native fixture can drive the real
        /// resolution instead of a patched boolean. At integration the property above becomes U2's
        /// one-line manifest delegation and this stays the resolver.
        /// </summary>
        private bool TryGetEscortedWitness(out Y4NGZCompany.Contracts.Whistleblower.WhistleblowerEnemyAI witness)
        {
            if (_escortedWitnessFrame == Time.frameCount)
            { witness = _escortedWitness; return witness != null; }

            _escortedWitnessFrame = Time.frameCount;
            _escortedWitness = null;
            witness = null;
            if (!BlacksiteCustodySettings.EscortEnabled || isEnemyDead)
                return false;

            // The same live-enemy set BlacksiteShipController.BoardingSlot walks; one pass, no
            // allocation, and it ends on the first witness because MaxCount is 1.
            List<EnemyAI> enemies = RoundManager.Instance != null ? RoundManager.Instance.SpawnedEnemies : null;
            if (enemies == null)
                return false;
            for (int i = 0; i < enemies.Count; i++)
            {
                // #1293 R2: the extraction seat ends custody, so a seated witness names nobody.
                if (!(enemies[i] is Y4NGZCompany.Contracts.Whistleblower.WhistleblowerEnemyAI held)
                    || !held.IsEscortedBy(this))
                {
                    continue;
                }

                _escortedWitness = held;
                witness = held;
                return true;
            }

            return false;
        }

        internal bool TryGetSquadDirector(out BlacksiteSquadDirector director)
        {
            director = _squadDirector;
            if (_transportLost || !TryGetMission(out var liveMission) || liveMission.IsTerminal)
            { _squadDirector = null; director = null; return false; }
            if (director != null)
                return true;
            if (!TryGetMission(out BlacksiteRetrievalMissionController mission))
                return false;
            director = mission.SquadDirector;
            _squadDirector = director;
            return director != null;
        }

        internal void CancelExtractionToRecoverDrive()
        {
            _extractingToShip = false;
            _boardedShip = false;
            if (IsServerAuthority()) _poseFlags.Value = (byte)(_poseFlags.Value & ~BoardedPoseFlag);
            ApplyBoardedVisualState(false);
            RequestImmediateDestinationRefresh();
        }

        internal void DropCarriedDriveServer(string reason)
        {
            _driveCarrier = _driveCarrier ?? new BlacksiteDriveCarrier(this);
            if (TryGetMission(out var mission)) mission.Objectives.DropCarrier(this, reason);
            _driveCarrier.DropServer(reason);
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            BlacksiteVanillaMonsters.Register(this);
            if (!_networkStateSubscribed)
            {
                _spawnState.OnValueChanged += OnSpawnStateChanged;
                _networkStateSubscribed = true;
            }
            if (!_weaponStateSubscribed)
            {
                _weaponState.OnValueChanged += OnWeaponStateChanged;
                _weaponStateSubscribed = true;
            }
            if (!_poseFlagsSubscribed)
            {
                _poseFlags.OnValueChanged += OnPoseFlagsChanged;
                _poseFlagsSubscribed = true;
            }

            EnsureBehaviourStates();
            EnsureRuntimeComponents();
            EnsureCombatController();
            _lastAppliedReactionSequence = _spawnState.Value.ReactionSequence;
            ApplySpawnState(_spawnState.Value, applyReaction: false);
            ApplyWeaponStateVisual(_weaponState.Value);
            ApplyLifecyclePresentation("network spawn");
        }

        public override void EnableEnemyMesh(bool enable, bool overrideDoNotSet = false, bool tamperWithMeshes = false)
        {
            // Dynamic rifle visuals can be destroyed after vanilla cached their renderers.
            // #951 S11H U3 (f): the same result with one pass and no allocation in the common case.
            // Vanilla culling calls this on every actor as it enters and leaves view, and the two
            // Where(...).ToArray() calls allocated an enumerator, a list and an array each time even
            // when nothing had been destroyed, which is almost always.
            skinnedMeshRenderers = CompactRenderers(skinnedMeshRenderers);
            meshRenderers = CompactRenderers(meshRenderers);
            // #874 S2.4: vanilla culling calls this too, and it must not reveal a boarded or
            // consumed actor between the applier's frames.
            if (enable && BlacksiteLifecycle.Hides(EffectiveLifecycle))
                enable = false;
            base.EnableEnemyMesh(enable, overrideDoNotSet, tamperWithMeshes);
        }

        /// <summary>
        /// #951 S11H U3 (f): the live entries of <paramref name="renderers"/>, keeping the array
        /// itself when none has been destroyed. The null test is made through UnityEngine.Object on
        /// purpose: a type parameter does not pick up Unity's overloaded ==, so a destroyed renderer
        /// would read as live and the guard this replaces would be lost.
        /// </summary>
        private static T[] CompactRenderers<T>(T[] renderers) where T : UnityEngine.Object
        {
            if (renderers == null)
                return Array.Empty<T>();
            int live = 0;
            for (int i = 0; i < renderers.Length; i++)
                if ((UnityEngine.Object)renderers[i] != null)
                    live++;
            if (live == renderers.Length)
                return renderers;
            var compacted = new T[live];
            int next = 0;
            for (int i = 0; i < renderers.Length; i++)
                if ((UnityEngine.Object)renderers[i] != null)
                    compacted[next++] = renderers[i];
            return compacted;
        }
        public override void OnNetworkDespawn()
        {
            BlacksiteVanillaMonsters.Unregister(this);
            if (IsServerAuthority())
            {
                DropCarriedDriveServer("carrier network despawn");
                if (TryGetMission(out BlacksiteRetrievalMissionController despawnMission))
                    despawnMission.DecisionQueue?.Remove(NetworkObjectId);
                if (TryGetSquadDirector(out BlacksiteSquadDirector director))
                    director.UnregisterMember(this);
            }
            if (_networkStateSubscribed)
            {
                _spawnState.OnValueChanged -= OnSpawnStateChanged;
                _networkStateSubscribed = false;
            }
            if (_weaponStateSubscribed)
            {
                _weaponState.OnValueChanged -= OnWeaponStateChanged;
                _weaponStateSubscribed = false;
            }
            if (_poseFlagsSubscribed)
            {
                _poseFlags.OnValueChanged -= OnPoseFlagsChanged;
                _poseFlagsSubscribed = false;
            }
            _navigator?.Dispose();
            _navigator = null;
            _behaviourSelector.Reset();
            // #1017 S3: a corpse holds no decision. `?.` so a despawn never constructs a brain.
            _brain?.Reset();
            _combatController?.Reset();
            _strollRawMoveX = _strollRawMoveZ = 0f;
            _restStanceController = null;
            _strollCapability = BlacksiteStrollCapability.None;
            _combatController = null;
            base.OnNetworkDespawn();
        }

        public override void Start()
        {
            EnsureBehaviourStates();
            base.Start();
            EnsureRuntimeComponents();
            EnsureCombatController();
            ApplySpawnState(_spawnState.Value, applyReaction: false);
            ventAnimationFinished = true;
            _lastPosition = transform.position;
            _lastAnimationSampleTime = Time.time;
            _lastAnimationYaw = transform.eulerAngles.y;
            if (!_initialized)
                _shipPosition = transform.position;
        }

        private void OnSpawnStateChanged(BlacksiteSoldierSpawnState previous, BlacksiteSoldierSpawnState current)
        {
            ApplySpawnState(current, current.ReactionSequence != previous.ReactionSequence);
        }

        private void OnWeaponStateChanged(BlacksiteWeaponSnapshot previous, BlacksiteWeaponSnapshot current)
        {
            // Live transitions already carry reliable presentation RPCs. Replicated
            // state is applied here only where it must immediately suppress effects;
            // OnNetworkSpawn applies the full snapshot for late joiners.
            if (previous.State != current.State && current.State == BlacksiteWeaponState.Disabled)
                ApplyWeaponStateVisual(current);
        }

        private void OnPoseFlagsChanged(byte previous, byte current)
        {
            ApplyBoardedVisualState((current & BoardedPoseFlag) != 0);
        }

        // The local immediate apply the boarding completion, the pose-flag subscription and the
        // un-board path use so the host frame is never one behind its own decision. Every state
        // decision itself belongs to ApplyLifecyclePresentation.
        private void ApplyBoardedVisualState(bool boarded)
        {
            _boardedVisualsApplied = boarded;
            if (IsServerAuthority())
            {
                bool held = boarded && !isEnemyDead;
                Navigator.SetPassengerHold(held);
                if (TryGetMission(out var mission) && mission.Objectives.Ship != null)
                {
                    // #1219 C2: the reserved seat. #1283 S4: the board crossing walks the deck to this same
                    // seat (BlacksiteNavigator.TryBeginShipCrossing), so the write finds the body on it; SeatPassengerServer is idempotent.
                    if (held) mission.Objectives.Ship.SeatPassengerServer(NetworkObjectId, this, mission.Objectives.Ship.BoardingSlot(this));
                    else mission.Objectives.Ship.ReleasePassengerServer(NetworkObjectId);
                }
            }
            ApplyLifecyclePresentation(boarded ? "boarded" : "un-boarded");
        }

        private BlacksiteLifecycleState _lifecycle = BlacksiteLifecycleState.Active;
        private bool _lifecycleApplied;
        private int _lifecycleHierarchy = -1, _lifecycleWalks;
        private const string LifecycleFrameReason = "frame";
        private readonly Dictionary<Renderer, bool> _lifecycleRenderers = new Dictionary<Renderer, bool>();
        private readonly Dictionary<Collider, bool> _lifecycleColliders = new Dictionary<Collider, bool>();

        // #874 S2.4: the single presentation writer. It runs last in the frame's presentation pass,
        // so a late weapon attach, vanilla's own mesh toggle and the equipment writer cannot undo
        // it, and it is idempotent - re-applying the same state changes nothing.
        /// <summary>
        /// #1066 U7: the reason a lifecycle TRANSITION is logged with has to describe the transition,
        /// not whichever of the nine callers happened to observe it. There are nine call sites with
        /// nine different labels and any of them can win the `changed` branch: on a monster kill the
        /// kill path flips the boarded pose flag first, so ApplyBoardedVisualState(false) reached the
        /// applier before the intended "dead" call and 2026-09-15 logged
        /// `from=Captured, to=Dead, reason=un-boarded` for four MouthDog kills - a label that
        /// actively misleads anyone reading the log for why a soldier died.
        ///
        /// The state owner is this object's own flags, so the cause is derived from them rather than
        /// plumbed through every caller. A caller whose label already explains the state it is
        /// reporting keeps that label untouched (the "dead", "consumed" and "monster capture" sites
        /// all do), so this only ever replaces a PRESENTATION label that cannot explain a terminal
        /// state - "boarded", "un-boarded", "frame" and "network spawn".
        /// </summary>
        private string DescribeLifecycleTransition(BlacksiteLifecycleState state, string reason)
        {
            switch (state)
            {
                case BlacksiteLifecycleState.Consumed:
                    return "consumed";
                case BlacksiteLifecycleState.Dead:
                    // A body the monster still holds, or one it had hold of when the kill landed, is
                    // a monster kill; anything else is a plain death and keeps the generic label.
                    return _monsterCaptured ? "monster kill" : "dead";
                case BlacksiteLifecycleState.Captured:
                    return "monster capture";
                default:
                    return reason;
            }
        }

        private void ApplyLifecyclePresentation(string reason)
        {
            BlacksiteLifecycleState state = EffectiveLifecycle;
            bool changed = !_lifecycleApplied || state != _lifecycle;
            if (changed)
            {
                if (_lifecycleApplied)
                    BlacksiteDiagnostics.Log("Lifecycle", $"member={MemberIndex}, from={_lifecycle}, to={state}, reason={DescribeLifecycleTransition(state, reason)}");
                _lifecycle = state;
                _lifecycleApplied = true;
            }

            bool hidden = BlacksiteLifecycle.Hides(state);
            // Walking the hierarchy is the only allocating work here, and a hidden actor is
            // otherwise inert, so it is done on a transition, on an explicit apply, and when the
            // hierarchy itself changed - which is exactly the late attach a rig bind or a hosted
            // weapon makes. An unchanged hidden frame does the direct writes below and nothing else.
            if (hidden)
            {
                if (changed || !string.Equals(reason, LifecycleFrameReason, StringComparison.Ordinal)
                    || transform.hierarchyCount != _lifecycleHierarchy)
                    CaptureAndHide();
            }
            else if (_lifecycleRenderers.Count > 0 || _lifecycleColliders.Count > 0)
                ReleaseLifecycleSnapshot(BlacksiteLifecycle.Restores(state));

            if (!BlacksiteLifecycle.HasLight(state))
            {
                if (_helmetLight != null)
                    _helmetLight.enabled = false;
                if (_muzzleLight != null)
                    _muzzleLight.intensity = 0f;
                if (_muzzleFlashRenderer != null)
                    _muzzleFlashRenderer.enabled = false;
            }
            if (hidden)
            {
                if (_weaponVisual != null && _weaponVisual.Model != null)
                    _weaponVisual.Model.SetActive(false);
                _batonEffects?.Power(false, false);
            }
            if (_batonModel != null)
                _batonModel.SetActive(!hidden && !isEnemyDead && !ReplicatedDead);
            if (_batonHolster != null)
                _batonHolster.SetActive(!hidden);
            if (_rigBinding != null && _rigBinding.HitCapsule != null)
                _rigBinding.HitCapsule.enabled = BlacksiteLifecycle.HasHitPresence(state);
            if (_scanNode != null)
                _scanNode.gameObject.SetActive(!hidden);
            SetBodyCollision(true, state.ToString());
        }

        // The snapshot is the intent each renderer carried when it went out of the world, re-read
        // every hidden frame so a renderer that attaches later is captured with the state its own
        // writer just gave it. Renderers under the weapon root are excluded: the rifle branch owns
        // their baseline and restores it itself, and recording the off state it writes while hidden
        // would overwrite that baseline with the hiding.
        private void CaptureAndHide()
        {
            _lifecycleHierarchy = transform.hierarchyCount;
            _lifecycleWalks++;
            Transform weaponRoot = _rigBinding != null ? _rigBinding.WeaponRoot : null;
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                if (weaponRoot != null && renderer.transform.IsChildOf(weaponRoot)) { renderer.enabled = false; continue; }
                if (!_lifecycleRenderers.ContainsKey(renderer)) _lifecycleRenderers[renderer] = renderer.enabled;
                renderer.enabled = false;
            }
            foreach (Collider collider in GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || BlacksiteBodyCollision.IsBody(collider)) continue;
                if (!_lifecycleColliders.ContainsKey(collider)) _lifecycleColliders[collider] = collider.enabled;
                collider.enabled = false;
            }
        }

        private void ReleaseLifecycleSnapshot(bool restore)
        {
            if (restore)
            {
                foreach (var entry in _lifecycleRenderers)
                    if (entry.Key != null) entry.Key.enabled = entry.Value;
                foreach (var entry in _lifecycleColliders)
                    if (entry.Key != null) entry.Key.enabled = entry.Value;
            }
            _lifecycleRenderers.Clear();
            _lifecycleColliders.Clear();
            _lifecycleHierarchy = -1;
        }

        private CapsuleCollider _bodyCollider;

        // #874 S2.2: the only writer for the solid torso capsule. A boarded, dead, captured or
        // consumed actor has no body presence; every other state does, including the hand-driven
        // ship passage and rappel (a player standing in the landing spot is pushed out by its own
        // CharacterController.Move, never by a position write). #874 S2.4 drives it from the
        // lifecycle applier and reports the resolved state as the reason.
        internal void SetBodyCollision(bool active, string reason)
        {
            if (_bodyCollider == null)
            {
                _bodyCollider = EnemyBodyCollision.Ensure(gameObject, BlacksiteBodyCollision.ObjectName,
                    BlacksiteBodyCollision.Radius, BlacksiteBodyCollision.Height, BlacksiteBodyCollision.CenterY, kinematic: false);
                BlacksiteBodyCollision.AttachMarker(_bodyCollider);
            }
            if (_bodyCollider == null)
                return;

            bool wanted = active && BlacksiteLifecycle.HasBodyPresence(EffectiveLifecycle);
            if (_bodyCollider.enabled == wanted)
                return;

            _bodyCollider.enabled = wanted;
            BlacksiteDiagnostics.Log("BodyCollision", $"member={MemberIndex}, active={wanted}, reason={reason}");
        }

        private void ApplyWeaponStateVisual(BlacksiteWeaponSnapshot state)
        {
            if (UsesBaton || HasOccupiedCargoHands || _climbingPose) return;
            switch (state.State)
            {
                case BlacksiteWeaponState.FiringBurst:
                    TryTrigger(FireHash);
                    break;
                case BlacksiteWeaponState.Reloading:
                    TryTrigger(ReloadHash);
                    break;
                case BlacksiteWeaponState.Disabled:
                    if (_muzzleLight != null)
                        _muzzleLight.intensity = 0f;
                    if (_muzzleFlashRenderer != null)
                        _muzzleFlashRenderer.enabled = false;
                    break;
            }
        }

        private void ApplySpawnState(BlacksiteSoldierSpawnState state, bool applyReaction)
        {
            if (!state.IsConfigured)
                return;

            bool identityChanged = !_initialized
                || _index != state.MemberIndex
                || _animationSeed != state.AnimationSeed;

            _initialized = true;
            _index = state.MemberIndex;
            _role = state.Role;
            _tacticalRole = state.TacticalRole;
            _animationSeed = state.AnimationSeed;
            RefreshDeathSfx();                                                            // #1066 U8
            _movementAcceleration = BlacksiteRetrievalTuning.GetMovementAcceleration(_animationSeed);
            _animationPhaseOffset = BlacksiteRetrievalTuning.GetAnimationPhase(_animationSeed);
            enemyHP = Mathf.Max(0, state.Health);
            gameObject.name = $"BlacksiteRetrieval_{_tacticalRole}_{_index + 1}";

            Navigator.ApplySpawnMovement();

            if (_scanNode != null)
                _scanNode.subText = GetScanSubText();

            TrySetInteger(IdentitySeedHash, _animationSeed & int.MaxValue);
            TrySetFloat(AnimationPhaseHash, _animationPhaseOffset);
            TrySetInteger(DeathVariantHash, (int)((uint)_animationSeed % 4u));
            ApplyReplicatedStun(state);

            if (identityChanged && _animator != null)
            {
                _animatorKickstarted = false;
                KickstartAnimator();
            }
            if (identityChanged)
            {
                // #1233 S8: the equipment bind belongs to the frame the equipment becomes known -
                // InitializeServer on the host, the state's arrival on a client or late-joiner -
                // not to the first Update, where it was the 75.6 ms frame of 27 September.
                BindEquipment();
                _diagnosticOrderInitialized = false;
                if (BlacksiteDiagnostics.Enabled)
                {
                    BlacksiteDiagnostics.Log(
                        "Member",
                        $"identity index={_index}, role={_tacticalRole}, slot={state.FormationSlot}, seed={_animationSeed}, health={enemyHP}, network={NetworkObjectId}.");
                }
            }

            if (applyReaction && state.ReactionSequence != _lastAppliedReactionSequence)
                ApplyReactionVisual(state);
            _lastAppliedReactionSequence = state.ReactionSequence;

            if (state.IsDead && !_deadHandled)
                KillEnemy(destroy: false);
        }

        private void ApplyReplicatedStun(BlacksiteSoldierSpawnState state)
        {
            if (!state.IsStunned)
            {
                if (stunNormalizedTimer > 0f)
                    base.SetEnemyStunned(false);
                return;
            }

            float remaining = Mathf.Max(0.05f, (float)(state.StunEndServerTime - GetServerTime()));
            base.SetEnemyStunned(true, remaining);
            stunNormalizedTimer = Mathf.Max(stunNormalizedTimer, remaining);
        }

        private void ApplyReactionVisual(BlacksiteSoldierSpawnState state)
        {
            switch (state.Reaction)
            {
                case BlacksiteSoldierReactionKind.DirectDamage:
                case BlacksiteSoldierReactionKind.Explosion:
                case BlacksiteSoldierReactionKind.Stun:
                    if (UsesBaton)
                    {
                        _batonHitUntil = Time.time + .45f; _batonHitSequence++;
                        if (IsServerAuthority() && BlacksiteBatonPolicy.DamageRequiresStow(state.Reaction == BlacksiteSoldierReactionKind.Stun, HandsUnavailable)) SetBatonPhase(BlacksiteBatonPhase.Stowed);
                    }
                    else PresentRifleHit(state.Reaction == BlacksiteSoldierReactionKind.Stun, Time.time);
                    break;
            }

            if (state.Reaction != BlacksiteSoldierReactionKind.DirectDamage)
                return;

            if (Time.time < _nextHitReactionAt)
                return;
            AudioClip[] clips = ContractAssetLoader.BlacksiteHitReactionClips;
            if (clips == null || clips.Length == 0)
                return;
            int index = (int)(((uint)_animationSeed + state.ReactionSequence) % (uint)clips.Length);
            _nextHitReactionAt = Time.time + HitReactionCooldownSeconds;
            PlayReactionClipLocal(clips[index], 0.62f, 1f);
        }

        private double GetServerTime()
        {
            return NetworkManager != null && NetworkManager.IsListening
                ? NetworkManager.ServerTime.Time
                : Time.timeAsDouble;
        }

        public override void Update()
        {
            if (NetworkObject == null || !NetworkObject.IsSpawned)
                return;
            // #874 Q11: the hitch tracer's per-soldier scope (the 442 ms frame of 23 September).
            using var hitch = BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierUpdate);

            // Native LandOnly recovery infers swimming from repeated stationary position syncs.
            // Blacksite owns all locomotion/recovery; this unrelated timer must never teleport it.
            enemySwimTimer = 0f;
            swimming = false;
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierDiagnostics))
                MeasureActualMovement();
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierVanillaUpdate))
                base.Update();
            enemySwimTimer = 0f;
            swimming = false;

            if (!_runtimeComponentsReady || _agent == null || _animator == null || _audioSource == null)
                EnsureRuntimeComponents();

            // #316: a hosted-weapon miss at spawn is only an answer once the bridge has settled. Six
            // soldiers binding in one frame share a single throttled type lookup, so five of them
            // come away with "not yet" - and the runtime-component pass above stops running the
            // moment the components are ready, so nothing asked again. Keep offering the rig (the
            // callee gates itself to once a second) until the answer is a model or a permanently
            // inert bridge, after which this is one bool per frame.
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierPresentation))
            {
                if (!isEnemyDead && (_weaponVisual == null || !_weaponVisual.IsSettled))
                {
                    TryAttachHostedWeapon();
                    // #929: a hosted pack that reseats or replaces the weapon source must not leave the
                    // rifle report off the diagetic mixer; the call is a no-op on an already-routed source.
                    ConfigureWeaponAudio();
                }
            }
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierAudio))
                _weaponPropagation?.Tick();

            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierPresentation))
                BlacksiteVanillaMonsters.PoseCapture(this);
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierLights))
            {
                UpdateMuzzleLight();
                UpdateHelmetLightPose();
            }
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierAnimation))
                UpdateAnimation();
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierPresentation))
            {
                UpdateEquipmentPresentation();
                // #874 S2.4: last in the presentation pass, after the late weapon attach, the vanilla
                // mesh toggle and the equipment writer, so none of them can leave a gun-only actor.
                ApplyLifecyclePresentation("frame");
            }

            bool hasServerAuthority = IsServerAuthority();
            if (hasServerAuthority)
                UpdateRoleCues();

            if (hasServerAuthority && !_initialized)
            {
                PublishIntent(BlacksiteBehaviour.None, "initializing");
                return;
            }

            if (isEnemyDead)
            {
                if (hasServerAuthority)
                {
                    PublishIntent(BlacksiteBehaviour.None, "dead");
                    // #991 round 2 (should-fix 5): a member killed in the air never reaches TickJump
                    // again, so the leg has to be dropped here or the corpse freezes mid-arc with
                    // agent.updatePosition still false. ReleaseJump restores the agent flags and
                    // does not move the body, so the death handler places it.
                    _navigator?.ReleaseJump("dead");
                    // #874 F07: and the same for the world ladder, for the same reason and on the
                    // same line of argument. A member killed on the rungs never reaches
                    // TickWorldLadder again, so nothing dropped the motor, released its column and
                    // queue slots or restored the agent flags the mount wrote: the corpse held a
                    // slot on a column its squadmates were queueing for, with updatePosition still
                    // false. ReleaseWorldLadder is the single owner of all of that and, like
                    // ReleaseJump, it never moves the body - the death handler below places it.
                    _navigator?.ReleaseWorldLadder();
                    // #874 F07: the rope controller swap is the third thing a death used to keep.
                    // ApplyGuardTraversalAnimation's revert arm needs a tick of that writer with the
                    // rope flag already down, and a dead member is not guaranteed one, so the swap
                    // could outlive the traversal with the baton sidecar bound over the corpse.
                    RestoreGuardTraversalController();
                    using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierCombat))
                        _combatController?.TickServer(disabled: true);
                    UpdatePoseFlagsServer(forceInactive: true);
                }
                HandleDeadState();
                return;
            }

            if (!hasServerAuthority)
                return;

            if (_monsterCaptured)
            {
                _navigator?.EndPatrolAlert();
                _navigator?.ReleaseWorldLadder();
                // #991 round 2 (blocking 3): without this the crossing survives the capture, and the
                // capture snapshot records the crossing's own updatePosition=false as the value to
                // restore.
                _navigator?.ReleaseJump("captured");
                using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierCombat))
                    _combatController?.TickServer(disabled: true);
                UpdatePoseFlagsServer(forceInactive: true);
                SelectExclusiveBehaviour(BlacksiteBehaviour.Hold, "monster-capture");
                return;
            }
            RefreshStunStateServer();
            RefreshTransportLifecycleServer();
            // #872: the off-mesh guard runs ahead of the recovery and passage gates so no early
            // return below can leave an agent off the mesh; it no-ops while those systems own the agent.
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierNavigator))
            {
                Navigator.TickMaintenance();
                if (Navigator.TickWorldLadder())
                {
                    using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierCombat))
                        _combatController?.TickServer(disabled: true);
                    UpdatePoseFlagsServer(forceInactive: true);
                    SelectExclusiveBehaviour(BlacksiteBehaviour.CommittedTraversal, "world-ladder"); return;
                }
                // #991: beside the ladder and for the same reason. A member on the arc owns its own
                // transform for a bounded second or two and nothing below may take a frame from it.
                if (Navigator.TickJump())
                {
                    using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierCombat))
                        _combatController?.TickServer(disabled: true);
                    UpdatePoseFlagsServer(forceInactive: true);
                    SelectExclusiveBehaviour(BlacksiteBehaviour.CommittedTraversal, "jump"); return;
                }
            }
            // #874 S4.0b: retires contact-response pairs that were never answered. Runs once per
            // frame across the squad, whichever member reaches it first.
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierDiagnostics))
                BlacksitePerceptionDiagnostics.Tick();
            EnsureCombatController();
            if (_spawnState.Value.IsStunned || stunNormalizedTimer > 0f)
                _navigator?.EndPatrolAlert();
            UpdateRaidWithdrawal();
            TickCargoThreatServer();
            if (TickMonsterReaction()) return;
            Navigator.SetAutomaticRotation(AgentOwnsRotation);
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierCombat))
                _combatController.TickServer(disabled: ResolveCombatDisabledServer());
            UpdatePoseFlagsServer(forceInactive: false);
            if (!_initialized || _spawnState.Value.IsStunned)
            {
                if (_spawnState.Value.IsStunned && _interactionCoordinator != null && _interactionCoordinator.IsCommittedTraversal)
                { _interactionCoordinator.TickActiveInteraction(); SelectExclusiveBehaviour(BlacksiteBehaviour.CommittedTraversal, "stunned-elevator-ride"); return; }
                if (_spawnState.Value.IsStunned)
                    _interactionCoordinator?.Interrupt("replicated stun");
                // #874 S4, #1293 R3: a stun is a dwell to its own end; a zap-gun stun's lasts until
                // RefreshStunStateServer clears the stun and ends it.
                if (_spawnState.Value.IsStunned)
                {
                    float remaining = (float)(_spawnState.Value.StunEndServerTime - GetServerTime());
                    Navigator.Dwell(remaining > 0f ? remaining : float.PositiveInfinity, "stunned");
                }
                SelectExclusiveBehaviour(BlacksiteBehaviour.Hold, "stunned-or-uninitialized");
                return;
            }

            MaybeLogEntranceSettleComplete();
            UpdateFacing();
            // #312 P0.1 / #1293 R4: the elevator link ticks every frame - its lane forgets a member
            // that has not asked for 2 s, and CarryActorWithElevator applies the car's per-frame
            // delta - so the link ticks at frame rate here and the brain's interaction continuation
            // does not tick it a second time.
            if (_interactionCoordinator != null
                && _interactionCoordinator.IsElevatorRideActive
                && _agent != null
                && _agent.enabled)
            {
                _interactionCoordinator.TickActiveInteraction();
                // Only a committed ride pre-empts the brain; walking into the car is interruptible.
                if (_interactionCoordinator.IsCommittedTraversal)
                { SelectExclusiveBehaviour(BlacksiteBehaviour.CommittedTraversal, "elevator-ride"); return; }
            }

            // #1020 S6, #1293 R3: the one decision core, below every motor return: a decision when
            // dirty and admitted, otherwise the committed action's continuation.
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierBrain))
                Brain.Frame();
        }

        public override void DoAIInterval()
        {
            base.DoAIInterval();
        }

        public override void OnCollideWithPlayer(Collider other)
        {
        }

        public override void OnCollideWithEnemy(Collider other, EnemyAI collidedEnemy = null)
        {
            base.OnCollideWithEnemy(other, collidedEnemy);
            EnemyAI enemy = collidedEnemy ?? (other != null ? other.GetComponentInParent<EnemyAI>() : null);
            if (!IsServerAuthority()
                || enemy == null
                || enemy == this
                || enemy is BlacksiteSoldierAI
                || enemy.isEnemyDead)
                return;

            if (BlacksiteVanillaMonsters.Contact(enemy, this)) return;

            // Contact is not damage. Harmless/unshootable wildlife must not restart hit poses or jobs.
            // #1305 H (C3): a slime's contact hurts; standing in any hazard's footprint is the next
            // perception pass's to admit a way out of (ObserveHazardRowsServer).
            if (BlacksiteHazardAwareness.IsHazard(enemy))
            {
                if (enemy is BlobAI blob) BlacksiteVanillaMonsters.SlimeContact(blob, this);
                return;
            }
            if (!CanConsiderEnemy(enemy)) return;
            if (!_contactCooldown.TryAccept(enemy.GetInstanceID(), 0, Time.time, 1f)) return;
            NotifyAttackedByEnemy(enemy);
            BlacksiteMonsterTargeting.ForceAggro(enemy, this, 7f);
        }

        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
        {
            if (isEnemyDead)
                return;

            base.HitEnemy(force, playerWhoHit, playHitSFX, hitID);
            if (IsServerAuthority())
                ApplyDamageServer(force, playerWhoHit);
        }

        public override void SetEnemyStunned(
            bool setToStunned,
            float setToStunTime = 1f,
            PlayerControllerB setStunnedByPlayer = null)
        {
            base.SetEnemyStunned(setToStunned, setToStunTime, setStunnedByPlayer);
            if (!IsServerAuthority() || isEnemyDead || !_spawnState.Value.IsConfigured)
                return;

            BlacksiteSoldierSpawnState state = _spawnState.Value;
            state.IsStunned = setToStunned;
            state.StunEndServerTime = setToStunned
                ? GetServerTime() + Mathf.Max(0.1f, setToStunTime) * Mathf.Max(0.1f, enemyType.stunTimeMultiplier)
                : 0d;

            if (setToStunned)
            {
                Vector3 stimulus = setStunnedByPlayer != null
                    ? setStunnedByPlayer.transform.position
                    : transform.position - transform.forward * 2f;
                StampReaction(
                    ref state,
                    BlacksiteSoldierReactionKind.Stun,
                    stimulus,
                    setStunnedByPlayer != null ? setStunnedByPlayer.playerClientId : ulong.MaxValue);
            }

            _spawnState.Value = state;
            EnsureCombatController();
            _combatController.TickServer(disabled: setToStunned);
            if (setToStunned)
            {
                // S1's mid-shaft note: a committed ride is atomic, as a transport detach already
                // leaves it (DetachFromTransportServer); the stunned-elevator-ride branch ticks it on.
                if (!HasCommittedJobTraversal) _interactionCoordinator?.Interrupt("stunned");
                if (setToStunTime >= BlacksiteDriveCarrier.QualifyingHeavyStunSeconds)
                    DropCarriedDriveServer("qualifying heavy stun");
                InterruptForStimulusServer(state.StimulusPosition);
                BroadcastSquadStimulusServer(
                    BlacksiteSoldierReactionKind.Stun,
                    state.StimulusPosition,
                    this);
            }
        }

        public override void DetectNoise(
            Vector3 noisePosition,
            float noiseLoudness,
            int timesPlayedInOneSpot = 0,
            int noiseID = 0)
        {
            base.DetectNoise(noisePosition, noiseLoudness, timesPlayedInOneSpot, noiseID);
            // #1233 S8: a squadmate's boot or rifle is not a stimulus (see _squadNoiseInFlight).
            if (_squadNoiseInFlight != null || !IsServerAuthority() || noiseLoudness <= 0.05f)
                return;

            float hearingRadius = Mathf.Lerp(6f, MaxStimulusOffset, Mathf.Clamp01(noiseLoudness));
            if (Vector3.Distance(transform.position, noisePosition) > hearingRadius)
                return;

            ReactToStimulusServer(
                BlacksiteSoldierReactionKind.Noise,
                noisePosition,
                ulong.MaxValue,
                broadcastToSquad: true);
        }

        public override void ReceiveLoudNoiseBlast(Vector3 position, float angle)
        {
            if (!IsServerAuthority() || Vector3.Distance(transform.position, position) > 60f)
                return;

            ReactToStimulusServer(
                BlacksiteSoldierReactionKind.LoudNoise,
                position,
                ulong.MaxValue,
                broadcastToSquad: true);
        }

        public override void HitFromExplosion(float distance)
        {
            base.HitFromExplosion(distance);
            if (!IsServerAuthority())
                return;

            Vector3 known = _combatController != null ? _combatController.GetCurrentAimPoint() : Vector3.zero;
            Vector3 stimulus = known != Vector3.zero
                ? known
                : transform.position - transform.forward * Mathf.Clamp(distance, 1f, MaxStimulusOffset);
            ReactToStimulusServer(
                BlacksiteSoldierReactionKind.Explosion,
                stimulus,
                ulong.MaxValue,
                broadcastToSquad: true);
        }

        public override void KillEnemy(bool destroy = false)
        {
            if (_deadHandled)
                return;

            _deadHandled = true;
            if (IsServerAuthority() && TryGetMission(out var casualtyMission)) casualtyMission.Objectives.RequestGuardBackup(this, true);
            _interactionCoordinator?.Interrupt("soldier died");
            if (IsServerAuthority())
            {
                EnsureCombatController();
                _combatController.TickServer(disabled: true);
                UpdatePoseFlagsServer(forceInactive: true);
            }
            if (IsServerAuthority())
                DropCarriedDriveServer("carrier death");
            // #316: the corpse leaves a real, pickable MX16A4 with the rounds he had left. Server
            // only, and a no-op when Y4NGZUpgrades is absent - then this is exactly today's death.
            if (IsServerAuthority() && _weaponVisual != null && _weaponVisual.HasModel)
                if (UsesBaton) BlacksiteEquipmentAssets.DropServer(this);
                else BlacksiteSoldierWeaponVisual.TrySpawnDeathDropServer(this);
            if (IsServerAuthority() && _spawnState.Value.IsConfigured && !_spawnState.Value.IsDead)
            {
                BlacksiteSoldierSpawnState state = _spawnState.Value;
                state.Health = 0;
                state.IsDead = true;
                state.IsStunned = false;
                state.StunEndServerTime = 0d;
                _spawnState.Value = state;
            }

            // #1066 U8: vanilla KillEnemy stops creatureVoice and then PlayOneShot(dieSFX) on it.
            // creatureVoice IS _voiceSource, and PlaySquadVoiceLocal/PlayReactionClipLocal leave
            // whatever pitch their last line used on it (0.85-1.15), so the death clip would
            // inherit a pitch that belongs to an unrelated radio line. Our own reaction always
            // played the death clip at pitch 1; restore that neutral pitch here so the clip vanilla
            // is about to start sounds the same whatever the soldier last said.
            if (_voiceSource != null)
                _voiceSource.pitch = 1f;
            base.KillEnemy(destroy);
            isEnemyDead = true;
            BlacksiteMonsterTargeting.NotifySoldierUnavailable(this);
            HandleDeadState();
            if (IsServerAuthority())
            {
                // #1293 R3: a corpse owes no decision.
                if (TryGetMission(out var deadMission)) deadMission.DecisionQueue?.Remove(NetworkObjectId);
                if (TryGetSquadDirector(out BlacksiteSquadDirector director))
                    director.ReportCasualty(this);
            }
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Soldier {_index} killed.");
        }

        private void ApplyDamageServer(int force, PlayerControllerB attacker)
        {
            if (!_spawnState.Value.IsConfigured || _spawnState.Value.IsDead)
                return;

            BlacksiteSoldierSpawnState state = _spawnState.Value;
            int previousHealth = state.Health;
            // #988 B3: the damage clock the behaviour loop reads. Stamped where the hit is applied
            // rather than where it is reported, so it is live on the same frame the member is bitten
            // and cannot be lost to a squad-memory round trip.
            if (force > 0) _lastDamageTakenAt = Time.time;
            if (force > 0) ArmCargoThreatServer(Time.time, "direct damage; freeing hands for defence");
            if (force > 0 && TryGetMission(out var damageMission)) damageMission.Objectives.RequestGuardBackup(this, false);
            state.Health = Mathf.Max(0, state.Health - Mathf.Max(1, force));
            var nativeShot = BlacksiteNativeShotgun.Current;
            if (nativeShot != null)
            {
                NotifyAttackedByEnemy(nativeShot.Enemy);
                BlacksiteDiagnostics.Log("NativeShot", $"shot={nativeShot.Id}, enemy={nativeShot.Enemy.GetInstanceID()}, member={MemberIndex}, hp={previousHealth}->{state.Health}, force={force}, source=native-shotgun.");
            }
            state.IsDead = state.Health <= 0;
            if (state.IsDead)
            {
                state.IsStunned = false;
                state.StunEndServerTime = 0d;
            }

            Vector3 known = attacker == null && _combatController != null ? _combatController.GetCurrentAimPoint() : Vector3.zero;
            Vector3 stimulus = attacker != null
                ? attacker.transform.position
                : (known != Vector3.zero
                    ? known
                    : transform.position - transform.forward * 2f);
            StampReaction(
                ref state,
                BlacksiteSoldierReactionKind.DirectDamage,
                stimulus,
                attacker != null ? attacker.playerClientId : ulong.MaxValue);
            _spawnState.Value = state;

            if (attacker != null && CanConsiderPlayer(attacker))
            {
                // Being shot is a first-hand sense: he knows exactly who hit him even without a
                // clean line of sight back. #1293 R1: the attacker's row is Heard where he stands;
                // the perception pass's eyes make it Saw.
                NoteHeardServer(attacker.NetworkObjectId, attacker, BlacksiteWorldClass.Player,
                    attacker.transform.position, !attacker.isInsideFactory);
                EnsureCombatController();
                _combatController.NotifyImmediateAttacker(attacker);
            }

            if (attacker != null && !IsPlayerCombatEligible(attacker)) return;
            InterruptForStimulusServer(state.StimulusPosition);
            BroadcastSquadStimulusServer(
                BlacksiteSoldierReactionKind.DirectDamage,
                state.StimulusPosition,
                this);
        }

        private void ReactToStimulusServer(
            BlacksiteSoldierReactionKind reaction,
            Vector3 stimulusPosition,
            ulong attackerClientId,
            bool broadcastToSquad)
        {
            if (!IsServerAuthority()
                || !_spawnState.Value.IsConfigured
                || _spawnState.Value.IsDead
                || isEnemyDead)
            {
                return;
            }

            Vector3 boundedPosition = ClampStimulusPosition(stimulusPosition);
            if (!_reactionCooldown.TryAccept((int)reaction, 0, Time.time, reaction == BlacksiteSoldierReactionKind.Noise ? 1.5f : StimulusDebounceSeconds))
            {
                return;
            }

            _lastStimulusKind = reaction;
            _lastStimulusTime = Time.time;

            BlacksiteSoldierSpawnState state = _spawnState.Value;
            StampReaction(ref state, reaction, boundedPosition, attackerClientId);
            _spawnState.Value = state;
            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Reaction",
                    $"member={_index}, role={_tacticalRole}, reaction={reaction}, sequence={state.ReactionSequence}, point={state.StimulusPosition}, attacker={attackerClientId}.");
            }
            // Hearing a sound updates awareness; it must not abort a doorway or elevator ride.
            if (reaction != BlacksiteSoldierReactionKind.Noise)
                InterruptForStimulusServer(state.StimulusPosition);

            if (broadcastToSquad)
                BroadcastSquadStimulusServer(reaction, state.StimulusPosition, this);
        }

        private void StampReaction(
            ref BlacksiteSoldierSpawnState state,
            BlacksiteSoldierReactionKind reaction,
            Vector3 stimulusPosition,
            ulong attackerClientId)
        {
            state.ReactionValue = (byte)reaction;
            unchecked
            {
                state.ReactionSequence++;
            }
            state.StimulusPosition = ClampStimulusPosition(stimulusPosition);
            state.LastAttackerClientId = attackerClientId;
        }

        private Vector3 ClampStimulusPosition(Vector3 stimulusPosition)
        {
            Vector3 offset = stimulusPosition - transform.position;
            if (offset.sqrMagnitude > MaxStimulusOffset * MaxStimulusOffset)
                offset = offset.normalized * MaxStimulusOffset;
            return transform.position + offset;
        }

        private void InterruptForStimulusServer(Vector3 stimulusPosition)
        {
            if (!IsServerAuthority() || isEnemyDead)
                return;

            if (_interactionCoordinator != null && _interactionCoordinator.IsCommittedTraversal) return;
            _interactionCoordinator?.Interrupt("damage or forced reaction");

            Vector3 investigationPoint = ResolveNavMeshPosition(stimulusPosition, 6f);
            Navigator.PauseForStimulus();
            RequestDecision(BlacksiteDecisionCause.Stimulus);
            // The brief hold is enough to sell the reaction; wiping the path here made a
            // whole squad stutter every time a player walked past.
            FacePoint(investigationPoint);
        }

        private void BroadcastSquadStimulusServer(
            BlacksiteSoldierReactionKind reaction,
            Vector3 stimulusPosition,
            BlacksiteSoldierAI source)
        {
            if (IsServerAuthority()
                && _spawnState.Value.IsConfigured
                && TryGetSquadDirector(out BlacksiteSquadDirector director))
                director.ReportStimulus(source ?? this, reaction, stimulusPosition);
        }

        internal void ReceiveSquadStimulusFromDirector(
            BlacksiteSoldierReactionKind sourceReaction,
            Vector3 stimulusPosition)
        {
            BlacksiteSoldierReactionKind reaction = sourceReaction == BlacksiteSoldierReactionKind.Explosion
                || sourceReaction == BlacksiteSoldierReactionKind.LoudNoise
                    ? sourceReaction
                    : BlacksiteSoldierReactionKind.Noise;
            ReactToStimulusServer(
                reaction,
                stimulusPosition,
                ulong.MaxValue,
                broadcastToSquad: false);
        }

        private void RefreshStunStateServer()
        {
            if (!_spawnState.Value.IsConfigured
                || !_spawnState.Value.IsStunned
                || stunnedIndefinitely > 0
                || GetServerTime() < _spawnState.Value.StunEndServerTime)
            {
                return;
            }

            BlacksiteSoldierSpawnState state = _spawnState.Value;
            state.IsStunned = false;
            state.StunEndServerTime = 0d;
            _spawnState.Value = state;
            // #1293 R3: the stun's end ends its dwell and is an Outcome for planner and brain.
            _navigator?.EndDwell();
            RequestDecision(BlacksiteDecisionCause.Outcome);
        }

        public override void OnDestroy()
        {
            if (IsServerAuthority())
            {
                try
                {
                    DropCarriedDriveServer("carrier destroyed");
                    if (TryGetSquadDirector(out BlacksiteSquadDirector director))
                        director.UnregisterMember(this);
                }
                catch (Exception e)
                {
                    if (!_teardownFailureLogged)
                    {
                        _teardownFailureLogged = true;
                        ContractsBootstrap.Log?.LogDebug(
                            $"[MoonContracts.Blacksite] Soldier {_index} teardown cleanup skipped: {e.Message}");
                    }
                }
            }
            _squadDirector = null;
            _navigator?.Dispose();
            _navigator = null;
            base.OnDestroy();
        }

        private void EnsureRuntimeComponents()
        {
            if (_rigBinding == null || !_rigBinding.IsValid)
            {
                if (_rigContractFailedHard || Time.time < _nextRigBindAttemptTime)
                {
                    _runtimeComponentsReady = false;
                    return;
                }

                _nextRigBindAttemptTime = Time.time + RigBindRetryInterval;
                if (!BlacksiteSoldierRigBinding.TryBind(gameObject, out BlacksiteSoldierRigBinding binding, out string failure))
                {
                    // A duplicate binding name is an authoring fault that never resolves itself.
                    // A missing binding can still be a spawn-order race (the prefab is assembled
                    // over several frames), so retry a bounded number of times before giving up.
                    if (failure != null && failure.Contains("appears more than once"))
                    {
                        _rigContractFailedHard = true;
                    }
                    else if (++_rigBindFailureCount >= MaximumRigBindAttempts)
                    {
                        _rigContractFailedHard = true;
                    }
                    if (!_rigFailureLogged)
                    {
                        _rigFailureLogged = true;
                        ContractsBootstrap.Log?.LogError(
                            $"[MoonContracts.Blacksite] Soldier {_index} disabled because its rig contract is invalid: {failure}.");
                    }
                    _runtimeComponentsReady = false;
                    return;
                }
                ApplyRigBinding(binding);
            }

            TryAttachHostedWeapon();

            Navigator.Bind(agent != null ? agent : GetComponent<NavMeshAgent>());
            if (creatureAnimator == null && _animator != null)
                creatureAnimator = _animator;
            if (creatureSFX == null)
                creatureSFX = _audioSource;
            if (creatureVoice == null)
                creatureVoice = _voiceSource;

            if (!_sceneComponentsDisabled)
                DisableImportedSceneComponents();

            openDoorSpeedMultiplier = VanillaDoorOpenSpeedMultiplier;
            // Vanilla defaults leave clients guessing the interpolation rate, which the
            // animator then reads back as stuttering locomotion.
            syncMovementSpeed = AgentSyncMovementSpeed;
            updatePositionThreshold = AgentUpdatePositionThreshold;

            ResolveAttachmentAnchors();
            AttachMuzzleEffects();

            EnsureScanNode();

            Navigator.ConfigureAgent();

            if (_animator != null)
            {
                _animator.enabled = true;
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                LogAnimatorStateOnce();
                try
                {
                    if (!_animator.isInitialized)
                    {
                        _animator.Rebind();
                        InvalidateAnimatorParameterCache();
                    }
                    EnsureAnimationEventFootsteps();
                    EnsureAimPoseHelper();
                    KickstartAnimator();
                }
                catch
                {
                    // Some imported controllers report initialization late; normal Update will recover.
                }
            }

            Navigator.EnsureInteractions();
            EnsureHitDetectionWired();
            _runtimeComponentsReady = _agent != null
                && _animator != null
                && _audioSource != null
                && _voiceSource != null
                && _footstepSource != null
                && _rigBinding != null
                && _rigBinding.IsValid;
        }

        private void UpdateAnimation()
        {
            if (_animator == null)
                return;

            if (IsServerAuthority())
            {
                float roleSpeed = GetRoleMoveSpeed();
                if (_animationRoleSpeed.Value != roleSpeed)
                    _animationRoleSpeed.Value = roleSpeed;
            }

            float now = Time.time;
            float delta = Mathf.Max(now - _lastAnimationSampleTime, 0.001f);
            Vector3 worldVelocity = (transform.position - _lastPosition) / delta;
            // #1283 S7a: a rider's deltas are the hull's flight, not his walk. Stowed (not yet configured)
            // or boarded, he stands on every peer; a client takes the zero unsmoothed, so no run tail plays.
            bool riding = !HasReplicatedIdentity || EffectiveLifecycle == BlacksiteLifecycleState.Boarded;
            Vector3 planarVelocity = riding ? Vector3.zero : Vector3.ProjectOnPlane(worldVelocity, Vector3.up);
            if (riding || IsServerAuthority())
            {
                _smoothedPlanarVelocity = planarVelocity;
            }
            else
            {
                // Replicated transforms arrive as catch-up bursts; without smoothing the
                // run/idle blend snaps between the bursts and the idle frames after them.
                float velocitySmoothing = 1f - Mathf.Exp(-delta / ClientVelocitySmoothingSeconds);
                _smoothedPlanarVelocity = Vector3.Lerp(
                    _smoothedPlanarVelocity,
                    planarVelocity,
                    velocitySmoothing);
                planarVelocity = _smoothedPlanarVelocity;
            }
            float speed = planarVelocity.magnitude;
            UpdateCalibratedAnimationSpeed();
            _currentMoveSpeed = speed;
            _lastPosition = transform.position;
            _lastAnimationSampleTime = now;

            // #874 Q7 (E3): the one normalisation. MoveX/MoveZ/Speed are the body's planar velocity in
            // its own frame over the replicated role speed, through BlacksiteLocomotionSpeedTable, and
            // the rest and aimed trees read the same values, so a stance change never re-normalises.
            float maxSpeed = Mathf.Max(0.1f, AnimationRoleMoveSpeed);
            Vector3 localPlanarVelocity = transform.InverseTransformDirection(planarVelocity);
            BlacksiteLocomotionSpeedTable.Blend(localPlanarVelocity.x, localPlanarVelocity.z, AnimationRoleMoveSpeed,
                out float moveX, out float moveZ, out float normalizedSpeed);
            _normalizedMoveSpeed = normalizedSpeed;
            if (normalizedSpeed >= BlacksiteLocomotionSpeedTable.StationaryBlend) _stationarySince = -1f;
            else if (_stationarySince < 0f) _stationarySince = now;
            float currentYaw = transform.eulerAngles.y;
            float turnAngle = Mathf.DeltaAngle(_lastAnimationYaw, currentYaw);
            float yawRate = turnAngle / delta;
            float normalizedTurnRate = Mathf.Clamp(
                yawRate / 360f,
                -1f,
                1f);
            _lastAnimationYaw = currentYaw;

            bool grounded = false;
            try
            {
                grounded = _agent != null && _agent.enabled && _agent.isOnNavMesh;
            }
            catch
            {
                grounded = false;
            }

            TrySetFloatDamped(MoveSpeedHash, normalizedSpeed, AnimationFloatDampTime, delta);
            TrySetFloatDamped(PlanarVelocityXHash, moveX, AnimationFloatDampTime, delta);
            TrySetFloatDamped(PlanarVelocityYHash, moveZ, AnimationFloatDampTime, delta);
            TrySetFloatDamped(TurnRateHash, normalizedTurnRate, AnimationFloatDampTime, delta);
            byte poseFlags = _poseFlags.Value;
            bool aimingIntent = (poseFlags & AimingPoseFlag) != 0;
            bool crouched = (poseFlags & CrouchedPoseFlag) != 0;
            bool climbing = (poseFlags & ClimbingPoseFlag) != 0;
            // #991: replicated, so a client picks the jump state on the frame the bit arrives.
            _jumpingPose = (poseFlags & JumpingPoseFlag) != 0;
            // #874 S5 (F07): a rope crossing whose sidecar has no BatonRope gets a code-only stand-in, a slow walk at
            // the climb rate. #1283 S3b: a ladder has none; a controller without LadderClimb shows plain StandingPatrol.
            bool ropeWalks = _ropeDirection.Value != 0 && RopeStateMissing;
            float climbBlend = climbing && ropeWalks
                ? BlacksiteTraversalPresentation.LadderFallbackBlend(maxSpeed)
                : float.NaN;
            TrySetBool(GroundedHash, grounded);
            TrySetInteger(StanceHash, ResolveAnimationStance());
            TrySetInteger(IdentitySeedHash, _animationSeed & int.MaxValue);
            TrySetFloat(AnimationPhaseHash, Mathf.Repeat(_animationPhaseOffset + now * 0.01f, 1f));

            // #1219 E4: damp the body-frame velocity BEFORE mapping it onto the stroll axis, so the
            // map never interpolates through the run child. Other trees retain their parameter damping.
            float strollX = moveX, strollZ = moveZ;
            if (!UsesBaton && !aimingIntent && !crouched && !climbing && !_jumpingPose
                && _strollCapability.IsValid
                && ReferenceEquals(_animator.runtimeAnimatorController, _restStanceController))
            {
                float smoothing = 1f - Mathf.Exp(-delta / AnimationFloatDampTime);
                _strollRawMoveX = Mathf.Lerp(_strollRawMoveX, moveX, smoothing);
                _strollRawMoveZ = Mathf.Lerp(_strollRawMoveZ, moveZ, smoothing);
                strollX = _strollRawMoveX; strollZ = _strollRawMoveZ;
                BlacksiteLocomotionSpeedTable.StrollBlend(_strollCapability, ref strollX, ref strollZ);
                TrySetFloat(MoveXHash, strollX);
                TrySetFloat(MoveZHash, strollZ);
            }
            else
            {
                _strollRawMoveX = moveX; _strollRawMoveZ = moveZ;
                TrySetFloatDamped(MoveXHash, moveX, AnimationFloatDampTime, delta);
                TrySetFloatDamped(MoveZHash, float.IsNaN(climbBlend) ? moveZ : climbBlend, AnimationFloatDampTime, delta);
            }
            TrySetFloatDamped(SpeedHash, float.IsNaN(climbBlend) ? normalizedSpeed : climbBlend, AnimationFloatDampTime, delta);
            TrySetBool(CrouchedHash, crouched);
            TrySetBool(ClimbingHash, climbing);
            WriteLadderClimbRate();
            TrySetInteger(DeathVariantHash, (int)((uint)_animationSeed % 4u));
            TrySetBool(DeadHash, isEnemyDead);
            _climbingPose = climbing;
            // #874 S5.2 (#873): the rifle traversal presentation, from replicated state only (the pose flags and
            // the traversal kind), so a client shows what the host shows; the baton's is UpdateEquipmentPresentation.
            bool traversalHoldsBody = UsesBaton
                ? climbing || _jumpingPose
                : DriveRifleTraversalPresentation(climbing, maxSpeed);
            if (!UsesBaton && !_guardTraversalActive)
                BindRestStanceStates();
            BlacksiteStance stance = ApplyStance(aimingIntent, traversalHoldsBody);
            PublishTurnAngleServer(stance, crouched, normalizedSpeed);
            float turnAngleValue = _turnAngle.Value;
            TrySetFloat(TurnAngleHash, turnAngleValue);
            DriveAimPose(stance.Aiming, delta);
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierAudio))
                TickFootstepAudio(speed);
            DriveRestIdleVariant(stance, crouched, now);
            ReleasePlantedFireClip(stance.Aiming, crouched);
            // #874 G5: the gait sample, LAST so it records the state this frame leaves behind. Without the Shadow
            // Raid switch and an open per-member detail window nothing is sampled or allocated.
            if (BlacksiteDiagnostics.Enabled && BlacksiteDiagnostics.IsDetailOpen(DiagnosticIdentity))
            {
                using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierDiagnostics))
                    SampleGait(
                        delta,
                        worldVelocity,
                        planarVelocity,
                        localPlanarVelocity,
                        strollX,
                        strollZ,
                        normalizedSpeed,
                        maxSpeed,
                        climbBlend,
                        normalizedTurnRate,
                        turnAngleValue,
                        grounded);
            }
        }

        /// <summary>
        /// #874 (2026-09-25): the controller gives RestIdleVariant no incoming transition, so the
        /// runtime cross-fades into it once the member has stood still in StandingPatrol for
        /// <see cref="BlacksiteStancePolicy.IdleVariantAfterSeconds"/>; the authored exits bring it
        /// back to StandingPatrol at the clip's end (or to RestToAim / RestToSlung). Peer-local and
        /// cosmetic by design: every peer runs its own still-clock from the same replicated inputs.
        /// </summary>
        private void DriveRestIdleVariant(in BlacksiteStance stance, bool crouched, float now)
        {
            // Only a controller that carries the rest states has the variant. The current state,
            // never a transition: a StandingPatrol that is already leaving (for RestTurn90,
            // RestToAim) must not be cut, and a variant already leaving must not be re-issued.
            if (UsesBaton || !_restStanceAuthored || !_animator.isInitialized || _animator.IsInTransition(0))
            { _idleVariantStillSince = -1f; return; }
            bool rest = stance.Reason == BlacksiteStanceReason.Rest && !crouched
                && !isEnemyDead && !ReplicatedDead && !ReplicatedStunned;
            int state = _animator.GetCurrentAnimatorStateInfo(0).shortNameHash;
            bool hasSpeed = HasParameter(SpeedHash, AnimatorControllerParameterType.Float);
            float dampedSpeed = hasSpeed ? _animator.GetFloat(SpeedHash) : 0f;
            // #874 (2026-09-25): the variant's only authored exits are Aiming, HandsUnavailable and
            // the clip's end; a walk or turn during it is taken back to StandingPatrol here.
            if (state == RestIdleVariantStateHash)
            {
                if (BlacksiteStancePolicy.IdleVariantCancel(true, dampedSpeed >= BlacksiteLocomotionSpeedTable.StationaryBlend,
                        _turnAngle.Value != 0f, rest, stance.Aiming || stance.HandsUnavailable))
                    _animator.CrossFadeInFixedTime(StandingPatrolStateHash, .2f, 0);
                _idleVariantStillSince = -1f;
                return;
            }
            // An in-place turn (TurnAngle live) is movement.
            bool still = rest && _turnAngle.Value == 0f && state == StandingPatrolStateHash
                && hasSpeed && dampedSpeed < BlacksiteLocomotionSpeedTable.StationaryBlend;
            if (!still) { _idleVariantStillSince = -1f; return; }
            if (_idleVariantStillSince < 0f) { _idleVariantStillSince = now; return; }
            if (!BlacksiteStancePolicy.IdleVariantDue(now - _idleVariantStillSince, MemberIndex, true, rest, stance.HandsUnavailable))
                return;
            _animator.CrossFadeInFixedTime(RestIdleVariantStateHash, .25f, 0);
            _idleVariantStillSince = -1f;
        }

        /// <summary>
        /// #1219 F2: StandingFire and CrouchFire (BlacksiteInsurgentRuntime, read offline from the
        /// pinned bundle) are single full-body clips - StandingFire 1.17 s, CrouchRapidFire 0.77 s -
        /// whose only exits are at exit time 0.92, to StandingAimed/StandingPatrol on Aiming and to
        /// CrouchedLocomotion. A rifleman who fired standing and then takes a combat step inside
        /// that window would slide on planted legs until the clip ran out. Once the body is
        /// actually moving, the base layer is handed to the state the authored exit would have
        /// chosen, whose MoveX/MoveZ blend tree carries the step. Only the presentation changes:
        /// the rounds are released on the host's own clock and every fire gate is untouched.
        /// Peer-local and cosmetic like DriveRestIdleVariant: every peer reads the same replicated
        /// Speed and pose flags, so no RPC is involved.
        /// </summary>
        private void ReleasePlantedFireClip(bool aiming, bool crouched)
        {
            if (UsesBaton || _guardTraversalActive || !_animator.isInitialized || _animator.IsInTransition(0)
                || !HasParameter(SpeedHash, AnimatorControllerParameterType.Float)
                || _animator.GetFloat(SpeedHash) < BlacksiteLocomotionSpeedTable.StationaryBlend)
                return;
            int state = _animator.GetCurrentAnimatorStateInfo(0).shortNameHash;
            if (state != StandingFireStateHash && state != CrouchFireStateHash) return;
            int locomotion = crouched ? CrouchedLocomotionStateHash : aiming ? StandingAimedStateHash : StandingPatrolStateHash;
            if (HasAnimatorState(locomotion))
                _animator.CrossFadeInFixedTime(locomotion, .15f, 0);
        }
        // #873: replicated climbing pose for the aim overlay; a climbing member's hands hold the rungs.
        private bool _climbingPose;
        // Replicated jumping pose. Both loadouts use the maintained Baton jump states; the rifle
        // writer temporarily pairs that controller with its matching Generic avatar.
        private bool _jumpingPose;
        // One warning per member per round when the bound controller cannot play the jump.
        private bool _jumpPresentationReported;
        // True while this writer owns _animator.speed, so it hands it back exactly once.
        private bool _traversalSpeedHeld;
        // #1283 S4: this frame's TopsOut, written by WriteLadderClimbRate before the ladder writer reads it.
        private bool _ladderTopsOut;

        // #1283 S4: a dismount that does not top out holds the rise state still. Resolved for ResolveLadderState on the
        // bound controller: by UpdateAnimation every frame, and again by a baton member's rifle-pair writer, whose swap
        // (#1283 S3b) runs after UpdateAnimation.
        private void WriteLadderClimbRate()
        {
            _ladderTopsOut = BlacksiteTraversalPresentation.TopsOut(TraversalKind, HasAnimatorState(RifleLadderTopOutStateHash));
            TrySetFloat(ClimbRateHash, LadderPhase == BlacksiteLadderPhase.Dismounting && !_ladderTopsOut
                ? 0f : BlacksiteTraversalPresentation.LadderClimbRate);
        }

        // #874 S5.2 (#873), F07: the rifle loadout's ladder, rope and jump presentation and the animator speed
        // that goes with it. #874 Q7: returns whether a traversal state owns the whole body this frame (an
        // authored rung state, or the jump or rope on the shared controller) rather than locomotion.
        private bool DriveRifleTraversalPresentation(bool climbing, float roleMoveSpeed)
        {
            if (_animator == null)
                return false;

            // #874 F07: the shared controller's jump and, since #874 S5, its rope, both at the
            // crossing rate. TickJump and HandleWorldLadder exclude each other, so _jumpingPose and
            // _climbingPose are never both set. A sidecar with no BatonRope walks the legs at the
            // rope stand-in's rate. The speed is held so it is handed back exactly once.
            bool rope = climbing && BlacksiteTraversalPresentation.IsRope(TraversalKind);
            if (rope || (_jumpingPose && BlacksiteTraversalPresentation.IsJump(TraversalKind)))
            {
                ApplyGuardTraversalAnimation();
                if (rope && RopeStateMissing)
                    _animator.speed = BlacksiteTraversalPresentation.LadderFallbackSpeed(roleMoveSpeed);
                _traversalSpeedHeld = true;
                return _guardTraversalActive;
            }

            // #1283 S3b: a controller without the ladder states keeps plain locomotion (StandingPatrol) on its own clock.
            if (climbing && !BlacksiteTraversalPresentation.IsRope(TraversalKind) && DriveRifleLadderState())
            {
                _animator.speed = BlacksiteTraversalPresentation.LadderPlaybackSpeed;
                _traversalSpeedHeld = true;
                return true;
            }

            if (_traversalSpeedHeld)
            {
                _traversalSpeedHeld = false;
                // UpdateCalibratedAnimationSpeed already ran this frame and owns the value on a
                // calibrated controller; on one without the calibration nothing else writes it.
                if (!_strideCalibrated)
                    _animator.speed = 1f;
                else
                    UpdateCalibratedAnimationSpeed();
            }
            return _guardTraversalActive;
        }

        /// <summary>
        /// #874 Q7 (E2): the one stance owner. Every peer, every frame, from retained intent alone:
        /// the host's replicated aiming bit (a target the member intends to engage), the replicated
        /// weapon action, and the resolved hand owner. It is the only writer of the animator's
        /// <c>Aiming</c> and <c>HandsUnavailable</c>; nothing in presentation or movement sets either,
        /// and the controller's RestToAim / AimToRest / RestToSlung / SlungToRest states present every
        /// change. The rule, per hand owner, is <see cref="BlacksiteStancePolicy.Resolve"/>.
        /// </summary>
        private BlacksiteStance ApplyStance(bool targetIntent, bool traversalHoldsBody)
        {
            BlacksiteWeaponState weapon = WeaponSnapshot.State;
            bool weaponAction = weapon == BlacksiteWeaponState.FiringBurst || weapon == BlacksiteWeaponState.Recovering
                || weapon == BlacksiteWeaponState.Reloading || weapon == BlacksiteWeaponState.GrenadeCommitted;
            BlacksiteHandOwner owner = CurrentHandOwner;
            BlacksiteStance stance = BlacksiteStancePolicy.Resolve(targetIntent, weaponAction, owner, !traversalHoldsBody, ReplicatedStunned);
            TrySetBool(AimingHash, stance.Aiming);
            TrySetBool(HandsUnavailableHash, stance.HandsUnavailable);
            if (!_hasStance || stance.Aiming != _stance.Aiming || stance.HandsUnavailable != _stance.HandsUnavailable
                || stance.Reason != _stance.Reason)
            {
                _hasStance = true;
                _stance = stance;
                if (BlacksiteDiagnostics.Enabled && IsServerAuthority())
                    BlacksiteDiagnostics.Log("Stance",
                        $"member={MemberIndex}, aiming={stance.Aiming}, handsUnavailable={stance.HandsUnavailable}, "
                        + $"reason={stance.Reason}, owner={owner}, intent={targetIntent}, weapon={weapon}, "
                        + $"restStates={(_restStanceAuthored ? "present" : "absent")}");
            }
            return stance;
        }

        /// <summary>
        /// #874 Q7: resolves the rest-stance states once per bound rifle controller. A controller
        /// without them (the pre-Q7 bundle) keeps working on the parameters alone - it has no
        /// transition that reads <c>HandsUnavailable</c> - and says so in one warning per asset.
        /// #1219 E4: and the stroll capability of its StandingPatrol tree, which the probe reports in
        /// one bind line per asset naming the chosen walk and run children.
        /// </summary>
        private void BindRestStanceStates()
        {
            RuntimeAnimatorController controller = _animator.runtimeAnimatorController;
            if (controller == null || ReferenceEquals(controller, _restStanceController) || !_animator.isInitialized)
                return;
            _restStanceController = controller;
            _strollCapability = BlacksiteStrollCapabilityProbe.Resolve(controller);
            _strollRawMoveX = _strollRawMoveZ = 0f;
            string missing = null;
            for (int i = 0; i < RestStanceStateHashes.Length; i++)
            {
                if (HasAnimatorState(RestStanceStateHashes[i])) continue;
                string name = BlacksiteStancePolicy.RestStanceStateNames[i];
                missing = missing == null ? name : missing + ", " + name;
            }
            _restStanceAuthored = missing == null;
            if (missing == null || ReferenceEquals(_restStanceReportedController, controller))
                return;
            _restStanceReportedController = controller;
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.Blacksite] Soldier controller '{controller.name}' has no rest-stance state(s) {missing};"
                + " Aiming and HandsUnavailable are still written, but the rifle raise, lower, stow, draw and rest turns"
                + " are not presented. Rebuild the contracts bundle that carries the #874 Q7 controller.");
        }

        /// <summary>
        /// #874 Q7: the host latches the in-place turn the facing request asked for
        /// (<see cref="BlacksiteStancePolicy.TurnAngle"/>) and replicates it, whatever the stance and
        /// whatever the bound controller: the rest turns and the aimed/crouched turns each read it
        /// from their own base state. The rest-set mirror applies only on a controller that authors
        /// the rest states. The facing error is recorded by <see cref="FaceCombatPoint"/>
        /// on the frame before this one.
        /// </summary>
        private void PublishTurnAngleServer(in BlacksiteStance stance, bool crouched, float normalizedSpeed)
        {
            if (!IsServerAuthority())
                return;
            bool facedInPlace = _facingYawFrame >= Time.frameCount - 1;
            float value = BlacksiteStancePolicy.TurnAngle(_turnAngle.Value, _facingYawError, facedInPlace,
                _restStanceAuthored && BlacksiteStancePolicy.IsRestClipSet(stance, crouched), stance.HandsUnavailable, normalizedSpeed);
            if (_turnAngle.Value != value)
                _turnAngle.Value = value;
        }

        // #874 Q7: the base layer is on, and if blending still going to, a state whose rest/aimed
        // presentation belongs to the stance owner.
        private bool InStanceOwnedState()
        {
            if (!ContainsHash(StanceOwnedStateHashes, _animator.GetCurrentAnimatorStateInfo(0).shortNameHash))
                return false;
            return !_animator.IsInTransition(0)
                || ContainsHash(StanceOwnedStateHashes, _animator.GetNextAnimatorStateInfo(0).shortNameHash);
        }

        private static bool ContainsHash(int[] hashes, int hash)
        {
            for (int i = 0; i < hashes.Length; i++)
                if (hashes[i] == hash) return true;
            return false;
        }

        private static int[] HashStates(string[] names)
        {
            var hashes = new int[names.Length];
            for (int i = 0; i < names.Length; i++)
                hashes[i] = Animator.StringToHash(names[i]);
            return hashes;
        }

        // #315: the aim overlay, look-at and torso pitch run on every peer from the replicated
        // aiming flag and aim point. The server substitutes the live point it publishes
        // (TryGetServerAimPoint) so the host never lags its own shots.
        private void DriveAimPose(bool aiming, float delta)
        {
            if (_aimPose == null)
                return;

            // Only hands on the rifle aim: a climb's are on the rungs, and a member in the air is not aiming
            // (#874 F07: the jump states are outside OverlayStateNames). Resolved locally from replicated state
            // (#874 S2.4), so it holds on a client and on a frame the host's aiming bit still trails.
            bool traversing = _climbingPose || _jumpingPose;
            bool engaged = aiming && !traversing && AllowsCombatLifecycle;
            bool hasPoint = false;
            Vector3 point = Vector3.zero;
            if (engaged)
            {
                if (IsServerAuthority() && _combatController != null && TryGetServerAimPoint(out point))
                {
                    hasPoint = true;
                }
                else
                {
                    point = _aimPoint.Value;
                    hasPoint = point != Vector3.zero;
                }
            }
            if (_weaponVisual != null && _weaponVisual.HasModel && _rigBinding != null)
                _aimPose.BindRifle(_rigBinding.WeaponRoot, _weaponVisual.Model.transform);
            BlacksiteHandOwner owner = CurrentHandOwner;
            BlacksiteWeaponState state = WeaponSnapshot.State; // #1271: only busy hands release the support grip
            _aimPose.SetHandState(owner != BlacksiteHandOwner.Weapon, CarryClipHeld);
            _aimPose.Drive(engaged && !UsesBaton && owner == BlacksiteHandOwner.Weapon, hasPoint, point, delta,
                state == BlacksiteWeaponState.Reloading || state == BlacksiteWeaponState.GrenadeCommitted);
        }

        private void EnsureAimPoseHelper()
        {
            if (_animator == null || _animator.runtimeAnimatorController == null)
                return;

            var helper = _animator.GetComponent<BlacksiteSoldierAimPose>();
            if (helper == null)
                helper = _animator.gameObject.AddComponent<BlacksiteSoldierAimPose>();
            helper.Configure(_animator, transform, _headAnchor);
            _aimPose = helper;
            _strideCalibrated = false;
            foreach (var parameter in _animator.parameters)
                if (parameter.name == "StrideCalibrated") { _strideCalibrated = true; break; }
        }

        private void PublishAimPointServer(bool aiming)
        {
            if (!aiming)
            {
                if (_aimPoint.Value != Vector3.zero)
                    _aimPoint.Value = Vector3.zero;
                return;
            }

            if (Time.time < _nextAimPointSyncTime || _combatController == null
                || !TryGetServerAimPoint(out Vector3 point))
                return;
            if ((point - _aimPoint.Value).sqrMagnitude < AimPointSyncDistance * AimPointSyncDistance)
                return;

            _nextAimPointSyncTime = Time.time + AimPointSyncInterval;
            _aimPoint.Value = point;
        }

        // #1271 S3a: the server's aim point follows whoever turns the body - the Coil-Head while the
        // watch faces it, the combat aim otherwise. Callers hold a combat controller.
        private bool TryGetServerAimPoint(out Vector3 point)
        {
            if (!WatchFaces)
                return _combatController.TryGetAimPoint(out point);
            point = WatchPoint;
            return true;
        }

        private void UpdatePoseFlagsServer(bool forceInactive)
        {
            if (!IsServerAuthority())
                return;

            bool traversalActive = !isEnemyDead && !_spawnState.Value.IsDead && !_boardedShip && !_monsterCaptured;
            _ropeDirection.Value = (byte)(traversalActive && Navigator.IsRappelling ? (Navigator.IsAscendingRope ? 2 : 1) : 0);
            bool dead = forceInactive || isEnemyDead || _spawnState.Value.IsDead;
            bool stunned = _spawnState.Value.IsStunned || stunNormalizedTimer > 0f;
            bool aiming = !dead && !stunned && !HasOccupiedCargoHands && !_climbingPose && _combatController != null
                && ((_watchingMonster && _monsterReaction != null) || (WeaponSnapshot.State != BlacksiteWeaponState.Disabled && _combatController.HasCombatIntent));
            bool crouchEligible = _tacticalRole == BlacksiteSoldierTacticalRole.Support
                || _tacticalRole == BlacksiteSoldierTacticalRole.RearGuard;
            float planarSpeed = GetServerPlanarSpeed();
            bool crouched = _serverCrouched;

            if (dead || stunned || !crouchEligible)
            {
                crouched = false;
            }
            else if (crouched)
            {
                if (planarSpeed > CrouchExitSpeedThreshold)
                {
                    if (float.IsNegativeInfinity(_crouchFastMoveStartedAt))
                        _crouchFastMoveStartedAt = Time.time;
                }
                else
                {
                    _crouchFastMoveStartedAt = float.NegativeInfinity;
                }

                bool minimumHoldComplete = Time.time - _crouchEnteredAt >= CrouchMinimumHoldSeconds;
                bool fastMoveSustained = !float.IsNegativeInfinity(_crouchFastMoveStartedAt)
                    && Time.time - _crouchFastMoveStartedAt >= CrouchFastMoveExitDelaySeconds;
                if (minimumHoldComplete && (!aiming || fastMoveSustained))
                    crouched = false;
            }
            else
            {
                _crouchFastMoveStartedAt = float.NegativeInfinity;
                if (aiming
                    && Time.time >= _crouchReentryAllowedAt
                    && planarSpeed < CrouchEnterSpeedThreshold)
                {
                    crouched = true;
                }
            }

            if (crouched != _serverCrouched)
            {
                _serverCrouched = crouched;
                if (crouched)
                {
                    _crouchEnteredAt = Time.time;
                    _crouchFastMoveStartedAt = float.NegativeInfinity;
                }
                else
                {
                    _crouchReentryAllowedAt = Time.time + CrouchReentryCooldownSeconds;
                    _crouchFastMoveStartedAt = float.NegativeInfinity;
                }
                ApplyAgentMoveSpeed();
            }

            byte poseFlags = 0;
            if (aiming)
                poseFlags |= AimingPoseFlag;
            if (_serverCrouched)
                poseFlags |= CrouchedPoseFlag;
            poseFlags |= (byte)((ResolveServerAnimationStance(dead, stunned) << StancePoseShift) & StancePoseMask);
            if (_boardedShip)
                poseFlags |= BoardedPoseFlag;
            BlacksiteShipCrossingKind? shipCrossing = traversalActive ? _navigator?.ShipCrossingKind : null;
            bool worldClimbing = traversalActive && (_navigator?.IsWorldClimbing ?? false);
            // #874 S3/S5: the rope and the ship ladder publish Climbing, as the world ladder does.
            // A lip drop publishes no pose bit: the drop plays whatever the legs were playing. #1283 S4: the
            // ship ladder only on its rungs; its walked legs keep the rifle (ShipCrossingTakesHands).
            if ((shipCrossing.HasValue && BlacksiteLinkCrossing.ShipCrossingTakesHands(shipCrossing.Value, _navigator.LadderPhase))
                || worldClimbing)
                poseFlags |= ClimbingPoseFlag;
            // #991: its own bit, from the frame the member steps onto an interior link; the jump clip plays only
            // when the crossing lasts JumpClipMinimumCrossingSeconds (ShowsJumpClip). A dead member publishes neither.
            bool jumping = traversalActive && !shipCrossing.HasValue && (_navigator?.IsJumping ?? false);
            if (jumping)
                poseFlags |= JumpingPoseFlag;
            // #874 S5.2 (#873): the kind behind that bit, resolved from the same sources in the
            // same order so the flag and the kind can never disagree on a frame.
            byte traversalKind = (byte)(traversalActive
                ? ResolveServerTraversalKind(shipCrossing, jumping) : BlacksiteTraversalKind.None);
            if (_traversalKind.Value != traversalKind)
                _traversalKind.Value = traversalKind;
            byte ladderPhase = (byte)(traversalActive && _navigator != null ? _navigator.LadderPhase : BlacksiteLadderPhase.None);
            if (_ladderPhase.Value != ladderPhase)
                _ladderPhase.Value = ladderPhase;
            // #874 S5: the clip rate for the whole crossing, jump or rope: clip seconds over crossing seconds.
            bool rope = shipCrossing == BlacksiteShipCrossingKind.RopeDescend || shipCrossing == BlacksiteShipCrossingKind.RopeAscend;
            float crossingPlaybackSpeed = jumping
                ? BlacksiteTraversalPresentation.JumpAnimatorSpeed(_navigator.CrossingSeconds)
                : rope
                    ? BlacksiteTraversalPresentation.CrossingAnimatorSpeed(BlacksiteEquipmentAssets.RopeClipSeconds, _navigator.CrossingSeconds)
                    : 1f;
            if (_crossingPlaybackSpeed.Value != crossingPlaybackSpeed)
                _crossingPlaybackSpeed.Value = crossingPlaybackSpeed;
            if (_poseFlags.Value != poseFlags)
                _poseFlags.Value = poseFlags;
            PublishAimPointServer(aiming);
        }

        // #874 S5.2 (#873), S3: the ship crossing's own kind names the rope's direction and the
        // ladder (which only ever rises); the world ladder carries its direction on the motor's
        // authored points. An interior jump is the only crossing with no ship kind.
        private BlacksiteTraversalKind ResolveServerTraversalKind(BlacksiteShipCrossingKind? shipCrossing, bool jumping)
        {
            if (jumping)
                return _navigator.IsRunningJump ? BlacksiteTraversalKind.JumpRunning : BlacksiteTraversalKind.Jump;
            switch (shipCrossing)
            {
                case BlacksiteShipCrossingKind.RopeAscend: return BlacksiteTraversalKind.RopeAscend;
                case BlacksiteShipCrossingKind.RopeDescend: return BlacksiteTraversalKind.RopeDescend;
                case BlacksiteShipCrossingKind.LadderUp: return BlacksiteTraversalKind.LadderUp;
            }
            if (_navigator?.IsWorldClimbing ?? false)
                return _navigator.IsWorldClimbingUp ? BlacksiteTraversalKind.LadderUp : BlacksiteTraversalKind.LadderDown;
            return BlacksiteTraversalKind.None;
        }

        /// <summary>
        /// #1064 WP3: when this member's weapon last LEFT <see cref="BlacksiteWeaponState.Disabled"/>,
        /// host-side only. It is the clock <see cref="BlacksiteCombatPolicy.ArmedDwellSeconds"/> is
        /// measured from; a member that has never armed this round carries negative infinity, which
        /// the policy reads as "no dwell to hold".
        /// </summary>
        private float _weaponArmedAt = float.NegativeInfinity;

        /// <summary>
        /// #1064 WP3: the one place the hands arbitration is decided, host-side, for the rifle and
        /// the baton alike. Three facts, in order:
        ///
        /// 1. <c>bodyUnavailable</c> - something other than this member's own steering owns the
        ///    body or the lifecycle forbids combat outright. It is ALWAYS immediate: nothing about
        ///    an intent can shoot from inside an elevator cabin or an entrance teleport.
        /// 2. the hand owner. After the WP3 narrowing, <c>Traversal</c> means a leg that needs both
        ///    hands OR the replicated <c>_climbingPose</c>, and only the second can be transient -
        ///    it trails this host's own leg by a frame because it is published through the pose
        ///    flags. <c>Navigator.RequiresBothHands</c> is therefore the immediate half of it.
        /// 3. the armed dwell. A stow that is not immediate may not take a weapon that armed less
        ///    than <see cref="BlacksiteCombatPolicy.ArmedDwellSeconds"/> ago. Arming itself is
        ///    never delayed: a walking member that sights a contact arms on the same think.
        ///
        /// The 20 ms Disabled -> Ready -> Disabled flip the 2026-09-15 D3 capture logged once a
        /// second per member is closed by fact 2 - the narrowing - and by that alone. Fact 3 and
        /// the <c>EngagementWinsHands</c> call below have NO live producer against the current
        /// disable set: every source in it is immediate here, and on the host <c>_climbingPose</c>
        /// is derived from the same Climb/Rappel/world-ladder facts as
        /// <c>Navigator.RequiresBothHands</c> one frame earlier (UpdateAnimation reads the pose
        /// flags this think already wrote), so in the single trailing frame where it claims alone
        /// the weapon is already Disabled and <c>armed</c> is false. Both are kept as the rule a
        /// future non-immediate claim is answered by, pinned in BlacksiteReactiveRegression against
        /// a synthetic claim rather than assumed.
        /// </summary>
        private bool ResolveCombatDisabledServer()
        {
            BlacksiteHandOwner handOwner = CurrentHandOwner;
            bool legNeedsBothHands = Navigator.RequiresBothHands;
            bool bodyUnavailable = !_initialized
                || !AllowsCombatLifecycle
                || IsEntranceTransitionActive()
                || (_interactionCoordinator != null && _interactionCoordinator.IsCommittedTraversal)
                || _spawnState.Value.IsStunned
                || stunNormalizedTimer > 0f;
            bool handsPhysicallyTaken = legNeedsBothHands
                || handOwner == BlacksiteHandOwner.Incapacitated
                || handOwner == BlacksiteHandOwner.Cargo
                || handOwner == BlacksiteHandOwner.Releasing;
            // #1064 WP3 change 3: an Engage decision over a live exact contact outranks a claim on
            // the hands the member could simply drop. It cannot outrank a two-hand leg, cargo, the
            // carry handback, an incapacitated actor or a motor that owns the body - which leaves
            // it with no live producer today (see the remark above); it is the rule a future
            // arbitrable claim is answered by, not a path this build takes.
            bool engagementWins = BlacksiteCombatPolicy.EngagementWinsHands(
                LastThreatIntent,
                IsCombatEngaged,
                handsPhysicallyTaken,
                !bodyUnavailable);
            bool disabled = bodyUnavailable || (handOwner != BlacksiteHandOwner.Weapon && !engagementWins);
            bool armed = WeaponSnapshot.State != BlacksiteWeaponState.Disabled;
            bool immediate = bodyUnavailable || handsPhysicallyTaken;
            if (disabled && !immediate && armed
                && BlacksiteCombatPolicy.HoldsArmedDwell(Time.time, _weaponArmedAt))
            {
                disabled = false;
            }
            // Stamp the dwell on the think that actually arms the weapon, so the floor is measured
            // from the arm rather than from the decision that permitted it.
            if (!disabled && !armed) _weaponArmedAt = Time.time;
            else if (disabled) _weaponArmedAt = float.NegativeInfinity;
            return disabled;
        }

        private float GetServerPlanarSpeed()
        {
            try
            {
                if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
                {
                    Vector3 velocity = _agent.velocity;
                    velocity.y = 0f;
                    return velocity.magnitude;
                }
            }
            catch
            {
                // A NavMeshAgent can become invalid during authored traversal.
            }
            return Mathf.Max(0f, _currentMoveSpeed);
        }

        private void ApplyAgentMoveSpeed() => Navigator.ApplyAgentMoveSpeed();

        private int ResolveAnimationStance()
        {
            // The target fields exist on the server only, so remote peers read the
            // stance the server folded into the replicated pose flags.
            if (!IsServerAuthority())
                return (_poseFlags.Value & StancePoseMask) >> StancePoseShift;

            return ResolveServerAnimationStance(
                isEnemyDead || _spawnState.Value.IsDead,
                _spawnState.Value.IsStunned || stunNormalizedTimer > 0f);
        }

        private int ResolveServerAnimationStance(bool dead, bool stunned)
        {
            if (dead)
                return 3;
            if (stunned)
                return 2;
            if (IsCombatEngaged || IsAware)
                return 1;
            return 0;
        }

        private bool IsActivelyMoving(float speed)
        {
            if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
            {
                if (_agent.isStopped)
                    return false;
                if (!_agent.pathPending && _navigator != null && _navigator.RemainingDistance > Mathf.Max(_agent.stoppingDistance + 0.25f, 0.8f))
                    return true;
            }

            return speed > 0.18f;
        }

        private void UpdateMuzzleLight()
        {
            if (_muzzleLight != null && _muzzleLight.intensity > 0f && Time.time >= _muzzleLightOffTime)
                _muzzleLight.intensity = 0f;

            if (_muzzleFlashRenderer != null && _muzzleFlashRenderer.enabled && Time.time >= _muzzleVisualOffTime)
                _muzzleFlashRenderer.enabled = false;
        }

        private void ResolveAttachmentAnchors()
        {
            if (_attachmentsResolved)
                return;

            if (_rigBinding == null || !_rigBinding.IsValid)
                return;
            _headAnchor = _rigBinding.Head;
            _muzzleAnchor = _rigBinding.Muzzle;
            _driveCarryAnchor = _rigBinding.CarryAnchor;
            _magazineVisual = _rigBinding.Magazine;
            _attachmentsResolved = true;
        }

        private void AttachMuzzleEffects()
        {
            if (_muzzleAnchor == null)
                return;

            Vector3 localOffset = Vector3.zero;
            if (_muzzleLight != null)
            {
                _muzzleLight.transform.SetParent(_muzzleAnchor, false);
                _muzzleLight.transform.localPosition = localOffset;
                _muzzleLight.transform.localRotation = Quaternion.identity;
            }

            if (_muzzleFlashRenderer != null)
            {
                _muzzleFlashRenderer.transform.SetParent(_muzzleAnchor, false);
                _muzzleFlashRenderer.transform.localPosition = localOffset;
                _muzzleFlashRenderer.transform.localRotation = Quaternion.identity;
            }
        }

        private void UpdateHelmetLightPose()
        {
            if (_helmetLight == null)
                return;

            // #1305 F1: the helmet shadow A/B; with the toggle on (default) nothing is written.
            var shadowToggle = ContractsBootstrap.BlacksiteHelmetLightShadows;
            if (shadowToggle != null && !shadowToggle.Value)
            {
                if (_helmetLight.shadows != LightShadows.None)
                {
                    _helmetLight.shadows = LightShadows.None;
                    _helmetLightShadowsForcedOff = true;
                }
            }
            else if (_helmetLightShadowsForcedOff)
            {
                _helmetLight.shadows = _helmetLightBaseShadows;
                _helmetLightShadowsForcedOff = false;
            }

            var clock = TimeOfDay.Instance;
            var level = StartOfRound.Instance?.currentLevel;
            float day = clock != null && clock.totalTime > 0f ? clock.currentDayTime / clock.totalTime : 0f;
            _helmetLight.enabled = BlacksiteHelmetLightPolicy.Enabled(!isEnemyDead, isOutside,
                level != null && level.planetHasTime, day, _helmetLight.enabled);
            if (isEnemyDead || _helmetLightBaseIntensity <= 0f)
                return;

            // #813: HDRP renders from Light.intensity (the lumen value the prefab authored is
            // already converted into it), so scaling the Light is enough and needs no HDRP
            // reference. Role and isOutside are both replicated, so every peer lands on the
            // same power.
            float factor = BlacksiteHelmetLightPolicy.GetPowerFactor(_tacticalRole, isOutside);
            float target = _helmetLightBaseIntensity * factor;
            if (!Mathf.Approximately(_helmetLight.intensity, target))
                _helmetLight.intensity = target;
        }

        private void EnsureScanNode()
        {
            Transform existing = transform.Find("BlacksiteSoldierScanNode");
            GameObject scanObject = existing != null
                ? existing.gameObject
                : new GameObject("BlacksiteSoldierScanNode");
            scanObject.transform.SetParent(transform, false);
            scanObject.transform.localPosition = new Vector3(0f, 1.45f, 0f);
            scanObject.layer = ScanNodeLayer;

            SphereCollider collider = scanObject.GetComponent<SphereCollider>();
            if (collider == null)
                collider = scanObject.AddComponent<SphereCollider>();
            collider.radius = 0.95f;
            collider.isTrigger = true;

            _scanNode = scanObject.GetComponent<ScanNodeProperties>();
            if (_scanNode == null)
                _scanNode = scanObject.AddComponent<ScanNodeProperties>();

            _scanNode.headerText = "BLACKSITE SOLDIER";
            _scanNode.subText = GetScanSubText();
            _scanNode.maxRange = 32;
            _scanNode.minRange = 1;
            _scanNode.requiresLineOfSight = true;
            _scanNode.nodeType = 1;
            _scanNode.scrapValue = 0;
        }

        private string GetScanSubText() => string.Empty;

        private bool _playerResponseInterrupt;
        private int _approachOrderFrame = -1;
        // #1293 R3: the facing the executed order asks for and when; the brain's SquadOrder
        // continuation re-asks it every frame, since a single ask lapses on a body still settling.
        private enum OrderFacing : byte { None, Dwell, Response, Arrival }
        private OrderFacing _orderFacing;
        private BlacksiteMemberOrder _orderFacingOrder;

        /// <summary>
        /// #1013 A6: true when this order left the member under way - it produced a movement
        /// request that was Issued or Unchanged (the goal it asked for is the one the legs already
        /// have), or it handed motion to a traversal motor. False on every other path: a dropped
        /// request, a refused route, and the paths that deliberately make no request at
        /// all (the deployment or stimulus hold, a boarded member).
        ///
        /// It is the order's MOTION verdict, not "did this behaviour consume the decision"; the two
        /// are not the same question and the behaviour loop's `handled` is not gated on it in this
        /// stage. See the note at BlacksiteSoldierAI.Behaviour.cs for why.
        /// </summary>
        private bool ExecuteSquadOrder(
            BlacksiteSquadDirector director,
            BlacksiteMemberOrder memberOrder)
        {
            // #1015 S1 (review Y3c): the idle clear used to run unconditionally at the top of every
            // execution, which reset _holdingSince on every think and meant the old
            // HoldWithoutOrder's four-second re-site could never arm. The idle state is cleared by
            // an order that actually moved the legs, and by nothing else.
            // #1020 S6: and it is cleared by its own name - the ClearHoldWithoutOrder forwarder
            // that stood between this line and ClearIdleAction is deleted.
            var motion = Navigator.Motion;
            long verdictsBefore = motion.VerdictSeq;
            bool handled = ExecuteSquadOrderCore(director, memberOrder);
            if (motion.VerdictSeq != verdictsBefore
                && (motion.LastOutcome == BlacksiteMotionOutcome.Moving
                    || motion.LastOutcome == BlacksiteMotionOutcome.Arrived))
                ClearIdleAction();
            return handled;
        }

        private bool ExecuteSquadOrderCore(
            BlacksiteSquadDirector director,
            BlacksiteMemberOrder memberOrder)
        {
            // #874 S1.2: one owner of movement. The order's reach contract is (re-)applied here on
            // every execution and released the moment an order without one is issued, so no
            // interaction, hold or cancellation can leave a stale approach behind. The frame stamp
            // tells CommitBehaviour that this decision did execute an order.
            _approachOrderFrame = Time.frameCount;
            _orderFacing = OrderFacing.None;
            // #874 S8.2: the squad's chosen door, cached for the survivor exit and the jester flight.
            SyncEntranceFromDirector(director);
            if (!memberOrder.HasDutyKind || memberOrder.DutyKind != BlacksiteJobKind.Guard)
                Navigator.EndPatrolAlert();
            // #1219 E2: the executed order's declared pace is what the speed reads (CurrentPace).
            if (_orderPace != memberOrder.Pace)
            {
                _orderPace = memberOrder.Pace;
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("Member",
                        $"member={_index}, pace={_orderPace}, current={CurrentPace}, speed={BlacksitePatrolPolicy.AgentSpeed(CurrentPace):0.00}.");
            }
            // #874 S4.2: the order's own tolerance is the third value of the navigator's arrival
            // contract. Recording it here, beside the approach profile, keeps one owner for all
            // three: the duty board, the stall classifier and the motion monitor read the navigator.
            Navigator.RecordOrderArrivalTolerance(memberOrder.ArrivalTolerance);
            if (memberOrder.Approach.HasValue) Navigator.ApplyApproachProfile(memberOrder.Approach);
            else Navigator.ReleaseApproachProfile();
            if (memberOrder.IsPlayerResponse && !_playerResponseInterrupt)
                _interactionCoordinator?.Interrupt("reported player contact");
            _playerResponseInterrupt = memberOrder.IsPlayerResponse;
            BlacksiteSquadOrderKind kind = memberOrder.SquadOrder.Kind;
            if (BlacksiteDiagnostics.Enabled
                && (!_diagnosticOrderInitialized
                    || _lastDiagnosticOrderKind != kind
                    || _lastDiagnosticFormation != memberOrder.SquadOrder.Formation
                    || _lastDiagnosticInteractionOperator != memberOrder.IsInteractionOperator))
            {
                _diagnosticOrderInitialized = true;
                _lastDiagnosticOrderKind = kind;
                _lastDiagnosticFormation = memberOrder.SquadOrder.Formation;
                _lastDiagnosticInteractionOperator = memberOrder.IsInteractionOperator;
                BlacksiteDiagnostics.Log(
                    "Member",
                    $"member={_index}, role={_tacticalRole}, slot={FormationSlot}, order={kind}, formation={memberOrder.SquadOrder.Formation}, operator={memberOrder.IsInteractionOperator}, destination={memberOrder.Destination}.");
            }
            if (memberOrder.IsPlayerResponse)
            {
                // #1305 G2 (R16): toward the observed player; a contact in the other world is a goal
                // there, walked from outside along the landing corridor to its end, and the route
                // crosses. This must not set the extraction/boarding latch or sample a hidden player.
                _extractingToShip = false;
                bool across = memberOrder.TargetOutside != isOutside;
                var response = isOutside && across
                    ? Navigator.SetCrossingDestination(memberOrder.Destination, memberOrder.IssuerTag)
                    : SetDestinationThrottled(memberOrder.Destination, issuer: memberOrder.IssuerTag);
                if (!across) SetOrderFacing(OrderFacing.Response, memberOrder);
                return response.MotionUnderway();
            }
            if (kind == BlacksiteSquadOrderKind.Extract
                || kind == BlacksiteSquadOrderKind.Board)
            {
                // -> the extraction route reports its own verdict, path by path.
                return UpdateExtractionRoute(director, memberOrder);
            }

            _extractingToShip = false;
            // #874 S4: a dwell (the deployment stagger, a stimulus flinch, a door pause, a combat
            // action or a ladder queue) holds the member; the order faces and waits it out.
            if (Navigator.InDwell)
            {
                SetOrderFacing(OrderFacing.Dwell, memberOrder);
                // #1015 S1 (review Y3a): `handled` means "this behaviour owns this tick", not "the
                // legs moved". The order IS holding this member, on purpose; reporting false would
                // drop it into HoldWithoutOrder, which is the opposite of what the hold asked for.
                return true;
            }

            // #874 P-S2: a Follow is walked by the navigator's Follow request, whose goal is
            // re-derived from the live anchor on every path refresh. #1305 G2 (R16): an anchor in the
            // other world ends the follow at a plain goal there, and the route crosses.
            if (TryGetMission(out BlacksiteRetrievalMissionController followMission)
                && followMission.Objectives != null
                && followMission.Objectives.Planner.TryGetFollowOrder(NetworkObjectId, out var follow))
                return Navigator.RequestFollow(follow.Follow, memberOrder.IssuerTag).MotionUnderway();

            // #1305 G2 (R16): heading into the facility the order walks the corridor; a goal inside
            // (BlacksiteSquadPlanner.GoalOutside) walks it to its end and the route takes the doorway.
            if (director.MemberRequiresInteriorTraversal(this))
            {
                bool goalOutside = BlacksiteObjectiveManifestController.BlacksiteSquadPlanner.GoalOutside(memberOrder, out bool known);
                return (known && goalOutside != isOutside
                    ? Navigator.SetCrossingDestination(memberOrder.Destination, memberOrder.IssuerTag)
                    : Navigator.SetDestinationThrottled(memberOrder.Destination, groundRouteOutbound: true,
                        issuer: memberOrder.IssuerTag)).MotionUnderway();
            }

            if ((kind == BlacksiteSquadOrderKind.OverrideObjective
                    || kind == BlacksiteSquadOrderKind.InspectObjective
                    || kind == BlacksiteSquadOrderKind.ApproachObjective)
                && memberOrder.IsInteractionOperator
                && TryGetMission(out BlacksiteRetrievalMissionController mission)
                && _interactionCoordinator != null
                && _interactionCoordinator.TryGetMissionObjectiveDestination(
                    mission,
                    out Vector3 objectiveDestination))
            {
                var objective = SetDestinationThrottled(objectiveDestination, issuer: memberOrder.IssuerTag);
                return objective.MotionUnderway();
            }

            var ordered = SetDestinationThrottled(memberOrder.Destination, issuer: memberOrder.IssuerTag);
            SetOrderFacing(OrderFacing.Arrival, memberOrder);
            return ordered.MotionUnderway();
        }

        private void SetOrderFacing(OrderFacing facing, in BlacksiteMemberOrder order)
        {
            _orderFacing = facing;
            _orderFacingOrder = order;
            ContinueOrderFacing();
        }

        // #1293 R3: the committed order's facing, on its decision and every frame after it.
        internal void ContinueOrderFacing()
        {
            switch (_orderFacing)
            {
                case OrderFacing.Dwell:
                    if (Navigator.InDwell) FacePoint(_orderFacingOrder.PreferredFacing);
                    break;
                case OrderFacing.Response:
                    if (HorizontalDistance(transform.position, _orderFacingOrder.Destination) <= _orderFacingOrder.ArrivalTolerance)
                        FacePoint(_orderFacingOrder.PreferredFacing);
                    break;
                case OrderFacing.Arrival:
                    if (HorizontalDistance(transform.position, _orderFacingOrder.Destination) > _orderFacingOrder.ArrivalTolerance)
                    {
                        _hasPatrolLook = false;
                        break;
                    }
                    // #874 (2026-09-25): a patrol leg's arrival look is the one deliberate turn
                    // (PatrolLookPoint), not a snap to the outward the order carries.
                    FacePoint(_orderFacingOrder.Approach.RequiresFacing
                        ? _orderFacingOrder.Approach.FacingPoint
                        : _orderFacingOrder.HasDutyKind && _orderFacingOrder.DutyKind == BlacksiteJobKind.Guard && IsShipGuard && isOutside
                            ? PatrolLookPoint(_orderFacingOrder.Destination, _orderFacingOrder.PreferredFacing)
                            : _orderFacingOrder.PreferredFacing);
                    break;
            }
        }

        /// <summary>
        /// #1013 A6: the same motion verdict as <see cref="ExecuteSquadOrder"/>, which returns this
        /// value unchanged for an Extract or Board order.
        /// </summary>
        private bool UpdateExtractionRoute(
            BlacksiteSquadDirector director,
            BlacksiteMemberOrder memberOrder)
        {
            // #1015 S1 (review Y3a): aboard already. There is nothing to walk, and a boarded member
            // that reported false fell through to HoldWithoutOrder and walked back off the ship.
            if (_boardedShip)
                return true;

            _extractingToShip = true;
            // #874 Q1: the executor walks the PLANNER's goal in both worlds and never a point of its
            // own: the Board order's passage link start (#1219 C2) or an Extract order's delivery
            // point. #1305 G2 (R16): from inside the route crosses on the way; the corridor below is
            // an outside walk only.
            Vector3 destination = memberOrder.Destination;
            var walk = Navigator.SetDestinationThrottled(destination, groundRouteOutbound: false, issuer: memberOrder.IssuerTag);
            // #1219 C2: an Extract order outside is a delivery walk (the witness escort's deck point,
            // a carrier's delivery point); it boards nobody. A Board order boards by the board
            // crossing from the link start: arrived is path-true (C1) and on the link start's storey,
            // so a member on the deck above the rope foot or the ladder mount is not there.
            if (memberOrder.SquadOrder.Kind != BlacksiteSquadOrderKind.Board
                || !Navigator.HasArrived(destination)
                || Mathf.Abs(transform.position.y - destination.y) > BlacksiteNavigator.VerticalReboundMetres)
            {
                _boardingRefusal = _boardingRefusalReported = null;
                return walk.MotionUnderway();
            }

            // -> the director refused the boarding this tick; the walk above still stands.
            if (!director.NotifyMemberBoarded(this, out string refusal))
            {
                NoteBoardingRefusal(refusal);
                return walk.MotionUnderway(); // not-a-wait: ends on every living member aboard or on every unboarded member stranded (no route, no session)
            }

            _boardingRefusal = _boardingRefusalReported = null;
            // -> the board crossing is asked for: the motor tick begins it (or it waits its FIFO turn
            // in the ship-queue dwell here); its completion is the boarding (CompleteBoardCrossingServer).
            if (TryGetMission(out var mission) && mission.Objectives != null)
                Navigator.TryBoardShip(mission.Objectives.Ship);
            return true;
        }

        /// <summary>
        /// #1219 C2: host-only. The completed board crossing - the one place a member boards. First
        /// the custody event (the manifest loads what this member carries up: his witness, his
        /// prop), then the boarding gate; accepted, he is aboard on his reserved seat, refused, he
        /// stands on the deck unboarded and his Board goal walks him off and back up.
        /// <see cref="CancelExtractionToRecoverDrive"/> stays the un-board.
        /// </summary>
        internal void CompleteBoardCrossingServer()
        {
            if (!IsServerAuthority() || isEnemyDead || _boardedShip || !TryGetMission(out var mission)) return;
            mission.Objectives?.LoadCarriedByBoardCrossingServer(this);
            var director = mission.SquadDirector;
            if (director == null) return;
            if (!director.NotifyMemberBoarded(this, out string refusal))
            {
                NoteBoardingRefusal(refusal);
                return;
            }
            _boardingRefusal = _boardingRefusalReported = null;
            _boardedShip = true;
            // #1271 S8: a plain request, so the lock and cap let one or two of six boardings speak.
            AnnounceBoarded();
            Navigator.ClearPath();
            // #874 S2.4: the retained contact goes with the body. A boarded actor that kept one
            // reported Ready combat over a target its aim layer was forbidden to face.
            _combatController?.HoldFireForTravel();
            // Boarding is only tracked server-side, so the replicated pose flags carry it to
            // every peer; the local apply keeps the host frame-accurate.
            if (_poseFlags.Value != (byte)(_poseFlags.Value | BoardedPoseFlag))
                _poseFlags.Value |= BoardedPoseFlag;
            ApplyBoardedVisualState(true);
        }

        // #1219 A3: one line per change of reason, rate-limited per member on the squad-motion
        // cadence; a change caught by the limit is printed on the next refused tick past it.
        private void NoteBoardingRefusal(string refusal)
        {
            _boardingRefusal = refusal;
            _boardingRefusalDecision = Brain.Decisions;
            if (refusal == _boardingRefusalReported || Time.time < _nextBoardingRefusalLog) return;
            _boardingRefusalReported = refusal;
            _nextBoardingRefusalLog = Time.time + BlacksiteObjectivePolicy.SquadMoveSampleSeconds;
            BlacksiteDiagnostics.Log("Extraction", $"member={MemberIndex}, boarding refused, reason={refusal}");
        }

        private bool IsEntranceTransitionActive()
        {
            return (_interactionCoordinator != null
                    && _interactionCoordinator.ActiveKind == BlacksiteInteractionKind.Entrance)
                || (_entranceSettleLogPending && Time.time < _entranceSettleUntil);
        }

        /// <summary>
        /// #874 S8.2: members keep their start-of-mission copies of the door and compare the
        /// director's generation counter on every order they execute, so the director's republished
        /// door moves the whole squad without turning every field read into a director lookup.
        /// </summary>
        private void SyncEntranceFromDirector(BlacksiteSquadDirector director)
        {
            if (director == null || _entranceGeneration == director.EntranceGeneration)
                return;
            _entranceGeneration = director.EntranceGeneration;
            _outsideMainEntrance = director.OutsideMainEntrance;
            _insideMainEntrance = director.InsideMainEntrance;
            _chosenEntranceId = director.ChosenEntranceId;
        }

        private void BeginEntranceSettlePause(string reason)
        {
            PauseForDoorInteraction(EntrancePostTeleportSettleSeconds);
            _entranceSettleUntil = Time.time + EntrancePostTeleportSettleSeconds;
            _entranceSettleLogPending = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Soldier {_index} entrance settle start. reason={reason}, hold={EntrancePostTeleportSettleSeconds:0.00}s, position={transform.position}.");
        }

        private void MaybeLogEntranceSettleComplete()
        {
            if (!_entranceSettleLogPending || Time.time < _entranceSettleUntil)
                return;

            _entranceSettleLogPending = false;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Soldier {_index} entrance settle end. position={transform.position}.");
        }

        // #957 S12A U4: every present caller of FacePoint keeps the 0.5 s
        // FacingRequestHoldSeconds. The overload below exists for the idle facing sweep, which is
        // the only caller that holds a look for longer than a think.
        private void FacePoint(Vector3 point) => FacePoint(point, FacingRequestHoldSeconds);

        private void FacePoint(Vector3 point, float holdSeconds)
        {
            Vector3 direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
                return;

            // Facing is a request, not an assignment: UpdateFacing turns towards it at a
            // bounded rate so think-rate callers cannot snap the soldier around.
            _facingTarget = point;
            _hasFacingTarget = true;
            _facingTargetExpiry = Time.time + Mathf.Max(0.1f, holdSeconds);
            _facingHoldExtended = false;
        }

        private void UpdateFacing()
        {
            if ((_combatController != null && _combatController.HasCombatIntent) || WatchFaces)
            {
                // Combat aim owns rotation while engaged, and so does a watcher's Coil-Head
                // (#1271 S3a, WatchFaces); drop the stimulus request so a stale facing target
                // cannot resume once the engagement or the watch ends.
                _hasFacingTarget = false;
                // #957 S12A U4: a combat intent is a real order. The sweep ends with it and never
                // resumes on its own; the duty board arms a new one if the member goes idle again.
                EndFacingSweep();
                return;
            }

            // #957 S12A U4: the idle facing sweep. #874 S4: it turns the head only; a member at its
            // goal is arrived and nothing holds its body.
            if (_facingSweeping)
            {
                if (FacingSweepInterrupted())
                    EndFacingSweep();
                else if (!_hasFacingTarget || Time.time >= _facingTargetExpiry)
                    RequestFacingSweepTarget(_facingSweepIndex + _facingSweepStride);
            }

            if (!_hasFacingTarget)
                return;

            if (Time.time >= _facingTargetExpiry
                || IsActivelyMoving(_currentMoveSpeed))
            {
                _hasFacingTarget = false;
                return;
            }

            // #874 (2026-09-25): BlacksiteStancePolicy.IdleFacingMode. A moving body - chiefly one the
            // rule above keeps because its agent is stopped while a crossing motor carries the transform (an
            // entrance member walks as an agent; arrival at the threshold point fires the paired teleport) - turns at once
            // at 240 deg/s as before; a standing one first settles for IdleTurnSettleSeconds (so the
            // damped Speed the rest turns are authored on is down too) and then turns at the stance's
            // idle rate, 68 deg/s on a lowered rifle.
            bool moving = _stationarySince < 0f;
            float stillSeconds = moving ? 0f : Time.time - _stationarySince;
            bool restClipSet = !UsesBaton && _restStanceAuthored && _hasStance
                && _stance.Reason == BlacksiteStanceReason.Rest && (_poseFlags.Value & CrouchedPoseFlag) == 0;
            BlacksiteIdleFacingMode mode = BlacksiteStancePolicy.IdleFacingMode(moving, stillSeconds, restClipSet, out float rate);
            // #874: a one-shot request (the 0.5 s stimulus look) is kept alive until a settling or
            // rest-rate turn can finish (BlacksiteStancePolicy.FacingHoldFor). The extension is applied
            // ONCE per request, from the first evaluation: the yaw error only shrinks as the body turns,
            // so the first estimate covers the whole turn, and a finished look then expires on time.
            if (!_facingHoldExtended && (mode == BlacksiteIdleFacingMode.Settle || rate < FacingTurnDegreesPerSecond))
            {
                _facingHoldExtended = true;
                Vector3 flat = _facingTarget - transform.position;
                flat.y = 0f;
                float yawError = flat.sqrMagnitude > .01f
                    ? Vector3.SignedAngle(Vector3.ProjectOnPlane(transform.forward, Vector3.up), flat, Vector3.up)
                    : 0f;
                _facingTargetExpiry = Mathf.Max(_facingTargetExpiry, Time.time
                    + BlacksiteStancePolicy.FacingHoldFor(BlacksiteStancePolicy.IdleTurnSettleSeconds - stillSeconds, yawError, rate));
            }
            if (mode == BlacksiteIdleFacingMode.Settle)
                return;
            FaceCombatPoint(_facingTarget, rate);
        }

        // #874 (2026-09-25): the patrol look-around point, latched on the first arrived think of a
        // patrol leg and kept while the member stays on that waypoint.
        private bool _hasPatrolLook;
        private Vector3 _patrolLookPoint, _patrolLookFor, _patrolLookFrom;

        /// <summary>
        /// #874 (2026-09-25): where a patrolling guard looks on arriving at
        /// <paramref name="waypoint"/>: the one turn <see cref="BlacksiteStancePolicy.PatrolLookYaw"/>
        /// sizes from the body's arrival heading and the outward (<paramref name="outwardPoint"/>),
        /// at the outward's own distance. Latched, so a think-rate caller does not turn again from
        /// the heading the turn produced; the squad-order arrival and the idle sweep both ask here.
        /// </summary>
        internal Vector3 PatrolLookPoint(Vector3 waypoint, Vector3 outwardPoint)
        {
            if (_hasPatrolLook
                && HorizontalDistance(_patrolLookFor, waypoint) <= BlacksiteArrivalPolicy.MinDestinationChange
                && HorizontalDistance(_patrolLookFrom, transform.position) <= BlacksiteArrivalPolicy.MinDestinationChange)
                return _patrolLookPoint;
            Vector3 outward = outwardPoint - waypoint; outward.y = 0f;
            Vector3 forward = transform.forward; forward.y = 0f;
            if (outward.sqrMagnitude < .0001f || forward.sqrMagnitude < .0001f)
                return outwardPoint;
            float yaw = BlacksiteStancePolicy.PatrolLookYaw(Vector3.SignedAngle(forward, outward, Vector3.up));
            _patrolLookPoint = transform.position + Quaternion.Euler(0f, yaw, 0f) * forward.normalized * outward.magnitude;
            _patrolLookFor = waypoint; _patrolLookFrom = transform.position;
            _hasPatrolLook = true;
            return _patrolLookPoint;
        }

        // -----------------------------------------------------------------------------------
        // #957 S12A U4: purposeful idle.
        //
        // An idle soldier used to re-arm after four seconds of stillness and take a coverage or
        // frontier step that any micro-step reset, which read as fidgeting: Lawson's verdict on
        // the 2026-09-10 March run was "a bunch of useless steps every 1-3 s". It now stands at its
        // goal and sweeps its facing through the doorways and corners the room graph published
        // for the room it is standing in, holding each for a seeded 3 to 6 s, and issues no
        // destination at all while it does. Movement and facing only: the vanilla-variant Aim
        // layer has one state (docs/assets/09-blacksite-soldier-aim-layer-rifle-grip-and-locomotion.md), so a scan or lean clip is a later
        // Stage 15 item with its own probe.
        //
        // #874 S4: the sweep turns the head only; it no longer stops the agent. A sweeping member
        // standing at its goal reads stallOwner=arrived on [UAT][Motion], which is correct: the
        // member is standing still on purpose, and the duty board's own budget is still the clock
        // that escalates it.
        // -----------------------------------------------------------------------------------
        internal const byte FacingSweepDoorwayKind = 0;
        internal const byte FacingSweepCornerKind = 1;
        internal const byte FacingSweepEntranceKind = 2;
        private Vector3[] _facingSweepTargets = Array.Empty<Vector3>();
        private byte[] _facingSweepKinds = Array.Empty<byte>();
        private int _facingSweepCount;
        private int _facingSweepIndex;
        private int _facingSweepStride;
        private bool _facingSweeping;
        // What the navigator had been told to walk to when the sweep began. The only destination
        // the issue gate lets through while a member is idle is one worth walking to, so a change
        // here IS the real order the sweep ends on.
        private Vector3 _facingSweepIssue;
        private bool _facingSweepHadIssue;

        internal bool IsFacingSweeping => _facingSweeping;
        internal int FacingSweepIndex => _facingSweeping ? _facingSweepIndex : -1;
        internal int FacingSweepCount => _facingSweeping ? _facingSweepCount : 0;

        /// <summary>
        /// The closed value the duty board prints as <c>benefit=sweep:&lt;kind&gt;</c>.
        /// </summary>
        internal string FacingSweepTargetToken
        {
            get
            {
                if (!_facingSweeping || _facingSweepCount <= 0) return BlacksiteDiagnosticFormat.None;
                switch (_facingSweepKinds[_facingSweepIndex])
                {
                    case FacingSweepEntranceKind: return "entrance";
                    case FacingSweepCornerKind: return "corner";
                    default: return "doorway";
                }
            }
        }

        /// <summary>
        /// Arms (or keeps) the idle facing sweep over <paramref name="targets"/>. The duty board
        /// builds the list from the room graph; this owns the hold, the per-target clock and the
        /// seeded starting index.
        /// </summary>
        internal void BeginFacingSweep(IReadOnlyList<Vector3> targets, IReadOnlyList<byte> kinds)
        {
            int count = targets != null ? targets.Count : 0;
            if (count <= 0 || kinds == null || kinds.Count != count)
            {
                EndFacingSweep();
                return;
            }

            if (_facingSweeping && SameFacingSweepTargets(targets))
            {
                // The board reviews an idle choice every four seconds and the room has not
                // changed, so this is the same sweep. Restarting it here would re-arm the current
                // target and the member would never look anywhere else.
                return;
            }

            if (_facingSweepTargets.Length < count)
            {
                _facingSweepTargets = new Vector3[count];
                _facingSweepKinds = new byte[count];
            }
            for (int i = 0; i < count; i++)
            {
                _facingSweepTargets[i] = targets[i];
                _facingSweepKinds[i] = kinds[i];
            }
            _facingSweepCount = count;
            // The synchronisation guard: six members standing in one room start on six different
            // targets, so they do not all look at the same doorway together.
            _facingSweepIndex = ((MemberIndex % count) + count) % count;
            _facingSweepStride = BlacksiteRetrievalTuning.GetFacingSweepStride(AnimationSeed, count);
            _facingSweeping = true;
            _facingSweepHadIssue = Navigator.HasIssuedDestination;
            _facingSweepIssue = Navigator.IssuedDestination;
            RequestFacingSweepTarget(_facingSweepIndex);
        }

        /// <summary>
        /// Ends the sweep.
        /// </summary>
        internal void EndFacingSweep()
        {
            if (!_facingSweeping) return;
            _facingSweeping = false;
            _facingSweepCount = 0;
            _hasFacingTarget = false;
        }

        private bool SameFacingSweepTargets(IReadOnlyList<Vector3> targets)
        {
            if (targets.Count != _facingSweepCount) return false;
            for (int i = 0; i < _facingSweepCount; i++)
                if (Vector3.SqrMagnitude(targets[i] - _facingSweepTargets[i]) > 0.0025f)
                    return false;
            return true;
        }

        private bool FacingSweepInterrupted()
        {
            // Something is carrying this body: a recovery, a passage, an elevator or a real leg.
            if (IsActivelyMoving(_currentMoveSpeed)) return true;
            if (!Navigator.HasIssuedDestination) return false;
            if (!_facingSweepHadIssue) return true;
            // The issue gate drops anything inside BlacksiteArrivalPolicy.MinDestinationChange of
            // the point already issued, so this is exactly the set of destinations that reached
            // the agent since the sweep began.
            return Vector3.Distance(Navigator.IssuedDestination, _facingSweepIssue)
                > BlacksiteArrivalPolicy.MinDestinationChange;
        }

        private void RequestFacingSweepTarget(int index)
        {
            // A coprime stride visits every target exactly once per cycle, so stepping by the
            // stride to skip a target the member is standing on can never run out of targets.
            for (int attempt = 0; attempt < _facingSweepCount; attempt++)
            {
                int at = ((index + attempt * _facingSweepStride) % _facingSweepCount + _facingSweepCount)
                    % _facingSweepCount;
                Vector3 flat = _facingSweepTargets[at] - transform.position;
                flat.y = 0f;
                // A target the member is standing on is not a look; skip to the next one rather
                // than burn a hold facing nowhere.
                if (flat.sqrMagnitude <= 0.0001f) continue;
                _facingSweepIndex = at;
                FacePoint(_facingSweepTargets[at],
                    BlacksiteRetrievalTuning.GetFacingSweepHoldSeconds(AnimationSeed, at));
                return;
            }
            EndFacingSweep();
        }

        private void BeginInteriorSweepAfterEntrance(string reason)
        {
            // #1066 U8: clearing the facility threshold is one of the two facts that can never
            // happen to a squad that is going nowhere, so it is what re-earns the right to claim the
            // sector is secure. Idempotent by the flag below - this only runs on the transition.
            if (!HasEnteredFacility)
                _squadDirector?.NoteSquadProgress();
            HasEnteredFacility = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Soldier {_index} joined the squad interior route after entrance use. reason={reason}, position={transform.position}.");
        }

        internal void NotifyTeleported()
        {
            Navigator.NotifyTeleported();
            _strollRawMoveX = _strollRawMoveZ = 0f;
            _hasFacingTarget = false;
            _lastPosition = transform.position;
            _lastAnimationSampleTime = Time.time;
            _lastAnimationYaw = transform.eulerAngles.y;
            _smoothedPlanarVelocity = Vector3.zero;
            _currentMoveSpeed = 0f;
            TrySetFloat(MoveSpeedHash, 0f);
            TrySetFloat(SpeedHash, 0f);
            TrySetFloat(PlanarVelocityXHash, 0f);
            TrySetFloat(PlanarVelocityYHash, 0f);
            TrySetFloat(MoveXHash, 0f);
            TrySetFloat(MoveZHash, 0f);
            TrySetFloat(TurnRateHash, 0f);
            TrySetFloat(TurnAngleHash, 0f);
        }

        /// <summary>
        /// #874 F05: a completed world transfer is ONE lifecycle event, and this is it.
        ///
        /// <see cref="NotifyTeleported"/> and the navigator's half of it already settle the BODY -
        /// the motion sample, the leases a motor held, the ground route and the no-route run. What
        /// they never settled is everything that was DECIDED for the body while it stood in the
        /// other world, and the 2026-09-21 round 3 log is what that costs: member 2 crossed into
        /// the facility at t=903.39 holding an assignment planned outside, executed it at t=904.04,
        /// reserved the same doorway outward at t=904.35, left at t=905.10 and came back in at
        /// t=908.18 (log 36369-36694).
        ///
        /// The four owners are settled here, before anything can make the next movement request,
        /// and each keeps its own existing invalidation - there is no new planner, no new cache and
        /// no goal invented for the new world:
        /// <list type="number">
        /// <item>everything the navigator decided in the old world, in one call
        /// (<c>BlacksiteNavigator.NoteWorldChanged</c>): its last destination, the motion owner's
        /// live goal, and the destination the agent is still carrying. It runs FIRST, before
        /// <c>BeginEntranceSettlePause</c> below raises its 0.2 s interaction dwell. Before S4 that
        /// pause was a Door lease, and taking a lease copied the live goal into a suspended slot -
        /// Gale "Issue 874 Full11" member 0 restored the exterior goal 0.2 s after crossing for
        /// exactly that reason (t=2052.64-2053.04);</item>
        /// <item>the interaction coordinator's standing request, which its own task completion,
        /// interrupt and restarts would otherwise re-aim the smart path at, together
        /// with the smart-path TASK itself, whose ready result was planned from the other world
        /// and answers the same requested destination
        /// (<c>BlacksiteInteractionCoordinator.NoteWorldTransferred</c>);</item>
        /// <item>the planner's APPROACH, dropped while its INTENT is kept
        /// (<c>BlacksiteSquadPlanner.NoteMemberCrossedWorld</c>), so <c>TryGetOrder</c> answers no
        /// order until the next plan (#1305 S);</item>
        /// <item>the brain's cached duty resolve and executor verdicts, through the existing
        /// "decide now" - a request, never an interrupt, so a committed traversal may take one.</item>
        /// </list>
        ///
        /// <paramref name="worldChanged"/> is false for an authored internal teleport, which moves
        /// the body a long way inside ONE world: the request and the approach it is carrying are
        /// still for the world it is in, and dropping them would abandon a goal the smart path
        /// chose that very teleport to reach. Its body half is identical, which is why both
        /// transfers end here.
        /// </summary>
        private void CompleteWorldTransferServer(string reason, bool worldChanged)
        {
            Navigator.InvalidateDestination(clearPosition: true);
            if (worldChanged)
            {
                // #874: the navigator's decisions go first - the settle pause at the bottom of
                // this method takes a lease, and a lease suspends whatever goal it finds.
                Navigator.NoteWorldChanged();
                _interactionCoordinator?.NoteWorldTransferred();
                // A stranded member is deliberately not reached: TryGetMission refuses while the
                // transport is lost, and the planner has already retired its duty.
                if (TryGetMission(out BlacksiteRetrievalMissionController transferMission)
                    && transferMission.Objectives != null)
                {
                    transferMission.Objectives.Planner.NoteMemberCrossedWorld(this, reason);
                }
            }
            RequestDecision(BlacksiteDecisionCause.Transfer);
            BeginEntranceSettlePause(reason);
        }

        private bool TryUseBestEntranceTeleport(Vector3 reference, bool isEntranceToBuilding, int entranceId, string reason)
        {
            EntranceTeleport best = BlacksiteEntranceInteraction.FindBestEntrance(
                reference,
                isEntranceToBuilding,
                entranceId,
                allowAnyId: entranceId >= 0);
            return TryUseSmartEntranceTeleport(best, reason);
        }

        internal bool TryUseSmartEntranceTeleport(EntranceTeleport entrance, string reason)
        {
            if (entrance == null || _interactionCoordinator == null)
                return false;
            Vector3 finalDestination = entrance.isEntranceToBuilding
                ? _insideMainEntrance
                : _outsideFireExit;
            return _interactionCoordinator.TryBeginEntrance(
                entrance,
                finalDestination,
                reason);
        }

        // #1150: no silent false. Every refusal names itself in <paramref name="refusal"/>, and the
        // entrance interaction puts it on the task so the [UAT][Interaction] reason= line carries
        // it; the 2026-09-19 Museum run failed 37 transfers with reason=none.
        // #1233 S1 (rule 3): a coupled cart and its pusher cross as one transaction, and the world is
        // published only after both bodies moved. #1293 R2: an escort crosses like any member; the
        // witness rides on his transform.
        internal bool TransferThroughEntranceServer(EntranceTeleport entrance, string reason, out string refusal)
        {
            if (!IsServerAuthority()) { refusal = "not the server authority"; return false; }
            if (entrance == null) { refusal = "entrance is null"; return false; }

            try
            {
                if (!BlacksiteEntranceInteraction.TryResolveExitEndpoint(entrance, out EntranceTeleport exit, out Vector3 target, out refusal))
                    return false;
                Vector3 exitPosition = GetEntrancePoint(exit);
                Vector3 forward = BlacksiteEntranceInteraction.ExitForward(exit);
                var payload = Y4NGZCompany.Contracts.Payload.PayloadContract.ActiveCart;
                bool pushing = payload != null && payload.IsPushedBy(this);
                Quaternion exitFacing = Quaternion.LookRotation(Vector3.ProjectOnPlane(forward, Vector3.up).normalized);
                // #1219 B1: the one rule for which world a doorway leads to. #874 F05: read BEFORE the
                // flip; a member already on the far side re-using a paired doorway changes no world.
                bool outsideAfterUse = BlacksiteWorld.AfterUsing(entrance);
                bool worldChanged = outsideAfterUse != isOutside;
                // A pair lands two bodies at this doorway, so it takes the wider endpoint sample.
                if (pushing) target = ResolveNavMeshPosition(exitPosition + forward * .8f, 1f);
                if (pushing)
                {
                    // The coupling moves both bodies or neither (PayloadCartController.TryCrossPortalServer).
                    if (!payload.TryCrossPortalServer(this, entrance.entranceId, outsideAfterUse, target, exitFacing, out refusal))
                        return false;
                }
                // #1293 R2: the agent binds at the exit endpoint or nothing moves; the refusal is the
                // entrance task's own failure.
                else if (!Navigator.TryTransferTo(target))
                {
                    refusal = $"the agent cannot bind at the exit endpoint {target:F2}";
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.Blacksite] Soldier {_index} refused paired entrance " +
                        $"{entrance.entranceId} for {reason}: {refusal}.");
                    return false;
                }
                SetOutsideState(outsideAfterUse);
                Navigator.SetRotation(exitFacing);
                // #874 P-S2: the door this member cleared into the other world, read by whoever is
                // following it (never the objective's origin). Only a real world change counts.
                if (worldChanged && TryGetMission(out BlacksiteRetrievalMissionController crossingMission)
                    && crossingMission.Objectives != null)
                    crossingMission.Objectives.Planner.NoteMemberCrossing(this, GetEntrancePoint(entrance),
                        entrance.entranceId, fromOutside: !outsideAfterUse);
                CompleteWorldTransferServer(reason, worldChanged);
                if (!outsideAfterUse)
                {
                    BeginInteriorSweepAfterEntrance(reason);
                }
                else
                {
                    RequestSquadVoice(BlacksiteVoiceEvent.TacticalAction, volume: 0.7f);
                }
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Soldier {_index} traversed paired entrance " +
                    $"{entrance.entranceId} one-at-a-time for {reason}; " +
                    $"outside={outsideAfterUse}, endpoint={target}.");
                if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
                    return true;
                refusal = $"crossed, but the agent did not bind to the NavMesh at {target:F2}";
                return false;
            }
            catch (Exception e)
            {
                refusal = "exception: " + e.Message;
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Blacksite] Paired entrance traversal failed for {reason}: {e.Message}");
                return false;
            }
        }

        private void SetOutsideState(bool outside)
        {
            try
            {
                SetEnemyOutside(outside);
            }
            catch
            {
                isOutside = outside;
            }

            isOutside = outside;
            if (RoundManager.Instance != null)
            {
                GameObject[] nodes = outside ? RoundManager.Instance.outsideAINodes : RoundManager.Instance.insideAINodes;
                if (nodes != null && nodes.Length > 0)
                    allAINodes = nodes;
            }
        }

        private static Vector3 GetEntrancePoint(EntranceTeleport entrance)
        {
            if (entrance == null)
                return Vector3.zero;
            return entrance.entrancePoint != null ? entrance.entrancePoint.position : entrance.transform.position;
        }

        private void EngagePlayerTarget(PlayerControllerB target)
        {
            if (target == null || target.isPlayerDead || !target.isPlayerControlled)
                return;
            EnsureCombatController();
            _combatController.EngagePlayer(target);
        }

        private void EngageEnemyTarget(EnemyAI target)
        {
            if (target == null || target.isEnemyDead)
                return;
            EnsureCombatController();
            _combatController.EngageEnemy(target);
        }

        internal void PauseForDoorInteraction(float seconds) => Navigator.PauseForDoorInteraction(seconds);

        internal void HoldForCombatAction(float seconds) => Navigator.HoldForCombatAction(seconds);

        internal void SetCombatDestination(Vector3 destination) => Navigator.SetCombatDestination(destination);

        internal void ReleaseCombatApproach() => _navigator?.ReleaseCombatApproachProfile();

        // #1219 F2: the combat controller re-applies the agent speed on the edges of a burst, so
        // BlacksiteCombatPolicy.FiringWalkSpeed binds for exactly the burst; and it traces the
        // member's own planar speed on every round it releases.
        internal void RefreshAgentMoveSpeed() => ApplyAgentMoveSpeed();
        internal float CombatPlanarSpeed => GetServerPlanarSpeed();

        // #874 Q7: the facing error at the moment a turn is asked for, before this frame's step, is
        // what the in-place turn presents (PublishTurnAngleServer). The navigator's own traversal facing
        // (ladder, jump, passage) calls Navigator.FaceCombatPoint directly and never lands here.
        internal void FaceCombatPoint(Vector3 point, float degreesPerSecond)
        {
            Vector3 flat = point - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude > .01f)
            {
                _facingYawError = Vector3.SignedAngle(Vector3.ProjectOnPlane(transform.forward, Vector3.up), flat, Vector3.up);
                _facingYawFrame = Time.frameCount;
            }
            Navigator.FaceCombatPoint(point, degreesPerSecond);
        }

        // #314 suggestion 1: combat positioning is bound to the member order the awareness gate
        // produced -- never the raw squad order -- so an unaware member fighting alone keeps his
        // objective slot while aware members cluster around the Contact formation.
        private void PublishCombatFormationSlot()
        {
            if (TryGetSquadDirector(out BlacksiteSquadDirector director)
                && director.TryGetMemberOrder(this, out BlacksiteMemberOrder order)
                && (order.SquadOrder.Kind == BlacksiteSquadOrderKind.Contact
                    || order.SquadOrder.Kind == BlacksiteSquadOrderKind.SearchLastKnown))
            {
                _combatController.SetFormationSlot(order.Destination);
                return;
            }

            _combatController.ClearFormationSlot();
        }

        internal void StopForWorldInteraction(float seconds)
        {
            PauseForDoorInteraction(seconds);
        }

        internal void FaceWorldInteraction(Vector3 point)
        {
            FacePoint(point);
        }

        internal void PlayWorldInteractionGesture()
        {
            // Item interaction uses its carry/release pose, never the full-body Signal.
        }

        internal void OverrideWorldInteractionStoppingDistance(float distance) => Navigator.OverrideWorldInteractionStoppingDistance(distance);

        // #874 S1.2: an action's reach contract, applied and released by whoever issues the order.
        internal void ApplyApproachProfile(BlacksiteApproachProfile profile) => Navigator.ApplyApproachProfile(profile);
        internal void ReleaseApproachProfile() => Navigator.ReleaseApproachProfile();
        // Dwell may not accumulate while the actor is still travelling, nor before he is turned
        // toward the thing he is about to operate.
        internal bool IsSettledForInteraction => _agent == null || !_agent.enabled || !_agent.isOnNavMesh
            || _agent.velocity.magnitude <= .35f;
        internal bool IsFacing(Vector3 point, float degrees)
        {
            Vector3 flat = point - transform.position;
            flat.y = 0f;
            return flat.sqrMagnitude <= .0001f
                || Vector3.Angle(Vector3.ProjectOnPlane(transform.forward, Vector3.up), flat) <= degrees;
        }

        // #874 S4: a dwell is in force (name kept for the native fixtures).
        internal bool IsWorldInteractionHoldActive => Navigator.IsWorldInteractionHoldActive;

        internal void RestoreWorldInteractionNavigation() => Navigator.RestoreWorldInteractionNavigation();

        internal void RequestImmediateDestinationRefresh() => Navigator.RequestImmediateDestinationRefresh();

        internal void NotifyAttackedByEnemy(EnemyAI enemy)
        {
            if (!IsServerAuthority()
                || enemy == null
                || enemy == this
                || enemy is BlacksiteSoldierAI
                || enemy.isEnemyDead
                || isEnemyDead)
                return;

            // #948 S12 U3: latch source 2. A sapsucker that has hit one of ours is a threat to
            // fight, not a hazard to walk around, and this runs for every monster hit already. It
            // is deliberately the FIRST thing after the guards: a member that is being killed must
            // never be route-refused away from the bird that is killing it.
            BlacksiteKiwiThreatState.NotifyAttacked(enemy);

            // #1293 R1: the hit is first-hand: the attacker's row is Heard where it stands, sight or
            // not; the perception pass's eyes make it Saw.
            NoteHeardServer(enemy.NetworkObjectId, enemy, BlacksiteWorldClass.Monster, enemy.transform.position, enemy.isOutside);

            // #874 WP F: a class the squad's weapons cannot hurt is a THREAT, not a target. The hit
            // is still known - its row is written above and the brain re-decides - but it does not
            // become the rifle's immediate attacker, because that would hand it to the converge
            // order, the draw and the withdrawal's "still fighting" gate (TryGetLiveMonsterContact)
            // as a fight to finish.
            // #874 N8 (decision 7): and it CLOSES the squad's warning fire at it - every rifle of
            // this squad now evades it as the batons do - with this member's reaction recomputed at
            // once (#1305 H, C4), so the member that was hit starts walking away on its next frame.
            if (BlacksiteThreatAdapterRegistry.EngagementRefusedFor(enemy))
            {
                BlacksiteThreatAdapterRegistry.NoteRefusedContactAttack(enemy, this);
                ObserveMonsterReactionServer();
                RequestDecision(BlacksiteDecisionCause.Stimulus);
                return;
            }

            RequestDecision(BlacksiteDecisionCause.Stimulus);
            EnsureCombatController();
            _combatController.NotifyImmediateAttacker(enemy);
        }

        // #874 S7.2/S7.3: how close a known threat is, and the only perception input the behaviour
        // selector gets. Deliberately NOT TryFindCombatTarget, which stamps the carrier's
        // acquisition latch. #1293 R1: read from the squad table - the nearest player row and the
        // row of the monster contact this member holds, each only while some member's eyes have it
        // now (row.InSight): a point a contact left seconds ago is not a threat at hand.
        // #1233 S6 (rule 5): that monster counts only while the contact (live or ordered) passes
        // ContactEngageable. A sighting of a body the weapon cannot hit, or of a creature that
        // shows no threat, is knowledge: it scores nothing here.
        internal BlacksiteThreatProximity ThreatProximity()
        {
            if (!TryGetWorld(out BlacksiteWorldModel world))
                return BlacksiteThreatProximity.None;
            Vector3 here = transform.position;
            float nearest = float.MaxValue;
            var rows = world.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                BlacksiteWorldRow row = rows[i];
                if (row.Class == BlacksiteWorldClass.Player && row.InSight)
                    nearest = Mathf.Min(nearest, Vector3.Distance(here, row.Position));
            }
            EnemyAI contact = CombatEnemyTarget;
            if (contact != null && world.TryGet(contact.NetworkObjectId, out BlacksiteWorldRow held)
                && held.InSight && ContactEngageable(contact))
                nearest = Mathf.Min(nearest, Vector3.Distance(here, held.Position));
            // #1305 H (C4): a protected carrier fighting a contact keeps it at melee reach out to
            // CarrierHoldReach, so a contact standing on the gate neither flips him between Combat and
            // his job nor flickers the Frame band event; the state is his own selection.
            if (nearest <= BlacksiteCombatPolicy.BatonAttackRange
                || (nearest <= BlacksiteCombatPolicy.CarrierHoldReach
                    && _behaviourSelector.Current == BlacksiteBehaviour.Combat && IsObjectiveCarrier))
                return BlacksiteThreatProximity.MeleeReach;
            return nearest <= BlacksiteCombatPolicy.ThreatRadius
                ? BlacksiteThreatProximity.ThreatRadius
                : BlacksiteThreatProximity.None;
        }

        // #988 B3: the squad's monster order, as this member holds it. `_monsterOrderEnemy` is the
        // contact a squadmate inside ThreatRadius reported, refreshed by every further report and
        // expiring on its own so a dead or departed contact stops ordering without a release
        // message. It is knowledge plus an instruction to close; it applies no damage rule of its
        // own and it never makes a member fire-eligible on something it cannot see.
        private EnemyAI _monsterOrderEnemy;
        private float _monsterOrderUntil;
        // #988 B3: when this member last took damage. DamageDirection is the stimulus kind the squad
        // memory files a hit under (BlacksiteSquadDirector.ReportStimulus), and this is the same
        // event measured locally, because the behaviour loop needs it without a squad round trip.
        private float _lastDamageTakenAt = float.NegativeInfinity;
        internal bool HasRecentDamage => Time.time - _lastDamageTakenAt < BlacksiteCombatPolicy.DamageCombatLockSeconds;
        // #988 round 2: is this member already under an order about THIS contact? A refresh of a
        // held order is free and must not be throttled; only reconsidering one is.
        internal bool HasMonsterAlertOrderFor(EnemyAI enemy)
            => enemy != null && HasMonsterAlertOrder && ReferenceEquals(enemy, _monsterOrderEnemy);
        internal bool HasMonsterAlertOrder => _monsterOrderEnemy != null
            && !_monsterOrderEnemy.isEnemyDead
            && _monsterOrderEnemy.isActiveAndEnabled
            && Time.time <= _monsterOrderUntil;
        // #988 B1: the contact this member is actually fighting, live target first and its squad
        // order second. The baton sweep prefers it when several candidates share one swept
        // capsule.
        private EnemyAI CombatEnemyTarget
        {
            get
            {
                var live = _combatController?.CurrentEnemyTarget;
                if (live != null && !live.isEnemyDead) return live;
                return HasMonsterAlertOrder ? _monsterOrderEnemy : null;
            }
        }

        /// <summary>
        /// #988 B3: a monster alert inside <see cref="BlacksiteCombatPolicy.ThreatRadius"/> is an
        /// ORDER. The squad director raises it on every living member inside
        /// <see cref="BlacksiteCombatPolicy.MonsterAlertOrderRadius"/> of the contact; this is what
        /// a member does with it. #1293 R1: the contact is already a row of the squad table - a
        /// member's eyes raised the order - so the order adds the identity the behaviour selector's
        /// threat input and the baton's draw-on-alert read, and asks for an immediate decision.
        ///
        /// It deliberately does NOT force a behaviour. The brain's rule still owns the outcome,
        /// which is how the carrier rule survives: a protected carrier's ObjectiveJob precedes
        /// Combat under a ThreatRadius contact, so an ordered carrier keeps walking the cargo out
        /// and only fights when the contact reaches melee reach.
        /// </summary>
        internal void ReceiveMonsterAlertOrder(EnemyAI enemy)
        {
            if (!IsServerAuthority() || isEnemyDead || enemy == null || enemy.isEnemyDead) return;
            if (enemy is BlacksiteSoldierAI || !AllowsCombatLifecycle) return;
            // #874 WP F / #1262 P4: an order is an instruction to CLOSE, so it is refused for a contact
            // THIS member's weapon cannot stop (a baton member is never ordered onto a keeper). The
            // knowledge half is not lost: the contact's row stays in the squad table.
            if (!BlacksiteThreatAdapterRegistry.IsAttackableBy(enemy, this)) return;
            // #988 round 2, SHOULD-FIX 3: an order ARBITRATES rather than overwriting. Two reporters
            // fighting two monsters used to make a third raider walk X, Y, X on consecutive thinks,
            // because the last writer won every time. A held order keeps the member unless the new
            // contact is meaningfully closer, or the held one is dead, gone or has lapsed - the same
            // shape as the target commitment S10.1 gives combat selection, measured against the
            // member's own body because that is what it is being ordered to walk to.
            if (!ReferenceEquals(enemy, _monsterOrderEnemy) && HasMonsterAlertOrder
                && !BlacksiteCombatPolicy.MonsterAlertOrderReplaces(
                    Vector3.Distance(transform.position, _monsterOrderEnemy.transform.position),
                    Vector3.Distance(transform.position, enemy.transform.position)))
            {
                // The held order is refreshed by a report about the SAME contact only; a refused
                // rival must not extend it.
                return;
            }
            bool fresh = !HasMonsterAlertOrder;
            _monsterOrderEnemy = enemy;
            _monsterOrderUntil = Time.time + BlacksiteCombatPolicy.MonsterAlertOrderSeconds;
            EnsureCombatController();
            if (fresh) RequestDecision(BlacksiteDecisionCause.Stimulus);
        }

        // #988 B3/B5: the live monster this member is fighting or has been ordered onto, and how far
        // away it is. One accessor, so the withdrawal gate and the converge order ask one question.
        internal bool TryGetLiveMonsterContact(out EnemyAI enemy, out float distance)
        {
            enemy = CombatEnemyTarget;
            if (enemy == null || enemy.isEnemyDead || !enemy.isActiveAndEnabled)
            { enemy = null; distance = float.PositiveInfinity; return false; }
            distance = Vector3.Distance(transform.position, enemy.transform.position);
            return true;
        }

        /// <summary>
        /// #1011 A3 (observed failure O3): the monster holding a squadmate within
        /// <see cref="BlacksiteCombatPolicy.RescueRadius"/>, for a member carrying the tool that
        /// ends a capture. One connected baton hit stuns the captor and
        /// <c>BlacksiteVanillaMonsterPatches</c> ends the capture on the hurt reaction, so the
        /// baton is a full rescue and the rifle - which is why this is baton-only - is not.
        /// #874 Plan F: true only for the ONE member elected as that capture's committed rescuer
        /// (<see cref="BlacksiteVanillaMonsters.TryGetRescueAssignment"/>), so the Rescue intent,
        /// the captor acquisition and the rescue target priority all name the same member.
        /// </summary>
        internal bool TryGetRescueContact(out BlacksiteSoldierAI victim, out EnemyAI captor)
        {
            victim = null; captor = null;
            return UsesBaton && BlacksiteVanillaMonsters.TryGetRescueAssignment(
                this, BlacksiteCombatPolicy.RescueRadius, out victim, out captor);
        }

        // #874 Plan F: the rescuer election's question about a candidate. The weapons gate stays
        // private under its own name (fixtures reflect on it); this is its one outside door.
        // #1233 S6 (rule 5): and the engagement gate - a captor with no body this member's baton
        // can hit is no rescue for this member. A capture is evidence by itself (Hunts), so the
        // second half is never what refuses a rescuer here.
        internal bool CanConsiderRescueCaptor(EnemyAI captor) => CanConsiderEnemy(captor) && ContactEngageable(captor);

        // #1011 A3: whether this enemy is holding a squadmate of this member's - the question the
        // rifle asks before it fires, and the one the baton close-in asks for its ring. It is
        // NOT gated on the baton: a rifleman never fires at a monster attached to a squadmate
        // whatever he is carrying, which is the decision Lawson took for #1011.
        internal bool IsAllyAttachedContact(EnemyAI enemy)
        {
            if (enemy == null) return false;
            BlacksiteSoldierAI victim = BlacksiteVanillaMonsters.CaptureVictim(enemy);
            return victim != null && !ReferenceEquals(victim, this);
        }

        // #1011 A3: whether this enemy is THE rescue contact this baton member has chosen, which is
        // the narrower question the target priority asks.
        internal bool IsRescueTarget(EnemyAI enemy)
            => enemy != null && TryGetRescueContact(out _, out EnemyAI captor) && ReferenceEquals(captor, enemy);

        // #874 G2, #1293 R3: the last acquisition, for the per-frame cargo-threat test; re-run only
        // when the squad table's ChangeCount moved (-1: no table; int.MinValue: never).
        private int _acquiredChangeCount = int.MinValue;
        private PlayerControllerB _acquiredPlayer;
        private EnemyAI _acquiredEnemy;

        /// <summary>
        /// #1293 R1: combat target selection reads the squad table and nothing else. A candidate is
        /// a row some member's EYES wrote (<see cref="BlacksiteWorldSource.Saw"/>): a Heard or Told
        /// row never makes a member fire-eligible (#314). It must be an eligible player or an
        /// attackable, engageable monster in this member's world (or within 6 m), inside his engage
        /// range: <see cref="MaxAggroRange"/>, and for a ship guard his rifle's
        /// <see cref="BlacksiteCombatController.MaximumShotRange"/> from where he stands - the 12 m
        /// post bounds where he walks, never what he may shoot. Priority is unchanged: the rescue
        /// captor, then a confirmed drive carrier, then the nearest player unless a lethal monster is
        /// inside <see cref="BlacksiteCombatPolicy.MonsterPriorityRange"/>, and an Old Bird under
        /// open warning fire only when nothing attackable is held. At equal distance a row this
        /// member sees himself wins.
        /// </summary>
        private bool TryFindCombatTarget(out PlayerControllerB playerTarget, out EnemyAI enemyTarget)
        {
            playerTarget = null;
            enemyTarget = null;
            // #1011 A3 (O3): the rescue is decided ahead of the table. A flea riding a squadmate's
            // head is the one contact the ray to it - through the carrier - never clears, and the
            // capture registry already names it.
            if (TryGetRescueContact(out _, out EnemyAI rescueCaptor))
                enemyTarget = rescueCaptor;
            else if (TryGetWorld(out BlacksiteWorldModel world))
            {
                Vector3 here = transform.position;
                int slot = PerceptionSlot;
                float range = IsShipGuard ? BlacksiteCombatController.MaximumShotRange : MaxAggroRange;
                PlayerControllerB bestPlayer = null;
                EnemyAI bestEnemy = null, warning = null;
                float playerDistance = range, enemyDistance = range, warningDistance = range;
                bool playerOwn = false, enemyOwn = false, warningOwn = false;
                var rows = world.Rows;
                for (int i = 0; i < rows.Count; i++)
                {
                    BlacksiteWorldRow row = rows[i];
                    if (row.Source != BlacksiteWorldSource.Saw) continue;
                    float distance = Vector3.Distance(here, row.Position);
                    if (distance > range) continue;
                    bool own = row.SeenByMember(slot);
                    if (row.Class == BlacksiteWorldClass.Player)
                    {
                        if (row.Subject is PlayerControllerB player && Nearer(distance, own, playerDistance, playerOwn)
                            && CanConsiderPlayer(player))
                        { bestPlayer = player; playerDistance = distance; playerOwn = own; }
                    }
                    else if (row.Class == BlacksiteWorldClass.Monster && row.Subject is EnemyAI enemy && CanConsiderEnemy(enemy))
                    {
                        // #874 N8: a class the squad's weapons cannot hurt passes the weapons gate only
                        // under this rifle's open warning fire, and is taken only when nothing
                        // attackable is held. #1233 S6 (rule 5): an attackable contact must also pass
                        // the engagement gate.
                        if (BlacksiteThreatAdapterRegistry.EngagementRefusedFor(enemy))
                        {
                            if (Nearer(distance, own, warningDistance, warningOwn))
                            { warning = enemy; warningDistance = distance; warningOwn = own; }
                        }
                        else if (Nearer(distance, own, enemyDistance, enemyOwn) && ContactEngageable(enemy))
                        { bestEnemy = enemy; enemyDistance = distance; enemyOwn = own; }
                    }
                }
                if (bestEnemy == null) bestEnemy = warning;
                bool confirmedDriveCarrier = bestPlayer != null && IsConfirmedDriveCarrier(bestPlayer);
                // #874 N8: an Old Bird taken only for warning fire is never the close lethal monster
                // that outranks a player.
                bool closeLethalMonster = bestEnemy != null
                    && !BlacksiteThreatAdapterRegistry.EngagementRefusedFor(bestEnemy)
                    && Vector3.Distance(here, bestEnemy.transform.position) <= BlacksiteCombatPolicy.MonsterPriorityRange;
                if (confirmedDriveCarrier || bestPlayer != null && !closeLethalMonster)
                    playerTarget = bestPlayer;
                else
                    enemyTarget = bestEnemy;
            }
            _acquiredChangeCount = WorldChangeCount;
            _acquiredPlayer = playerTarget;
            _acquiredEnemy = enemyTarget;
            return playerTarget != null || enemyTarget != null;
        }

        // Nearer, or as near and seen by this member's own eyes when the best so far is not.
        private static bool Nearer(float distance, bool own, float best, bool bestOwn)
            => distance < best || (distance == best && own && !bestOwn);

        // The squad table's ChangeCount, or -1 for a member with no table.
        private int WorldChangeCount => TryGetWorld(out BlacksiteWorldModel world) ? world.ChangeCount : -1;

        /// <summary>
        /// #874 G2: a protected carrier's threat sensing and cargo-threat latch (normally its only
        /// acquisition). #1293 R3: re-run only on a squad-table change; the distance to the held threat
        /// is per frame. #1233 S6 (rule 5): only an engageable contact arms it, never knowledge alone.
        /// </summary>
        private void TickCargoThreatServer()
        {
            if (!IsObjectiveCarrier) return;
            float now = Time.time;
            if (WorldChangeCount != _acquiredChangeCount)
                TryFindCombatTarget(out _, out _);
            if (!HasRetainedCargoThreat(now)) return;
            ArmCargoThreatServer(now, "immediate threat; free hands before combat");
        }

        // #1293 R2: a threat or a hit arms the latch and frees a carrier's hands for defence. A
        // witness escort keeps his hold: custody is not in his hands, and only his death or a stun
        // ends it.
        private void ArmCargoThreatServer(float now, string reason)
        {
            _cargoThreatUntil = now + BlacksiteObjectivePolicy.CargoThreatLatchSeconds;
            if (!IsEscortingWitness)
                DropCarriedDriveServer(reason);
        }

        // The held acquisition stands until the table changes; eligibility and distance are per frame.
        private bool HasRetainedCargoThreat(float now)
        {
            Vector3 here = transform.position;
            return RetainedPlayerArmsCargo(_acquiredPlayer, here, now, now)
                || RetainedEnemyArmsCargo(_acquiredEnemy, here, now, now)
                || RetainedEnemyArmsCargo(_combatController?.CurrentEnemyTarget, here, now, now);
        }

        private static bool RetainedPlayerArmsCargo(PlayerControllerB player, Vector3 here, float retainedUntil, float now)
            => PlayerKnowledgeIneligibility(player) == null
                && BlacksiteObjectivePolicy.RetainedThreatArmsCargo(Vector3.Distance(here, player.transform.position), retainedUntil, now);

        private bool RetainedEnemyArmsCargo(EnemyAI enemy, Vector3 here, float retainedUntil, float now)
            => ContactEngageable(enemy)
                && BlacksiteObjectivePolicy.RetainedThreatArmsCargo(Vector3.Distance(here, enemy.transform.position), retainedUntil, now);

        internal bool IsPlayerKnowledgeEligible(PlayerControllerB player)
        {
            return PlayerKnowledgeIneligibility(player) == null;
        }
        internal static string PlayerKnowledgeIneligibility(PlayerControllerB player)
        {
            if (player == null) return "destroyed or disconnected";
            if (player.isPlayerDead) return "dead";
            if (!player.isPlayerControlled) return "not controlled/disconnected";
            if (!player.isActiveAndEnabled) return "inactive";
            if (MobIgnoreBridge.IsIgnored(player.actualClientId)) return "debug ignore enabled";
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && !player.IsSpawned) return "network despawned";
            return null;
        }

        // #1293 R1: the 12 m leash is no target gate: a ship guard sees and shoots past it, and only
        // his movement keeps the post (AllowsPlayerResponse, the planner, the controller's OffGuardPost).
        internal bool IsPlayerCombatEligible(PlayerControllerB player)
        {
            return IsPlayerKnowledgeEligible(player) && PlayerIsTargetable(player);
        }

        // The ship guard's POST: where he may walk to answer a player.
        internal bool AllowsPlayerResponse(Vector3 observedPosition, bool outside) => !IsShipGuard
            || (TryGetMission(out var mission) && BlacksiteDeploymentPolicy.CanGuardPursue(outside,
                Vector3.Distance(observedPosition, mission.Objectives.Ship.RendezvousPosition)));

        // #874 S7.1b, #1293 R3: "decide now", raised by every event source on a TRANSITION of its fact:
        // marks the brain dirty and (planner causes) the planner's set. Server, live, configured only.
        internal void RequestDecision(BlacksiteDecisionCause cause)
        {
            if (!IsServerAuthority()
                || isEnemyDead
                || !_spawnState.Value.IsConfigured
                || _spawnState.Value.IsDead)
            {
                return;
            }
            Brain.Invalidate(cause);
            MarkPlannerFor(cause);
        }

        // #1293 R3: the planner's half of a decision event, also raised by the brain for the premises
        // it observes itself: every cause but Wake, Exclusive, Order and None marks this member.
        internal void MarkPlannerFor(BlacksiteDecisionCause cause)
        {
            if (cause != BlacksiteDecisionCause.Wake && cause != BlacksiteDecisionCause.Exclusive
                && cause != BlacksiteDecisionCause.Order && cause != BlacksiteDecisionCause.None
                && TryGetMission(out BlacksiteRetrievalMissionController mission) && mission.Objectives != null)
                mission.Objectives.Planner.MarkDirty(NetworkObjectId);
        }

        // #874 S7.5: the pre-visual monster alert. BlacksiteMonsterCombatAdapterRegistry and
        // BlacksiteVanillaMonsters.Acquire (#1262 P4) acquire only with a line of effect, so a passive acquire is itself evidence
        // the monster can reach this member. In the 2026-09-09 run the crawler was announced at
        // t=309.3 and 7.9 m; member 0's own exact monster visual did not fire until 311.9, 0.7 s
        // AFTER the first bite, and its 1.233 s baton draw began 0.4 s before it died.
        //
        // #1293 R1: an adapter acquire is not a sighting anybody made, so it writes a Told row of the
        // squad table, never Saw: the squad knows where the monster is and nobody becomes
        // fire-eligible on it. What it buys is a turn, a drawn baton and a decision on this frame
        // instead of the next one.
        // `distance` is the registry's measured acquire distance, kept on the signature so the hook
        // can grow a range term without the registry changing again.
        internal void NoteAdapterAcquireServer(EnemyAI enemy, float distance)
        {
            if (!IsServerAuthority()
                || enemy == null
                || enemy == this
                || enemy is BlacksiteSoldierAI
                || enemy.isEnemyDead
                || isEnemyDead
                || !_spawnState.Value.IsConfigured)
            {
                return;
            }

            Vector3 acquirePoint = enemy.transform.position;
            if (TryGetWorld(out BlacksiteWorldModel world))
                NoteWorldChange(world, enemy.NetworkObjectId, world.Told(enemy.NetworkObjectId, enemy,
                    BlacksiteWorldClass.Monster, acquirePoint, enemy.isOutside, NetworkObjectId, Time.time));
            // Same guard InterruptForStimulusServer uses: a committed traversal keeps its facing.
            // No PauseForStimulus either - this warns the member, it does not stop it.
            if (_interactionCoordinator == null || !_interactionCoordinator.IsCommittedTraversal)
                FacePoint(acquirePoint);
            // ReadyBaton's only effects are the draw itself and the contact stamp that keeps the
            // baton out for four seconds; it needs no combat target and no lifecycle flag. A rifle
            // guard is not a passive-acquire soldier under the Stage 7 crew decision, and its Ready
            // state already exists, so there is no rifle branch here.
            // #874 WP F / #1262 P4: only for a contact this member's baton can stop.
            if (UsesBaton && !HandsUnavailable && BlacksiteThreatAdapterRegistry.IsAttackableBy(enemy, this))
                ReadyBaton();
            RequestDecision(BlacksiteDecisionCause.Stimulus);
        }


        private bool CanConsiderPlayer(PlayerControllerB player)
        {
            if (!IsPlayerCombatEligible(player))
                return false;
            // #1219 B1: the world term is BlacksiteWorld's; the 6 m horizontal close-acquire
            // tolerance is this perception gate's own term, unchanged.
            return BlacksiteWorld.Same(player, this)
                || HorizontalDistance(player.transform.position, transform.position) <= 6f;
        }

        // NaN means "this branch never needed the view dot"; the diagnostic wrapper measures it.
        // `rays` is the first body sample that cleared, or `none`. #1293 R1: `tracked` is this
        // member's own sight bit on the player's row and `threshold` is the pass's view cone.
        private bool EvaluatePlayerSight(PlayerControllerB player, float distance, bool tracked, float threshold,
            out float dot, out string rays)
        {
            dot = float.NaN;
            rays = BlacksiteSightPolicy.RayNone;
            // #874 S10.2 P2: release hysteresis. A contact is still ACQUIRED at VisionRange; the
            // extra metre is only spent on a player this member already has in sight, so nothing
            // detects further than it did before.
            if (!BlacksiteSightPolicy.InRange(distance, tracked))
                return false;

            if (!HasLineOfSight(player, out rays))
                return false;
            if (distance <= ProximitySenseRange)
                return true;

            Vector3 toPlayer = GetPlayerAimPoint(player) - GetEyePosition();
            dot = Vector3.Dot(transform.forward, toPlayer.normalized);
            return dot >= threshold;
        }

        private float MeasureViewDot(Vector3 point)
        {
            Vector3 toPoint = point - GetEyePosition();
            return toPoint.sqrMagnitude > .0001f ? Vector3.Dot(transform.forward, toPoint.normalized) : 1f;
        }

        private bool CanHearPlayer(PlayerControllerB player, float distance)
        {
            float range = EstimatePlayerNoiseRange(player, distance);
            if (range <= 0f || distance > range)
                return false;

            return true;
        }

        private float EstimatePlayerNoiseRange(PlayerControllerB player, float distance)
        {
            float speed = GetPlayerHorizontalSpeed(player);
            float range = 0f;
            if (distance <= VoiceNoiseRange && PlayerIsSpeaking(player))
                range = Mathf.Max(range, VoiceNoiseRange);

            if (speed < 0.55f)
                return range;

            if (player.isCrouching)
                return Mathf.Max(range, CrouchNoiseRange);
            if (player.isSprinting || speed > 4.8f)
                return Mathf.Max(range, SprintNoiseRange);
            return Mathf.Max(range, WalkNoiseRange);
        }

        private static float GetPlayerHorizontalSpeed(PlayerControllerB player)
        {
            if (player == null)
                return 0f;

            if (player.thisController != null)
            {
                Vector3 velocity = player.thisController.velocity;
                velocity.y = 0f;
                return velocity.magnitude;
            }

            ulong id = player.actualClientId;
            Vector3 current = player.transform.position;
            if (!LastPlayerPositions.TryGetValue(id, out Vector3 previous))
            {
                LastPlayerPositions[id] = current;
                return 0f;
            }

            LastPlayerPositions[id] = current;
            Vector3 delta = current - previous;
            delta.y = 0f;
            return delta.magnitude / Mathf.Max(Time.deltaTime, 0.001f);
        }

        private static bool PlayerIsSpeaking(PlayerControllerB player)
        {
            if (player == null)
                return false;

            try
            {
                if (!_voicePlayerStateFieldResolved)
                {
                    _voicePlayerStateField = typeof(PlayerControllerB).GetField(
                        "voicePlayerState",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _voicePlayerStateFieldResolved = true;
                }
                object state = _voicePlayerStateField?.GetValue(player);
                if (state == null)
                    return false;

                Type type = state.GetType();
                if (_voiceSpeakingStateType != type)
                    ResolveVoiceSpeakingAccessor(type);
                if (_voiceSpeakingAccessor != null)
                    return _voiceSpeakingAccessor(state);
                if (_voiceSpeakingProperty != null)
                    return (bool)_voiceSpeakingProperty.GetValue(state);
                if (_voiceSpeakingField != null)
                    return (bool)_voiceSpeakingField.GetValue(state);
                return false;
            }
            catch
            {
                return false;
            }
        }

        // The WEAPONS gate. Its readers are combat target selection (TryFindCombatTarget), the
        // collision handler and the rescue captor - the three places a contact becomes something this member
        // acts on. #1233 S6: whether it may be FOUGHT is the engagement gate, ContactEngageable.
        // #874 WP F: it asks the attackable-contact definition, which is every answer it gave before
        // (null, dead, weapons-excluded) plus the classes the squad's weapons cannot hurt. The sight
        // gate below does not, so an Old Bird is refused here and still seen there. #874 N8: for
        // THIS member, so a rifle with warning fire left at an Old Bird may acquire it.
        private bool CanConsiderEnemy(EnemyAI enemy)
        {
            if (!BlacksiteThreatAdapterRegistry.IsAttackableBy(enemy, this))
                return false;
            return EnemyPassesReachGates(enemy);
        }

        // #1126: the SIGHT gate. Same tests, one term different: an excluded contact is still unseen
        // unless it is the contract's escort objective, which is the whole of
        // BlacksiteSightPolicy.BlindedByWeaponExclusion and the reason is written there. Its one
        // reader is the perception pass (PerceiveServer), so the witness's row and the
        // [UAT][Sighting] line can report a man the squad may not shoot.
        // The weapons predicate is evaluated exactly once here, as it is in the gate above.
        private bool CanSightEnemy(EnemyAI enemy)
        {
            if (enemy == null || enemy.isEnemyDead)
                return false;
            if (BlacksiteSightPolicy.BlindedByWeaponExclusion(
                    BlacksiteHazardAwareness.ExcludeFromRifleTargets(enemy),
                    BlacksiteThreatAdapterRegistry.IsEscortObjective(enemy)))
                return false;
            return EnemyPassesReachGates(enemy);
        }

        // #1126: everything the two gates above share, held in one place so they cannot drift -
        // "not us and not a squadmate", and the world test with its 6 m close-acquire tolerance.
        // #1293 R1: the ship guard's 12 m leash is no longer one of them. Callers have already
        // rejected null.
        private bool EnemyPassesReachGates(EnemyAI enemy)
        {
            if (enemy == this || enemy is BlacksiteSoldierAI)
                return false;
            // #1219 B1: the world term is BlacksiteWorld's; the 6 m close-acquire tolerance stays.
            return BlacksiteWorld.Same(enemy, this)
                || HorizontalDistance(enemy.transform.position, transform.position) <= 6f;
        }

        // #874 S10.2 P1: three samples on the body, not one on the camera. The single camera ray
        // ran from a 2.18 m eye to a ~2.3 m camera, i.e. level at lintel, ceiling-beam and pipe
        // height for its whole length, and any non-player first hit was a total sight loss: run 3
        // produced eleven `visible=False` sightings with dot >= 0.90 inside 12 m. The samples are
        // ordered camera, chest, shins and the first that clears wins, so the common visible case
        // still costs exactly one linecast and only a blocked one pays for three. Each sample
        // keeps the unchanged "the first thing hit must be this player" rule, so this is the real
        // target volume rather than a widened radius: nothing is seen that a body does not occupy.
        private bool HasLineOfSight(PlayerControllerB player, out string rays)
        {
            rays = BlacksiteSightPolicy.RayNone;
            if (player == null)
                return false;

            Vector3 origin = GetEyePosition();
            Vector3 root = player.transform.position;
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : ~0;

            if (PlayerSampleClear(origin, GetPlayerAimPoint(player), player, mask))
            {
                rays = BlacksiteSightPolicy.RayCamera;
                return true;
            }
            if (PlayerSampleClear(origin, root + Vector3.up * BlacksiteSightPolicy.ChestSampleHeight, player, mask))
            {
                rays = BlacksiteSightPolicy.RayChest;
                return true;
            }
            if (PlayerSampleClear(origin, root + Vector3.up * BlacksiteSightPolicy.ShinSampleHeight, player, mask))
            {
                rays = BlacksiteSightPolicy.RayShins;
                return true;
            }
            return false;
        }

        private static bool PlayerSampleClear(Vector3 origin, Vector3 target, PlayerControllerB player, int mask)
        {
            BlacksiteHitchTracer.CountPhysicsQuery();
            if (!Physics.Linecast(origin, target, out RaycastHit hit, mask, QueryTriggerInteraction.Ignore))
                return true;
            var hitPlayer = hit.collider != null ? hit.collider.GetComponentInParent<PlayerControllerB>() : null;
            return hitPlayer == player;
        }

        // #947 S12 U2: the same three-sample shape S10.2 shipped for players, for an enemy body.
        //
        // EnemyTorsoSampleHeight 1.1 m is not a new number: it is the single point
        // HasLineOfSight(EnemyAI) below has always tested, kept verbatim so this test is a strict
        // SUPERSET of the old one - nothing that used to be visible becomes invisible. The low
        // sample reuses BlacksiteSightPolicy.ShinSampleHeight 0.35 m (a 0.30 m stair riser plus the
        // 0.05 m linecast tolerance) rather than inventing a second number for the same floor lip.
        //
        // The order is what matters: the AIM POINT is sampled first, so when it clears, aim and
        // sight name the same point and the round that follows travels the line this test proved.
        // The torso and shin samples are the S10.2 fallback: a contact whose chest is behind a rock
        // is still seen and still tracked, but the rounds fired at it may land on the rock, which is
        // what BlacksiteRepositionReason.NoEffect exists to notice. Each sample keeps the unchanged
        // "the first thing hit must be this enemy" rule, so this is the real target volume and not a
        // widened radius.
        internal const float EnemyTorsoSampleHeight = 1.1f;

        private bool HasEnemyLineOfSight(Vector3 aimPoint, EnemyAI enemy, out Vector3 sightPoint)
        {
            sightPoint = Vector3.zero;
            if (enemy == null)
                return false;

            Vector3 origin = GetEyePosition();
            Vector3 root = enemy.transform.position;
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : ~0;
            // #1011 A3 (O3): the VICTIM's own body is not cover for the thing riding it. A snare
            // flea is posed on the carrier's head socket, so every sample of it starts by passing
            // through the carrier's capsule and EnemySampleClear rejected the hit for not being the
            // flea - member 3 of the 2026-09-12 run reported LostSight on 60 of its 76 melee
            // verdicts through a squadmate it was standing next to. The exemption is exactly one
            // body wide and exists only while the capture does: outside a live capture
            // CaptureVictim answers null and this is the unchanged two-parameter test.
            //
            // N9: and only while the captor RIDES the victim. A Giant, a Dog or a Bracken poses the
            // SOLDIER onto a socket on the monster and leaves the monster's own body standing where
            // a rescuer can already see it, so exempting the victim there would erase real cover a
            // held squadmate provides. TryGetCaptureAnchor is the one place RidesVictim is encoded
            // (it answers false for every hold that is not a ride), so the exemption asks it rather
            // than asking the registry whether a capture exists at all. The rifle hold
            // (IsAllyAttachedTarget) is untouched and still covers every capture kind.
            EnemyAI carrier = BlacksiteVanillaMonsters.TryGetCaptureAnchor(enemy, out _)
                ? BlacksiteVanillaMonsters.CaptureVictim(enemy)
                : null;
            if (ReferenceEquals(carrier, this)) carrier = null;

            if (aimPoint != Vector3.zero && EnemySampleClear(origin, aimPoint, enemy, mask, carrier))
            {
                sightPoint = aimPoint;
                return true;
            }
            Vector3 torso = root + Vector3.up * EnemyTorsoSampleHeight;
            if (EnemySampleClear(origin, torso, enemy, mask, carrier))
            {
                sightPoint = torso;
                return true;
            }
            Vector3 shin = root + Vector3.up * BlacksiteSightPolicy.ShinSampleHeight;
            if (EnemySampleClear(origin, shin, enemy, mask, carrier))
            {
                sightPoint = shin;
                return true;
            }
            return false;
        }

        // The enemy twin of PlayerSampleClear. A vanilla enemy's own body collider usually sits on
        // a layer outside collidersAndRoomMaskAndDefault, so the common clear case is "nothing was
        // hit at all"; when the mask does contain it, the hit is accepted through the same two
        // routes the shot trace uses (BlacksiteCombatController.ResolveEnemy): the collider's own
        // EnemyAICollisionDetect, or an EnemyAI parent.
        // #1011 A3: `exempt` is the one other body a sample may pass through and still count as
        // clear - the squadmate this enemy is riding, and nothing else. Null for every caller and
        // every contact that is not a live captor, which is the unchanged behaviour.
        private static bool EnemySampleClear(Vector3 origin, Vector3 target, EnemyAI enemy, int mask,
            EnemyAI exempt = null)
        {
            BlacksiteHitchTracer.CountPhysicsQuery();
            if (!Physics.Linecast(origin, target, out RaycastHit hit, mask, QueryTriggerInteraction.Ignore))
                return true;
            if (hit.collider == null)
                return false;
            EnemyAICollisionDetect detect = hit.collider.GetComponent<EnemyAICollisionDetect>();
            EnemyAI hitEnemy = detect != null && detect.mainScript != null
                ? detect.mainScript
                : hit.collider.GetComponentInParent<EnemyAI>();
            return hitEnemy == enemy || (exempt != null && hitEnemy == exempt);
        }

        // #947 S12 U2: retained for the one NON-COMBAT enemy sight caller, which asks "can this
        // member see that creature at all" and has no aim point to be handed: the perception pass
        // (PerceiveServer, the squad table's sight bit and the [UAT][Sighting] line). It measures
        // from the root, so nothing about awareness changes; only the combat test, which decides
        // where a round goes, is body-sampled. It was left on one ray deliberately rather than
        // "improved" to three: a body-sampled scan would widen what the squad ACQUIRES, and no
        // range or acquisition rule may widen in this unit.
        private bool HasLineOfSight(EnemyAI enemy)
        {
            if (enemy == null)
                return false;

            Vector3 origin = GetEyePosition();
            Vector3 target = enemy.transform.position + Vector3.up * 1.1f;
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : ~0;

            BlacksiteHitchTracer.CountPhysicsQuery();
            if (Physics.Linecast(origin, target, out RaycastHit hit, mask, QueryTriggerInteraction.Ignore))
            {
                EnemyAI hitEnemy = hit.collider != null ? hit.collider.GetComponentInParent<EnemyAI>() : null;
                return hitEnemy == enemy;
            }

            return true;
        }

        // #874 S10.2 P1: eye level inside the soldier's own body. The old origin was 2.18 m with a
        // 0.20 m forward push; 2.18 m is 0.28 m ABOVE this actor's own capsule top (see
        // BlacksiteBodyCollision: CenterY + Height/2), which is why every ray from it ran at lintel
        // height. The height is derived from the body constants rather than restated, and the
        // forward push is gone with it: at 1.75 m the origin sits inside a capsule whose layer
        // (EnemiesNotRendered) is deliberately absent from collidersAndRoomMaskAndDefault, so
        // nothing self-occludes and the offset only skewed the view cone off the body centre.
        private static readonly float EyeHeight =
            BlacksiteSightPolicy.EyeHeight(BlacksiteBodyCollision.CenterY, BlacksiteBodyCollision.Height);

        private Vector3 GetEyePosition()
        {
            return transform.position + Vector3.up * EyeHeight;
        }

        private static Vector3 GetPlayerAimPoint(PlayerControllerB player)
        {
            if (player?.gameplayCamera != null)
                return player.gameplayCamera.transform.position;
            return player != null ? player.transform.position + Vector3.up * 1.5f : Vector3.zero;
        }

        // #1013 A6: the soldier-level shorthand passes the navigator's motion result straight
        // through. Every existing caller that ignores it keeps compiling unchanged.
        private BlacksiteMotionVerdict SetDestinationThrottled(Vector3 destination, bool validatePath = true, string issuer = "order") => Navigator.SetDestinationThrottled(destination, validatePath: validatePath, issuer: issuer);


        // #874 S4.0a: every caller of this shim is an interaction step (door, entrance, elevator,
        // objective queue or approach), so the issuer tag is named once here rather than at
        // fifteen call sites. The signature is left at one parameter deliberately: native fixtures
        // resolve it by name with a single argument.
        internal void IssueRawAgentDestination(Vector3 resolved) => Navigator.IssueAgentDestination(resolved, "interaction");


        private static BlacksiteSoldierTacticalRole ResolveTacticalRole(BlacksiteSoldierRole role, int index)
        {
            return BlacksiteFormationPlanner.ResolveRole(index, 6);
        }

        private float GetDeploymentStaggerSeconds()
        {
            return BlacksiteRetrievalTuning.GetDeploymentDelay(
                _tacticalRole,
                _index,
                _animationSeed);
        }

        // #1219 E2: the replicated animation role speed (the NetVar UpdateAnimation writes). A
        // stroll is calibrated over the Normal role, so only Urgent raises it.
        private float GetRoleMoveSpeed() => BlacksitePatrolPolicy.RoleSpeed(CurrentPace);

        // #1219 E2: the pace the last executed order declared (ExecuteSquadOrderCore). Host-only,
        // like every speed decision; it reaches peers through the role speed it resolves to.
        private BlacksiteOrderPace _orderPace;

        /// <summary>
        /// #1219 E2: the pace this member walks now - Urgent under a named escalation, otherwise the
        /// order's declared pace while an order owns the body, and Normal under every other owner
        /// (a lost transport, any other selected behaviour).
        /// </summary>
        internal BlacksiteOrderPace CurrentPace
            => BlacksitePatrolPolicy.ResolvePace(_orderPace,
                !_transportLost
                && (SelectedBehaviour == BlacksiteBehaviour.SquadOrder || SelectedBehaviour == BlacksiteBehaviour.IdleWithGoal),
                UrgentEscalation);

        // #1219 E2: the named escalations, the only things worth the urgent role speed. An alert
        // without contact is not one: the guard that heard it scans, and its next leg is Normal.
        // #1086: running from a popped Jester - true while that is this member's evade (#1305 H,
        // C4: no window of its own). #1096 (WP5): a Jester winding up within its reaction radius
        // of any member; the squad keeps its objective and does it faster. The flag is the 0.2 s
        // host scan's own derived bool, so this is a field read on the quiet path.
        private bool UrgentEscalation
            => IsObjectiveCombatActive || IsRaidWithdrawing
               || IsFleeingPoppedJester || BlacksiteVanillaMonsters.CrankingJesterKnown;

        private void UpdateRoleCues()
        {
            // #874: role cues are voice only. The only gesture clips are aimed full-body strafes
            // and turns (Signal = QuickStrafe, Radio = RifleTurnIdle), so a rest-stance guard
            // snapped into an aimed strafe on every cue; there is no hand-signal clip yet.
            // The cadence roll is local Random, so only the server rolls it.
            if (!IsServerAuthority() || !_initialized || isEnemyDead || Time.time < _nextRoleCueTime)
                return;

            _nextRoleCueTime = Time.time + UnityEngine.Random.Range(28f, 45f);
            if (_tacticalRole == BlacksiteSoldierTacticalRole.Leader)
                RequestSquadVoice(BlacksiteVoiceEvent.TacticalAction, volume: 0.58f, ambient: true);
        }

        private void PlayDeployCue()
        {
            if (_deployCuePlayed)
                return;

            _deployCuePlayed = true;
            switch (_tacticalRole)
            {
                case BlacksiteSoldierTacticalRole.Pointman:
                    break;
                case BlacksiteSoldierTacticalRole.Leader:
                    // #874: no Radio gesture on deploy (see UpdateRoleCues).
                    if (TryGetSquadDirector(out BlacksiteSquadDirector director))
                        director.ReportInitialEntry(this);
                    break;
            }
        }

        // #314: cadence is a commitment, not a per-trigger coin flip. He picks burst or single for
        // a two-to-three second window so the fire pattern reads as a decision he is holding.
        internal bool ShouldFireBurst(float distance)
        {
            if (distance < 5.5f)
                return false;

            if (Time.time >= _burstCadenceCommittedUntil)
            {
                float chance = _tacticalRole == BlacksiteSoldierTacticalRole.Support
                    ? SupportBurstFireChance
                    : _tacticalRole == BlacksiteSoldierTacticalRole.Rifleman
                        ? BurstFireChance + 0.08f
                        : BurstFireChance;
                _burstCadenceCommitment = UnityEngine.Random.value < chance;
                _burstCadenceCommittedUntil = Time.time
                    + BlacksiteRetrievalTuning.GetBurstCadenceWindowSeconds(_animationSeed);
            }

            return _burstCadenceCommitment;
        }

        private void HandleDeadState()
        {
            if (_deadStateApplied)
                return;

            _deadStateApplied = true;
            // #874 S4: a corpse is seated; nothing moves it but the death handler below.
            Navigator.BeginSeat("dead");
            // #1066 U8: vanilla EnemyAI.KillEnemy stops creatureVoice and then PlayOneShot(dieSFX)
            // unconditionally. dieSFX was never assigned on this enemy, so every death logged
            // "PlayOneShot was called with a null AudioClip" (6x in the 2026-09-15 session, each one
            // paired with the vanilla "Playing death sound for enemy: Blacksite Soldier" line) and
            // this reaction was the only audible death. RefreshDeathSfx now assigns the SAME seeded
            // clip this block would have chosen, and creatureVoice IS _voiceSource, so vanilla plays
            // it through the same source: the reaction below would be a second, layered copy of the
            // clip vanilla just started. Play it only when vanilla could not.
            if (dieSFX == null || creatureVoice == null)
            {
                AudioClip[] deathClips = ContractAssetLoader.BlacksiteDeathReactionClips;
                if (deathClips != null && deathClips.Length > 0)
                {
                    int deathIndex = (int)((uint)_animationSeed % (uint)deathClips.Length);
                    PlayReactionClipLocal(deathClips[deathIndex], 0.72f, 1f);
                }
            }
            TrySetBool(DeadHash, true);
            foreach (Collider collider in GetComponentsInChildren<Collider>(true))
            {
                if (collider != null)
                    collider.enabled = false;
            }
            // #316: the cosmetic weapon goes in the same frame the corpse settles, on every peer.
            // The server's drop (if any) already spawned the real item; a client that somehow has no
            // hosted weapon simply has nothing to destroy.
            _weaponVisual?.Destroy();
            // #874 S2.4: the one-time corpse work above, then the applier owns lights, capsules and
            // body presence for the Dead state; nothing here fights it on later frames.
            ApplyLifecyclePresentation("dead");
        }

        private void EnsureBehaviourStates()
        {
            if (enemyBehaviourStates != null && enemyBehaviourStates.Length >= 4)
                return;

            enemyBehaviourStates = new[]
            {
                new EnemyBehaviourState { name = "Deploying", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Moving", IsAnimTrigger = false, parameterString = string.Empty },
                new EnemyBehaviourState { name = "Contact", IsAnimTrigger = true, parameterString = FireParameter },
                new EnemyBehaviourState { name = "Dead", IsAnimTrigger = false, parameterString = DeadParameter, boolValue = true },
            };
        }

        private void EnsureHitDetectionWired()
        {
            EnemyAICollisionDetect existingDetect = GetComponentInChildren<EnemyAICollisionDetect>(true);
            if (existingDetect != null)
            {
                existingDetect.mainScript = this;
                existingDetect.canCollideWithEnemies = true;
                existingDetect.onlyCollideWhenGrounded = false;
                TrySetEnemyTag(existingDetect.gameObject);
                return;
            }
            if (!_rigFailureLogged)
            {
                _rigFailureLogged = true;
                ContractsBootstrap.Log?.LogError(
                    $"[MoonContracts.Blacksite] Soldier {_index} has no authored EnemyAICollisionDetect on its hit capsule.");
            }
        }

        // #956 S12A U3: the Unity reads stay here; the arbitration is
        // BlacksiteMotionPolicy.CommandedSpeed, which also names the term that bound the result so
        // the [UAT][Motion] line can print speedOwner= instead of leaving the number unattributed.
        // The arithmetic and its order are unchanged; the only behaviour change is the reach
        // contract gate inside ShouldUseApproachSlowdown below.
        private float GetAgentMoveSpeed() => GetAgentMoveSpeed(out _, out _);

        private float GetAgentMoveSpeed(out float roleSpeed, out BlacksiteSpeedOwner owner)
        {
            // #1219 E2: the pace binds the role speed the rest of this arbitrates against: a Stroll
            // order walks StrollSpeed, a named escalation the urgent role speed (BlacksitePatrolPolicy.AgentSpeed).
            roleSpeed = BlacksitePatrolPolicy.AgentSpeed(CurrentPace);
            bool cargoCapped = IsPushingPayload;
            if (cargoCapped)
            {
                float cap = Y4NGZCompany.Contracts.Payload.PayloadCartController.NpcPushSpeed
                    * (Y4NGZCompany.Contracts.Payload.PayloadContract.ActiveCart?.NpcSlopeSpeedScale ?? 1f);
                // The cap binds the ROLE speed, so every term below arbitrates against the capped
                // value and the second Mathf.Min the old expression carried is redundant.
                cargoCapped = cap < roleSpeed;
                roleSpeed = Mathf.Min(roleSpeed, cap);
            }
            // #990 S16 D2: the pursuit floor. A Witness-RESERVED operator (the man walking the
            // target down, not the escort walking him out) that can see its target is commanded
            // above the target's own escape speed while that line of sight holds; the 2026-09-12
            // run chased 6.6 m/s with 4.8 and its closest approach got worse, not better, over
            // 267 s. It binds where the caps bind - on the role speed, before the approach lerp and
            // the multipliers - and only upward, so a crouched, webbed or arriving pursuer is still
            // slowed by every term above it.
            //
            // #990 round 2: the guard is what the member IS, not whether a cap happened to bind.
            // `cargoCapped` is false when a cap applies but is looser than the role speed, so it
            // would have let a pushing member take the floor on exactly the frames the cap was not
            // the binding term. A reservation is not a carry, so in practice these are already
            // exclusive; the guard now says so. #1293 R2: an escort walks at his role speed - the
            // witness is attached - and takes no pursuit floor either.
            bool pursuing = !IsPushingPayload && !IsEscortingWitness && IsPursuingWitnessInSight;
            if (pursuing)
            {
                pursuing = BlacksiteMotionPolicy.WitnessPursuitSpeed > roleSpeed;
                roleSpeed = Mathf.Max(roleSpeed, BlacksiteMotionPolicy.WitnessPursuitSpeed);
            }
            return BlacksiteMotionPolicy.CommandedSpeed(
                roleSpeed,
                // #1066 U3: the agent's remaining distance may only be read from an ACTIVE agent on a
                // NavMesh. A null check is not that check, and this was the one unguarded read in
                // the file - it logged 25 "GetRemainingDistance can only be called on an active
                // agent" errors per session, during spawn (before the warp onto the mesh) and on
                // every death/capture (the agent is disabled in the same frame). Navigator's
                // RemainingDistance is the existing AgentBound && hasPath guard and returns -1 when
                // it does not hold, which CommandedSpeed's approach lerp (remaining >= 0 and below
                // ApproachSlowdownDistance) never takes. #1271 S1: and it is finite on a path too.
                _navigator != null ? _navigator.RemainingDistance : -1f,
                IsServerAuthority() && ShouldUseApproachSlowdown(),
                _serverCrouched,
                Time.time < _webSlowUntil,
                cargoCapped,
                out owner,
                pursuing);
        }

        private bool ShouldUseApproachSlowdown()
        {
            if (_agent == null
                || !_agent.enabled
                || !_agent.isOnNavMesh
                || !_agent.hasPath
                || _agent.pathPending
                || Navigator.RemainingDistance >= ApproachSlowdownDistance
                || _extractingToShip)
            {
                return false;
            }

            // #956 S12A U3: the lerp protects an ACTION's standing point, not a waypoint. The
            // March run issued 86 % of its destinations with stopOwner=default - a plain transit
            // goal with no reach contract, no ground-route corridor and no world interaction - and
            // walked the last 1.4 m of every one of them down toward WalkApproachSpeed 2.2 for
            // nothing, on legs whose median length was 2.04 m. A goal the member is only passing
            // through is walked at the role speed; the reach contract is the same distinction
            // BlacksiteNavigator.SetDestinationThrottled already makes for its re-issue threshold.
            if (Navigator.IsTransitGoal)
                return false;

            if (!TryGetSquadDirector(out BlacksiteSquadDirector director))
                return true;
            BlacksiteSquadOrderKind kind = director.CurrentOrderKind;
            return kind != BlacksiteSquadOrderKind.Contact
                && kind != BlacksiteSquadOrderKind.Extract
                && kind != BlacksiteSquadOrderKind.Board;
        }

        private static void TrySetEnemyTag(GameObject target)
        {
            if (target == null)
                return;

            try
            {
                target.tag = "Enemy";
            }
            catch (UnityException e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Tag 'Enemy' was not found; leaving '{target.name}' tag unchanged. {e.Message}");
            }
        }

        private void DisableImportedSceneComponents()
        {
            _sceneComponentsDisabled = true;
            foreach (Camera camera in GetComponentsInChildren<Camera>(true))
            {
                if (camera == null)
                    continue;
                camera.enabled = false;
                camera.gameObject.SetActive(false);
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Disabled imported soldier camera '{camera.name}' to keep the player view active.");
            }

            foreach (AudioListener listener in GetComponentsInChildren<AudioListener>(true))
            {
                if (listener == null)
                    continue;
                listener.enabled = false;
                listener.gameObject.SetActive(false);
            }

            foreach (Light light in GetComponentsInChildren<Light>(true))
            {
                if (light == null
                    || light == _muzzleLight
                    || light == _helmetLight
                    || string.Equals(light.name, BlacksiteSoldierRigBinding.MuzzleLightName, StringComparison.Ordinal)
                    || string.Equals(light.name, BlacksiteSoldierRigBinding.HelmetLightName, StringComparison.Ordinal))
                    continue;
                light.enabled = false;
                light.gameObject.SetActive(false);
            }
        }

        private static Material CreateRuntimeMaterial(Color color)
        {
            Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = "BlacksiteRuntimeMaterial" };
            SetMaterialColor(material, color);
            ForceOpaque(material);
            return material;
        }

        private static void ForceOpaque(Material material)
        {
            material.renderQueue = 2000;
            material.SetOverrideTag("RenderType", "Opaque");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            TrySetMaterialFloat(material, "_SurfaceType", 0f);
            TrySetMaterialFloat(material, "_BlendMode", 0f);
            TrySetMaterialFloat(material, "_ZWrite", 1f);
            TrySetMaterialFloat(material, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            TrySetMaterialFloat(material, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            TrySetMaterialFloat(material, "_AlphaSrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            TrySetMaterialFloat(material, "_AlphaDstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
        }

        private static void SetMaterialColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
        }

        private static void TrySetMaterialFloat(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
                material.SetFloat(property, value);
        }

        // #1293 R2: an off-mesh agent is bound where the body stands or the member is stranded
        // (BlacksiteNavigator.BindInPlace); no body is moved to find navmesh. A ship passage and a
        // committed ride run the agent off the mesh by design and own it until they finish.

        private void SnapToNavMesh(Vector3 origin, float maxDistance) => Navigator.SnapToNavMesh(origin, maxDistance);

        private static Vector3 ResolveNavMeshPosition(Vector3 origin, float maxDistance)
        {
            if (NavMesh.SamplePosition(origin, out NavMeshHit hit, maxDistance, NavMesh.AllAreas))
                return hit.position;
            return origin;
        }

        internal void SetWeaponSnapshotServer(BlacksiteWeaponSnapshot snapshot)
        {
            if (!IsServerAuthority()
                || !_initialized
                || NetworkObject == null
                || !NetworkObject.IsSpawned)
            {
                return;
            }
            _weaponState.Value = snapshot;
        }

        internal Vector3 GetCombatAimPoint(PlayerControllerB player)
        {
            return GetPlayerAimPoint(player);
        }

        // #947 S12 U2: the point this member last PROVED sight to, or Vector3.zero when it proved
        // none. Written by HasCombatLineOfSight itself so the combat trace can print sightPoint=
        // beside aimPoint= without casting a second ray on a think that is only being logged. When
        // the two differ, the member can see the contact but not through the line it is shooting
        // down, which is the whole of run 2's "aggroed and did nothing".
        internal Vector3 LastCombatSightPoint { get; private set; }

        // #947 S12 U2: this used to DISCARD `point` whenever there was a live contact, so the rifle
        // aimed at BlacksiteCombatController.GetCurrentAimPoint and the sight test answered about
        // transform.position + up * 1.1 instead. On a Giant Sapsucker those are 4 m apart: legs
        // clear, head behind a rock, visible=true, every round into the rock. The enemy branch now
        // samples the body starting AT the point it was handed.
        internal bool HasCombatLineOfSight(
            Vector3 point,
            PlayerControllerB playerTarget,
            EnemyAI enemyTarget)
        {
            if (playerTarget != null)
            {
                // Unchanged S10.2 behaviour; only the sample that cleared is now recorded.
                bool seenPlayer = HasLineOfSight(playerTarget, out string rays);
                LastCombatSightPoint = seenPlayer ? PlayerSamplePoint(playerTarget, rays) : Vector3.zero;
                return seenPlayer;
            }
            if (enemyTarget != null)
            {
                bool seenEnemy = HasEnemyLineOfSight(point, enemyTarget, out Vector3 enemySightPoint);
                LastCombatSightPoint = seenEnemy ? enemySightPoint : Vector3.zero;
                return seenEnemy;
            }

            Vector3 origin = CombatMuzzlePosition;
            Vector3 delta = point - origin;
            if (delta.sqrMagnitude <= 0.01f)
            {
                LastCombatSightPoint = point;
                return true;
            }
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : ~0;
            BlacksiteHitchTracer.CountPhysicsQuery();
            bool clear = !Physics.Raycast(
                origin,
                delta.normalized,
                delta.magnitude,
                mask,
                QueryTriggerInteraction.Ignore);
            LastCombatSightPoint = clear ? point : Vector3.zero;
            return clear;
        }

        // The point behind a player `rays=` answer, for sightPoint= only. No ray is cast here: the
        // three sample heights are the S10.2 constants, untouched.
        private static Vector3 PlayerSamplePoint(PlayerControllerB player, string rays)
        {
            if (player == null)
                return Vector3.zero;
            Vector3 root = player.transform.position;
            if (rays == BlacksiteSightPolicy.RayChest)
                return root + Vector3.up * BlacksiteSightPolicy.ChestSampleHeight;
            if (rays == BlacksiteSightPolicy.RayShins)
                return root + Vector3.up * BlacksiteSightPolicy.ShinSampleHeight;
            return GetPlayerAimPoint(player);
        }

        internal bool IsConfirmedDriveCarrier(PlayerControllerB player)
        {
            if (player == null
                || !TryGetMission(out BlacksiteRetrievalMissionController mission)
                || mission.DriveState != BlacksiteHardDriveState.PlayerHeld
                || !BlacksiteHardDriveObjectiveService.TryGetDrive(mission, out BreachHardDriveItem drive))
            {
                return false;
            }
            PhysicsProp prop = drive.PhysicsProp;
            return prop != null && prop.playerHeldBy == player;
        }

        internal float GetActionMarkerDelay(string standingClip, string crouchClip, string eventName, float fallback)
        {
            if (_animator == null || _animator.runtimeAnimatorController == null) return fallback;
            string name = (_poseFlags.Value & CrouchedPoseFlag) != 0 ? crouchClip : standingClip;
            foreach (AnimationClip clip in _animator.runtimeAnimatorController.animationClips)
            {
                if (clip == null || clip.name != name) continue;
                foreach (AnimationEvent marker in clip.events)
                    if (marker.functionName == eventName) return Mathf.Max(.05f, marker.time);
            }
            return fallback;
        }

        internal float GetFireEventDelay()
        {
            if (_animator == null || _animator.runtimeAnimatorController == null) return .2f;
            bool crouched = (_poseFlags.Value & CrouchedPoseFlag) != 0;
            string name = crouched ? "CrouchRapidFire" : "StandingFire";
            foreach (AnimationClip clip in _animator.runtimeAnimatorController.animationClips)
            {
                if (clip == null || clip.name != name) continue;
                foreach (AnimationEvent marker in clip.events)
                    if (marker.functionName == "OnBlacksiteFire")
                        return Mathf.Clamp(marker.time, .09f, 1.5f);
                return Mathf.Clamp(clip.length * .32f, .09f, 1.5f);
            }
            return .2f;
        }

        internal bool HasCombatAimAlignment(Vector3 point)
            => GetCombatAimReadiness(point, out _, out _) == BlacksiteFireReadiness.Ready;

        internal BlacksiteFireReadiness GetCombatAimReadiness(Vector3 point, out float bodyAngle, out float rifleAngle)
        {
            Vector3 direction = point - CombatMuzzlePosition;
            Vector3 flat = Vector3.ProjectOnPlane(direction, Vector3.up);
            bodyAngle = flat.sqrMagnitude > 0.001f ? Vector3.Angle(transform.forward, flat) : 180f;
            rifleAngle = _weaponVisual != null && _weaponVisual.HasModel
                ? (_muzzleAnchor != null ? Vector3.Angle(_muzzleAnchor.forward, direction) : 180f)
                : bodyAngle; // The legacy audio/flash fallback has no visible rifle to solve.
            _lastBodyAimAngle = bodyAngle;
            _lastRifleAimAngle = rifleAngle;
            if (_aimPose == null) return BlacksiteFireReadiness.PoseMissing;
            var pose = _aimPose.FireReadiness;
            if (pose != BlacksiteFireReadiness.Ready) return pose;
            // #1064 WP3: the alignment band is read at the width a MOVING body can hold, because a
            // member reaching this gate is often walking. #1219 F2: the readiness steps no longer
            // raise a combat dwell, so a rifleman on a committed combat step reaches this gate at
            // his walking speed (FiringWalkSpeed while a burst fires) and only the movement decision
            // holds a member with nowhere to go. The base layer keeps the locomotion clip and the
            // masked Aim layer drives the upper body, so the torso and the muzzle ride the stride;
            // holding those frames to the standing 18/10 degrees is part of what produced the nine
            // consecutive BodyMisaligned/RifleMisaligned thinks between member 4's two bursts on
            // 2026-09-15. A stationary member is unchanged.
            bool moving = BlacksiteCombatPolicy.IsMovingShot(GetServerPlanarSpeed());
            if (!BlacksiteCombatPolicy.CanReleaseShot(true, true, false, bodyAngle, 0f, moving))
                return BlacksiteFireReadiness.BodyMisaligned;
            if (!BlacksiteCombatPolicy.CanReleaseShot(true, true, false, 0f, rifleAngle, moving))
                return BlacksiteFireReadiness.RifleMisaligned;
            return BlacksiteFireReadiness.Ready;
        }

        internal void TriggerCombatFireAnimation()
        {
            TriggerAnimationNetworked(FireParameter);
        }

        internal void TriggerCombatReloadAnimation()
        {
            TriggerAnimationNetworked(ReloadParameter);
        }



        internal void OnBlacksiteFireEvent()
        {
            if (!IsServerAuthority() || UsesBaton || HasOccupiedCargoHands || _climbingPose)
                return;
            EnsureCombatController();
            _combatController.NotifyShotAnimationEvent();
        }



        internal void OnBlacksiteMagazineRelease()
        {
            SetMagazinePresentation(visible: false);
            if (!IsServerAuthority())
                return;
            EnsureCombatController();
            _combatController.NotifyMagazineReleaseAnimationEvent();
        }

        internal void OnBlacksiteMagazineInsert()
        {
            SetMagazinePresentation(visible: true);
            if (!IsServerAuthority())
                return;
            EnsureCombatController();
            _combatController.NotifyMagazineInsertAnimationEvent();
        }

        internal void OnBlacksiteActionComplete()
        {
            if (!IsServerAuthority())
                return;
            EnsureCombatController();
            _combatController.NotifyActionCompleteAnimationEvent();
        }

        internal void PresentMagazineStateNetworked(bool visible)
        {
            SetMagazinePresentation(visible);
            if (NetworkObject != null && NetworkObject.IsSpawned && IsServerAuthority())
                SetMagazinePresentationClientRpc(visible);
        }

        [ClientRpc]
        private void SetMagazinePresentationClientRpc(bool visible)
        {
            if (IsServerAuthority())
                return;
            SetMagazinePresentation(visible);
        }

        private void SetMagazinePresentation(bool visible)
        {
            ResolveAttachmentAnchors();
            if (_magazineVisual != null && _magazineVisual.gameObject.activeSelf != visible)
                _magazineVisual.gameObject.SetActive(visible);
        }

        internal void PresentAuthoritativeShot(
            Vector3 origin,
            Vector3 endPoint,
            bool burst,
            bool firstInBurst,
            BlacksiteShotImpactKind impactKind)
        {
            PlayShot(burst, firstInBurst);
            PlayShotTracerLocal(origin, endPoint, impactKind);
            if (NetworkObject != null && NetworkObject.IsSpawned && IsServerAuthority())
                PlayShotTracerClientRpc(origin, endPoint, (byte)impactKind);
        }

        private void PlayShot(bool burst, bool firstInBurst)
        {
            int index = 0;
            if (!burst && ContractAssetLoader.BlacksiteGunshotSingleClips.Length > 0)
                index = UnityEngine.Random.Range(0, ContractAssetLoader.BlacksiteGunshotSingleClips.Length);
            // #316: the cue always goes out. Whether it is *heard* is decided per peer inside
            // PlayAudioCueLocal, because only that peer knows whether its own copy of this soldier
            // ended up carrying a hosted MX16A4 with its own fire audio. Deciding it here would use
            // the server's install to silence - or double - the shot for everybody else.
            if (!burst || firstInBurst)
            {
                PlayAudioCueNetworked(
                    burst ? BlacksiteAudioCue.BurstShot : BlacksiteAudioCue.SingleShot,
                    index,
                    burst ? 0.95f : 0.9f,
                    UnityEngine.Random.Range(0.96f, 1.04f));
            }
            PlayMuzzleFlashLocal(burst);
            if (NetworkObject != null && NetworkObject.IsSpawned && IsServerAuthority())
                PlayMuzzleFlashClientRpc(burst);
        }

        [ClientRpc]
        private void PlayShotTracerClientRpc(Vector3 origin, Vector3 endPoint, byte impactKind)
        {
            if (IsServerAuthority())
                return;
            PlayShotTracerLocal(origin, endPoint, (BlacksiteShotImpactKind)impactKind);
        }

        private void PlayShotTracerLocal(Vector3 origin, Vector3 endPoint, BlacksiteShotImpactKind impactKind)
        {
            _aimPose?.PresentShot();
            var tracer = new GameObject("BlacksiteAuthoritativeTracer");
            var line = tracer.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPosition(0, origin);
            line.SetPosition(1, endPoint);
            line.startWidth = 0.018f;
            line.endWidth = 0.006f;
            Color color = impactKind == BlacksiteShotImpactKind.World
                ? new Color(1f, 0.62f, 0.22f, 0.92f)
                : new Color(1f, 0.92f, 0.62f, 0.95f);
            line.startColor = color;
            line.endColor = new Color(color.r, color.g, color.b, 0.12f);
            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                var material = new Material(shader) { color = color };
                line.sharedMaterial = material;
                Destroy(material, 0.2f);
            }
            Destroy(tracer, 0.075f);
            PlayHostedImpactLocal(origin, endPoint, impactKind);
        }

        /// <summary>
        /// #316: the hosted weapon's surface burst, bullet hole and impact clip, on every peer that
        /// saw the tracer. Pure addition - the rig's bound BlacksiteImpactEffect particle was never
        /// played on any path, so nothing here replaces an existing effect.
        ///
        /// The shot resolution is server-side and only its end point is replicated, and the effects
        /// API needs the struck collider to pick a surface family. Rather than widen the RPC for a
        /// cosmetic, each peer re-acquires the contact with one short ray into the end point: the
        /// tracer already costs a GameObject and a material per round, so a single 0.4m raycast is
        /// well inside the budget this path was built with.
        /// </summary>
        private void PlayHostedImpactLocal(Vector3 origin, Vector3 endPoint, BlacksiteShotImpactKind impactKind)
        {
            if (_weaponVisual == null || !_weaponVisual.HasModel)
                return;
            // Only a round that actually struck the world or a monster leaves a mark. A hit on a
            // crewmate - or one the friendly-fire guard suppressed - must not paint a bullet hole and
            // a surface burst on a player.
            if (impactKind != BlacksiteShotImpactKind.World && impactKind != BlacksiteShotImpactKind.Enemy)
                return;

            Vector3 direction = endPoint - origin;
            if (direction.sqrMagnitude < 1e-6f)
                return;
            direction.Normalize();

            Vector3 probeStart = endPoint - direction * HostedImpactProbeBack;
            BlacksiteHitchTracer.CountPhysicsQuery();
            if (!Physics.Raycast(
                    probeStart,
                    direction,
                    out RaycastHit hit,
                    HostedImpactProbeBack * 2f,
                    GetHostedImpactProbeMask(),
                    QueryTriggerInteraction.Ignore))
            {
                return;
            }

            BlacksiteUpgradesWeaponBridge.PlayImpact(endPoint, hit.normal, hit.collider);
        }

        /// <summary>
        /// Layers the impact probe is allowed to find: the world surfaces every other raycast in this
        /// mod uses, plus Enemies so a monster hit still resolves to the body that took it. Anything
        /// else - players, triggers, scan nodes - is either already excluded by
        /// <see cref="PlayHostedImpactLocal"/>'s kind gate or is not a surface worth marking.
        /// </summary>
        private static int GetHostedImpactProbeMask()
        {
            if (_hostedImpactProbeMask != 0)
                return _hostedImpactProbeMask;

            int world = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : Physics.DefaultRaycastLayers;
            int enemyLayer = LayerMask.NameToLayer("Enemies");
            if (enemyLayer >= 0)
                world |= 1 << enemyLayer;

            _hostedImpactProbeMask = world;
            return world;
        }

        private static int _hostedImpactProbeMask;

        [ClientRpc]
        private void PlayMuzzleFlashClientRpc(bool burst)
        {
            if (IsServerAuthority())
                return;
            PlayMuzzleFlashLocal(burst);
        }

        private void PlayMuzzleFlashLocal(bool burst)
        {
            AttachMuzzleEffects();
            bool hosted = PlayHostedShotEffectsLocal();

            // The dynamic light stays either way: it is what makes a shot read on the walls and on
            // the soldier himself, which the weapon's own particle burst does not do.
            if (_muzzleLight != null)
            {
                _muzzleLight.range = burst ? 4.2f : 3.2f;
                _muzzleLight.intensity = burst ? 4.2f : 3.1f;
                _muzzleLightOffTime = Time.time + (burst ? 0.14f : 0.055f);
            }

            // The authored quad is the stand-in for a flash the soldier has no model for; a hosted
            // weapon draws its own, and showing both reads as two guns firing.
            if (!hosted && _muzzleFlashRenderer != null)
            {
                Transform flash = _muzzleFlashRenderer.transform;
                SetWorldScale(flash, Vector3.one * (burst ? 0.09f : 0.06f));
                _muzzleFlashRenderer.enabled = true;
                _muzzleVisualOffTime = Time.time + (burst ? 0.12f : 0.05f);
            }
        }

        /// <summary>
        /// #316: draw and play the hosted MX16A4's own shot, locally on this peer. Returns whether it
        /// drew a muzzle flash, which is what suppresses the authored stand-in quad.
        /// </summary>
        private bool PlayHostedShotEffectsLocal()
        {
            if (_weaponVisual == null || !_weaponVisual.HasModel)
                return false;

            // The bore is the aim here: the muzzle anchor sits on the model's real barrel and the body
            // turns to face the target before the shot commits, so muzzle-forward is the shot vector.
            // The shot's own direction is not available on the flash path - the muzzle-flash ClientRpc
            // carries no endpoint and arrives ahead of the tracer one that does.
            Vector3 forward = _muzzleAnchor != null ? _muzzleAnchor.forward : transform.forward;
            bool flashed = _weaponVisual.PlayMuzzleFlash(forward);

            AudioClip fireClip = _weaponVisual.NextFireClip();
            if (fireClip != null && _audioSource != null)
            {
                // The shared weapon source is left detuned by the built-in cue path, which pitches it
                // per shot; the hosted clips are authored at pitch and must not inherit that.
                // #929: the hosted clips are normalized to the -1 dBFS ceiling at install, so the
                // presented volume is 1.0 here for the same reason the built-in cue's is.
                _audioSource.pitch = 1f;
                _audioSource.PlayOneShot(fireClip, 1f);
            }

            // Only a flash that actually drew suppresses the authored stand-in quad - a bridge call
            // that threw, or a pack that went inert mid-session, must fall back rather than show
            // nothing.
            return flashed;
        }

        private static void SetWorldScale(Transform transform, Vector3 worldScale)
        {
            if (transform == null)
                return;

            Vector3 parentScale = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
            transform.localScale = new Vector3(
                SafeScale(worldScale.x, parentScale.x),
                SafeScale(worldScale.y, parentScale.y),
                SafeScale(worldScale.z, parentScale.z));
        }

        private static float SafeScale(float desired, float parentScale)
        {
            float scale = Mathf.Abs(parentScale);
            return scale > 0.0001f ? desired / scale : desired;
        }

        internal void OnBlacksiteFootstep()
        {
            PlayAnimationStep(_nextLegacyStepIsLeft);
            _nextLegacyStepIsLeft = !_nextLegacyStepIsLeft;
        }

        internal void OnBlacksiteLeftStep()
        {
            PlayAnimationStep(leftFoot: true);
        }

        internal void OnBlacksiteRightStep()
        {
            PlayAnimationStep(leftFoot: false);
        }

        private void PlayAnimationStep(bool leftFoot)
        {
            if (isEnemyDead)
                return;
            if (!IsActivelyMoving(_currentMoveSpeed) && _currentMoveSpeed < 0.35f)
                return;

            PlayFootstepAudio(leftFoot);
        }

        private void TickFootstepAudio(float speed)
        {
            if (speed < 0.35f || isEnemyDead)
            {
                _lastFootstepCycle = -1f;
                return;
            }

            if (_footstepEventsEnabled && Time.time - _lastFootstepAudioAt < 0.7f)
                return;

            if (_animator != null && _animator.isActiveAndEnabled)
            {
                AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(0);
                if (state.shortNameHash == LocomotionStateHash)
                {
                    float cycle = Mathf.Repeat(state.normalizedTime, 1f);
                    bool step = CrossedAnimationCyclePoint(_lastFootstepCycle, cycle, FootstepCyclePointA)
                        || CrossedAnimationCyclePoint(_lastFootstepCycle, cycle, FootstepCyclePointB);
                    _lastFootstepCycle = cycle;
                    if (!step)
                        return;
                    PlayFootstepAudio(_nextLegacyStepIsLeft);
                    _nextLegacyStepIsLeft = !_nextLegacyStepIsLeft;
                    return;
                }
            }

            if (Time.time < _nextFootstepTime)
                return;

            _nextFootstepTime = Time.time + Mathf.Lerp(0.46f, 0.26f, Mathf.InverseLerp(0.5f, 3.5f, speed));
            PlayFootstepAudio(_nextLegacyStepIsLeft);
            _nextLegacyStepIsLeft = !_nextLegacyStepIsLeft;
        }

        private static bool CrossedAnimationCyclePoint(float previous, float current, float point)
        {
            if (previous < 0f)
                return false;
            if (current >= previous)
                return previous < point && current >= point;
            return previous < point || current >= point;
        }

        private void PlayFootstepAudio(bool leftFoot)
        {
            EmitFootstepNoise();
            int count = ContractAssetLoader.BlacksiteFootstepClips.Length;
            if (count <= 0 || _footstepSource == null)
                return;
            if (Time.time < _lastFootstepAudioAt + FootstepAudioMinInterval)
                return;

            int index = UnityEngine.Random.Range(0, count);
            if (count > 1 && index == _lastFootstepClipIndex)
                index = (index + UnityEngine.Random.Range(1, count)) % count;

            _lastFootstepClipIndex = index;
            _lastFootstepAudioAt = Time.time;
            UpdateFootstepOcclusion();
            _footstepSource.pitch = UnityEngine.Random.Range(0.94f, 1.07f) + (leftFoot ? -0.008f : 0.008f);
            _footstepSource.PlayOneShot(
                ContractAssetLoader.BlacksiteFootstepClips[index],
                UnityEngine.Random.Range(0.32f, 0.42f));

            AudioClip[] gear = ContractAssetLoader.BlacksiteGearFoleyClips;
            if (gear == null || gear.Length == 0)
                return;
            int gearIndex = UnityEngine.Random.Range(0, gear.Length);
            if (gear.Length > 1 && gearIndex == _lastGearClipIndex)
                gearIndex = (gearIndex + UnityEngine.Random.Range(1, gear.Length)) % gear.Length;
            _lastGearClipIndex = gearIndex;
            _footstepSource.PlayOneShot(gear[gearIndex], UnityEngine.Random.Range(0.10f, 0.16f));
        }

        private void UpdateFootstepOcclusion()
        {
            if (_footstepLowPass == null || _footstepSource == null)
                return;
            PlayerControllerB listener = GameNetworkManager.Instance?.localPlayerController;
            if (listener == null)
            {
                _footstepLowPass.enabled = false;
                return;
            }

            Vector3 origin = _footstepSource.transform.position + Vector3.up * 0.18f;
            Vector3 target = listener.gameplayCamera != null
                ? listener.gameplayCamera.transform.position
                : listener.transform.position + Vector3.up * 1.5f;
            Vector3 delta = target - origin;
            bool occluded = false;
            if (delta.sqrMagnitude > 0.04f)
            {
                int mask = StartOfRound.Instance != null
                    ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                    : ~0;
                BlacksiteHitchTracer.CountPhysicsQuery();
                RaycastHit[] hits = Physics.RaycastAll(
                    origin,
                    delta.normalized,
                    delta.magnitude,
                    mask,
                    QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider collider = hits[i].collider;
                    if (collider == null
                        || collider.transform.IsChildOf(transform)
                        || collider.GetComponentInParent<PlayerControllerB>() == listener)
                    {
                        continue;
                    }
                    occluded = true;
                    break;
                }
            }
            _footstepLowPass.cutoffFrequency = occluded ? 1800f : 22000f;
            _footstepLowPass.enabled = occluded;
        }

        private void RequestSquadVoice(
            BlacksiteVoiceEvent voiceEvent,
            bool critical = false,
            bool transition = false,
            int transitionToken = 0,
            float volume = 0.76f,
            bool ambient = false,
            bool immediate = false)
        {
            if (!IsServerAuthority() || !TryGetSquadDirector(out BlacksiteSquadDirector director))
                return;
            director.RequestVoice(
                voiceEvent,
                this,
                critical,
                transition,
                transitionToken,
                volume,
                ambient,
                immediate);
        }

        /// <summary>
        /// #1011 A3: the call-out a rifleman makes when he refuses a round because the contact is
        /// attached to a squadmate. It reuses <see cref="BlacksiteVoiceEvent.MonsterContact"/> - the
        /// squad's existing monster alert line - rather than adding a bundle event, and raises it as
        /// a TRANSITION keyed on the captor, so one capture produces one call-out however many
        /// members hold fire on it and a second capture by a different monster still speaks.
        ///
        /// The token is derived from the captor's network id and forced negative, because the
        /// other MonsterContact sender keys its transitions on the director's own ascending
        /// counter: a negative key cannot collide with a positive one, so a rescue call-out can
        /// never silently consume an ordinary contact bark or be consumed by one.
        /// </summary>
        internal void ReportAllyAttachedToSquad(EnemyAI captor)
        {
            if (captor == null) return;
            int token = -(int)(captor.NetworkObjectId % int.MaxValue) - 1;
            RequestSquadVoice(
                BlacksiteVoiceEvent.MonsterContact,
                critical: true,
                transition: true,
                transitionToken: token,
                volume: 0.84f);
        }

        // The one line that is worthless late: it has to beat the fuse, so it bypasses the
        // channel queue. One request per grenade keeps that from being a spam channel.


        internal void ReportKillClearToSquad()
        {
            if (IsServerAuthority() && TryGetSquadDirector(out BlacksiteSquadDirector director))
                director.ReportKillClear(this);
        }

        // #314 D9: only the Support gunner suppresses, and the Leader inherits it when Support is
        // dead, so the squad never devolves into six soldiers hosing a remembered point.
        internal bool IsSuppressionFireRole
        {
            get
            {
                if (_tacticalRole == BlacksiteSoldierTacticalRole.Support)
                    return true;
                return _tacticalRole == BlacksiteSoldierTacticalRole.Leader
                    && TryGetSquadDirector(out BlacksiteSquadDirector director)
                    && !director.HasLivingMemberWithRole(BlacksiteSoldierTacticalRole.Support);
            }
        }

        internal static float GetPlanarSpeed(PlayerControllerB player)
        {
            return GetPlayerHorizontalSpeed(player);
        }

        // #314 suggestion 6: roles are re-derived over the survivors on every casualty, so the
        // squad always has a Leader. Formation offsets follow through BuildMemberOrder.
        // Returns whether the soldier is authoritatively in that role afterwards, so the director's
        // MemberRecord is never left claiming a role the soldier refused to take.
        internal bool ApplyReassignedTacticalRoleServer(BlacksiteSoldierTacticalRole role)
        {
            if (!IsServerAuthority()
                || isEnemyDead
                || !_spawnState.Value.IsConfigured
                || _spawnState.Value.IsDead)
            {
                return false;
            }

            // Already there: only the caller's record disagreed, so let it sync.
            if (_tacticalRole == role)
                return true;

            BlacksiteSoldierSpawnState state = _spawnState.Value;
            state.TacticalRoleValue = (byte)role;
            _spawnState.Value = state;
            _tacticalRole = role;
            gameObject.name = $"BlacksiteRetrieval_{_tacticalRole}_{_index + 1}";
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Soldier {_index} reassigned to {role} after a squad casualty.");
            return true;
        }

        // #314 acquisition ramp: he calls the target before he shoots it, using an existing
        // tactical line rather than a dedicated clip.
        internal void RequestAcquisitionVoice()
        {
            RequestSquadVoice(BlacksiteVoiceEvent.TacticalAction, volume: 0.66f);
        }

        internal void PresentSquadVoiceServer(
            BlacksiteVoiceEvent voiceEvent,
            int clipIndex,
            float volume,
            float pitch)
        {
            if (!IsServerAuthority())
                return;
            PlaySquadVoiceLocal(voiceEvent, clipIndex, volume, pitch);
            if (NetworkObject != null && NetworkObject.IsSpawned)
                PlaySquadVoiceClientRpc((int)voiceEvent, clipIndex, volume, pitch);
        }

        [ClientRpc]
        private void PlaySquadVoiceClientRpc(int voiceEvent, int clipIndex, float volume, float pitch)
        {
            if (IsServerAuthority())
                return;
            PlaySquadVoiceLocal((BlacksiteVoiceEvent)voiceEvent, clipIndex, volume, pitch);
        }

        private void PlaySquadVoiceLocal(
            BlacksiteVoiceEvent voiceEvent,
            int clipIndex,
            float volume,
            float pitch)
        {
            AudioClip[] clips = ContractAssetLoader.GetBlacksiteVoiceClips(voiceEvent);
            if (clips == null || clips.Length == 0 || _voiceSource == null)
                return;
            AudioClip clip = clips[Mathf.Clamp(clipIndex, 0, clips.Length - 1)];
            if (clip == null)
                return;
            _voiceSource.pitch = Mathf.Clamp(pitch, 0.85f, 1.15f);
            _voiceSource.PlayOneShot(clip, Mathf.Clamp01(volume));
        }

        /// <summary>
        /// #1066 U8: give this soldier the death clip vanilla's own kill path plays.
        ///
        /// EnemyAI.KillEnemy calls creatureVoice.PlayOneShot(dieSFX) with no null check, and the
        /// Blacksite soldier's dieSFX was never assigned - the 2026-09-15 session logged six
        /// "PlayOneShot was called with a null AudioClip" warnings, each immediately before vanilla's
        /// "Playing death sound for enemy: Blacksite Soldier". The clips themselves exist (the
        /// bootstrap reports death=7); only the field was unassigned.
        ///
        /// The clip is the seeded per-member one HandleDeadState already picked, so the squad keeps
        /// its seven-clip variety rather than every soldier dying on Death_01, and it is re-derived
        /// wherever the seed is written - the server's own init and the replicated spawn state - so
        /// a client and a late joiner resolve the same clip as the host.
        /// </summary>
        private void RefreshDeathSfx()
        {
            AudioClip[] deathClips = ContractAssetLoader.BlacksiteDeathReactionClips;
            if (deathClips == null || deathClips.Length == 0)
                return;
            dieSFX = deathClips[(int)((uint)_animationSeed % (uint)deathClips.Length)];
        }

        private void PlayReactionClipLocal(AudioClip clip, float volume, float pitch)
        {
            if (clip == null || _voiceSource == null)
                return;
            _voiceSource.pitch = Mathf.Clamp(pitch, 0.85f, 1.15f);
            _voiceSource.PlayOneShot(clip, Mathf.Clamp01(volume));
        }

        private void PlayAudioCueNetworked(BlacksiteAudioCue cue, int clipIndex, float volume, float pitch)
        {
            PlayAudioCueLocal(cue, clipIndex, volume, pitch);
            if (NetworkObject != null && NetworkObject.IsSpawned && IsServerAuthority())
                PlayAudioCueClientRpc((int)cue, clipIndex, volume, pitch);
        }

        [ClientRpc]
        private void PlayAudioCueClientRpc(int cue, int clipIndex, float volume, float pitch)
        {
            if (IsServerAuthority())
                return;
            PlayAudioCueLocal((BlacksiteAudioCue)cue, clipIndex, volume, pitch);
        }

        private void PlayAudioCueLocal(BlacksiteAudioCue cue, int clipIndex, float volume, float pitch)
        {
            // #316: this peer's own soldier carries a hosted MX16A4 with its own fire audio, which
            // PlayMuzzleFlashLocal already plays per round. Dropping the shot cue here - rather than
            // at the sender - keeps the decision local, so a peer without the pack still hears the
            // built-in gunshot even when the server has one, and vice versa.
            if ((cue == BlacksiteAudioCue.SingleShot || cue == BlacksiteAudioCue.BurstShot)
                && _weaponVisual != null
                && _weaponVisual.HasFireAudio)
            {
                return;
            }

            AudioClip[] clips = GetCueClips(cue);
            AudioClip clip = null;
            if (clips != null && clips.Length > 0)
                clip = clips[Mathf.Clamp(clipIndex, 0, clips.Length - 1)];
            if (clip == null && cue == BlacksiteAudioCue.SingleShot)
                clip = GetShotClip();
            if (clip == null)
                return;

            AudioSource source = IsVoiceCue(cue) ? (_voiceSource ?? _audioSource) : _audioSource;
            if (source == null)
                return;

            // #929: the rifle read quiet because the authored gunshot clips sit below full scale and
            // nothing scaled them, then were played at 0.9/0.95. The gain is now the measured distance
            // between the clip's own peak and the -1 dBFS ceiling, applied once to a cached copy, and
            // the presented volume is 1.0 so the carry curve alone decides what distance costs.
            if (cue == BlacksiteAudioCue.SingleShot || cue == BlacksiteAudioCue.BurstShot)
            {
                clip = BlacksiteAudioPolicy.NormalizedShot(clip) ?? clip;
                volume = 1f;
            }

            source.pitch = Mathf.Clamp(pitch, 0.75f, 1.25f);
            source.PlayOneShot(clip, volume);
        }

        /// <summary>
        /// #929. Put the rig's weapon source on the facility carry path. The prefab serialized it as
        /// a bare AudioSource: no mixer group (so no occlusion low-pass, no room reverb, no volume
        /// slider) and whatever rolloff it happened to carry. Routed once per bound source, because
        /// a re-bind to the same source must not re-log or re-add anything.
        /// </summary>
        private void ConfigureWeaponAudio()
        {
            if (_audioSource == null || ReferenceEquals(_weaponAudioRouted, _audioSource))
                return;
            _weaponAudioRouted = _audioSource;
            _weaponPropagation = BlacksiteAudioPolicy.ConfigureWeaponSource(_audioSource, MemberIndex);
        }

        private static bool IsVoiceCue(BlacksiteAudioCue cue)
        {
            return cue == BlacksiteAudioCue.VoiceAffirmative
                || cue == BlacksiteAudioCue.VoiceContact
                || cue == BlacksiteAudioCue.VoiceCommand
                || cue == BlacksiteAudioCue.VoiceDeploy
                || cue == BlacksiteAudioCue.VoiceHit
                || cue == BlacksiteAudioCue.VoiceRetreat;
        }

        private static AudioClip[] GetCueClips(BlacksiteAudioCue cue)
        {
            switch (cue)
            {
                case BlacksiteAudioCue.SingleShot:
                    return ContractAssetLoader.BlacksiteGunshotSingleClips.Length > 0
                        ? ContractAssetLoader.BlacksiteGunshotSingleClips
                        : new[] { GetShotClip() };
                case BlacksiteAudioCue.BurstShot:
                    return ContractAssetLoader.BlacksiteGunshotBurstClip != null
                        ? new[] { ContractAssetLoader.BlacksiteGunshotBurstClip }
                        : ContractAssetLoader.BlacksiteGunshotSingleClips;
                case BlacksiteAudioCue.Grenade:
                    return ContractAssetLoader.BlacksiteGrenadeClip != null
                        ? new[] { ContractAssetLoader.BlacksiteGrenadeClip }
                        : Array.Empty<AudioClip>();
                case BlacksiteAudioCue.Footstep:
                    return ContractAssetLoader.BlacksiteFootstepClips;
                case BlacksiteAudioCue.VoiceAffirmative:
                    return ContractAssetLoader.BlacksiteVoiceAffirmativeClips;
                case BlacksiteAudioCue.VoiceContact:
                    return ContractAssetLoader.BlacksiteVoiceContactClips;
                case BlacksiteAudioCue.VoiceCommand:
                    return ContractAssetLoader.BlacksiteVoiceCommandClips;
                case BlacksiteAudioCue.VoiceDeploy:
                    return ContractAssetLoader.BlacksiteVoiceDeployClips;
                case BlacksiteAudioCue.VoiceHit:
                    return ContractAssetLoader.BlacksiteVoiceHitClips;
                case BlacksiteAudioCue.VoiceRetreat:
                    return ContractAssetLoader.BlacksiteVoiceRetreatClips;
                default:
                    return Array.Empty<AudioClip>();
            }
        }

        private static AudioClip GetShotClip()
        {
            if (_shotClip != null)
                return _shotClip;

            const int sampleRate = 24000;
            const float duration = 0.18f;
            int samples = Mathf.CeilToInt(sampleRate * duration);
            float[] data = new float[samples];
            var random = new System.Random(7139);
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)sampleRate;
                float envelope = Mathf.Exp(-t * 26f);
                float noise = (float)(random.NextDouble() * 2.0 - 1.0);
                float crack = Mathf.Sin(2f * Mathf.PI * 96f * t) * 0.45f;
                data[i] = Mathf.Clamp((noise * 0.82f + crack) * envelope, -1f, 1f);
            }

            _shotClip = AudioClip.Create("Blacksite rifle shot", samples, 1, sampleRate, stream: false);
            _shotClip.SetData(data, 0);
            return _shotClip;
        }

        private void TryTrigger(int parameterHash)
        {
            if (!HasParameter(parameterHash, AnimatorControllerParameterType.Trigger))
                return;
            // A trigger left latched from a culled or interrupted transition would fire
            // the moment the state becomes reachable again.
            _animator.ResetTrigger(parameterHash);
            _animator.SetTrigger(parameterHash);
        }

        // #874 S3.3: a gesture accepted a frame before the hands were taken stays latched inside the
        // animator and fires the moment the gesture state becomes reachable again, which is the
        // frame the rifle comes back. Clearing it where the hands are lost is the only way a
        // suppressed gesture stays suppressed.
        private void TryResetTrigger(int parameterHash)
        {
            if (HasParameter(parameterHash, AnimatorControllerParameterType.Trigger))
                _animator.ResetTrigger(parameterHash);
        }

        internal void ApplyAuthoritativePlayerDamage(PlayerControllerB victim, int damage, CauseOfDeath causeOfDeath)
        {
            if (damage <= 0 || !IsPlayerCombatEligible(victim))
                return;

            // PlayerControllerB.DamagePlayer is victim-owner-only (it early-returns unless IsOwner),
            // so the server announces the hit and only the owning client invokes vanilla damage.
            if (!IsServerAuthority())
                return;

            int playerIndex = GetPlayerIndex(victim);
            if (playerIndex < 0)
                return;

            if (NetworkObject == null || !NetworkObject.IsSpawned)
            {
                if (victim.IsOwner)
                    DamageOwnedVictim(victim, damage, causeOfDeath);
                return;
            }

            ApplyAuthoritativePlayerDamageClientRpc(
                playerIndex,
                damage,
                (int)causeOfDeath,
                ++_playerDamageSerial);
        }

        [ClientRpc]
        private void ApplyAuthoritativePlayerDamageClientRpc(int playerIndex, int damage, int causeOfDeath, int serial)
        {
            if (serial <= _lastAppliedPlayerDamageSerial)
                return;
            _lastAppliedPlayerDamageSerial = serial;

            PlayerControllerB victim = ResolvePlayerByIndex(playerIndex);
            if (!IsPlayerCombatEligible(victim) || !victim.IsOwner)
                return;

            DamageOwnedVictim(victim, damage, (CauseOfDeath)causeOfDeath);
        }

        /// <summary>
        /// #1209: the one place a soldier hit becomes vanilla damage, reached only on the victim's
        /// own client and only after the serial and owner checks. A gunshot passes through that
        /// player's own Bullet Resistance here, exactly once: the host chose the hit and its raw
        /// damage, but only this client knows the rank. The baton and anything else pass through
        /// unchanged.
        /// </summary>
        private static void DamageOwnedVictim(PlayerControllerB victim, int damage, CauseOfDeath causeOfDeath)
        {
            bool gunshot = causeOfDeath == CauseOfDeath.Gunshots;
            int applied = BlacksiteCombatPolicy.VictimOwnedPlayerDamage(
                damage,
                gunshot,
                gunshot ? BlacksiteUpgradesWeaponBridge.IncomingFirearmResistanceOrNull() : null);
            if (applied > 0)
                victim.DamagePlayer(applied, hasDamageSFX: true, callRPC: true, causeOfDeath);
        }

        private static int GetPlayerIndex(PlayerControllerB player)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || player == null)
                return -1;

            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] == player)
                    return i;
            }

            return -1;
        }

        private static PlayerControllerB ResolvePlayerByIndex(int playerIndex)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || playerIndex < 0 || playerIndex >= players.Length)
                return null;
            return players[playerIndex];
        }

        private float _lastCombatHitPoseAt = float.NegativeInfinity;
        private void PresentRifleHit(bool hardStun, float now)
        {
            if (_guardTraversalActive) return; // #1283 S3a: no cosmetic flinch in the rifle's own Jump or on the rope; health, recoil audio and stun still process.
            bool engaged = (_poseFlags.Value & AimingPoseFlag) != 0 || (_combatController?.HasCombatIntent ?? false);
            if (!BlacksiteCombatPolicy.AllowHitPose(hardStun, engaged, now, _lastCombatHitPoseAt)) return;
            _lastCombatHitPoseAt = now;
            TryTrigger(HitHash); // Health, recoil audio and real stun still process every damage event.
        }
        // #874 S3.3: both authored gestures are two-handed rifle poses on layer 0, so both need the
        // hands. Cargo, the carry handback, traversal and every incapacitated state forbid them on
        // either peer; only Signal additionally yields to an engaged weapon. #874 Q7 (E1): and both
        // are full-body base-layer states, so a moving member is refused them rather than having
        // its legs replaced (BlacksiteStancePolicy.AllowsGesture).
        private bool IsGestureParameter(string parameter)
            => parameter == SignalParameter || parameter == RadioParameter;
        private bool AllowsGesture(string parameter) => !IsGestureParameter(parameter)
            || BlacksiteStancePolicy.AllowsGesture(parameter == SignalParameter, UsesBaton, CurrentHandOwner,
                (_poseFlags.Value & AimingPoseFlag) != 0 || (_combatController?.HasCombatIntent ?? false),
                _normalizedMoveSpeed);
        private void TriggerAnimationNetworked(string parameter)
        {
            if (string.IsNullOrWhiteSpace(parameter) || !AllowsGesture(parameter))
                return;
            TryTrigger(Animator.StringToHash(parameter));
            if (NetworkObject != null && NetworkObject.IsSpawned && IsServerAuthority())
                TriggerAnimationClientRpc(parameter);
        }

        [ClientRpc]
        private void TriggerAnimationClientRpc(string parameter)
        {
            if (IsServerAuthority() || string.IsNullOrWhiteSpace(parameter) || !AllowsGesture(parameter))
                return;
            TryTrigger(Animator.StringToHash(parameter));
        }

        private void TrySetFloat(int parameterHash, float value)
        {
            if (HasParameter(parameterHash, AnimatorControllerParameterType.Float))
                _animator.SetFloat(parameterHash, value);
        }

        private void TrySetFloatDamped(
            int parameterHash,
            float value,
            float dampTime,
            float deltaTime)
        {
            if (HasParameter(parameterHash, AnimatorControllerParameterType.Float))
            {
                _animator.SetFloat(
                    parameterHash,
                    value,
                    Mathf.Max(0f, dampTime),
                    Mathf.Max(0.001f, deltaTime));
            }
        }

        private void TrySetBool(int parameterHash, bool value)
        {
            if (HasParameter(parameterHash, AnimatorControllerParameterType.Bool))
                _animator.SetBool(parameterHash, value);
        }

        private void TrySetInteger(int parameterHash, int value)
        {
            if (HasParameter(parameterHash, AnimatorControllerParameterType.Int))
                _animator.SetInteger(parameterHash, value);
        }

        private bool HasParameter(int parameterHash, AnimatorControllerParameterType type)
        {
            if (_animator == null)
                return false;

            RefreshAnimatorParameterCache();
            return _animatorParameters.TryGetValue(parameterHash, out AnimatorControllerParameterType cached)
                && cached == type;
        }

        private void RefreshAnimatorParameterCache()
        {
            // Animator.parameters allocates a fresh array per read, so the layout is
            // cached until the controller behind it is swapped or re-bound.
            RuntimeAnimatorController controller = _animator.runtimeAnimatorController;
            if (ReferenceEquals(controller, _cachedParameterController) && _parameterCacheValid)
                return;

            _cachedParameterController = controller;
            _parameterCacheValid = true;
            _animatorParameters.Clear();
            if (controller == null)
                return;

            AnimatorControllerParameter[] parameters = _animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                AnimatorControllerParameter parameter = parameters[i];
                if (parameter != null)
                    _animatorParameters[parameter.nameHash] = parameter.type;
            }
        }

        private void InvalidateAnimatorParameterCache()
        {
            _cachedParameterController = null;
            _parameterCacheValid = false;
            _animatorParameters.Clear();
            _animatorStates.Clear();
        }

        // #874 S5.2 (#873): capability by state. BlacksiteEquipmentBuild.cs declares the Climbing
        // Bool parameter on the baton controller and authors no state or transition
        // that consumes it, so the old HasParameter probe was a false positive on every baton
        // member; the shipped rifle controller has neither the parameter nor the states. Asking the
        // animator whether it can actually play the clip is the only test that cannot lie.
        internal bool HasAnimatorState(int stateHash)
        {
            if (_animator == null || _animator.runtimeAnimatorController == null)
                return false;

            RefreshAnimatorParameterCache();
            if (_animatorStates.TryGetValue(stateHash, out bool cached))
                return cached;

            bool present;
            try
            {
                present = _animator.HasState(0, stateHash);
            }
            catch
            {
                // An animator that has not bound yet answers nothing; the substitute is correct
                // until it has, and the cache is dropped with the controller.
                return false;
            }

            _animatorStates[stateHash] = present;
            return present;
        }

        /// <summary>
        /// #874 S5.2 (#873): the one ladder resolver both loadouts use. It never returns a rope
        /// state: the two rope kinds are resolved before this is reached.
        ///
        /// #874 F07: it returns a POSE, not a bare state, and the absence of the authored climb is
        /// part of it. Both callers used to receive the string <c>StandingPatrol</c> for two
        /// unrelated facts - "this controller has no climb clip" and "this member is walking to a
        /// ladder foot" - and the baton writer could not tell them apart at all, so a member
        /// climbing an 8.51 m column with a stale sidecar was presented as a completed climb in a
        /// slow walk with nothing said about it anywhere the game was not already running with
        /// diagnostics on. The capability now travels with the state
        /// (<see cref="BlacksiteLadderPose.ClimbAuthored"/>), and the absence is REPORTED whether
        /// or not diagnostics are enabled, because a missing authored state is a build defect and
        /// not a diagnostic.
        /// </summary>
        private BlacksiteLadderPose ResolveLadderState(string mountState, string climbState, string descendState)
        {
            if (!HasAnimatorState(Animator.StringToHash(climbState)))
            {
                ReportLadderPresentation(false, climbState);
                return BlacksiteTraversalPresentation.LadderPoseForPhase(
                    LadderPhase, TraversalKind, climbState, climbState, descendState, climbState, climbAuthored: false);
            }

            ReportLadderPresentation(true, climbState);
            // A descent is the ascent clip authored once more at the negated rate; a controller
            // without that second state plays the ascent forwards rather than freezing, because
            // Animator.speed refuses a negative.
            string riseState = BlacksiteTraversalPresentation.LadderRiseState(TraversalKind, climbState, descendState);
            if (riseState != climbState && !HasAnimatorState(Animator.StringToHash(riseState)))
                riseState = climbState;
            if (!HasAnimatorState(Animator.StringToHash(mountState)))
                mountState = riseState;
            // #1283 S4: an ascent's top step plays the top-out where authored, else the rise state held still.
            string topOutState = _ladderTopsOut ? BlacksiteTraversalPresentation.RifleLadderTopOut : riseState;
            return BlacksiteTraversalPresentation.LadderPoseForPhase(
                LadderPhase, TraversalKind, mountState, riseState, riseState, topOutState, climbAuthored: true);
        }

        // #991: the jump pose, resolved the way the ladder pose is - by asking the bound controller
        // whether it can actually play the clip, never by asking whether a parameter exists. A
        // sidecar built before this issue has neither state, and the member then slides across the
        // arc in its locomotion clip with one warning, which is a look defect and not a crash.
        private string ResolveJumpState(string standingState, string runningState)
        {
            string state = BlacksiteTraversalPresentation.JumpState(TraversalKind, standingState, runningState);
            if (HasAnimatorState(Animator.StringToHash(state)))
            {
                ReportJumpPresentation(state, true);
                return state;
            }
            // A controller with the standing jump but not the running one plays the standing clip,
            // exactly as a ladder without a descend state plays the ascent forwards.
            if (state != standingState && HasAnimatorState(Animator.StringToHash(standingState)))
            {
                ReportJumpPresentation(standingState, true);
                return standingState;
            }
            ReportJumpPresentation(state, false);
            return BlacksiteTraversalPresentation.Fallback;
        }

        private void ReportJumpPresentation(string state, bool present)
        {
            if (_jumpPresentationReported || !BlacksiteDiagnostics.Enabled)
                return;

            _jumpPresentationReported = true;
            string controller = _animator != null && _animator.runtimeAnimatorController != null
                ? _animator.runtimeAnimatorController.name
                : BlacksiteDiagnosticFormat.None;
            BlacksiteDiagnostics.Log(
                "Jump",
                $"event=presentation, member={MemberIndex}, controller={controller}, state={state}, "
                + $"jumpState={(present ? "present" : "absent")}, "
                + $"fallback={(present ? BlacksiteDiagnosticFormat.None : "slide")}");
        }


        /// <summary>
        /// Once per member per round, on the first ladder or climb leg it presents.
        ///
        /// #874 F07: the ABSENT half is no longer gated on diagnostics. A controller that cannot
        /// play the climb it has been asked for is a build defect - a stale sidecar or a stale
        /// contracts bundle - and the whole of its visible consequence is a member ascending a
        /// ladder in a walk cycle, which reads as working. Gated behind
        /// <c>BlacksiteDiagnostics.Enabled</c>, the one line that said so was absent from exactly
        /// the runs nobody was watching. The warning names the state that is missing, so a reader
        /// knows which asset to rebuild; the [UAT] line keeps its shape and its field order for
        /// the score script, and is still diagnostics-gated on the PRESENT side.
        /// </summary>
        private void ReportLadderPresentation(bool present, string climbState)
        {
            if (_ladderPresentationReported)
                return;
            if (present && !BlacksiteDiagnostics.Enabled)
                return;

            _ladderPresentationReported = true;
            string controller = _animator != null && _animator.runtimeAnimatorController != null
                ? _animator.runtimeAnimatorController.name
                : BlacksiteDiagnosticFormat.None;
            if (!present)
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Blacksite] Soldier controller '{controller}' has no '{climbState}' state (member {MemberIndex});"
                    + $" the climb shows plain {BlacksiteTraversalPresentation.Fallback} and is NOT an authored climb. Rebuild the bundle that carries it.");
            if (BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.Log(
                    "WorldLadder",
                    $"event=presentation, member={MemberIndex}, controller={controller}, "
                    + $"ladderState={(present ? "present" : "absent")}, "
                    + $"fallback={(present ? BlacksiteDiagnosticFormat.None : BlacksiteTraversalPresentation.Fallback)}");
        }

        // #874 S5.2 (#873): the rifle ladder pose; stale bundles keep the diagnosed substitute.
        // #874 F07: it plays whatever the pose resolves to and returns whether that is a ladder
        // (BlacksiteLadderPose.PresentsLadder). A world ladder's approach and an unauthored
        // climb resolve to the locomotion substitute and are still driven: an early return would leave a
        // member that has left the ladder in its rise clip. #1283 S4: the dismount (the step over the top)
        // plays LadderTopOut where authored, else holds the rise state at ClimbRate 0. A LadderUp's
        // approach and deck legs never reach here: ClimbingPoseFlag is off on them (ShipCrossingTakesHands),
        // and the animator leaves LadderClimb through its authored `IfNot Climbing` exit.
        private bool DriveRifleLadderState()
        {
            BlacksiteLadderPose pose = ResolveLadderState(
                BlacksiteTraversalPresentation.RifleLadderMount,
                BlacksiteTraversalPresentation.RifleLadderClimb,
                BlacksiteTraversalPresentation.RifleLadderDescend);
            int hash = Animator.StringToHash(pose.State);
            if (!HasAnimatorState(hash)) return false;
            if (_animator.GetCurrentAnimatorStateInfo(0).shortNameHash == hash)
                return pose.PresentsLadder;
            if (_animator.IsInTransition(0) && _animator.GetNextAnimatorStateInfo(0).shortNameHash == hash)
                return pose.PresentsLadder;
            // #874 Q7: the substitute for an approach or a dismount is the locomotion tree, and the
            // rest/aimed family already is it; cutting to StandingPatrol from there would bypass
            // AimToRest and SlungToRest. Only a member leaving a ladder state is cut back.
            if (!pose.PresentsLadder && InStanceOwnedState())
                return false;
            _animator.CrossFadeInFixedTime(hash, .1f, 0);
            return pose.PresentsLadder;
        }

        private void LogAnimatorStateOnce()
        {
            if (_animationLogged || _animator == null)
                return;

            _animationLogged = true;
            Avatar avatar = _animator.avatar;
            string controllerName = _animator.runtimeAnimatorController != null ? _animator.runtimeAnimatorController.name : "null";
            string avatarName = avatar != null ? avatar.name : "null";
            bool avatarHuman = avatar != null && avatar.isHuman;
            bool avatarValid = avatar != null && avatar.isValid;
            int aimLayer = BlacksiteSoldierAimPose.ResolveAimLayer(_animator);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Soldier animator controller={controllerName}, avatar={avatarName}, human={avatarHuman}, valid={avatarValid}, layers={_animator.layerCount}, params={_animator.parameterCount}, aimLayer={aimLayer}.");
            if (!HasAnimatorState(RifleLadderClimbStateHash))
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Soldier controller has no LadderClimb state; a ladder climb shows plain {BlacksiteTraversalPresentation.Fallback}. Rebuild lgucontracts.bundle.");
            if (aimLayer < 0)
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.Blacksite] Soldier controller has no 'Aim' layer (#315); the upper-body aim overlay is off and only the look-at and torso pitch can run. Rebuild lgucontracts.bundle.");
            }
        }

        private void EnsureCombatController()
        {
            if (_combatController == null)
                _combatController = new BlacksiteCombatController(this);
        }

        private void EnsureAnimationEventFootsteps()
        {
            if (_animator == null || _animator.runtimeAnimatorController == null)
                return;

            var forwarder = _animator.GetComponent<BlacksiteAnimationEventForwarder>();
            if (forwarder == null)
                forwarder = _animator.gameObject.AddComponent<BlacksiteAnimationEventForwarder>();
            forwarder.Init(this);

            int configuredClips = 0;
            foreach (AnimationClip clip in _animator.runtimeAnimatorController.animationClips)
            {
                if (!IsLocomotionFootstepClip(clip))
                    continue;

                if (FootstepEventClipIds.Contains(clip.GetInstanceID()) || ClipHasFootstepEvent(clip))
                {
                    configuredClips++;
                    continue;
                }

                try
                {
                    float length = Mathf.Max(clip.length, 0.1f);
                    clip.AddEvent(new AnimationEvent
                    {
                        functionName = nameof(BlacksiteAnimationEventForwarder.OnBlacksiteLeftStep),
                        time = Mathf.Clamp(length * FootstepCyclePointA, 0.01f, Mathf.Max(0.01f, length - 0.01f))
                    });
                    clip.AddEvent(new AnimationEvent
                    {
                        functionName = nameof(BlacksiteAnimationEventForwarder.OnBlacksiteRightStep),
                        time = Mathf.Clamp(length * FootstepCyclePointB, 0.01f, Mathf.Max(0.01f, length - 0.01f))
                    });
                    FootstepEventClipIds.Add(clip.GetInstanceID());
                    configuredClips++;
                }
                catch (Exception e)
                {
                    ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Soldier footstep AnimationEvent injection skipped for '{clip.name}': {e.Message}");
                }
            }

            _footstepEventsEnabled = configuredClips > 0;
            EnsureCombatAnimationEvents();
        }

        private void EnsureCombatAnimationEvents()
        {
            if (_animator == null || _animator.runtimeAnimatorController == null)
                return;

            foreach (AnimationClip clip in _animator.runtimeAnimatorController.animationClips)
            {
                if (clip == null || CombatEventClipIds.Contains(clip.GetInstanceID()))
                    continue;

                string name = clip.name ?? string.Empty;
                if (name.IndexOf("Fire", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Shoot", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    TryAddAnimationEvent(
                        clip,
                        nameof(BlacksiteAnimationEventForwarder.OnBlacksiteFire),
                        0.32f);
                    TryAddAnimationEvent(
                        clip,
                        nameof(BlacksiteAnimationEventForwarder.OnBlacksiteActionComplete),
                        0.9f);
                }
                else if (name.IndexOf("Reload", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    TryAddAnimationEvent(
                        clip,
                        nameof(BlacksiteAnimationEventForwarder.OnBlacksiteMagazineRelease),
                        0.28f);
                    TryAddAnimationEvent(
                        clip,
                        nameof(BlacksiteAnimationEventForwarder.OnBlacksiteMagazineInsert),
                        0.72f);
                    TryAddAnimationEvent(
                        clip,
                        nameof(BlacksiteAnimationEventForwarder.OnBlacksiteActionComplete),
                        0.94f);
                }
                CombatEventClipIds.Add(clip.GetInstanceID());
            }
        }

        private static void TryAddAnimationEvent(AnimationClip clip, string eventName, float normalizedTime)
        {
            if (clip == null || ClipHasAnimationEvent(clip, eventName))
                return;
            try
            {
                float length = Mathf.Max(clip.length, 0.1f);
                clip.AddEvent(new AnimationEvent
                {
                    functionName = eventName,
                    time = Mathf.Clamp(length * normalizedTime, 0.01f, Mathf.Max(0.01f, length - 0.01f))
                });
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.Blacksite] AnimationEvent '{eventName}' injection skipped for '{clip.name}': {e.Message}");
            }
        }

        private static bool IsLocomotionFootstepClip(AnimationClip clip)
        {
            if (clip == null)
                return false;

            string name = clip.name ?? string.Empty;
            return name.IndexOf("CombatWalk", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Locomotion", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Walk", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Run", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ClipHasFootstepEvent(AnimationClip clip)
        {
            try
            {
                AnimationEvent[] events = clip.events;
                if (events == null)
                    return false;

                for (int i = 0; i < events.Length; i++)
                {
                    if (events[i] != null
                        && (events[i].functionName == nameof(BlacksiteAnimationEventForwarder.OnBlacksiteFootstep)
                            || events[i].functionName == nameof(BlacksiteAnimationEventForwarder.OnBlacksiteLeftStep)
                            || events[i].functionName == nameof(BlacksiteAnimationEventForwarder.OnBlacksiteRightStep)))
                        return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static void ResolveVoiceSpeakingAccessor(Type stateType)
        {
            _voiceSpeakingStateType = stateType;
            _voiceSpeakingAccessor = null;
            _voiceSpeakingProperty = null;
            _voiceSpeakingField = null;
            if (stateType == null)
                return;

            const BindingFlags flags = BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic;
            try
            {
                var state = System.Linq.Expressions.Expression.Parameter(
                    typeof(object),
                    "state");
                var typedState = System.Linq.Expressions.Expression.Convert(state, stateType);
                for (int i = 0; i < VoiceSpeakingMemberNames.Length; i++)
                {
                    string memberName = VoiceSpeakingMemberNames[i];
                    PropertyInfo property = stateType.GetProperty(memberName, flags);
                    if (property != null
                        && property.PropertyType == typeof(bool)
                        && property.GetIndexParameters().Length == 0)
                    {
                        _voiceSpeakingProperty = property;
                        var read = System.Linq.Expressions.Expression.Property(typedState, property);
                        _voiceSpeakingAccessor = System.Linq.Expressions.Expression
                            .Lambda<Func<object, bool>>(read, state)
                            .Compile();
                        return;
                    }

                    FieldInfo field = stateType.GetField(memberName, flags);
                    if (field != null && field.FieldType == typeof(bool))
                    {
                        _voiceSpeakingField = field;
                        var read = System.Linq.Expressions.Expression.Field(typedState, field);
                        _voiceSpeakingAccessor = System.Linq.Expressions.Expression
                            .Lambda<Func<object, bool>>(read, state)
                            .Compile();
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.Blacksite] Voice speaking accessor unavailable for {stateType.FullName}: {e.Message}");
            }
        }

        private static bool ClipHasAnimationEvent(AnimationClip clip, string functionName)
        {
            try
            {
                AnimationEvent[] events = clip.events;
                if (events == null)
                    return false;
                for (int i = 0; i < events.Length; i++)
                {
                    if (events[i] != null && events[i].functionName == functionName)
                        return true;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private void KickstartAnimator()
        {
            if (_animatorKickstarted || _animator == null || _animator.runtimeAnimatorController == null)
                return;

            _animatorKickstarted = true;
            try
            {
                // Rebind resets every parameter, so identity has to be re-stamped after it.
                _animator.Rebind();
                InvalidateAnimatorParameterCache();
                _animator.speed = GetAnimationSpeedMultiplier();
                TrySetInteger(IdentitySeedHash, _animationSeed & int.MaxValue);
                TrySetFloat(AnimationPhaseHash, _animationPhaseOffset);
                TrySetInteger(DeathVariantHash, (int)((uint)_animationSeed % 4u));
                _animator.Update(0.001f);
                // Always attempt the Play: HasState with a bare-name hash is unreliable when the
                // state lives inside a sub-state machine, so it only gates the warning.
                _animator.Play(StandingPatrolStateHash, 0, GetAnimationStartOffset());
                if (!_animator.HasState(0, StandingPatrolStateHash))
                {
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Blacksite] Soldier {_index} animator controller has no 'StandingPatrol' state on layer 0; the authored idle entry is missing.");
                }
                _animator.Update(0.001f);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Soldier animator kickstart skipped: {e.Message}");
            }
        }

        public ThreatType type => BlacksiteMonsterCapabilities.SoldierThreatType;

        public int GetThreatLevel(Vector3 seenByPosition)
        {
            // #947 S12 U1: GetInterestLevel and GetVisibility already answer 0 for a corpse; this one
            // did not, so any native consumer that ranks by threat level alone kept scoring the body.
            if (isEnemyDead || ReplicatedDead) return 0;
            float distance = Vector3.Distance(seenByPosition, transform.position);
            int weaponThreat = _weaponState.Value.State == BlacksiteWeaponState.FiringBurst ? 3 : 2;
            return Mathf.Clamp(weaponThreat + (distance < 6f ? 1 : 0), 1, 4);
        }

        public int GetInterestLevel()
        {
            return isEnemyDead || ReplicatedDead ? 0 : 2;
        }

        public Transform GetThreatLookTransform()
        {
            ResolveAttachmentAnchors();
            return _headAnchor != null ? _headAnchor : transform;
        }

        public Transform GetThreatTransform()
        {
            return transform;
        }

        public Vector3 GetThreatVelocity()
        {
            return _agent != null && _agent.enabled && _agent.isOnNavMesh
                ? _agent.velocity
                : Vector3.zero;
        }

        public float GetVisibility()
        {
            return isEnemyDead || ReplicatedDead || !gameObject.activeInHierarchy ? 0f : 1f;
        }

        public int SendSpecialBehaviour(int id)
        {
            if (IsServerAuthority() && id > 0)
            {
                ReactToStimulusServer(
                    BlacksiteSoldierReactionKind.LoudNoise,
                    transform.position - transform.forward * 2f,
                    ulong.MaxValue,
                    broadcastToSquad: false);
            }
            return 0;
        }

        public GrabbableObject GetHeldObject()
        {
            return BlacksiteHardDriveObjectiveService.TryGetDriveForCarrier(this, out BreachHardDriveItem drive)
                ? drive.PhysicsProp
                : null;
        }

        public bool IsThreatDead()
        {
            return isEnemyDead || ReplicatedDead;
        }

        private float GetAnimationStartOffset()
        {
            return Mathf.Repeat(_animationPhaseOffset, 1f);
        }

        private void UpdateCalibratedAnimationSpeed()
        {
            if (_animator == null || !_animator.isActiveAndEnabled) return;
            if (!_strideCalibrated || UsesBaton || _guardTraversalActive) return;
            var state = _animator.IsInTransition(0) ? _animator.GetNextAnimatorStateInfo(0) : _animator.GetCurrentAnimatorStateInfo(0);
            int hash = state.shortNameHash;
            bool locomotion = hash == StandingPatrolStateHash || hash == CrouchedLocomotionStateHash
                || hash == StandingAimedStateHash;
            // #874 Q7 (E3): the calibrated trees run on the speed table's clock; actions retain
            // authored real-time pacing.
            _animator.speed = locomotion ? BlacksiteLocomotionSpeedTable.AnimatorSpeed(AnimationRoleMoveSpeed) : 1f;
        }

        private float GetAnimationSpeedMultiplier()
        {
            return Mathf.Clamp(
                0.97f + Mathf.Repeat(_animationPhaseOffset, 1f) * 0.08f,
                0.97f,
                1.05f);
        }

        /// <summary>
        /// #874 F08: the gait sample.
        ///
        /// F08 was opened on a "stable state hash", which on its own is evidence of nothing. The
        /// shipped StandingPatrol and StandingAimed states are LOOPING MoveX/MoveZ blend trees
        /// (BlacksiteAimPosePolicy, measured 2026-09-20: twelve locomotion children each, differing
        /// only in their (0,0) centre), so their hash is identically stable for a member sprinting,
        /// strafing and standing still. What separates those three is never the state. It is
        ///
        ///  1. the blend the tree was actually given (moveX=, moveZ=, speed=, moveSpeed=),
        ///  2. the clips that blend actually selected and at what weight (clips=),
        ///  3. the rate the state's own clock is running at (animSpeed=, cycles=, clock=), and
        ///  4. whether an override layer took the body back off the tree afterwards (layers=).
        ///
        /// All four, per member, at a fixed rate, for as long as the run lasts - which is what
        /// "sampleable over sustained loops" means. Two frames cannot distinguish a stopped clock
        /// from a slow one.
        ///
        /// Every float prints as target/held: the value THIS frame asked for, and the value the
        /// animator is holding after <see cref="AnimationFloatDampTime"/> has had its say. A damp
        /// time that cannot keep up with its signal shows up as a held column that never reaches
        /// its target column, and nothing else in this file can see that.
        ///
        /// cycles= is the number the finding actually needs: the state's own normalised time,
        /// advanced per REAL second, across two consecutive samples of the same state. A looping
        /// locomotion state whose cycles= is zero has a stopped clock; one whose cycles= is healthy
        /// while the body slides has a blend, a mask or a layer problem instead. len= converts it
        /// to clip seconds.
        ///
        /// clock= names the owner of _animator.speed on the sampled frame, from the same four
        /// latches the writers arbitrate with. `clock=none` beside an animSpeed that is not 1 is
        /// exactly the "speed left where a previous owner set it" case, in one token.
        ///
        /// yawOffset= is the frame question. The blend is written in the AI ROOT's local frame
        /// (transform.InverseTransformDirection), while the clips play on the Animator, which is a
        /// descendant of the visual root (BlacksiteSoldierRigBinding: VisualRoot's child Animator)
        /// and carries its own rotation. A non-zero yawOffset means moveX/moveZ are being written
        /// in the wrong frame and the legs are walking a direction the body is not.
        /// </summary>
        internal const float GaitSampleIntervalSeconds = 0.2f;
        private float _nextGaitSampleAt;
        private float _lastGaitSampleAt;
        private int _gaitSampleIndex;
        private int _lastGaitStateHash;
        private float _lastGaitStateTime;
        private bool _hasGaitStateTime;
        // Main thread only, filled and drained inside one call, exactly like the baton scratch
        // collections this file already keeps. Nothing here survives the method, and nothing here
        // is touched at all unless diagnostics are on.
        private static readonly List<AnimatorClipInfo> GaitClipScratch = new List<AnimatorClipInfo>(8);
        private static readonly System.Text.StringBuilder GaitLine = new System.Text.StringBuilder(768);

        private void SampleGait(
            float delta,
            Vector3 rawVelocity,
            Vector3 planarVelocity,
            Vector3 localPlanarVelocity,
            float moveX,
            float moveZ,
            float normalizedSpeed,
            float maxSpeed,
            float climbBlend,
            float normalizedTurnRate,
            float turnAngleValue,
            bool agentBound)
        {
            // BlacksiteDiagnostics.Enabled is the caller's guard and is not re-read here.
            if (_animator == null || _animator.runtimeAnimatorController == null)
                return;
            // Host-side. The host is the sole writer of every value below, so a host sample is the
            // writer's own record rather than a rendering of it. The blend normalization and
            // calibrated stride clock now share the host-published role speed on every peer.
            // This diagnostic remains host-only; connected-client presentation needs its own capture.
            if (!IsServerAuthority())
                return;

            float now = Time.time;
            if (now < _nextGaitSampleAt)
                return;
            _nextGaitSampleAt = now + GaitSampleIntervalSeconds;

            int layerCount;
            float animatorSpeed;
            int stateHash = 0;
            float stateTime = 0f;
            float stateLength = 0f;
            bool inTransition = false;
            int nextHash = 0;
            float nextTime = 0f;
            try
            {
                layerCount = _animator.layerCount;
                animatorSpeed = _animator.speed;
                if (layerCount > 0)
                {
                    AnimatorStateInfo current = _animator.GetCurrentAnimatorStateInfo(0);
                    stateHash = current.fullPathHash;
                    stateTime = current.normalizedTime;
                    stateLength = current.length;
                    inTransition = _animator.IsInTransition(0);
                    if (inTransition)
                    {
                        AnimatorStateInfo next = _animator.GetNextAnimatorStateInfo(0);
                        nextHash = next.fullPathHash;
                        nextTime = next.normalizedTime;
                    }
                    _animator.GetCurrentAnimatorClipInfo(0, GaitClipScratch);
                }
                else
                {
                    GaitClipScratch.Clear();
                }
            }
            catch
            {
                // An animator that has not bound yet answers nothing; the next sample will.
                GaitClipScratch.Clear();
                return;
            }

            // Cycles per real second of the SAME state. A state change makes the difference
            // meaningless, so it is reported as none rather than as a number nobody can use.
            float cycles = float.NaN;
            if (_hasGaitStateTime && stateHash == _lastGaitStateHash)
            {
                float span = now - _lastGaitSampleAt;
                if (span > 0.001f)
                    cycles = (stateTime - _lastGaitStateTime) / span;
            }
            _lastGaitStateHash = stateHash;
            _lastGaitStateTime = stateTime;
            _lastGaitSampleAt = now;
            _hasGaitStateTime = true;

            float yawOffset = 0f;
            Transform animatorTransform = _animator.transform;
            if (animatorTransform != null && animatorTransform != transform)
            {
                yawOffset = Vector3.SignedAngle(
                    Vector3.ProjectOnPlane(transform.forward, Vector3.up),
                    Vector3.ProjectOnPlane(animatorTransform.forward, Vector3.up),
                    Vector3.up);
            }

            string clockOwner = UsesBaton ? "baton"
                : _traversalSpeedHeld ? "traversal"
                : _guardTraversalActive ? "shared-traversal"
                : _strideCalibrated ? "calibrated"
                : "none";

            var line = GaitLine;
            line.Clear();
            line.Append("member=").Append(MemberIndex)
                .Append(", i=").Append(_gaitSampleIndex++)
                .Append(", dt=").Append(delta.ToString("0.0000"))
                .Append(", role=").Append(maxSpeed.ToString("0.00"))
                .Append(", damp=").Append(AnimationFloatDampTime.ToString("0.000"));
            // The measurement itself: what the body did, before any normalisation. vRaw is the
            // unsmoothed transform delta and v is what the writes were derived from; on the host
            // they are the same vector, and the pair is printed so a client capture taken with the
            // authority gate lifted shows what ClientVelocitySmoothingSeconds did to it.
            line.Append(", vRaw=").Append(Vector3.ProjectOnPlane(rawVelocity, Vector3.up).ToString("0.00"))
                .Append(", v=").Append(planarVelocity.ToString("0.00"))
                .Append(", speed=").Append(planarVelocity.magnitude.ToString("0.000"))
                .Append(", vLocal=(").Append(localPlanarVelocity.x.ToString("0.00"))
                .Append(", ").Append(localPlanarVelocity.z.ToString("0.00")).Append(')');

            GaitAppendFloat(line, "moveX", moveX, MoveXHash);
            GaitAppendFloat(
                line,
                "moveZ",
                float.IsNaN(climbBlend) ? moveZ : climbBlend,
                MoveZHash);
            GaitAppendFloat(line, "speedParam", float.IsNaN(climbBlend) ? normalizedSpeed : climbBlend, SpeedHash);
            GaitAppendFloat(line, "moveSpeed", normalizedSpeed, MoveSpeedHash);
            GaitAppendFloat(line, "pvX", moveX, PlanarVelocityXHash);
            GaitAppendFloat(line, "pvY", moveZ, PlanarVelocityYHash);
            GaitAppendFloat(line, "turnRate", normalizedTurnRate, TurnRateHash);
            GaitAppendFloat(line, "turnAngle", turnAngleValue, TurnAngleHash);
            line.Append(", climbBlend=")
                .Append(float.IsNaN(climbBlend) ? BlacksiteDiagnosticFormat.None : climbBlend.ToString("0.000"));

            line.Append(", animSpeed=").Append(animatorSpeed.ToString("0.000"))
                .Append(", clock=").Append(clockOwner)
                .Append(", updateMode=").Append(_animator.updateMode)
                .Append(", culling=").Append(_animator.cullingMode)
                .Append(", rootMotion=").Append(_animator.applyRootMotion)
                .Append(", animEnabled=").Append(_animator.enabled)
                .Append(", initialized=").Append(_animator.isInitialized);

            line.Append(", state=").Append(stateHash)
                .Append(", stateTime=").Append(stateTime.ToString("0.000"))
                .Append(", len=").Append(stateLength.ToString("0.000"))
                .Append(", cycles=")
                .Append(float.IsNaN(cycles) ? BlacksiteDiagnosticFormat.None : cycles.ToString("0.000"))
                .Append(", transition=").Append(inTransition)
                .Append(", next=").Append(inTransition ? nextHash.ToString() : BlacksiteDiagnosticFormat.None)
                .Append(", nextTime=")
                .Append(inTransition ? nextTime.ToString("0.000") : BlacksiteDiagnosticFormat.None);

            line.Append(", clips=[");
            for (int i = 0; i < GaitClipScratch.Count; i++)
            {
                AnimatorClipInfo info = GaitClipScratch[i];
                if (i > 0)
                    line.Append('|');
                line.Append(info.clip != null ? info.clip.name : BlacksiteDiagnosticFormat.None)
                    .Append(':').Append(info.weight.ToString("0.00"));
            }
            line.Append(']');
            GaitClipScratch.Clear();

            // Every layer, by authored name, every sample. A lower body suppressed by an override
            // layer is visible here and nowhere else: the base layer keeps its weight and its
            // clips, and the layer above it is the one holding the legs.
            line.Append(", layers=[");
            for (int i = 0; i < layerCount; i++)
            {
                if (i > 0)
                    line.Append('|');
                line.Append(i).Append(':').Append(_animator.GetLayerName(i)).Append('=')
                    .Append(_animator.GetLayerWeight(i).ToString("0.00"));
            }
            line.Append(']');

            GaitAppendBool(line, "grounded", GroundedHash);
            GaitAppendBool(line, "aiming", AimingHash);
            GaitAppendBool(line, "crouched", CrouchedHash);
            GaitAppendBool(line, "climbing", ClimbingHash);
            line.Append(", stance=")
                .Append(HasParameter(StanceHash, AnimatorControllerParameterType.Int)
                    ? _animator.GetInteger(StanceHash).ToString()
                    : BlacksiteDiagnosticFormat.None);
            line.Append(", agentBound=").Append(agentBound)
                .Append(", yawOffset=").Append(yawOffset.ToString("0.00"))
                .Append(", aimWeight=")
                .Append(_aimPose != null ? _aimPose.LayerWeight.ToString("0.00") : BlacksiteDiagnosticFormat.None)
                .Append(", carryWeight=")
                .Append(_aimPose != null ? _aimPose.CarryLayerWeight.ToString("0.00") : BlacksiteDiagnosticFormat.None)
                .Append(", ikPass=").Append(_aimPose != null && _aimPose.IkPassObserved);

            BlacksiteDiagnostics.Log("Gait", line.ToString());
            line.Clear();
        }

        // target/held for one float parameter. A parameter the bound controller does not carry
        // prints its target and "none", which is itself the answer to "is this write reaching
        // anything at all".
        private void GaitAppendFloat(System.Text.StringBuilder line, string key, float target, int parameterHash)
        {
            line.Append(", ").Append(key).Append('=').Append(target.ToString("0.000")).Append('/');
            if (HasParameter(parameterHash, AnimatorControllerParameterType.Float))
                line.Append(_animator.GetFloat(parameterHash).ToString("0.000"));
            else
                line.Append(BlacksiteDiagnosticFormat.None);
        }

        private void GaitAppendBool(System.Text.StringBuilder line, string key, int parameterHash)
        {
            line.Append(", ").Append(key).Append('=');
            if (HasParameter(parameterHash, AnimatorControllerParameterType.Bool))
                line.Append(_animator.GetBool(parameterHash));
            else
                line.Append(BlacksiteDiagnosticFormat.None);
        }

        private static bool IsServerAuthority()
        {
            return RoundManager.Instance != null && RoundManager.Instance.IsServer;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
