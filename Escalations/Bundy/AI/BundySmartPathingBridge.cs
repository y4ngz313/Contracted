using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Facility.Elevators;

namespace Y4NGZCompany.Escalations.Bundy.AI;

internal sealed class BundySmartPathingBridge : IDisposable
{
    // SmartPathfindingLinkFlags, split by whether crossing the link changes which side of the facility
    // boundary the agent is on. Interior links (internal teleports, elevators) never do; boundary links
    // (main entrance, fire exits) always do.
    //
    // 2026-07-31 (#199): these used to be one const requested unconditionally, and that is what stranded
    // Bundy. With a same-side outdoor target the smart layer answered "route in through the main
    // entrance"; he took it, the crossing route then correctly concluded "target is outside, leave" and
    // walked him back out, and the round was four minutes of that loop with the gap never closing. A
    // boundary link cannot be part of an honest route to a destination on the side he is already on, so
    // the caller now says whether one is admissible and the mask follows.
    private const int FallbackInteriorLinkMask = 1 | 2;  // InternalTeleports | Elevators
    private const int FallbackBoundaryLinkMask = 4 | 8;  // MainEntrance | FireExits
    private const float ActivateDistance = 1.35f;
    private const float RecreateTaskDistance = 4f;
    private const float MinTaskRestartInterval = 0.45f;

    private static int _interiorLinkMask = FallbackInteriorLinkMask;
    private static int _boundaryLinkMask = FallbackBoundaryLinkMask;

    private static bool _resolved;
    private static bool _available;
    private static bool _loggedAvailable;
    private static bool _loggedUnavailable;
    private static Type _taskType;
    private static Type _flagsType;
    private static Type _destinationType;
    private static Type _destinationKindType;
    private static MethodInfo _registerSmartAgent;
    private static MethodInfo _getPathOrigin;
    private static MethodInfo _startPathTask;
    private static MethodInfo _isResultReady;
    private static MethodInfo _getResult;
    private static MethodInfo _disposeTask;
    private static PropertyInfo _destinationKind;
    private static PropertyInfo _destinationPosition;
    private static PropertyInfo _destinationEntranceTeleport;
    private static PropertyInfo _destinationElevatorFloor;
    private static PropertyInfo _destinationInternalTeleport;
    private static MethodInfo _destinationCanActivate;
    private static PropertyInfo _internalTeleportDestination;

    private object _task;
    private Vector3 _lastRequestedDestination;
    private bool _hasRequestedDestination;
    private int _lastRequestedLinkMask;
    private bool _allowBoundaryLinks = true;
    private float _nextTaskStartAllowedAt;
    private readonly SmartElevatorRideCoordinator _elevatorRide = new SmartElevatorRideCoordinator();
    private readonly AnimatedElevatorRideCoordinator _animatedElevatorRide = new AnimatedElevatorRideCoordinator();

    internal bool ElevatorRideInProgress =>
        _elevatorRide.RideInProgress || _animatedElevatorRide.RideInProgress;

    internal static bool IsAvailable
    {
        get
        {
            EnsureResolved();
            return _available;
        }
    }

    internal static void TryRegisterSmartAgent(NavMeshAgent agent)
    {
        if (agent == null || !IsAvailable)
            return;

        try
        {
            _registerSmartAgent?.Invoke(null, new object[] { agent });
        }
        catch (Exception e)
        {
            LogDebug($"Smart path agent registration skipped: {e.Message}");
        }
    }

    // allowBoundaryLinks: whether a route through the main entrance or a fire exit is admissible for
    // this request. False whenever the final destination is known to be on the side of the boundary
    // Bundy is already on — see BundyAIBase.ShouldAllowBoundarySmartLinks.
    internal bool TrySetDestination(
        BundyAIBase bundy,
        NavMeshAgent agent,
        Vector3 finalDestination,
        bool allowBoundaryLinks,
        out Vector3 issuedDestination,
        out bool requestPending,
        out bool noSmartPath)
    {
        issuedDestination = Vector3.zero;
        requestPending = false;
        noSmartPath = false;
        _allowBoundaryLinks = allowBoundaryLinks;
        bool smartPathAvailable = IsAvailable;
        bool animatedElevatorAvailable = AnimatedElevatorRideCoordinator.HasAvailableElevator(LogInfo);
        if (bundy == null || agent == null || !agent.enabled || !agent.isOnNavMesh || (!smartPathAvailable && !animatedElevatorAvailable))
        {
            noSmartPath = true;
            return false;
        }

        try
        {
            if (_animatedElevatorRide.TryContinueRide(
                    bundy,
                    agent,
                    finalDestination,
                    bundy.IssueRawAgentDestination,
                    bundy.PauseForSmartInteraction,
                    LogInfo,
                    out issuedDestination,
                    out bool restartAnimatedElevatorTask))
            {
                if (restartAnimatedElevatorTask && smartPathAvailable)
                    StartPathTask(agent, finalDestination, forceNew: true);
                return true;
            }

            if (_animatedElevatorRide.TryHandleDestination(
                    bundy,
                    agent,
                    finalDestination,
                    bundy.IssueRawAgentDestination,
                    bundy.PauseForSmartInteraction,
                    LogInfo,
                    out issuedDestination,
                    out bool restartAfterAnimatedElevatorStep))
            {
                if (restartAfterAnimatedElevatorStep && smartPathAvailable)
                    StartPathTask(agent, finalDestination, forceNew: true);
                return true;
            }

            if (!smartPathAvailable)
            {
                noSmartPath = true;
                return false;
            }

            if (_elevatorRide.TryContinueRide(
                    bundy,
                    agent,
                    bundy.IssueRawAgentDestination,
                    bundy.PauseForSmartInteraction,
                    LogInfo,
                    out issuedDestination,
                    out bool restartElevatorTask))
            {
                if (restartElevatorTask)
                    StartPathTask(agent, finalDestination, forceNew: true);
                return true;
            }

            // A task computed under a wider link mask can still be holding an entrance answer that is
            // no longer admissible, so a narrowing counts as a destination change and tears it down.
            bool linkMaskChanged = _hasRequestedDestination && GetRequestedLinkMask() != _lastRequestedLinkMask;
            bool destinationChanged = linkMaskChanged ||
                (_hasRequestedDestination && Vector3.Distance(_lastRequestedDestination, finalDestination) > RecreateTaskDistance);
            EnsureTask();
            if (_task == null)
            {
                noSmartPath = true;
                return false;
            }

            if (!_hasRequestedDestination)
            {
                StartPathTask(agent, finalDestination);
                requestPending = true;
                return false;
            }

            if (destinationChanged)
            {
                if (IsCurrentResultReady() || Time.time >= _nextTaskStartAllowedAt)
                    StartPathTask(agent, finalDestination, forceNew: true);
                requestPending = true;
                return false;
            }

            if ((bool)_isResultReady.Invoke(_task, new object[] { 0 }))
            {
                object nullableResult = _getResult.Invoke(_task, new object[] { 0 });
                object destination = UnwrapNullable(nullableResult);
                if (destination == null)
                {
                    noSmartPath = true;
                    StartPathTask(agent, finalDestination);
                    return false;
                }

                int kind = Convert.ToInt32(_destinationKind.GetValue(destination));
                Vector3 position = (Vector3)_destinationPosition.GetValue(destination);
                // Belt and braces on the mask: whatever the link flags did or did not exclude upstream,
                // an entrance answer for a same-side destination is refused here rather than walked to.
                // Reported as "no route" so the caller falls through to ordinary navigation instead of
                // holding for a result that will never become admissible.
                if (kind == 2 && !allowBoundaryLinks)
                {
                    LogInfo(
                        $"Smart path refused an entrance link at {FormatVector(position)} for same-side " +
                        $"final={FormatVector(finalDestination)}; falling back to ordinary navigation.");
                    noSmartPath = true;
                    StartPathTask(agent, finalDestination, forceNew: true);
                    return false;
                }

                if (kind == 3 &&
                    _elevatorRide.TryHandleElevatorDestination(
                        bundy,
                        agent,
                        _destinationElevatorFloor.GetValue(destination),
                        position,
                        ActivateDistance,
                        CanActivateSmartDestination(destination, bundy.transform.position),
                        bundy.IssueRawAgentDestination,
                        bundy.PauseForSmartInteraction,
                        LogInfo,
                        out issuedDestination,
                        out bool restartAfterElevatorStep))
                {
                    if (restartAfterElevatorStep)
                        StartPathTask(agent, finalDestination, forceNew: true);
                    return true;
                }

                if (TryActivateSmartDestination(bundy, destination, kind, position))
                {
                    issuedDestination = position;
                    StartPathTask(agent, finalDestination, forceNew: true);
                    return true;
                }

                if (kind == 0)
                {
                    noSmartPath = true;
                    StartPathTask(agent, finalDestination);
                    return false;
                }

                LogInfo(
                    $"Smart path routing to {DescribeSmartDestination(kind)} link at {FormatVector(position)} " +
                    $"for final={FormatVector(finalDestination)}.");
                bundy.IssueRawAgentDestination(position);
                issuedDestination = position;
                StartPathTask(agent, finalDestination);
                return true;
            }

            requestPending = true;
        }
        catch (Exception e)
        {
            noSmartPath = true;
            LogDebug($"Smart path destination skipped: {e.Message}");
        }

        return false;
    }

    public void Dispose()
    {
        if (_task != null)
        {
            try
            {
                _disposeTask?.Invoke(_task, Array.Empty<object>());
            }
            catch
            {
            }
            _task = null;
        }

        _elevatorRide.Reset();
        _animatedElevatorRide.Reset();
    }

    private void EnsureTask()
    {
        if (_task != null)
            return;

        _task = Activator.CreateInstance(_taskType);
    }

    private bool IsCurrentResultReady()
    {
        if (_task == null)
            return false;

        try
        {
            return (bool)_isResultReady.Invoke(_task, new object[] { 0 });
        }
        catch
        {
            return false;
        }
    }

    private void StartPathTask(NavMeshAgent agent, Vector3 finalDestination, bool forceNew = false)
    {
        if (!forceNew && Time.time < _nextTaskStartAllowedAt)
            return;

        if (forceNew)
        {
            Dispose();
            _task = Activator.CreateInstance(_taskType);
        }

        if (_task == null)
            _task = Activator.CreateInstance(_taskType);

        Vector3 origin = agent.transform.position;
        if (_getPathOrigin != null)
        {
            try
            {
                origin = (Vector3)_getPathOrigin.Invoke(null, new object[] { agent });
            }
            catch
            {
                origin = agent.transform.position;
            }
        }

        int linkMask = GetRequestedLinkMask();
        object flags = Enum.ToObject(_flagsType, linkMask);
        _startPathTask.Invoke(_task, new[] { agent, origin, finalDestination, flags });
        _lastRequestedDestination = finalDestination;
        _lastRequestedLinkMask = linkMask;
        _hasRequestedDestination = true;
        _nextTaskStartAllowedAt = Time.time + MinTaskRestartInterval;
    }

    private int GetRequestedLinkMask()
    {
        return _allowBoundaryLinks ? _interiorLinkMask | _boundaryLinkMask : _interiorLinkMask;
    }

    private bool TryActivateSmartDestination(BundyAIBase bundy, object destination, int kind, Vector3 position)
    {
        if (Vector3.Distance(bundy.transform.position, position) > ActivateDistance)
            return false;

        if (!CanActivateSmartDestination(destination, bundy.transform.position))
            return false;

        switch (kind)
        {
            case 1:
                LogInfo($"Smart path activating {DescribeSmartDestination(kind)} link at {FormatVector(position)}.");
                object internalTeleport = _destinationInternalTeleport.GetValue(destination);
                Transform internalDestination = _internalTeleportDestination?.GetValue(internalTeleport) as Transform;
                return bundy.TryUseSmartInternalTeleport(internalDestination);
            case 2:
                if (!_allowBoundaryLinks)
                    return false;
                LogInfo($"Smart path activating {DescribeSmartDestination(kind)} link at {FormatVector(position)}.");
                EntranceTeleport entrance = _destinationEntranceTeleport.GetValue(destination) as EntranceTeleport;
                return bundy.TryUseSmartEntranceTeleport(entrance, "smart path entrance");
            case 3:
                return false;
            default:
                return false;
        }
    }

    private static bool CanActivateSmartDestination(object destination, Vector3 actorPosition)
    {
        if (_destinationCanActivate == null)
            return true;

        try
        {
            return (bool)_destinationCanActivate.Invoke(destination, new object[] { actorPosition });
        }
        catch
        {
            return false;
        }
    }

    private static object UnwrapNullable(object nullable)
    {
        if (nullable == null)
            return null;

        Type type = nullable.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Nullable<>))
            return nullable;

        PropertyInfo hasValue = type.GetProperty("HasValue");
        if (hasValue == null || !(bool)hasValue.GetValue(nullable))
            return null;

        return type.GetProperty("Value")?.GetValue(nullable);
    }

    private static void EnsureResolved()
    {
        if (_resolved)
            return;

        _resolved = true;
        try
        {
            Type smartPathfinding = FindType("PathfindingLib.API.SmartPathfinding.SmartPathfinding");
            Type agentExtensions = FindType("PathfindingLib.Utilities.AgentExtensions");
            _taskType = FindType("PathfindingLib.API.SmartPathfinding.SmartPathTask");
            _flagsType = FindType("PathfindingLib.API.SmartPathfinding.SmartPathfindingLinkFlags");
            _destinationType = FindType("PathfindingLib.API.SmartPathfinding.SmartPathDestination");
            _destinationKindType = FindType("PathfindingLib.API.SmartPathfinding.SmartDestinationType");
            if (smartPathfinding == null || _taskType == null || _flagsType == null || _destinationType == null || _destinationKindType == null)
                throw new InvalidOperationException("PathfindingLib smart path types are not loaded.");

            _registerSmartAgent = smartPathfinding.GetMethod("RegisterSmartAgent", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(NavMeshAgent) }, null);
            _getPathOrigin = agentExtensions?.GetMethod("GetPathOrigin", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(NavMeshAgent) }, null);
            _startPathTask = _taskType.GetMethod("StartPathTask", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(NavMeshAgent), typeof(Vector3), typeof(Vector3), _flagsType }, null);
            _isResultReady = _taskType.GetMethod("IsResultReady", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            _getResult = _taskType.GetMethod("GetResult", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            _disposeTask = _taskType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance);
            _destinationKind = _destinationType.GetProperty("Type", BindingFlags.Public | BindingFlags.Instance);
            _destinationPosition = _destinationType.GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
            _destinationEntranceTeleport = _destinationType.GetProperty("EntranceTeleport", BindingFlags.Public | BindingFlags.Instance);
            _destinationElevatorFloor = _destinationType.GetProperty("ElevatorFloor", BindingFlags.Public | BindingFlags.Instance);
            _destinationInternalTeleport = _destinationType.GetProperty("InternalTeleport", BindingFlags.Public | BindingFlags.Instance);
            _destinationCanActivate = _destinationType.GetMethod("CanActivateDestination", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Vector3) }, null);

            Type internalTeleportType = FindType("PathfindingLib.API.SmartPathfinding.IInternalTeleport");
            _internalTeleportDestination = internalTeleportType?.GetProperty("Destination", BindingFlags.Public | BindingFlags.Instance);
            ResolveLinkMasks();

            _available = _registerSmartAgent != null
                && _startPathTask != null
                && _isResultReady != null
                && _getResult != null
                && _destinationKind != null
                && _destinationPosition != null
                && _destinationEntranceTeleport != null
                && _destinationElevatorFloor != null
                && _destinationInternalTeleport != null
                && _internalTeleportDestination != null
                && _destinationCanActivate != null;
        }
        catch (Exception e)
        {
            _available = false;
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                LogInfo($"PathfindingLib smart pathing unavailable; Bundy will use vanilla navigation. {e.Message}");
            }
        }

        if (_available && !_loggedAvailable)
        {
            _loggedAvailable = true;
            LogInfo("PathfindingLib smart pathing bridge enabled for Bundy.");
        }
    }

    // Read the two masks off the enum by NAME. The bit positions are PathfindingLib's to change, and a
    // silent renumber here would either re-admit the entrance links the gate exists to exclude or drop
    // the elevator links Bundy needs indoors. Falls back to the observed 2.4.1 values if a name is gone.
    private static void ResolveLinkMasks()
    {
        _interiorLinkMask = FallbackInteriorLinkMask;
        _boundaryLinkMask = FallbackBoundaryLinkMask;
        if (_flagsType == null || !_flagsType.IsEnum)
            return;

        int interior = 0;
        int boundary = 0;
        string[] names = Enum.GetNames(_flagsType);
        Array values = Enum.GetValues(_flagsType);
        for (int i = 0; i < names.Length; i++)
        {
            int value = Convert.ToInt32(values.GetValue(i));
            switch (names[i])
            {
                case "InternalTeleports":
                case "Elevators":
                    interior |= value;
                    break;
                case "MainEntrance":
                case "FireExits":
                    boundary |= value;
                    break;
            }
        }

        if (interior != 0)
            _interiorLinkMask = interior;
        if (boundary != 0)
            _boundaryLinkMask = boundary;

        if (_interiorLinkMask != FallbackInteriorLinkMask || _boundaryLinkMask != FallbackBoundaryLinkMask)
            LogInfo($"Smart path link masks resolved: interior={_interiorLinkMask}, boundary={_boundaryLinkMask}.");
    }

    private static Type FindType(string fullName)
    {
        Type type = Type.GetType(fullName);
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

    private static string DescribeSmartDestination(int kind)
    {
        switch (kind)
        {
            case 1:
                return "internal-teleport";
            case 2:
                return "entrance";
            case 3:
                return "elevator";
            default:
                return $"kind-{kind}";
        }
    }

    private static string FormatVector(Vector3 value)
    {
        return $"({value.x:F2}, {value.y:F2}, {value.z:F2})";
    }

    private static void LogInfo(string message)
    {
        Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] {message}");
    }

    private static void LogDebug(string message)
    {
        Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogDebug($"[Bundy] {message}");
    }
}
