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
    private void TryPlayPursuitTurnClip(PlayerControllerB target, float distanceToTarget, bool allowMovementDirectionFallback = false)
    {
        if (!TurnClipsEnabled || creatureAnimator == null || IsActionLocked() || !IsLivingPlayer(target))
            return;

        if (ShouldForcePostBasicAttackWalk(target))
            return;

        if (IsTurnClipRootLockActive())
            return;

        if (distanceToTarget <= StopDistance + 0.25f || distanceToTarget > TurnClipMaxDistance)
            return;

        if (Time.time < _nextTurnClipAllowedAt)
            return;

        if (agent != null && agent.velocity.sqrMagnitude > 0.09f && !TurnClipsCanInterruptMovement)
            return;

        Vector3 direction;
        float signedYaw;
        string directionSource;
        if (!TryResolvePursuitTurnDirection(target, allowMovementDirectionFallback, out direction, out signedYaw, out directionSource))
            return;

        float absYaw = Mathf.Abs(signedYaw);
        int turnIndex = absYaw >= 135f
            ? BundyAnimatorParams.Turn180
            : (signedYaw < 0f ? BundyAnimatorParams.TurnLeft90 : BundyAnimatorParams.TurnRight90);

        _nextTurnClipAllowedAt = Time.time + Mathf.Max(0.1f, TurnClipCooldownSeconds);
        BeginTurnClipRootSync(direction, turnIndex);
        SetAnimatorIntegerThenTrigger(BundyAnimatorParams.TurnIndex, turnIndex, BundyAnimatorParams.DoTurn);

        if (ShouldLog(BundyLogCategory.Anim, 2))
        {
            string stateName = GetCurrentAnimatorStateName();
            int motionState = creatureAnimator != null ? creatureAnimator.GetInteger(BundyAnimatorParams.MotionState) : -1;
            // #716 F3: through the Bundy writer (DebugLogs master applies there) rather than
            // UnityEngine.Debug; WriteInfoLine supplies the same [VariantName] prefix.
            WriteInfoLine(
                $"TURN_TRIGGERED: index={turnIndex}({TurnIndexName(turnIndex)}), " +
                $"yaw={signedYaw:F1}, distance={distanceToTarget:F2}, directionSource={directionSource}, " +
                $"animState={stateName}, motionState={motionState}, " +
                $"manualFacing={(UsesManualTargetFacing && agent != null && !agent.updateRotation)}, " +
                $"rootLock={_turnClipRootLockActive}, turnSpeed={ManualPursuitTurnSpeedDegrees:F0}deg/s");
        }
    }

    private bool TryResolvePursuitTurnDirection(
        PlayerControllerB target,
        bool allowMovementDirectionFallback,
        out Vector3 direction,
        out float signedYaw,
        out string directionSource)
    {
        // #306: while he is following a path, the PATH heading is the direction he is about to travel;
        // the straight-line bearing to the target points through whatever wall the corner bends around.
        // Turning to that bearing stopped him, aimed him into the wall and resumed — every cooldown.
        // The bearing still governs the pathless and close-quarters combat cases, where the steering
        // target has collapsed onto him and carries no heading (see the reach gate in the helper).
        if (TryGetPursuitPathHeading(out Vector3 pathHeading))
            return TryUseTurnDirection(pathHeading, "path", out direction, out signedYaw, out directionSource);

        Vector3 targetDirection = target.transform.position - transform.position;
        if (TryUseTurnDirection(targetDirection, "target", out direction, out signedYaw, out directionSource))
            return true;

        if (!allowMovementDirectionFallback || !TurnClipsCanInterruptMovement || agent == null)
            return false;

        if (TryUseTurnDirection(agent.desiredVelocity, "desiredVelocity", out direction, out signedYaw, out directionSource))
            return true;

        if (TryUseTurnDirection(agent.velocity, "velocity", out direction, out signedYaw, out directionSource))
            return true;

        return false;
    }

    // The reach gate is what keeps this out of the combat case: on the last leg of a path the steering
    // target sits on top of him and its "heading" is noise, so the caller falls through to the bearing.
    private bool TryGetPursuitPathHeading(out Vector3 heading)
    {
        heading = Vector3.zero;
        if (agent == null || !agent.enabled || !agent.isOnNavMesh || !agent.hasPath)
            return false;

        heading = agent.steeringTarget - transform.position;
        heading.y = 0f;
        return heading.sqrMagnitude >= TurnClipPathHeadingMinReach * TurnClipPathHeadingMinReach;
    }

    private bool TryUseTurnDirection(
        Vector3 candidate,
        string source,
        out Vector3 direction,
        out float signedYaw,
        out string directionSource)
    {
        direction = candidate;
        direction.y = 0f;
        signedYaw = 0f;
        directionSource = source;

        if (direction.sqrMagnitude < 0.001f)
            return false;

        direction.Normalize();
        signedYaw = Vector3.SignedAngle(transform.forward, direction, Vector3.up);
        return Mathf.Abs(signedYaw) >= TurnClipMinYawDegrees;
    }

    private void BeginTurnClipRootSync(Vector3 direction, int turnIndex)
    {
        if (!TurnClipsCanInterruptMovement)
            return;

        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f)
            return;

        _turnClipRootLockActive = true;
        _turnClipRootLockStartedAt = Time.time;
        _turnClipRootLockUntil = Time.time + Mathf.Max(0.05f, GetTurnClipRootSyncSeconds(turnIndex));
        _turnClipRootLockIndex = turnIndex;
        _turnClipRootStartRotation = transform.rotation;
        _turnClipRootTargetRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        _locomotionIntentHoldUntil = 0f;

        if (agent != null && agent.enabled && agent.isOnNavMesh)
        {
            agent.isStopped = true;
            agent.updateRotation = false;
            agent.nextPosition = transform.position;
        }

        UpdateLocomotionSpeedMultiplier(false, false);
        SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
    }

    private void UpdateActiveTurnClipRootSync()
    {
        if (!_turnClipRootLockActive)
            return;

        if (isEnemyDead || IsActionLocked() || _pairedActionRootLockActive)
        {
            EndTurnClipRootSync("interrupted");
            return;
        }

        float duration = Mathf.Max(0.05f, _turnClipRootLockUntil - _turnClipRootLockStartedAt);
        float t = Mathf.Clamp01((Time.time - _turnClipRootLockStartedAt) / duration);
        float eased = t * t * (3f - 2f * t);
        transform.rotation = Quaternion.Slerp(_turnClipRootStartRotation, _turnClipRootTargetRotation, eased);

        if (agent != null && agent.enabled && agent.isOnNavMesh)
        {
            agent.isStopped = true;
            agent.updateRotation = false;
            agent.nextPosition = transform.position;
        }

        UpdateLocomotionSpeedMultiplier(false, false);
        SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);

        if (t >= 1f)
        {
            transform.rotation = _turnClipRootTargetRotation;
            EndTurnClipRootSync("complete");
        }
    }

    private bool IsTurnClipRootLockActive()
    {
        if (!_turnClipRootLockActive)
            return false;

        if (Time.time >= _turnClipRootLockUntil)
        {
            transform.rotation = _turnClipRootTargetRotation;
            EndTurnClipRootSync("expired");
            return false;
        }

        return true;
    }

    private void EndTurnClipRootSync(string reason)
    {
        if (!_turnClipRootLockActive)
            return;

        int turnIndex = _turnClipRootLockIndex;
        _turnClipRootLockActive = false;
        _turnClipRootLockIndex = -1;

        if (agent != null && agent.enabled && agent.isOnNavMesh && !IsActionLocked())
            agent.isStopped = false;

        if (ShouldLog(BundyLogCategory.Anim, 2))
        {
            // #716 F3: Bundy writer instead of UnityEngine.Debug (same [VariantName] prefix).
            WriteInfoLine(
                $"TURN_ROOT_SYNC_END: reason={reason}, index={turnIndex}({TurnIndexName(turnIndex)}), " +
                $"rotation={transform.eulerAngles.y:F1}, agentStopped={(agent != null && agent.enabled && agent.isStopped)}");
        }
    }

    private void ApplyMotionStateFromMovement()
    {
        if (IsTurnClipRootLockActive())
        {
            UpdateLocomotionSpeedMultiplier(false, false);
            SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
            return;
        }

        if (_batterySabotageRouteActive && RoundReady() && AgentReadyForMovement() && !IsActionLocked())
        {
            float batteryDistance = Vector3.Distance(transform.position, _batterySabotageDestination);
            bool batteryMovingByIntent = IsLocomotionIntended() || HasRecentLocomotionIntent();
            if (batteryDistance <= StopDistance && !batteryMovingByIntent)
            {
                UpdateLocomotionSpeedMultiplier(false, false);
                SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
                return;
            }

            bool batteryRun = batteryDistance > RunThreshold;
            agent.speed = batteryRun ? RunSpeed : WalkSpeed;
            agent.angularSpeed = AgentAngularSpeedDegrees;
            agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);
            if (!UsesManualTargetFacing)
                agent.updateRotation = !ManualPursuitControlsAgentRotation;
            UpdateLocomotionSpeedMultiplier(true, batteryRun);
            SetMotionStateIfChanged(batteryRun ? BundyAnimatorParams.MotionRun : BundyAnimatorParams.MotionWalk);
            return;
        }

        PlayerControllerB huntTarget = GetCurrentHuntTarget();
        if (!RoundReady() || !AgentReadyForMovement() || !IsLivingPlayer(huntTarget))
        {
            UpdateLocomotionSpeedMultiplier(false, false);
            SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
            return;
        }

        if (IsActionLocked())
        {
            // During a lunge keep the leg playback synced to the (fast) lunge velocity so the
            // override-layer run legs don't slide; otherwise neutralize during stationary actions.
            UpdateLocomotionSpeedMultiplier(_lungeActive, _lungeActive);
            return;
        }

        float distance = GetCurrentHuntDistance(huntTarget);
        bool movingByIntent = IsLocomotionIntended() || HasRecentLocomotionIntent();
        if (distance <= StopDistance && !movingByIntent)
        {
            UpdateLocomotionSpeedMultiplier(false, false);
            SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
            return;
        }

        bool running = ShouldRun(huntTarget);
        ApplyGaitSpeed(running);
        agent.angularSpeed = AgentAngularSpeedDegrees;
        agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);
        if (!UsesManualTargetFacing)
            agent.updateRotation = !ManualPursuitControlsAgentRotation;
        UpdateLocomotionSpeedMultiplier(true, running);
        SetMotionStateIfChanged(running ? BundyAnimatorParams.MotionRun : BundyAnimatorParams.MotionWalk);
    }

    // Issue #205: the ONE place agent.speed is chosen. It was assigned from nine sites as
    // `running ? RunSpeed : WalkSpeed`, and the sprint cannot be a tenth special case: its clips are
    // in-place, so the stance foot only stops skating when the agent is actually moving at the speed
    // the cycle was authored for. A single site that missed the sprint would slide his feet for the
    // whole gait.
    //
    // Only the RUNNING phase takes sprint speed. The wind-down deliberately falls back to the run,
    // because the outro is authored at 2.43 m/s in armature units — 4.25 m/s once the 1.75x animator
    // scale is applied, which is ChainsawRunSpeed exactly. The clip is what decelerates him, and it
    // was built to hand him over at the run's speed.
    //
    // Issue #207: the sprint's own speed is not one number. GetSprintGroundSpeed follows the intro's
    // authored acceleration ramp and returns the loop's constant everywhere else — this is called
    // every frame from Update (Core.cs) via ApplyMotionStateFromMovement, not at DoAIInterval, so the
    // ramp is smooth rather than five steps across a one-second intro.
    protected void ApplyGaitSpeed(bool running)
    {
        if (agent == null)
            return;

        agent.speed = IsSprintDrivingGroundSpeed() ? GetSprintGroundSpeed() : (running ? RunSpeed : WalkSpeed);
    }

    private bool IsLocomotionIntended()
    {
        if (!AgentReadyForMovement())
            return false;

        Vector3 velocity = agent.velocity;
        velocity.y = 0f;
        if (velocity.sqrMagnitude > DirectionalMoveDeadband * DirectionalMoveDeadband)
            return true;

        Vector3 desired = agent.desiredVelocity;
        desired.y = 0f;
        if (desired.sqrMagnitude > DirectionalMoveDeadband * DirectionalMoveDeadband)
            return true;

        if (!agent.isStopped && agent.hasPath && agent.remainingDistance > Mathf.Max(0.1f, agent.stoppingDistance + 0.1f))
            return true;

        return false;
    }

    private void HoldLocomotionIntent(float seconds)
    {
        if (!UsesDirectionalLocomotion || seconds <= 0f)
            return;

        _locomotionIntentHoldUntil = Mathf.Max(_locomotionIntentHoldUntil, Time.time + seconds);
    }

    private bool HasRecentLocomotionIntent()
    {
        return UsesDirectionalLocomotion && Time.time < _locomotionIntentHoldUntil;
    }

    private void UpdateObservedLocomotionVisuals()
    {
        if (creatureAnimator == null)
            return;

        Vector3 currentPosition = transform.position;
        Vector3 worldVelocity = Vector3.zero;
        if (_hasLastDirectionalPos && Time.deltaTime > 0.0001f)
            worldVelocity = (currentPosition - _lastDirectionalPos) / Time.deltaTime;
        _lastDirectionalPos = currentPosition;
        _hasLastDirectionalPos = true;
        worldVelocity.y = 0f;

        float observedSpeed = worldVelocity.magnitude;
        bool moving = observedSpeed > DirectionalMoveDeadband;
        bool running = observedSpeed >= Mathf.Max(WalkSpeed + 0.25f, RunSpeed * 0.65f);

        UpdateLocomotionSpeedMultiplierFromSpeed(moving, running, observedSpeed);
        SetMotionStateIfChanged(moving
            ? (running ? BundyAnimatorParams.MotionRun : BundyAnimatorParams.MotionWalk)
            : BundyAnimatorParams.MotionIdle);
        UpdateDirectionalLocomotionFromWorldVelocity(worldVelocity, allowDesiredVelocityFallback: false);
    }

    // Stage 1: drive the "Speed" float into the Walk/Run states' Speed Multiplier so foot speed
    // tracks the agent's real velocity. At steady state (velocity == target gait speed) this is
    // ~1.0, i.e. identical to the previous fixed-rate playback; during acceleration, deceleration
    // and cornering it scales the clip so the feet stop sliding/floating. Owner-only (the only
    // caller); the NetworkAnimator replicates the float to remote clients.
    private void UpdateLocomotionSpeedMultiplier(bool moving, bool running)
    {
        if (!UsesLocomotionSpeedMultiplier)
            return;

        Vector3 v = agent != null ? agent.velocity : Vector3.zero;
        float planarSpeed = new Vector3(v.x, 0f, v.z).magnitude;
        if (moving && agent != null && planarSpeed <= DirectionalMoveDeadband)
        {
            Vector3 desired = agent.desiredVelocity;
            desired.y = 0f;
            if (desired.sqrMagnitude > v.sqrMagnitude)
                planarSpeed = desired.magnitude;
        }
        UpdateLocomotionSpeedMultiplierFromSpeed(moving, running, planarSpeed);
    }

    private void UpdateLocomotionSpeedMultiplierFromSpeed(bool moving, bool running, float planarSpeed)
    {
        if (!UsesLocomotionSpeedMultiplier || !HasAnimatorFloatParameter(BundyAnimatorParams.Speed, ref _hasSpeedParameter))
            return;

        float multiplier = 1f;
        bool keepSourcePlayback =
            UsesSourcePackNormalLocomotionPlayback &&
            !_lungeActive &&
            !_finisherLungeActive;

        if (moving && !keepSourcePlayback)
        {
            // Issue #205: the sprint has its own authored cadence. Without this branch a sprinting
            // Bundy divides 7.65 m/s by the RUN's 3.6 and plays the sprint loop at 2.1x.
            float reference = IsSprintDrivingGroundSpeed()
                ? LocomotionSprintClipCadence
                : (running ? LocomotionRunClipCadence : LocomotionWalkClipCadence);
            multiplier = reference > 0.01f ? planarSpeed / reference : 1f;
            multiplier = Mathf.Clamp(multiplier, LocomotionSpeedMultiplierMin, LocomotionSpeedMultiplierMax);
        }

        SetAnimatorFloat(BundyAnimatorParams.SpeedHash, BundyAnimatorParams.Speed, multiplier);
    }

    // Stage 2: drive the LungeLowerBody override layer weight. While lunging AND actually moving fast,
    // the legs run on top of the base-layer swing; as he arrives/stops the weight fades so the base
    // attack's own legs take over (the "stop & recover" feel). Owner-driven; the NetworkAnimator
    // replicates the layer weight to remote clients.
    private void UpdateLungeLayerWeight()
    {
        if (!UsesAttackLunge || creatureAnimator == null)
            return;

        if (_lungeLayerIndex < 0)
        {
            if (_lungeLayerIndex == -2)
                return;

            _lungeLayerIndex = creatureAnimator.GetLayerIndex(LungeLowerBodyLayer);
            if (_lungeLayerIndex < 0)
            {
                _lungeLayerIndex = -2;
                WarnOnce(ref _lungeLayerMissingLogged, $"Animator layer '{LungeLowerBodyLayer}' missing; lunge lower-body override disabled.");
                return;
            }
        }

        float planarSpeed = agent != null
            ? new Vector3(agent.velocity.x, 0f, agent.velocity.z).magnitude
            : 0f;
        float target = (_lungeActive && !_finisherLungeActive && planarSpeed > 1.5f) ? 1f : 0f;
        _lungeLayerWeight = Mathf.MoveTowards(_lungeLayerWeight, target, Time.deltaTime / 0.12f);
        creatureAnimator.SetLayerWeight(_lungeLayerIndex, _lungeLayerWeight);
    }

    // Stage 4: feed the directional blend tree with Bundy's planar velocity in his own local frame
    // (right = MoveX, forward = MoveY). Because he faces his target, this plays the correct
    // strafe/backpedal/diagonal clip while he keeps facing the player. Owner-driven; the
    // NetworkAnimator replicates MoveX/MoveY to remote clients.
    private void UpdateDirectionalLocomotion()
    {
        if (!UsesDirectionalLocomotion || creatureAnimator == null)
            return;

        // Measure his ACTUAL world movement (position delta per frame). This is ground truth: it can never
        // read zero while he's visibly moving, which is what made strafe/backpedal "glide" with no
        // animation when we relied on agent.velocity / desiredVelocity (both stutter to ~0 during the
        // frequently-repathed maneuvers). Snaps/teleports are absorbed by the MoveTowards smoothing below.
        Vector3 currentPosition = transform.position;
        Vector3 worldVelocity = Vector3.zero;
        if (_hasLastDirectionalPos && Time.deltaTime > 0.0001f)
            worldVelocity = (currentPosition - _lastDirectionalPos) / Time.deltaTime;
        _lastDirectionalPos = currentPosition;
        _hasLastDirectionalPos = true;
        worldVelocity.y = 0f;
        UpdateDirectionalLocomotionFromWorldVelocity(worldVelocity, allowDesiredVelocityFallback: true);
    }

    private void UpdateDirectionalLocomotionFromWorldVelocity(Vector3 worldVelocity, bool allowDesiredVelocityFallback)
    {
        if (!UsesDirectionalLocomotion || creatureAnimator == null)
            return;

        if (!HasAnimatorFloatParameter(BundyAnimatorParams.MoveX, ref _hasMoveXParameter) ||
            !HasAnimatorFloatParameter(BundyAnimatorParams.MoveY, ref _hasMoveYParameter))
            return;

        float actualSpeed = worldVelocity.magnitude;
        UpdateObservedVelocityIdleDemotion(actualSpeed, allowDemotion: allowDesiredVelocityFallback);

        Vector3 headingVelocity = worldVelocity;
        if (allowDesiredVelocityFallback && actualSpeed <= DirectionalMoveDeadband && agent != null)
        {
            Vector3 desired = agent.desiredVelocity;
            desired.y = 0f;
            if (desired.sqrMagnitude > actualSpeed * actualSpeed)
                headingVelocity = desired;
        }

        Vector3 magnitudeVelocity = actualSpeed > DirectionalMoveDeadband ? worldVelocity : headingVelocity;
        Vector3 magnitudeLocalVelocity = transform.InverseTransformDirection(magnitudeVelocity);
        Vector2 localBlendVelocity = new Vector2(magnitudeLocalVelocity.x, magnitudeLocalVelocity.z);
        if (LockDirectionalLocomotionToForward)
            localBlendVelocity = new Vector2(0f, localBlendVelocity.magnitude);
        float axisYaw = _lastMotionState == BundyAnimatorParams.MotionRun
            ? DirectionalRunLocomotionAxisYawDegrees
            : DirectionalLocomotionAxisYawDegrees;
        if (Mathf.Abs(axisYaw) > 0.001f)
            localBlendVelocity = RotatePlanarBlend(localBlendVelocity, axisYaw);

        Vector2 targetBlend = magnitudeVelocity.magnitude <= DirectionalMoveDeadband
            ? Vector2.zero
            : localBlendVelocity;
        targetBlend = KeepDirectionalBlendOffOrigin(targetBlend, axisYaw);
        float maxDelta = Mathf.Max(0.01f, DirectionalLocomotionResponse) * Time.deltaTime;
        _moveX = Mathf.MoveTowards(_moveX, targetBlend.x, maxDelta);
        _moveY = Mathf.MoveTowards(_moveY, targetBlend.y, maxDelta);
        _moveMag = new Vector2(_moveX, _moveY).magnitude;
        SetAnimatorFloat(BundyAnimatorParams.MoveXHash, BundyAnimatorParams.MoveX, _moveX);
        SetAnimatorFloat(BundyAnimatorParams.MoveYHash, BundyAnimatorParams.MoveY, _moveY);

        LogDirectionalLocomotionSummary(actualSpeed);
        LogDirectionalLocomotionDiag(magnitudeLocalVelocity.x, magnitudeLocalVelocity.z);
    }

    // C1.5: the default-verbosity 8-way heartbeat. The full DirLoco dump above is (Anim, 3) and was
    // silent in the 2026-07-30 run, which left "did the blend tree actually run 8-way?" unanswerable
    // — the entire visual judgement had to be thrown out when the persisted cfg turned out to have
    // forward-lock on. One line every 2s while he is actually in directional locomotion is enough to
    // read lock state and a non-forward blend from a default LogVerbosity=1 run.
    private float _dirLocoSummaryNextAt;

    private void LogDirectionalLocomotionSummary(float actualSpeed)
    {
        if (!ShouldLog(BundyLogCategory.Anim, 1) || Time.time < _dirLocoSummaryNextAt)
            return;

        if (!IsDirectionalLocomotionMotionState(_lastMotionState) || actualSpeed <= DirectionalMoveDeadband)
            return;

        _dirLocoSummaryNextAt = Time.time + 2f;
        LogDiagnostic(
            BundyLogCategory.Anim,
            1,
            $"[BundyDirLoco] lock={LockDirectionalLocomotionToForward}, " +
            $"motion={(_lastMotionState == BundyAnimatorParams.MotionRun ? "Run" : "Walk")}, " +
            $"blend=({_moveX:F2}, {_moveY:F2}), speed={actualSpeed:F2}");
    }

    private void UpdateObservedVelocityIdleDemotion(float observedSpeed, bool allowDemotion)
    {
        if (!UsesObservedVelocityIdleDemotion || IsPostBasicAttackCalmWindowActive())
        {
            ClearObservedVelocityIdleDemotionState();
            return;
        }

        if (observedSpeed > DirectionalMoveDeadband)
        {
            _observedStationarySince = -1f;
            if (!_observedVelocityIdleDemotionActive)
                return;

            _observedVelocityIdleDemotionActive = false;
            int pendingMotionState = _pendingObservedVelocityMotionState;
            _pendingObservedVelocityMotionState = -1;
            if (IsDirectionalLocomotionMotionState(pendingMotionState))
                SetMotionStateIfChanged(pendingMotionState);
            return;
        }

        if (!allowDemotion)
            return;

        if (_observedStationarySince < 0f)
            _observedStationarySince = Time.time;

        if (_observedVelocityIdleDemotionActive)
        {
            UpdateLocomotionSpeedMultiplier(false, false);
            return;
        }

        // The observed hold deliberately ignores HasRecentLocomotionIntent: intent already gets a
        // short grace period from this timer, while renewing it indefinitely would recreate the
        // stationary Walk/Run state this ground-truth gate exists to correct.
        if (Time.time - _observedStationarySince < Mathf.Max(0f, ObservedVelocityIdleDemotionHoldSeconds) ||
            !IsDirectionalLocomotionMotionState(_lastMotionState))
        {
            return;
        }

        _pendingObservedVelocityMotionState = _lastMotionState;
        _observedVelocityIdleDemotionActive = true;
        UpdateLocomotionSpeedMultiplier(false, false);
        SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
    }

    private void ClearObservedVelocityIdleDemotionState()
    {
        _observedStationarySince = -1f;
        _observedVelocityIdleDemotionActive = false;
        _pendingObservedVelocityMotionState = -1;
    }

    private Vector2 KeepDirectionalBlendOffOrigin(Vector2 targetBlend, float axisYaw)
    {
        if (targetBlend.sqrMagnitude > 0.000001f)
        {
            _lastNonZeroDirectionalBlend = targetBlend;
            _hasLastNonZeroDirectionalBlend = true;
            return targetBlend;
        }

        if (!IsDirectionalLocomotionMotionState(_lastMotionState))
            return Vector2.zero;

        if (_hasLastNonZeroDirectionalBlend)
            return _lastNonZeroDirectionalBlend;

        Vector2 currentBlend = new Vector2(_moveX, _moveY);
        if (currentBlend.sqrMagnitude > 0.000001f)
        {
            _lastNonZeroDirectionalBlend = currentBlend;
            _hasLastNonZeroDirectionalBlend = true;
            return currentBlend;
        }

        float authoredCadence = _lastMotionState == BundyAnimatorParams.MotionRun
            ? LocomotionRunClipCadence
            : LocomotionWalkClipCadence;
        Vector2 authoredForward = new Vector2(0f, Mathf.Max(DirectionalMoveDeadband, authoredCadence));
        if (Mathf.Abs(axisYaw) > 0.001f)
            authoredForward = RotatePlanarBlend(authoredForward, axisYaw);

        _lastNonZeroDirectionalBlend = authoredForward;
        _hasLastNonZeroDirectionalBlend = true;
        return authoredForward;
    }

    private static bool IsDirectionalLocomotionMotionState(int motionState)
    {
        return motionState == BundyAnimatorParams.MotionWalk ||
            motionState == BundyAnimatorParams.MotionRun;
    }

    private static Vector2 RotatePlanarBlend(Vector2 value, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        float sin = Mathf.Sin(radians);
        float cos = Mathf.Cos(radians);
        return new Vector2(
            value.x * cos + value.y * sin,
            -value.x * sin + value.y * cos);
    }

    // Throttled instrumentation — fires every 0.15s.
    // Answers: which state is active, are MoveX/MoveY actually arriving, real speed, clips playing.
    //
    // (Anim, 3). At 0.15s this is the single loudest emitter Bundy has — 4,095 lines in run 7, 14% of
    // the whole log — and it was reaching Debug.Log directly, so LogVerbosity and LogAnim had no effect
    // on it whatsoever. Verbosity 3 is "everything, i.e. the pre-Job-1 behaviour", which is exactly the
    // tier a per-frame blend-tree dump belongs in.
    private void LogDirectionalLocomotionDiag(float rawX, float rawZ)
    {
        if (!ShouldLog(BundyLogCategory.Anim, 3) || Time.time < _dirLocoLogNextAt) return;
        _dirLocoLogNextAt = Time.time + 0.15f;

        AnimatorStateInfo si = creatureAnimator.GetCurrentAnimatorStateInfo(0);
        bool inTransition = creatureAnimator.IsInTransition(0);

        string stateName;
        if (si.IsName("Idle")) stateName = "Idle";
        else if (si.IsName("Locomotion")) stateName = "Locomotion";
        else stateName = $"hash:{si.fullPathHash}";

        AnimatorClipInfo[] clips = creatureAnimator.GetCurrentAnimatorClipInfo(0);
        string clipStr;
        if (clips.Length == 0)
        {
            clipStr = "none";
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            foreach (AnimatorClipInfo c in clips)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(c.clip != null ? c.clip.name : "?");
                sb.Append(':');
                sb.Append(c.weight.ToString("F2"));
            }
            clipStr = sb.ToString();
        }

        float animMoveX = creatureAnimator.GetFloat(BundyAnimatorParams.MoveX);
        float animMoveY = creatureAnimator.GetFloat(BundyAnimatorParams.MoveY);
        float realSpeed = new Vector2(rawX, rawZ).magnitude;

        // #716 F3: Bundy writer instead of UnityEngine.Debug (same [VariantName] prefix).
        WriteInfoLine(
            $"DirLoco | state={stateName} inTransition={inTransition} | " +
            $"set=({_moveX:F3},{_moveY:F3}) anim=({animMoveX:F3},{animMoveY:F3}) | " +
            $"rawVel=({rawX:F3},{rawZ:F3}) speed={realSpeed:F3} | " +
            $"animSpeed={creatureAnimator.speed:F2} clips=[{clipStr}]");
    }
}
