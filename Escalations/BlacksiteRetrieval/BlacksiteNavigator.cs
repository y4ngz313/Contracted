using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        // Per-member movement owner. Nesting keeps the soldier's mission and animation details
        // private while navigation is extracted without changing their decisions.
        internal sealed partial class BlacksiteNavigator : IDisposable
        {
            private NavMeshAgent _agent;
            private BlacksiteInteractionCoordinator _interactionCoordinator;
            private bool _followingGroundRoute;
            private bool _groundRouteHasFurtherWaypoint;
            private Vector3 _lastDestination;
            private bool _hasLastDestination;
            // #874 S1.2: one writer, three named owners. Each owner sets or clears only its own
            // slot; ApplyNavigationSettings recomputes the agent from the innermost live owner, so
            // a door, elevator, entrance, combat hold or cancellation restores to the still-active
            // order profile when there is one and to the spawn baseline when there is not.
            // #874 S4.2: both live in BlacksiteArrivalPolicy, which is Unity-free, so the headless
            // arrival-ordering regression measures the same numbers the agent is configured with.
            private const float DefaultStoppingDistance = BlacksiteArrivalPolicy.DefaultStoppingDistance;
            private const float GroundRouteStoppingDistance = BlacksiteArrivalPolicy.GroundRouteStoppingDistance;
            private bool _worldInteractionStoppingDistanceOverridden;
            private float _worldInteractionStoppingDistance;
            private BlacksiteApproachProfile _approach;
            // #874 S2.1: a fourth named owner for the one action combat itself performs. Combat is
            // not an order, so CommitBehaviour releases the order slot on every combat decision;
            // a melee close-in that stored its reach contract there would lose it the same frame.
            // Its own slot keeps the single-writer rule intact - ApplyNavigationSettings still
            // recomputes the agent from the innermost live owner - while letting the combat
            // action, and only the combat action, release it.
            private BlacksiteApproachProfile _combatApproach;
            private float _combatDestinationAt = float.NegativeInfinity;
            private readonly BlacksiteSoldierAI _soldier;
            // #1015 S1: the one writer of this member's NavMeshAgent. Every destination, stop,
            // speed and stopping-distance write in the runtime goes through it.
            private readonly BlacksiteMotionOwner _motion;
            // #874 S4: who writes the transform. Patrol alert owns a separately cancellable dwell
            // contribution; unrelated holds retain their deadline even while a longer scan is live.
            private BlacksiteBodyWriter _bodyWriter;
            private float _dwellUntil;
            private string _dwellReason;
            private float _patrolAlertUntil;
            private readonly NavMeshPath _navPath = new NavMeshPath();
            private bool _disposed;
            internal NavMeshAgent Agent => _agent;
            internal BlacksiteInteractionCoordinator Interactions => _interactionCoordinator;
            internal BlacksiteMotionOwner Motion => _motion;
            internal BlacksiteBodyWriter BodyWriter => _bodyWriter;
            /// <summary>#874 S4: the Time.time deadline of the dwell in force; 0 when none.</summary>
            internal float DwellUntil => Mathf.Max(_dwellUntil, _patrolAlertUntil);
            internal bool InDwell => Time.time < DwellUntil;
            // The name survives because native fixtures read it by string.
            internal bool MotionHeld => InDwell || _bodyWriter != BlacksiteBodyWriter.Agent;
            /// <summary>
            /// #874 A5: the route generation of the retained goal - host-only, monotonic, owned by the
            /// motion owner. Read-only seam for the per-generation walk budget and goal blacklist.
            /// </summary>
            internal long RouteGeneration => _motion.RouteGeneration;
            /// <summary>#874 A5: the duty board opened a new attempt for this member's duty.</summary>
            internal void NoteDutyAttempt() => _motion.NoteDutyAttempt();
            internal bool FollowingGroundRoute => _followingGroundRoute;
            internal bool IsWorldInteractionHoldActive => InDwell;
            internal bool HasLastDestination => _hasLastDestination;
            internal Vector3 LastDestination => _lastDestination;
            /// <summary>#1305 R2/R18/R23: the cart's goal is the destination this navigator holds for the agent (the last one, else the one issued to it); none is no plan, never the soldier's own position.</summary>
            internal Vector3? IntendedGoal => _hasLastDestination ? _lastDestination : _hasIssuedDestination ? _issuedDestination : (Vector3?)null;
            internal void PauseForStimulus() => Dwell(.18f, "stimulus");
            internal void InvalidateDestination(bool clearPosition)
            {
                _hasLastDestination = false;
                _motion.ForgetHeldRequest();
                ReleaseDestinationClaim();
                ForgetIssuedDestination();
                if (clearPosition) _lastDestination = Vector3.zero;
            }
            internal bool HasLastGoodPosition => _hasLastGoodNavPosition;
            internal Vector3 LastGoodPosition => _lastGoodNavPosition;
            // #874 S4: a crossing motor owns the body. The payload coupling is the one crossing that
            // is NOT committed: the duty board keeps issuing the pusher's goal, because that goal is
            // where the cart steers the pair (EndPayloadCoupling).
            internal bool HasCommittedMotion => (_bodyWriter == BlacksiteBodyWriter.Crossing && !_payloadCoupled)
                || _interactionCoordinator?.IsCommittedTraversal == true;
            /// <summary>#1271 S2: the member is under way on a route of its own (BlacksiteMotionPolicy.RouteInForce).</summary>
            internal bool HasRoute
                => BlacksiteMotionPolicy.RouteInForce(HasCommittedMotion, _hasIssuedDestination, StandsAtRouteEnd);

            /// <summary>
            /// #1064 WP3: the NARROWER question the weapon needs, and deliberately not <see cref="HasCommittedMotion"/>:
            /// a member walking the ship's deck has both hands free, and stowing its rifle for the whole leg left
            /// riflemen 4 and 5 disabled from t=112.6 to t=125.1 beside a dog (2026-09-15 capture D1). TRUE only for
            /// the motions that physically take both hands: a world ladder, the ship's rope and the ship ladder's rungs
            /// (#1283 S4: not its walk onto the mount or its deck leg), and an interior
            /// jump long enough to play its clip (#1271: a shorter hop keeps the rifle or the cargo in the hands, and
            /// a lip drop takes none). They are the crossings UpdatePoseFlagsServer publishes as Climbing, or as
            /// Jumping with a rate that shows the clip, so this host-side answer and the replicated pose agree.
            /// </summary>
            internal bool RequiresBothHands => IsWorldClimbing || (IsJumping && (ShipCrossingKind.HasValue
                ? BlacksiteLinkCrossing.ShipCrossingTakesHands(ShipCrossingKind.Value, LadderPhase)
                : BlacksiteTraversalPresentation.ShowsJumpClip(BlacksiteTraversalPresentation.JumpAnimatorSpeed(CrossingSeconds))));

            internal BlacksiteNavigator(BlacksiteSoldierAI soldier)
            { _soldier = soldier; _motion = new BlacksiteMotionOwner(soldier, this); }
            internal void Bind(NavMeshAgent agent)
            {
                if (_agent == null) _agent = agent;
                if (_soldier.agent == null && _agent != null) _soldier.agent = _agent;
            }
            internal void EnsureInteractions()
            {
                if (!_disposed && _interactionCoordinator == null && _agent != null)
                    _interactionCoordinator = new BlacksiteInteractionCoordinator(_soldier, _agent);
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                ReleaseWorldLadder();
                ReleaseJump("dispose");
                _interactionCoordinator?.Dispose();
                _interactionCoordinator = null;
                _motion.Clear();
                _bodyWriter = BlacksiteBodyWriter.Agent;
                _dwellUntil = 0f;
                _dwellReason = null;
                _patrolAlertUntil = 0f;
                _shipQueueSince = 0f;
                _directBoardShip = null;
            }

            // ---------------------------------------------------------------------------------
            // #874 S4: the body writer and the one dwell. A crossing (jump link, ship rope, ladder
            // or lip, world-ladder climb, elevator ride, payload coupling) or a seat (ship passenger
            // seat, corpse, monster capture) writes the transform; every other stillness is a dwell,
            // under which the agent stays the writer and is stopped until the deadline.
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #874 S4: stand still for <paramref name="seconds"/>. Extends a live dwell, never
            /// shortens it. Requests meanwhile are issued normally (the path computes while the agent
            /// is stopped) and the expiry in <see cref="TickMaintenance"/> releases the body.
            /// </summary>
            internal void Dwell(float seconds, string reason)
            {
                if (seconds <= 0f) return;
                float until = Time.time + seconds;
                if (reason == "patrolAlert")
                {
                    if (until <= _patrolAlertUntil) return;
                    _patrolAlertUntil = until;
                }
                else
                {
                    if (until <= _dwellUntil) return;
                    _dwellUntil = until;
                    _dwellReason = reason;
                }
                _motion.StopAgent();
            }

            /// <summary>Releasing the guard's scan never releases a door, combat or traversal hold.</summary>
            internal void EndPatrolAlert()
            {
                if (_patrolAlertUntil <= 0f) return;
                _patrolAlertUntil = 0f;
                if (!InDwell && _bodyWriter == BlacksiteBodyWriter.Agent) _motion.ResumeAgent();
            }

            internal void EndDwell()
            {
                _dwellUntil = 0f;
                _dwellReason = null;
                _patrolAlertUntil = 0f;
                if (_bodyWriter == BlacksiteBodyWriter.Agent) _motion.ResumeAgent();
            }

            /// <summary>#874 S4: a crossing motor takes the body (refused while a crossing or seat has it). TickMaintenance raises the Crossing event.</summary>
            internal bool BeginCrossing(string reason)
            {
                if (_bodyWriter != BlacksiteBodyWriter.Agent) return false;
                _bodyWriter = BlacksiteBodyWriter.Crossing;
                _motion.StopAgent();
                return true;
            }

            /// <summary>#874 S4: the crossing motor hands the body back at <paramref name="at"/>.</summary>
            internal void EndCrossing(Vector3 at)
            {
                if (_bodyWriter == BlacksiteBodyWriter.Crossing) HandBack(at);
            }

            /// <summary>#874 S4: a seat takes the body; it outranks a crossing, whose own end is then ignored.</summary>
            internal void BeginSeat(string reason)
            {
                _bodyWriter = BlacksiteBodyWriter.Seated;
                _motion.StopAgent();
            }

            internal void EndSeat(Vector3 at)
            {
                if (_bodyWriter == BlacksiteBodyWriter.Seated) HandBack(at);
            }

            private void HandBack(Vector3 at)
            {
                _bodyWriter = BlacksiteBodyWriter.Agent;
                if (!InDwell) _motion.ResumeAgent();
                if (_agent != null && _agent.enabled && !_agent.isOnNavMesh && !_soldier._boardedShip)
                    BindAt(at, "hand-back");
                RequestImmediateDestinationRefresh();
            }

            // The motion facts TickMaintenance raises a decision on, each on its rising edge: the issued
            // goal reached, the corridor leg due and the body at the end of a route that stops short;
            // the outdoor flood step changing; and the body's writer since the last tick (_observedWriter).
            private bool _issuedGoalArrived, _groundRouteLegDue, _atRouteEnd;
            private int _floodStep = int.MinValue;
            private BlacksiteBodyWriter _observedWriter;

            /// <summary>
            /// #874 S4: the dwell's expiry and the off-mesh guard. #1233 S5: a body held on its route
            /// asks for a gate within reach. #1293 R3: motion facts become events on the frame they change.
            /// </summary>
            internal void TickMaintenance()
            {
                if (_disposed) return;
                _motion.Tick();
                if (DwellUntil > 0f)
                {
                    if (!InDwell)
                    {
                        EndDwell();
                        _soldier.RequestDecision(BlacksiteDecisionCause.Outcome);
                    }
                    else if (_bodyWriter == BlacksiteBodyWriter.Agent) _motion.StopAgent();
                }
                EnsureOnNavMesh();
                _interactionCoordinator?.TickGateInReach(BodyHeldOnRoute, _issuedDestination);
                // #1305 G2 (R16): the route's next transition is handed to its owner where the body
                // reaches it; the step it issued is a leg of the goal in force, as in TrySetSmartDestination's caller.
                Vector3 legGoal = CommitmentGoal;
                if (_interactionCoordinator != null && _interactionCoordinator.TickLeg(out Vector3 legStep))
                {
                    RecordInteractionStep(legStep);
                    NoteIssuedGoal(legGoal, BlacksiteMotionOwner.InteractionLeg);
                }
                // A re-path in flight (Pending) keeps the arrival and route-end facts it replaces: its
                // path is unknown, so arrival is not path-true (#1219 C1) and the route has no end yet.
                // A route end the motion owner already asked again from (the same path state, the same
                // end) is no new fact. Each agent fact the two latches read is read once.
                BlacksitePathState path = PathState;
                bool arrived = _issuedGoalArrived, atRouteEnd = _atRouteEnd;
                if (path != BlacksitePathState.Pending)
                {
                    arrived = atRouteEnd = false;
                    if (_hasIssuedDestination)
                    {
                        float tolerance = ArrivalTolerance;
                        float gap = PathEndGapOf(path);
                        bool stopsShort = BlacksiteMotionPolicy.PathStopsShort(path, gap, tolerance);
                        Vector3 body = _soldier.transform.position;
                        arrived = ArrivedOn(_issuedGoal, body, tolerance, stopsShort)
                            || (IssuedWaypointLeg && ArrivedOn(_issuedDestination, body, tolerance, stopsShort));
                        atRouteEnd = stopsShort && BlacksiteMotionPolicy.StandsAtRouteEnd(path, gap, RemainingDistance,
                            EffectiveStoppingDistance, tolerance);
                    }
                }
                bool legDue = _motion.GroundRouteAdvanceDue || _motion.GroundRouteJoinDue;
                if ((arrived && !_issuedGoalArrived) || (legDue && !_groundRouteLegDue)
                    || (atRouteEnd && !_atRouteEnd && !_motion.RouteEndAsked(path, RouteEnd)))
                    _soldier.RequestDecision(BlacksiteDecisionCause.Outcome);
                _issuedGoalArrived = arrived;
                _groundRouteLegDue = legDue;
                _atRouteEnd = atRouteEnd;
                int floodStep = CurrentFloodStep();
                if (floodStep != _floodStep && floodStep != int.MinValue && _floodStep != int.MinValue)
                    _soldier.RequestDecision(BlacksiteDecisionCause.World);
                _floodStep = floodStep;
                // A same-goal link refusal ends when the car's state changes.
                _interactionCoordinator?.TickMissedLink();
                // A crossing or a seat taking the body, or handing it back, is a Crossing event on the net
                // change since the last tick: a motor that takes the body and hands it back between two
                // ticks (a refused hand step) changed no writer and raises nothing.
                if (_bodyWriter != _observedWriter)
                {
                    _observedWriter = _bodyWriter;
                    _soldier.RequestDecision(BlacksiteDecisionCause.Crossing);
                }
            }

            /// <summary>
            /// #1293 R3: the body stands on a point issued for its goal short of that goal - a
            /// smart-path step the interaction coordinator issued, or (#1305 H, C3) a detour waypoint
            /// beside a live hazard disc - so the goal is asked for again from here.
            /// </summary>
            internal bool WaypointLegReached
                => IssuedWaypointLeg && HasArrived(_issuedDestination) && !HasArrived(_issuedGoal);
            private bool IssuedWaypointLeg
                => _hasIssuedDestination && (string.Equals(_issuedLeg, BlacksiteMotionOwner.InteractionLeg, StringComparison.Ordinal)
                    || string.Equals(_issuedLeg, BlacksiteMotionOwner.DetourLeg, StringComparison.Ordinal));
            private bool IssuedDetourLeg
                => _hasIssuedDestination && string.Equals(_issuedLeg, BlacksiteMotionOwner.DetourLeg, StringComparison.Ordinal);

            /// <summary>
            /// #1233 S5 (rule 6): the body is standing where its route is not. The agent is the
            /// writer and nothing holds it, it has a route in force to a point it has not reached,
            /// and it is not moving along it: it has come to rest where a carved route ends short
            /// of the goal, or it is pressed against a closed leaf's obstacle on a complete one. A
            /// live physical fact of this body and nothing else; the one reader is the hand, which
            /// asks whether an operable gate lies within reach along that route. Arrival is the
            /// path-true <see cref="HasArrived"/>, so a member standing where a partial path ends is
            /// held, never arrived.
            /// </summary>
            internal bool BodyHeldOnRoute
                => _bodyWriter == BlacksiteBodyWriter.Agent && !InDwell && !_soldier.InCombat
                   && _hasIssuedDestination && AgentBound && _agent.hasPath && !_agent.pathPending && !_agent.isStopped
                   && AgentVelocity < BlacksiteMotionPolicy.MovingSpeed && !HasArrived(_issuedDestination);

            /// <summary>
            /// #1293 R2: queued for a traversal reservation - the coordinator's task waits in its
            /// Queued phase, or the ship's FIFO has not admitted the member yet. A fact with no clock;
            /// the progress ledger declares it and never judges it.
            /// </summary>
            internal bool InTraversalQueue
                => (_interactionCoordinator != null && _interactionCoordinator.ActivePhase == BlacksiteInteractionPhase.Queued)
                   || IsQueuedAtShipLink;

            // #1013 A6 / #1015 S1: the motion request contract. Every return path names its
            // outcome. The eleven gates themselves now live in BlacksiteMotionOwner.Request, which
            // is the only writer of this member's agent; this stays as a named thin wrapper because
            // five native fixtures (HazardMemory, KiwiAvoidance, MotionPacing, Navigator, SquadRoute)
            // reflect on it by name alone, and an overload would make those lookups ambiguous.
            internal BlacksiteMotionVerdict SetDestinationThrottled(Vector3 destination, bool validatePath = true, bool? groundRouteOutbound = null, string issuer = "order")
                => _motion.Request(new BlacksiteGoal(destination, validatePath, groundRouteOutbound, issuer));

            // #1305 G2 (R16): a goal the caller names in the other world, walked from outside along the
            // outbound corridor to its end; the route takes the doorway there. Its own name, so the
            // fixtures' by-name lookups of SetDestinationThrottled stay unambiguous.
            internal BlacksiteMotionVerdict SetCrossingDestination(Vector3 destination, string issuer)
                => _motion.Request(new BlacksiteGoal(destination, true, true, issuer, goalAcross: true));

            /// <summary>
            /// #1013 A6: the one voice of the motion request contract.
            ///
            /// Every result that is not <see cref="BlacksiteMotionResult.Issued"/> is reported once
            /// per change of the (result, reason) pair for that member, plus at most once per
            /// <see cref="MotionRequestRepeatSeconds"/> while the same pair repeats, as
            /// <c>[UAT][MotionRequest] member=N, issuer=&lt;issuer&gt;, result=&lt;result&gt;, reason=&lt;reason&gt;</c>.
            /// The issuer is the caller's own <c>issuer:</c> tag - including the <c>groundRoute</c>
            /// substitution SetDestinationThrottled makes for itself (#874 S4.0a).
            ///
            /// NOTE-1: the fields are <c>", "</c>-separated like every other UAT area. This line
            /// shipped space-separated and was the only area in the contract that was, which cost
            /// the scoring script a dedicated splitter branch for one line shape.
            ///
            /// Issued is deliberately not recorded in the gate: the gate folds against the last
            /// LOGGED pair, so a refusal that alternates with successful issues still prints each
            /// time the refusal changes rather than each time it recurs.
            ///
            /// Observation only. It returns the result it was handed so every return path can be
            /// written as a single statement.
            /// </summary>
            private const float MotionRequestRepeatSeconds = 5f;
            private static readonly BlacksiteChangeGate MotionRequestGate = new BlacksiteChangeGate();
            private static readonly BlacksiteWindowGate MotionRequestWindow = new BlacksiteWindowGate(MotionRequestRepeatSeconds);
            /// <summary>
            /// #1013 A6 (N7): the gate key is this SOLDIER, not its member index. Both gates are
            /// static, member indices run 0..3 WITHIN a squad, and two squads on the same moon
            /// share every one of them - so a second squad's refusals folded into the first's and
            /// the [UAT][MotionRequest] histogram under-counted both. The key mirrors the
            /// neighbouring FootprintGate precedent, which keys on a per-object identity
            /// (<c>Source.GetInstanceID()</c>) rather than on a per-squad ordinal, and prefers the
            /// network identity where there is one so host and client key the same member the same
            /// way. <c>member=N</c> in the line text is unchanged: the key is not the label.
            /// </summary>
            internal int MotionRequestKey
                => _soldier == null
                    ? 0
                    : _soldier.NetworkObject != null && _soldier.NetworkObject.IsSpawned
                        ? unchecked((int)_soldier.NetworkObject.NetworkObjectId)
                        : _soldier.GetInstanceID();
            internal BlacksiteMotionVerdict ReportMotionRequest(BlacksiteMotionVerdict verdict, string issuer)
            {
                BlacksiteMotionResult result = verdict.Result;
                if (result == BlacksiteMotionResult.Issued || !BlacksiteDiagnostics.Enabled) return verdict;
                int member = _soldier.MemberIndex;
                int key = MotionRequestKey;
                string tag = issuer ?? BlacksiteDiagnosticFormat.None;
                // Both gates are evaluated on every report, never short-circuited: the change gate
                // prints the transition, the window gate a standing refusal at 0.2 Hz (#874 G5: only
                // inside the detail capture window). The fold key is (result, reason) only: requests
                // alternate between issuers within one decision, and keying on the issuer would put
                // the line back at the request rate (#944 S11 U6).
                bool changed = MotionRequestGate.ShouldReport(key, "motionRequest", $"{result}|{verdict.Reason}", out _);
                bool due = MotionRequestWindow.ShouldReport(key, Time.time, out _);

                // #874 G5: open a detail capture window on a non-Issued motion request verdict.
                // Keyed on `key` (MotionRequestKey, the network identity), never on the squad
                // ordinal - the same key BlacksiteSoldierAI.DiagnosticIdentity computes, so a
                // window opened here and one opened by a stall in TraceMotion are the SAME window
                // for the same soldier, and two squads on one moon never share one.
                if (changed)
                    BlacksiteDiagnostics.OpenDetailWindow(key, member, "motionRequest");

                // Always emit on a result|reason change; the 5 s window repeat only emits
                // while this soldier's detail capture window is open.
                if (changed || (due && BlacksiteDiagnostics.IsDetailOpen(key)))
                    BlacksiteDiagnostics.Log("MotionRequest",
                        $"member={member}, issuer={tag}, result={result}, reason={verdict.Reason}"
                        // #1015 S1: appended AFTER reason=, so every key score.ps1 already parses
                        // keeps its name and its position.
                        + $", outcome={BlacksiteMotionVerdict.Token(verdict.Outcome)}");
                return verdict;
            }

            /// <summary>
            /// #874 Q12 (F12): the issue path's route admission, one walk of the route service from the
            /// body. Outdoors while a flood exists, a walk the service does not plan is not walked: its
            /// path is cleared and the request answers floodedGoal. #1262 P3: the corner fold is the
            /// service's, so this refusal and the ship's route fact are one answer; there is no carve and
            /// no escape point, BlacksiteHazardPolicy.FloodRefusesRoute already lets a member the water
            /// has caught walk out. #1305 G1 (Q1, M5): indoors on a level with the mineshaft car, a walk
            /// over a link the mover refuses (<see cref="BlacksiteRouteGap.RefusedLink"/>: a cabin link
            /// under the body, the car's column) is refused the same way; any other indoor walk is issued.
            /// </summary>
            internal bool RefuseFloodedGoal(Vector3 resolved)
            {
                BlacksiteHazardFootprint water = default;
                bool flood = _soldier.isOutside && BlacksiteHazardAwareness.TryGetFlood(out water);
                if (!flood && (_soldier.isOutside || !BlacksiteElevatorInteraction.TryGetCarColumn(out _))) return false;
                Vector3 start = _soldier.transform.position;
                BlacksiteRouteVerdict walk = BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(_soldier), start, resolved, _navPath);
                if (flood ? walk.Planned : walk.Gap != BlacksiteRouteGap.RefusedLink) return false;

                _motion.ClearPath();
                if (BlacksiteDiagnostics.Enabled)
                {
                    BlacksiteDiagnostics.LogRateLimited($"hazardRoute.{_soldier.NetworkObjectId}", 3f, "Navigation",
                        $"member={_soldier.MemberIndex}, route to {resolved} refused "
                        + (flood ? "while hazard footprint live; hazard=water, reason=floodedGoal." : $"by the route service; reason={walk.PathReason}."));
                    if (flood) ReportFlood(water.CurrentCentre.y, start.y, resolved);
                }
                return true;
            }

            /// <summary>
            /// #874 Q12: <c>[UAT][Flood] footprint=water, member=2, plane=-3.10, startDepth=-0.40,
            /// destination=(x, y, z), action=held</c>. Depths are metres below the plane (negative is
            /// dry); #1262 P3: the route's lowest corner is the oracle's fold, not reported here. One
            /// line per member per half-metre of plane, through the S11 U6 change gate, so a member
            /// held at the shore while the water rises reports each rise rather than each re-plan.
            /// </summary>
            private void ReportFlood(float plane, float startY, Vector3 destination)
            {
                if (!FootprintGate.ShouldReport(unchecked((int)_soldier.NetworkObjectId), "water",
                    "held|" + FloodStep(plane), out _)) return;
                BlacksiteDiagnostics.Log("Flood",
                    $"footprint=water, member={_soldier.MemberIndex}, plane={plane:0.00}, "
                    + $"startDepth={plane - startY:0.00}, "
                    + $"destination={BlacksiteDiagnosticFormat.V(destination)}, action=held");
            }

            private static readonly BlacksiteChangeGate FootprintGate = new BlacksiteChangeGate();

            /// <summary>#874 Q12: the flood plane's half-metre step (the [UAT][Flood] rise granularity).</summary>
            private static int FloodStep(float plane) => Mathf.FloorToInt(plane * 2f);

            /// <summary>#1293 R3: the flood step where this member stands now; int.MinValue indoors or in a dry round.</summary>
            private int CurrentFloodStep()
                => _soldier.isOutside && BlacksiteHazardAwareness.TryGetFlood(out var water) ? FloodStep(water.CurrentCentre.y) : int.MinValue;

            /// <summary>#1293 R3: a forgotten goal releases its destination claim (claims no longer expire).</summary>
            private void ReleaseDestinationClaim() => _soldier._squadDirector?.ReleaseDestinationClaim(_soldier);

            // #1305 H (C3): one corner buffer for the admission's planned legs. Main thread only.
            private static readonly Vector3[] AdmitCorners = new Vector3[32];

            /// <summary>
            /// #1305 H (C3): the one route admission against the squad's live hazard discs
            /// (<see cref="BlacksiteHazardAwareness.LiveDiscs"/>), run where a Request goal is
            /// issued; no live disc, no work. The route - the agent's own path when
            /// <paramref name="inForce"/>, else the route service's synchronous walk from the
            /// body - clear of every disc is admitted as asked. One that enters a disc is admitted by
            /// a detour waypoint beside the disc it reaches first (<see cref="TryDetour"/>), which
            /// replaces <paramref name="resolved"/> while the goal stays the goal; with none it is
            /// refused, naming <paramref name="disc"/>. A member standing in a disc is admitted only
            /// on a route that leaves it directly, else by its way out. A detour walked for this goal
            /// stands while the way to its waypoint stays admitted. No side effects, so the
            /// awareness pass may ask it again to see whether a block still stands.
            /// </summary>
            internal bool RefuseHazardRoute(Vector3 goal, ref Vector3 resolved, bool inForce, out bool detour,
                out BlacksiteHazardFootprint disc)
            {
                detour = false; disc = default;
                if (!_soldier.TryGetWorld(out BlacksiteWorldModel world)) return false;
                List<BlacksiteHazardFootprint> discs = BlacksiteHazardAwareness.LiveDiscs(world);
                if (discs.Count == 0) return false;
                Vector3 start = _soldier.transform.position;
                bool walking = AgentBound && _agent.hasPath && !_agent.pathPending;
                if (walking && IssuedDetourLeg && Vector3.Distance(goal, _issuedGoal) <= BlacksiteArrivalPolicy.MinDestinationChange
                    && !HasArrived(_issuedDestination) && !RouteInForceRefused(discs, out _))
                {
                    resolved = _issuedDestination; detour = true;
                    return false;
                }
                bool refused = inForce && walking
                    ? RouteInForceRefused(discs, out disc)
                    : FirstRefusal(discs, start, AdmitCorners, 1, PlanCorners(start, resolved, out _), out disc);
                if (!refused || !TryDetour(discs, disc, start, resolved, true, out Vector3 waypoint)) return refused;
                resolved = waypoint; detour = true;
                return false;
            }

            /// <summary>#1305 H (C3): <see cref="RouteInForceRefused(List{BlacksiteHazardFootprint}, out BlacksiteHazardFootprint)"/> against the squad's live discs now.</summary>
            internal bool HazardRouteInForceRefused()
                => _soldier.TryGetWorld(out BlacksiteWorldModel world)
                   && RouteInForceRefused(BlacksiteHazardAwareness.LiveDiscs(world), out _);

            /// <summary>
            /// #1305 H (C3): whether the planned route from the body to <paramref name="to"/> is clear
            /// of every live disc - a retreat point's own test, so an evade or a regroup never picks a
            /// goal the admission would only detour or refuse.
            /// </summary>
            internal bool HazardRouteClear(Vector3 to)
            {
                if (!_soldier.TryGetWorld(out BlacksiteWorldModel world)) return true;
                List<BlacksiteHazardFootprint> discs = BlacksiteHazardAwareness.LiveDiscs(world);
                if (discs.Count == 0) return true;
                Vector3 start = _soldier.transform.position;
                return !FirstRefusal(discs, start, AdmitCorners, 1, PlanCorners(start, to, out _), out _);
            }

            /// <summary>
            /// #1305 H (C3): the route the agent is walking, from the body, against
            /// <paramref name="discs"/>; a body with no route is refused while it stands in one. A
            /// path still being computed is not judged.
            /// </summary>
            internal bool RouteInForceRefused(List<BlacksiteHazardFootprint> discs, out BlacksiteHazardFootprint disc)
            {
                disc = default;
                if (!AgentBound || _agent.pathPending) return false;
                int next = _agent.hasPath ? SteeredCorner() : 0;
                return FirstRefusal(discs, BodyFloor, _routeCorners, next, next == 0 ? 0 : _routeCornerCount, out disc);
            }

            // The disc a route - start, then corners[from..count) - is refused by first along it; of
            // discs refusing at the same point (a body standing in several), the widest, whose way
            // out leaves them all.
            private static bool FirstRefusal(List<BlacksiteHazardFootprint> discs, Vector3 start, Vector3[] corners,
                int from, int count, out BlacksiteHazardFootprint disc)
            {
                disc = default;
                float first = float.PositiveInfinity;
                for (int d = 0; d < discs.Count; d++)
                {
                    float at = RefusedAt(discs[d], start, corners, from, count);
                    if (at < first || (at == first && at < float.PositiveInfinity && discs[d].Radius > disc.Radius)) { first = at; disc = discs[d]; }
                }
                return disc.HasValue;
            }

            // How far along the route the disc refuses it, or +inf. From outside: the first leg that
            // enters it. From inside: at once unless the route leaves directly - its distance to the
            // centre never shrinks until it is out - and does not come back in.
            private static float RefusedAt(in BlacksiteHazardFootprint disc, Vector3 start, Vector3[] corners, int from, int count)
            {
                const float margin = BlacksiteHazardPolicy.FootprintMargin;
                bool inside = disc.Contains(start, margin);
                Vector3 centre = disc.CurrentCentre, a = start;
                float along = 0f, previous = Vector3.ProjectOnPlane(start - centre, Vector3.up).magnitude;
                for (int i = from; i < count; a = corners[i], i++)
                {
                    Vector3 b = corners[i];
                    if (!inside && disc.Crosses(a, b, margin)) return along;
                    int samples = inside ? Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / BlacksiteHazardPolicy.RouteSampleMetres)) : 0;
                    for (int j = 1; j <= samples && inside; j++)
                    {
                        Vector3 point = Vector3.Lerp(a, b, (float)j / samples);
                        float distance = Vector3.ProjectOnPlane(point - centre, Vector3.up).magnitude;
                        if (distance < previous - .01f) return 0f;
                        previous = distance;
                        inside = disc.Contains(point, margin);
                    }
                    along += Vector3.Distance(a, b);
                }
                return inside ? 0f : float.PositiveInfinity;
            }

            // A fresh walk from -> to on this agent's areas into AdmitCorners (the route service's: a
            // refused link is no walk); 0 corners for none, a PartialFrontier walk's corners otherwise.
            private int PlanCorners(Vector3 from, Vector3 to, out bool complete)
            {
                BlacksiteRouteVerdict walk = BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(_soldier), from, to, _navPath);
                complete = walk.Reachable;
                return walk.Planned ? _navPath.GetCornersNonAlloc(AdmitCorners) : 0;
            }

            private static float PlannedLength(int count)
            {
                float length = 0f;
                for (int i = 1; i < count; i++) length += Vector3.Distance(AdmitCorners[i - 1], AdmitCorners[i]);
                return length;
            }

            /// <summary>
            /// #1305 H (C3): a waypoint around <paramref name="disc"/> - beside it, where the tangents
            /// from the body and from the goal meet (one per side), or, from inside it, on a ring
            /// outside it (the S7.4 escape search's ring, at the disc). A candidate stands when its
            /// first leg is complete and admitted and, with a goal, its onward leg is complete and
            /// clear of every live disc: the shortest wins, one that does not point back past the
            /// member first. From inside, with no admitted onward leg, the shortest way out alone -
            /// the goal is admitted again from there, and with no goal it is the request itself.
            /// </summary>
            internal bool TryDetour(List<BlacksiteHazardFootprint> discs, in BlacksiteHazardFootprint disc, Vector3 start,
                Vector3 goal, bool hasGoal, out Vector3 waypoint)
            {
                waypoint = start;
                Vector3 centre = disc.CurrentCentre;
                float ring = disc.Radius + BlacksiteHazardPolicy.FootprintMargin + BlacksiteHazardPolicy.DetourClearance;
                bool inside = disc.Contains(start, BlacksiteHazardPolicy.FootprintMargin);
                Vector3 away = Vector3.ProjectOnPlane(start - centre, Vector3.up);
                if (away.sqrMagnitude < .01f) away = Vector3.ProjectOnPlane(hasGoal ? goal - centre : _soldier.transform.forward, Vector3.up);
                if (away.sqrMagnitude < .01f) away = Vector3.forward;
                Vector3 toGoal = Vector3.ProjectOnPlane(goal - start, Vector3.up);
                float best = float.PositiveInfinity, shortestExit = float.PositiveInfinity;
                bool found = false, bestReverses = true;
                Vector3 exit = start;
                for (int k = 0; k < (inside ? 5 : 2); k++)
                {
                    Vector3 point = centre + Quaternion.Euler(0f, (k - 2) * 35f, 0f) * away.normalized * ring;
                    if (!inside && !Beside(centre, ring, start, goal, k == 0 ? 1f : -1f, out point)) continue;
                    point.y = start.y;
                    if (!NavMesh.SamplePosition(point, out NavMeshHit hit, 1.5f, _agent.areaMask)
                        || Mathf.Abs(hit.position.y - start.y) > BlacksiteHazardPolicy.DiscFootprintHeight) continue;
                    int count = PlanCorners(start, hit.position, out bool complete);
                    if (!complete || FirstRefusal(discs, start, AdmitCorners, 1, count, out _)) continue;
                    float length = PlannedLength(count);
                    if (hasGoal)
                    {
                        int onward = PlanCorners(hit.position, goal, out bool onwardComplete);
                        if (onwardComplete && !FirstRefusal(discs, hit.position, AdmitCorners, 1, onward, out _))
                        {
                            float total = length + PlannedLength(onward);
                            bool reverses = Vector3.Dot(Vector3.ProjectOnPlane(hit.position - start, Vector3.up), toGoal) < 0f;
                            if (!found || (bestReverses && !reverses) || (reverses == bestReverses && total < best))
                            { found = true; best = total; bestReverses = reverses; waypoint = hit.position; }
                            continue;
                        }
                    }
                    if (inside && length < shortestExit) { shortestExit = length; exit = hit.position; }
                }
                if (!found && shortestExit < float.PositiveInfinity) { found = true; waypoint = exit; }
                return found;
            }

            // Where the tangents to the ring from start and from goal meet on one side (+1 counter-
            // clockwise round the centre): at the middle of the arc between their touch points,
            // ring / cos(arc / 2) out. None past four rings out, or the long way round.
            private static bool Beside(Vector3 centre, float ring, Vector3 start, Vector3 goal, float side, out Vector3 point)
            {
                point = centre;
                Vector3 s = Vector3.ProjectOnPlane(start - centre, Vector3.up), g = Vector3.ProjectOnPlane(goal - centre, Vector3.up);
                float fromStart = Mathf.Atan2(s.z, s.x) + side * Mathf.Acos(Mathf.Min(1f, ring / Mathf.Max(s.magnitude, .01f)));
                float fromGoal = Mathf.Atan2(g.z, g.x) - side * Mathf.Acos(Mathf.Min(1f, ring / Mathf.Max(g.magnitude, .01f)));
                float arc = Mathf.Repeat(side * (fromGoal - fromStart), 2f * Mathf.PI);
                float stretch = Mathf.Cos(arc / 2f);
                if (stretch < .25f) return false;
                float angle = fromStart + side * arc / 2f;
                point = centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (ring / stretch);
                return true;
            }

            /// <summary>
            /// #1305 H (C3): the one [UAT][Hazard] line, on a transition only - a goal
            /// <c>blocked</c>, a <c>detour</c> taken, a block <c>clear</c>ed:
            /// <c>member=2, action=blocked, kind=Coil, owner=SpringManAI, centre=(x, y, z),
            /// radius=2.50, goal=(x, y, z), issuer=duty:Backup</c>; a detour appends
            /// <c>waypoint=(x, y, z)</c>.
            /// </summary>
            internal void LogHazard(string action, in BlacksiteHazardFootprint disc, Vector3 goal, string issuer, Vector3? waypoint = null)
            {
                if (!BlacksiteDiagnostics.Enabled) return;
                BlacksiteDiagnostics.Log("Hazard",
                    $"member={_soldier.MemberIndex}, action={action}, kind={disc.Kind}, "
                    + $"owner={(disc.Owner != null ? disc.Owner.GetType().Name : BlacksiteDiagnosticFormat.None)}, "
                    + $"centre={BlacksiteDiagnosticFormat.V(disc.CurrentCentre)}, radius={disc.Radius:0.00}, "
                    + $"goal={BlacksiteDiagnosticFormat.V(goal)}, issuer={issuer ?? BlacksiteDiagnosticFormat.None}"
                    + (waypoint.HasValue ? $", waypoint={BlacksiteDiagnosticFormat.V(waypoint.Value)}" : string.Empty));
            }

            private void UpdateGroundRoute(bool following, bool hasFurtherWaypoint = false)
            {
                _followingGroundRoute = following;
                // #1127: intermediate waypoints have a further point ahead → no brake.
                _groundRouteHasFurtherWaypoint = hasFurtherWaypoint;
                ApplyNavigationSettings();
            }

            /// <summary>
            /// B1: <paramref name="issuer"/> is the requesting caller's own <c>issuer:</c> tag,
            /// carried through to the interaction coordinator
            /// (<c>BlacksiteInteractionCoordinator.TryHandleDestination</c>). It is passed, never
            /// consumed, on this level.
            /// It is optional so the existing single-argument reflection lookups keep working.
            /// </summary>
            internal bool TrySetSmartDestination(Vector3 resolved, string issuer = null)
            {
                EnsureInteractions();
                BlacksiteInteractionCoordinator coordinator = _interactionCoordinator;
                if (coordinator == null || !coordinator.TryHandleDestination(resolved, out Vector3 issuedDestination, issuer))
                    return false;
                RecordInteractionStep(issuedDestination);
                return true;
            }

            // The coordinator's step for a goal: the point the agent was handed.
            private void RecordInteractionStep(Vector3 issuedDestination)
            {
                _lastDestination = issuedDestination;
                _hasLastDestination = true;
                RecordIssuedDestination(issuedDestination, "interaction");
            }

            // #874 S4.0a: the one-parameter entry point is kept exactly as it was. The native
            // NavigatorChecks fixture reflects on it by name with a single argument, and an
            // overload would make that lookup ambiguous, so the tagged form has its own name.
            internal void IssueRawAgentDestination(Vector3 resolved) => IssueAgentDestination(resolved, "raw");

            internal void IssueAgentDestination(Vector3 resolved, string issuer)
            {
                // #874 S4: a raw destination - the door, entrance, elevator and objective tasks -
                // is a deliberate command. It ends a dwell and is refused while a crossing or a
                // seat owns the body.
                _motion.IssueDirect(resolved, issuer);
            }

            private bool _payloadCoupled;
            /// <summary>#874: the payload coupling is walking this body on the cart's own step.</summary>
            internal bool IsPayloadCoupled => _payloadCoupled;
            // #874: the coupling has suspended this agent's position updates and has not put them
            // back yet. It survives a PREEMPTION on purpose: a monster capture snapshots the flag
            // it finds and restores the value it snapshotted, so a capture that took the body from
            // the coupling would put the coupling's own suspension back and leave the member with a
            // body nobody moves. The motion owner's sweep clears this once nothing owns the body.
            private bool _payloadPositionOwed;
            internal bool PayloadPositionOwed => _payloadPositionOwed;
            internal void NotePayloadPositionRestored() => _payloadPositionOwed = false;

            /// <summary>
            /// #874: take or renew this body for the payload coupling, and suspend the agent's own
            /// position updates while the coupling has it.
            ///
            /// #874 S4: the coupling is a crossing - the cart writes the body. Refused while a dwell
            /// (a combat stance, a door pause) or any other crossing or seat has the body, and that
            /// refusal is what makes a stop, a turn or a queue a WAIT at the handle instead of a
            /// walk: the cart holds where it stands and takes the body again once it is free.
            /// It is the one crossing <see cref="HasCommittedMotion"/> leaves out and the one whose
            /// requests are still issued, because the duty board's goal is where the cart steers the pair.
            /// #1233 S1: <paramref name="atPortal"/> (the portal transaction) is the entrance interaction's own act, not a wait.
            /// </summary>
            internal bool TryHoldForPayloadCoupling(bool atPortal = false)
            {
                if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh) return false;
                if ((InDwell && !atPortal) || HasCommittedMotion) return false;
                if (_payloadCoupled ? _bodyWriter != BlacksiteBodyWriter.Crossing : !BeginCrossing("payload")) return false;
                _payloadCoupled = true;
                if (_motion.SuspendAgentPosition()) { _payloadPositionOwed = true; return true; }
                EndPayloadCoupling();
                return false;
            }

            /// <summary>
            /// #874: carry the coupled body to a pose the cart has already proved legal for both of
            /// them. The one write the coupling makes, and it goes through the motion owner exactly
            /// as every other agent write in this navigator does. #1233 S1: <paramref name="throughPortal"/>
            /// carries it through a paired entrance on the same ownership (writer Crossing, agent suspended).
            /// </summary>
            internal bool TryCarryCoupledBody(Vector3 position, bool throughPortal = false)
                => _payloadCoupled && (throughPortal ? _motion.WarpCoupledBody(position) : _motion.CarryCoupledBody(position));

            /// <summary>
            /// #874: the coupled body faces the way the pair is travelling. Combat keeps the facing
            /// when it owns it, on the same test the passage and ladder motors apply.
            /// </summary>
            internal void FaceCoupledHaul(Vector3 travel)
            {
                if (travel.sqrMagnitude <= .0001f) return;
                if (_soldier._combatController != null && _soldier._combatController.OwnsFacing) return;
                FaceCombatPointAtStep(_soldier.transform.position + travel, FacingTurnDegreesPerSecond, Time.fixedDeltaTime);
            }

            /// <summary>
            /// #874: the coupling is done with this body - the cart let the handle go, or a seat
            /// took the body. The agent is put back on the pose the pair left the member in and
            /// follows the transform again, and the member walks its own goal on the next request.
            /// A seat that took the body is moving it on the same suspended flag, so the suspension
            /// stays OWED and the motion owner's tick hands it back once nothing owns the body.
            /// </summary>
            internal void EndPayloadCoupling()
            {
                if (!_payloadCoupled) return;
                _payloadCoupled = false;
                if (_bodyWriter == BlacksiteBodyWriter.Crossing)
                {
                    if (_motion.ResumeAgentPosition()) _payloadPositionOwed = false;
                    EndCrossing(_soldier.transform.position);
                }
                RequestImmediateDestinationRefresh();
            }

            // -----------------------------------------------------------------------------
            // #874 (2026-09-22): what the coupled pair may ask this navigator for.
            //
            // Connectivity, and nothing else. The cart owns the plan: it cuts a horizon off the
            // world route, searches its own poses to it and chooses any alternative corridor
            // itself (PayloadCartController.Blacksite.cs). This file used to own a second steering
            // memory beside that - a refused-ground disc, a latched waypoint, a side choice and a
            // waypoint-refusal list - and the two disagreed about what had failed: the cart
            // remembered a corner against a pose it could not leave while this latch re-picked a
            // waypoint per plan, which on the 2026-09-22 run produced five waypoints and
            // thirty-one metres of travel for a twelve-metre journey. One owner, one outcome.
            //
            // <see cref="Agent"/> supplies the route and its area mask; these two supply the rest
            // of what a waypoint has to satisfy to be somewhere this member could be sent. Neither
            // is a steering command and neither writes anything.

            /// <summary>The areas a coupled waypoint may be sampled on, deck included.</summary>
            internal int CoupledAreaMask => RecoveryAreaMask();

            // #874 (passenger seats): the ship owns a boarded body's transform on every peer, so
            // the agent must not also be moving it. The two agent update flags are saved as they
            // were found so the release gives back exactly what it took.
            private bool _passengerHeld, _passengerUpdatePosition, _passengerUpdateRotation;

            /// <summary>
            /// #874: take or give back a boarded member's body for the ship's own seat (#874 S4: a
            /// seat). Idempotent in both directions, and the flags are captured exactly ONCE. No Warp
            /// on the way in: the seat is the authority on where the body is. The path is cleared,
            /// so with both agent updates suspended there is no transform for the agent to write.
            /// A DEAD actor stays seated: the death handler owns the corpse.
            /// </summary>
            internal void SetPassengerHold(bool held)
            {
                if (held)
                {
                    if (_passengerHeld || _agent == null) return;
                    _passengerHeld = true;
                    _passengerUpdatePosition = _agent.updatePosition;
                    _passengerUpdateRotation = _agent.updateRotation;
                    UpdateGroundRoute(false);
                    BeginSeat("passenger");
                    if (_agent.enabled && _agent.isOnNavMesh && _agent.hasPath) _motion.ClearPath();
                    _agent.updatePosition = false;
                    _agent.updateRotation = false;
                    return;
                }
                if (!_passengerHeld) return;
                _passengerHeld = false;
                if (_soldier.isEnemyDead) return;
                if (_agent != null)
                {
                    _agent.updateRotation = _passengerUpdateRotation;
                    _agent.updatePosition = _passengerUpdatePosition;
                }
                EndSeat(_soldier.transform.position);
            }

            /// <summary>
            /// #874 S4: a door, entrance, objective or drive-handoff interaction holds the member at
            /// its point: a dwell. #1293 R4: the elevator link never pauses here; a member it has
            /// committed is its crossing, so no other interaction's pause applies to him.
            /// </summary>
            internal void PauseForDoorInteraction(float seconds)
            {
                if (_interactionCoordinator?.IsCommittedTraversal == true) return;
                Dwell(Mathf.Max(0.2f, seconds), "interaction");
            }

            internal void HoldForCombatAction(float seconds)
            {
                // This is an accepted combat body action, not the brain's speculative probe.
                // Combat moves already end the dwell before requesting their destination below.
                EndPatrolAlert();
                Dwell(seconds, "combat");
            }

            // #874 S4: a combat destination is a MOVE, so it ends the dwell and issues; a crossing
            // or a seat still answers it OwnedByMotor.
            internal void SetCombatDestination(Vector3 destination)
            {
                EndDwell();
                _motion.Request(new BlacksiteGoal(
                    destination,
                    validatePath: false,
                    groundRouteOutbound: null,
                    issuer: "combat"));
            }

            // #874 S2.1: the melee close-in is the one combat move that must track a walking
            // target. It is asked again as the target walks and relies on the combat profile's
            // 0.2 m re-issue threshold to follow it, so a target that has not moved is the goal the
            // agent already has. The dwell is ended explicitly: a close-in must never leave the agent
            // stopped from the previous attack hold with no path, which is what read as zero movement.
            internal void SetMeleeApproachDestination(Vector3 destination)
            {
                EndDwell();
                _combatDestinationAt = Time.time;
                _motion.Request(new BlacksiteGoal(
                    destination,
                    validatePath: false,
                    groundRouteOutbound: null,
                    issuer: "melee"));
            }

            internal void FaceCombatPoint(Vector3 point, float degreesPerSecond) => FaceCombatPointAtStep(point, degreesPerSecond, Time.deltaTime);
            internal void FaceCombatPointAtStep(Vector3 point, float degreesPerSecond, float deltaSeconds)
            {
                Vector3 flat = point - _soldier.transform.position;
                flat.y = 0f;
                if (flat.sqrMagnitude <= 0.01f)
                    return;
                _soldier.transform.rotation = Quaternion.RotateTowards(
                    _soldier.transform.rotation,
                    Quaternion.LookRotation(flat.normalized, Vector3.up),
                    Mathf.Max(30f, degreesPerSecond) * deltaSeconds);
            }

            internal float EffectiveStoppingDistance => _followingGroundRoute ? GroundRouteStoppingDistance
                : _worldInteractionStoppingDistanceOverridden ? _worldInteractionStoppingDistance
                : _combatApproach.HasValue ? _combatApproach.StopDistance
                : _approach.HasValue ? _approach.StopDistance
                : DefaultStoppingDistance;
            // Named for the diagnostics: a stop distance nobody can attribute is how the v11
            // melee stall read as an anonymous Repositioning.
            internal string StoppingDistanceOwner => _followingGroundRoute ? "ground-route"
                : _worldInteractionStoppingDistanceOverridden ? "world-interaction"
                : _combatApproach.HasValue ? "combat"
                : _approach.HasValue ? "order"
                : "default";
            internal string MovementOwner => IsCrossingShipLink ? "passage"
                : IsWorldClimbing ? "world-ladder"
                : IsJumping ? "jump"
                : _interactionCoordinator?.IsCommittedTraversal == true ? "interaction"
                // #874: the payload coupling walks this body on the cart's step. Above hold because
                // `hold` would hide which owner is moving the member.
                : _payloadCoupled ? "payload"
                : MotionHeld ? "hold"
                : "agent";
            internal float CombatDestinationAge => float.IsNegativeInfinity(_combatDestinationAt)
                ? -1f : Time.time - _combatDestinationAt;
            // #874 S4.2: the ordered arrival contract, and the only place its three values are
            // computed. StopDistance <= ArrivalFloor <= ArrivalTolerance is an invariant, enforced
            // by clamping upward inside BlacksiteArrivalPolicy.Resolve, so no authority above the
            // agent can be tighter than the agent was told to be. The 2026-09-09 run had the three
            // at 1.10 / 1.45 / 1.80 with no relation between them, and a goal that had to be
            // reached inside 1.45 m produced a member standing still that nothing supervised.
            //
            // The order's own tolerance is the duty tolerance in force. It is recorded here rather
            // than looked up because the board and the stall classifier must both read
            // one number, and only the navigator sees every owner that can change it.
            private float _orderArrivalTolerance;
            internal BlacksiteArrivalContract ArrivalContract
                => ResolveArrivalContract(EffectiveStoppingDistance, StoppingDistanceOwner, false);
            // #874 S8.5: the contract a groundRoute issue is made under. EffectiveStoppingDistance
            // puts the ground-route stop ahead of every other owner while the member is following
            // the corridor, so this is the same contract the member will be walking under one line
            // later; reading it before UpdateGroundRoute(following) latches keeps the follower's
            // advance radius stable on the tick the corridor is picked up.
            internal BlacksiteArrivalContract GroundRouteArrivalContract
                => ResolveArrivalContract(GroundRouteStoppingDistance, "ground-route", true);
            private BlacksiteArrivalContract ResolveArrivalContract(float stopDistance, string stopOwner, bool corridor)
            {
                // The innermost live reach contract: combat outranks the order, exactly as
                // EffectiveStoppingDistance already resolves them.
                bool hasReach = _combatApproach.HasValue || _approach.HasValue;
                var reach = _combatApproach.HasValue ? _combatApproach : _approach;
                var contract = BlacksiteArrivalPolicy.Resolve(
                    stopDistance,
                    stopOwner,
                    hasReach,
                    hasReach ? reach.StopDistance : 0f,
                    hasReach ? reach.ReachRadius : 0f,
                    BlacksiteArrivalPolicy.OrderToleranceFor(corridor, _orderArrivalTolerance));
#if DEBUG
                // #874 S8.5: the follower's advance radius is part of the same ordering.
                if (!contract.OrderedForFollowing)
                    ContractsBootstrap.Log?.LogError(
                        $"[MoonContracts.Blacksite] Soldier {_soldier.MemberIndex} arrival contract out of order: "
                        + $"stop={contract.StopDistance:0.00}, floor={contract.ArrivalFloor:0.00}, tolerance={contract.ArrivalTolerance:0.00}"
                        + $", advance={contract.AdvanceRadius:0.00}, owner={contract.StopOwner}.");
#endif
                return contract;
            }
            internal float StopDistance => EffectiveStoppingDistance;
            // A member parked outside an action's own gate is not "near enough to be waiting": the
            // floor follows the live reach contract instead of the movement default. Without a
            // profile this is the historical Max(0.9, stoppingDistance + 0.35).
            internal float ArrivalFloor => ArrivalContract.ArrivalFloor;
            internal float ArrivalTolerance => ArrivalContract.ArrivalTolerance;
            // The single arrival test. Horizontal, because a member standing on the floor below or
            // above its goal has not arrived at it no matter what the straight-line distance says.
            // #1219 C1: and path-true for the goal in force - a member whose path ends short of
            // that goal (Partial/None beyond the tolerance) is standing where the path stopped,
            // not at the goal. Any other point keeps the pure horizontal test.
            internal bool HasArrived(Vector3 point)
            {
                Vector3 offset = point - _soldier.transform.position;
                offset.y = 0f;
                if (!BlacksiteArrivalPolicy.WithinArrival(offset.magnitude, _soldier.transform.position.y - point.y, ArrivalTolerance,
                        GoalInForce(point), _orderArrivalTolerance, BlacksiteCombatPolicy.EntranceHandoffHeight)) return false;
                return !GoalInForce(point) || !PathStopsShort;
            }
            // HasArrived on the body position, tolerance and path verdict TickMaintenance read this tick.
            private bool ArrivedOn(Vector3 point, Vector3 body, float tolerance, bool pathStopsShort)
            {
                Vector3 offset = point - body;
                offset.y = 0f;
                if (!BlacksiteArrivalPolicy.WithinArrival(offset.magnitude, body.y - point.y, tolerance,
                        GoalInForce(point), _orderArrivalTolerance, BlacksiteCombatPolicy.EntranceHandoffHeight)) return false;
                return !GoalInForce(point) || !pathStopsShort;
            }
            private bool GoalInForce(Vector3 point)
                => _hasIssuedDestination && Vector3.Distance(point, _issuedGoal) <= BlacksiteArrivalPolicy.MinDestinationChange;
            // Recorded where the order is executed. An order without its own tolerance leaves this
            // zero and the contract falls back to the derived stop + 0.3 m.
            internal void RecordOrderArrivalTolerance(float tolerance)
                => _orderArrivalTolerance = tolerance > 0f ? tolerance : 0f;

            // #1015 S1: the owner ladder above stays here, where every reader of it lives; only the
            // agent write moves into the motion owner.
            private void ApplyNavigationSettings()
                => _motion.ApplyStop(EffectiveStoppingDistance);

            // Re-applied on every order execution; releasing is the order's own responsibility and
            // never depends on an interaction having ended first.
            internal void ApplyApproachProfile(BlacksiteApproachProfile profile)
            {
                _approach = profile;
                ApplyNavigationSettings();
            }

            internal void ReleaseApproachProfile()
            {
                if (!_approach.HasValue)
                    return;
                _approach = default;
                ApplyNavigationSettings();
            }

            /// <summary>
            /// B1: whether a combat action is currently holding this navigator's approach profile.
            /// Read-only, and the only reason it exists: a member walking a combat approach was
            /// given that goal for its stop distance, so the #950 A5 squad-goal substitution must
            /// not rewrite it into an elevator trip.
            /// </summary>
            internal bool HasCombatApproach => _combatApproach.HasValue;

            // #874 S2.1: applied and released by the combat action itself. Releasing the order
            // slot from a non-order commit leaves this one alone, which is the whole point.
            internal void ApplyCombatApproachProfile(BlacksiteApproachProfile profile)
            {
                _combatApproach = profile;
                ApplyNavigationSettings();
            }

            internal void ReleaseCombatApproachProfile()
            {
                if (!_combatApproach.HasValue)
                    return;
                _combatApproach = default;
                _combatDestinationAt = float.NegativeInfinity;
                ApplyNavigationSettings();
            }

            internal void OverrideWorldInteractionStoppingDistance(float distance)
            {
                _worldInteractionStoppingDistance = Mathf.Max(0.1f, distance);
                _worldInteractionStoppingDistanceOverridden = true;
                ApplyNavigationSettings();
            }


            internal void RestoreWorldInteractionNavigation()
            {
                // Releases only the interaction's own slot. An order approach profile that is still
                // live keeps its reach contract instead of being silently reset to the 1.1 m default.
                _worldInteractionStoppingDistanceOverridden = false;
                ApplyNavigationSettings();
            }

            internal void RequestImmediateDestinationRefresh()
            {
                _hasLastDestination = false;
                _motion.ForgetHeldRequest();
                _lastDestination = Vector3.zero;
            }

            /// <summary>
            /// #1233 S1 / #1293 R2: the paired-entrance and authored-teleport transfer. The agent is
            /// bound at <paramref name="target"/> first and the body moves only if it binds; otherwise
            /// the transfer is refused and nothing moves, so the member stays on the near side.
            /// </summary>
            internal bool TryTransferTo(Vector3 target)
            {
                try { if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh || !_motion.WarpResetAndMove(target)) return false; }
                catch (Exception e) { ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Soldier transfer skipped: {e.Message}"); return false; }
                _soldier.transform.position = target;
                _soldier.SyncPositionToClients();
                _soldier.NotifyTeleported();
                return true;
            }

            internal void SnapToNavMesh(Vector3 origin, float maxDistance)
            {
                if (_agent == null || !_agent.enabled)
                    return;

                int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
                if (BlacksiteLandingGeometry.Ground(origin, .6f, mask, null, out var floor)
                    && BlacksiteShipNavigation.IsShipFloor(floor.collider))
                {
                    BlacksiteShipNavigation.TryBindDeploymentAgent(_agent, origin, mask);
                    _hasLastDestination = false; _motion.ForgetHeldRequest(); return;
                }
                Vector3 target = ResolveNavMeshPosition(origin, maxDistance);
                // #940 S11 U1: a warp of a pushing member is a deliberate, logged release. The cart
                // is a soft leash with no joint, so a teleport out of it used to break the link on
                // the next FixedUpdate with nothing in the log naming a cause.
                Y4NGZCompany.Contracts.Payload.PayloadCartController.ReleaseForWarpServer(_soldier);
                try
                {
                    _agent.Warp(target);
                }
                catch
                {
                    _soldier.transform.position = target;
                }
                _hasLastDestination = false;
                _motion.ForgetHeldRequest();
            }

            /// <summary>
            /// #1293 R2 (rule 3): the per-tick off-mesh guard. A bound agent clears the stranded fact;
            /// a body off the navmesh is bound where it stands or is stranded (<see cref="BindInPlace"/>),
            /// never moved to find navmesh. A crossing or a seat defers it; a boarded body is the ship's.
            /// </summary>
            internal void EnsureOnNavMesh()
            {
                if (_agent == null || !_agent.enabled) return;
                if (_agent.isOnNavMesh) { NoteStranded(false, "bound", _soldier.transform.position); return; }
                if (_soldier._boardedShip) return;
                BindInPlace("agent off navmesh");
            }

            // #956 S12A U3: the one writer of agent.speed, and now of agent.autoBraking with it.
            //
            // autoBraking used to be set true once in ConfigureAgent and never reconsidered. It is
            // the switch that makes an agent decelerate so it lands exactly on its destination, and
            // the Unity manual's own advice is to turn it off for an actor following a series of
            // waypoints. The March run was exactly that: a median goal 2.04 m away against a 1.10 m
            // stop distance leaves 0.94 m of travel, and with autoBraking on the agent spent every
            // one of those metres inside its acceleration or its braking ramp and never reached
            // cruise. Achieved speed was 0.14 of commanded; agentVelocity was below 0.2 m/s on
            // 74.7 % of samples (artifacts/s12a-u3-evidence/investigation.md).
            //
            // So braking is now owned by the goal rather than by the agent's lifetime: a transit
            // goal is passed through, an action's standing point is landed on. The stop distance is
            // untouched - that contract belongs to BlacksiteArrivalPolicy and to #955.
            internal void ApplyAgentMoveSpeed() => _motion.ApplySpeed();

            /// <summary>
            /// #956 S12A U3: a goal with no reach contract behind it. This is the same condition
            /// StoppingDistanceOwner reports as "default", written once so the braking rule, the
            /// approach lerp and the printed stop owner can never disagree about what a transit
            /// goal is. 86 % of the March run's samples were one.
            ///
            /// #1127: a ground-route waypoint with another point beyond it is still a transit
            /// goal — the agent must not brake for an intermediate corridor point. The last
            /// waypoint keeps autoBraking so the route end arrives to its contract.
            /// </summary>
            internal bool IsTransitGoal => (!_followingGroundRoute || _groundRouteHasFurtherWaypoint)
                && !_worldInteractionStoppingDistanceOverridden
                && !_combatApproach.HasValue
                && !_approach.HasValue;

            internal void ApplySpawnMovement()
            {
                if (_agent == null) return;
                ApplyAgentMoveSpeed();
                _motion.ApplyAcceleration(_soldier._movementAcceleration);
                ApplyNavigationSettings();
            }

            internal void SetAutomaticRotation(bool enabled)
            {
                if (_agent != null) _agent.updateRotation = enabled;
            }

            internal void ClearPath() => _motion.ClearPath();

            internal void SetRotation(Quaternion rotation) => _soldier.transform.rotation = rotation;

            // Interaction tasks retain their own phase/state; all actor movement passes here.
            // #874 S4: the ride's hand-back - the crossing ends here. Refused while a seat (a
            // monster capture) has the body: telling the elevator the restore succeeded while
            // updatePosition is still false is how a ride loses its body.
            internal bool TryRestoreAtCurrentPosition(float radius, float maxHeight, bool updatePosition, bool preservePosition)
            {
                if (_agent == null || !_agent.enabled || _bodyWriter == BlacksiteBodyWriter.Seated) return false;
                Vector3 current = _soldier.transform.position;
                if (!NavMesh.SamplePosition(current, out var nav, radius, NavMesh.AllAreas)
                    || Mathf.Abs(nav.position.y - current.y) > maxHeight || !_agent.Warp(current)) return false;
                if (preservePosition) _soldier.transform.position = current;
                _agent.updatePosition = updatePosition;
                EndCrossing(current);
                _soldier.SyncPositionToClients();
                return true;
            }

            /// <summary>
            /// #874 S4: the elevator ride takes the body as a crossing and returns the agent's
            /// updatePosition to restore at its end. Refused, touching nothing, while another
            /// crossing (the payload coupling) or a seat has the body; the caller checks
            /// <see cref="BodyWriter"/> first.
            /// </summary>
            internal bool BeginManualMovement()
            {
                bool updatePosition = _agent.updatePosition;
                if (!BeginCrossing("ride")) return updatePosition;
                UpdateGroundRoute(false);
                // A deliberate path wipe: the body is about to be moved by hand for an unbounded
                // number of frames and the route it was walking is not resumable afterwards.
                // #874 N5: wiped whether or not a path is showing yet. A SetDestination issued on
                // the same frame reads hasPath=false AND pathPending=false until the engine's
                // navigation update answers it at the end of the frame; the old hasPath gate
                // skipped the wipe, and that answer then gave the carried agent a route to the
                // boarding-floor goal one frame into the ride (N5 ride fixture). ResetPath is
                // what cancels the queued request, and it is a no-op on an agent with none.
                _motion.ClearPath();
                _agent.updatePosition = false;
                return updatePosition;
            }

            // #874 N5: the cabin moves the body and the agent goes with it (see
            // BlacksiteMotionOwner.FollowCarriedBody). The transform alone left the Vow run's agent
            // on the boarding floor for the whole shaft.
            internal void CarryWithPlatform(Vector3 delta, float bindRadius)
            {
                _soldier.transform.position += delta;
                _motion.FollowCarriedBody(bindRadius);
            }

            /// <summary>#874 N5: the same follow, after a ride-owned hand step moved the body.</summary>
            internal void FollowCarriedBody(float bindRadius) => _motion.FollowCarriedBody(bindRadius);

            /// <summary>
            /// #874 N5: hand a ride-held body back on a landing rather than where it stands. For a
            /// member the car left behind in its doorway: the cabin floor he was straddling has
            /// gone, so there is nothing under him to restore onto and nothing to hand-step across.
            /// Refused, with the body left where it was, when the landing does not bind, and always
            /// while a monster has the body: a captured body is where the monster puts it.
            /// </summary>
            internal bool TryRestoreOnLanding(Vector3 landing, bool updatePosition)
            {
                if (_soldier._monsterCaptured) return false;
                Vector3 left = _soldier.transform.position;
                _soldier.transform.position = landing;
                if (TryRestoreAtCurrentPosition(BlacksitePhysicalStep.BodyRadius, BlacksitePhysicalStep.StepHeight,
                        updatePosition, preservePosition: true))
                    return true;
                _soldier.transform.position = left;
                return false;
            }

            /// <summary>
            /// #874 S1: the ride's hand-back where nothing binds under the body and no parked landing
            /// takes it. The position flag the ride suspended is put back - it is always the ride's
            /// to return, as ReleaseJump returns its own - and the crossing ends. The transform is
            /// rewritten where it stood; a body off the mesh is bound there or stranded (EnsureOnNavMesh).
            /// </summary>
            internal void ReleaseManualMovementInPlace(bool updatePosition)
            {
                if (_agent == null || !_agent.enabled) return;
                Vector3 body = _soldier.transform.position;
                _agent.updatePosition = updatePosition;
                _soldier.transform.position = body;
                EndCrossing(body);
            }

            // #874 S5.3: the failure that actually refused the walking step reaches the caller.
            // It used to be discarded here, which is why the elevator's event=stepRefused line had
            // to replay the identical step through a second, diagnostics-only probe to name a
            // blocker. The step is now attempted once and reported once. The caller's ignore is the
            // elevator's car scope composed with the squad-body ignore every hand-driven step uses.
            internal bool WalkPhysicalThreshold(Vector3 target, Predicate<Collider> ignore, out BlacksiteStepFailure failure)
            {
                Vector3 before = _soldier.transform.position;
                if (!BlacksitePhysicalStep.TryStep(_soldier.transform, target, BlacksitePhysicalStep.WalkSpeed * Time.deltaTime,
                        PhysicalMask, ignore, out var next, out failure))
                    return false;
                _soldier.transform.position = next;
                _soldier.SyncPositionToClients();
                Vector3 forward = Vector3.ProjectOnPlane(next - before, Vector3.up);
                if (forward.sqrMagnitude > .00001f)
                    _soldier.transform.rotation = Quaternion.RotateTowards(_soldier.transform.rotation,
                        Quaternion.LookRotation(forward), 240f * Time.deltaTime);
                // A physical step is taken BY a crossing motor on purpose; it never ends one.
                return true;
            }

            internal void ConfigureAgent()
            {
                if (_agent != null)
                {
                    _agent.angularSpeed = 270f;
                    // #874: 18 m/s^2 reaches the 4.80 m/s urgent role speed in 0.267 s over 0.64 m.
                    // #956 S12A U3 re-derived it and left it alone: the brief's own floor for a
                    // 3.6 m/s cruise inside 0.5 s is 7.2 m/s^2 and the vanilla soldier-class agents
                    // use 8, so 18 is already 2.5x the floor and the ramp was never the throttle.
                    _agent.acceleration = _soldier._movementAcceleration;
                    _agent.radius = 0.45f;
                    _agent.height = 2.65f;
                    _agent.updateRotation = true;
                    // #956 S12A U3: last, because it owns autoBraking as well as the speed.
                    ApplyAgentMoveSpeed();
                    // #874 S4 (Parity/HallwayCorner/squad4): the quality the vanilla reference agent
                    // uses, Unity's default. With MedQuality the rear member of a bunched squad read
                    // 27 % slower than its reference at 120 fps; agent configuration, not a judge.
                    _agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
                    // #874 S2 (rule 2): off unconditionally - Unity has no pre-traversal hook, so the
                    // elevator and ship links could not be backed off if Unity moved the body first.
                    // Every other link is crossed like vanilla crosses it, by TickJump (Jump.cs).
                    _agent.autoTraverseOffMeshLink = false;
                    // #874 S4: written once per bind and never again: 35 + member index, so two
                    // members always resolve a contact the same way round.
                    _agent.avoidancePriority = Mathf.Clamp(35 + _soldier._index, 0, 99);
                    ApplyNavigationSettings();
                    // #1305 G1 (R15): PathfindingLib's non-smart link area is the elevator's, never this agent's.
                    _agent.areaMask &= BlacksiteReachability.SoldierAreaMask;
                    LogAgentAreaMaskOnce();
                }

            }

            /// <summary>
            /// The paired-entrance transfer (TryTransferTo, or the coupled carry through the portal)
            /// and the authored internal teleport. The climb and the jump are unwound, and the
            /// interaction's own dwell ends with the teleport that ends the interaction.
            /// </summary>
            internal void NotifyTeleported()
            {
                ReleaseWorldLadder();
                ReleaseJump("teleport");
                UpdateGroundRoute(false);
                _shipQueueSince = 0f;
                // #1219 C2: a board request made before the teleport never begins from the new place.
                _directBoardShip = null;
                // #874: the coupled pair's record is the cart's (#1233 S1: TryCrossPortalServer
                // resets it on a crossing).
                EndDwell();
            }

            /// <summary>
            /// #874: the DECISION half of a completed world transfer, and the reason it is separate
            /// from <see cref="NotifyTeleported"/> is ordering. NotifyTeleported settles the body
            /// and runs inside the warp; this drops the goals decided in the world left behind.
            /// </summary>
            internal void NoteWorldChanged()
            {
                _motion.DiscardGoals();
                _motion.AdvanceRouteGeneration("world");
                ForgetIssuedDestination();
            }
        }
    }
}
