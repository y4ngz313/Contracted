using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;
using Y4NGZCompany.Escalations.Bundy.HitReactions;
using Y4NGZCompany.Facility.Elevators;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.Bundy.AI;

public abstract partial class BundyAIBase
{
    protected void SetMotionIdle()
    {
        SetMotionState(BundyAnimatorParams.MotionIdle);
    }

    protected void SetMotionWalk()
    {
        SetMotionState(BundyAnimatorParams.MotionWalk);
    }

    protected void SetMotionRun()
    {
        SetMotionState(BundyAnimatorParams.MotionRun);
    }

    protected void SetMotionState(int motionState)
    {
        SetAnimatorInteger(BundyAnimatorParams.MotionState, motionState);
    }

    protected void PlayAttack(int attackIndex)
    {
        SetAnimatorInteger(BundyAnimatorParams.AttackIndex, attackIndex);
        SetAnimatorTrigger(BundyAnimatorParams.DoAttack);
    }

    public void OnBatterySiegeSwingPulse(Vector3 socketWorldPosition)
    {
        if (!IsServer ||
            isEnemyDead ||
            !IsFiniteVector(socketWorldPosition) ||
            IsActionLocked() ||
            IsTurnClipRootLockActive() ||
            _pairedActionRootLockActive)
        {
            return;
        }

        FaceActionPoint(socketWorldPosition);
        int attackIndex = SelectBasicAttackIndex(1, _lastBatterySiegeSwingAttackIndex);
        _lastBatterySiegeSwingAttackIndex = attackIndex;
        _batterySiegeSwingPoseActive = true;

        PlayBatterySiegeSwingClientRpc(attackIndex, socketWorldPosition);
        PlayRandomVocal(BundyAudioCue.AttackVocal);
        PlayRandomBundyAudio(BundyAudioCue.ChainsawWallImpact, ChainsawAudioVolume, 0.92f, 1.06f);
        if (UsesMovementLoopSwingModulation && !_sawStalled)
            PulseChainsawSwellClientRpc(GetBasicAttackImpactDelay(attackIndex) + 0.3f);

        if (DebugLogs)
        {
            LogInfo(
                $"[BundySiege] swing pulse socket={FormatVector(socketWorldPosition)}, " +
                $"attackIndex={attackIndex}");
        }
    }

    public void OnBatterySiegeAnimationStopped()
    {
        if (!IsServer || !_batterySiegeSwingPoseActive)
            return;

        bool resetAnimator = !IsActionLocked() &&
            !IsTurnClipRootLockActive() &&
            !_pairedActionRootLockActive;
        StopBatterySiegeAnimationClientRpc(resetAnimator);
        _batterySiegeSwingPoseActive = false;
    }

    [ClientRpc]
    private void PlayBatterySiegeSwingClientRpc(int attackIndex, Vector3 socketWorldPosition)
    {
        if (isEnemyDead || !IsFiniteVector(socketWorldPosition))
            return;

        FaceActionPoint(socketWorldPosition);
        _batterySiegeSwingPoseActive = true;
        PlayAttack(attackIndex);
    }

    [ClientRpc]
    private void StopBatterySiegeAnimationClientRpc(bool resetAnimator)
    {
        _batterySiegeSwingPoseActive = false;
        if (!resetAnimator || creatureAnimator == null)
            return;

        creatureAnimator.ResetTrigger(BundyAnimatorParams.DoAttack);
        creatureAnimator.CrossFade(
            BaseLayerIdleStateHash,
            Mathf.Max(0.01f, BasicAttackVisualExitBlendSeconds),
            0,
            0f);
    }

    // #281: state hashes for the chain attack's crossfade rep handoff. Short-name hashes, resolved
    // once, same shape as BaseLayerIdleStateHash.
    private static readonly int ChainAttackLeftSwingStateHash = Animator.StringToHash("Atk_Attack_LeftSwing");
    private static readonly int ChainAttackRightSwingStateHash = Animator.StringToHash("Atk_Attack_RightSwing");

    /// <summary>
    /// #281: enters the next chain swing by BLENDING from wherever the previous swing currently is,
    /// partway into the new clip, instead of re-entering it from AnyState at frame 0 (which snapped
    /// him back to the neutral guard pose mid follow-through). Animator.CrossFade* is a local call
    /// that NetworkAnimator does NOT replicate, so the server fans it out here and every peer — host
    /// included — runs the identical blend.
    /// </summary>
    [ClientRpc]
    private void PlayChainAttackSwingCrossFadeClientRpc(int attackIndex, float blendSeconds, float entryOffsetSeconds)
    {
        ApplyChainAttackSwingCrossFade(attackIndex, blendSeconds, entryOffsetSeconds);
    }

    /// <summary>Local half of the chain rep handoff. The server invokes it through the RPC above
    /// (which also runs here on the host); a non-server owner still drives its own animator.</summary>
    protected void ApplyChainAttackSwingCrossFade(int attackIndex, float blendSeconds, float entryOffsetSeconds)
    {
        if (creatureAnimator == null)
            return;

        // A DoAttack still queued from the opener (or from a trigger that never consumed) would let
        // the AnyState transition drive the same state from frame 0 and fight this blend.
        creatureAnimator.ResetTrigger(BundyAnimatorParams.DoAttack);

        int stateHash = attackIndex == 1
            ? ChainAttackRightSwingStateHash
            : ChainAttackLeftSwingStateHash;
        creatureAnimator.CrossFadeInFixedTime(
            stateHash,
            Mathf.Max(0.01f, blendSeconds),
            0,
            Mathf.Max(0f, entryOffsetSeconds));
    }

    private static bool IsFiniteVector(Vector3 value)
    {
        return !float.IsNaN(value.x) &&
            !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) &&
            !float.IsInfinity(value.z);
    }

    protected void PlayEmote(int emoteIndex)
    {
        BeginTimedAction(ActionKind.Emote, targetPlayer, 0f, 0f, 2f);
        SetAnimatorInteger(BundyAnimatorParams.EmoteIndex, emoteIndex);
        SetAnimatorTrigger(BundyAnimatorParams.DoEmote);
    }

    // ---- Hit-reaction staggers -----------------------------------------------------------------
    public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
    {
        // Phase 0 diagnostics: event-driven (one line per registered hit) and DebugLogs-gated.
        int hpBeforeHit = enemyHP;
        bool wasDeadBeforeHit = isEnemyDead;
        base.HitEnemy(force, playerWhoHit, playHitSFX, hitID);
        if (DebugLogs)
        {
            LogInfo(
                $"[BundyHit] force={force} player={PlayerLabel(playerWhoHit)} playHitSFX={playHitSFX} hitID={hitID} " +
                $"IsOwner={IsOwner} IsServer={IsServer} actionKind={_actionKind} behaviourState={currentBehaviourStateIndex} " +
                $"enemyHP={hpBeforeHit}->{enemyHP} wasDead={wasDeadBeforeHit} isDead={isEnemyDead} " +
                $"killedByHit={!wasDeadBeforeHit && isEnemyDead}");
        }
        if (!isEnemyDead)
        {
            if (IsOwner)
                TryEnterNonTargetInterruptFromAttack(playerWhoHit);
            // #288 tier 1: EVERY registered hit flinches, on every client, before the stagger
            // decision below gets a vote. It is additive and never touches the animator state
            // machine, the timed-action system, the agent, or the chain-attack sequence — so a
            // player shooting him mid-combo can always see the hit land without wiping the combo.
            PlayProceduralFlinch(force, playerWhoHit);
            // Vanilla invokes HitEnemy once on every client. Let the server choose one clip and
            // fan it out through the existing audio ClientRpc so the hit cannot double-play.
            // C3.2/C3.3: the Hurt pool belongs to the two NON-knockdown staggers only. A hit that
            // triggers (or lands during) the real knockdown must not bark Hurt on top of the
            // KnockdownScream — the stagger decision therefore runs first and gates the vocal.
            bool knockdown = TriggerStaggerFromDamage(force, playerWhoHit, fromStun: false);
            if (IsServer && !knockdown && !_staggerKnockdownActive)
                PlayRandomVocal(BundyAudioCue.Hurt);
        }
    }

    public override void SetEnemyStunned(bool setToStunned, float setToStunTime = 1f, PlayerControllerB setStunnedByPlayer = null)
    {
        // #288: vanilla can REJECT a stun outright — EnemyAI.SetEnemyStunned drops it when the type
        // is not stunnable at all, or when the 0.5s post-stun invincibility from the previous stun
        // has not expired. Sample the gate BEFORE the base call consumes it. Both inputs tick
        // identically on every client, so this stays a deterministic decision; without it a zap gun
        // held on him would leak 30 bar per rejected re-stun and walk him into a knockdown that
        // never actually stunned him.
        bool stunAccepted = enemyType != null &&
            enemyType.canBeStunned &&
            postStunInvincibilityTimer <= 0f;
        base.SetEnemyStunned(setToStunned, setToStunTime, setStunnedByPlayer);
        if (setToStunned && !isEnemyDead && stunAccepted)
        {
            if (IsOwner)
                TryEnterNonTargetInterruptFromAttack(setStunnedByPlayer);
            // #288: a stun device flinches like anything else, then takes the stun tier — never the
            // knockdown (TriggerStaggerFromDamage clamps the bar to 1 for fromStun hits). Force 1 is
            // the flinch weight only; the bar cost is StaggerBarStunDeviceCost.
            PlayProceduralFlinch(1, setStunnedByPlayer);
            TriggerStaggerFromDamage(0, setStunnedByPlayer, fromStun: true);
        }
    }

    public override void DetectNoise(Vector3 noisePosition, float noiseLoudness, int timesPlayedInOneSpot = 0, int noiseID = 0)
    {
        base.DetectNoise(noisePosition, noiseLoudness, timesPlayedInOneSpot, noiseID);
        if (!IsOwner ||
            !_strangerTargetingActive ||
            _pursuitMode == PursuitMode.NonTargetInterrupt ||
            _actionKind == ActionKind.Stagger ||
            noiseLoudness < StrangerPlayerNoiseMinLoudness ||
            !IsLivingPlayer(_strangerTarget) ||
            Vector3.Distance(transform.position, noisePosition) > HearingRadiusMeters)
        {
            return;
        }

        PlayerControllerB noisePlayer = FindLivingPlayerNearPosition(noisePosition, StrangerNoisePlayerMatchRadius);
        if (noisePlayer != _strangerTarget)
            return;

        // #289: this was the one inference path that wrote the target's EXACT position. Every other
        // one scatters (world noise 8m, pings 12m->4m), and hearing a footstep through two floors is
        // strictly weaker evidence than any of them -- so an unjittered write here was both a hiding
        // leak (a single step gave a pinpoint) and the free restart on the PING->HUNT->TRACK->SEARCH
        // starvation lap. It reuses the world-noise magnitude rather than inventing a constant; the
        // 8s inferred HUNT dwell below is unchanged, he still commits, he just commits to a
        // neighbourhood.
        //
        // Scaled by how far away the noise was, mirroring the world-noise bus's confidence scaling:
        // full jitter at the edge of hearing (a footstep through a floor localises to a region),
        // tapering to nearly nothing at point-blank (a step two metres away localises to a step). Flat
        // 8m would have made corner fights worse than before the fix, which is not what this is for.
        float heardDistance = Vector3.Distance(transform.position, noisePosition);
        float heardJitter = Mathf.Max(0f, WorldNoisePositionNoiseMeters) *
            Mathf.Clamp01(heardDistance / Mathf.Max(1f, HearingRadiusMeters));
        Vector3 heardPosition = ApplyInferredPositionJitter(noisePlayer.transform.position, heardJitter);

        AcquireStrangerContact(
            heardPosition,
            $"heard target noise id={noiseID} loudness={noiseLoudness:F2}",
            StrangerContactQuality.Inferred);
    }

    // ---- #288 reaction-suppression mirror ------------------------------------------------------
    // The problem this exists to solve: vanilla dispatches HitEnemy to EVERY client and each one
    // drives its own animator, but the state that says "this hit must not produce a reaction"
    // (_actionKind, _activeBasicVictimPaired, _sprintArrivalAttackActive) is owner-only and reads as
    // None/false everywhere else. A guard reading it directly therefore suppresses on exactly one
    // machine. Two visible consequences, both of which defeat the ruling they implement:
    //   - the player being run down by a sprint arrival swing watched Bundy recoil out of it on his
    //     own screen while the swing still hit him (#208);
    //   - the victim locked into a paired finisher's first-person camera got additive spine/head
    //     offsets on top of the choreographed pose, and the attacker playable graph evaluates before
    //     LateUpdate so nothing downstream could correct it.
    // Vehicle: the #281 PlayChainAttackSwingCrossFadeClientRpc precedent — the owner computes, the
    // server fans out, every peer (host included) ends up holding the identical mask.
    //
    // Recomputed from a single place rather than pushed from each begin/complete site: the mask is
    // a pure function of state the owner already maintains, so change-detection catches every path
    // — including ones a hand-placed publish call would miss — and only transitions cost an RPC.
    private ReactionSuppression ComputeLocalReactionSuppression()
    {
        ReactionSuppression mask = ReactionSuppression.None;
        if (_actionKind == ActionKind.Finisher ||
            (_actionKind == ActionKind.Attack && _activeBasicVictimPaired))
        {
            mask |= ReactionSuppression.PairedAction;
        }
        if (_sprintArrivalAttackActive && _actionKind == ActionKind.Attack)
            mask |= ReactionSuppression.SprintArrivalAttack;
        return mask;
    }

    /// <summary>
    /// Owner-side. Cheap and idempotent — safe to call from the per-frame tick AND eagerly from the
    /// hot transitions (attack begin, action cleanup) to close the one-frame publish gap on them.
    /// </summary>
    private void MaintainReactionSuppressionMirror()
    {
        if (!IsOwner)
            return;

        ReactionSuppression mask = ComputeLocalReactionSuppression();
        if (_reactionSuppressionPublished && mask == _reactionSuppression)
            return;

        _reactionSuppression = mask;
        _reactionSuppressionPublished = true;
        if (IsServer)
            SetReactionSuppressionClientRpc((byte)mask);
    }

    [ClientRpc]
    private void SetReactionSuppressionClientRpc(byte mask)
    {
        _reactionSuppression = (ReactionSuppression)mask;
        _reactionSuppressionPublished = true;
    }

    private bool IsReactionSuppressed(ReactionSuppression flag) => (_reactionSuppression & flag) != 0;

    // #288: the tier 2/3 half of the hit reaction. Tier 1 (the additive procedural flinch) already
    // played in HitEnemy/SetEnemyStunned and is unconditional; this decides whether the hit ALSO
    // earns a small stagger (bar at or under the stun threshold) or the fall-to-ground knockdown
    // (bar empty). Returns true when this call started (or restarted) the real knockdown, so
    // HitEnemy can keep the Hurt pool off the knockdown's scream.
    private bool TriggerStaggerFromDamage(int force, PlayerControllerB source, bool fromStun)
    {
        if (!StaggersEnabled || creatureAnimator == null)
            return false;

        // Fold the elapsed regen in BEFORE this hit's drain, so a hit that lands the frame the
        // regen delay lapses cannot be evaluated against a stale bar. Runs identically on every
        // client (vanilla replicates HitEnemy with the same force to all of them).
        UpdateStaggerBarRegen();

        float barBefore = _staggerBar;
        float cost = fromStun
            ? Mathf.Max(0f, StaggerBarStunDeviceCost)
            : Mathf.Clamp(Mathf.Max(0, force) * StaggerBarDamagePerForce, StaggerBarDamageMin, StaggerBarDamageMax);
        _staggerBar = Mathf.Max(0f, _staggerBar - cost);
        _lastStaggerBarDamageAt = Time.time;

        // Two floors on the bar, both of which say "this damage counts, but it may not be the hit
        // that puts him down":
        //  - stun devices (grenade / zap gun) are a stun tier by rule, never a knockdown;
        //  - the post-recovery immunity window suppresses tiers 2 AND 3 (below), so letting the bar
        //    sit at 0 through it would fire the knockdown on the very frame it lapsed.
        // Clamping to 1 rather than refilling keeps the banked damage: the next real hit lands the
        // knockdown immediately.
        bool immune = !_staggerKnockdownActive && Time.time < _knockdownImmunityUntil;
        if ((fromStun || immune) && _staggerBar <= 0f)
            _staggerBar = 1f;

        bool large = _staggerBar <= 0f;
        float barAfterDrain = _staggerBar; // logging only: the shared consumption below refills it

        // C3.3 down-hold interaction (defined behavior for "player keeps hitting me while he is
        // down"): while the knockdown is live — falling, held on the floor, or rising — a small
        // hit is ABSORBED (no fresh flinch may yank a grounded man upright; the bar still drained
        // above, and tier 1 still played), and a second EARNED knockdown — i.e. the player emptied
        // the refilled bar all over again while he lay there — RESTARTS the hold instead of
        // replaying the fall from the ground.
        if (_staggerKnockdownActive)
        {
            if (!large)
            {
                if (DebugLogs)
                {
                    LogInfo(
                        $"[BundyStagger] bar={barBefore:F0}->{barAfterDrain:F0}/{StaggerBarMax:F0} force={force} " +
                        $"decision=absorbed-during-knockdown phase={_knockdownPhase} fromStun={fromStun}");
                }
                return false;
            }

            ResetStaggerBar();
            RestartKnockdownHold(force, fromStun);
            return true;
        }

        // DETERMINISM GATE. Everything below this line may return early on OWNER-ONLY state
        // (_actionKind / _activeBasicVictimPaired are written only behind IsOwner gates and are
        // permanently None/false on remotes), so every mutation of the SHARED, all-clients-simulated
        // stagger bar has to be finished before we reach it. Consuming the earned knockdown here is
        // what the old accumulator did for the same reason ("consume the decision before owner-only
        // action checks so every client keeps the accumulator identical"), and dropping that
        // ordering was a real divergence: a shot that emptied the bar while the owner was mid-paired
        // -finisher left the owner at bar=0 and every remote at bar=100.
        //
        // Refilling at knockdown START (not only in FinishKnockdownExit) is also what stops the bar
        // sitting at 0 for the whole down-hold, where every follow-up hit would re-earn a knockdown
        // and restart the hold forever. Refilled, "keep shooting him while he is down" has to empty a
        // fresh bar to buy the restart, which is the intended price.
        if (large)
            ResetStaggerBar();

        if (!large)
        {
            // Tier 2 gates. A hit that fails any of them has already produced its tier 1 flinch, so
            // it is never a "nothing happened" hit.
            // Stun devices always take the stun tier — that is the whole point of the device — so
            // only real damage is held to the bar threshold.
            if (!fromStun && _staggerBar > StaggerBarStunThreshold)
            {
                if (DebugLogs)
                {
                    LogInfo(
                        $"[BundyStagger] bar={barBefore:F0}->{barAfterDrain:F0}/{StaggerBarMax:F0} force={force} " +
                        $"decision=flinch-only threshold={StaggerBarStunThreshold:F0}");
                }
                return false;
            }
            if (Time.time < _nextStaggerAllowedAt)
                return false;
            // Post-recovery immunity suppresses the STUN tier too, not just the knockdown: a man
            // who just climbed off the floor should not be staggerable straight back into it.
            if (immune)
            {
                if (DebugLogs)
                {
                    LogInfo(
                        $"[BundyStagger] bar={barBefore:F0}->{barAfterDrain:F0}/{StaggerBarMax:F0} force={force} " +
                        $"decision=flinch-only-knockdown-immune remaining={_knockdownImmunityUntil - Time.time:F1}s");
                }
                return false;
            }
            // #208 sprint arrival swing is uninterruptible: the gait's entire point is catching a
            // fast player and hitting him AT speed, and a stun mid-conversion drops the swing after
            // the sprint has already been abandoned — he pays the run and lands nothing. Emptying
            // the bar still puts him down (that branch never reaches here).
            // Reads the REPLICATED mirror, not _sprintArrivalAttackActive/_actionKind: the player
            // being run down is usually not the owner, and it is his screen the ruling is about.
            if (IsReactionSuppressed(ReactionSuppression.SprintArrivalAttack))
            {
                if (DebugLogs)
                {
                    LogInfo(
                        $"[BundyStagger] bar={barBefore:F0}->{barAfterDrain:F0}/{StaggerBarMax:F0} force={force} " +
                        "decision=flinch-only-sprint-attack");
                }
                return false;
            }
        }
        // Never interrupt a paired finisher / paired basic attack — it would break the locked actor
        // pairing. Replicated mirror again: the pairing's victim is on another machine, and it was
        // his view of the choreography this has always been protecting.
        if (IsReactionSuppressed(ReactionSuppression.PairedAction))
            return false;

        bool fromBehind = source != null &&
            Vector3.Dot(transform.forward, (source.transform.position - transform.position).normalized) < -0.2f;

        int staggerType;
        if (large)
        {
            staggerType = fromBehind ? StaggerLargeFromBehindType : StaggerLargeType;
        }
        else
        {
            staggerType = _lastSmallStaggerType == StaggerSmall01Type ? StaggerSmall02Type : StaggerSmall01Type;
            _lastSmallStaggerType = staggerType;
        }

        BeginStaggerInternal(staggerType, large);
        if (DebugLogs)
        {
            bool slowdownWillApply = large &&
                StaggerKnockdownImpactSlowdown < 1f &&
                StaggerKnockdownSlowdownStartFraction < 1f;
            LogInfo(
                $"[BundyStagger] bar={barBefore:F0}->{barAfterDrain:F0}/{StaggerBarMax:F0} force={force} cost={cost:F0} " +
                $"decision={(large ? "knockdown" : "stun")} type={staggerType} fromStun={fromStun} " +
                $"fromBehind={fromBehind} slowdownApplied={slowdownWillApply} lock={StaggerMovementLockEnabled}");
        }

        return large;
    }

    // The shared "a stagger starts NOW" body, extracted from TriggerStaggerFromDamage so the #318
    // forced knockdown enters through exactly the same seams instead of a parallel copy. Everything
    // in here is client-simulated (each client drives its own animator through the reaction); the
    // owner-only block is the authoritative movement/action half. Callers own the decision — the
    // bar arithmetic, the tier gates and the immunity window all live above this.
    private void BeginStaggerInternal(int staggerType, bool large)
    {
        _nextStaggerAllowedAt = Time.time + StaggerCooldownSeconds;
        // A retriggered stagger is also a new action visually on non-owners. Route the previous
        // knockdown's teardown (animator speed, phase state, armed recovery taunt) through the ONE
        // exit function — never clear _staggerKnockdownActive directly (C1.2).
        ExitStaggerKnockdown("preempted by new stagger");

        // HitEnemy already runs once on every client through vanilla's hit RPC flow, so every client
        // drives its own Animator. Only the owner may change authoritative movement/action state.
        if (IsOwner)
        {
            _lungeActive = false;
            _finisherLungeActive = false;
            float duration = large ? 1.6f : 1.0f;
            // The impact slowdown stretches real-time playback of the knockdown tail; the action
            // window must cover the stretched clip or the AI resumes mid-animation.
            float slowdown = StaggerKnockdownImpactSlowdown;
            if (large && slowdown < 1f && slowdown > 0f)
                duration *= 1f + (1f - StaggerKnockdownSlowdownStartFraction) * (1f / slowdown - 1f);
            BeginTimedAction(ActionKind.Stagger, _actionTarget, 0f, duration, duration);
            if (StaggerMovementLockEnabled)
                HoldAgentForStagger();
            if (large)
                WipeStrangerKnowledgeAfterKnockdown();
        }

        if (large)
        {
            // (the bar was already consumed by the caller, above the determinism gate)
            _staggerKnockdownActive = true;
            _staggerKnockdownStateSeen = false;
            _staggerKnockdownStartedAt = Time.time;
            _knockdownPhase = KnockdownPhase.None;
            _knockdownStateHash = 0;
            _knockdownHoldOverrideSeconds = -1f;
            _knockdownDownEnteredAt = float.NegativeInfinity;
            // C1.2 fix: arm the recovery taunt at knockdown START. It fires from whichever path
            // ends the knockdown — the phase machine's own exit, a preempting stagger, the
            // defensive clears in BeginTimedAction/CompleteAction/death cleanup — instead of
            // depending on one specific exit branch that a follow-up hit could bypass.
            _knockdownRecoveryTauntArmed = true;
            // #188: pin from the hit frame, not from the ground pose — the fall is part of the window
            // a player can shove him through, and the spot he goes down at IS the spot he must rise at.
            BeginKnockdownRootPin();
        }

        PlayStaggerAnimation(staggerType);
    }

    // ---- #318 spike roof trap ------------------------------------------------------------------
    // Vanilla SpikeRoofTrap.OnTriggerStay calls KillEnemyOnOwnerClient on anything with canDie, and
    // Bundy has no death representation: vanilla KillEnemy sets isEnemyDead, disables the agent and
    // drives animator params his controller does not have, leaving a frozen walking corpse. The trap
    // is suppressed in BundySpikeTrapPatch and lands here instead — a real registered hit plus a
    // knockdown he has to climb out of.
    //
    // OWNER ONLY. The patch enforces it (the vanilla gate it replaces was owner-gated too), and the
    // work below is a fan-out from the owner: the hit goes through vanilla's own hit RPC and the
    // knockdown through the RPC pair below, so every client still runs the identical reaction.
    internal void OnSpikeTrapSlam(SpikeRoofTrap trap)
    {
        if (!IsOwner || isEnemyDead)
            return;

        // OnTriggerStay refires every physics frame for the whole overlap; without this one slam
        // would land dozens of hits and restart the knockdown until the spikes retracted.
        int trapId = trap != null ? trap.GetInstanceID() : 0;
        if (trapId == _lastSpikeTrapSlamTrapId && Time.time - _lastSpikeTrapSlamAt < SpikeTrapSlamDedupSeconds)
            return;

        _lastSpikeTrapSlamTrapId = trapId;
        _lastSpikeTrapSlamAt = Time.time;

        // playerWhoHit null on purpose: with no player the local call is a pure
        // ServerRpc -> ClientRpc round trip, so HitEnemy runs EXACTLY once on every client (including
        // this one) and the deterministic stagger bar stays in sync. Passing a player would double it
        // here. Nothing decrements enemyHP — the trap must not be able to kill him.
        ReportSpikeTrapDamageBlood(trap);
        HitEnemyOnLocalClient(SpikeTrapHitForce, Vector3.down, null, playHitSFX: true, hitID: -1);

        // A paired finisher owns both actors' poses; flooring him mid-pairing would strand the victim
        // in a choreography with no partner. The damage above still lands — only the knockdown is
        // skipped, matching the tier 2/3 ruling in TriggerStaggerFromDamage. A variant with no
        // stagger set at all takes the damage-only branch for the same reason.
        if (!StaggersEnabled || IsReactionSuppressed(ReactionSuppression.PairedAction))
        {
            LogInfo(
                BundyLogCategory.Combat, 1,
                $"[BundySpikeTrap] slam trap={trapId} force={SpikeTrapHitForce} knockdown=skipped " +
                $"staggersEnabled={StaggersEnabled} paired={IsReactionSuppressed(ReactionSuppression.PairedAction)}");
            return;
        }

        LogInfo(
            BundyLogCategory.Combat, 1,
            $"[BundySpikeTrap] slam trap={trapId} force={SpikeTrapHitForce} knockdown=forced " +
            $"IsServer={IsServer} knockdownActive={_staggerKnockdownActive}");

        if (IsServer)
            ForceKnockdownClientRpc(StaggerLargeType);
        else
            ForceKnockdownServerRpc(StaggerLargeType);
    }

    // Ownership of an enemy can sit on a client, so the owner is not necessarily the server and
    // cannot invoke a ClientRpc itself. RequireOwnership stays on: nothing but the owner may force
    // a knockdown.
    [ServerRpc]
    private void ForceKnockdownServerRpc(int staggerType)
    {
        ForceKnockdownClientRpc(staggerType);
    }

    // The forced counterpart of TriggerStaggerFromDamage's tier 3. Fanned out rather than run
    // owner-side because the whole stagger/knockdown machine is client-simulated (see the
    // DETERMINISM GATE above): every client has to start the same knockdown on its own animator.
    [ClientRpc]
    private void ForceKnockdownClientRpc(int staggerType)
    {
        if (!StaggersEnabled || creatureAnimator == null || isEnemyDead)
            return;

        // Deliberately bypasses the bar threshold AND _knockdownImmunityUntil: being caught under a
        // slamming spike trap floors him regardless of how recently he last got up.
        ResetStaggerBar();

        // Already down (the hit above can have earned its own knockdown a moment earlier, or a
        // player put him there): reuse the C3.3 restart instead of replaying the fall from the
        // ground pose.
        if (_staggerKnockdownActive)
        {
            RestartKnockdownHold(SpikeTrapHitForce, fromStun: false);
            return;
        }

        BeginStaggerInternal(staggerType, large: true);
    }

    // #288 tier 1. All clients, every registered hit, before any tier 2/3 decision. Suppressed while
    // he is already on the floor or locked into a paired action — those poses are choreographed
    // against a partner and an extra bone offset would fight the pairing — and the suppression is a
    // gate on NEW flinches only, so a live one blends out instead of popping to zero.
    private void PlayProceduralFlinch(int force, PlayerControllerB source)
    {
        if (!ProceduralFlinchEnabled || creatureAnimator == null)
            return;

        // _staggerKnockdownActive is client-simulated (set inside the all-clients `if (large)` block),
        // so it is safe to read directly. The paired half must come from the replicated mirror: the
        // victim of a paired finisher is on another machine, watching the pose from a first-person
        // camera driven by a playable graph that evaluates BEFORE LateUpdate — an additive offset
        // applied there is uncorrectable, and reading _actionKind would only ever have suppressed it
        // on the owner. Suppression is a gate on NEW flinches; a live one still blends out.
        if (_staggerKnockdownActive || IsReactionSuppressed(ReactionSuppression.PairedAction))
            return;

        BundyProceduralFlinch flinch = EnsureProceduralFlinch();
        if (flinch == null)
            return;

        Vector3 direction = source != null
            ? transform.position - source.transform.position
            : -transform.forward;
        flinch.Flinch(force, direction, ProceduralFlinchIntensityScale);
        if (DebugLogs)
            LogInfo($"[BundyFlinch] force={force} scale={ProceduralFlinchIntensityScale:F2} source={PlayerLabel(source)}");
    }

    // #288: replaces ResetExpiredStaggerDamageAccumulator. Ticked from Core.Update on every client.
    private void UpdateStaggerBarRegen()
    {
        float max = StaggerBarMax;
        if (float.IsNaN(_staggerBar))
        {
            // Lazy seed: StaggerBarMax is config-driven and the config is not necessarily bound
            // when the field initializer runs.
            _staggerBar = max;
            return;
        }

        if (_staggerBar >= max)
        {
            if (_staggerBar > max)
                _staggerBar = max;
            return;
        }

        if (Time.time - _lastStaggerBarDamageAt < StaggerBarRegenDelaySeconds)
            return;

        _staggerBar = Mathf.Min(max, _staggerBar + Mathf.Max(0f, StaggerBarRegenPerSecond) * Time.deltaTime);
    }

    private void ResetStaggerBar()
    {
        _staggerBar = StaggerBarMax;
        _lastStaggerBarDamageAt = float.NegativeInfinity;
    }

    // Owner-side hard hold for the stagger window: no residual slide, no turning.
    private void HoldAgentForStagger()
    {
        // #188: while the knockdown pin owns the root, MaintainKnockdownRootPin is the single writer.
        // This one only stops the agent — it leaves updatePosition on, which is exactly the hole the
        // pin closes, so it must not run alongside it.
        if (_knockdownRootPinActive)
            return;
        if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh)
            return;
        agent.isStopped = true;
        agent.velocity = Vector3.zero;
        _staggerAgentStopApplied = true;
    }

    // ---- Knockdown root pin (#188) -------------------------------------------------------------
    // A downed Bundy is furniture: nothing may move him until he stands. The old hold set only
    // `agent.isStopped` and left `updatePosition`/`updateRotation` on, so the root could still be
    // displaced during a hold that runs up to 12 s — and once the root and `agent.nextPosition`
    // diverged (or the root left the NavMesh), the get-up handed him to a recovery Warp:
    // `UnlockAgentForMovement` warps on any desync past 0.5 m, and `EnsureAgentOnNavMesh` warps up to
    // 35 m. That is the ~15 m jump Lawson saw, and the same hole is why a player could shove the body.
    //
    // Same shape as MaintainPairedActionRootLock, which already solved this for paired actions: pin
    // the transform, take `updatePosition`/`updateRotation` away from the agent, and glue
    // `nextPosition` to the pin so the two can never disagree. Runs on EVERY client (each drives its
    // own animator through the knockdown), and the release re-seats from the pin so there is nothing
    // left for any warp path to correct.
    private bool _knockdownRootPinActive;
    private Vector3 _knockdownRootPinPosition;
    private Quaternion _knockdownRootPinRotation = Quaternion.identity;
    private bool _knockdownPinSavedAgentUpdatePosition;
    private bool _knockdownPinSavedAgentUpdateRotation;
    private Rigidbody _knockdownPinRigidbody;
    private bool _knockdownPinSavedRigidbodyKinematic;

    private static bool KnockdownRootPinEnabled =>
        BundyPlugin.ModConfig?.KnockdownRootPin?.Value ?? true;

    private void BeginKnockdownRootPin()
    {
        if (_knockdownRootPinActive || !KnockdownRootPinEnabled)
            return;

        // Sample the graph once, here: a pin that sits on the NavMesh keeps `agent.isOnNavMesh` true
        // for the whole hold, which is what stops EnsureAgentOnNavMesh from ever looking at him.
        Vector3 pin = transform.position;
        if (agent != null && agent.enabled &&
            NavMesh.SamplePosition(pin, out NavMeshHit hit, 1.5f, agent.areaMask))
        {
            pin = hit.position;
        }

        _knockdownRootPinActive = true;
        _knockdownRootPinPosition = pin;
        _knockdownRootPinRotation = transform.rotation;

        if (agent != null && agent.enabled)
        {
            _knockdownPinSavedAgentUpdatePosition = agent.updatePosition;
            _knockdownPinSavedAgentUpdateRotation = agent.updateRotation;
        }
        else
        {
            _knockdownPinSavedAgentUpdatePosition = true;
            _knockdownPinSavedAgentUpdateRotation = !ManualPursuitControlsAgentRotation;
        }

        // The prefab root ships a kinematic Rigidbody, but a downed body is exactly the case where a
        // stray non-kinematic state would show, so assert it rather than trust it.
        _knockdownPinRigidbody = GetComponent<Rigidbody>();
        if (_knockdownPinRigidbody != null)
        {
            _knockdownPinSavedRigidbodyKinematic = _knockdownPinRigidbody.isKinematic;
            _knockdownPinRigidbody.isKinematic = true;
        }

        MaintainKnockdownRootPin();
        LogInfo(
            BundyLogCategory.Combat, 1,
            $"[BundyKnockdown] root pinned at {FormatVector(_knockdownRootPinPosition)} " +
            $"(sampled {Vector3.Distance(transform.position, _knockdownRootPinPosition):F2}m), " +
            $"rigidbody={(_knockdownPinRigidbody != null ? (_knockdownPinSavedRigidbodyKinematic ? "already-kinematic" : "forced-kinematic") : "<none>")}.");
    }

    private void MaintainKnockdownRootPin()
    {
        if (!_knockdownRootPinActive)
            return;

        transform.SetPositionAndRotation(_knockdownRootPinPosition, _knockdownRootPinRotation);

        if (agent == null || !agent.enabled)
            return;

        agent.updatePosition = false;
        agent.updateRotation = false;
        if (!agent.isOnNavMesh)
            return;

        agent.isStopped = true;
        agent.velocity = Vector3.zero;
        agent.nextPosition = _knockdownRootPinPosition;
    }

    private void EndKnockdownRootPin(string reason)
    {
        if (!_knockdownRootPinActive)
            return;

        _knockdownRootPinActive = false;

        if (_knockdownPinRigidbody != null)
        {
            _knockdownPinRigidbody.isKinematic = _knockdownPinSavedRigidbodyKinematic;
            _knockdownPinRigidbody = null;
        }

        // He stands up where he fell. Restoring from the pin (rather than from wherever the transform
        // happens to be) is what leaves ResumeMovementAfterAction's desync check nothing to warp.
        transform.position = _knockdownRootPinPosition;

        if (agent == null || !agent.enabled)
            return;

        agent.updatePosition = _knockdownPinSavedAgentUpdatePosition;
        agent.updateRotation = _knockdownPinSavedAgentUpdateRotation;
        if (agent.isOnNavMesh)
            agent.nextPosition = _knockdownRootPinPosition;
        else
            agent.Warp(_knockdownRootPinPosition);

        LogInfo(
            BundyLogCategory.Combat, 1,
            $"[BundyKnockdown] root released at {FormatVector(_knockdownRootPinPosition)}; " +
            $"agentDesync={(agent.isOnNavMesh ? Vector3.Distance(agent.nextPosition, transform.position).ToString("F2") + "m" : "<off-mesh>")}, reason={reason}.");
    }

    // ---- Real knockdown (C3.3) -----------------------------------------------------------------
    // The 3rd stagger animation (type 2/3, Stagger_Large[FromBehind]) is a full knock-to-ground.
    // All clients drive the same phase machine off their own animator (animation events are dead
    // on Bundy clips — r40 rebake — so ALL timing comes from normalized state time and timers):
    //   Fall  — the clip plays; the existing impact slowdown weights the landing.
    //   Down  — animator frozen at the ground pose; he screams (KnockdownScream); the hold length
    //           IS the server-chosen scream clip's length (replicated), fallback const otherwise.
    //   GetUp — animator resumes at a slowed speed while KnockdownRecover plays; the agent stays
    //           hard-held the whole time (Core.Update re-applies HoldAgentForStagger every frame
    //           while _actionKind == Stagger), so he cannot translate during the rise.
    // Release is the STANDARD unconditional unlock: the owner clamps the stagger action's recover
    // fallback so UpdateActionFallbacks -> CompleteAction("fallback end") -> ResumeMovementAfterAction
    // runs the same frame. No new exit seam; the post-action BUG SIGNAL must stay at 0.
    //
    // Wave 2: the four tunables promoted to Combat-section config (defaults preserved exactly).
    private static float KnockdownDownPoseNormalizedTime =>         // ground-pose moment in the clip
        Mathf.Clamp(BundyPlugin.ModConfig?.KnockdownDownPoseFraction?.Value ?? 0.60f, 0.05f, 0.95f);
    private const float KnockdownGetUpCompleteNormalizedTime = 0.99f;
    // #188: was a hard 0 — a freeze-frame, which reads as a paused game rather than a stunned man.
    // A crawl keeps the ground pose alive; KnockdownHoldMaxCreepNormalizedTime caps how far it may
    // travel so even a 6 s hold cannot advance the clip into its own get-up.
    private static float KnockdownHoldAnimatorSpeed =>
        Mathf.Clamp(BundyPlugin.ModConfig?.KnockdownHoldAnimatorSpeed?.Value ?? 0.06f, 0f, 0.5f);
    private static float KnockdownHoldMaxCreepNormalizedTime =>
        Mathf.Clamp(BundyPlugin.ModConfig?.KnockdownHoldMaxCreepFraction?.Value ?? 0.12f, 0.01f, 0.35f);
    private static float KnockdownGetUpAnimatorSpeed =>             // slowed, labored rise
        Mathf.Clamp(BundyPlugin.ModConfig?.KnockdownGetUpSlowdown?.Value ?? 0.55f, 0.1f, 1f);
    private static float KnockdownHoldFallbackSeconds =>            // only if the scream clip is missing
        BundyPlugin.ModConfig?.KnockdownHoldFallbackSeconds?.Value ?? 2.6f;
    private static float KnockdownMaxHoldSeconds =>                 // sanity clamp on clip-driven holds
        BundyPlugin.ModConfig?.KnockdownMaxHoldSeconds?.Value ?? 6f;
    private static float KnockdownImmunitySeconds =>                // no re-knockdown after recovery
        BundyPlugin.ModConfig?.KnockdownImmunitySeconds?.Value ?? 30f;
    private const float KnockdownMaxTotalSeconds = 12f;             // watchdog: whole knockdown, worst case
    private const float KnockdownActionWindowLeadSeconds = 0.75f;   // rolling stagger-action extension

    private enum KnockdownPhase { None, Fall, Down, GetUp }

    private KnockdownPhase _knockdownPhase;
    private float _knockdownDownEnteredAt = float.NegativeInfinity;
    private float _knockdownHoldOverrideSeconds = -1f;  // server-replicated scream clip length
    private int _knockdownStateHash;                    // which knockdown state actually played
    private int _lastKnockdownScreamIndex = -1;
    private bool _knockdownRecoveryTauntArmed;          // C1.2: armed at knockdown start
    private float _knockdownImmunityUntil = float.NegativeInfinity; // no re-knockdown until then

    // All clients: animator speed control + phase machine for the knockdown. External writers
    // (BeginTimedAction, CompleteAction, death cleanup in Core.Update) still clear
    // _staggerKnockdownActive + RestoreStaggerAnimatorSpeed directly; the !active branch below
    // catches those so the armed taunt still fires and no phase state leaks into the next knockdown.
    private void UpdateStaggerImpactSlowdown()
    {
        if (!_staggerKnockdownActive)
        {
            if (_knockdownPhase != KnockdownPhase.None || _knockdownRecoveryTauntArmed)
                FinishKnockdownExit("external clear");
            // Backstop for every path that clears _staggerKnockdownActive without going through
            // FinishKnockdownExit (BeginTimedAction, CompleteAction, the death cleanup in Core.Update):
            // the pin must never outlive the knockdown by more than the frame that ended it.
            EndKnockdownRootPin("knockdown no longer active");
            if (_staggerAnimatorSlowdownActive)
                RestoreStaggerAnimatorSpeed();
            return;
        }

        // Re-asserted before the phase machine and before any early return below, so a watchdog or
        // animator-loss exit still leaves from a pinned root rather than a drifted one.
        MaintainKnockdownRootPin();

        if (creatureAnimator == null)
        {
            ExitStaggerKnockdown("no animator");
            return;
        }

        // Watchdog: no phase wedge may hold the animator (or, through the rolling action window,
        // the agent) hostage. Forces the standard release rather than trusting the phase math.
        if (Time.time - _staggerKnockdownStartedAt > KnockdownMaxTotalSeconds)
        {
            ExitStaggerKnockdown("watchdog timeout");
            return;
        }

        AnimatorStateInfo state = creatureAnimator.GetCurrentAnimatorStateInfo(0);
        bool inKnockdownState =
            state.shortNameHash == StaggerLargeStateHash ||
            state.shortNameHash == StaggerLargeFromBehindStateHash;

        if (inKnockdownState)
        {
            _staggerKnockdownStateSeen = true;
            _knockdownStateHash = state.shortNameHash;
            UpdateKnockdownPhases(state);
            MaintainKnockdownActionWindow();
            return;
        }

        // The DoStagger transition takes a few frames to land in the state; give up quietly if it
        // never arrives (e.g. trigger consumed by a competing transition). After the get-up,
        // leaving the state IS the natural completion path.
        if (_staggerKnockdownStateSeen || Time.time - _staggerKnockdownStartedAt > 2.5f)
            ExitStaggerKnockdown(_staggerKnockdownStateSeen ? "left knockdown state" : "state never entered");
    }

    private void UpdateKnockdownPhases(AnimatorStateInfo state)
    {
        float normalized = state.normalizedTime;
        switch (_knockdownPhase)
        {
            case KnockdownPhase.None:
                _knockdownPhase = KnockdownPhase.Fall;
                goto case KnockdownPhase.Fall;

            case KnockdownPhase.Fall:
                if (normalized >= KnockdownDownPoseNormalizedTime)
                {
                    BeginKnockdownDownPhase("fall reached ground pose");
                    break;
                }
                // Pre-ground: the original impact slowdown weights the landing.
                if (normalized >= StaggerKnockdownSlowdownStartFraction)
                    SetStaggerAnimatorSpeed(StaggerKnockdownImpactSlowdown);
                break;

            case KnockdownPhase.Down:
                // Re-assert every frame: a defensive RestoreStaggerAnimatorSpeed elsewhere must not
                // silently unfreeze a man who is still supposed to be on the floor. #188: the hold is a
                // crawl now, hard-stopped once it has crept its allowance past the ground pose — the
                // hold length is the scream's, and the clip must not outrun it into the get-up.
                SetStaggerAnimatorSpeed(
                    normalized >= KnockdownDownPoseNormalizedTime + KnockdownHoldMaxCreepNormalizedTime
                        ? 0f
                        : KnockdownHoldAnimatorSpeed);
                if (Time.time >= _knockdownDownEnteredAt + CurrentKnockdownHoldSeconds())
                    BeginKnockdownGetUpPhase();
                break;

            case KnockdownPhase.GetUp:
                SetStaggerAnimatorSpeed(KnockdownGetUpAnimatorSpeed);
                if (normalized >= KnockdownGetUpCompleteNormalizedTime)
                    ExitStaggerKnockdown("get-up complete");
                break;
        }
    }

    private void BeginKnockdownDownPhase(string reason)
    {
        _knockdownPhase = KnockdownPhase.Down;
        _knockdownDownEnteredAt = Time.time;
        SetStaggerAnimatorSpeed(KnockdownHoldAnimatorSpeed);

        float screamLength = -1f;
        if (IsServer)
        {
            // Audio + hold length are server-authoritative and replicate through the same
            // ClientRpc path as every other vocal. The hold IS the chosen clip's length.
            int screamIndex = BundyAudioCatalog.PickIndex(BundyAudioCue.KnockdownScream, _lastKnockdownScreamIndex);
            if (screamIndex >= 0)
            {
                _lastKnockdownScreamIndex = screamIndex;
                AudioClip scream = BundyAudioCatalog.GetClip(BundyAudioCue.KnockdownScream, screamIndex);
                if (scream != null && scream.length > 0.1f)
                    screamLength = scream.length;
                PlayBundyAudio(
                    BundyAudioCue.KnockdownScream,
                    screamIndex,
                    VoiceAudioVolume * UnityEngine.Random.Range(0.95f, 1.05f),
                    1f);
            }

            SetKnockdownHoldClientRpc(ResolveKnockdownHoldSeconds(screamLength));
        }

        LogInfo(
            BundyLogCategory.Combat, 1,
            $"[BundyKnockdown] hold-begin holdSeconds={CurrentKnockdownHoldSeconds():F2} " +
            $"screamClipLength={(screamLength > 0f ? screamLength.ToString("F2") + "s" : "<missing/non-server>")} " +
            $"reason={reason} sinceKnockdown={Time.time - _staggerKnockdownStartedAt:F2}s");
    }

    private void BeginKnockdownGetUpPhase()
    {
        float heldFor = Time.time - _knockdownDownEnteredAt;
        _knockdownPhase = KnockdownPhase.GetUp;
        SetStaggerAnimatorSpeed(KnockdownGetUpAnimatorSpeed);

        if (IsServer)
        {
            int recoverIndex = BundyAudioCatalog.PickIndex(BundyAudioCue.KnockdownRecover);
            if (recoverIndex >= 0)
            {
                PlayBundyAudio(
                    BundyAudioCue.KnockdownRecover,
                    recoverIndex,
                    VoiceAudioVolume * UnityEngine.Random.Range(0.95f, 1.05f),
                    1f);
            }
        }

        LogInfo(
            BundyLogCategory.Combat, 1,
            $"[BundyKnockdown] getup-begin heldFor={heldFor:F2}s target={CurrentKnockdownHoldSeconds():F2}s " +
            $"getUpSpeed={KnockdownGetUpAnimatorSpeed:F2}");
    }

    // C3.3 down-hold interaction: a second earned knockdown while he is already down restarts the
    // hold. During the fall the hold has not begun (nothing to restart); during the hold the timer
    // and scream reset; during the get-up the animator rewinds to the ground pose first.
    private void RestartKnockdownHold(int force, bool fromStun)
    {
        _nextStaggerAllowedAt = Time.time + StaggerCooldownSeconds;
        _staggerKnockdownStartedAt = Time.time; // re-feed the watchdog for the extended stay
        _staggerKnockdownStateSeen = true;      // only reachable after the state actually played
        _knockdownRecoveryTauntArmed = true;

        switch (_knockdownPhase)
        {
            case KnockdownPhase.GetUp:
                if (creatureAnimator != null && _knockdownStateHash != 0)
                    creatureAnimator.Play(_knockdownStateHash, 0, KnockdownDownPoseNormalizedTime);
                _knockdownHoldOverrideSeconds = -1f;
                BeginKnockdownDownPhase($"knockdown restart during get-up (force={force}, fromStun={fromStun})");
                break;
            case KnockdownPhase.Down:
                _knockdownHoldOverrideSeconds = -1f;
                BeginKnockdownDownPhase($"knockdown restart during hold (force={force}, fromStun={fromStun})");
                break;
            default:
                // Fall (or the pre-state gap): the fall is still playing and the hold will begin
                // on its own; the reset watchdog above is the whole restart.
                if (DebugLogs)
                    LogInfo($"[BundyStagger] decision=knockdown-restart-absorbed phase={_knockdownPhase} force={force}");
                break;
        }

        if (IsOwner)
            WipeStrangerKnowledgeAfterKnockdown();
    }

    // Owner: the stagger action window rolls forward while the knockdown lives, so the standard
    // UpdateActionFallbacks release cannot fire mid-hold; ExitStaggerKnockdown clamps it back to
    // "now" and the very same CompleteAction("fallback end") seam releases him. Zero new seams.
    private void MaintainKnockdownActionWindow()
    {
        if (!IsOwner || _actionKind != ActionKind.Stagger)
            return;

        float minEnd = Time.time + KnockdownActionWindowLeadSeconds;
        if (_actionRecoverFallbackAt < minEnd)
            _actionRecoverFallbackAt = minEnd;
    }

    private float CurrentKnockdownHoldSeconds()
    {
        return _knockdownHoldOverrideSeconds > 0f
            ? _knockdownHoldOverrideSeconds
            : Mathf.Min(KnockdownHoldFallbackSeconds, KnockdownMaxHoldSeconds);
    }

    private static float ResolveKnockdownHoldSeconds(float screamClipLength)
    {
        float hold = screamClipLength > 0.1f ? screamClipLength : KnockdownHoldFallbackSeconds;
        return Mathf.Clamp(hold, 0.5f, KnockdownMaxHoldSeconds);
    }

    [ClientRpc]
    private void SetKnockdownHoldClientRpc(float holdSeconds)
    {
        _knockdownHoldOverrideSeconds = Mathf.Clamp(holdSeconds, 0.5f, KnockdownMaxHoldSeconds);
    }

    // THE knockdown exit (C1.2): every internal path that ends the knockdown funnels through here —
    // phase completion, state-never-entered timeout, watchdog, animator loss, preempting stagger,
    // the dead anim-event OnStaggerEnd. Restores animator speed, fires the armed recovery taunt,
    // and hands the agent back through the standard action seam.
    private void ExitStaggerKnockdown(string reason)
    {
        bool endedLiveKnockdown = _staggerKnockdownActive;
        RestoreStaggerAnimatorSpeed();
        _staggerKnockdownActive = false;
        FinishKnockdownExit(reason);

        // Owner: clamp the stagger action window to now so UpdateActionFallbacks (later this same
        // frame) completes it through CompleteAction -> ResumeMovementAfterAction — the ONE
        // unconditional unlock. Only when a LIVE knockdown ended here: the routine "preempted by
        // new stagger" call while no knockdown is running must never truncate the small staggers'
        // own action windows.
        if (endedLiveKnockdown &&
            IsOwner &&
            _actionKind == ActionKind.Stagger &&
            _actionRecoverFallbackAt > Time.time)
        {
            _actionRecoverFallbackAt = Time.time;
        }
    }

    // Shared teardown for both the internal exit above and externally-observed clears. Resets the
    // phase state and fires the armed taunt exactly once per knockdown.
    private void FinishKnockdownExit(string reason)
    {
        KnockdownPhase endedPhase = _knockdownPhase;
        bool wasDown = endedPhase == KnockdownPhase.Down || endedPhase == KnockdownPhase.GetUp;
        float heldFor = wasDown && !float.IsNegativeInfinity(_knockdownDownEnteredAt)
            ? Time.time - _knockdownDownEnteredAt
            : 0f;

        _knockdownPhase = KnockdownPhase.None;
        _knockdownHoldOverrideSeconds = -1f;
        _knockdownDownEnteredAt = float.NegativeInfinity;
        // Release the root BEFORE the action seam runs: CompleteAction -> ResumeMovementAfterAction ->
        // UnlockAgentForMovement warps on any agent/transform desync past 0.5 m, and re-seating from the
        // pin first is what guarantees that check finds nothing to correct.
        EndKnockdownRootPin(reason);

        if (endedPhase != KnockdownPhase.None)
        {
            // A knockdown that actually put him down grants the post-recovery immunity window
            // (runs identically on every client — the decision gate is client-local anyway).
            _knockdownImmunityUntil = Time.time + KnockdownImmunitySeconds;
            // #288: he stands up with a full stagger bar. Whatever damage was banked into it while
            // he lay there is spent — the knockdown WAS the payoff for it.
            ResetStaggerBar();
            LogInfo(
                BundyLogCategory.Combat, 1,
                $"[BundyKnockdown] release reason={reason} endedPhase={endedPhase} " +
                $"downFor={heldFor:F2}s total={Time.time - _staggerKnockdownStartedAt:F2}s " +
                $"immuneFor={KnockdownImmunitySeconds:F0}s staggerBar={_staggerBar:F0}");
        }

        if (_knockdownRecoveryTauntArmed)
        {
            _knockdownRecoveryTauntArmed = false;
            if (!isEnemyDead)
                QueueKnockdownRecoveryTaunt();
        }
    }

    private void SetStaggerAnimatorSpeed(float speed)
    {
        if (creatureAnimator == null)
            return;

        creatureAnimator.speed = speed;
        // Flag any non-1 speed (including the frozen 0 hold) so every RestoreStaggerAnimatorSpeed
        // caller — including the defensive ones in Actions/Core — puts it back. Speed != 1 must
        // never survive a knockdown exit.
        _staggerAnimatorSlowdownActive = !Mathf.Approximately(speed, 1f);
    }

    // Job 2e frustration trigger: getting put on the floor and climbing back up is the loudest
    // provocation in the fight, so he answers it. C1.2: armed at knockdown START
    // (_knockdownRecoveryTauntArmed) and fired from FinishKnockdownExit on EVERY recovery path —
    // including external clears — so "player keeps hitting me" can no longer disarm it. Only on a
    // knockdown that actually played (_staggerKnockdownStateSeen) — the "DoStagger never landed"
    // timeout is a non-event with nothing to react to. Owner-only because TryPlayPendingTaunt runs
    // behind Core.Update's owner gate, while this method runs on every client.
    //
    // Shares _nextBlockedTargetTauntAt with the blocked-pursuit taunt rather than carrying its own
    // cooldown: these fire in the same situations (a player who is winning is a player Bundy cannot
    // reach), and a frustration cluster — stagger, recover, blocked, stagger again — would otherwise
    // chain taunts back to back and read as a stutter instead of a threat.
    private void QueueKnockdownRecoveryTaunt()
    {
        if (!IsOwner || !_staggerKnockdownStateSeen || !TauntsEnabled || !BasicActionsEnabled)
            return;

        if (Time.time < _nextBlockedTargetTauntAt)
            return;

        _nextBlockedTargetTauntAt = Time.time + BlockedTargetTauntCooldownSeconds;
        _tauntPending = true;
        // Issue #205: tag the SOURCE. _tauntPending is shared with the first-attack and fresh-kill
        // taunts, and only this one — being put on the floor and climbing back up — promotes him to
        // the overhead sprint. Consumed when the taunt's action completes (Actions.cs CompleteAction).
        _sprintPendingFromTaunt = UsesOverheadSprint && SprintAfterKnockdownTauntEnabled;
        LogInfo("Taunt queued: knockdown recovery." + (_sprintPendingFromTaunt ? " Sprint armed." : string.Empty));
    }

    private void RestoreStaggerAnimatorSpeed()
    {
        if (creatureAnimator != null && _staggerAnimatorSlowdownActive)
            creatureAnimator.speed = 1f;
        _staggerAnimatorSlowdownActive = false;
    }

    // ---- Intimidation taunt --------------------------------------------------------------------
    // Fires when free (not action-locked) if a taunt was queued by the first attack or a kill.
    private bool TryPlayPendingTaunt()
    {
        if (!_tauntPending || !TauntsEnabled || !BasicActionsEnabled || creatureAnimator == null)
            return false;

        _tauntPending = false;
        PlayEmote(TauntEmoteIndex);
        LogInfo("Taunt: playing Taunt_Stomp (first attack / kill).");
        return true;
    }

    protected bool BeginContextualAction(PlayerControllerB target, int contextType, float durationSeconds, float cooldownSeconds, string reason)
    {
        if (Time.time < _nextMovementContextAllowedAt || IsActionLocked())
            return false;

        BeginTimedAction(
            ActionKind.Contextual,
            target,
            0f,
            Mathf.Max(0.25f, durationSeconds),
            Mathf.Max(0.25f, durationSeconds));
        _nextMovementContextAllowedAt = Time.time + Mathf.Max(cooldownSeconds, durationSeconds);
        HoldAgentWithoutReset();

        SetAnimatorInteger(BundyAnimatorParams.ContextType, contextType);
        SetAnimatorTrigger(BundyAnimatorParams.DoContextual);
        LogInfo($"Contextual movement start: type={contextType}, reason={reason}, target={PlayerLabel(target)}.");
        return true;
    }

    protected void SetAnimatorIntegerThenTrigger(string integerParameterName, int value, string triggerParameterName)
    {
        SetAnimatorInteger(integerParameterName, value);
        SetAnimatorTrigger(triggerParameterName);
    }

    protected void SetAnimatorIntegersThenTrigger(
        string firstIntegerParameterName,
        int firstValue,
        string secondIntegerParameterName,
        int secondValue,
        string triggerParameterName)
    {
        SetAnimatorInteger(firstIntegerParameterName, firstValue);
        SetAnimatorInteger(secondIntegerParameterName, secondValue);
        SetAnimatorTrigger(triggerParameterName);
    }

    protected void SetAnimatorTriggerOnly(string triggerParameterName)
    {
        SetAnimatorTrigger(triggerParameterName);
    }

    protected abstract int SelectBasicAttackIndex(int nearbyLivingPlayerCount, int lastAttackIndex);
    protected virtual int SelectFinisherBodyIndex(int lastBodyIndex)
    {
        return PickNoRepeatIndex(Mathf.Max(1, FinisherBodyCount), lastBodyIndex);
    }

    protected virtual int SelectFinisherEntryIndex(int bodyIndex)
    {
        return 0;
    }

    protected abstract void PlayFinisherAnimation(int entryIndex, int bodyIndex);

    protected virtual bool TryBeginPairedBasicAttack(PlayerControllerB victim, int attackIndex)
    {
        return false;
    }

    protected virtual void ReleasePairedBasicAttack(PlayerControllerB victim, int attackIndex, string reason)
    {
    }

    protected virtual bool TryBeginPairedFinisher(PlayerControllerB victim, int entryIndex, int bodyIndex)
    {
        return false;
    }

    protected virtual bool IsPairedFinisherVictimReady(PlayerControllerB victim, int entryIndex, int bodyIndex)
    {
        return true;
    }

    protected virtual void ReleasePairedFinisher(PlayerControllerB victim, int entryIndex, int bodyIndex, string reason)
    {
    }

    protected virtual bool IsPairedFinisherVictimActive(PlayerControllerB victim)
    {
        return _activeFinisherVictimPaired;
    }

    protected virtual bool BasicAttackCountsAsGrab(int attackIndex)
    {
        return false;
    }

    protected virtual bool BasicAttackIsOpeningThrow(int attackIndex)
    {
        return false;
    }

    protected virtual bool BasicAttackDealsDamage(int attackIndex)
    {
        return true;
    }

    protected virtual float GetBasicAttackStartRange(float baseRange)
    {
        return baseRange;
    }

    protected virtual float GetBasicAttackImpactDelay(int attackIndex)
    {
        return AttackImpactFallbackSeconds;
    }

    protected virtual float GetBasicAttackRecoverDelay(int attackIndex)
    {
        return AttackRecoverFallbackSeconds;
    }

    protected virtual float GetBasicAttackVisualExitNormalizedTime(int attackIndex)
    {
        return float.PositiveInfinity;
    }

    protected virtual void PlayPullStartAnimation()
    {
    }

    protected virtual bool TryBeginMovementContext(PlayerControllerB huntTarget)
    {
        return false;
    }

    public void OnAttackImpact()
    {
        if (_actionKind != ActionKind.Attack || _attackHitConfirmed || _actionImpactResolved)
            return;

        string damageSource = _dispatchingAttackImpactFallback ? "fallback timer" : "AnimationEvent";
        if (RequiresPhysicalBasicAttackContact &&
            !_activeAttackCountsAsGrab &&
            Time.time > _attackActiveWindowEndAt &&
            !TryGetLatchedBasicAttackContactTarget(out _))
        {
            FinalizeAttackMiss($"{damageSource}: late impact event", Time.time - _actionStartedAt);
            return;
        }

        EnterAttackPhase(AttackPhase.Active, damageSource);
        UpdateChainsawBladeHitboxWindow();

        if (_activeAttackCountsAsGrab || !RequiresPhysicalBasicAttackContact)
        {
            ResolveBasicAttackImpact(damageSource, finalizeMissImmediately: true);
            return;
        }

        if (ResolveBasicAttackImpact(damageSource, finalizeMissImmediately: false))
            return;

        if (DebugLogs)
        {
            LogAnimEvent(
                nameof(OnAttackImpact),
                $"active window opened without contact; phase={_attackPhase}, remaining={Mathf.Max(0f, _attackActiveWindowEndAt - Time.time):F2}s");
        }
    }

    private bool ResolveBasicAttackImpact(string damageSource, bool finalizeMissImmediately)
    {
        if (_actionKind != ActionKind.Attack || _attackHitConfirmed || _actionImpactResolved)
            return false;

        float elapsed = Time.time - _actionStartedAt;
        if (RequiresActiveMovementLoopAudioForAttacks && !IsMovementLoopAudioWindowReady())
        {
            FinalizeAttackMiss($"{damageSource}: movement-loop audio expired", elapsed);
            return false;
        }

        if (RequiresPhysicalBasicAttackContact &&
            !_activeAttackCountsAsGrab &&
            Time.time > _attackActiveWindowEndAt &&
            !TryGetLatchedBasicAttackContactTarget(out _))
        {
            if (finalizeMissImmediately)
                FinalizeAttackMiss($"{damageSource}: late physical impact", elapsed);
            return false;
        }

        PlayerControllerB victim = ResolveBasicAttackImpactTarget();
        bool usingLatchedPhysicalContact = IsLatchedBasicAttackContactTarget(victim);

        // Actual runtime blade contact is authoritative, but still bounded by the attack's start range so
        // a legitimate long blade swing is not vetoed by Bundy's root while impossible far hits stay blocked.
        if (victim != null && RequiresPhysicalBasicAttackContact && !_activeAttackCountsAsGrab)
        {
            float rootReach = MaxPhysicalBasicAttackRootReach;
            float reach = usingLatchedPhysicalContact ? MaxLatchedPhysicalBasicAttackRootReach : rootReach;
            float rootDistance = Vector3.Distance(transform.position, victim.transform.position);
            if (rootDistance > reach)
            {
                LogAnimEvent(
                    nameof(OnAttackImpact),
                    $"rejected far victim {PlayerLabel(victim)}: {rootDistance:F2}m > reach {reach:F2}m; " +
                    $"latchedBladeContact={usingLatchedPhysicalContact}");
                victim = null;
            }
            else if (usingLatchedPhysicalContact && rootDistance > rootReach)
            {
                LogAnimEvent(
                    nameof(OnAttackImpact),
                    $"accepted latched blade contact beyond root reach {PlayerLabel(victim)}: " +
                    $"{rootDistance:F2}m > rootReach {rootReach:F2}m <= latchedReach {reach:F2}m; runtime-saw-contact=true");
            }
        }

        if (victim == null)
        {
            if (finalizeMissImmediately)
                FinalizeAttackMiss(damageSource, elapsed);
            return false;
        }

        _attackHitConfirmed = true;
        _actionImpactResolved = true;
        ClearChainsawBladeHitboxWindow($"impact resolved: {damageSource}");

        float victimDistance = usingLatchedPhysicalContact
            ? _latchedBasicAttackRootDistance
            : Vector3.Distance(transform.position, victim.transform.position);

        if (!BasicAttackDealsDamage(_activeAttackIndex))
        {
            LogAnimEvent(nameof(OnAttackImpact), $"connected without direct damage; attackIndex={_activeAttackIndex}, source={damageSource}, elapsed={elapsed:F3}s, victimDistance={victimDistance:F2}m");
            if (BasicAttackIsOpeningThrow(_activeAttackIndex))
                return true;

            CompleteAction("non-damaging attack impact");
            return true;
        }

        Vector3 knockbackStart = victim.transform.position;
        Vector3 knockbackDirection = victim.transform.position - transform.position;
        knockbackDirection.y = 0f;
        if (knockbackDirection.sqrMagnitude < 0.001f)
            knockbackDirection = transform.forward;
        knockbackDirection.y = 0f;
        if (knockbackDirection.sqrMagnitude < 0.001f)
            knockbackDirection = Vector3.forward;
        knockbackDirection.Normalize();

        bool victimMayDie = victim.health <= BasicAttackDamage;
        ApplyBundyBasicAttackDamage(victim, BasicAttackDamage);
        StartCoroutine(GuardPlayerKnockback(victim, knockbackStart, knockbackDirection, KnockbackGuardSeconds, 0));
        if (victimMayDie)
            StartCoroutine(ConfirmBasicAttackKill(victim));
        ApplyBasicAttackBlood(victim);
        if (KeepMovementLoopAudioUntilBasicAttackImpact && !UsesFiniteMovementLoopAudioWindow)
            SetMovementLoopAudioRequested(false);

        int impactClipIndex = PlayRandomBundyAudio(BasicAttackDamageAudioCue, BasicAttackDamageAudioVolume, 0.96f, 1.05f);
        // Issue #210: blood follows the impact rather than joining it, offset past that clip's own
        // measured peak so the pair reads as "the hit caused this" instead of as one thicker hit.
        PlayBloodLayerForImpact(BasicAttackDamageAudioCue, impactClipIndex);
        // contactToDamage: latency between the first physical blade overlap (latch) and damage
        // application — the Task B "exact moment" verification number. 0.000 = same frame.
        float contactToDamage = usingLatchedPhysicalContact && !float.IsNegativeInfinity(_latchedBasicAttackContactAt)
            ? Mathf.Max(0f, Time.time - _latchedBasicAttackContactAt)
            : -1f;
        // Job 1, verbosity 0 and staying there: this is the ONLY reliable hit marker Bundy emits.
        // attackHitConfirmed never prints true and cost an entire session a false "zero hits" reading.
        // Nothing about combat can be judged from a log that does not carry this line.
        LogInfo(BundyLogCategory.Combat, 0, $"BundyBasicAttackDamage attackIndex={_activeAttackIndex}, source={damageSource}, elapsed={elapsed:F3}s, victim={PlayerLabel(victim)}, victimDistance={victimDistance:F2}m, damage={BasicAttackDamage}, contactToDamage={(contactToDamage >= 0f ? contactToDamage.ToString("F3") + "s" : "<non-latched>")}.");
        _consecutiveAttackMisses = 0;
        // Issue #205: the no-contact sprint trigger measures from a LANDED hit, and this is the only
        // place one is confirmed. Sitting it beside the miss counter keeps the two readings of "did
        // that swing connect" in the same place.
        NoteLandedHitOnPlayer();
        EnterAttackPhase(AttackPhase.Recovery, damageSource);
        return true;
    }

    // Vanilla DamagePlayer can crit-save a would-be-lethal hit (the player survives at 5 HP,
    // critically injured), and a real kill can resolve through an RPC a few frames later. The kill
    // must therefore be confirmed on the actual death flag; predicting it from pre-damage health
    // made Bundy drop his still-living target and stall waiting for a replacement assignment.
    private IEnumerator ConfirmBasicAttackKill(PlayerControllerB victim)
    {
        float deadline = Time.time + 1.5f;
        while (Time.time < deadline)
        {
            if (victim == null)
                yield break;

            if (victim.isPlayerDead)
            {
                _tauntPending = true; // taunt over a fresh kill
                HandleKilledTarget(victim, "basic attack");
                yield break;
            }

            yield return null;
        }

        LogInfo($"Basic attack was predicted lethal but {PlayerLabel(victim)} survived (crit-save); keeping target.");
    }

    private void FinalizeAttackMiss(string damageSource, float elapsed)
    {
        if (_actionKind != ActionKind.Attack || _attackHitConfirmed)
            return;

        _actionImpactResolved = true;
        ClearChainsawBladeHitboxWindow($"miss: {damageSource}");
        _consecutiveAttackMisses++;
        EnterAttackPhase(AttackPhase.Recovery, damageSource);
        string targetMode = RequiresPhysicalBasicAttackContact && !_activeAttackCountsAsGrab
            ? "no physical contact"
            : "no valid target in range";
        LogAnimEvent(nameof(OnAttackImpact), $"missed; source={damageSource}, elapsed={elapsed:F3}s, {targetMode}, missCount={_consecutiveAttackMisses}");
    }

    public void OnAttackRecover()
    {
        if (_actionKind != ActionKind.Attack || _actionRecoverResolved)
            return;

        _actionRecoverResolved = true;
        CompleteAction("attack recover");
    }

    public void OnFinisherGrab()
    {
        if (_actionKind != ActionKind.Finisher || _actionGrabResolved)
            return;

        _actionGrabResolved = true;
        PlayerControllerB victim = ResolveLockedActionTarget(FinisherAttackRange + 0.75f);
        if (victim != null)
        {
            _actionTarget = victim;
            _grabbedTarget = victim;
            MarkGrabConnected(victim);
            string pairedStatus = _activeFinisherVictimPaired
                ? "paired victim animation active"
                : "paired victim animation unavailable";
            LogAnimEvent(nameof(OnFinisherGrab), $"grabbed {PlayerLabel(victim)}; {pairedStatus}");
            LogFinisherSyncPhase("grab", victim, pairedStatus);
        }
        else
        {
            _activeFinisherVictimPaired = false;
            LogAnimEvent(nameof(OnFinisherGrab), "no valid victim; paired victim animation unavailable");
            LogFinisherSyncPhase("grab-missed", null, "no valid victim");
        }
    }

    public void OnFinisherImpact()
    {
        if (_actionKind != ActionKind.Finisher || _actionImpactResolved)
            return;

        PlayerControllerB victim = ResolveLockedActionTarget(FinisherAttackRange + 1f);
        if (_activeFinisherVictimPaired && Time.time + 0.05f < _actionImpactFallbackAt)
        {
            TryStartFinisherBlood(victim, "early paired impact event");
            float remaining = Mathf.Max(0f, _actionImpactFallbackAt - Time.time);
            LogAnimEvent(nameof(OnFinisherImpact), $"early paired impact deferred for {remaining:F2}s");
            LogFinisherSyncPhase("impact-deferred", victim, $"waiting {remaining:F2}s for paired sequence end");
            return;
        }

        _actionImpactResolved = true;
        if (victim == null)
        {
            LogAnimEvent(nameof(OnFinisherImpact), "missed; no valid victim in range");
            LogFinisherSyncPhase("impact-missed", null, "no valid victim");
            return;
        }

        if (!_activeFinisherVictimPaired || !IsPairedFinisherVictimActive(victim))
        {
            _activeFinisherVictimPaired = false;
            LogAnimEvent(nameof(OnFinisherImpact), $"lethal impact suppressed for {PlayerLabel(victim)}; paired victim animation inactive");
            LogFinisherSyncPhase("impact-suppressed", victim, "paired victim inactive");
            return;
        }

        TryStartFinisherBlood(victim, "lethal impact");
        ApplyFinisherBlood(victim);

        Vector3 force = (victim.transform.position - transform.position).normalized * 8f + Vector3.up * 4f;
        // #323: this one-shot fires at the kill frame, which is exactly where the gore soundtrack's
        // REMOVE phase already lands its pull-out. Two hits on the same frame read as one smeared
        // hit, not as two events, and the removal is the authored one — so when the phased set is
        // live this is suppressed rather than layered. It stays as the whole finisher's audio when
        // ChainsawFinisherCuttingAudio is off or the gore clips are missing, which is the only
        // reason not to simply delete it. (ChainsawFinisherDamage also backs the basic-attack pool;
        // that path is untouched.)
        //
        // Gated on the LIVE phase state, not on the static config+asset check: the phases are only
        // scheduled when BeginSawCuttingLocal ran, which is the Interactions path alone. A finisher
        // that fell back to the legacy proxy path (Interactions absent, config off, start failed)
        // has no removal to collide with, and gating on the static flag left it with no finisher
        // audio at all.
        if (FinisherDamageAudioCue.HasValue && !_finisherGoreAudioActive)
            PlayRandomBundyAudio(FinisherDamageAudioCue.Value, ChainsawAudioVolume, 0.98f, 1.02f);

        int playerIndex = GetPlayerIndex(victim);
        if (IsServer && playerIndex >= 0)
        {
            KillFinisherVictimClientRpc(playerIndex, force);
            // KillPlayer executes on the victim owner's client, so the server must confirm the real
            // death flag before retargeting — otherwise a lagged/disconnected owner leaves a live
            // player Bundy has already stopped targeting (issue #275).
            StartCoroutine(ConfirmFinisherKill(victim, playerIndex, force));
        }
        else
        {
            KillLocalVictim(playerIndex, force);
            HandleKilledTarget(victim, "finisher");
        }

        LogAnimEvent(nameof(OnFinisherImpact), $"kill issued for {PlayerLabel(victim)}");
        LogFinisherSyncPhase("impact-kill", victim, "kill issued");
    }

    // Finisher counterpart of ConfirmBasicAttackKill: the kill RPC only takes effect on the victim
    // owner's client, so retarget/teardown must wait for the replicated death flag. On timeout the
    // RPC is re-issued once (covers a dropped/late delivery), then the fallback is server-visible
    // lethal damage — the victim either dies or is re-acquired, never silently ignored while alive.
    private IEnumerator ConfirmFinisherKill(PlayerControllerB victim, int playerIndex, Vector3 force)
    {
        float deadline = Time.time + 1.5f;
        while (Time.time < deadline)
        {
            if (victim == null)
                yield break;

            if (victim.isPlayerDead)
            {
                HandleKilledTarget(victim, "finisher");
                yield break;
            }

            yield return null;
        }

        LogWarning($"Finisher kill unconfirmed for {PlayerLabel(victim)} after 1.5s; re-issuing kill RPC.");
        KillFinisherVictimClientRpc(playerIndex, force);

        deadline = Time.time + 1.5f;
        while (Time.time < deadline)
        {
            if (victim == null)
                yield break;

            if (victim.isPlayerDead)
            {
                HandleKilledTarget(victim, "finisher");
                yield break;
            }

            yield return null;
        }

        // The owner never executed the kill (lagged out or disconnected mid-finisher). Lethal
        // damage through the replicated damage path keeps the outcome server-visible; sized past
        // both current health and the 50-damage crit-save threshold so it cannot be survived.
        LogWarning($"Finisher kill still unconfirmed for {PlayerLabel(victim)}; falling back to server-visible lethal damage before retargeting.");
        if (IsLivingPlayer(victim))
            ApplyBundyBasicAttackDamage(victim, Mathf.Max(100, victim.health));
        HandleKilledTarget(victim, "finisher kill fallback");
    }

    public void OnFinisherEnd()
    {
        if (_actionKind != ActionKind.Finisher || _actionRecoverResolved)
            return;

        PlayerControllerB victim = _activeFinisherVictim ?? _grabbedTarget ?? _actionTarget;
        if (_activeFinisherVictimPaired && Time.time + 0.05f < _actionRecoverFallbackAt)
        {
            float remaining = Mathf.Max(0f, _actionRecoverFallbackAt - Time.time);
            LogAnimEvent(nameof(OnFinisherEnd), $"early paired end deferred for {remaining:F2}s");
            LogFinisherSyncPhase("end-deferred", victim, $"waiting {remaining:F2}s for paired sequence cleanup");
            return;
        }

        _actionRecoverResolved = true;
        LogFinisherSyncPhase("end", victim, "finisher end event");
        CompleteAction("finisher end");
    }

    // AnimationEvent entry point (kept parameterless for the animator). Everything else goes
    // through PlayFootstepImpact so a heavier step — the taunt stomps — can ask for one.
    public void OnFootstep()
    {
        PlayFootstepImpact(1f, 1f, skipWalkAttenuation: false);
    }

    private void PlayFootstepImpact(float volumeScale, float pitchScale, bool skipWalkAttenuation)
    {
        if (!IsOwner && !IsServer)
            return;

        if (Time.time < _nextFootstepAudioAt)
            return;

        _nextFootstepAudioAt = Time.time + FootstepAudioMinInterval;

        // A stomp is not a walk step: skipping the 0.82 non-running attenuation is most of where its
        // extra level comes from, because AudioSource volume clamps at 1 and the multiplier alone
        // would spend nearly all of it against that ceiling.
        bool running = _lastMotionState == BundyAnimatorParams.MotionRun;
        float walkAttenuation = skipWalkAttenuation || running ? 1f : 0.82f;
        float steppedVolume = Mathf.Clamp01(FootstepAudioVolume * walkAttenuation * volumeScale);
        // Pitched slightly low for a 3.4 m heavyweight. Applied to the authored per-surface pools as
        // well as to vanilla's clips: both are human-scale footstep libraries, so both want the same
        // shift to read as this character rather than as a person.
        float steppedPitch = UnityEngine.Random.Range(0.85f, 0.93f) * pitchScale;

        bool hasSurface = TryResolveFootstepSurface(out int surfaceIndex, out string surfaceTag);
        LogFootstepSurfaceMappingOnce();

        // Issue #209: Lawson's own per-material pools come first where he has authored one. Surface
        // detection was already here and already correct — the gap was that it only ever indexed
        // VANILLA's clips, so his files were loaded and never played.
        if (hasSurface && BundyAudioCatalog.TryResolveSurfaceFootstepCue(surfaceTag, out BundyAudioCue surfaceCue))
        {
            int surfaceClipIndex = BundyAudioCatalog.PickIndex(surfaceCue);
            if (surfaceClipIndex >= 0)
            {
                // Through PlayBundyAudio, so clip choice, the footstep emitter and noise propagation
                // all replicate exactly the way every other Bundy cue does — a locally-rolled clip
                // would have remote clients hearing a different footstep from the one the owner picked.
                PlayBundyAudio(surfaceCue, surfaceClipIndex, steppedVolume, steppedPitch);
                return;
            }
        }

        // 2026-07-22 round 3: fall back to the game's own player footstep set, surface-matched,
        // exactly like vanilla (MaskedPlayerEnemy.PlayFootstepSound) — the boots pool never sat right
        // in LC's mix, and vanilla ships more surface types than the three Lawson has authored.
        if (hasSurface &&
            (BundyPlugin.ModConfig?.BundyVanillaFootsteps?.Value ?? true) &&
            TryPickVanillaFootstepClip(surfaceIndex, out int vanillaClipIndex))
        {
            if (IsServer)
                PlayBundyVanillaFootstepClientRpc(surfaceIndex, vanillaClipIndex, steppedVolume, steppedPitch);
            return;
        }

        BundyAudioCue cue = FootstepAudioCue;
        int clipIndex = BundyAudioCatalog.PickIndex(cue, _lastFootstepClipIndex);
        if (clipIndex < 0)
            return;

        _lastFootstepClipIndex = clipIndex;
        PlayBundyAudio(
            cue,
            clipIndex,
            Mathf.Clamp01(FootstepAudioVolume * volumeScale),
            UnityEngine.Random.Range(0.96f, 1.04f) * pitchScale);
        LogFootstepAnimEvent(cue, clipIndex);
    }

    // Vanilla surface detection, copied from MaskedPlayerEnemy.GetMaterialStandingOn: raycast
    // down, tag-match the collider against StartOfRound.footstepSurfaces. Keeps the last known
    // surface when the ray misses (same as vanilla).
    //
    // Issue #209 split this from the clip pick. The surface answer is now needed by two consumers —
    // Bundy's own per-material pools and vanilla's clips — and only the second of them wants an
    // index into vanilla's arrays; the first wants the TAG.
    private bool TryResolveFootstepSurface(out int surfaceIndex, out string surfaceTag)
    {
        surfaceIndex = _vanillaFootstepSurfaceIndex;
        surfaceTag = null;

        FootstepSurface[] surfaces = StartOfRound.Instance?.footstepSurfaces;
        if (surfaces == null || surfaces.Length == 0)
            return false;

        var ray = new Ray(transform.position + Vector3.up, -Vector3.up);
        if (Physics.Raycast(ray, out RaycastHit hit, 6f, StartOfRound.Instance.walkableSurfacesMask, QueryTriggerInteraction.Ignore) &&
            !hit.collider.CompareTag(surfaces[_vanillaFootstepSurfaceIndex].surfaceTag))
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

        surfaceIndex = Mathf.Clamp(_vanillaFootstepSurfaceIndex, 0, surfaces.Length - 1);
        surfaceTag = surfaces[surfaceIndex].surfaceTag;
        return true;
    }

    private bool TryPickVanillaFootstepClip(int surfaceIndex, out int clipIndex)
    {
        clipIndex = -1;
        FootstepSurface[] surfaces = StartOfRound.Instance?.footstepSurfaces;
        if (surfaces == null || surfaceIndex < 0 || surfaceIndex >= surfaces.Length)
            return false;

        AudioClip[] clips = surfaces[surfaceIndex].clips;
        if (clips == null || clips.Length == 0)
            return false;

        clipIndex = UnityEngine.Random.Range(0, clips.Length);
        if (clipIndex == _lastFootstepClipIndex && clips.Length > 1)
            clipIndex = (clipIndex + 1) % clips.Length;
        _lastFootstepClipIndex = clipIndex;
        return true;
    }

    /// <summary>
    /// Issue #209: dump every vanilla footstep surface and which of Bundy's authored pools it
    /// resolved to, once per session.
    ///
    /// The tag list is serialized prefab data — it cannot be read outside a running game, so the
    /// mapping in <c>BundyAudioCatalog.TryResolveSurfaceFootstepCue</c> is substring matching against
    /// names rather than a table of indices. This line is what turns "grass never plays" from a
    /// silent mystery into one grep: an unmapped surface prints as <c>-&gt; vanilla</c> next to its
    /// real tag, which is the exact string the mapping needs adding.
    /// </summary>
    private void LogFootstepSurfaceMappingOnce()
    {
        if (_loggedFootstepSurfaceMapping || !ShouldLog(BundyLogCategory.Audio, 1))
            return;

        FootstepSurface[] surfaces = StartOfRound.Instance?.footstepSurfaces;
        if (surfaces == null || surfaces.Length == 0)
            return;

        _loggedFootstepSurfaceMapping = true;
        var sb = new StringBuilder();
        sb.Append("[BundyFootsteps] vanilla surfaces -> Bundy pools: ");
        for (int i = 0; i < surfaces.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            string tag = surfaces[i].surfaceTag;
            sb.Append(i).Append(':').Append(string.IsNullOrEmpty(tag) ? "<none>" : tag).Append("->");
            sb.Append(BundyAudioCatalog.TryResolveSurfaceFootstepCue(tag, out BundyAudioCue cue)
                ? cue.ToString()
                : "vanilla");
        }

        LogInfo(BundyLogCategory.Audio, 1, sb.ToString());
    }

    [ClientRpc]
    public void PlayBundyVanillaFootstepClientRpc(int surfaceIndex, int clipIndex, float volume, float pitch)
    {
        FootstepSurface[] surfaces = StartOfRound.Instance?.footstepSurfaces;
        if (surfaces == null || surfaceIndex < 0 || surfaceIndex >= surfaces.Length)
            return;

        AudioClip[] clips = surfaces[surfaceIndex].clips;
        if (clips == null || clipIndex < 0 || clipIndex >= clips.Length)
            return;

        ResolveRuntimeReferences();
        AudioSource source = _footstepAudioSource ?? creatureSFX ?? creatureVoice;
        if (source == null)
            return;

        AudioClip clip = clips[clipIndex];
        source.pitch = Mathf.Clamp(pitch, 0.5f, 1.5f);
        source.PlayOneShot(clip, Mathf.Clamp01(volume));
        WalkieTalkie.TransmitOneShotAudio(source, clip, Mathf.Clamp01(volume));
        // #212: was a hardcoded (8 m, 0.4) here against PlayBundyAudio's (8 m, 0.35) for the same
        // event. One emitter now, so both paths propagate identically and #213's sprint/taunt lift
        // applies whichever clip set is playing.
        EmitFootstepNoise(source.transform.position);
    }

    public void OnEmoteBeat()
    {
        LogAnimEvent(nameof(OnEmoteBeat), "mimicry punish window not implemented");
    }

    public void OnContextHit()
    {
        LogAnimEvent(nameof(OnContextHit), "contextual world interaction not implemented");
    }

    public void OnPullStartDone()
    {
        if (_actionKind != ActionKind.PullStart)
            return;

        CompleteAction("pull start done");
    }

    public void OnStaggerEnd()
    {
        // Dead in production (r40 rebake stripped all animation events) but kept wired: if events
        // ever return, this must still route through the ONE knockdown exit (C1.2).
        ExitStaggerKnockdown("stagger end anim event");
        if (_actionKind != ActionKind.Stagger)
            return;

        CompleteAction("stagger end");
    }

    // Job 1: per-swing AnimationEvent tracing — 147 lines in run 5, every one at Warning level, which
    // is why they survived earlier attempts to thin the log by severity. Anim category, per-tick tier.
    protected void LogAnimEvent(string eventName, string message)
    {
        if (!ShouldLog(BundyLogCategory.Anim, 2)) return;
        LogWarning($"AnimationEvent {eventName}: {message}");
    }

    private void LogFootstepAnimEvent(BundyAudioCue cue, int clipIndex)
    {
        if (!DebugLogs)
            return;

        if (Time.unscaledTime < _nextFootstepAnimEventLogAt)
        {
            _suppressedFootstepAnimEventLogs++;
            return;
        }

        int suppressed = _suppressedFootstepAnimEventLogs;
        _suppressedFootstepAnimEventLogs = 0;
        _nextFootstepAnimEventLogAt = Time.unscaledTime + 2f;

        string suffix = suppressed > 0 ? $"; suppressed={suppressed}" : string.Empty;
        LogInfo($"AnimationEvent {nameof(OnFootstep)}: played footstep cue={cue}, clipIndex={clipIndex}{suffix}");
    }

    private bool DebugLogs => BundyPlugin.DebugLogsEnabled;
    private bool SyncDebugLogs => DebugLogs;
}
