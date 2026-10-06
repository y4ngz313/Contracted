using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Facility.Elevators;

internal sealed class SmartElevatorRideCoordinator
{
    private enum RideStage
    {
        None,
        WaitingForArrival,
        Riding,
    }

    private const float InsideButtonMatchDistance = 0.45f;
    private const float ElevatorEarlyCallDistance = 18f;
    private const float ElevatorForceBoardDistance = 9f;
    private const float ElevatorStallForceDistance = 14f;
    private const float ElevatorStallSeconds = 1.25f;
    private const float ElevatorStallProgressEpsilon = 0.45f;
    private const float ElevatorApproachSampleDistance = 7f;
    private const float ElevatorProgressLogIntervalSeconds = 1.5f;
    private const float ElevatorWaitPauseSeconds = 0.25f;
    private const float ElevatorCommandPauseSeconds = 0.45f;
    private const string ElevatorInterfaceTypeName = "PathfindingLib.API.SmartPathfinding.IElevator";

    private RideStage _stage;
    private object _activeFloor;
    private object _activeElevator;
    private Vector3 _rideOffset;
    private Vector3 _lastApproachDestination;
    private float _lastApproachDistance = float.PositiveInfinity;
    private float _lastApproachProgressAt;
    private float _nextProgressLogAt;

    // A ride owns the agent: KeepActorOnElevator/ForceActorOntoElevator call ResetPath every tick,
    // so "no path" is the expected state here and no path watchdog may read it as a failure.
    internal bool RideInProgress => _stage != RideStage.None;

    internal bool TryContinueRide(
        EnemyAI actor,
        NavMeshAgent agent,
        Action<Vector3> issueDestination,
        Action<float> pause,
        Action<string> log,
        out Vector3 issuedDestination,
        out bool restartTask)
    {
        issuedDestination = Vector3.zero;
        restartTask = false;
        if (_stage == RideStage.None)
            return false;

        if (actor == null || agent == null || _activeFloor == null || !TryGetElevatorInfo(_activeFloor, out ElevatorInfo info, log))
        {
            Reset();
            restartTask = true;
            return false;
        }

        if (!ReferenceEquals(info.Elevator, _activeElevator))
        {
            Reset();
            restartTask = true;
            return false;
        }

        switch (_stage)
        {
            case RideStage.WaitingForArrival:
                issuedDestination = TryResolveElevatorApproachPoint(actor.transform.position, info.CallButton.position, out Vector3 callApproach)
                    ? callApproach
                    : info.CallButton.position;
                if (IsFloorCurrentAndOpen(info, _activeFloor))
                {
                    log?.Invoke(
                        $"Elevator ride arrival: floor={FloorLabel(_activeFloor)}, " +
                        $"callButton={FormatVector(issuedDestination)}, inside={FormatVector(info.InsideButton.position)}.");
                    ResetApproachTracking();
                    Reset();
                    restartTask = true;
                    return true;
                }

                issueDestination?.Invoke(issuedDestination);
                pause?.Invoke(ElevatorWaitPauseSeconds);
                LogProgress(
                    log,
                    $"Elevator ride progress: waiting for car at {FloorLabel(_activeFloor)}, " +
                    $"doorsOpen={info.DoorsAreOpen}, current={FloorLabel(info.CurrentFloor)}, " +
                    $"target={FloorLabel(info.TargetFloor)}, actor={FormatVector(actor.transform.position)}.");
                return true;

            case RideStage.Riding:
                KeepActorOnElevator(actor, agent, info);
                issuedDestination = info.InsideButton.position + _rideOffset;
                if (IsFloorCurrentAndOpen(info, _activeFloor))
                {
                    log?.Invoke(
                        $"Elevator ride exit: destinationFloor={FloorLabel(_activeFloor)}, " +
                        $"inside={FormatVector(info.InsideButton.position)}, actor={FormatVector(actor.transform.position)}.");
                    Reset();
                    restartTask = true;
                    return true;
                }

                pause?.Invoke(ElevatorWaitPauseSeconds);
                LogProgress(
                    log,
                    $"Elevator ride progress: riding toward {FloorLabel(_activeFloor)}, " +
                    $"doorsOpen={info.DoorsAreOpen}, current={FloorLabel(info.CurrentFloor)}, " +
                    $"target={FloorLabel(info.TargetFloor)}, actor={FormatVector(actor.transform.position)}, " +
                    $"inside={FormatVector(info.InsideButton.position)}.");
                return true;
        }

        Reset();
        restartTask = true;
        return false;
    }

    internal bool TryHandleElevatorDestination(
        EnemyAI actor,
        NavMeshAgent agent,
        object elevatorFloor,
        Vector3 linkPosition,
        float activateDistance,
        bool canActivate,
        Action<Vector3> issueDestination,
        Action<float> pause,
        Action<string> log,
        out Vector3 issuedDestination,
        out bool restartTask)
    {
        issuedDestination = Vector3.zero;
        restartTask = false;
        if (actor == null || agent == null || elevatorFloor == null)
            return false;
        if (!TryGetElevatorInfo(elevatorFloor, out ElevatorInfo info, log))
            return false;

        bool rideDestination = Vector3.Distance(linkPosition, info.InsideButton.position) <= InsideButtonMatchDistance;
        if (rideDestination)
        {
            issuedDestination = TryResolveElevatorApproachPoint(actor.transform.position, info.InsideButton.position, out Vector3 boardApproach)
                ? boardApproach
                : info.InsideButton.position;

            bool actorInsideElevator = info.IsInsideElevator(actor.transform.position);
            bool closeEnoughToBoard = Vector3.Distance(actor.transform.position, issuedDestination) <= ElevatorForceBoardDistance ||
                Vector3.Distance(actor.transform.position, linkPosition) <= ElevatorForceBoardDistance ||
                canActivate;
            bool stalledNearElevator = IsElevatorApproachStalled(actor, agent, issuedDestination);

            if (!actorInsideElevator)
            {
                issueDestination?.Invoke(issuedDestination);
                if (!info.DoorsAreOpen)
                {
                    LogProgress(
                        log,
                        $"Elevator ride boarding approach: destinationFloor={FloorLabel(elevatorFloor)}, " +
                        $"doorsOpen={info.DoorsAreOpen}, current={FloorLabel(info.CurrentFloor)}, " +
                        $"target={FloorLabel(info.TargetFloor)}, approach={FormatVector(issuedDestination)}, " +
                        $"actor={FormatVector(actor.transform.position)}.");
                    return true;
                }

                if (!closeEnoughToBoard && !stalledNearElevator)
                {
                    LogProgress(
                        log,
                        $"Elevator ride boarding approach: destinationFloor={FloorLabel(elevatorFloor)}, " +
                        $"approach={FormatVector(issuedDestination)}, inside={FormatVector(info.InsideButton.position)}, " +
                        $"actor={FormatVector(actor.transform.position)}, distance={Vector3.Distance(actor.transform.position, issuedDestination):F2}.");
                    return true;
                }

                log?.Invoke(
                    $"Elevator ride force boarding: destinationFloor={FloorLabel(elevatorFloor)}, " +
                    $"reason={(stalledNearElevator ? "stalled" : "near-open-car")}, approach={FormatVector(issuedDestination)}, " +
                    $"inside={FormatVector(info.InsideButton.position)}, actor={FormatVector(actor.transform.position)}.");
                ForceActorOntoElevator(actor, agent, info);
                actorInsideElevator = true;
            }

            if (!actorInsideElevator)
                return true;

            log?.Invoke(
                $"Elevator ride boarding: destinationFloor={FloorLabel(elevatorFloor)}, " +
                $"inside={FormatVector(info.InsideButton.position)}, actor={FormatVector(actor.transform.position)}.");
            info.CallFloor(elevatorFloor);
            _stage = RideStage.Riding;
            _activeFloor = elevatorFloor;
            _activeElevator = info.Elevator;
            _rideOffset = actor.transform.position - info.InsideButton.position;
            ResetApproachTracking();
            _nextProgressLogAt = 0f;
            pause?.Invoke(ElevatorCommandPauseSeconds);
            issuedDestination = info.InsideButton.position + _rideOffset;
            return true;
        }

        issuedDestination = TryResolveElevatorApproachPoint(actor.transform.position, info.CallButton.position, out Vector3 callApproach)
            ? callApproach
            : info.CallButton.position;
        issueDestination?.Invoke(issuedDestination);
        info.CallFloor(elevatorFloor);

        float linkDistance = Vector3.Distance(actor.transform.position, linkPosition);
        bool earlyCall = linkDistance > activateDistance || !canActivate;
        bool withinEarlyCallRange = linkDistance <= ElevatorEarlyCallDistance;
        log?.Invoke(
            $"{(earlyCall ? "Elevator ride early call" : "Elevator ride call")}: requestedFloor={FloorLabel(elevatorFloor)}, " +
            $"callButton={FormatVector(info.CallButton.position)}, approach={FormatVector(issuedDestination)}, " +
            $"actor={FormatVector(actor.transform.position)}, linkDistance={linkDistance:F2}, " +
            $"withinEarlyRange={withinEarlyCallRange}.");

        if (IsFloorCurrentAndOpen(info, elevatorFloor))
        {
            log?.Invoke(
                $"Elevator ride arrival: floor={FloorLabel(elevatorFloor)}, already open; " +
                $"inside={FormatVector(info.InsideButton.position)}.");
            ResetApproachTracking();
            restartTask = true;
            return true;
        }

        _stage = RideStage.WaitingForArrival;
        _activeFloor = elevatorFloor;
        _activeElevator = info.Elevator;
        _rideOffset = Vector3.zero;
        _nextProgressLogAt = 0f;
        pause?.Invoke(ElevatorCommandPauseSeconds);
        return true;
    }

    internal void Reset()
    {
        _stage = RideStage.None;
        _activeFloor = null;
        _activeElevator = null;
        _rideOffset = Vector3.zero;
        ResetApproachTracking();
        _nextProgressLogAt = 0f;
    }

    private static bool IsFloorCurrentAndOpen(ElevatorInfo info, object floor)
    {
        return info.DoorsAreOpen && ReferenceEquals(info.CurrentFloor, floor);
    }

    private static void KeepActorOnElevator(EnemyAI actor, NavMeshAgent agent, ElevatorInfo info)
    {
        actor.MoveWithMineshaftElevator();
        Vector3 target = info.InsideButton.position;
        if (agent != null && agent.enabled)
        {
            try
            {
                if (agent.isOnNavMesh)
                {
                    agent.ResetPath();
                    agent.isStopped = true;
                    agent.Warp(actor.transform.position);
                }
            }
            catch
            {
            }
        }

        actor.serverPosition = actor.transform.position;
        if (Vector3.Distance(actor.transform.position, target) > 3f)
        {
            actor.transform.position = target;
            actor.serverPosition = target;
        }
    }

    private static bool TryResolveElevatorApproachPoint(Vector3 actorPosition, Vector3 anchor, out Vector3 approach)
    {
        if (NavMesh.SamplePosition(anchor, out NavMeshHit direct, ElevatorApproachSampleDistance, NavMesh.AllAreas))
        {
            approach = direct.position;
            return true;
        }

        Vector3 awayFromAnchor = actorPosition - anchor;
        awayFromAnchor.y = 0f;
        if (awayFromAnchor.sqrMagnitude < 0.01f)
            awayFromAnchor = Vector3.forward;
        awayFromAnchor.Normalize();

        float[] offsets = { 1.5f, 3f, 5f, 7f, 9f };
        for (int i = 0; i < offsets.Length; i++)
        {
            Vector3 candidate = anchor + awayFromAnchor * offsets[i];
            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, ElevatorApproachSampleDistance, NavMesh.AllAreas))
            {
                approach = hit.position;
                return true;
            }
        }

        approach = anchor;
        return false;
    }

    private bool IsElevatorApproachStalled(EnemyAI actor, NavMeshAgent agent, Vector3 approachDestination)
    {
        if (actor == null)
            return false;

        float currentDistance = Vector3.Distance(actor.transform.position, approachDestination);
        if (currentDistance > ElevatorStallForceDistance)
        {
            ResetApproachTracking();
            return false;
        }

        if (Vector3.Distance(_lastApproachDestination, approachDestination) > 1.5f ||
            float.IsInfinity(_lastApproachDistance) ||
            Time.time <= 0f)
        {
            _lastApproachDestination = approachDestination;
            _lastApproachDistance = currentDistance;
            _lastApproachProgressAt = Time.time;
            return false;
        }

        if (currentDistance + ElevatorStallProgressEpsilon < _lastApproachDistance)
        {
            _lastApproachDistance = currentDistance;
            _lastApproachProgressAt = Time.time;
            return false;
        }

        bool noRecentProgress = Time.time - _lastApproachProgressAt >= ElevatorStallSeconds;
        return noRecentProgress && currentDistance <= ElevatorStallForceDistance;
    }

    private static void ForceActorOntoElevator(EnemyAI actor, NavMeshAgent agent, ElevatorInfo info)
    {
        if (actor == null)
            return;

        Vector3 target = info.InsideButton.position;
        if (agent != null && agent.enabled)
        {
            try
            {
                if (agent.isOnNavMesh)
                {
                    agent.ResetPath();
                    agent.Warp(target);
                }
            }
            catch
            {
            }
        }

        actor.transform.position = target;
        actor.serverPosition = target;
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

        _nextProgressLogAt = Time.time + ElevatorProgressLogIntervalSeconds;
        log(message);
    }

    private static bool TryGetElevatorInfo(object elevatorFloor, out ElevatorInfo info, Action<string> log = null)
    {
        info = default;
        if (elevatorFloor == null)
            return LogElevatorInfoFailure(log, null, null, "ElevatorFloor value");

        Type floorType = elevatorFloor.GetType();
        PropertyInfo elevatorProperty = floorType.GetProperty("Elevator", BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo callButtonProperty = floorType.GetProperty("CallButtonNavMeshNode", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo callElevatorMethod = floorType.GetMethod("CallElevator", BindingFlags.Public | BindingFlags.Instance);
        if (elevatorProperty == null)
            return LogElevatorInfoFailure(log, floorType, null, "ElevatorFloor.Elevator");
        if (callButtonProperty == null)
            return LogElevatorInfoFailure(log, floorType, null, "ElevatorFloor.CallButtonNavMeshNode");
        if (callElevatorMethod == null)
            return LogElevatorInfoFailure(log, floorType, null, "ElevatorFloor.CallElevator()");

        object elevator = elevatorProperty?.GetValue(elevatorFloor);
        Transform callButton = callButtonProperty?.GetValue(elevatorFloor) as Transform;
        if (elevator == null)
            return LogElevatorInfoFailure(log, floorType, null, "ElevatorFloor.Elevator value");
        Type elevatorType = elevator.GetType();
        if (callButton == null)
            return LogElevatorInfoFailure(log, floorType, elevatorType, "ElevatorFloor.CallButtonNavMeshNode value");

        Type elevatorInterfaceType = ResolveElevatorInterfaceType(elevator);
        if (elevatorInterfaceType == null)
            return LogElevatorInfoFailure(log, floorType, elevatorType, ElevatorInterfaceTypeName);

        PropertyInfo insideButtonProperty = elevatorInterfaceType.GetProperty("InsideButtonNavMeshNode", BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo doorsOpenProperty = elevatorInterfaceType.GetProperty("DoorsAreOpen", BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo currentFloorProperty = elevatorInterfaceType.GetProperty("CurrentFloor", BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo targetFloorProperty = elevatorInterfaceType.GetProperty("TargetFloor", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo isInsideMethod = elevatorInterfaceType.GetMethod("IsInsideElevator", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Vector3) }, null);
        if (insideButtonProperty == null)
            return LogElevatorInfoFailure(log, floorType, elevatorType, "IElevator.InsideButtonNavMeshNode");
        if (doorsOpenProperty == null)
            return LogElevatorInfoFailure(log, floorType, elevatorType, "IElevator.DoorsAreOpen");
        if (currentFloorProperty == null)
            return LogElevatorInfoFailure(log, floorType, elevatorType, "IElevator.CurrentFloor");
        if (targetFloorProperty == null)
            return LogElevatorInfoFailure(log, floorType, elevatorType, "IElevator.TargetFloor");
        if (isInsideMethod == null)
            return LogElevatorInfoFailure(log, floorType, elevatorType, "IElevator.IsInsideElevator");

        Transform insideButton = insideButtonProperty?.GetValue(elevator) as Transform;
        if (insideButton == null)
            return LogElevatorInfoFailure(log, floorType, elevatorType, "IElevator.InsideButtonNavMeshNode value");

        info = new ElevatorInfo(
            elevator,
            elevatorFloor,
            callButton,
            insideButton,
            callElevatorMethod,
            isInsideMethod,
            doorsOpenProperty,
            currentFloorProperty,
            targetFloorProperty);
        return true;
    }

    private static Type ResolveElevatorInterfaceType(object elevator)
    {
        Type interfaceType = FindType(ElevatorInterfaceTypeName);
        if (interfaceType != null && (elevator == null || interfaceType.IsInstanceOfType(elevator)))
            return interfaceType;

        Type elevatorType = elevator?.GetType();
        if (elevatorType == null)
            return interfaceType;

        Type[] interfaces = elevatorType.GetInterfaces();
        for (int i = 0; i < interfaces.Length; i++)
        {
            Type candidate = interfaces[i];
            if (candidate != null && string.Equals(candidate.FullName, ElevatorInterfaceTypeName, StringComparison.Ordinal))
                return candidate;
        }

        return interfaceType;
    }

    private static Type FindType(string fullName)
    {
        Type type = Type.GetType(fullName, throwOnError: false);
        if (type != null)
            return type;

        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            try
            {
                type = assemblies[i].GetType(fullName, throwOnError: false);
                if (type != null)
                    return type;
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool LogElevatorInfoFailure(Action<string> log, Type floorType, Type elevatorType, string missingMember)
    {
        log?.Invoke(
            $"Elevator ride info unavailable: missing {missingMember}; " +
            $"floorType={(floorType != null ? floorType.FullName : "<null>")}, " +
            $"elevatorType={(elevatorType != null ? elevatorType.FullName : "<null>")}.");
        return false;
    }

    private static string FloorLabel(object floor)
    {
        return floor != null ? floor.ToString() : "<none>";
    }

    private static string FormatVector(Vector3 value)
    {
        return $"({value.x:F2}, {value.y:F2}, {value.z:F2})";
    }

    private readonly struct ElevatorInfo
    {
        internal readonly object Elevator;
        internal readonly object Floor;
        internal readonly Transform CallButton;
        internal readonly Transform InsideButton;
        private readonly MethodInfo _callElevatorMethod;
        private readonly MethodInfo _isInsideMethod;
        private readonly PropertyInfo _doorsOpenProperty;
        private readonly PropertyInfo _currentFloorProperty;
        private readonly PropertyInfo _targetFloorProperty;

        internal ElevatorInfo(
            object elevator,
            object floor,
            Transform callButton,
            Transform insideButton,
            MethodInfo callElevatorMethod,
            MethodInfo isInsideMethod,
            PropertyInfo doorsOpenProperty,
            PropertyInfo currentFloorProperty,
            PropertyInfo targetFloorProperty)
        {
            Elevator = elevator;
            Floor = floor;
            CallButton = callButton;
            InsideButton = insideButton;
            _callElevatorMethod = callElevatorMethod;
            _isInsideMethod = isInsideMethod;
            _doorsOpenProperty = doorsOpenProperty;
            _currentFloorProperty = currentFloorProperty;
            _targetFloorProperty = targetFloorProperty;
        }

        internal bool DoorsAreOpen => (bool)_doorsOpenProperty.GetValue(Elevator);

        internal object CurrentFloor => _currentFloorProperty.GetValue(Elevator);

        internal object TargetFloor => _targetFloorProperty?.GetValue(Elevator);

        internal void CallFloor(object floor)
        {
            _callElevatorMethod.Invoke(floor ?? Floor, Array.Empty<object>());
        }

        internal bool IsInsideElevator(Vector3 position)
        {
            return (bool)_isInsideMethod.Invoke(Elevator, new object[] { position });
        }
    }
}
