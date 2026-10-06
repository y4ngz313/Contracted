// #1018 S4: the planner's successor point sources.
//
// Every point-producing rule an idle episode can end in, moved off the duty board as a COPY: the
// coverage slot (…Duties.cs:792-818), the frontier node and the routeless doorway (…Raid.cs:914-982
// and …Duties.cs:610-654), the regroup point (the squad lead; #874 S4 deleted the anchor input), and the
// posts - the guard ring (…Jobs.cs:30-116, since replaced by the #874 Q6 patrol loop). #1271 S2 deleted
// the witness cordon post with the cordon, #1305 G3 the lone-arrival landing post.
//
// The originals stay in place and untouched in this block, so each rule exists twice until block 6
// deletes the board's copy; that is expected and is what keeps blocks 1 and 2 free of edits to any
// existing production file. Nothing here changes a rule while moving it: every candidate count,
// every radius, every clock and every rejection test is the one that was already there. The two
// deliberate exceptions are Mono-hazard fixes design note G10 calls for in passing and are marked
// where they happen.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed partial class BlacksiteObjectiveManifestController
    {
        internal sealed partial class BlacksiteSquadPlanner
        {
            // ---------------------------------------------------------------------------------
            // OWNERSHIP (fix batch 1, B2). The planner owns EXACTLY two pieces of per-member state:
            // _duties (typed on its own nested Duty) and _sequence,
            // plus the reused scratch buffers and the diagnostics gates below. Every other
            // per-member dictionary these rules read or write is the MANIFEST's and is reached
            // through _manifest., because the manifest's own producers are still live and still
            // writing them:
            //
            //   _planningBlocked    SupportPoint writes it on refusal (…Jobs.cs:1335) and replays
            //                       the cached cause (:1261); a planner-local copy read None and the
            //                       whole #872 D8 / C.5 goal-kind case never fired.
            //   _supportPoints      Occupied's reservation test; a local copy saw an empty set, so
            //   _supportCenters     two members could be handed the same slot. With
            //   _supportUntil       _supportMisses, _supportCursor and _blockedSupport.
            //   _operatorLane       InOperatorLane's reserved lanes, same failure.
            //   _patrolPoints       the patrol waypoint (#874 Q6, TickPatrol). With _guardDuty
            //   _guardDuty          (#1219 E1) and _patrolSource.
            //   _searchRoutes       the 2 s re-pick, the 30 s goal life and the 90 s node stamps.
            //   _visited            With _insideSearchNodes and _outsideSearchNodes.
            //   _idleDoorwayVisited the 90 s doorway stamp.
            //   _focusLogged        cleared on release, as ForgetDuty:963 does.
            //   SpendCandidatePath    the ONE 24-per-frame / 4-per-member route budget. A second
            //                         budget doubled the squad's path queries per frame; the
            //                         manifest's is called, never copied.
            //
            // Whether any of these later move onto the planner is the deletion block's decision,
            // not this one's.
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #874 P-S1: the goal substitution that changes where a member will END, applied to
            /// the ORDER where the duty's goal is chosen, so the navigator walks, the board measures
            /// and the log prints one point. S8.8: the guard leash, for the Guard duty only
            /// (<see cref="BlacksiteDeploymentPolicy.LeashesDuty"/>). The navigator used to clamp every
            /// destination an outside ship guard was given, a Board order included: on 2026-09-23
            /// round 3 member 5 was clamped onto the ring he was standing on, the navigator dropped
            /// every request as identical and the duty board invalidated the same Rally 103 times.
            /// </summary>
            /// <remarks>
            /// #874 Q4 (2026-09-24): the returning ground ship's landing-footprint exit is DELETED
            /// with the recall it served (and with ClearReturnFootprint): a ground ship stays landed
            /// for the whole raid, so no hull ever comes back down onto a member's goal.
            /// #874 Q6 (F9): the leash is a ROUTE test (<see cref="BlacksiteDeploymentPolicy.LeashedGoal"/>):
            /// a Guard goal whose navmesh route from the rendezvous leaves the leash is refused for
            /// the member's own patrol waypoint, never snapped onto a ring. A patrol leg faces
            /// OUTWARD - away from the ship it guards. #1219 E2: every issued order passes through
            /// here, so the Guard duty declares the order's pace here: the visit's leg pace
            /// (<see cref="BlacksitePatrolPolicy.OrderPace"/>), Normal under a scan, which faces the alert.
            /// </remarks>
            private BlacksiteMemberOrder ShapeDutyOrder(BlacksiteSoldierAI soldier, BlacksiteJobKind kind,
                BlacksiteMemberOrder order)
            {
                var ship = _manifest.Ship;
                bool leashed = ship != null && soldier.IsShipGuard && soldier.isOutside
                               && BlacksiteDeploymentPolicy.LeashesDuty(kind);
                // #874 Q6: the waypoint is asked of TryPatrolWaypoint, which
                // refuses a dead one, never read raw - so neither the leash fallback nor an order
                // that still carries a dead waypoint (a held beat, the Unreachable re-target) can
                // hand it back.
                Vector3 waypoint = default;
                bool hasWaypoint = leashed && TryPatrolWaypoint(soldier, out waypoint);
                Vector3 shaped = order.Destination;
                if (leashed && NearDeadPatrolPoint(shaped))
                    shaped = hasWaypoint ? waypoint : GuardGroundPoint(soldier);
                else if (hasWaypoint && Vector3.Distance(shaped, waypoint) > BlacksiteArrivalPolicy.MinDestinationChange)
                    shaped = BlacksiteDeploymentPolicy.LeashedGoal(kind, true,
                        LeashRouteMetres(ship.RendezvousPosition, shaped), shaped, waypoint);
                bool patrolLeg = hasWaypoint && Vector3.Distance(shaped, waypoint) <= BlacksiteArrivalPolicy.MinDestinationChange;
                if (!patrolLeg) return (shaped - order.Destination).sqrMagnitude <= .0001f ? order
                    : new BlacksiteMemberOrder(order.SquadOrder, shaped, order.PreferredFacing,
                        order.SeparationRadius, order.ArrivalTolerance, order.IsInteractionOperator,
                        order.IsPlayerResponse, order.TargetOutside, order.Approach, order.HasDutyKind, order.DutyKind, order.Pace);
                var guard = _manifest._guardDuty[soldier.NetworkObjectId];
                Vector3 facing = PatrolOutward(ship.RendezvousPosition, shaped, soldier.transform.forward);
                // #1219 E1: a scan faces the alert. The body is stood still by the navigator's own
                // dwell (TickPatrol opens it for the scan), never by a hold order at the guard's
                // feet, which the issue path would read as a refusal.
                if (guard.Phase == BlacksiteGuardPhase.Alert && soldier.TryGetWorld(out var squadWorld)
                    && squadWorld.TryGetAlert(soldier.PerceptionSlot, out var alert))
                    facing = alert.Position;
                return new BlacksiteMemberOrder(order.SquadOrder, shaped, facing,
                    order.SeparationRadius, order.ArrivalTolerance, order.IsInteractionOperator,
                    order.IsPlayerResponse, order.TargetOutside, order.Approach, order.HasDutyKind, order.DutyKind,
                    BlacksitePatrolPolicy.OrderPace(in guard));
            }

            /// <summary>
            /// #874 Q6: the navmesh route length from the projected rendezvous to
            /// <paramref name="goal"/>, or -1 when there is no complete route. A goal farther than
            /// the leash in a straight line cannot have a shorter route, so it answers without a query.
            /// </summary>
            private float LeashRouteMetres(Vector3 rendezvous, Vector3 goal)
            {
                if (Vector3.Distance(rendezvous, goal) > BlacksitePatrolPolicy.LeashMetres) return -1f;
                if (!NavMesh.SamplePosition(rendezvous, out var from, BlacksiteArrivalPolicy.AgentRadius * 2f, NavMesh.AllAreas)
                    || !BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(true), from.position, goal, _patrolPath).Reachable) return -1f;
                int count = PatrolPathCorners();
                float route = 0f;
                for (int c = 1; c < count; c++) route += Vector3.Distance(_patrolCorners[c - 1], _patrolCorners[c]);
                return route;
            }

            // #874 Q6: the corners of _patrolPath, read into a reused buffer. NavMeshPath.corners
            // allocates a new array on every read; the loop proof reads up to 36 paths in one frame
            // and every shaped Guard order reads one. The buffer grows once when a path overflows it.
            private Vector3[] _patrolCorners = new Vector3[16];

            private int PatrolPathCorners()
            {
                int count = _patrolPath.GetCornersNonAlloc(_patrolCorners);
                while (count >= _patrolCorners.Length)
                {
                    _patrolCorners = new Vector3[_patrolCorners.Length * 2];
                    count = _patrolPath.GetCornersNonAlloc(_patrolCorners);
                }
                return count;
            }

            // ---------------------------------------------------------------------------------
            // ProveSuccessors - the 4 s idle review, which no longer moves a body.
            // ---------------------------------------------------------------------------------

            // The sweep's target list, rebuilt on each four-second idle review and handed to the
            // soldier, which keeps its own copy. Reused so a squad standing still allocates nothing.
            private readonly List<Vector3> _idleSweepPoints = new List<Vector3>();
            private readonly List<byte> _idleSweepKinds = new List<byte>();
            // The idle review's own clock, and (#1293 R3) the reviewed member's deadline.
            private const float IdleReviewSeconds = 4f;

            /// <summary>
            /// #1018 S4 (design note C.3, C.4): the 4 s idle review, with its arming test, its
            /// <see cref="BlacksiteIdlePolicy"/> choice, its <c>[UAT][Autonomy]</c> line and its
            /// facing-sweep target list all unchanged - and its OUTPUT changed completely.
            ///
            /// The board's version ended in <c>duty.Order = MakeOrder(…, step, facing)</c>, which is
            /// writer 3 of design note A.2 and the aimless step #952 measured. This one proves up to
            /// four successor points, puts them on the assignment, and moves nobody: the idle Action
            /// picks exactly one of them on its own phase clock and walks to it under a real expiry.
            /// That is why the 4 s re-arm no longer produces a new point (design note C.7).
            /// </summary>
            private void ProveSuccessors(BlacksiteSoldierAI soldier, Duty duty, float now)
            {
                if (!duty.Assignment.IsValid) return;
                // #874 Q4 (F4): a member that is going home reviews nothing. On 23 September round 1
                // the review kept proposing frontier search goals 130 m away (search outside=True,
                // objectiveKnown=False; idle choice=Frontier, 16531-24397) for a squad whose Board
                // order already owned its body - arbitration kept it still, so the cost was CPU and
                // log, but a successor set is evidence about a raid that is over. The set is cleared
                // so the idle Action has nothing stale to take.
                if (_manifest.Returning || soldier.IsRaidWithdrawing)
                {
                    EndPatrolDuty(soldier);
                    duty.IdleSince = now; duty.ObservedPosition = soldier.transform.position;
                    Publish(soldier, duty, default(BlacksiteSuccessorSet));
                    return;
                }
                // #874 Q6 (F9): an outside ship guard on its Guard duty patrols. The patrol IS its
                // idle - the dwell, the outward look and the next leg - so it never reaches the
                // 4 s review below, whose arming test would hold the look-around back for four
                // seconds of stillness it is meant to fill.
                if (duty.Intent.Kind == BlacksiteJobKind.Guard && soldier.IsShipGuard && soldier.isOutside)
                {
                    TickPatrol(soldier, duty, now);
                    return;
                }
                EndPatrolDuty(soldier);
                bool interrupted = soldier.IsObjectiveCombatActive || soldier.Navigator.HasCommittedMotion
                    || soldier.ReplicatedStunned || soldier.SelectedBehaviour == BlacksiteBehaviour.Evade;
                bool discretionary = Discretionary(duty.Intent.Kind);
                // #955 S12A U2 / #957 S12A U4: the arming test, unchanged. A member that has moved
                // more than 0.6 m since the last review, is interrupted, is holding a world
                // interaction, is aboard the ship (#1233 S4, the live answer), or is not
                // discretionary, is not idle.
                if (interrupted || !discretionary || soldier.HasActiveWorldInteraction
                    || soldier.IsAboardShip
                    || Vector3.Distance(duty.ObservedPosition, soldier.transform.position) > .6f)
                {
                    duty.IdleSince = now; duty.ObservedPosition = soldier.transform.position;
                    // #1018 S4 (SF6): the set is KEPT (walking to a successor moves 0.6 m); Issue clears it.
                    Publish(soldier, duty, duty.Assignment.Successors);
                    // #1293 R3: only a discretionary member is ever reviewed.
                    if (discretionary) duty.NextPlan = Mathf.Min(duty.NextPlan, now + IdleReviewSeconds);
                    return;
                }
                if (now - duty.IdleSince < IdleReviewSeconds)
                {
                    Publish(soldier, duty, duty.Assignment.Successors);
                    duty.NextPlan = Mathf.Min(duty.NextPlan, duty.IdleSince + IdleReviewSeconds);
                    return;
                }

                var choice = ChooseIdleGoal(soldier, duty, out var step, out var facing, out string benefit);
                duty.IdleGoal = choice; duty.IdleSince = now;
                // Deliberate observation is bounded: reviewed again in four seconds, its next deadline.
                duty.NextPlan = Mathf.Min(duty.NextPlan, now + IdleReviewSeconds);

                bool routed = _manifest.RoutePlanner != null && _manifest.RoutePlanner.CommittedRoute.Count > 0;
                var set = default(BlacksiteSuccessorSet);
                // #1018 S4 (fix batch 1, SF12): the separation term is the existing 14 m rule, not
                // "a regroup point happened to be provable".
                bool separated = !_manifest.HasOwnJob(soldier) && SeparatedFromLead(soldier); // #1305 G5 (D17): the escort's lead is the holder

                // 1. The cover step. TryCoveragePoint has already proved it: off the lane, in sight
                //    of it, occupiable, separately reserved and reachable by a complete route.
                if (choice == BlacksiteIdleGoal.Lane || choice == BlacksiteIdleGoal.Coverage)
                    // S6: Lane and Coverage are the same proved point; only Lane means the member
                    // is standing ON the operator's approach and clearing it is a correctness move,
                    // which is the ranking's first precondition. The distinction is recorded here
                    // because this is the only place that knows it.
                    set = set.WithCover(step, facing, BlacksiteIdleActionPolicy.CoverSeconds,
                        choice == BlacksiteIdleGoal.Lane);

                // 2. The frontier tile, PROVED only under the condition the board's own rule ran
                //    under (fix batch 1, B3): a choice that means "I have nothing here" - Frontier
                //    or Blocked - AND no committed route, which is exactly
                //    BlacksiteIdlePolicy.IssuesRoutelessDestination. Calling it unconditionally on
                //    every review made a ROUTED squad prove (and, before the stamp was split out,
                //    spend for 90 s) every doorway of the room it was standing in, so when the route
                //    died there was no frontier left to prove - the #990 S16 D3 regression exactly.
                //
                //    Proving is now also separate from STAMPING. The 90 s doorway stamp belongs to
                //    an ISSUE, not to a prove, so the key travels on the plan state and the idle
                //    Action spends it through NoteFrontierTaken when it actually issues the
                //    FrontierTile successor.
                string intentSource = BlacksiteDiagnosticFormat.None;
                long frontierDoorway = 0L;
                if (BlacksiteIdlePolicy.IssuesRoutelessDestination(choice, routed)
                    && TryIdleIntentDestination(soldier, choice, step, out Vector3 frontier,
                        out intentSource, out frontierDoorway))
                    set = set.WithFrontier(frontier, frontier, BlacksiteIdleActionPolicy.FrontierSeconds);
                duty.FrontierDoorway = frontierDoorway;

                // 3. The regroup anchor - only for a member that IS separated (SF12).
                if (separated && TryRegroupPoint(soldier, now, out Vector3 regroup, out Vector3 regroupFacing))
                    set = set.WithRegroup(regroup, regroupFacing, BlacksiteIdleActionPolicy.RegroupSeconds);

                // …Duties.cs:302 reported NoReachableCandidate on a Blocked choice outright. The
                // `!set.Any` term is a deliberate NARROWING and is flagged as one: a Blocked choice
                // means "no covering position and no frontier", which was the whole of what the
                // board could offer, but the planner also proves a regroup and a post. A member
                // standing on a proved guard post is not out of reachable candidates, and reporting
                // it as such would make the observation half measure its leg as remaining=Infinity -
                // the very reading design note C.5 exists to remove. With no successor at all the
                // report is unchanged.
                if (choice == BlacksiteIdleGoal.Blocked && !set.Any)
                    duty.Blocked = BlacksiteBlockedCause.NoReachableCandidate;

                // #1018 S4 (fix batch 2, B1): the review DOES NOT ARM THE SWEEP. Design note C.3 is
                // explicit that the idle Action arms it, on entering Hold and through a GuardPost
                // successor, and only there - because starting a sweep stops the member where it
                // stands. Arming it from the 4 s review pinned a guard whose successor was its own
                // post, and wiped the path of any idle member that had a live order, once every
                // four seconds.
                //
                // The review still builds the TARGET LIST - that is the planner's, and it is what
                // BeginIdleSweep does - and still reports the sweep's own place when the member is
                // sweeping, which it will be whenever the action has a hold or a post live.
                string sweep = BlacksiteDiagnosticFormat.None;
                if (soldier.IsFacingSweeping)
                {
                    // #1293 R2: the sweep is the idle action's dwell and ends with its phase; a
                    // stalled member is the progress ledger's miss, so the sweep carries no
                    // staleness label of its own (the old sweep:stale clock is gone).
                    benefit = "sweep:" + soldier.FacingSweepTargetToken;
                    sweep = soldier.FacingSweepIndex + "/" + soldier.FacingSweepCount;
                }

                Publish(soldier, duty, set);

                // #990 / #1018 S4 (design note E.2): every existing key keeps its name, its position
                // and its closed value set; phase=, until=, successor= and rejected= are appended at
                // the END. phase and rejected belong to the idle Action's own state and are None
                // until block 4 wires its body; successor and until are what this review PROVED, so
                // the log says what the member will do next and when it stops doing it.
                // #1018 S4 (fix batch 2, S6): the preview and the body rank with the SAME inputs,
                // because the one that could differ is now a fact on the record. onOperatorLane is
                // the preference's own Lane arm - the one idle choice that was always a correctness
                // move - and the planner is the only thing that knows it, so it travels on the
                // successor set as CoverIsLane rather than being re-derived on the soldier side from
                // "a cover point exists", which is a different question.
                var ranked = BlacksiteIdleActionPolicy.Rank(
                    set.CoverIsLane,
                    separated, routed, WaitingForResource(soldier), set,
                    BlacksiteIdleActionPolicy.NoneRejected);
                // phase=, until=, successor= and rejected= are the idle ACTION's own state, read off
                // the member now that its body is wired (block 5 i). until= is an absolute Time.time
                // so "no idle hold without an expiry" is checkable straight off the log; before the
                // action has entered a phase it is the expiry the ranking above would give it, so the
                // key is never empty.
                // #1018 S4 (fix batch 3, item 7): the whole line, the float formats and
                // DescribeIdleRejected's own string join are behind the switch. BlacksiteDiagnostics
                // .Log checks Enabled too, but it checks it AFTER the caller has built the message,
                // which on this path is several formats and a join - the #944 S11 U6 review P12
                // rule that both the key and the message are built after the check, not before.
                if (!BlacksiteDiagnostics.Enabled) return;
                float until = soldier.IdleExpiresAt > 0f
                    ? soldier.IdleExpiresAt
                    : now + BlacksiteIdleActionPolicy.SecondsFor(ranked, set);
                var successor = soldier.IdlePhase == BlacksiteIdlePhase.None
                    ? ranked : soldier.IdleSuccessor;
                BlacksiteDiagnostics.Log("Autonomy",
                    $"member={soldier.MemberIndex}, idle choice={choice}, goal={(set.Any ? BlacksiteDiagnosticFormat.V(step) : BlacksiteDiagnosticFormat.V(soldier.transform.position))}"
                    + $", benefit={benefit}, blockedChoices={duty.IdleBlocked}, sweep={sweep}"
                    + $", route={(routed ? "committed" : "none")}, intent={intentSource}"
                    + $", phase={soldier.IdlePhase}, until={until:0.00}, successor={successor}"
                    + $", rejected={soldier.IdleRejectedToken}.");
            }

            /// <summary>
            /// The successors the planner proved this tick. They are not part of the work, so they
            /// do not move <c>Sequence</c>: a successor is not an assignment (design note F.5).
            /// #1293 R3: a set offering different work is the member's Order decision.
            /// </summary>
            private void Publish(BlacksiteSoldierAI soldier, Duty duty, in BlacksiteSuccessorSet set)
            {
                bool differs = SuccessorsDiffer(duty.Assignment.Successors, set);
                var updated = duty.Assignment.WithSuccessors(set);
                duty.Assignment = updated;
                _assignments[soldier.NetworkObjectId] = updated;
                if (differs) soldier.RequestDecision(BlacksiteDecisionCause.Order);
            }

            /// <summary>#1293 R3: different work: a kind or the lane case changed, or a point or facing
            /// moved past the minimum destination change (hold lengths are not compared).</summary>
            private static bool SuccessorsDiffer(in BlacksiteSuccessorSet a, in BlacksiteSuccessorSet b)
                => a.HasCover != b.HasCover || a.HasFrontier != b.HasFrontier || a.HasRegroup != b.HasRegroup
                   || a.HasPost != b.HasPost || a.CoverIsLane != b.CoverIsLane
                   || (a.HasCover && (Moved(a.Cover, b.Cover) || Moved(a.CoverFacing, b.CoverFacing)))
                   || (a.HasFrontier && (Moved(a.Frontier, b.Frontier) || Moved(a.FrontierFacing, b.FrontierFacing)))
                   || (a.HasRegroup && (Moved(a.Regroup, b.Regroup) || Moved(a.RegroupFacing, b.RegroupFacing)))
                   || (a.HasPost && (Moved(a.Post, b.Post) || Moved(a.PostFacing, b.PostFacing)));

            private static bool Moved(Vector3 a, Vector3 b)
                => Vector3.Distance(a, b) > BlacksiteArrivalPolicy.MinDestinationChange;

            /// <summary>
            /// #1018 S4 (design note G3): whether this member is queued for anything at all, taken
            /// on the PLANNER's tick and written into the assignment, because
            /// <c>BlacksiteResourceBroker.TryGetWaitKey</c> scans every lane and asks each one
            /// <c>IsWaiting(member)</c> - not a per-frame read, and deliberately not on
            /// <c>BlacksiteSense</c>.
            /// </summary>
            private bool WaitingForResource(BlacksiteSoldierAI soldier)
                => _manifest._mission != null && _manifest._mission.IsServer
                   && _manifest._mission.Blackboard.Broker.IsWaitingForAnything(soldier.NetworkObjectId);

            // ---------------------------------------------------------------------------------
            // The idle preference (…Duties.cs:500-558) verbatim. BlacksiteIdlePolicy survives as
            // the coverage/frontier PREFERENCE and stops being the thing that moves the body (G9).
            // ---------------------------------------------------------------------------------

            private BlacksiteIdleGoal ChooseIdleGoal(BlacksiteSoldierAI soldier, Duty duty,
                out Vector3 step, out Vector3 facing, out string benefit)
            {
                step = soldier.transform.position; facing = duty.Order.PreferredFacing; benefit = "none";
                ulong id = soldier.NetworkObjectId;
                if (soldier.IsShipGuard)
                {
                    // #874 Q6: a ship guard's idle choice is its duty. Outside on Guard duty it
                    // patrols (TickPatrol, which never reaches this review); anywhere else - inside
                    // on its way back, or under another duty - it observes rather than taking a
                    // raider's cover or frontier step.
                    benefit = "ship guard holds its duty";
                    return BlacksiteIdlePolicy.Choose(true, false, false, false, false, false);
                }
                var op = ActiveOperator(soldier);
                OperatorLane(op, out var laneFrom, out var laneTo);
                bool onLane = op != null
                    && LaneDistance(soldier.transform.position, laneFrom, laneTo) < BlacksiteIdlePolicy.LaneClearance;
                // Already off the lane, in sight of it, and this was a deliberate covering choice:
                // hold and observe rather than pick another slot around the same actor. The flag,
                // not the last logged choice, carries that through repeated observation ticks.
                bool holding = op != null && !onLane && duty.IdleCovering && HasSight(soldier.transform.position, laneFrom);
                bool cover = false, frontier = false;
                Vector3 coverPoint = step, frontierPoint = step; string coverBenefit = null;
                if (!holding && op != null) cover = TryCoveragePoint(soldier, laneFrom, laneTo, out coverPoint, out coverBenefit);
                if (!holding && !cover)
                {
                    frontierPoint = SearchGoal(soldier);
                    frontier = Vector3.Distance(frontierPoint, soldier.transform.position) >= 1.5f
                        && !(_manifest._planningBlocked.TryGetValue(id, out var searchCause)
                             && searchCause == BlacksiteBlockedCause.NoReachableCandidate);
                }
                var choice = BlacksiteIdlePolicy.Choose(false, false, holding, onLane, cover, frontier);
                switch (choice)
                {
                    case BlacksiteIdleGoal.Lane:
                    case BlacksiteIdleGoal.Coverage:
                        step = coverPoint; facing = laneFrom; benefit = coverBenefit;
                        duty.IdleBlocked = 0; duty.IdleCovering = true; break;
                    case BlacksiteIdleGoal.Frontier:
                        // #957 S12A U4: the planner has owned the frontier since #955, so an idle
                        // Frontier is not a step to a node the squad may not be going to. The node
                        // is kept as the point the successor set reads and as the one the line
                        // reports; the facing is the doorway the committed route leaves by.
                        step = frontierPoint;
                        facing = TryRouteNextDoorway(soldier, out Vector3 hop) ? hop : frontierPoint;
                        benefit = "unvisited node";
                        duty.IdleBlocked = 0; duty.IdleCovering = false; break;
                    case BlacksiteIdleGoal.Blocked:
                        // Bounded: hold and retry a couple of times, then report the blockage rather
                        // than hop around the same refused geometry forever.
                        duty.IdleBlocked++;
                        if (duty.IdleBlocked < BlacksiteIdlePolicy.MaxBlockedChoices)
                        { benefit = "no usable covering position or frontier; holding and retrying"; choice = BlacksiteIdleGoal.Observe; }
                        else benefit = "no usable covering position or frontier";
                        facing = laneFrom; break;
                    default:
                        benefit = holding ? "holding a covering position with sight of the operator lane" : "observing";
                        facing = op != null ? laneFrom : duty.Order.PreferredFacing; duty.IdleBlocked = 0; break;
                }
                return choice;
            }

            // ---------------------------------------------------------------------------------
            // The cover step's point source (…Duties.cs:760-818) verbatim.
            // ---------------------------------------------------------------------------------

            private BlacksiteSoldierAI ActiveOperator(BlacksiteSoldierAI soldier)
            {
                // #1018 S4 (design note G10 f): by index, for the same reason ResolveIntent's own
                // record scan is - Records' enumerator is interface-typed and boxes.
                var records = _manifest.Records;
                for (int i = 0; i < records.Count; i++)
                {
                    var r = records[i];
                    if (r.Kind == BlacksiteObjectiveKind.Loot
                        || (r.Phase != BlacksiteObjectivePhase.Reserved && r.Phase != BlacksiteObjectivePhase.Carried)) continue;
                    var owner = _manifest.Soldier(r.CarrierId);
                    if (owner != null && owner != soldier && !owner.isEnemyDead && owner.isOutside == soldier.isOutside) return owner;
                }
                return null;
            }

            // The operator's approach lane: from where it stands to the goal it walks in its own
            // world (#1233 S3: TryGetTaskGoal - an inside carrier's leg is the link, not its
            // delivery point outside).
            private void OperatorLane(BlacksiteSoldierAI op, out Vector3 from, out Vector3 to)
            {
                from = to = Vector3.zero;
                if (op == null) return;
                from = op.transform.position;
                to = TryGetTaskGoal(op, out Vector3 goal) ? goal : from;
            }

            private static float LaneDistance(Vector3 point, Vector3 from, Vector3 to)
            {
                Vector3 lane = Vector3.ProjectOnPlane(to - from, Vector3.up);
                Vector3 offset = Vector3.ProjectOnPlane(point - from, Vector3.up);
                if (lane.sqrMagnitude < .01f) return offset.magnitude;
                return Vector3.Distance(offset, lane * Mathf.Clamp01(Vector3.Dot(offset, lane) / lane.sqrMagnitude));
            }

            private static bool HasSight(Vector3 from, Vector3 to)
            {
                int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
                return !Physics.Linecast(from + Vector3.up * 1.6f, to + Vector3.up * 1.2f, mask, QueryTriggerInteraction.Ignore);
            }

            // #1018 S4 (fix batch 3, blocker 1): ONE body, on the manifest, beside the
            // SupportPoint candidate loop that is its other caller. Batch 2 made both copies index
            // loops, which fixed the allocation but left two bodies of one rule reading one pair of
            // dictionaries; a separation test that two rules can disagree about is not a separation
            // test. The manifest's is the survivor because SupportPoint is still the manifest's.
            private bool Occupied(BlacksiteSoldierAI soldier, Vector3 point)
                => _manifest.Occupied(soldier, point);

            /// <summary>
            /// A covering slot on the operator's own storey: off its approach lane, with line of
            /// sight to it, physically occupiable, separately reserved and reachable by a complete
            /// route. Twelve bearings, the same two radii, the same score.
            /// </summary>
            private bool TryCoveragePoint(BlacksiteSoldierAI soldier, Vector3 laneFrom, Vector3 laneTo,
                out Vector3 point, out string benefit)
            {
                point = soldier.transform.position; benefit = null;
                int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
                float best = float.NegativeInfinity;
                for (int i = 0; i < 12; i++)
                {
                    Vector3 candidate = laneFrom
                        + Quaternion.Euler(0, soldier.MemberIndex * 75f + i * 30f, 0) * Vector3.forward * (i < 6 ? 3.2f : 5f);
                    if (!NavMesh.SamplePosition(candidate, out var nav, .65f, NavMesh.AllAreas)
                        || !BlacksiteResponsePolicy.OnFloor(nav.position.y, laneFrom.y)
                        || Occupied(soldier, nav.position)) continue;
                    float lane = LaneDistance(nav.position, laneFrom, laneTo);
                    if (lane < BlacksiteIdlePolicy.LaneClearance || !HasSight(nav.position, laneFrom)
                        || BlacksiteHazardAwareness.Nearby(nav.position, 12f) != null
                        || !BlacksitePhysicalStep.CanOccupy(soldier.transform, nav.position, mask)) continue;
                    float travel = Vector3.Distance(soldier.transform.position, nav.position);
                    if (travel < 1.5f) continue;
                    if (!_manifest.SpendCandidatePath(soldier.NetworkObjectId)) break;
                    if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(soldier), soldier.transform.position, nav.position).Reachable) continue;
                    float score = lane - travel * .25f;
                    if (score <= best) continue;
                    best = score; point = nav.position; benefit = $"lane+{lane:0.0}m, sight of the operator";
                }
                return best > float.NegativeInfinity;
            }

            // ---------------------------------------------------------------------------------
            // The frontier tile's point sources (…Raid.cs:901-982, …Duties.cs:610-664) verbatim.
            // ---------------------------------------------------------------------------------

            // #1018 S4 (fix batch 1, B1): the reused nearest-first buffers. Scratch, not per-member
            // state, so they are the planner's under the ownership split above.
            private int[] _searchOrder;
            private float[] _searchKeys;

            internal Vector3 SearchGoal(BlacksiteSoldierAI leader) => SearchGoal(leader, -1);

            /// <summary>
            /// The nearest unvisited AI node, with its 90 s stamp, its 3 m auto-visit and its 2 s
            /// re-pick unchanged. A non-negative <paramref name="tileFilter"/> confines it to one
            /// room, which is the in-room advisory a role action takes.
            /// </summary>
            internal Vector3 SearchGoal(BlacksiteSoldierAI leader, int tileFilter)
            {
                if (!_manifest._searchRoutes.TryGetValue(leader.NetworkObjectId, out var route) || route.Outside != leader.isOutside)
                    _manifest._searchRoutes[leader.NetworkObjectId] = route =
                        new SearchRoute { Goal = leader.transform.position, Outside = leader.isOutside };
                if (Time.time < route.Until
                    && Vector3.Distance(leader.transform.position, route.Goal) > 1.8f) return route.Goal;
                if (Time.time < route.Next)
                {
                    if (route.Blocked) _manifest._planningBlocked[leader.NetworkObjectId] = BlacksiteBlockedCause.NoReachableCandidate;
                    return route.Goal;
                }
                route.Next = Time.time + 2f;
                // #874 S6.4b: the frontier yields to what is already known.
                bool objectiveKnown = _manifest.TryFocusRecord(out var objective, out _);
                Vector3 objectivePoint = objectiveKnown ? objective.Position : Vector3.zero;
                float bestBias = 0f;
                var nodes = leader.isOutside
                    ? (_manifest._outsideSearchNodes ?? (_manifest._outsideSearchNodes = GameObject.FindGameObjectsWithTag("OutsideAINode")))
                    : (_manifest._insideSearchNodes ?? (_manifest._insideSearchNodes = GameObject.FindGameObjectsWithTag("AINode")));
                GameObject best = null; float bestScore = float.PositiveInfinity;
                var tileOwner = tileFilter >= 0 ? _manifest.RoutePlanner : null;
                // #1018 S4 (fix batch 1, B1): NEAREST-FIRST, restored exactly.
                //
                // The board's rule is `nodes.Where(n => n != null).OrderBy(n => Distance(leader, n))`
                // and that order is LOAD-BEARING, because the loop below breaks early -
                // `if (!SpendCandidatePath(...)) break;` after at most four route queries per member
                // per frame. So the order does not merely break ties: it decides WHICH four
                // candidates are ever scored. A first draft dropped the sort on the argument that
                // SearchScore ranks every candidate independently, which is true of the ranking and
                // false of which candidates reach it; scene order would have chosen the four.
                //
                // Reproduced without the per-call allocation: planner-owned index and key buffers,
                // grown on demand and never allocated per call, ordered by an INSERTION sort, which
                // is stable - equal distances keep array order, exactly as LINQ's OrderBy does. It
                // runs once per 2 s re-pick per member, in front of a loop that spends a route
                // query per candidate, so its cost is not the one that matters here.
                if (_searchOrder == null || _searchOrder.Length < nodes.Length)
                { _searchOrder = new int[nodes.Length]; _searchKeys = new float[nodes.Length]; }
                int ordered = 0;
                for (int n = 0; n < nodes.Length; n++)
                {
                    if (nodes[n] == null) continue;   // the Where(n => n != null) half
                    float key = Vector3.Distance(leader.transform.position, nodes[n].transform.position);
                    int at = ordered++;
                    while (at > 0 && _searchKeys[at - 1] > key)
                    { _searchOrder[at] = _searchOrder[at - 1]; _searchKeys[at] = _searchKeys[at - 1]; at--; }
                    _searchOrder[at] = n; _searchKeys[at] = key;
                }
                for (int o = 0; o < ordered; o++)
                {
                    var node = nodes[_searchOrder[o]];
                    if (node == null
                        || BlacksiteHazardAwareness.Nearby(node.transform.position, 12f) != null) continue;
                    // #955 S12A U2: the in-room filter. Outside the squad's own tile this rule has
                    // no say at all while a route is committed.
                    if (tileOwner != null && tileOwner.TileOf(node.transform.position) != tileFilter) continue;
                    if (_manifest._visited.TryGetValue(node.GetInstanceID(), out float visited) && Time.time - visited < 90f) continue;
                    float distance = Vector3.Distance(leader.transform.position, node.transform.position);
                    if (distance < 3f) { _manifest._visited[node.GetInstanceID()] = Time.time; continue; }
                    if (!_manifest.SpendCandidatePath(leader.NetworkObjectId)) break;
                    // #950 A4 (O2), #1305 G1: the route rides the mineshaft car, so the off-storey
                    // licence that kept a car-only node as a penalised candidate is gone with the
                    // raw walk it compensated for: a node the route cannot reach is spent like any other.
                    if (!BlacksiteReachability.Route(BlacksiteTraveller.Soldier(leader), leader.transform.position, node.transform.position).Reachable)
                    { _manifest._visited[node.GetInstanceID()] = Time.time - 75f; continue; }
                    float bias = objectiveKnown ? Vector3.Distance(node.transform.position, objectivePoint) : 0f;
                    float score = BlacksiteSquadFocusPolicy.SearchScore(distance, bias, objectiveKnown);
                    if (score < bestScore) { bestScore = score; best = node; bestBias = bias; }
                }
                route.Blocked = best == null;
                if (best == null)
                {
                    if (_manifest._planningBlocked.TryGetValue(leader.NetworkObjectId, out var cause)
                        && cause == BlacksiteBlockedCause.PlanningBudget) route.Next = Time.time + .2f;
                    else _manifest._planningBlocked[leader.NetworkObjectId] = BlacksiteBlockedCause.NoReachableCandidate;
                }
                else _manifest._planningBlocked.Remove(leader.NetworkObjectId);
                route.Goal = best != null ? best.transform.position : leader.transform.position;
                if (best != null) _manifest._visited[best.GetInstanceID()] = Time.time;
                route.Until = Time.time + 30f;
                BlacksiteDiagnostics.Log("Raid",
                    $"search outside={leader.isOutside}, leader={leader.MemberIndex}, goal={route.Goal}, visited={_manifest._visited.Count}"
                    + $", objectiveKnown={objectiveKnown}, bias={(objectiveKnown && best != null ? bestBias.ToString("0.00") : "none")}"
                    + $", route={_manifest.RouteNextRoomToken}.");
                return route.Goal;
            }

            /// <summary>
            /// #990 S16 D3: how long a doorway this member has already been sent to stays spent.
            /// <c>SearchGoal</c>'s own stamp, because this rule is the same kind of rule against the
            /// same kind of target: "somewhere I have not tried lately", not "somewhere new forever".
            /// </summary>
            /// #1018 S4: the planner's own copy of the number, which SHADOWS the manifest's
            /// identical const inside this type until block 6 deletes the board's rule. It is a
            /// constant, not per-member state, so the fix batch 1 ownership split does not cover it.
            internal const float IdleDoorwayVisitedSeconds = 90f;

            /// <summary>
            /// #990 S16 D3: where a routeless idle member walks. Two candidates, in order - the
            /// frontier node the <c>Frontier</c> choice already proved, and otherwise a doorway of
            /// the room the member is standing in, chosen by the ROOM GRAPH. Verbatim, including the
            /// entry-doorway refusal and the 90 s visited window.
            ///
            /// #1018 S4 (fix batch 1, B3): this method PROVES and no longer STAMPS. In the board it
            /// was called from the rule that immediately turned its answer into an order, so
            /// writing <c>_idleDoorwayVisited</c> here and issuing were the same event. Under S4 a
            /// prove happens every 4 s review and an issue happens when the idle Action takes the
            /// FrontierTile successor, which may be never - so a prove that spent the 90 s stamp
            /// retired doorways nobody ever walked to. The chosen doorway's key comes back in
            /// <paramref name="doorwayKey"/>, travels on the plan state, and is spent by
            /// <see cref="NoteFrontierTaken"/>. Zero when the answer is the frontier NODE, which has
            /// no doorway identity and is stamped by <c>SearchGoal</c>'s own <c>_visited</c> map.
            ///
            /// The board's "standing on it IS having tried it" stamp goes with the rest: the
            /// candidate is still skipped by the same distance test, so the CHOICE is unchanged and
            /// only a stamp nothing issued is no longer spent.
            /// </summary>
            private bool TryIdleIntentDestination(BlacksiteSoldierAI soldier, BlacksiteIdleGoal choice,
                Vector3 frontier, out Vector3 point, out string source, out long doorwayKey)
            {
                point = soldier.transform.position; source = BlacksiteDiagnosticFormat.None;
                doorwayKey = 0L;
                if (choice == BlacksiteIdleGoal.Frontier
                    && Vector3.ProjectOnPlane(frontier - soldier.transform.position, Vector3.up).magnitude
                        > BlacksiteArrivalPolicy.MinDestinationChange)
                { point = frontier; source = "frontierNode"; return true; }

                var planner = _manifest.RoutePlanner;
                if (planner == null || soldier.isOutside) return false;
                int tile = planner.TileOf(soldier.transform.position);
                if (tile < 0) return false;
                var doorways = planner.DoorwayTargets(tile);
                // Still honoured when the planner does know the way in: a member with nowhere to be
                // should not be sent back through the door the squad entered by.
                bool hasEntry = _manifest.TryEntryDoorway(out Vector3 entry) && IsOwnDoorway(doorways, entry);
                ulong id = soldier.NetworkObjectId;
                float best = float.PositiveInfinity;
                bool bestUnexplored = false;
                long bestKey = 0;
                for (int i = 0; i < doorways.Count; i++)
                {
                    Vector3 candidate = doorways[i];
                    if (hasEntry && NearIdleSweepTarget(candidate, entry)) continue;
                    long key = DoorwayKey(candidate);
                    if (_manifest._idleDoorwayVisited.TryGetValue((id, key), out float visited)
                        && Time.time - visited < IdleDoorwayVisitedSeconds) continue;
                    float distance = Vector3.ProjectOnPlane(candidate - soldier.transform.position, Vector3.up).magnitude;
                    // Standing on it IS having tried it, so it is skipped - by the same distance
                    // test the board used. The stamp that went with it is now
                    // NoteFrontierTaken's (B3), and this candidate is never issued, so nothing
                    // spends it.
                    if (distance <= BlacksiteArrivalPolicy.MinDestinationChange) continue;
                    if (!NavMesh.SamplePosition(candidate, out var nav, BlacksiteArrivalPolicy.AgentRadius * 2f, NavMesh.AllAreas)) continue;
                    int across = planner.TileAcrossDoorway(tile, candidate);
                    bool unexplored = across >= 0 && !planner.IsTileExplored(across);
                    if (bestUnexplored && !unexplored) continue;
                    if (unexplored == bestUnexplored && distance >= best) continue;
                    best = distance; bestUnexplored = unexplored; bestKey = key;
                    point = nav.position; source = unexplored ? "frontierDoorway" : "knownDoorway";
                }
                if (best == float.PositiveInfinity) return false;
                doorwayKey = bestKey;
                return true;
            }

            /// <summary>
            /// #1018 S4 (fix batch 1, B3): the doorway this member's FrontierTile successor would
            /// walk to, so the idle Action's body can spend its stamp when it actually issues it.
            /// Zero, and false, when the proved frontier is a node rather than a doorway.
            /// </summary>
            internal bool TryGetFrontierDoorway(ulong member, out long doorway)
            {
                doorway = _duties.TryGetValue(member, out var duty) ? duty.FrontierDoorway : 0L;
                return doorway != 0L;
            }

            /// <summary>
            /// #1018 S4 (fix batch 1, B3): the ISSUE half of #990 S16 D3's doorway memory. Called by
            /// the idle Action when it commits the FrontierTile successor, never by a prove, so a
            /// doorway is spent for <see cref="IdleDoorwayVisitedSeconds"/> only once a member has
            /// actually been sent to it. Without it a routeless member alternates between the two
            /// openings of a corridor and calls it progress; with it spent on a prove instead, a
            /// routed squad retired every doorway of the room it was standing in.
            /// </summary>
            internal void NoteFrontierTaken(ulong member, long doorway)
            {
                if (doorway == 0L) return;
                _manifest._idleDoorwayVisited[(member, doorway)] = Time.time;
            }

            /// <summary>The doorway the committed route leaves this member's own room by.</summary>
            private bool TryRouteNextDoorway(BlacksiteSoldierAI soldier, out Vector3 doorway)
            {
                doorway = Vector3.zero;
                var planner = _manifest.RoutePlanner;
                if (planner == null || soldier.isOutside || planner.NextRoom < 0) return false;
                int tile = planner.TileOf(soldier.transform.position);
                return tile >= 0 && TryHopDoorway(planner, tile, planner.LeadWaypoint, out doorway);
            }

            // ---------------------------------------------------------------------------------
            // The regroup successor's point source: the squad lead (design note C.4, row 3).
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #1018 S4 (fix batch 1, SF12): "separated from the lead", by the existing rule -
            /// <c>…Raid.cs:714-715</c>'s 14 m, in the lead's own world. The board measured it here
            /// and nowhere else, and the successor ranking's third precondition IS this fact; a
            /// first draft passed "a regroup point was provable" instead, which is a different
            /// question and made the anchor arm self-justifying.
            /// </summary>
            private bool SeparatedFromLead(BlacksiteSoldierAI soldier)
            {
                var lead = _manifest.RouteLead();
                return lead != null && lead != soldier && !lead.isEnemyDead
                       && lead.isOutside == soldier.isOutside
                       && Vector3.Distance(lead.transform.position, soldier.transform.position) > 14f;
            }

            /// <summary>
            /// #1018 S4: a regroup point, from the squad lead when this member is separated from it.
            /// #874 S4: the moving-squadmate anchor went with the blocked-answer ladder that chose it.
            ///
            /// Both of the existing numbers: separation is the 14 m <c>SupportOrSearchOrder</c>
            /// already measures (<c>…Raid.cs:715</c>), and the lead must be on this member's own
            /// storey by <see cref="BlacksiteResponsePolicy.OffStorey"/>'s 4 m - off it, the
            /// elevator, the stairs or a world ladder owns the route and a regroup point on this
            /// storey would leave the member with nothing to walk to.
            /// </summary>
            private bool TryRegroupPoint(BlacksiteSoldierAI soldier, float now, out Vector3 point, out Vector3 facing)
            {
                point = Vector3.zero; facing = Vector3.zero;
                var lead = _manifest.RouteLead();
                if (lead == null || lead == soldier || lead.isEnemyDead
                    || lead.isOutside != soldier.isOutside) return false;
                if (BlacksiteResponsePolicy.OffStorey(soldier.transform.position.y, lead.transform.position.y)) return false;
                if (Vector3.Distance(lead.transform.position, soldier.transform.position) <= 14f) return false;
                point = lead.transform.position;
                facing = point + lead.transform.forward * 5f;
                return true;
            }

            // ---------------------------------------------------------------------------------
            // #874 Q6 (F9): the outside ship guard's patrol loop.
            //
            // Replaces the 3.5 m guard ring: twelve bearings 55 degrees apart per post, a new post
            // on arrival or after 12 s, MemberIndex % 2 * 180 to split the pair, and a guard facing
            // the ship. The loop is proved ONCE per rendezvous and shared by both guards; each guard
            // keeps its own place on it in the manifest's per-member patrol dictionaries, which the
            // mission reset (ClearDutyJobs) and ForgetMember already clear:
            //   _guardDuty     #1219 E1: the guard's duty state (BlacksiteGuardDuty) - the waypoint
            //                  it is on or walking to, its phase, its paused dwell and its leg pace;
            //   _patrolPoints  that waypoint (present only while a patrol is live);
            //   _patrolSource  the [UAT][Job] source= token.
            // #1219 E3: the two guards start half a loop apart and never share a waypoint: a guard
            // holds the waypoint it is walking to or standing on, and nobody else is sent there.
            // ---------------------------------------------------------------------------------

            private readonly List<Vector3> _patrolLoop = new List<Vector3>();
            private readonly List<Vector3> _patrolCandidatePoints = new List<Vector3>();
            private readonly List<BlacksitePatrolCandidate> _patrolCandidates = new List<BlacksitePatrolCandidate>();
            private readonly List<int> _patrolPick = new List<int>();
            private readonly NavMeshPath _patrolPath = new NavMeshPath();
            private Vector3 _patrolAnchor;
            private bool _patrolProved;
            private float _patrolRetryAt;
            private string _patrolLoopSource = "none";
            // #874 Q6: the loop's dead waypoints. Each death carries its own
            // expiry (BlacksitePatrolPolicy.DeathExpiry): a stalled leg (#1293 R2) is transient and
            // comes back after TransientDeathSeconds; only an
            // Unreachable closure is durable, and a moved rendezvous clears even that. The mask is
            // derived from the expiries at the moment it is read, never stored. The positions
            // outlive a re-proof (with the same expiries) so a re-proof does not hand a candidate on
            // a still-dead point straight back.
            private readonly List<float> _patrolDeadUntil = new List<float>();
            private readonly List<(Vector3 Point, float Until)> _patrolDeadPoints = new List<(Vector3, float)>();
            private readonly List<float> _patrolStartRanges = new List<float>();
            private int _patrolDead => BlacksitePatrolPolicy.DeadMask(_patrolDeadUntil, Time.time);
            // The evaluation each member's patrol last ran on; one that skips the patrol drops it, so
            // time away is never dwell.
            private readonly Dictionary<ulong, float> _patrolTickAt = new Dictionary<ulong, float>();
            // The temporary destinations of a mutual exchange. Neither real post is admitted
            // until its physical occupant has left; the incoming reservations live in _guardDuty.
            private readonly Dictionary<ulong, Vector3> _patrolExchangePoints = new Dictionary<ulong, Vector3>();
            // A single-post fallback is re-proved on this cadence: the ship's own navmesh and the
            // staged cargo can change the ground the first proof saw, and a proof is ~36 path queries.
            private const float PatrolRetrySeconds = 15f;
            // FacePoint reads the direction and nothing else; the distance only has to clear its own
            // dead zone, so this is the loop's own spacing rather than a new tuning number.
            private const float PatrolLookMetres = BlacksitePatrolPolicy.WaypointSpacingMetres;

            /// <summary>Everything the loop proof owns, reset by <see cref="Clear"/> on a new mission.</summary>
            private void ClearPatrolLoop()
            {
                EndPatrolScans();
                _patrolLoop.Clear(); _patrolCandidatePoints.Clear(); _patrolCandidates.Clear(); _patrolPick.Clear();
                _patrolDeadPoints.Clear(); _patrolTickAt.Clear(); _patrolExchangePoints.Clear(); _patrolDeadUntil.Clear(); _patrolStartRanges.Clear();
                _patrolProved = false; _patrolRetryAt = 0f; _patrolLoopSource = "none";
            }

            /// <summary>
            /// The Guard duty's destination: the member's current patrol waypoint, or his ground slot
            /// (<see cref="GuardGroundPoint"/>) while there is none.
            /// </summary>
            internal Vector3 GuardPatrolPoint(BlacksiteSoldierAI soldier)
                => TryPatrolWaypoint(soldier, out var point) ? point : GuardGroundPoint(soldier);

            /// <summary>#1305 S (R2): a postless guard's slot on the plan's ground floor routed from the plan exit, so the same point wherever he stands (on it he is at his post); else that exit, never the raw slot.</summary>
            private Vector3 GuardGroundPoint(BlacksiteSoldierAI soldier)
            {
                var ship = _manifest.Ship; Vector3 slot = ship.EgressPosition(soldier.MemberIndex), exit = ship.ExitPosition;
                if (slot == Vector3.zero) slot = ship.RendezvousPosition;
                float reach = BlacksiteArrivalPolicy.AgentRadius * 2f;
                if (!NavMesh.SamplePosition(exit, out var from, reach, NavMesh.AllAreas)) return exit;
                if (NavMesh.SamplePosition(new Vector3(slot.x, exit.y, slot.z), out var target, reach, NavMesh.AllAreas))
                {
                    var walk = BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(soldier), from.position, target.position, _patrolPath);
                    if (walk.Planned) return walk.Reachable ? target.position : new Vector3(walk.EndX, walk.EndY, walk.EndZ);
                    if (NavMesh.Raycast(from.position, target.position, out var stop, NavMesh.AllAreas)) return stop.position;
                }
                return from.position;
            }

            /// <summary>
            /// #874 Q6: the member's current patrol waypoint. Proves the loop on first use (and on a
            /// moved rendezvous, or once fewer than two waypoints are alive), places a new member
            /// (<see cref="StartWaypoint"/>, #1219 E3: half a loop from the other guard), and refuses
            /// a DEAD one: a waypoint the planner closed Unreachable is marked dead for the whole
            /// loop (a stalled leg is marked by <see cref="TryAnswerStalledPatrol"/>) and the member
            /// moves to the next live waypoint no other guard holds, so every caller - the order
            /// planner, <see cref="ShapeDutyOrder"/>'s leash fallback and the Unreachable re-target -
            /// gets a live, unshared waypoint or none. The single-post fallback is never taken by a
            /// transient death. The duty's phases (#1219 E1) are <see cref="TickPatrol"/>'s.
            /// </summary>
            internal bool TryPatrolWaypoint(BlacksiteSoldierAI soldier, out Vector3 point)
            {
                point = default;
                ulong id = soldier.NetworkObjectId;
                // The same gate the other duties already use: a member aboard the ship (#1233 S4,
                // the live answer) is still on its Egress duty and is not planned a patrol.
                var ship = _manifest.Ship;
                if (ship == null || soldier.IsAboardShip)
                {
                    _manifest._patrolSource[id] = "notDeployed"; _manifest._patrolPoints.Remove(id);
                    return false;
                }
                for (int pass = 0; pass < 2; pass++)
                {
                    if (!TryPatrolLoop(soldier, ship)) return NoPatrolPost(id, out point);
                    int count = _patrolLoop.Count;
                    if (!_manifest._guardDuty.TryGetValue(id, out var guard) || guard.Index < 0 || guard.Index >= count
                        || !_manifest._patrolPoints.ContainsKey(id) || !BlacksitePatrolPolicy.IsLive(guard.Index, _patrolDead))
                    {
                        int start = StartWaypoint(soldier);
                        if (start < 0) return NoPatrolPost(id, out point, "held");
                        guard = BlacksiteGuardDuty.Begin(start, Time.time);
                        _manifest._guardDuty[id] = guard;
                        _manifest._patrolSource[id] = PatrolSourceToken(start, count);
                    }
                    ObservePatrolDeparture(soldier, ref guard);
                    _manifest._guardDuty[id] = guard;
                    point = guard.Exchanging && _patrolExchangePoints.TryGetValue(id, out var staging)
                        ? staging : _patrolLoop[guard.Index];
                    _manifest._patrolPoints[id] = point;
                    if (!PatrolWaypointUnreachable(id, point)) return true;
                    if (MarkPatrolWaypointDead(soldier, guard.Index, "unreachable", true, out point)) return true;
                    if (Time.time < _patrolRetryAt) return NoPatrolPost(id, out point, "exhausted");
                    // Otherwise the next pass re-proves the loop now.
                }
                return NoPatrolPost(id, out point);
            }

            private bool NoPatrolPost(ulong id, out Vector3 point, string source = null)
            {
                point = default;
                _manifest._patrolSource[id] = source ?? _patrolLoopSource;
                _manifest._patrolPoints.Remove(id);
                _manifest._planningBlocked[id] = BlacksiteBlockedCause.NoReachableCandidate;
                return false;
            }

            /// <summary>Whether the planner closed the member's attempt on this waypoint Unreachable.</summary>
            private bool PatrolWaypointUnreachable(ulong id, Vector3 waypoint)
                => _duties.TryGetValue(id, out var duty) && !duty.Budget.AttemptOpen
                   && duty.Budget.Result == BlacksiteJobResult.Unreachable
                   && Vector3.Distance(duty.Order.Destination, waypoint) <= BlacksiteArrivalPolicy.MinDestinationChange;

            /// <summary>
            /// #874 Q6: marks waypoint <paramref name="dead"/> dead for the whole loop - durably for an
            /// Unreachable closure, for <see cref="BlacksitePatrolPolicy.TransientDeathSeconds"/> for a
            /// stalled leg - and starts a fresh leg to the next live waypoint no other guard holds.
            /// True with that waypoint in <paramref name="point"/> while enough of the loop is left or
            /// the 15 s re-proof limit is not yet up; false, the member's post withdrawn, otherwise.
            /// </summary>
            private bool MarkPatrolWaypointDead(BlacksiteSoldierAI soldier, int dead, string cause, bool unreachable,
                out Vector3 point)
            {
                ulong id = soldier.NetworkObjectId;
                int count = _patrolLoop.Count, index = dead, mask = _patrolDead;
                float dwellLeft = -1f;
                bool live = BlacksitePatrolPolicy.MarkDead(ref index, ref dwellLeft, count, ref mask, HeldMask(id));
                float expiry = BlacksitePatrolPolicy.DeathExpiry(Time.time, unreachable);
                _patrolDeadUntil[dead] = expiry;
                _patrolDeadPoints.Add((_patrolLoop[dead], expiry));
                // A dead waypoint ends the visit: the next one is a fresh leg.
                _manifest._guardDuty[id] = BlacksiteGuardDuty.Begin(index, Time.time);
                _patrolExchangePoints.Remove(id);
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("Job",
                        $"member={soldier.MemberIndex}, patrol waypoint dead waypoint={dead}/{count}, cause={cause}"
                        + $", until={(unreachable ? "durable" : expiry.ToString("0.00"))}"
                        + $", live={BlacksitePatrolPolicy.LiveCount(count, mask)}, next={(live ? index.ToString() : BlacksiteDiagnosticFormat.None)}.");
                // Enough left alive, or the 15 s re-proof limit not yet up: walk what is left.
                if (live && (!BlacksitePatrolPolicy.NeedsReprove(count, mask) || Time.time < _patrolRetryAt))
                {
                    point = _patrolLoop[index];
                    _manifest._patrolPoints[id] = point;
                    _manifest._patrolSource[id] = PatrolSourceToken(index, count);
                    return true;
                }
                point = default;
                _manifest._patrolPoints.Remove(id);
                return false;
            }

            /// <summary>
            /// #1293 R2: the planner's stall answer for a patrol leg (BlacksiteSquadPlanner.AnswerStalled).
            /// A leg is a travel commitment the ledger judges; a miss on the leg to the member's current
            /// patrol point (<paramref name="goal"/>) marks that waypoint dead with cause=stalled - a
            /// transient death - and revises the goal, so the next pass plans the next leg. False when the member is not
            /// walking that leg (dwelling, scanning, responding, yielding, another goal) or holds the
            /// single-post fallback a transient death never takes, so the duty answer runs instead.
            /// </summary>
            internal bool TryAnswerStalledPatrol(BlacksiteSoldierAI member, Vector3 goal)
            {
                if (member == null) return false;
                ulong id = member.NetworkObjectId;
                if (!_duties.TryGetValue(id, out var duty) || duty.Intent.Kind != BlacksiteJobKind.Guard
                    || !_manifest._guardDuty.TryGetValue(id, out var guard)
                    || (guard.Phase != BlacksiteGuardPhase.Walk && guard.Phase != BlacksiteGuardPhase.Look)
                    || guard.Index < 0 || guard.Index >= _patrolLoop.Count
                    || !BlacksitePatrolPolicy.AppliesDeath(_patrolLoop.Count, unreachable: false)
                    || !_manifest._patrolPoints.TryGetValue(id, out Vector3 leg)
                    || Vector3.Distance(leg, goal) > BlacksiteArrivalPolicy.MinDestinationChange) return false;
                MarkPatrolWaypointDead(member, guard.Index, "stalled", false, out _);
                duty.NextPlan = Time.time; duty.Arrived = false; duty.ReviseAt = Time.time;
                return true;
            }

            private string PatrolSourceToken(int index, int count)
                => count >= BlacksitePatrolPolicy.MinWaypoints ? "patrol:" + index + "/" + count : _patrolLoopSource;

            /// <summary>
            /// #1219 E3: where a new guard starts - the nearest live waypoint when it is alone on the
            /// loop, otherwise half a loop on from the waypoint the other guard holds - and -1 when
            /// every live waypoint is dead or held. Never a shared waypoint
            /// (<see cref="BlacksitePatrolPolicy.ChooseStart"/>).
            /// </summary>
            private int StartWaypoint(BlacksiteSoldierAI soldier)
            {
                _patrolStartRanges.Clear();
                for (int i = 0; i < _patrolLoop.Count; i++)
                    _patrolStartRanges.Add(Vector3.Distance(_patrolLoop[i], soldier.transform.position));
                return BlacksitePatrolPolicy.ChooseStart(_patrolStartRanges, HeldMask(soldier.NetworkObjectId), _patrolDead);
            }

            /// <summary>Incoming reservations plus actual bodies, including the source of a new leg.</summary>
            private int HeldMask(ulong id)
            {
                int mask = OccupiedPatrolMask(id);
                foreach (var other in _manifest._guardDuty)
                    if (other.Key != id && other.Value.Index >= 0 && other.Value.Index < 31
                        && _manifest._patrolPoints.ContainsKey(other.Key))
                        mask |= 1 << other.Value.Index;
                return mask;
            }

            private int OccupiedPatrolMask(ulong except)
            {
                int mask = 0;
                var members = _manifest._members;
                for (int m = 0; m < members.Count; m++)
                {
                    var member = members[m];
                    if (member == null || member.isEnemyDead || member.NetworkObjectId == except || !member.isOutside) continue;
                    for (int i = 0; i < _patrolLoop.Count; i++)
                        if (Vector3.Distance(member.transform.position, _patrolLoop[i]) <= PatrolSourceClearance)
                            mask |= 1 << i;
                }
                return mask;
            }

            private const float PatrolSourceClearance = BlacksitePatrolPolicy.ArrivalRadius + BlacksiteArrivalPolicy.AgentRadius;

            private void ObservePatrolDeparture(BlacksiteSoldierAI soldier, ref BlacksiteGuardDuty guard)
            {
                bool vacated = guard.SourceIndex < 0 || guard.SourceIndex >= _patrolLoop.Count
                    || Vector3.Distance(soldier.transform.position, _patrolLoop[guard.SourceIndex]) > PatrolSourceClearance;
                bool exchanging = guard.Exchanging;
                BlacksitePatrolPolicy.ObserveDeparture(ref guard, vacated, OccupiedPatrolMask(soldier.NetworkObjectId));
                if (exchanging && !guard.Exchanging)
                {
                    ulong id = soldier.NetworkObjectId;
                    _patrolExchangePoints.Remove(id);
                    if (_duties.TryGetValue(id, out var duty)) { duty.NextPlan = Time.time; duty.Arrived = false; }
                }
            }

            private bool TryPatrolExchange(BlacksiteSoldierAI soldier, ref BlacksiteGuardDuty guard, float now)
            {
                if (!guard.HeldOut || guard.Phase != BlacksiteGuardPhase.Dwell) return false;
                var members = _manifest._members;
                // Choose by stable network identity, not dictionary enumeration order.
                BlacksiteSoldierAI partner = null;
                BlacksiteGuardDuty other = default;
                for (int i = 0; i < members.Count; i++)
                {
                    var candidate = members[i];
                    if (candidate == null || candidate.isEnemyDead || candidate == soldier || !candidate.isOutside
                        || !_manifest._guardDuty.TryGetValue(candidate.NetworkObjectId, out var state)
                        || !_duties.TryGetValue(candidate.NetworkObjectId, out var plan) || plan.Intent.Kind != BlacksiteJobKind.Guard
                        || state.Phase != BlacksiteGuardPhase.Dwell || candidate.IsObjectiveCombatActive
                        || candidate.ReplicatedStunned || candidate.SelectedBehaviour == BlacksiteBehaviour.Evade
                        || candidate.Navigator.HasCommittedMotion || candidate.Navigator.IsStranded // #1293 R2: cannot walk the swap
                        || Vector3.Distance(candidate.transform.position, _patrolLoop[state.Index]) > BlacksitePatrolPolicy.ArrivalRadius)
                        continue;
                    if (partner == null || candidate.NetworkObjectId < partner.NetworkObjectId) { partner = candidate; other = state; }
                }
                float peerDwelt = other.DwellStartedAt >= 0f ? now - other.DwellStartedAt : 0f;
                if (partner == null
                    || Vector3.Distance(soldier.transform.position, _patrolLoop[guard.Index]) > BlacksitePatrolPolicy.ArrivalRadius
                    || !TryExchangeStage(guard.Index, other.Index, out var firstStage)
                    || !TryExchangeStage(other.Index, guard.Index, out var secondStage)
                    || !BlacksitePatrolPolicy.TryExchange(ref guard, ref other, _patrolDead)) return false;
                ulong id = soldier.NetworkObjectId, peer = partner.NetworkObjectId;
                _patrolExchangePoints[id] = firstStage; _patrolExchangePoints[peer] = secondStage;
                _manifest._guardDuty[peer] = other;
                _manifest._patrolPoints[peer] = secondStage;
                _manifest._patrolSource[peer] = PatrolSourceToken(other.Index, _patrolLoop.Count);
                _duties[peer].NextPlan = now; _duties[peer].Arrived = false;
                soldier.Navigator.EndPatrolAlert(); partner.Navigator.EndPatrolAlert();
                if (BlacksiteDiagnostics.Enabled)
                {
                    BlacksiteDiagnostics.Log("Job",
                        $"member={soldier.MemberIndex}, patrol exchange peer={partner.MemberIndex}, source={guard.SourceIndex}, next={guard.Index}, phase=vacating.");
                    BlacksiteDiagnostics.Log("Job",
                        $"member={partner.MemberIndex}, patrol next waypoint={other.Index}/{_patrolLoop.Count}"
                        + $", goal={BlacksiteDiagnosticFormat.V(secondStage)}, dwelt={peerDwelt:0.0}, pace={other.LegPace}.");
                }
                return true;
            }

            private bool TryExchangeStage(int source, int destination, out Vector3 point)
            {
                point = default;
                if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(true), _patrolLoop[source], _patrolLoop[destination], _patrolPath).Reachable) return false;
                int count = PatrolPathCorners();
                float length = 0f;
                for (int i = 1; i < count; i++) length += Vector3.Distance(_patrolCorners[i - 1], _patrolCorners[i]);
                float remaining = length / 3f;
                for (int i = 1; i < count; i++)
                {
                    float leg = Vector3.Distance(_patrolCorners[i - 1], _patrolCorners[i]);
                    if (remaining > leg) { remaining -= leg; continue; }
                    if (leg <= .001f) continue;
                    point = Vector3.Lerp(_patrolCorners[i - 1], _patrolCorners[i], remaining / leg);
                    float clearance = PatrolSourceClearance + BlacksiteArrivalPolicy.DefaultStoppingDistance;
                    return Vector3.Distance(point, _patrolLoop[source]) > clearance
                        && Vector3.Distance(point, _patrolLoop[destination]) > clearance;
                }
                return false;
            }

            /// <summary>
            /// #874 Q6: proves the shared loop round the ground rendezvous. Every candidate is a point
            /// on the GROUND: the 2 m height band, clear of the hull footprint at the LANDING pose
            /// (the hover ship is away, so the live hull is the wrong test), occupiable, and reached
            /// from the projected rendezvous by a complete route that never crosses the footprint.
            /// The route's length is the leash test (<see cref="BlacksitePatrolPolicy.InsideLeash"/>);
            /// the sightline to the entrance or the rendezvous is the preference. Fewer than two
            /// spaced survivors fall back to one post, the rendezvous projected onto the mesh -
            /// never the guard's own position, which is what put run 1's posts on the deck - and the
            /// proof is retried on <see cref="PatrolRetrySeconds"/>. A loop with fewer than two live
            /// waypoints is re-proved on the same cadence (#874 Q6), and a candidate
            /// on a point whose death has not yet expired is not proved again; a moved rendezvous
            /// forgets every death, and the projected-rendezvous post is refused only by a durable
            /// (Unreachable) one, so a transient obstruction can never leave the guards postless.
            /// </summary>
            private bool TryPatrolLoop(BlacksiteSoldierAI soldier, BlacksiteShipController ship)
            {
                Vector3 anchor = ship.RendezvousPosition;
                bool moved = (anchor - _patrolAnchor).sqrMagnitude > .25f;
                bool retry = _patrolProved && BlacksitePatrolPolicy.NeedsReprove(_patrolLoop.Count, _patrolDead)
                             && Time.time >= _patrolRetryAt;
                if (_patrolProved && !moved && !retry)
                    return BlacksitePatrolPolicy.LiveCount(_patrolLoop.Count, _patrolDead) > 0;

                _patrolProved = true; _patrolAnchor = anchor;
                _patrolRetryAt = Time.time + PatrolRetrySeconds;
                _patrolLoop.Clear(); _patrolCandidates.Clear(); _patrolCandidatePoints.Clear();
                _patrolDeadUntil.Clear();
                if (moved) _patrolDeadPoints.Clear();
                else _patrolDeadPoints.RemoveAll(d => d.Until <= Time.time);
                // Every member's place was on the old loop.
                ResetPatrolMembers();
                if (!NavMesh.SamplePosition(anchor, out var anchorHit, BlacksiteArrivalPolicy.AgentRadius * 2f, NavMesh.AllAreas))
                {
                    _patrolLoopSource = "unprojected";
                    LogPatrolLoop(anchor, 0);
                    return false;
                }
                Vector3 anchorNav = anchorHit.position;
                int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
                Vector3 eye = Vector3.up * 1.5f;
                Vector3 entrance = _manifest._mission != null ? _manifest._mission.SquadDirector.OutsideMainEntrance : anchor;
                float step = 360f / BlacksitePatrolPolicy.CandidateBearings;
                var radii = BlacksitePatrolPolicy.CandidateRadii;
                int rejected = 0;
                for (int ring = 0; ring < radii.Length; ring++)
                    for (int b = 0; b < BlacksitePatrolPolicy.CandidateBearings; b++)
                    {
                        // Alternate rings are staggered half a bearing so the rings do not line up
                        // into spokes, which is where the spacing test would refuse them all.
                        float angle = b * step + (ring % 2) * step * .5f;
                        Vector3 candidate = anchor + Quaternion.Euler(0, angle, 0) * Vector3.forward * radii[ring];
                        if (!NavMesh.SamplePosition(candidate, out var nav, 1f, NavMesh.AllAreas)
                            || Mathf.Abs(nav.position.y - anchor.y) > 2f
                            || NearDeadPatrolPoint(nav.position)
                            || BlacksiteHazardAwareness.Nearby(nav.position, 12f) != null
                            || BlacksiteHullBlockers.InsideGroundFootprint(ship.LandingPosition, ship.LandingRotation,
                                nav.position, BlacksiteHullBlockers.GuardClearance)
                            || !BlacksitePhysicalStep.CanOccupy(soldier.transform, nav.position, mask)
                            || !BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(soldier), anchorNav, nav.position, _patrolPath).Reachable)
                        { rejected++; continue; }
                        int cornerCount = PatrolPathCorners();
                        float route = 0f; bool crossesHull = false;
                        for (int c = 1; c < cornerCount; c++)
                        {
                            route += Vector3.Distance(_patrolCorners[c - 1], _patrolCorners[c]);
                            if (BlacksiteHullBlockers.InsideGroundFootprint(ship.LandingPosition, ship.LandingRotation, _patrolCorners[c], 0f))
                            { crossesHull = true; break; }
                        }
                        if (crossesHull || !BlacksitePatrolPolicy.InsideLeash(route)) { rejected++; continue; }
                        Vector3 from = nav.position + eye;
                        bool sightline = !Physics.Linecast(from, entrance + eye, mask, QueryTriggerInteraction.Ignore)
                                         || !Physics.Linecast(from, anchorNav + eye, mask, QueryTriggerInteraction.Ignore);
                        _patrolCandidatePoints.Add(nav.position);
                        _patrolCandidates.Add(new BlacksitePatrolCandidate(nav.position.x - anchor.x,
                            nav.position.z - anchor.z, route, sightline));
                    }

                if (BlacksitePatrolPolicy.SelectLoop(_patrolCandidates, _patrolPick) > 0)
                {
                    for (int i = 0; i < _patrolPick.Count; i++) _patrolLoop.Add(_patrolCandidatePoints[_patrolPick[i]]);
                    _patrolLoopSource = "patrol";
                }
                else if (!NearDeadPatrolPoint(anchorNav, durableOnly: true))
                {
                    _patrolLoop.Add(anchorNav);
                    _patrolLoopSource = "rendezvousProjected";
                }
                else
                {
                    // The single post itself is durably unreachable: nothing to hold until the
                    // rendezvous moves or the next proof finds ground.
                    _patrolLoopSource = "exhausted";
                    LogPatrolLoop(anchor, 0, _patrolCandidates.Count, rejected);
                    return false;
                }
                for (int i = 0; i < _patrolLoop.Count; i++) _patrolDeadUntil.Add(0f);
                if (BlacksiteDiagnostics.Enabled)
                {
                    int sighted = 0;
                    for (int i = 0; i < _patrolPick.Count; i++) if (_patrolCandidates[_patrolPick[i]].Sightline) sighted++;
                    LogPatrolLoop(anchor, sighted, _patrolCandidates.Count, rejected);
                }
                return true;
            }

            private void EndPatrolDuty(BlacksiteSoldierAI soldier)
            {
                soldier.Navigator.EndPatrolAlert();
                ulong id = soldier.NetworkObjectId;
                _manifest._guardDuty.Remove(id); _manifest._patrolPoints.Remove(id);
                _manifest._patrolSource.Remove(id);
                _patrolTickAt.Remove(id); _patrolExchangePoints.Remove(id);
            }

            private void EndPatrolScans()
            {
                for (int i = 0; i < _manifest._members.Count; i++)
                    if (_manifest._members[i] != null) _manifest._members[i].Navigator.EndPatrolAlert();
            }

            private void ResetPatrolMembers()
            {
                EndPatrolScans();
                _manifest._guardDuty.Clear(); _manifest._patrolPoints.Clear();
                _patrolTickAt.Clear(); _patrolExchangePoints.Clear();
            }

            private bool NearDeadPatrolPoint(Vector3 point, bool durableOnly = false)
            {
                float now = Time.time;
                for (int i = 0; i < _patrolDeadPoints.Count; i++)
                {
                    var dead = _patrolDeadPoints[i];
                    if (dead.Until <= now || (durableOnly && !BlacksitePatrolPolicy.IsDurable(dead.Until))) continue;
                    if (Vector3.Distance(dead.Point, point) <= BlacksiteArrivalPolicy.MinDestinationChange) return true;
                }
                return false;
            }

            private void LogPatrolLoop(Vector3 anchor, int sighted, int proved = 0, int rejected = 0)
            {
                if (!BlacksiteDiagnostics.Enabled) return;
                BlacksiteDiagnostics.Log("Job",
                    $"patrol loop source={_patrolLoopSource}, waypoints={_patrolLoop.Count}, sightline={sighted}"
                    + $", candidates={proved}, rejected={rejected}, anchor={BlacksiteDiagnosticFormat.V(anchor)}"
                    + $", leash={BlacksitePatrolPolicy.LeashMetres:0.0}m.");
            }

            /// <summary>
            /// #874 Q6 / #1219 E1: one evaluation of an outside guard's duty and the only run of its
            /// state machine (<see cref="BlacksitePatrolPolicy.Step"/>): arrival dwells 10-15 s, then
            /// looks on to the next free waypoint; an alert scans once per visit. #1293 R3: the dwell's
            /// and scan's ends are deadlines; a phase change is a held-out guard's event.
            /// </summary>
            private void TickPatrol(BlacksiteSoldierAI soldier, Duty duty, float now)
            {
                ulong id = soldier.NetworkObjectId;
                // The idle review never runs for a patrolling guard: the patrol IS its idle.
                duty.IdleSince = now; duty.ObservedPosition = soldier.transform.position;
                var ship = _manifest.Ship;
                // A patrol not ticking on the last evaluation counts none of the time away as dwell.
                bool notTicking = !_patrolTickAt.ContainsKey(id);
                _patrolTickAt[id] = now;
                int held = _manifest._guardDuty.TryGetValue(id, out var heldDuty) ? heldDuty.Index : -1;
                // A waypoint that died is replaced before anything below reads it.
                if (ship == null || !TryPatrolWaypoint(soldier, out Vector3 waypoint)
                    || !_manifest._guardDuty.TryGetValue(id, out var guard) || _patrolLoop.Count == 0)
                {
                    soldier.Navigator.EndPatrolAlert();
                    Publish(soldier, duty, default(BlacksiteSuccessorSet));
                    return;
                }
                if (guard.Index != held) duty.NextPlan = now;
                // #1219 E1: an interrupted duty (a response, a replan elsewhere) resumes where it
                // stood: the time it was away is not dwell.
                if (notTicking) guard.LastTick = now;
                int count = _patrolLoop.Count;
                int seed = PatrolSeed(soldier);
                // Respond is exact contact only (a backup dispatch takes the member off the Guard
                // duty, so it never reaches here); a stun or a hazard is not a response and Yields.
                bool contact = soldier.IsObjectiveCombatActive;
                bool yielded = soldier.ReplicatedStunned || soldier.SelectedBehaviour == BlacksiteBehaviour.Evade
                    || soldier.Navigator.HasCommittedMotion;
                // #1293 R1: alerted = the squad holds a threat row this member's own eyes do not.
                BlacksiteWorldRow alert = null;
                bool alerted = soldier.TryGetWorld(out var squadWorld)
                    && squadWorld.TryGetAlert(soldier.PerceptionSlot, out alert);
                // #874 Q6: the latch is historical and survives a displacement
                // back to the SAME waypoint, so the dwell also needs the body on the waypoint now.
                Vector3 offset = soldier.transform.position - waypoint; offset.y = 0f;
                bool arrived = !guard.Exchanging
                    && BlacksitePatrolPolicy.ArrivedForDwell(ArrivedAtPost(id, waypoint), offset.magnitude);
                var before = guard;
                var phase = BlacksitePatrolPolicy.Step(ref guard, count, now, seed, arrived, alerted, contact, yielded,
                    _patrolDead, HeldMask(id));
                if (TryPatrolExchange(soldier, ref guard, now)) phase = guard.Phase;
                _manifest._guardDuty[id] = guard;
                bool entered = phase != before.Phase;
                bool log = BlacksiteDiagnostics.Enabled;
                if (phase != BlacksiteGuardPhase.Alert) soldier.Navigator.EndPatrolAlert();
                if (phase == BlacksiteGuardPhase.Respond || phase == BlacksiteGuardPhase.Yield)
                {
                    if (entered)
                    {
                        // The body may be carried off the waypoint; the arrival it had is spent, so
                        // the paused dwell resumes only on a fresh arrival on the planner's latch.
                        duty.Arrived = false; duty.ArrivedGoal = Vector3.zero;
                        if (log)
                            BlacksiteDiagnostics.Log("Job",
                                $"member={soldier.MemberIndex}, patrol {(phase == BlacksiteGuardPhase.Respond ? "respond" : "yield")} waypoint={guard.Index}/{count}"
                                + $", cause={(contact ? "combat" : soldier.ReplicatedStunned ? "stunned" : "hazard")}"
                                + $", dwellLeft={(guard.Dwelling ? guard.DwellLeft.ToString("0.0") : BlacksiteDiagnosticFormat.None)}.");
                    }
                }
                else if (phase == BlacksiteGuardPhase.Alert)
                {
                    if (entered)
                    {
                        // The scan stands the body for its whole span; the navigator's dwell is the
                        // one stillness owner, and a leg resumes when it lapses.
                        soldier.Navigator.Dwell(guard.AlertUntil - now, "patrolAlert");
                        if (log)
                            BlacksiteDiagnostics.Log("Job",
                                $"member={soldier.MemberIndex}, patrol alert waypoint={guard.Index}/{count}"
                                + $", scan={BlacksitePatrolPolicy.AlertScanSeconds:0.0}"
                                + $", dwellLeft={(guard.Dwelling ? guard.DwellLeft.ToString("0.0") : BlacksiteDiagnosticFormat.None)}"
                                + $", nextLeg={BlacksiteOrderPace.Normal}.");
                    }
                }
                else if (phase == BlacksiteGuardPhase.Dwell && entered)
                {
                    bool resumed = before.Dwelling && before.Index == guard.Index && !guard.HeldOut;
                    if (log)
                        BlacksiteDiagnostics.Log("Job",
                            $"member={soldier.MemberIndex}, patrol dwell waypoint={guard.Index}/{count}"
                            + $", at={BlacksiteDiagnosticFormat.V(waypoint)}, dwell={guard.DwellLeft:0.0}"
                            + $", {(resumed ? "resumed" : guard.HeldOut ? "heldOut" : "fresh")}, look=outward.");
                }
                else if (phase == BlacksiteGuardPhase.Look)
                {
                    float dwelt = before.DwellStartedAt >= 0f ? now - before.DwellStartedAt : 0f;
                    waypoint = guard.Exchanging ? _patrolExchangePoints[id] : _patrolLoop[guard.Index];
                    _manifest._patrolPoints[id] = waypoint;
                    _manifest._patrolSource[id] = PatrolSourceToken(guard.Index, count);
                    duty.NextPlan = now;
                    duty.Arrived = false;
                    if (log)
                        BlacksiteDiagnostics.Log("Job",
                            $"member={soldier.MemberIndex}, patrol next waypoint={guard.Index}/{count}"
                            + $", goal={BlacksiteDiagnosticFormat.V(waypoint)}, dwelt={dwelt:0.0}, pace={guard.LegPace}.");
                }
                if (guard.HeldOut && !before.HeldOut && log)
                    BlacksiteDiagnostics.Log("Job",
                        $"member={soldier.MemberIndex}, patrol held out waypoint={guard.Index}/{count}, cause=everyOtherWaypointHeld.");
                // #1219 E2, #1293 R3: a phase change, or a leg order off the duty's pace, plans next pass.
                if (entered) { duty.NextPlan = now; MarkHeldOutGuards(id); }
                if (BlacksitePatrolPolicy.OrderPace(in guard) != duty.Order.Pace
                    && Vector3.Distance(duty.Order.Destination, waypoint) <= BlacksiteArrivalPolicy.MinDestinationChange
                    && duty.ReviseAt > now)
                { duty.ReviseAt = now; duty.NextPlan = now; }
                // #1293 R3: the patrol's own clocks are this guard's deadlines.
                if (phase == BlacksiteGuardPhase.Dwell && guard.Dwelling && !guard.HeldOut)
                    duty.NextPlan = Mathf.Min(duty.NextPlan, now + guard.DwellLeft);
                else if (phase == BlacksiteGuardPhase.Alert)
                    duty.NextPlan = Mathf.Min(duty.NextPlan, guard.AlertUntil);
                float seconds = guard.Dwelling
                    ? Mathf.Max(.5f, guard.DwellLeft + (phase == BlacksiteGuardPhase.Alert ? guard.AlertUntil - now : 0f))
                    : BlacksitePatrolPolicy.DwellSeconds(seed, guard.Index);
                Vector3 facing = phase == BlacksiteGuardPhase.Alert && alerted
                    ? alert.Position : PatrolOutward(ship.RendezvousPosition, waypoint, soldier.transform.forward);
                Publish(soldier, duty, default(BlacksiteSuccessorSet).WithPost(waypoint, facing, seconds));
            }

            private static int PatrolSeed(BlacksiteSoldierAI soldier)
                => unchecked(soldier.AnimationSeed * 31 + soldier.MemberIndex);

            /// <summary>#1293 R3: a phase change may free a waypoint for a held-out guard.</summary>
            private void MarkHeldOutGuards(ulong except)
            {
                foreach (var pair in _manifest._guardDuty)
                    if (pair.Key != except && pair.Value.HeldOut) _dirty.Mark(pair.Key);
            }

            /// <summary>
            /// A point straight out from the ship through <paramref name="waypoint"/>: what a guard
            /// on a waypoint faces. A waypoint on the rendezvous itself (the single-post fallback)
            /// has no outward, so the member's own heading is used.
            /// </summary>
            private static Vector3 PatrolOutward(Vector3 anchor, Vector3 waypoint, Vector3 forward)
            {
                Vector3 outward = waypoint - anchor; outward.y = 0f;
                if (outward.sqrMagnitude < .01f) { outward = forward; outward.y = 0f; }
                if (outward.sqrMagnitude < .0001f) outward = Vector3.forward;
                return waypoint + outward.normalized * PatrolLookMetres;
            }

            // ---------------------------------------------------------------------------------
            // The facing sweep's target list (…Duties.cs:668-758) verbatim.
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #957 S12A U4. What an idle soldier looks at: the doorways of the room it is standing
            /// in, the one toward the route's next room first and the one the squad came in by last,
            /// then that room's corners. A member the room graph does not place gets the way in and
            /// the way its own formation is facing. The rear guard's list is reversed, so it starts
            /// on the way in rather than the way on.
            /// #874 Q6: a patrolling guard looks OUT from its waypoint instead, never back at the ship.
            /// #874 (2026-09-25): ONE look, the deliberate turn <see cref="BlacksiteSoldierAI.PatrolLookPoint"/>
            /// sizes for the rest-turn clip - the same latched point the squad-order arrival faces,
            /// so the two paths agree. The old -50/outward/+50 sweep turned at 240 deg/s in steps
            /// too small for RestTurn90 and read as fidgeting.
            /// </summary>
            private void BeginIdleSweep(BlacksiteSoldierAI soldier, Duty duty)
            {
                _idleSweepPoints.Clear(); _idleSweepKinds.Clear();
                var ship = _manifest.Ship;
                if (ship != null && soldier.IsShipGuard && soldier.isOutside
                    && _manifest._patrolPoints.TryGetValue(soldier.NetworkObjectId, out Vector3 waypoint))
                {
                    AddIdleSweepTarget(soldier.PatrolLookPoint(waypoint,
                            PatrolOutward(ship.RendezvousPosition, waypoint, soldier.transform.forward)),
                        BlacksiteSoldierAI.FacingSweepCornerKind);
                    soldier.BeginFacingSweep(_idleSweepPoints, _idleSweepKinds);
                    return;
                }
                var planner = _manifest.RoutePlanner;
                int tile = planner != null && !soldier.isOutside
                    ? planner.TileOf(soldier.transform.position) : -1;
                if (tile >= 0)
                {
                    var doorways = planner.DoorwayTargets(tile);
                    Vector3 hop = Vector3.zero;
                    bool hasHop = planner.NextRoom >= 0
                        && TryHopDoorway(planner, tile, planner.LeadWaypoint, out hop);
                    // The way in only counts when it is a doorway of THIS room.
                    bool hasEntry = _manifest.TryEntryDoorway(out Vector3 entry) && IsOwnDoorway(doorways, entry);
                    if (hasHop) AddIdleSweepTarget(hop, BlacksiteSoldierAI.FacingSweepDoorwayKind);
                    for (int i = 0; i < doorways.Count; i++)
                    {
                        if (hasHop && NearIdleSweepTarget(doorways[i], hop)) continue;
                        if (hasEntry && NearIdleSweepTarget(doorways[i], entry)) continue;
                        AddIdleSweepTarget(doorways[i], BlacksiteSoldierAI.FacingSweepDoorwayKind);
                    }
                    // Last, and only when it is not also the way on: a room with one doorway is a
                    // dead end, and the way in is the only thing there is to watch.
                    if (hasEntry && !(hasHop && NearIdleSweepTarget(entry, hop)))
                        AddIdleSweepTarget(entry, BlacksiteSoldierAI.FacingSweepDoorwayKind);
                    var corners = planner.CornerTargets(tile);
                    for (int i = 0; i < corners.Count; i++)
                        AddIdleSweepTarget(corners[i], BlacksiteSoldierAI.FacingSweepCornerKind);
                }
                else
                {
                    Vector3 crossing = soldier.isOutside
                        ? _manifest._mission.SquadDirector.OutsideMainEntrance
                        : _manifest._mission.SquadDirector.InsideMainEntrance;
                    AddIdleSweepTarget(crossing, BlacksiteSoldierAI.FacingSweepEntranceKind);
                    // FacePoint reads the direction and nothing else, so the distance below only has
                    // to clear its own 0.01 m dead zone; LaneClearance is reused rather than a new
                    // constant invented for a number that cannot matter.
                    Vector3 forward = duty.Order.SquadOrder.Forward;
                    if (forward.sqrMagnitude > 0.001f)
                        AddIdleSweepTarget(
                            soldier.transform.position + forward.normalized * BlacksiteIdlePolicy.LaneClearance,
                            BlacksiteSoldierAI.FacingSweepEntranceKind);
                }
                if (RouteRole(soldier) == BlacksiteSoldierTacticalRole.RearGuard)
                { _idleSweepPoints.Reverse(); _idleSweepKinds.Reverse(); }
                soldier.BeginFacingSweep(_idleSweepPoints, _idleSweepKinds);
            }

            private static bool IsOwnDoorway(IReadOnlyList<Vector3> doorways, Vector3 point)
            {
                for (int i = 0; i < doorways.Count; i++)
                    if (NearIdleSweepTarget(doorways[i], point)) return true;
                return false;
            }

            private void AddIdleSweepTarget(Vector3 point, byte kind)
            {
                for (int i = 0; i < _idleSweepPoints.Count; i++)
                    if (NearIdleSweepTarget(_idleSweepPoints[i], point)) return;
                _idleSweepPoints.Add(point); _idleSweepKinds.Add(kind);
            }

            // Two doorway records of the same opening are the same world position on both sides, so
            // this is an identity test, not a tolerance to tune.
            private static bool NearIdleSweepTarget(Vector3 a, Vector3 b)
                => Vector3.SqrMagnitude(a - b) <= 0.0025f;

            /// <summary>This member's place in the squad's formation, by the same rule #955 uses.</summary>
            private BlacksiteSoldierTacticalRole RouteRole(BlacksiteSoldierAI soldier)
            {
                int slot = _manifest._routeRaiders.IndexOf(soldier);
                return slot <= 0
                    ? BlacksiteSoldierTacticalRole.Leader
                    : BlacksiteFormationPlanner.ResolveRole(slot, Mathf.Max(2, _manifest._routeRaiders.Count));
            }
        }
    }
}
