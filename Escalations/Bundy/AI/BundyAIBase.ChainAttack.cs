using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.AI;

// Issues #281/#282: multi-rep attack sequences — the chain attack (3-4 alternating L/R swings
// that keep advancing on the target) and the spin loop (the FullSpinSwing clip looped into a
// sustained spin). Runtime-driven, no bundle change: both reuse existing attack states.
//
// The whole sequence lives inside ONE ActionKind.Attack window. A rep handoff resets exactly
// the per-swing state a fresh BeginBasicAttack would have set — contact latch, blade hitbox
// window, impact/recover fallbacks, attack phase, lunge aim — and re-stamps _actionStartedAt
// so every downstream "elapsed since action start" consumer (lunge aim freeze at
// AttackLungeCommitFraction, wall-impact eligibility, impact logging) measures within the rep.
// The LAST rep keeps its normal recover fallback, so the sequence exits through the ordinary
// OnAttackRecover -> CompleteAction -> pursuit path with no new exit plumbing.
//
// The two sequences differ in animation entry, pattern, cadence, and movement:
// - CHAIN (#281): pattern alternates L/R and reads as one flowing figure-8. Reps 2+ do NOT
//   re-fire the AnyState trigger — that entered the clip at frame 0 and snapped him back to the
//   swing's neutral guard pose mid follow-through. Instead the server fans out
//   PlayChainAttackSwingCrossFadeClientRpc, which blends from the live pose straight into the
//   next swing ChainAttackEntryOffsetClipFraction of the way in, skipping the wind-in. The
//   handoff lands at ChainAttackHandoffClipFraction of the previous clip's playhead, well past
//   its contact/active window, so the rep has already resolved hit-or-miss. Because the rep
//   starts partway into the clip, its impact/recover/active-window timings are shifted earlier
//   by that same entry offset so the damage window still tracks the visible blade arc.
//   Every rep re-commits the lunge at the LIVE player, and between the impact and the next rep
//   he glides at the victim at action-walk speed instead of holding still (see
//   IsChainAttackGlideActive) — the cadence reads surge, glide, surge.
// - SPIN LOOP (#282): pattern is the FullSpinSwing alone, re-entered through PlayAttack (the
//   AnyState re-fire) at ~0.8 of the clip so the revolution mostly completes before blending
//   into the next one, and reps do NOT lunge — DriveActionApproach's non-lunge branch walks him
//   slowly at the target mid-spin. One resolution per revolution (per-rep _attackHitConfirmed)
//   is the "one revolution cannot multi-hit" dedup; successive revolutions re-arm and can hit
//   again. Standing outside the blade never latches, so it never damages.
//
// Interruptions end a sequence the same way they end any attack: staggers/knockdowns go through
// BeginTimedAction (which clears the sequence), a chainsaw wall impact or saw stall cancels the
// remaining reps and lets the stall/recovery own the exit, and a dead or vanished victim stops
// the sequence at the current rep's recovery.
public abstract partial class BundyAIBase
{
    protected enum ComboAttackKind
    {
        None,
        Chain,
        SpinLoop,
    }

    // Sequence state. _chainAttackRepsRemaining > 0 means more swings are owed after the current
    // one; 0 is "not sequencing / last rep is the current one".
    private int _chainAttackRepsRemaining;
    private int _chainAttackPatternCursor;
    private int _chainAttackRepNumber;
    private int _chainAttackTotalReps;
    private float _chainAttackNextRepAt;
    private int[] _chainAttackActivePattern;
    private bool _chainAttackRepsLunge;
    private string _chainAttackLabel = "BundyChainAttack";

    // #281: the impact delay of the CURRENT chain rep, already shifted earlier by the crossfade
    // entry offset, or <= 0 when no crossfaded rep is running (opener, spin loop, lone attack).
    // Everything that measures the swing clock against the re-stamped _actionStartedAt — the damage
    // fallbacks here, and the lunge arrival/aim-freeze clocks in RootSync — must read the SAME
    // number, or the surge and the aim freeze aim at a contact moment the rep no longer has.
    private float _chainAttackRepImpactDelayOverride = -1f;

    /// <summary>Impact delay of the swing currently in flight: the chain rep's shifted delay when
    /// one is running, otherwise the authored delay for the active attack index.</summary>
    private float EffectiveBasicAttackImpactDelay
    {
        get
        {
            if (_chainAttackRepImpactDelayOverride > 0f)
                return _chainAttackRepImpactDelayOverride;

            return _activeAttackIndex >= 0 ? GetBasicAttackImpactDelay(_activeAttackIndex) : -1f;
        }
    }

    /// <summary>
    /// The lunge wind-up hold, rescaled to keep the share of the pre-contact budget it was tuned
    /// for. A crossfaded rep's contact arrives entryOffset sooner, so an unscaled 0.30s hold would
    /// eat 37% of the remaining travel time instead of the authored ~26%.
    /// </summary>
    private float EffectiveAttackLungeWindupHoldSeconds
    {
        get
        {
            float hold = Mathf.Max(0f, AttackLungeWindupHoldSeconds);
            if (hold <= 0f || _chainAttackRepImpactDelayOverride <= 0f || _activeAttackIndex < 0)
                return hold;

            float authoredDelay = GetBasicAttackImpactDelay(_activeAttackIndex);
            if (authoredDelay <= 0f)
                return hold;

            return hold * Mathf.Clamp01(_chainAttackRepImpactDelayOverride / authoredDelay);
        }
    }

    // Floor for a rep's shifted impact delay. The entry offset is subtracted from the authored
    // impact/recover delays, and a rep still needs a non-zero window between "swing started" and
    // "blade is live" for the contact latch and the fallback resolver to behave.
    private const float ChainAttackMinRepImpactSeconds = 0.10f;

    // Floor for the handoff interval, so a mistuned fraction pair cannot re-fire every frame.
    private const float ChainAttackMinHandoffSeconds = 0.20f;

    /// <summary>Playhead fraction of the CURRENT swing clip at which the next chain rep takes
    /// over. Must sit past the clip's contact moment so the rep has resolved hit-or-miss.</summary>
    protected virtual float ChainAttackHandoffClipFraction => 0.70f;

    /// <summary>How far into the NEXT swing clip a chain rep is entered, as a fraction of that
    /// clip. Skips the neutral wind-in so the swings read as one continuous figure-8.</summary>
    protected virtual float ChainAttackEntryOffsetClipFraction => 0.15f;

    /// <summary>Blend length (seconds) of the chain rep crossfade.</summary>
    protected virtual float ChainAttackCrossFadeSeconds => 0.30f;

    /// <summary>Length in seconds of the clip a basic-attack index plays. Only the chain handoff
    /// consumes it; variants without measured clips can leave the default.</summary>
    protected virtual float GetBasicAttackClipLengthSeconds(int attackIndex)
    {
        return 2.0f;
    }

    /// <summary>Alternation pattern of basic-attack indices for the chain attack, or null when
    /// the variant has no chain. The chainsaw supplies { LeftSwing, RightSwing }.</summary>
    protected virtual int[] ChainAttackPattern => null;

    /// <summary>Server-side roll for replacing a lone basic attack with a chain. 0 disables.</summary>
    protected virtual float ChainAttackChance => 0f;

    protected virtual int ChainAttackMinReps => 3;
    protected virtual int ChainAttackMaxReps => 4;

    /// <summary>The basic-attack index the spin loop repeats, or -1 when the variant has no
    /// spin loop. The chainsaw supplies FullSpinSwing.</summary>
    protected virtual int SpinLoopAttackIndex => -1;

    /// <summary>Server-side roll for upgrading an eligible basic attack into the spin loop.
    /// Only rolled when he is surrounded or the target is at point-blank range. 0 disables.</summary>
    protected virtual float SpinLoopChance => 0f;

    protected virtual int SpinLoopMinRevolutions => 3;
    protected virtual int SpinLoopMaxRevolutions => 4;

    /// <summary>Seconds from one revolution's start to the next re-trigger. Tuned to ~0.8 of the
    /// spin clip so the revolution mostly completes before the next one blends in.</summary>
    protected virtual float SpinLoopRepIntervalSeconds => 2.48f;

    /// <summary>
    /// Rolled at the BeginBasicAttack call site in TryBeginAttackOpportunity. The chain requires
    /// the lunge (its advance IS DriveActionApproach's lunge branch); both require the
    /// physical-contact damage path — per-rep damage windows are the acceptance criterion, and
    /// only the latch path provides them.
    /// </summary>
    private ComboAttackKind RollComboAttack(int nearbyLivingPlayerCount, float pathDistance)
    {
        if (!RequiresPhysicalBasicAttackContact || !BasicActionsEnabled)
            return ComboAttackKind.None;

        // Spin first: it is the rarer, situational pick (surrounded, or point-blank where the
        // alternating chain has no room to advance).
        if (SpinLoopAttackIndex >= 0 &&
            Mathf.Clamp01(SpinLoopChance) > 0f &&
            (nearbyLivingPlayerCount >= 2 || pathDistance <= BasicAttackRange + 0.5f) &&
            UnityEngine.Random.value <= Mathf.Clamp01(SpinLoopChance))
        {
            return ComboAttackKind.SpinLoop;
        }

        int[] pattern = ChainAttackPattern;
        if (pattern != null && pattern.Length >= 2 &&
            UsesAttackLunge &&
            Mathf.Clamp01(ChainAttackChance) > 0f &&
            UnityEngine.Random.value <= Mathf.Clamp01(ChainAttackChance))
        {
            return ComboAttackKind.Chain;
        }

        return ComboAttackKind.None;
    }

    private int GetComboOpenerAttackIndex(ComboAttackKind combo)
    {
        if (combo == ComboAttackKind.Chain && ChainAttackPattern != null && ChainAttackPattern.Length > 0)
            return ChainAttackPattern[0];
        if (combo == ComboAttackKind.SpinLoop)
            return SpinLoopAttackIndex;
        return -1;
    }

    /// <summary>Called from BeginBasicAttack once the timed action exists (BeginTimedAction
    /// defensively clears sequence state, so arming must come after it).</summary>
    private void ArmComboAttack(ComboAttackKind combo, int openerAttackIndex)
    {
        if (combo == ComboAttackKind.Chain)
        {
            _chainAttackActivePattern = ChainAttackPattern;
            _chainAttackTotalReps = UnityEngine.Random.Range(ChainAttackMinReps, ChainAttackMaxReps + 1);
            _chainAttackRepsLunge = true;
            _chainAttackLabel = "BundyChainAttack";
            // The opener plays from clip frame 0 (AnyState trigger), so its playhead and its
            // elapsed time are the same thing — wait the full handoff fraction of its clip.
            _chainAttackNextRepAt = Time.time + Mathf.Max(
                ChainAttackMinHandoffSeconds,
                ResolvedChainAttackHandoffFraction * GetBasicAttackClipLengthSeconds(openerAttackIndex));
        }
        else
        {
            _spinLoopPatternScratch[0] = openerAttackIndex;
            _chainAttackActivePattern = _spinLoopPatternScratch;
            _chainAttackTotalReps = UnityEngine.Random.Range(SpinLoopMinRevolutions, SpinLoopMaxRevolutions + 1);
            _chainAttackRepsLunge = false;
            _chainAttackLabel = "BundySpinLoop";
            _chainAttackNextRepAt = Time.time + Mathf.Max(0.5f, SpinLoopRepIntervalSeconds);
        }

        _chainAttackRepsRemaining = _chainAttackTotalReps - 1;
        _chainAttackRepNumber = 1;
        _chainAttackPatternCursor = 0;
        LogInfo(
            BundyLogCategory.Combat,
            0,
            $"{_chainAttackLabel} begin: reps={_chainAttackTotalReps}, opener={openerAttackIndex}, " +
            $"target={PlayerLabel(_activeBasicVictim)}.");
    }

    private readonly int[] _spinLoopPatternScratch = new int[1];

    private float ResolvedChainAttackHandoffFraction => Mathf.Clamp(ChainAttackHandoffClipFraction, 0.1f, 0.99f);

    // Kept strictly below the handoff fraction: entering a clip at or past the point the NEXT
    // handoff is measured to would leave zero swing between reps.
    private float ResolvedChainAttackEntryOffsetFraction =>
        Mathf.Clamp(ChainAttackEntryOffsetClipFraction, 0f, ResolvedChainAttackHandoffFraction - 0.05f);

    /// <summary>
    /// #281: true while a chain sequence still owes swings and the current rep's impact has been
    /// resolved. MaintainActionMovement reads it to keep walking at the live victim instead of
    /// holding still through the recovery — the glide between two surges. Server-side only, like
    /// the rest of the action movement code.
    /// </summary>
    private bool IsChainAttackGlideActive()
    {
        return _chainAttackRepsRemaining > 0 &&
            _chainAttackRepsLunge &&
            _actionKind == ActionKind.Attack &&
            !_lungeActive &&
            !_sawStalled &&
            !_chainsawWallImpactResolved;
    }

    private void ClearChainAttack()
    {
        _chainAttackRepsRemaining = 0;
        _chainAttackPatternCursor = 0;
        _chainAttackRepNumber = 0;
        _chainAttackTotalReps = 0;
        _chainAttackNextRepAt = 0f;
        _chainAttackActivePattern = null;
        _chainAttackRepsLunge = false;
        _chainAttackLabel = "BundyChainAttack";
        // BeginTimedAction clears the sequence before every new action, so dropping the override
        // here is also what puts the chain OPENER (and every lone attack) back on the authored
        // impact delay.
        _chainAttackRepImpactDelayOverride = -1f;
    }

    private void CancelChainAttack(string reason)
    {
        if (_chainAttackRepsRemaining <= 0)
            return;

        LogInfo(
            BundyLogCategory.Combat,
            0,
            $"{_chainAttackLabel} cancelled at rep {_chainAttackRepNumber}/{_chainAttackTotalReps}: {reason}.");
        ClearChainAttack();
    }

    /// <summary>Per-frame sequence driver, run alongside UpdateActionFallbacks. The current
    /// rep's own fallbacks still resolve its impact; this only fires the NEXT rep on cadence.</summary>
    private void UpdateChainAttack()
    {
        if (_chainAttackRepsRemaining <= 0)
            return;

        if (_actionKind != ActionKind.Attack)
        {
            // The action ended under us (killed target completing the action, external cleanup).
            ClearChainAttack();
            return;
        }

        // A wall impact or saw stall owns the recovery read; swinging through it would undo the
        // stall cycle's whole point.
        if (_chainsawWallImpactResolved || _sawStalled)
        {
            CancelChainAttack(_sawStalled ? "saw stalled" : "chainsaw wall impact");
            return;
        }

        if (Time.time < _chainAttackNextRepAt)
            return;

        PlayerControllerB victim = _activeBasicVictim ?? _actionTarget;
        if (!IsLivingPlayer(victim))
        {
            CancelChainAttack("victim lost");
            return;
        }

        BeginNextChainAttackRep(victim);
    }

    private void BeginNextChainAttackRep(PlayerControllerB victim)
    {
        int[] pattern = _chainAttackActivePattern;
        if (pattern == null || pattern.Length == 0)
        {
            ClearChainAttack();
            return;
        }

        _chainAttackPatternCursor = (_chainAttackPatternCursor + 1) % pattern.Length;
        int attackIndex = pattern[_chainAttackPatternCursor];
        _chainAttackRepsRemaining--;
        _chainAttackRepNumber++;

        _activeAttackIndex = attackIndex;
        _lastAttackIndex = attackIndex;

        float clipLength = GetBasicAttackClipLengthSeconds(attackIndex);
        // Chain reps are crossfaded into partway through the clip; the spin loop still re-enters
        // from frame 0 through PlayAttack, so its offset is zero and its timings are untouched.
        float entryOffsetSeconds = _chainAttackRepsLunge
            ? Mathf.Max(0f, ResolvedChainAttackEntryOffsetFraction * clipLength)
            : 0f;

        // The authored impact/recover delays are measured from clip frame 0. This rep's clip is
        // ALREADY entryOffsetSeconds along, so subtract the offset: without it the damage window
        // would open that much after the blade has visibly swung through.
        float impactDelay = Mathf.Max(
            ChainAttackMinRepImpactSeconds,
            GetBasicAttackImpactDelay(attackIndex) - entryOffsetSeconds);
        float recoverDelay = Mathf.Max(
            impactDelay + 0.1f,
            GetBasicAttackRecoverDelay(attackIndex) - entryOffsetSeconds);

        // Publish the shifted delay before anything reads the swing clock this frame: the lunge
        // arrival timing and the aim freeze in DriveActionApproach/IsLungeAimFrozen measure against
        // the same re-stamped _actionStartedAt and must use this rep's contact moment, not the
        // authored one. Spin-loop reps have no offset and stay on the authored delay.
        _chainAttackRepImpactDelayOverride = _chainAttackRepsLunge ? impactDelay : -1f;

        if (_chainAttackRepsLunge)
        {
            // Handoff is measured on the clip PLAYHEAD, and this rep starts at the entry offset,
            // so only the remaining span up to the handoff fraction is waited out.
            float handoffSpan = (ResolvedChainAttackHandoffFraction - ResolvedChainAttackEntryOffsetFraction) * clipLength;
            _chainAttackNextRepAt = Time.time + Mathf.Max(ChainAttackMinHandoffSeconds, handoffSpan);
        }
        else
        {
            _chainAttackNextRepAt = Time.time + Mathf.Max(0.5f, SpinLoopRepIntervalSeconds);
        }

        // The per-swing reset — the same slate BeginBasicAttack gives a fresh attack.
        _actionStartedAt = Time.time;
        _attackHitConfirmed = false;
        _hasLastAttackContactPoint = false;
        _actionImpactResolved = false;
        _actionRecoverResolved = false;
        _actionImpactFallbackAt = Time.time + impactDelay;
        _actionRecoverFallbackAt = Time.time + recoverDelay;
        _attackActiveWindowStartAt = Time.time + Mathf.Max(0f, impactDelay - AttackActiveLeadSeconds);
        _attackActiveWindowEndAt = Time.time + impactDelay + AttackActiveTrailSeconds;
        _chainsawWallImpactResolved = false;
        ClearBasicAttackContactLatch();
        InvalidateAttackContactColliderCache();
        ClearChainsawBladeHitboxWindow("sequence rep handoff");

        if (_chainAttackRepsLunge)
        {
            // Chain: re-engage the advance. The previous rep's impact/recovery cleared the lunge,
            // so commit a fresh one at the live player (knockback usually moved them) unless the
            // path closed.
            bool lungePathClear = HasClearLungePathTo(victim.transform.position, victim);
            _lungeActive = UsesAttackLunge && lungePathClear;
            if (_lungeActive)
                CommitLungeAim(victim.transform.position);
        }
        else
        {
            // Spin loop: no surge mid-spin. With the lunge off, DriveActionApproach's non-lunge
            // branch walks him slowly at the target while the revolutions play.
            _lungeActive = false;
            ClearLungeAim();
        }

        EnterAttackPhase(AttackPhase.Windup, $"{_chainAttackLabel} rep {_chainAttackRepNumber}/{_chainAttackTotalReps}");

        if (IsServer && UsesMovementLoopSwingModulation && !_sawStalled)
            PulseChainsawSwellClientRpc(impactDelay + 0.3f);

        if (_chainAttackRepsLunge)
        {
            // Chain: blend from the live pose into the next swing. Keep the replicated AttackIndex
            // parameter in step with the state we crossfaded to, but do NOT set DoAttack — the
            // AnyState transition it drives is exactly the frame-0 snap this replaces.
            SetAnimatorInteger(BundyAnimatorParams.AttackIndex, attackIndex);
            float blendSeconds = Mathf.Max(0.01f, ChainAttackCrossFadeSeconds);
            if (IsServer)
                PlayChainAttackSwingCrossFadeClientRpc(attackIndex, blendSeconds, entryOffsetSeconds);
            else
                ApplyChainAttackSwingCrossFade(attackIndex, blendSeconds, entryOffsetSeconds);
        }
        else
        {
            PlayAttack(attackIndex);
        }

        LogInfo(
            BundyLogCategory.Combat,
            0,
            $"{_chainAttackLabel} rep {_chainAttackRepNumber}/{_chainAttackTotalReps}: attackIndex={attackIndex}, " +
            $"target={PlayerLabel(victim)}, dist={Vector3.Distance(transform.position, victim.transform.position):F2}m, " +
            $"lunge={_lungeActive}.");
    }
}
