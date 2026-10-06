using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed class BlacksiteInteractionCoordinator : IDisposable
    {
        // #1016 S2 (design note A.5, block L): ReservationLifetimeSeconds,
        // DestinationClaimLifetimeSeconds and SharedDestinationThreshold are DELETED. They were
        // four stale copies of the director's own reservation model - declared here, referenced
        // nowhere, and three of them duplicating BlacksiteSquadDirector.cs:16-18, which is where
        // the live values are and where ClaimChecks.cs:34 reflects for them. A grep across src/ and
        // tests/ found zero references to any of the three outside this file.
        // #874 S6.2: ObjectiveApproachDistance (3.4 m) is gone. It was the coordinator's own guess at
        // when an override could start, measured to a substituted destination rather than to the lock,
        // and it was the reason the container override could not start until the operator had already
        // walked to the point behind its door. The gate is now the interaction's own use range,
        // BlacksiteObjectiveInteraction.CanBeginOverride, and it has no other caller.

        private static RoundManager _reservationRound;
        private static int _reservationRoundKey = int.MinValue;

        private readonly BlacksiteSoldierAI _soldier;
        private readonly NavMeshAgent _agent;
        private readonly BlacksiteDoorInteraction _doorInteraction;
        private readonly BlacksiteEntranceInteraction _entranceInteraction;
        private readonly BlacksiteElevatorInteraction _elevatorInteraction;
        private readonly BlacksiteObjectiveInteraction _objectiveInteraction;

        private BlacksiteInteractionTask _activeTask;
        private Vector3 _desiredDestination;
        private bool _hasDesiredDestination;
        private Vector3 _lastIssuedDestination;
        private int _lastInteractionTickFrame = -1;
        private bool _disposed;
        // #1293 R4 F2: the last finish printed for a survivor's link. A miss refuses his goal only until
        // the car's state changes (BlacksiteElevatorInteraction.TickMissedLink), so a car that fails him
        // the same way is taken again on each change. Default when the last finish was anything else.
        // The car is the round's own controller, so a new round never matches.
        private (object Car, string Reason) _survivorLinkFailure;
        // #1305 G1 (R15, R2): the destination's own-world route and, only when that is not Reachable,
        // its cross-world route, kept while they answer the same question. No clock: the exit events are
        // a different goal, a different world, the body 0.5 m or more from where they were asked, any
        // task ending (AdvanceActiveTask, Interrupt; the door hand's task included) and a world
        // transfer (NoteWorldTransferred). Each exit asks again on the next destination.
        private const float RouteMemoMetres = .5f;
        private BlacksiteRouteVerdict _ownRoute, _acrossRoute;
        private bool _routesAsked, _acrossAsked, _routesOutside;
        private Vector3 _routesGoal, _routesFrom;
        // #1305 G2 (R16): the leg's owner refused it here; the next memo ask or 0.5 m lifts it.
        private bool _legRefused;
        private Vector3 _legRefusedAt;

        internal BlacksiteInteractionCoordinator(
            BlacksiteSoldierAI soldier,
            NavMeshAgent agent)
        {
            _soldier = soldier;
            _agent = agent;
            EnsureRoundRegistry();
            _doorInteraction = new BlacksiteDoorInteraction(soldier, agent);
            _entranceInteraction = new BlacksiteEntranceInteraction(this, soldier, agent);
            _elevatorInteraction = new BlacksiteElevatorInteraction(this, soldier, agent);
            _objectiveInteraction = new BlacksiteObjectiveInteraction(this, soldier, agent);
        }

        internal bool HasDesiredDestination => _hasDesiredDestination;
        internal Vector3 DesiredDestination => _desiredDestination;

        internal bool IsRequesting(BlacksiteInteractionTask task) => HasActiveTask && task != null
            && _activeTask.Kind == task.Kind && ReferenceEquals(_activeTask.Target, task.Target);
        internal bool HasActiveTask => _activeTask != null && !_activeTask.IsTerminal;
        internal BlacksiteInteractionKind ActiveKind => _activeTask?.Kind ?? BlacksiteInteractionKind.None;
        internal BlacksiteInteractionPhase ActivePhase => _activeTask?.Phase ?? BlacksiteInteractionPhase.None;
        internal string ActiveLabel => _activeTask?.Label ?? string.Empty;
        internal bool IsEntranceTraversalActive => HasActiveTask
            && ActiveKind == BlacksiteInteractionKind.Entrance;
        // #1293 R4: the whole elevator link ticks at frame rate from the soldier's update - its lane
        // forgets a holder or waiter that has not asked for 2 s, and the cabin carries a rider frame
        // by frame.
        internal bool IsElevatorRideActive => HasActiveTask
            && ActiveKind == BlacksiteInteractionKind.Elevator;
        internal bool IsTraversalActive => IsEntranceTraversalActive || IsElevatorRideActive;
        // #1293 R3: once a frame from the navigator's maintenance tick (host only): the link's same-goal
        // refusal ends when the car's state changes.
        internal void TickMissedLink() => _elevatorInteraction.TickMissedLink();
        // #312 P0.4 / #1293 R4: committed means the link holds the body (a hand step or the ride) or
        // the body is in the cabin. Nothing outside the link - a re-destination, a stun, an
        // extraction order - may take him from here.
        internal bool IsCommittedTraversal => HasActiveTask
            && ActiveKind == BlacksiteInteractionKind.Elevator
            && _elevatorInteraction.IsCommitted(_activeTask);
        // #874 S7.4: whether a MOTOR owns this member's body right now, so a stop is not the
        // member's to make. This is the gate hazard avoidance is withheld behind; the gate it used
        // to be withheld behind was "is a traversal committed", which the 2026-09-09 mineshaft run
        // exercised as "walk into a slime and keep walking" (findings-reactivity.md M6).
        //
        // TRUE while something other than the member's own steering is moving the body:
        //   - IsCommittedTraversal: the elevator link holds the body or the body is in the cabin.
        //   - a world-ladder climb and a ship link crossing: both write the transform directly,
        //     with the agent stood down.
        //   - the entrance teleport frame itself, Perform and Confirm, where the member is being
        //     moved between two worlds and has no position to stop at.
        // FALSE while a committed task is still WALKING and the agent is steering: the walk to an
        // elevator landing or queue place, the walk to a ladder foot, an entrance Approach or Align. Those are
        // ordinary walking, and a member walking into a hazard has to be allowed to stop.
        internal bool TraversalForbidsStop
        {
            get
            {
                if (IsCommittedTraversal) return true;
                var navigator = _soldier?.Navigator;
                if (navigator != null && (navigator.IsWorldClimbing || navigator.IsCrossingShipLink)) return true;
                return IsEntranceTraversalActive
                    && (ActivePhase == BlacksiteInteractionPhase.Perform
                        || ActivePhase == BlacksiteInteractionPhase.Confirm);
            }
        }
        internal ulong OwnerToken => _soldier != null && _soldier.NetworkObject != null
            && _soldier.NetworkObject.IsSpawned
                ? _soldier.NetworkObject.NetworkObjectId
                : (ulong)(_soldier != null ? (uint)_soldier.GetInstanceID() : 0U);
        internal ulong SquadId => _soldier != null ? _soldier.SquadId : 0UL;

        /// <summary>
        /// B1: <paramref name="issuer"/> is the requesting caller's <c>issuer:</c> tag. Optional, and null
        /// everywhere it is not known.
        /// </summary>
        internal bool TryHandleDestination(
            Vector3 finalDestination,
            out Vector3 issuedDestination,
            string issuer = null)
        {
            issuedDestination = Vector3.zero;
            if (_disposed || _soldier == null || _agent == null || !_agent.enabled || !_agent.isOnNavMesh)
                return false;

            EnsureRoundRegistry();
            _desiredDestination = finalDestination;
            _hasDesiredDestination = true;

            if (HasActiveTask)
                return TickActiveTask(out issuedDestination);

            // #1305 G1 (R15, Q2): a goal this member's own world does not reach may be the other world's,
            // and the route service names the doorway: its cross-world route's first transition, taken
            // only when that route is Better than the own-world one - and, against an own route that
            // cannot start (a flooded goal outside), only when it is Reachable. #874 A4: a goal the
            // member's world reaches is never an entrance's - the bridge's main-out, fire-in loop of 99
            // crossings in 70 s.
            Vector3 body = _soldier.transform.position;
            AskRoutes(finalDestination, body);
            BlacksiteRouteVerdict own = _ownRoute;
            if (own.Reachable)
                // #1305 G2 (R16): an own-world route that rides the mineshaft car is the car's (its gates
                // unchanged), asked only when the route rides it.
                return own.Next.Kind == BlacksiteRouteLegKind.ElevatorCar
                    && _elevatorInteraction.TryCreateNativeMineshaftTask(finalDestination, out _activeTask)
                    && TickActiveTask(out issuedDestination);
            if (!_acrossAsked)
            {
                _acrossRoute = BlacksiteReachability.Route(BlacksiteTraveller.Soldier(_soldier), body, finalDestination,
                    goalOutside: !_soldier.isOutside);
                _acrossAsked = true;
            }
            BlacksiteRouteVerdict across = _acrossRoute;
            if (!across.Next.Exists || !BlacksiteRouteVerdict.Better(across, own)
                || (!own.Planned && !across.Reachable))
                return false;
            // #1305 G2 (R16): the duty's world admission is the route's input, asked only for a
            // cross-world transition to hand - a world the duty refuses is never routed to
            // (BlacksiteSquadPlanner.AdmitsWorldCrossing).
            if (!_entranceInteraction.AdmitsWorld(!_soldier.isOutside))
                return false;
            // #1305 G2 (R16): the route's first transition goes to its owner. A door is walked through
            // to its far side: the closed leaf ends the walk where the hand opens it (TickLeg). An
            // entrance is handed only inside the hand-off (CanBeginEntrance): short of it the member
            // walks to the doorway with no task, so his weapon, combat and the door's column are not
            // taken from afar; TickLeg hands it at the band. A stranded member has no squad director,
            // so an entrance reservation can never be granted (the task would stay queued and disable
            // its weapon): he walks to the doorway instead.
            BlacksiteRouteTransition next = across.Next;
            if (next.Kind == BlacksiteRouteLegKind.OperableDoor
                || (next.Kind == BlacksiteRouteLegKind.Entrance && (_soldier.HasLostTransport || !AtEntry(next, body))))
            {
                Vector3 leg = next.Kind == BlacksiteRouteLegKind.OperableDoor
                    ? new Vector3(next.ExitX, next.ExitY, next.ExitZ)
                    : new Vector3(next.EntryX, next.EntryY, next.EntryZ);
                if (!TryClaimDestination(leg, out issuedDestination,
                        acceptanceRadius: _soldier.Navigator.ClaimAllowance(leg)))
                    return false;
                _soldier.IssueRawAgentDestination(issuedDestination);
                return true;
            }
            bool created = next.Kind == BlacksiteRouteLegKind.Entrance
                ? _entranceInteraction.TryCreateEntranceTask((EntranceTeleport)next.Handle, finalDestination,
                    "route entrance", out _activeTask)
                : _elevatorInteraction.TryCreateNativeMineshaftLeg(finalDestination,
                    new Vector3(next.ExitX, next.ExitY, next.ExitZ), out _activeTask);
            return created && TickActiveTask(out issuedDestination);
        }

        // #1305 G1 (R15): the route memo, asked again only on one of its exit events.
        private void AskRoutes(Vector3 goal, Vector3 body)
        {
            if (_routesAsked && _routesGoal == goal && _routesOutside == _soldier.isOutside
                && (body - _routesFrom).sqrMagnitude < RouteMemoMetres * RouteMemoMetres)
                return;
            _ownRoute = BlacksiteReachability.Route(BlacksiteTraveller.Soldier(_soldier), body, goal);
            _routesAsked = true;
            _acrossAsked = false;
            _legRefused = false;
            _routesGoal = goal;
            _routesOutside = _soldier.isOutside;
            _routesFrom = body;
        }

        /// <summary>
        /// #1305 G2 (R16): the transition belongs to the leg, whatever the order. Once a frame from the
        /// navigator's maintenance tick (host only): when the body has reached the memoised route's
        /// next transition (<see cref="BlacksiteCombatPolicy.CanBeginEntrance"/>, the hand-off), that
        /// leg goes to its owner - an entrance or the car through <see cref="TryHandleDestination"/>, a
        /// door to the door hand. True with <paramref name="issued"/> when a task issued a step. The memo
        /// is not re-gated: the hand-off re-asks it. A refusal stands until the memo is asked again or the
        /// body moves 0.5 m from where it was refused.
        /// </summary>
        internal bool TickLeg(out Vector3 issued)
        {
            issued = default;
            if (_disposed || _soldier == null || _agent == null || HasActiveTask || !_hasDesiredDestination || !_routesAsked)
                return false;
            Vector3 body = _soldier.transform.position;
            if (_legRefused && (body - _legRefusedAt).sqrMagnitude < RouteMemoMetres * RouteMemoMetres)
                return false;
            if (!AtNextEntry(body))
                return false;
            bool door = NextLeg.Kind == BlacksiteRouteLegKind.OperableDoor;
            bool handed;
            if (door)
            {
                handed = _doorInteraction.TryCreatePathTask(_desiredDestination, out _activeTask);
                if (handed) TickActiveTask(out _);
            }
            else
                handed = TryHandleDestination(_desiredDestination, out issued) && HasActiveTask;
            _legRefused = !handed;
            _legRefusedAt = body;
            return handed && !door;
        }

        // The memo's next transition: the own-world route's when it is Reachable, else the cross-world one's.
        private BlacksiteRouteTransition NextLeg
            => _ownRoute.Reachable || !_acrossAsked ? _ownRoute.Next : _acrossRoute.Next;

        private bool AtNextEntry(Vector3 body) => AtEntry(NextLeg, body);

        // The hand-off band at a transition's entry (BlacksiteCombatPolicy.CanBeginEntrance).
        private static bool AtEntry(in BlacksiteRouteTransition next, Vector3 body)
        {
            if (!next.Exists) return false;
            float dx = body.x - next.EntryX, dz = body.z - next.EntryZ;
            return BlacksiteCombatPolicy.CanBeginEntrance(Mathf.Sqrt(dx * dx + dz * dz), body.y - next.EntryY);
        }

        /// <summary>
        /// #1233 S5 (rule 6): the hand, once per frame from the navigator's maintenance tick.
        /// <paramref name="held"/> is <see cref="BlacksiteSoldierAI.BlacksiteNavigator.BodyHeldOnRoute"/>:
        /// the agent is the writer, it has an unreached route in force and the body is not moving
        /// along it. Only then is the route ahead probed for an operable gate within reach, and a
        /// gate found is operated on this same tick through the ordinary task tail, so the
        /// [UAT][Interaction] line and the navigation restore are the ones
        /// every other kind gets. A body that is not held forgets the gate it last operated, so a
        /// door closed behind it is a door again on its next approach.
        /// </summary>
        internal void TickGateInReach(bool held, Vector3 goal)
        {
            if (_disposed || _soldier == null || _agent == null)
                return;
            if (!held || HasActiveTask)
            {
                _doorInteraction.ReleaseHand();
                return;
            }
            if (_doorInteraction.TryCreatePathTask(goal, out _activeTask))
                TickActiveTask(out _);
        }

        internal bool TryGetMissionObjectiveDestination(
            BlacksiteRetrievalMissionController mission,
            out Vector3 destination)
        {
            destination = Vector3.zero;
            if (_disposed || mission == null || mission.IsTerminal)
                return false;

            if (!_objectiveInteraction.TryResolvePendingObjective(
                    mission,
                    out object target,
                    out BlacksiteInteractionKind kind,
                    out destination,
                    out Vector3 alignAnchor))
            {
                return false;
            }

            if (HasActiveTask)
                return _activeTask.Kind == BlacksiteInteractionKind.ContainerOverride
                    || _activeTask.Kind == BlacksiteInteractionKind.CacheOverride;

            // #874 S6.2: the override is the first step of the drive job, so it starts when the
            // operator is inside the interaction's OWN use range of the lock. It used to start only
            // inside 3.4 m of a substituted destination, which the operator only ever reached after
            // walking to the wrong point - the point behind the container door - first.
            if (BlacksiteObjectiveInteraction.CanBeginOverride(_soldier.transform.position, alignAnchor)
                && _objectiveInteraction.TryCreateTask(
                    mission,
                    target,
                    kind,
                    destination,
                    out _activeTask))
            {
                _desiredDestination = destination;
                _hasDesiredDestination = true;
            }

            return true;
        }

        internal void TickActiveInteraction()
        {
            if (_disposed || !HasActiveTask)
                return;

            TickActiveTask(out _);
        }

        internal bool TryBeginEntrance(
            EntranceTeleport entrance,
            Vector3 finalDestination,
            string reason)
        {
            // Stranded members never queue an entrance reservation; see TryHandleDestination.
            if (_disposed || HasActiveTask || (_soldier != null && _soldier.HasLostTransport))
                return false;

            if (!_entranceInteraction.TryCreateEntranceTask(
                    entrance,
                    finalDestination,
                    reason,
                    out _activeTask))
            {
                return false;
            }

            _desiredDestination = finalDestination;
            _hasDesiredDestination = true;
            return true;
        }

        internal void Interrupt(string reason)
        {
            if (_activeTask != null)
            {
                if (_activeTask.Kind == BlacksiteInteractionKind.ContainerOverride
                    || _activeTask.Kind == BlacksiteInteractionKind.CacheOverride)
                {
                    _objectiveInteraction.Interrupt(_activeTask, reason);
                }
                else if (_activeTask.Kind == BlacksiteInteractionKind.Elevator)
                {
                    _elevatorInteraction.Interrupt(_activeTask, reason);
                }
                ReleaseReservation(_activeTask);
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Soldier {_soldier.MemberIndex} interrupted " +
                    $"{_activeTask.Kind} task '{_activeTask.Label}': {reason}.");
                if (BlacksiteDiagnostics.Enabled)
                {
                    BlacksiteDiagnostics.Log(
                        "Interaction",
                        $"member={_soldier.MemberIndex}, kind={_activeTask.Kind}, phase={_activeTask.Phase}, target={_activeTask.Label}, result=interrupted, reason={reason}.");
                }
                _survivorLinkFailure = default; // #1293 R4 F2: an interrupt is a finish of another result.
                _activeTask = null;
                _routesAsked = false;
                // An interrupted task leaves no stamp: a task begun later on this frame takes its first tick.
                _lastInteractionTickFrame = -1;
            }
            _soldier.RestoreWorldInteractionNavigation();
        }

        internal bool TryAcquireReservation(
            BlacksiteInteractionTask task,
            out Vector3 queuePosition)
        {
            queuePosition = task != null ? task.ApproachPosition : _soldier.transform.position;
            if (task == null || task.Target == null)
                return false;

            if (_soldier.TryGetSquadDirector(out BlacksiteSquadDirector director))
            {
                return director.TryAcquireInteraction(
                    _soldier,
                    task,
                    OwnerToken,
                    out queuePosition);
            }

            return false;
        }

        internal void RenewReservation(BlacksiteInteractionTask task)
        {
            if (task == null || task.Target == null)
                return;

            if (_soldier.TryGetSquadDirector(out BlacksiteSquadDirector director))
                director.RenewInteraction(task, OwnerToken);
        }

        // #874 S5.4 (#873): a member has transferred and the threshold is physically free. The
        // director owns the column, so it owns the cleared-group measurement and its line.
        internal void NoteEntranceCleared(BlacksiteInteractionTask task)
        {
            if (task == null || task.Target == null)
                return;

            _soldier.NoteTraversalMilestone("entrance:cleared"); // #874 S6.1 wait milestone.
            if (_soldier.TryGetSquadDirector(out BlacksiteSquadDirector director))
                director.NoteEntranceCleared(_soldier, task, OwnerToken);
        }

        /// <summary>
        /// #874 F05: this member has just changed WORLD, and the standing destination this
        /// coordinator holds was resolved for the world it left.
        ///
        /// <c>TryHandleDestination</c> records <c>_desiredDestination</c> on EVERY call, task
        /// active or not. On 2026-09-21 round 3 member 2 crossed into the facility at t=903.39
        /// while the last request it had taken was the outside main entrance, and the smart path
        /// then restarted toward that point and reserved the same doorway outward 0.96 s later
        /// (t=904.35), which is the exit half of the exit/re-entry loop that cost 5.4 s and three
        /// more crossings.
        ///
        /// So the request is DROPPED rather than re-pointed: this type does not own goals and must
        /// not invent one for the new world. The next decision issues its own.
        /// </summary>
        internal void NoteWorldTransferred()
        {
            if (_disposed)
                return;
            _hasDesiredDestination = false;
            _desiredDestination = Vector3.zero;
            _routesAsked = false;
        }

        internal void ReleaseReservation(BlacksiteInteractionTask task)
        {
            if (task == null || task.Target == null)
                return;

            if (_soldier.TryGetSquadDirector(out BlacksiteSquadDirector director))
                director.ReleaseInteraction(task, OwnerToken);
            _soldier.RestoreWorldInteractionNavigation();
        }

        /// <summary>
        /// #874 Q1: an interaction's own queue or staging point, claimed within the table's widest
        /// crowd ring (<see cref="BlacksiteArrivalPolicy.MaxClaimOffset"/>).
        /// </summary>
        internal Vector3 ClaimDestination(Vector3 destination)
        {
            // The landing corridor already has physical clearance. Native avoidance supplies
            // spacing; a formation offset would move its waypoints outside that proof.
            if (_soldier.Navigator.FollowingGroundRoute) return destination;
            if (_soldier.TryGetSquadDirector(out BlacksiteSquadDirector director))
                return director.ClaimDestination(_soldier, OwnerToken, destination);
            return destination;
        }

        /// <summary>
        /// #874 Q2: <see cref="ClaimDestination"/> for a leg of the member's own request, which
        /// can refuse: false when the point sits in an objective holder's keep-out and cannot be
        /// moved out within <paramref name="acceptanceRadius"/>. Nothing is issued then.
        /// </summary>
        internal bool TryClaimDestination(Vector3 destination, out Vector3 claimed, bool exact = false,
            float acceptanceRadius = BlacksiteArrivalPolicy.MaxClaimOffset)
        {
            claimed = destination;
            if (_soldier.Navigator.FollowingGroundRoute) return true;
            if (_soldier.TryGetSquadDirector(out BlacksiteSquadDirector director))
                return director.TryClaimDestination(_soldier, OwnerToken, destination, exact, acceptanceRadius, out claimed);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Interrupt("coordinator disposed");
            _disposed = true;
            _survivorLinkFailure = default;
        }

        // #312, #1293 R3: a task advances at most once per frame whoever asks; a later ask on that
        // frame answers with the destination that tick issued.
        private bool TickActiveTask(out Vector3 issuedDestination)
        {
            if (_activeTask != null && _lastInteractionTickFrame == Time.frameCount)
            {
                issuedDestination = _lastIssuedDestination;
                return true;
            }
            _lastInteractionTickFrame = Time.frameCount;
            bool handled = AdvanceActiveTask(out issuedDestination);
            _lastIssuedDestination = issuedDestination;
            // A finished task leaves no stamp: a task begun later on this frame takes its first tick.
            if (_activeTask == null) _lastInteractionTickFrame = -1;
            return handled;
        }

        private bool AdvanceActiveTask(out Vector3 issuedDestination)
        {
            issuedDestination = _soldier.transform.position;
            if (_activeTask == null)
                return false;

            BlacksiteInteractionTickResult result;
            switch (_activeTask.Kind)
            {
                case BlacksiteInteractionKind.Door:
                    result = _doorInteraction.Tick(_activeTask, out issuedDestination);
                    break;
                case BlacksiteInteractionKind.Entrance:
                    result = _entranceInteraction.Tick(_activeTask, out issuedDestination);
                    break;
                case BlacksiteInteractionKind.Elevator:
                    result = _elevatorInteraction.Tick(_activeTask, out issuedDestination);
                    break;
                case BlacksiteInteractionKind.ContainerOverride:
                case BlacksiteInteractionKind.CacheOverride:
                    result = _objectiveInteraction.Tick(_activeTask, out issuedDestination);
                    break;
                default:
                    result = BlacksiteInteractionTickResult.Failed;
                    break;
            }

            if (result == BlacksiteInteractionTickResult.Running)
                return true;

            BlacksiteInteractionTask finished = _activeTask;
            if (result == BlacksiteInteractionTickResult.Failed && finished.Kind == BlacksiteInteractionKind.Elevator)
                _elevatorInteraction.Interrupt(finished, finished.FailureReason ?? "elevator task failed");
            ReleaseReservation(finished);
            _activeTask = null;
            _routesAsked = false;
            _soldier.RestoreWorldInteractionNavigation();

            if (!RepeatsSurvivorLinkFailure(finished, result))
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Soldier {_soldier.MemberIndex} interaction " +
                    $"'{finished.Label}' finished with {result}{(finished.FailureReason != null ? ": " + finished.FailureReason : string.Empty)}.");
                if (BlacksiteDiagnostics.Enabled)
                {
                    BlacksiteDiagnostics.Log(
                        "Interaction",
                        $"member={_soldier.MemberIndex}, kind={finished.Kind}, phase={finished.Phase}, target={finished.Label}, result={result}, reason={finished.FailureReason ?? "none"}.");
                }
            }
            issuedDestination = _soldier.transform.position;
            // #1293 R3: a finished task ends the commitment the member's decision carried out.
            _soldier.RequestDecision(BlacksiteDecisionCause.Outcome);
            return true;
        }

        /// <summary>
        /// #1293 R4 F2: whether this finish repeats the survivor link failure printed last - a
        /// member with no mission whose link on the same car failed again for the same reason. Only
        /// the finish lines are withheld; the link's own [UAT][Elevator] lines are not. Any other
        /// finish (a mission member, another reason, another car, a Complete, another kind) is
        /// printed and forgets the key, so the next failure of a cause prints once again.
        /// </summary>
        private bool RepeatsSurvivorLinkFailure(BlacksiteInteractionTask finished, BlacksiteInteractionTickResult result)
        {
            if (finished.Kind != BlacksiteInteractionKind.Elevator
                || result != BlacksiteInteractionTickResult.Failed
                || finished.Target == null
                || !_elevatorInteraction.HasNoMission)
            {
                _survivorLinkFailure = default;
                return false;
            }
            bool repeat = ReferenceEquals(_survivorLinkFailure.Car, finished.Target)
                && _survivorLinkFailure.Reason == finished.FailureReason;
            _survivorLinkFailure = (finished.Target, finished.FailureReason);
            return repeat;
        }

        // #1016 S2 (design note A.5, block L): BuildQueuePosition is DELETED. It was the
        // coordinator's own two-abreast fan for a queue position, superseded by
        // BlacksiteSquadDirector.BuildInteractionQueuePosition and its entrance column in #874
        // S5.4, and referenced nowhere since. Nothing replaces it: the column that took over its
        // job is now a BlacksiteResourceLane, and BlacksiteEntranceColumn.BackMetres is the
        // standing place.
        private static void EnsureRoundRegistry()
        {
            RoundManager round = RoundManager.Instance;
            int roundKey = GetRoundKey();
            if (ReferenceEquals(round, _reservationRound)
                && roundKey == _reservationRoundKey)
                return;

            _reservationRound = round;
            _reservationRoundKey = roundKey;
            BlacksiteEntranceInteraction.ResetRoundCache();
            BlacksiteElevatorInteraction.ResetRoundSessions();
            BlacksiteObjectiveInteraction.ResetRoundProgress();
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
                int level = RoundManager.Instance?.currentLevel != null
                    ? RoundManager.Instance.currentLevel.GetInstanceID()
                    : 0;
                return ((seed * 397) ^ scene) * 397 ^ level;
            }
        }
    }
}
