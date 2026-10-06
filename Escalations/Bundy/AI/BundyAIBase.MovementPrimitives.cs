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
    private void ClampMovementAgainstWorldGeometry()
    {
        // #188: the knockdown pin is a deliberate root hold, same as the paired-action lock — the clamp
        // would read the pin as movement into geometry and warp him off it.
        if (!IsOwner || isEnemyDead || _pairedActionRootLockActive || _knockdownRootPinActive)
        {
            ResetMovementWallClampTracker();
            return;
        }

        // #306: an off-mesh link traversal is agent-authored motion that legitimately leaves the mesh and
        // frequently clips geometry. The clamp has no say over it.
        if (agent != null && agent.enabled && agent.isOnNavMesh && agent.isOnOffMeshLink)
        {
            ResetMovementWallClampTracker();
            return;
        }

        Vector3 current = transform.position;
        if (!_hasLastMovementWallClampPosition)
        {
            _lastMovementWallClampPosition = current;
            _hasLastMovementWallClampPosition = true;
            return;
        }

        Vector3 previous = _lastMovementWallClampPosition;
        Vector3 totalDelta = current - previous;
        if (totalDelta.sqrMagnitude > MovementWallClampMaxStep * MovementWallClampMaxStep)
        {
            _lastMovementWallClampPosition = current;
            return;
        }

        Vector3 planarDelta = totalDelta;
        planarDelta.y = 0f;
        float distance = planarDelta.magnitude;
        if (distance < MovementWallClampMinDistance)
        {
            _lastMovementWallClampPosition = current;
            return;
        }

        Vector3 direction = planarDelta / distance;
        Vector3 origin = previous + Vector3.up * MovementWallClampProbeHeight;
        float radius = agent != null
            ? Mathf.Clamp(agent.radius * 0.9f, 0.24f, 0.42f)
            : 0.32f;

        if (!Physics.SphereCast(
                origin,
                radius,
                direction,
                out RaycastHit hit,
                distance + MovementWallClampPadding,
                GetWorldCollisionMask(),
                QueryTriggerInteraction.Ignore) ||
            !IsBundyMovementBlockingSurface(hit))
        {
            _lastMovementWallClampPosition = current;
            return;
        }

        bool openedDoor = _doorOpener != null && _doorOpener.ForceOpenDoorForCollider(hit.collider);
        Vector3 hitNormal = hit.normal.sqrMagnitude > 0.001f ? hit.normal.normalized : -direction;
        _lastMovementClampHitNormal = hitNormal;
        _lastMovementClampHitAt = Time.time;

        Vector3 contactPosition = previous + direction * Mathf.Max(0f, hit.distance - MovementWallClampPadding);

        // #271: a closed door leaf is ~0.1m thick and does not carve the NavMesh while unlocked, so the
        // slide-and-resample below sees near-mesh on BOTH sides and phased him through in one frame.
        // Hold on the near side while the opener works instead. Doors ONLY — the hold must never come
        // back for general geometry (#306). Locked doors skip the hold entirely (policy pending, #278),
        // and the hold window caps at MovementWallClampDoorHoldMaxSeconds so a door that will not open
        // falls through to the normal slide.
        DoorLock blockingDoor = hit.collider.GetComponentInParent<DoorLock>();
        if (blockingDoor != null && !blockingDoor.isLocked && !BundyDoorOpener.DoorIsOpen(blockingDoor))
        {
            int doorId = blockingDoor.GetInstanceID();
            if (_movementClampDoorHoldDoorId != doorId ||
                Time.time - _movementClampDoorHoldStartedAt > MovementWallClampDoorHoldRearmSeconds)
            {
                _movementClampDoorHoldDoorId = doorId;
                _movementClampDoorHoldStartedAt = Time.time;
            }

            if (Time.time - _movementClampDoorHoldStartedAt <= MovementWallClampDoorHoldMaxSeconds)
            {
                Vector3 holdPosition = contactPosition;
                holdPosition.y = current.y;
                if (agent != null && agent.enabled && agent.isOnNavMesh)
                {
                    try
                    {
                        agent.nextPosition = holdPosition;
                    }
                    catch (Exception e)
                    {
                        LogWarning($"Movement wall clamp door hold skipped: {e.Message}");
                    }
                }

                transform.position = holdPosition;
                _lastMovementWallClampPosition = holdPosition;
                PauseForSmartInteraction(MovementWallClampDoorHoldPauseSeconds);

                if (Time.time >= _nextMovementWallClampLogAt &&
                    ShouldLog(BundyLogCategory.Movement, BundyLogGate.DefaultVerbosity))
                {
                    _nextMovementWallClampLogAt = Time.time + MovementWallClampDiagnosticIntervalSeconds;
                    LogDiagnostic(BundyLogCategory.Movement, BundyLogGate.DefaultVerbosity,
                        $"Movement wall clamp door hold: door={ColliderLabel(hit.collider)}, " +
                        $"layer={hit.collider.gameObject.layer}, openedDoor={openedDoor}, " +
                        $"held={FormatVector(holdPosition)}.");
                }
                return;
            }
        }

        // #306: the baked NavMesh legally corner-cuts within ~0.3m of props and corners, so a geometry
        // contact is the NORMAL case, not a fault. Warping back to the contact point, resetting the path
        // and arming a hold turned every one of those into a stop-and-retry loop that re-ran the next
        // frame from the same place — the grinding-on-furniture stall. Slide along the surface instead
        // and KEEP the path: the agent stays on its route and the watchdog keeps its own signal.
        Vector3 slide = Vector3.ProjectOnPlane(planarDelta, hitNormal);
        slide.y = 0f;
        Vector3 resolvedPosition = contactPosition + slide;
        resolvedPosition.y = current.y;

        int navMask = agent != null ? agent.areaMask : NavMesh.AllAreas;
        if (NavMesh.SamplePosition(resolvedPosition, out NavMeshHit navHit, 1f, navMask))
        {
            // #271: a sample on the far side of thin blocking geometry is a teleport, not a slide.
            if (IsNearSideNavSample(contactPosition, navHit.position))
                resolvedPosition = navHit.position;
        }
        else
        {
            resolvedPosition = contactPosition;
            resolvedPosition.y = current.y;
            if (NavMesh.SamplePosition(resolvedPosition, out navHit, 1.25f, navMask) &&
                IsNearSideNavSample(contactPosition, navHit.position))
            {
                resolvedPosition = navHit.position;
            }
        }

        // nextPosition rather than Warp: it re-maps the agent's internal position without discarding the
        // path, which is the whole point of the slide.
        if (agent != null && agent.enabled && agent.isOnNavMesh)
        {
            try
            {
                agent.nextPosition = resolvedPosition;
            }
            catch (Exception e)
            {
                LogWarning($"Movement wall clamp slide skipped: {e.Message}");
            }
        }

        transform.position = resolvedPosition;
        _lastMovementWallClampPosition = resolvedPosition;

        if (CanResolveChainsawWallImpact())
            ResolveChainsawWallImpact(hit.point, hit.normal.sqrMagnitude > 0.001f ? hit.normal : -direction, hit.collider, "movement wall clamp");

        if (Time.time >= _nextMovementWallClampLogAt)
        {
            _nextMovementWallClampLogAt = Time.time + MovementWallClampDiagnosticIntervalSeconds;
            LogDiagnostic(
                $"Movement wall clamp slide: collider={ColliderLabel(hit.collider)}, openedDoor={openedDoor}, " +
                $"from={FormatVector(previous)}, attempted={FormatVector(current)}, slid={FormatVector(resolvedPosition)}.");
        }
    }

    private void ResetMovementWallClampTracker()
    {
        _hasLastMovementWallClampPosition = false;
        _lastMovementWallClampPosition = transform.position;
    }

    private bool IsNearSideNavSample(Vector3 contactPosition, Vector3 samplePosition)
    {
        Vector3 probeOffset = Vector3.up * MovementWallClampProbeHeight;
        return !Physics.Linecast(
            contactPosition + probeOffset,
            samplePosition + probeOffset,
            GetWorldCollisionMask(),
            QueryTriggerInteraction.Ignore);
    }

    private bool IsBundyMovementBlockingSurface(RaycastHit hit)
    {
        if (hit.collider == null)
            return false;
        if (hit.normal.y > 0.62f)
            return false;

        return IsBundyMovementBlockingCollider(hit.collider);
    }

    private bool IsBundyMovementBlockingCollider(Collider collider)
    {
        if (collider == null)
            return false;
        if (collider.isTrigger || IsNonBlockingUtilityCollider(collider))
            return false;

        Transform hitTransform = collider.transform;
        if (hitTransform == null || hitTransform.IsChildOf(transform))
            return false;

        if (collider.GetComponentInParent<PlayerControllerB>() != null ||
            collider.GetComponentInParent<EnemyAI>() != null ||
            collider.GetComponentInParent<GrabbableObject>() != null)
        {
            return false;
        }

        return true;
    }

    private static bool IsNonBlockingUtilityCollider(Collider collider)
    {
        if (collider == null)
            return false;

        Transform current = collider.transform;
        while (current != null)
        {
            string name = current.name ?? string.Empty;
            if (name.IndexOf("ScanNode", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            current = current.parent;
        }

        return false;
    }

    private void StopMovement()
    {
        StopPursuitSearchIfActive();
        moveTowardsDestination = false;
        movingTowardsTargetPlayer = false;
        _locomotionIntentHoldUntil = 0f;
        SetLocalBehaviourState(BehaviourStateIdle);

        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return;

        agent.isStopped = true;
        agent.ResetPath();
    }

    private void HoldAgentWithoutReset()
    {
        moveTowardsDestination = false;
        movingTowardsTargetPlayer = false;

        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return;

        agent.isStopped = true;
    }

    private void RunBaseAIIntervalIfSafe()
    {
        long baseIntervalTimingStarted = BundyRuntimePerformanceDiagnostics.Begin();
        try
        {
            if (agent == null)
                return;

            if (moveTowardsDestination && agent.enabled && !agent.isOnNavMesh)
                return;

            base.DoAIInterval();
        }
        finally
        {
            BundyRuntimePerformanceDiagnostics.End(
                BundyRuntimeStep.BaseAIInterval,
                baseIntervalTimingStarted);
        }
    }

    private void SetLocalBehaviourState(int stateIndex)
    {
        EnsureInstanceBehaviourStates();
        if (stateIndex < 0 || stateIndex >= enemyBehaviourStates.Length)
            return;

        if (currentBehaviourStateIndex == stateIndex && currentBehaviourState != null)
            return;

        previousBehaviourStateIndex = currentBehaviourStateIndex;
        currentBehaviourStateIndex = stateIndex;
        currentBehaviourState = enemyBehaviourStates[stateIndex];
    }

    private void SetMotionStateIfChanged(int motionState)
    {
        if (UsesObservedVelocityIdleDemotion &&
            _observedVelocityIdleDemotionActive &&
            IsDirectionalLocomotionMotionState(motionState))
        {
            _pendingObservedVelocityMotionState = motionState;
            return;
        }

        if (_lastMotionState == motionState)
            return;

        if (creatureAnimator == null)
        {
            SetMotionState(motionState);
            return;
        }

        _lastMotionState = motionState;
        SetMotionState(motionState);
        LogMotionStateChangeDiagnostics(motionState);
        // Job 1: 424 lines in run 5. It is a locomotion-state trace, not a behaviour-state transition —
        // the state machine every remaining diagnosis reads through is BundyPerception, not this.
        LogInfo(BundyLogCategory.Anim, 2, $"MotionState={motionState} ({MotionStateName(motionState)}).");
    }
}
