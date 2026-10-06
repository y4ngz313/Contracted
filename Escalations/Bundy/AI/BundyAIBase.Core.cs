using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;
using Y4NGZCompany.Facility.Elevators;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.Bundy.AI;

public abstract partial class BundyAIBase : EnemyAI
{
    internal const int BehaviourStateIdle = 0;
    internal const int BehaviourStateTracking = 1;

    private enum PursuitMode
    {
        NonTargetInterrupt = 0,
        PrimaryStalk = 1,
    }

    private enum StrangerPerceptionState
    {
        Hunt = 0,
        Track = 1,
        Search = 2,
        Ping = 3,
    }

    // A contact carries two independent facts: that he should commit to a pursuit, and where the target
    // is right now. Direct sight supplies both. A noise supplies only the first -- it is a direction and
    // an age, not a live position. Before D5 both rode on one clock (SightMemoryGraceSeconds), so the
    // only lever available to stop a single footstep from granting live tracking was to keep that clock
    // short. At 2.5s it is shorter than the 2.45m he can walk in it at ChainsawWalkSpeed, which is why a
    // noise contact could never convert into sustained pursuit: HUNT/TRACK flapping was arithmetic, not
    // circumstance. Quality lets the commitment outlive the knowledge without inflating it.
    private enum StrangerContactQuality
    {
        // Sight, or a hand already on the target. The live position is trustworthy.
        Live = 0,
        // Heard only. Good enough to commit and run at, never good enough to track with.
        Inferred = 1,
    }

    // #288: what the CURRENT action forbids a hit reaction from doing. Replicated to every client
    // (see _reactionSuppression) because the reactions themselves run on every client.
    [Flags]
    private enum ReactionSuppression
    {
        None = 0,
        // A paired finisher / paired basic attack: the pose is choreographed against a locked
        // partner, so neither a stagger nor an additive bone offset may touch it. The victim is
        // watching it from a first-person paired camera driven by a playable graph that evaluates
        // BEFORE LateUpdate, so a flinch offset applied there cannot be corrected — it just breaks.
        PairedAction = 1 << 0,
        // #208 sprint arrival swing: tier 2 only. Emptying the bar still puts him down.
        SprintArrivalAttack = 1 << 1,
    }

    private enum ActionKind
    {
        None,
        Attack,
        Finisher,
        PullStart,
        Stagger,
        Contextual,
        Emote,
    }

    private enum AttackPhase
    {
        None,
        Windup,
        Active,
        Recovery,
    }

    private enum CombatBehavior
    {
        DirectChase,
        Intercept,
        CircleLeft,
        CircleRight,
        Backstep,
        Reorient,
    }

    private const float AttackImpactFallbackSeconds = 3.5f;
    private const float AttackRecoverFallbackSeconds = 5.25f;
    private const float FinisherGrabFallbackSeconds = 1.5f;
    private const float FinisherImpactFallbackSeconds = 10f;
    private const float FinisherEndFallbackSeconds = 14f;
    private const float PullStartFallbackSeconds = 3.25f;
    private const float ActionApproachStopDistance = 1.15f;
    private const float ActionApproachSpeedMultiplier = 0.75f;
    private const float ActionTurnSpeedDegrees = 720f;
    private const float ManualFacingCloseRange = 12f;
    private const float DefaultAgentAngularSpeedDegrees = 720f;
    private const float DefaultAgentAcceleration = 18f;
    private const float PressUpThrowDefaultVictimForwardOffset = 0f;
    private const float PairedFinisherRootBackoff = 0.35f;
    private const float PairedFinisherMaxRootSnapDistance = 4f;
    private const float PairedFinisherKillLeadSeconds = 0.25f;
    private const float PairedFinisherRecoverTailSeconds = 0.18f;
    private const float PairedRootCorrectionDeadZone = 0.025f;
    // B3 maneuver shaping. The backstep leash used to be 1.55x the engage range, which parked a point
    // most corridors could not hold; 1.2x is roughly a body length shorter and stays inside the band
    // the arc maneuvers also work in.
    private const float CombatManeuverBackstepLeashScale = 1.2f;
    private const float CombatManeuverMinBackstepMeters = 1f;
    private const float CombatManeuverBackstepClearanceMargin = 0.75f;
    private const float PairedRootCorrectionMaxStep = 0.85f;
    private const float PairedRootCorrectionMaxDistance = 6.25f;
    private const float PairedRootCorrectionLogInterval = 0.75f;
    private const float PairedGroundProbeHeight = 2.5f;
    private const float PairedGroundProbeDistance = 7f;
    private const float PairedPresentationGroundClearance = 0.03f;
    private const float PairedPresentationMaxLift = 2.35f;
    private const float PairedPresentationLogInterval = 0.5f;
    private const float KnockbackGuardSeconds = 0.7f;
    private const float KnockbackGuardSphereRadius = 0.34f;
    private const float KnockbackGuardWallPadding = 0.18f;
    // C3.6: the old hardcoded DialogueIntervalSeconds (20s) is gone — search chatter cadence is
    // state-scaled + jittered, config-backed (SearchChatterActive/IdleIntervalSeconds), computed
    // by NextSearchChatterIntervalSeconds in Audio.cs.
    private const float DialogueAfterEventDelaySeconds = 1f;
    private const float FootstepAudioMinInterval = 0.18f;
    private const float VoiceAudioDedupSeconds = 1.25f;
    // C3.2: attack lines raised 6 -> 11s (Lawson wants FEWER combat lines); the vocal itself is
    // also delayed past attack begin so it lands after the windup is visibly underway. Hurt
    // responds to most hits but not to every tick of a rapid-fire weapon.
    private const float AttackVocalDedupSeconds = 11f;
    private const float AttackVocalWindupDelaySeconds = 0.45f;
    private const float HurtVocalDedupSeconds = 2.5f;
    private const float ShortAudioDedupSeconds = 0.08f;
    // C4.1: collapses same-beat rev double-fires (HUNT acquire + pull start) into one bark while
    // leaving per-swing revs (2s attack cooldown) untouched.
    private const float ChainsawRevDedupSeconds = 0.5f;
    private const float NavigationObstacleProbeHeight = 0.75f;
    private const float NavigationObstacleDetourForwardStep = 1.5f;
    private const float NavigationObstacleMaxSampleDistance = 5f;
    private const float NavigationDirectSampleDistance = 3.25f;
    private const float NavigationTargetApproachMaxRadius = 12f;
    private const float NavigationTargetApproachSampleDistance = 4f;
    private const float NavigationTargetApproachMinProgress = 1.25f;
    private const float NavigationAgentMaxInteriorRadius = 0.32f;
    private const int NavigationAgentAvoidancePriority = 35;
    private const float NavigationNodeRouteSampleDistance = 8f;
    private const float MovementStuckCheckSeconds = 1.5f;
    private const float MovementStuckMinProgress = 0.18f;
    private const float MovementStuckRecoveryCooldownSeconds = 1.25f;
    private const float MovementStuckWarpAfterSeconds = 3.25f;
    // #306: displacement alone cannot see a wall-slide -- an agent grinding along a prop crosses
    // MovementStuckMinProgress every second while going nowhere. A steering demand that produces no
    // actual velocity is the direct signal, and it is only trusted once it has held for
    // MovementVelocityStallSeconds so ordinary RVO yields and stop-distance arrivals do not read as one.
    private const float MovementVelocityStallDesiredSpeed = 0.5f;
    private const float MovementVelocityStallActualSpeed = 0.15f;
    private const float MovementVelocityStallSeconds = 0.5f;
    // A confirmed velocity stall is already 0.5s of evidence, so it does not have to wait out the full
    // displacement window before the cheap local tier is allowed to try.
    private const float MovementVelocityStallCheckSeconds = 0.75f;
    // #306: the local sidestep tier that runs before the AI-node sweep. Short arc along the wall he is
    // grinding on; the hold is only long enough for the agent to commit to it before pursuit resumes.
    private const float MovementSidestepMinDistance = 1f;
    private const float MovementSidestepMaxDistance = 1.5f;
    private const int MovementSidestepSampleCount = 6;
    private const float MovementSidestepHoldSeconds = 0.4f;
    private const float MovementSidestepCooldownSeconds = 2.5f;
    private const float MovementSidestepSampleRadius = 0.6f;
    // How long a recorded clamp-hit normal still describes the surface he is grinding on.
    private const float MovementClampHitMemorySeconds = 1.5f;
    private const float MovementRecoveryActivePlayerRange = 65f;
    private const float MovementRecoveryFarPlayerCooldownSeconds = 8f;
    private const float AttackGateDiagnosticIntervalSeconds = 1f;
    private const float NavigationCombatOverlayMaxPathDistance = 8.5f;
    private const float NavigationSearchRestartDistance = 6f;
    private const float NavigationCompletePathCacheNearSeconds = 0.35f;
    private const float NavigationCompletePathCacheFarSeconds = 1.25f;
    private const float NavigationFallbackSuccessCacheSeconds = 0.75f;
    private const float NavigationFallbackFailureCacheSeconds = 1.5f;
    private const float NavigationPathCacheTargetMoveTolerance = 1.25f;
    private const float NavigationPathCacheSelfMoveTolerance = 2.5f;
    private const float PursuitSearchWidth = 55f;
    private const float PursuitSearchPrecision = 5f;
    private const float PursuitSearchFailureCooldownSeconds = 4f;
    private const float NavigationNodeRouteMaxTargetRegression = 8f;
    private const float StrangerPlayerNoiseMinLoudness = 0.55f;
    private const float StrangerNoisePlayerMatchRadius = 3f;
    private const float StrangerDestinationReachedPadding = 0.75f;
    // Obstacle detours are LOCAL avoidance. Past this range a blocked straight line just means the
    // route bends, which is NavMesh pathfinding's job, not ours.
    private const float NavigationObstacleLocalDetourRange = 25f;
    private const float NavigationObstacleDetourMinProgress = 2f;
    // TRACK watchdog: it used to exit only on arrival, so an unreachable last-known pinned him
    // there forever with no ping loop feeding him new information.
    private const float StrangerTrackNoProgressSeconds = 12f;
    private const float StrangerTrackProgressEpsilon = 1.5f;
    private const float StrangerTrackMaxSeconds = 45f;
    // #289: how long a path<->straight-line metric switch must persist before it is allowed to
    // re-baseline the no-progress clock. WALL CLOCK, not a tick count: three AI ticks is ~0.6s, which
    // fits entirely inside one NavigationCompletePathCache window (~1.5s), so a tick-count guard could
    // be satisfied by a single real path evaluation and cache-granularity flapping would still reset
    // the 12s watchdog indefinitely. 2s is comfortably longer than that cache window, so a switch has
    // to survive at least one genuine re-evaluation to count.
    private const float StrangerTrackMetricSwitchHoldSeconds = 2f;
    // Far-sprint: re-evaluated on a short cadence (it costs one LOS raycast per living player) and
    // released with hysteresis so the gait cannot flicker on the distance boundary.
    private const float FarSprintEvaluationIntervalSeconds = 0.4f;
    private const float FarSprintDistanceHysteresisMeters = 12f;
    private const float FarSprintObserverConeDegrees = 68f;
    private const float StrangerShipSiegeLogIntervalSeconds = 5f;
    // Job 3a, the never-freeze siege fallback. Resolving the anchor costs ~130 NavMesh path
    // calculations (TryFindReachableNavPointAround), so it is cached until the sheltered target
    // actually relocates inside the ship; past the revalidation window a single CalculatePath
    // confirms the cached anchor rather than redoing the sweep.
    private const float ShipSiegeAnchorRevalidateSeconds = 4f;
    private const float ShipSiegeAnchorTargetMoveTolerance = 3f;
    private const float ShipSiegeOrbitFlipSeconds = 4.5f;
    private const float ShipSiegeOrbitStepDegrees = 55f;
    private const float ShipSiegeArrivePaddingMeters = 1.25f;
    private const float ShipSiegePoundRecoverSeconds = 0.55f;
    private const float ShipSiegePoundApproachTimeoutSeconds = 6f;
    private const float ShipSiegeProwlLogIntervalSeconds = 3f;
    // How long the fallback may go untouched before it is considered exited. It only runs from
    // DoAIInterval's siege branch, and several earlier branches (NonTargetInterrupt, an unusable
    // snapshot target) return before reaching it, so the exit cannot be driven from that branch alone.
    private const float ShipSiegeFallbackStaleSeconds = 0.75f;
    private const float StrangerBoundaryRouteLogIntervalSeconds = 1.5f;
    private const float AssignedStrangerEntranceUseDistance = 3.5f;
    private const float AssignedStrangerEntranceRouteSampleDistance = 8f;
    private const float AssignedStrangerEntranceApproachSearchRadius = 28f;
    private const float AssignedStrangerEntranceForceUseDistance = 5.5f;
    private const float AssignedStrangerEntranceReachedUseDistance = 9f;
    private const float AssignedStrangerEntranceReachedDestinationDistance = 3f;
    private const float AssignedStrangerEntranceStalledUseDistance = 30f;
    private const float AssignedStrangerEntranceStalledVelocity = 0.2f;
    private const float SmartEntranceThresholdPauseSeconds = 0.7f;
    private const float SmartEntranceThresholdFallbackSeconds = 2.5f;
    private const float SmartEntranceThresholdCloseDistance = 3f;
    private const float SmartEntranceThresholdApproachRefreshSeconds = 0.25f;
    private const float SmartEntrancePostTeleportSettleSeconds = 0.75f;
    // #317 guaranteed-entry fallback. The primary router rejects an entrance unless a COMPLETE path to
    // a ring-sampled approach point exists; on Etern/Greenhouse every candidate failed that for 166s and
    // he never entered. This tier drops both requirements -- a partial path still walks him closer, and
    // the entrance point itself is the destination -- so the non-main penalty is a strict ordering
    // device (main entrance first, then nearest), not a distance in metres.
    private const float BlockedEntranceFallbackNonMainPenalty = 10000f;
    private const float BlockedEntranceFallbackReissueSeconds = 1f;
    // Retry cadence after the threshold flow reached the door but the crossing could not fire (a
    // lockdown gate in the way). He waits it out at the door instead of freezing or bypassing content.
    private const float BlockedEntranceFallbackRetrySeconds = 3f;
    private const float BlockedEntranceFallbackLogIntervalSeconds = 15f;
    // #317 boundary-entry failsafe (Lawson, 2026-08-06): the pursuit's core promise is "he always finds
    // you", and 4 of 5 field runs broke it with him standing outside for minutes. If he has made no
    // progress toward a same-side entrance for this long while his target is across the boundary, he
    // teleports in through a fire escape. 30s is Lawson's number. Progress is metres of closing on the
    // NEAREST entrance, not on the player -- inside PING his player estimate is stale by design, but
    // "getting to a door" is measurable in every perception state.
    private const float BoundaryEntryFailsafeSeconds = 30f;
    private const float BoundaryEntryFailsafeProgressMeters = 2f;
    // Fairness retry: a deferred teleport (a player is looking at him or at the arrival point) re-tests
    // on this cadence rather than per tick. The observer cone matches the far-sprint observer test.
    private const float BoundaryEntryFailsafeRetrySeconds = 2f;
    private const float BoundaryEntryFailsafeObserverConeDegrees = 70f;
    // At the door the threshold/crossing flow owns entry. Teleporting from the doorstep would bypass a
    // deliberately sealed gate (containment lockdown), so inside this range the failsafe stands down.
    private const float BoundaryEntryFailsafeMinEntranceDistance = 6f;
    private const float VanillaDoorOpenSpeedMultiplier = 1.5f;
    private const float OutsideStateAINodeConfidenceMargin = 6f;
    private const float OutsideStateAINodeMaxDistance = 85f;
    // The boundary-link decision only changes when he or the destination crosses sides, so it is
    // re-derived on movement rather than per smart request (the inference walks every AI node).
    private const float BoundarySmartLinkCacheSeconds = 1f;
    private const float BoundarySmartLinkCacheMoveTolerance = 6f;
    private const float BatterySabotageDiagnosticIntervalSeconds = 5f;
    private const float AnimatorStateDiagnosticIntervalSeconds = 5f;
    private const float ObstacleDetourDiagnosticIntervalSeconds = 2f;
    private const float BlockedTargetTauntCooldownSeconds = 8f;
    private const float BlockedTargetHideCooldownSeconds = 3.5f;
    private const float BlockedTargetLogIntervalSeconds = 2f;
    private const float PunchAudioVolume = 0.95f;
    // 2026-07-22 round 2: vocals at -16 dB mean played at 1.0 through a flat rolloff read as
    // "too loud / fake"; footsteps at -28.8 dB files x 0.65 were inaudible. Both are now
    // config-backed (Audio/BundyVoiceVolume, Audio/BundyFootstepVolume) for live dialing.
    private float FootstepAudioVolume => BundyPlugin.ModConfig?.BundyFootstepVolume?.Value ?? 0.9f;
    private float VoiceAudioVolume => BundyPlugin.ModConfig?.BundyVoiceVolume?.Value ?? 0.75f;
    // #212: ONE answer for how far a footstep propagates as AI/mechanic noise. Two playback paths
    // disagreed — the vanilla-clip RPC hardcoded (8 m, 0.4) while PlayBundyAudio's generic table
    // pinned footsteps at (8 m, 0.35) — for the same event. 0.4 is kept because the vanilla path is
    // the default one and is therefore what has actually been shipping.
    private const float FootstepNoiseRange = 8f;
    private const float FootstepNoiseLoudness = 0.4f;
    // #213. The sprint and the taunt should announce themselves from further away. Two dials, because
    // they are two different things: this one is what OTHER MONSTERS and the radar react to, and
    // HeavyGaitFootstepRangeScale below is what a player hears. Raising this one alone produces no
    // audible change while making the map's other enemies notice him, so it is deliberately the more
    // conservative of the pair and can be zeroed back to 1.0 if it makes rounds chaotic.
    private static float HeavyGaitFootstepNoiseScale =>
        Mathf.Clamp(BundyPlugin.ModConfig?.BundyHeavyGaitFootstepNoiseScale?.Value ?? 1.75f, 1f, 4f);
    // #213, the audible half. maxDistance rather than volume: AudioSource.volume is hard-clamped to 1
    // and BundyFootstepVolume already sits at 0.9, so a gain would spend almost all of itself against
    // that ceiling — the same wall BundyTauntStompVolumeScale hit. The footstep rolloff is a Custom
    // curve evaluated over d/maxDistance, so widening the range lifts the WHOLE shape instead of just
    // moving the silence line.
    private static float HeavyGaitFootstepRangeScale =>
        Mathf.Clamp(BundyPlugin.ModConfig?.BundyHeavyGaitFootstepRangeScale?.Value ?? 1.5f, 1f, 3f);
    // #210. The blood take is layered UNDER the impact, never level with it — Lawson, 2026-08-01:
    // "Make sure the blood sounds aren't too loud." The gap is measured from each impact clip's own
    // peak (BundyAudioCatalog), so this is only the "how long after the peak" part.
    private static bool BloodLayerEnabled => BundyPlugin.ModConfig?.BundyBloodLayer?.Value ?? true;
    private static float BloodLayerVolumeScale =>
        Mathf.Clamp01(BundyPlugin.ModConfig?.BundyBloodLayerVolume?.Value ?? 0.45f);
    private static float BloodLayerGapSeconds =>
        Mathf.Clamp(BundyPlugin.ModConfig?.BundyBloodLayerGapSeconds?.Value ?? 0.06f, 0f, 0.5f);
    // Humans never say a line at pitch 1.000 twice; +/-3% per voiceline (NOT per frame).
    private const float VoicePitchJitter = 0.03f;
    private const float ChainsawAudioVolume = 1f;
    private const string RuntimeChainsawPropName = "BundyProp_Chainsaw";
    private const float ChainsawWallImpactProbeRadius = 0.38f;
    private const float ChainsawWallImpactProbeDistance = 1.25f;
    private const float ChainsawWallImpactMaxVerticalNormal = 0.62f;
    private const float ChainsawWallImpactMinFacingDot = 0.15f;
    private const float ChainsawWallImpactCooldownSeconds = 0.45f;
    private const float ChainsawWallImpactStuckPauseSeconds = 0.38f;
    private const float ChainsawWallImpactAnimatorSpeed = 0.08f;
    private const int ChainsawWallImpactDustBurstCount = 18;
    private const int ChainsawMetalSparkBurstCount = 26;
    // Saw stall cycle (2026-07-22): wall hit -> STALLED (idle loop, no attacks) -> dwell -> PullStart -> RUNNING.
    private const float SawStallDwellSeconds = 0.8f;
    // Swing-feel modulation on the loop source: pitch/volume track the saw prop's world speed.
    // Round 2 (2026-07-22): first-pass values (0.03/1.12/0.04/0.15, 0.1s smoothing) were
    // inaudible in-game — roughly doubled, and an attack-begin "swell" pulse (ClientRpc) revs
    // the loop into every swing so attacks read even when prop speed alone lags.
    private const float LoopModulationSmoothingSeconds = 0.07f;
    private const float LoopModulationPitchPerSpeed = 0.06f;
    private const float LoopModulationMaxPitch = 1.3f;
    private const float LoopModulationVolumePerSpeed = 0.06f;
    private const float LoopModulationMaxVolumeBoost = 0.3f;
    private const float LoopSwellPitchBoost = 0.16f;
    private const float LoopSwellVolumeBoost = 0.25f;
    private const float LoopSwellLerpSeconds = 0.12f;
    private const float LoopModulationAbsoluteMaxPitch = 1.4f;
    // Organic engine irregularity: slow Perlin pitch wobble so the loop never sounds like a
    // mathematically perfect digital loop ("floating recording" tell).
    private const float LoopWobbleDepth = 0.018f;
    private const float LoopWobbleRate = 1.1f;
    // C2.2 (2026-07-30) inverted the CONTACT duck — PlayerHit/WallImpact now extend the swell
    // window instead of ducking, so contact is the loudest moment of the swing rather than a dip.
    // LoopDuckLerpSeconds still shapes the (speech-only) duck envelope.
    private const float LoopDuckLerpSeconds = 0.05f;
    // C2.2: how long a landed hit / wall impact holds the loop swelled past the contact moment.
    private const float ContactSwellSeconds = 0.55f;
    // Blade proximity (2026-07-31 follow-up to #187). The swell above is a WINDOW, not a distance:
    // it opens at windup and holds flat, so the swing got louder the instant the animation changed
    // — with the blade still behind him — and then stopped changing. These shape a 0-1 "how close
    // is the cutting end to this listener" term that the swell rides instead.
    // Full at the modeled contact reach, zero at the near layer's own band edge; squared so the
    // whole effect lives in the final approach rather than smearing across the room.
    private const float BladeProximityFullMeters = 1.2f;
    private const float BladeProximityZeroMeters = 7f;
    // Deliberately much faster than LoopModulationSmoothingSeconds: this has to resolve inside a
    // ~0.35s swing arc, and smoothing it like the speed term would put the peak after the hit.
    private const float BladeProximitySmoothingSeconds = 0.035f;
    private const float BladeProximityPitchBoost = 0.05f;
    // #298: the carry loop's own proximity lift. Base volume is 0.65, so there is real Clamp01
    // headroom when the speed/swell boosts are quiet — exactly the frozen-Bundy point-blank case
    // where the saw previously sat at 65% with no proximity term at all. 0.4 * proximity takes a
    // stationary point-blank carry to ~0.91 while leaving distant behaviour untouched.
    private const float BladeProximityCarryVolumeBoost = 0.4f;
    // What the swell is worth when the blade is far from you but the swing window is open — a
    // distant swing must still read as a swing, it just must not read as a swing at your face.
    private const float SwingSwellProximityFloor = 0.3f;
    // Taunt (2026-07-31): the loop is speed-driven and the taunt is performed standing still.
    // A slow idle oscillation keeps the motor alive, and each detected stomp adds a rev spike.
    private const float TauntLoopIdleOscillationHz = 0.55f;
    private const float TauntLoopIdleOscillationPitch = 0.05f;
    private const float TauntLoopIdleOscillationVolume = 0.07f;
    private const float TauntLoopStompBlipSeconds = 0.42f;
    private const float TauntLoopStompBlipPitch = 0.12f;
    private const float TauntLoopStompBlipVolume = 0.16f;
    // Foot-plant detection for the taunt stomps. The plant is the frame a descending foot stops
    // descending while it is near the ground — animation-agnostic, so it stays in sync with the
    // clip instead of with a hardcoded cycle fraction the way Walk/Run do.
    private const float TauntFootDescendSpeed = 0.35f;      // m/s downward to count as a fall
    private const float TauntFootPlantMaxHeight = 0.32f;    // root-relative foot height at plant
    private const float TauntStompPitchScale = 0.88f;       // heavier than an ordinary step
    // While Bundy speaks the loop ducks for the whole line — voice over machine. C3.5 deepened it
    // (0.55 -> 0.35) so the saw genuinely yields while he talks; gated on the range-extension kill
    // switch so OFF restores the pre-C3.5 mix exactly.
    private static float LoopSpeechDuckGain => RangeExtensionEnabled ? 0.35f : 0.55f;
    // #187: a live contact swell releases the speech duck, so the blade landing is never quieter for
    // the accident of him being mid-sentence.
    private static bool ContactSwellOverridesSpeechDuck =>
        BundyPlugin.ModConfig?.ChainsawContactOverridesSpeechDuck?.Value ?? true;
    private static bool BladeProximityModulationEnabled =>
        BundyPlugin.ModConfig?.ChainsawBladeProximityModulation?.Value ?? true;
    private static float BladeProximityNearGain =>
        Mathf.Clamp(BundyPlugin.ModConfig?.ChainsawBladeProximityNearGain?.Value ?? 0.45f, 0f, 1.5f);
    private static bool TauntLoopOscillationEnabled =>
        BundyPlugin.ModConfig?.ChainsawTauntLoopOscillation?.Value ?? true;
    private static bool TauntStompFootstepsEnabled =>
        BundyPlugin.ModConfig?.BundyTauntStompFootsteps?.Value ?? true;
    private static float TauntStompVolumeScale =>
        Mathf.Clamp(BundyPlugin.ModConfig?.BundyTauntStompVolumeScale?.Value ?? 1.35f, 1f, 2f);
    private static bool VoiceDistanceReverbEnabled =>
        BundyPlugin.ModConfig?.BundyVoiceDistanceReverb?.Value ?? true;
    private static bool VoiceDistanceSpreadEnabled =>
        BundyPlugin.ModConfig?.BundyVoiceDistanceSpread?.Value ?? true;
    private static float VoiceOutdoorVolumeScale =>
        Mathf.Clamp(BundyPlugin.ModConfig?.BundyVoiceOutdoorVolumeScale?.Value ?? 1.3f, 1f, 2f);
    // Fade an interrupted voiceline out instead of hard-stopping it mid-word.
    private const float VocalInterruptFadeSeconds = 0.07f;
    private static readonly int BaseLayerIdleStateHash = Animator.StringToHash("Base Layer.Idle");
    private const float MovementWallClampProbeHeight = 0.9f;
    private const float MovementWallClampPadding = 0.18f;
    private const float MovementWallClampMaxStep = 3.5f;
    private const float MovementWallClampMinDistance = 0.04f;
    private const float MovementWallClampDiagnosticIntervalSeconds = 2f;
    // #271: holding at a closed door waits out the open animation; the cap keeps a door that refuses
    // to open (open RPC failing, remote authority) from becoming an infinite stall, and the rearm
    // keeps a failed door from re-triggering a fresh hold every time he pushes into it again.
    private const float MovementWallClampDoorHoldMaxSeconds = 1f;
    private const float MovementWallClampDoorHoldRearmSeconds = 3f;
    private const float MovementWallClampDoorHoldPauseSeconds = 0.25f;
    private static readonly RaycastHit[] ChainsawWallImpactHits = new RaycastHit[16];
    private static Material _chainsawWallDustMaterial;
    private static bool _searchedChainsawWallDustMaterial;
    private static Material _chainsawSparkMaterial;
    private static bool _searchedChainsawSparkMaterial;

    private int _lastMotionState = -1;
    private float _nextAnimatorStateDiagnosticAt;
    private Transform _animStateHandL;
    private Transform _animStateHandR;
    private Transform _animStateSawProp;
    private Renderer _animStateSawRenderer;
    private float _lastAppliedVisualYaw = float.NaN; // Stage 3: last applied visual yaw, for log-on-change.
    private float _lastAppliedVisualRoll = float.NaN; // Stage 3b: last applied visual roll, for log-on-change.
    private bool _lungeActive;                  // Stage 2: a lunging basic attack is in progress.
    private int _lungeLayerIndex = -1;          // Stage 2: cached LungeLowerBody layer index (-1 = unresolved, -2 = absent).
    private bool _lungeLayerMissingLogged;
    private float _lungeLayerWeight;            // Stage 2: current (lerped) lower-body override weight.
    private float _moveX;                        // Stage 4: smoothed local-space right velocity (MoveX).
    private float _moveY;                        // Stage 4: smoothed local-space forward velocity (MoveY).
    private Vector2 _moveDir = new Vector2(0f, 1f); // Stage 4: smoothed UNIT travel heading (rides the blend ring).
    private float _moveMag;                       // Stage 4: smoothed travel magnitude (radius into the blend ring).
    private Vector3 _lastDirectionalPos;         // Stage 4: previous-frame position for actual-velocity measurement.
    private bool _hasLastDirectionalPos;
    private Vector2 _lastNonZeroDirectionalBlend;
    private bool _hasLastNonZeroDirectionalBlend;
    private float _observedStationarySince = -1f;
    private bool _observedVelocityIdleDemotionActive;
    private int _pendingObservedVelocityMotionState = -1;
    private Animator _parameterCacheAnimator;
    private int _hasSpeedParameter = -1;
    private int _hasMoveXParameter = -1;
    private int _hasMoveYParameter = -1;
    private const string LungeLowerBodyLayer = "LungeLowerBody"; // must match BundyAssetBuilder layer name.
    private bool _finisherLungeActive;          // Stage 2 Part B: running in for a finisher (full-body run, no swing).
    private float _finisherLungeUntil;          // Stage 2 Part B: finisher-lunge timeout.
    private const float FinisherLungeTimeoutSeconds = 2.25f;
    private Vector3 _lungeTargetPoint;          // Player position captured at basic-lunge START (committed, no re-tracking).
    private Vector3 _lungeAttackDirection;      // Planar facing captured at lunge start; keeps attack root from yawing with NavMesh drift.
    private bool _hasLungeAttackDirection;
    private float _nextLungeFacingLogAt;
    private float _nextLungeDriveLogAt;         // Job 2a: throttle for the BundyLungeDrive diagnostic.
    private float _lastLungeTimedSpeed = -1f;   // R1: speed the swing clock asked for, last lunge frame.
    private float _lastLungeStoppableSpeed = -1f; // R1: speed still brakeable inside the remaining gap.
    private float _lastLateralManeuverAt = -999f; // R2: last frame a circle/backstep drove the agent.
    private bool _nextCombatCircleLeft;          // B2: alternates circle side per commit (deterministic variation).
    private bool LungeAimActive => _lungeActive; // any committed lunge (basic OR finisher) holds its aim, no re-tracking.
    // Stagger reaction (StaggerType param order from BundyControllerBuilder Chn_Staggers).
    private const int StaggerSmall01Type = 0;
    private const int StaggerSmall02Type = 1;
    private const int StaggerLargeType = 2;
    private const int StaggerLargeFromBehindType = 3;
    // #318: minimum gap between two accepted slams from the SAME spike trap. Comfortably longer than
    // one slam-and-retract cycle, so the per-physics-frame OnTriggerStay storm collapses to one hit.
    private const float SpikeTrapSlamDedupSeconds = 1.5f;
    private const string RuntimeBodyHitboxName = "BundyBodyHitbox";
    private const int EnemyHitboxLayer = 19;
    private const float RuntimeBodyHitboxRadius = 0.58f;
    private const float RuntimeBodyHitboxHeight = 3.45f;
    private static readonly Vector3 RuntimeBodyHitboxCenter = new Vector3(0f, 1.78f, 0f);
    private const int TauntEmoteIndex = 0;              // Taunt_Stomp = EmoteIndex 0 on the chainsaw rig.
    private float _nextStaggerAllowedAt;
    private int _lastSmallStaggerType = -1;
    // #288: the stagger bar replaced the old rolling damage accumulator. Seeded lazily (NaN = "not
    // yet initialized") because StaggerBarMax is config-driven and the config may not be bound when
    // the field initializer runs. Simulated identically on every client.
    private float _staggerBar = float.NaN;
    private float _lastStaggerBarDamageAt = float.NegativeInfinity;
    private bool _sprintArrivalAttackActive;   // #288/#208: this swing was converted from a sprint arrival.
    // #288: the reaction-suppression mirror. _actionKind / _activeBasicVictimPaired /
    // _sprintArrivalAttackActive are OWNER-ONLY (written behind IsOwner gates, permanently
    // None/false on remotes), but hit reactions are dispatched on EVERY client — so a guard reading
    // them directly suppresses on the owner and nowhere else. The owner republishes this mask
    // whenever it changes and the server fans it out; every client's guards read the mirror.
    private ReactionSuppression _reactionSuppression;
    private bool _reactionSuppressionPublished;
    // #318: SpikeRoofTrap.OnTriggerStay refires every physics frame for as long as a hitbox overlaps
    // the spikes, so one slam is many calls. Owner-only bookkeeping (only the owner reaches the
    // entry point) — the trap id is tracked separately so two DIFFERENT traps can never dedup each
    // other, which a bare timestamp would do in a corridor with a pair of them.
    private float _lastSpikeTrapSlamAt = float.NegativeInfinity;
    private int _lastSpikeTrapSlamTrapId;
    private bool _staggerKnockdownActive;
    private bool _staggerKnockdownStateSeen;
    private float _staggerKnockdownStartedAt;
    private bool _staggerAnimatorSlowdownActive;
    private bool _staggerAgentStopApplied;
    private static readonly int StaggerLargeStateHash = Animator.StringToHash("Stagger_Large");
    private static readonly int StaggerLargeFromBehindStateHash = Animator.StringToHash("Stagger_LargeFromBehind");
    private bool _hasActedOnce;                          // taunt after the FIRST attack (once per life).
    private bool _tauntPending;                          // a taunt is queued (first attack / kill).
    private bool _warnedMissingAgent;
    private bool _warnedAgentNotOnNavMesh;
    private bool _warnedMissingAnimator;
    private bool _loggedRuntimeDiagnostics;
    private float _dirLocoLogNextAt; // throttle: directional locomotion instrumentation.
    private AudioSource _footstepAudioSource;

    private PursuitMode _pursuitMode = PursuitMode.PrimaryStalk;
    private PlayerControllerB _primaryTarget;
    private PlayerControllerB _pursuitZeroTarget;
    private float _pursuitZeroAttackAttentionUntil = float.NegativeInfinity;
    private bool _strangerTargetingActive;
    private PlayerControllerB _strangerTarget;
    private Vector3 _strangerLastKnownPosition;
    private float _strangerLastKnownAt = float.NegativeInfinity;
    private bool _hasStrangerLastKnownPosition;

    // #307. A third tier between "I can see you" and "I have no idea": _strangerRecentMemoryUntil is
    // how long he keeps PURSUING an estimate, and _strangerLastKnownVelocity is the heading he saw the
    // target leave on, so that estimate can be extrapolated instead of being a pin in the floor.
    // Deliberately separate from _strangerLiveTargetUntil: only steering and search seeding read these,
    // never attack eligibility -- swinging at an extrapolated ghost is exactly the failure the split
    // exists to prevent. The velocity is written only from sight-quality contacts; an inferred contact
    // is jittered by construction, so differencing two of them measures the jitter, not the player.
    private Vector3 _strangerLastKnownVelocity;
    private float _strangerRecentMemoryUntil = float.NegativeInfinity;
    // Prediction cache. The destination resolver is read several times per tick and the NavMesh sample
    // is the expensive half; invalidated (not merely aged out) whenever the estimate itself changes.
    private Vector3 _strangerPredictedPursuitPosition;
    private float _strangerPredictedPursuitAt = float.NegativeInfinity;
    private float _nextStrangerSightRefreshAt = float.NegativeInfinity;
    private bool _strangerSearchRoamBiasPending;

    // Knowledge and destination used to share _strangerLastKnownPosition, and the field quietly meant
    // two things: "where I last knew the target to be" in HUNT/TRACK, and "the roam point I am walking
    // to" in SEARCH. That is survivable for as long as every destination is still a guess at the
    // target's position. It stops being survivable the moment a destination is deliberately NOT where
    // he thinks you are -- 3c's intercept point is chosen precisely because you are somewhere else --
    // because TryReleaseStrangerHuntHold and TRACK's arrival test both read "standing on the
    // last-known" as "arrived where the target was", and a doorway would satisfy both falsely.
    //
    // So the split: _strangerLastKnownPosition is KNOWLEDGE, written only by AcquireStrangerContact
    // and IssueStrangerPing. The override below is WHERE HE IS GOING when that differs from his
    // knowledge. Acquiring knowledge clears it, because knowledge always outranks a guess-destination.
    private Vector3 _strangerDestinationOverride;
    private bool _hasStrangerDestinationOverride;
    private string _strangerDestinationOverrideKind = "none";
    private float _strangerExitLurkCommittedUntil = float.NegativeInfinity;
    private float _strangerExitLurkAllowedAt = float.NegativeInfinity;
    private int _strangerExitLurkEntranceId = -1;
    private float _strangerLiveTargetUntil = float.NegativeInfinity;
    private readonly float[] _worldNoiseNextAcceptAt = new float[BundyWorldNoiseCatalog.MeaningCount];
    private float _strangerHuntHoldUntil = float.NegativeInfinity;
    private float _strangerHuntStartedAt = float.NegativeInfinity;
    private StrangerContactQuality _strangerHuntContactQuality = StrangerContactQuality.Live;
    private StrangerPerceptionState _strangerPerceptionState = StrangerPerceptionState.Search;
    private bool _strangerPerceptionInitialized;
    private Vector3 _strangerSearchCenter;
    private float _strangerSearchStartedAt = float.NegativeInfinity;
    private float _strangerTrackStartedAt = float.NegativeInfinity;
    private float _strangerTrackBestDistance = float.PositiveInfinity;
    private float _strangerTrackBestDistanceAt = float.NegativeInfinity;
    // #289 V1: which metric _strangerTrackBestDistance was measured with. Remaining PATH distance and
    // straight-line distance are not comparable (path >= euclidean, sometimes by a lot), so a route
    // going complete/incomplete under him must re-baseline instead of reading as a jump either way --
    // but only after the switch has held for StrangerTrackMetricSwitchHoldSeconds, or a flapping route
    // would reset the no-progress clock forever.
    private bool _strangerTrackBestFromPath;
    private float _strangerTrackMetricSwitchSince = float.NegativeInfinity;
    // #289: observed seconds of elevator ride excluded from StrangerTrackMaxSeconds / SearchWindowSeconds.
    private float _strangerTrackElevatorHeldSeconds;
    private float _strangerSearchElevatorHeldSeconds;
    private float _elevatorSuspensionTickAt = float.NegativeInfinity;
    private float _nextFarSprintEvaluationAt = float.NegativeInfinity;
    private bool _farSprintActive;
    private bool _loggedFarSprintActive;
    private int _strangerSearchRoamPointsRemaining;
    private float _searchRoamPathlessSeconds;
    private float _searchRoamWatchdogTickAt = float.NegativeInfinity;
    private float _nextStrangerPingAt = float.PositiveInfinity;
    private float _strangerEscalationElapsedSeconds;
    private float _nextStrangerShipSiegeLogAt;
    private float _nextStrangerBoundaryRouteLogAt;
    private float _nextAssignedStrangerEntranceLogAt;
    // #317: why the last FRESH crossing-route resolve rejected every entrance. Counters rather than a
    // string because the resolve runs every AI interval and only the throttled stall warning ever
    // formats them.
    private int _entranceRouteCandidates;
    private int _entranceRouteDirectionMatched;
    private int _entranceRouteNoExitScript;
    private int _entranceRouteNoApproach;
    private int _entranceRouteNoCompletePath;
    private bool _entranceRouteResolveRan;
    // #317 guaranteed-entry fallback state. Owned solely by HandleUnrecoverableBlockedPursuit; the
    // primary router clears it the moment it routes again or the boundary condition ends.
    private bool _blockedEntranceFallbackActive;
    private EntranceTeleport _blockedEntranceFallbackEntrance;
    private float _blockedEntranceFallbackEngagedAt;
    private float _nextBlockedEntranceFallbackIssueAt;
    private float _nextBlockedEntranceFallbackRetryAt;
    private float _nextBlockedEntranceFallbackLogAt;
    // Reused scratch path for the fallback's direct CalculatePath/SetPath issue (allocating a
    // NavMeshPath per reissue tick would churn).
    private NavMeshPath _blockedEntranceFallbackPath;
    // #317 boundary-entry failsafe state. Progress is tracked as the best (smallest) planar distance to
    // the nearest same-direction entrance while the target is across the boundary; the clock re-anchors
    // on every BoundaryEntryFailsafeProgressMeters of improvement and clears with the condition.
    private float _boundaryEntryBestEntranceDistance = float.PositiveInfinity;
    private float _boundaryEntryLastProgressAt = float.NegativeInfinity;
    private float _nextBoundaryEntryFailsafeRetryAt;
    private float _nextBoundaryEntryFailsafeLogAt;
    private bool _batterySabotageRouteActive;
    private Vector3 _batterySabotageDestination;
    private bool _batterySiegeSwingPoseActive;
    private int _lastBatterySiegeSwingAttackIndex = -1;
    // Job 3a state.
    private bool _shipSiegeFallbackActive;
    private float _shipSiegeFallbackLastTickAt = float.NegativeInfinity;
    private bool _hasShipSiegeAnchor;
    private Vector3 _shipSiegeAnchor;
    private string _shipSiegeAnchorMode = "none";
    private Vector3 _shipSiegeAnchorTargetPosition;
    private float _nextShipSiegeAnchorRevalidateAt = float.NegativeInfinity;
    private float _nextShipSiegePoundAt = float.NegativeInfinity;
    private float _shipSiegePoundDueSince = -1f;
    private float _shipSiegePoundHoldUntil = float.NegativeInfinity;
    private int _shipSiegePoundCount;
    private int _shipSiegeOrbitDirection = 1;
    private float _nextShipSiegeOrbitFlipAt = float.NegativeInfinity;
    private float _nextShipSiegeProwlLogAt = float.NegativeInfinity;
    private BundySmartPathingBridge _smartPathing;
    private bool _lastSmartPathPending;
    private bool _lastSmartPathNoRoute;
    private BundyDoorOpener _doorOpener;
    private EnemyAICollisionDetect _runtimeBodyHitbox;
    private float _smartInteractionHoldUntil;
    private EntranceTeleport _pendingSmartEntrance;
    private Vector3 _pendingSmartEntrancePoint;
    private Vector3 _pendingSmartEntranceApproach;
    private string _pendingSmartEntranceReason;
    private float _pendingSmartEntranceStartedAt;
    private float _pendingSmartEntrancePauseUntil;
    private float _pendingSmartEntranceDeadline;
    private float _nextPendingSmartEntranceApproachAt;
    private bool _pendingSmartEntranceHoldingAtThreshold;
    private float _smartEntranceSettleUntil;
    private bool _smartEntranceSettleLogPending;

    private ActionKind _actionKind = ActionKind.None;
    // B1: what the movement watchdog names when it has to recover after an action exit.
    private ActionKind _lastActionExitKind = ActionKind.None;
    private float _lastActionExitAt = -999f;
    private PlayerControllerB _actionTarget;
    private PlayerControllerB _grabbedTarget;
    private bool _actionGrabResolved;
    private bool _actionImpactResolved;
    private bool _actionRecoverResolved;
    private float _actionGrabFallbackAt;
    private float _actionImpactFallbackAt;
    private float _actionRecoverFallbackAt;
    private float _actionStartedAt;
    private bool _dispatchingAttackImpactFallback;
    private float _nextAttackAllowedAt;
    private float _postBasicAttackWalkUntil;
    private int _lastAttackIndex = -1;
    private AttackPhase _attackPhase;
    private float _attackActiveWindowStartAt;
    private float _attackActiveWindowEndAt;
    private bool _attackHitConfirmed;
    private Vector3 _lastAttackContactPoint;
    private bool _hasLastAttackContactPoint;
    private PlayerControllerB _latchedBasicAttackContactTarget;
    private float _latchedBasicAttackContactAt = float.NegativeInfinity;
    private float _latchedBasicAttackRootDistance;
    private bool _chainsawWallImpactResolved;
    private float _lastChainsawWallImpactAt = float.NegativeInfinity;
    private bool _sawStalled;
    private float _sawStallRecoverAt;
    // C2.2: _movementLoopDuckUntil removed — contact transients extend the swell window instead
    // of ducking; only speech ducks the loop now.
    private float _movementLoopSpeechDuckUntil;
    private int _vanillaFootstepSurfaceIndex;
    private float _loopDuckGainCurrent = 1f;
    private Coroutine _vocalInterruptFadeRoutine;
    private Vector3 _loopModLastPosition;
    private bool _loopModHasLastPosition;
    private float _loopModSmoothedSpeed;
    private float _loopSwellUntil;
    private float _loopSwellCurrent;
    private bool _audioMixerRoutingApplied;
    private float _nextAudioMixerRoutingAttemptAt;
    private int _audioMixerRoutingAttempts;
    private UnityEngine.Audio.AudioMixerGroup _resolvedAudioMixerGroup;
    private AudioClip _vanillaVoiceTestClip;
    private float _loopWobbleSeed;
    private OccludeAudio _voiceOcclude;
    private OccludeAudio _sfxOcclude;
    private OccludeAudio _loopOcclude;
    private OccludeAudio _nearLoopOcclude;                 // C2.3 near-field layer occlusion
    private AudioSource _movementLoopNearAudioSource;      // C2.3 short-range blade-tip loop layer
    private Vector3 _nearLoopLocalOffset;                  // blade-tip offset in chainsaw-prop local space
    private OccludeAudio _contactOcclude;                  // #187 blade contact emitter occlusion
    // #212: footsteps were the ONE Bundy source routed without an OccludeAudio, so they had neither
    // the occlusion low-pass nor the reverb send nor any air absorption — excluded from the whole
    // distance-realism model every other Bundy sound gets.
    private OccludeAudio _footstepOcclude;
    private bool _loggedFootstepSurfaceMapping;            // #209 one-shot surface -> pool dump
    private AudioSource _chainsawContactAudioSource;       // #187 blade contact one-shot emitter
    private Vector3 _chainsawContactLocalOffset;           // its blade offset in chainsaw-prop local space
    private float _pursuitStallBestDistance = float.PositiveInfinity;   // #496 best of the CURRENT bucket
    private float _pursuitStallPreviousBestDistance = float.PositiveInfinity; // #496 best of the previous bucket
    private float _pursuitStallBucketStartedAt = float.NegativeInfinity;
    private float _pursuitStallTrendDistance = float.PositiveInfinity;  // #496 slow-closing trend anchor
    private float _pursuitStallTrendAt = float.NegativeInfinity;
    private float _pursuitStallBestAt = float.NegativeInfinity;
    private float _nextPursuitStallReportAt;
    private float _nextAirAbsorptionResolveAt;
    private float _nextAudioOcclusionCheckAt;
    private bool _audioPathOccluded;
    private AudioReverbFilter _voiceReverb;                 // owned by OccludeAudio; re-written in LateUpdate
    private BundyAudioLateTick _audioLateTick;              // the LateUpdate slot the reverb needs
    private float _bladeProximity01;                        // smoothed blade-to-listener proximity, 0-1
    // One base-layer clip sample per frame, shared by the synthetic footstep driver and the
    // taunt-aware loop modulation — both run on every client and both used to fetch it separately.
    private int _baseLayerClipFrame = -1;
    private string _baseLayerClipName = string.Empty;
    private float _baseLayerClipFraction;
    private Transform _leftFootBone;
    private Transform _rightFootBone;
    private Animator _footBoneSearchAnimator;
    private float _leftFootLastHeight;
    private float _rightFootLastHeight;
    private bool _leftFootDescending;
    private bool _rightFootDescending;
    private bool _hasFootHeightSample;
    private float _tauntLoopStompBlipAt = float.NegativeInfinity;
    private float _currentSawAudioMaxDistance;
    private float _nextAudioRangeEscalationUpdateAt;
    private Coroutine _chainsawWallStuckPauseRoutine;
    private float _nextWallImpactSuppressedLogAt;
    private bool _attackVisualExitArmed;
    private bool _attackVisualExitApplied;
    private int _attackVisualReturnStateHash = BaseLayerIdleStateHash;
    private BundyChainsawBladeHitbox _chainsawBladeHitbox;
    private BundyChainsawPropSpin _chainsawPropSpin;
    private bool _chainsawBladeWindowActive;
    private float _nextBladeAimDiagnosticAt;
    private float _nextChainsawGripDriftDiagnosticAt;
    private Vector3 _lastMovementWallClampPosition;
    private bool _hasLastMovementWallClampPosition;
    private float _nextMovementWallClampLogAt;
    private Vector3 _lastMovementClampHitNormal;
    private float _lastMovementClampHitAt = float.NegativeInfinity;
    private int _movementClampDoorHoldDoorId;
    private float _movementClampDoorHoldStartedAt = float.NegativeInfinity;
    private int _lastFinisherBodyIndex = -1;
    private int _activeAttackIndex = -1;
    private bool _activeAttackCountsAsGrab;
    private PlayerControllerB _activeBasicVictim;
    private bool _activeBasicVictimPaired;
    private PlayerControllerB _activeFinisherVictim;
    private int _activeFinisherEntryIndex = -1;
    protected int _activeFinisherBodyIndex = -1;
    private bool _activeFinisherVictimPaired;
    private bool _finisherBloodVisualStarted;
    private bool _finisherBloodVisualPlayedLocal;
    private float _finisherBloodFallbackAt = float.PositiveInfinity;
    private bool _pairedActionRootLockActive;
    private Vector3 _pairedActionRootPosition;
    private Quaternion _pairedActionRootRotation = Quaternion.identity;
    private string _activePairedActionId;
    private bool _activeChainsawFinisherUsesInteractions;
    // #321 (B3): per-client, not networked. True when THIS client's TryStartInteraction failed and
    // the legacy victim playable took over, while the rest of the lobby may still be on the
    // Interactions path. Kept separate from _activeChainsawFinisherUsesInteractions so a host-side
    // fallback cannot reroute the lobby-wide release.
    private bool _chainsawFinisherVictimFallbackLocal;
    private float _nextPairedRootCorrectionLogAt;
    private float _nextPairedSyncDebugAt;
    private int _pairedSyncDebugTick;
    private bool _pairedVisualLocalPositionSaved;
    private Vector3 _pairedVisualSavedLocalPosition;
    private float _pairedPresentationLiftY;
    private float _nextPairedPresentationLogAt;
        private float _nextMovementRecoveryLogAt;
        private float _nextAttackGateDiagnosticAt;
        private float _nextFacingProbeAt;
        private float _nextFacingProbeClientAt;
    private float _nextMovementContextAllowedAt;
    private float _nextBlockedTargetTauntAt;
    private float _nextBlockedTargetHideAt;
    private float _nextBlockedTargetLogAt;
    private PlayerControllerB _blockedPursuitTarget;
    private float _blockedPursuitStartedAt;
    private Vector3 _lastMovementProgressPosition;
    private float _lastMovementProgressAt;
    private bool _hasLastMovementProgress;
    private float _nextStuckRecoveryAt;
    private float _movementVelocityStallSince;
    private float _movementSidestepHoldUntil;
    private float _nextMovementSidestepAt;
    private readonly AISearchRoutine _pursuitSearch = new AISearchRoutine
    {
        loopSearch = true,
        searchWidth = PursuitSearchWidth,
        searchPrecision = PursuitSearchPrecision,
        randomized = true,
        onlySearchNodesInLOS = false
    };
    private Vector3 _lastKnownPursuitPosition;
    private bool _hasLastKnownPursuitPosition;
    private float _nextPursuitSearchAllowedAt;
    // #289 V2: throttles for the non-HUNT blocked-pursuit recovery (warning line + estimate re-anchor).
    private float _nextBlockedPursuitRecoveryLogAt;
    private float _nextBlockedPursuitRecoveryPingAt;
    private float _nextDialogueAt = float.PositiveInfinity;
    private float _queuedDialogueAt = float.PositiveInfinity;
    private BundyAudioCue _queuedVocalCue = BundyAudioCue.Search;
    private float _nextMaskedScanAt;
    private float _nextMaskedReactionAt;
    private BundyLipsync _lipsyncForVocals;
    private float _nextFootstepAudioAt;
    private float _nextFootstepAnimEventLogAt;
    // Synthetic footstep timing: the baked locomotion clips (R40 rebake chain, ship v02) lost
    // their OnFootstep AnimationEvents; these are the authored event times from
    // SourcePackCHN Chainsaw_WalkForward (0.12/0.70/1.28 of 1.3333s) and Chainsaw_RunForward
    // (0.08/0.42/0.76 of 0.8333s) as cycle fractions, fired from animator playback instead.
    private static readonly float[] WalkFootstepCycleFractions = { 0.090f, 0.525f, 0.960f };
    private static readonly float[] RunFootstepCycleFractions = { 0.096f, 0.504f, 0.912f };
    // Issue #213. The sprint had NO table and therefore no footsteps at all — UpdateSyntheticFootsteps
    // matches WalkForward/RunForward/Taunt by clip name and early-returns on everything else, so a
    // sprinting Bundy was silent-footed rather than mistimed.
    //
    // Derived from the clip's own authoring rather than captured or eyeballed. In
    // author_bundy_sprint_set.py, `stride_phase` defines phase 0 as THAT FOOT'S contact with the left
    // leading and the right half a cycle behind, so on a 20-frame cycle the contacts are frames 0 and
    // 10 — normalized 0.000 and 0.500. They are nudged 0.02 later because the crossing test in
    // UpdateSyntheticFootsteps is `f > prev`, which can never fire on exactly 0. At the loop's 0.667 s
    // that nudge is 13 ms of lateness, well under the ~30 ms an ear resolves against a visual.
    private static readonly float[] SprintLoopFootstepCycleFractions = { 0.020f, 0.520f };
    // The intro's contacts are NOT evenly spaced: its phase clock advances as sqrt(v) over a linear
    // speed ramp, so the two steps are not half a clip apart. Computed by replaying the authoring's
    // own speed_ramp/stride_length/phase_clock: the left foot's contact is frame 0 (a standing pose at
    // 0 m/s — no impact to sound), the right foot plants at frame 18.736 of 29 = 0.646 normalized,
    // 0.625 s in and already doing 5.01 m/s, and the left's next contact IS the end of the intro,
    // which is the loop's own frame-0 plant and fires 13 ms into the first lap instead.
    private static readonly float[] SprintIntroFootstepCycleFractions = { 0.646f };
    private int _syntheticFootstepCycleKind;
    private float _syntheticFootstepLastFrac = -1f;
    // Single-vocal channel state: only one voiceline may play at a time on the voice source.
    private int _vocalChannelPriority;
    private float _vocalChannelEndsAt;
    private bool _audioAnchorsApplied;
    private readonly int[] _lastVocalClipIndex = CreateUnsetIndexArray();
    private int _lastFootstepClipIndex = -1;
    private int _suppressedFootstepAnimEventLogs;
    private AudioSource _movementLoopAudioSource;
    private bool _movementLoopAudioRequested;
    private float _movementLoopAudioReadyUntil = float.NegativeInfinity;
    private readonly float[] _nextAudioCueAllowedAt = new float[Enum.GetValues(typeof(BundyAudioCue)).Length];
    private float _nextTurnClipAllowedAt;
    private bool _loggedTurnSuppressThisFrame;
    private float _locomotionIntentHoldUntil;
    private bool _turnClipRootLockActive;
    private float _turnClipRootLockStartedAt;
    private float _turnClipRootLockUntil;
    private int _turnClipRootLockIndex = -1;
    private Quaternion _turnClipRootStartRotation = Quaternion.identity;
    private Quaternion _turnClipRootTargetRotation = Quaternion.identity;
    private CombatBehavior _currentCombatBehavior = CombatBehavior.DirectChase;

    // [BundyAttackSeam] R39 §6 sampler (#87): event-driven seam telemetry around attack start,
    // visual/authored exit, and action cleanup. Read in Update, so poses are the previous frame's
    // fully evaluated (rendered) result — same convention as FormatSkeletonTruth.
    private Transform _attackSeamPelvis;
    private bool _attackSeamPelvisResolved;
    private float _attackSeamTailLogUntil;
    private bool _attackSeamPreExitActiveLastFrame;
    private string _attackSeamLastFacingWriter = "<none>";
    private int _attackSeamLastFacingWriterFrame = -1;
    private CombatBehavior _lastCombatBehavior = CombatBehavior.DirectChase;
    private float _nextCombatBehaviorChangeAt;
    private int _consecutiveAttackMisses;
    private Vector3 _lastTrackedTargetPosition;
    private Vector3 _trackedTargetVelocity;
    private float _lastTrackedTargetSampleAt = -1f;
    private bool _hasTrackedTargetSample;
    private float _nextCombatDebugAt;
    private float _nextBatterySabotageForcedLogAt;
    private float _nextObstacleDetourLogAt;
    private bool _hasCompletePathCache;
    private bool _cachedCompletePathSucceeded;
    private Vector3 _cachedCompletePathOrigin;
    private Vector3 _cachedCompletePathTargetPosition;
    private float _cachedCompletePathLength;
    private float _completePathCacheUntil;
    private bool _hasPursuitFallbackCache;
    private bool _cachedPursuitFallbackSucceeded;
    private Vector3 _cachedPursuitFallbackOrigin;
    private Vector3 _cachedPursuitFallbackTargetPosition;
    private Vector3 _cachedPursuitFallbackDesired;
    private Vector3 _cachedPursuitFallbackResolved;
    private string _cachedPursuitFallbackMode;
    private float _pursuitFallbackCacheUntil;
    private bool _hasBoundarySmartLinkCache;
    private bool _boundarySmartLinkCacheAllowed;
    private bool _boundarySmartLinkCacheOutside;
    private Vector3 _boundarySmartLinkCacheDestination;
    private float _boundarySmartLinkCacheUntil;
    [SerializeField] private bool _rigLabMode;

    protected virtual string VariantName => "Bundy";
    protected bool IsRigLabMode => _rigLabMode;
    protected virtual float WalkSpeed => 3.5f;
    protected virtual float RunSpeed => 5f;
    // Issue #205: the overhead sprint's ground speed. Base variants have no sprint clip set, so this
    // reduces to the run and UsesOverheadSprint keeps them out of the gait entirely.
    protected virtual float SprintSpeed => RunSpeed;
    protected virtual bool UsesOverheadSprint => false;
    protected virtual int BasicAttackDamage => 30;
    protected virtual float FinisherChance => 0.5f;
    protected virtual int FinisherBodyCount => 1;
    protected virtual bool UsesPullStart => false;
    protected virtual bool BasicActionsEnabled => true;
    protected virtual bool FinishersEnabled => false;
    protected virtual bool RequireHalfHealthForFinisher => true;
    protected virtual bool UsesForcedCriticalFinisherGate => false;
    protected virtual BundyAudioCue BasicAttackDamageAudioCue => BundyAudioCue.Punch;
    protected virtual float BasicAttackDamageAudioVolume => PunchAudioVolume;
    protected virtual BundyAudioCue FootstepAudioCue => BundyAudioCue.Footstep;
    protected virtual BundyAudioCue? FinisherDamageAudioCue => null;
    protected virtual BundyAudioCue? PullStartAudioCue => null;
    protected virtual BundyAudioCue? MovementLoopAudioCue => null;
    // C2.1 (2026-07-30): the loop base volume is config-backed at 0.65, NOT ChainsawAudioVolume (1.0).
    // At 1.0 the Clamp01 in UpdateMovementLoopAudioModulation ate every positive boost, so the speed
    // boost and attack swell had never once been audible. One-shots keep the 1.0 constant.
    protected virtual float MovementLoopAudioVolume =>
        Mathf.Clamp01(BundyPlugin.ModConfig?.ChainsawLoopBaseVolume?.Value ?? 0.65f);
    protected virtual bool KeepMovementLoopAudioUntilBasicAttackImpact => false;
    protected virtual bool UsesFiniteMovementLoopAudioWindow => false;
    // True when the movement-loop clip is a seamless loop segment (engine idle) rather than a
    // one-shot start+run recording: the source loops it and the finite window uses the fallback
    // duration instead of the clip length.
    protected virtual bool MovementLoopAudioClipLoops => false;
    protected virtual bool StartsMovementLoopAudioOnPullStart => false;
    protected virtual bool RequiresActiveMovementLoopAudioForAttacks => false;
    protected virtual float MovementLoopAudioWindowFallbackSeconds => 45f;
    // Wall hit -> STALLED (idle loop, attacks blocked) -> dwell -> PullStart -> RUNNING again.
    // Variants that enable this should make MovementLoopAudioCue state-dependent via IsSawStalled.
    protected virtual bool UsesSawStallCycle => false;
    // Swing-feel pitch/volume modulation of the movement loop from saw prop world speed (all clients).
    protected virtual bool UsesMovementLoopSwingModulation => false;
    protected bool IsSawStalled => _sawStalled;
    protected virtual float PostBasicAttackWalkSeconds => 0f;
    protected virtual bool UsesManualTargetFacing => false;
    protected virtual bool ManualPursuitControlsAgentRotation => false;
    protected virtual bool ManualPursuitFacingWhileMoving => true;
    protected virtual float ManualPursuitFacingDistance => Mathf.Min(ManualFacingCloseRange, StopDistance + 0.75f);
    protected virtual float ManualPursuitTurnSpeedDegrees => AgentAngularSpeedDegrees;
    protected virtual float ManualActionTurnSpeedDegrees => ActionTurnSpeedDegrees;
    protected virtual float AgentAngularSpeedDegrees => DefaultAgentAngularSpeedDegrees;
    protected virtual float AgentAcceleration => DefaultAgentAcceleration;
    protected virtual bool TurnClipsEnabled => false;
    protected virtual float TurnClipMinYawDegrees => 70f;
    protected virtual float VisualYawCorrectionDegrees => 0f;
    // Stage 3b: per-state run correction. Run defaults to the same yaw as idle/walk; the roll
    // counters a run clip's sideways lean (yaw cannot fix a roll).
    protected virtual float RunVisualYawCorrectionDegrees => VisualYawCorrectionDegrees;
    protected virtual float RunVisualRollCorrectionDegrees => 0f;
    // Stage 2 attack-while-moving lunge. When enabled, a basic attack drives the agent fast at the
    // target (legs keep running via the LungeLowerBody override layer) instead of stopping.
    protected virtual bool UsesAttackLunge => false;
    protected virtual float AttackLungeSpeed => Mathf.Max(RunSpeed, 8f);
    protected virtual float AttackLungeStopDistance => 1.0f;
    // Job 2a: the lunge standoff is derived from BLADE reach and is deliberately NOT an override of
    // AttackLungeStopDistance, which has three other consumers that must keep the 1.0 value:
    // ShouldRunAtDistance (the walk/run gait tell), HasClearLungePathTo (spherecast length — raising
    // it shortens the cast and makes the gate MORE permissive), and UpdateManualTargetFacing's
    // hand-off threshold. The default sits half a contact radius inside the contact sphere so the
    // player is comfortably within reach on arrival instead of tangent to its outer edge.
    protected virtual float AttackLungeBladeStandoff =>
        BasicAttackContactForwardOffset + BasicAttackContactRadius * 0.5f;
    // Job 2a: fraction of the swing's impact delay at which the lunge stops tracking the live player
    // and freezes its aim. The SAME fraction times the arrival, so surge, commit and blade all land on
    // one beat. One constant, two uses — split into two numbers they drift apart under tuning.
    protected virtual float AttackLungeCommitFraction => 0.85f;
    // Job 2b: seconds of plant-and-wind-up at the head of a basic attack before the lunge may drive.
    // Spends the front of the impact delay as a visible tell instead of surging on frame 1. Costs the
    // close nothing — DriveActionApproach times arrival from _actionStartedAt, so the remaining gap is
    // covered faster to compensate, bounded by R1's stoppable ceiling.
    protected virtual float AttackLungeWindupHoldSeconds => 0f;
    // Job 2a: arrival tolerance once the standoff has been baked into the destination. The standoff
    // lives in exactly one place; applying it here as well would park him at ~2x the standoff, outside
    // blade reach.
    protected virtual float LungeArrivalToleranceMeters => 0.1f;
    protected virtual float TurnClipCooldownSeconds => 1.1f;
    protected virtual float TurnClipMaxDistance => 8f;
    // #306: how far ahead the steering target must sit before its heading is trusted over the straight
    // line to the player. Below this it has collapsed onto him (arriving) and carries no direction.
    private const float TurnClipPathHeadingMinReach = 0.75f;
    protected virtual bool TurnClipsCanInterruptMovement => false;
    protected virtual float GetTurnClipRootSyncSeconds(int turnIndex) =>
        turnIndex == BundyAnimatorParams.Turn180 ? 0.75f : 0.55f;
    protected virtual bool RequiresPhysicalBasicAttackContact => false;
    protected virtual float BasicAttackContactForwardOffset => 1.05f;
    protected virtual float BasicAttackContactVerticalOffset => 1.0f;
    protected virtual float BasicAttackContactRadius => 0.9f;
    protected virtual float BasicAttackContactLatchSeconds => 0.45f;
    protected virtual float MaxPhysicalBasicAttackRootReach => BasicAttackContactForwardOffset + BasicAttackContactRadius + 1.0f;
    protected virtual float MaxLatchedPhysicalBasicAttackRootReach =>
        Mathf.Max(MaxPhysicalBasicAttackRootReach, GetBasicAttackStartRange(BasicAttackRange) + BasicAttackContactRadius + 1.0f);
    protected virtual float AttackActiveLeadSeconds => 0.08f;
    protected virtual float AttackActiveTrailSeconds => 0.12f;
    protected virtual bool UsesBasicAttackVisualExit => false;
    protected virtual float BasicAttackVisualExitBlendSeconds => 0.05f;
    // R39 A2: turn rate applied on the lunge-commit frame. +inf = legacy same-frame snap to the
    // committed direction; MaintainCommittedLungeFacing keeps turning at ManualActionTurnSpeedDegrees
    // on the following frames either way, so a finite rate here just turns the entry snap into a turn.
    protected virtual float LungeCommitFacingTurnSpeedDegrees => float.PositiveInfinity;
    // Stage 1 locomotion speed-sync. When enabled, the variant drives the animator "Speed" float
    // (= real planar velocity / target gait speed) into the Walk/Run states' Speed Multiplier so
    // foot speed tracks actual movement. Off (and float left untouched) for variants without it.
    protected virtual bool UsesLocomotionSpeedMultiplier => false;
    protected virtual bool UsesSourcePackNormalLocomotionPlayback => false;
    protected virtual float LocomotionSpeedMultiplierMin => 0.2f;
    protected virtual float LocomotionSpeedMultiplierMax => 2.5f;
    // The clip's authored ground cadence (m/s) at which it plays at rate 1.0. Used as the
    // multiplier reference so it is DECOUPLED from move-speed config: changing WalkSpeed/RunSpeed
    // (gameplay) does not reintroduce foot slide. Defaults to the move speed for variants where
    // they coincide; the chainsaw variant pins these to the authored source-pack cycle speeds.
    protected virtual float LocomotionWalkClipCadence => WalkSpeed;
    protected virtual float LocomotionRunClipCadence => RunSpeed;
    protected virtual float LocomotionSprintClipCadence => SprintSpeed;
    // Stage 4: directional locomotion. When enabled, the variant feeds (MoveX, MoveY) = local-space
    // planar velocity into a 2D blend tree so it plays the correct 8-way walk/run clip for any travel
    // direction relative to facing (strafe / backpedal / diagonals).
    protected virtual bool UsesDirectionalLocomotion => false;
    protected virtual float DirectionalLocomotionResponse => 10f;
    protected virtual float DirectionalLocomotionAxisYawDegrees => 0f;
    protected virtual float DirectionalRunLocomotionAxisYawDegrees => DirectionalLocomotionAxisYawDegrees;
    protected virtual bool UsesObservedVelocityIdleDemotion => false;
    protected virtual float ObservedVelocityIdleDemotionHoldSeconds => 0.35f;
    // R33: pins the blend DIRECTION to pure forward (magnitude preserved) so locomotion always plays
    // the forward walk/run clip. For source packs whose directional children are authored with
    // divergent body cants, blending between them wobbles the visible facing as the nav heading
    // wiggles; the AI root already rotates toward travel, so the variants add only cant noise.
    protected virtual bool LockDirectionalLocomotionToForward => false;
    // How fast the smoothed travel HEADING rotates toward the real one (deg/s). The heading is smoothed
    // angularly so a direction reversal sweeps AROUND the blend ring instead of cutting through the Idle
    // clip at the centre — that center-transit was why he "moved with no animation" on every turn.
    protected virtual float DirectionalTurnResponseDegrees => 540f;
    // Below this planar speed he is treated as stopped (blend eases back to Idle). Above it he is "moving"
    // and the blend magnitude is floored to DirectionalMinMoveRadius so a directional clip always dominates.
    protected virtual float DirectionalMoveDeadband => 0.12f;
    // Walk-ring radius in the 2D blend (clips sit at ~0.95). Flooring the magnitude here whenever he moves
    // keeps the blend point ON the ring (full walk/run clip) instead of inside it where Idle bleeds in.
    // Stage 4 Phase 2: while on attack cooldown at engagement range, circle/back off instead of
    // beelining (shows off the directional clips and re-establishes lunge range). Off by default.
    protected virtual bool UsesCombatManeuvers => false;
    protected virtual float CombatManeuverDuration => 1.4f;
    // Hit-reaction staggers + intimidation taunt. Off by default; the chainsaw variant enables them and
    // supplies the actual animation triggers.
    protected virtual bool StaggersEnabled => false;
    protected virtual bool TauntsEnabled => false;
    protected virtual bool BlockedTargetTauntsEnabled => TauntsEnabled;
    protected virtual bool BlockedTargetHideEnabled => true;
    protected virtual bool UsesBatterySabotageDiversion => true;
    protected virtual bool UsesPerceptionAcquireRev => false;
    protected bool ShouldUseIdleChainsawLoop =>
        _strangerTargetingActive &&
        _pursuitMode != PursuitMode.NonTargetInterrupt &&
        _strangerPerceptionState != StrangerPerceptionState.Hunt;
    protected virtual bool TryEnterShipSiege(PlayerControllerB target)
    {
        BundyShipSystemsBridge.TryForceBatterySabotageTarget(this);
        return TryRunBatterySabotageRoute();
    }
    protected virtual void PlayStaggerAnimation(int staggerType) { }
    protected bool IsNonTargetInterruptActive => _pursuitMode == PursuitMode.NonTargetInterrupt;

    protected virtual void SetFinisherPropVisibilityLocal(int bodyIndex, bool visible)
    {
    }

    protected virtual void ResetActionPropVisibilityLocal()
    {
    }

    private float RunThreshold => BundyPlugin.ModConfig?.RunThreshold?.Value ?? 5f;
    private bool FarSprintEnabled => BundyPlugin.ModConfig?.FarSprintEnabled?.Value ?? true;
    private float FarSprintMinDistanceMeters => BundyPlugin.ModConfig?.FarSprintMinDistanceMeters?.Value ?? 60f;
    private bool TeleporterRevealEnabled => BundyPlugin.ModConfig?.TeleporterRevealEnabled?.Value ?? true;
    private bool ShipSiegeFallbackEnabled => BundyPlugin.ModConfig?.ShipSiegeFallbackEnabled?.Value ?? true;
    private float ShipSiegeProwlRadiusMeters => BundyPlugin.ModConfig?.ShipSiegeProwlRadiusMeters?.Value ?? 3.5f;
    private float ShipSiegePoundIntervalSeconds => BundyPlugin.ModConfig?.ShipSiegePoundIntervalSeconds?.Value ?? 3.5f;
    private float StopDistance => BundyPlugin.ModConfig?.StopDistance?.Value ?? 1.75f;
    private float BasicAttackRange => BundyPlugin.ModConfig?.BasicAttackRange?.Value ?? 2.25f;
    private float FinisherAttackRange => BundyPlugin.ModConfig?.FinisherAttackRange?.Value ?? 2.75f;
    private float AttackCooldown => BundyPlugin.ModConfig?.AttackCooldown?.Value ?? 2f;
    // R2 interim: keep combat maneuvers and the attack lunge out of the same beat. Config-backed so it
    // can be A/B'd off in-game against the movement-authority fix when that lands.
    private bool CombatManeuverAttackGuardEnabled =>
        BundyPlugin.ModConfig?.CombatManeuverAttackGuard?.Value ?? true;
    private float CombatManeuverAttackDwellSeconds =>
        BundyPlugin.ModConfig?.CombatManeuverAttackDwellSeconds?.Value ?? 0.45f;
    // B2/B3: the deterministic maneuver machine's three tuning knobs. Defaults ARE the new behaviour.
    private float CombatManeuverMinWindowSeconds =>
        BundyPlugin.ModConfig?.CombatManeuverMinWindowSeconds?.Value ?? 0.55f;
    private float CombatManeuverArcStepDegrees =>
        BundyPlugin.ModConfig?.CombatManeuverArcStepDegrees?.Value ?? 65f;
    private float CombatManeuverArrivalHoldSeconds =>
        BundyPlugin.ModConfig?.CombatManeuverArrivalHoldSeconds?.Value ?? 0.35f;
    private float NonTargetInterruptRange => BundyPlugin.ModConfig?.NonTargetInterruptRange?.Value ?? 8f;
    private float EffectiveNonTargetInterruptRange => Mathf.Max(0f, NonTargetInterruptRange);
    private float MultiAttackNearbyRange => BundyPlugin.ModConfig?.MultiAttackNearbyRange?.Value ?? 3.25f;
    private float StrangerPingIntervalSeconds => BundyPlugin.ModConfig?.StrangerPingIntervalSeconds?.Value ?? 7f;
    private float PingPositionNoiseMeters => BundyPlugin.ModConfig?.PingPositionNoiseMeters?.Value ?? 12f;
    private float SearchWindowSeconds => BundyPlugin.ModConfig?.SearchWindowSeconds?.Value ?? 25f;
    private float HearingRadiusMeters => BundyPlugin.ModConfig?.HearingRadiusMeters?.Value ?? 14f;
    private float SightMemoryGraceSeconds => BundyPlugin.ModConfig?.SightMemoryGraceSeconds?.Value ?? 2.5f;
    private float StrangerSightProximityMeters => BundyPlugin.ModConfig?.StrangerSightProximityMeters?.Value ?? 3.5f;
    private float PursuitMemorySeconds => BundyPlugin.ModConfig?.PursuitMemorySeconds?.Value ?? 9f;
    private bool HuntDwellEnabled => BundyPlugin.ModConfig?.HuntDwellEnabled?.Value ?? true;
    private float NoiseContactHuntDwellSeconds => BundyPlugin.ModConfig?.NoiseContactHuntDwellSeconds?.Value ?? 8f;
    private bool WorldNoiseBusEnabled => BundyPlugin.ModConfig?.WorldNoiseBusEnabled?.Value ?? true;
    private float WorldNoiseRadiusScale => BundyPlugin.ModConfig?.WorldNoiseRadiusScale?.Value ?? 1f;
    private float WorldNoiseLiveConfidenceThreshold => BundyPlugin.ModConfig?.WorldNoiseLiveConfidenceThreshold?.Value ?? 0.9f;
    private float WorldNoisePositionNoiseMeters => BundyPlugin.ModConfig?.WorldNoisePositionNoiseMeters?.Value ?? 8f;
    private float WorldNoiseAttributionRadiusMeters => BundyPlugin.ModConfig?.WorldNoiseAttributionRadiusMeters?.Value ?? 4f;
    private float WorldNoiseEventCooldownSeconds => BundyPlugin.ModConfig?.WorldNoiseEventCooldownSeconds?.Value ?? 3f;
    private bool ExitLurkEnabled => BundyPlugin.ModConfig?.ExitLurkEnabled?.Value ?? true;
    private bool CrossBoundaryPathSuppressionEnabled => BundyPlugin.ModConfig?.CrossBoundaryPathSuppressionEnabled?.Value ?? true;
    private float CrossingRouteCacheSeconds => BundyPlugin.ModConfig?.CrossingRouteCacheSeconds?.Value ?? 5f;
    private bool LongRangeAsyncReachabilityEnabled => BundyPlugin.ModConfig?.LongRangeAsyncReachabilityEnabled?.Value ?? true;
    private bool SmartBoundaryLinkGateEnabled => BundyPlugin.ModConfig?.SmartBoundaryLinkGateEnabled?.Value ?? true;
    private float ExitLurkMaxPathMeters => BundyPlugin.ModConfig?.ExitLurkMaxPathMeters?.Value ?? 60f;
    private float ExitLurkMaxTargetDistanceMeters => BundyPlugin.ModConfig?.ExitLurkMaxTargetDistanceMeters?.Value ?? 40f;
    private float ExitLurkAbandonDistanceFactor => BundyPlugin.ModConfig?.ExitLurkAbandonDistanceFactor?.Value ?? 1.5f;
    private float ExitLurkRaceMargin => BundyPlugin.ModConfig?.ExitLurkRaceMargin?.Value ?? 1.35f;
    private float ExitLurkCommitSeconds => BundyPlugin.ModConfig?.ExitLurkCommitSeconds?.Value ?? 20f;
    private float ExitLurkCooldownSeconds => BundyPlugin.ModConfig?.ExitLurkCooldownSeconds?.Value ?? 45f;
    private int SearchRoamPointCount => BundyPlugin.ModConfig?.SearchRoamPointCount?.Value ?? 3;
    private float SearchRoamRadiusMeters => BundyPlugin.ModConfig?.SearchRoamRadiusMeters?.Value ?? 10f;
    private float StrangerEscalationStartSeconds => BundyPlugin.ModConfig?.StrangerEscalationStartSeconds?.Value ?? 120f;
    private float StrangerEscalationDurationSeconds => BundyPlugin.ModConfig?.StrangerEscalationDurationSeconds?.Value ?? 300f;
    private float EscalatedStrangerPingIntervalSeconds => BundyPlugin.ModConfig?.EscalatedStrangerPingIntervalSeconds?.Value ?? 3.5f;
    private float EscalatedPingPositionNoiseMeters => BundyPlugin.ModConfig?.EscalatedPingPositionNoiseMeters?.Value ?? 4f;
    // #288 stagger bar.
    private float StaggerBarMax => Mathf.Max(1f, BundyPlugin.ModConfig?.StaggerBarMax?.Value ?? 100f);
    private float StaggerBarDamagePerForce => BundyPlugin.ModConfig?.StaggerBarDamagePerForce?.Value ?? 5f;
    private float StaggerBarDamageMin => BundyPlugin.ModConfig?.StaggerBarDamageMin?.Value ?? 4f;
    // The two bounds are independent config entries, so a profile can legally set min > max —
    // Mathf.Clamp(value, min, max) with min > max silently returns max, which would pin EVERY hit to
    // the smaller number and make heavy weapons weaker than a shovel. Resolve it here instead.
    private float StaggerBarDamageMax =>
        Mathf.Max(StaggerBarDamageMin, BundyPlugin.ModConfig?.StaggerBarDamageMax?.Value ?? 25f);
    private float StaggerBarStunThreshold =>
        StaggerBarMax * Mathf.Clamp01((BundyPlugin.ModConfig?.StaggerBarStunThresholdPercent?.Value ?? 50f) / 100f);
    private float StaggerBarRegenDelaySeconds => BundyPlugin.ModConfig?.StaggerBarRegenDelaySeconds?.Value ?? 4f;
    private float StaggerBarRegenPerSecond => BundyPlugin.ModConfig?.StaggerBarRegenPerSecond?.Value ?? 5f;
    private float StaggerBarStunDeviceCost => BundyPlugin.ModConfig?.StaggerBarStunDeviceCost?.Value ?? 30f;
    private float StaggerCooldownSeconds => BundyPlugin.ModConfig?.StaggerCooldownSeconds?.Value ?? 1.6f;
    private bool ProceduralFlinchEnabled => BundyPlugin.ModConfig?.ProceduralFlinchEnabled?.Value ?? true;
    private float ProceduralFlinchIntensityScale => BundyPlugin.ModConfig?.ProceduralFlinchIntensityScale?.Value ?? 1f;
    private float StaggerKnockdownImpactSlowdown => BundyPlugin.ModConfig?.StaggerKnockdownImpactSlowdown?.Value ?? 0.65f;
    private float StaggerKnockdownSlowdownStartFraction => BundyPlugin.ModConfig?.StaggerKnockdownSlowdownStartFraction?.Value ?? 0.5f;
    private bool StaggerMovementLockEnabled => BundyPlugin.ModConfig?.StaggerMovementLock?.Value ?? true;
    private int SpikeTrapHitForce => Mathf.Max(0, BundyPlugin.ModConfig?.SpikeTrapHitForce?.Value ?? 5);
    // Job 5a: drop-down follow ("jump"). See BundyAIBase.DropJump.cs. Always on — the old
    // DropDownFollowEnabled toggle was retired when the drop became core pursuit behaviour; the
    // min-height/cooldown config keys were renamed on re-default (see BundyConfig.cs).
    private float DropDownFollowMinHeightMeters => BundyPlugin.ModConfig?.DropDownFollowMinDropMeters?.Value ?? 2f;
    private float DropDownFollowMaxHeightMeters => BundyPlugin.ModConfig?.DropDownFollowMaxHeightMeters?.Value ?? 12f;
    private float DropDownFollowCooldownSeconds => BundyPlugin.ModConfig?.DropDownFollowMinIntervalSeconds?.Value ?? 3f;
    private float DropDownFollowGravity => BundyPlugin.ModConfig?.DropDownFollowGravity?.Value ?? 22f;
    private float DropDownFollowLaunchUpSpeed => BundyPlugin.ModConfig?.DropDownFollowLaunchUpSpeed?.Value ?? 1.5f;
    private float DropDownFollowMaxHorizontalSpeed => BundyPlugin.ModConfig?.DropDownFollowMaxHorizontalSpeed?.Value ?? 9f;
    private float DropDownFollowMaxTraversalSeconds => BundyPlugin.ModConfig?.DropDownFollowMaxTraversalSeconds?.Value ?? 3.5f;
    // Issue #205: the overhead sprint. See BundyAIBase.Sprint.cs.
    private float SprintMinDistanceMeters => BundyPlugin.ModConfig?.ChainsawSprintMinDistance?.Value ?? 18f;
    private float SprintNoContactSeconds => BundyPlugin.ModConfig?.ChainsawSprintNoContactSeconds?.Value ?? 25f;
    private float SprintChance => BundyPlugin.ModConfig?.ChainsawSprintChance?.Value ?? 0.35f;
    private float SprintRollIntervalSeconds => BundyPlugin.ModConfig?.ChainsawSprintRollIntervalSeconds?.Value ?? 8f;
    private float SprintCooldownSeconds => BundyPlugin.ModConfig?.ChainsawSprintCooldownSeconds?.Value ?? 20f;
    private float SprintMaxSeconds => BundyPlugin.ModConfig?.ChainsawSprintMaxSeconds?.Value ?? 12f;
    private bool SprintAfterKnockdownTauntEnabled => BundyPlugin.ModConfig?.ChainsawSprintAfterKnockdownTaunt?.Value ?? true;
    private bool SprintIntroSpeedRampEnabled => BundyPlugin.ModConfig?.ChainsawSprintIntroSpeedRamp?.Value ?? true;
    private bool ForceSprintEnabled => BundyPlugin.ModConfig?.ChainsawForceSprint?.Value ?? false;

    internal void ConfigureRigLabMode(bool enabled)
    {
        _rigLabMode = enabled;
        if (enabled)
            LogInfo("RigLab mode enabled: battery sabotage diversion disabled; turn-clip test path enabled by variant.");
    }

    internal int ActiveAttackIndexForDiagnostics => _activeAttackIndex;
    internal string AttackPhaseForDiagnostics => _attackPhase.ToString();
    internal float AttackElapsedForDiagnostics => _actionKind == ActionKind.Attack ? Time.time - _actionStartedAt : -1f;
    internal float AttackActiveWindowRemainingForDiagnostics => _actionKind == ActionKind.Attack ? _attackActiveWindowEndAt - Time.time : 0f;
    internal bool AttackLungeActiveForDiagnostics => _lungeActive;

    internal static EnemyBehaviourState[] CreateSafeBehaviourStates()
    {
        return new[]
        {
            new EnemyBehaviourState { name = "Idle" },
            new EnemyBehaviourState { name = "Tracking" },
        };
    }

    public override void Start()
    {
        try
        {
            base.Start();
        }
        catch (Exception e)
        {
            LogError($"base.Start exception: {e}");
        }

        try
        {
            EnsureInstanceBehaviourStates();
            ResolveRuntimeReferences();
            RunSkinMaterialDiagnosticsForInstance();
            ApplyBodySkinTintForInstance();
            BundyStrangerSack.ApplyToInstance(this);
            LogRuntimeDiagnosticsOnce();
            ConfigureAgent();
            ApplyVisualYawCorrection();
            SetLocalBehaviourState(BehaviourStateIdle);
            SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
            ResetDialogueTimer();

            LogInfo(
                $"Spawn audit: pos={transform.position}, IsServer={IsServer}, IsHost={IsHost}, IsOwner={IsOwner}, " +
                $"agent={(agent != null)}, onNavMesh={(agent != null && agent.enabled && agent.isOnNavMesh)}, animator={(creatureAnimator != null)}");

            if (IsOwner && RoundReady())
                TryPrepareStrangerSnapshotTarget();
        }
        catch (Exception e)
        {
            LogError($"Start setup exception: {e}");
        }
    }

    public override void EnableEnemyMesh(bool enable, bool overrideDoNotSet = false, bool tamperWithMeshes = false)
    {
        BundyStrangerEmployee.RefreshVisibilityCaches(this);
        base.EnableEnemyMesh(enable, overrideDoNotSet, tamperWithMeshes);
        // Vanilla may rebuild its renderer lists when tamperWithMeshes is requested.
        BundyStrangerEmployee.RefreshVisibilityCaches(this);
    }

    public override void Update()
    {
        long updateTimingStarted = BundyRuntimePerformanceDiagnostics.Begin();
        try
        {
        try
        {
            base.Update();

            UpdateForcedCriticalFinisherState();

            long ensureAgentTimingStarted = BundyRuntimePerformanceDiagnostics.Begin();
            try
            {
                EnsureAgentOnNavMesh();
            }
            finally
            {
                BundyRuntimePerformanceDiagnostics.End(
                    BundyRuntimeStep.EnsureAgentOnNavMesh,
                    ensureAgentTimingStarted);
            }
            ReportEscalationAudioSignal();
            if (isEnemyDead)
            {
                // #323: the gore bed's fade/stop backstop and the engine muffle's release both live
                // in this driver, and death returns before the main call site below. Without this a
                // Bundy killed mid-finisher leaves a looping gore bed playing from his corpse and
                // the muffle frozen at full depth.
                UpdateFinisherGoreAudio();
                RestoreStaggerAnimatorSpeed();
                _staggerKnockdownActive = false;
                // Death returns before UpdateStaggerImpactSlowdown's backstop can run, so the pin has to
                // be released here or a Bundy who dies while down keeps his root and agent held.
                EndKnockdownRootPin("enemy died");
                EndPairedFinisherAlignment("enemy died"); // #321
                if (IsOwner)
                    ReleaseStaggerAgentStop();
                _movementLoopAudioReadyUntil = float.NegativeInfinity;
                if (IsServer)
                    SetMovementLoopAudioRequested(false);
                else
                    SetMovementLoopAudioLocal(false, BundyAudioCue.ChainsawIdle, 0f);
                return;
            }

            // #300 instrumentation: everything from here to the end of Update was untimed, which is
            // where the constant 21-27ms Tier-3 drip has to live if it is Bundy's at all. One residual
            // bracket splits "Bundy's tail" from "vanilla base.Update()" in a single report window.
            long updateTailTimingStarted = BundyRuntimePerformanceDiagnostics.Begin();
            try
            {
            ResolveRuntimeReferences();
            ApplyVisualYawCorrection(); // Stage 3: live-tunable stance yaw, applied on all clients.
            UpdateSyntheticFootsteps();
            UpdateFootstepGaitRange(); // #213: all clients — each evaluates its own listener distance
            UpdateMovementLoopAudioModulation(); // all clients, before the owner gate
            // #323: all clients, and BEFORE the air absorption that reads its muffle envelope.
            // Separate from UpdateMovementLoopAudioModulation on purpose — that method early-returns
            // when the engine loop is not playing, and a finisher still has to make the body sound
            // when the saw happens to be stalled or the loop emitter has not resolved yet.
            UpdateFinisherGoreAudio();
            // #321/#322/#323: all clients. A release RPC that never arrives would otherwise leave a
            // remote client's suppression, chute and muffle armed forever — no CompleteAction runs
            // anywhere but the server.
            UpdateFinisherLocalTeardownBackstop();
            UpdateAudioAirAbsorption(); // all clients, before the owner gate
            UpdateStaggerBarRegen();       // #288: all clients — the bar is simulated identically everywhere
            UpdateStaggerImpactSlowdown(); // all clients — each drives its own animator during staggers
            if (DebugLogs)
                LogFacingProbe("pre-owner-gate");
            if (!IsOwner)
            {
                MaintainPairedActionRootLock();
                // #321: every client owns the victim lock it can see, so every client corrects the
                // pair it renders. Immediately after the root pin so it reads this frame's locked root.
                MaintainPairedFinisherAlignment();
                UpdateObservedLocomotionVisuals();
                return;
            }

            // #288: republish the reaction-suppression mask on change. Owner-only compute, server
            // fanout; every client's hit-reaction guards read the mirror it maintains.
            MaintainReactionSuppressionMirror();
            UpdatePullStartCompletion(); // C4.3: before the fallback check so a retime lands same-frame
            UpdateChainAttack(); // #281: a due chain rep re-arms the fallbacks before they can resolve
            UpdateActionFallbacks();
            if (_actionKind == ActionKind.Stagger && StaggerMovementLockEnabled)
                HoldAgentForStagger();
            MaintainPairedActionRootLock();
            MaintainPairedFinisherAlignment(); // #321, same frame as the root pin it reads
            // Job 5a: while the drop-down arc is airborne it owns the root outright — facing, position,
            // the locomotion channels and the movement watchdog all belong to it for that window. Audio
            // and dialogue keep ticking so a 1s fall does not leave the chainsaw loop stuck.
            if (UpdateActiveDropJump())
            {
                UpdateDialoguePlayback();
                UpdateMovementLoopAudioState();
                return;
            }
            UpdateTrackedTargetMotion();
            UpdateActiveTurnClipRootSync();
            UpdateManualTargetFacing();
            CheckPursuitZeroRelease();
            UpdateStrangerSightRefresh(); // #307: owner-only, throttled LOS re-acquire in HUNT/TRACK
            ApplyMotionStateFromMovement();
            UpdateSprintIntroTravelSample();
            UpdateLungeLayerWeight();
            UpdateAttackPhase();
            UpdateBasicAttackVisualExit();
            UpdateAttackSeamSampler();
            ClampMovementAgainstWorldGeometry();
            LogTurnClipDiagnostics();
            long recoveryTimingStarted = BundyRuntimePerformanceDiagnostics.Begin();
            try
            {
                RecoverMovementIfNeeded();
            }
            finally
            {
                BundyRuntimePerformanceDiagnostics.End(
                    BundyRuntimeStep.RecoverMovement,
                    recoveryTimingStarted);
            }
            // #300: a sliced reachable-point sweep must not depend on the caller that opened it asking
            // again -- the ship-siege caller backs off for seconds. Pump it here so it completes at
            // frame rate. The gate is "was the stuck-recovery sweep ATTEMPTED this frame", not "is a
            // stuck sweep flagged active" (#300 B1c) -- a suspended sweep leaves the flag up while never
            // reaching its advance, and gating on the flag killed the pump for the round. Attempted, not
            // spent: the frame stamp is set before the shared-budget check, so on a frame where the pot
            // is already empty both machines stand down. Conservative and intentional.
            if (!StuckRecoveryAttemptedThisFrame)
                AdvancePendingReachableNavPointSearch();
            UpdateDirectionalLocomotion();
            LogThrottledAnimatorState();
            if (DebugLogs)
                LogFacingProbe("owner-after-movement");
            UpdateDialoguePlayback();
            UpdateMaskedReaction();
            UpdateMovementLoopAudioState();
            }
            finally
            {
                BundyRuntimePerformanceDiagnostics.End(
                    BundyRuntimeStep.UpdateTail,
                    updateTailTimingStarted);
            }
        }
        catch (Exception e)
        {
            LogError($"Update exception: {e}");
        }
        }
        finally
        {
            BundyRuntimePerformanceDiagnostics.End(BundyRuntimeStep.Update, updateTimingStarted);
        }
    }

    public override void DoAIInterval()
    {
        long intervalTimingStarted = BundyRuntimePerformanceDiagnostics.Begin();
        try
        {
            if (!IsOwner || isEnemyDead)
                return;

            ResolveRuntimeReferences();

            // Job 5a: the arc drives the transform itself, so the pursuit layer must not issue anything
            // this interval. base.DoAIInterval still runs (via RunBaseAIIntervalIfSafe) because its
            // SyncPositionToClients is what lets remote clients see the fall — inSpecialAnimation is
            // deliberately never set for exactly that reason.
            if (IsDropJumpActive())
            {
                RunBaseAIIntervalIfSafe();
                return;
            }

            if (!RoundReady())
            {
                StopMovement();
                SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
                RunBaseAIIntervalIfSafe();
                return;
            }

            ReconcileOutsideStateWithPhysicalPosition();
            MaybeLogSmartEntranceSettleComplete();

            if (!AgentReadyForMovement())
            {
                StopMovement();
                SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
                return;
            }

            PlayerControllerB forcedCriticalFinisherTarget = null;
            bool forcedCriticalFinisherWindowActive =
                UsesForcedCriticalFinisherGate &&
                TryGetForcedCriticalFinisherTarget(out forcedCriticalFinisherTarget);
            if (!IsActionLocked() && forcedCriticalFinisherWindowActive)
            {
                targetPlayer = forcedCriticalFinisherTarget;
                if (TryBeginAttackOpportunity(forcedCriticalFinisherTarget))
                    return;
            }

            if (!IsActionLocked() &&
                !forcedCriticalFinisherWindowActive &&
                Time.time < _smartInteractionHoldUntil)
            {
                HoldAgentWithoutReset();
                SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
                return;
            }

            if (!IsActionLocked() &&
                !forcedCriticalFinisherWindowActive &&
                TryPlayPendingTaunt())
                return;

            if (!forcedCriticalFinisherWindowActive && !TryPrepareStrangerSnapshotTarget())
            {
                StopMovement();
                SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
                RunBaseAIIntervalIfSafe();
                return;
            }

            long perceptionTimingStarted = BundyRuntimePerformanceDiagnostics.Begin();
            try
            {
                UpdateStrangerPerception();
                UpdateStrangerPursuitStallWatchdog();
                UpdateBoundaryEntryFailsafe();
                MaybeExpireStrangerShipSiegeFallback();
            }
            finally
            {
                BundyRuntimePerformanceDiagnostics.End(
                    BundyRuntimeStep.Perception,
                    perceptionTimingStarted);
            }

            if (!forcedCriticalFinisherWindowActive && TryRunBatterySabotageRoute())
                return;

            if (!forcedCriticalFinisherWindowActive &&
                _pursuitMode != PursuitMode.NonTargetInterrupt)
                TryEnterNonTargetInterrupt();

            if (!forcedCriticalFinisherWindowActive &&
                _pursuitMode != PursuitMode.NonTargetInterrupt &&
                TryHandleStrangerShipSiege())
            {
                RunBaseAIIntervalIfSafe();
                return;
            }

            PlayerControllerB huntTarget = forcedCriticalFinisherWindowActive
                ? forcedCriticalFinisherTarget
                : GetCurrentHuntTarget();
            if (!IsReachableHuntTarget(huntTarget))
            {
                if (_pursuitMode == PursuitMode.NonTargetInterrupt)
                {
                    ExitPursuitZero("non-target lost");
                    huntTarget = GetCurrentHuntTarget();
                }
            }

            if (!IsReachableHuntTarget(huntTarget))
            {
                StopMovement();
                SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
                RunBaseAIIntervalIfSafe();
                return;
            }

            targetPlayer = huntTarget;
            TryRefreshStrangerLiveTargetKnowledge(huntTarget);

            if (IsTurnClipRootLockActive())
            {
                HoldAgentWithoutReset();
                UpdateLocomotionSpeedMultiplier(false, false);
                SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
                UpdateDirectionalLocomotion();
                return;
            }

            if (IsActionLocked())
            {
                MaintainActionMovement();
                RunBaseAIIntervalIfSafe();
                return;
            }

            if (CanUseLiveHuntTarget(huntTarget) && TryBeginAttackOpportunity(huntTarget))
                return;

            if (!forcedCriticalFinisherWindowActive &&
                CanUseLiveHuntTarget(huntTarget) &&
                TryBeginMovementContext(huntTarget))
                return;

            // Job 5a: last thing before the pursuit resolver. Deliberately after the attack and
            // movement-context opportunities — a swing that is already available always beats a drop.
            if (!forcedCriticalFinisherWindowActive && TryBeginDropDownFollow(huntTarget))
                return;

            // Issue #205: last thing before the pursuit resolver, so the gait's speed is in place for
            // the destination this interval issues. After the attack opportunity above on purpose —
            // starting a sprint must never pre-empt a swing that is already available.
            if (!forcedCriticalFinisherWindowActive)
                UpdateSprintState(huntTarget);

            // #306: a local sidestep owns the destination for its short hold — the pursuit resolver would
            // re-issue the point he is already grinding against and undo the escape on the next interval.
            // The base interval still drives the agent toward the sidestep point (SetDestinationToPosition
            // wrote it), so this only suppresses the mod's re-resolve, not movement.
            if (Time.time < _movementSidestepHoldUntil)
            {
                UpdateDirectionalLocomotion();
                RunBaseAIIntervalIfSafe();
                return;
            }

            long movementTimingStarted = BundyRuntimePerformanceDiagnostics.Begin();
            try
            {
                UpdateMovementDestination(huntTarget);
            }
            finally
            {
                BundyRuntimePerformanceDiagnostics.End(
                    BundyRuntimeStep.MovementDestination,
                    movementTimingStarted);
            }
            UpdateDirectionalLocomotion();
            RunBaseAIIntervalIfSafe();
        }
        catch (Exception e)
        {
            LogError($"DoAIInterval exception: {e}");
        }
        finally
        {
            BundyRuntimePerformanceDiagnostics.End(BundyRuntimeStep.DoAIInterval, intervalTimingStarted);
        }
    }

    public override void CancelSpecialAnimationWithPlayer()
    {
        BundyVictimInteractionsBridge.EndForBundy(this, "Bundy special animation cancelled");
        BundyVictimPlayableController.EndForBundy(this, "Bundy special animation cancelled");
        // #321/#322/#323: vanilla cancels the paired animation from paths that never reach our own
        // release (a teleport, a vehicle grab, another enemy taking the player). Runs on every
        // client this cancel reaches, and is a no-op when no finisher is in flight.
        RunFinisherLocalTeardown("Bundy special animation cancelled");
        base.CancelSpecialAnimationWithPlayer();
    }

    public override void KillEnemy(bool destroy = false)
    {
        ClearForcedCriticalFinisherStateOnTeardown("Bundy killed");
        // #321/#322/#323: death never reaches CompleteAction and Update early-returns on
        // isEnemyDead, so this is the only teardown a Bundy killed mid-finisher gets. Before
        // base.KillEnemy: it is what despawns/disables the body the colliders belong to.
        RunFinisherLocalTeardown("Bundy killed");
        base.KillEnemy(destroy);
    }

    public override void OnNetworkDespawn()
    {
        ClearForcedCriticalFinisherStateOnTeardown("Bundy network despawn");
        RunFinisherLocalTeardown("Bundy network despawn", stopAudioImmediately: true);
        ClearDamageBloodOnTeardown();
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        ClearForcedCriticalFinisherStateOnTeardown("Bundy destroyed");
        // Also purges this Bundy's registry entry outright, so a destroyed instance can never leave
        // the static suppression dictionary holding references to destroyed colliders.
        RunFinisherLocalTeardown("Bundy destroyed", stopAudioImmediately: true);
        ClearDamageBloodOnTeardown();
        _smartPathing?.Dispose();
        _smartPathing = null;
        base.OnDestroy();
    }

}
