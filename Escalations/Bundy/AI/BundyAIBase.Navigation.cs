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
    private void UpdateMovementDestination(PlayerControllerB target)
    {
        TryRefreshStrangerLiveTargetKnowledge(target);
        Vector3 pursuitDestination = GetPursuitDestination(target);
        bool canUseLiveHuntTarget = CanUseLiveHuntTarget(target);
        bool pursuingAssignedStrangerTarget = IsAssignedStrangerPursuitTarget(target);
        bool pursuingLiveTarget = canUseLiveHuntTarget;
        Vector3 navigationTargetPosition = pursuingLiveTarget
            ? target.transform.position
            : pursuitDestination;
        float targetPathDistance = Vector3.Distance(transform.position, pursuitDestination);
        bool deferredToAsyncPath = false;
        bool hasCompleteTargetPath = IsLivingPlayer(target) &&
            TryGetPursuitPathDistance(target, navigationTargetPosition, out targetPathDistance, out deferredToAsyncPath);
        bool hasDirectEngagementLine = canUseLiveHuntTarget && HasClearLungePathTo(target.transform.position);
        if (pursuingLiveTarget)
        {
            _lastKnownPursuitPosition = target.transform.position;
            _hasLastKnownPursuitPosition = true;
        }
        else if (ShouldUseStrangerSnapshotDestination(target))
        {
            _lastKnownPursuitPosition = pursuitDestination;
            _hasLastKnownPursuitPosition = true;
        }

        bool postBasicAttackCalm = ShouldForcePostBasicAttackWalk(target);
        if (postBasicAttackCalm)
            EnforcePostBasicAttackCalmWindow();

        if (pursuingLiveTarget &&
            hasCompleteTargetPath &&
            ShouldUseVanillaTargetChase(target, targetPathDistance, hasDirectEngagementLine))
        {
            MoveTowardTargetPlayerViaVanillaChase(target, targetPathDistance);
            return;
        }

        Vector3 destination = pursuitDestination;
        bool allowCombatManeuver = UsesCombatManeuvers &&
            pursuingLiveTarget &&
            !IsActionLocked() &&
            !postBasicAttackCalm &&
            hasCompleteTargetPath &&
            hasDirectEngagementLine &&
            targetPathDistance <= Mathf.Max(NavigationCombatOverlayMaxPathDistance, GetBasicAttackStartRange(BasicAttackRange) + 2f);
        if (allowCombatManeuver)
            destination = ApplyCombatManeuver(target, destination);
        PlayerControllerB navigationTarget = pursuingLiveTarget ? target : null;
        PursuitResolutionKind resolution = ResolvePursuitDestination(
            target,
            navigationTarget,
            ref pursuitDestination,
            navigationTargetPosition,
            pursuingAssignedStrangerTarget,
            hasCompleteTargetPath,
            deferredToAsyncPath,
            destination,
            out destination,
            out string navigationMode,
            out string navigationReason);
        _lastPursuitResolution = resolution;
        _lastPursuitResolutionAt = Time.time;
        if (resolution != PursuitResolutionKind.HoldSmartPending)
            ClearSmartPathHoldClock();

        switch (resolution)
        {
            case PursuitResolutionKind.HoldThreshold:
                HoldAgentWithoutReset();
                SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
                return;
            case PursuitResolutionKind.HoldSmartPending:
                HoldAssignedStrangerSmartPathPending(target);
                return;
            case PursuitResolutionKind.HoldFallbackPending:
                HoldAgentWithoutReset();
                return;
            case PursuitResolutionKind.Blocked:
                // #317: while the entrance fallback owns movement it records its own destination every
                // tick. Re-asserting "blocked" here first would make the authority record alternate
                // between the two sources forever, one [BundyMoveAuth] line per source per interval.
                if (!_blockedEntranceFallbackActive)
                    RecordPursuitDestination(pursuitDestination, navigationMode, navigationReason);
                // #289 V2: the pursuit-search arm is no longer HUNT-only -- it never needed live
                // tracking (it anchors on _lastKnownPursuitPosition and uses `target` only to pick the
                // AI-node set), and gating it on pursuingLiveTarget meant a Blocked resolution outside
                // HUNT fell straight through to StopMovement/Idle: a silent, indefinite freeze.
                //
                // TRACK only, though. Extending it to SEARCH looked harmless and is not: the vanilla
                // node sweep is a PursuitSearchWidth (55m) roam that issues its own destinations every
                // tick, so it would (a) blow the deaf window's coverage open from a ~10m annulus to a
                // 55m sweep around the estimate, and (b) keep agent.hasPath true every tick, which is
                // the exact condition the #286 roam watchdog tests for -- it would silently disable the
                // one mechanism that owns recovery in SEARCH. So a Blocked resolution there is left to
                // that watchdog, with the warning below for attribution. PING keeps the pre-existing
                // exclusion for the same reason.
                bool searchArmEligible = pursuingLiveTarget ||
                    (_strangerTargetingActive &&
                     IsAssignedStrangerPursuitTarget(target) &&
                     _strangerPerceptionState == StrangerPerceptionState.Track);
                if (searchArmEligible && StartOrContinuePursuitSearch(target, pursuitDestination, navigationReason))
                    return;

                if (pursuingLiveTarget && TryHandleBlockedPursuitTarget(target, pursuitDestination))
                    return;

                HandleUnrecoverableBlockedPursuit(target, pursuitDestination, navigationReason);
                return;
        }

        float distanceToTarget = hasCompleteTargetPath ? targetPathDistance : Vector3.Distance(transform.position, pursuitDestination);
        float distanceToDestination = Vector3.Distance(transform.position, destination);

        // While maneuvering (circle/retreat), stop only when he reaches his MANEUVER destination — not
        // when he's merely close to the player. Otherwise the lunge leaves him inside StopDistance and he
        // freezes there instead of backing off/circling between attacks.
        bool maneuvering = allowCombatManeuver;
        float stopReference = maneuvering || !pursuingLiveTarget ? distanceToDestination : distanceToTarget;
        if (stopReference <= StopDistance)
        {
            // Job B3 hysteresis: a maneuver arrival is not a pursuit stop. StopMovement() zeroes the
            // locomotion-intent hold as well as the path, so ApplyMotionStateFromMovement demotes to
            // Idle in the same frame and the next tick re-issues the maneuver — the Walk->Idle->Walk
            // flicker that reads as darting. Settle onto the arrived point instead and keep the
            // locomotion state: the agent is deliberately NOT isStopped (that would trip the
            // post-action watchdog bug signal and would also strand him if a stale path is still
            // queued), it simply has nowhere further to go. ChainsawVelocityGatedIdleDemotion still
            // demotes him on observed velocity once he has genuinely been standing still, which is the
            // demotion that belongs here.
            // The stopReference split above is untouched — it is what keeps the lunge from leaving him
            // frozen inside StopDistance instead of backing off/circling.
            if (maneuvering && CombatManeuverHoldsMotionOnArrival(_currentCombatBehavior))
            {
                if (agent != null && agent.enabled && agent.isOnNavMesh)
                {
                    agent.isStopped = false;
                    movingTowardsTargetPlayer = false;
                    moveTowardsDestination = true;
                    RecordPursuitDestination(destination, navigationMode, navigationReason);
                    SetDestinationToPosition(destination);
                    SetLocalBehaviourState(BehaviourStateTracking);
                }

                HoldLocomotionIntent(CombatManeuverArrivalHoldSeconds);
                return;
            }

            StopMovement();
            SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
            return;
        }

        bool running = ShouldRun(target);
        ApplyGaitSpeed(running);
        agent.angularSpeed = AgentAngularSpeedDegrees;
        agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);
        if (!UsesManualTargetFacing)
            agent.updateRotation = !ManualPursuitControlsAgentRotation;
        if (agent.isOnNavMesh)
            agent.isStopped = false;

        StopPursuitSearchIfActive();
        movingTowardsTargetPlayer = false;
        moveTowardsDestination = true;
        RecordPursuitDestination(destination, navigationMode, navigationReason);
        SetDestinationToPosition(destination);
        HoldLocomotionIntent(0.4f);
        SetLocalBehaviourState(BehaviourStateTracking);
        SetMotionStateIfChanged(running ? BundyAnimatorParams.MotionRun : BundyAnimatorParams.MotionWalk);
        if (pursuingLiveTarget)
            TryPlayPursuitTurnClip(target, distanceToTarget, allowMovementDirectionFallback: !UsesManualTargetFacing);

        if (DebugLogs && Time.time >= _nextCombatDebugAt)
        {
            _nextCombatDebugAt = Time.time + 1f;
            Debug.DrawLine(transform.position + Vector3.up * 0.15f, destination + Vector3.up * 0.15f, Color.cyan, 0.3f, false);
            LogDiagnostic(
                BundyLogCategory.Movement,
                1,
                $"BundyCombatDest behavior={_currentCombatBehavior}, prev={_lastCombatBehavior}, " +
                $"atkCd={Mathf.Max(0f, _nextAttackAllowedAt - Time.time):F2}s, " +
                $"mvrWin={Mathf.Max(0f, _nextCombatBehaviorChangeAt - Time.time):F2}s, target={PlayerLabel(target)}, " +
                $"dest={FormatVector(destination)}, nav={navigationMode}, distTarget={distanceToTarget:F2}, distDest={distanceToDestination:F2}");
        }
    }

    private void HoldAssignedStrangerSmartPathPending(PlayerControllerB target)
    {
        LogStrangerBoundaryRoute(target, "smart-pending", target != null ? target.transform.position : transform.position);
        if (!AgentReadyForMovement())
            return;

        // pathPending counts as having a path. Unity resolves a long SetDestination over one or more
        // frames, and during that window hasPath is false — so on a >40m pursuit, where the smart
        // task is in flight most ticks, the old test caught the agent mid-computation and dropped it
        // into the branch below. That branch called StopMovement(), which calls agent.ResetPath():
        // the hold whose entire purpose is to PRESERVE the current path was throwing it away, one
        // tick after it was issued, for as long as the target stayed far. He never closed distance.
        if (agent.hasPath || agent.pathPending)
        {
            bool running = ShouldRun(target);
            ApplyGaitSpeed(running);
            agent.angularSpeed = AgentAngularSpeedDegrees;
            agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);
            if (!UsesManualTargetFacing)
                agent.updateRotation = !ManualPursuitControlsAgentRotation;
            if (agent.isOnNavMesh)
                agent.isStopped = false;

            movingTowardsTargetPlayer = false;
            moveTowardsDestination = true;
            HoldLocomotionIntent(0.25f);
            SetLocalBehaviourState(BehaviourStateTracking);
            SetMotionStateIfChanged(running ? BundyAnimatorParams.MotionRun : BundyAnimatorParams.MotionWalk);
            return;
        }

        // Genuinely nothing queued. Hold WITHOUT resetting: there is no path to protect here, but
        // ResetPath also clears the destination the next tick would otherwise still be holding, and
        // a wait is not a stop.
        HoldAgentWithoutReset();
        if (IsLivingPlayer(target))
            FaceActionPoint(target.transform.position);
        SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
    }

    private void LogStrangerBoundaryRoute(PlayerControllerB target, string mode, Vector3 destination)
    {
        if (Time.time < _nextStrangerBoundaryRouteLogAt)
            return;

        _nextStrangerBoundaryRouteLogAt = Time.time + StrangerBoundaryRouteLogIntervalSeconds;
        LogInfo(
            $"Stranger boundary pursuit: target={PlayerLabel(target)}, bundyOutside={isOutside}, " +
            $"targetInside={(target != null && target.isInsideFactory)}, mode={mode}, " +
            $"destination={FormatVector(destination)}.");
    }

    private bool TryRouteAssignedStrangerBoundaryEntrance(PlayerControllerB target, out Vector3 destination)
    {
        destination = Vector3.zero;
        if (!IsAssignedStrangerPursuitTarget(target) || !AgentReadyForMovement())
            return false;

        if (!TryFindAssignedStrangerBoundaryEntrance(
                target,
                out EntranceTeleport entrance,
                out Vector3 entranceDestination,
                out Vector3 exitPosition,
                out string direction,
                out string approachMode))
        {
            return false;
        }

        destination = entranceDestination;
        Vector3 entrancePoint = GetEntrancePoint(entrance);
        float entranceDistance = PlanarDistance(transform.position, entrancePoint);
        float approachDistance = PlanarDistance(transform.position, entranceDestination);
        bool reachedApproach = IsAssignedStrangerEntranceApproachReached(entranceDestination);
        bool stalledNearEntrance = IsAssignedStrangerEntranceApproachStalled(entrancePoint, entranceDestination, reachedApproach);
        if (Time.time >= _nextAssignedStrangerEntranceLogAt)
        {
            _nextAssignedStrangerEntranceLogAt = Time.time + StrangerBoundaryRouteLogIntervalSeconds;
            LogInfo(
                $"Assigned Stranger crossing route: direction={direction}, entranceId={entrance.entranceId}, " +
                $"entrance={FormatVector(entrancePoint)}, approach={FormatVector(entranceDestination)}, " +
                $"approachMode={approachMode}, exit={FormatVector(exitPosition)}, " +
                $"entranceDistance={entranceDistance:F1}m, approachDistance={approachDistance:F1}m, " +
                $"reachedApproach={reachedApproach}, stalledNearEntrance={stalledNearEntrance}, " +
                $"resolve={(_lastCrossingRouteFromCache ? "cached" : "fresh")}, target={PlayerLabel(target)}.");
        }

        if (ShouldUseAssignedStrangerEntrance(entranceDistance, reachedApproach, stalledNearEntrance))
        {
            if (TryHandleAssignedStrangerEntranceThreshold(
                    entrance,
                    entrancePoint,
                    entranceDestination,
                    entranceDistance,
                    reachedApproach,
                    stalledNearEntrance,
                    out bool fired))
            {
                destination = fired ? transform.position : entranceDestination;
                return true;
            }

            return false;
        }

        ClearPendingSmartEntranceThreshold();
        IssueRawAgentDestination(entranceDestination, "assigned-stranger-entrance", "walking to entrance approach");
        return true;
    }

    private bool TryHandleAssignedStrangerEntranceThreshold(
        EntranceTeleport entrance,
        Vector3 entrancePoint,
        Vector3 entranceDestination,
        float initialEntranceDistance,
        bool reachedApproach,
        bool stalledNearEntrance,
        out bool fired)
    {
        fired = false;
        if (entrance == null)
            return false;

        const string reason = "assigned stranger crossing";
        if (_pendingSmartEntrance != entrance)
        {
            _pendingSmartEntrance = entrance;
            _pendingSmartEntrancePoint = entrancePoint;
            _pendingSmartEntranceApproach = entranceDestination;
            _pendingSmartEntranceReason = reason;
            _pendingSmartEntranceStartedAt = Time.time;
            _pendingSmartEntrancePauseUntil = 0f;
            _pendingSmartEntranceDeadline = Time.time + SmartEntranceThresholdFallbackSeconds;
            _nextPendingSmartEntranceApproachAt = 0f;
            _pendingSmartEntranceHoldingAtThreshold = false;
            LogInfo(
                $"Assigned Stranger crossing threshold-pause start: entranceId={entrance.entranceId}, " +
                $"entranceDistance={initialEntranceDistance:F1}m, reachedApproach={reachedApproach}, " +
                $"stalledNearEntrance={stalledNearEntrance}, approach={FormatVector(entranceDestination)}, " +
                $"fallback={SmartEntranceThresholdFallbackSeconds:F1}s.");
        }

        float entranceDistance = PlanarDistance(transform.position, _pendingSmartEntrancePoint);
        bool deadlineReached = Time.time >= _pendingSmartEntranceDeadline;
        if (!_pendingSmartEntranceHoldingAtThreshold && !deadlineReached)
        {
            if (entranceDistance > SmartEntranceThresholdCloseDistance)
            {
                if (Time.time >= _nextPendingSmartEntranceApproachAt)
                {
                    _nextPendingSmartEntranceApproachAt = Time.time + SmartEntranceThresholdApproachRefreshSeconds;
                    if (TryResolveSmartEntranceThresholdApproach(_pendingSmartEntrancePoint, entranceDestination, out Vector3 approachPoint))
                    {
                        _pendingSmartEntranceApproach = approachPoint;
                        IssueRawAgentDestination(approachPoint, "assigned-stranger-entrance", "closing to entrance threshold");
                        LogInfo(
                            $"Assigned Stranger crossing closing to threshold: entranceId={entrance.entranceId}, " +
                            $"distance={entranceDistance:F1}m, approach={FormatVector(approachPoint)}.");
                        return true;
                    }

                    BeginSmartEntranceThresholdHold(entranceDistance, "no reachable close approach");
                    return true;
                }

                return true;
            }

            BeginSmartEntranceThresholdHold(entranceDistance, "threshold reached");
            return true;
        }

        if (_pendingSmartEntranceHoldingAtThreshold && !deadlineReached && Time.time < _pendingSmartEntrancePauseUntil)
        {
            HoldAgentWithoutReset();
            FaceActionPoint(_pendingSmartEntrancePoint);
            return true;
        }

        EntranceTeleport entranceToUse = _pendingSmartEntrance;
        string pendingReason = _pendingSmartEntranceReason ?? reason;
        Vector3 approach = _pendingSmartEntranceApproach;
        float elapsed = Time.time - _pendingSmartEntranceStartedAt;
        ClearPendingSmartEntranceThreshold();
        LogInfo(
            $"Assigned Stranger crossing teleport fire: entranceId={(entranceToUse != null ? entranceToUse.entranceId : -1)}, " +
            $"elapsed={elapsed:F2}s, deadlineReached={deadlineReached}, approach={FormatVector(approach)}.");
        fired = TryUseSmartEntranceTeleport(entranceToUse, pendingReason);
        return fired;
    }

    private bool TryResolveSmartEntranceThresholdApproach(Vector3 entrancePoint, Vector3 fallbackApproach, out Vector3 approachPoint)
    {
        approachPoint = fallbackApproach;
        if (!AgentReadyForMovement())
            return false;

        if (NavMesh.SamplePosition(entrancePoint, out NavMeshHit sample, AssignedStrangerEntranceRouteSampleDistance, NavMesh.AllAreas) &&
            TryCalculateCompletePath(sample.position, out _))
        {
            approachPoint = sample.position;
            return true;
        }

        if (TryCalculateCompletePath(fallbackApproach, out _))
        {
            approachPoint = fallbackApproach;
            return true;
        }

        return false;
    }

    private void BeginSmartEntranceThresholdHold(float distance, string mode)
    {
        _pendingSmartEntranceHoldingAtThreshold = true;
        _pendingSmartEntrancePauseUntil = Time.time + SmartEntranceThresholdPauseSeconds;
        HoldAgentWithoutReset();
        FaceActionPoint(_pendingSmartEntrancePoint);
        PauseForSmartInteraction(SmartEntranceThresholdPauseSeconds);
        LogInfo(
            $"Assigned Stranger crossing holding threshold: entranceId={(_pendingSmartEntrance != null ? _pendingSmartEntrance.entranceId : -1)}, " +
            $"mode={mode}, distance={distance:F1}m, hold={SmartEntranceThresholdPauseSeconds:F2}s.");
    }

    private bool IsSmartEntranceTransitionHolding()
    {
        return (_pendingSmartEntranceHoldingAtThreshold && Time.time < _pendingSmartEntrancePauseUntil) ||
            (_smartEntranceSettleLogPending && Time.time < _smartEntranceSettleUntil);
    }

    private void ClearPendingSmartEntranceThreshold()
    {
        _pendingSmartEntrance = null;
        _pendingSmartEntrancePoint = Vector3.zero;
        _pendingSmartEntranceApproach = Vector3.zero;
        _pendingSmartEntranceReason = null;
        _pendingSmartEntranceStartedAt = 0f;
        _pendingSmartEntrancePauseUntil = 0f;
        _pendingSmartEntranceDeadline = 0f;
        _nextPendingSmartEntranceApproachAt = 0f;
        _pendingSmartEntranceHoldingAtThreshold = false;
    }

    private bool TryFindAssignedStrangerBoundaryEntrance(
        PlayerControllerB target,
        out EntranceTeleport entrance,
        out Vector3 routeDestination,
        out Vector3 exitPosition,
        out string direction,
        out string approachMode)
    {
        entrance = null;
        routeDestination = Vector3.zero;
        exitPosition = Vector3.zero;
        direction = "none";
        approachMode = "none";
        _lastCrossingRouteFromCache = false;
        if (!IsLivingPlayer(target))
            return false;

        bool wantEnterBuilding = isOutside && target.isInsideFactory;
        bool wantExitBuilding = !isOutside && !target.isInsideFactory;
        if (!wantEnterBuilding && !wantExitBuilding)
            return false;

        direction = wantEnterBuilding ? "outside-to-inside" : "inside-to-outside";

        // Crossing-route cache: the resolution is deterministic in (entrance set, his side, target
        // side), and re-resolving it every AI interval costs a FindObjectsOfType plus a ring of
        // synchronous CalculatePath scans for an answer that does not change mid-approach. Sides
        // changing (his or the target's) flips wantEnterBuilding and misses the cache naturally.
        bool cacheEnabled = CrossingRouteCacheSeconds > 0f;
        if (cacheEnabled &&
            _crossingRouteCacheValid &&
            Time.time <= _crossingRouteCacheUntil &&
            _crossingRouteCacheWantEnter == wantEnterBuilding &&
            (!_crossingRouteCacheSucceeded || _crossingRouteCacheEntrance != null))
        {
            _lastCrossingRouteFromCache = true;
            if (!_crossingRouteCacheSucceeded)
                return false;

            entrance = _crossingRouteCacheEntrance;
            routeDestination = _crossingRouteCacheDestination;
            exitPosition = _crossingRouteCacheExit;
            approachMode = _crossingRouteCacheApproachMode;
            return true;
        }

        bool resolved = TryResolveAssignedStrangerBoundaryEntranceFresh(
            target,
            wantEnterBuilding,
            out entrance,
            out routeDestination,
            out exitPosition,
            out approachMode);

        _crossingRouteCacheValid = cacheEnabled;
        _crossingRouteCacheSucceeded = resolved;
        _crossingRouteCacheWantEnter = wantEnterBuilding;
        _crossingRouteCacheUntil = Time.time + CrossingRouteCacheSeconds;
        _crossingRouteCacheEntrance = resolved ? entrance : null;
        _crossingRouteCacheDestination = routeDestination;
        _crossingRouteCacheExit = exitPosition;
        _crossingRouteCacheApproachMode = approachMode;
        return resolved;
    }

    // #300: FindObjectsOfType<EntranceTeleport> measured 22-38ms per call in a heavily modded scene and
    // sat on both the perception path (exit lurk) and the crossing-route resolver. Vanilla never spawns
    // or destroys an EntranceTeleport mid-round -- they come with the dungeon and live until the level
    // unloads -- so the set is resolved once and re-resolved on a long interval. The interval is the
    // only cover for a mod that adds an entrance after the dungeon finishes generating; level changes,
    // scene unloads and destroyed entrances invalidate immediately via the level-id and null checks.
    // Static because the set is per-scene, not per-Bundy: two Bundys share one scan.
    private const float EntranceTeleportCacheSeconds = 10f;
    private static EntranceTeleport[] _cachedEntranceTeleports;
    private static float _entranceTeleportCacheResolvedAt = float.NegativeInfinity;
    private static int _entranceTeleportCacheLevelId = int.MinValue;

    private static EntranceTeleport[] GetCachedEntranceTeleports()
    {
        int levelId = StartOfRound.Instance != null ? StartOfRound.Instance.currentLevelID : int.MinValue;
        float now = Time.time;
        if (_cachedEntranceTeleports != null &&
            _entranceTeleportCacheLevelId == levelId &&
            now >= _entranceTeleportCacheResolvedAt &&
            now - _entranceTeleportCacheResolvedAt < EntranceTeleportCacheSeconds &&
            !CachedEntranceTeleportsStale())
        {
            return _cachedEntranceTeleports;
        }

        _cachedEntranceTeleports = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>();
        _entranceTeleportCacheLevelId = levelId;
        _entranceTeleportCacheResolvedAt = now;
        return _cachedEntranceTeleports;
    }

    // A destroyed EntranceTeleport compares equal to null through Unity's overload, which is exactly the
    // signal a scene unload leaves behind -- cheaper and more reliable than hooking round lifecycle.
    private static bool CachedEntranceTeleportsStale()
    {
        EntranceTeleport[] cached = _cachedEntranceTeleports;
        for (int i = 0; i < cached.Length; i++)
        {
            if (cached[i] == null)
                return true;
        }

        return false;
    }

    private bool TryResolveAssignedStrangerBoundaryEntranceFresh(
        PlayerControllerB target,
        bool wantEnterBuilding,
        out EntranceTeleport entrance,
        out Vector3 routeDestination,
        out Vector3 exitPosition,
        out string approachMode)
    {
        entrance = null;
        routeDestination = Vector3.zero;
        exitPosition = Vector3.zero;
        approachMode = "none";
        // #317: the resolve had no voice in the field -- every rejection was silent, and the only
        // surviving evidence of 166s of failure was "blocked assigned Stranger boundary pursuit"
        // repeating. Reset the per-candidate rejection tally here; nothing formats it unless the
        // throttled stall warning actually prints.
        _entranceRouteResolveRan = true;
        _entranceRouteCandidates = 0;
        _entranceRouteDirectionMatched = 0;
        _entranceRouteNoExitScript = 0;
        _entranceRouteNoApproach = 0;
        _entranceRouteNoCompletePath = 0;
        EntranceTeleport[] entrances = GetCachedEntranceTeleports();
        if (entrances == null || entrances.Length == 0)
            return false;

        _entranceRouteCandidates = entrances.Length;
        for (int pass = 0; pass < 2; pass++)
        {
            bool wantMainEntrance = pass == 0;
            float bestScore = float.PositiveInfinity;
            EntranceTeleport bestEntrance = null;
            Vector3 bestRouteDestination = Vector3.zero;
            Vector3 bestExitPosition = Vector3.zero;
            string bestApproachMode = "none";

            foreach (EntranceTeleport candidate in entrances)
            {
                if (candidate == null || candidate.isEntranceToBuilding != wantEnterBuilding)
                    continue;
                if (wantMainEntrance && candidate.entranceId != 0)
                    continue;
                if (!wantMainEntrance && candidate.entranceId == 0)
                    continue;
                if (!TryScoreAssignedStrangerEntrance(candidate, target, wantEnterBuilding, out Vector3 candidateRoute, out Vector3 candidateExit, out float score, out string candidateApproachMode))
                    continue;

                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestEntrance = candidate;
                bestRouteDestination = candidateRoute;
                bestExitPosition = candidateExit;
                bestApproachMode = candidateApproachMode;
            }

            if (bestEntrance == null)
                continue;

            entrance = bestEntrance;
            routeDestination = bestRouteDestination;
            exitPosition = bestExitPosition;
            approachMode = bestApproachMode;
            return true;
        }

        return false;
    }

    private bool TryScoreAssignedStrangerEntrance(
        EntranceTeleport entrance,
        PlayerControllerB target,
        bool wantEnterBuilding,
        out Vector3 routeDestination,
        out Vector3 exitPosition,
        out float score,
        out string approachMode)
    {
        routeDestination = Vector3.zero;
        exitPosition = Vector3.zero;
        score = float.PositiveInfinity;
        approachMode = "none";
        if (entrance == null || !IsLivingPlayer(target))
            return false;

        bool directionMatches = entrance.isEntranceToBuilding == wantEnterBuilding;
        if (!directionMatches)
            return false;

        // #317: the caller already filtered on direction, so reaching here is what "a candidate the
        // router was willing to consider" means -- the denominator the rejection counters divide.
        _entranceRouteDirectionMatched++;

        if (entrance.exitScript == null && !entrance.FindExitPoint())
        {
            _entranceRouteNoExitScript++;
            return false;
        }

        EntranceTeleport exit = entrance.exitScript;
        if (exit == null)
        {
            _entranceRouteNoExitScript++;
            return false;
        }

        Vector3 entrancePoint = GetEntrancePoint(entrance);
        if (!TryResolveAssignedStrangerEntranceApproach(entrancePoint, target, out routeDestination, out approachMode))
        {
            _entranceRouteNoApproach++;
            return false;
        }

        if (!TryCalculateCompletePath(routeDestination, out NavMeshPath path))
        {
            _entranceRouteNoCompletePath++;
            return false;
        }

        exitPosition = GetEntrancePoint(exit);
        float pathLength = EstimatePathLength(path);
        float routeDistance = PlanarDistance(transform.position, routeDestination);
        float exitToTarget = PlanarDistance(exitPosition, target.transform.position);
        score = wantEnterBuilding
            ? exitToTarget + pathLength * 0.08f
            : routeDistance * 0.75f + exitToTarget * 0.35f + pathLength * 0.08f;
        return true;
    }

    private bool TryResolveAssignedStrangerEntranceApproach(Vector3 entrancePoint, PlayerControllerB target, out Vector3 routeDestination, out string approachMode)
    {
        routeDestination = Vector3.zero;
        approachMode = "none";
        if (!AgentReadyForMovement())
            return false;

        float[] radii = { 0f, 2.5f, 5f, 8f, 12f, 16f, 22f, AssignedStrangerEntranceApproachSearchRadius };
        int[] angleStepsByRadius = { 1, 8, 12, 16, 16, 20, 24, 28 };
        float bestCost = float.PositiveInfinity;
        bool found = false;

        // #497: 125 candidates, each a NavMesh sample plus a synchronous CalculatePath, ran to
        // completion in one frame and showed up as 28-46ms DoAIInterval spikes. Unlike the pursuit
        // fallback this solver cannot be made resumable without restructuring both of its callers --
        // each wants an answer inside the decision it is already making -- so it gets the other half of
        // the same treatment: the same 2ms slice, spent best-first.
        //
        // Truncation degrades gracefully because the sweep is ordered outward from the door and cost is
        // dominated by entranceDistance at weight 2.1: the candidates that would win are the ones
        // evaluated first. An early stop can only lose a distant candidate that a nearer one already
        // beats, and if the budget expires before ANYTHING resolves the caller sees the same "no
        // approach" it already handles.
        long approachSweepStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        bool approachBudgetExpired = false;

        for (int r = 0; r < radii.Length && !approachBudgetExpired; r++)
        {
            float radius = radii[r];
            int angleSteps = angleStepsByRadius[Mathf.Min(r, angleStepsByRadius.Length - 1)];
            for (int a = 0; a < angleSteps; a++)
            {
                if (EntranceApproachBudgetExpired(approachSweepStarted))
                {
                    approachBudgetExpired = true;
                    break;
                }

                float angle = angleSteps <= 1 ? 0f : (360f / angleSteps) * a;
                Vector3 offset = angleSteps <= 1
                    ? Vector3.zero
                    : Quaternion.Euler(0f, angle, 0f) * Vector3.forward * radius;
                Vector3 candidate = entrancePoint + offset;
                float sampleDistance = Mathf.Max(AssignedStrangerEntranceRouteSampleDistance, radius <= 0.01f ? 4f : radius * 0.35f);
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit sample, sampleDistance, NavMesh.AllAreas))
                    continue;

                float entranceDistance = PlanarDistance(sample.position, entrancePoint);
                if (entranceDistance > AssignedStrangerEntranceApproachSearchRadius + 2f)
                    continue;

                if (!TryCalculateCompletePath(sample.position, out NavMeshPath path))
                    continue;

                float pathLength = EstimatePathLength(path);
                float selfDistance = PlanarDistance(transform.position, sample.position);
                float targetHeightCost = target != null ? Mathf.Abs(sample.position.y - target.transform.position.y) * 0.05f : 0f;
                float entranceHeightCost = Mathf.Abs(sample.position.y - entrancePoint.y) * 0.25f;
                float cost = entranceDistance * 2.1f + pathLength * 0.12f + selfDistance * 0.03f + entranceHeightCost + targetHeightCost;
                if (cost >= bestCost)
                    continue;

                bestCost = cost;
                routeDestination = sample.position;
                approachMode = $"r{radius:F1}/a{angle:F0}/door{entranceDistance:F1}/path{pathLength:F1}";
                found = true;
            }

        }

        if (approachBudgetExpired && found)
            approachMode += "/budgeted";

        return found;
    }

    // #497: the slice for one entrance-approach sweep. Deliberately the same 2ms the pursuit fallback
    // and the stuck-recovery sweeps already spend per AI interval, because this solver runs at most
    // once per decision and never alongside them in the same tick.
    private const double EntranceApproachMillisecondsPerSweep = 2d;

    private static bool EntranceApproachBudgetExpired(long started)
    {
        double elapsedMilliseconds =
            (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d /
            System.Diagnostics.Stopwatch.Frequency;
        return elapsedMilliseconds >= EntranceApproachMillisecondsPerSweep;
    }

    private bool ShouldUseAssignedStrangerEntrance(float entranceDistance, bool reachedApproach, bool stalledNearEntrance)
    {
        if (entranceDistance <= AssignedStrangerEntranceForceUseDistance)
            return true;

        if (entranceDistance <= AssignedStrangerEntranceReachedUseDistance && reachedApproach)
            return true;

        return stalledNearEntrance;
    }

    private bool IsAssignedStrangerEntranceApproachReached(Vector3 routeDestination)
    {
        if (PlanarDistance(transform.position, routeDestination) <= AssignedStrangerEntranceReachedDestinationDistance)
            return true;

        if (agent == null || !agent.enabled || !agent.isOnNavMesh || agent.pathPending)
            return false;

        return agent.remainingDistance <= AssignedStrangerEntranceReachedDestinationDistance;
    }

    private bool IsAssignedStrangerEntranceApproachStalled(Vector3 entrancePoint, Vector3 routeDestination, bool reachedApproach)
    {
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return false;

        float entranceDistance = PlanarDistance(transform.position, entrancePoint);
        if (entranceDistance > AssignedStrangerEntranceStalledUseDistance)
            return false;

        float velocity = agent.velocity.magnitude;
        bool lowVelocity = velocity <= AssignedStrangerEntranceStalledVelocity;
        bool pathTroubled = !agent.pathPending &&
            (agent.pathStatus == NavMeshPathStatus.PathInvalid || agent.pathStatus == NavMeshPathStatus.PathPartial);
        bool closeToApproach = PlanarDistance(transform.position, routeDestination) <=
            Mathf.Max(AssignedStrangerEntranceReachedDestinationDistance, StopDistance + 0.75f);
        bool movementWatchdogStalled = _hasLastMovementProgress &&
            Time.time - _lastMovementProgressAt >= MovementStuckCheckSeconds;

        return lowVelocity && (reachedApproach || closeToApproach || pathTroubled || movementWatchdogStalled);
    }

    private bool ShouldUseVanillaTargetChase(PlayerControllerB target, float pathDistance, bool hasDirectEngagementLine)
    {
        if (!IsLivingPlayer(target) || IsActionLocked())
            return false;

        if (!UsesCombatManeuvers)
            return true;

        float combatOverlayRange = Mathf.Max(
            NavigationCombatOverlayMaxPathDistance,
            GetBasicAttackStartRange(BasicAttackRange) + 2f);
        return pathDistance > combatOverlayRange || !hasDirectEngagementLine;
    }

    private void MoveTowardTargetPlayerViaVanillaChase(PlayerControllerB target, float pathDistance)
    {
        if (!IsLivingPlayer(target) || !AgentReadyForMovement())
            return;

        StopPursuitSearchIfActive();
        ClearBlockedPursuitTarget(target);

        bool running = !ShouldForcePostBasicAttackWalk(target) && ShouldRunAtDistance(target, pathDistance);
        ApplyGaitSpeed(running);
        agent.stoppingDistance = StopDistance;
        agent.angularSpeed = AgentAngularSpeedDegrees;
        agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);
        agent.isStopped = false;
        if (!UsesManualTargetFacing)
            agent.updateRotation = !ManualPursuitControlsAgentRotation;
        else
            agent.updateRotation = false;

        targetPlayer = target;
        moveTowardsDestination = true;
        RecordPursuitDestination(target.transform.position, "vanilla-target-chase", $"live chase path {pathDistance:F1}m");
        SetMovingTowardsTargetPlayer(target);
        NavigateTowardsTargetPlayer();
        HoldLocomotionIntent(0.4f);
        SetLocalBehaviourState(BehaviourStateTracking);
        SetMotionStateIfChanged(running ? BundyAnimatorParams.MotionRun : BundyAnimatorParams.MotionWalk);
        TryPlayPursuitTurnClip(target, pathDistance, allowMovementDirectionFallback: !UsesManualTargetFacing);

        if (DebugLogs && Time.time >= _nextCombatDebugAt)
        {
            _nextCombatDebugAt = Time.time + 1f;
            LogDiagnostic(
                BundyLogCategory.Movement,
                1,
                $"BundyCombatDest behavior={_currentCombatBehavior}, prev={_lastCombatBehavior}, " +
                $"atkCd={Mathf.Max(0f, _nextAttackAllowedAt - Time.time):F2}s, " +
                $"mvrWin={Mathf.Max(0f, _nextCombatBehaviorChangeAt - Time.time):F2}s, target={PlayerLabel(target)}, " +
                $"dest={FormatVector(destination)}, nav=vanilla-target-chase, distTarget={pathDistance:F2}, " +
                $"distDest={Vector3.Distance(transform.position, destination):F2}");
        }
    }

    private bool StartOrContinuePursuitSearch(PlayerControllerB target, Vector3 desiredStart, string reason)
    {
        if (!IsLivingPlayer(target) || !AgentReadyForMovement())
            return false;
        if (Time.time < _nextPursuitSearchAllowedAt)
            return false;

        GetNavigationNodesForTarget(target);
        if (allAINodes == null || allAINodes.Length == 0)
            return false;

        Vector3 searchStart = _hasLastKnownPursuitPosition ? _lastKnownPursuitPosition : desiredStart;
        if (!HasSearchNodeCandidateNear(searchStart, PursuitSearchWidth))
        {
            _nextPursuitSearchAllowedAt = Time.time + PursuitSearchFailureCooldownSeconds;
            StopPursuitSearchIfActive();
            LogWarning(
                $"Pursuit search skipped: no AI nodes within {PursuitSearchWidth:F0}m of start; " +
                $"target={PlayerLabel(target)}, reason={reason}, start={FormatVector(searchStart)}.");
            return false;
        }

        if (_pursuitSearch.inProgress &&
            Vector3.Distance(_pursuitSearch.currentSearchStartPosition, searchStart) > NavigationSearchRestartDistance)
        {
            StopSearch(_pursuitSearch);
        }

        if (!_pursuitSearch.inProgress)
        {
            ResetPursuitSearchRoutine();
            _pursuitSearch.searchWidth = PursuitSearchWidth;
            _pursuitSearch.searchPrecision = PursuitSearchPrecision;
            _pursuitSearch.randomized = true;
            _pursuitSearch.onlySearchNodesInLOS = false;
            StartSearch(searchStart, _pursuitSearch);
            // The vanilla search coroutine issues its own per-node destinations; the authority line
            // records the handoff to it, not each roam point.
            RecordPursuitDestination(searchStart, "pursuit-search", reason);
            LogInfo($"Pursuit search started: target={PlayerLabel(target)}, reason={reason}, start={FormatVector(searchStart)}.");
        }

        bool running = !ShouldForcePostBasicAttackWalk(target) && Vector3.Distance(transform.position, searchStart) > RunThreshold;
        ApplyGaitSpeed(running);
        agent.stoppingDistance = StopDistance;
        agent.angularSpeed = AgentAngularSpeedDegrees;
        agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);
        if (!UsesManualTargetFacing)
            agent.updateRotation = !ManualPursuitControlsAgentRotation;
        else
            agent.updateRotation = false;
        if (agent.isOnNavMesh)
            agent.isStopped = false;

        targetPlayer = target;
        movingTowardsTargetPlayer = false;
        moveTowardsDestination = true;
        HoldLocomotionIntent(0.4f);
        SetLocalBehaviourState(BehaviourStateTracking);
        SetMotionStateIfChanged(running ? BundyAnimatorParams.MotionRun : BundyAnimatorParams.MotionWalk);
        return true;
    }

    // #289 V2. Last resort after BOTH recovery arms declined: the node search found nothing near his
    // estimate (or is cooling down) and the taunt/hide arm needs a live target he does not have. The
    // old code stopped here and idled, silently, for as long as the condition lasted -- and in
    // TRACK/SEARCH nothing else was going to move him, because those states are driven by arrival at
    // a destination he can no longer path to.
    //
    // Two things happen now. It reports at WARNING under the same [BundyPursuitStall] tag the
    // no-progress watchdog uses, so a field log attributes the freeze instead of showing a gap; and
    // for the estimate-driven states it re-opens the ping clock, which re-anchors him on a fresh
    // estimate and gives the destination resolver a different point to path to. PING is deliberately
    // excluded: it is already re-pinging on its own schedule, and forcing an extra ping every AI
    // interval while blocked would hand him a free position update several times a second.
    private const float BlockedPursuitRecoveryLogIntervalSeconds = 10f;
    private const float BlockedPursuitRecoveryPingIntervalSeconds = 6f;

    private void HandleUnrecoverableBlockedPursuit(
        PlayerControllerB target,
        Vector3 pursuitDestination,
        string navigationReason)
    {
        // #317: a cross-boundary block is never genuinely unrecoverable -- the boundary has doors, and
        // walking to one is always progress. Try that BEFORE the stop below, which is what left him
        // idling outdoors for 166s on Etern while the primary router silently rejected every entrance.
        // Only the boundary condition ending retires the fallback here. A transient decline (action
        // lock, agent not yet on the mesh) must not, or the engagement would be torn down and re-logged
        // every time an attack interrupts him.
        bool targetAcrossBoundary = IsLivingPlayer(target) && isOutside == target.isInsideFactory;
        if (!targetAcrossBoundary)
            ClearBlockedEntranceFallback();
        else if (TryEngageBlockedEntranceFallback(target))
            return;

        StopMovement();
        FaceActionPoint(pursuitDestination);
        SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);

        // TRACK only. SEARCH is excluded deliberately: its own roam watchdog (#286) is what recovers a
        // dead destination there, and a re-ping from here would cut the deaf window short. PING
        // re-pings on its own schedule already.
        bool estimateDriven =
            _strangerTargetingActive &&
            IsAssignedStrangerPursuitTarget(target) &&
            _strangerPerceptionState == StrangerPerceptionState.Track;

        bool reAnchored = false;
        if (estimateDriven && Time.time >= _nextBlockedPursuitRecoveryPingAt)
        {
            _nextBlockedPursuitRecoveryPingAt = Time.time + BlockedPursuitRecoveryPingIntervalSeconds;
            BeginStrangerPings("blocked pursuit destination -- re-anchoring on a fresh ping");
            reAnchored = true;
        }

        if (!reAnchored && Time.time < _nextBlockedPursuitRecoveryLogAt)
            return;

        _nextBlockedPursuitRecoveryLogAt = Time.time + BlockedPursuitRecoveryLogIntervalSeconds;
        LogWarning(
            "[BundyPursuitStall] blocked pursuit destination with no recovery available: " +
            $"target={PlayerLabel(target)}, state={_strangerPerceptionState.ToString().ToUpperInvariant()}, " +
            $"dest={FormatVector(pursuitDestination)} ({Vector3.Distance(transform.position, pursuitDestination):F1}m), " +
            $"reason={navigationReason}, live={CanUseLiveHuntTarget(target)}, " +
            $"searchCooldown={Mathf.Max(0f, _nextPursuitSearchAllowedAt - Time.time):F1}s, " +
            $"recovery={(reAnchored ? "re-pinged" : "none")}, " +
            $"entranceRoute={DescribeEntranceRouteRejections()}.");
    }

    // #317: the router's rejection tally, formatted only inside a throttled warning branch. "cache=hit"
    // means the counters describe an earlier resolve whose NEGATIVE result is still being served from
    // the crossing-route cache -- not a fresh rejection this tick.
    private string DescribeEntranceRouteRejections()
    {
        if (!_entranceRouteResolveRan)
            return "[not-attempted]";

        return $"[total={_entranceRouteCandidates} enter={_entranceRouteDirectionMatched} " +
            $"noExit={_entranceRouteNoExitScript} noApproach={_entranceRouteNoApproach} " +
            $"noPath={_entranceRouteNoCompletePath} (cache={(_lastCrossingRouteFromCache ? "hit" : "miss")})]";
    }

    // #317 guaranteed-entry fallback. The primary router only accepts an entrance it can reach by a
    // COMPLETE NavMesh path to a ring-sampled approach point; when the interior/exterior meshes are
    // split, an approach ring lands entirely off-mesh, or a lockdown blocker sits in the ring, every
    // candidate is rejected and the pursuit resolves Blocked forever. On Etern/Greenhouse that ran for
    // 166s with no recovery and no visible reason.
    //
    // This tier drops both requirements. A partial path (NavMeshPathStatus.PathPartial) still walks him
    // measurably closer to the door, and closing the last stretch is what the already-proven threshold
    // flow is for -- so the crossing mechanics are reused verbatim, never duplicated. It runs in ANY
    // perception state including PING, because "walk to the door" is correct regardless of how well he
    // knows where the target is; the only thing being asserted is which SIDE the target is on.
    //
    // It is structurally subordinate to the primary: the only caller is the unrecoverable-Blocked
    // handler, which is reached only after the primary declined this tick.
    private bool TryEngageBlockedEntranceFallback(PlayerControllerB target)
    {
        if (!IsLivingPlayer(target) || !AgentReadyForMovement() || IsActionLocked())
            return false;

        // Same side means there is no boundary to cross and nothing this tier can add.
        if (isOutside != target.isInsideFactory)
            return false;

        bool wantEnter = isOutside;
        if (!TryPickBlockedEntranceFallbackEntrance(wantEnter, out EntranceTeleport entrance, out Vector3 entrancePoint))
            return false;

        float entranceDistance = PlanarDistance(transform.position, entrancePoint);
        if (!_blockedEntranceFallbackActive || _blockedEntranceFallbackEntrance != entrance)
        {
            _blockedEntranceFallbackActive = true;
            _blockedEntranceFallbackEntrance = entrance;
            _blockedEntranceFallbackEngagedAt = Time.time;
            _nextBlockedEntranceFallbackIssueAt = 0f;
            _nextBlockedEntranceFallbackRetryAt = 0f;
            _nextBlockedEntranceFallbackLogAt = Time.time + BlockedEntranceFallbackLogIntervalSeconds;
            LogWarning(
                $"[BundyPursuitStall] entrance fallback engaged: target={PlayerLabel(target)}, " +
                $"state={_strangerPerceptionState.ToString().ToUpperInvariant()}, " +
                $"direction={(wantEnter ? "outside-to-inside" : "inside-to-outside")}, " +
                $"entranceId={entrance.entranceId}, entrance={FormatVector(entrancePoint)} ({entranceDistance:F1}m), " +
                $"pathStatus={DescribeAgentPathStatus()}, entranceRoute={DescribeEntranceRouteRejections()}.");
        }

        bool reachedEntrance = entranceDistance <= AssignedStrangerEntranceReachedDestinationDistance;
        if (ShouldUseAssignedStrangerEntrance(entranceDistance, reachedEntrance, stalledNearEntrance: false))
            return DriveBlockedEntranceFallbackCrossing(target, entrance, entrancePoint, entranceDistance, reachedEntrance);

        MaybeLogBlockedEntranceFallbackProgress(target, entrance, entranceDistance, "approaching");
        DriveBlockedEntranceFallbackApproach(target, entrancePoint);
        return true;
    }

    // Hand-off to the proven crossing flow. It owns the threshold pause, the close-in re-approach and
    // the teleport fire; a declined return means the crossing itself could not happen (a lockdown gate
    // physically in the way is the designed case), so he waits it out at the door on a modest retry
    // instead of freezing or being teleported through sealed content.
    private bool DriveBlockedEntranceFallbackCrossing(
        PlayerControllerB target,
        EntranceTeleport entrance,
        Vector3 entrancePoint,
        float entranceDistance,
        bool reachedEntrance)
    {
        if (Time.time < _nextBlockedEntranceFallbackRetryAt)
        {
            MaybeLogBlockedEntranceFallbackProgress(target, entrance, entranceDistance, "retry-hold");
            HoldAgentWithoutReset();
            FaceActionPoint(entrancePoint);
            SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
            return true;
        }

        if (TryHandleAssignedStrangerEntranceThreshold(
                entrance,
                entrancePoint,
                entrancePoint,
                entranceDistance,
                reachedEntrance,
                stalledNearEntrance: false,
                out bool fired))
        {
            if (fired)
            {
                LogWarning(
                    $"[BundyPursuitStall] entrance fallback crossed: entranceId={entrance.entranceId}, " +
                    $"held={Time.time - _blockedEntranceFallbackEngagedAt:F1}s, outside={isOutside}.");
                ClearBlockedEntranceFallback();
            }
            else
            {
                RecordPursuitDestination(
                    entrancePoint,
                    "blocked-entrance-fallback",
                    "primary entrance router failed; at entrance threshold");
            }

            return true;
        }

        _nextBlockedEntranceFallbackRetryAt = Time.time + BlockedEntranceFallbackRetrySeconds;
        MaybeLogBlockedEntranceFallbackProgress(target, entrance, entranceDistance, "crossing-declined");
        HoldAgentWithoutReset();
        FaceActionPoint(entrancePoint);
        SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
        return true;
    }

    // Destination re-issue is throttled; the gait/behaviour writes are not, so a tick that does not
    // re-issue still reads as pursuit rather than demoting him to Idle under the locomotion watchdog.
    private void DriveBlockedEntranceFallbackApproach(PlayerControllerB target, Vector3 entrancePoint)
    {
        if (Time.time >= _nextBlockedEntranceFallbackIssueAt)
        {
            _nextBlockedEntranceFallbackIssueAt = Time.time + BlockedEntranceFallbackReissueSeconds;
            Vector3 destination = entrancePoint;
            // 2026-08-06 field freeze: the agent's own areaMask, NOT AllAreas. An AllAreas sample can
            // land the destination on a polygon the agent is not allowed to plan onto, and then every
            // SetDestination resolves PathInvalid -- no path, no pending, zero displacement.
            int areaMask = agent != null && agent.enabled ? agent.areaMask : NavMesh.AllAreas;
            if (NavMesh.SamplePosition(entrancePoint, out NavMeshHit sample, AssignedStrangerEntranceRouteSampleDistance, areaMask))
                destination = sample.position;

            StopPursuitSearchIfActive();
            // Deliberately NOT TryCalculateCompletePath-gated: a partial path is the point.
            IssueRawAgentDestination(
                destination,
                "blocked-entrance-fallback",
                "primary entrance router failed; walking to entrance");

            // 2026-08-06 field freeze, the other half: IssueRawAgentDestination only writes the
            // EnemyAI.destination field; the agent pick-up happens in base.DoAIInterval, which dedupes
            // on `destination != prevDestination` (vanilla). Re-issuing the SAME entrance point after
            // the async path compute failed (or after any ResetPath) is therefore swallowed for up to
            // 15s at a time -- the 2026-08-06 13:20 run sat pathless at 117.6m for 210s exactly like
            // this. Path directly when the agent is visibly pathless: CalculatePath honours the area
            // mask and hands back a PARTIAL corridor when the entrance is unreachable, and walking
            // that corridor's end is precisely this tier's contract.
            if (agent != null && agent.enabled && agent.isOnNavMesh && !agent.pathPending && !agent.hasPath)
            {
                _blockedEntranceFallbackPath ??= new NavMeshPath();
                if (agent.CalculatePath(destination, _blockedEntranceFallbackPath) &&
                    _blockedEntranceFallbackPath.corners.Length >= 2)
                {
                    agent.SetPath(_blockedEntranceFallbackPath);
                }
                else if (Time.time >= _nextBlockedEntranceFallbackLogAt)
                {
                    _nextBlockedEntranceFallbackLogAt = Time.time + BlockedEntranceFallbackLogIntervalSeconds;
                    LogWarning(
                        "[BundyPursuitStall] entrance fallback cannot path at all: " +
                        $"destination={FormatVector(destination)}, areaMask={areaMask}, " +
                        $"agent={DescribeAgentDriveState()} -- exterior mesh likely disconnected here; " +
                        "the boundary-entry failsafe is the remaining recovery.");
                }
            }
        }

        bool running = ShouldRun(target);
        ApplyGaitSpeed(running);
        agent.angularSpeed = AgentAngularSpeedDegrees;
        agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);
        if (!UsesManualTargetFacing)
            agent.updateRotation = !ManualPursuitControlsAgentRotation;
        if (agent.isOnNavMesh)
            agent.isStopped = false;

        movingTowardsTargetPlayer = false;
        moveTowardsDestination = true;
        HoldLocomotionIntent(0.4f);
        SetLocalBehaviourState(BehaviourStateTracking);
        SetMotionStateIfChanged(running ? BundyAnimatorParams.MotionRun : BundyAnimatorParams.MotionWalk);
    }

    // Main entrance first, then nearest by planar distance. No complete-path test and no approach-ring
    // resolution: those are exactly the two requirements that failed in the field.
    private bool TryPickBlockedEntranceFallbackEntrance(bool wantEnter, out EntranceTeleport entrance, out Vector3 entrancePoint)
    {
        entrance = null;
        entrancePoint = Vector3.zero;
        EntranceTeleport[] entrances = GetCachedEntranceTeleports();
        if (entrances == null || entrances.Length == 0)
            return false;

        float bestScore = float.PositiveInfinity;
        for (int i = 0; i < entrances.Length; i++)
        {
            EntranceTeleport candidate = entrances[i];
            if (candidate == null || candidate.isEntranceToBuilding != wantEnter)
                continue;

            Vector3 candidatePoint = GetEntrancePoint(candidate);
            float score = PlanarDistance(transform.position, candidatePoint) +
                (candidate.entranceId == 0 ? 0f : BlockedEntranceFallbackNonMainPenalty);
            if (score >= bestScore)
                continue;

            bestScore = score;
            entrance = candidate;
            entrancePoint = candidatePoint;
        }

        return entrance != null;
    }

    private void MaybeLogBlockedEntranceFallbackProgress(
        PlayerControllerB target,
        EntranceTeleport entrance,
        float entranceDistance,
        string phase)
    {
        if (Time.time < _nextBlockedEntranceFallbackLogAt)
            return;

        _nextBlockedEntranceFallbackLogAt = Time.time + BlockedEntranceFallbackLogIntervalSeconds;
        LogWarning(
            $"[BundyPursuitStall] entrance fallback holding: target={PlayerLabel(target)}, phase={phase}, " +
            $"entranceId={(entrance != null ? entrance.entranceId : -1)}, distance={entranceDistance:F1}m, " +
            $"engagedFor={Time.time - _blockedEntranceFallbackEngagedAt:F0}s, " +
            $"pathStatus={DescribeAgentPathStatus()}, agent={DescribeAgentDriveState()}.");
    }

    private string DescribeAgentPathStatus()
    {
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return "off-mesh";
        if (agent.pathPending)
            return "pending";
        // 2026-08-06: agent.pathStatus is the status of the LAST COMPUTED path and survives arrival and
        // ResetPath -- the 13:20 field log printed "PathComplete" through 210s of hasPath=False. Bare
        // pathStatus is a lie without hasPath next to it.
        return agent.hasPath ? agent.pathStatus.ToString() : $"none(last={agent.pathStatus})";
    }

    // The fields the 13:20 freeze diagnosis needed and the logs did not carry: whether anything is
    // holding the agent (isStopped/updatePosition/speed) versus whether it simply has no path, plus
    // which root-lock layer owns him if one does. One string, warnings only.
    private string DescribeAgentDriveState()
    {
        if (agent == null || !agent.enabled)
            return "(disabled)";

        Vector3 velocity = agent.velocity;
        velocity.y = 0f;
        string holds = !_pairedActionRootLockActive && !_knockdownRootPinActive && !_turnClipRootLockActive
            ? "none"
            : $"{(_pairedActionRootLockActive ? "pairedLock " : string.Empty)}" +
              $"{(_knockdownRootPinActive ? "knockdownPin " : string.Empty)}" +
              $"{(_turnClipRootLockActive ? "turnClip" : string.Empty)}".TrimEnd();
        return $"(onMesh={agent.isOnNavMesh}, stopped={(agent.isOnNavMesh && agent.isStopped)}, " +
            $"hasPath={agent.hasPath}, pending={agent.pathPending}, speed={agent.speed:F1}, " +
            $"vel={velocity.magnitude:F2}, updatePos={agent.updatePosition}, " +
            $"mvDest={moveTowardsDestination}, holds={holds})";
    }

    // Own fields only. The pending-threshold state is shared with the primary router, which
    // re-initialises it on a different entrance and clears it itself once the boundary condition ends
    // -- clearing it here would tear down a crossing the primary had just taken over.
    private void ClearBlockedEntranceFallback()
    {
        if (!_blockedEntranceFallbackActive)
            return;

        _blockedEntranceFallbackActive = false;
        _blockedEntranceFallbackEntrance = null;
        _blockedEntranceFallbackEngagedAt = 0f;
        _nextBlockedEntranceFallbackIssueAt = 0f;
        _nextBlockedEntranceFallbackRetryAt = 0f;
        _nextBlockedEntranceFallbackLogAt = 0f;
    }

    // ---- #317 boundary-entry failsafe (Lawson-approved, 2026-08-06) ----------------------------
    // The last line of the outside->inside (and inside->outside) pursuit chain. Everything above it --
    // primary router, smart paths, entrance fallback -- steers the agent; this tier asserts the design
    // promise directly: a Bundy who has demonstrably failed to close on ANY boundary door for 30s
    // teleports through a fire escape. It runs in every perception state including PING, because the
    // promise is not conditional on how well he knows where the player is.
    //
    // Fairness rules (design decisions recorded on issue #317):
    //   - never fires while any living player has line of sight to him OR to the arrival point --
    //     an on-screen teleport reads as a glitch, an off-screen one reads as Mr. X;
    //   - never fires from the doorstep (BoundaryEntryFailsafeMinEntranceDistance): at the door the
    //     threshold/crossing flow owns entry, and a sealed lockdown gate must stay authoritative;
    //   - stands down entirely while the target shelters in the closed ship -- that is the siege's
    //     situation, and the shelter is designed counterplay, not a pursuit failure.
    private void UpdateBoundaryEntryFailsafe()
    {
        if (!IsOwner || isEnemyDead || !_strangerTargetingActive || !IsLivingPlayer(_strangerTarget))
        {
            ResetBoundaryEntryFailsafe();
            return;
        }

        PlayerControllerB target = _strangerTarget;
        if (isOutside != target.isInsideFactory)
        {
            // Same side: no boundary to cross. This is also the post-teleport clear.
            ResetBoundaryEntryFailsafe();
            return;
        }

        // An action lock means he is fighting someone HERE; sheltered-ship time belongs to the siege.
        // Both re-anchor the clock rather than pausing it: coming out of either, 30 fresh seconds of
        // genuine failure are required before a teleport, which keeps the failsafe a last resort.
        if (IsActionLocked() || IsDropJumpActive() || IsStrangerTargetShelteredInClosedShip())
        {
            _boundaryEntryLastProgressAt = Time.time;
            return;
        }

        if (!TryPickBlockedEntranceFallbackEntrance(isOutside, out _, out Vector3 nearestEntrancePoint))
        {
            // No same-direction entrances resolvable at all (round teardown, catalogue not up yet).
            ResetBoundaryEntryFailsafe();
            return;
        }

        float entranceDistance = PlanarDistance(transform.position, nearestEntrancePoint);
        if (entranceDistance < _boundaryEntryBestEntranceDistance - BoundaryEntryFailsafeProgressMeters ||
            float.IsNegativeInfinity(_boundaryEntryLastProgressAt))
        {
            _boundaryEntryBestEntranceDistance = Mathf.Min(_boundaryEntryBestEntranceDistance, entranceDistance);
            _boundaryEntryLastProgressAt = Time.time;
            return;
        }

        if (entranceDistance <= BoundaryEntryFailsafeMinEntranceDistance)
        {
            // At the door. The crossing flow owns it from here; a sealed gate stays sealed.
            _boundaryEntryLastProgressAt = Time.time;
            return;
        }

        float heldFor = Time.time - _boundaryEntryLastProgressAt;
        if (heldFor < BoundaryEntryFailsafeSeconds || Time.time < _nextBoundaryEntryFailsafeRetryAt)
            return;

        if (!TryPickBoundaryEntryFailsafeEntrance(isOutside, out EntranceTeleport entrance, out Vector3 arrivalPoint))
        {
            _nextBoundaryEntryFailsafeRetryAt = Time.time + BoundaryEntryFailsafeRetrySeconds;
            return;
        }

        if (IsPositionObservedByAnyLivingPlayer(transform.position) ||
            IsPositionObservedByAnyLivingPlayer(arrivalPoint))
        {
            _nextBoundaryEntryFailsafeRetryAt = Time.time + BoundaryEntryFailsafeRetrySeconds;
            if (Time.time >= _nextBoundaryEntryFailsafeLogAt)
            {
                _nextBoundaryEntryFailsafeLogAt = Time.time + BlockedEntranceFallbackLogIntervalSeconds;
                LogWarning(
                    "[BundyPursuitStall] boundary-entry failsafe due but deferred (observed): " +
                    $"heldFor={heldFor:F0}s, entranceId={entrance.entranceId}, retrying every " +
                    $"{BoundaryEntryFailsafeRetrySeconds:F0}s until unseen.");
            }
            return;
        }

        LogWarning(
            "[BundyPursuitStall] boundary-entry failsafe FIRING: no progress toward any entrance for " +
            $"{heldFor:F0}s (best={_boundaryEntryBestEntranceDistance:F1}m, now={entranceDistance:F1}m), " +
            $"state={_strangerPerceptionState.ToString().ToUpperInvariant()}, " +
            $"direction={(isOutside ? "outside-to-inside" : "inside-to-outside")}, " +
            $"entranceId={entrance.entranceId}, arrival={FormatVector(arrivalPoint)}, " +
            $"agent={DescribeAgentDriveState()}, entranceRoute={DescribeEntranceRouteRejections()}.");

        if (TryUseSmartEntranceTeleport(entrance, "boundary-entry failsafe (#317)"))
        {
            ResetBoundaryEntryFailsafe();
            ClearBlockedEntranceFallback();
        }
        else
        {
            _nextBoundaryEntryFailsafeRetryAt = Time.time + BoundaryEntryFailsafeRetrySeconds;
        }
    }

    private void ResetBoundaryEntryFailsafe()
    {
        _boundaryEntryBestEntranceDistance = float.PositiveInfinity;
        _boundaryEntryLastProgressAt = float.NegativeInfinity;
        _nextBoundaryEntryFailsafeRetryAt = 0f;
    }

    // Fire escapes first (entranceId != 0), per the approved design -- an off-screen fire-escape entry
    // reads as flanking rather than cheating -- then the main door. A candidate must resolve its exit
    // script here: the arrival point feeds the observer fairness check BEFORE the teleport primitive
    // runs, so an unresolvable exit disqualifies rather than surprising TryUseSmartEntranceTeleport.
    private bool TryPickBoundaryEntryFailsafeEntrance(bool wantEnter, out EntranceTeleport entrance, out Vector3 arrivalPoint)
    {
        entrance = null;
        arrivalPoint = Vector3.zero;
        EntranceTeleport[] entrances = GetCachedEntranceTeleports();
        if (entrances == null || entrances.Length == 0)
            return false;

        float bestScore = float.PositiveInfinity;
        for (int i = 0; i < entrances.Length; i++)
        {
            EntranceTeleport candidate = entrances[i];
            if (candidate == null || candidate.isEntranceToBuilding != wantEnter)
                continue;

            try
            {
                if (candidate.exitScript == null && !candidate.FindExitPoint())
                    continue;
            }
            catch
            {
                continue;
            }

            if (candidate.exitScript == null)
                continue;

            float score = PlanarDistance(transform.position, GetEntrancePoint(candidate)) +
                (candidate.entranceId == 0 ? BlockedEntranceFallbackNonMainPenalty : 0f);
            if (score >= bestScore)
                continue;

            bestScore = score;
            entrance = candidate;
            arrivalPoint = GetEntrancePoint(candidate.exitScript);
        }

        return entrance != null;
    }

    // Same observer test the far-sprint gate uses: eye-cone LOS from every living player, range
    // stretched to the actual separation so a long clear sightline still counts as observed.
    private bool IsPositionObservedByAnyLivingPlayer(Vector3 position)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null)
            return false;

        foreach (PlayerControllerB player in players)
        {
            if (!IsLivingPlayer(player))
                continue;

            float distance = Vector3.Distance(position, player.transform.position);
            if (player.HasLineOfSightToPosition(
                    position,
                    BoundaryEntryFailsafeObserverConeDegrees,
                    Mathf.CeilToInt(distance + 5f),
                    -1f))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasSearchNodeCandidateNear(Vector3 searchStart, float radius)
    {
        if (allAINodes == null || allAINodes.Length == 0)
            return false;

        float radiusSq = Mathf.Max(1f, radius) * Mathf.Max(1f, radius);
        for (int i = 0; i < allAINodes.Length; i++)
        {
            GameObject node = allAINodes[i];
            if (node == null || !node.activeInHierarchy)
                continue;

            if ((node.transform.position - searchStart).sqrMagnitude <= radiusSq)
                return true;
        }

        return false;
    }

    private void StopPursuitSearchIfActive()
    {
        if (_pursuitSearch.inProgress)
            StopSearch(_pursuitSearch);
    }

    private void ResetPursuitSearchRoutine()
    {
        _pursuitSearch.unsearchedNodes.Clear();
        _pursuitSearch.currentTargetNode = null;
        _pursuitSearch.nextTargetNode = null;
        _pursuitSearch.waitingForTargetNode = false;
        _pursuitSearch.choseTargetNode = false;
        _pursuitSearch.timesFinishingSearch = 0;
        _pursuitSearch.nodesEliminatedInCurrentSearch = 0;
        _pursuitSearch.calculatingNodeInSearch = false;
        _pursuitSearch.startedSearchAtSelf = false;
    }

    private bool TryHandleBlockedPursuitTarget(PlayerControllerB target, Vector3 desiredDestination)
    {
        if (!IsLivingPlayer(target) || !AgentReadyForMovement() || IsActionLocked())
            return false;

        TrackBlockedPursuitTarget(target);
        bool visible = HasLineOfSightToPlayer(target, Mathf.Max(18f, Vector3.Distance(transform.position, target.transform.position) + 2f));

        if (visible && BlockedTargetTauntsEnabled && creatureAnimator != null && Time.time >= _nextBlockedTargetTauntAt)
        {
            _nextBlockedTargetTauntAt = Time.time + BlockedTargetTauntCooldownSeconds;
            _nextBlockedTargetHideAt = Mathf.Max(_nextBlockedTargetHideAt, Time.time + 1.2f);
            StopMovement();
            FaceActionPoint(target.transform.position);
            PlayEmote(TauntEmoteIndex);
            LogInfo(
                $"Blocked target response: taunt; target={PlayerLabel(target)}, " +
                $"blockedFor={Time.time - _blockedPursuitStartedAt:F1}s, desired={FormatVector(desiredDestination)}.");
            return true;
        }

        if (BlockedTargetHideEnabled &&
            Time.time >= _nextBlockedTargetHideAt &&
            TryFindBlockedTargetHideDestination(target, out Vector3 hideDestination))
        {
            _nextBlockedTargetHideAt = Time.time + BlockedTargetHideCooldownSeconds;
            MoveToBlockedTargetHideDestination(target, hideDestination);
            return true;
        }

        StopMovement();
        FaceActionPoint(target.transform.position);
        SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);

        if (Time.time >= _nextBlockedTargetLogAt)
        {
            _nextBlockedTargetLogAt = Time.time + BlockedTargetLogIntervalSeconds;
            LogInfo(
                $"Blocked target response: holding; target={PlayerLabel(target)}, visible={visible}, " +
                $"blockedFor={Time.time - _blockedPursuitStartedAt:F1}s, desired={FormatVector(desiredDestination)}.");
        }

        return true;
    }

    private void TrackBlockedPursuitTarget(PlayerControllerB target)
    {
        if (_blockedPursuitTarget == target)
            return;

        _blockedPursuitTarget = target;
        _blockedPursuitStartedAt = Time.time;
        _nextBlockedTargetTauntAt = 0f;
        _nextBlockedTargetHideAt = 0f;
        _nextBlockedTargetLogAt = 0f;
    }

    private void ClearBlockedPursuitTarget(PlayerControllerB target)
    {
        if (_blockedPursuitTarget != null && (_blockedPursuitTarget == target || !IsLivingPlayer(_blockedPursuitTarget)))
        {
            _blockedPursuitTarget = null;
            // The sliced cover search only restarts when the target changes, so leaving a half-swept
            // cursor behind would let a later block on the SAME player resume a sweep whose "away"
            // direction and running best were computed from where Bundy stood minutes ago.
            AbandonBlockedTargetHideSearch();
        }
    }

    private void AbandonBlockedTargetHideSearch()
    {
        _hideSearchCursor = -1;
        _hideSearchTarget = null;
        _hideSearchBest = transform.position;
        _hideSearchBestCost = float.PositiveInfinity;
        _hideSearchFound = false;
    }

    // WP-A5: the cover search evaluated all 32 candidates in one frame, each one a
    // NavMesh.SamplePosition, a full NavMesh.CalculatePath and a Physics raycast. The consumer
    // (TryHandleBlockedPursuitTarget) treats a false result as "hold position and face the target",
    // which is what Bundy does anyway while a target is unreachable, so the search can take a few
    // frames: it now evaluates HideSearchCandidatesPerFrame candidates per call and returns false
    // until the sweep is finished. The candidate set, the cost function and the winner are unchanged.
    private const int HideSearchCandidatesPerFrame = 8;
    private static readonly float[] HideSearchDistances = { 3.5f, 5.5f, 7.5f, 10f };
    private static readonly float[] HideSearchAngles = { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f };
    private PlayerControllerB _hideSearchTarget;
    private int _hideSearchCursor = -1;
    private Vector3 _hideSearchAway;
    private Vector3 _hideSearchBest;
    private float _hideSearchBestCost;
    private bool _hideSearchFound;

    private bool TryFindBlockedTargetHideDestination(PlayerControllerB target, out Vector3 destination)
    {
        destination = transform.position;
        if (!IsLivingPlayer(target) || agent == null || !agent.enabled || !agent.isOnNavMesh)
        {
            AbandonBlockedTargetHideSearch();
            return false;
        }

        int candidateTotal = HideSearchDistances.Length * HideSearchAngles.Length;
        if (_hideSearchCursor < 0 || _hideSearchTarget != target)
        {
            Vector3 away = transform.position - target.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.001f)
                away = -target.transform.forward;
            away.y = 0f;
            if (away.sqrMagnitude < 0.001f)
                away = -transform.forward;
            away.y = 0f;
            if (away.sqrMagnitude < 0.001f)
                away = Vector3.back;
            away.Normalize();

            _hideSearchTarget = target;
            _hideSearchAway = away;
            _hideSearchCursor = 0;
            _hideSearchBest = transform.position;
            _hideSearchBestCost = float.PositiveInfinity;
            _hideSearchFound = false;
        }

        Vector3 playerEye = target.transform.position + Vector3.up * 1.45f;
        int evaluated = 0;
        while (_hideSearchCursor < candidateTotal && evaluated < HideSearchCandidatesPerFrame)
        {
            int index = _hideSearchCursor++;
            evaluated++;
            int d = index / HideSearchAngles.Length;
            int a = index % HideSearchAngles.Length;

            Vector3 direction = Quaternion.Euler(0f, HideSearchAngles[a], 0f) * _hideSearchAway;
            Vector3 candidate = transform.position + direction * HideSearchDistances[d];
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit sample, NavigationObstacleMaxSampleDistance, NavMesh.AllAreas))
                continue;

            if (!TryCalculateCompletePath(sample.position, out NavMeshPath path))
                continue;

            float pathLength = EstimatePathLength(path);
            if (pathLength < 0.5f)
                continue;

            bool visibleFromPlayer = HasClearWorldLine(playerEye, sample.position + Vector3.up * 1.15f, target);
            float distanceFromTarget = Vector3.Distance(sample.position, target.transform.position);
            float cost = pathLength * 0.35f - distanceFromTarget * 0.12f + (visibleFromPlayer ? 20f : 0f);
            if (cost >= _hideSearchBestCost)
                continue;

            _hideSearchBestCost = cost;
            _hideSearchBest = sample.position;
            _hideSearchFound = true;
        }

        if (_hideSearchCursor < candidateTotal)
            return false;

        Vector3 result = _hideSearchBest;
        bool found = _hideSearchFound;
        AbandonBlockedTargetHideSearch();
        destination = result;
        return found;
    }

    private void MoveToBlockedTargetHideDestination(PlayerControllerB target, Vector3 destination)
    {
        if (!AgentReadyForMovement())
            return;

        float distance = Vector3.Distance(transform.position, destination);
        if (distance <= StopDistance)
            return;

        bool running = distance > RunThreshold;
        ApplyGaitSpeed(running);
        agent.angularSpeed = AgentAngularSpeedDegrees;
        agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);
        agent.updateRotation = true;
        agent.stoppingDistance = StopDistance;
        agent.isStopped = false;

        movingTowardsTargetPlayer = false;
        moveTowardsDestination = true;
        RecordPursuitDestination(destination, "blocked-hide", "target unreachable; taking cover");
        SetDestinationToPosition(destination);
        HoldLocomotionIntent(0.4f);
        SetLocalBehaviourState(BehaviourStateTracking);
        SetMotionStateIfChanged(running ? BundyAnimatorParams.MotionRun : BundyAnimatorParams.MotionWalk);

        LogInfo(
            $"Blocked target response: moving to cover; target={PlayerLabel(target)}, " +
            $"destination={FormatVector(destination)}, distance={distance:F1}m.");
    }

    private static readonly RaycastHit[] WorldLineHitBuffer = new RaycastHit[64];
    private static bool s_worldLineOverflowLogged;

    private bool HasClearWorldLine(Vector3 from, Vector3 to, PlayerControllerB ignoredPlayer)
    {
        Vector3 delta = to - from;
        float distance = delta.magnitude;
        if (distance <= 0.05f)
            return true;

        // WP-A5: the result is "does ANY hit block", so the distance sort never affected it - it only
        // allocated a comparison delegate and reordered an array that is thrown away. RaycastNonAlloc
        // into a shared buffer drops the per-call RaycastHit[] as well.
        int hitCount = Physics.RaycastNonAlloc(
            from,
            delta / distance,
            WorldLineHitBuffer,
            distance,
            GetWorldCollisionMask(),
            QueryTriggerInteraction.Ignore);
        if (hitCount <= 0)
            return true;

        // A full buffer means the cast was truncated and a blocking collider may be among the hits
        // that were dropped, so the line reports as blocked rather than clear on partial evidence.
        if (hitCount >= WorldLineHitBuffer.Length)
        {
            if (!s_worldLineOverflowLogged)
            {
                s_worldLineOverflowLogged = true;
                BundyPlugin.Log?.LogDebug(
                    $"[{VariantName}] World-line raycast filled its {WorldLineHitBuffer.Length}-hit buffer; treating the line as blocked.");
            }

            return false;
        }

        for (int i = 0; i < hitCount; i++)
        {
            Collider collider = WorldLineHitBuffer[i].collider;
            if (collider == null)
                continue;

            if (IsNavigationBlockingCollider(collider, ignoredPlayer))
                return false;
        }

        return true;
    }

    // Job B1: the single action-exit seam. CompleteAction's finally block is the only caller, so this
    // is where the action layer hands the agent back — and the handback has to be unconditional. It
    // was not: an unreachable hunt target or a momentarily unready agent both returned with the
    // action's hold (isStopped / updatePosition / applyRootMotion / inSpecialAnimation) still applied,
    // and nothing cleared it until the movement watchdog noticed a beat later. That is the
    // "Movement watchdog recovered Bundy after action; isStopped=True" line, 22 times in the
    // 2026-07-30 run: a frozen idle after nearly every swing, paid at watchdog latency.
    //
    // The action layer keeps its own movement writes — that boundary is deliberate. What it owes the
    // navigation layer is a guaranteed RELEASE on the way out, which is what the unconditional unlock
    // below is.
    private void ResumeMovementAfterAction()
    {
        // Release first, decide second. Root motion, the special-animation flags and the agent flags
        // belong to the action that just ended no matter what pursuit chooses to do next.
        UnlockAgentForMovement("action resume");

        PlayerControllerB huntTarget = GetCurrentHuntTarget();
        if (!IsReachableHuntTarget(huntTarget))
        {
            // Deliberate idle, not a leaked hold: the agent has already been released above, so this
            // stop is one the pursuit layer can undo on its next tick.
            StopMovement();
            return;
        }

        if (!AgentReadyForMovement())
        {
            ApplyDesiredMotionState(huntTarget);
            return;
        }

        UpdateMovementDestination(huntTarget);
    }

    private void RecoverMovementIfNeeded()
    {
        // C1.1: second anchor for the saw-stall watchdog (the perception hook is the first). This
        // one runs per-frame on the owner even when Stranger targeting is inactive, so the
        // "stall persists because no code path could reach BeginPullStart" hole stays closed in
        // every pursuit mode, not just the Stranger machine.
        MaybeRecoverStalledSaw();

        // #300 B1: every early return out of the watchdog is classified. These three declare the
        // recovery moot -- an action, a smart-interaction hold or a dead round owns him now, and by the
        // time any of them releases, the stall, the target and the destination may all be different.
        // Abandon the sliced sweep rather than leaving the flag up for a later stall to resume.
        if (IsActionLocked() || !RoundReady())
        {
            CancelStuckRecoverySearch();
            return;
        }
        // #306: a smart-interaction hold (door open, teleport settle) is a deliberate stop, so the sweep
        // must not advance — but cancelling it threw the sliced cursor away and restarted it at zero on
        // the far side, which is how a stall behind a door could never finish a recovery. Suspend it
        // instead: the cursor and the stall clock both survive the hold untouched.
        if (Time.time < _smartInteractionHoldUntil)
        {
            SuspendMovementProgressClock();
            return;
        }
        // #306: same reasoning for turn clips, and more urgently — the clip cooldown (1.1s) is shorter
        // than MovementStuckCheckSeconds (1.5s), so resetting the tracker on every clip meant a Bundy
        // turning against a corner could never accumulate enough stall time for the watchdog to fire.
        if (IsTurnClipRootLockActive())
        {
            SuspendMovementProgressClock();
            return;
        }
        // The drop-jump return below cancels via ResetMovementProgressTracker.
        // Job 5a: a parked agent during a drop-down arc is intentional, not a leaked hold. FinishDropJump
        // is the release; without this the watchdog would fire the BUG SIGNAL every time he jumps.
        if (IsDropJumpActive())
        {
            ResetMovementProgressTracker();
            return;
        }

        PlayerControllerB huntTarget = GetCurrentHuntTarget();
        if (!IsReachableHuntTarget(huntTarget) || !AgentReadyForMovement())
        {
            // #300 B1: no reachable target (or no usable agent) means the sweep's whole premise is gone.
            CancelStuckRecoverySearch();
            return;
        }

        // #300 instrumentation: the two Tier-1 candidates inside RecoverMovement get their own scopes so
        // one run separates them instead of leaving the whole 235-276ms plateau on the parent bracket.
        long huntDistanceTimingStarted = BundyRuntimePerformanceDiagnostics.Begin();
        float distance;
        try
        {
            distance = GetCurrentHuntDistance(huntTarget);
        }
        finally
        {
            BundyRuntimePerformanceDiagnostics.End(
                BundyRuntimeStep.HuntDistance,
                huntDistanceTimingStarted);
        }

        if (distance <= StopDistance + 0.25f)
        {
            ResetMovementProgressTracker();
            return;
        }

        if (ShouldDeferMovementRecovery(distance))
        {
            ResetMovementProgressTracker();
            _nextStuckRecoveryAt = Mathf.Max(_nextStuckRecoveryAt, Time.time + MovementRecoveryFarPlayerCooldownSeconds);
            return;
        }

        bool rotationLocked = !agent.updateRotation && !ManualPursuitControlsAgentRotation;
        bool agentLocked = agent.isStopped || !agent.updatePosition || rotationLocked;
        bool agentDesynced = (agent.nextPosition - transform.position).sqrMagnitude > 1f;
        // #300 B1: a held or desynced agent short-circuits past TryRecoverStuckMovement into the unlock
        // branch below, which IS a recovery -- so any sliced sweep in flight is superseded, not paused.
        if (agentLocked || agentDesynced)
            CancelStuckRecoverySearch();
        else if (!TryRecoverStuckMovementTimed(huntTarget))
            return;

        if (agentLocked || agentDesynced)
        {
            // C1.3: the trigger here is ANY held agent, but the BUG SIGNAL's message blames the
            // action-exit seam — and in the 2026-07-30 run 6 of 8 firings had no action anywhere
            // near them (lastAction=None, sinceActionExit=2163s). Keep the warning ONLY for
            // genuine post-action seams (an action exited within the seam window); everything else
            // is a real recovery worth a line, but of an unknown non-action path — info level,
            // same throttle. The genuine seam count to drive to 0 is Stagger exits (2 observed).
            float sinceActionExit = Time.time - _lastActionExitAt;
            bool postActionSeam = _lastActionExitKind != ActionKind.None &&
                sinceActionExit <= PostActionSeamWindowSeconds;
            string details =
                $"isStopped={agent.isStopped}, updatePosition={agent.updatePosition}, " +
                $"updateRotation={agent.updateRotation}, desynced={agentDesynced}, " +
                $"lastAction={_lastActionExitKind}, sinceActionExit={sinceActionExit:F2}s.";
            if (postActionSeam)
            {
                LogPostActionMovementRecoveryBugSignal(
                    $"BUG SIGNAL: movement watchdog had to recover Bundy after an action — an action-exit " +
                    $"path left the agent held instead of releasing it (ResumeMovementAfterAction). " +
                    details);
            }
            else
            {
                LogAgentParkedByUnknownPath(
                    $"Movement watchdog recovered an agent parked by unknown path (no recent action exit). " +
                    details);
            }

            UnlockAgentForMovement("movement watchdog");
            UpdateMovementDestination(huntTarget);
        }
    }

    // C1.3: how close to an action exit a held agent still counts as the action-exit seam.
    // Config-backed since wave 2 (AI/PostActionSeamWindowSeconds, default preserved at 0.5).
    private static float PostActionSeamWindowSeconds =>
        BundyPlugin.ModConfig?.PostActionSeamWindowSeconds?.Value ?? 0.5f;

    // C1.3: the non-seam half of the split — a held agent with no recent action exit. Info level so a
    // default-verbosity run still shows the recovery, on the same 2s throttle as the BUG SIGNAL.
    private void LogAgentParkedByUnknownPath(string message)
    {
        if (Time.time < _nextMovementRecoveryLogAt)
            return;

        _nextMovementRecoveryLogAt = Time.time + 2f;
        LogInfo(message);
    }

    private bool ShouldDeferMovementRecovery(float huntTargetDistance)
    {
        if (huntTargetDistance <= MovementRecoveryActivePlayerRange)
            return false;

        return CountLivingPlayersNear(transform.position, MovementRecoveryActivePlayerRange) <= 0;
    }

    private bool TryRecoverStuckMovementTimed(PlayerControllerB huntTarget)
    {
        long timingStarted = BundyRuntimePerformanceDiagnostics.Begin();
        try
        {
            return TryRecoverStuckMovement(huntTarget);
        }
        finally
        {
            BundyRuntimePerformanceDiagnostics.End(BundyRuntimeStep.StuckRecovery, timingStarted);
        }
    }

    private bool TryRecoverStuckMovement(PlayerControllerB huntTarget)
    {
        // #306: an off-mesh link traversal produces exactly the signature the watchdog hunts (no path
        // progress, no agent velocity) while being entirely legitimate. Nothing arms off it.
        if (agent.isOnOffMeshLink)
        {
            _movementVelocityStallSince = 0f;
            SuspendMovementProgressClock();
            return false;
        }

        // #306: the sidestep tier owns movement for its short hold; when it expires the real destination
        // is re-issued here rather than waiting on the next AI interval.
        if (_movementSidestepHoldUntil > 0f)
        {
            if (Time.time < _movementSidestepHoldUntil)
                return false;

            _movementSidestepHoldUntil = 0f;
            UpdateMovementDestination(huntTarget);
            ResetMovementProgressTracker();
            return true;
        }

        bool wantsMovement = moveTowardsDestination
            || movingTowardsTargetPlayer
            || agent.hasPath
            || agent.pathPending
            || agent.desiredVelocity.sqrMagnitude > 0.05f;
        if (!wantsMovement)
        {
            ResetMovementProgressTracker();
            return false;
        }

        Vector3 current = transform.position;
        if (!_hasLastMovementProgress)
        {
            _lastMovementProgressPosition = current;
            _lastMovementProgressAt = Time.time;
            _hasLastMovementProgress = true;
            return false;
        }

        // #300 B2: MovementStuckMinProgress is the DETECTION threshold and it is deliberately tiny
        // (0.18m) -- an agent pinned against geometry with residual velocity crosses it over and over
        // without escaping. That is fine as an entry test, but using it to cancel an in-flight sliced
        // sweep resets the cursor faster than the sweep can finish and the reroute never lands. So an
        // active sweep survives until he has genuinely travelled StuckRecoveryCancelProgressMeters from
        // where the sweep began; below that the progress tracker is deliberately NOT rolled forward,
        // so stuckFor keeps accruing and the sweep keeps advancing.
        //
        // #306: and displacement alone cannot see the wall-slide case at all -- grinding along a prop or
        // being churned by RVO drifts well past 0.18m every second while making no route progress, so the
        // escape below fired forever. A sustained steering demand that produces no actual velocity is the
        // direct signal; while it holds, drift is not allowed to reset the clock.
        bool velocityStalled = UpdateMovementVelocityStall();
        if (!velocityStalled &&
            PlanarDistance(current, _lastMovementProgressPosition) >= MovementStuckMinProgress &&
            (!_stuckRecoverySearchActive || StuckRecoverySearchEscaped()))
        {
            CancelStuckRecoverySearch();
            _lastMovementProgressPosition = current;
            _lastMovementProgressAt = Time.time;
            return false;
        }

        float stuckFor = Time.time - _lastMovementProgressAt;
        float stuckThreshold = velocityStalled ? MovementVelocityStallCheckSeconds : MovementStuckCheckSeconds;
        if (stuckFor < stuckThreshold ||
            (!_stuckRecoverySearchActive && Time.time < _nextStuckRecoveryAt))
        {
            return false;
        }

        // #306: cheapest tier first. A 1-1.5m step along the surface he is grinding on clears the great
        // majority of these without touching the 150-250 node sweep behind it; the sweep only opens when
        // the sidestep found nothing reachable or the stall outlived it.
        if (!_stuckRecoverySearchActive && TryLocalSidestepRecovery(huntTarget, stuckFor))
            return true;

        // #300: the two AI-node sweeps behind this watchdog used to run whole inside one frame --
        // NavMesh.SamplePosition plus a synchronous CalculatePath for every AI node, ~150-250 of them,
        // measured at 235-276ms and matching the profiler's unexplained hitches one for one. They now
        // run through the incremental machine in Pathing.cs: same node order, same reject guards, same
        // cost comparison and therefore the same winner, spread over frames at a bounded budget.
        // MovementStuckRecoveryCooldownSeconds now means "do not START another sweep"; while one is in
        // flight the watchdog takes no action at all (no early warp, no re-entry, no restart).
        //
        // #300 B1: a leftover sweep is only resumed when it is still answering THIS stall -- i.e. when it
        // is still routing toward the same navigation target. Anything else restarts it, so a suspended
        // sweep can never be revived later and publish a reroute scored toward a player he stopped
        // hunting. The destination is NOT part of the match test (it is live player position in HUNT and
        // would restart the cursor every fraction of a second); it is re-anchored per advance instead.
        PlayerControllerB navigationTarget = CanUseLiveHuntTarget(huntTarget) ? huntTarget : null;
        if (!StuckRecoverySearchMatches(navigationTarget))
            BeginStuckRecoverySearch(huntTarget, navigationTarget, GetPursuitDestination(huntTarget), stuckFor);

        StuckRecoverySearchOutcome outcome = AdvanceStuckRecoverySearch(
            out Vector3 searchResult,
            out string searchMode);
        if (outcome == StuckRecoverySearchOutcome.Pending)
            return false;

        CancelStuckRecoverySearch();
        _nextStuckRecoveryAt = Time.time + MovementStuckRecoveryCooldownSeconds;

        if (outcome == StuckRecoverySearchOutcome.Rerouted)
        {
            agent.ResetPath();
            agent.isStopped = false;
            movingTowardsTargetPlayer = false;
            moveTowardsDestination = true;
            RecordPursuitDestination(searchResult, "recovery-ai-node", $"stuck {stuckFor:F1}s; watchdog reroute");
            SetDestinationToPosition(searchResult);
            HoldLocomotionIntent(0.45f);
            ResetMovementProgressTracker();
            LogMovementRecoveryWarning(
                $"Movement watchdog rerouted Bundy through AI node; target={PlayerLabel(huntTarget)}, " +
                $"stuckFor={stuckFor:F1}s, mode={searchMode}, destination={FormatVector(searchResult)}.");
            return true;
        }

        if (outcome == StuckRecoverySearchOutcome.Warped &&
            TryWarpAgentToNavPosition(searchResult, $"stuck AI-node recovery/{searchMode}"))
        {
            UpdateMovementDestination(huntTarget);
            return true;
        }

        agent.ResetPath();
        agent.isStopped = false;
        UpdateMovementDestination(huntTarget);
        ResetMovementProgressTracker();
        LogMovementRecoveryWarning(
            $"Movement watchdog refreshed Bundy's path after stalled movement; " +
            $"target={PlayerLabel(huntTarget)}, stuckFor={stuckFor:F1}s.");
        return true;
    }

    // #306: "the agent wants to move and isn't". desiredVelocity is what the steering solver asked for
    // this frame; velocity is what the simulation actually produced. A gap between them that survives
    // MovementVelocityStallSeconds is a grind against geometry or a mutual RVO deadlock, both of which
    // displacement tracking reads as healthy movement.
    private bool UpdateMovementVelocityStall()
    {
        if (agent == null || !agent.enabled || !agent.isOnNavMesh || agent.isOnOffMeshLink ||
            agent.desiredVelocity.sqrMagnitude <= MovementVelocityStallDesiredSpeed * MovementVelocityStallDesiredSpeed ||
            agent.velocity.sqrMagnitude >= MovementVelocityStallActualSpeed * MovementVelocityStallActualSpeed)
        {
            _movementVelocityStallSince = 0f;
            return false;
        }

        if (_movementVelocityStallSince <= 0f)
        {
            _movementVelocityStallSince = Time.time;
            return false;
        }

        return Time.time - _movementVelocityStallSince >= MovementVelocityStallSeconds;
    }

    // #306: the local recovery tier. Candidates run perpendicular to the surface he last clamped against
    // (i.e. ALONG it), alternating sides, so the step is the escape the slide was already trying to make.
    // Reachability is a NavMesh.Raycast rather than a CalculatePath: no allocation, no corridor build, and
    // a straight mesh line is exactly the guarantee a 1.5m step needs.
    private bool TryLocalSidestepRecovery(PlayerControllerB huntTarget, float stuckFor)
    {
        if (Time.time < _nextMovementSidestepAt ||
            agent == null || !agent.enabled || !agent.isOnNavMesh || agent.isOnOffMeshLink)
        {
            return false;
        }

        Vector3 basis = Time.time - _lastMovementClampHitAt <= MovementClampHitMemorySeconds
            ? _lastMovementClampHitNormal
            : transform.forward;
        basis.y = 0f;
        if (basis.sqrMagnitude < 0.001f)
        {
            basis = transform.forward;
            basis.y = 0f;
            if (basis.sqrMagnitude < 0.001f)
                return false;
        }

        Vector3 along = Vector3.Cross(Vector3.up, basis.normalized);
        Vector3 origin = transform.position;
        Vector3 desired = IsLivingPlayer(huntTarget) ? GetPursuitDestination(huntTarget) : origin + along;
        int areaMask = agent.areaMask;

        bool found = false;
        Vector3 best = origin;
        float bestScore = float.PositiveInfinity;
        for (int i = 0; i < MovementSidestepSampleCount; i++)
        {
            float side = (i % 2 == 0) ? 1f : -1f;
            int step = i / 2;
            int steps = Mathf.Max(1, (MovementSidestepSampleCount / 2) - 1);
            float reach = Mathf.Lerp(MovementSidestepMinDistance, MovementSidestepMaxDistance, step / (float)steps);
            Vector3 candidate = origin + along * (side * reach);
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit sampleHit, MovementSidestepSampleRadius, areaMask))
                continue;
            if (NavMesh.Raycast(origin, sampleHit.position, out NavMeshHit _, areaMask))
                continue;

            float score = PlanarDistance(sampleHit.position, desired);
            if (score >= bestScore)
                continue;

            bestScore = score;
            best = sampleHit.position;
            found = true;
        }

        _nextMovementSidestepAt = Time.time + MovementSidestepCooldownSeconds;
        if (!found)
            return false;

        agent.isStopped = false;
        movingTowardsTargetPlayer = false;
        moveTowardsDestination = true;
        RecordPursuitDestination(best, "recovery-sidestep", $"stuck {stuckFor:F1}s; local sidestep");
        SetDestinationToPosition(best);
        HoldLocomotionIntent(MovementSidestepHoldSeconds);
        _movementSidestepHoldUntil = Time.time + MovementSidestepHoldSeconds;
        _movementVelocityStallSince = 0f;
        ResetMovementProgressTracker();
        LogMovementRecoveryWarning(
            $"Movement watchdog sidestepped Bundy out of a local stall; target={PlayerLabel(huntTarget)}, " +
            $"stuckFor={stuckFor:F1}s, destination={FormatVector(best)}.");
        return true;
    }

    // Job B1: post-action recovery is a BUG SIGNAL, not routine housekeeping. Every action exit now
    // releases the agent unconditionally, so this can only fire when some path put the agent back into
    // a hold and left it there. The watchdog stays as the safety net that keeps him moving, but the
    // line is a warning regardless of DebugLogs so a run without debug logging still surfaces it.
    // Target for a clean run is zero of these (the 2026-07-30 run had 22).
    private void LogPostActionMovementRecoveryBugSignal(string message)
    {
        if (Time.time < _nextMovementRecoveryLogAt)
            return;

        _nextMovementRecoveryLogAt = Time.time + 2f;
        LogWarning(message);
    }

    private void LogMovementRecoveryWarning(string message)
    {
        if (!DebugLogs || Time.time < _nextMovementRecoveryLogAt)
            return;

        _nextMovementRecoveryLogAt = Time.time + 2f;
        LogWarning(message);
    }

    private void ResetMovementProgressTracker()
    {
        // #300: every path that declares him un-stuck also abandons an in-flight recovery sweep. The
        // sliced sweep only makes sense while the stall it was opened for is still standing.
        CancelStuckRecoverySearch();
        _lastMovementProgressPosition = transform.position;
        _lastMovementProgressAt = Time.time;
        _hasLastMovementProgress = false;
    }

    // #306: the pause button for the stall clock. A deliberate hold (door, turn clip) is neither progress
    // nor a stall, so the elapsed frame is added back to the timestamp rather than the whole tracker
    // being rebased — and the in-flight recovery sweep is left standing.
    private void SuspendMovementProgressClock()
    {
        if (!_hasLastMovementProgress)
        {
            _lastMovementProgressPosition = transform.position;
            _lastMovementProgressAt = Time.time;
            _hasLastMovementProgress = true;
            return;
        }

        _lastMovementProgressAt += Time.deltaTime;
        _movementVelocityStallSince = 0f;
    }

    private void UnlockAgentForMovement(string reason)
    {
        if (creatureAnimator != null)
            creatureAnimator.applyRootMotion = false;

        inSpecialAnimation = false;
        inSpecialAnimationWithPlayer = null;

        if (agent == null || !agent.enabled)
            return;

        agent.updatePosition = true;
        if (!UsesManualTargetFacing)
            agent.updateRotation = !ManualPursuitControlsAgentRotation;
        agent.stoppingDistance = StopDistance;
        agent.angularSpeed = AgentAngularSpeedDegrees;
        agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);

        if (!agent.isOnNavMesh)
            return;

        if ((agent.nextPosition - transform.position).sqrMagnitude > 0.25f)
            agent.Warp(transform.position);

        agent.ResetPath();
        agent.isStopped = false;
        LogInfo($"Agent unlocked for movement; reason={reason}.");
    }

    private Vector3 GetPursuitDestination(PlayerControllerB target)
    {
        if (ShouldUseStrangerSnapshotDestination(target))
            return GetStrangerSnapshotDestination();

        return target.transform.position;
    }

    // Stage 4 Phase 2: vary the pursuit so he doesn't just beeline. While he CAN'T attack (cooldown)
    // and is already at engagement range, circle the player (strafe) or back off (re-establish lunge
    // range) instead of walking into melee. When off cooldown / still far, return the direct
    // destination so the lunge can trigger. He keeps facing the player throughout (manual facing), so
    // these produce real strafe/backpedal animation via the directional blend tree.
    //
    // Job B3: the maneuvers are MOTION now, not parked absolute points. Circles issue a short
    // tangential arc waypoint re-issued every tick; Backstep is a short, clearance-checked step;
    // Reorient issues no destination at all and lets the turn-in-place clips work. The old version
    // parked a point at `target + orbit * engageRange` (or 1.55x engageRange behind), which in a
    // corridor sampled onto a wall or nearly behind him — the darty left-right jitter — and whose
    // arrival inside StopDistance flipped MotionState Walk->Idle->Walk.
    private Vector3 ApplyCombatManeuver(PlayerControllerB target, Vector3 directDestination)
    {
        Vector3 fromPlayer = transform.position - target.transform.position;
        fromPlayer.y = 0f;
        float distance = fromPlayer.magnitude;
        float engageRange = Mathf.Max(BasicAttackRange, GetBasicAttackStartRange(BasicAttackRange));
        Vector3 dirFromPlayer = distance > 0.1f ? fromPlayer / distance : transform.forward;

        CombatBehavior selected = SelectCombatBehavior(target, distance, engageRange);
        if (!TryBuildManeuverDestination(
                target,
                ref selected,
                distance,
                engageRange,
                dirFromPlayer,
                directDestination,
                out Vector3 behaviorDestination))
        {
            // Nothing the maneuver layer can do from here (no clearance behind him, no room to arc):
            // drive the direct destination rather than parking a point in a wall. Reported as
            // DirectChase so BundyCombatDest and the directional-locomotion consumer see what actually
            // drove the agent.
            selected = CombatBehavior.DirectChase;
            behaviorDestination = directDestination;
        }

        _currentCombatBehavior = selected;

        if (!TryResolveNavigableDestination(behaviorDestination, out Vector3 navigableDestination))
            return directDestination;

        // R2 interim: remember when a maneuver last actually drove him sideways or backwards. Recorded
        // here rather than at selection, because an unnavigable maneuver destination falls through to
        // the direct one above and produces no dart for the attack gate to wait out.
        if (selected == CombatBehavior.CircleLeft ||
            selected == CombatBehavior.CircleRight ||
            selected == CombatBehavior.Backstep)
        {
            _lastLateralManeuverAt = Time.time;
        }

        return navigableDestination;
    }

    // Job B2: a deterministic state machine keyed on (attack cooldown remaining, distance band, lunge
    // path clear). It replaces a weighted die roll per commit window that never consulted the cooldown
    // at all — run 5 recorded CircleRight at distTarget=7.74 with attack-start 14 lines later, and a
    // circle whose destination sat 2.19 m away while the target was 6.91 m: a two-metre sidestep
    // immediately before a lunge, the reported "walking backward or sideways very quickly right before
    // attacking". A cooldown-aware guard on top could only rescue the worst frame; here the cooldown
    // is the clock every branch is measured against.
    //
    // Decision table, first match wins:
    //   player behind, inside 1.35x engage        -> Reorient (turn in place, no destination override)
    //   cooldown ready, in range, lunge clear     -> DirectChase, always
    //   cooldown ready, out of range / lunge blocked -> Intercept when far and the target is moving,
    //                                                else DirectChase
    //   inside a commit window                    -> hold the committed maneuver
    //   window too short for a whole maneuver     -> DirectChase
    //   cooldown far, far band                    -> Intercept when the target is moving, else DirectChase
    //   cooldown far, mid band (or lunge blocked) -> Circle, direction alternating per commit
    //   cooldown far, close band                  -> Backstep (re-establish lunge range)
    //
    // Deterministic, not lifeless: the variation comes from which band he is in, which way he circled
    // last and whether the lane is clear — never from a die roll against the clock.
    private CombatBehavior SelectCombatBehavior(PlayerControllerB target, float distance, float engageRange)
    {
        bool playerBehind = IsPlayerBehindBundy(target);
        bool lungeBlocked = UsesAttackLunge && !HasClearLungePathTo(target.transform.position);
        bool far = distance > engageRange * 1.8f;
        bool mid = !far && distance > engageRange * 1.1f;
        float cooldownRemaining = Mathf.Max(0f, _nextAttackAllowedAt - Time.time);
        bool targetMoving = _trackedTargetVelocity.sqrMagnitude > 0.25f;

        if (playerBehind && distance <= engageRange * 1.35f)
            return CommitCombatBehavior(CombatBehavior.Reorient, 0.45f);

        // Job B2, guard 1 is now structural rather than a correction on top of a die roll: the cooldown
        // IS the first thing the machine looks at, so a maneuver can no longer be chosen in the beat
        // before a swing. Preempts a maneuver already inside its commit window — the cooldown expiring
        // mid-circle is the case that produced the worst frames, and waiting out the remaining window
        // would only prolong it. Reorient above keeps priority because attacking while the player is
        // behind him is worse than a beat of turning.
        if (cooldownRemaining <= 0f)
        {
            if (!lungeBlocked && distance <= engageRange)
                return CommitCombatBehavior(CombatBehavior.DirectChase, 0f);

            // Off cooldown but not yet able to swing: close, leading a moving target from far range.
            return CommitCombatBehavior(
                far && targetMoving ? CombatBehavior.Intercept : CombatBehavior.DirectChase,
                0f);
        }

        if (Time.time < _nextCombatBehaviorChangeAt)
            return _currentCombatBehavior;

        // Size the maneuver to the cooldown. It has to finish — and the post-maneuver attack dwell has
        // to elapse — before the swing becomes available, otherwise the swing grows out of a sidestep
        // again. A window too short for that gets no maneuver at all rather than half of one.
        float dwell = CombatManeuverAttackGuardEnabled ? Mathf.Max(0f, CombatManeuverAttackDwellSeconds) : 0f;
        float window = cooldownRemaining - dwell;
        if (window < Mathf.Max(0.1f, CombatManeuverMinWindowSeconds))
            return CommitCombatBehavior(CombatBehavior.DirectChase, 0f);

        float duration = Mathf.Min(Mathf.Max(0.55f, CombatManeuverDuration), window);

        // Far: still an approach problem, not a maneuver one.
        if (far)
            return CommitCombatBehavior(targetMoving ? CombatBehavior.Intercept : CombatBehavior.DirectChase, duration);

        // Two misses in a row means the range he keeps swinging from is not working; back off to
        // re-establish the lunge instead of arcing at the same distance again.
        if (mid && !lungeBlocked && _consecutiveAttackMisses >= 2)
            return CommitCombatBehavior(CombatBehavior.Backstep, duration);

        // Mid band keeps range with an arc; a blocked lunge lane circles at any band to look for a
        // clear one. Close band backs off to re-establish lunge range.
        if (mid || lungeBlocked)
            return CommitCombatBehavior(NextCircleBehavior(), duration);

        return CommitCombatBehavior(CombatBehavior.Backstep, duration);
    }

    // Circle direction alternates per commit, so the maneuver still varies by state without a die roll
    // against the clock. ApplyCombatManeuver also calls this when a backstep has no clearance, which
    // flips the side for the substituted arc.
    private CombatBehavior NextCircleBehavior()
    {
        _nextCombatCircleLeft = !_nextCombatCircleLeft;
        return _nextCombatCircleLeft ? CombatBehavior.CircleLeft : CombatBehavior.CircleRight;
    }

    // Job B3: build the destination the selected maneuver actually wants. May rewrite `behavior` when
    // it substitutes a reachable maneuver for an unreachable one (a backstep with no clearance becomes
    // an arc). Returns false only if a maneuver cannot be expressed here at all; the caller then falls
    // back to the direct destination rather than parking a point in geometry.
    private bool TryBuildManeuverDestination(
        PlayerControllerB target,
        ref CombatBehavior behavior,
        float distance,
        float engageRange,
        Vector3 dirFromPlayer,
        Vector3 directDestination,
        out Vector3 destination)
    {
        switch (behavior)
        {
            case CombatBehavior.Intercept:
                destination = PredictTargetPosition(target, 0.55f, engageRange * 0.85f);
                return true;

            case CombatBehavior.CircleLeft:
            case CombatBehavior.CircleRight:
                return TryBuildCircleArcWaypoint(
                    target,
                    behavior == CombatBehavior.CircleLeft,
                    distance,
                    engageRange,
                    dirFromPlayer,
                    out destination);

            case CombatBehavior.Backstep:
                if (TryBuildBackstepDestination(distance, engageRange, dirFromPlayer, out destination))
                    return true;

                // No room behind him: arc instead of backing into geometry. This is the corridor case
                // the old fixed 1.55x point sampled straight into a wall.
                behavior = NextCircleBehavior();
                return TryBuildCircleArcWaypoint(
                    target,
                    behavior == CombatBehavior.CircleLeft,
                    distance,
                    engageRange,
                    dirFromPlayer,
                    out destination);

            case CombatBehavior.Reorient:
                // Facing work only. The 0.75 m micro-step this used to issue fought the turn-in-place
                // clips for the same beat; holding position lets TryPlayPursuitTurnClip and manual
                // facing own the turn, and the arrival hysteresis in UpdateMovementDestination keeps
                // the hold from demoting MotionState to Idle on the spot.
                destination = transform.position;
                return true;

            default:
                destination = directDestination;
                return true;
        }
    }

    // A circle is a short tangential arc waypoint on the orbit, re-issued every tick, not a parked
    // point beside the player. Two constraints shape the lead angle:
    //   - it must put the waypoint further than StopDistance away, or he "arrives" every tick and the
    //     Walk->Idle->Walk flip is back;
    //   - it is capped (CombatManeuverArcStepDegrees) so the destination sweeps the orbit smoothly.
    // Both keep the per-tick destination step to roughly his travel in one AI interval, which is well
    // under the 2 m [BundyMoveAuth] change threshold, and the source stays whatever the resolver
    // picks — so re-issuing does not spam the authority log.
    private bool TryBuildCircleArcWaypoint(
        PlayerControllerB target,
        bool circleLeft,
        float distance,
        float engageRange,
        Vector3 dirFromPlayer,
        out Vector3 destination)
    {
        // Orbit at his CURRENT separation, clamped into the engagement band, so the arc keeps range
        // instead of dragging him onto a fixed radius the corridor may not have room for.
        float radius = Mathf.Clamp(distance, engageRange * 0.85f, engageRange * 1.25f);
        radius = Mathf.Max(0.5f, radius);

        float lead = Mathf.Max(StopDistance + 0.75f, 1.25f);
        float chordAngle = 2f * Mathf.Asin(Mathf.Clamp01(lead / (2f * radius))) * Mathf.Rad2Deg;
        float step = Mathf.Min(Mathf.Clamp(CombatManeuverArcStepDegrees, 10f, 120f), Mathf.Max(10f, chordAngle));

        Vector3 arcDirection = Quaternion.Euler(0f, circleLeft ? step : -step, 0f) * dirFromPlayer;
        destination = target.transform.position + arcDirection * radius;
        return true;
    }

    // A backstep is a short step to a point he can actually stand on, on a shorter leash than the old
    // 1.55x engage range. Refused outright when the step would be too small to read as a retreat (it
    // would land inside StopDistance and freeze him) or when there is no clearance behind him.
    private bool TryBuildBackstepDestination(float distance, float engageRange, Vector3 dirFromPlayer, out Vector3 destination)
    {
        destination = transform.position;
        float leash = engageRange * CombatManeuverBackstepLeashScale;
        float step = leash - distance;
        if (step < Mathf.Max(CombatManeuverMinBackstepMeters, StopDistance + 0.4f))
            return false;

        Vector3 candidate = transform.position + dirFromPlayer * step;

        // Minimum clearance: he must be able to walk the step plus a margin without scraping geometry,
        // and the mesh has to actually connect the two points (SamplePosition alone would happily drop
        // the destination on the far side of a wall).
        Vector3 probeFrom = transform.position + Vector3.up * 0.9f;
        Vector3 probeTo = candidate + dirFromPlayer * CombatManeuverBackstepClearanceMargin + Vector3.up * 0.9f;
        if (!HasClearWorldLine(probeFrom, probeTo, null))
            return false;

        if (NavMesh.Raycast(transform.position, candidate, out _, NavMesh.AllAreas))
            return false;

        destination = candidate;
        return true;
    }

    // Which behaviours keep their locomotion state when their destination is reached, instead of
    // demoting to Idle. Everything the maneuver layer drives mid-combat: the demotion belongs to the
    // observed-velocity gate (ChainsawVelocityGatedIdleDemotion), which needs him to have actually
    // been standing still, not merely to have touched a waypoint.
    private static bool CombatManeuverHoldsMotionOnArrival(CombatBehavior behavior)
    {
        return behavior == CombatBehavior.CircleLeft ||
            behavior == CombatBehavior.CircleRight ||
            behavior == CombatBehavior.Backstep ||
            behavior == CombatBehavior.Reorient;
    }

    private CombatBehavior CommitCombatBehavior(CombatBehavior behavior, float duration)
    {
        _lastCombatBehavior = _currentCombatBehavior;
        _currentCombatBehavior = behavior;
        _nextCombatBehaviorChangeAt = Time.time + duration;
        return behavior;
    }

    private bool ShouldRun(PlayerControllerB target)
    {
        if (!IsLivingPlayer(target))
            return false;

        if (_strangerTargetingActive &&
            _pursuitMode != PursuitMode.NonTargetInterrupt &&
            _strangerPerceptionState != StrangerPerceptionState.Hunt)
        {
            // Gait is a tell: walk whenever he is not hunting. The one exemption is the stalker-genre
            // distance cheat -- when he is far from EVERY living player and in nobody's line of sight,
            // nobody can witness the sprint, so the tell survives while the cross-map commute does not.
            return CanFarSprintUnobserved();
        }

        if (ShouldForcePostBasicAttackWalk(target))
            return false;

        if (ShouldUseStrangerSnapshotDestination(target))
            return Vector3.Distance(transform.position, GetStrangerSnapshotDestination()) > RunThreshold;

        float distance = TryGetPursuitPathDistance(target, target.transform.position, out float pathDistance)
            ? pathDistance
            : Vector3.Distance(transform.position, target.transform.position);
        return ShouldRunAtDistance(target, distance);
    }

    // True only when no living player could possibly witness the gait break: everyone is beyond the
    // far-sprint distance AND nobody has line of sight to him. Cached on a short interval because it
    // costs an LOS raycast per player, and released with hysteresis so approaching the boundary does
    // not flicker him between walk and run.
    private bool CanFarSprintUnobserved()
    {
        if (!FarSprintEnabled)
        {
            _farSprintActive = false;
            return false;
        }

        if (Time.time < _nextFarSprintEvaluationAt)
            return _farSprintActive;

        _nextFarSprintEvaluationAt = Time.time + FarSprintEvaluationIntervalSeconds;

        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null)
        {
            _farSprintActive = false;
            return false;
        }

        // Once sprinting, hold it until he is well inside the threshold rather than exactly on it.
        float requiredDistance = Mathf.Max(1f, FarSprintMinDistanceMeters) -
            (_farSprintActive ? FarSprintDistanceHysteresisMeters : 0f);
        Vector3 position = transform.position;
        bool anyLivingPlayer = false;
        bool allowed = true;

        foreach (PlayerControllerB player in players)
        {
            if (!IsLivingPlayer(player))
                continue;

            anyLivingPlayer = true;
            float distance = Vector3.Distance(position, player.transform.position);
            if (distance < requiredDistance)
            {
                allowed = false;
                break;
            }

            // Range follows the actual separation so a long clear sightline still counts as observed.
            if (player.HasLineOfSightToPosition(
                    position,
                    FarSprintObserverConeDegrees,
                    Mathf.CeilToInt(distance + 5f),
                    -1f))
            {
                allowed = false;
                break;
            }
        }

        _farSprintActive = allowed && anyLivingPlayer;

        if (DebugLogs && _farSprintActive != _loggedFarSprintActive)
        {
            _loggedFarSprintActive = _farSprintActive;
            LogInfo(BundyLogCategory.Perception, 0,
                $"[BundyPerception] farSprint={(_farSprintActive ? "ENGAGED" : "released")} " +
                $"state={_strangerPerceptionState.ToString().ToUpperInvariant()} threshold={requiredDistance:F0}m");
        }

        return _farSprintActive;
    }

    private float GetCurrentHuntDistance(PlayerControllerB target)
    {
        if (!IsLivingPlayer(target))
            return 0f;

        if (ShouldUseStrangerSnapshotDestination(target))
            return Vector3.Distance(transform.position, GetStrangerSnapshotDestination());

        return TryGetPursuitPathDistance(target, target.transform.position, out float pathDistance)
            ? pathDistance
            : Vector3.Distance(transform.position, target.transform.position);
    }

    protected bool ShouldForcePostBasicAttackWalk(PlayerControllerB target)
    {
        return IsPostBasicAttackCalmWindowActive() &&
            IsLivingPlayer(target);
    }

    private bool IsPostBasicAttackCalmWindowActive()
    {
        return PostBasicAttackWalkSeconds > 0f && Time.time < _postBasicAttackWalkUntil;
    }

    private void EnforcePostBasicAttackCalmWindow()
    {
        if (_currentCombatBehavior != CombatBehavior.DirectChase)
        {
            CombatBehavior previous = _currentCombatBehavior;
            _lastCombatBehavior = previous;
            _currentCombatBehavior = CombatBehavior.DirectChase;
            if (DebugLogs)
                LogDiagnostic($"BundyPostAttackCalm maneuver={previous}->DirectChase, remaining={Mathf.Max(0f, _postBasicAttackWalkUntil - Time.time):F2}s");
        }

        // Do not let the maneuver selector immediately replace DirectChase on the next AI tick.
        _nextCombatBehaviorChangeAt = Mathf.Max(_nextCombatBehaviorChangeAt, _postBasicAttackWalkUntil);
    }

    protected virtual bool ShouldRunAtDistance(PlayerControllerB target, float distance)
    {
        // Standard Bundy's escalated-hunt gait tier is gone; Chainsaw Bundy overrides this.
        return false;
    }
}
