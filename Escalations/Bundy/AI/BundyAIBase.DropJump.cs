using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.Bundy.AI;

// Job 5a: drop-down follow ("jump"). The narrow half of the traversal work — he steps off a ledge and
// falls after a target who is BELOW him and better reached by dropping than by the walking route (a
// real detour, or no complete path at all). Core pursuit behaviour, not a feature toggle: the old
// enable config was retired, only policy tunables remain. The gap-cross (5b) is deliberately not
// here: this needs one downward raycast, one NavMesh sample below the ledge and a scripted arc, with no
// far-side search and no gap math.
//
// Why it is not an ActionKind: a NavMeshAgent cannot jump, so the traversal window is off-navmesh, and
// the action layer's contract (BeginTimedAction/CompleteAction, the impact/recover fallbacks, the paired
// victim cleanup) has nothing to do with falling. What the action layer DOES own that this must honour is
// the release: the post-action watchdog (Navigation.cs RecoverMovementIfNeeded) fires a loud BUG SIGNAL
// whenever something leaves the agent held. So this runs its own root lock, modelled on the turn-clip
// lock in Locomotion.cs, and every exit path — landing, early ground contact, watchdog timeout, aborted
// sample — funnels through FinishDropJump, whose finally-equivalent is the same unconditional
// UnlockAgentForMovement the action exit uses. _lastActionExitKind/_lastActionExitAt are untouched, so a
// drop can never be misattributed to an action in that warning.
//
// Movement authority (plan step 8): "jump-drop" is a registered pursuit source. The begin marker keeps
// [BundyMoveAuth] continuous across the airborne window (same idea as "action-override:{kind}") and the
// landing issues the real destination through IssueRawAgentDestination.
//
// Networking: the owner drives the arc by writing transform.position directly. inSpecialAnimation is
// deliberately NOT set — vanilla EnemyAI.Update skips the remote SmoothDamp toward serverPosition while
// that flag is up, and the owner skips DoAIInterval (hence SyncPositionToClients) entirely, so setting it
// would freeze remotes at the ledge and snap them on landing. Left clear, the owner keeps ticking
// DoAIInterval (we short-circuit it to RunBaseAIIntervalIfSafe, whose base.DoAIInterval still calls
// SyncPositionToClients) and remotes interpolate through the fall like any other movement. The Falling
// animator bool replicates through the same NetworkAnimator that carries MotionState/Speed/MoveX/MoveY.
public abstract partial class BundyAIBase
{
    // How far in front of him the ledge probe is cast, and how far below that probe still counts as
    // "there is floor here" (i.e. he is not actually standing at an edge). The probe is not a single
    // ray any more: a small fan around the bearing to the target (±FanDegrees) at two distances
    // (near and far), because the player who drops down beside or behind his facing still has to be
    // findable without him first walking the long way to line the ledge up.
    private const float DropJumpLedgeProbeForwardMeters = 1.1f;
    private const float DropJumpLedgeProbeFarMeters = 2.2f;
    private const float DropJumpLedgeProbeFanDegrees = 25f;
    private const float DropJumpLedgeProbeFloorTolerance = 0.6f;
    private const float DropJumpGroundProbeUpOffset = 0.5f;
    private const float DropJumpLandingSampleRadius = 3f;
    private const float DropJumpMinHorizontalMeters = 0.75f;
    private const float DropJumpSweepRadius = 0.35f;
    private const float DropJumpFacingTurnDegreesPerSecond = 540f;
    private const float DropJumpForcedProbeMaxHeightMeters = 40f;
    // The landing must actually be worth the drop: it has to close at least this much of the horizontal
    // distance to the target, otherwise he is throwing himself off a ledge for nothing. Kept low on
    // purpose — a player almost straight below him is the strongest case for dropping, and a
    // straight-down chase closes very little horizontal distance.
    private const float DropJumpMinHorizontalGainMeters = 1f;

    private bool _dropJumpActive;
    private float _dropJumpStartedAt;
    private float _dropJumpDuration;
    private float _dropJumpGravity;
    private float _dropJumpUpSpeed;
    private Vector3 _dropJumpStart;
    private Vector3 _dropJumpHorizontalVelocity;
    private Vector3 _dropJumpLanding;
    private Vector3 _dropJumpPreviousPosition;
    private float _nextDropJumpAllowedAt;
    private int _hasFallingParameter = -1;
    private bool _warnedFallingParameterMissing;
    private bool _loggedFallingParameterPresent;

    private bool IsDropJumpActive() => _dropJumpActive;

    // Owner-side gate. Called from DoAIInterval immediately before UpdateMovementDestination, i.e. after
    // the action/turn-clip locks and after the attack and movement-context opportunities have had their
    // say — dropping off a ledge must never pre-empt a swing that is already available.
    private bool TryBeginDropDownFollow(PlayerControllerB huntTarget)
    {
        if (_dropJumpActive || isEnemyDead || !IsOwner)
            return false;

        if (Time.time < _nextDropJumpAllowedAt)
            return false;

        // Never mid-action, mid-lunge, mid-finisher, mid-stagger or mid-turn-clip: all of those own the
        // root already, and two owners of the root is the bug this whole area keeps producing.
        if (IsActionLocked() ||
            _lungeActive ||
            _finisherLungeActive ||
            _pairedActionRootLockActive ||
            _turnClipRootLockActive)
        {
            return false;
        }

        if (!AgentReadyForMovement() || !IsReachableHuntTarget(huntTarget))
            return false;

        bool forced = BundyPlugin.ModConfig?.DropDownFollowForceTrigger?.Value ?? false;
        Vector3 targetPosition = GetPursuitDestination(huntTarget);
        Vector3 origin = transform.position;

        // The motivating condition: the target sits at least a drop's worth below him. Everything
        // after this is only deciding whether the walk is bad enough and the ledge safe enough.
        float verticalDrop = origin.y - targetPosition.y;
        if (!forced && verticalDrop < DropDownFollowMinHeightMeters)
            return false;

        // A connected interior usually has a complete path even when it winds around an entire floor.
        // Keep the walk only while it stays close to the direct chase. Three ways past this gate: no
        // COMPLETE path at all (partial/invalid — there is no walk to keep, so the ledge geometry
        // below decides alone; the cached helper returning false IS that pass, kept explicit here on
        // purpose), the detour ratio, and the absolute detour excess (on a long chase a serious
        // detour still looks mild as a ratio, so extra corridor meters count on their own).
        if (!forced &&
            TryGetCompleteNavigationPathDistanceCached(targetPosition, out float pathDistance))
        {
            float directDistance = Vector3.Distance(origin, targetPosition);
            float detourRatio = Mathf.Max(
                1f,
                BundyPlugin.ModConfig?.DropDownFollowDetourRatio?.Value ?? 1.25f);
            float minDetourMeters = Mathf.Max(
                0f,
                BundyPlugin.ModConfig?.DropDownFollowMinDetourMeters?.Value ?? 6f);
            bool detourByRatio = pathDistance > directDistance * detourRatio;
            bool detourByExcess = minDetourMeters > 0f && pathDistance - directDistance >= minDetourMeters;
            if (!detourByRatio && !detourByExcess)
                return false;
        }

        if (!TryResolveDropLanding(targetPosition, forced, out Vector3 landing, out string rejection))
        {
            // C1.5: verbosity 1, not 2 — drop evaluations must be visible in a default-verbosity run
            // (the 2026-07-30 run showed 0 evaluations and 5a was unjudgeable because of it).
            LogDiagnostic(
                BundyLogCategory.Movement,
                1,
                $"BundyDropJump rejected: reason={rejection}, target={PlayerLabel(huntTarget)}, " +
                $"drop={verticalDrop:F1}m.");
            return false;
        }

        BeginDropJump(landing, huntTarget);

        if (forced && BundyPlugin.ModConfig?.DropDownFollowForceTrigger != null)
        {
            BundyPlugin.ModConfig.DropDownFollowForceTrigger.Value = false;
            LogInfo(BundyLogCategory.Movement, 0, "BundyDropJump forced trigger accepted; request consumed.");
        }

        return true;
    }

    // The whole 5a geometry test. Probe forward-and-down from the ledge lip along the horizontal bearing
    // to the target — and, because the lip is rarely exactly along that bearing, a small ±25° fan at a
    // near and a far distance — take the floor a probe finds, and require that floor to be on the
    // NavMesh, inside the configured height band, and closer to the target than where he stands. First
    // candidate that passes every safety check wins; the rejection reported is the first candidate's,
    // i.e. the one closest to the straight chase.
    private bool TryResolveDropLanding(
        Vector3 targetPosition,
        bool forced,
        out Vector3 landing,
        out string rejection)
    {
        landing = Vector3.zero;
        rejection = "none";

        Vector3 origin = transform.position;
        Vector3 bearing = targetPosition - origin;
        bearing.y = 0f;
        if (bearing.sqrMagnitude >= DropJumpMinHorizontalMeters * DropJumpMinHorizontalMeters)
        {
            bearing.Normalize();
        }
        else
        {
            // Target (almost) directly underfoot — the strongest reason to drop, but no usable
            // bearing to probe along. Fan around his facing instead: the ledge he is standing at is
            // the one the target went down through, so it is somewhere in front of him.
            bearing = transform.forward;
            bearing.y = 0f;
            if (bearing.sqrMagnitude < 0.001f)
            {
                rejection = "no horizontal bearing to probe along";
                return false;
            }

            bearing.Normalize();
        }

        int mask = GetWorldCollisionMask();
        bool rejectionRecorded = false;
        for (int fanStep = 0; fanStep < 3; fanStep++)
        {
            float yaw = fanStep == 0 ? 0f : (fanStep == 1 ? DropJumpLedgeProbeFanDegrees : -DropJumpLedgeProbeFanDegrees);
            Vector3 direction = yaw == 0f ? bearing : Quaternion.Euler(0f, yaw, 0f) * bearing;
            for (int ring = 0; ring < 2; ring++)
            {
                float forwardMeters = ring == 0 ? DropJumpLedgeProbeForwardMeters : DropJumpLedgeProbeFarMeters;
                if (TryResolveDropLandingAlong(
                        origin, targetPosition, direction, forwardMeters, forced, mask,
                        out landing, out string candidateRejection))
                {
                    return true;
                }

                if (!rejectionRecorded)
                {
                    rejection = candidateRejection;
                    rejectionRecorded = true;
                }
            }
        }

        return false;
    }

    // One fan candidate: the original single-probe geometry test, unchanged in its safety checks.
    private bool TryResolveDropLandingAlong(
        Vector3 origin,
        Vector3 targetPosition,
        Vector3 direction,
        float forwardMeters,
        bool forced,
        int mask,
        out Vector3 landing,
        out string rejection)
    {
        landing = Vector3.zero;
        rejection = "none";

        Vector3 probeOrigin = origin + Vector3.up * DropJumpGroundProbeUpOffset + direction * forwardMeters;
        float probeHeight = forced ? DropJumpForcedProbeMaxHeightMeters : DropDownFollowMaxHeightMeters;
        float maxProbe = probeHeight + DropJumpGroundProbeUpOffset + DropJumpLedgeProbeFloorTolerance + 1f;
        if (!Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit hit, maxProbe, mask, QueryTriggerInteraction.Ignore))
        {
            rejection = "no floor found below the ledge probe";
            return false;
        }

        float dropHeight = origin.y - hit.point.y;
        if (!forced && dropHeight < DropDownFollowMinHeightMeters)
        {
            // Either there is no ledge at all in front of him, or it is a step he can simply walk down.
            rejection = dropHeight <= DropJumpLedgeProbeFloorTolerance ? "no ledge in front of him" : "drop below the minimum band";
            return false;
        }

        if (!forced && dropHeight > DropDownFollowMaxHeightMeters)
        {
            rejection = $"drop {dropHeight:F1}m exceeds the configured band";
            return false;
        }

        // Abort-to-recovery contract: if the floor below the ledge is not NavMesh, the drop is refused
        // here rather than half-committed and rescued later.
        if (!NavMesh.SamplePosition(hit.point, out NavMeshHit navHit, DropJumpLandingSampleRadius, NavMesh.AllAreas))
        {
            rejection = "no NavMesh below the ledge";
            return false;
        }

        float horizontalBefore = PlanarDistance(origin, targetPosition);
        float horizontalAfter = PlanarDistance(navHit.position, targetPosition);
        if (!forced &&
            horizontalBefore - horizontalAfter < DropJumpMinHorizontalGainMeters &&
            horizontalAfter > DropJumpMinHorizontalGainMeters)
        {
            rejection = "landing does not close on the target";
            return false;
        }

        landing = navHit.position;
        return true;
    }

    private void BeginDropJump(Vector3 landing, PlayerControllerB huntTarget)
    {
        Vector3 origin = transform.position;
        Vector3 horizontal = landing - origin;
        horizontal.y = 0f;
        float horizontalDistance = horizontal.magnitude;
        float height = Mathf.Max(0.01f, origin.y - landing.y);

        // Arc: a small forward hop, then a fixed gravity. Solve the fall time from the vertical
        // kinematics (0.5*g*t^2 - v0*t - h = 0) and derive the horizontal speed that lands him exactly on
        // the sampled point. If that speed exceeds the clamp, hold the clamp and re-derive the gravity
        // that still terminates at the same height — a slower, floatier fall rather than a landing that
        // misses the point the NavMesh sample validated.
        float gravity = Mathf.Max(1f, DropDownFollowGravity);
        float upSpeed = Mathf.Max(0f, DropDownFollowLaunchUpSpeed);
        float duration = (upSpeed + Mathf.Sqrt(upSpeed * upSpeed + 2f * gravity * height)) / gravity;
        float horizontalSpeed = duration > 0.01f ? horizontalDistance / duration : 0f;
        float maxHorizontalSpeed = Mathf.Max(0.5f, DropDownFollowMaxHorizontalSpeed);
        if (horizontalSpeed > maxHorizontalSpeed)
        {
            horizontalSpeed = maxHorizontalSpeed;
            duration = horizontalDistance / horizontalSpeed;
            gravity = 2f * (upSpeed * duration + height) / (duration * duration);
        }

        _dropJumpActive = true;
        _dropJumpStartedAt = Time.time;
        _dropJumpDuration = Mathf.Max(0.05f, duration);
        _dropJumpGravity = gravity;
        _dropJumpUpSpeed = upSpeed;
        _dropJumpStart = origin;
        _dropJumpPreviousPosition = origin;
        _dropJumpLanding = landing;
        _dropJumpHorizontalVelocity = horizontalDistance > 0.001f
            ? horizontal / horizontalDistance * horizontalSpeed
            : Vector3.zero;

        // Park the agent. Same shape as the turn-clip root lock: the agent keeps its NavMesh footing at
        // the ledge (so agent.isOnNavMesh stays true and EnsureAgentOnNavMesh never fires mid-arc) while
        // the transform is driven by the arc; the landing Warp reunites them.
        StopPursuitSearchIfActive();
        moveTowardsDestination = false;
        movingTowardsTargetPlayer = false;
        _locomotionIntentHoldUntil = 0f;
        if (creatureAnimator != null)
            creatureAnimator.applyRootMotion = false;
        if (agent != null && agent.enabled && agent.isOnNavMesh)
        {
            agent.ResetPath();
            agent.isStopped = true;
            agent.updatePosition = false;
            agent.updateRotation = false;
        }

        UpdateLocomotionSpeedMultiplier(false, false);
        // #308: MotionState is the only channel the arc used to leave alone, and it is the one that
        // decides which clip plays. On an animator that has no Jump_Fall state nothing takes him out of
        // Walk/Run, so he ran through the air; neutralizing here makes the degrade path idle-in-air.
        // Nothing has to restore it — ApplyMotionStateFromMovement re-drives MotionState from the
        // agent every Update frame, and the first such frame after FinishDropJump is the one that
        // re-enters the locomotion stack.
        SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
        SetFallingAnimatorFlag(true);

        // Movement authority: the airborne window is visible in [BundyMoveAuth] rather than a silent gap.
        RecordPursuitDestination(landing, "jump-drop:begin", "drop-down follow; scripted arc owns movement");
        LogInfo(
            BundyLogCategory.Movement,
            0,
            $"BundyDropJump start: target={PlayerLabel(huntTarget)}, from={FormatVector(origin)}, " +
            $"landing={FormatVector(landing)}, drop={height:F1}m, horizontal={horizontalDistance:F1}m, " +
            $"duration={_dropJumpDuration:F2}s, gravity={gravity:F1}, horizontalSpeed={horizontalSpeed:F1}m/s.");
    }

    // Per-frame arc integration. Returns true while the drop owns the frame; the caller (Core.Update)
    // skips the whole locomotion/facing/watchdog stack for that frame, exactly as it would during a
    // turn-clip root lock.
    private bool UpdateActiveDropJump()
    {
        if (!_dropJumpActive)
            return false;

        if (isEnemyDead || !IsOwner)
        {
            FinishDropJump(transform.position, "interrupted", landed: false);
            return false;
        }

        float elapsed = Time.time - _dropJumpStartedAt;
        if (elapsed > DropDownFollowMaxTraversalSeconds)
        {
            // Watchdog: never leave him airborne. The landing sample from the trigger is still the best
            // known ground, so aim the recovery at it.
            FinishDropJump(_dropJumpLanding, "traversal watchdog timeout", landed: true);
            return false;
        }

        float clamped = Mathf.Min(elapsed, _dropJumpDuration);
        Vector3 position = _dropJumpStart +
            _dropJumpHorizontalVelocity * clamped +
            Vector3.up * (_dropJumpUpSpeed * clamped - 0.5f * _dropJumpGravity * clamped * clamped);

        // Early ground contact: past the apex, sweep the travelled segment so a roof, catwalk or railing
        // between him and the sampled floor stops him there instead of being passed through.
        bool pastApex = _dropJumpGravity > 0.01f && clamped > _dropJumpUpSpeed / _dropJumpGravity;
        Vector3 travel = position - _dropJumpPreviousPosition;
        float travelDistance = travel.magnitude;
        if (pastApex && travelDistance > 0.01f &&
            Physics.SphereCast(
                _dropJumpPreviousPosition + Vector3.up * DropJumpSweepRadius,
                DropJumpSweepRadius,
                travel / travelDistance,
                out RaycastHit contact,
                travelDistance,
                GetWorldCollisionMask(),
                QueryTriggerInteraction.Ignore))
        {
            FinishDropJump(contact.point, "ground contact", landed: true);
            return false;
        }

        transform.position = position;
        _dropJumpPreviousPosition = position;

        Vector3 facing = _dropJumpHorizontalVelocity;
        facing.y = 0f;
        if (facing.sqrMagnitude > 0.001f)
        {
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                Quaternion.LookRotation(facing.normalized, Vector3.up),
                DropJumpFacingTurnDegreesPerSecond * Time.deltaTime);
        }

        // Keep the agent parked at the ledge and the locomotion channels quiet: the fall's velocity would
        // otherwise saturate MoveX/MoveY and the Speed multiplier.
        if (agent != null && agent.enabled && agent.isOnNavMesh)
            agent.isStopped = true;
        UpdateLocomotionSpeedMultiplier(false, false);
        SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
        UpdateDirectionalLocomotionFromWorldVelocity(Vector3.zero, allowDesiredVelocityFallback: false);

        if (elapsed >= _dropJumpDuration)
        {
            FinishDropJump(_dropJumpLanding, "arc complete", landed: true);
            return false;
        }

        return true;
    }

    // The single drop-exit seam, and the only place _dropJumpActive is cleared. Its release is
    // unconditional for the same reason ResumeMovementAfterAction's is: an exit that returns with the
    // agent still held is the failure this codebase has already paid for once.
    private void FinishDropJump(Vector3 position, string reason, bool landed)
    {
        if (!_dropJumpActive)
            return;

        _dropJumpActive = false;
        _nextDropJumpAllowedAt = Time.time + DropDownFollowCooldownSeconds;
        SetFallingAnimatorFlag(false);

        bool reacquired = false;
        if (landed)
        {
            if (NavMesh.SamplePosition(position, out NavMeshHit hit, DropJumpLandingSampleRadius, NavMesh.AllAreas))
            {
                transform.position = hit.position;
                reacquired = true;
            }
            else if (NavMesh.SamplePosition(_dropJumpLanding, out NavMeshHit fallback, DropJumpLandingSampleRadius, NavMesh.AllAreas))
            {
                transform.position = fallback.position;
                reacquired = true;
            }
            else
            {
                transform.position = position;
            }
        }

        if (agent != null && agent.enabled)
        {
            agent.updatePosition = true;
            try
            {
                agent.Warp(transform.position);
            }
            catch (System.Exception e)
            {
                LogWarning($"Drop-down follow landing warp failed ({reason}): {e.Message}");
            }
        }

        // Release first, decide second — the same order the action exit uses. UnlockAgentForMovement
        // restores updatePosition/updateRotation/stoppingDistance, re-syncs nextPosition and clears
        // isStopped, so nothing downstream sees a stranded hold.
        UnlockAgentForMovement($"drop-down follow {reason}");
        ResetMovementProgressTracker();

        if (!reacquired && landed)
        {
            // Abort-to-recovery: no sample anywhere near the landing. He is released and pursuing
            // normally; EnsureAgentOnNavMesh owns getting him back onto the graph from here.
            LogWarning(
                $"Drop-down follow could not re-acquire the NavMesh at {FormatVector(position)} ({reason}); " +
                "released to normal pursuit and NavMesh recovery.");
        }

        LogInfo(
            BundyLogCategory.Movement,
            0,
            $"BundyDropJump end: reason={reason}, landed={landed}, reacquired={reacquired}, " +
            $"position={FormatVector(transform.position)}, cooldown={DropDownFollowCooldownSeconds:F1}s.");

        if (agent != null && agent.enabled && agent.isOnNavMesh)
            IssueRawAgentDestination(transform.position, "jump-drop", $"landed ({reason}); NavMesh re-acquired");

        // Resume pursuit the same way the action exit does — but only if we still own him; an ownership
        // change mid-arc lands through here too, and a non-owner must not write destinations.
        if (!IsOwner || isEnemyDead)
            return;

        PlayerControllerB huntTarget = GetCurrentHuntTarget();
        if (IsReachableHuntTarget(huntTarget) && AgentReadyForMovement())
            UpdateMovementDestination(huntTarget);
    }

    // Animator contract with the Unity-side BundyJumpStatePatch: a bool named "Falling" on the shipping
    // controller's Base Layer drives an AnyState -> Jump_Fall transition. An older bundle that predates
    // the patch simply has no such parameter, so the drop still works and only the airborne pose is
    // missing — the same degrade shape as the missing LungeLowerBody layer in Locomotion.cs.
    private void SetFallingAnimatorFlag(bool falling)
    {
        if (creatureAnimator == null)
            return;

        if (!HasAnimatorBoolParameter(BundyAnimatorParams.Falling, ref _hasFallingParameter))
        {
            WarnOnce(
                ref _warnedFallingParameterMissing,
                $"Animator bool parameter '{BundyAnimatorParams.Falling}' missing; drop-down follow runs " +
                "without its fall pose (rebuild the Bundy bundle to pick up BundyJumpStatePatch).");
            return;
        }

        creatureAnimator.SetBool(BundyAnimatorParams.Falling, falling);

        // #308: only the failure path used to say anything, so a deployed bundle that DOES carry the
        // parameter was indistinguishable from one whose write never happened. One receipt per session
        // on the first successful write, with the base-layer state the drop leaves from — the pair
        // (parameter present, state still Walk/Run a frame later) is what identifies a bundle that has
        // the bool but not the Jump_Fall state.
        if (!_loggedFallingParameterPresent)
        {
            _loggedFallingParameterPresent = true;
            LogInfo(
                BundyLogCategory.Anim,
                0,
                $"Animator bool parameter '{BundyAnimatorParams.Falling}' present; first write={falling}, " +
                $"baseLayerState={GetCurrentAnimatorStateName()}.");
        }
    }
}
