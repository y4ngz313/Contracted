using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Facility.Elevators;

internal sealed class AnimatedElevatorRideCoordinator
{
    private enum RideStage
    {
        None,
        ApproachCall,
        WaitingForCar,
        ApproachBoard,
        Riding,
    }

    private const float MaxElevatorRouteHorizontalDistance = 120f;
    private const float MinimumFloorHeight = 4f;
    private const float DoorApproachSampleDistance = 8f;
    private const float DoorApproachNearDistance = 1.35f;
    private const float BoardAlignmentRange = 1.5f;
    private const float ApproachStallDistance = 1.75f;
    private const float ApproachStallSeconds = 1.35f;
    private const float ApproachProgressEpsilon = 0.35f;
    private const float TravelArrivalThreshold = 1.65f;
    private const float TravelMinimumSeconds = 2.0f;
    // #289 V1b: one budget used to cover both halves of a ride at 14s. Waiting for the car is a
    // request that can legitimately be refused (car busy, another actor riding), so a short cap there
    // is right -- it re-elects a route. The RIDE is not: once boarded, KeepActorCarried parents the
    // actor to the car every tick and he is safe for as long as the car takes, and a mineshaft shaft
    // traversal is comfortably longer than 14s. Sharing the cap turned every long shaft into
    // abort -> warp back to the boarding floor -> re-elect the same elevator, forever.
    private const float CallWaitMaximumSeconds = 14f;
    private const float RideMaximumSeconds = 60f;
    private const float ToggleCooldownSeconds = 1.0f;
    private const float ProgressLogIntervalSeconds = 1.5f;
    private const float WaitPauseSeconds = 0.18f;
    private const float CommandPauseSeconds = 0.35f;

    private static readonly List<HellevatorInfo> CachedElevators = new List<HellevatorInfo>();
    private static readonly HashSet<int> LoggedDetections = new HashSet<int>();
    private static RoundManager _cachedRound;
    private static int _cachedRoundKey = int.MinValue;
    private static bool _cacheBuilt;
    private static bool _loggedMissingAnchors;

    private RideStage _stage;
    private HellevatorInfo _activeElevator;
    private FloorInfo _sourceFloor;
    private FloorInfo _targetFloor;
    private Vector3 _sourceApproach;
    private Vector3 _targetExit;
    private Vector3 _finalDestination;
    private Vector3 _lastApproachDestination;
    private float _lastApproachDistance = float.PositiveInfinity;
    private float _lastApproachProgressAt;
    private float _nextProgressLogAt;
    private float _nextToggleAllowedAt;
    private float _travelStartedAt;
    private Transform _originalParent;
    private bool _actorParented;
    private bool _savedAgentState;
    private bool _agentUpdatePosition;
    private bool _agentUpdateRotation;
    private bool _agentWasStopped;

    // A ride owns the agent: TryBoardActor/KeepActorCarried call ResetPath every tick, so "no path"
    // is the expected state here and no path watchdog may read it as a failure.
    internal bool RideInProgress => _stage != RideStage.None;

    internal static bool HasAvailableElevator(Action<string> log = null)
    {
        RefreshElevatorCache(log);
        for (int i = CachedElevators.Count - 1; i >= 0; i--)
        {
            if (CachedElevators[i].IsValid)
                return true;

            CachedElevators.RemoveAt(i);
        }

        return false;
    }

    internal bool TryContinueRide(
        EnemyAI actor,
        NavMeshAgent agent,
        Vector3 finalDestination,
        Action<Vector3> issueDestination,
        Action<float> pause,
        Action<string> log,
        out Vector3 issuedDestination,
        out bool restartRoute)
    {
        issuedDestination = Vector3.zero;
        restartRoute = false;
        if (_stage == RideStage.None)
            return false;

        if (actor == null || agent == null || !HasActorAuthority(actor) || !_activeElevator.IsValid)
        {
            AbortRide(actor, agent, log, "missing actor/agent/authority/elevator");
            restartRoute = true;
            return false;
        }

        _finalDestination = finalDestination;
        return TickRide(actor, agent, issueDestination, pause, log, out issuedDestination, out restartRoute);
    }

    internal bool TryHandleDestination(
        EnemyAI actor,
        NavMeshAgent agent,
        Vector3 finalDestination,
        Action<Vector3> issueDestination,
        Action<float> pause,
        Action<string> log,
        out Vector3 issuedDestination,
        out bool restartRoute)
    {
        issuedDestination = Vector3.zero;
        restartRoute = false;
        if (_stage != RideStage.None)
            return TryContinueRide(actor, agent, finalDestination, issueDestination, pause, log, out issuedDestination, out restartRoute);

        if (actor == null || agent == null || !HasActorAuthority(actor) || !agent.enabled || !agent.isOnNavMesh)
            return false;

        if (!TryFindRideRoute(actor, agent, finalDestination, log, out HellevatorRoute route))
            return false;

        _activeElevator = route.Elevator;
        _sourceFloor = route.SourceFloor;
        _targetFloor = route.TargetFloor;
        _sourceApproach = route.SourceApproach;
        _targetExit = route.TargetExit;
        _finalDestination = finalDestination;
        _stage = IsCarAtFloor(_activeElevator, _sourceFloor) ? RideStage.ApproachBoard : RideStage.ApproachCall;
        _nextProgressLogAt = 0f;
        _nextToggleAllowedAt = 0f;
        ResetApproachTracking();

        log?.Invoke(
            $"Animated elevator route selected: elevator='{_activeElevator.Root.name}', " +
            $"source={_sourceFloor.Label}, target={_targetFloor.Label}, car={CurrentCarFloorLabel(_activeElevator)}, " +
            $"approach={FormatVector(_sourceApproach)}, exit={FormatVector(_targetExit)}, final={FormatVector(finalDestination)}.");

        return TickRide(actor, agent, issueDestination, pause, log, out issuedDestination, out restartRoute);
    }

    internal void Reset()
    {
        _stage = RideStage.None;
        _activeElevator = default;
        _sourceFloor = default;
        _targetFloor = default;
        _sourceApproach = Vector3.zero;
        _targetExit = Vector3.zero;
        _finalDestination = Vector3.zero;
        _originalParent = null;
        _actorParented = false;
        _savedAgentState = false;
        _nextToggleAllowedAt = 0f;
        _travelStartedAt = 0f;
        ResetApproachTracking();
        _nextProgressLogAt = 0f;
    }

    private bool TickRide(
        EnemyAI actor,
        NavMeshAgent agent,
        Action<Vector3> issueDestination,
        Action<float> pause,
        Action<string> log,
        out Vector3 issuedDestination,
        out bool restartRoute)
    {
        issuedDestination = _sourceApproach;
        restartRoute = false;

        try
        {
            switch (_stage)
            {
                case RideStage.ApproachCall:
                    issuedDestination = _sourceApproach;
                    issueDestination?.Invoke(issuedDestination);
                    if (!IsNearOrStalled(actor, agent, _sourceApproach))
                    {
                        LogProgress(log, $"Animated elevator approach call: elevator='{_activeElevator.Root.name}', floor={_sourceFloor.Label}, actor={FormatVector(actor.transform.position)}, approach={FormatVector(_sourceApproach)}, car={CurrentCarFloorLabel(_activeElevator)}.");
                        return true;
                    }

                    if (!TryToggleElevator(_activeElevator, log, $"call to {_sourceFloor.Label}"))
                        return true;

                    _stage = RideStage.WaitingForCar;
                    _travelStartedAt = Time.time;
                    ResetApproachTracking();
                    pause?.Invoke(CommandPauseSeconds);
                    log?.Invoke($"Animated elevator call: elevator='{_activeElevator.Root.name}', requestedFloor={_sourceFloor.Label}, actor={FormatVector(actor.transform.position)}.");
                    return true;

                case RideStage.WaitingForCar:
                    issuedDestination = _sourceApproach;
                    issueDestination?.Invoke(issuedDestination);
                    if (IsCarAtFloor(_activeElevator, _sourceFloor) && Time.time >= _travelStartedAt + TravelMinimumSeconds)
                    {
                        _stage = RideStage.ApproachBoard;
                        ResetApproachTracking();
                        log?.Invoke($"Animated elevator arrived for boarding: elevator='{_activeElevator.Root.name}', floor={_sourceFloor.Label}, car={FormatVector(GetCarReferencePosition(_activeElevator))}.");
                        return true;
                    }

                    if (Time.time >= _travelStartedAt + CallWaitMaximumSeconds)
                    {
                        AbortRide(actor, agent, log, $"call timed out waiting for {_sourceFloor.Label}");
                        restartRoute = true;
                        return false;
                    }

                    pause?.Invoke(WaitPauseSeconds);
                    LogProgress(log, $"Animated elevator waiting: elevator='{_activeElevator.Root.name}', requestedFloor={_sourceFloor.Label}, car={CurrentCarFloorLabel(_activeElevator)}, actor={FormatVector(actor.transform.position)}.");
                    return true;

                case RideStage.ApproachBoard:
                    issuedDestination = _sourceApproach;
                    if (!IsCarAtFloor(_activeElevator, _sourceFloor))
                    {
                        _stage = RideStage.ApproachCall;
                        ResetApproachTracking();
                        log?.Invoke($"Animated elevator boarding interrupted: elevator='{_activeElevator.Root.name}', car moved to {CurrentCarFloorLabel(_activeElevator)}; recalling {_sourceFloor.Label}.");
                        return true;
                    }

                    issueDestination?.Invoke(issuedDestination);
                    if (!IsNearOrStalled(actor, agent, _sourceApproach)
                        && Vector3.Distance(actor.transform.position, _sourceApproach) > BoardAlignmentRange)
                    {
                        LogProgress(log, $"Animated elevator boarding approach: elevator='{_activeElevator.Root.name}', floor={_sourceFloor.Label}, actor={FormatVector(actor.transform.position)}, approach={FormatVector(_sourceApproach)}.");
                        return true;
                    }

                    if (!TryBoardActor(actor, agent, _activeElevator, log))
                    {
                        AbortRide(actor, agent, log, "boarding failed");
                        restartRoute = true;
                        return false;
                    }

                    if (!TryToggleElevator(_activeElevator, log, $"travel to {_targetFloor.Label}"))
                    {
                        AbortRide(actor, agent, log, "travel toggle failed");
                        restartRoute = true;
                        return false;
                    }

                    _stage = RideStage.Riding;
                    _travelStartedAt = Time.time;
                    issuedDestination = actor.transform.position;
                    pause?.Invoke(CommandPauseSeconds);
                    log?.Invoke($"Animated elevator travel start: elevator='{_activeElevator.Root.name}', from={_sourceFloor.Label}, to={_targetFloor.Label}, actor={FormatVector(actor.transform.position)}, car={FormatVector(GetCarReferencePosition(_activeElevator))}.");
                    return true;

                case RideStage.Riding:
                    KeepActorCarried(actor, agent, _activeElevator);
                    issuedDestination = actor.transform.position;
                    if (Time.time >= _travelStartedAt + TravelMinimumSeconds && IsCarAtFloor(_activeElevator, _targetFloor))
                    {
                        CompleteRide(actor, agent, issueDestination, pause, log, out issuedDestination);
                        restartRoute = true;
                        return true;
                    }

                    if (Time.time >= _travelStartedAt + RideMaximumSeconds)
                    {
                        AbortRide(actor, agent, log, $"travel timed out toward {_targetFloor.Label} after {RideMaximumSeconds:F0}s");
                        restartRoute = true;
                        return false;
                    }

                    pause?.Invoke(WaitPauseSeconds);
                    LogProgress(log, $"Animated elevator riding: elevator='{_activeElevator.Root.name}', target={_targetFloor.Label}, car={CurrentCarFloorLabel(_activeElevator)}, actor={FormatVector(actor.transform.position)}.");
                    return true;
            }
        }
        catch (Exception e)
        {
            AbortRide(actor, agent, log, $"exception: {e.Message}");
            restartRoute = true;
            return false;
        }

        Reset();
        restartRoute = true;
        return false;
    }

    private static bool TryFindRideRoute(EnemyAI actor, NavMeshAgent agent, Vector3 finalDestination, Action<string> log, out HellevatorRoute route)
    {
        route = default;
        RefreshElevatorCache(log);
        if (CachedElevators.Count == 0)
            return false;

        if (HasCompletePath(agent, finalDestination))
            return false;

        float bestScore = float.PositiveInfinity;
        for (int i = 0; i < CachedElevators.Count; i++)
        {
            HellevatorInfo elevator = CachedElevators[i];
            if (!elevator.IsValid)
                continue;

            if (PlanarDistance(actor.transform.position, elevator.Root.transform.position) > MaxElevatorRouteHorizontalDistance &&
                PlanarDistance(finalDestination, elevator.Root.transform.position) > MaxElevatorRouteHorizontalDistance)
            {
                continue;
            }

            FloorInfo sourceFloor = NearestFloorByHeight(elevator, actor.transform.position);
            FloorInfo targetFloor = NearestFloorByHeight(elevator, finalDestination);
            if (sourceFloor.SameFloor(targetFloor))
                continue;

            if (!TryResolveDoorPoint(actor.transform.position, sourceFloor.Anchor, out Vector3 sourceApproach))
                continue;

            if (!TryResolveDoorPoint(finalDestination, targetFloor.Anchor, out Vector3 targetExit))
                targetExit = targetFloor.Anchor;

            if (!TryCalculateCompletePath(agent, sourceApproach, out float approachPathLength))
                continue;

            float targetElevatorDistance = PlanarDistance(finalDestination, targetFloor.Anchor);
            float score = approachPathLength + targetElevatorDistance * 0.2f;
            if (score >= bestScore)
                continue;

            bestScore = score;
            route = new HellevatorRoute(elevator, sourceFloor, targetFloor, sourceApproach, targetExit);
        }

        return !float.IsPositiveInfinity(bestScore);
    }

    private static bool HasActorAuthority(EnemyAI actor)
    {
        return actor != null && (actor.IsOwner || actor.IsServer);
    }

    private static bool HasCompletePath(NavMeshAgent agent, Vector3 destination)
    {
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return false;

        Vector3 resolved = destination;
        if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 4f, NavMesh.AllAreas))
            resolved = hit.position;

        using (NavMeshPathScope scope = new NavMeshPathScope())
        {
            if (!agent.CalculatePath(resolved, scope.Path))
                return false;

            return scope.Path.status == NavMeshPathStatus.PathComplete;
        }
    }

    private static bool TryCalculateCompletePath(NavMeshAgent agent, Vector3 destination, out float pathLength)
    {
        pathLength = float.PositiveInfinity;
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return false;

        using (NavMeshPathScope scope = new NavMeshPathScope())
        {
            if (!agent.CalculatePath(destination, scope.Path) || scope.Path.status != NavMeshPathStatus.PathComplete)
                return false;

            pathLength = EstimatePathLength(scope.Path);
            return true;
        }
    }

    private bool IsNearOrStalled(EnemyAI actor, NavMeshAgent agent, Vector3 approachDestination)
    {
        if (actor == null)
            return false;

        float currentDistance = Vector3.Distance(actor.transform.position, approachDestination);
        if (currentDistance <= DoorApproachNearDistance)
        {
            ResetApproachTracking();
            return true;
        }

        if (currentDistance > ApproachStallDistance)
        {
            ResetApproachTracking();
            return false;
        }

        if (agent != null && agent.enabled && agent.isOnNavMesh && !agent.pathPending && agent.remainingDistance <= DoorApproachNearDistance)
            return true;

        if (Vector3.Distance(_lastApproachDestination, approachDestination) > 1.5f ||
            float.IsInfinity(_lastApproachDistance) ||
            Time.time <= 0f)
        {
            _lastApproachDestination = approachDestination;
            _lastApproachDistance = currentDistance;
            _lastApproachProgressAt = Time.time;
            return false;
        }

        if (currentDistance + ApproachProgressEpsilon < _lastApproachDistance)
        {
            _lastApproachDistance = currentDistance;
            _lastApproachProgressAt = Time.time;
            return false;
        }

        return Time.time - _lastApproachProgressAt >= ApproachStallSeconds;
    }

    private bool TryBoardActor(EnemyAI actor, NavMeshAgent agent, HellevatorInfo elevator, Action<string> log)
    {
        if (actor == null || !elevator.IsValid)
            return false;

        try
        {
            SaveAgentState(agent);
            _originalParent = actor.transform.parent;
            Vector3 boardPoint = GetBoardPoint(elevator, agent);

            if (agent != null && agent.enabled)
            {
                if (agent.isOnNavMesh)
                    agent.ResetPath();
                agent.isStopped = true;
                agent.updatePosition = false;
            }

            actor.transform.position = boardPoint;
            actor.transform.SetParent(elevator.AnimContainer, true);
            actor.serverPosition = actor.transform.position;
            _actorParented = true;
            log?.Invoke($"Animated elevator board: elevator='{elevator.Root.name}', body='{elevator.Body.name}', board={FormatVector(boardPoint)}.");
            return true;
        }
        catch (Exception e)
        {
            log?.Invoke($"Animated elevator board failed: {e.Message}");
            RestoreActorFromManualCarry(actor, agent, _sourceApproach, log);
            return false;
        }
    }

    private void KeepActorCarried(EnemyAI actor, NavMeshAgent agent, HellevatorInfo elevator)
    {
        if (actor == null || !elevator.IsValid)
            return;

        if (!_actorParented)
            actor.transform.SetParent(elevator.AnimContainer, true);

        if (agent != null && agent.enabled)
        {
            try
            {
                if (agent.isOnNavMesh && agent.hasPath)
                    agent.ResetPath();
                agent.isStopped = true;
                agent.updatePosition = false;
            }
            catch
            {
            }
        }

        actor.serverPosition = actor.transform.position;
    }

    private void CompleteRide(
        EnemyAI actor,
        NavMeshAgent agent,
        Action<Vector3> issueDestination,
        Action<float> pause,
        Action<string> log,
        out Vector3 issuedDestination)
    {
        issuedDestination = _targetExit;
        RestoreActorFromManualCarry(actor, agent, _targetExit, log);
        issueDestination?.Invoke(_targetExit);
        pause?.Invoke(CommandPauseSeconds);
        log?.Invoke($"Animated elevator travel end: elevator='{_activeElevator.Root.name}', arrived={_targetFloor.Label}, exit={FormatVector(_targetExit)}, final={FormatVector(_finalDestination)}.");
        Reset();
    }

    private void AbortRide(EnemyAI actor, NavMeshAgent agent, Action<string> log, string reason)
    {
        Vector3 safePosition = _sourceApproach != Vector3.zero ? _sourceApproach : actor != null ? actor.transform.position : Vector3.zero;
        if (_activeElevator.IsValid)
        {
            FloorInfo currentFloor = NearestFloorByHeight(_activeElevator, GetCarReferencePosition(_activeElevator));
            if (TryResolveDoorPoint(safePosition, currentFloor.Anchor, out Vector3 currentDoor))
                safePosition = currentDoor;
        }

        RestoreActorFromManualCarry(actor, agent, safePosition, log);
        log?.Invoke($"Animated elevator ride aborted: reason={reason}, safe={FormatVector(safePosition)}.");
        Reset();
    }

    private void RestoreActorFromManualCarry(EnemyAI actor, NavMeshAgent agent, Vector3 targetPosition, Action<string> log)
    {
        try
        {
            if (actor != null && _actorParented)
                actor.transform.SetParent(_originalParent, true);

            if (agent != null && agent.enabled)
            {
                agent.updatePosition = _savedAgentState ? _agentUpdatePosition : true;
                agent.updateRotation = _savedAgentState ? _agentUpdateRotation : agent.updateRotation;
                if (agent.isOnNavMesh)
                    agent.ResetPath();
            }

            Vector3 resolved = targetPosition;
            if (NavMesh.SamplePosition(targetPosition, out NavMeshHit hit, DoorApproachSampleDistance, NavMesh.AllAreas))
                resolved = hit.position;

            bool warped = false;
            if (agent != null && agent.enabled)
            {
                try
                {
                    warped = agent.Warp(resolved);
                    agent.isStopped = _savedAgentState ? _agentWasStopped : false;
                }
                catch (Exception e)
                {
                    log?.Invoke($"Animated elevator agent restore warp skipped: {e.Message}");
                }
            }

            if (actor != null)
            {
                actor.transform.position = resolved;
                actor.serverPosition = resolved;
            }

            if (!warped && agent != null && agent.enabled && NavMesh.SamplePosition(resolved, out NavMeshHit retry, DoorApproachSampleDistance, NavMesh.AllAreas))
            {
                try
                {
                    agent.Warp(retry.position);
                }
                catch
                {
                }
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"Animated elevator restore failed: {e.Message}");
        }
        finally
        {
            _actorParented = false;
            _originalParent = null;
            _savedAgentState = false;
        }
    }

    private void SaveAgentState(NavMeshAgent agent)
    {
        if (agent == null || _savedAgentState)
            return;

        _savedAgentState = true;
        _agentUpdatePosition = agent.updatePosition;
        _agentUpdateRotation = agent.updateRotation;
        _agentWasStopped = agent.isStopped;
    }

    private bool TryToggleElevator(HellevatorInfo elevator, Action<string> log, string reason)
    {
        if (!elevator.IsValid)
            return false;
        if (Time.time < _nextToggleAllowedAt)
            return false;

        _nextToggleAllowedAt = Time.time + ToggleCooldownSeconds;
        try
        {
            elevator.Trigger.TriggerAnimationNonPlayer(playSecondaryAudios: false, overrideBool: false, setBoolFalse: false);
            log?.Invoke($"Animated elevator toggle: elevator='{elevator.Root.name}', reason={reason}, boolValue={elevator.Trigger.boolValue}, car={FormatVector(GetCarReferencePosition(elevator))}.");
            return true;
        }
        catch (Exception e)
        {
            log?.Invoke($"Animated elevator toggle failed: elevator='{elevator.Root.name}', reason={reason}, error={e.Message}.");
            return false;
        }
    }

    private static void RefreshElevatorCache(Action<string> log)
    {
        RoundManager round = RoundManager.Instance;
        int roundKey = GetRoundKey();
        if (!ReferenceEquals(round, _cachedRound)
            || roundKey != _cachedRoundKey)
        {
            CachedElevators.Clear();
            LoggedDetections.Clear();
            _cachedRound = round;
            _cachedRoundKey = roundKey;
            _cacheBuilt = false;
            _loggedMissingAnchors = false;
        }

        if (_cacheBuilt)
            return;

        _cacheBuilt = true;
        CachedElevators.Clear();

        AnimatedObjectTrigger[] triggers = UnityEngine.Object.FindObjectsOfType<AnimatedObjectTrigger>();
        if (triggers == null || triggers.Length == 0)
            return;

        HashSet<int> seen = new HashSet<int>();
        for (int i = 0; i < triggers.Length; i++)
        {
            AnimatedObjectTrigger trigger = triggers[i];
            if (trigger == null)
                continue;

            GameObject root = ResolveHellevatorRoot(trigger);
            if (root == null)
                continue;

            int id = root.GetInstanceID();
            if (!seen.Add(id))
                continue;

            if (TryCreateInfo(root, trigger, log, out HellevatorInfo info))
            {
                CachedElevators.Add(info);
                if (LoggedDetections.Add(id))
                {
                    log?.Invoke(
                        $"Animated elevator detected: root='{root.name}', lower={FormatVector(info.Lower.Anchor)}, " +
                        $"upper={FormatVector(info.Upper.Anchor)}, car={FormatVector(GetCarReferencePosition(info))}, " +
                        $"height={Mathf.Abs(info.Upper.Anchor.y - info.Lower.Anchor.y):F1}m.");
                }
            }
        }
    }

    private static int GetRoundKey()
    {
        unchecked
        {
            int seed = StartOfRound.Instance != null
                ? StartOfRound.Instance.randomMapSeed
                : 0;
            int scene = UnityEngine.SceneManagement.SceneManager
                .GetActiveScene()
                .handle;
            return seed * 397 ^ scene;
        }
    }

    private static GameObject ResolveHellevatorRoot(AnimatedObjectTrigger trigger)
    {
        if (trigger == null || !LooksLikeHellevatorTrigger(trigger))
            return null;

        Transform current = trigger.transform;
        while (current != null)
        {
            if (current.name.StartsWith("ACF_Hellevator", StringComparison.OrdinalIgnoreCase))
                return current.gameObject;
            current = current.parent;
        }

        GameObject own = trigger.gameObject;
        return LooksLikeHellevatorRoot(own, trigger) ? own : null;
    }

    private static bool TryCreateInfo(GameObject root, AnimatedObjectTrigger trigger, Action<string> log, out HellevatorInfo info)
    {
        info = default;
        if (root == null || trigger == null)
            return false;

        InteractTrigger interact = root.GetComponent<InteractTrigger>() ?? root.GetComponentInChildren<InteractTrigger>(true);
        if (!LooksLikeHellevatorRoot(root, trigger) && !LooksLikeHellevatorInteract(interact))
            return false;

        Transform animContainer = root.transform.Find("AnimContainer") ?? FindChildByName(root.transform, "AnimContainer");
        Transform body = root.transform.Find("AnimContainer/ElevatorMainBody") ?? FindChildByName(root.transform, "ElevatorMainBody");
        if (animContainer == null || body == null)
            return false;

        if (!TryFindFloorAnchor(root.transform, upper: false, out Vector3 lower) ||
            !TryFindFloorAnchor(root.transform, upper: true, out Vector3 upper) ||
            Mathf.Abs(upper.y - lower.y) < MinimumFloorHeight)
        {
            if (!_loggedMissingAnchors)
            {
                _loggedMissingAnchors = true;
                log?.Invoke($"Animated elevator candidate skipped: root='{root.name}' missing usable LowerOuter/UpperOuter anchors.");
            }
            return false;
        }

        if (upper.y < lower.y)
        {
            Vector3 swap = lower;
            lower = upper;
            upper = swap;
        }

        info = new HellevatorInfo(
            root,
            trigger,
            animContainer,
            body,
            new FloorInfo("lower", lower),
            new FloorInfo("upper", upper));
        return true;
    }

    private static bool LooksLikeHellevatorRoot(GameObject root, AnimatedObjectTrigger trigger)
    {
        if (root == null || trigger == null || !LooksLikeHellevatorTrigger(trigger))
            return false;
        if (root.name.StartsWith("ACF_Hellevator", StringComparison.OrdinalIgnoreCase))
            return true;

        return LooksLikeHellevatorInteract(root.GetComponent<InteractTrigger>() ?? root.GetComponentInChildren<InteractTrigger>(true));
    }

    private static bool LooksLikeHellevatorTrigger(AnimatedObjectTrigger trigger)
    {
        return trigger != null &&
            trigger.isBool &&
            string.Equals(trigger.animationString, "Open", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeHellevatorInteract(InteractTrigger trigger)
    {
        return trigger != null &&
            !string.IsNullOrEmpty(trigger.hoverTip) &&
            trigger.hoverTip.IndexOf("Use Elevator", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool TryFindFloorAnchor(Transform root, bool upper, out Vector3 anchor)
    {
        anchor = Vector3.zero;
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        if (TryAverageNamedTransforms(transforms, upper, requireOuter: true, out anchor))
            return true;
        if (TryAverageNamedTransforms(transforms, upper, requireOuter: false, out anchor))
            return true;

        BoxCollider[] colliders = root.GetComponentsInChildren<BoxCollider>(true);
        Vector3 sum = Vector3.zero;
        int count = 0;
        for (int i = 0; i < colliders.Length; i++)
        {
            BoxCollider collider = colliders[i];
            if (collider == null || !NameMatchesFloor(collider.name, upper, requireOuter: true))
                continue;

            sum += collider.transform.TransformPoint(collider.center);
            count++;
        }

        if (count <= 0)
            return false;

        anchor = sum / count;
        return true;
    }

    private static bool TryAverageNamedTransforms(Transform[] transforms, bool upper, bool requireOuter, out Vector3 anchor)
    {
        anchor = Vector3.zero;
        if (transforms == null)
            return false;

        Vector3 sum = Vector3.zero;
        int count = 0;
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform transform = transforms[i];
            if (transform == null || !NameMatchesFloor(transform.name, upper, requireOuter))
                continue;

            sum += transform.position;
            count++;
        }

        if (count <= 0)
            return false;

        anchor = sum / count;
        return true;
    }

    private static bool NameMatchesFloor(string name, bool upper, bool requireOuter)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        string lowerName = name.ToLowerInvariant();
        bool floorMatch = upper ? lowerName.Contains("upper") : lowerName.Contains("lower");
        if (!floorMatch)
            return false;

        return !requireOuter || lowerName.Contains("outer");
    }

    private static Transform FindChildByName(Transform root, string name)
    {
        if (root == null || string.IsNullOrEmpty(name))
            return null;

        Transform[] children = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] != null && string.Equals(children[i].name, name, StringComparison.OrdinalIgnoreCase))
                return children[i];
        }

        return null;
    }

    private static bool TryResolveDoorPoint(Vector3 reference, Vector3 anchor, out Vector3 point)
    {
        if (NavMesh.SamplePosition(anchor, out NavMeshHit direct, DoorApproachSampleDistance, NavMesh.AllAreas))
        {
            point = direct.position;
            return true;
        }

        Vector3 away = reference - anchor;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f)
            away = Vector3.forward;
        away.Normalize();

        float[] offsets = { 1.5f, 3f, 5f, 7f, 10f, 14f };
        for (int i = 0; i < offsets.Length; i++)
        {
            Vector3 candidate = anchor + away * offsets[i];
            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, DoorApproachSampleDistance, NavMesh.AllAreas))
            {
                point = hit.position;
                return true;
            }
        }

        point = anchor;
        return false;
    }

    private static Vector3 GetBoardPoint(HellevatorInfo elevator, NavMeshAgent agent)
    {
        Bounds bounds;
        if (TryGetBodyBounds(elevator.Body, out bounds))
        {
            Vector3 point = bounds.center;
            point.y = bounds.min.y + (agent != null ? Mathf.Max(0f, agent.baseOffset) : 0f);
            return point;
        }

        return elevator.Body.position;
    }

    private static bool TryGetBodyBounds(Transform body, out Bounds bounds)
    {
        bounds = default;
        if (body == null)
            return false;

        bool hasBounds = false;
        Collider[] colliders = body.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null || collider.isTrigger)
                continue;

            if (!hasBounds)
            {
                bounds = collider.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(collider.bounds);
            }
        }

        if (hasBounds)
            return true;

        Renderer[] renderers = body.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
                continue;

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return hasBounds;
    }

    private static bool IsCarAtFloor(HellevatorInfo elevator, FloorInfo floor)
    {
        if (!elevator.IsValid)
            return false;

        float threshold = Mathf.Max(TravelArrivalThreshold, Mathf.Abs(elevator.Upper.Anchor.y - elevator.Lower.Anchor.y) * 0.08f);
        return Mathf.Abs(GetCarReferencePosition(elevator).y - floor.Anchor.y) <= threshold;
    }

    private static FloorInfo NearestFloorByHeight(HellevatorInfo elevator, Vector3 position)
    {
        return Mathf.Abs(position.y - elevator.Upper.Anchor.y) < Mathf.Abs(position.y - elevator.Lower.Anchor.y)
            ? elevator.Upper
            : elevator.Lower;
    }

    private static Vector3 GetCarReferencePosition(HellevatorInfo elevator)
    {
        if (elevator.Body != null)
            return elevator.Body.position;
        if (elevator.AnimContainer != null)
            return elevator.AnimContainer.position;
        return elevator.Root != null ? elevator.Root.transform.position : Vector3.zero;
    }

    private static string CurrentCarFloorLabel(HellevatorInfo elevator)
    {
        if (!elevator.IsValid)
            return "<none>";

        FloorInfo floor = NearestFloorByHeight(elevator, GetCarReferencePosition(elevator));
        return floor.Label;
    }

    private void ResetApproachTracking()
    {
        _lastApproachDestination = Vector3.zero;
        _lastApproachDistance = float.PositiveInfinity;
        _lastApproachProgressAt = 0f;
    }

    private void LogProgress(Action<string> log, string message)
    {
        if (log == null || Time.time < _nextProgressLogAt)
            return;

        _nextProgressLogAt = Time.time + ProgressLogIntervalSeconds;
        log(message);
    }

    private static float EstimatePathLength(NavMeshPath path)
    {
        if (path == null || path.corners == null || path.corners.Length < 2)
            return 0f;

        float length = 0f;
        Vector3 previous = path.corners[0];
        for (int i = 1; i < path.corners.Length; i++)
        {
            Vector3 current = path.corners[i];
            length += Vector3.Distance(previous, current);
            previous = current;
        }

        return length;
    }

    private static float PlanarDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static string FormatVector(Vector3 value)
    {
        return $"({value.x:F2}, {value.y:F2}, {value.z:F2})";
    }

    private readonly struct HellevatorRoute
    {
        internal readonly HellevatorInfo Elevator;
        internal readonly FloorInfo SourceFloor;
        internal readonly FloorInfo TargetFloor;
        internal readonly Vector3 SourceApproach;
        internal readonly Vector3 TargetExit;

        internal HellevatorRoute(HellevatorInfo elevator, FloorInfo sourceFloor, FloorInfo targetFloor, Vector3 sourceApproach, Vector3 targetExit)
        {
            Elevator = elevator;
            SourceFloor = sourceFloor;
            TargetFloor = targetFloor;
            SourceApproach = sourceApproach;
            TargetExit = targetExit;
        }
    }

    private readonly struct HellevatorInfo
    {
        internal readonly GameObject Root;
        internal readonly AnimatedObjectTrigger Trigger;
        internal readonly Transform AnimContainer;
        internal readonly Transform Body;
        internal readonly FloorInfo Lower;
        internal readonly FloorInfo Upper;

        internal HellevatorInfo(GameObject root, AnimatedObjectTrigger trigger, Transform animContainer, Transform body, FloorInfo lower, FloorInfo upper)
        {
            Root = root;
            Trigger = trigger;
            AnimContainer = animContainer;
            Body = body;
            Lower = lower;
            Upper = upper;
        }

        internal bool IsValid => Root != null && Trigger != null && AnimContainer != null && Body != null;
    }

    private readonly struct FloorInfo
    {
        internal readonly string Label;
        internal readonly Vector3 Anchor;

        internal FloorInfo(string label, Vector3 anchor)
        {
            Label = label;
            Anchor = anchor;
        }

        internal bool SameFloor(FloorInfo other)
        {
            return string.Equals(Label, other.Label, StringComparison.Ordinal);
        }
    }

    private sealed class NavMeshPathScope : IDisposable
    {
        internal readonly NavMeshPath Path = new NavMeshPath();

        public void Dispose()
        {
            Path.ClearCorners();
        }
    }
}
