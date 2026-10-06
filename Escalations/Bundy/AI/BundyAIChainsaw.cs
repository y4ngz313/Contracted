namespace Y4NGZCompany.Escalations.Bundy.AI;

public class BundyAIChainsaw : BundyAIBase
{
    private const int LeftSwingAttackIndex = 0;
    private static readonly int[] SingleTargetBasicAttackIndices = { LeftSwingAttackIndex, 1, 2 };
    private static readonly int[] CrowdBasicAttackIndices = { LeftSwingAttackIndex, 1, 2, 3 };
    // Runtime aim diagnostics showed RightSwing/FullSpin contacts landing ~80-90 degrees off target.
    // Keep RigLab on the known-good pack-authored left swing until those clips get clip-specific windows.
    private static readonly int[] RigLabCuratedBasicAttackIndices = { LeftSwingAttackIndex };
    private const float ChainsawSingleSwingContactSeconds = 1.17f;
    private const float ChainsawFullSpinContactSeconds = 1.55f;
    private const float ChainsawDoubleSwingContactSeconds = 1.47f;
    // Authored clip lengths, measured off the controller graph.
    private const float ChainsawSingleSwingClipSeconds = 2.333f;
    private const float ChainsawFullSpinClipSeconds = 3.10f;
    private const float ChainsawDoubleSwingClipSeconds = 3.50f;
    private const float ChainsawSingleSwingRecoverSeconds = 2.2f;
    private const float ChainsawFullSpinRecoverSeconds = 2.9f;
    private const float ChainsawDoubleSwingRecoverSeconds = 3.25f;
    private const float ChainsawRunDistance = 8f;
    private const float AuthoredChainsawWalkSpeed = 0.98f;
    private const float AuthoredChainsawRunSpeed = 3.6f;
    // Job 4.2 (2026-08-04). The walk's cadence reference is deliberately NOT its authored 0.98, and
    // this is the ONLY lever that produces stride. UpdateLocomotionSpeedMultiplierFromSpeed divides
    // ground speed by this number, so stride per cycle collapses to 1.333s x cadence and nothing
    // else: at 0.98 the walk covers 1.31 m (0.90 leg lengths) for ANY value of ChainsawWalkSpeed,
    // because the multiplier divides the speed straight back out. Raising WalkSpeed alone therefore
    // buys steps/min, not stride — 0.98 -> 1.7 moves him from 90 to 156 steps/min at the SAME 0.90
    // leg lengths, which is more scurrying, not less. Over-driving the reference to 1.4 plays the
    // clip at 1.7/1.4 = 1.21x instead of 1.73x, so he covers 1.87 m per cycle (1.29 leg lengths) at
    // 109 steps/min. The cost is deliberate: the feet sweep at 0.98 x 1.21 = 1.19 m/s against
    // 1.7 m/s of travel, so 30% of each stride is foot slip (the fraction is exactly 1 - 0.98/1.4,
    // independent of WalkSpeed). Keep this in lockstep with the ChainsawWalkSpeed config default —
    // they are two halves of one tuning, and moving either alone changes the gait.
    // The run needs no equivalent: its 3.6 IS authored, so it already strides 3.0 m (2.07 leg
    // lengths) per cycle at 4.25 m/s with zero slip.
    private const float ChainsawWalkClipCadence = 1.4f;
    // Issue #205. Unlike the walk and run — whose clips came from the source pack and were measured
    // afterwards, hence an authored cadence (3.6) that differs from the gameplay speed (4.25) — the
    // sprint was authored TO the gameplay speed on purpose: the clip is in-place, so the stance foot
    // only stops skating when the agent moves at exactly the speed the cycle was derived for. The two
    // numbers coincide here by construction, and they must stay that way.
    private const float AuthoredChainsawSprintSpeed = 7.65f;
    // Rotate Bundy's local forward velocity onto each gait tree's exact authored forward child.
    private const float SourcePackWalkLocomotionAxisYawDegrees = -52.38f;
    private const float SourcePackRunLocomotionAxisYawDegrees = -49.25f;
    protected override string VariantName => IsRigLabMode ? "Bundy Chainsaw Rig Lab" : "Bundy Chainsaw";
    protected override float WalkSpeed => Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawWalkSpeed?.Value ?? AuthoredChainsawWalkSpeed;
    protected override float RunSpeed => Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawRunSpeed?.Value ?? AuthoredChainsawRunSpeed;
    protected override bool UsesLocomotionSpeedMultiplier => true;
    protected override bool UsesSourcePackNormalLocomotionPlayback => false;
    protected override float LocomotionWalkClipCadence => ChainsawWalkClipCadence;
    protected override float LocomotionRunClipCadence => AuthoredChainsawRunSpeed;
    protected override float LocomotionSprintClipCadence => AuthoredChainsawSprintSpeed;
    protected override float SprintSpeed =>
        Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawSprintSpeed?.Value ?? AuthoredChainsawSprintSpeed;
    protected override bool UsesOverheadSprint => true;
    // Forced-critical finisher pipeline: exact, non-configurable damage keeps the state transition
    // legible. First hit from full health leaves 60; the next landed hit starts the six-second window.
    protected override int BasicAttackDamage => 40;
    protected override float FinisherChance => (Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawFinisherChancePercent?.Value ?? 75) / 100f;
    protected override int FinisherBodyCount => 3;
    protected override bool UsesPullStart => true;
    protected override bool BasicActionsEnabled => true;
    protected override bool FinishersEnabled => IsChainsawFinisherVictimPoolReady();
    protected override bool UsesForcedCriticalFinisherGate => true;
    // 2026-07-23 (Lawson): damage sound switched back to the PlayerHit pool now that it holds
    // the two Adobe-enhanced v2 takes (player_hit_01..02). Wall impacts remain on the
    // ChainsawWallImpact cue for wall/stall hits only.
    protected override Y4NGZCompany.Escalations.Bundy.BundyAudioCue BasicAttackDamageAudioCue => Y4NGZCompany.Escalations.Bundy.BundyAudioCue.PlayerHit;
    protected override float BasicAttackDamageAudioVolume => 1f;
    protected override Y4NGZCompany.Escalations.Bundy.BundyAudioCue FootstepAudioCue => Y4NGZCompany.Escalations.Bundy.BundyAudioCue.ChainsawFootstep;
    protected override Y4NGZCompany.Escalations.Bundy.BundyAudioCue? FinisherDamageAudioCue => Y4NGZCompany.Escalations.Bundy.BundyAudioCue.ChainsawFinisherDamage;
    // #293: the pull start plays its authored cord-yank/catch sequence, and the running loop
    // enters under its tail (see the pull-start latch in BundyAIBase.Audio). The generic rev is
    // the fallback for installs whose audio set predates the catch clips.
    protected override Y4NGZCompany.Escalations.Bundy.BundyAudioCue? PullStartAudioCue =>
        Y4NGZCompany.Escalations.Bundy.BundyAudioCatalog.HasAny(Y4NGZCompany.Escalations.Bundy.BundyAudioCue.ChainsawPullStart)
            ? Y4NGZCompany.Escalations.Bundy.BundyAudioCue.ChainsawPullStart
            : Y4NGZCompany.Escalations.Bundy.BundyAudioCue.ChainsawRev;
    // Saw state machine: RUNNING is a HUNT-only tell. TRACK/SEARCH/PING and wall stalls use
    // the distant idle loop; base forces the replicated cue swap at perception state edges.
    protected override Y4NGZCompany.Escalations.Bundy.BundyAudioCue? MovementLoopAudioCue =>
        IsSawStalled || ShouldUseIdleChainsawLoop
            ? Y4NGZCompany.Escalations.Bundy.BundyAudioCue.ChainsawIdle
            : Y4NGZCompany.Escalations.Bundy.BundyAudioCue.ChainsawRunningLoop;
    protected override bool UsesPerceptionAcquireRev => true;
    protected override bool UsesSawStallCycle =>
        Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawSawStallCycle?.Value ?? true;
    protected override bool UsesMovementLoopSwingModulation => true;
    protected override bool KeepMovementLoopAudioUntilBasicAttackImpact => true;
    protected override bool UsesFiniteMovementLoopAudioWindow => true;
    protected override bool MovementLoopAudioClipLoops => true;
    protected override bool StartsMovementLoopAudioOnPullStart => true;
    protected override bool RequiresActiveMovementLoopAudioForAttacks => true;
    protected override float MovementLoopAudioWindowFallbackSeconds => 45f;
    protected override float PostBasicAttackWalkSeconds => 3f;
    protected override bool UsesManualTargetFacing => true;
    protected override bool ManualPursuitControlsAgentRotation => true;
    protected override bool ManualPursuitFacingWhileMoving => false;
    protected override float ManualPursuitFacingDistance => 4f;
    // Smooth root facing while he is close or maneuvering; the 8-way locomotion tree handles
    // backpedal/strafe visuals without needing separate turn one-shots.
    protected override float ManualPursuitTurnSpeedDegrees => 320f;
    // 2026-07-09 transition-smoothing pass: these were 2160/1080 deg/s, which read as instant
    // snaps — after a lunge overshoot Bundy would snap 180° to the victim during recovery, then
    // whip 180° back when the nav agent reclaimed rotation toward a stale path heading. 540/420
    // keep attacks responsive (180° in ~0.33s) while the turns read as motion, not teleports.
    protected override float ManualActionTurnSpeedDegrees => 540f;
    protected override float AgentAngularSpeedDegrees => 420f;
    protected override float AgentAcceleration => 24f;
    protected override bool TurnClipsEnabled => true;
    // The clean direct-sampled controller includes the source-pack turn one-shots. Interrupting
    // movement briefly keeps root facing and the visual turn clip in sync when Bundy reorients.
    protected override bool TurnClipsCanInterruptMovement => true;
    protected override float TurnClipMaxDistance => 12f;
    // The source chainsaw locomotion clips are authored in a bladed stance. Keep the correction
    // config-driven and locomotion-only so attacks/finishers still use the raw pack alignment.
    protected override float VisualYawCorrectionDegrees => Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawVisualYawCorrection?.Value ?? 0f;
    protected override float RunVisualYawCorrectionDegrees => Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawRunVisualYawCorrection?.Value ?? VisualYawCorrectionDegrees;
    protected override float RunVisualRollCorrectionDegrees => Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawRunVisualRollCorrection?.Value ?? 0f;
    // Stage 2: attack-while-moving lunge. Starts the swing at ~5m and surges in fast; legs run via the
    // LungeLowerBody override layer (clamp raised so they keep up at lunge speed ~11 / run cadence 3.6).
    protected override bool UsesDirectionalLocomotion => true;
    protected override float DirectionalLocomotionAxisYawDegrees => SourcePackWalkLocomotionAxisYawDegrees;
    protected override float DirectionalRunLocomotionAxisYawDegrees => SourcePackRunLocomotionAxisYawDegrees;
    protected override bool UsesObservedVelocityIdleDemotion =>
        Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawVelocityGatedIdleDemotion?.Value ?? true;
    // Kept config-driven for the first R39b field test. The approved controller's directional
    // locomotion clips are now squared, so false safely restores true 8-way blending; true retains
    // the shipped forward-only behavior as a conservative A/B fallback.
    protected override bool LockDirectionalLocomotionToForward =>
        Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawLockLocomotionDirection?.Value ?? true;
    protected override bool UsesCombatManeuvers => true;
    // Hit-reaction staggers + intimidation taunt (wire the previously-unused stagger/taunt clips).
    protected override bool StaggersEnabled => true;
    // Job 2e: re-enabled. The "weightless balloon" read was never the clip's arm speed on its own — the
    // prop is parented to the LEFT hand, and BundyChainsawHandIK.ClassifyClipName used to file
    // Chainsaw_Taunt_Stomp under "blocked" (it matched neither the Chainsaw_Idle/Walk/Run/Turn locomotion
    // prefixes nor the *_Attack_ prefixes), so the right-hand grip solver ran at weight 0 for the entire
    // emote and the saw was flung around one-handed. Every other upper-body-active state holds the grip:
    // 0.92 in locomotion, 0.72-0.86 during basic attacks. ClassifyClipName now matches Chainsaw_Taunt into
    // the locomotion bucket, so the taunt keeps both hands on the tool and reads with weight.
    // Note this flag never actually suppressed the clip in play — BlockedTargetTauntsEnabled below is true
    // and PlayEmote(TauntEmoteIndex) fires from the blocked-pursuit path regardless, so the bad-looking
    // taunt was already shipping on an 8s cooldown; the classifier fix repairs that path too.
    protected override bool TauntsEnabled => true;
    protected override bool BlockedTargetTauntsEnabled => true;
    protected override bool UsesBatterySabotageDiversion => !IsRigLabMode;
    protected override void PlayStaggerAnimation(int staggerType) => PlayStagger(staggerType);
    protected override bool UsesAttackLunge => true;
    protected override float AttackLungeSpeed => Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawLungeSpeed?.Value ?? 11f;
    protected override float LocomotionSpeedMultiplierMax => 3.5f;
    protected override bool RequiresPhysicalBasicAttackContact => true;
    protected override float BasicAttackContactForwardOffset => 1.2f;
    protected override float BasicAttackContactVerticalOffset => 1.05f;
    protected override float AttackActiveLeadSeconds => 0.16f;
    protected override float AttackActiveTrailSeconds => 0.22f;
    protected override bool UsesBasicAttackVisualExit => IsRigLabMode;
    // R39 A1: config-backed for live dialing at the attack exit seam. Defaults preserve the shipped
    // 0.04 s blend exactly.
    protected override float BasicAttackVisualExitBlendSeconds =>
        Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawAttackVisualExitBlendSeconds?.Value ?? 0.04f;
    // R39 A2: 0/unset keeps the legacy same-frame snap at lunge commit; >0 rate-limits the commit
    // turn (540 matches ManualActionTurnSpeedDegrees, ~0.28 s for a 150-degree correction).
    protected override float LungeCommitFacingTurnSpeedDegrees
    {
        get
        {
            float configured = Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawLungeFacingMaxSpeed?.Value ?? 0f;
            return configured > 0f ? configured : float.PositiveInfinity;
        }
    }
    // Tightened from 1.15: damage only when the blade is genuinely on the player (was catching nearby misses).
    protected override float BasicAttackContactRadius => 0.6f;
    // Job 2a: blade-derived lunge standoff, config-backed for live dialing. 0/unset keeps the base
    // derivation (BasicAttackContactForwardOffset + BasicAttackContactRadius * 0.5 = 1.2 + 0.3 = 1.5m),
    // which puts the player half a contact radius inside the 1.8m contact sphere on arrival instead of
    // at its outer edge. This is deliberately NOT an AttackLungeStopDistance override — that value has
    // three other consumers (the ShouldRunAtDistance gait threshold below, HasClearLungePathTo's
    // spherecast length, and UpdateManualTargetFacing's hand-off) that must keep 1.0.
    protected override float AttackLungeBladeStandoff
    {
        get
        {
            float configured = Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawLungeBladeStandoff?.Value ?? 0f;
            return configured > 0f ? configured : base.AttackLungeBladeStandoff;
        }
    }
    // Job 2a: the swing fraction at which the lunge freezes its aim AND the fraction its arrival is
    // timed to. Shipped 0.85 replaces the old hard-coded 0.5 arrival, which had him finishing his
    // motion ~0.6s before the blade landed.
    protected override float AttackLungeCommitFraction =>
        Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawLungeCommitFraction?.Value ?? 0.85f;
    // Job 2b: the wind-up hold that makes the swing's 1.17-1.55s impact lead readable. Only the
    // chainsaw opts in — the base default stays 0 so no other variant inherits a timing change from a
    // telegraph tuned to this one's clips.
    protected override float AttackLungeWindupHoldSeconds =>
        Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawLungeWindupHoldSeconds?.Value ?? 0.30f;
    protected override float BasicAttackContactLatchSeconds => 0.55f;

    protected override bool ShouldRunAtDistance(GameNetcodeStuff.PlayerControllerB target, float distance)
    {
        // Run while actively chasing a target right up to the lunge, so he never drops to a slow walk
        // between run range and lunge range — he begins the lunge straight out of the run. He only
        // walks at point-blank (inside the lunge stop distance).
        return distance > AttackLungeStopDistance + 0.5f;
    }

    // #281: the chain attack alternates the two single swings, opener first. RigLab keeps its
    // curated left-swing-only diet (RightSwing contact windows were measured ~80-90 degrees off).
    private static readonly int[] ChainAttackSwingPattern = { LeftSwingAttackIndex, 1 };
    protected override int[] ChainAttackPattern => ChainAttackSwingPattern;
    protected override float ChainAttackChance => IsRigLabMode ? 0f : 0.3f;
    // #281: reps 2+ crossfade from the live pose into the next swing. Handoff at 0.70 of the
    // 2.333s single-swing clip (~1.63s) sits well past its 1.17s contact; entering the next clip
    // 0.15 in (~0.35s) skips the neutral wind-in so the pair reads as one figure-8.
    protected override float ChainAttackHandoffClipFraction =>
        Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawChainAttackHandoffFraction?.Value ?? 0.70f;
    protected override float ChainAttackEntryOffsetClipFraction =>
        Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawChainAttackEntryOffsetFraction?.Value ?? 0.15f;
    protected override float ChainAttackCrossFadeSeconds =>
        Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawChainAttackBlendSeconds?.Value ?? 0.30f;
    // #282: the spin loop repeats FullSpinSwing (3.10s clip); re-trigger at ~0.8 of the clip so
    // each revolution mostly completes before the next blends in. Rolled only when surrounded or
    // at point-blank (see RollComboAttack).
    protected override int SpinLoopAttackIndex => 2;
    protected override float SpinLoopChance => IsRigLabMode ? 0f : 0.25f;
    protected override float SpinLoopRepIntervalSeconds => 2.48f;

    protected override int SelectBasicAttackIndex(int nearbyLivingPlayerCount, int lastAttackIndex)
    {
        if (IsRigLabMode)
            return SelectFromAttackSet(RigLabCuratedBasicAttackIndices, lastAttackIndex);

        int[] attackIndices = nearbyLivingPlayerCount >= 2
            ? CrowdBasicAttackIndices
            : SingleTargetBasicAttackIndices;

        return SelectFromAttackSet(attackIndices, lastAttackIndex);
    }

    private static int SelectFromAttackSet(int[] attackIndices, int lastAttackIndex)
    {
        if (attackIndices == null || attackIndices.Length == 0)
            return LeftSwingAttackIndex;

        if (lastAttackIndex < 0)
            return attackIndices[0];

        for (int i = 0; i < attackIndices.Length; i++)
        {
            if (attackIndices[i] == lastAttackIndex)
                return attackIndices[(i + 1) % attackIndices.Length];
        }

        return attackIndices[0];
    }

    protected override float GetBasicAttackStartRange(float baseRange)
    {
        // Stage 2: the lunge lets him start the swing from ~5m and surge in to connect.
        return UnityEngine.Mathf.Max(baseRange + 0.2f, Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawAttackStartRange?.Value ?? 5f);
    }

    protected override float GetBasicAttackImpactDelay(int attackIndex)
    {
        switch (attackIndex)
        {
            case 0: // Chainsaw_Attack_LeftSwing: contact at ~0.50 normalized of 2.333s.
            case 1: // Chainsaw_Attack_RightSwing: contact at ~0.50 normalized of 2.333s.
                return ChainsawSingleSwingContactSeconds;
            case 2: // Chainsaw_Attack_FullSpinSwing: contact at ~0.50 normalized of 3.10s.
                return ChainsawFullSpinContactSeconds;
            case 3: // Chainsaw_Attack_DoubleSwing: contact at ~0.42 normalized of 3.50s.
                return ChainsawDoubleSwingContactSeconds;
            default:
                return base.GetBasicAttackImpactDelay(attackIndex);
        }
    }

    protected override float GetBasicAttackClipLengthSeconds(int attackIndex)
    {
        switch (attackIndex)
        {
            case 0: // Chainsaw_Attack_LeftSwing
            case 1: // Chainsaw_Attack_RightSwing
                return ChainsawSingleSwingClipSeconds;
            case 2: // Chainsaw_Attack_FullSpinSwing
                return ChainsawFullSpinClipSeconds;
            case 3: // Chainsaw_Attack_DoubleSwing
                return ChainsawDoubleSwingClipSeconds;
            default:
                return base.GetBasicAttackClipLengthSeconds(attackIndex);
        }
    }

    protected override float GetBasicAttackRecoverDelay(int attackIndex)
    {
        switch (attackIndex)
        {
            case 0:
            case 1:
                return ChainsawSingleSwingRecoverSeconds;
            case 2:
                return ChainsawFullSpinRecoverSeconds;
            case 3:
                return ChainsawDoubleSwingRecoverSeconds;
            default:
                return base.GetBasicAttackRecoverDelay(attackIndex);
        }
    }

    protected override float GetBasicAttackVisualExitNormalizedTime(int attackIndex)
    {
        switch (attackIndex)
        {
            case 0: // Chainsaw_Attack_LeftSwing exits before the controller's 0.85 -> Idle blend.
            case 1: // Chainsaw_Attack_RightSwing exits before the controller's 0.85 -> Idle blend.
                return Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawAttackVisualExitSwingThreshold?.Value ?? 0.82f;
            case 2: // Chainsaw_Attack_FullSpinSwing has the same generated transition shape.
            case 3: // Chainsaw_Attack_DoubleSwing has the same generated transition shape.
                return Y4NGZCompany.Bootstrap.BundyPlugin.ModConfig?.ChainsawAttackVisualExitHeavyThreshold?.Value ?? 0.83f;
            default:
                return base.GetBasicAttackVisualExitNormalizedTime(attackIndex);
        }
    }

    protected override void PlayFinisherAnimation(int entryIndex, int bodyIndex)
    {
        PlayFinisher(bodyIndex);
    }

    protected override void PlayPullStartAnimation()
    {
        PlayPullStart();
    }

    protected override bool IsPairedFinisherVictimReady(GameNetcodeStuff.PlayerControllerB victim, int entryIndex, int bodyIndex)
    {
        return IsChainsawFinisherVictimReady(bodyIndex);
    }

    /// <summary>
    /// #321 (C6): the base no-repeat pick assumed all three variants can play. When one cannot —
    /// its clip missing from the animationbundle, or its interaction refused by the API — picking it
    /// produced a finisher where the victim animated nothing, which reads in-game as "only one
    /// finisher ever plays". Skip proven-dead body indices instead, keeping the no-repeat rotation
    /// across whatever is left.
    /// </summary>
    protected override int SelectFinisherBodyIndex(int lastBodyIndex)
    {
        return SelectAvailableChainsawFinisherBodyIndex(base.SelectFinisherBodyIndex(lastBodyIndex));
    }

    protected override bool TryBeginPairedFinisher(GameNetcodeStuff.PlayerControllerB victim, int entryIndex, int bodyIndex)
    {
        return BeginChainsawFinisherVictim(victim, bodyIndex);
    }

    protected override void ReleasePairedFinisher(GameNetcodeStuff.PlayerControllerB victim, int entryIndex, int bodyIndex, string reason)
    {
        ReleaseChainsawFinisherVictim(victim, bodyIndex, reason);
    }

    protected override bool IsPairedFinisherVictimActive(GameNetcodeStuff.PlayerControllerB victim)
    {
        return IsChainsawFinisherVictimActive(victim, _activeFinisherBodyIndex);
    }

    private void PlayFinisher(int bodyIndex)
    {
        SetAnimatorIntegerThenTrigger(
            Y4NGZCompany.Escalations.Bundy.AI.BundyAnimatorParams.FinisherBody,
            bodyIndex,
            Y4NGZCompany.Escalations.Bundy.AI.BundyAnimatorParams.DoFinisher);
    }

    private void PlayPullStart()
    {
        SetAnimatorTriggerOnly(Y4NGZCompany.Escalations.Bundy.AI.BundyAnimatorParams.PullStart);
    }

    protected void PlayStagger(int staggerType)
    {
        SetAnimatorIntegerThenTrigger(
            Y4NGZCompany.Escalations.Bundy.AI.BundyAnimatorParams.StaggerType,
            staggerType,
            Y4NGZCompany.Escalations.Bundy.AI.BundyAnimatorParams.DoStagger);
    }
}
