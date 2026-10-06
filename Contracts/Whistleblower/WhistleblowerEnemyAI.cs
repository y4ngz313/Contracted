using System;
using System.Collections;
using System.IO;
using BepInEx;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Networking;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core.Compat;

namespace Y4NGZCompany.Contracts.Whistleblower
{
    // #963 S14 U1: partial so the Blacksite custody seam lives in its own file
    // (WhistleblowerEnemyAI.Blacksite.cs), exactly as the payload cart keeps its Blacksite seam
    // in PayloadCartController.Blacksite.cs.
    public sealed partial class WhistleblowerEnemyAI : EnemyAI
    {
        private const int HidingState = 0;
        private const int CorneredState = 1;
        private const int EscapeState = 2;
        private const int LastStandState = 3;
        private const int NeutralizedState = 4;
        // #963 S14 U1: appended, never renumbered. The index replicates through vanilla's
        // currentBehaviourStateIndex, so a client that has not been told about this state would
        // read every state after it as the wrong one.
        private const int CustodyState = 5;
        // #1158: appended after the fugitive partial's AssessingState (6), never renumbered. A
        // state rather than a flag because a client - a late joiner included - has nothing but
        // the replicated index to derive his pose from.
        private const int CoweringState = 7;
        // #874 F02: appended after the cower, never renumbered. He has recognised a Blacksite
        // soldier and is walking to him. A state rather than a flag for the reason every other
        // one here is: a client - a late joiner included - derives his pose from nothing but the
        // replicated index.
        private const int RallyState = 8;

        private const int MaxEscapes = 4;
        private const int PunchDamage = 10;
        // #982: he is found when somebody actually looks at him from talking distance. 12 m
        // through a 160 degree field meant a crew walking a corridor flushed him from the far
        // end without ever seeing him.
        private const float DiscoveryDistance = 8f;
        // Continuous line of sight this long before he breaks cover, so a camera sweep past
        // his hiding spot is not a discovery. At the 10 Hz observer sample that is ~4 samples.
        private const float DiscoveryDwellSeconds = 0.35f;
        private const float ObservationDistance = 60f;
        private const float CorneredSpeed = 3.55f;
        private const float EscapeSpeed = 6.6f;
        // #982: the last stand is meant to be reachable, not a free kite. A player walks at
        // 4.6 m/s (PlayerControllerB.movementSpeed), so 1.6 m/s meant anybody who backed up
        // never had to fight him at all. 5.0 still reads as a limp next to the 5.4 m/s
        // advance he has before his escapes are spent; Lawson tunes both by feel.
        private const float LastStandSpeed = 5f;
        // Bursts are single authored steps of the boxing shuffle, so their ground speed
        // tracks the clip stride in every state. Last stand keeps the pace the shuffle was
        // reviewed at rather than inheriting the advance speed above and skating.
        private const float LastStandBurstSpeed = 1.6f;
        private const float CrawlSpeed = 0.72f;
        private const float PunchRange = 2.15f;
        private const float PunchCooldown = 1.55f;

        // Combat is burst-based: he stays planted and only moves in short steps
        // whose speed matches the authored stride of the boxing shuffle clips, so
        // the model never skates. BurstSpeed tracks BoxingReferenceSpeed in
        // WhistleblowerAnimationDriver, and BurstDuration has to outlast the boxing
        // blend-in or the shuffle never reaches full weight before the step ends.
        private const float BurstSpeed = 0.95f;
        private const float BurstDuration = 0.6f;
        private const float BurstCooldownMin = 0.5f;
        private const float BurstCooldownMax = 0.9f;
        private const float ChainedBurstGap = 0.15f;
        private const int MaxBurstChain = 2;
        // #765 verdict round 2: bursts only cover the last metre or so; anything further
        // is an advance, or he never reaches punch range at all.
        private const float CloseBurstRange = 3.2f;
        private const float RetreatBurstRange = 1.2f;
        private const float CircleBurstMinRange = 2f;
        private const float CircleBurstMaxRange = PunchRange + 0.6f;
        private const float CircleBurstChance = 0.35f;
        // #982: the 10 Hz observer sample used to overwrite _combatTarget outright, so the
        // nearest player with a line of sight stole the fight every tenth of a second and he
        // never committed to anyone. A new face has to be materially closer than the man he
        // is already swinging at.
        private const float RetargetDistanceAdvantage = 4f;

        // The final stand still closes just inside punch range.
        private const float AdvanceStopRange = PunchRange - 0.35f;
        // Server holds the knockdown this much longer than the clip chain so clients,
        // which sequence the clips on their own clocks, finish standing up before he moves.
        private const float KnockdownRecoveryMargin = 0.3f;

        // #983: the driver hands baked hip travel over one frame at a time. No single frame
        // may move him further than this (a hitch used to arrive as one large step), and the
        // step is truncated at the first navmesh edge in the way, so a fall or get-up whose
        // authored travel points into a wall stops at the wall instead of crossing it.
        private const float MaxDisplacementStep = 0.4f;
        private const float DisplacementWallMargin = 0.08f;
        // A truncated step shorter than this is dropped: there is nowhere useful to go.
        private const float MinDisplacementStep = 0.03f;

        // #983: NavMesh.SamplePosition around a blind offset candidate answers happily with a
        // point on the far side of a thin wall, and the retreat, the sidestep and the crawl all
        // used one. Candidates keep this sample radius but are then truncated to what a straight
        // navmesh ray from his feet actually reaches, minus the agent radius.
        private const float DestinationSampleRadius = 1.5f;
        private const float MinBurstStep = 0.35f;
        private const float MinCrawlStep = 0.5f;
        // Scan node and contract card treat him as in sight for this long after the
        // last positive line-of-sight sample, so a glance away does not flip them.
        private const float SightingHoldSeconds = 4f;

        // #765: the pre-discovery roam uses vanilla's node search at a nervous walk.
        private const float RoamSpeed = 1.7f;

        private const float FleeBaseChance = 0.10f;
        private const float FleeChancePerHit = 0.07f;
        private const float MinEscapeDuration = 4.25f;
        private const float MaxEscapeDuration = 35f;
        // #765: escape distance is measured in dungeon tiles (Flee Distance Tiles). When
        // the tile graph is unavailable this many path metres stand in for one tile.
        private const float MetersPerTileFallback = 11f;
        private const float SafeHideDistance = 25f;
        // Grace after settling into hiding before another discovery can begin.
        private const float RediscoveryLockoutSeconds = 6f;
        private const float KnockdownCooldown = 3.5f;
        // #982: a single hit of this force or more puts him down at once (a shotgun). Below it,
        // CumulativeKnockdownHits landed inside CumulativeKnockdownWindowSeconds of the first
        // do the same, which is what lets a shovel reach the chain at all. Three swings is
        // roughly two seconds of a committed melee player, so the window is not generous.
        private const int KnockdownHeavyHitForce = 3;
        private const int CumulativeKnockdownHits = 3;
        private const float CumulativeKnockdownWindowSeconds = 4f;
        private const float PostKnockdownFleeChance = 0.35f;
        private const float SwingMissLeadTime = 0.12f;
        private const float SwingMissVolume = 0.16f;
        // #982: the landed punch, played at the victim. Loud relative to the whoosh on
        // purpose: a hit you take has to read over your own footsteps and breathing.
        private const float PunchImpactVolume = 0.9f;
        // #984: creatureSFX's source volume went 0.75 -> 1.0 so footsteps could carry
        // vanilla's loudness in the one-shot scalar. The combat foley was balanced by ear
        // against the old 0.75, so every one-shot on that source is scaled back by the same
        // 0.75 and comes out exactly as loud as it did before. Applied at the call site
        // rather than folded into the constants so the tuned values stay legible.
        private const float SfxSourceGainCompensation = 0.75f;
        private const float BluntHitVolume = 0.85f;
        private const float ChimeVolume = 0.75f;

        // #984: vanilla's own footstep loudness (PlayerControllerB.PlayFootstepSound —
        // movementAudio.PlayOneShot at 0.9 sprinting, 0.6 otherwise) mapped onto the driver's
        // locomotion intensities (WhistleblowerAnimationDriver.FootstepIntensity: run 1.0,
        // walk 0.62, boxing 0.45). The map is anchored on walk and run so boxing footwork
        // clamps to the walking scalar rather than falling below it.
        private const float WalkFootstepIntensity = 0.62f;
        private const float RunFootstepIntensity = 1f;
        private const float WalkFootstepVolume = 0.6f;
        private const float RunFootstepVolume = 0.9f;
        // Vanilla's PlayFootstepServer/PlayFootstepLocal noise pair, unchanged.
        private const float RunFootstepNoiseRange = 22f;
        private const float RunFootstepNoiseLoudness = 0.6f;
        private const float WalkFootstepNoiseRange = 17f;
        private const float WalkFootstepNoiseLoudness = 0.4f;
        // Vanilla tags a footstep 6 when the local player took it and 7 when a remote one
        // did; 7 exists so enemies can discount position-sync footstep noise they did not
        // witness (MouthDogAI/PumaAI/CaveDwellerAI all filter it outright). His steps are
        // simulated locally off a real footfall event on every machine, so they are the
        // "witnessed" kind: 6.
        private const int FootstepNoiseId = 6;
        // Above this driver intensity he is running (the run cycle reports exactly 1.0).
        private const float RunFootstepThreshold = 0.9f;
        // Metres of full gain on the footstep emitter's carry curve before the fade begins.
        private const float FootstepNearFieldPlateauMeters = 3.5f;

        private readonly NetworkVariable<bool> _observed = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
        // #765 verdict round 2: every client plays hit staggers locally, and a client
        // whose knockdown chain is not running (late graph, dropped RPC) used to stand
        // him up mid-fall on the next hit. The server's knockdown state is replicated
        // so hit reactions stay suppressed while he is down anywhere.
        private readonly NetworkVariable<bool> _knockedDown = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _escapesCompleted = new NetworkVariable<int>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private InteractTrigger _trigger;
        private ScanNodeProperties _scanNode;
        private AudioClip _completeVoice;
        private PlayerControllerB _combatTarget;
        private Vector3 _lastPosition;
        private Vector3 _escapeDestination;
        private Vector3 _escapeStartPosition;
        private float _escapeStartedAt;
        private float _escapeStumbleUntil;
        private float _nextDestinationUpdate;
        private float _nextDiscoveryAllowedAt;
        private float _nextPunchAt;
        private float _movementLockedUntil;
        private float _burstUntil;
        private float _nextBurstAt;
        private int _burstChain;
        // #982: serial guard for the landed-punch ClientRpc, so a re-delivered message
        // cannot apply the damage twice on the owning client.
        private int _punchLandedSerial;
        private int _punchAttemptSerial;
        private int _lastAppliedPunchSerial;
        // #982: melee reaches the knockdown chain through a hit-count window instead of
        // the shotgun-only force threshold.
        private int _cumulativeHitCount;
        private float _cumulativeHitWindowEnds;
        private bool _quietRetreat;
        private int _hitsSinceEscape;
        private bool _punchLeftNext = true;
        private bool _neutralizedNotified;
        private int _sightingCount;
        private ulong _lastAttackerClientId = ulong.MaxValue;
        private bool _visualRepairComplete;
        private float _nextVisualRepairAttempt;
        private WhistleblowerAnimationDriver _animationDriver;

        // #716/B10: the per-frame rig walks, delegate re-subscription and collider
        // lookup are all resolved once and latched here instead of every Update.
        private WhistleblowerFallbackVisual _fallbackVisual;
        private bool _fallbackVisualSearched;
        private bool _footstepSubscribed;
        private bool _interactionPromptDisabled;
        private Collider _triggerCollider;

        // #765: bare AudioSources bypassed the diagetic mixer (no occlusion, no reverb, no
        // volume slider) and died one room away. Both sources now take the shared facility
        // carry profile, which needs a per-frame tick for air absorption and occlusion.
        private ContractAudioPropagation _voicePropagation;
        private ContractAudioPropagation _sfxPropagation;
        // #984: footsteps own their own emitter at foot height, with a near-field plateau on
        // the carry curve and the relaxed transient absorption profile.
        private AudioSource _footstepAudio;
        private ContractAudioPropagation _footstepPropagation;
        private bool _audioRolesResolved;

        // #765: vanilla-friendly footsteps pick the surface under him the way
        // MaskedPlayerEnemy does; the last known surface is kept when the ray misses.
        private int _vanillaFootstepSurfaceIndex;
        private int _lastVanillaFootstepClip = -1;

        // #765: pre-discovery roam over vanilla's search routine, and the tile graph the
        // escape uses to measure "rooms away" instead of metres.
        private bool _roaming;
        private FacilityTileGraph _tileGraph;
        private bool _tileGraphSearched;
        private int[] _escapeStartHops;
        private int _fleeTiles = 5;
        // "Reach" is in tile units: doorway hops with a graph, path metres over
        // MetersPerTileFallback without one. When no node reaches the configured
        // distance (small interior, high setting) the sweep keeps the farthest it saw
        // and the arrival check relaxes to that, so the run cannot loop to the cap.
        private float _escapeRequiredReach = 5f;
        private Vector3 _escapeSearchRelaxedPosition;
        private float _escapeSearchRelaxedReach = -1f;

        // Observer line of sight costs one raycast batch per player, so it is sampled at
        // ~10 Hz and the last answer is reused between samples.
        private const float ObserverSampleIntervalSeconds = 0.1f;
        private float _nextObserverSampleAt;
        private bool _cachedAnyObserver;
        private PlayerControllerB _cachedObserver;
        // #982: discovery rides the same sample instead of sweeping every player every frame.
        // The closest line-of-sight holder within ObservationDistance is, when it is inside
        // DiscoveryDistance, exactly the closest one inside discovery range.
        private float _cachedObserverDistance = float.MaxValue;
        private float _discoverySince;

        // #716/B10: the escape search used to run one CalculatePath per inside AI node in
        // a single frame. It is now sliced across frames, keeping the running best.
        private const int EscapeNodesPerFrame = 6;
        private const int EscapeNodeScanCapPerFrame = 48;
        private GameObject[] _escapeSearchNodes;
        private int _escapeSearchIndex;
        private bool _escapeSearchActive;
        private Vector3 _escapeSearchAway;
        private Vector3 _escapeSearchThreatPosition;
        private Vector3 _escapeSearchBestPosition;
        private float _escapeSearchBestScore;

        // #716/E11: DisplayTip titles are sentence case across the mod (Payload's
        // "Payload controls"), and the guideline's contract copy rule is sentence case;
        // ALL CAPS is reserved for persistent telemetry rows, not popups.
        private const string TipTitle = "Whistleblower";

        // #716/E11: each of these tips fires once, not once per rediscovery and not
        // twice down two death paths.
        private bool _corneredTipShown;
        private bool _neutralizedTipShown;

        private bool _knockdownActive;
        // #982: who put him down, so the post-knockdown escape flees them rather than a
        // combat target that can be null when the fall came out of hiding.
        private PlayerControllerB _knockdownAttacker;
        private bool _knockdownHitFromFront;
        private float _knockdownStartedAt;
        private float _knockdownEndsAt;
        private float _knockdownCooldownUntil;
        private float _nextCrawlDestinationAt;
        // #983: latched knockdown request waiting for this client's driver to be ready.
        private bool _knockdownChainPending;
        private bool _knockdownFlagSubscribed;

        internal Vector3 MarkerPosition => transform.position + Vector3.up * 1.73f;
        internal int EscapeCount => _escapesCompleted.Value;
        internal bool IsObserved => _observed.Value;
        /// <summary>True while any player has line of sight, held <see cref="SightingHoldSeconds"/> past the last sample.</summary>
        internal bool IsObservedRecently => Time.time - _lastObservedAt < SightingHoldSeconds;
        private float _lastObservedAt = float.NegativeInfinity;
        internal bool LastStand => currentBehaviourStateIndex == LastStandState;
        // #963 S14 U1: an exact index test. It used to read "any state at or past Neutralized",
        // which was the same thing while Neutralized was the last state and would have called a
        // witness in custody (state 5) neutralized the moment an escort took him: the scan node
        // would read "neutralized", the contract would credit a kill nobody made, and
        // UpdateContractStateFromBehaviour would notify it once per round. States 0 to 4 are
        // unaffected, so nothing outside custody changes.
        internal bool Neutralized => currentBehaviourStateIndex == NeutralizedState || isEnemyDead;
        /// <summary>Client id of the last player to land a hit, or <see cref="ulong.MaxValue"/> when nobody has.</summary>
        internal ulong LastAttackerClientId => _lastAttackerClientId;

        public override void Start()
        {
            EnsureBehaviourStates();
            base.Start();
            if (DespawnIfWrongContract())
                return;

            ResolveRuntimeReferences();
            RepairVisualIfNeeded();
            LogVanillaVisualHeightCheck();
            ConfigureAnimationDriver();
            ConfigureAgent();
            // #765 verdict round 2: vanilla's 1 m / 0.22 s position sync showed a 6.6 m/s
            // escape as a chain of jumps. Same values BlacksiteSoldierAI settled on.
            updatePositionThreshold = 0.35f;
            syncMovementSpeed = 0.13f;
            ConfigureSpatialAudio();
            DisableInteractionPrompt();
            // Vanilla doors meter their opening off this; he roams now, so he has to be
            // able to get through them like any other interior enemy.
            openDoorSpeedMultiplier = 1f;

            enemyHP = ResolveHealthForCurrentRisk();
            ventAnimationFinished = true;
            _lastPosition = transform.position;
            _escapeDestination = transform.position;

            if (creatureAnimator != null)
            {
                creatureAnimator.applyRootMotion = false;
                creatureAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            UpdatePresentationForState();
            StartCoroutine(LoadCompleteVoice());
            WhistleblowerSoundLibrary.EnsureLoaded(this);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Whistleblower initialized with {enemyHP} HP for risk {MoonContractState.GetCurrentRiskTier()}.");
        }

        public override void Update()
        {
            base.Update();
            if (DespawnIfWrongContract())
                return;

            ResolveRuntimeReferences();
            RepairVisualIfNeeded();
            ConfigureAnimationDriver();
            DisableInteractionPrompt();
            if (_observed.Value)
                _lastObservedAt = Time.time;

            if (!isEnemyDead && IsServer)
                UpdateServerBehaviour();

            _voicePropagation?.Tick();
            _sfxPropagation?.Tick();
            _footstepPropagation?.Tick();
            UpdatePresentationForState();
            TickKnockdownChainClient();
            UpdateAnimation();
            UpdateContractStateFromBehaviour();
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
        }

        public override void HitEnemy(
            int force = 1,
            PlayerControllerB playerWhoHit = null,
            bool playHitSFX = false,
            int hitID = -1)
        {
            if (isEnemyDead)
                return;

            base.HitEnemy(force, playerWhoHit, playHitSFX, hitID);

            if (playerWhoHit != null)
                _lastAttackerClientId = playerWhoHit.actualClientId;

            int damage = Mathf.Max(1, force);
            enemyHP -= damage;
            if (!_knockedDown.Value)
                PlayDeterministicHitReaction(force, hitID);
            PlayBluntHitSound(force, hitID);

            if (enemyHP <= 0)
            {
                if (!isEnemyDead && (IsOwner || IsServer))
                    KillEnemyOnOwnerClient();
                return;
            }

            if (!IsServer)
                return;

            // A hit reaction interrupts an in-flight strike, including its delayed damage.
            _punchAttemptSerial++;
            float fleeChance = FleeChanceForHit(_hitsSinceEscape);
            _hitsSinceEscape++;

            // #982: this guard used to sit below the Hiding -> Cornered call, so a hit landed
            // while he was on the floor in hiding entered combat and played the Evade one-shot
            // straight over the running knockdown chain. Nothing reacts to a hit while he is
            // down or already committed to a run.
            if (_knockdownActive
                || currentBehaviourStateIndex == EscapeState
                || currentBehaviourStateIndex == LastStandState)
            {
                return;
            }

            // #874 F02: RallyState joins the two states a hit flushes him out of. A man walking
            // himself to a soldier is not in custody and not committed to a run, so a crew that
            // reaches him first still gets exactly what it has always got from a hit - he breaks
            // off, and inside he may roll the escape below. Without the term a hit on a rallying
            // witness would land silently and he would keep walking, which is the counterplay
            // suppression this change must not cause.
            if (currentBehaviourStateIndex == HidingState
                || currentBehaviourStateIndex == AssessingState
                || currentBehaviourStateIndex == RallyState)
                EnterCorneredServer(playerWhoHit, assess: false);
            if (currentBehaviourStateIndex == CorneredState)
                _movementLockedUntil = Mathf.Max(_movementLockedUntil, Time.time + 0.5f);

            // #982: the cumulative window. A shovel hits for force 1, so the force >= 3 test
            // below is a shotgun test and the post-knockdown flee valve never fired in a melee
            // round. Every eligible hit is counted from the first one in the window.
            float now = Time.time;
            if (now >= _cumulativeHitWindowEnds)
            {
                _cumulativeHitCount = 0;
                _cumulativeHitWindowEnds = now + CumulativeKnockdownWindowSeconds;
            }

            _cumulativeHitCount++;

            // Roll flee before the knockdown branch so weapons that always hit heavy
            // (guns) can still spend his escapes instead of chain-knockdowns forever.
            // #963 S14 U1: in custody the damage still lands and a heavy hit still puts him down
            // (that knockdown is one of the custody breaks), but he does not roll an escape out
            // of an escort's hands: the break is what frees him, and it spends no escape.
            // #1158: nor anywhere outside, where the run has nowhere to go: out there a hit that
            // flushes him out of hiding cowers him instead (EnterCorneredServer). The ROLL is
            // gated rather than the escape entry, so the knockdown branch below still reaches him.
            bool canFlee = IsInsideFacility
                && currentBehaviourStateIndex != CustodyState
                && _escapesCompleted.Value < MaxEscapes;
            if (canFlee && UnityEngine.Random.value <= fleeChance)
            {
                BeginEscapeServer(playerWhoHit);
                return;
            }

            // A heavy hit is still instant. A run of light hits inside the window reaches the
            // same chain, so a shovel crew sees the knockdown and the flee valve behind it.
            bool heavyHit = force >= KnockdownHeavyHitForce;
            bool cumulativeHit = _cumulativeHitCount >= CumulativeKnockdownHits;
            if ((heavyHit || cumulativeHit) && now >= _knockdownCooldownUntil)
                BeginKnockdownServer(playerWhoHit);
        }

        public override void KillEnemy(bool destroy = false)
        {
            if (isEnemyDead && _neutralizedNotified)
                return;

            // #963 S14 U1: killing him is how the crew wins, in every phase including out of an
            // escort's hands. Custody is released before the state switch so the release line
            // still names the escort that was holding him, and it is released with its own cause
            // so no other break can claim the kill.
            ReleaseCustodyServer(Escalations.BlacksiteRetrieval.BlacksiteCustodyBreak.WitnessDead);

            if (currentBehaviourStateIndex != NeutralizedState)
                SwitchToBehaviourState(NeutralizedState);

            base.KillEnemy(destroy);
            isEnemyDead = true;
            _knockdownActive = false;
            if (IsServer && _knockedDown.Value)
                _knockedDown.Value = false;
            if (agent != null)
                agent.updateRotation = true;
            StopAgent();
            _animationDriver?.ApplyAction(WhistleblowerAnimationAction.Death);
            SetNeutralizedPresentation();
            PlayCompleteVoice();

            if (!_neutralizedNotified)
            {
                _neutralizedNotified = true;
                MoonContractState.NotifyWhistleblowerNeutralized(this);
                if (IsServer)
                {
                    if (_observed.Value)
                        _observed.Value = false;
                    // #716/E11: guarded independently of _neutralizedNotified, which the
                    // behaviour-state path can already have set without tipping.
                    if (!_neutralizedTipShown)
                    {
                        _neutralizedTipShown = true;
                        ShowTipClientRpc(TipTitle, "Target neutralized.", false);
                    }
                    // #944 S11 U6: the "Whistleblower neutralized." line is emitted by
                    // MoonContractState.NotifyWhistleblowerNeutralized, which the call above already
                    // made and which covers the other caller at :1549 as well. Logging it here too
                    // produced the identical pair at run 1 lines 35899-35900, same millisecond.
                }
            }
        }

        private void UpdateServerBehaviour()
        {
            // #716/B10: TryFindObservingPlayer raycasts line of sight for every player.
            // Sampling it at ~10 Hz keeps the "observed" scan state responsive without
            // paying for a full LOS sweep on every rendered frame.
            bool anyObserver;
            PlayerControllerB observer;
            if (Time.time >= _nextObserverSampleAt)
            {
                _nextObserverSampleAt = Time.time + ObserverSampleIntervalSeconds;
                _cachedAnyObserver = TryFindObservingPlayer(out _cachedObserver, out _cachedObserverDistance);
            }
            else if (_cachedObserver != null && !ObservingPlayerIsEligible(_cachedObserver))
            {
                // Never hand a stale sample back once its player is gone.
                _cachedObserver = null;
                _cachedAnyObserver = false;
                _cachedObserverDistance = float.MaxValue;
            }

            anyObserver = _cachedAnyObserver;
            observer = _cachedObserver;
            if (_observed.Value != anyObserver)
                _observed.Value = anyObserver;

            // #1293 R2: a hand-off that found no navmesh under him retries in place each tick, and
            // his own behaviour waits for it. A knockdown's timeline still runs to its stand-up
            // (which clears the replicated _knockedDown); it does not crawl an unbound agent.
            bool bound = RetryAgentBindServer();
            if (_knockdownActive)
            {
                UpdateKnockdownServer();
                return;
            }

            if (!bound)
                return;

            switch (currentBehaviourStateIndex)
            {
                case HidingState:
                    UpdateHidingServer();
                    break;
                case CorneredState:
                    UpdateDefensiveServer();
                    break;
                case AssessingState:
                    UpdateAssessmentServer();
                    break;
                case EscapeState:
                    UpdateEscapeServer();
                    break;
                case LastStandState:
                    UpdateLastStandServer(observer);
                    break;
                case NeutralizedState:
                    StopAgent();
                    break;
                // #963 S14 U1: no roam, no discovery, no escape re-entry and no last stand while
                // an escort has him. All four are suppressed by construction: each of them is
                // reached only from a state this case replaces. #1293 R2: the tick only watches for
                // the real events that end custody; the attachment (LateUpdate) moves him.
                case CustodyState:
                    UpdateCustodyServer();
                    break;
                // #1158: nothing in the cower plans a path, so the tick early-outs and only
                // re-asserts the stop on a frame where something else (a release's hand-off bind)
                // gave his agent back.
                //
                // #874 F02: except that a cowering man who can see a Blacksite soldier is not
                // frozen for the rest of the round. The cower is his answer to a crew he cannot
                // run from out here, not to the squad, and the recognition's own admission still
                // has to pass - no player at hand, the escort setting on, and the custody stage
                // Free, so a break's backoff leaves him cowering exactly as long as it leaves him
                // untakeable.
                case CoweringState:
                    if (TryFindRallyEscortServer(out var cowerRally))
                    {
                        EnterRallyServer(cowerRally);
                        break;
                    }
                    if (moveTowardsDestination
                        || (agent != null && agent.enabled && agent.isOnNavMesh && !agent.isStopped))
                        StopAgent();
                    break;
                // #874 F02: the walk to a recognised soldier, dispatched here so the behaviour
                // switch that already owns his body is the only writer, and nothing here adds an
                // Update of its own.
                case RallyState:
                    UpdateRallyServer();
                    break;
            }
        }

        private void UpdateHidingServer()
        {
            if (TryDiscoveredServer(out PlayerControllerB player))
            {
                _discoverySince = 0f;
                StopRoamServer();
                EnterCorneredServer(player, assess: true);
                return;
            }

            // #874 F02: a man who has seen the squad stops wandering and walks to them. Asked
            // after discovery, because a crew in the room outranks the squad, and before the roam,
            // because the roam is what he does when there is nobody to go to.
            if (TryFindRallyEscortServer(out var rally))
            {
                EnterRallyServer(rally);
                return;
            }

            UpdateRoamServer();
        }

        /// <summary>
        /// #982: discovery reads the 10 Hz observer sample (it used to sweep every player's
        /// line of sight on every rendered frame) and needs
        /// <see cref="DiscoveryDwellSeconds"/> of unbroken positives before he breaks cover,
        /// so one frame of a crosshair crossing him at range is not a discovery.
        /// </summary>
        private bool TryDiscoveredServer(out PlayerControllerB discoveredBy)
        {
            discoveredBy = null;
            if (Time.time < _nextDiscoveryAllowedAt)
            {
                _discoverySince = 0f;
                return false;
            }

            bool seen = _cachedAnyObserver
                && _cachedObserver != null
                && _cachedObserverDistance <= DiscoveryDistance;
            if (!seen)
            {
                _discoverySince = 0f;
                return false;
            }

            if (_discoverySince <= 0f)
            {
                _discoverySince = Time.time;
                return false;
            }

            if (Time.time - _discoverySince < DiscoveryDwellSeconds)
                return false;

            discoveredBy = _cachedObserver;
            return true;
        }

        // #765: before discovery he wanders the facility on vanilla's AI-node search, the
        // same routine every interior enemy patrols with, instead of standing where he
        // spawned. The search coroutine feeds SetDestinationToPosition and DoAIInterval
        // applies it, so the only local work is keeping the agent released at walk pace.
        private void UpdateRoamServer()
        {
            bool roamEnabled = ContractsBootstrap.WhistleblowerBalance == null
                || ContractsBootstrap.WhistleblowerBalance.RoamWhileHiding.Value;
            if (!roamEnabled || agent == null || !agent.enabled || !agent.isOnNavMesh)
            {
                StopRoamServer();
                return;
            }

            if (!_roaming || currentSearch == null || !currentSearch.inProgress)
            {
                _roaming = true;
                agent.updateRotation = true;
                SetAgentSpeed(RoamSpeed, stopped: false, stoppingDistance: 0.6f);
                StartSearch(transform.position);
                return;
            }

            agent.speed = RoamSpeed;
            if (agent.isStopped)
                agent.isStopped = false;
        }

        private void StopRoamServer()
        {
            if (_roaming)
            {
                _roaming = false;
                if (currentSearch != null && currentSearch.inProgress)
                    StopSearch(currentSearch);
            }

            StopAgent();
        }

        private void EnterCorneredServer(PlayerControllerB player, bool assess)
        {
            bool wasHidden = currentBehaviourStateIndex == HidingState;
            StopRoamServer();
            _combatTarget = IsLivingInteriorPlayer(player)
                ? player
                : FindClosestLivingInteriorPlayer(transform.position, out _);
            ResetBurstState();

            // Each fresh break out of hiding is one sighting: whoever flushed him out gets the
            // credit, and a crew that only stumbled onto him together shares it.
            if (wasHidden && IsServer)
            {
                _sightingCount++;
                ulong spotterClientId = ContractProgressionEvents.ResolveClientId(_combatTarget);
                if (spotterClientId == ulong.MaxValue)
                    ContractProgressionEvents.PublishWhistleblowerSightedToAll(NetworkObjectId, _sightingCount);
                else
                    ContractProgressionEvents.PublishWhistleblowerSighted(spotterClientId, NetworkObjectId, _sightingCount);
            }

            // #1158: outside there is no cornering him. Both entries here - a sighting from
            // hiding and a hit that flushes him out - end in a combat state whose opponent comes
            // from IsLivingInteriorPlayer, which no player outside can satisfy, so outside each
            // landed him in a targetless Cornered that leaves through the fugitive exit and the
            // escape run. Gated at the one entry both callers share, after the sighting credit.
            if (!IsInsideFacility)
            {
                EnterCowerServer();
                return;
            }

            bool lastStand = _escapesCompleted.Value >= MaxEscapes;
            _fugitiveRetreat = false;
            _nextFugitiveCheckAt = 0f;
            _movementLockedUntil = 0f;
            SwitchToBehaviourState(lastStand ? LastStandState : assess ? AssessingState : CorneredState);
            StopAgent();

            if (lastStand)
            {
                ShowTipClientRpc(TipTitle, "No escapes left. Finish him.", true);
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Whistleblower entered his permanent last stand.");
                return;
            }

            // A moving evade cannot adapt to a doorway. Assess in a stationary
            // nervous pose, then let a checked navigation path move him.
            _assessmentUntil = Time.time + WhistleblowerFugitivePolicy.AssessmentSeconds;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Whistleblower discovered; assessing escape with {_escapesCompleted.Value}/{MaxEscapes} escapes spent.");
        }

        /// <summary>
        /// #1158. He cowers where he stands: agent stopped in place, no path planned, which is
        /// all a cower is on a rig that has no cower clip. Reached outside only: a custody break,
        /// a sighting or a hit while he is free, and standing up from a knockdown. An escort may
        /// still take him from here - the take gate reads his body and the backoff, not his state.
        /// </summary>
        private void EnterCowerServer()
        {
            if (currentBehaviourStateIndex == CoweringState)
                return;

            CancelEscapeSearch();
            StopRoamServer();
            ResetBurstState();
            _quietRetreat = false;
            _fugitiveRetreat = false;
            _nextFugitiveCheckAt = 0f;
            _movementLockedUntil = 0f;
            _combatTarget = null;
            _discoverySince = 0f;
            SwitchToBehaviourState(CoweringState);
            StopAgent();

            ContractsBootstrap.Log?.LogInfo("[MoonContracts] Whistleblower cowers in place outside the facility.");
        }

        private void UpdateLastStandServer(PlayerControllerB observer)
        {
            // #982: a fight has one opponent. The observer sample only takes the fight over
            // when there is nobody to fight or it brings a materially closer one; otherwise he
            // keeps swinging at the man in front of him.
            if (!IsLivingInteriorPlayer(_combatTarget))
                _combatTarget = IsLivingInteriorPlayer(observer)
                    ? observer
                    : FindClosestLivingInteriorPlayer(transform.position, out _);
            else if (IsLivingInteriorPlayer(observer) && observer != _combatTarget)
            {
                float currentDistance = Vector3.Distance(transform.position, _combatTarget.transform.position);
                float observerDistance = Vector3.Distance(transform.position, observer.transform.position);
                if (observerDistance <= currentDistance - RetargetDistanceAdvantage)
                    RetargetCombatServer(observer);
            }

            if (_combatTarget == null)
            {
                StopAgent();
                return;
            }
            float targetDistance = Vector3.Distance(transform.position, _combatTarget.transform.position);

            if (Time.time < _movementLockedUntil)
            {
                _burstUntil = 0f;
                if (agent != null)
                    agent.updateRotation = false;
                FaceTarget(_combatTarget);
                StopAgent();
                return;
            }

            // A burst runs open-loop: its destination is set once at the start so he
            // covers one short authored step instead of re-homing on the player.
            // FaceTarget owns rotation through it; letting the agent also steer the
            // transform makes the two fight and the model twitch.
            if (Time.time < _burstUntil)
            {
                FaceTarget(_combatTarget);
                return;
            }

            _burstUntil = 0f;

            // #765: outside close range he closes the gap. The agent steers and turns
            // here, because running a corner while staring at the player twists the
            // model sideways.
            if (targetDistance > CloseBurstRange)
            {
                _burstChain = 0;
                if (agent != null)
                    agent.updateRotation = true;
                SetAgentSpeed(LastStandSpeed, stopped: false, stoppingDistance: AdvanceStopRange);
                SetDestinationToPosition(_combatTarget.transform.position, checkForPath: false);
                CommitDestination();
                return;
            }

            if (agent != null)
                agent.updateRotation = false;
            FaceTarget(_combatTarget);
            StopAgent();

            if (targetDistance <= PunchRange && Time.time >= _nextPunchAt)
            {
                BeginPunchServer(_combatTarget);
                return;
            }

            if (Time.time < _nextBurstAt)
                return;

            if (targetDistance < RetreatBurstRange && Time.time < _nextPunchAt)
            {
                BeginRetreatBurstServer(lastStand: true);
                return;
            }

            // Round 2: circling only while a punch is cooling down inside reach; before
            // that the closing burst always wins, or a third of his decisions were sidesteps.
            if (targetDistance >= CircleBurstMinRange
                && targetDistance <= CircleBurstMaxRange
                && Time.time < _nextPunchAt
                && UnityEngine.Random.value <= CircleBurstChance)
            {
                BeginCircleBurstServer(lastStand: true);
                return;
            }

            if (targetDistance > PunchRange && targetDistance <= CloseBurstRange)
            {
                BeginCloseBurstServer(lastStand: true);
                return;
            }

            // Nothing worth doing at this range: stay planted until the next expiry.
            _burstChain = 0;
            _nextBurstAt = Time.time + UnityEngine.Random.Range(BurstCooldownMin, BurstCooldownMax);
        }

        /// <summary>Commit the final stand to its newly selected opponent.</summary>
        private void RetargetCombatServer(PlayerControllerB player)
        {
            _combatTarget = player;
        }

        private void BeginBurstServer(Vector3 destination, bool lastStand, float cooldown)
        {
            _burstUntil = Time.time + BurstDuration;
            _nextBurstAt = _burstUntil + cooldown;
            if (agent != null)
                agent.updateRotation = false;
            SetAgentSpeed(lastStand ? LastStandBurstSpeed : BurstSpeed, stopped: false, stoppingDistance: 0.1f);
            SetDestinationToPosition(destination, checkForPath: false);
            CommitDestination();
        }

        private void BeginCloseBurstServer(bool lastStand)
        {
            _burstChain++;
            bool chainSpent = _burstChain >= MaxBurstChain;
            float cooldown = chainSpent
                ? UnityEngine.Random.Range(BurstCooldownMin, BurstCooldownMax)
                : ChainedBurstGap;
            if (chainSpent)
                _burstChain = 0;

            BeginBurstServer(_combatTarget.transform.position, lastStand, cooldown);
        }

        private void BeginRetreatBurstServer(bool lastStand)
        {
            _burstChain = 0;
            // #983: a blind sample behind him resolved through a thin wall and the burst
            // walked him into it. Backed against something, there is no step to take: hold
            // the normal cooldown so the ladder tries a punch or a circle next tick instead
            // of re-rolling a blocked retreat every frame.
            if (!TryResolveReachableDestination(transform.position - transform.forward * 1.6f, MinBurstStep, out Vector3 destination))
            {
                _nextBurstAt = Time.time + UnityEngine.Random.Range(BurstCooldownMin, BurstCooldownMax);
                return;
            }

            BeginBurstServer(destination, lastStand, UnityEngine.Random.Range(BurstCooldownMin, BurstCooldownMax));
            PlayActionClientRpc((int)WhistleblowerAnimationAction.StepBack);
        }

        private void BeginCircleBurstServer(bool lastStand)
        {
            _burstChain = 0;
            float side = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            // #983: same wall problem, but a sidestep has a second option - if the rolled
            // side is blocked, the other one usually is not.
            if (!TryResolveReachableDestination(transform.position + transform.right * (1.4f * side), MinBurstStep, out Vector3 destination)
                && !TryResolveReachableDestination(transform.position - transform.right * (1.4f * side), MinBurstStep, out destination))
            {
                _nextBurstAt = Time.time + UnityEngine.Random.Range(BurstCooldownMin, BurstCooldownMax);
                return;
            }

            BeginBurstServer(destination, lastStand, UnityEngine.Random.Range(BurstCooldownMin, BurstCooldownMax));
        }

        private void ResetBurstState()
        {
            _burstUntil = 0f;
            _nextBurstAt = 0f;
            _burstChain = 0;
        }

        private void BeginPunchServer(PlayerControllerB target)
        {
            if (!IsLivingInteriorPlayer(target))
                return;

            StopAgent();
            FaceTarget(target);
            _nextPunchAt = Time.time + PunchCooldown;
            _movementLockedUntil = Time.time + (_animationDriver != null
                ? _animationDriver.PunchDuration(_punchLeftNext) : 0.8f);

            WhistleblowerAnimationAction action = _punchLeftNext
                ? WhistleblowerAnimationAction.PunchLeft
                : WhistleblowerAnimationAction.PunchRight;
            float impactDelay = _animationDriver != null
                ? _animationDriver.PunchContactDelay(_punchLeftNext)
                : WhistleblowerFugitivePolicy.PunchContactTime(_punchLeftNext, 1f, WhistleblowerAnimationDriver.PunchPlaybackRate);
            _punchLeftNext = !_punchLeftNext;
            PlayActionClientRpc((int)action);
            StartCoroutine(ApplyPunchAfterDelay(target, ++_punchAttemptSerial, impactDelay));
        }

        private IEnumerator ApplyPunchAfterDelay(PlayerControllerB target, int attempt, float impactDelay)
        {
            // The punch clip plays faster than authored, so the impact frame arrives
            // proportionally earlier. Check for a miss slightly before it so the whoosh
            // can be sent early enough to line up with the swing on every client.
            yield return new WaitForSeconds(Mathf.Max(0.02f, impactDelay - SwingMissLeadTime));
            if (!IsServer || isEnemyDead || _knockdownActive || IsInCustody || attempt != _punchAttemptSerial
                || (currentBehaviourStateIndex != CorneredState && currentBehaviourStateIndex != LastStandState))
                yield break;

            bool predictedMiss = !IsLivingInteriorPlayer(target)
                || Vector3.Distance(transform.position, target.transform.position) > PunchRange + 0.55f;
            if (predictedMiss)
            {
                PlaySwingMissClientRpc();
                yield break;
            }

            yield return new WaitForSeconds(SwingMissLeadTime);
            if (!IsServer || isEnemyDead || _knockdownActive || IsInCustody || attempt != _punchAttemptSerial
                || (currentBehaviourStateIndex != CorneredState && currentBehaviourStateIndex != LastStandState)
                || !IsLivingInteriorPlayer(target))
                yield break;
            if (Vector3.Distance(transform.position, target.transform.position) > PunchRange + 0.55f
                || !HasClearPunchLine(target))
            {
                PlaySwingMissClientRpc();
                yield break;
            }

            ApplyPunchLandedServer(target);
        }

        /// <summary>
        /// #982/#422: announces a landed punch to every client. <c>PlayerControllerB.DamagePlayer</c>
        /// early-returns unless <c>IsOwner</c>, so the server calling it directly only ever damaged
        /// the host; the damage is now applied by the client that owns the victim, guarded by a
        /// serial so a re-delivered RPC cannot double up. Same shape as
        /// <c>BlacksiteSoldierAI.ApplyAuthoritativePlayerDamage</c> and <c>CongregantAI</c>.
        /// </summary>
        private void ApplyPunchLandedServer(PlayerControllerB target)
        {
            if (!IsServer || !IsLivingInteriorPlayer(target))
                return;

            int victimIndex = FindPlayerIndex(target);
            if (victimIndex < 0)
                return;

            if (NetworkObject == null || !NetworkObject.IsSpawned)
            {
                // Unspawned (editor/teardown): no RPC channel, so fall back to the local path.
                PlayPunchImpactAt(target);
                if (target.IsOwner)
                    ApplyPunchDamageLocally(target);
                return;
            }

            PunchLandedClientRpc(victimIndex, ++_punchLandedSerial);
        }

        [ClientRpc]
        private void PunchLandedClientRpc(int victimPlayerIndex, int serial)
        {
            if (serial <= _lastAppliedPunchSerial)
                return;
            _lastAppliedPunchSerial = serial;

            PlayerControllerB victim = FindPlayerByIndex(victimPlayerIndex);
            if (victim == null)
                return;

            // Everyone hears the body impact where the punch landed; only the owner applies it.
            PlayPunchImpactAt(victim);

            if (!victim.IsOwner || victim.isPlayerDead || !victim.isPlayerControlled)
                return;

            ApplyPunchDamageLocally(victim);
        }

        private static void ApplyPunchDamageLocally(PlayerControllerB victim)
        {
            try
            {
                victim.DamagePlayer(
                    PunchDamage,
                    hasDamageSFX: true,
                    callRPC: true,
                    CauseOfDeath.Bludgeoning);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Whistleblower punch damage skipped: {e.Message}");
            }
        }

        /// <summary>
        /// Plays the body impact at the victim rather than at him: the punch is heard where it
        /// lands. The walkie transmit matches how vanilla one-shots reach a held radio.
        /// </summary>
        private void PlayPunchImpactAt(PlayerControllerB victim)
        {
            AudioClip clip = WhistleblowerSoundLibrary.RandomPunchImpact();
            if (clip == null || victim == null)
                return;

            AudioSource source = victim.movementAudio;
            if (source == null)
            {
                ResolveRuntimeReferences();
                source = creatureSFX != null ? creatureSFX : creatureVoice;
                if (source == null)
                    return;

                // #984: this fallback plays on HIS SFX emitter, which is now a volume 1.0
                // source, so the scalar takes the same compensation as the rest of the foley.
                // The normal path below is the victim's own movementAudio (volume 1) and is
                // therefore unchanged.
                source.PlayOneShot(clip, PunchImpactVolume * SfxSourceGainCompensation);
                return;
            }

            source.PlayOneShot(clip, PunchImpactVolume);
            WalkieTalkie.TransmitOneShotAudio(source, clip, PunchImpactVolume);
        }

        private static int FindPlayerIndex(PlayerControllerB player)
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

        private static PlayerControllerB FindPlayerByIndex(int index)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || index < 0 || index >= players.Length)
                return null;
            return players[index];
        }

        private void BeginEscapeServer(PlayerControllerB threat)
        {
            if (_escapesCompleted.Value >= MaxEscapes || currentBehaviourStateIndex == EscapeState)
                return;
            // #963 S14 U1: the counted escape is a thing he does to the crew, not a way out of an
            // escort's hands: a custody break spends nothing and hands him to his own state.
            if (currentBehaviourStateIndex == CustodyState)
                return;

            _escapesCompleted.Value++;
            MoonContractState.NotifyWhistleblowerEscaped(this, _escapesCompleted.Value);
            _quietRetreat = false;
            _fugitiveRetreat = false;
            ResetBurstState();
            _hitsSinceEscape = 0;
            _combatTarget = threat;
            _escapeStartPosition = transform.position;
            _escapeStartedAt = Time.time;
            PrepareEscapeDistance();
            BeginEscapeSearch(threat);
            _nextDestinationUpdate = Time.time + 1.25f;
            SwitchToBehaviourState(EscapeState);

            StopAgent();
            float stumbleDuration = _animationDriver != null
                ? _animationDriver.StumbleDuration
                : 0.8f;
            _escapeStumbleUntil = Time.time + Mathf.Clamp(stumbleDuration * 0.72f, 0.55f, 1.1f);
            PlayActionClientRpc((int)WhistleblowerAnimationAction.Stumble);
            TriggerFlashbangClientRpc(transform.position - transform.forward * 0.25f);
            ShowTipClientRpc(
                TipTitle,
                $"He broke contact behind a flashbang ({_escapesCompleted.Value}/{MaxEscapes}).",
                true);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Whistleblower escape {_escapesCompleted.Value}/{MaxEscapes} started toward {FormatVector(_escapeDestination)}.");
        }

        // Disengagement, not a spent escape: no flashbang, no counter, no stumble and
        // no contract notification. It only reuses the Escape state's run/arrive loop.
        private void BeginQuietRetreatServer(PlayerControllerB threat)
        {
            if (currentBehaviourStateIndex == EscapeState)
                return;

            _quietRetreat = true;
            ResetBurstState();
            _combatTarget = threat;
            _escapeStartPosition = transform.position;
            _escapeStartedAt = Time.time;
            _escapeStumbleUntil = 0f;
            PrepareEscapeDistance();
            BeginEscapeSearch(threat);
            _nextDestinationUpdate = Time.time + 1.25f;
            SwitchToBehaviourState(EscapeState);

            StopAgent();
            ShowTipClientRpc(TipTitle, "He slipped away.", false);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Whistleblower disengaged quietly toward {FormatVector(_escapeDestination)}.");
        }

        private void UpdateEscapeServer()
        {
            if (Time.time < _escapeStumbleUntil)
            {
                StopAgent();
                return;
            }

            if (UpdateFugitiveRetreatServer())
                return;

            SetAgentSpeed(EscapeSpeed, stopped: false, stoppingDistance: 0.75f);
            if (agent != null)
                agent.updateRotation = true;
            SetDestinationToPosition(_escapeDestination, checkForPath: false);

            // Advance the in-flight search before deciding whether a new one is needed,
            // and never start a second sweep on top of one already running.
            TickEscapeSearch();
            CommitDestination();

            // #765: the run ends where it was meant to end: on arrival at a spot the
            // configured number of tiles from where he started, with nobody standing on
            // it. Arriving anywhere closer (the first-frame fallback point, a dead end)
            // starts another sweep from there; only the time cap ends the run otherwise.
            float elapsed = Time.time - _escapeStartedAt;
            bool arrived = HasReachedDestination(1.6f);
            bool timedOut = elapsed >= MaxEscapeDuration;
            bool settled = arrived
                && elapsed >= MinEscapeDuration
                && EscapeFarEnough(transform.position)
                && !AnyLivingInteriorPlayerWithin(SafeHideDistance);
            if (!settled && !timedOut)
            {
                if (!_escapeSearchActive
                    && Time.time >= _nextDestinationUpdate
                    && (NeedsNewDestination() || arrived))
                {
                    _nextDestinationUpdate = Time.time + 1.25f;
                    BeginEscapeSearch(_combatTarget);
                    SetDestinationToPosition(_escapeDestination, checkForPath: false);
                }
                return;
            }

            bool wasQuietRetreat = _quietRetreat;
            _quietRetreat = false;
            _fugitiveRetreat = false;
            CancelEscapeSearch();
            SwitchToBehaviourState(HidingState);
            _combatTarget = null;
            // Avoid rediscovering him on the same frame he settles into hiding.
            _nextDiscoveryAllowedAt = Time.time + RediscoveryLockoutSeconds;
            ResetBurstState();
            StopAgent();
            ContractsBootstrap.Log?.LogInfo(wasQuietRetreat
                ? "[MoonContracts] Whistleblower hid after quietly disengaging."
                : $"[MoonContracts] Whistleblower hid after escape {_escapesCompleted.Value}/{MaxEscapes}.");
        }

        private bool AnyLivingInteriorPlayerWithin(float radius)
        {
            FindClosestLivingInteriorPlayer(transform.position, out float distance);
            return distance <= radius;
        }

        private void BeginKnockdownServer(PlayerControllerB attacker)
        {
            Vector3 toAttacker = attacker != null
                ? attacker.transform.position - transform.position
                : transform.forward;
            toAttacker.y = 0f;
            if (toAttacker.sqrMagnitude < 0.01f)
                toAttacker = transform.forward;

            // #963 S14 U1: a man on the floor cannot be marched. The heavy hit that puts him down
            // is the crew's cheap, repeatable stall, and the host setting decides whether it
            // actually frees him; the release is taken before the knockdown state is entered so
            // the follow never runs a tick against an escort holding a body that cannot walk.
            if (IsServer && IsInCustody
                && Escalations.BlacksiteRetrieval.BlacksiteCustodySettings.CustodyBrokenByKnockdown)
                ReleaseCustodyServer(Escalations.BlacksiteRetrieval.BlacksiteCustodyBreak.Knockdown);

            _knockdownHitFromFront = Vector3.Dot(transform.forward, toAttacker.normalized) >= 0f;
            _knockdownActive = true;
            _knockedDown.Value = true;
            _knockdownStartedAt = Time.time;
            float duration = WhistleblowerAnimationDriver.KnockdownDuration(_knockdownHitFromFront);
            _knockdownEndsAt = Time.time + duration + KnockdownRecoveryMargin;
            _nextCrawlDestinationAt = 0f;
            // #982: the cumulative window is spent by the knockdown it bought, so the next
            // three hits have to be earned again rather than the fourth hit chaining a fall.
            _cumulativeHitCount = 0;
            _cumulativeHitWindowEnds = 0f;
            // #982: the EscapeState fixup that used to sit here was unreachable - HitEnemy is
            // the only caller and it returns on EscapeState before the knockdown branch.
            _knockdownAttacker = attacker;

            StopAgent();
            if (agent != null)
                agent.updateRotation = false;
            PlayActionClientRpc((int)(_knockdownHitFromFront
                ? WhistleblowerAnimationAction.KnockdownFront
                : WhistleblowerAnimationAction.KnockdownBack));
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Whistleblower knocked down from {(_knockdownHitFromFront ? "front" : "back")} for {duration:0.00}s.");
        }

        private void UpdateKnockdownServer()
        {
            float now = Time.time;
            if (now >= _knockdownEndsAt)
            {
                _knockdownActive = false;
                _knockedDown.Value = false;
                _knockdownCooldownUntil = now + KnockdownCooldown;

                // #1293 R2: a knockdown that did not free him (Custody Broken By Knockdown off), or
                // one taken in the ship's seat, stands him back up held: the attachment or the seat
                // still writes his body, so his agent stays stood down and his behaviour suspended.
                if (IsInCustody || _custodyHeldAboard)
                {
                    _knockdownAttacker = null;
                    StandDownAgentServer();
                    if (currentBehaviourStateIndex != CustodyState)
                        SwitchToBehaviourState(CustodyState);
                    return;
                }

                if (agent != null && !_agentUnbound)
                    agent.updateRotation = true;

                // Getting back up is his best moment to break contact: give him a
                // strong flee roll so knockdown loops don't pin him in place forever.
                // #1158: outside there is nothing to break contact TO, and the roll is the last
                // way into the escape run out there (a knockdown that broke custody hands his
                // body back with Release's Cower, and a heavy hit landing on a cowering man
                // reaches this same stand-up), so outside it is replaced by the cower itself.
                if (!IsInsideFacility)
                    EnterCowerServer();
                else if (_escapesCompleted.Value < MaxEscapes
                    && !_agentUnbound
                    && currentBehaviourStateIndex != EscapeState
                    && currentBehaviourStateIndex != LastStandState
                    && UnityEngine.Random.value <= PostKnockdownFleeChance)
                {
                    // #982: the threat he runs from is whoever put him down. _combatTarget is
                    // null on a knockdown taken out of hiding, and a null threat makes the
                    // escape search flee his own back instead of the man with the shovel.
                    PlayerControllerB threat = IsLivingInteriorPlayer(_knockdownAttacker)
                        ? _knockdownAttacker
                        : _combatTarget;
                    BeginEscapeServer(threat);
                }

                _knockdownAttacker = null;
                return;
            }

            float elapsed = now - _knockdownStartedAt;
            float crawlStart = WhistleblowerAnimationDriver.CrawlStartTime(_knockdownHitFromFront);
            float crawlEnd = WhistleblowerAnimationDriver.CrawlEndTime(_knockdownHitFromFront);
            // #1293 R2: no crawl while something else writes his body (the attachment, the seat)
            // or his agent has not bound yet: a steered agent there only mimes a crawl.
            if (elapsed < crawlStart || elapsed >= crawlEnd || IsInCustody || _custodyHeldAboard || _agentUnbound)
            {
                StopAgent();
                return;
            }

            if (now < _nextCrawlDestinationAt)
                return;

            _nextCrawlDestinationAt = now + 0.35f;
            if (agent != null)
                agent.updateRotation = false;

            // #983: the crawl destination is a blind 1.35 m behind him with his facing frozen
            // for the chain, and the sample radius resolved it through thin walls. He fell with
            // his back to something: the crawl step is skipped and he scrambles in place
            // instead of scrambling through the wall.
            if (!TryResolveReachableDestination(transform.position - transform.forward * 1.35f, MinCrawlStep, out Vector3 crawlDestination))
            {
                StopAgent();
                return;
            }

            SetAgentSpeed(CrawlSpeed, stopped: false, stoppingDistance: 0.05f);
            SetDestinationToPosition(crawlDestination, checkForPath: false);
            CommitDestination();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (!_knockdownFlagSubscribed)
            {
                _knockedDown.OnValueChanged += OnKnockedDownChanged;
                _knockdownFlagSubscribed = true;
            }

            // OnValueChanged never fires for the value that arrived with the spawn payload,
            // so a client that spawns him while he is already on the floor reconciles off it.
            if (_knockedDown.Value)
                _knockdownChainPending = true;
        }

        public override void OnNetworkDespawn()
        {
            if (_knockdownFlagSubscribed)
            {
                _knockedDown.OnValueChanged -= OnKnockedDownChanged;
                _knockdownFlagSubscribed = false;
            }

            _knockdownChainPending = false;
            base.OnNetworkDespawn();
        }

        /// <summary>
        /// #983: <see cref="PlayActionClientRpc"/> is dropped on the floor by any client whose
        /// Playables graph is not built when it lands - <c>ApplyAction</c> returns early - and
        /// that client then never played the chain and never recovered, because it only used the
        /// replicated flag to suppress staggers. The flag stays the source of truth: the
        /// transition is latched here and <see cref="TickKnockdownChainClient"/> starts the chain
        /// as soon as the driver is ready.
        /// </summary>
        private void OnKnockedDownChanged(bool previous, bool current)
        {
            if (previous == current)
                return;

            // He is up again. A chain still playing past its expected end finishes naturally
            // rather than being cut mid-get-up; only a request that never started is dropped.
            _knockdownChainPending = current;
        }

        private void TickKnockdownChainClient()
        {
            if (!_knockdownChainPending)
                return;

            if (isEnemyDead || Neutralized)
            {
                _knockdownChainPending = false;
                return;
            }

            if (_animationDriver == null || !_animationDriver.IsReady)
                return;

            _knockdownChainPending = false;
            if (_animationDriver.IsKnockdownPlaying)
                return;

            // The hit direction only picks the event: both knockdown actions map onto the
            // continuity-safe front fall/crawl/front get-up chain (docs/assets/11-whistleblower-animation-inventory-and-selection.md),
            // so the unreplicated _knockdownHitFromFront is not needed here.
            _animationDriver.ApplyAction(WhistleblowerAnimationAction.KnockdownFront);
        }

        private bool TryFindObservingPlayer(out PlayerControllerB observer, out float bestDistance)
        {
            observer = null;
            bestDistance = float.MaxValue;
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return false;

            foreach (PlayerControllerB player in players)
            {
                if (!PlayerObserves(player, ObservationDistance, out float distance))
                    continue;
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                observer = player;
            }

            return observer != null;
        }

        private bool PlayerObserves(PlayerControllerB player, float maxDistance, out float distance)
        {
            distance = float.MaxValue;
            if (!ObservingPlayerIsEligible(player))
                return false;

            distance = Vector3.Distance(player.transform.position, transform.position);
            if (distance > maxDistance)
                return false;

            // #982: the width is a half-angle, so 80 was a 160 degree field - he was "seen" by
            // anyone facing roughly his half of the room. 45 is the vanilla enemy figure and
            // gives a 90 degree cone. The range argument used to be the measured distance plus
            // two metres, which can never gate anything; it is the caller's range now, so a
            // discovery check really is a discovery-range check.
            return player.HasLineOfSightToPosition(
                MarkerPosition,
                45f,
                Mathf.CeilToInt(maxDistance),
                1.5f);
        }

        /// <summary>
        /// Starts an escape-destination search and immediately points him at a cheap
        /// fallback so he moves from the first frame. #716/B10: the full search used to
        /// run one <c>CalculatePath</c> per inside AI node in a single frame; it is now
        /// sliced by <see cref="TickEscapeSearch"/> a few paths at a time, keeping the
        /// running best, and its result replaces the fallback when it lands.
        /// </summary>
        private void BeginEscapeSearch(PlayerControllerB threat)
        {
            Vector3 threatPosition = IsLivingInteriorPlayer(threat)
                ? threat.transform.position
                : transform.position - transform.forward;
            Vector3 away = transform.position - threatPosition;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
                away = transform.forward;
            away.Normalize();

            _escapeSearchThreatPosition = threatPosition;
            _escapeSearchAway = away;
            _escapeSearchBestScore = float.MinValue;
            _escapeSearchBestPosition = Vector3.zero;
            _escapeSearchRelaxedReach = -1f;
            _escapeSearchRelaxedPosition = Vector3.zero;
            _escapeSearchIndex = 0;

            // #963 S14 U1: the node set is the one for the world he is standing in. He can only
            // be outside after a custody hold took him there (nothing else moves him out of the
            // facility), so this reads exactly as it always has on every other path.
            GameObject[] nodes = isOutside
                ? RoundManager.Instance?.outsideAINodes
                : RoundManager.Instance?.insideAINodes;
            if (nodes == null || nodes.Length == 0)
                nodes = GameObject.FindGameObjectsWithTag("AINode");
            _escapeSearchNodes = nodes;
            _escapeSearchActive = nodes != null && nodes.Length > 0;

            if (!_fugitiveRetreat)
                _escapeDestination = FallbackEscapeDestination(away);
        }

        /// <summary>Abandons an in-flight escape search when he leaves the escape state.</summary>
        private void CancelEscapeSearch()
        {
            _escapeSearchActive = false;
            _escapeSearchNodes = null;
        }

        private Vector3 FallbackEscapeDestination(Vector3 away)
        {
            Vector3 fallback = transform.position + away * 32f;
            if (NavMesh.SamplePosition(fallback, out NavMeshHit fallbackHit, 18f, NavMesh.AllAreas))
                return fallbackHit.position;
            return transform.position + away * 8f;
        }

        /// <summary>
        /// Advances the sliced escape search by at most <see cref="EscapeNodesPerFrame"/>
        /// path calculations, and commits the winner once the node list is exhausted.
        /// </summary>
        private void TickEscapeSearch()
        {
            if (!_escapeSearchActive)
                return;

            GameObject[] nodes = _escapeSearchNodes;
            if (nodes == null)
            {
                _escapeSearchActive = false;
                return;
            }

            int paths = 0;
            int scanned = 0;
            while (_escapeSearchIndex < nodes.Length && paths < EscapeNodesPerFrame && scanned < EscapeNodeScanCapPerFrame)
            {
                GameObject node = nodes[_escapeSearchIndex++];
                scanned++;

                if (node == null || !NavMesh.SamplePosition(node.transform.position, out NavMeshHit hit, 8f, NavMesh.AllAreas))
                    continue;

                Vector3 candidate = hit.position;
                float selfDistance = Vector3.Distance(transform.position, candidate);
                if (selfDistance < 12f)
                    continue;

                // #765: the tile graph decides "far enough" before any path is paid for;
                // the path-length fallback only applies when there is no graph. A node
                // that falls short still competes as the relaxed pick when it reaches
                // further than anything seen so far.
                int hops = _tileGraph != null ? _tileGraph.HopsTo(_escapeStartHops, candidate) : -1;
                if (_tileGraph != null && hops < 0)
                    continue;
                bool qualifies = _tileGraph == null || hops >= _fleeTiles;
                if (_tileGraph != null && !qualifies && hops <= _escapeSearchRelaxedReach)
                    continue;

                float pathLength = selfDistance;
                if (agent != null && agent.enabled && agent.isOnNavMesh)
                {
                    paths++;
                    var path = new NavMeshPath();
                    if (!agent.CalculatePath(candidate, path) || path.status != NavMeshPathStatus.PathComplete)
                        continue;
                    if (_fugitiveRetreat && !FugitivePathClearsCrew(path))
                        continue;
                    pathLength = PathLength(path);
                }

                float reach = _tileGraph != null ? hops : pathLength / MetersPerTileFallback;
                if (_tileGraph == null)
                    qualifies = reach >= _fleeTiles;
                if (!qualifies)
                {
                    if (reach > _escapeSearchRelaxedReach)
                    {
                        _escapeSearchRelaxedReach = reach;
                        _escapeSearchRelaxedPosition = candidate;
                    }
                    continue;
                }

                Vector3 direction = candidate - transform.position;
                direction.y = 0f;
                float awayScore = direction.sqrMagnitude > 0.01f
                    ? Vector3.Dot(direction.normalized, _escapeSearchAway)
                    : 0f;
                float threatDistance = Vector3.Distance(_escapeSearchThreatPosition, candidate);
                // Far enough wins, but not across the whole map: past the target hop
                // count each extra tile costs, so he settles a few rooms out rather
                // than at the far end of the facility.
                float overshoot = hops >= 0
                    ? Mathf.Max(0, hops - (_fleeTiles + 2)) * 6f
                    : Mathf.Max(0f, pathLength - (_fleeTiles + 2) * MetersPerTileFallback) * 0.5f;
                float score = threatDistance * 1.25f
                    + selfDistance * 0.35f
                    + awayScore * 16f
                    - overshoot
                    + UnityEngine.Random.Range(0f, 4f);

                if (score <= _escapeSearchBestScore)
                    continue;
                _escapeSearchBestScore = score;
                _escapeSearchBestPosition = candidate;
            }

            if (_escapeSearchIndex < nodes.Length)
                return;

            _escapeSearchActive = false;
            _escapeSearchNodes = null;

            // The retry window only opens once a search has actually finished, so an
            // unreachable destination cannot restart the sweep every 1.25s.
            _nextDestinationUpdate = Time.time + 1.25f;

            if (_escapeSearchBestScore > float.MinValue)
            {
                _escapeRequiredReach = _fleeTiles;
                _escapeDestination = _escapeSearchBestPosition;
                SetDestinationToPosition(_escapeDestination, checkForPath: false);
            }
            else if (_escapeSearchRelaxedReach > 0f)
            {
                _escapeRequiredReach = Mathf.Max(1f, _escapeSearchRelaxedReach);
                _escapeDestination = _escapeSearchRelaxedPosition;
                SetDestinationToPosition(_escapeDestination, checkForPath: false);
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Whistleblower escape: no node reaches {_fleeTiles} tiles from here; settling for {_escapeSearchRelaxedReach:0.0}.");
            }
        }

        private bool NeedsNewDestination()
        {
            if (agent == null || !agent.enabled || !agent.isOnNavMesh || agent.pathPending)
                return false;
            if (agent.pathStatus == NavMeshPathStatus.PathInvalid)
                return true;
            return !agent.hasPath && Vector3.Distance(transform.position, _escapeDestination) > 2f;
        }

        private bool HasReachedDestination(float distance)
        {
            if (agent == null || !agent.enabled || !agent.isOnNavMesh)
                return Vector3.Distance(transform.position, _escapeDestination) <= distance;
            if (agent.pathPending)
                return false;
            // #765: with no path yet, remainingDistance reads zero and used to count as
            // an arrival the moment the stumble released him.
            if (!agent.hasPath)
                return Vector3.Distance(transform.position, _escapeDestination) <= distance;
            return agent.remainingDistance <= distance;
        }

        private static float PathLength(NavMeshPath path)
        {
            Vector3[] corners = path?.corners;
            if (corners == null || corners.Length < 2)
                return 0f;

            float length = 0f;
            for (int i = 1; i < corners.Length; i++)
                length += Vector3.Distance(corners[i - 1], corners[i]);
            return length;
        }

        /// <summary>#765: latches the configured flee distance and the hop table from the
        /// tile he is escaping from, so every candidate and the arrival check measure
        /// against the same origin.</summary>
        private void PrepareEscapeDistance()
        {
            _fleeTiles = ContractsBootstrap.WhistleblowerBalance != null
                ? Mathf.Clamp(ContractsBootstrap.WhistleblowerBalance.FleeDistanceTiles.Value, 2, 12)
                : 5;

            if (!_tileGraphSearched)
            {
                _tileGraphSearched = true;
                _tileGraph = FacilityTileGraph.TryBuild();
                ContractsBootstrap.Log?.LogInfo(_tileGraph != null
                    ? $"[MoonContracts] Whistleblower flee distance: {_fleeTiles} tiles over a {_tileGraph.TileCount}-tile graph."
                    : $"[MoonContracts] Whistleblower flee distance: no tile graph; using {_fleeTiles * MetersPerTileFallback:0} path metres.");
            }

            _escapeStartHops = _tileGraph != null
                ? _tileGraph.HopsFrom(_tileGraph.FindTile(transform.position))
                : null;
            _escapeRequiredReach = _fleeTiles;
        }

        private bool EscapeFarEnough(Vector3 position)
        {
            if (_tileGraph != null)
                return _tileGraph.HopsTo(_escapeStartHops, position) >= _escapeRequiredReach;

            // Straight-line stands in for the path here, so it is scaled down: a route
            // through rooms is always longer than the crow flies.
            return Vector3.Distance(_escapeStartPosition, position) >= _escapeRequiredReach * MetersPerTileFallback * 0.6f;
        }

        private void FaceTarget(PlayerControllerB target)
        {
            if (target == null)
                return;

            Vector3 direction = target.transform.position - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.01f)
                return;

            Quaternion desired = Quaternion.LookRotation(direction.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, desired, 540f * Time.deltaTime);
        }

        private void ResolveRuntimeReferences()
        {
            if (agent == null)
                agent = GetComponent<NavMeshAgent>();
            if (creatureAnimator == null)
                creatureAnimator = GetComponentInChildren<Animator>(true);
            if (_animationDriver == null)
                _animationDriver = GetComponent<WhistleblowerAnimationDriver>();
            if (creatureVoice == null || creatureSFX == null || !_audioRolesResolved)
                ResolveAudioSourcesByRole();
            if (_trigger == null)
                _trigger = GetComponentInChildren<InteractTrigger>(true);
            if (_scanNode == null)
                _scanNode = GetComponentInChildren<ScanNodeProperties>(true);
        }

        /// <summary>
        /// #984: pick the three emitters by NAME, never by "first AudioSource in the
        /// hierarchy". The old <c>creatureVoice ??= GetComponentInChildren&lt;AudioSource&gt;</c>
        /// could resolve to the SFX child (child order is not ours on a bundled visual), and
        /// <see cref="ConfigureSpatialAudio"/>'s <c>creatureSFX != creatureVoice</c> guard
        /// then skipped the SFX carry profile entirely — the combat foley silently stayed
        /// off the diagetic mixer for the whole round.
        /// </summary>
        private void ResolveAudioSourcesByRole()
        {
            AudioSource[] sources = GetComponentsInChildren<AudioSource>(true);
            if (sources == null || sources.Length == 0)
                return;

            // ResolveRuntimeReferences runs every Update; latch the search so a visual that
            // simply has no footstep child does not walk the hierarchy every frame.
            _audioRolesResolved = true;

            AudioSource voice = null;
            AudioSource sfx = null;
            AudioSource footsteps = null;
            AudioSource unnamed = null;

            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource source = sources[i];
                if (source == null)
                    continue;

                string name = source.gameObject.name;
                if (footsteps == null && name.IndexOf("Footstep", StringComparison.OrdinalIgnoreCase) >= 0)
                    footsteps = source;
                else if (sfx == null && name.IndexOf("SFX", StringComparison.OrdinalIgnoreCase) >= 0)
                    sfx = source;
                else if (voice == null && name.IndexOf("Voice", StringComparison.OrdinalIgnoreCase) >= 0)
                    voice = source;
                else if (unnamed == null)
                    unnamed = source;
            }

            // A visual without our named children still needs a voice; take an unrelated
            // source only when it is not already serving one of the other two roles.
            if (voice == null && unnamed != null)
                voice = unnamed;

            if (creatureVoice == null && voice != null)
                creatureVoice = voice;
            if (creatureSFX == null && sfx != null)
                creatureSFX = sfx;
            if (_footstepAudio == null)
                _footstepAudio = footsteps ?? creatureSFX;
        }

        private void ConfigureAnimationDriver()
        {
            if (creatureAnimator == null)
                return;

            if (_animationDriver == null)
                _animationDriver = GetComponent<WhistleblowerAnimationDriver>();
            if (_animationDriver == null)
                _animationDriver = gameObject.AddComponent<WhistleblowerAnimationDriver>();
            _animationDriver.Configure(creatureAnimator);

            // #716/B10: this ran every frame, unsubscribing and resubscribing the same
            // delegate. Subscribe once; OnDestroy takes it back off.
            if (!_footstepSubscribed)
            {
                _animationDriver.Footstep -= OnDriverFootstep;
                _animationDriver.Footstep += OnDriverFootstep;
                _animationDriver.RootDisplacement -= OnDriverRootDisplacement;
                _animationDriver.RootDisplacement += OnDriverRootDisplacement;
                _footstepSubscribed = true;
            }
        }

        public override void OnDestroy()
        {
            if (_footstepSubscribed && _animationDriver != null)
            {
                _animationDriver.Footstep -= OnDriverFootstep;
                _animationDriver.RootDisplacement -= OnDriverRootDisplacement;
            }
            _footstepSubscribed = false;
            base.OnDestroy();
        }

        // #765: the driver holds the hips in place while a one-shot's baked travel plays
        // and hands the travel here; the owner moves the root by it, on the navmesh when
        // the agent is live, so the enemy ends the clip where the clip put him and the
        // clients follow through the normal position sync.
        private void OnDriverRootDisplacement(Vector3 worldDelta)
        {
            // #1293 R2: while he is held or seated, the attachment or the ship's seat is his one
            // body writer, so a clip's baked travel is not applied.
            if (!IsServer || isEnemyDead || IsInCustody || _custodyHeldAboard)
                return;

            // #983: the vertical component is never travel - the hip offset is measured flat
            // and the agent owns his height - and the per-frame cap keeps a hitch from
            // arriving as one metre-long step.
            worldDelta.y = 0f;
            float magnitude = worldDelta.magnitude;
            if (magnitude <= 0.0001f)
                return;
            if (magnitude > MaxDisplacementStep)
            {
                worldDelta *= MaxDisplacementStep / magnitude;
                magnitude = MaxDisplacementStep;
            }

            Vector3 from = transform.position;
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                // #983: agent.Move applied the raw step with no navmesh test at all, so a fall
                // taken with his back to a wall pushed him through it. The step is truncated at
                // the first edge in the way and dropped when the wall is already on top of him.
                if (TryTruncateAgainstNavMesh(from, worldDelta, magnitude, DisplacementWallMargin, MinDisplacementStep, out Vector3 clamped))
                    agent.Move(clamped);
                return;
            }

            // Round 2: the raw fallback walked him through walls when a fall played with
            // the agent off the mesh. Clamp the step to the navmesh or drop it. #983: and
            // never accept a sample the navmesh says is not reachable in a straight line
            // from where he stands.
            if (!NavMesh.SamplePosition(from + worldDelta, out NavMeshHit hit, 0.75f, NavMesh.AllAreas))
                return;

            // The straight-line test needs a start on the mesh. When he is too far off it to
            // project, the sample is the only way back and is taken as before.
            if (!NavMesh.SamplePosition(from, out NavMeshHit here, DestinationSampleRadius, NavMesh.AllAreas))
            {
                transform.position = hit.position;
                return;
            }

            Vector3 recovery = hit.position - here.position;
            recovery.y = 0f;
            float recoveryLength = recovery.magnitude;
            if (recoveryLength <= 0.0001f)
            {
                transform.position = hit.position;
                return;
            }

            if (TryTruncateAgainstNavMesh(here.position, recovery, recoveryLength, DisplacementWallMargin, MinDisplacementStep, out Vector3 step))
                transform.position = here.position + step;
        }

        /// <summary>
        /// #983: truncates <paramref name="delta"/> at the first navmesh edge between
        /// <paramref name="from"/> and its end, keeping <paramref name="margin"/> clear of the
        /// edge. False means the step is blocked within <paramref name="minUseful"/> and must
        /// be dropped rather than shortened.
        /// </summary>
        private static bool TryTruncateAgainstNavMesh(
            Vector3 from,
            Vector3 delta,
            float magnitude,
            float margin,
            float minUseful,
            out Vector3 truncated)
        {
            truncated = delta;
            if (magnitude <= 0.0001f)
                return false;

            if (!NavMesh.Raycast(from, from + delta, out NavMeshHit blocked, NavMesh.AllAreas))
                return true;

            float usable = Vector3.Distance(from, blocked.position) - margin;
            if (usable < minUseful)
                return false;

            truncated = delta * (Mathf.Min(usable, magnitude) / magnitude);
            return true;
        }

        /// <summary>
        /// #983: resolves a blind offset candidate (a step back, a sidestep, a crawl behind him)
        /// into somewhere he can actually walk to. The sample radius alone answers with points
        /// through thin walls, so the sampled candidate is truncated at the first navmesh edge
        /// on the straight line from his feet, kept an agent radius clear of it. False means
        /// there is less than <paramref name="minUseful"/> of room and the move should be skipped.
        /// </summary>
        private bool TryResolveReachableDestination(Vector3 candidate, float minUseful, out Vector3 result)
        {
            Vector3 from = transform.position;
            result = from;
            if (NavMesh.SamplePosition(candidate, out NavMeshHit sampled, DestinationSampleRadius, NavMesh.AllAreas))
                candidate = sampled.position;

            Vector3 offset = candidate - from;
            offset.y = 0f;
            float distance = offset.magnitude;
            if (distance < minUseful)
                return false;

            float clearance = agent != null ? agent.radius : 0.44f;
            if (!TryTruncateAgainstNavMesh(from, offset, distance, clearance, minUseful, out Vector3 step))
                return false;

            result = from + step;
            return true;
        }

        private void ConfigureSpatialAudio()
        {
            if (_voicePropagation == null && creatureVoice != null)
                _voicePropagation = ContractSpatialAudio.ApplyFacilityCarryProfile(creatureVoice, 36f, 2f);
            if (_sfxPropagation == null && creatureSFX != null && creatureSFX != creatureVoice)
                _sfxPropagation = ContractSpatialAudio.ApplyFacilityCarryProfile(creatureSFX, 26f, 1.5f);
            // #984: footsteps take the same facility carry range, plus the two things that
            // separate "a body walking next to you" from "something moving elsewhere in the
            // building": 3.5 m of full gain before the fade starts (Custom rolloff ignores
            // minDistance, so without this the curve is already at half gain by 2.6 m), and
            // the relaxed transient absorption so the 1-6 kHz click survives the near field.
            if (_footstepPropagation == null
                && _footstepAudio != null
                && _footstepAudio != creatureVoice
                && _footstepAudio != creatureSFX)
            {
                _footstepPropagation = ContractSpatialAudio.ApplyNearFieldCarryProfile(
                    _footstepAudio,
                    26f,
                    1.5f,
                    FootstepNearFieldPlateauMeters,
                    ContractAudioAbsorptionProfile.NearFieldTransient);
            }
        }

        private void ConfigureAgent()
        {
            if (agent == null)
                return;

            agent.speed = CorneredSpeed;
            agent.angularSpeed = 540f;
            agent.acceleration = 18f;
            agent.stoppingDistance = 1.55f;
            agent.radius = 0.44f;
            agent.height = 2.55f;
            agent.baseOffset = 0f;
            agent.autoBraking = true;
            agent.updateRotation = true;

            if (!agent.enabled)
                ContractAssetLoader.TryActivateAgentOnNavMesh(gameObject, 8f);
        }

        private void SetAgentSpeed(float speed, bool stopped, float stoppingDistance = 1.55f)
        {
            if (agent == null)
                return;

            agent.speed = speed;
            agent.stoppingDistance = Mathf.Max(0f, stoppingDistance);
            agent.acceleration = stopped ? 40f : 18f;
            if (agent.enabled && agent.isOnNavMesh)
                agent.isStopped = stopped;
        }

        private void StopAgent()
        {
            // Round 2: vanilla's DoAIInterval kept re-issuing the last destination behind every
            // stop, so a stopped agent drifted toward the player and the crawl started on the
            // stale combat target. Cleared FIRST: an agentless body (a stow, a teardown) must
            // still count as stopped, which is what #1158's cower tick early-outs on.
            moveTowardsDestination = false;
            if (agent == null)
                return;

            if (agent.enabled && agent.isOnNavMesh)
            {
                agent.isStopped = true;
                agent.ResetPath();
            }
            agent.speed = 0f;
        }

        // SetDestinationToPosition only records the destination; vanilla hands it to the
        // agent on the next DoAIInterval (~0.2 s). A 0.6 s burst or a 0.35 s crawl step
        // cannot afford that, so the paths that need it push it through at once.
        private void CommitDestination()
        {
            if (agent == null || !agent.enabled || !agent.isOnNavMesh || agent.isStopped)
                return;
            agent.SetDestination(destination);
        }

        // #486 verdict round 3: the runtime player match scaled him 7x. A skinned mesh is
        // sized by its bones, not by its renderer transform, so `sharedMesh.bounds *
        // renderer.lossyScale` read vanilla's LOD1 as 12.10 m (16.33 mesh units * 0.7407).
        // The root scale is now committed on the prefab (WhistleblowerEnemyRegistry
        // .VanillaVisualScale = 1.576, derived from the ripped Player prefab and mesh).
        // This only logs, once per session, what the correct rule reads on both models:
        // mesh-space extent * (bone world scale / bind-pose bone scale). Both are the same
        // scavenger mesh, so the two numbers should agree (ratio 1.000) when he stands
        // player height. Nothing here changes the transform.
        private static bool _vanillaVisualHeightLogged;

        private void LogVanillaVisualHeightCheck()
        {
            if (_vanillaVisualHeightLogged || !ContractAssetLoader.WhistleblowerVisualIsVanillaVariant)
                return;
            _vanillaVisualHeightLogged = true;

            float ownExtent = MeasureSkinnedExtent(transform, out string ownRenderer);
            float playerExtent = MeasurePlayerSkinnedExtent();
            if (ownExtent <= 0f || playerExtent <= 0f)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts] Whistleblower vanilla visual height check unmeasured (player {playerExtent:0.00} m, own {ownExtent:0.00} m); root scale {transform.lossyScale.y:0.000} kept.");
                return;
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Whistleblower vanilla visual height check: root scale {transform.lossyScale.y:0.000}, own '{ownRenderer}' {ownExtent:0.00} m, player model {playerExtent:0.00} m, ratio {playerExtent / ownExtent:0.000} (1.000 = player height).");
        }

        private static float MeasurePlayerSkinnedExtent()
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return 0f;

            foreach (PlayerControllerB player in players)
            {
                if (player == null)
                    continue;
                Transform model = player.transform.Find("ScavengerModel");
                float extent = MeasureSkinnedExtent(model != null ? model : player.transform, out _);
                if (extent > 0f)
                    return extent;
            }

            return 0f;
        }

        // Largest bind-pose extent of the tallest skinned mesh under the root, in world
        // metres. Skinned vertices follow their bones, so mesh units convert to world units
        // through a bone's current world scale over its bind-pose scale in mesh space
        // (the inverse bind pose), not through the renderer's own transform. Pose-invariant:
        // humanoid animation rotates bones and never rescales them.
        private static float MeasureSkinnedExtent(Transform root, out string rendererName)
        {
            rendererName = null;
            float best = 0f;
            if (root == null)
                return 0f;

            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer == null || renderer.sharedMesh == null)
                    continue;
                if (renderer.name.IndexOf("ArmsOnly", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                float meshToWorld = MeshToWorldScale(renderer);
                if (meshToWorld <= 0f)
                    continue;

                Vector3 size = renderer.sharedMesh.bounds.size;
                float extent = Mathf.Max(size.x, size.y, size.z) * meshToWorld;
                if (extent > best)
                {
                    best = extent;
                    rendererName = renderer.name;
                }
            }

            return best;
        }

        private static float MeshToWorldScale(SkinnedMeshRenderer renderer)
        {
            Transform[] bones = renderer.bones;
            Matrix4x4[] bindposes = renderer.sharedMesh.bindposes;
            if (bones == null || bindposes == null || bones.Length == 0 || bindposes.Length == 0)
                return 0f;

            int index = -1;
            if (renderer.rootBone != null)
                index = Array.IndexOf(bones, renderer.rootBone);
            if (index < 0 || index >= bindposes.Length || bones[index] == null)
            {
                for (int i = 0; i < bones.Length && i < bindposes.Length; i++)
                {
                    if (bones[i] != null)
                    {
                        index = i;
                        break;
                    }
                }
            }
            if (index < 0)
                return 0f;

            // The bind pose maps mesh space into bone space, so its inverse holds the bone's
            // mesh-space transform at bind time; the length of one basis column is its scale.
            float bindScale = ((Vector3)bindposes[index].inverse.GetColumn(0)).magnitude;
            float worldScale = Mathf.Abs(bones[index].lossyScale.y);
            return bindScale > 1e-5f ? worldScale / bindScale : 0f;
        }

        private void EnsureBehaviourStates()
        {
            // #1023 appends assessment after custody, #1158 the cower after that and #874 F02 the
            // rally after that; an older prefab's array would throw the moment a switch indexed
            // the state it lacks.
            if (enemyBehaviourStates != null && enemyBehaviourStates.Length == 9)
                return;

            enemyBehaviourStates = CreateBehaviourStates();
        }

        internal static EnemyBehaviourState[] CreateBehaviourStates()
        {
            return new[]
            {
                new EnemyBehaviourState { name = "Hiding", IsAnimTrigger = false },
                new EnemyBehaviourState { name = "Cornered", IsAnimTrigger = false },
                new EnemyBehaviourState { name = "Escape", IsAnimTrigger = false },
                new EnemyBehaviourState { name = "LastStand", IsAnimTrigger = false },
                new EnemyBehaviourState { name = "Neutralized", IsAnimTrigger = false },
                // #963 S14 U1: appended after Neutralized so indices 0 to 4 keep their meaning.
                new EnemyBehaviourState { name = "Custody", IsAnimTrigger = false },
                new EnemyBehaviourState { name = "Assessing", IsAnimTrigger = false },
                // #1158: appended after Assessing, same rule.
                new EnemyBehaviourState { name = "Cowering", IsAnimTrigger = false },
                // #874 F02: appended after Cowering, same rule.
                new EnemyBehaviourState { name = "Rally", IsAnimTrigger = false },
            };
        }

        private void UpdateContractStateFromBehaviour()
        {
            if (Neutralized && !_neutralizedNotified)
            {
                _neutralizedNotified = true;
                MoonContractState.NotifyWhistleblowerNeutralized(this);
            }
        }

        private void DisableInteractionPrompt()
        {
            if (_trigger == null)
                return;

            // #716/B10: this is a one-shot teardown, but it ran (with a GetComponent) on
            // every frame. Latch it and only redo the work if something re-enables it.
            if (_interactionPromptDisabled
                && !_trigger.interactable
                && (_triggerCollider == null || !_triggerCollider.enabled))
                return;

            _trigger.interactable = false;
            _trigger.hoverTip = string.Empty;
            _trigger.disabledHoverTip = string.Empty;
            if (_triggerCollider == null)
                _triggerCollider = _trigger.GetComponent<Collider>();
            if (_triggerCollider != null)
                _triggerCollider.enabled = false;
            _interactionPromptDisabled = true;
        }

        private void UpdatePresentationForState()
        {
            if (_scanNode == null)
                return;

            if (Neutralized)
                _scanNode.subText = "neutralized";
            else
                _scanNode.subText = IsObservedRecently ? "kill target" : "find";
        }

        private void SetNeutralizedPresentation()
        {
            DisableInteractionPrompt();
            if (_scanNode != null)
                _scanNode.subText = "neutralized";
        }

        private void RepairVisualIfNeeded()
        {
            if (_visualRepairComplete || Time.time < _nextVisualRepairAttempt)
                return;

            _nextVisualRepairAttempt = Time.time + 1.5f;
            _visualRepairComplete = WhistleblowerEnemyRegistry.RepairVisualInstance(gameObject) > 0;
            // A repair pass is the only thing that can add the fallback visual.
            _fallbackVisualSearched = false;
        }

        private void UpdateAnimation()
        {
            if (creatureAnimator == null)
                return;

            float delta = Mathf.Max(Time.deltaTime, 0.001f);
            Vector3 worldVelocity;
            float speed;
            if (_attached)
            {
                // #1293 R2: held, he walks by the attachment's own motion on every peer
                // (LateUpdate), so he strides with his carrier instead of sliding; his stood-down
                // agent and the client's position smoothing say nothing about it.
                worldVelocity = _attachedVelocity;
                speed = worldVelocity.magnitude;
            }
            else
            {
                worldVelocity = (transform.position - _lastPosition) / delta;
                speed = worldVelocity.magnitude;
                // Only realised movement drives the locomotion pick and its stride scale.
                // agent.desiredVelocity is what the steering solver asked for and stays
                // large while he is blocked or planted, which made him mime a walk on the
                // spot and jitter between shuffle directions.
                if (agent != null && agent.enabled)
                {
                    if (agent.velocity.sqrMagnitude > worldVelocity.sqrMagnitude)
                        worldVelocity = agent.velocity;
                    speed = Mathf.Max(speed, agent.velocity.magnitude);
                }
            }

            _lastPosition = transform.position;
            float normalizedSpeed = Mathf.InverseLerp(0.05f, EscapeSpeed, speed);
            WhistleblowerAnimationMode mode = ModeForState(currentBehaviourStateIndex);
            _animationDriver?.SetMovement(normalizedSpeed, worldVelocity, mode, Neutralized);
            // Only the repair pass can add the fallback visual, so the rig walk happens
            // once and again only after a repair attempt - never on a normal frame.
            if (!_fallbackVisualSearched)
            {
                _fallbackVisualSearched = true;
                _fallbackVisual = WhistleblowerEnemyRegistry.FindFallbackVisual(gameObject);
            }
            WhistleblowerEnemyRegistry.UpdateFallbackPose(
                _fallbackVisual,
                // #963 S14 U1: the fallback rig's "scared" pose is the same one custody reads as,
                // so a client on the fallback visual sees a marched man rather than a boxer.
                mode == WhistleblowerAnimationMode.Hiding || mode == WhistleblowerAnimationMode.Custody,
                Neutralized,
                normalizedSpeed);
        }

        private void PlayDeterministicHitReaction(int force, int hitID)
        {
            int seed = enemyHP * 31 + force * 17 + Mathf.Max(0, hitID);
            int variant = Mathf.Abs(seed) % 3;
            WhistleblowerAnimationAction action = variant == 0
                ? WhistleblowerAnimationAction.HeadHit
                : variant == 1
                    ? WhistleblowerAnimationAction.BodyHit
                    : WhistleblowerAnimationAction.RibHit;
            _animationDriver?.ApplyAction(action);
        }

        [ClientRpc]
        private void PlayActionClientRpc(int action)
        {
            ResolveRuntimeReferences();
            ConfigureAnimationDriver();
            if (Enum.IsDefined(typeof(WhistleblowerAnimationAction), action))
                _animationDriver?.ApplyAction((WhistleblowerAnimationAction)action);
        }

        [ClientRpc]
        private void PlaySwingMissClientRpc()
        {
            ResolveRuntimeReferences();
            AudioClip clip = WhistleblowerSoundLibrary.RandomSwingMiss();
            AudioSource source = creatureSFX != null ? creatureSFX : creatureVoice;
            if (clip == null || source == null)
                return;

            source.pitch = UnityEngine.Random.Range(0.96f, 1.05f);
            source.PlayOneShot(clip, SwingMissVolume * SfxSourceGainCompensation);
        }

        private void PlayBluntHitSound(int force, int hitID)
        {
            AudioSource source = creatureSFX != null ? creatureSFX : creatureVoice;
            AudioClip clip = WhistleblowerSoundLibrary.DeterministicBluntHit(enemyHP * 13 + force * 7 + Mathf.Max(0, hitID));
            if (clip == null || source == null)
            {
                PlayLocalChime(enemyHP <= 0 ? 0.55f : 0.85f);
                return;
            }

            source.pitch = UnityEngine.Random.Range(0.94f, 1.06f);
            source.PlayOneShot(clip, BluntHitVolume * SfxSourceGainCompensation);
        }

        private void OnDriverFootstep(float intensity)
        {
            // #984: a body that is prone or dead does not take steps. The driver has its own
            // gates (death lock, movement speed, root-mixer weight) and they stay; this is
            // the replicated knockdown state, which the driver cannot see, so a client whose
            // knockdown animation is late used to keep walking audibly while he lay there.
            if (isEnemyDead || _knockedDown.Value)
                return;

            ResolveRuntimeReferences();
            // The emitter can resolve after Start on a late-built visual; configuring here
            // costs three null checks on a footfall event, not per frame.
            if (_footstepPropagation == null)
                ConfigureSpatialAudio();

            // #984: the AI/mechanic half of a footstep, emitted once per footfall so it lands
            // exactly once whichever clip path plays below. Local on every client, like
            // vanilla's PlayFootstepLocal and like Bundy's EmitFootstepNoise: the footfall
            // event is itself client-local off the animation, so there is no RPC to double
            // up on, and the host fires it exactly once per step.
            EmitFootstepNoise(intensity);

            // #765: the vanilla-friendly model walks on vanilla's own surface footsteps.
            if (ContractAssetLoader.WhistleblowerVisualIsVanillaVariant && TryPlayVanillaFootstep(intensity))
                return;

            AudioClip clip = WhistleblowerSoundLibrary.RandomFootstep();
            AudioSource source = ResolveFootstepSource();
            if (clip == null || source == null)
                return;

            // #984: vanilla's own 0.6 walk / 0.9 run, on the emitter at his feet whose source
            // volume is 1.0 — not the old 0.55 * intensity on a 0.75 source.
            float volume = FootstepVolume(intensity);
            source.pitch = UnityEngine.Random.Range(0.92f, 1.08f);
            source.PlayOneShot(clip, volume);
            WalkieTalkie.TransmitOneShotAudio(source, clip, volume);
        }

        /// <summary>
        /// The footstep emitter, falling back to the combat-SFX and voice sources so a visual
        /// without the dedicated child still has audible steps.
        /// </summary>
        private AudioSource ResolveFootstepSource()
        {
            if (_footstepAudio != null)
                return _footstepAudio;
            return creatureSFX != null ? creatureSFX : creatureVoice;
        }

        /// <summary>
        /// #984: PlayerControllerB.PlayFootstepSound's loudness pair, driven off the driver's
        /// locomotion intensity: walking 0.6, running 0.9, boxing footwork clamped to walking.
        /// </summary>
        private static float FootstepVolume(float intensity)
        {
            return Mathf.Lerp(
                WalkFootstepVolume,
                RunFootstepVolume,
                Mathf.InverseLerp(WalkFootstepIntensity, RunFootstepIntensity, Mathf.Clamp01(intensity)));
        }

        /// <summary>
        /// #984: vanilla's footstep noise propagation (PlayFootstepServer/PlayFootstepLocal):
        /// 22 m at loudness 0.6 running, 17 m at 0.4 otherwise, halved inside a closed ship.
        /// This is the dial other enemies, mines and the radar read — not what a player hears.
        /// </summary>
        private void EmitFootstepNoise(float intensity)
        {
            RoundManager round = RoundManager.Instance;
            if (round == null)
                return;

            bool running = intensity >= RunFootstepThreshold;
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

        // Surface pick copied from vanilla (MaskedPlayerEnemy.GetMaterialStandingOn, also
        // Bundy's TryResolveFootstepSurface): raycast down, tag-match the collider against
        // StartOfRound.footstepSurfaces, keep the last known surface on a miss. The clip
        // plays locally on every client off its own footfall event, the same way the
        // foley path does, so no RPC is needed.
        private bool TryPlayVanillaFootstep(float intensity)
        {
            StartOfRound round = StartOfRound.Instance;
            FootstepSurface[] surfaces = round?.footstepSurfaces;
            if (surfaces == null || surfaces.Length == 0)
            {
                LogVanillaFootstepOnce("StartOfRound.footstepSurfaces is empty; using foley");
                return false;
            }

            AudioSource source = ResolveFootstepSource();
            if (source == null)
            {
                LogVanillaFootstepOnce("no creature AudioSource; using foley");
                return false;
            }

            var ray = new Ray(transform.position + Vector3.up, Vector3.down);
            if (Physics.Raycast(ray, out RaycastHit hit, 6f, round.walkableSurfacesMask, QueryTriggerInteraction.Ignore))
            {
                for (int i = 0; i < surfaces.Length; i++)
                {
                    if (hit.collider.CompareTag(surfaces[i].surfaceTag))
                    {
                        _vanillaFootstepSurfaceIndex = i;
                        break;
                    }
                }
            }

            int surfaceIndex = Mathf.Clamp(_vanillaFootstepSurfaceIndex, 0, surfaces.Length - 1);
            AudioClip[] clips = surfaces[surfaceIndex].clips;
            if (clips == null || clips.Length == 0)
                return false;

            // #486 verdict: a null entry used to reach PlayOneShot (silent) while this still
            // returned true, which also muted the foley fallback. Pick among non-null clips
            // only and hand back to the foley path when the surface has none.
            int clipIndex = UnityEngine.Random.Range(0, clips.Length);
            if (clipIndex == _lastVanillaFootstepClip && clips.Length > 1)
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
            {
                LogVanillaFootstepOnce($"surface '{surfaces[surfaceIndex].surfaceTag}' has no playable clips; using foley");
                return false;
            }
            _lastVanillaFootstepClip = clipIndex;

            // PlayerControllerB plays its own steps at 0.6 walking and 0.9 sprinting; #984
            // anchors the map on the driver's walk and run intensities so those two numbers
            // come out exactly, instead of the old 0.45-at-full-walk approximation.
            float volume = FootstepVolume(intensity);
            source.pitch = UnityEngine.Random.Range(0.93f, 1.07f);
            source.PlayOneShot(clip, volume);
            WalkieTalkie.TransmitOneShotAudio(source, clip, volume);
            LogVanillaFootstepOnce(
                $"surface '{surfaces[surfaceIndex].surfaceTag}' clip '{clip.name}' volume {volume:F2} " +
                $"via '{source.name}' (mixer={source.outputAudioMixerGroup?.name ?? "none"}, " +
                $"spatial={source.spatialBlend:F1}, max={source.maxDistance:F0}m)");
            return true;
        }

        private bool _vanillaFootstepLogged;

        // One line per spawn so the next verdict round says whether the vanilla path ran.
        private void LogVanillaFootstepOnce(string detail)
        {
            if (_vanillaFootstepLogged)
                return;
            _vanillaFootstepLogged = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Whistleblower vanilla footstep: {detail}.");
        }

        [ClientRpc]
        private void TriggerFlashbangClientRpc(Vector3 position)
        {
            StartCoroutine(TriggerFlashbangAfterDrop(position));
        }

        private IEnumerator TriggerFlashbangAfterDrop(Vector3 position)
        {
            yield return new WaitForSeconds(0.16f);
            if (GameNetworkManager.Instance?.localPlayerController == null)
                yield break;

            StunGrenadeItem.StunExplosion(
                position,
                affectAudio: true,
                flashSeverityMultiplier: 1f,
                enemyStunTime: 7.5f,
                flashSeverityDistanceRolloff: 1f);
            PlayLocalChime(0.72f);
        }

        [ClientRpc]
        private void ShowTipClientRpc(string title, string body, bool warning)
        {
            HUDManager.Instance?.DisplayTip(title, body, warning);
        }

        private void PlayCompleteVoice()
        {
            AudioSource source = creatureVoice != null ? creatureVoice : creatureSFX;
            if (source != null && _completeVoice != null)
            {
                source.pitch = 1f;
                source.PlayOneShot(_completeVoice, 1f);
                return;
            }

            PlayLocalChime(1.25f);
        }

        private IEnumerator LoadCompleteVoice()
        {
            string path = ResolveCompleteVoicePath();
            if (string.IsNullOrWhiteSpace(path))
                yield break;

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.WAV))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Failed loading Whistleblower voice '{path}': {request.error}");
                    yield break;
                }

                _completeVoice = DownloadHandlerAudioClip.GetContent(request);
                if (_completeVoice != null)
                    _completeVoice.name = "Whistleblower complete";
            }
        }

        private static int ResolveHealthForCurrentRisk()
        {
            WhistleblowerBalanceConfig balance = ContractsBootstrap.WhistleblowerBalance;
            return balance != null
                ? Mathf.Max(1, balance.ForRisk(MoonContractState.GetCurrentRiskTier()).Health.Value)
                : 20;
        }

        private static float FleeChanceForHit(int previousHits)
        {
            return Mathf.Clamp01(FleeBaseChance + Mathf.Max(0, previousHits) * FleeChancePerHit);
        }

        private static WhistleblowerAnimationMode ModeForState(int state)
        {
            switch (state)
            {
                case CorneredState:
                    return WhistleblowerAnimationMode.Cornered;
                case EscapeState:
                    return WhistleblowerAnimationMode.Escape;
                case LastStandState:
                case NeutralizedState:
                    return WhistleblowerAnimationMode.LastStand;
                // #963 S14 U1: clients derive the custody pose from the replicated state index,
                // like every other pose here. Decision 6: v1 is the existing Walk and NervousIdle
                // clips, no bound-hands clip and no bundle rebuild.
                //
                // #874 F02: the walk TO an escort takes the same mode as the walk WITH one. It is
                // the same man doing the same thing - a frightened civilian walking, and standing
                // in the nervous idle when he stops - and it needs no clip, no bundle rebuild and
                // no new mode of its own.
                case CustodyState:
                case RallyState:
                    return WhistleblowerAnimationMode.Custody;
                // #1158: no cower clip exists, so the cower takes Hiding - stopped, that mode IS
                // the nervous idle - rather than Cornered, whose moving branch is the boxing
                // shuffle a man who is not fighting must never mime.
                case CoweringState:
                    return WhistleblowerAnimationMode.Hiding;
                default:
                    return WhistleblowerAnimationMode.Hiding;
            }
        }

        /// <summary>
        /// #963 S14 U1: who can see him, for the scan node, the contract card and discovery.
        /// It used to be <see cref="IsLivingInteriorPlayer"/>, which requires the player to be
        /// inside the facility whatever world the witness is in. Once a Blacksite escort marches
        /// him out of the front door nobody could observe him any more, so IsObservedRecently went
        /// false and the card flipped from "Neutralize the witness" back to "Locate the witness"
        /// at the exact moment the crew could watch him being walked to the dropship. The test is
        /// now same-world, which is identical to the old one for every witness who never leaves
        /// the facility, i.e. for every round without custody. The combat and punch paths keep
        /// IsLivingInteriorPlayer: he only ever boxes indoors.
        ///
        /// #1159: the debug mob-ignore is consulted in this one predicate rather than at
        /// each of its callers - every observation passes through it, so a player the host
        /// has switched off is invisible to the scan node, the card and discovery alike.
        /// </summary>
        private bool ObservingPlayerIsEligible(PlayerControllerB player)
        {
            return player != null
                && player.isPlayerControlled
                && !player.isPlayerDead
                && player.isInsideFactory == IsInsideFacility
                && !MobIgnoreBridge.IsIgnored(player.actualClientId);
        }

        private static bool IsLivingInteriorPlayer(PlayerControllerB player)
        {
            return player != null
                && player.isPlayerControlled
                && !player.isPlayerDead
                && player.isInsideFactory;
        }

        private static PlayerControllerB FindClosestLivingInteriorPlayer(Vector3 origin, out float distance)
        {
            PlayerControllerB best = null;
            distance = float.MaxValue;
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            foreach (PlayerControllerB player in players)
            {
                if (!IsLivingInteriorPlayer(player))
                    continue;

                float candidateDistance = Vector3.Distance(origin, player.transform.position);
                if (candidateDistance >= distance)
                    continue;
                distance = candidateDistance;
                best = player;
            }

            return best;
        }

        private bool DespawnIfWrongContract()
        {
            if (!IsServer)
                return false;
            if (MoonContractState.GetCurrentContract() == MoonContractType.Whistleblower)
                return false;

            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts] Despawning Whistleblower because current contract is {MoonContractState.GetCurrentContract()}.");
            NetworkObject networkObject = NetworkObject;
            if (networkObject != null && networkObject.IsSpawned)
                networkObject.Despawn(destroy: true);
            else
                Destroy(gameObject);
            return true;
        }

        private static string ResolveCompleteVoicePath()
        {
            const string fileName = "Whistleblower complete.wav";
            string assemblyDir = Path.GetDirectoryName(typeof(ContractsBootstrap).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                Path.Combine(assemblyDir, "ContractAudio", "Whistleblower", fileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", "ContractAudio", "Whistleblower", fileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "ContractAudio", "Whistleblower", fileName),
                Path.Combine(Paths.PluginPath, "ContractAudio", "Whistleblower", fileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ContractAudio", "Whistleblower", fileName),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private void PlayLocalChime(float pitch)
        {
            AudioSource source = creatureSFX != null ? creatureSFX : creatureVoice;
            if (source == null)
                return;

            AudioClip clip = GetChimeClip();
            if (clip == null)
                return;

            source.pitch = Mathf.Clamp(pitch, 0.65f, 1.55f);
            source.PlayOneShot(clip, ChimeVolume * SfxSourceGainCompensation);
        }

        private static string FormatVector(Vector3 value)
        {
            return $"{value.x:0.0},{value.y:0.0},{value.z:0.0}";
        }

        private static AudioClip _chimeClip;
        private static AudioClip GetChimeClip()
        {
            if (_chimeClip != null)
                return _chimeClip;

            const int sampleRate = 22050;
            const float duration = 0.24f;
            int samples = Mathf.CeilToInt(sampleRate * duration);
            float[] data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)sampleRate;
                float envelope = Mathf.Clamp01(1f - i / (float)samples);
                float toneA = Mathf.Sin(2f * Mathf.PI * 660f * t);
                float toneB = Mathf.Sin(2f * Mathf.PI * 990f * t) * 0.45f;
                data[i] = (toneA + toneB) * envelope * 0.28f;
            }

            _chimeClip = AudioClip.Create("LGU Whistleblower Chime", samples, 1, sampleRate, false);
            _chimeClip.SetData(data, 0);
            return _chimeClip;
        }
    }
}
