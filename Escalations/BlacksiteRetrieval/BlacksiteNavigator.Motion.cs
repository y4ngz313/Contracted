using System;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        internal sealed partial class BlacksiteNavigator : IDisposable
        {
            // #874 S4.0a: the observation surface behind [UAT][Motion]. Nothing in this file
            // decides anything; every member is written by the existing owner at the moment it
            // already acts, and read only when diagnostics are on.

            // The destination actually handed to the agent, after ResolveNavMeshPosition, the
            // abandonment detour, ClaimDestination and the ground-route substitution. The
            // 2026-09-09 log had no field for this, so claim-driven goal oscillation could not be
            // proven from a log at all.
            private Vector3 _issuedDestination;
            private bool _hasIssuedDestination;
            private string _issuer = "none";
            private float _issuedAt = float.NegativeInfinity;
            // Horizontal metres between the requested destination and the claim-adjusted one.
            private float _claimOffset;
            // #874 P-S1: the GOAL the issued destination is a leg of - the request's own position -
            // and which substitution the issued point is ("groundRoute", "interim", "claim",
            // "follow", "interaction"), or null when the issued point is the goal itself. Written
            // with the issued destination; the identical-issue gate and [UAT][Motion] remaining= read them.
            private Vector3 _issuedGoal;
            private string _issuedLeg;
            // #874 Q1: the completion test of the goal the last accepted request asked for - its
            // point, its radius and the stop distance a leg of it settles at. Written by the motion
            // owner on every request before anything below it may substitute a point, and read by
            // every substitution (the coordinator's and the director's claims, a follow leg) and by
            // the arrival verdict, so all of them judge the one test the request carried.
            private Vector3 _goalTestCentre;
            private float _goalTestRadius, _goalTestStop;
            private bool _hasGoalTest;
            private static readonly BlacksiteChangeGate SubstitutionGate = new BlacksiteChangeGate();

            // #956 S12A U3: the term of BlacksiteMotionPolicy.CommandedSpeed that bound agent.speed
            // at the moment ApplyAgentMoveSpeed wrote it, so the printed owner is the one that was
            // actually in force rather than one re-derived from the number a frame later.
            private BlacksiteSpeedOwner _commandedSpeedOwner = BlacksiteSpeedOwner.None;

            // #955 S12A U2: identical re-issues this member's issue gate refused, and the room
            // advance that lets the next one through. The counter is observation only and is
            // printed as dropped= on the [UAT][Motion] line; the flag is written by the duty board
            // from the route planner's own advanced event and consumed by the next gated issue.
            private int _droppedIssues;
            private bool _roomAdvancePending;

            internal int DroppedIssues => _droppedIssues;
            internal bool RoomAdvancePending => _roomAdvancePending;

            /// <summary>
            /// #955 S12A U2: the squad advanced a room, so this member's next gated destination is
            /// worth issuing however close it lands to the last one. Set by the duty board on the
            /// tick <c>BlacksiteSquadRoutePlanner.Observe</c> returns true.
            /// </summary>
            internal void NoteRoomAdvance() => _roomAdvancePending = true;

            internal bool HasIssuedDestination => _hasIssuedDestination;
            internal Vector3 IssuedDestination => _issuedDestination;
            /// <summary>#874 S4: the planner's goal the issued destination serves (not the leg).</summary>
            internal Vector3 IssuedGoal => _issuedGoal;
            /// <summary>
            /// #1293 R2: the planner goal the progress ledger opens a commitment on - the last
            /// accepted request's own goal (its goal test), which no raw step, leg or issuer flip
            /// rewrites; the issued goal before any request.
            /// </summary>
            internal Vector3 CommitmentGoal => _hasGoalTest ? _goalTestCentre : _issuedGoal;
            internal string Issuer => _issuer;
            internal float IssuedAge => float.IsNegativeInfinity(_issuedAt) ? -1f : Time.time - _issuedAt;
            /// <summary>#1293 R2: where the route in force ends (the issued point with no path); the ledger's route identity.</summary>
            internal Vector3 RouteEnd => AgentBound && _agent.hasPath ? _agent.pathEndPosition : _issuedDestination;
            /// <summary>#1293 R2: how near its point the leg in force counts as walked - the contract's own advance radius (1.10 m on the ground route).</summary>
            internal float LegReach => ArrivalContract.AdvanceRadius;
            internal float ClaimOffset => _claimOffset;

            private bool AgentBound => _agent != null && _agent.enabled && _agent.isOnNavMesh;
            internal float DesiredSpeed => _agent != null ? _agent.speed : 0f;
            internal float AgentVelocity => AgentBound ? _agent.velocity.magnitude : 0f;
            /// <summary>
            /// #1271 S1: metres the body still has to walk on the path in force, always finite; -1
            /// with no path. It is the agent's own number when that is finite. Unity reports Infinity
            /// (or NaN) when the path's end lies past an off-mesh link or past the agent's corner
            /// look-ahead, which is every long walk of the 2026-09-29 log, and then it is the sum from
            /// the body along the path's corners - a link is the straight segment between its two.
            /// </summary>
            internal float RemainingDistance
            {
                get
                {
                    if (!AgentBound || !_agent.hasPath) return -1f;
                    float reported = _agent.remainingDistance;
                    return float.IsNaN(reported) || float.IsInfinity(reported) ? CornerRemaining() : reported;
                }
            }

            // #1271 S1: the corners of the path in force, in one reused buffer. NavMeshAgent.path
            // allocates a NavMeshPath on every read, so the buffer is refreshed only when the path
            // changes - a new issue, or an end that moved - never per frame. Grows once, like the
            // combat controller's cover-corner buffer.
            private Vector3[] _routeCorners = new Vector3[16];
            private int _routeCornerCount;
            private float _routeCornersIssuedAt = float.NaN;
            private Vector3 _routeCornersEnd;

            private int ReadRouteCorners()
            {
                NavMeshPath path = _agent.path;
                _routeCornerCount = path.GetCornersNonAlloc(_routeCorners);
                while (_routeCornerCount >= _routeCorners.Length)
                {
                    _routeCorners = new Vector3[_routeCorners.Length * 2];
                    _routeCornerCount = path.GetCornersNonAlloc(_routeCorners);
                }
                _routeCornersIssuedAt = _issuedAt;
                _routeCornersEnd = _agent.pathEndPosition;
                return _routeCornerCount;
            }

            // From the body to the corner the agent is steering for, then corner to corner to the end.
            // Never refreshed while the new path is pending: the old path is still on the agent and
            // would be kept under the new issue's key.
            private float CornerRemaining()
            {
                if (RouteCornersStale && _agent.pathPending) return Vector3.Distance(BodyFloor, _agent.pathEndPosition);
                int next = SteeredCorner();
                if (next == 0) return Vector3.Distance(BodyFloor, _agent.pathEndPosition);
                float remaining = Vector3.Distance(BodyFloor, _routeCorners[next]);
                for (int i = next + 1; i < _routeCornerCount; i++)
                    remaining += Vector3.Distance(_routeCorners[i - 1], _routeCorners[i]);
                return remaining;
            }

            private bool RouteCornersStale => _routeCornersIssuedAt != _issuedAt
                || Vector3.Distance(_agent.pathEndPosition, _routeCornersEnd) > BlacksiteArrivalPolicy.MinDestinationChange;

            // The in-force corner the agent is steering for (buffer refreshed when stale), or 0 for a
            // route of fewer than two corners. #1305 H (C3): the route ahead the disc admission reads.
            // Callers do not ask while a new path is pending.
            private int SteeredCorner()
            {
                if (RouteCornersStale) ReadRouteCorners();
                if (_routeCornerCount < 2) return 0;
                Vector3 steer = _agent.steeringTarget;
                int next = 1;
                for (int i = 2; i < _routeCornerCount; i++)
                    if ((_routeCorners[i] - steer).sqrMagnitude < (_routeCorners[next] - steer).sqrMagnitude) next = i;
                return next;
            }

            // The body's floor point: the agent's own offset below the transform.
            private Vector3 BodyFloor
                => _soldier.transform.position - _soldier.transform.up * (_agent != null ? _agent.baseOffset : 0f);

            internal BlacksitePathState PathState
            {
                get
                {
                    if (!AgentBound) return BlacksitePathState.None;
                    if (_agent.pathPending) return BlacksitePathState.Pending;
                    if (!_agent.hasPath) return BlacksitePathState.None;
                    switch (_agent.pathStatus)
                    {
                        case NavMeshPathStatus.PathComplete: return BlacksitePathState.Complete;
                        case NavMeshPathStatus.PathPartial: return BlacksitePathState.Partial;
                        default: return BlacksitePathState.Invalid;
                    }
                }
            }

            /// <summary>
            /// #1219 C1: the one computation of how far the path in force ends short of the point
            /// it serves. Partial: 3D metres from the path's end to the issued destination. None or
            /// Invalid with an issue standing: 3D metres from the body's floor point to the goal
            /// (a consumed path at arrival reads near zero; a goal on another storey does not).
            /// Complete, Pending or no issue: -1, not measured.
            /// </summary>
            internal float PathEndGap => _hasIssuedDestination ? PathEndGapOf(PathState) : -1f;

            // PathEndGap for a path state already read.
            private float PathEndGapOf(BlacksitePathState path)
            {
                if (!_hasIssuedDestination) return -1f;
                switch (path)
                {
                    case BlacksitePathState.Partial:
                        return Vector3.Distance(_agent.pathEndPosition, _issuedDestination);
                    case BlacksitePathState.None:
                    case BlacksitePathState.Invalid:
                        return Vector3.Distance(BodyFloor, _issuedGoal);
                    default:
                        return -1f;
                }
            }

            /// <summary>#1219 C1: the path in force ends short of its point (BlacksiteMotionPolicy.PathStopsShort).</summary>
            internal bool PathStopsShort => BlacksiteMotionPolicy.PathStopsShort(PathState, PathEndGap, ArrivalTolerance);
            /// <summary>#1233 S5: the body rests where a route that stops short of its goal ends, or (#1271 S1) has no path left (BlacksiteMotionPolicy.StandsAtRouteEnd).</summary>
            internal bool StandsAtRouteEnd
                => BlacksiteMotionPolicy.StandsAtRouteEnd(PathState, PathEndGap, RemainingDistance, StopDistance, ArrivalTolerance);

            // Called at every point that actually hands a destination to the agent; the caller
            // names the goal it is a leg of right after (NoteIssuedGoal).
            private void RecordIssuedDestination(Vector3 issued, string issuer)
            {
                _issuedDestination = issued;
                _hasIssuedDestination = true;
                _issuer = string.IsNullOrWhiteSpace(issuer) ? "raw" : issuer;
                _issuedAt = Time.time;
                // #1015 S1 defect fix 2: the room advance is one event, and it is spent by the
                // gated destination that actually reached the agent - never by a gated request the
                // navigator went on to refuse.
                if (IsGatedIssuer(_issuer)) _roomAdvancePending = false;
                // #956 S12A U3: the commanded speed and braking rule belong to the GOAL, so a speed
                // lerped for a goal 0.5 m away never survives onto the next; every issue path calls this.
                ApplyAgentMoveSpeed();
            }

            /// <summary>
            /// #874 P-S1: the goal the destination just issued is a leg of and the leg's name.
            /// Called immediately after <see cref="RecordIssuedDestination"/>.
            /// </summary>
            internal void NoteIssuedGoal(Vector3 goal, string leg)
            {
                _issuedGoal = goal;
                _issuedLeg = string.IsNullOrEmpty(leg) ? null : leg;
            }

            /// <summary>
            /// #874 P-S2: the Follow request. The motion owner re-derives the goal from the live
            /// anchor as it walks; the position carried here is only the anchor when asked.
            /// </summary>
            internal BlacksiteMotionVerdict RequestFollow(in BlacksiteFollow follow, string issuer)
                => _motion.Request(new BlacksiteGoal(follow.Anchor.transform.position,
                    validatePath: true, groundRouteOutbound: null, issuer: issuer, follow: follow));

            /// <summary>#874 Q1: the motion owner names the goal test every substitution below it must pass.</summary>
            internal void NoteGoalTest(Vector3 goal, float radius, float stopDistance)
            {
                _goalTestCentre = goal;
                _goalTestRadius = radius;
                _goalTestStop = stopDistance;
                _hasGoalTest = true;
            }

            private static float Flat(Vector3 a, Vector3 b) { a.y = b.y; return Vector3.Distance(a, b); }

            /// <summary>
            /// #874 Q1: how far a claim may move <paramref name="leg"/> and still leave its member
            /// inside the goal test. With no test in force the table's widest ring applies.
            /// </summary>
            internal float ClaimAllowance(Vector3 leg)
                => _hasGoalTest
                    ? BlacksiteArrivalPolicy.ClaimAllowance(_goalTestRadius, _goalTestStop, Flat(leg, _goalTestCentre))
                    : BlacksiteArrivalPolicy.MaxClaimOffset;

            /// <summary>#874 Q1: a substituted standing point passes the goal test in force.</summary>
            internal bool SubstituteSatisfiesGoal(Vector3 substitute)
                => !_hasGoalTest
                   || BlacksiteArrivalPolicy.SubstituteSatisfies(Flat(substitute, _goalTestCentre), _goalTestRadius, _goalTestStop);

            /// <summary>
            /// #874 Q1: the member itself is inside the goal test in force - the one arrival.
            /// #1219 C1: and path-true, as HasArrived is - a path ending short of its point is not it.
            /// </summary>
            internal bool PassesGoalTest()
                => _hasGoalTest && Flat(_soldier.transform.position, _goalTestCentre) <= _goalTestRadius && !PathStopsShort;

            /// <summary>
            /// #874 Q1: a substitution that would have left the member outside its goal's test was
            /// refused. One [UAT][Motion] line per member, substitution kind and goal: the refusal
            /// is answered by handing back the requested point, so it repeats every refresh until the
            /// goal changes and only its first occurrence is news.
            /// </summary>
            internal void ReportSubstitutionRefused(string kind, Vector3 requested, Vector3 substitute, float allowed)
            {
                if (!BlacksiteDiagnostics.Enabled) return;
                Vector3 goal = _hasGoalTest ? _goalTestCentre : requested;
                if (!SubstitutionGate.ShouldReport(MotionRequestKey, "substitutionRefused",
                        $"{kind}|{goal.x:0}|{goal.y:0}|{goal.z:0}", out _)) return;
                string test = _hasGoalTest
                    ? $"within {_goalTestRadius:0.00}m of goal, stop={_goalTestStop:0.00}"
                    : $"within {allowed:0.00}m of requested";
                BlacksiteDiagnostics.Log("Motion",
                    $"substitutionRefused member={_soldier.MemberIndex}, kind={kind}, goal={BlacksiteDiagnosticFormat.V(goal)}"
                    + $", requested={BlacksiteDiagnosticFormat.V(requested)}, substitute={BlacksiteDiagnosticFormat.V(substitute)}"
                    + $", distance={Flat(substitute, goal):0.00}, allowed={allowed:0.00}, test={test}");
            }

            // #874: a destination the caller drops (InvalidateDestination, #1305 R18: a withdrawn goal or an authored warp,
            // which the next issue sets again) or resolved in the world the member left (NoteWorldChanged) is evidence of nothing
            // here - not of an identical re-issue for DropIdenticalIssue to fold against, an issue age or a claim offset - so it is forgotten.
            private void ForgetIssuedDestination()
            {
                _hasIssuedDestination = false;
                _issuer = "none";
                _issuedAt = float.NegativeInfinity;
                _claimOffset = 0f;
                _issuedLeg = null;
                // #874 Q1: and the goal test, which tested that goal.
                _hasGoalTest = false;
            }

            internal BlacksiteMotionSample BuildMotionSample(float measuredSpeed)
            {
                // #874 S4.2: the arrival contract is read once and carried in the sample, so the
                // classifier and the printed line quote one computation.
                var contract = ArrivalContract;
                // #874 P-S1: the leg in force and the distance to the goal it serves. Remaining is
                // the agent's own distance to the issued point; with a leg in force the goal is the
                // straight line from that point further on. #874 S4: an agent that has consumed its
                // path at arrival still has a goal, measured horizontally from the body.
                float remaining = RemainingDistance;
                string leg = _hasIssuedDestination ? _issuedLeg : null;
                float goalRemaining = remaining >= 0f
                    ? (leg != null ? remaining + Vector3.Distance(_issuedDestination, _issuedGoal) : remaining)
                    : _hasIssuedDestination ? Flat(_soldier.transform.position, _issuedGoal) : remaining;
                return new BlacksiteMotionSample
                {
                    ArrivalFloor = contract.ArrivalFloor,
                    ArrivalTolerance = contract.ArrivalTolerance,
                    Speed = measuredSpeed,
                    BodyWriter = _bodyWriter,
                    DwellUntil = DwellUntil,
                    Now = Time.time,
                    InCombat = _soldier.InCombat,
                    HasGoal = _hasLastDestination || _hasIssuedDestination,
                    Path = PathState,
                    Remaining = remaining,
                    Leg = leg,
                    GoalRemaining = goalRemaining,
                    PathEndGap = PathEndGap,
                    StoppingDistance = contract.StopDistance,
                    StoppingDistanceOwner = contract.StopOwner,
                    // #956 S12A U3: the speed half of the same frame. The Unity reads are here and
                    // the arbitration is in BlacksiteMotionPolicy, so the regression replays the
                    // same rows the log prints.
                    CommandedSpeed = DesiredSpeed,
                    SpeedOwner = _commandedSpeedOwner,
                    AgentVelocity = AgentVelocity,
                    Acceleration = _agent != null ? _agent.acceleration : 0f,
                    AutoBraking = _agent != null && _agent.autoBraking,
                    AgentStopped = AgentBound && _agent.isStopped,
                    AgentBound = AgentBound,
                    PathPending = !AgentBound || _agent.pathPending || !_agent.hasPath,
                    OffMeshLink = AgentBound && _agent.isOnOffMeshLink,
                    // #874 P-S2: the follow half. The gap and the anchor's motion are the owner's
                    // own sampled state, so the classifier and the pace quote one computation.
                    Follow = _motion.IsFollowing,
                    FollowSource = _motion.IsFollowing ? _motion.ActiveFollow.Source : null,
                    AnchorMoving = _motion.FollowAnchorMoving,
                    FollowGap = _motion.IsFollowing ? _motion.FollowGap : -1f,
                    FollowBand = _motion.IsFollowing ? _motion.ActiveFollow.Band : 0f,
                    FollowOffPath = _motion.IsFollowing && _motion.FollowOffPath
                };
            }

            /// <summary>
            /// The navigation half of the [UAT][Motion] line, in the exact order and spelling the
            /// S4.0 diagnostic contract specifies. Built only under BlacksiteDiagnostics.Enabled.
            /// </summary>
            internal string MotionDetails(BlacksiteMotionSample sample, Vector3? goal)
            {
                // #874 P-S2: a Follow's goal is the anchor plus the offset NOW, not the point the
                // planner stamped when it issued the follow.
                if (_motion.TryGetFollowGoal(out Vector3 followGoal)) goal = followGoal;
                return $"movementOwner={MovementOwner}"
                    + $", issued={(_hasIssuedDestination ? _issuedDestination.ToString("0.00") : "none")}"
                    + $", issuer={_issuer}"
                    + $", issuedAge={IssuedAge:0.00}"
                    + $", goal={(goal.HasValue ? goal.Value.ToString("0.00") : "none")}"
                    // #874 P-S1: the distance to the GOAL, same key and position. It equals the
                    // agent's remaining distance whenever no leg is in force; legRemaining= at the end
                    // of the line carries the agent's own number.
                    + $", remaining={sample.GoalRemaining:0.00}"
                    + $", path={BlacksiteMotionPolicy.PathToken(sample.Path)}"
                    + $", stop={sample.StoppingDistance:0.00}"
                    + $", stopOwner={sample.StoppingDistanceOwner}"
                    + $", arrivalFloor={sample.ArrivalFloor:0.00}"
                    // #874 S4.2: the tolerance the ordered contract actually puts in force, not
                    // the vanished Mathf.Max(1.8, order.ArrivalTolerance) the board used to apply.
                    + $", tolerance={sample.ArrivalTolerance:0.00}"
                    + $", claimOffset={_claimOffset:0.00}";
            }

            /// <summary>
            /// #874 P-S1: the leg half of the [UAT][Motion] line, appended after waitFor= so every key
            /// a parser already reads keeps its name and position. <c>leg=</c> names the substitution
            /// the issued point is (none when it is the goal itself) and <c>legRemaining=</c> is the
            /// agent's own distance to it.
            /// </summary>
            internal static string MotionLegDetails(BlacksiteMotionSample sample)
                => $", leg={(BlacksiteMotionPolicy.LegInForce(sample.Leg) ? sample.Leg : BlacksiteDiagnosticFormat.None)}"
                   + $", legRemaining={sample.Remaining:0.00}"
                   // #874 P-S2: a Follow names what its goal is derived from and the band it holds in.
                   + (sample.Follow
                       ? $", goalSource={sample.FollowSource ?? BlacksiteDiagnosticFormat.None}, band={sample.FollowBand:0.00}"
                       : string.Empty);

            /// <summary>
            /// #955 S12A U2: the issue gate. A destination is issued when it is worth issuing.
            ///
            /// The 2026-09-10 March run gave members 1 to 3 seventy per cent byte-identical
            /// re-issues at 36 to 45 a minute. Two things let them through, and neither is the
            /// distance test itself:
            ///
            ///   1. The 1 m <c>reissueThreshold</c> in SetDestinationThrottled returns early only
            ///      while <c>_agent.hasPath</c>. An arrived agent has consumed its path
            ///      (path=none on 30.9 % of the run's samples, 92.8 % of those with agentVelocity
            ///      under 0.2 m/s), so the identical point goes straight through. This gate
            ///      therefore does not look at hasPath at all.
            ///   2. <c>TrySetSmartDestination</c>, the issuer=interaction path, runs BEFORE that
            ///      threshold and is never measured against the last issued point. This gate runs
            ///      before it.
            ///
            /// What it covers, and what keeps today's thresholds: the issuers that hand out a place
            /// to walk to (<c>order</c>, <c>duty:*</c>, <c>interaction</c>) are gated; a ground
            /// route corridor waypoint (<c>groundRoute</c>, 0.01 m), a combat or melee lane
            /// (<c>combat</c>, <c>melee</c>), a raw point (<c>order:*</c>,
            /// <c>raw</c>) and any order carrying a reach contract are not, because each of those
            /// is a precise point whose whole purpose is to be re-approached as it moves.
            ///
            /// #874 P-S1: "identical" is decided against the GOAL
            /// (<see cref="BlacksiteMotionPolicy.IsIdenticalIssue"/>): the same goal, the same leg,
            /// and not a leg the member has already reached short of that goal. On 2026-09-23 the
            /// last leg was all this compared, so a guard standing on a leashed ring point had every
            /// request for the ladder mount 30.9 m away dropped as identical. The goal comparison is
            /// also what retires #1015 S1's IssuePredatesLease clause here: a goal that moved under
            /// a lease is a different goal from the one the carried destination was issued for.
            ///
            /// The alternative the cross-cutting rule requires: a room advance releases the gate
            /// outright, and a member whose route ended short of its goal (<c>routeEnded</c>) is
            /// never folded. #1293 R2: no leg age re-validates it; a member whose walk stops
            /// progressing is the progress ledger's miss.
            /// </summary>
            private bool DropIdenticalIssue(Vector3 goal, Vector3 resolved, string issuer, bool routeEnded)
            {
                if (!_hasIssuedDestination || !IsGatedIssuer(issuer)) return false;
                // A reach contract owns precise points of its own.
                if (_approach.HasValue || _combatApproach.HasValue)
                    return false;
                // #1015 S1 defect fix 2: the advance is READ here and consumed where a gated
                // destination actually reaches the agent (RecordIssuedDestination). It used to be
                // consumed on the way through this gate, so an advance spent on a request that was
                // then refused downstream - by the path validation, the hazard footprint, the
                // abandonment record or the sameGoal hysteresis - was lost without any issue ever
                // benefiting from it. The gate's position and thresholds are unchanged.
                if (_roomAdvancePending) return false;
                if (!BlacksiteMotionPolicy.IsIdenticalIssue(
                        Vector3.Distance(goal, _issuedGoal),
                        Vector3.Distance(resolved, _issuedDestination),
                        BlacksiteMotionPolicy.LegInForce(_issuedLeg),
                        HasArrived(_issuedDestination), HasArrived(goal), routeEnded))
                    return false;
                _droppedIssues++;
                return true;
            }

            // The closed set of issuers the gate owns. Everything else keeps the threshold it
            // already had.
            private static bool IsGatedIssuer(string issuer)
                => string.Equals(issuer, "order", StringComparison.Ordinal)
                   || string.Equals(issuer, "interaction", StringComparison.Ordinal)
                   || (issuer != null && issuer.StartsWith("duty:", StringComparison.Ordinal));
        }
    }
}
