using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations._Shared;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1293 R4 / #1305 G3: the round's vanilla mineshaft car as ONE group crossing.
    ///
    /// The link joins the car's two landings; the floor a member stands on is its source and the
    /// other floor its target. A member whose goal is off his storey, with no complete path to it
    /// and a complete one from the far landing, takes it. He walks to the near staging point and
    /// stands at the link start until the car is parked open at his floor, then takes a free ride
    /// slot, hand-steps onto it, rides with the cabin's own delta and hand-steps out. The car's
    /// Elevator lane (capacity 1, FIFO, no clock) is the command lease only: its holder is whoever
    /// must call the car to his landing or press the inside button to leave; riders need none.
    ///
    /// Every tick derives where he is from the link's few facts, the car's other live links, the
    /// lane and the car's own flags. The car is the only progress: a command it does not answer is
    /// refused once per car state (<see cref="VanillaCallCooldownCap"/>). Each event is one
    /// <c>[UAT][Elevator]</c> line. Host only: clients see the rider through
    /// <c>SyncPositionToClients</c> and the car through vanilla's own replication.
    /// </summary>
    internal sealed partial class BlacksiteElevatorInteraction
    {
        private const string ElevatorInterfaceTypeName =
            "PathfindingLib.API.SmartPathfinding.IElevator";
        // PathfindingLib's SmartPathfinding.NonSmartAgentOffMeshLinkAreaIndex: links authored for
        // agents that do NOT smart-path. A smart agent's crossing of the same place is PathfindingLib's.
        private const int NonSmartAgentLinkArea = 25;
        // Within this of the boarding target the member stops using the agent and hand-steps the
        // rest across the lip. The staging point sits exactly this far outside the door
        // (BlacksiteElevatorGeometry.StagingDistance), so the link starts where the step can.
        private const float BoardCommitDistance = BlacksiteElevatorGeometry.BoardCommitDistance;
        // A body the cabin carries stands no further from the landing than the stretch it stepped.
        private const float CarriedAgentBindRadius = BoardCommitDistance;
        // A body this near his ride slot is standing on it.
        private const float RidePointReach = .10f;
        // A hand-step target this far above or below the feet is not on the member's landing.
        private const float StepStoreyBand = 1.2f;
        // The step off walks out along the door normal and turns for the landing this far inside
        // the door face, so a body never cuts the jamb on the diagonal.
        private const float DoorReach = .45f;
        // The step off ends where the landing's NavMesh lies this close under the body.
        private const float ExitReach = .3f;
        // A walk target is re-issued only when it moved further than this (squared metres).
        private const float ReissueSqrMetres = .0025f;
        private const string RequestReason = "elevator";
        // A body is inside the cabin when its chest is: the lip under its feet is not.
        private static readonly Vector3 InsideProbe = Vector3.up * .6f;
        // #1305 G3: CallElevatorOnServer adds 0.09 s to its call cooldown per call (clamped to 4 s) and
        // the car switches only once that has run out, so a call every tick pins it and the car never
        // comes: call once per car state. 4 s is the longest vanilla waits to act on a call it accepted;
        // a call not answered by then is refused. It replaces the deleted 8 s motion-monitor window the
        // elevator trip was judged by; the link keeps no other clock.
        internal const float VanillaCallCooldownCap = 4f;

        /// <summary>The round's car, measured. One per controller; the floors are its two landings.</summary>
        private sealed class NativeMineshaftAdapter
        {
            internal readonly MineshaftElevatorController Controller;
            internal readonly Func<Vector3, bool> Contains;
            internal bool GeometryLogged;
            private BlacksiteElevatorGeometry _geometry;
            private Vector3 _geometrySeed;
            private bool _geometryBuilt;
            private float _cabinFloorY;
            private string _cabinFloorSource = BlacksiteDiagnosticFormat.None;
            private float _cabinFloorSeed = float.NaN;
            private int _cabinFloorFrame = -1;

            internal NativeMineshaftAdapter(MineshaftElevatorController controller)
            {
                Controller = controller;
                Contains = IsInside;
            }

            internal object Key => Controller;
            internal Transform InsideButton => Controller != null ? Controller.elevatorInsidePoint : null;
            internal bool Finished => Controller != null && Controller.elevatorFinishedMoving;
            internal bool DoorsAreOpen => Finished && Controller.elevatorDoorOpen;

            /// <summary>The floor the car stands at or travels to: vanilla's own direction flag.</summary>
            internal bool HeadsFor(bool bottom) => Controller != null && Controller.elevatorMovingDown == bottom;

            /// <summary>#1305 G3: the car has finished its trip at this floor, doors open or not.</summary>
            internal bool StandsAt(bool bottom) => Finished && HeadsFor(bottom);

            /// <summary>The car stands at this floor with its doors open.</summary>
            internal bool ParkedOpenAt(bool bottom)
                => DoorsAreOpen && Controller.elevatorInsidePoint != null
                    && IsBottom(Controller.elevatorInsidePoint.position) == bottom;

            internal enum CallOutcome { Coming, Refused }

            // #1305 G3: the one command per car state (vanilla's finished, direction and called flags and
            // its finish timer, which every trip resets and only travel runs down): when it was given and
            // the car's refusal of it, which stands until the state changes.
            private int _commandState = -1;
            private float _commandTimer, _commandAt;
            private string _refusal;

            private int CommandState => (Finished ? 1 : 0) | (Controller.elevatorMovingDown ? 2 : 0)
                | (Controller.elevatorCalled ? 4 : 0);

            private void Stamp()
            { _commandState = CommandState; _commandTimer = Controller.elevatorFinishTimer; _commandAt = Time.time; }

            // Was the car commanded to this floor in its current state? A call it has not answered
            // within VanillaCallCooldownCap and the frame vanilla acts in is refused here.
            private bool Commanded(bool floor)
            {
                if (_commandState != CommandState || _commandTimer != Controller.elevatorFinishTimer)
                { _commandState = -1; _refusal = null; return false; }
                if (!StandsAt(!floor))
                    return false;
                if (_refusal == null && Time.time - _commandAt > VanillaCallCooldownCap + Time.deltaTime)
                    _refusal = "callNotAnswered";
                return true;
            }

            /// <summary>
            /// #1305 G3: bring the car to a landing through vanilla <c>CallElevatorOnServer</c>, once per
            /// car state. A car standing there or still travelling is coming (vanilla's own finish
            /// timer bounds the trip); one standing at the other floor is called once, and refuses
            /// when it ignores the call or does not answer it in time.
            /// </summary>
            internal CallOutcome Call(bool bottom)
            {
                if (!StandsAt(!bottom))
                    return CallOutcome.Coming;
                if (!Commanded(bottom))
                {
                    try { Controller.CallElevatorOnServer(bottom); }
                    catch (Exception e) { LogAdapterFailure($"native mineshaft call ({e.Message})"); }
                    Stamp();
                    _refusal = Controller.elevatorCalled || !StandsAt(!bottom) ? null : "callIgnored";
                }
                return _refusal == null ? CallOutcome.Coming : CallOutcome.Refused;
            }

            /// <summary>
            /// #1305 G3: send the car on from inside through <c>PressElevatorButtonOnServer</c>, once per
            /// car state and only while it stands away from the target. The answer is synchronous: a
            /// car still standing refused it.
            /// </summary>
            internal bool PressInside(bool target)
            {
                if (!StandsAt(!target))
                    return true;
                if (!Commanded(target))
                {
                    try { Controller.PressElevatorButtonOnServer(requireFinishedMoving: true); }
                    catch (Exception e) { LogAdapterFailure($"native mineshaft press ({e.Message})"); }
                    if (!StandsAt(!target))
                        return true;
                    Stamp();
                    _refusal = "pressIgnored";
                }
                return _refusal == null;
            }

            /// <summary>#1305 G3: the car's refusal of a command to this floor in its current state, or null.</summary>
            internal string RefusalAt(bool floor) => Commanded(floor) ? _refusal : null;

            /// <summary>Which floor a position is on: the landing nearer in height, ties to the top.</summary>
            internal bool IsBottom(Vector3 position)
                => Mathf.Abs(position.y - Controller.elevatorBottomPoint.position.y)
                    < Mathf.Abs(position.y - Controller.elevatorTopPoint.position.y);

            internal float FloorHeight(bool bottom)
                => (bottom ? Controller.elevatorBottomPoint : Controller.elevatorTopPoint).position.y;

            /// <summary>The vanilla call anchor: every position below falls back to it without a measured cabin.</summary>
            internal Vector3 CallPosition(bool bottom)
            {
                Transform point = bottom
                    ? Controller.elevatorBottomPointDoor != null ? Controller.elevatorBottomPointDoor : Controller.elevatorBottomPoint
                    : Controller.elevatorTopPoint;
                return point != null ? point.position : Vector3.zero;
            }

            // #874 S5.3: the cabin, measured from the three things the vanilla controller publishes -
            // elevatorBounds, the cabin transform's own axes and elevatorBottomPointDoor.
            internal bool HasGeometry => EnsureGeometry().HasValue;
            internal BlacksiteElevatorGeometry Geometry => EnsureGeometry();

            private BlacksiteElevatorGeometry EnsureGeometry()
            {
                if (Controller == null || Controller.elevatorBounds == null)
                    return default;
                Bounds cabin = Controller.elevatorBounds.bounds;
                // The car moves vertically, so only a horizontal move or a resize invalidates it.
                var seed = new Vector3(cabin.center.x, cabin.size.x + cabin.size.z, cabin.center.z);
                if (_geometryBuilt && (seed - _geometrySeed).sqrMagnitude < .0025f)
                    return _geometry;
                Transform cabinTransform = Controller.elevatorBounds.transform;
                Transform doorAnchor = Controller.elevatorBottomPointDoor != null
                    ? Controller.elevatorBottomPointDoor
                    : Controller.elevatorBottomPoint;
                if (cabinTransform == null || doorAnchor == null)
                    return default;
                _geometry = BlacksiteElevatorGeometry.Build(cabin, cabinTransform.right, cabinTransform.forward, doorAnchor.position);
                _geometrySeed = seed;
                _geometryBuilt = true;
                return _geometry;
            }

            /// <summary>The landing outside the door on a floor, NavMesh-sampled.</summary>
            internal Vector3 LandingOf(bool bottom)
            {
                BlacksiteElevatorGeometry geometry = EnsureGeometry();
                return geometry.HasValue
                    ? Sample(geometry.LandingPosition(FloorHeight(bottom)))
                    : CallPosition(bottom);
            }

            /// <summary>
            /// Where the link starts on a floor. #1157: the 2 m sample can find the stair tile behind
            /// the top platform, so whatever it answers is held to the measured landing corridor.
            /// </summary>
            internal Vector3 StagingOf(bool bottom)
            {
                BlacksiteElevatorGeometry geometry = EnsureGeometry();
                if (!geometry.HasValue)
                    return CallPosition(bottom);
                float floorY = FloorHeight(bottom);
                return geometry.ClampToLanding(Sample(geometry.StagingPosition(floorY)), floorY);
            }

            /// <summary>Where a cart pusher rides alone: the cabin's centre (the in-car marker without a measurement) on its floor.</summary>
            internal Vector3 RidePoint
            {
                get
                {
                    float floorY = CabinFloorY(out _);
                    BlacksiteElevatorGeometry geometry = EnsureGeometry();
                    Vector3 centre = geometry.HasValue ? geometry.Center : Controller.elevatorInsidePoint.position;
                    return new Vector3(centre.x, floorY, centre.z);
                }
            }

            /// <summary>#1305 G3: ride slots on the cabin floor: one without a measured cabin, at most a slot mask's 32.</summary>
            internal int Capacity => HasGeometry ? Mathf.Min(Geometry.Capacity, 32) : 1;

            /// <summary>#1305 G3: ride slot <paramref name="slot"/> on the cabin's floor wherever the car is; the ride point without a measured cabin.</summary>
            internal Vector3 SlotPoint(int slot)
            {
                BlacksiteElevatorGeometry geometry = EnsureGeometry();
                return geometry.HasValue ? geometry.RideSlot(slot, CabinFloorY(out _)) : RidePoint;
            }

            /// <summary>The centre of the door face on the cabin's own floor, wherever the car is.</summary>
            internal Vector3 DoorwayWaypoint()
            {
                BlacksiteElevatorGeometry geometry = EnsureGeometry();
                return geometry.HasValue
                    ? SnapToCabinFloor(geometry.DoorwayWaypoint(CabinFloorY(out _)))
                    : Controller.elevatorInsidePoint.position;
            }

            /// <summary>The car whose colliders the hand step may sweep through, and its cabin volume.</summary>
            internal bool TryGetCarIgnore(out MineshaftElevatorController car, out Bounds cabin)
            {
                car = Controller;
                cabin = Controller != null && Controller.elevatorBounds != null
                    ? Controller.elevatorBounds.bounds
                    : default;
                return car != null && Controller.elevatorBounds != null;
            }

            internal bool IsInside(Vector3 position)
            {
                if (Controller == null)
                    return false;
                if (Controller.elevatorBounds != null)
                {
                    if (!Controller.elevatorBounds.bounds.Contains(position))
                        return false;
                    // #874 S9.1 (RC-E1): on the vanilla mineshaft elevatorBounds is the 42.59 m SHAFT,
                    // so a member is in the car only standing on the car's own floor, inside one body
                    // column of it; the slack under the floor is the arrival contract's profile margin.
                    float cabinFloor = CabinFloorY(out _);
                    return position.y >= cabinFloor - BlacksiteArrivalPolicy.ProfileReachMargin
                        && position.y <= cabinFloor + BlacksiteWorldLadderColumn.BodyColumnMetres;
                }
                return Controller.elevatorInsidePoint != null
                    && Mathf.Abs(position.y - Controller.elevatorInsidePoint.position.y) < 2f
                    && HorizontalDistance(position, Controller.elevatorInsidePoint.position) <= 1.25f;
            }

            /// <summary>
            /// #874 S9.1 (RC-E1): the live cabin floor, and the evidence it came from - the in-car
            /// marker (parented to the animated car), else the bounds' own minimum y - probed straight
            /// down onto the surface a member stands on. Memoised per frame and source height; the car
            /// moves, so the probe is re-spent whenever either changes.
            /// </summary>
            internal float CabinFloorY(out string source)
            {
                BlacksiteElevatorGeometry geometry = EnsureGeometry();
                float raw;
                if (Controller != null && Controller.elevatorInsidePoint != null)
                { raw = Controller.elevatorInsidePoint.position.y; source = "insidePoint"; }
                else
                { raw = geometry.HasValue ? geometry.FloorY : 0f; source = "bounds"; }
                if (_cabinFloorFrame == Time.frameCount && _cabinFloorSeed == raw)
                { source = _cabinFloorSource; return _cabinFloorY; }
                Vector3 anchor = geometry.HasValue
                    ? new Vector3(geometry.Center.x, raw, geometry.Center.z)
                    : Controller != null && Controller.elevatorInsidePoint != null
                        ? new Vector3(Controller.elevatorInsidePoint.position.x, raw, Controller.elevatorInsidePoint.position.z)
                        : new Vector3(0f, raw, 0f);
                _cabinFloorY = SnapToCabinFloor(anchor).y;
                _cabinFloorSeed = raw;
                _cabinFloorSource = source;
                _cabinFloorFrame = Time.frameCount;
                return _cabinFloorY;
            }

            private static Vector3 Sample(Vector3 candidate)
                => NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas)
                    ? hit.position
                    : candidate;

            // The cabin has no baked NavMesh, so a point on its floor is placed by a downward probe.
            private static Vector3 SnapToCabinFloor(Vector3 candidate)
            {
                int mask = StartOfRound.Instance != null
                    ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                    : ~0;
                if (Physics.Raycast(candidate + Vector3.up * 1.2f, Vector3.down, out RaycastHit floor, 2.4f,
                        mask, QueryTriggerInteraction.Ignore))
                    candidate.y = floor.point.y;
                return candidate;
            }
        }

        /// <summary>
        /// One member's link: the task context. The phase is never stored - <see cref="Tick"/> derives
        /// it from these facts, the car's other live links, the lane and the car every tick.
        /// </summary>
        private sealed class Link
        {
            internal NativeMineshaftAdapter Car;
            /// <summary>The floor he boards at; the target is the other one.</summary>
            internal bool SourceBottom;
            /// <summary>He holds the command lease (lane-less: he commands): a holder that stops
            /// holding without giving it back lost it (the ledger's stall answer, the stale window).</summary>
            internal bool Reserved;
            /// <summary>#1305 G3: he boarded; only the link moves him from here.</summary>
            internal bool Boarded;
            /// <summary>#1305 G3: his ride slot while stepping in or aboard; -1 without one.</summary>
            internal int Slot = -1;
            /// <summary>#1305 G3: he pushes the cart, which takes the floor: he rides alone on the ride point.</summary>
            internal bool Alone;
            internal bool AgentSaved;
            internal bool AgentUpdatePosition;
            internal Vector3 LastInsidePosition;
            internal bool TargetBottom => !SourceBottom;
        }

        private static NativeMineshaftAdapter _car;
        // #1305 G3: every member with an elevator task, for ride slots, boarding and followers.
        private static readonly List<BlacksiteElevatorInteraction> LiveLinks = new List<BlacksiteElevatorInteraction>();
        // Keyed by member index (tripRefused, queued, stepRefused, boardRefused); reused next round.
        private static readonly BlacksiteChangeGate LinkGate = new BlacksiteChangeGate();
        private static readonly string[] PlaceTokens = { "0", "1", "2", "3", "4", "5", "6", "7" };
        private static readonly HashSet<string> LoggedAdapterFailures = new HashSet<string>();
        private static readonly Dictionary<Type, bool> ElevatorComponentTypes = new Dictionary<Type, bool>();
        private static readonly List<MonoBehaviour> ElevatorPartScratch = new List<MonoBehaviour>();

        private readonly BlacksiteInteractionCoordinator _coordinator;
        private readonly BlacksiteSoldierAI _soldier;
        private readonly NavMeshAgent _agent;
        // #1305 G3: the elevator task this member's live-link entry stands for.
        private BlacksiteInteractionTask _task;
        // A link that ended without arriving (a survivor's only on a fact of the car) refuses this goal
        // until the member's goal changes or the car's state does (TickMissedLink): the car state the
        // miss was taken against.
        private Vector3 _missedGoal;
        private bool _hasMissedGoal;
        private int _missedCarState;

        internal BlacksiteElevatorInteraction(
            BlacksiteInteractionCoordinator coordinator,
            BlacksiteSoldierAI soldier,
            NavMeshAgent agent)
        {
            _coordinator = coordinator;
            _soldier = soldier;
            _agent = agent;
        }

        /// <summary>
        /// #1016 S2 / #1293 R4: the claim ledger that holds the car's command lane (#1305 G3) - this
        /// member's mission's, even after he lost his transport (#872), so while the mission stands a
        /// stranded member and the squad share the same lane. Null off the server or once the
        /// mission is gone (ruling A: the link is then lane-less, see <see cref="Tick"/>).
        /// </summary>
        private BlacksiteResourceBroker Broker
            => _soldier != null && _soldier.TryGetMission(out BlacksiteRetrievalMissionController m, ignoreTransportLoss: true)
                && m.IsServer
                ? m.Blackboard.Broker
                : null;

        /// <summary>
        /// #1293 R4 F2: the link runs lane-less (no mission broker, the <c>reason=noMission</c>
        /// grant). The coordinator prints a survivor's repeated link failure once per cause.
        /// </summary>
        internal bool HasNoMission => Broker == null;

        /// <summary>
        /// #1016 S2 (amendment E): the round reset, called by
        /// <c>BlacksiteInteractionCoordinator.EnsureRoundRegistry</c>. It forgets the measured car and
        /// the live links (#1305 G3) and clears every live mission broker, which holds every link's lane.
        /// </summary>
        internal static void ResetRoundSessions()
        {
            _car = null;
            LiveLinks.Clear();
            LinkGate.Reset();
            BlacksiteResourceBroker.ClearAll();
        }

        /// <summary>
        /// Committed: the link holds the body (a hand step or the ride), the body is in the cabin, or
        /// he boarded (#1305 G3). Nothing outside the link may re-destination, stun out or extract
        /// him from here.
        /// </summary>
        internal bool IsCommitted(BlacksiteInteractionTask task)
        {
            if (_soldier == null || !(task?.Context is Link link) || link.Car.Controller == null)
                return false;
            return Committed(link, link.Car.IsInside(_soldier.transform.position + InsideProbe));
        }

        private static bool Committed(Link link, bool inside) => link.AgentSaved || inside || link.Boarded;

        /// <summary>
        /// Take the link for this goal, or refuse. A goal on the member's own storey, one he can walk
        /// to, one the far landing cannot reach, and the goal of a link that ended without arriving
        /// (until his goal or the car's state changes) are not trips.
        /// </summary>
        internal bool TryCreateNativeMineshaftTask(Vector3 finalDestination, out BlacksiteInteractionTask task)
            => TryCreateNativeMineshaftLeg(finalDestination, finalDestination, out task);

        /// <summary>
        /// #1305 G2 (R16): the same entry for the car as a route's leg - <paramref name="landing"/> is the
        /// landing the trip ends on, on the way to <paramref name="finalDestination"/> (another world for
        /// the coordinator's cross-world leg); the storey, direct-path and far-landing gates read the
        /// landing, the miss memory and the task keep the goal.
        /// </summary>
        internal bool TryCreateNativeMineshaftLeg(Vector3 finalDestination, Vector3 landing, out BlacksiteInteractionTask task)
        {
            task = null;
            if (_soldier == null || _soldier.isOutside)
                return false;
            NativeMineshaftAdapter car = CurrentCar();
            if (car == null || car.InsideButton == null)
                return false;
            // A miss stands until the goal moves or the car's state changes (TickMissedLink): the ledger
            // reads a squad member as Travel with no path and its window retargets him; a survivor (#1293
            // R4 M1: no mission, no ledger, no planner to move his goal) takes the car again when it does.
            if (_hasMissedGoal && Vector3.Distance(finalDestination, _missedGoal) > BlacksiteArrivalPolicy.MinDestinationChange)
                _hasMissedGoal = false;
            Vector3 body = _soldier.transform.position;
            if (!BlacksiteResponsePolicy.OffStorey(body.y, landing.y))
                return false;
            if (_hasMissedGoal)
            {
                ReportTripRefused(finalDestination, "missedGoal: the last link for this goal ended without arriving");
                return false;
            }
            if (HasCompleteDirectPath(landing))
                return false;
            bool sourceBottom = car.IsBottom(body);
            if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(_soldier), car.LandingOf(!sourceBottom), landing).Reachable)
            {
                ReportTripRefused(finalDestination, "approachUnreachable: the far landing has no complete route to the goal");
                return false;
            }
            LogGeometry(car);
            task = new BlacksiteInteractionTask(
                BlacksiteInteractionKind.Elevator,
                car.Key,
                car.StagingOf(sourceBottom),
                finalDestination,
                $"mineshaft elevator {Label(sourceBottom)}-to-{Label(!sourceBottom)}")
            {
                AlignPosition = car.CallPosition(sourceBottom),
                ConfirmPosition = finalDestination,
                Context = new Link
                {
                    Car = car,
                    SourceBottom = sourceBottom,
                    LastInsidePosition = car.InsideButton.position
                }
            };
            _task = task;
            if (!LiveLinks.Contains(this))
                LiveLinks.Add(this);
            return true;
        }

        // An off-mesh member has no direct path and is still offered the car.
        private bool HasCompleteDirectPath(Vector3 goal)
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
                return false;
            return BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(_soldier), _soldier.transform.position, goal).Reachable;
        }

        /// <summary>
        /// One tick of the link. Ticked every frame while the task is active
        /// (<c>BlacksiteInteractionCoordinator.IsElevatorRideActive</c>), so the lane hears from its
        /// holder and its callers well inside the 2 s stale window and the cabin's delta is applied
        /// frame by frame. #1305 G3: one rider path - every member boards, rides and steps off alike;
        /// the lease holder only adds commanding the car.
        /// </summary>
        internal BlacksiteInteractionTickResult Tick(BlacksiteInteractionTask task, out Vector3 issuedDestination)
        {
            issuedDestination = _soldier != null ? _soldier.transform.position : Vector3.zero;
            if (_soldier == null || !(task?.Context is Link link))
            {
                task?.TransitionTo(BlacksiteInteractionPhase.Failed);
                return BlacksiteInteractionTickResult.Failed;
            }
            BlacksiteResourceBroker broker = Broker;
            NativeMineshaftAdapter car = link.Car;
            Transform insidePoint = car.InsideButton;
            if (insidePoint == null)
                return End(task, link, "noCar", arrived: false, miss: false);
            BlacksiteResourceKey key = BlacksiteResourceKey.Elevator(car.Key);

            // The cabin's own travel moves a body standing in it before anything reads where he is. A
            // body the link is riding (Perform: boarded, not yet arrived) is carried on every tick
            // whatever the cabin band says - one long frame can leave him outside it, and the full
            // delta brings him back. The band only decides whether a body not yet riding is taken.
            bool inside = car.IsInside(_soldier.transform.position + InsideProbe);
            if (inside || task.Phase == BlacksiteInteractionPhase.Perform)
            {
                CarryActorWithElevator(link, insidePoint.position);
                inside = car.IsInside(_soldier.transform.position + InsideProbe);
            }
            else
                link.LastInsidePosition = insidePoint.position;
            Vector3 body = _soldier.transform.position;
            issuedDestination = body;

            // The stall answer, or any other release of his lease. An idle lane is forgotten by the
            // broker, so a missing lane is a taken lease too; only a missing broker is not. A holder
            // renews it every tick.
            if (broker != null && link.Reserved)
            {
                if (!broker.TryGetLane(key, out BlacksiteResourceLane lane) || !lane.Holds(_coordinator.OwnerToken))
                    return End(task, link, _soldier.Navigator.IsStranded ? "stranded" : "stalled", arrived: false, miss: true);
                broker.Request(key, _coordinator.OwnerToken, Time.time, RequestReason);
            }
            if (!Committed(link, inside))
            {
                // An interrupt is a miss only for a member the ledger judges: nothing about the car
                // refused a survivor, so his next decision takes it again.
                if (_soldier.IsWorldInteractionInterrupted)
                    return End(task, link, "interrupted", arrived: false, miss: broker != null);
                if (!_coordinator.HasDesiredDestination
                    || !BlacksiteResponsePolicy.OffStorey(body.y, _coordinator.DesiredDestination.y))
                    return End(task, link, "goalChanged", arrived: false, miss: false);
            }
            bool source = link.SourceBottom, target = link.TargetBottom;
            if (link.Boarded && (car.ParkedOpenAt(target) || (!inside && car.IsBottom(body) == target)))
            {
                if (task.Phase != BlacksiteInteractionPhase.Confirm)
                {
                    task.TransitionTo(BlacksiteInteractionPhase.Confirm);
                    link.Slot = -1;
                    Event(link, "arrived");
                    _soldier.NoteTraversalMilestone("elevator:arrived"); // #874 S6.1 wait milestone.
                }
                return StepOff(task, link, body, inside, out issuedDestination);
            }
            // #1305 G3: a command the car refused in this state ends every trip waiting on it, once.
            string refusal = car.RefusalAt(link.Boarded ? target : source);
            if (refusal != null)
                return End(task, link, "refused", arrived: false, miss: true, cause: refusal);
            ReadCrowd(link, out int taken, out bool alone, out bool arriving, out bool boarding);
            // Aboard: on his slot, or carried off by a car that left with him inside.
            if (!link.Boarded && !(inside && (!car.StandsAt(source) || OnSlot(link, body))))
                return TickBoarding(task, link, body, inside, broker, key, taken, alone, arriving, boarding, out issuedDestination);
            // The car leaves once nobody is getting off and his floor is done boarding.
            return TickAboard(task, link, broker, key, car.ParkedOpenAt(source) && !arriving && !boarding);
        }

        // #1305 G3: aboard. Every rider logs boarded; the car still parked open at his floor leaves
        // once boarding is done, pressed by the one rider who holds the command lease or takes it
        // from an idle lane. A press the car refuses ends every rider's trip.
        private BlacksiteInteractionTickResult TickAboard(BlacksiteInteractionTask task, Link link,
            BlacksiteResourceBroker broker, in BlacksiteResourceKey key, bool departs)
        {
            if (!link.Boarded)
            {
                link.Boarded = true;
                task.TransitionTo(BlacksiteInteractionPhase.Perform);
                Event(link, "boarded");
                _soldier.NoteTraversalMilestone("elevator:boarded"); // #874 S6.1 wait milestone.
                DropWaiter(link, broker, key);
            }
            if (departs && Commands(link, broker, key, queue: false) && !link.Car.PressInside(link.TargetBottom))
                return End(task, link, "refused", arrived: false, miss: true, cause: link.Car.RefusalAt(link.TargetBottom));
            return BlacksiteInteractionTickResult.Running;
        }

        // #1305 G3: not yet aboard. He walks to the link start and stands there until the car is parked
        // open at his floor; then he takes the lowest free ride slot, walks to the landing and
        // hand-steps onto it. The hand step starts only in the landing corridor: outside it the agent
        // walks there, round whatever stands beside the door (#1056's BottomRoomPillar2), so the
        // stepped stretch is the corridor in front of the door and never the diagonal past a pillar.
        private BlacksiteInteractionTickResult TickBoarding(BlacksiteInteractionTask task, Link link, Vector3 body,
            bool inside, BlacksiteResourceBroker broker, in BlacksiteResourceKey key, int taken, bool alone,
            bool arriving, bool boarding, out Vector3 issuedDestination)
        {
            issuedDestination = body;
            NativeMineshaftAdapter car = link.Car;
            bool source = link.SourceBottom;
            if (link.Slot < 0 && !inside && !AtLinkStart(task, body))
            {
                task.TransitionTo(BlacksiteInteractionPhase.Approach);
                return Walk(task.ApproachPosition, out issuedDestination);
            }
            if (!car.ParkedOpenAt(source))
            {
                // No car to step onto: a body on the lip goes back and gives up its slot. Whoever must
                // command calls the car here, never while a body it carries is still getting off or the
                // other floor is still boarding it: those riders need no lease, and this call brings them.
                link.Slot = -1;
                HandBack(link);
                if (Commands(link, broker, key, queue: true) && !arriving && !boarding
                    && car.Call(source) == NativeMineshaftAdapter.CallOutcome.Refused)
                    return End(task, link, "refused", arrived: false, miss: true, cause: car.RefusalAt(source));
                return Wait(task, link, body, inside, out issuedDestination);
            }
            if (link.Slot < 0)
            {
                if (arriving)
                    return Wait(task, link, body, inside, out issuedDestination);
                link.Alone = PushesCart(_soldier);
                link.Slot = alone || (link.Alone && taken != 0) ? -1 : FirstFree(taken, car.Capacity);
                if (link.Slot < 0)
                {
                    // The cabin is full this cycle: a holder has nothing to command at a car parked
                    // open here, so he gives the lease to the riders.
                    if (broker != null && link.Reserved)
                    { broker.Release(key, _coordinator.OwnerToken); link.Reserved = false; Event(link, "released", "cabinFull"); }
                    return Wait(task, link, body, inside, out issuedDestination);
                }
                DropWaiter(link, broker, key);
            }
            // #1157: the landing sample is held to the measured corridor, as the staging point is.
            Vector3 landing = car.LandingOf(source);
            if (car.HasGeometry)
                landing = car.Geometry.ClampToLanding(landing, car.FloorHeight(source));
            if (!inside && !link.AgentSaved && (HorizontalDistance(body, landing) > BlacksiteElevatorGeometry.LandingStandoff
                || Mathf.Abs(body.y - landing.y) >= StepStoreyBand))
            {
                task.TransitionTo(BlacksiteInteractionPhase.Approach);
                return Walk(landing, out issuedDestination);
            }
            Vector3 slot = RideTarget(link);
            Vector3 step = car.HasGeometry ? car.Geometry.BoardingTarget(body, slot, slot.y) : slot;
            // #874 S9.1 (RC-E1): with the car parked here its floor and this landing are one surface;
            // a target a shaft away is a geometry defect, refused loudly rather than paced at.
            if (CabinFloorDisagreesWithLanding(step.y, car.FloorHeight(source), out float disagreement))
            {
                if (BlacksiteDiagnostics.Enabled
                    && LinkGate.ShouldReport(_soldier.MemberIndex, "boardRefused", "cabinFloor", out _))
                    BlacksiteDiagnostics.Log("Elevator",
                        $"event=boardRefused, member={_soldier.MemberIndex}, "
                        + $"cause=cabin floor disagrees with the landing by {disagreement:0.00}m, "
                        + $"target={BlacksiteDiagnosticFormat.V(step)}, landing={car.FloorHeight(source):0.00}, "
                        + $"floor={Label(source)}");
                return End(task, link, "cabinFloor", arrived: false, miss: true);
            }
            task.TransitionTo(BlacksiteInteractionPhase.Align);
            issuedDestination = step;
            // #1305 G3: a refused step is the exit event: the trip ends once, never retried every tick.
            return HandStep(link, step)
                ? BlacksiteInteractionTickResult.Running
                : End(task, link, "stepRefused", arrived: false, miss: true);
        }

        // Out along the door normal, then to the landing; the link ends on the landing's NavMesh.
        private BlacksiteInteractionTickResult StepOff(BlacksiteInteractionTask task, Link link, Vector3 body,
            bool inside, out Vector3 issuedDestination)
        {
            issuedDestination = body;
            if (!inside && NavMesh.SamplePosition(body, out NavMeshHit _, ExitReach, NavMesh.AllAreas))
                return End(task, link, "arrived", arrived: true, miss: false);
            Vector3 target = ExitTarget(link.Car, link.TargetBottom, body);
            issuedDestination = target;
            if (HandStep(link, target))
                return BlacksiteInteractionTickResult.Running;
            // The refused step handed the body back, onto the landing when nothing bound where it stood.
            bool off = !link.Car.IsInside(_soldier.transform.position + InsideProbe);
            return End(task, link, "stepRefused", arrived: off, miss: !off);
        }

        /// <summary>
        /// The link is over: the body back to the agent, the member out of the live links, his slot
        /// and the lane given back (holder or caller), the <c>released</c> event for a holder, and -
        /// for a link that did not arrive and was not simply abandoned for another goal - the
        /// same-goal refusal.
        /// </summary>
        private BlacksiteInteractionTickResult End(BlacksiteInteractionTask task, Link link, string reason,
            bool arrived, bool miss, string cause = null)
        {
            HandBack(link);
            LiveLinks.Remove(this);
            link.Slot = -1;
            BlacksiteResourceBroker broker = Broker;
            if (broker != null)
                broker.Release(BlacksiteResourceKey.Elevator(link.Car.Key), _coordinator.OwnerToken);
            if (link.Reserved)
                Event(link, "released", reason, cause);
            link.Reserved = false;
            if (_soldier != null && BlacksiteDiagnostics.Enabled)
                LinkGate.ShouldReport(_soldier.MemberIndex, "queued", BlacksiteDiagnosticFormat.None, out _);
            if (miss)
            {
                _missedGoal = task.FinalDestination;
                _hasMissedGoal = true;
                _missedCarState = CarState(CurrentCar());
            }
            task.FailureReason = arrived ? null : reason;
            task.TransitionTo(arrived ? BlacksiteInteractionPhase.Complete : BlacksiteInteractionPhase.Failed);
            return arrived ? BlacksiteInteractionTickResult.Complete : BlacksiteInteractionTickResult.Failed;
        }

        /// <summary>
        /// #1293 R3: the same-goal refusal stands on the car's state it was taken against - parked open
        /// at either floor, its doors, its travel. Once that changes the car is offered again and the
        /// member decides on it (Outcome). Once a frame from the navigator's maintenance tick, host only;
        /// nothing is read while no miss is held.
        /// </summary>
        internal void TickMissedLink()
        {
            if (!_hasMissedGoal || _soldier == null || CarState(CurrentCar()) == _missedCarState)
                return;
            _hasMissedGoal = false;
            _soldier.RequestDecision(BlacksiteDecisionCause.Outcome);
        }

        // The car's state as the same-goal refusal reads it: -1 with no car.
        private static int CarState(NativeMineshaftAdapter car)
            => car == null || car.InsideButton == null ? -1
                : (car.ParkedOpenAt(false) ? 1 : 0) | (car.ParkedOpenAt(true) ? 2 : 0)
                  | (car.DoorsAreOpen ? 4 : 0) | (car.Finished ? 0 : 8);

        /// <summary>
        /// The coordinator took the task away (a stun, combat, a damage reaction, a world change,
        /// despawn): a miss for a member the ledger judges; a survivor takes the car again on the
        /// decision that answers the reaction. A task the link already ended only has its body checked.
        /// </summary>
        internal void Interrupt(BlacksiteInteractionTask task, string reason)
        {
            if (!(task?.Context is Link link))
                return;
            if (task.IsTerminal)
            {
                HandBack(link);
                return;
            }
            End(task, link, "interrupted", arrived: false, miss: Broker != null, cause: reason);
        }

        private static bool AtLinkStart(BlacksiteInteractionTask task, Vector3 body)
            => HorizontalDistance(body, task.ApproachPosition) <= BoardCommitDistance
                && Mathf.Abs(body.y - task.ApproachPosition.y) < StepStoreyBand;

        private static Vector3 ExitTarget(NativeMineshaftAdapter car, bool bottom, Vector3 body)
        {
            if (!car.HasGeometry)
                return car.LandingOf(bottom);
            BlacksiteElevatorGeometry geometry = car.Geometry;
            return geometry.DepthOf(body) < geometry.HalfDepth - DoorReach
                ? car.DoorwayWaypoint()
                : car.LandingOf(bottom);
        }

        // #1305 G3: where he rides: his slot, or the cabin's ride point when the cart takes the floor.
        private static Vector3 RideTarget(Link link) => link.Alone ? link.Car.RidePoint : link.Car.SlotPoint(link.Slot);

        // On his ride slot, and a cart he pushes is in the cabin with him.
        private bool OnSlot(Link link, Vector3 body)
            => link.Slot >= 0 && HorizontalDistance(body, RideTarget(link)) <= RidePointReach
                && (!PushesCart(_soldier) || Y4NGZCompany.Contracts.Payload.PayloadContract.ActiveCart.FitsNpcElevator(link.Car.Contains));

        private static bool PushesCart(BlacksiteSoldierAI soldier)
            => Y4NGZCompany.Contracts.Payload.PayloadContract.ActiveCart is var cart && cart != null && cart.IsPushedBy(soldier);

        private static int FirstFree(int taken, int capacity)
        {
            for (int i = 0; i < capacity; i++)
                if ((taken & (1 << i)) == 0)
                    return i;
            return -1;
        }

        // A live link: his elevator task is still the coordinator's and he is alive.
        private bool TryGetLiveLink(out Link link)
        {
            link = _task?.Context as Link;
            return link != null && !_soldier.isEnemyDead && _coordinator.IsRequesting(_task);
        }

        // #1305 G3: the car's other live links, read once a tick: the ride slots taken, a cart pusher
        // riding alone, a body inside bound for the floor the car stands at (arriving or getting off),
        // and whether the floor the car stands open at is still boarding - a member there stepping in,
        // or one unboarded at his link start while the cabin has room (a cart pusher there boards only
        // an empty cabin, so he holds nobody up).
        private void ReadCrowd(Link link, out int taken, out bool alone, out bool arriving, out bool boarding)
        {
            taken = 0;
            alone = arriving = boarding = false;
            bool waiting = false;
            NativeMineshaftAdapter car = link.Car;
            for (int i = LiveLinks.Count - 1; i >= 0; i--)
            {
                BlacksiteElevatorInteraction other = LiveLinks[i];
                if (other._soldier == null)
                { LiveLinks.RemoveAt(i); continue; }
                if (other == this || !other.TryGetLiveLink(out Link o) || o.Car != car)
                    continue;
                Vector3 body = other._soldier.transform.position;
                if (o.Slot >= 0)
                { taken |= 1 << o.Slot; alone |= o.Alone; }
                arriving |= car.StandsAt(o.TargetBottom) && car.IsInside(body + InsideProbe);
                if (o.Boarded || !car.ParkedOpenAt(o.SourceBottom))
                    continue;
                if (o.Slot >= 0)
                    boarding = true;
                else if (AtLinkStart(other._task, body) && !PushesCart(other._soldier))
                    waiting = true;
            }
            int own = link.Slot >= 0 ? 1 << link.Slot : 0;
            boarding |= waiting && !alone && !(own != 0 && link.Alone) && FirstFree(taken | own, car.Capacity) >= 0;
        }

        /// <summary>
        /// #1305 G3: may he command the car? Only a member who must command asks for the lease: a
        /// caller at a landing takes his place in the lane (<paramref name="queue"/>), a rider takes it
        /// only from an idle lane. Lane-less (no mission) there is no lease and he commands freely.
        /// </summary>
        private bool Commands(Link link, BlacksiteResourceBroker broker, in BlacksiteResourceKey key, bool queue)
        {
            if (link.Reserved)
                return true;
            if (broker == null)
            { link.Reserved = true; Event(link, "reserved", "noMission"); return true; }
            if (!queue && broker.TryGetLane(key, out BlacksiteResourceLane lane) && (lane.Occupants > 0 || lane.Waiting > 0))
                return false;
            BlacksiteBrokerVerdict verdict = broker.Request(key, _coordinator.OwnerToken, Time.time, RequestReason);
            if (verdict.Admitted)
            { link.Reserved = true; Event(link, "reserved"); }
            else if (!queue)
                broker.Release(key, _coordinator.OwnerToken);
            else if (verdict.Outcome == BlacksiteWaitOutcome.Queued)
                ReportQueued(link, verdict.Place);
            return verdict.Admitted;
        }

        // #1305 G3: a member boarding without the lease needs no place in its lane.
        private void DropWaiter(Link link, BlacksiteResourceBroker broker, in BlacksiteResourceKey key)
        {
            if (broker != null && !link.Reserved)
                broker.Release(key, _coordinator.OwnerToken);
        }

        // #1305 G3: every wait for the car is Queued where he stands (a declared wait).
        private BlacksiteInteractionTickResult Wait(BlacksiteInteractionTask task, Link link, Vector3 body, bool inside,
            out Vector3 issuedDestination)
        {
            task.TransitionTo(BlacksiteInteractionPhase.Queued);
            issuedDestination = body;
            if (inside || link.AgentSaved)
                return BlacksiteInteractionTickResult.Running;
            _soldier.ArmIdleSweep();
            return Walk(body, out issuedDestination);
        }

        private BlacksiteInteractionTickResult Walk(Vector3 target, out Vector3 issuedDestination)
        {
            issuedDestination = target;
            // Stop on the point, not a stopping distance short of it: the hand step starts there. Set
            // on every walk tick, as an interrupt gives the stop back to its owner while the agent may
            // still be walking to this point, and a retaken link issues nothing new for it.
            _soldier.OverrideWorldInteractionStoppingDistance(BlacksiteArrivalPolicy.AgentRadius);
            var navigator = _soldier.Navigator;
            if (!navigator.HasIssuedDestination || (navigator.IssuedDestination - target).sqrMagnitude > ReissueSqrMetres)
                _soldier.IssueRawAgentDestination(target);
            return BlacksiteInteractionTickResult.Running;
        }

        /// <summary>
        /// One hand-driven step toward <paramref name="target"/> under the car-step scope. The link
        /// holds the body only while it steps: a refused step hands it back (#1305 G3: and ends the trip).
        /// </summary>
        private bool HandStep(Link link, Vector3 target)
        {
            SaveAgentForRide(link);
            if (WalkCabinThreshold(link.Car, target, out BlacksiteStepFailure failure))
            {
                _soldier.Navigator.FollowCarriedBody(CarriedAgentBindRadius);
                return true;
            }
            ReportStepRefused(failure, target);
            HandBack(link);
            return false;
        }

        private void SaveAgentForRide(Link link)
        {
            if (link.AgentSaved || _agent == null || !_agent.enabled)
                return;
            // #874 S4: the ride is a crossing, and a body another crossing (the payload coupling) or a
            // seat already writes is not the link's to take: the cabin carries the transform and the
            // body's owner hands it back.
            var writer = _soldier.Navigator.BodyWriter;
            if (writer != BlacksiteBodyWriter.Agent)
            {
                // Diagnostics only: the rate limit bounds a log line, not a decision.
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.LogRateLimited($"elevatorRideRefused.{_soldier.NetworkObjectId}", 3f, "Elevator",
                        $"event=rideSaveRefused, member={_soldier.MemberIndex}, writer={writer}");
                return;
            }
            link.AgentSaved = true;
            link.AgentUpdatePosition = _soldier.Navigator.BeginManualMovement();
        }

        // The cabin's delta since the last tick, applied exactly once (vanilla MoveWithMineshaftElevator
        // would add it again), to the member and a cart he pushes.
        private void CarryActorWithElevator(Link link, Vector3 insidePosition)
        {
            SaveAgentForRide(link);
            Vector3 delta = insidePosition - link.LastInsidePosition;
            if (delta.sqrMagnitude > 0f && delta.sqrMagnitude < 100f)
            {
                var cart = Y4NGZCompany.Contracts.Payload.PayloadContract.ActiveCart;
                if (cart != null && cart.IsPushedBy(_soldier))
                    cart.CarryNpcWithElevator(delta);
                _soldier.Navigator.CarryWithPlatform(delta, CarriedAgentBindRadius);
            }
            link.LastInsidePosition = insidePosition;
            _soldier.SyncPositionToClients();
        }

        /// <summary>
        /// The body back to the agent. #874 S1: where nothing binds under it (the cabin has no baked
        /// NavMesh) it is handed back on the landing the car is parked at, never while a monster has
        /// it (TryRestoreOnLanding refuses), and otherwise released where it stands.
        /// </summary>
        private void HandBack(Link link)
        {
            if (!link.AgentSaved || _agent == null || !_agent.enabled)
                return;
            bool updatePosition = link.AgentUpdatePosition;
            if (!_soldier.Navigator.TryRestoreAtCurrentPosition(ExitReach, float.PositiveInfinity,
                    updatePosition, preservePosition: true))
            {
                Vector3 body = _soldier.transform.position;
                NativeMineshaftAdapter car = link.Car;
                bool atTarget = car.ParkedOpenAt(link.TargetBottom);
                bool landed = (atTarget || car.ParkedOpenAt(link.SourceBottom))
                    && _soldier.Navigator.TryRestoreOnLanding(car.LandingOf(atTarget ? link.TargetBottom : link.SourceBottom), updatePosition);
                if (!landed)
                    _soldier.Navigator.ReleaseManualMovementInPlace(updatePosition);
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("Elevator",
                        $"event=handBack, member={_soldier.MemberIndex}, from={BlacksiteDiagnosticFormat.V(body)}, "
                        + $"landing={(landed ? BlacksiteDiagnosticFormat.V(_soldier.transform.position) : BlacksiteDiagnosticFormat.None)}, "
                        + $"moved={Vector3.Distance(body, _soldier.transform.position):0.00}, doors={car.DoorsAreOpen}");
            }
            link.AgentSaved = false;
        }

        /// <summary>
        /// One hand-driven step across the cabin lip, with the car's own colliders removed from the
        /// horizontal sweep while it is parked here with its doors open (see <see cref="BeginCarStep"/>);
        /// the floor support test still has to hit the cabin floor under every step.
        /// </summary>
        private bool WalkCabinThreshold(NativeMineshaftAdapter car, Vector3 target, out BlacksiteStepFailure failure)
        {
            bool scoped = car.TryGetCarIgnore(out MineshaftElevatorController controller, out Bounds cabin)
                && car.DoorsAreOpen;
            if (scoped)
                BeginCarStep(controller, cabin);
            try
            {
                return _soldier.Navigator.WalkPhysicalThreshold(target, MemberStepIgnore, out failure);
            }
            finally
            {
                if (scoped)
                    EndCarStep();
            }
        }

        // ---- #874 S5.3: the elevator-aware ignore, scoped to one hand step. ----
        //
        // While the car is parked at the member's floor with its doors open, its walls and jambs are
        // the thing he is walking INTO, and the baked navmesh stops at the lip. The ignore is bounded
        // three ways: it is opened only around the link's own hand step and closed in the same frame;
        // it is keyed on GetComponentInParent<MineshaftElevatorController>() matching THIS car plus a
        // bounds test against the cabin volume; and it reaches the swept segment only -
        // BlacksitePhysicalStep.TrySupportingGround takes no ignore, so the cabin floor must still be
        // hit under every step. It is composed with BlacksitePhysicalStep.IsSquadBody and, for this
        // member's own steps, the cart he pushes (BlacksitePhysicalStep.IsCartPushedBy).
        private static MineshaftElevatorController _scopedCar;
        private static Bounds _scopedCabin;
        private static readonly Predicate<Collider> CarStepIgnore = IgnoreCarStep;
        private Predicate<Collider> _memberStepIgnore;
        private Predicate<Collider> MemberStepIgnore => _memberStepIgnore
            ?? (_memberStepIgnore = c => CarStepIgnore(c) || BlacksitePhysicalStep.IsCartPushedBy(c, _soldier));

        private static void BeginCarStep(MineshaftElevatorController car, Bounds cabin)
        {
            _scopedCar = car;
            _scopedCabin = cabin;
            // The sweep is a 0.32 m capsule, so a wall whose own bounds stop just short of the cabin
            // volume can still be what the capsule grazes on the way in.
            _scopedCabin.Expand(BlacksiteElevatorGeometry.CapsuleRadius * 2f);
        }

        private static void EndCarStep() => _scopedCar = null;

        private static bool IgnoreCarStep(Collider c) => BlacksitePhysicalStep.IsSquadBody(c) || IsScopedCarCollider(c);

        private static bool IsScopedCarCollider(Collider c)
        {
            if (_scopedCar == null || c == null) return false;
            if (!ReferenceEquals(c.GetComponentInParent<MineshaftElevatorController>(), _scopedCar)) return false;
            return _scopedCabin.Intersects(c.bounds);
        }

        /// <summary>
        /// #874 S9.1: a boarding target more than one step height off the landing it is boarded from
        /// is not on the car's floor.
        /// </summary>
        internal static bool CabinFloorDisagreesWithLanding(float targetY, float landingY, out float metres)
        {
            metres = Mathf.Abs(targetY - landingY);
            return metres > BlacksitePhysicalStep.StepHeight;
        }

        /// <summary>
        /// #1305 G3: a follower never follows his lead across the car. A lead whose trip ends on the
        /// member's floor is met at that landing; one bound for the other floor is followed to its
        /// landing once at his link start, boarding or riding; a lead with no trip, on the car's other
        /// floor and off the member's storey, to the landing on his side. The coordinator turns an
        /// off-storey landing into the member's own trip, and he boards the same car.
        /// </summary>
        internal static bool TryGetCrossingLanding(BlacksiteSoldierAI anchor, Vector3 member, out Vector3 landing)
        {
            landing = default;
            if (anchor == null || anchor.isEnemyDead || anchor.isOutside)
                return false;
            Vector3 at = anchor.transform.position;
            for (int i = 0; i < LiveLinks.Count; i++)
            {
                BlacksiteElevatorInteraction entry = LiveLinks[i];
                if (entry._soldier != anchor || !entry.TryGetLiveLink(out Link link))
                    continue;
                if (link.TargetBottom != link.Car.IsBottom(member)
                    && !AtLinkStart(entry._task, at) && !Committed(link, link.Car.IsInside(at + InsideProbe)))
                    return false;
                landing = link.Car.LandingOf(link.TargetBottom);
                return true;
            }
            NativeMineshaftAdapter car = CurrentCar();
            if (car == null || !BlacksiteResponsePolicy.OffStorey(member.y, at.y) || car.IsBottom(at) == car.IsBottom(member))
                return false;
            landing = car.LandingOf(car.IsBottom(at));
            return true;
        }

        /// <summary>
        /// #874 N6: the round's car's two landings, measured as the link measures them. The car is not
        /// a NavMesh link, so the reachability oracle (<see cref="BlacksiteInteractionCoordinator.Route(Vector3, Vector3, BlacksiteSoldierAI, NavMeshPath)"/>)
        /// crosses it between these two points. Read-only: no lane or car state.
        /// </summary>
        internal static bool TryGetNativeLandings(out Vector3 top, out Vector3 bottom)
        {
            top = bottom = default;
            NativeMineshaftAdapter car = CurrentCar();
            if (car == null)
                return false;
            top = car.LandingOf(false);
            bottom = car.LandingOf(true);
            return true;
        }

        private static NativeMineshaftAdapter CurrentCar()
        {
            MineshaftElevatorController controller = RoundManager.Instance != null
                ? RoundManager.Instance.currentMineshaftElevator
                : null;
            if (controller == null || controller.elevatorTopPoint == null || controller.elevatorBottomPoint == null)
                return null;
            if (_car == null || !ReferenceEquals(_car.Controller, controller))
                _car = new NativeMineshaftAdapter(controller);
            return _car;
        }

        /// <summary>
        /// #874 N5: whether an off-mesh link belongs to an elevator - the vanilla mineshaft car or a
        /// PathfindingLib elevator - rather than to the ground. Those are the link's, never the Jump
        /// motor's. Ownership comes from the objects: the authored component, its two endpoint
        /// transforms and the agent's navmesh owner are walked up to a MineshaftElevatorController or
        /// a behaviour implementing PathfindingLib's IElevator; a link in PathfindingLib's non-smart
        /// area is PathfindingLib's; and a link with an end inside the live car's column is the car's.
        /// </summary>
        internal static bool OwnsOffMeshLink(OffMeshLinkData link, UnityEngine.Object navMeshOwner)
        {
            if (!link.valid) return false;
            OffMeshLink authored = link.offMeshLink;
            if (authored != null
                && (authored.area == NonSmartAgentLinkArea
                    || IsElevatorPart(authored.transform)
                    || IsElevatorPart(authored.startTransform)
                    || IsElevatorPart(authored.endTransform)))
                return true;
            if (navMeshOwner is Component owner && IsElevatorPart(owner.transform)) return true;
            MineshaftElevatorController car = RoundManager.Instance != null
                ? RoundManager.Instance.currentMineshaftElevator
                : null;
            if (car == null || car.elevatorBounds == null) return false;
            Bounds column = car.elevatorBounds.bounds;
            // An endpoint laid on the car floor sits on the column's bottom face, not inside it.
            column.Expand(new Vector3(0f, BlacksitePhysicalStep.StepHeight * 2f, 0f));
            return column.Contains(link.startPos) || column.Contains(link.endPos);
        }

        private static bool IsElevatorPart(Transform part)
        {
            for (Transform node = part; node != null; node = node.parent)
            {
                if (node.GetComponent<MineshaftElevatorController>() != null) return true;
                node.GetComponents(ElevatorPartScratch);
                for (int i = 0; i < ElevatorPartScratch.Count; i++)
                {
                    MonoBehaviour behaviour = ElevatorPartScratch[i];
                    if (behaviour == null) continue;
                    Type type = behaviour.GetType();
                    if (!ElevatorComponentTypes.TryGetValue(type, out bool elevator))
                        ElevatorComponentTypes[type] = elevator = ImplementsElevatorInterface(type);
                    if (elevator) { ElevatorPartScratch.Clear(); return true; }
                }
            }
            ElevatorPartScratch.Clear();
            return false;
        }

        private static bool ImplementsElevatorInterface(Type type)
        {
            Type[] interfaces = type.GetInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
                if (string.Equals(interfaces[i].FullName, ElevatorInterfaceTypeName, StringComparison.Ordinal))
                    return true;
            return false;
        }

        // ---- [UAT][Elevator]. Nothing below changes a decision. ----

        /// <summary>
        /// A link event: one <c>[UAT][Elevator] event=&lt;e&gt; member= car= from= to=[, reason=][, cause=]</c> line.
        /// </summary>
        private void Event(Link link, string name, string reason = null, string cause = null)
        {
            if (!BlacksiteDiagnostics.Enabled)
                return;
            BlacksiteDiagnostics.Log("Elevator",
                $"event={name} member={_soldier.MemberIndex} car={BlacksiteResourceKey.Elevator(link.Car.Key).Id} "
                + $"from={Label(link.SourceBottom)} to={Label(link.TargetBottom)}"
                + (reason != null ? ", reason=" + reason : string.Empty)
                + (cause != null ? ", cause=" + cause : string.Empty));
        }

        // One line per change of place; End re-arms it so the next link's first place prints.
        private void ReportQueued(Link link, int place)
        {
            if (!BlacksiteDiagnostics.Enabled)
                return;
            string token = place >= 0 && place < PlaceTokens.Length ? PlaceTokens[place] : "deep";
            if (!LinkGate.ShouldReport(_soldier.MemberIndex, "queued", token, out _))
                return;
            BlacksiteDiagnostics.Log("Elevator",
                $"event=queued member={_soldier.MemberIndex} car={BlacksiteResourceKey.Elevator(link.Car.Key).Id} "
                + $"from={Label(link.SourceBottom)} to={Label(link.TargetBottom)}, place={place}");
        }

        // The BlacksiteStepFailure that refused the step, once per change of blocker.
        private void ReportStepRefused(in BlacksiteStepFailure failure, Vector3 target)
        {
            if (!BlacksiteDiagnostics.Enabled)
                return;
            string blocker = failure.Blocker != null ? failure.Blocker.name : BlacksiteDiagnosticFormat.None;
            if (!LinkGate.ShouldReport(_soldier.MemberIndex, "stepRefused", blocker, out int folded))
                return;
            BlacksiteDiagnostics.Log("Elevator",
                $"event=stepRefused, member={_soldier.MemberIndex}, stepReason={failure.Describe()}, "
                + $"{BlacksiteDiagnosticFormat.Blocker(failure.Blocker)}, "
                + $"from={BlacksiteDiagnosticFormat.V(_soldier.transform.position)}, "
                + $"to={BlacksiteDiagnosticFormat.V(target)}, folded={folded}");
        }

        // #874 N5: a trip refused, and why. Once per member per change of cause or goal cell, because
        // TryCreateNativeMineshaftTask is asked on every destination request.
        private void ReportTripRefused(Vector3 goal, string reason)
        {
            if (!BlacksiteDiagnostics.Enabled)
                return;
            int colon = reason.IndexOf(':');
            string cause = colon > 0 ? reason.Substring(0, colon) : reason;
            float cell = BlacksiteArrivalPolicy.MinDestinationChange;
            if (!LinkGate.ShouldReport(_soldier.MemberIndex, "tripRefused",
                    $"{cause}|{Mathf.RoundToInt(goal.x / cell)}|{Mathf.RoundToInt(goal.y / cell)}|{Mathf.RoundToInt(goal.z / cell)}",
                    out int folded))
                return;
            BlacksiteDiagnostics.Log("Elevator",
                $"event=tripRefused, member={_soldier.MemberIndex}, goal={BlacksiteDiagnosticFormat.V(goal)}, "
                + $"cause={cause}, reason={reason}, folded={folded}");
        }

        // One line per measured car: the cabin, its door face, both landings and staging points.
        private static void LogGeometry(NativeMineshaftAdapter car)
        {
            if (car.GeometryLogged || !BlacksiteDiagnostics.Enabled)
                return;
            car.GeometryLogged = true;
            BlacksiteElevatorGeometry geometry = car.Geometry;
            Transform inside = car.InsideButton;
            float cabinFloorY = car.CabinFloorY(out string floorSource);
            BlacksiteDiagnostics.Log("Elevator",
                $"event=geometry, cabinBounds={(car.Controller.elevatorBounds != null ? BlacksiteDiagnosticFormat.B(car.Controller.elevatorBounds.bounds) : BlacksiteDiagnosticFormat.None)}, "
                + $"inside={(inside != null ? BlacksiteDiagnosticFormat.V(inside.position) : BlacksiteDiagnosticFormat.None)}, "
                + $"doorNormal={(geometry.HasValue ? BlacksiteDiagnosticFormat.V(geometry.DoorNormal) : BlacksiteDiagnosticFormat.None)}, "
                + $"landingTop={BlacksiteDiagnosticFormat.V(car.LandingOf(false))}, landingBottom={BlacksiteDiagnosticFormat.V(car.LandingOf(true))}, "
                + $"stagingTop={BlacksiteDiagnosticFormat.V(car.StagingOf(false))}, stagingBottom={BlacksiteDiagnosticFormat.V(car.StagingOf(true))}, "
                + $"ridePoint={BlacksiteDiagnosticFormat.V(car.RidePoint)}, cabinFloorY={cabinFloorY:0.00}, "
                + $"cabinHeight={(geometry.HasValue ? geometry.CabinHeight.ToString("0.00") : BlacksiteDiagnosticFormat.None)}, "
                + $"floorSource={floorSource}, boundsRejected={(geometry.HasValue && !geometry.BoundsAreCabinShaped ? "shaft" : "none")}");
        }

        private static void LogAdapterFailure(string missing)
        {
            if (LoggedAdapterFailures.Add(missing ?? "<unknown>"))
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Blacksite] Elevator adapter disabled for one integration: missing {missing}.");
        }

        private static string Label(bool bottom) => bottom ? "bottom" : "top";

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
