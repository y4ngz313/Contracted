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

public abstract partial class BundyAIBase
{
    private bool TryRunBatterySabotageRoute()
    {
        bool strangerTargetSheltered = IsStrangerTargetShelteredInClosedShip();
        if (_strangerTargetingActive && !strangerTargetSheltered)
        {
            ExitBatterySabotageRoute(GetStrangerBatterySiegeExitReason());
            return false;
        }

        if (!UsesBatterySabotageDiversion)
        {
            ExitBatterySabotageRoute("battery broke");
            return false;
        }

        bool forcedByBlockedShipTarget = TryForceBatterySabotageForBlockedShipTarget();
        if (!forcedByBlockedShipTarget && !BundyShipSystemsBridge.IsBatterySabotageTargetActive(this))
        {
            ExitBatterySabotageRoute("battery broke");
            return false;
        }

        if (!_strangerTargetingActive &&
            BundyShipSystemsBridge.TryGetBatterySabotageInterruptTarget(this, out PlayerControllerB interruptTarget))
        {
            _batterySabotageRouteActive = false;
            if (BundyShipSystemsBridge.ShouldReleaseBatterySabotageInterrupt(this, interruptTarget))
            {
                BundyShipSystemsBridge.ClearBatterySabotageInterrupt(this);
                ClearBatterySabotagePlayerTarget(interruptTarget);
            }
            else
            {
                EnterBatterySabotageInterrupt(interruptTarget);
                return false;
            }
        }

        if (IsActionLocked())
            return false;

        ClearBatterySabotagePlayerTarget(null, preservePrimaryTarget: true);

        if (!BundyShipSystemsBridge.TryGetBatterySabotageDestination(this, out Vector3 destination))
        {
            ExitBatterySabotageRoute("battery broke");
            return false;
        }

        DriveBatterySabotageDestination(destination);
        RunBaseAIIntervalIfSafe();
        return true;
    }

    private bool TryForceBatterySabotageForBlockedShipTarget()
    {
        bool strangerTargetSheltered = IsStrangerTargetShelteredInClosedShip();
        if (_strangerTargetingActive && !strangerTargetSheltered)
            return false;

        PlayerControllerB target = _strangerTargetingActive ? _strangerTarget : _primaryTarget;
        if (!IsLivingPlayer(target) || !PlayerIsAtShip(target))
            return false;

        if (!strangerTargetSheltered && HasDirectAccessLineToPlayer(target))
            return false;

        bool wasActive = _batterySabotageRouteActive;
        bool forced = BundyShipSystemsBridge.TryForceBatterySabotageTarget(this);
        if (forced && (!wasActive || Time.time >= _nextBatterySabotageForcedLogAt))
        {
            _nextBatterySabotageForcedLogAt = Time.time + BatterySabotageDiagnosticIntervalSeconds;
            LogInfo($"Battery sabotage forced: target {PlayerLabel(target)} is at the ship with no direct access line.");
        }
        return forced;
    }

    private void ExitBatterySabotageRoute(string reason)
    {
        bool wasActive = _batterySabotageRouteActive;
        _batterySabotageRouteActive = false;
        if (!wasActive)
            return;

        OnBatterySiegeAnimationStopped();
        if (DebugLogs)
            LogInfo($"[BundySiege] route exited reason={reason}");
    }

    private string GetStrangerBatterySiegeExitReason()
    {
        if (!IsLivingPlayer(_strangerTarget))
            return "target died";
        if (!PlayerIsAtShip(_strangerTarget))
            return "target left ship";
        if (StartOfRound.Instance != null && !StartOfRound.Instance.hangarDoorsClosed)
            return "doors opened";
        return "battery broke";
    }

    private bool PlayerIsAtShip(PlayerControllerB player)
    {
        return IsLivingPlayer(player) && (player.isInHangarShipRoom || player.isInElevator);
    }

    private bool HasDirectAccessLineToPlayer(PlayerControllerB player)
    {
        if (!IsLivingPlayer(player) || !AgentReadyForMovement())
            return true;

        Vector3 destination = player.transform.position;
        if (TryFindNavigationObstacle(destination, player, out _, out _))
            return false;

        try
        {
            NavMeshPath path = new NavMeshPath();
            if (agent != null && agent.enabled && agent.isOnNavMesh && agent.CalculatePath(destination, path))
                return path.status == NavMeshPathStatus.PathComplete;
        }
        catch
        {
            return true;
        }

        return true;
    }

    private void EnterBatterySabotageInterrupt(PlayerControllerB interruptTarget)
    {
        if (!IsLivingPlayer(interruptTarget))
            return;

        _primaryTarget = interruptTarget;
        _pursuitZeroTarget = null;
        EnterPursuitMode(PursuitMode.PrimaryStalk, interruptTarget);
        LogInfo($"Battery sabotage interrupt: diverting to {PlayerLabel(interruptTarget)}.");
    }

    private void ClearBatterySabotagePlayerTarget(PlayerControllerB releasedTarget, bool preservePrimaryTarget = false)
    {
        if (_pursuitMode == PursuitMode.NonTargetInterrupt)
        {
            _pursuitZeroTarget = null;
        }

        if (preservePrimaryTarget)
        {
            targetPlayer = null;
            _pursuitMode = PursuitMode.PrimaryStalk;
            return;
        }

        if (releasedTarget == null || _primaryTarget == releasedTarget || targetPlayer == releasedTarget)
        {
            _primaryTarget = null;
            targetPlayer = null;
            _pursuitMode = PursuitMode.PrimaryStalk;
        }
    }

    private void DriveBatterySabotageDestination(Vector3 destination)
    {
        bool wasActive = _batterySabotageRouteActive;
        _batterySabotageRouteActive = true;
        _batterySabotageDestination = destination;

        if (!AgentReadyForMovement())
        {
            if (!wasActive && DebugLogs)
                LogInfo($"[BundySiege] route engaged destination={FormatVector(destination)}");
            StopMovement();
            SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
            return;
        }

        Vector3 resolved = TryGetSmartDestination(destination, out Vector3 smartDestination)
            ? smartDestination
            : ResolveObstacleAwareDestination(destination, null, allowDetour: true);
        _batterySabotageDestination = resolved;
        if (!wasActive && DebugLogs)
            LogInfo($"[BundySiege] route engaged destination={FormatVector(resolved)}");
        float distance = Vector3.Distance(transform.position, resolved);
        if (distance <= StopDistance)
        {
            StopMovement();
            FaceActionPoint(resolved);
            SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
            return;
        }

        bool running = distance > RunThreshold;
        ApplyGaitSpeed(running);
        agent.angularSpeed = AgentAngularSpeedDegrees;
        agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);
        if (!UsesManualTargetFacing)
            agent.updateRotation = !ManualPursuitControlsAgentRotation;
        else
            FaceActionPoint(resolved);

        if (agent.isOnNavMesh)
            agent.isStopped = false;

        movingTowardsTargetPlayer = false;
        moveTowardsDestination = true;
        // Siege layer, not pursuit: the battery route deliberately keeps its own destination write
        // (movement authority, plan step 8, scope boundary).
        SetDestinationToPosition(resolved);
        SetLocalBehaviourState(BehaviourStateTracking);
        SetMotionStateIfChanged(running ? BundyAnimatorParams.MotionRun : BundyAnimatorParams.MotionWalk);
    }

    private void TryEnterNonTargetInterrupt()
    {
        PlayerControllerB interruptTarget = FindNonTargetInterruptPlayer();
        if (interruptTarget == null)
            return;

        _pursuitZeroTarget = interruptTarget;
        _pursuitZeroAttackAttentionUntil = float.NegativeInfinity;
        EnterPursuitMode(PursuitMode.NonTargetInterrupt, interruptTarget);
        LogInfo($"Pursuit 0 interrupt: diverting to {PlayerLabel(interruptTarget)}; returnMode={PursuitMode.PrimaryStalk}.");
    }

    private void TryEnterNonTargetInterruptFromAttack(PlayerControllerB attacker)
    {
        if (!IsReachableHuntTarget(attacker) || attacker == _primaryTarget)
            return;

        _pursuitZeroTarget = attacker;
        _pursuitZeroAttackAttentionUntil = Time.time + Mathf.Max(0.25f, SightMemoryGraceSeconds);
        EnterPursuitMode(PursuitMode.NonTargetInterrupt, attacker);
        LogInfo(
            $"Pursuit 0 attack interrupt: diverting to {PlayerLabel(attacker)} without changing " +
            $"primary={PlayerLabel(_primaryTarget)}.");
    }

    private void CheckPursuitZeroRelease()
    {
        if (_pursuitMode != PursuitMode.NonTargetInterrupt)
            return;

        if (!IsReachableHuntTarget(_pursuitZeroTarget))
        {
            ExitPursuitZero("non-target dead, gone, or unreachable");
            return;
        }

        if (HasLineOfSightToPlayer(_pursuitZeroTarget, EffectiveNonTargetInterruptRange))
            return;

        if (Time.time <= _pursuitZeroAttackAttentionUntil)
            return;

        ExitPursuitZero("lost direct sight of non-target");
    }

    private void ExitPursuitZero(string reason)
    {
        LogInfo($"Pursuit 0 exit: {reason}.");
        _pursuitZeroTarget = null;
        _pursuitZeroAttackAttentionUntil = float.NegativeInfinity;

        PlayerControllerB nextTarget = null;
        if (_strangerTargetingActive && IsLivingPlayer(_strangerTarget))
        {
            _primaryTarget = _strangerTarget;
            targetPlayer = _strangerTarget;
            nextTarget = _strangerTarget;
            LogInfo($"Pursuit 0 resume: assigned Stranger target {PlayerLabel(nextTarget)} after {reason}.");
        }
        else if (TryPrepareStrangerSnapshotTarget())
        {
            nextTarget = _primaryTarget;
        }

        if (nextTarget == null)
        {
            StopMovement();
            return;
        }

        EnterPursuitMode(PursuitMode.PrimaryStalk, nextTarget);
    }

    private void EnterPursuitMode(PursuitMode mode, PlayerControllerB newTarget)
    {
        PursuitMode previousMode = _pursuitMode;
        PlayerControllerB previousTarget = targetPlayer;

        _pursuitMode = mode;
        targetPlayer = newTarget;

        if (UsesPullStart &&
            (mode == PursuitMode.PrimaryStalk || mode == PursuitMode.NonTargetInterrupt) &&
            (!UsesFiniteMovementLoopAudioWindow || !IsMovementLoopAudioWindowReady()))
        {
            BeginPullStart(newTarget);
        }

        bool pursuitChanged = previousMode != mode || previousTarget != newTarget;
        bool shouldSpotPursuitChange =
            !_strangerTargetingActive ||
            mode == PursuitMode.NonTargetInterrupt;
        if (pursuitChanged && shouldSpotPursuitChange)
            QueueVocal(BundyAudioCue.Spot, DialogueAfterEventDelaySeconds, $"pursuit {mode}");
        if (pursuitChanged &&
            _strangerTargetingActive &&
            IsServer &&
            _movementLoopAudioRequested)
        {
            SetMovementLoopAudioRequested(true, forceUpdate: true);
        }
    }

    private void MarkGrabConnected(PlayerControllerB victim)
    {
        if (!IsLivingPlayer(victim))
            return;

        if (_pursuitMode == PursuitMode.NonTargetInterrupt && victim == _pursuitZeroTarget)
            return;

        if (_strangerTargetingActive && victim == _strangerTarget)
            AcquireStrangerContact(victim.transform.position, "grab connected");
    }

    private void HandleKilledTarget(PlayerControllerB victim, string reason)
    {
        if (victim == null)
            return;

        if (victim == _pursuitZeroTarget && victim != _primaryTarget)
        {
            ExitPursuitZero($"{reason} killed non-target interrupt");
            return;
        }

        if (victim != _primaryTarget)
            return;

        PlayerControllerB nextTarget = _strangerTargetingActive ? null : SelectRandomLivingPlayer(victim);
        _primaryTarget = nextTarget;
        _pursuitZeroTarget = null;
        if (_strangerTargetingActive)
        {
            _strangerTarget = null;
            _hasStrangerLastKnownPosition = false;
            ClearStrangerDestinationOverride();
            ResetStrangerExitLurk();
            _strangerLiveTargetUntil = float.NegativeInfinity;
            _strangerHuntHoldUntil = float.NegativeInfinity;
            // #307: the pursuit-memory tier refers to a player who no longer exists.
            _strangerRecentMemoryUntil = float.NegativeInfinity;
            _strangerLastKnownVelocity = Vector3.zero;
        }

        if (nextTarget == null)
        {
            targetPlayer = null;
            _pursuitMode = PursuitMode.PrimaryStalk;
            StopMovement();
            LogInfo($"Primary target killed by {reason}; waiting for Stranger director to assign replacement target.");
            return;
        }

        EnterPursuitMode(PursuitMode.PrimaryStalk, nextTarget);
        LogInfo($"Primary target killed by {reason}; new target selected: {PlayerLabel(nextTarget)}.");
    }

    private bool TryBeginAttackOpportunity(PlayerControllerB huntTarget)
    {
        // Chainsaw finishers no longer roll against health. A qualifying landed hit owns one exact
        // six-second window; while it is active, Bundy commits to that victim and either reaches the
        // live front slot or lets the opportunity expire without substituting another basic attack.
        if (UsesForcedCriticalFinisherGate &&
            TryGetForcedCriticalFinisherTarget(out PlayerControllerB forcedCriticalVictim))
        {
            if (!FinishersEnabled)
            {
                LogAttackGateDiagnostic(
                    $"BundyForcedCritical finisher unavailable; victim pool not ready for {PlayerLabel(forcedCriticalVictim)}.");
                return false;
            }

            if (!TryGetCompleteNavigationPathDistanceCached(
                    forcedCriticalVictim.transform.position,
                    out float forcedCriticalPathDistance))
            {
                LogAttackGateDiagnostic(
                    $"BundyForcedCritical no complete path; target={PlayerLabel(forcedCriticalVictim)}.");
                return false;
            }

            BeginForcedCriticalFinisherApproach(forcedCriticalVictim, forcedCriticalPathDistance);
            return true;
        }

        if (Time.time < _nextAttackAllowedAt)
            return false;

        // Issue #205 hold, narrowed by #208. The hold now applies to the WIND-DOWN only: sprints that
        // end WITHOUT an attack (target lost, duration cap, force-toggle cleared) still walk out
        // through the authored outro. A RUNNING sprint that has carried him into reach converts
        // directly into the swing instead — the attack supersedes Sprint_Loop from AnyState (upper
        // body), the LungeLowerBody override layer keeps the legs running at his arrival speed, and
        // BeginTimedAction's sprint abandon ends the gait and stamps the cooldown, so one swing plays
        // and the sequence is over. That is the point of the gait: catch fast players and hit them AT
        // speed, not after a stop.
        if (IsSprintHoldingAttacks() && !IsSprintArrivalAttackReady(huntTarget))
        {
            LogAttackGateDiagnostic($"BundyAttackGate held by sprint ({_sprintPhase}); target={PlayerLabel(huntTarget)}.");
            return false;
        }

        if (RequiresActiveMovementLoopAudioForAttacks && !IsMovementLoopAudioWindowReady())
        {
            if (UsesPullStart)
                return BeginPullStart(huntTarget);

            LogAttackGateDiagnostic($"BundyAttackGate blocked; movement-loop audio window is not active for {PlayerLabel(huntTarget)}.");
            return false;
        }

        if (!TryGetCompleteNavigationPathDistanceCached(huntTarget.transform.position, out float distance))
        {
            LogAttackGateDiagnostic($"BundyAttackGate no complete path; target={PlayerLabel(huntTarget)}");
            return false;
        }

        bool healthAllowsFinisher = !UsesForcedCriticalFinisherGate &&
            (!RequireHalfHealthForFinisher || IsPlayerAtHalfHealthOrLower(huntTarget));
        // Stage 2 Part B: when he lunges, the finisher must also be reachable from lunge range, otherwise
        // the larger basic-attack lunge range always pre-empts the (short) finisher range and finishers
        // almost never fire. Let the finisher trigger from the same range and run in.
        float finisherStartRange = (UsesAttackLunge && FinishersEnabled)
            ? GetBasicAttackStartRange(BasicAttackRange)
            : FinisherAttackRange;
        bool finishersReady = FinishersEnabled;
        bool canFinisher = finishersReady && healthAllowsFinisher && distance <= finisherStartRange;
        if (DebugLogs && healthAllowsFinisher && distance <= Mathf.Max(finisherStartRange, FinisherAttackRange))
            LogInfo($"FinisherGate: ready={finishersReady}, healthAllows={healthAllowsFinisher}, dist={distance:F2}m, startRange={finisherStartRange:F2}m, chance={FinisherChance:F2}, basicActions={BasicActionsEnabled}.");
        if (canFinisher && (!BasicActionsEnabled || UnityEngine.Random.value <= FinisherChance))
        {
            if (UsesAttackLunge && distance > FinisherAttackRange)
                BeginFinisherLunge(huntTarget); // run in; the real finisher fires on contact
            else
                BeginFinisher(huntTarget);
            return true;
        }

        float attackStartRange = Mathf.Max(BasicAttackRange, GetBasicAttackStartRange(BasicAttackRange));
        if (distance > attackStartRange)
            return false;

        if (!BasicActionsEnabled)
            return false;

        if (UsesAttackLunge && distance > BasicAttackRange && !HasClearLungePathTo(huntTarget.transform.position, huntTarget))
        {
            LogAttackGateDiagnostic($"BundyAttackGate lunge blocked; target={PlayerLabel(huntTarget)}, dist={distance:F2}");
            return false;
        }

        // R2 interim, guard 2: let a lateral maneuver settle before swinging out of it. Guard 1 in
        // SelectCombatBehavior stops a circle being chosen while an attack is available, but he can
        // still be mid-circle at a distance where the attack was not yet available and arrive into
        // range during it. Holding the swing for a beat is what separates the sidestep from the lunge
        // visually. Basic attacks only — finishers are far rarer, have never fired in a run, and
        // starving that path further would cost more than it buys.
        if (UsesCombatManeuvers && CombatManeuverAttackGuardEnabled)
        {
            float maneuverDwell = Mathf.Max(0f, CombatManeuverAttackDwellSeconds);
            float sinceManeuver = Time.time - _lastLateralManeuverAt;
            if (maneuverDwell > 0f && sinceManeuver < maneuverDwell)
            {
                LogAttackGateDiagnostic(
                    $"BundyAttackGate held by maneuver dwell; target={PlayerLabel(huntTarget)}, " +
                    $"dist={distance:F2}m, sinceManeuver={sinceManeuver:F2}s, dwell={maneuverDwell:F2}s.");
                return false;
            }
        }

        BeginBasicAttack(
            huntTarget,
            RollComboAttack(CountLivingPlayersNear(transform.position, MultiAttackNearbyRange), distance));
        return true;
    }

    private void LogAttackGateDiagnostic(string message)
    {
        if (!ShouldLog(BundyLogCategory.Combat, 1) || Time.time < _nextAttackGateDiagnosticAt)
            return;

        _nextAttackGateDiagnosticAt = Time.time + AttackGateDiagnosticIntervalSeconds;
        LogDiagnostic(BundyLogCategory.Combat, 1, message);
    }

    private void BeginBasicAttack(PlayerControllerB victim, ComboAttackKind combo = ComboAttackKind.None)
    {
        // #288/#208: latch BEFORE BeginTimedAction, which abandons the sprint. A swing that a
        // RUNNING sprint converted into (IsSprintArrivalAttackReady in TryStartAttack is the only
        // way an attack starts while the gait still owns him) is uninterruptible by the stun tier —
        // the run is already spent, so a stagger here costs the swing it paid for. Cleared with the
        // rest of the attack state in the action cleanup.
        _sprintArrivalAttackActive = _sprintPhase == SprintPhase.Running;
        if (!_hasActedOnce)
        {
            _hasActedOnce = true;
            _tauntPending = true; // taunt once, after his very first attack on a player
        }
        int nearbyLivingPlayerCount = CountLivingPlayersNear(transform.position, MultiAttackNearbyRange);
        int previousAttackIndex = _lastAttackIndex;
        // #281/#282: a combo opener uses its sequence's fixed opening swing (chain: pattern[0],
        // spin loop: the spin index) so the sequence is deterministic; everything else keeps the
        // rotation-based selection.
        int comboOpenerIndex = combo != ComboAttackKind.None ? GetComboOpenerAttackIndex(combo) : -1;
        int attackIndex = comboOpenerIndex >= 0
            ? comboOpenerIndex
            : SelectBasicAttackIndex(nearbyLivingPlayerCount, _lastAttackIndex);
        _lastAttackIndex = attackIndex;
        _activeAttackIndex = attackIndex;
        _activeAttackCountsAsGrab = BasicAttackCountsAsGrab(attackIndex);
        _activeBasicVictim = victim;
        _activeBasicVictimPaired = false;
        _activePairedActionId = null;
        _postBasicAttackWalkUntil = 0f;
        ClearBasicAttackContactLatch();
        // #276: fresh contact caches per attack — collider membership and the prop hierarchy are
        // assumed stable only within a single attack window.
        InvalidateAttackContactColliderCache();
        InvalidateRuntimeChainsawPropCache();
        float impactDelay = GetBasicAttackImpactDelay(attackIndex);
        float recoverDelay = GetBasicAttackRecoverDelay(attackIndex);
        ArmBasicAttackVisualExit(attackIndex);

        BeginTimedAction(
            ActionKind.Attack,
            victim,
            impactDelay,
            recoverDelay,
            recoverDelay);

        // #281/#282: arm AFTER BeginTimedAction (it defensively clears sequence state). The victim
        // field the sequence driver reads (_activeBasicVictim) is already set above.
        if (comboOpenerIndex >= 0)
            ArmComboAttack(combo, attackIndex);

        _attackHitConfirmed = false;
        _hasLastAttackContactPoint = false;
        _attackActiveWindowStartAt = Time.time + Mathf.Max(0f, impactDelay - AttackActiveLeadSeconds);
        _attackActiveWindowEndAt = Time.time + Mathf.Max(impactDelay, impactDelay + AttackActiveTrailSeconds);
        EnterAttackPhase(AttackPhase.Windup, "attack begin");
        LogAttackSeamEvent("attack-start");

        if (_activeAttackCountsAsGrab)
            _grabbedTarget = victim;

        // Stage 2: a normal (non-grab) basic attack lunges. Paired/grab attacks are root-locked, so
        // they must not lunge — cleared below once pairing is known.
        // Job 2a item 6: consult the lunge path here too. The gate in TryStartAttack only runs when
        // distance > BasicAttackRange, so an attack started from INSIDE basic range previously lunged
        // without ever checking whether the path was clear. A blocked path still attacks — he is in
        // range, the swing is legitimate — it just does not surge into geometry to get there.
        bool lungePathClear = victim == null || HasClearLungePathTo(victim.transform.position, victim);
        _lungeActive = UsesAttackLunge && !_activeAttackCountsAsGrab && lungePathClear;
        if (_lungeActive && victim != null)
        {
            // Commit the lunge to where the player is RIGHT NOW. Job 2a: this aim now re-tracks the live
            // player until AttackLungeCommitFraction of the swing (see MaintainLungeAimTracking), then
            // freezes — so a dodge inside the commit window still makes the lunge miss.
            CommitLungeAim(victim.transform.position);
        }
        else if (UsesAttackLunge && !_activeAttackCountsAsGrab && !lungePathClear && DebugLogs)
        {
            LogDiagnostic(
                $"BundyLungeDrive lunge suppressed at attack start; path blocked, target={PlayerLabel(victim)}, " +
                $"dist={(victim != null ? Vector3.Distance(transform.position, victim.transform.position) : -1f):F2}m.");
        }
        PrepareActionMovement(victim);
        // #281/#282: combo reps have no paired/playable choreography — a root-locked pair would end
        // the advance and the rep re-triggers would fight the playable. Combos stay physical-contact only.
        _activeBasicVictimPaired = comboOpenerIndex < 0 && TryBeginPairedBasicAttack(victim, attackIndex);
        if (_activeBasicVictimPaired)
        {
            _lungeActive = false;
            ClearLungeAim();
        }
        // #288: publish eagerly here rather than waiting for the next Update tick — this is the
        // frame the sprint-arrival and paired latches settle, and it is also the frame a swing is
        // most likely to be shot out of.
        MaintainReactionSuppressionMirror();
        // Job 1 correction (run 6): attack COUNT is the plan's primary combat metric — "judge combat by
        // attack count, contact resolution" — and the first Job 1 pass left no line saying an attack
        // started at the default verbosity. attack-start rode on [BundyAttackSeam] (Anim, 2) and
        // BundyBasicAttackDiag (Combat, 2), so run 6 could only be counted through "Action complete:
        // Attack" and a throttled hold:windup sample. This is the missing half of BundyBasicAttackDamage:
        // one is the numerator, this is the denominator, and neither is judgeable without the other.
        LogInfo(
            BundyLogCategory.Combat,
            0,
            $"BundyAttackStart attackIndex={attackIndex}, target={PlayerLabel(victim)}, " +
            $"dist={(victim != null ? Vector3.Distance(transform.position, victim.transform.position) : -1f):F2}m, " +
            $"impactDelay={impactDelay:F2}s, lunge={_lungeActive}, paired={_activeBasicVictimPaired}, " +
            $"grab={_activeAttackCountsAsGrab}");
        // C3.2: the attack line lands AFTER the windup is visibly underway, not on the trigger
        // frame. Timer-based (animation events are dead): 0.45s clears the animator transition
        // plus the 0.30s lunge plant, and still leads the earliest blade contact (1.17s) by
        // ~0.7s. Queued, not played: priority 4 wins the queue slot over any pending chatter.
        if (IsServer)
            QueueVocal(BundyAudioCue.AttackVocal, AttackVocalWindupDelaySeconds, "attack windup underway");
        // Swing swell: rev the running loop into this swing on every client, from windup until
        // just past the expected contact moment.
        if (IsServer && UsesMovementLoopSwingModulation && !_sawStalled)
            PulseChainsawSwellClientRpc(impactDelay + 0.3f);
        // Task D option: lead every swing with a rev over the running loop. Config-gated OFF by
        // default — chainsaw_rev.wav is a 10s pull-start clip, so layering it per swing needs an
        // in-game A/B before committing (a trimmed rev one-shot would be the clean version).
        if (IsServer &&
            UsesSawStallCycle &&
            !_sawStalled &&
            (BundyPlugin.ModConfig?.ChainsawAttackRevOneShot?.Value ?? false))
        {
            PlayRandomBundyAudio(BundyAudioCue.ChainsawRev, ChainsawAudioVolume, 0.98f, 1.02f);
        }
        bool attackerPlayableActive = BundyAttackerPlayableController.IsActive(this);
        if (_activeBasicVictimPaired)
            BeginPairedActionRootLock(transform.position, transform.rotation);
        if (!attackerPlayableActive)
            PlayAttack(attackIndex);
        else if (DebugLogs)
            LogWarning($"BundyBasicAttackDiag animator trigger skipped; paired attacker playable is active for attackIndex={attackIndex}.");

        string attackIndexParam = creatureAnimator != null
            ? ReadAnimatorInteger(creatureAnimator, BundyAnimatorParams.AttackIndex)
            : "<no-animator>";
        // Job 1: the per-attack flow trace, including the 0.25s-interval coroutine below. Combat
        // category, per-tick tier — BundyLungeDrive at verbosity 1 carries what combat is judged on now.
        if (ShouldLog(BundyLogCategory.Combat, 2))
        {
            LogInfo(
                BundyLogCategory.Combat,
                2,
                $"BundyBasicAttackDiag start: target={PlayerLabel(victim)}, selectedAttackIndex={attackIndex}, " +
                $"nearbyPlayers={nearbyLivingPlayerCount}, lastAttackIndexBeforeSelect={previousAttackIndex}, " +
                $"animatorAttackIndexAfterSet={attackIndexParam}, grabProxy={_activeAttackCountsAsGrab}, " +
                $"pairedVictim={_activeBasicVictimPaired}, pairedAttacker={attackerPlayableActive}, " +
                $"impactDelay={impactDelay:F2}s, recoverDelay={recoverDelay:F2}s, animator={FormatAnimatorIdentity(creatureAnimator)}.");
            StartCoroutine(LogBasicAttackFlowDiagnostics(victim, attackIndex));
        }
    }

    private void BeginForcedCriticalFinisherApproach(PlayerControllerB victim, float pathDistance)
    {
        float remaining = GetForcedCriticalFinisherRemainingSeconds(victim);
        if (!IsLivingPlayer(victim) || remaining <= 0f)
            return;

        _finisherLungeActive = true; // existing routing flag; this path uses controlled catch-up, not an attack lunge
        _lungeActive = false;
        ClearLungeAim();
        _finisherLungeUntil = Time.time + remaining;
        _activeFinisherVictim = victim;
        _activeAttackIndex = -1;
        _activeAttackCountsAsGrab = false;
        _activeFinisherVictimPaired = false;
        CaptureForcedCriticalFinisherApproachForward(victim);
        _nextForcedCriticalApproachLogAt = Time.time;

        BeginTimedAction(ActionKind.Finisher, victim, 0f, remaining, remaining);
        WriteInfoLine(
            $"BundyForcedCritical approach begin: target={PlayerLabel(victim)}, " +
            $"pathDistance={pathDistance:F2}m, remaining={remaining:F2}s, " +
            $"stickyApproachForward={FormatVector(_forcedCriticalApproachForward)}.");
        MaintainForcedCriticalFinisherApproach();
    }

    // Stage 2 Part B: begin a finisher by RUNNING IN at lunge speed (full-body run, no swing, no snap),
    // then firing the real BeginFinisher on contact. Keeps the paired-finisher machinery untouched —
    // it only changes how he closes the gap and when the grab fires.
    private void BeginFinisherLunge(PlayerControllerB victim)
    {
        _finisherLungeActive = true;
        _lungeActive = true;                 // reuse the fast lunge movement (DriveActionApproach)
        _finisherLungeUntil = Time.time + FinisherLungeTimeoutSeconds;
        _activeFinisherVictim = victim;
        _activeAttackIndex = -1;
        _activeAttackCountsAsGrab = false;   // not grabbing yet — just charging in
        _activeFinisherVictimPaired = false;
        // Commit the finisher run-in to where the player is RIGHT NOW (dodgeable) — it won't re-track.
        CommitLungeAim(victim.transform.position);
        // Lightweight Finisher lock so pursuit yields; the paired finisher itself is NOT started here.
        BeginTimedAction(ActionKind.Finisher, victim, 0f, FinisherLungeTimeoutSeconds, FinisherLungeTimeoutSeconds);
        LogInfo($"Finisher lunge begin: target={PlayerLabel(victim)}, dist={Vector3.Distance(transform.position, victim.transform.position):F2}m.");
    }

    // Per-frame while running in for a finisher: face + drive toward the victim; on contact, hand off to
    // the real BeginFinisher (snap + paired animation, unchanged). Miss/timeout -> recover.
    private void MaintainFinisherLunge()
    {
        if (UsesForcedCriticalFinisherGate)
        {
            MaintainForcedCriticalFinisherApproach();
            return;
        }

        PlayerControllerB victim = _activeFinisherVictim;
        if (!IsLivingPlayer(victim) || Time.time >= _finisherLungeUntil)
        {
            _finisherLungeActive = false;
            _lungeActive = false;
            CompleteAction("finisher lunge miss/timeout");
            return;
        }

        // Aim is committed at lunge start (no re-tracking). Fire the real finisher only if the LIVE player
        // is actually within grab range — so a dodge during the run-in makes the finisher whiff & recover.
        if (Vector3.Distance(transform.position, victim.transform.position) <= FinisherAttackRange)
        {
            _finisherLungeActive = false;
            _lungeActive = false;
            BeginFinisher(victim); // contact: trigger the real (snap + paired) finisher
            return;
        }

        DriveActionApproach(victim); // charge toward the committed point at lunge speed
    }

    private void MaintainForcedCriticalFinisherApproach()
    {
        PlayerControllerB victim = _activeFinisherVictim;
        float remaining = GetForcedCriticalFinisherRemainingSeconds(victim);
        if (!IsLivingPlayer(victim) || remaining <= 0f || Time.time >= _finisherLungeUntil)
        {
            _finisherLungeActive = false;
            _lungeActive = false;
            CompleteAction("forced-critical finisher window expired");
            return;
        }

        if (!TryGetForcedCriticalFinisherFrontSlot(victim, out Vector3 frontSlot))
        {
            HoldAgentWithoutReset();
            LogAttackGateDiagnostic(
                $"BundyForcedCritical front slot unreachable; target={PlayerLabel(victim)}, remaining={remaining:F2}s.");
            return;
        }

        FaceActionTarget(victim, ManualActionTurnSpeedDegrees);

        Vector3 positionDelta = frontSlot - transform.position;
        positionDelta.y = 0f;
        Vector3 toVictim = victim.transform.position - transform.position;
        toVictim.y = 0f;
        float yawError = toVictim.sqrMagnitude > 0.0001f
            ? Mathf.Abs(Vector3.SignedAngle(transform.forward, toVictim.normalized, Vector3.up))
            : 0f;
        bool snapMeasured = TryMeasurePendingFinisherRootSnap(victim, out Vector3 pendingSnap);
        float pendingSnapDistance = snapMeasured ? pendingSnap.magnitude : float.PositiveInfinity;
        LogForcedCriticalFinisherApproach(
            victim,
            frontSlot,
            positionDelta.magnitude,
            yawError,
            pendingSnapDistance,
            remaining);

        if (positionDelta.magnitude <= ForcedCriticalFinisherPositionTolerance &&
            yawError <= ForcedCriticalFinisherYawToleranceDegrees &&
            pendingSnapDistance <= ForcedCriticalFinisherPositionTolerance)
        {
            HoldAgentWithoutReset();
            _finisherLungeActive = false;
            _lungeActive = false;
            WriteInfoLine(
                $"BundyForcedCritical finisher entry ready: target={PlayerLabel(victim)}, " +
                $"positionError={positionDelta.magnitude:F3}m, yawError={yawError:F1}deg, " +
                $"pendingRootSnap={pendingSnapDistance:F3}m, remaining={remaining:F2}s.");
            BeginFinisher(victim);
            return;
        }

        DriveForcedCriticalFinisherFrontSlot(frontSlot);
    }

    private bool TryGetForcedCriticalFinisherFrontSlot(PlayerControllerB victim, out Vector3 frontSlot)
    {
        frontSlot = transform.position;
        if (!IsLivingPlayer(victim))
            return false;

        if (!_hasForcedCriticalApproachForward)
            CaptureForcedCriticalFinisherApproachForward(victim);

        Vector3 desired = victim.transform.position +
            _forcedCriticalApproachForward * GetPairedFinisherRootBackoff();
        desired = ResolveGroundedPairedRoot(desired, victim);
        return TryResolveNavigableDestination(desired, out frontSlot);
    }

    private void CaptureForcedCriticalFinisherApproachForward(PlayerControllerB victim)
    {
        Vector3 victimForward = victim != null ? victim.transform.forward : Vector3.forward;
        victimForward.y = 0f;
        if (victimForward.sqrMagnitude < 0.001f)
            victimForward = Vector3.forward;
        victimForward.Normalize();

        Vector3 victimToBundy = victim != null
            ? transform.position - victim.transform.position
            : victimForward;
        victimToBundy.y = 0f;
        if (victimToBundy.sqrMagnitude < 0.001f)
            victimToBundy = victimForward;
        victimToBundy.Normalize();

        float frontDot = Vector3.Dot(victimForward, victimToBundy);
        if (frontDot >= ForcedCriticalFinisherFrontArcMinDot)
        {
            _forcedCriticalApproachForward = victimToBundy;
        }
        else
        {
            Vector3 lateral = victimToBundy - victimForward * frontDot;
            lateral.y = 0f;
            if (lateral.sqrMagnitude < 0.001f)
            {
                _forcedCriticalApproachForward = victimForward;
            }
            else
            {
                lateral.Normalize();
                float lateralWeight = Mathf.Sqrt(
                    Mathf.Max(0f, 1f - ForcedCriticalFinisherFrontArcMinDot * ForcedCriticalFinisherFrontArcMinDot));
                _forcedCriticalApproachForward =
                    (victimForward * ForcedCriticalFinisherFrontArcMinDot + lateral * lateralWeight).normalized;
            }
        }

        _hasForcedCriticalApproachForward = true;
    }

    private void LogForcedCriticalFinisherApproach(
        PlayerControllerB victim,
        Vector3 frontSlot,
        float slotError,
        float yawError,
        float pendingSnapDistance,
        float remaining)
    {
        if (Time.time < _nextForcedCriticalApproachLogAt)
            return;
        _nextForcedCriticalApproachLogAt =
            Time.time + ForcedCriticalApproachLogIntervalSeconds;

        Vector3 victimVelocity = victim?.thisController != null
            ? victim.thisController.velocity
            : Vector3.zero;
        victimVelocity.y = 0f;
        Vector3 currentVictimForward = victim != null ? victim.transform.forward : Vector3.forward;
        currentVictimForward.y = 0f;
        if (currentVictimForward.sqrMagnitude < 0.001f)
            currentVictimForward = Vector3.forward;
        currentVictimForward.Normalize();
        Vector3 victimToBundy = victim != null
            ? transform.position - victim.transform.position
            : Vector3.zero;
        victimToBundy.y = 0f;
        float currentFrontDot = victimToBundy.sqrMagnitude > 0.001f
            ? Vector3.Dot(currentVictimForward, victimToBundy.normalized)
            : 1f;

        WriteInfoLine(
            $"BundyForcedCriticalApproach: target={PlayerLabel(victim)}, remaining={remaining:F2}s, " +
            $"victimSpeed={victimVelocity.magnitude:F2}m/s, slot={FormatVector(frontSlot)}, " +
            $"slotError={slotError:F3}m, yawError={yawError:F1}deg, " +
            $"pendingRootSnap={pendingSnapDistance:F3}m, currentVictimFrontDot={currentFrontDot:F2}, " +
            $"victimFacingRequired=False.");
    }

    private void DriveForcedCriticalFinisherFrontSlot(Vector3 frontSlot)
    {
        if (!AgentReadyForMovement())
        {
            HoldAgentWithoutReset();
            return;
        }

        float distance = Vector3.Distance(transform.position, frontSlot);
        if (distance <= ForcedCriticalFinisherPositionTolerance)
        {
            HoldAgentWithoutReset();
            return;
        }

        agent.speed = Mathf.Max(WalkSpeed, RunSpeed);
        agent.angularSpeed = AgentAngularSpeedDegrees;
        agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);
        agent.updateRotation = false;
        agent.stoppingDistance = Mathf.Max(0.02f, ForcedCriticalFinisherPositionTolerance * 0.25f);
        agent.isStopped = false;
        movingTowardsTargetPlayer = false;
        moveTowardsDestination = true;
        SetDestinationToPosition(frontSlot);
        HoldLocomotionIntent(0.25f);
        SetLocalBehaviourState(BehaviourStateTracking);
    }

    private void BeginFinisher(PlayerControllerB victim)
    {
        Vector3 bundyEntryVelocity = agent != null ? agent.velocity : Vector3.zero;
        Vector3 trackedVictimEntryVelocity = _trackedTargetVelocity;
        bool trackedVictimVelocityValid = _hasTrackedTargetSample && IsFinite(trackedVictimEntryVelocity);
        float trackedVictimVelocitySampleAge = trackedVictimVelocityValid
            ? Mathf.Max(0f, Time.time - _lastTrackedTargetSampleAt)
            : -1f;
        Vector3 controllerVictimEntryVelocity = victim?.thisController != null
            ? victim.thisController.velocity
            : Vector3.zero;
        bool controllerVictimVelocityValid = victim?.thisController != null && IsFinite(controllerVictimEntryVelocity);
        trackedVictimEntryVelocity.y = 0f;
        controllerVictimEntryVelocity.y = 0f;
        bool useControllerVelocity = controllerVictimVelocityValid &&
            (!trackedVictimVelocityValid || controllerVictimEntryVelocity.sqrMagnitude >= trackedVictimEntryVelocity.sqrMagnitude);
        Vector3 victimEntryVelocity = useControllerVelocity
            ? controllerVictimEntryVelocity
            : trackedVictimEntryVelocity;
        string victimVelocitySource = useControllerVelocity
            ? "controller"
            : (trackedVictimVelocityValid ? "tracker" : "none");

        int bodyIndex = SelectFinisherBodyIndex(_lastFinisherBodyIndex);
        int entryIndex = SelectFinisherEntryIndex(bodyIndex);
        _lastFinisherBodyIndex = bodyIndex;
        _activeAttackIndex = -1;
        _activeAttackCountsAsGrab = true;
        _activeFinisherVictim = victim;
        _activeFinisherEntryIndex = entryIndex;
        _activeFinisherBodyIndex = bodyIndex;
        _activeFinisherVictimPaired = false;
        _activeChainsawFinisherUsesInteractions = false;
        _chainsawFinisherVictimFallbackLocal = false;
        _activePairedActionId = null;

        BeginTimedAction(
            ActionKind.Finisher,
            victim,
            FinisherImpactFallbackSeconds,
            FinisherEndFallbackSeconds,
            FinisherEndFallbackSeconds);

        _grabbedTarget = victim;
        PreparePairedFinisherMovement(victim);
        bool victimReady = IsPairedFinisherVictimReady(victim, entryIndex, bodyIndex);
        LogFinisherEntryBaseline(
            victim,
            bodyIndex,
            victimReady,
            bundyEntryVelocity,
            victimEntryVelocity,
            victimVelocitySource,
            controllerVictimEntryVelocity,
            controllerVictimVelocityValid,
            trackedVictimEntryVelocity,
            trackedVictimVelocityValid,
            trackedVictimVelocitySampleAge);
        if (victimReady)
            SnapRootToPairedFinisherVictim(victim);
        _activeFinisherVictimPaired = TryBeginPairedFinisher(victim, entryIndex, bodyIndex);
        bool attackerPlayableActive = BundyAttackerPlayableController.IsActive(this);
        if (_activeFinisherVictimPaired)
        {
            BeginPairedActionRootLock(transform.position, transform.rotation);
            ConfigurePairedFinisherTiming(_activePairedActionId);
        }
        _actionGrabFallbackAt = Time.time + FinisherGrabFallbackSeconds;
        ShowFinisherProp(bodyIndex);
        if (!attackerPlayableActive)
            PlayFinisherAnimation(entryIndex, bodyIndex);
        else if (DebugLogs)
            LogWarning($"BundyFinisherFlow animator trigger skipped; paired attacker playable is active for entryIndex={entryIndex}, bodyIndex={bodyIndex}.");
        if (DebugLogs)
        {
            LogInfo(
                $"BundyFinisherFlow start: target={PlayerLabel(victim)}, selectedActionId='{_activePairedActionId ?? "<none>"}', " +
                $"entryIndex={entryIndex}, bodyIndex={bodyIndex}, chance={FinisherChance:P0}, victimReady={victimReady}, " +
                $"BeginPairedFinisherReturned={_activeFinisherVictimPaired}, pairedAttackerActiveAfterBegin={attackerPlayableActive}, " +
                $"animator={FormatAnimatorIdentity(creatureAnimator)}, catalog={FormatPairedActionCatalogSummary(_activePairedActionId)}, " +
                $"attackerPlayable={BundyAttackerPlayableController.GetDebugState(this)}.");
        }
    }

    private void LogFinisherEntryBaseline(
        PlayerControllerB victim,
        int bodyIndex,
        bool victimReady,
        Vector3 bundyVelocity,
        Vector3 victimVelocity,
        string victimVelocitySource,
        Vector3 controllerVictimVelocity,
        bool controllerVictimVelocityValid,
        Vector3 trackedVictimVelocity,
        bool trackedVictimVelocityValid,
        float trackedVictimVelocitySampleAge)
    {
        if (!IsLivingPlayer(victim))
        {
            WriteInfoLine("BundyFinisherBaseline: victim=<invalid>; entry metrics unavailable.");
            return;
        }

        bundyVelocity.y = 0f;
        victimVelocity.y = 0f;

        Vector3 toVictim = victim.transform.position - transform.position;
        toVictim.y = 0f;
        float closingRate = toVictim.sqrMagnitude > 0.0001f
            ? Vector3.Dot(bundyVelocity - victimVelocity, toVictim.normalized)
            : 0f;

        BundyVictimClipId clipId = GetChainsawFinisherVictimClipId(bodyIndex);
        Quaternion pairedRotation = GetPairedVictimRotation(victim, clipId);
        float victimYawOffset = BundyPlugin.ModConfig?.ChainsawFinisherVictimYawOffset?.Value ?? 180f;
        Quaternion desiredVictimRotation = pairedRotation * Quaternion.Euler(0f, victimYawOffset, 0f);
        float signedYawDelta = Mathf.DeltaAngle(
            victim.transform.eulerAngles.y,
            desiredVictimRotation.eulerAngles.y);

        bool snapResolved = TryMeasurePendingFinisherRootSnap(victim, out Vector3 pendingSnapDelta);
        float requestedSnapDistance = snapResolved ? pendingSnapDelta.magnitude : -1f;
        float appliedSnapDistance = snapResolved
            ? Mathf.Min(requestedSnapDistance, Mathf.Max(0.1f, GetPairedFinisherMaxRootSnapDistance()))
            : -1f;

        WriteInfoLine(
            $"BundyFinisherBaseline: target={PlayerLabel(victim)}, bodyIndex={Mathf.Clamp(bodyIndex, 0, 2)}, " +
            $"yawDeltaSigned={signedYawDelta:F1}deg, yawError={Mathf.Abs(signedYawDelta):F1}deg, " +
            $"requestedRootSnap={requestedSnapDistance:F3}m, appliedRootSnap={appliedSnapDistance:F3}m, " +
            $"snapEligible={victimReady}, victimPlanarSpeed={victimVelocity.magnitude:F3}m/s, " +
            $"bundyPlanarSpeed={bundyVelocity.magnitude:F3}m/s, closingRate={closingRate:F3}m/s, " +
            $"velocitySource={victimVelocitySource}, controllerPlanarSpeed={controllerVictimVelocity.magnitude:F3}m/s, " +
            $"controllerVelocityValid={controllerVictimVelocityValid}, trackedPlanarSpeed={trackedVictimVelocity.magnitude:F3}m/s, " +
            $"trackedVelocityValid={trackedVictimVelocityValid}, trackedSampleAge={trackedVictimVelocitySampleAge:F3}s.");
    }

    private bool TryMeasurePendingFinisherRootSnap(PlayerControllerB victim, out Vector3 delta)
    {
        delta = Vector3.zero;
        if (!IsLivingPlayer(victim))
            return false;

        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
        {
            forward = victim.transform.position - transform.position;
            forward.y = 0f;
        }
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;
        forward.Normalize();

        Vector3 desired = victim.transform.position - forward * GetPairedFinisherRootBackoff();
        desired = ResolveGroundedPairedRoot(desired, victim);
        delta = desired - transform.position;
        return IsFinite(delta);
    }

    private IEnumerator LogBasicAttackFlowDiagnostics(PlayerControllerB victim, int attackIndex)
    {
        for (int i = 0; i <= 8; i++)
        {
            bool active = _actionKind == ActionKind.Attack && _activeAttackIndex == attackIndex;
            Vector3 vPos = victim != null ? victim.transform.position : Vector3.zero;
            float distance = victim != null ? Vector3.Distance(transform.position, victim.transform.position) : -1f;
            LogInfo(
                BundyLogCategory.Combat,
                2,
                $"BundyBasicAttackDiag t={i * 0.25f:F2}s attackIndex={attackIndex}, active={active}, " +
                $"impactResolved={_actionImpactResolved}, recoverResolved={_actionRecoverResolved}, " +
                $"animator={FormatAnimatorSnapshot(creatureAnimator)}, attackerRoot={FormatVector(transform.position)}, " +
                $"victimRoot={FormatVector(vPos)}, victimDistance={distance:F2}m.");
            yield return new WaitForSeconds(0.25f);
        }
    }

    private void ArmBasicAttackVisualExit(int attackIndex)
    {
        ResetBasicAttackVisualExit();

        if (!UsesBasicAttackVisualExit ||
            BasicAttackCountsAsGrab(attackIndex) ||
            BasicAttackIsOpeningThrow(attackIndex))
            return;

        float threshold = GetBasicAttackVisualExitNormalizedTime(attackIndex);
        if (!IsValidBasicAttackVisualExitThreshold(threshold))
            return;

        _attackVisualExitArmed = true;
        _attackVisualReturnStateHash = GetBasicAttackVisualReturnStateHash();

        if (DebugLogs)
        {
            LogDiagnostic(
                $"BundyAttackVisualExit armed: attackIndex={attackIndex}, threshold={threshold:F3}, " +
                $"returnStateHash={_attackVisualReturnStateHash}, animator={FormatAnimatorSnapshot(creatureAnimator)}");
        }
    }

    private void UpdateBasicAttackVisualExit()
    {
        if (!_attackVisualExitArmed ||
            _attackVisualExitApplied ||
            _actionKind != ActionKind.Attack ||
            _activeAttackIndex < 0 ||
            !UsesBasicAttackVisualExit ||
            _activeAttackCountsAsGrab ||
            _activeBasicVictimPaired ||
            BasicAttackIsOpeningThrow(_activeAttackIndex))
            return;

        if (creatureAnimator == null || creatureAnimator.runtimeAnimatorController == null || creatureAnimator.layerCount <= 0)
            return;

        float threshold = GetBasicAttackVisualExitNormalizedTime(_activeAttackIndex);
        if (!IsValidBasicAttackVisualExitThreshold(threshold))
            return;

        if (!_actionImpactResolved && Time.time < _attackActiveWindowEndAt)
            return;

        try
        {
            AnimatorStateInfo currentState = creatureAnimator.GetCurrentAnimatorStateInfo(0);
            if (currentState.normalizedTime < threshold)
                return;

            AnimatorClipInfo[] currentClips = creatureAnimator.GetCurrentAnimatorClipInfo(0);
            if (!TryGetBasicAttackClip(currentClips, out string attackClipName, out float attackClipWeight))
                return;

            string animatorBefore = FormatAnimatorSnapshot(creatureAnimator);
            bool inTransitionBefore = creatureAnimator.IsInTransition(0);
            float blendSeconds = Mathf.Max(0.01f, BasicAttackVisualExitBlendSeconds);
            _attackVisualExitApplied = true;
            creatureAnimator.CrossFade(_attackVisualReturnStateHash, blendSeconds, 0, 0f);
            LogAttackSeamEvent("visual-exit-applied");
            _attackSeamTailLogUntil = Mathf.Max(_attackSeamTailLogUntil, Time.time + 0.5f);

            if (DebugLogs)
            {
                LogDiagnostic(
                    $"BundyAttackVisualExit applied: attackIndex={_activeAttackIndex}, clip='{attackClipName}', " +
                    $"clipWeight={attackClipWeight:F3}, normalizedTime={currentState.normalizedTime:F3}, " +
                    $"threshold={threshold:F3}, blendSeconds={blendSeconds:F3}, " +
                    $"returnStateHash={_attackVisualReturnStateHash}, inTransitionBefore={inTransitionBefore}, " +
                    $"impactResolved={_actionImpactResolved}, hitConfirmed={_attackHitConfirmed}, " +
                    $"activeWindowClosed={Time.time >= _attackActiveWindowEndAt}, animatorBefore={animatorBefore}");
            }
        }
        catch (Exception e)
        {
            _attackVisualExitApplied = true;
            LogWarning($"BundyAttackVisualExit exception: {e.GetType().Name}: {e.Message}");
        }
    }

    private int GetBasicAttackVisualReturnStateHash()
    {
        if (creatureAnimator == null || creatureAnimator.runtimeAnimatorController == null || creatureAnimator.layerCount <= 0)
            return BaseLayerIdleStateHash;

        try
        {
            AnimatorClipInfo[] currentClips = creatureAnimator.GetCurrentAnimatorClipInfo(0);
            if (TryGetBasicAttackClip(currentClips, out _, out _) || AnimatorClipsContain(currentClips, "Turn"))
                return BaseLayerIdleStateHash;

            AnimatorStateInfo currentState = creatureAnimator.GetCurrentAnimatorStateInfo(0);
            return currentState.fullPathHash != 0 ? currentState.fullPathHash : BaseLayerIdleStateHash;
        }
        catch (Exception e)
        {
            if (DebugLogs)
                LogDiagnostic($"BundyAttackVisualExit return-state capture exception: {e.GetType().Name}: {e.Message}");
            return BaseLayerIdleStateHash;
        }
    }

    private void ResetBasicAttackVisualExit()
    {
        _attackVisualExitArmed = false;
        _attackVisualExitApplied = false;
        _attackVisualReturnStateHash = BaseLayerIdleStateHash;
    }

    private static bool IsValidBasicAttackVisualExitThreshold(float threshold)
    {
        return !float.IsNaN(threshold) && !float.IsInfinity(threshold) && threshold > 0f && threshold < 1f;
    }

    private static bool TryGetBasicAttackClip(AnimatorClipInfo[] clipInfos, out string clipName, out float clipWeight)
    {
        clipName = string.Empty;
        clipWeight = 0f;
        if (clipInfos == null)
            return false;

        for (int i = 0; i < clipInfos.Length; i++)
        {
            AnimationClip clip = clipInfos[i].clip;
            if (clip == null || !IsBasicAttackClipName(clip.name))
                continue;

            if (clipInfos[i].weight >= clipWeight)
            {
                clipName = clip.name;
                clipWeight = clipInfos[i].weight;
            }
        }

        return !string.IsNullOrEmpty(clipName);
    }

    private static bool IsBasicAttackClipName(string clipName)
    {
        return !string.IsNullOrEmpty(clipName) &&
            (clipName.IndexOf("Chainsaw_Attack_", StringComparison.OrdinalIgnoreCase) >= 0 ||
             clipName.IndexOf("Atk_Attack_", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool AnimatorClipsContain(AnimatorClipInfo[] clipInfos, string value)
    {
        if (clipInfos == null || string.IsNullOrEmpty(value))
            return false;

        for (int i = 0; i < clipInfos.Length; i++)
        {
            AnimationClip clip = clipInfos[i].clip;
            if (clip != null && clip.name.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private bool BeginPullStart(PlayerControllerB target)
    {
        if (IsActionLocked())
            return false;

        // Freshly stalled saw dwells (idle loop audible, stall readable) before the pull.
        if (_sawStalled && Time.time < _sawStallRecoverAt)
            return false;

        BeginTimedAction(ActionKind.PullStart, target, 0f, PullStartFallbackSeconds, PullStartFallbackSeconds);
        _pullStartClipSeen = false;
        _pullStartRetimed = false;
        PlayPullStartAnimation();
        SetChainsawPropSpinActive(true, "pull start");
        // C4.1: the pull-start cue fires on EVERY pull start; the per-cue dedup window collapses
        // same-beat double-fires into one. #293: it fires BEFORE the movement-loop window request
        // on purpose — both fan out over ordered ClientRpcs, and each client's running-loop start
        // defers to the catch clip's latched tail, so the clip RPC must land first.
        if (PullStartAudioCue.HasValue)
            PlayRandomBundyAudio(PullStartAudioCue.Value, ChainsawAudioVolume, 0.98f, 1.02f);
        bool startedMovementLoop = StartsMovementLoopAudioOnPullStart && TryStartMovementLoopAudioWindow("pull start");
        // C4.4 (cheap character pass): an exertion grunt on the yank. Vocal-channel priority 0, so
        // it politely yields if he is mid-line. Works with a null hunt target (stall watchdog path).
        if (IsServer)
            PlayRandomVocal(BundyAudioCue.Grunt);
        LogInfo($"PullStart: target={PlayerLabel(target)}, movementLoopWindow={startedMovementLoop}, readyFor={Mathf.Max(0f, _movementLoopAudioReadyUntil - Time.time):F2}s.");
        return true;
    }

    // ---- C4.3 pull-start completion synthesis --------------------------------------------------
    // Animation events are globally dead on Bundy clips (r40 rebake), so the action always ran the
    // flat 3.25s PullStartFallbackSeconds regardless of the clip. Same normalized-time technique as
    // the synthetic footsteps: once the Chainsaw_PullStart* clip is observed on layer 0, the action
    // window is retimed to the REAL clip length, and completion fires when the state's normalized
    // time crosses the threshold (or the animator transitions out). The 3.25s fallback survives
    // only as the safety net for "trigger never landed". Owner-only (action state is owner
    // authority); runs immediately before UpdateActionFallbacks in Core.Update.
    private const float PullStartClipCompleteNormalizedTime = 0.96f;
    private const float PullStartClipEndTailSeconds = 0.15f;
    private const float PullStartMaxRetimedSeconds = 6f; // sanity clamp on clip-driven retimes
    private bool _pullStartClipSeen;
    private bool _pullStartRetimed;

    private void UpdatePullStartCompletion()
    {
        if (_actionKind != ActionKind.PullStart)
            return;
        if (creatureAnimator == null || creatureAnimator.runtimeAnimatorController == null ||
            creatureAnimator.layerCount <= 0)
        {
            return; // fallback timer still guards the exit
        }

        AnimatorClipInfo[] clipInfo = creatureAnimator.GetCurrentAnimatorClipInfo(0);
        bool inPullStartClip = clipInfo != null && clipInfo.Length > 0 && clipInfo[0].clip != null &&
            clipInfo[0].clip.name.IndexOf("PullStart", StringComparison.OrdinalIgnoreCase) >= 0;

        if (inPullStartClip)
        {
            _pullStartClipSeen = true;
            float clipLength = clipInfo[0].clip.length;
            if (!_pullStartRetimed && clipLength > 0.2f)
            {
                _pullStartRetimed = true;
                float retimedEnd = _actionStartedAt +
                    Mathf.Min(clipLength + PullStartClipEndTailSeconds, PullStartMaxRetimedSeconds);
                LogInfo(
                    $"PullStart retimed to clip: length={clipLength:F2}s, " +
                    $"ends in {retimedEnd - Time.time:F2}s (dead {PullStartFallbackSeconds:F2}s fallback window bypassed).");
                _actionRecoverFallbackAt = retimedEnd;
            }

            if (creatureAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime >= PullStartClipCompleteNormalizedTime)
                CompleteAction("pull start clip complete");
            return;
        }

        // Transitioned out of the clip (controller-driven exit) — that IS the completion event.
        if (_pullStartClipSeen)
            CompleteAction("pull start clip exited");
    }

    private void SetChainsawPropSpinActive(bool active, string reason)
    {
        if (_chainsawPropSpin == null)
            _chainsawPropSpin = GetComponentInChildren<BundyChainsawPropSpin>(true);

        if (_chainsawPropSpin != null)
            _chainsawPropSpin.SetSpinning(active, reason);
        else if (DebugLogs)
            LogWarning($"Chainsaw prop spin component missing; active={active}, reason={reason}.");
    }

    private bool IsMovementLoopAudioWindowReady()
    {
        // A stalled saw can never attack; the hunt loop routes to PullStart instead (dwell-gated).
        if (_sawStalled)
            return false;

        if (!UsesFiniteMovementLoopAudioWindow)
            return true;

        return Time.time < _movementLoopAudioReadyUntil;
    }

    private void BeginSawStall(string reason)
    {
        if (!UsesSawStallCycle || !UsesPullStart || _sawStalled)
            return;

        _sawStalled = true;
        _sawStalledAt = Time.time;
        _sawStallRecoverAt = Time.time + SawStallDwellSeconds;
        SetChainsawPropSpinActive(false, $"saw stalled: {reason}");
        // MovementLoopAudioCue is state-dependent; force the RPC so the running->idle switch is
        // audible at the contact moment instead of waiting for the next state edge.
        if (IsServer)
            SetMovementLoopAudioRequested(true, forceUpdate: true);
        LogInfo($"Saw stalled ({reason}); idle loop until pull start, dwell={SawStallDwellSeconds:F2}s.");
    }

    private void EndSawStall(string reason)
    {
        if (!_sawStalled)
            return;

        _sawStalled = false;
        _sawStallRecoverAt = 0f;
        if (IsServer && MovementLoopAudioCue.HasValue && IsMovementLoopAudioWindowReady())
            SetMovementLoopAudioRequested(true, forceUpdate: true);
        LogInfo($"Saw stall ended ({reason}); running loop resumed.");
    }

    // C1.1 (2026-07-30): a stalled saw could persist indefinitely outside the hunt loop.
    // BeginPullStart was only reachable from EnterPursuitMode and TryBeginAttackOpportunity — both
    // hunt-loop paths — so a wall-hit stall that carried into SEARCH/TRACK never cleared (observed:
    // 105s stalled, attacks hard-blocked the whole window via IsMovementLoopAudioWindowReady, and
    // two HUNT acquires with an IDLE saw loop because MovementLoopAudioCue is stall-derived). This
    // watchdog runs regardless of perception state: once the stall dwell plus a short grace has
    // elapsed with no pull-start attempt, he pulls the cord wherever he is. The grace gives the
    // hunt loop first crack so in-combat pull-starts keep their existing timing; the guarantee is
    // that _sawStalled never outlives dwell+grace (~2.3s) without a pull-start attempt or a logged
    // hold.
    // Config-backed since wave 2 (Audio/SawStallRecoveryGraceSeconds, default preserved at 1.5).
    private static float SawStallRecoveryWatchdogGraceSeconds =>
        BundyPlugin.ModConfig?.SawStallRecoveryGraceSeconds?.Value ?? 1.5f;
    private float _sawStalledAt;
    private float _nextSawStallHoldLogAt;

    private void MaybeRecoverStalledSaw()
    {
        if (!_sawStalled || !UsesPullStart || !IsOwner || isEnemyDead)
            return;

        if (Time.time < _sawStallRecoverAt + SawStallRecoveryWatchdogGraceSeconds)
            return;

        // Never while another system owns the root (same guard list as TryBeginDropDownFollow):
        // these windows are all sub-second, so the stall guarantee still holds.
        if (_lungeActive || _finisherLungeActive || _pairedActionRootLockActive || IsTurnClipRootLockActive() || IsDropJumpActive())
            return;

        if (IsActionLocked())
        {
            // An action (stagger, an attack, an in-flight pull) legitimately owns him; the stall is
            // being HELD, not leaked. Say so instead of holding silently (2s throttle).
            if (Time.time >= _nextSawStallHoldLogAt)
            {
                _nextSawStallHoldLogAt = Time.time + 2f;
                LogInfo(
                    $"Saw stall held: action={_actionKind} owns Bundy, " +
                    $"stalledFor={Time.time - _sawStalledAt:F1}s; pull start deferred until the action releases.");
            }
            return;
        }

        if (BeginPullStart(GetCurrentHuntTarget()))
            LogInfo($"Saw stall watchdog pull start: stalledFor={Time.time - _sawStalledAt:F1}s (recovered outside the hunt loop).");
    }

    private bool TryStartMovementLoopAudioWindow(string reason)
    {
        if (!UsesFiniteMovementLoopAudioWindow)
            return false;

        BundyAudioCue? loopCue = MovementLoopAudioCue;
        if (!loopCue.HasValue)
            return false;

        float duration = GetMovementLoopAudioWindowSeconds(loopCue.Value);
        _movementLoopAudioReadyUntil = Time.time + duration;
        SetMovementLoopAudioRequested(true, true);
        LogInfo($"Movement loop audio window start: cue={loopCue.Value}, duration={duration:F2}s, reason={reason}.");
        return true;
    }

    private float GetMovementLoopAudioWindowSeconds(BundyAudioCue cue)
    {
        if (!MovementLoopAudioClipLoops)
        {
            AudioClip clip = BundyAudioCatalog.GetClip(cue, 0);
            if (clip != null && clip.length > 0.1f)
                return clip.length;
        }

        return Mathf.Max(0.5f, MovementLoopAudioWindowFallbackSeconds);
    }

    private void BeginTimedAction(
        ActionKind kind,
        PlayerControllerB target,
        float impactDelay,
        float recoverDelay,
        float hardEndDelay)
    {
        if (kind != ActionKind.None)
            StopPursuitSearchIfActive();

        // Issue #205: drop the sprint at action START, not at action end. Every action state enters
        // from AnyState, so the Base Layer has already left the gait — but DoAIInterval returns early
        // while an action is locked, so UpdateSprintState never runs and the bool would stay true for
        // the whole swing. It would then still be true when the attack's own exit transition reached
        // Idle/RunForward, and those states enter the sprint on exactly that bool: he would relaunch
        // into a sprint intro for the frame or two before the completion hook cleared it.
        if (kind != ActionKind.None && _sprintPhase != SprintPhase.None)
        {
            // #208: an attack starting out of a RUNNING sprint is the arrival conversion, not a
            // failure case — name it so the log reads as the designed sequence.
            AbandonSprint(kind == ActionKind.Attack && _sprintPhase == SprintPhase.Running
                ? "sprint arrival converted into the swing (#208)"
                : "action started: " + kind);
        }

        // #281: any new action supersedes a live chain — a stagger mid-chain must not leave reps
        // queued to fire into the stagger's animator states. The chain opener re-arms after this.
        ClearChainAttack();

        // Defensive: a new action, including a retriggered stagger, must never inherit the previous
        // stagger's animator speed or agent hold.
        RestoreStaggerAnimatorSpeed();
        _staggerKnockdownActive = false;
        ReleaseStaggerAgentStop();

        _actionKind = kind;
        _actionTarget = target;
        _grabbedTarget = null;
        _actionGrabResolved = kind != ActionKind.Finisher;
        _actionImpactResolved = impactDelay <= 0f;
        _actionRecoverResolved = false;
        _actionStartedAt = Time.time;
        _dispatchingAttackImpactFallback = false;
        _chainsawWallImpactResolved = false;
        _finisherBloodVisualStarted = false;
        _finisherBloodVisualPlayedLocal = false;
        _finisherBloodFallbackAt = float.PositiveInfinity;
        _actionGrabFallbackAt = float.PositiveInfinity;
        _actionImpactFallbackAt = impactDelay > 0f ? Time.time + impactDelay : float.PositiveInfinity;
        _actionRecoverFallbackAt = recoverDelay > 0f ? Time.time + recoverDelay : Time.time + hardEndDelay;

        bool keepMovementLoopThroughAttackWindup =
            kind == ActionKind.Attack &&
            KeepMovementLoopAudioUntilBasicAttackImpact &&
            _movementLoopAudioRequested;

        if (kind != ActionKind.None && !keepMovementLoopThroughAttackWindup && !UsesFiniteMovementLoopAudioWindow)
            SetMovementLoopAudioRequested(false);

        // Movement authority (Job B1): an action owns the agent for its duration — DriveActionApproach
        // and the paired root lock write it directly, by design. Recording the hold keeps
        // [BundyMoveAuth] continuous instead of going silent for the length of every attack, which read
        // the same as a dropped destination. The release is the next pursuit-layer line after
        // ResumeMovementAfterAction.
        if (kind != ActionKind.None)
            RecordPursuitDestination(transform.position, $"action-override:{kind}", "action owns movement");
    }

    private void ConfigurePairedFinisherTiming(string actionId)
    {
        if (string.IsNullOrEmpty(actionId) ||
            !BundyPairedActionCatalog.TryGet(actionId, out BundyPairedActionDefinition action) ||
            action == null)
        {
            return;
        }

        float duration = action.duration > 0.1f ? action.duration : FinisherImpactFallbackSeconds;
        float normalizedImpact = action.damageNormalizedTime > 0f
            ? Mathf.Clamp01(action.damageNormalizedTime)
            : Mathf.Clamp01((duration - PairedFinisherKillLeadSeconds) / Mathf.Max(0.01f, duration));
        float killDelay = Mathf.Max(0.25f, duration * normalizedImpact);
        float recoverDelay = Mathf.Max(killDelay + 0.05f, duration + PairedFinisherRecoverTailSeconds);
        float bloodDelay = Mathf.Clamp(duration * 0.38f, 0.45f, Mathf.Max(0.45f, killDelay - 0.35f));

        _actionImpactFallbackAt = Time.time + killDelay;
        _actionRecoverFallbackAt = Time.time + recoverDelay;
        _finisherBloodFallbackAt = Time.time + bloodDelay;

        LogDiagnostic(
            $"Paired finisher timing '{actionId}': duration={duration:F2}s, " +
            $"damageNormalized={normalizedImpact:F3}, bloodDelay={bloodDelay:F2}s, killDelay={killDelay:F2}s, recoverDelay={recoverDelay:F2}s.");
    }

    private void UpdateActionFallbacks()
    {
        if (_actionKind == ActionKind.None)
            return;

        if (_actionKind == ActionKind.Finisher && !_actionGrabResolved && Time.time >= _actionGrabFallbackAt)
            OnFinisherGrab();

        if (_actionKind == ActionKind.Finisher &&
            !_finisherBloodVisualStarted &&
            Time.time >= _finisherBloodFallbackAt)
        {
            PlayerControllerB victim = _activeFinisherVictim ?? _grabbedTarget ?? _actionTarget;
            TryStartFinisherBlood(victim, "paired finisher blood fallback");
        }

        if (!_actionImpactResolved && Time.time >= _actionImpactFallbackAt)
        {
            if (_actionKind == ActionKind.Attack)
            {
                _dispatchingAttackImpactFallback = true;
                OnAttackImpact();
                _dispatchingAttackImpactFallback = false;
            }
            else if (_actionKind == ActionKind.Finisher)
                OnFinisherImpact();
            else
                _actionImpactResolved = true;
        }

        if (_actionRecoverResolved || Time.time < _actionRecoverFallbackAt)
            return;

        switch (_actionKind)
        {
            case ActionKind.Attack:
                OnAttackRecover();
                break;
            case ActionKind.Finisher:
                OnFinisherEnd();
                break;
            default:
                CompleteAction("fallback end");
                break;
        }
    }

    private bool IsActionLocked()
    {
        return _actionKind != ActionKind.None;
    }

    internal bool AllowsChainsawFootGrounding =>
        !isEnemyDead && !IsActionLocked() && !_pairedActionRootLockActive && !_turnClipRootLockActive &&
        !_dropJumpActive;

    private void CompleteAction(string reason)
    {
        ActionKind completedKind = _actionKind;
        bool startPostBasicAttackWalk =
            completedKind == ActionKind.Attack &&
            PostBasicAttackWalkSeconds > 0f &&
            !_activeBasicVictimPaired;
        LogInfo($"Action complete: {completedKind}; reason={reason}.");
        if (completedKind == ActionKind.Attack)
        {
            // Sampled before the state clears so attackIndex/phase are still live; the tail covers
            // the post-cleanup re-face + maneuver roll (H3 window).
            LogAttackSeamEvent($"action-cleanup({reason})");
            _attackSeamTailLogUntil = Mathf.Max(_attackSeamTailLogUntil, Time.time + 0.5f);
        }
        try
        {
            if (completedKind == ActionKind.Attack)
                ReleaseActivePairedBasicAttack(_activeBasicVictim ?? _grabbedTarget ?? _actionTarget, reason);
            else if (completedKind == ActionKind.Finisher)
                ReleaseActivePairedFinisher(_activeFinisherVictim ?? _grabbedTarget ?? _actionTarget, reason);

            BundyVictimInteractionsBridge.EndForBundy(this, $"Bundy action cleanup: {reason}");
            BundyVictimPlayableController.EndForBundy(this, $"Bundy action cleanup: {reason}");
            // #321 (A1): last-resort restore. Covers every path that never reaches a release RPC —
            // victim despawn, enemy death mid-finisher, timeout cleanup — so an ignored collision
            // pair can never outlive the action that created it.
            BundyFinisherCollisionSuppressor.RestoreAllForBundy(this, $"Bundy action cleanup: {reason}");
            // #322: the gore chute is a per-frame driver, so it follows the same teardown points as
            // #321's collision suppression rather than relying on its own timeout.
            EndFinisherGoreBloodFX($"Bundy action cleanup: {reason}");
            // #323: same teardown point, same argument — the phased gore audio and the engine
            // muffle must not outlive the action either. A no-op when no cut is in flight; when one
            // is, this is what catches a finisher that never reached a release RPC (victim despawn,
            // Bundy killed mid-cut, timeout), and it hard-fades rather than ringing out because by
            // definition the picture stopped early.
            EndSawCuttingLocal($"Bundy action cleanup: {reason}", interrupted: true);
            BundyAttackerPlayableController.EndActive(this, $"Bundy action cleanup: {reason}");
            ResetActionPropVisibility();
        }
        catch (Exception e)
        {
            LogWarning($"Action cleanup exception during {reason}: {e}");
        }
        finally
        {
            _actionKind = ActionKind.None;
            _attackPhase = AttackPhase.None;
            ClearChainAttack();
            _lungeActive = false;
            _finisherLungeActive = false;
            _hasForcedCriticalApproachForward = false;
            _forcedCriticalApproachForward = Vector3.zero;
            ClearLungeAim();
            _actionTarget = null;
            _grabbedTarget = null;
            _actionGrabResolved = false;
            _actionImpactResolved = false;
            _actionRecoverResolved = false;
            _activeAttackIndex = -1;
            _activeAttackCountsAsGrab = false;
            ResetBasicAttackVisualExit();
            _activeBasicVictim = null;
            _activeBasicVictimPaired = false;
            _sprintArrivalAttackActive = false; // #288: the sprint-attack stun immunity dies with the swing
            _dispatchingAttackImpactFallback = false;
            _attackActiveWindowStartAt = 0f;
            _attackActiveWindowEndAt = 0f;
            _attackHitConfirmed = false;
            _chainsawWallImpactResolved = false;
            _hasLastAttackContactPoint = false;
            ClearChainsawBladeHitboxWindow($"action cleanup: {reason}");
            ClearBasicAttackContactLatch();
            _activeFinisherVictim = null;
            _activeFinisherEntryIndex = -1;
            _activeFinisherBodyIndex = -1;
            _activeFinisherVictimPaired = false;
            _activeChainsawFinisherUsesInteractions = false;
            _chainsawFinisherVictimFallbackLocal = false;
            _finisherBloodVisualStarted = false;
            _finisherBloodVisualPlayedLocal = false;
            _finisherBloodFallbackAt = float.PositiveInfinity;
            _activePairedActionId = null;
            ClearPairedActionRootLock();
            _nextAttackAllowedAt = completedKind == ActionKind.PullStart
                ? Time.time
                : Time.time + AttackCooldown;
            // Covers both completion paths: the OnPullStartDone anim event AND the 3.25s fallback
            // (the r40 ship rebake dropped ALL animation events, so the fallback is the live path).
            if (completedKind == ActionKind.PullStart)
                EndSawStall(reason);
            if (startPostBasicAttackWalk)
            {
                _postBasicAttackWalkUntil = Time.time + PostBasicAttackWalkSeconds;
                EnforcePostBasicAttackCalmWindow();
                ClearObservedVelocityIdleDemotionState();
                LogInfo($"BundyPostAttackWalk started: duration={PostBasicAttackWalkSeconds:F2}s, reason={reason}.");
            }
            if (completedKind == ActionKind.Finisher)
                QueueVocal(BundyAudioCue.FinisherVocal, DialogueAfterEventDelaySeconds, "finisher complete");

            if (completedKind == ActionKind.Stagger)
            {
                RestoreStaggerAnimatorSpeed();
                _staggerKnockdownActive = false;
            }
            ReleaseStaggerAgentStop();
            // #288: THE action-exit seam is also the one place every suppression source is
            // guaranteed to have been cleared, so republish here instead of leaving a frame of stale
            // "still paired" on every remote.
            MaintainReactionSuppressionMirror();

            // Job B1: the ONE action-exit seam. Every action — timed action, basic attack, lunge,
            // finisher, finisher-lunge miss/timeout, emote, movement context, pull start, stagger and
            // the generic fallback end — funnels through CompleteAction, and this finally block runs
            // even when the cleanup above throws. ResumeMovementAfterAction now releases the agent
            // unconditionally, so nothing leaves here still holding it.
            _lastActionExitKind = completedKind;
            _lastActionExitAt = Time.time;
            // Issue #205: the knockdown-recovery taunt is the sprint's primary trigger, and it starts
            // here rather than when the taunt begins — the taunt still owns the root while it plays,
            // and putting the agent at 7.65 m/s under a held root is how the movement-authority
            // watchdog gets tripped. Any OTHER action ending here means something entered from
            // AnyState and already took the Base Layer off the gait.
            OnActionCompletedForSprint(completedKind);
            ResumeMovementAfterAction();
        }
    }

    private void ReleaseStaggerAgentStop()
    {
        if (!_staggerAgentStopApplied)
            return;
        _staggerAgentStopApplied = false;
        if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
            agent.isStopped = false;
    }

    private void ShowFinisherProp(int bodyIndex)
    {
        SetFinisherPropVisibilityLocal(bodyIndex, true);

        if (IsServer)
            SetFinisherPropVisibilityClientRpc(bodyIndex, true);
    }

    private void ResetActionPropVisibility()
    {
        ResetActionPropVisibilityLocal();

        if (IsServer)
            ResetActionPropsClientRpc();
    }

    [ClientRpc]
    public void SetFinisherPropVisibilityClientRpc(int bodyIndex, bool visible)
    {
        SetFinisherPropVisibilityLocal(bodyIndex, visible);
    }

    [ClientRpc]
    public void ResetActionPropsClientRpc()
    {
        ResetActionPropVisibilityLocal();
    }

    private PlayerControllerB ResolveActionTarget(float range)
    {
        if (IsReachableHuntTarget(_actionTarget) && Vector3.Distance(transform.position, _actionTarget.transform.position) <= range)
            return _actionTarget;

        PlayerControllerB huntTarget = GetCurrentHuntTarget();
        if (IsReachableHuntTarget(huntTarget) && Vector3.Distance(transform.position, huntTarget.transform.position) <= range)
            return huntTarget;

        return FindClosestLivingPlayer(range);
    }

    private PlayerControllerB ResolveBasicAttackImpactTarget()
    {
        if (_activeAttackCountsAsGrab)
            return ResolveLockedActionTarget(BasicAttackRange + 1.5f);

        if (RequiresPhysicalBasicAttackContact)
            return ResolvePhysicalBasicAttackContactTarget();

        return ResolveActionTarget(BasicAttackRange + 0.75f);
    }

    private PlayerControllerB ResolvePhysicalBasicAttackContactTarget()
    {
        if (TryGetLatchedBasicAttackContactTarget(out PlayerControllerB latchedTarget))
            return latchedTarget;

        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null)
            return null;

        if (_chainsawBladeHitbox != null && _chainsawBladeHitbox.IsWindowActive)
        {
            bool sampledBlade = _chainsawBladeHitbox.TryResolveContact(
                players,
                out PlayerControllerB bladeContact,
                out Vector3 bladePoint,
                out float bladeDistance,
                out Bounds bladeBounds,
                out Vector3 bladeStart,
                out Vector3 bladeEnd);

            if (sampledBlade)
            {
                LogBladeAimDiagnostic(bladeContact, bladePoint, bladeDistance, bladeBounds, bladeStart, bladeEnd);
                LogChainsawGripDriftDiagnostic();

                if (ShouldLog(BundyLogCategory.Combat, 2))
                {
                    LogDiagnostic(
                        BundyLogCategory.Combat,
                        2,
                        $"BundyBladeHitboxContact hit={(bladeContact != null ? PlayerLabel(bladeContact) : "<none>")}, " +
                        $"distance={bladeDistance:F2}, point={FormatVector(bladePoint)}, " +
                        $"boundsCenter={FormatVector(bladeBounds.center)}, boundsSize={FormatVector(bladeBounds.size)}.");
                }

                return bladeContact;
            }

            LogDiagnostic(BundyLogCategory.Combat, 2, "BundyBladeHitboxContact hit=<none>, reason=active blade hitbox had no usable geometry; broad fallback suppressed.");
            return null;
        }

        Transform propTx = GetRuntimeChainsawProp();
        Vector3 contactCenter = transform.position +
            (Vector3.up * BasicAttackContactVerticalOffset) +
            (transform.forward * BasicAttackContactForwardOffset);
        float contactRadius = BasicAttackContactRadius;

        if (TryFindClosestPlayerTouchingRuntimeChainsaw(
            players,
            contactRadius,
            out PlayerControllerB sawContact,
            out float sawContactDistance,
            out Vector3 sawContactPoint,
            out Bounds chainsawBounds))
        {
            // Renderer bounds are the authoritative runtime saw contact source; this avoids guessing
            // blade direction from prop.forward, which is not aligned with the actual blade.
            contactCenter = sawContact != null ? sawContactPoint : chainsawBounds.center;

            if (DebugLogs)
            {
                string propFwd = propTx != null ? FormatVector(propTx.forward) : "<n/a>";
                string propUp = propTx != null ? FormatVector(propTx.up) : "<n/a>";
                LogDiagnostic(
                    $"BundyAttackChainsawProp chainsawBoundsCenter={FormatVector(chainsawBounds.center)}, " +
                    $"chainsawBoundsExtents={FormatVector(chainsawBounds.extents)}, " +
                    $"rootFwd={FormatVector(transform.forward)}, propFwd={propFwd}, propUp={propUp}, " +
                    $"attackForwardOffset={BasicAttackContactForwardOffset}, verticalOffset={BasicAttackContactVerticalOffset}, " +
                    $"contactPoint={FormatVector(contactCenter)}, contactRadius={contactRadius:F2}, " +
                    $"rendererContact={(sawContact != null)}, contactDistance={sawContactDistance:F2}.");

                Debug.DrawRay(contactCenter, Vector3.up * 0.35f, sawContact != null ? Color.red : Color.yellow, 0.2f, false);

                if (sawContact == null)
                {
                    LogAnimEvent(
                        nameof(OnAttackImpact),
                        $"physical chainsaw contact miss; runtimeSawBounds center={FormatVector(chainsawBounds.center)}, " +
                        $"size={FormatVector(chainsawBounds.size)}, radius={contactRadius:F2}");
                }
            }

            return sawContact;
        }
        else if (propTx != null)
        {
            // Fallback: prop exists but has no renderer bounds (e.g. missing mesh).
            // Use prop position + forward reach instead of the root-based straight-ahead offset.
            contactCenter = propTx.position + propTx.forward * BasicAttackContactForwardOffset;
        }

        Vector3 sweepStart = _hasLastAttackContactPoint ? _lastAttackContactPoint : contactCenter;
        _lastAttackContactPoint = contactCenter;
        _hasLastAttackContactPoint = true;

        PlayerControllerB closest = FindClosestPlayerAlongAttackSweep(players, sweepStart, contactCenter, contactRadius, out float closestContactDistance);

        if (DebugLogs)
        {
            Debug.DrawLine(sweepStart, contactCenter, closest != null ? Color.red : Color.yellow, 0.2f, false);
            Debug.DrawRay(contactCenter, Vector3.up * 0.35f, closest != null ? Color.red : Color.yellow, 0.2f, false);
        }

        if (closest == null && DebugLogs)
        {
            LogAnimEvent(
                nameof(OnAttackImpact),
                $"physical chainsaw contact miss; center={FormatVector(contactCenter)}, radius={contactRadius:F2}, propBased={(propTx != null)}");
        }

        return closest;
    }

    private void LogBladeAimDiagnostic(
        PlayerControllerB bladeContact,
        Vector3 bladePoint,
        float bladeDistance,
        Bounds bladeBounds,
        Vector3 bladeStart,
        Vector3 bladeEnd)
    {
        if (!DebugLogs || Time.time < _nextBladeAimDiagnosticAt)
            return;

        _nextBladeAimDiagnosticAt = Time.time + 0.15f;

        PlayerControllerB target = bladeContact ?? _activeBasicVictim ?? _actionTarget ?? GetCurrentHuntTarget();
        Vector3 targetPoint;
        if (!TryGetBasicAttackPropAimPoint(out targetPoint))
            targetPoint = target != null ? target.transform.position + Vector3.up * 1.1f : bladeBounds.center;

        Vector3 segment = bladeEnd - bladeStart;
        float bladeLength = segment.magnitude;
        Vector3 bladeMid = (bladeStart + bladeEnd) * 0.5f;
        Vector3 toTarget = targetPoint - bladeMid;
        float signedAimDot = 0f;
        float bestAimDot = 0f;
        float aimAngle = 180f;
        float closestT = 0f;
        if (bladeLength > 0.0001f && toTarget.sqrMagnitude > 0.0004f)
        {
            Vector3 segmentDir = segment / bladeLength;
            Vector3 targetDir = toTarget.normalized;
            signedAimDot = Mathf.Clamp(Vector3.Dot(segmentDir, targetDir), -1f, 1f);
            bestAimDot = Mathf.Abs(signedAimDot);
            aimAngle = Mathf.Acos(bestAimDot) * Mathf.Rad2Deg;
            closestT = Mathf.Clamp01(Vector3.Dot(targetPoint - bladeStart, segment) / Mathf.Max(0.0001f, segment.sqrMagnitude));
        }

        float rootDistance = target != null ? Vector3.Distance(transform.position, target.transform.position) : -1f;
        string rootDistanceText = rootDistance >= 0f ? $"{rootDistance:F2}m" : "<n/a>";
        float contactRadius = _chainsawBladeHitbox != null
            ? Mathf.Max(0.01f, _chainsawBladeHitbox.radius + _chainsawBladeHitbox.padding)
            : BasicAttackContactRadius;

        LogDiagnostic(
            $"BundyBladeAim attackIndex={_activeAttackIndex}, target={(target != null ? PlayerLabel(target) : "<none>")}, " +
            $"hit={(bladeContact != null ? PlayerLabel(bladeContact) : "<none>")}, rootDistance={rootDistanceText}, " +
            $"bladeDistance={bladeDistance:F2}m, contactRadius={contactRadius:F2}, signedAimDot={signedAimDot:F3}, " +
            $"bestAimDot={bestAimDot:F3}, aimAngle={aimAngle:F1}, closestT={closestT:F2}, " +
            $"start={FormatVector(bladeStart)}, end={FormatVector(bladeEnd)}, contactPoint={FormatVector(bladePoint)}, " +
            $"targetPoint={FormatVector(targetPoint)}, boundsSize={FormatVector(bladeBounds.size)}");
    }

    private void LogChainsawGripDriftDiagnostic()
    {
        if (!DebugLogs || Time.time < _nextChainsawGripDriftDiagnosticAt)
            return;

        _nextChainsawGripDriftDiagnosticAt = Time.time + 0.15f;

        Transform prop = GetRuntimeChainsawProp();
        bool hasHands = TryGetAnimatorHandBones(out Transform leftHand, out Transform rightHand);
        if (prop == null || !hasHands)
        {
            LogDiagnostic(
                $"BundyChainsawProp mode=ai-diagnostic-only, source=AIBase, missingSetup prop={(prop != null)}, " +
                $"animator={(creatureAnimator != null)}, leftHand={(leftHand != null)}, rightHand={(rightHand != null)}");
            return;
        }

        Transform rightTarget = FindChildRecursive(prop, "BundyChainsaw_RightHandIKTarget");

        Vector3 leftGripWorld = leftHand.position;
        Vector3 rightGripWorld = rightTarget != null ? rightTarget.position : rightHand.position;
        float rightError = rightTarget != null ? Vector3.Distance(rightGripWorld, rightHand.position) : -1f;
        string leftErrorText = "<n/a>";
        string rightErrorText = rightError >= 0f ? $"{rightError:F3}m" : "<n/a>";
        string rightTargetText = rightTarget != null ? FormatVector(rightTarget.position) : "<none>";

        string clipName = "<none>";
        float normalizedTime = 0f;
        float clipWeight = 0f;
        if (creatureAnimator != null && creatureAnimator.runtimeAnimatorController != null && creatureAnimator.layerCount > 0)
        {
            TryGetBasicAttackClip(creatureAnimator.GetCurrentAnimatorClipInfo(0), out clipName, out clipWeight);
            normalizedTime = creatureAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        }

        LogDiagnostic(
            $"BundyChainsawProp mode=ai-diagnostic-only, source=AIBase, clip='{clipName}', norm={normalizedTime:F3}, " +
            $"weight={clipWeight:F2}, leftError={leftErrorText}, rightError={rightErrorText}, " +
            $"handSeparation={Vector3.Distance(leftHand.position, rightHand.position):F3}m, " +
            $"leftHand={FormatVector(leftHand.position)}, rightHand={FormatVector(rightHand.position)}, " +
            $"leftGripWorld={FormatVector(leftGripWorld)}, rightGripWorld={FormatVector(rightGripWorld)}, " +
            $"rightTarget={rightTargetText}, propPos={FormatVector(prop.position)}, propEuler={FormatVector(prop.eulerAngles)}");
    }

    private bool TryGetAnimatorHandBones(out Transform leftHand, out Transform rightHand)
    {
        leftHand = null;
        rightHand = null;

        Animator animator = creatureAnimator;
        if (animator == null)
            return false;

        if (animator.avatar != null && animator.avatar.isHuman)
        {
            leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        }

        // Prefer the live 68-bone chainsaw armature. The CC5 head graft retains
        // a hidden full CC_Base hierarchy for facial skinning; choosing those
        // similarly named hands reports metre-scale nonsense and does not
        // describe the body that the Animator actually drives.
        Transform canonicalArmature = FindChildRecursive(animator.transform, "Armature");
        if (leftHand == null && canonicalArmature != null)
            leftHand = FindChildRecursive(canonicalArmature, "hand_l");
        if (rightHand == null && canonicalArmature != null)
            rightHand = FindChildRecursive(canonicalArmature, "hand_r");

        if (leftHand == null)
            leftHand = FindChildRecursive(animator.transform, "mixamorig:LeftHand") ??
                FindChildRecursive(animator.transform, "CC_Base_L_Hand");
        if (rightHand == null)
            rightHand = FindChildRecursive(animator.transform, "mixamorig:RightHand") ??
                FindChildRecursive(animator.transform, "CC_Base_R_Hand");

        return leftHand != null && rightHand != null;
    }

    private bool TryLatchPhysicalBasicAttackContact(string source)
    {
        if (_actionKind != ActionKind.Attack ||
            _attackHitConfirmed ||
            _activeAttackCountsAsGrab ||
            !RequiresPhysicalBasicAttackContact ||
            !_lungeActive ||
            Time.time < _attackActiveWindowStartAt)
        {
            return false;
        }

        if (Time.time > _attackActiveWindowEndAt)
            return false;

        if (TryGetLatchedBasicAttackContactTarget(out _))
            return true;

        PlayerControllerB closest = ResolvePhysicalBasicAttackContactTarget();
        if (closest == null)
            return false;

        _latchedBasicAttackContactTarget = closest;
        _latchedBasicAttackContactAt = Time.time;
        _latchedBasicAttackRootDistance = Vector3.Distance(transform.position, closest.transform.position);

        if (DebugLogs)
        {
            LogAnimEvent(
                nameof(OnAttackImpact),
                $"latched physical lunge contact; source={source}, victim={PlayerLabel(closest)}, " +
                $"rootDistance={_latchedBasicAttackRootDistance:F2}m, runtime-saw-contact=true");
        }

        return true;
    }

    private bool TryGetLatchedBasicAttackContactTarget(out PlayerControllerB target)
    {
        target = null;
        if (_latchedBasicAttackContactTarget == null)
            return false;

        if (!IsLivingPlayer(_latchedBasicAttackContactTarget) ||
            Time.time > _latchedBasicAttackContactAt + BasicAttackContactLatchSeconds ||
            Time.time > _attackActiveWindowEndAt + BasicAttackContactLatchSeconds)
        {
            ClearBasicAttackContactLatch();
            return false;
        }

        target = _latchedBasicAttackContactTarget;
        return true;
    }

    private bool IsLatchedBasicAttackContactTarget(PlayerControllerB target)
    {
        return target != null &&
            TryGetLatchedBasicAttackContactTarget(out PlayerControllerB latchedTarget) &&
            latchedTarget == target;
    }

    private void ClearBasicAttackContactLatch()
    {
        _latchedBasicAttackContactTarget = null;
        _latchedBasicAttackContactAt = float.NegativeInfinity;
        _latchedBasicAttackRootDistance = 0f;
    }

    // #276: the runtime chainsaw prop is baked into the prefab and never reparented at runtime, but
    // the contact path used to FindChildRecursive + GetComponentsInChildren<Renderer> for it every
    // lunge frame. Cache both (mirrors BundyChainsawBladeHitbox's renderer cache) and invalidate on
    // attack begin; a destroyed prop transform re-resolves via the Unity null check.
    private Transform _runtimeChainsawPropCache;
    private Renderer[] _runtimeChainsawRendererCache;

    private void InvalidateRuntimeChainsawPropCache()
    {
        _runtimeChainsawPropCache = null;
        _runtimeChainsawRendererCache = null;
    }

    private Transform GetRuntimeChainsawProp()
    {
        if (_runtimeChainsawPropCache == null)
        {
            _runtimeChainsawPropCache = FindChildRecursive(transform, RuntimeChainsawPropName);
            _runtimeChainsawRendererCache = null;
        }

        return _runtimeChainsawPropCache;
    }

    private Renderer[] GetRuntimeChainsawRenderers(Transform prop)
    {
        if (prop == null)
            return null;

        if (_runtimeChainsawRendererCache == null || _runtimeChainsawRendererCache.Length == 0)
            _runtimeChainsawRendererCache = prop.GetComponentsInChildren<Renderer>(true);

        return _runtimeChainsawRendererCache;
    }

    private static PlayerControllerB FindClosestPlayerAlongAttackSweep(
        PlayerControllerB[] players,
        Vector3 start,
        Vector3 end,
        float radius,
        out float closestContactDistance)
    {
        PlayerControllerB closest = null;
        closestContactDistance = radius;
        const int samples = 5;
        for (int step = 0; step < samples; step++)
        {
            float t = samples <= 1 ? 1f : step / (float)(samples - 1);
            Vector3 point = Vector3.Lerp(start, end, t);
            foreach (PlayerControllerB player in players)
            {
                if (player == null || player.isPlayerDead || !player.gameObject.activeInHierarchy)
                    continue;

                float contactDistance = DistanceToPlayerCollision(player, point);
                if (contactDistance <= closestContactDistance)
                {
                    closest = player;
                    closestContactDistance = contactDistance;
                }
            }
        }

        return closest;
    }

    private bool TryFindClosestPlayerTouchingRuntimeChainsaw(
        PlayerControllerB[] players,
        float contactPadding,
        out PlayerControllerB closest,
        out float closestContactDistance,
        out Vector3 closestSawPoint,
        out Bounds aggregateBounds)
    {
        closest = null;
        closestContactDistance = float.PositiveInfinity;
        closestSawPoint = Vector3.zero;
        aggregateBounds = default;

        if (players == null)
            return false;

        Transform prop = GetRuntimeChainsawProp();
        if (prop == null)
            return false;

        Renderer[] renderers = GetRuntimeChainsawRenderers(prop);
        if (renderers == null)
            return false;

        bool foundRenderer = false;
        bool foundNearest = false;
        Vector3 nearestSawPoint = Vector3.zero;
        float nearestDistance = float.PositiveInfinity;

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null ||
                !renderer.enabled ||
                !renderer.gameObject.activeInHierarchy ||
                !IsRuntimeChainsawContactRenderer(renderer))
                continue;

            Bounds rendererBounds = renderer.bounds;
            Vector3 rootContactCenter = transform.position + Vector3.up * BasicAttackContactVerticalOffset;
            if (Vector3.Distance(rootContactCenter, rendererBounds.ClosestPoint(rootContactCenter)) > MaxPhysicalBasicAttackRootReach)
                continue;

            if (!foundRenderer)
            {
                aggregateBounds = rendererBounds;
                foundRenderer = true;
            }
            else
            {
                aggregateBounds.Encapsulate(rendererBounds);
            }

            foreach (PlayerControllerB player in players)
            {
                if (player == null || player.isPlayerDead || !player.gameObject.activeInHierarchy)
                    continue;

                Vector3 playerCenter = player.transform.position + Vector3.up * 0.9f;
                Vector3 sawPoint = rendererBounds.ClosestPoint(playerCenter);
                float contactDistance = DistanceToPlayerCollision(player, sawPoint);

                if (contactDistance < nearestDistance)
                {
                    nearestDistance = contactDistance;
                    nearestSawPoint = sawPoint;
                    foundNearest = true;
                }

                if (contactDistance <= contactPadding && contactDistance <= closestContactDistance)
                {
                    closest = player;
                    closestContactDistance = contactDistance;
                    closestSawPoint = sawPoint;
                }
            }
        }

        if (!foundRenderer)
            return false;

        if (closest == null)
        {
            closestContactDistance = foundNearest ? nearestDistance : float.PositiveInfinity;
            closestSawPoint = foundNearest ? nearestSawPoint : aggregateBounds.center;
        }

        return true;
    }

    private static bool IsRuntimeChainsawContactRenderer(Renderer renderer)
    {
        return renderer is MeshRenderer || renderer is SkinnedMeshRenderer;
    }

    private bool TryGetRuntimeChainsawBounds(out Bounds bounds)
    {
        bounds = default;
        Transform prop = GetRuntimeChainsawProp();
        if (prop == null)
            return false;

        Renderer[] renderers = GetRuntimeChainsawRenderers(prop);
        if (renderers == null)
            return false;

        bool found = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                continue;

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return found;
    }

    internal Vector3 ResolveFinisherBloodContactPoint(PlayerControllerB victim)
    {
        return ResolveFinisherBloodContactPoint(victim, _activeFinisherBodyIndex);
    }

    /// <summary>
    /// Where the blade is currently in the victim. <paramref name="bodyIndex"/> selects the wound
    /// band (#322): the neck slash opens a throat, the saw-middle a chest, the stab-lift a belly, and
    /// clamping all three to one torso band put every variant's blood in the same place.
    /// </summary>
    internal Vector3 ResolveFinisherBloodContactPoint(PlayerControllerB victim, int bodyIndex)
    {
        Vector3 fallback = GetPlayerBloodCenter(victim);
        if (victim == null)
            return fallback;

        Bounds playerBounds = GetPlayerBloodBounds(victim);
        Vector3 playerCenter = GetPlayerBloodCenter(victim);
        GetFinisherBloodBodyBand(bodyIndex, out float minFraction, out float maxFraction);

        if (TryGetRuntimeChainsawBounds(out Bounds chainsawBounds))
        {
            Vector3 pointOnSaw = chainsawBounds.ClosestPoint(playerCenter);
            return ClampFinisherBloodPointToBodyBand(pointOnSaw, playerBounds, playerCenter, minFraction, maxFraction);
        }

        Transform prop = GetRuntimeChainsawProp();
        if (prop != null)
        {
            Vector3 bladeDirection = prop.forward.sqrMagnitude > 0.001f ? prop.forward.normalized : transform.forward;
            Vector3 propPoint = prop.position + bladeDirection * 0.45f;
            return ClampFinisherBloodPointToBodyBand(propPoint, playerBounds, playerCenter, minFraction, maxFraction);
        }

        return fallback;
    }

    /// <summary>
    /// How far the chute is tilted up out of the wound. Purely horizontal reads as a garden hose
    /// aimed at the far wall; straight up reads as a fountain. This is the arc between them.
    /// </summary>
    private const float FinisherBloodUpwardLift = 0.35f;

    /// <summary>
    /// Which way the blood leaves the wound (#322).
    ///
    /// <b>This used to be backwards, and that was the whole visible defect.</b> The old code computed
    /// <c>bundyCenter - contactPoint</c>, i.e. a vector pointing from the victim back INTO Bundy, so
    /// the cone hosed the attacker's chest. Blood comes out of the far side of a wound: the axis is
    /// blade-to-victim, <c>contactPoint - bundyCenter</c>. Every fallback below is outward for the
    /// same reason — Bundy faces his victim during a finisher, so his own forward is outward too.
    /// </summary>
    internal Vector3 ResolveFinisherBloodDirection(Vector3 contactPoint, PlayerControllerB victim)
    {
        Vector3 bundyCenter = transform.position + Vector3.up * 1.05f;
        Vector3 direction = contactPoint - bundyCenter;
        direction.y = 0f;

        if (direction.sqrMagnitude < 0.001f && victim != null)
        {
            direction = victim.transform.position - transform.position;
            direction.y = 0f;
        }

        if (direction.sqrMagnitude < 0.001f)
        {
            direction = transform.forward;
            direction.y = 0f;
        }

        if (direction.sqrMagnitude < 0.001f)
            direction = Vector3.forward;

        direction.Normalize();
        return (direction + Vector3.up * FinisherBloodUpwardLift).normalized;
    }

    internal Quaternion ResolveFinisherBloodRotation(Vector3 contactPoint, PlayerControllerB victim)
    {
        return Quaternion.LookRotation(ResolveFinisherBloodDirection(contactPoint, victim), Vector3.up);
    }

    /// <summary>Vertical wound band as a fraction of the victim's visual height, per finisher variant.</summary>
    private static void GetFinisherBloodBodyBand(int bodyIndex, out float minFraction, out float maxFraction)
    {
        switch (Mathf.Clamp(bodyIndex, 0, 2))
        {
            case 0: // neck slash
                minFraction = 0.74f;
                maxFraction = 0.94f;
                return;
            case 2: // stab lift
                minFraction = 0.38f;
                maxFraction = 0.68f;
                return;
            default: // saw middle
                minFraction = 0.45f;
                maxFraction = 0.80f;
                return;
        }
    }

    // #276: per-player collider arrays for the attack-contact scans. DistanceToPlayerCollision runs
    // up to ~20x per frame during an active swing window (sweep samples x players, plus the blade
    // hitbox calling twice per player), and GetComponentsInChildren<Collider> allocates every call.
    // Membership only changes when colliders are added/destroyed, so cache the arrays for the attack
    // window and rebuild on attack begin / blade-window begin. Destroyed colliders inside a cached
    // array are skipped by the existing per-element null/enabled checks.
    private static readonly Dictionary<PlayerControllerB, Collider[]> s_attackContactColliderCache = new();

    internal static void InvalidateAttackContactColliderCache()
    {
        s_attackContactColliderCache.Clear();
    }

    private static Collider[] GetAttackContactColliders(PlayerControllerB player)
    {
        if (!s_attackContactColliderCache.TryGetValue(player, out Collider[] colliders) || colliders == null)
        {
            colliders = player.GetComponentsInChildren<Collider>(true);
            s_attackContactColliderCache[player] = colliders;
        }

        return colliders;
    }

    internal static float DistanceToPlayerCollision(PlayerControllerB player, Vector3 point)
    {
        if (player == null)
            return float.PositiveInfinity;

        float closest = Vector3.Distance(point, player.transform.position + Vector3.up * 0.9f);
        Collider[] colliders = GetAttackContactColliders(player);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                continue;

            Vector3 closestPoint = collider.ClosestPoint(point);
            float distance = Vector3.Distance(point, closestPoint);
            if (distance < closest)
                closest = distance;
        }

        return closest;
    }

    private static Vector3 GetPlayerBloodCenter(PlayerControllerB player)
    {
        if (player == null)
            return Vector3.zero;

        if (TryGetPlayerVisualBloodBounds(player, out Bounds bounds))
            return new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * 0.62f, bounds.center.z);

        Transform body = player.playerBodyAnimator != null ? player.playerBodyAnimator.transform : player.transform;
        return body.position + Vector3.up * 1.18f;
    }

    private static Bounds GetPlayerBloodBounds(PlayerControllerB player)
    {
        if (TryGetPlayerVisualBloodBounds(player, out Bounds bounds))
            return bounds;

        Vector3 center = GetPlayerBloodCenter(player);
        return new Bounds(center, new Vector3(0.9f, 1.4f, 0.9f));
    }

    private static Vector3 ClampFinisherBloodPointToBodyBand(
        Vector3 point, Bounds playerBounds, Vector3 fallbackCenter, float minFraction, float maxFraction)
    {
        if (!IsFinite(point))
            point = fallbackCenter;

        if (playerBounds.size.sqrMagnitude < 0.001f)
            return point;

        float minY = playerBounds.min.y + playerBounds.size.y * minFraction;
        float maxY = playerBounds.min.y + playerBounds.size.y * maxFraction;
        Vector3 torsoPoint = point;
        torsoPoint.y = Mathf.Clamp(torsoPoint.y, minY, maxY);

        Vector3 closestBodyPoint = playerBounds.ClosestPoint(torsoPoint);
        if (Vector3.Distance(torsoPoint, closestBodyPoint) > 0.35f)
            torsoPoint = Vector3.Lerp(torsoPoint, closestBodyPoint, 0.65f);

        torsoPoint.y = Mathf.Clamp(torsoPoint.y, minY, maxY);
        return torsoPoint;
    }

    // #276: the finisher blood VFX resolves these bounds several times per frame for ~3.15s per
    // finisher, and the HashSet + GetComponentsInChildren<Renderer> scans allocated every call.
    // Renderer MEMBERSHIP on a player is stable, so cache the filtered set per victim; the bounds
    // themselves are still recomputed from live renderer.bounds every call. Invalidated at finisher
    // VFX spawn (BundyFinisherBloodVfx.Spawn) and whenever a different player is queried.
    private static PlayerControllerB s_bloodRendererCachePlayer;
    private static Renderer[] s_bloodRendererCache;

    internal static void InvalidatePlayerBloodRendererCache()
    {
        s_bloodRendererCachePlayer = null;
        s_bloodRendererCache = null;
    }

    private static Renderer[] GetPlayerBloodRenderers(PlayerControllerB player)
    {
        if (s_bloodRendererCache == null || !ReferenceEquals(s_bloodRendererCachePlayer, player))
        {
            var renderers = new HashSet<Renderer>();
            AddBloodRenderer(renderers, player.thisPlayerModel);
            AddBloodRenderer(renderers, player.thisPlayerModelLOD1);
            AddBloodRenderer(renderers, player.thisPlayerModelLOD2);
            AddBloodRenderers(renderers, player.thisPlayerBody != null ? player.thisPlayerBody.gameObject : null);
            AddBloodRenderers(renderers, player.playerBodyAnimator != null ? player.playerBodyAnimator.gameObject : null);
            AddBloodRenderers(renderers, player.meshContainer != null ? player.meshContainer.gameObject : null);

            var cached = new Renderer[renderers.Count];
            renderers.CopyTo(cached);
            s_bloodRendererCache = cached;
            s_bloodRendererCachePlayer = player;
        }

        return s_bloodRendererCache;
    }

    private static bool TryGetPlayerVisualBloodBounds(PlayerControllerB player, out Bounds bounds)
    {
        bounds = default;
        if (player == null)
            return false;

        Renderer[] renderers = GetPlayerBloodRenderers(player);

        bool found = false;
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                continue;

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return found;
    }

    private static void AddBloodRenderers(HashSet<Renderer> renderers, GameObject root)
    {
        if (renderers == null || root == null)
            return;

        Renderer[] childRenderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < childRenderers.Length; i++)
            AddBloodRenderer(renderers, childRenderers[i]);
    }

    private static void AddBloodRenderer(HashSet<Renderer> renderers, Renderer renderer)
    {
        if (renderers == null || renderer == null)
            return;

        if (!IsUsablePlayerBloodRenderer(renderer))
            return;

        renderers.Add(renderer);
    }

    private static bool IsUsablePlayerBloodRenderer(Renderer renderer)
    {
        if (renderer == null)
            return false;

        string name = renderer.name ?? string.Empty;
        string objectName = renderer.gameObject != null ? renderer.gameObject.name ?? string.Empty : string.Empty;
        string combined = (name + " " + objectName).ToLowerInvariant();
        if (combined.Contains("arm") || combined.Contains("hand") || combined.Contains("item") || combined.Contains("map"))
            return false;

        return true;
    }

    private PlayerControllerB ResolveLockedActionTarget(float fallbackRange)
    {
        if (IsLivingPlayer(_grabbedTarget))
            return _grabbedTarget;

        if (IsLivingPlayer(_actionTarget))
            return _actionTarget;

        return ResolveActionTarget(fallbackRange);
    }

}
