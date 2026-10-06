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
    private void ApplyDesiredMotionState(PlayerControllerB target)
    {
        if (!IsLivingPlayer(target))
        {
            SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
            return;
        }

        bool running = ShouldRun(target);
        SetMotionStateIfChanged(running ? BundyAnimatorParams.MotionRun : BundyAnimatorParams.MotionWalk);
    }

    private void PrepareActionMovement(PlayerControllerB target)
    {
        if (!LungeAimActive)
            FaceActionTarget(target, ManualActionTurnSpeedDegrees);
        DriveActionApproach(target);
    }

    private void MaintainActionMovement()
    {
        if (_finisherLungeActive)
        {
            MaintainFinisherLunge();
            return;
        }

        // The action gate suppresses destination updates for either config value. With the lock
        // enabled, also stop and zero the agent; disabled leaves its existing path/velocity alone.
        if (_actionKind == ActionKind.Stagger)
        {
            if (StaggerMovementLockEnabled)
                HoldAgentForStagger();
            return;
        }

        PlayerControllerB target = ResolveLockedActionTarget(BasicAttackRange + 2f);
        if (!IsLivingPlayer(target))
        {
            HoldAgentWithoutReset();
            return;
        }

        if ((_actionKind == ActionKind.Finisher && _activeFinisherVictimPaired) ||
            (_actionKind == ActionKind.Attack && _activeBasicVictimPaired))
        {
            MaintainPairedActionRootLock();
            HoldAgentWithoutReset();
            return;
        }

        if (!LungeAimActive)
            FaceActionTarget(target, ManualActionTurnSpeedDegrees);

        // #281: mid-chain the recovery hold is what made him dead-stop between swings. While the
        // sequence still owes reps he keeps closing on the live victim instead — the lunge was
        // already cleared on impact, so this falls through to DriveActionApproach's plain
        // action-walk branch and the cadence reads surge (during the swing), glide, surge.
        if ((_actionImpactResolved && !IsChainAttackGlideActive()) || _actionKind == ActionKind.Emote)
        {
            HoldAgentWithoutReset();
            return;
        }

        DriveActionApproach(target);
    }

    private void PreparePairedFinisherMovement(PlayerControllerB target)
    {
        FaceActionTarget(target);
        HoldAgentWithoutReset();
    }

    internal void ApplyPairedPlayableRootCorrection(
        string actionId,
        Transform attackerAnchor,
        float elapsed,
        float fallbackSequenceLength)
    {
        if (!_pairedActionRootLockActive ||
            _actionKind != ActionKind.Finisher ||
            !_activeFinisherVictimPaired ||
            attackerAnchor == null ||
            string.IsNullOrEmpty(actionId))
        {
            return;
        }

        if (!string.IsNullOrEmpty(_activePairedActionId) &&
            !string.Equals(_activePairedActionId, actionId, StringComparison.Ordinal))
        {
            return;
        }

        PlayerControllerB victim = _activeFinisherVictim ?? _grabbedTarget ?? _actionTarget;
        if (!IsLivingPlayer(victim) ||
            !BundyPairedActionCatalog.TryGet(actionId, out BundyPairedActionDefinition action) ||
            action == null)
        {
            return;
        }

        bool victimDebugAvailable = BundyVictimPlayableController.TryGetActiveDebugState(
            victim,
            this,
            out Vector3 victimAnchor,
            out Vector3 realPlayerAnchor,
            out Vector3 desiredVictimAnchor,
            out string victimDebugState);
        if (!victimDebugAvailable)
        {
            if (SyncDebugLogs)
            {
                LogDiagnostic(
                    $"BundySyncDebug root skipped-victim-debug: frame={Time.frameCount}, action='{actionId}', " +
                    $"victim={PlayerLabel(victim)}, elapsed={elapsed:F3}/{fallbackSequenceLength:F3}, " +
                    $"bundyRoot={FormatVector(transform.position)}, attackerAnchor={FormatVector(attackerAnchor.position)}.");
            }
            return;
        }

        if (string.Equals(action.kind, "Finisher", StringComparison.OrdinalIgnoreCase))
        {
            MaintainPairedActionRootLock();
            string presentationState = IsChainsawFinisherActionId(actionId)
                ? DisablePairedPresentationLift(victim, "chainsaw")
                : ApplyPairedPresentationGrounding(victim);
            if (SyncDebugLogs && Time.time >= _nextPairedSyncDebugAt)
            {
                _nextPairedSyncDebugAt = Time.time + 0.25f;
                _pairedSyncDebugTick++;
                LogDiagnostic(
                    $"BundySyncDebug root pinned: tick={_pairedSyncDebugTick}, frame={Time.frameCount}, " +
                    $"action='{actionId}', elapsed={elapsed:F3}/{fallbackSequenceLength:F3}, " +
                    $"bundyRoot={FormatVector(transform.position)}, lockedRoot={FormatVector(_pairedActionRootPosition)}, " +
                    $"attackerAnchor={FormatVector(attackerAnchor.position)}, victimSourceAnchor={FormatVector(victimAnchor)}, " +
                    $"victimPlayerAnchor={FormatVector(realPlayerAnchor)}, victimDesiredAnchor={FormatVector(desiredVictimAnchor)}, " +
                    $"{presentationState}, {victimDebugState}.");
            }
            return;
        }

        bool pelvisPin = false;
        Vector3 anchorDelta = SamplePairedAnchorDelta(action, elapsed, fallbackSequenceLength);
        Vector3 desiredAttackerAnchor = victimAnchor - (_pairedActionRootRotation * anchorDelta);
        Vector3 rawCorrection = desiredAttackerAnchor - attackerAnchor.position;
        rawCorrection.y = 0f;
        Vector3 correction = rawCorrection;

        if (!IsFinite(correction))
        {
            LogPairedSyncDebug(
                actionId,
                pelvisPin,
                elapsed,
                fallbackSequenceLength,
                attackerAnchor.position,
                victimAnchor,
                realPlayerAnchor,
                desiredVictimAnchor,
                anchorDelta,
                desiredAttackerAnchor,
                rawCorrection,
                Vector3.zero,
                "skipped-nonfinite",
                victimDebugState);
            return;
        }

        if (correction.sqrMagnitude < PairedRootCorrectionDeadZone * PairedRootCorrectionDeadZone)
        {
            LogPairedSyncDebug(
                actionId,
                pelvisPin,
                elapsed,
                fallbackSequenceLength,
                attackerAnchor.position,
                victimAnchor,
                realPlayerAnchor,
                desiredVictimAnchor,
                anchorDelta,
                desiredAttackerAnchor,
                rawCorrection,
                Vector3.zero,
                "deadzone",
                victimDebugState);
            return;
        }

        float distance = correction.magnitude;
        float maxDistance = PairedRootCorrectionMaxDistance;
        if (distance > maxDistance)
        {
            LogPairedSyncDebug(
                actionId,
                pelvisPin,
                elapsed,
                fallbackSequenceLength,
                attackerAnchor.position,
                victimAnchor,
                realPlayerAnchor,
                desiredVictimAnchor,
                anchorDelta,
                desiredAttackerAnchor,
                rawCorrection,
                Vector3.zero,
                "skipped-max",
                victimDebugState);
            if (Time.time >= _nextPairedRootCorrectionLogAt)
            {
                _nextPairedRootCorrectionLogAt = Time.time + PairedRootCorrectionLogInterval;
                LogDiagnostic(
                    $"Paired root correction skipped '{actionId}' ({(pelvisPin ? "pelvis-pin" : "metadata-delta")}): " +
                    $"correction={distance:F2}m, max={maxDistance:F2}m, " +
                    $"attackerAnchor={FormatVector(attackerAnchor.position)}, victimAnchor={FormatVector(victimAnchor)}.");
            }
            return;
        }

        if (distance > PairedRootCorrectionMaxStep)
            correction = correction.normalized * PairedRootCorrectionMaxStep;

        _pairedActionRootPosition += correction;
        transform.position += correction;

        if (agent != null && agent.enabled && agent.isOnNavMesh)
            agent.nextPosition = _pairedActionRootPosition;

        LogPairedSyncDebug(
            actionId,
            pelvisPin,
            elapsed,
            fallbackSequenceLength,
            attackerAnchor.position,
            victimAnchor,
            realPlayerAnchor,
            desiredVictimAnchor,
            anchorDelta,
            desiredAttackerAnchor,
            rawCorrection,
            correction,
            "applied",
            victimDebugState);

        if (Time.time >= _nextPairedRootCorrectionLogAt)
        {
            _nextPairedRootCorrectionLogAt = Time.time + PairedRootCorrectionLogInterval;
            LogDiagnostic(
                $"Paired root correction '{actionId}' ({(pelvisPin ? "pelvis-pin" : "metadata-delta")}): " +
                $"step={FormatVector(correction)}, " +
                $"remaining={(desiredAttackerAnchor - attackerAnchor.position).magnitude:F2}m, " +
                $"elapsed={elapsed:F2}/{fallbackSequenceLength:F2}.");
        }
    }

    private string ApplyPairedPresentationGrounding(PlayerControllerB victim)
    {
        if (!_pairedActionRootLockActive || _actionKind != ActionKind.Finisher)
            return "presentationLift=<inactive>";

        float groundY = _pairedActionRootPosition.y + PairedPresentationGroundClearance;
        bool foundMin = false;
        float minY = float.PositiveInfinity;
        float attackerMinY = 0f;
        float victimMinY = 0f;
        bool attackerBounds = creatureAnimator != null &&
            TryGetSkinnedRendererMinY(creatureAnimator.gameObject, out attackerMinY);
        bool victimBounds = BundyVictimPlayableController.TryGetActiveVisibleBoundsMinY(victim, this, out victimMinY);

        if (attackerBounds)
        {
            minY = Mathf.Min(minY, attackerMinY);
            foundMin = true;
        }

        if (victimBounds)
        {
            minY = Mathf.Min(minY, victimMinY);
            foundMin = true;
        }

        if (!foundMin || !IsFinite(minY))
        {
            ApplyPairedPresentationLift(victim, _pairedPresentationLiftY);
            return
                $"presentationLift={_pairedPresentationLiftY:F3}, groundY={groundY:F3}, " +
                $"minY=<none>, attackerBounds={attackerBounds}, victimBounds={victimBounds}";
        }

        float requiredLift = groundY - minY;
        bool changed = false;
        if (requiredLift > 0.005f)
        {
            float nextLift = Mathf.Clamp(_pairedPresentationLiftY + requiredLift, 0f, PairedPresentationMaxLift);
            if (nextLift > _pairedPresentationLiftY + 0.005f)
            {
                _pairedPresentationLiftY = nextLift;
                changed = true;
            }
        }

        ApplyPairedPresentationLift(victim, _pairedPresentationLiftY);

        if ((changed || Time.time >= _nextPairedPresentationLogAt) && _pairedPresentationLiftY > 0.005f)
        {
            _nextPairedPresentationLogAt = Time.time + PairedPresentationLogInterval;
            LogDiagnostic(
                $"Paired presentation grounding: lift={_pairedPresentationLiftY:F3}, groundY={groundY:F3}, " +
                $"minY={minY:F3}, attackerMinY={(attackerBounds ? attackerMinY.ToString("F3") : "<none>")}, " +
                $"victimMinY={(victimBounds ? victimMinY.ToString("F3") : "<none>")}, changed={changed}.");
        }

        return
            $"presentationLift={_pairedPresentationLiftY:F3}, groundY={groundY:F3}, minY={minY:F3}, " +
            $"attackerMinY={(attackerBounds ? attackerMinY.ToString("F3") : "<none>")}, " +
            $"victimMinY={(victimBounds ? victimMinY.ToString("F3") : "<none>")}";
    }

    private string DisablePairedPresentationLift(PlayerControllerB victim, string reason)
    {
        _pairedPresentationLiftY = 0f;
        ApplyPairedPresentationLift(victim, 0f);
        return $"presentationLift=disabled-{reason}";
    }

    private void ApplyPairedPresentationLift(PlayerControllerB victim, float liftY)
    {
        if (creatureAnimator != null && creatureAnimator.transform != transform)
        {
            if (!_pairedVisualLocalPositionSaved)
            {
                _pairedVisualSavedLocalPosition = creatureAnimator.transform.localPosition;
                _pairedVisualLocalPositionSaved = true;
            }

            Vector3 local = _pairedVisualSavedLocalPosition;
            local.y += Mathf.Max(0f, liftY);
            creatureAnimator.transform.localPosition = local;
        }

        BundyVictimPlayableController.ApplyActivePresentationLift(victim, this, liftY);
    }

    private void RestorePairedPresentationLift()
    {
        if (_pairedVisualLocalPositionSaved && creatureAnimator != null && creatureAnimator.transform != transform)
            creatureAnimator.transform.localPosition = _pairedVisualSavedLocalPosition;

        _pairedVisualLocalPositionSaved = false;
        _pairedVisualSavedLocalPosition = Vector3.zero;
        _pairedPresentationLiftY = 0f;
        _nextPairedPresentationLogAt = 0f;
    }

    private static bool TryGetSkinnedRendererMinY(GameObject root, out float minY)
    {
        minY = 0f;
        if (root == null)
            return false;

        bool found = false;
        float best = float.PositiveInfinity;
        foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer == null ||
                renderer.sharedMesh == null ||
                !renderer.enabled ||
                !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            best = Mathf.Min(best, renderer.bounds.min.y);
            found = true;
        }

        if (!found || !IsFinite(best))
            return false;

        minY = best;
        return true;
    }

    private void LogPairedSyncDebug(
        string actionId,
        bool pelvisPin,
        float elapsed,
        float sequenceLength,
        Vector3 attackerAnchorPosition,
        Vector3 victimSourceAnchor,
        Vector3 victimPlayerAnchor,
        Vector3 victimDesiredAnchor,
        Vector3 anchorDelta,
        Vector3 desiredAttackerAnchor,
        Vector3 rawCorrection,
        Vector3 appliedCorrection,
        string outcome,
        string victimDebugState)
    {
        if (!SyncDebugLogs)
            return;

        if (Time.time < _nextPairedSyncDebugAt)
            return;

        _nextPairedSyncDebugAt = Time.time + 0.25f;
        _pairedSyncDebugTick++;
        float remaining = Vector3.Distance(desiredAttackerAnchor, attackerAnchorPosition);
        LogDiagnostic(
            $"BundySyncDebug root {outcome}: tick={_pairedSyncDebugTick}, frame={Time.frameCount}, " +
            $"action='{actionId}', mode={(pelvisPin ? "pelvis-pin" : "metadata-delta")}, " +
            $"elapsed={elapsed:F3}/{sequenceLength:F3}, bundyRoot={FormatVector(transform.position)}, " +
            $"lockedRoot={FormatVector(_pairedActionRootPosition)}, attackerAnchor={FormatVector(attackerAnchorPosition)}, " +
            $"victimSourceAnchor={FormatVector(victimSourceAnchor)}, victimPlayerAnchor={FormatVector(victimPlayerAnchor)}, " +
            $"victimDesiredAnchor={FormatVector(victimDesiredAnchor)}, anchorDelta={FormatVector(anchorDelta)}, " +
            $"desiredAttackerAnchor={FormatVector(desiredAttackerAnchor)}, rawCorrection={FormatVector(rawCorrection)}, " +
            $"appliedCorrection={FormatVector(appliedCorrection)}, remaining={remaining:F3}m, {victimDebugState}.");
    }

    private void LogFinisherSyncPhase(string phase, PlayerControllerB victim, string detail)
    {
        if (!SyncDebugLogs)
            return;

        LogDiagnostic(
            $"BundySyncDebug finisher phase={phase}: action='{_activePairedActionId ?? "<none>"}', " +
            $"entry={_activeFinisherEntryIndex}, body={_activeFinisherBodyIndex}, victim={PlayerLabel(victim)}, " +
            $"detail={detail}, pairedVictim={_activeFinisherVictimPaired}, rootLock={_pairedActionRootLockActive}, " +
            $"bundyRoot={FormatVector(transform.position)}, lockedRoot={FormatVector(_pairedActionRootPosition)}, " +
                $"targetRoot={FormatVector(victim != null ? victim.transform.position : Vector3.zero)}.");
    }

    private Vector3 ResolveGroundedPairedRoot(Vector3 position, PlayerControllerB victim)
    {
        Vector3 grounded = position;
        float fallbackY = IsLivingPlayer(victim) ? victim.transform.position.y : position.y;

        if (TryResolveGroundY(position, victim, fallbackY, out float groundY))
            grounded.y = groundY;
        else
            grounded.y = fallbackY;

        return grounded;
    }

    private bool TryResolveGroundY(Vector3 position, PlayerControllerB victim, float referenceY, out float groundY)
    {
        bool found = false;
        float bestY = float.NegativeInfinity;

        if (NavMesh.SamplePosition(position, out NavMeshHit navHit, 3f, NavMesh.AllAreas))
        {
            bestY = navHit.position.y;
            found = true;
        }

        if (TryResolvePhysicsGroundY(position, victim, referenceY, out float physicsY))
        {
            bestY = found ? Mathf.Max(bestY, physicsY) : physicsY;
            found = true;
        }

        if (IsFinite(referenceY))
        {
            bestY = found ? Mathf.Max(bestY, referenceY) : referenceY;
            found = true;
        }

        groundY = found ? bestY : referenceY;
        return found && IsFinite(groundY);
    }

    private bool TryResolvePhysicsGroundY(Vector3 position, PlayerControllerB victim, float referenceY, out float groundY)
    {
        groundY = 0f;
        Vector3 origin = new Vector3(position.x, position.y + PairedGroundProbeHeight, position.z);
        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            Vector3.down,
            PairedGroundProbeDistance,
            ~0,
            QueryTriggerInteraction.Ignore);
        if (hits == null || hits.Length == 0)
            return false;

        bool found = false;
        float bestY = float.NegativeInfinity;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider == null || IsIgnoredPairedGroundHit(hit.collider.transform, victim))
                continue;

            if (Mathf.Abs(hit.point.y - referenceY) > 2.25f)
                continue;

            if (!IsFinite(hit.point.y))
                continue;

            bestY = found ? Mathf.Max(bestY, hit.point.y) : hit.point.y;
            found = true;
        }

        groundY = bestY;
        return found;
    }

    private bool IsIgnoredPairedGroundHit(Transform hitTransform, PlayerControllerB victim)
    {
        if (hitTransform == null)
            return true;

        if (hitTransform.IsChildOf(transform))
            return true;

        return victim != null && hitTransform.IsChildOf(victim.transform);
    }

    private void SnapRootToPairedFinisherVictim(PlayerControllerB victim)
    {
        if (!IsLivingPlayer(victim))
            return;

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

        Vector3 before = transform.position;
        Vector3 desired = victim.transform.position - forward * GetPairedFinisherRootBackoff();
        desired = ResolveGroundedPairedRoot(desired, victim);

        Vector3 delta = desired - before;
        if (!IsFinite(delta))
            return;

        float maxDistance = Mathf.Max(0.1f, GetPairedFinisherMaxRootSnapDistance());
        if (delta.sqrMagnitude > maxDistance * maxDistance)
            desired = before + Vector3.ClampMagnitude(delta, maxDistance);

        Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);
        bool warped = false;
        if (agent != null && agent.enabled && agent.isOnNavMesh)
        {
            warped = agent.Warp(desired);
            if (warped)
                agent.nextPosition = desired;
        }

        if (!warped)
            transform.position = desired;

        transform.rotation = rotation;

        LogInfo(
            $"Paired finisher root snap: victim={PlayerLabel(victim)}, " +
            $"from={FormatVector(before)}, to={FormatVector(transform.position)}, " +
            $"victimPos={FormatVector(victim.transform.position)}, warped={warped}.");
    }

    private void BeginPairedActionRootLock(Vector3 position, Quaternion rotation)
    {
        if (_actionKind == ActionKind.Finisher)
            position = ResolveGroundedPairedRoot(position, _activeFinisherVictim ?? _grabbedTarget ?? _actionTarget);

        _pairedActionRootLockActive = true;
        _pairedActionRootPosition = position;
        _pairedActionRootRotation = rotation;
        _pairedSyncDebugTick = 0;
        _nextPairedSyncDebugAt = 0f;
        _pairedPresentationLiftY = 0f;
        _nextPairedPresentationLogAt = 0f;
        if (SyncDebugLogs)
        {
            LogDiagnostic(
                $"BundySyncDebug root lock begin: action='{_activePairedActionId ?? "<pending>"}', " +
                $"root={FormatVector(position)}, yaw={rotation.eulerAngles.y:F1}.");
        }
        MaintainPairedActionRootLock();
    }

    private void MaintainPairedActionRootLock()
    {
        if (!_pairedActionRootLockActive)
            return;

        moveTowardsDestination = false;
        movingTowardsTargetPlayer = false;
        SetLocalBehaviourState(BehaviourStateIdle);

        if (creatureAnimator != null)
            creatureAnimator.applyRootMotion = false;

        transform.SetPositionAndRotation(_pairedActionRootPosition, _pairedActionRootRotation);

        if (agent == null || !agent.enabled)
            return;

        agent.updatePosition = false;
        agent.updateRotation = false;
        if (agent.isOnNavMesh)
        {
            agent.isStopped = true;
            agent.nextPosition = _pairedActionRootPosition;
        }
    }

    private void ClearPairedActionRootLock()
    {
        if (_pairedActionRootLockActive && SyncDebugLogs)
        {
            LogDiagnostic(
                $"BundySyncDebug root lock clear: action='{_activePairedActionId ?? "<none>"}', " +
                $"root={FormatVector(transform.position)}, lockedRoot={FormatVector(_pairedActionRootPosition)}, " +
                $"ticks={_pairedSyncDebugTick}.");
        }
        _pairedActionRootLockActive = false;
        _pairedActionRootPosition = Vector3.zero;
        _pairedActionRootRotation = Quaternion.identity;
        _pairedSyncDebugTick = 0;
        _nextPairedSyncDebugAt = 0f;
        RestorePairedPresentationLift();

        if (creatureAnimator != null)
            creatureAnimator.applyRootMotion = false;

        if (agent == null || !agent.enabled)
            return;

        agent.updatePosition = true;
        agent.updateRotation = true;
        agent.stoppingDistance = StopDistance;
        if (!agent.isOnNavMesh)
            return;

        if ((agent.nextPosition - transform.position).sqrMagnitude > 0.25f)
            agent.Warp(transform.position);
        else
            agent.nextPosition = transform.position;

        agent.ResetPath();
        agent.isStopped = false;
        if (SyncDebugLogs)
        {
            LogDiagnostic(
                $"BundySyncDebug root lock agent restored: isStopped={agent.isStopped}, " +
                $"updatePosition={agent.updatePosition}, updateRotation={agent.updateRotation}, " +
                $"nextPosition={FormatVector(agent.nextPosition)}.");
        }
    }

    private void UpdateManualTargetFacing()
    {
        // #188: the knockdown pin is a root lock like the paired one. Without this the stagger branch's
        // "action locked" facing kept turning a man lying on the floor toward the player, which both
        // fought the pin's rotation and looked like the body swivelling.
        if (!UsesManualTargetFacing || _pairedActionRootLockActive || _knockdownRootPinActive)
            return;

        if (IsTurnClipRootLockActive())
            return;

        // Committed basic-attack lunge: keep the aim fixed to where the player was at lunge start
        // (no re-tracking), so a dodge can make it miss.
        if (LungeAimActive)
        {
            MaintainCommittedLungeFacing();
            return;
        }

        // HYBRID PROOF: suppress manual rotation while a turn clip is active
        // so the authored turn animation is visible instead of being overridden
        // frame-by-frame by FaceActionTarget().
        if (IsAnimatorInTurnState())
        {
            if (ShouldLog(BundyLogCategory.Anim, 2) && !_loggedTurnSuppressThisFrame)
            {
                _loggedTurnSuppressThisFrame = true;
                string stateName = GetCurrentAnimatorStateName();
                Debug.Log(
                    $"[{VariantName}] MANUAL_FACING_SUPPRESSED: turnStateActive, animState={stateName}", this);
            }
            return;
        }
        _loggedTurnSuppressThisFrame = false;

        PlayerControllerB target = IsActionLocked()
            ? ResolveLockedActionTarget(BasicAttackRange + 2f)
            : GetCurrentHuntTarget();
        if (!IsLivingPlayer(target))
            return;
        if (!IsActionLocked() && !CanUseLiveHuntTarget(target))
        {
            if (agent != null && agent.enabled && !agent.updateRotation)
                agent.updateRotation = true;
            return;
        }

        float maxDistance = IsActionLocked()
            ? BasicAttackRange + 3f
            : ManualPursuitFacingDistance;
        if (!IsActionLocked() && ShouldHoldFacingForDirectionalLocomotion())
            maxDistance = Mathf.Max(maxDistance, GetBasicAttackStartRange(BasicAttackRange) * 1.8f);
        float distance = Vector3.Distance(transform.position, target.transform.position);
        if (!IsActionLocked() &&
            !ManualPursuitFacingWhileMoving &&
            IsLocomotionIntended() &&
            distance > Mathf.Max(BasicAttackRange + 0.35f, AttackLungeStopDistance + 0.75f))
        {
            if (agent != null && agent.enabled && !agent.updateRotation)
                agent.updateRotation = true;
            return;
        }

        if (distance > maxDistance)
        {
            if (agent != null && agent.enabled && !agent.updateRotation)
                agent.updateRotation = true;
            return;
        }

        if (agent != null && agent.enabled && agent.updateRotation)
            agent.updateRotation = false;

        float turnSpeed = IsActionLocked() ? ManualActionTurnSpeedDegrees : ManualPursuitTurnSpeedDegrees;
        float yawBefore = Vector3.SignedAngle(transform.forward, target.transform.position - transform.position, Vector3.up);
        if (!IsActionLocked() && TurnClipsEnabled && TurnClipsCanInterruptMovement &&
            Mathf.Abs(yawBefore) >= TurnClipMinYawDegrees)
        {
            TryPlayPursuitTurnClip(target, distance, allowMovementDirectionFallback: false);
            if (IsTurnClipRootLockActive())
                return;
        }

        FaceActionTarget(target, turnSpeed);
        float yawAfter = Vector3.SignedAngle(transform.forward, target.transform.position - transform.position, Vector3.up);
        if (ShouldLog(BundyLogCategory.Anim, 2) && Mathf.Abs(yawBefore) > 5f)
        {
            string stateName = GetCurrentAnimatorStateName();
            Debug.Log(
                $"[{VariantName}] MANUAL_FACING: yawBefore={yawBefore:F1}, yawAfter={yawAfter:F1}, " +
                $"corrected={Mathf.Abs(yawBefore) - Mathf.Abs(yawAfter):F1}, turnSpeed={turnSpeed:F0}, " +
                $"animState={stateName}, actionLocked={IsActionLocked()}", this);
        }
    }

    private void MaintainCommittedLungeFacing()
    {
        if (agent != null && agent.enabled && agent.updateRotation)
            agent.updateRotation = false;

        Vector3 direction = GetCommittedAttackDirection();
        float yawBefore = Vector3.SignedAngle(transform.forward, direction, Vector3.up);
        FaceActionDirection(direction, ManualActionTurnSpeedDegrees);
        float yawAfter = Vector3.SignedAngle(transform.forward, direction, Vector3.up);

        if (DebugLogs && Time.time >= _nextLungeFacingLogAt)
        {
            _nextLungeFacingLogAt = Time.time + 0.25f;
            LogDiagnostic(
                $"BundyLungeFacing committedDirection={FormatVector(direction)}, yawBefore={yawBefore:F1}, " +
                $"yawAfter={yawAfter:F1}, agentUpdateRotation={(agent != null && agent.enabled ? agent.updateRotation.ToString() : "<n/a>")}, " +
                $"activeWindow=({_attackActiveWindowStartAt - Time.time:F2},{_attackActiveWindowEndAt - Time.time:F2}), " +
                $"attackIndex={_activeAttackIndex}");
        }
    }

    private void CommitLungeAim(Vector3 targetPoint)
    {
        _lungeTargetPoint = targetPoint;
        Vector3 direction = targetPoint - transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f)
        {
            direction = transform.forward;
            direction.y = 0f;
        }
        if (direction.sqrMagnitude < 0.001f)
            direction = Vector3.forward;

        _lungeAttackDirection = direction.normalized;
        _hasLungeAttackDirection = true;
        FaceActionDirection(_lungeAttackDirection, LungeCommitFacingTurnSpeedDegrees);
    }

    private void ClearLungeAim()
    {
        _lungeTargetPoint = Vector3.zero;
        _lungeAttackDirection = Vector3.zero;
        _hasLungeAttackDirection = false;
        _nextLungeFacingLogAt = 0f;
        _nextLungeDriveLogAt = 0f;
    }

    // Job 2a: re-aim a committed basic-attack lunge at the live player until AttackLungeCommitFraction
    // of the swing has elapsed, then leave it frozen for the remainder. Preserves "the lunge can miss"
    // — there is still a commit window to dodge out of — while stopping him from driving at a point the
    // player has already left.
    private void MaintainLungeAimTracking(PlayerControllerB target)
    {
        if (_actionKind != ActionKind.Attack || !IsLivingPlayer(target) || IsLungeAimFrozen())
            return;

        Vector3 direction = target.transform.position - transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f)
            return;

        _lungeTargetPoint = target.transform.position;
        // Update the committed direction WITHOUT the commit-frame snap: MaintainCommittedLungeFacing
        // already turns toward GetCommittedAttackDirection() at ManualActionTurnSpeedDegrees each frame,
        // so re-snapping here would undo the R39 A2 rate limit on every tracking frame.
        _lungeAttackDirection = direction.normalized;
        _hasLungeAttackDirection = true;
    }

    // The lunge aim freezes once AttackLungeCommitFraction of the swing's impact delay has elapsed.
    // A finisher run-in has no impact delay to scale, so it counts as frozen from the start (its aim is
    // committed at BeginFinisherLunge and MaintainFinisherLunge owns its contact test).
    private bool IsLungeAimFrozen()
    {
        if (_finisherLungeActive || _activeAttackIndex < 0)
            return true;

        // #281: a crossfaded chain rep enters its clip partway in, so its contact lands earlier than
        // the authored delay. Freezing on the authored number would never freeze before the rep's
        // real impact, and the dodge-window telegraph would be gone.
        float impactDelay = EffectiveBasicAttackImpactDelay;
        if (impactDelay <= 0f)
            return true;

        return Time.time - _actionStartedAt >= impactDelay * AttackLungeCommitFraction;
    }

    // Job 2a instrumentation: the one line that makes the lunge fix verifiable in a playtest log.
    // Answers, per lunge: what standoff was used, is the aim still tracking or frozen, how far the
    // destination sits from the live player, and how the blade-capsule reach compares to the contact
    // sphere the standoff is derived from (item 5 — the two are compared here before anyone changes
    // the damage path).
    private void LogLungeDriveDiagnostic(
        PlayerControllerB target,
        Vector3 destination,
        float bladeStandoff,
        float aimDistance,
        string outcome)
    {
        if (!ShouldLog(BundyLogCategory.Combat, 1) || Time.time < _nextLungeDriveLogAt)
            return;

        _nextLungeDriveLogAt = Time.time + 0.15f;

        float liveDistance = IsLivingPlayer(target)
            ? Vector3.Distance(transform.position, target.transform.position)
            : -1f;
        // Effective, not authored: on a chain rep this is the shifted delay everything else is
        // timed to, so the log and the behaviour cannot disagree.
        float impactDelay = EffectiveBasicAttackImpactDelay;
        float bladeCapsuleReach = _chainsawBladeHitbox != null
            ? Mathf.Max(0.01f, _chainsawBladeHitbox.radius + _chainsawBladeHitbox.padding)
            : -1f;

        LogDiagnostic(
            BundyLogCategory.Combat,
            1,
            $"BundyLungeDrive outcome={outcome}, attackIndex={_activeAttackIndex}, aimFrozen={IsLungeAimFrozen()}, " +
            $"elapsed={Time.time - _actionStartedAt:F2}s, impactDelay={impactDelay:F2}s, " +
            $"commitFraction={AttackLungeCommitFraction:F2}, windupHold={EffectiveAttackLungeWindupHoldSeconds:F2}s, " +
            $"standoff={bladeStandoff:F2}m, " +
            $"aimDistance={aimDistance:F2}m, liveDistance={liveDistance:F2}m, " +
            $"destination={FormatVector(destination)}, aimPoint={FormatVector(_lungeTargetPoint)}, " +
            $"contactSphereReach={BasicAttackContactForwardOffset + BasicAttackContactRadius:F2}m, " +
            $"bladeCapsuleRadius={bladeCapsuleReach:F2}m, agentSpeed={(agent != null && agent.enabled ? agent.speed : -1f):F2}, " +
            // R1 readout: whichever of these two is lower is what governed the last drive frame.
            // timed < stoppable means the swing clock is in charge (the intended state). stoppable <
            // timed means he is braking to make the standoff, which is the case run 5 could not reach.
            $"timedSpeed={_lastLungeTimedSpeed:F2}, stoppableSpeed={_lastLungeStoppableSpeed:F2}");
    }

    private bool ShouldHoldFacingForDirectionalLocomotion()
    {
        if (!UsesDirectionalLocomotion || !UsesCombatManeuvers)
            return false;

        switch (_currentCombatBehavior)
        {
            case CombatBehavior.CircleLeft:
            case CombatBehavior.CircleRight:
            case CombatBehavior.Backstep:
            case CombatBehavior.Reorient:
                return true;
            default:
                return false;
        }
    }

    private void FaceActionTarget(PlayerControllerB target)
    {
        FaceActionTarget(target, ActionTurnSpeedDegrees);
    }

    private void FaceActionPoint(Vector3 point)
    {
        Vector3 direction = point - transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.0001f)
        {
            transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            if (DebugLogs)
                NoteFacingWriter("FaceActionPoint(instant)");
        }
    }

    private void FaceActionDirection(Vector3 direction, float turnSpeedDegrees)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
            return;

        Quaternion desired = Quaternion.LookRotation(direction.normalized, Vector3.up);
        float maxDegrees = float.IsPositiveInfinity(turnSpeedDegrees)
            ? 360f
            : Mathf.Max(0f, turnSpeedDegrees) * Time.deltaTime;
        transform.rotation = Quaternion.RotateTowards(transform.rotation, desired, maxDegrees);
        if (DebugLogs)
        {
            NoteFacingWriter(float.IsPositiveInfinity(turnSpeedDegrees)
                ? "FaceActionDirection(+inf)"
                : $"FaceActionDirection({turnSpeedDegrees:F0})");
        }
    }

    private void FaceActionTarget(PlayerControllerB target, float turnSpeedDegrees)
    {
        if (!IsLivingPlayer(target))
            return;

        Vector3 direction = target.transform.position - transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f)
            return;

        Quaternion desired = Quaternion.LookRotation(direction.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            desired,
            Mathf.Max(0f, turnSpeedDegrees) * Time.deltaTime);
        if (DebugLogs)
            NoteFacingWriter($"FaceActionTarget({turnSpeedDegrees:F0})");
    }

    private void DriveActionApproach(PlayerControllerB target)
    {
        if (!AgentReadyForMovement() || !IsLivingPlayer(target))
            return;

        // Job 2a: a committed basic-attack lunge re-aims at the live player until AttackLungeCommitFraction
        // of the swing has elapsed, then freezes. A dodge inside the commit window still makes the lunge
        // miss — what it no longer does is drive at a point the player left a second ago, which parked
        // him short when they ran away and drove him through their body when they ran past.
        bool basicLunge = _lungeActive && !_finisherLungeActive;
        if (basicLunge)
            MaintainLungeAimTracking(target);

        // Stage 2: a lunging basic attack closes the gap fast and stops tighter; otherwise the normal
        // slow action approach. _lungeActive is only set for non-grab/non-paired basic attacks, so
        // finishers and grabs keep the original behaviour.
        Vector3 approachPoint = LungeAimActive ? _lungeTargetPoint : target.transform.position;
        if (!_lungeActive && TryGetSmartDestination(approachPoint, out Vector3 smartApproach))
            approachPoint = smartApproach;
        else
            approachPoint = ResolveObstacleAwareDestination(approachPoint, target, allowDetour: !_lungeActive);
        float desiredStopDistance = _lungeActive
            ? Mathf.Min(StopDistance, AttackLungeStopDistance)
            : Mathf.Min(StopDistance, ActionApproachStopDistance);

        if (basicLunge)
        {
            // Job 2a: stop where the BLADE reaches, not where the body collides. The standoff is baked
            // into the destination and NOWHERE else — the arrival test and agent.stoppingDistance drop
            // to a tolerance hair, so the agent decelerates onto a point it can actually stop on and
            // overshoot is bounded by normal deceleration instead of by a radius test at 7-11 m/s.
            float bladeStandoff = Mathf.Max(0f, AttackLungeBladeStandoff);
            float aimDistance = Vector3.Distance(transform.position, approachPoint);

            // Job 2b: plant and wind up before the lunge is allowed to drive. The swing already carries
            // a 1.17-1.55s lead before the blade lands, but surging on the first frame spends that lead
            // invisibly and the attack reads as instant. Holding the front of it is the telegraph.
            //
            // This costs the close nothing, because the timed close below measures timeToContact from
            // _actionStartedAt: seconds spent holding shrink the time remaining, so the speed it asks
            // for rises to compensate, and R1's stoppable cap keeps that speed physically brakeable.
            // Aim tracking has already run this frame, so the hold tracks a moving player instead of
            // freezing on a stale point — the dodge window is unchanged for a player who moves, and
            // only a player who stands still gains anything.
            // #281: rescaled for a crossfaded chain rep, whose pre-contact budget is shorter by the
            // entry offset — an unscaled hold would spend most of it standing still.
            float windupHold = Mathf.Max(0f, EffectiveAttackLungeWindupHoldSeconds);
            if (windupHold > 0f && Time.time - _actionStartedAt < windupHold)
            {
                LogLungeDriveDiagnostic(target, approachPoint, bladeStandoff, aimDistance, "hold:windup");
                HoldAgentWithoutReset();
                return;
            }

            if (aimDistance <= bladeStandoff + LungeArrivalToleranceMeters)
            {
                // Arrived in blade reach. This is the run-4 failure frame — logged so a future run can
                // tell "arrived and waiting for the contact frame" apart from "parked at a stale point".
                LogLungeDriveDiagnostic(target, approachPoint, bladeStandoff, aimDistance, "hold:in-reach");
                HoldAgentWithoutReset();
                return;
            }

            // Backstop for the player who walks INTO him: if the live player is already inside blade
            // reach, stop regardless of where the frozen aim point sits. Armed only after the aim has
            // committed — before that the aim IS the live player, so this would fight the tracking and
            // cancel the surge on every approach.
            if (IsLungeAimFrozen() &&
                Vector3.Distance(transform.position, target.transform.position) <= bladeStandoff)
            {
                LogLungeDriveDiagnostic(target, approachPoint, bladeStandoff, aimDistance, "hold:live-brake");
                HoldAgentWithoutReset();
                return;
            }

            Vector3 approachDirection = approachPoint - transform.position;
            approachDirection.y = 0f;
            if (approachDirection.sqrMagnitude > 0.0001f)
                approachPoint -= approachDirection.normalized * bladeStandoff;
            desiredStopDistance = Mathf.Max(0.01f, LungeArrivalToleranceMeters);
            LogLungeDriveDiagnostic(target, approachPoint, bladeStandoff, aimDistance, IsLungeAimFrozen() ? "drive:committed" : "drive:tracking");
        }

        float distance = Vector3.Distance(transform.position, approachPoint);
        if (distance <= desiredStopDistance + 0.15f)
        {
            HoldAgentWithoutReset();
            return;
        }

        if (_finisherLungeActive)
        {
            // Finisher run-in: fixed fast charge (no swing to time the arrival to).
            agent.speed = AttackLungeSpeed;
        }
        else if (_lungeActive)
        {
            // Timed lunge, biased FAST: close the gap so the surge is hard to dodge and the blade
            // connects on the contact frame. Capped at the lunge speed, floored well above run speed so
            // it never crawls.
            // Job 2a: arrival is timed to AttackLungeCommitFraction of the impact delay, not the old
            // hard-coded 0.5. At 0.5 he finished moving ~0.6s before the blade landed and stood still
            // through the rest of the swing (run 4: motion ended t=0.50s, contact at t=1.17s). This is
            // the same constant the aim freeze uses, so surge, commit and blade share one beat.
            // #281: effective, not authored. A crossfaded chain rep's blade lands entryOffset
            // sooner, and timing the arrival to the authored contact would land the surge after the
            // rep's active window has already closed.
            float contactTime = EffectiveBasicAttackImpactDelay * AttackLungeCommitFraction;
            float timeToContact = Mathf.Max(0.05f, contactTime - (Time.time - _actionStartedAt));
            float remainingGap = Mathf.Max(0f, distance - desiredStopDistance);

            // R1 (run 5): the flat RunSpeed * 1.5 floor this used to carry is more speed than a short
            // lunge can absorb, so the standoff was never physically reachable. For the chainsaw the
            // floor was 4.25 * 1.5 = 6.375 m/s against a braking rate of AttackLungeSpeed * 4 = 44
            // m/s^2, giving a minimum stopping distance of v^2/2a = 0.46 m. Run 5 recorded him holding
            // at 1.00-1.20 m inside a 1.50 m standoff over 18 samples — that 0.3-0.5 m is the 0.46 m,
            // not a bug in the standoff logic.
            //
            // Cap BOTH ends of the clamp by the speed that can still be bled off inside remainingGap.
            // The ceiling turns the last ~1.4 m into a real braking ramp; the floor stops the "never
            // crawl" guarantee from overriding physics on a close-range lunge. This is also what makes
            // the rest of Job 2a run at all: run 5 logged drive:committed zero times because he always
            // arrived at ~0.21 s, far short of the 0.85 * 1.17 = 0.99 s commit point, which left the
            // late-commit tracking and the arrival retiming inert.
            float lungeDeceleration = Mathf.Max(agent.acceleration, AttackLungeSpeed * 4f);
            float stoppableSpeed = Mathf.Sqrt(2f * lungeDeceleration * remainingGap);
            float timedSpeed = remainingGap / timeToContact;
            float speedCeiling = Mathf.Min(AttackLungeSpeed, stoppableSpeed);
            float speedFloor = Mathf.Min(RunSpeed * 1.5f, speedCeiling);
            agent.speed = Mathf.Clamp(timedSpeed, speedFloor, speedCeiling);

            // Recorded for the BundyLungeDrive line so the next run can falsify R1 directly: if
            // stoppable is what is binding, applied tracks it and he stops on the standoff; if timed is
            // binding, the close is being governed by the swing clock as designed.
            _lastLungeTimedSpeed = timedSpeed;
            _lastLungeStoppableSpeed = stoppableSpeed;
        }
        else
        {
            agent.speed = Mathf.Max(1f, WalkSpeed * ActionApproachSpeedMultiplier);
        }
        agent.angularSpeed = AgentAngularSpeedDegrees;
        agent.acceleration = _lungeActive
            ? Mathf.Max(agent.acceleration, AttackLungeSpeed * 4f)
            : Mathf.Max(agent.acceleration, AgentAcceleration);
        if (_lungeActive)
            agent.updateRotation = false;
        agent.stoppingDistance = desiredStopDistance;
        agent.isStopped = false;

        movingTowardsTargetPlayer = false;
        moveTowardsDestination = true;
        // Action layer, not pursuit: the lunge/approach drive intentionally overrides navigation
        // while an attack is running (movement authority, plan step 8, scope boundary).
        SetDestinationToPosition(approachPoint);
        HoldLocomotionIntent(_lungeActive || _finisherLungeActive ? 0.35f : 0.2f);
        SetLocalBehaviourState(BehaviourStateTracking);
    }

}
