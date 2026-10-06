using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.AI;

// Issue #205: Chainsaw_Sprint_Overhead, Bundy's third gait. A looping locomotion cycle, not an
// attack — saw raised overhead, held until he can reach a basic attack.
//
// WHAT PROMOTES HIM (Lawson, 2026-08-01), and neither of these is a distance band:
//   1. He is knocked down, gets up, taunts, and charges. The knockdown-recovery taunt already exists
//      (Animation.cs QueueKnockdownRecoveryTaunt, armed at knockdown start and fired from every
//      recovery path); this hangs off its completion. Deliberately NOT distance-gated: knockdowns are
//      earned by accumulated damage, so the player who put him down is usually at shovel or shotgun
//      range, and a distance floor would stop the primary trigger ever firing.
//   2. Occasionally, when he has gone a long time without LANDING a hit on his target. Missed swings
//      do not count as contact — that is the frustration the sprint answers. Chance-rolled rather
//      than on a timer so it does not become a metronome, and distance-gated, because that trigger
//      has no dramatic beat to justify a sprint that has no room to play.
//
// WHY IT IS A THREE-PHASE MACHINE AND NOT A BOOL
// -----------------------------------------------
// The gait ships as three clips (loopTime is a per-clip import setting and only three clips can have
// it on for the loop and off for the ends). Their seams are exact by construction — the intro's last
// frame IS the loop's first, and the outro's first frame IS the loop's key two before its wrap — so
// the outro is enterable at exactly one phase per lap. The AI leaves the sprint asynchronously, so
// dropping the animator bool does not end the sprint; it starts a WIND-DOWN that lasts until the
// animator has walked out through the authored outro.
//
// That wind-down is why basic attacks are held off during it: the outro was authored INTO an
// attack's opening pose, and every attack enters from AnyState (measured:
// BundyControllerGraphReport, 2026-08-01), so an attack fired mid-outro would cut it. The hold is
// bounded by the loop's remaining lap plus the outro, ~0.6 s typical and ~0.93 s worst case, and
// SprintWindDownMaxSeconds is the backstop that stops a wedged wind-down starving his attacks.
//
// ARRIVAL is the exception (#208, Lawson 2026-08-05): stopping to attack defeated the gait's whole
// point of hitting fast players AT speed. A RUNNING sprint whose target is reachable converts
// straight into the swing — AnyState supersedes the loop for the upper body, the LungeLowerBody
// override layer keeps the legs running at his carried speed, and the abandon in BeginTimedAction
// ends the sprint (with cooldown) so exactly one swing plays and the sequence is over. The outro
// now owns only the sprints that end WITHOUT an attack (target lost, duration cap, force-toggle
// cleared, or an arrival whose attack attempt failed its own gates).
//
// SPEED IS NOT COSMETIC HERE. The clips are in-place, like every shipped Bundy clip: the stance foot
// is authored stationary in ARMATURE space, so it only stops skating in world when the agent moves at
// the speed the cycle was derived for. 7.65 m/s is that speed. ApplyGaitSpeed (Locomotion.cs) is the
// single choke point that guarantees it, because agent.speed is assigned from nine places.
public abstract partial class BundyAIBase
{
    protected enum SprintPhase
    {
        None,
        // Sprinting. `Sprinting` is true and the animator is in Sprint_Intro or Sprint_Loop.
        Running,
        // The AI has left the sprint; `Sprinting` is false and the animator is finishing the lap and
        // playing Sprint_Outro. Attacks are held off until this ends.
        WindingDown,
    }

    private SprintPhase _sprintPhase = SprintPhase.None;
    private float _sprintStartedAt;
    private float _sprintWindDownStartedAt;
    private float _nextSprintAllowedAt;
    private float _nextSprintRollAt;
    private float _lastLandedHitAt = float.NegativeInfinity;
    private bool _sprintPendingFromTaunt;
    private bool _sprintForcedByDebug;
    private bool _sprintIntroTravelLogged;
    private Vector3 _sprintIntroStartPosition;
    private int _hasSprintingParameter = -1;
    private bool _warnedSprintingParameterMissing;
    private string _sprintEntryReason = string.Empty;

    // The wind-down cannot outlast one loop lap plus the outro. This is a backstop for the case the
    // animator never entered a sprint state at all (an older bundle, or a stagger that stole the
    // Base Layer mid-sprint), so the attack hold can never latch on.
    private const float SprintWindDownMaxSeconds = 2f;

    /// <summary>
    /// Whether the SPRINT's authored speed should be on the agent, which is not the same question as
    /// whether the AI still wants to sprint. The loop is authored at 7.65 m/s and the outro at 2.43
    /// in armature units — 4.25 m/s once the 1.75x animator scale is applied, i.e. exactly the run.
    /// So the handover happens when the animator actually reaches the outro, not when the AI decided
    /// to leave: dropping to run speed at the decision would leave the loop's remaining lap, which is
    /// still a 7.65 m/s cycle, sliding for up to 0.6 s.
    ///
    /// agent.speed is a ceiling rather than a command, so holding it high does not force an overshoot
    /// — the agent's own arrival deceleration still slows him near the destination, and the Speed
    /// multiplier scales the clip to whatever velocity that produces.
    /// </summary>
    private bool IsSprintDrivingGroundSpeed()
    {
        if (_sprintPhase == SprintPhase.Running)
            return true;

        return _sprintPhase == SprintPhase.WindingDown && !IsAnimatorInSprintOutro();
    }

    // True while the sprint owns the gait in any phase. Since #208 this is no longer an absolute
    // attack hold: TryBeginAttackOpportunity pairs it with IsSprintArrivalAttackReady below.
    private bool IsSprintHoldingAttacks() => _sprintPhase != SprintPhase.None;

    /// <summary>
    /// #208: whether a RUNNING sprint should convert into the swing right now instead of holding it.
    /// Arrival is the one sprint exit that must NOT wait for the outro — the swing fires while the
    /// lower body is still at sprint speed (the lunge lower-body layer carries the legs), and the
    /// sprint is abandoned by BeginTimedAction as the attack starts. Every other exit keeps the
    /// authored wind-down/outro, which is why WindingDown always answers false here.
    /// </summary>
    private bool IsSprintArrivalAttackReady(PlayerControllerB huntTarget)
    {
        if (_sprintPhase != SprintPhase.Running)
            return false;

        if (!IsLivingPlayer(huntTarget) || !CanUseLiveHuntTarget(huntTarget))
            return false;

        return GetCurrentHuntDistance(huntTarget) <= GetBasicAttackStartRange(BasicAttackRange);
    }

    // Chainsaw_Sprint_Overhead_Intro is 30 keys at 30 fps, so 29 frame intervals = 0.967 s, and its
    // root curve carried 2.11 m of authored travel before the export flattened it — 3.69 m once the
    // 1.75x animator scale is applied. Measured, not assumed: BundyClipExportProof,
    // artifacts/bundy/bundy_clip_export_proof.md.
    private const float SprintIntroSeconds = 29f / 30f;
    private const float SprintIntroAuthoredMetres = 3.69f;

    /// <summary>
    /// Issue #207. The ground speed the sprint's clip is authored for RIGHT NOW.
    ///
    /// The loop is a constant 7.65 m/s cycle, but the INTRO is an authored acceleration ramp, and
    /// giving it the loop's speed is what made him skate out of the gate — Lawson, 2026-08-01: "he
    /// should basically be not moving at all during the intro portion of the animation."
    /// `author_bundy_sprint_set.py` builds the intro's per-frame speeds as `top * k / (frames - 1)`,
    /// i.e. LINEAR in the clip's own time, so the authored speed at normalized time t is exactly
    /// `SprintSpeed * t`. Integrating that over 0.967 s gives 3.70 m against the 3.69 m of root
    /// travel the export proof measured off the clip itself: this ramp IS the clip, not a fit to it.
    ///
    /// It is a CEILING rather than a command, so entering the sprint from a run spends the first
    /// ~0.15 s decelerating into the ramp at AgentAcceleration instead of snapping onto it. The
    /// alternative — holding the entry speed as a floor — was not taken, because the clip opens from
    /// the pack's idle and a floor would leave the legs mismatched for exactly the window this fixes.
    /// </summary>
    private float GetSprintGroundSpeed()
    {
        if (!SprintIntroSpeedRampEnabled)
            return SprintSpeed;

        return SprintSpeed * GetSprintIntroRampProgress();
    }

    /// <summary>
    /// How far through the intro's authored ramp the gait is, 0..1.
    ///
    /// The animator is the authority whenever the intro owns — or is blending in to own — the Base
    /// Layer. During the 0.12 s entry blend the CURRENT state is still Idle/Walk/Run while the intro
    /// is the NEXT one with its own normalizedTime already advancing from 0, so reading only the
    /// current state would hold him at full speed for the blend and then drop him to zero, which is
    /// worse than the bug. normalizedTime is clamped because a non-looping state keeps counting past
    /// 1 while it waits on its exit-time transition.
    ///
    /// Everything else falls back to a wall clock off the sprint's own start. That covers the two
    /// cases that are not the intro and must not be slowed: the loop and the wind-down's remaining
    /// lap, both already past 0.967 s and so clamped to 1; and an animator that never reached the
    /// sprint states at all (an older bundle with no `Sprinting` parameter), which would otherwise
    /// pin him at zero speed for the whole of SprintMaxSeconds.
    /// </summary>
    private float GetSprintIntroRampProgress()
    {
        if (creatureAnimator != null)
        {
            AnimatorStateInfo current = creatureAnimator.GetCurrentAnimatorStateInfo(0);
            if (current.IsName(SprintIntroStateName))
                return Mathf.Clamp01(current.normalizedTime);

            if (creatureAnimator.IsInTransition(0))
            {
                AnimatorStateInfo next = creatureAnimator.GetNextAnimatorStateInfo(0);
                if (next.IsName(SprintIntroStateName))
                    return Mathf.Clamp01(next.normalizedTime);
            }
        }

        if (_sprintPhase == SprintPhase.None)
            return 1f;

        return Mathf.Clamp01((Time.time - _sprintStartedAt) / SprintIntroSeconds);
    }

    /// <summary>
    /// Records that a hit actually LANDED on a player. The no-contact trigger measures from here, not
    /// from the last swing: a Bundy who keeps swinging and missing is exactly the one who should
    /// eventually charge.
    /// </summary>
    private void NoteLandedHitOnPlayer()
    {
        _lastLandedHitAt = Time.time;
    }

    /// <summary>
    /// Called from CompleteAction. The knockdown-recovery taunt is the primary trigger's seam, and the
    /// sprint starts when that taunt's action ends rather than when it begins: starting it earlier
    /// would put the agent at sprint speed while the taunt still holds the root.
    /// </summary>
    private void OnActionCompletedForSprint(ActionKind completedKind)
    {
        // Any sprint that was live when this action began was already abandoned at BeginTimedAction;
        // this hook exists only to start the taunt-triggered one.
        if (completedKind != ActionKind.Emote || !_sprintPendingFromTaunt)
            return;

        _sprintPendingFromTaunt = false;
        if (!CanEnterSprint(requireDistance: false))
            return;

        BeginSprint("knockdown recovery taunt");
    }

    /// <summary>
    /// Owner-side tick, called from DoAIInterval after the attack and drop-jump opportunities and
    /// before the pursuit resolver — so a swing that is already available always beats starting a
    /// sprint, and the resolver that follows sees the gait's speed for this interval.
    /// </summary>
    private void UpdateSprintState(PlayerControllerB huntTarget)
    {
        if (!UsesOverheadSprint || !IsOwner)
            return;

        if (_sprintPhase == SprintPhase.WindingDown)
        {
            if (!IsAnimatorInSprintState() || Time.time - _sprintWindDownStartedAt > SprintWindDownMaxSeconds)
                FinishSprintWindDown();
            return;
        }

        if (_sprintPhase == SprintPhase.Running)
        {
            string exit = GetSprintExitReason(huntTarget);
            if (exit != null)
                EndSprint(exit);
            return;
        }

        TryBeginPeriodicSprint(huntTarget);
    }

    private void TryBeginPeriodicSprint(PlayerControllerB huntTarget)
    {
        if (!CanEnterSprint(requireDistance: true))
            return;

        if (ForceSprintEnabled)
        {
            _sprintForcedByDebug = true;
            BeginSprint("forced (Debug/ChainsawForceSprint)");
            return;
        }

        if (Time.time < _nextSprintAllowedAt)
            return;

        // "Extended period without contact" is measured from the last LANDED hit. _lastLandedHitAt
        // starts at -infinity, so a Bundy who has never connected is eligible as soon as he has been
        // chasing long enough, which is the intended reading of the trigger rather than an edge case.
        if (Time.time - _lastLandedHitAt < Mathf.Max(0f, SprintNoContactSeconds))
            return;

        if (Time.time < _nextSprintRollAt)
            return;

        _nextSprintRollAt = Time.time + Mathf.Max(1f, SprintRollIntervalSeconds);
        float chance = Mathf.Clamp01(SprintChance);
        if (chance <= 0f || UnityEngine.Random.value > chance)
            return;

        BeginSprint(float.IsNegativeInfinity(_lastLandedHitAt)
            ? "never landed a hit"
            : $"no contact for {Time.time - _lastLandedHitAt:F0}s");
    }

    /// <summary>
    /// The gates both triggers share. <paramref name="requireDistance"/> is false for the knockdown
    /// taunt, which charges from wherever the player is.
    /// </summary>
    private bool CanEnterSprint(bool requireDistance)
    {
        if (!UsesOverheadSprint || !IsOwner || isEnemyDead || _sprintPhase != SprintPhase.None)
            return false;

        if (IsActionLocked() || IsDropJumpActive() || IsTurnClipRootLockActive() || _lungeActive)
            return false;

        if (!RoundReady() || !AgentReadyForMovement())
            return false;

        PlayerControllerB huntTarget = GetCurrentHuntTarget();
        if (!IsLivingPlayer(huntTarget) || !CanUseLiveHuntTarget(huntTarget))
            return false;

        // The gait cannot corner: it is a single forward cycle with no directional blend tree, unlike
        // Walk/Run. Sprinting while the path is turning hard reads as a strafe at 7.65 m/s.
        float distance = GetCurrentHuntDistance(huntTarget);
        if (distance <= GetBasicAttackStartRange(BasicAttackRange))
            return false;

        if (requireDistance && !ForceSprintEnabled && distance < Mathf.Max(0f, SprintMinDistanceMeters))
            return false;

        return true;
    }

    /// <summary>
    /// Null while the sprint should continue; otherwise why it is ending. Distance is the load-bearing
    /// one — "held until he can reach a basic attack" is the brief, and reaching attack range is what
    /// that means spatially. The attack cooldown deliberately does NOT extend the sprint: he would
    /// orbit the player at 7.65 m/s waiting for it.
    /// </summary>
    private string GetSprintExitReason(PlayerControllerB huntTarget)
    {
        if (isEnemyDead)
            return "dead";

        if (IsActionLocked() || IsDropJumpActive() || IsTurnClipRootLockActive())
            return "action";

        if (Time.time - _sprintStartedAt > Mathf.Max(1f, SprintMaxSeconds))
            return "duration cap";

        if (!IsLivingPlayer(huntTarget) || !CanUseLiveHuntTarget(huntTarget))
            return "target lost";

        // The debug toggle overrides the triggers, not the arrival: the point of forcing a sprint is
        // to watch the whole authored arc, outro included.
        //
        // #208: the normal arrival never reaches this line any more — TryBeginAttackOpportunity runs
        // earlier in DoAIInterval and converts a reachable RUNNING sprint into the swing directly.
        // Reaching here at attack range means that attempt failed its own gates (attack cooldown, no
        // complete path, blocked lunge), so fall back to the authored wind-down/outro exit.
        float distance = GetCurrentHuntDistance(huntTarget);
        if (distance <= GetBasicAttackStartRange(BasicAttackRange))
            return $"attack reachable at {distance:F1}m";

        if (_sprintForcedByDebug && !ForceSprintEnabled)
            return "force toggle cleared";

        return null;
    }

    private void BeginSprint(string reason)
    {
        _sprintPhase = SprintPhase.Running;
        _sprintStartedAt = Time.time;
        _sprintEntryReason = reason;
        _sprintIntroTravelLogged = false;
        _sprintIntroStartPosition = transform.position;
        SetSprintingAnimatorFlag(true);
        LogInfo(
            BundyLogCategory.Anim,
            0,
            $"[BundySprint] ENTER reason={reason} speed={SprintSpeed:F2}m/s " +
            $"dist={GetCurrentHuntDistance(GetCurrentHuntTarget()):F1}m");
    }

    /// <summary>
    /// Leaves the sprint the way the clip set was authored to be left: drop the flag and let the
    /// animator finish its lap into the outro. Attacks stay held off until it has.
    /// </summary>
    private void EndSprint(string reason)
    {
        if (_sprintPhase != SprintPhase.Running)
            return;

        _sprintPhase = SprintPhase.WindingDown;
        _sprintWindDownStartedAt = Time.time;
        _sprintForcedByDebug = false;
        _nextSprintAllowedAt = Time.time + Mathf.Max(0f, SprintCooldownSeconds);
        SetSprintingAnimatorFlag(false);
        LogInfo(
            BundyLogCategory.Anim,
            0,
            $"[BundySprint] WIND-DOWN reason={reason} after {Time.time - _sprintStartedAt:F2}s " +
            $"(entered on '{_sprintEntryReason}'); attacks held until the outro finishes.");
    }

    /// <summary>
    /// Ends the sprint without waiting for an outro that cannot play, because something entering from
    /// AnyState already owns the Base Layer.
    /// </summary>
    private void AbandonSprint(string reason)
    {
        if (_sprintPhase == SprintPhase.None)
            return;

        SprintPhase previous = _sprintPhase;
        _sprintPhase = SprintPhase.None;
        _sprintForcedByDebug = false;
        _nextSprintAllowedAt = Time.time + Mathf.Max(0f, SprintCooldownSeconds);
        SetSprintingAnimatorFlag(false);
        LogInfo(BundyLogCategory.Anim, 1, $"[BundySprint] ABANDONED in {previous}: {reason}.");
    }

    /// <summary>
    /// Issue #207's acceptance is a NUMBER — the intro should put 3.69 m of ground under him, the
    /// travel the export proof measured off the clip's own root curve — so it is measured rather than
    /// eyeballed off a video. One line per sprint, sampled on the same per-frame cadence the ramp
    /// runs at: a DoAIInterval sample would notice the ramp finishing up to 0.2 s late, and at
    /// 7.65 m/s that is 1.5 m of error on a 3.69 m measurement.
    /// </summary>
    private void UpdateSprintIntroTravelSample()
    {
        if (_sprintPhase == SprintPhase.None || _sprintIntroTravelLogged)
            return;

        if (GetSprintIntroRampProgress() < 1f)
            return;

        _sprintIntroTravelLogged = true;
        Vector3 travelled = transform.position - _sprintIntroStartPosition;
        travelled.y = 0f;
        LogInfo(
            BundyLogCategory.Anim,
            1,
            $"[BundySprint] intro ramp complete after {Time.time - _sprintStartedAt:F2}s: " +
            $"{travelled.magnitude:F2}m of ground against the clip's authored {SprintIntroAuthoredMetres:F2}m " +
            $"(ramp={(SprintIntroSpeedRampEnabled ? "on" : "off")}).");
    }

    private void FinishSprintWindDown()
    {
        _sprintPhase = SprintPhase.None;
        LogInfo(
            BundyLogCategory.Anim,
            1,
            $"[BundySprint] outro finished after {Time.time - _sprintWindDownStartedAt:F2}s; attacks released.");
    }

    /// <summary>
    /// The wind-down ends when the Base Layer is no longer in any sprint state — which covers both the
    /// outro completing and something superseding it from AnyState — rather than on a timer that would
    /// have to guess the lap phase the sprint was left at.
    /// </summary>
    private bool IsAnimatorInSprintState()
    {
        if (creatureAnimator == null)
            return false;

        AnimatorStateInfo current = creatureAnimator.GetCurrentAnimatorStateInfo(0);
        if (current.IsName(SprintIntroStateName) || current.IsName(SprintLoopStateName) || current.IsName(SprintOutroStateName))
            return true;

        // Mid-blend the NEXT state is already the truthful answer: the loop hands over to the outro
        // with a zero-length blend, but the entry blends are 0.12 s and would otherwise read as "not
        // in a sprint state" for four frames right after the sprint starts.
        if (!creatureAnimator.IsInTransition(0))
            return false;

        AnimatorStateInfo next = creatureAnimator.GetNextAnimatorStateInfo(0);
        return next.IsName(SprintIntroStateName) || next.IsName(SprintLoopStateName) || next.IsName(SprintOutroStateName);
    }

    /// <summary>
    /// True once the outro owns (or is blending in to own) the Base Layer. With no animator to ask,
    /// answers true: the only caller uses it to decide whether to KEEP sprint speed, and holding a
    /// 7.65 m/s agent on the strength of an animator that does not exist is the wrong default.
    /// </summary>
    private bool IsAnimatorInSprintOutro()
    {
        if (creatureAnimator == null)
            return true;

        if (creatureAnimator.GetCurrentAnimatorStateInfo(0).IsName(SprintOutroStateName))
            return true;

        return creatureAnimator.IsInTransition(0) &&
            creatureAnimator.GetNextAnimatorStateInfo(0).IsName(SprintOutroStateName);
    }

    // Animator contract with the Unity-side BundySprintStatePatch. Those states are entered from
    // Idle/WalkForward/RunForward/Emote_TauntStomp on this bool rather than from AnyState: `Sprinting`
    // stays true for the whole sprint, so an AnyState transition would re-fire every frame of the loop
    // and restart the intro forever.
    private const string SprintIntroStateName = "Sprint_Intro";
    private const string SprintLoopStateName = "Sprint_Loop";
    private const string SprintOutroStateName = "Sprint_Outro";

    private void SetSprintingAnimatorFlag(bool sprinting)
    {
        if (creatureAnimator == null)
            return;

        if (!HasAnimatorBoolParameter(BundyAnimatorParams.Sprinting, ref _hasSprintingParameter))
        {
            WarnOnce(
                ref _warnedSprintingParameterMissing,
                $"Animator bool parameter '{BundyAnimatorParams.Sprinting}' missing; the overhead sprint runs " +
                "at speed with no gait clip. Rebuild the bundle so BundySprintStatePatch can add it.");
            return;
        }

        creatureAnimator.SetBool(BundyAnimatorParams.Sprinting, sprinting);
    }
}
