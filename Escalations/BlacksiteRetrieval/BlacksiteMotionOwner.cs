using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        // #1015 S1: both types are nested INSIDE BlacksiteNavigator. C# accessibility flows inward
        // only - a sibling nested type cannot see the navigator's private fields - and the whole
        // point of this stage is that the gate ladder keeps reading the navigator's own
        // abandonment, hazard, ground-route and interaction state directly instead of growing an
        // accessor per field.
        internal sealed partial class BlacksiteNavigator
        {
        /// <summary>
        /// #1015 S1: one request for a place to be: the arguments <c>SetDestinationThrottled</c>
        /// takes.
        /// </summary>
        internal readonly struct BlacksiteGoal
        {
            internal readonly Vector3 Position;
            /// <summary>The caller's own issuer tag, unchanged.</summary>
            internal readonly string Issuer;
            internal readonly bool ValidatePath;
            internal readonly bool? GroundRouteOutbound;
            /// <summary>#874 P-S2: set for a Follow request; <see cref="Position"/> is then only the
            /// goal when asked - the owner re-derives it from the live anchor as it walks.</summary>
            internal readonly BlacksiteFollow Follow;
            /// <summary>#1305 G2 (R16): the caller names the goal in the other world, so an outbound
            /// corridor is walked to its end and the route takes the doorway there.</summary>
            internal readonly bool GoalAcross;

            internal BlacksiteGoal(
                Vector3 position,
                bool validatePath,
                bool? groundRouteOutbound,
                string issuer,
                BlacksiteFollow follow = default,
                bool goalAcross = false)
            {
                Position = position;
                ValidatePath = validatePath;
                GroundRouteOutbound = groundRouteOutbound;
                Issuer = issuer;
                Follow = follow;
                GoalAcross = goalAcross;
            }
        }

        /// <summary>
        /// #874 P-S2: the Follow request kind - a moving anchor, a slot and a band.
        ///
        /// The motion owner re-derives the goal from the anchor's current position on every path
        /// refresh; the point it hands the agent is a LEG of that goal
        /// (<see cref="BlacksiteMotionPolicy.FollowLeg"/>), led ahead of it while the anchor moves so
        /// the member never arrives and stops on a point the anchor has left. The 2026-09-23 R3
        /// escorts were given a sampled slot every ~2 s instead, walked it, stopped arrived:default
        /// and stood until the next one (member 1: 28 arrivals in 72 s).
        ///
        /// #874 Q2: the request does not carry the slot. The travel-frame offset the planner sampled
        /// once and the motion owner rotated with the anchor's heading projected round 1's slots
        /// 2-3 m past the apparatus as the holder walked in (the far corner). #1271 S2: the slot is
        /// a point on the route behind the anchor, <see cref="Rank"/> column spacings back
        /// (<see cref="BlacksiteMotionPolicy.TrailPoint"/>), re-derived on every refresh.
        /// </summary>
        internal readonly struct BlacksiteFollow
        {
            internal readonly BlacksiteSoldierAI Anchor;
            /// <summary>#874 Q2: this follower's place among the anchor's followers; how far back along the route its slot stands.</summary>
            internal readonly int Rank;
            internal readonly float Band;
            /// <summary>The [UAT][Motion] goalSource= token, e.g. <c>carrier:0</c> or <c>member:2</c>.</summary>
            internal readonly string Source;
            /// <summary>#874 Q2: the anchor carries the objective this follow escorts; dropping it ends the follow.</summary>
            internal readonly bool CarriesObjective;

            internal BlacksiteFollow(BlacksiteSoldierAI anchor, int rank, float band, string source, bool carriesObjective)
            {
                Anchor = anchor;
                Rank = rank;
                Band = band;
                Source = source;
                CarriesObjective = carriesObjective;
            }

            internal bool HasValue => Anchor != null;

            /// <summary>The same anchor, the same place among its followers, escorting the same thing: one follow.</summary>
            internal bool SameAs(in BlacksiteFollow other)
                => ReferenceEquals(Anchor, other.Anchor) && Rank == other.Rank && CarriesObjective == other.CarriesObjective;

            /// <summary>A flat unit heading; a degenerate one reads as world forward.</summary>
            internal static Vector3 Heading(Vector3 forward)
            {
                forward.y = 0f;
                return forward.sqrMagnitude < .0001f ? Vector3.forward : forward.normalized;
            }
        }

        /// <summary>
        /// #1015 S1: the ONE writer of a soldier's NavMeshAgent. Everything that touches a soldier
        /// agent is in this file. #874 S4: every deliberate stillness is the navigator's one dwell,
        /// and a crossing or a seat owns the body outright (<see cref="BlacksiteBodyWriter"/>).
        ///
        /// It is nested INSIDE <see cref="BlacksiteNavigator"/>, which is what lets the request
        /// keep reading the navigator's own ground-route, flood and interaction state directly
        /// instead of growing an accessor per field.
        /// </summary>
        internal sealed class BlacksiteMotionOwner
        {
            private readonly BlacksiteSoldierAI _soldier;
            private readonly BlacksiteNavigator _nav;

            // #874 A5: the route generation - the identity of the retained goal, host-only and
            // monotonic. It moves on a world change and when the goal's OWNER changes: a different
            // owning issuer, or a new duty attempt the board opened. A point moving under the same
            // owner, a reissue and every raw step leave it alone.
            private long _routeGeneration;
            private string _routeOwner;
            private int _dutyAttempt, _routeDutyAttempt;
            // #1015 S1 (review Y3c): monotonic, so ExecuteSquadOrder can tell whether the order it
            // just ran produced a verdict of its own or is looking at the previous one.
            private long _verdictSeq;
            private BlacksiteMotionVerdict _lastVerdict =
                new BlacksiteMotionVerdict(BlacksiteMotionOutcome.Blocked, BlacksiteMotionResult.Dropped, "none");
            // #1293 R3: the request the ladder last answered Moving or Arrived (TryAnswerHeld), its
            // verdict number and flood step. Forgotten by every write that changes the leg.
            private BlacksiteGoal _held;
            private bool _hasHeld;
            private long _heldSeq;
            private int _heldFloodStep = int.MinValue;
            // #1293 R3: the route facts a route end was last asked again against - the decision, the
            // path state, where the route ended and the goal it was issued for - so a route end is
            // re-asked once per decision or per change of those facts, never on every frame of a
            // per-frame continuation, and a new goal's route end is a fact of its own.
            private int _routeEndAskedDecision = int.MinValue;
            private BlacksitePathState _routeEndAskedPath;
            private Vector3 _routeEndAskedAt, _routeEndAskedGoal;
            // #1305 H (C3): the live hazard disc refusing the goal this member was last asked for -
            // the one per-member fact an exit event is detected against (ObserveHazardDiscs). It
            // replaces the soldier's hazard footprint record; it has no lifetime and no clock.
            private BlacksiteHazardFootprint _hazardBlock;

            /// <summary>#1305 H (C3): the disc this member waits facing, or none.</summary>
            internal BlacksiteHazardFootprint HazardBlock => _hazardBlock;

            internal BlacksiteMotionOwner(BlacksiteSoldierAI soldier, BlacksiteNavigator navigator)
            { _soldier = soldier; _nav = navigator; }

            private NavMeshAgent Agent => _nav._agent;

            internal BlacksiteMotionVerdict LastVerdict => _lastVerdict;
            internal long VerdictSeq => _verdictSeq;
            internal BlacksiteMotionOutcome LastOutcome => _lastVerdict.Outcome;
            internal long RouteGeneration => _routeGeneration;

            // ---------------------------------------------------------------------------------
            // #874 P-S2: the Follow in force and the anchor sampling behind it.
            //
            // Host-only like everything in this type. The follow is taken by a Follow request and
            // dropped with the goal it belongs to: a different owning goal adopted (NoteGoal), a
            // world change (DiscardGoals), Clear, or (#874 Q2) the anchor leaving the follow - gone,
            // dead, seated, boarding, in another world, or no longer carrying what was escorted
            // (BlacksiteMotionPolicy.FollowEndReason), sampled from Tick. #1293 R3: the sample re-asks
            // the leg once the anchor has walked FollowReissueMetres or started or stopped.
            // ---------------------------------------------------------------------------------

            private BlacksiteFollow _follow;
            // #1293 R3: the Follow request RefreshFollow re-asks, and where its leg was last derived.
            private BlacksiteGoal _followRequest;
            private Vector3 _followDerivedAnchorAt;
            private Vector3 _followAnchorAt;
            private float _followSampledAt;
            private float _followAnchorSpeed;
            private Vector3 _followHeading = Vector3.forward;
            private bool _followAnchorWasMoving;
            // #1271 S2: the goal as last derived - the slot on the route behind the anchor
            // (FollowSlot), re-derived on every issue of the follow.
            private Vector3 _followGoal;
            // The last re-derived leg, and the resolved point last issued for it, so an unmoved leg
            // is the same leg whether or not the agent still carries a path to it.
            private Vector3 _followLeg, _followIssuedLeg;
            private bool _hasFollowLeg, _hasFollowIssuedLeg;
            // Faster than any body walks; a displacement at this rate is a warp, not travel.
            private const float FollowWarpSpeed = 20f;

            internal bool IsFollowing => _follow.HasValue;
            internal BlacksiteFollow ActiveFollow => _follow;
            internal bool FollowAnchorMoving => _follow.HasValue && BlacksiteMotionPolicy.AnchorMoving(_followAnchorSpeed);

            /// <summary>The follow goal now: the slot last derived on the route behind the live anchor.</summary>
            internal bool TryGetFollowGoal(out Vector3 goal)
            {
                goal = _followGoal;
                var anchor = _follow.Anchor;
                return anchor != null && !anchor.isEnemyDead && anchor.isOutside == _soldier.isOutside;
            }

            /// <summary>Horizontal metres to the follow goal - or to the last leg, where that is nearer. Negative with no follow goal.</summary>
            internal float FollowGap
            {
                get
                {
                    if (!TryGetFollowGoal(out Vector3 goal)) return -1f;
                    Vector3 me = _soldier.transform.position;
                    float gap = Horizontal(goal - me);
                    if (_hasFollowLeg) gap = Mathf.Min(gap, Horizontal(_followLeg - me));
                    return gap;
                }
            }

            /// <summary>#874 Q2: the member stands off its anchor's next FollowPathClearMetres.</summary>
            internal bool FollowOffPath
            {
                get
                {
                    var anchor = _follow.Anchor;
                    return anchor != null
                        && BlacksiteMotionPolicy.OffAnchorPath(AnchorPathClearance(anchor, _soldier.transform.position));
                }
            }

            private static float Horizontal(Vector3 offset) { offset.y = 0f; return offset.magnitude; }

            /// <summary>
            /// #874 Q2: horizontal clearance of <paramref name="point"/> from the anchor's next
            /// <see cref="BlacksiteMotionPolicy.FollowPathClearMetres"/>: its body to its steering
            /// corner, then on toward its destination; a pathless anchor's next metres are the way it
            /// faces, which for a holder at its reach point is the object. Reads the agent's path
            /// only through Navigator.RemainingDistance, whose corner cache refreshes on a new issue.
            /// </summary>
            private static float AnchorPathClearance(BlacksiteSoldierAI anchor, Vector3 point)
            {
                float budget = BlacksiteMotionPolicy.FollowPathClearMetres;
                Vector3 at = anchor.transform.position;
                var agent = anchor.Navigator != null ? anchor.Navigator._agent : null;
                if (agent != null && agent.enabled && agent.isOnNavMesh && agent.hasPath && !agent.pathPending
                    && anchor.Navigator.RemainingDistance > BlacksiteArrivalPolicy.AgentRadius)
                {
                    Vector3 steer = agent.steeringTarget;
                    Vector3 first = steer - at; first.y = 0f;
                    float firstLength = first.magnitude;
                    if (firstLength >= budget)
                        return Clearance(at, at + first / firstLength * budget, point);
                    float clearance = Clearance(at, steer, point);
                    Vector3 onward = agent.destination - steer; onward.y = 0f;
                    float onwardLength = onward.magnitude;
                    if (onwardLength < .01f) return clearance;
                    Vector3 end = steer + onward / onwardLength * Mathf.Min(budget - firstLength, onwardLength);
                    return Mathf.Min(clearance, Clearance(steer, end, point));
                }
                return Clearance(at, at + BlacksiteFollow.Heading(anchor.transform.forward) * budget, point);
            }

            private static float Clearance(Vector3 a, Vector3 b, Vector3 point)
                => BlacksiteMotionPolicy.SegmentDistance(a.x, a.z, b.x, b.z, point.x, point.z);

            /// <summary>
            /// A Follow request names the follow in force. The same anchor and rank keep the anchor
            /// sampling and the goal they have; a different one starts it from the anchor as it
            /// stands now and derives its slot.
            /// </summary>
            private void BeginFollow(in BlacksiteGoal request)
            {
                var follow = request.Follow;
                _followRequest = request;
                if (_follow.SameAs(follow)) { _follow = follow; return; }
                _follow = follow;
                var anchor = follow.Anchor;
                _followAnchorAt = _followDerivedAnchorAt = anchor.transform.position;
                _followSampledAt = Time.time;
                _followHeading = BlacksiteFollow.Heading(anchor.transform.forward);
                // Seeded from the anchor's own agent, so a member that takes up a follow on an anchor
                // already walking does not read it as standing for the first smoothing window.
                _followAnchorSpeed = anchor.Navigator != null ? anchor.Navigator.AgentVelocity : 0f;
                _followAnchorWasMoving = BlacksiteMotionPolicy.AnchorMoving(_followAnchorSpeed);
                _hasFollowLeg = _hasFollowIssuedLeg = false;
                _followGoal = FollowSlot(anchor, _soldier, follow.Rank, out _);
            }

            private void EndFollow()
            {
                if (!_follow.HasValue) return;
                _follow = default;
                _followAnchorSpeed = 0f;
                _followAnchorWasMoving = false;
                _hasFollowLeg = _hasFollowIssuedLeg = false;
            }

            /// <summary>
            /// #874 Q2: why <paramref name="follow"/> no longer stands, or null. The world is the
            /// member's own; boarding is the anchor's rope or ladder crossing.
            /// </summary>
            private string FollowEnd(in BlacksiteFollow follow)
            {
                var anchor = follow.Anchor;
                if (anchor == null) return BlacksiteMotionPolicy.FollowEndReason(false, false, false, false, false, false, false);
                return BlacksiteMotionPolicy.FollowEndReason(true, anchor.isEnemyDead, anchor.isOutside == _soldier.isOutside,
                    AnchorBoarding(anchor), anchor.HasBoardedShip, follow.CarriesObjective,
                    !follow.CarriesObjective || anchor.IsObjectiveCarrier);
            }

            /// <summary>#874 Q2, S3: the anchor is crossing a ship link - the rope or the ladder - which nobody follows it onto.</summary>
            internal static bool AnchorBoarding(BlacksiteSoldierAI anchor)
                => anchor != null && anchor.Navigator != null && anchor.Navigator.IsCrossingShipLink;

            /// <summary>
            /// Every frame: the anchor's smoothed speed and travel heading. A warp or a world change
            /// is not travel and zeroes the speed; the heading is only turned while the anchor walks,
            /// so an anchor turning in place does not swing its followers round it. The pace is
            /// re-applied on the frame the anchor starts or stops, which is when it changes.
            /// #874 Q2: an anchor that has left the follow ends it here. #1293 R3: and the leg is
            /// re-asked here as the anchor walks (<see cref="RefreshFollow"/>).
            /// </summary>
            private void SampleFollowAnchor()
            {
                if (!_follow.HasValue) return;
                var anchor = _follow.Anchor;
                float now = Time.time;
                float dt = now - _followSampledAt;
                if (anchor != null && dt < BlacksiteMotionPolicy.FollowSampleSeconds) return;
                string ended = FollowEnd(_follow);
                if (ended != null)
                {
                    ReportFollowEnded(ended);
                    EndFollow();
                    return;
                }
                Vector3 at = anchor.transform.position;
                Vector3 moved = at - _followAnchorAt;
                moved.y = 0f;
                _followAnchorAt = at;
                _followSampledAt = now;
                float measured = moved.magnitude / dt;
                if (measured > FollowWarpSpeed) _followAnchorSpeed = 0f;
                else
                {
                    _followAnchorSpeed = BlacksiteMotionPolicy.SmoothAnchorSpeed(_followAnchorSpeed, measured, dt);
                    if (BlacksiteMotionPolicy.AnchorMoving(measured))
                        _followHeading = BlacksiteFollow.Heading(Vector3.Lerp(_followHeading, moved / (measured * dt),
                            dt / (dt + BlacksiteMotionPolicy.AnchorSpeedSmoothingSeconds)));
                }
                bool moving = BlacksiteMotionPolicy.AnchorMoving(_followAnchorSpeed);
                bool paceChanged = moving != _followAnchorWasMoving;
                if (paceChanged)
                {
                    _followAnchorWasMoving = moving;
                    ApplySpeed();
                }
                if (paceChanged || Horizontal(at - _followDerivedAnchorAt) >= BlacksiteMotionPolicy.FollowReissueMetres)
                    RefreshFollow(at);
            }

            /// <summary>#1293 R3: the Follow request re-asked through the same ladder, only while the
            /// agent is the writer (a crossing or seat hands back through a decision).</summary>
            private void RefreshFollow(Vector3 anchorAt)
            {
                _followDerivedAnchorAt = anchorAt;
                if (_nav.BodyWriter == BlacksiteBodyWriter.Agent) Hold(_followRequest, Ladder(_followRequest));
            }

            // One path for FollowSlot. Main thread only, like the corner buffer it reads into.
            private static readonly NavMeshPath FollowPath = new NavMeshPath();

            /// <summary>
            /// #1271 S2: the follow slot of <paramref name="rank"/> - the ONE follow goal, derived by
            /// this owner on every issue of a follow and asked by the planner (<see cref="FollowGoalFor"/>).
            /// It is <see cref="BlacksiteMotionPolicy.TrailPoint"/> on the route from the anchor to the
            /// member, <see cref="BlacksiteMotionPolicy.FollowTrailMetres"/> back, sampled onto the
            /// mesh. The route starts on the anchor's own floor, so an anchor in the other world or on
            /// the other storey keeps the slot on its side; across the mineshaft car it is the landing
            /// on the anchor's side (#1305 G3); with no route at all (an anchor off the mesh) the slot
            /// is the anchor itself. <paramref name="trailed"/> is how far
            /// back along the route the slot stands.
            /// </summary>
            private static Vector3 FollowSlot(BlacksiteSoldierAI anchor, BlacksiteSoldierAI member, int rank, out float trailed)
            {
                Vector3 at = anchor.transform.position;
                trailed = 0f;
                // #1305 G3: across the car the follow goal is the landing on the lead's side, never the lead.
                if (!member.isOutside && BlacksiteElevatorInteraction.TryGetCrossingLanding(anchor, member.transform.position, out Vector3 landing))
                    return landing;
                if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(member), at, member.transform.position, FollowPath).Planned)
                    return at;
                int count = FollowPath.GetCornersNonAlloc(RouteCorners);
                // The corners as x, y, z triples, in place: the policy is Unity-free.
                trailed = BlacksiteMotionPolicy.TrailPoint(MemoryMarshal.Cast<Vector3, float>(new ReadOnlySpan<Vector3>(RouteCorners, 0, count)),
                    BlacksiteMotionPolicy.FollowTrailMetres(rank), out float x, out float y, out float z);
                if (trailed < 0f) { trailed = 0f; return at; }
                var slot = new Vector3(x, y, z);
                return NavMesh.SamplePosition(slot, out NavMeshHit hit, BlacksiteArrivalPolicy.AgentRadius, NavMesh.AllAreas)
                    ? hit.position : slot;
            }

            /// <summary>
            /// #1271 S2: where this member's follow of <paramref name="anchor"/> at <paramref name="rank"/>
            /// stands now, for the planner: the goal the follow in force last derived, or, for one this
            /// owner has not taken up yet, the slot derived now.
            /// </summary>
            internal Vector3 FollowGoalFor(BlacksiteSoldierAI anchor, int rank)
                => ReferenceEquals(_follow.Anchor, anchor) && _follow.Rank == rank && TryGetFollowGoal(out Vector3 goal)
                    ? goal : FollowSlot(anchor, _soldier, rank, out _);

            private void ReportFollowEnded(string reason)
            {
                if (!BlacksiteDiagnostics.Enabled) return;
                BlacksiteDiagnostics.Log("Motion",
                    $"followEnded member={_soldier.MemberIndex}, anchor={(_follow.Anchor != null ? _follow.Anchor.MemberIndex.ToString() : BlacksiteDiagnosticFormat.None)}"
                    + $", source={_follow.Source ?? BlacksiteDiagnosticFormat.None}, reason={reason}");
            }

            /// <summary>
            /// The goal and the leg of a Follow, derived on this refresh: the slot on the route behind
            /// the anchor as it stands now (<see cref="FollowSlot"/>), and a leg led ahead of it along
            /// the anchor's travel while it moves (<see cref="BlacksiteMotionPolicy.FollowLead"/>),
            /// capped so the member stops a body behind the anchor. #874 Q2: the NavMesh.Raycast cut
            /// from the anchor is deleted - in a corridor it put every escort's leg on the holder's spot.
            /// </summary>
            private bool TryDeriveFollow(out Vector3 goal, out Vector3 leg)
            {
                leg = default;
                if (!TryGetFollowGoal(out goal)) return false;
                goal = _followGoal = FollowSlot(_follow.Anchor, _soldier, _follow.Rank, out float trailed);
                _followDerivedAnchorAt = _follow.Anchor.transform.position;
                float lead = BlacksiteMotionPolicy.FollowLead(FollowAnchorMoving, _followAnchorSpeed,
                    _nav.ArrivalContract.StopDistance, trailed);
                leg = goal + _followHeading * lead;
                _followLeg = leg;
                _hasFollowLeg = true;
                return true;
            }

            // ---------------------------------------------------------------------------------
            // #874 Q2: an objective holder's reach, read by the keep-out and the claim anchoring.
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #874 Q2: the member's standing point is an action's validated reach (an approach or a
            /// combat lane): its claim is exact, and it keeps its way against a member that has none.
            /// </summary>
            internal bool HoldsExactReach => _nav._approach.HasValue || _nav._combatApproach.HasValue;

            /// <summary>#874 Q2: the member is walking to an objective action's reach - a holder, whose claim publishes a keep-out.</summary>
            internal bool HoldsObjectiveReach => _nav._approach.HasValue;

            // One corner buffer for LastRouteCorner and FollowSlot. Main thread only, like every agent read here.
            private static readonly Vector3[] RouteCorners = new Vector3[32];

            /// <summary>
            /// #874 Q2: the corner this member's route arrives at its goal from - the last corner
            /// before the end - or its own position when the route is straight, unknown, or longer
            /// than the buffer. Read only when an exact claim is (re)anchored.
            /// </summary>
            internal Vector3 LastRouteCorner()
            {
                Vector3 here = _soldier.transform.position;
                var agent = Agent;
                if (agent == null || !agent.enabled || !agent.isOnNavMesh || !agent.hasPath || agent.pathPending) return here;
                int count = agent.path.GetCornersNonAlloc(RouteCorners);
                return count >= 2 && count < RouteCorners.Length ? RouteCorners[count - 2] : here;
            }

            /// <summary>
            /// #874 A5: the duty board opened a new attempt. Taken into the generation by the next
            /// goal this member actually adopts, so one change is one increment.
            /// </summary>
            internal void NoteDutyAttempt() => _dutyAttempt++;

            /// <summary>#874 A5: the one increment of the route generation.</summary>
            internal void AdvanceRouteGeneration(string evt) => _routeGeneration++;

            /// <summary>
            /// Raw issuers are steps of the goal the member already owns: the interaction tasks, the
            /// smart path and the entrance closer ("interaction") and the navigator-level raw write
            /// ("raw"). A restore can bring one back through Request with its raw tag.
            /// </summary>
            private static bool OwnsRoute(string issuer)
                => !string.IsNullOrEmpty(issuer)
                   && !string.Equals(issuer, "interaction", StringComparison.Ordinal)
                   && !string.Equals(issuer, "raw", StringComparison.Ordinal);

            /// <summary>
            /// #874 Q1: the leg name of a point the interaction coordinator issued for a request's
            /// goal (a smart-path step and its claim), printed as <c>leg=interaction</c> and, when
            /// reached short of the goal's test, as <c>waiting:interaction</c>.
            /// </summary>
            internal const string InteractionLeg = "interaction";

            /// <summary>#1305 H (C3): the leg name of a waypoint the hazard admission issued beside a live disc for a request's goal.</summary>
            internal const string DetourLeg = "detour";

            private bool IsNewRouteOwner(string issuer)
                => _dutyAttempt != _routeDutyAttempt
                   || (OwnsRoute(issuer) && !string.Equals(issuer, _routeOwner, StringComparison.Ordinal));

            /// <summary>Called where a request's goal is adopted: issued, or taken by the coordinator.</summary>
            private void NoteRouteOwner(in BlacksiteGoal goal)
            {
                if (!IsNewRouteOwner(goal.Issuer)) return;
                if (OwnsRoute(goal.Issuer)) _routeOwner = goal.Issuer;
                _routeDutyAttempt = _dutyAttempt;
                _routeGeneration++;
            }

            /// <summary>
            /// Every frame, from TickMaintenance: the payload coupling's owed position and the
            /// follow anchor's sample, with the follow leg it re-asks as the anchor walks.
            /// </summary>
            internal void Tick()
            {
                RestoreOwedCoupledPosition();
                SampleFollowAnchor();
            }

            /// <summary>
            /// #874 (payload coupling): the backstop on the one agent flag a seat can leave behind,
            /// scoped to the suspension the coupling itself made. <c>updatePosition = false</c> means
            /// "somebody else is moving this body"; a monster capture snapshots the flag it finds and
            /// restores the value it snapshotted, so a capture that took the body from the coupling
            /// put the coupling's own suspension back. It only ever clears a suspension the coupling
            /// owes (<see cref="BlacksiteNavigator.PayloadPositionOwed"/>) and only once the agent is
            /// the writer again, so a ride and a capture in progress are untouched.
            /// </summary>
            private void RestoreOwedCoupledPosition()
            {
                if (!_nav.PayloadPositionOwed || _nav.BodyWriter != BlacksiteBodyWriter.Agent
                    || _nav.HasCommittedMotion || _nav.IsPayloadCoupled) return;
                var agent = Agent;
                if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;
                if (!agent.updatePosition)
                {
                    agent.Warp(_soldier.transform.position);
                    agent.updatePosition = true;
                }
                _nav.NotePayloadPositionRestored();
            }

            // ---------------------------------------------------------------------------------
            // Agent writes. Every one of them in the runtime is in this region.
            // ---------------------------------------------------------------------------------

            /// <summary>The one stop in the runtime: a dwell, a crossing or a seat taking the body.</summary>
            internal void StopAgent()
            {
                var agent = Agent;
                if (agent != null && agent.enabled && agent.isOnNavMesh && !agent.isStopped) agent.isStopped = true;
            }

            internal void ResumeAgent()
            {
                var agent = Agent;
                if (agent != null && agent.enabled && agent.isOnNavMesh && agent.isStopped)
                    agent.isStopped = false;
            }

            /// <summary>
            /// #956 S12A U3, moved here unchanged: the one writer of agent.speed and agent.autoBraking.
            /// A dwell never zeroes the commanded speed: GetAgentMoveSpeed only scales the role speed
            /// (crouch, web) and caps it (cart, escort), so a link crossing timed off agent.speed
            /// cannot stall.
            /// </summary>
            internal void ApplySpeed()
            {
                var agent = Agent;
                if (agent == null) return;
                float speed = _soldier.GetAgentMoveSpeed(out _, out _nav._commandedSpeedOwner);
                // #874 P-S2: a Follow is paced to its anchor inside the band, so it neither overruns
                // its slot and brakes nor trails it. Only ever a cap: every term above still binds.
                if (_follow.HasValue)
                {
                    float paced = BlacksiteMotionPolicy.FollowSpeed(speed, FollowAnchorMoving, _followAnchorSpeed,
                        FollowGap, _follow.Band);
                    if (paced < speed) { speed = paced; _nav._commandedSpeedOwner = BlacksiteSpeedOwner.Follow; }
                }
                // #1219 F2: a rifle burst actually firing walks a committed combat step at a slow,
                // nonzero pace instead of stopping. Only ever a cap, and the last one: crouch, web,
                // the cargo caps and the follow pace all still bind below it.
                if (_soldier._combatController != null && _soldier._combatController.SlowsForFiring
                    && BlacksiteCombatPolicy.FiringWalkSpeed < speed)
                {
                    speed = BlacksiteCombatPolicy.FiringWalkSpeed;
                    _nav._commandedSpeedOwner = BlacksiteSpeedOwner.CombatFire;
                }
                agent.speed = speed;
                agent.autoBraking = !_nav.IsTransitGoal;
            }

            internal void ApplyAcceleration(float acceleration)
            {
                var agent = Agent;
                if (agent != null) agent.acceleration = acceleration;
            }

            /// <summary>
            /// #874 S1.2: the one writer of stoppingDistance. The owner ladder itself stays on the
            /// navigator, which is where every reader of it lives. #874 S4: avoidancePriority is
            /// written once, at ConfigureAgent.
            /// </summary>
            internal void ApplyStop(float stoppingDistance)
            {
                var agent = Agent;
                if (agent != null) agent.stoppingDistance = Mathf.Max(0.1f, stoppingDistance);
            }

            /// <summary>
            /// #1127 round 2: the destination write vanilla defers.
            ///
            /// <c>EnemyAI.SetDestinationToPosition</c> only RECORDS the vanilla <c>destination</c>
            /// field. The agent is given it by <c>EnemyAI.DoAIInterval</c>, which vanilla's Update
            /// runs on its own <c>AIIntervalTime</c> clock (0.2 s) behind a
            /// <c>destination != prevDestination</c> dedupe, so a leg decided just after an interval
            /// tick does not reach the agent for most of a fifth of a second.
            ///
            /// A ground-route leg cannot afford that. The follower hands out the next corridor point
            /// as the member enters the advance radius of the one it is walking, and if that point
            /// waits for the next interval the member has meanwhile walked ONTO the current one,
            /// consumed its path and stopped: the 2026-09-20 run's corridor walk reads
            /// <c>path=none, agentVelocity=0.00</c> at every issue (member 0, t=560-568).
            ///
            /// This is vanilla's own step taken on the frame the decision was made - the same point,
            /// through the same property. DoAIInterval still runs, still owns the
            /// <c>prevDestination</c> latch (it re-issues this point once, against a path the agent
            /// already has) and still owns <c>SyncPositionToClients</c>, so the native position
            /// authority and the client sync cadence are untouched. #874 S4: a stopped agent (a
            /// dwell) computes the path too; the dwell's expiry is what lets it walk it.
            /// </summary>
            private void CommitDestination(NavMeshAgent agent)
            {
                if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;
                agent.SetDestination(_soldier.destination);
            }

            /// <summary>A deliberate path wipe, and the only one outside a warp.</summary>
            internal void ClearPath()
            {
                _hasHeld = false;
                _nav.ReleaseDestinationClaim();
                var agent = Agent;
                if (agent != null && agent.enabled && agent.isOnNavMesh) agent.ResetPath();
            }

            /// <summary>#1293 R3: the leg changed outside the ladder; the held request and any ladder wait go.</summary>
            internal void ForgetHeldRequest()
            {
                _hasHeld = false;
                _nav.ReleaseWorldLadderWait();
            }

            /// <summary>
            /// The warp path: wipe, end the dwell, then Warp. Refused while a crossing or a seat is
            /// moving the body; the entrance interaction's own dwell ends with its teleport.
            /// </summary>
            internal bool WarpResetAndMove(Vector3 target)
            {
                if (_nav.BodyWriter != BlacksiteBodyWriter.Agent) return false;
                var agent = Agent;
                if (agent == null || !agent.enabled || !agent.isOnNavMesh) return false;
                _hasHeld = false;
                agent.ResetPath();
                _nav.EndDwell();
                return agent.Warp(target);
            }

            /// <summary>
            /// #874 (payload coupling): stop the agent moving the body, without touching the goal or
            /// the path - the going-in half of the pair every crossing motor makes
            /// (<c>updatePosition = false</c> here, restored on the way out).
            /// </summary>
            internal bool SuspendAgentPosition()
            {
                var agent = Agent;
                if (agent == null || !agent.enabled) return false;
                if (agent.updatePosition) agent.updatePosition = false;
                return true;
            }

            /// <summary>
            /// #874 (payload coupling): carry the body to a pose another owner has already proved
            /// legal, and keep the agent's own simulated position on it.
            ///
            /// The <c>nextPosition</c> write is why this is a hand-walk and not a teleport. With
            /// <c>updatePosition = false</c> the agent keeps simulating from wherever it was left,
            /// so its route, its <c>CalculatePath</c> - which is what the coupling steers by - and
            /// its <c>isOnNavMesh</c> would all be answered for a point the body has walked away
            /// from. Pinning it to the body on every step is what keeps the heading query honest.
            ///
            /// Refused unless the position updates are actually suspended: a write that moved the
            /// transform while the agent still owned it would fight the agent for the frame.
            /// </summary>
            internal bool CarryCoupledBody(Vector3 position)
            {
                var agent = Agent;
                if (agent == null || !agent.enabled || agent.updatePosition) return false;
                _soldier.transform.position = position;
                if (agent.isOnNavMesh) agent.nextPosition = position;
                // Native sync compares against the last transmitted position, exactly as a link
                // crossing's own hand-walk does, or remote players never receive these small steps.
                _soldier.SyncPositionToClients();
                return true;
            }

            /// <summary>#1233 S1: the coupled body through a paired entrance. The two sides are not connected, so the agent
            /// is warped with its updates still suspended and published by native sync, which owns the sent position.</summary>
            internal bool WarpCoupledBody(Vector3 position)
            {
                var agent = Agent;
                if (agent == null || !agent.enabled || agent.updatePosition || !agent.Warp(position)) return false;
                _soldier.transform.position = position;
                _soldier.SyncPositionToClients();
                _soldier.NotifyTeleported();
                return true;
            }

            /// <summary>
            /// #874 (payload coupling): the coming-out half. The agent is put back on the pose the
            /// body was left in before it follows the transform again, so the leg it plans next
            /// starts where the member is standing rather than where the coupling took him from.
            /// </summary>
            internal bool ResumeAgentPosition()
            {
                var agent = Agent;
                if (agent == null || !agent.enabled) return false;
                if (agent.isOnNavMesh) agent.Warp(_soldier.transform.position);
                if (!agent.updatePosition) agent.updatePosition = true;
                return true;
            }

            /// <summary>
            /// #874 N5: keep the agent on a body a moving platform carries - the elevator cabin -
            /// the way <see cref="CarryCoupledBody"/> keeps it on a coupled body.
            ///
            /// The Vow run carried member 1 37 m down the mineshaft by writing the transform alone,
            /// so the agent stayed on the boarding floor; at the exit it was standing on the top
            /// landing's cabin link and the Jump motor's back-off put the body back up there.
            /// <c>nextPosition</c> follows the body along the surface the agent is on, which is
            /// the whole answer while the cabin is at a landing. A shaft is not a surface, so once
            /// the body has left the agent's surface the agent is re-bound by a warp to the ground
            /// under the body: within <paramref name="bindRadius"/> of it and within one step
            /// height of its feet, which is the landing the car is standing at. Mid-shaft there is
            /// no such ground and the agent is left where it is, stopped and pathless under the
            /// ride's crossing, until the car reaches the next landing.
            ///
            /// A no-op unless the position updates are suspended, exactly as CarryCoupledBody
            /// refuses: a warp while the agent still owned the transform would fight it.
            /// </summary>
            internal void FollowCarriedBody(float bindRadius)
            {
                var agent = Agent;
                if (agent == null || !agent.enabled || agent.updatePosition) return;
                Vector3 body = _soldier.transform.position;
                float onBody = agent.radius * agent.radius;
                if (agent.isOnNavMesh)
                {
                    agent.nextPosition = body;
                    if ((agent.nextPosition - body).sqrMagnitude <= onBody) return;
                }
                if (!NavMesh.SamplePosition(body, out NavMeshHit under, bindRadius, agent.areaMask)
                    || Mathf.Abs(under.position.y - body.y) > BlacksitePhysicalStep.StepHeight)
                    return;
                // Already standing on that ground at the lip: the constrained follow above is as
                // close as the surface lets it get, and a warp would only re-issue the same point.
                if (agent.isOnNavMesh && (agent.nextPosition - under.position).sqrMagnitude <= onBody) return;
                // Warp moves the transform with the agent; the body stays where the cabin put it.
                if (agent.Warp(under.position)) _soldier.transform.position = body;
            }

            /// <summary>
            /// #874 S4.0a: the raw destination write - the smart-path bridge and the interaction
            /// tasks issue through here. A raw issue is a deliberate command from the task that owns
            /// the member's next step, so it ends a dwell. It is never written through a crossing or
            /// a seat, except the payload coupling, whose goal is where the cart steers the pair.
            /// </summary>
            internal bool IssueDirect(Vector3 resolved, string issuer)
            {
                var agent = Agent;
                if (agent == null || !agent.enabled || !agent.isOnNavMesh) return false;
                if (_nav.BodyWriter != BlacksiteBodyWriter.Agent && !_nav.IsPayloadCoupled) return false;
                if (_nav.BodyWriter == BlacksiteBodyWriter.Agent) _nav.EndDwell();
                _soldier.movingTowardsTargetPlayer = false;
                _soldier.moveTowardsDestination = true;
                _soldier.destination = resolved;
                if (!agent.SetDestination(resolved))
                {
                    ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Smart path destination rejected: {resolved}");
                    return false;
                }
                _hasHeld = false;
                EndHazardBlock(issuer);
                _nav._lastDestination = resolved;
                _nav._hasLastDestination = true;
                _nav.RecordIssuedDestination(resolved, issuer);
                _nav.NoteIssuedGoal(resolved, null);
                // #1293 R2 (S5): a new goal reached the agent, so no refusal stands for it.
                _refusedVerdictSeq = -1;
                return true;
            }

            // ---------------------------------------------------------------------------------
            // Request
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #1015 S1 / #874 S4: every return names its outcome. The refusals are exactly
            /// <c>noAgent</c>, <c>floodedGoal</c> and (#1305 H) <c>hazardDisc</c>; the other answers
            /// are <c>issued</c>, <c>sameGoal</c>, <c>identicalIssue</c>, <c>interaction</c>,
            /// <c>crossing</c>/<c>seated</c> (the goal is recorded to resume at the end) and
            /// <c>exception</c>. #1293 R3: a request equal to the held one is answered by
            /// <see cref="TryAnswerHeld"/> first.
            /// </summary>
            internal BlacksiteMotionVerdict Request(in BlacksiteGoal request)
                => TryAnswerHeld(request, out var held) ? held : Hold(request, Ladder(request));

            private BlacksiteMotionVerdict Ladder(in BlacksiteGoal request)
            {
                var agent = Agent;
                string issuer = request.Issuer;
                // -> Blocked: there is no agent to command.
                if (agent == null || !agent.enabled || !agent.isOnNavMesh)
                    return Report(BlacksiteMotionOutcome.Blocked, BlacksiteMotionResult.Dropped, issuer, "noAgent");
                // #874 P-S2: a Follow request names the follow in force. #874 S4: a follow its anchor
                // has already left (another world, aboard, dead, the carried objective dropped) is
                // issued as a plain goal to the request's position; the planner re-targets it.
                bool follow = request.Follow.HasValue;
                if (follow && FollowEnd(request.Follow) is string ended)
                {
                    if (_follow.HasValue) { ReportFollowEnded(ended); EndFollow(); }
                    follow = false;
                }
                if (follow) BeginFollow(request);
                // -> OwnedByMotor: a crossing or a seat owns the body. The request is the goal to
                // resume at the end. The payload coupling's goal is where the cart steers the pair,
                // so its requests are issued.
                var writer = _nav.BodyWriter;
                if (writer == BlacksiteBodyWriter.Seated || (writer == BlacksiteBodyWriter.Crossing && !_nav.IsPayloadCoupled))
                {
                    NoteGoal(request);
                    return Report(BlacksiteMotionOutcome.OwnedByMotor, BlacksiteMotionResult.OwnedByMotor, issuer,
                        writer == BlacksiteBodyWriter.Seated ? "seated" : "crossing");
                }
                // #874 P-S1: the request's position IS the goal. Everything below that hands the
                // agent a different point - a ground-route waypoint, a claim slot, a follow leg -
                // is a LEG of that goal, named in `leg` and recorded with the goal when it is
                // issued, so [UAT][Motion] remaining=, the identical-issue gate and the arrival
                // verdict all measure the goal the planner issued.
                Vector3 destination = request.Position;
                Vector3 missionDestination = destination;
                string leg = null;
                // #874 P-S2 / #1271 S2: a Follow's goal is re-derived on this refresh - its slot on the
                // route behind the live anchor - and the point handed to the agent is a leg of it.
                if (follow && TryDeriveFollow(out missionDestination, out destination)) leg = BlacksiteMotionPolicy.FollowLeg;
                else if (follow)
                {
                    follow = false;
                    EndFollow();
                    missionDestination = destination = request.Position;
                }
                // #874 Q1: the goal's own completion test travels with the request - the arrival
                // contract; #1271 S2: a Follow's too, never its pace band. It is
                // named here, before anything below may substitute a point for the goal, and every
                // substitution - the coordinator's smart step, the squad director's claim, a follow
                // leg - is judged by it.
                var contract = _nav.ArrivalContract;
                _nav.NoteGoalTest(missionDestination, contract.ArrivalTolerance, contract.StopDistance);
                Vector3 unrouted = destination;
                bool? groundRouteOutbound = request.GroundRouteOutbound;
                bool isLastWaypoint = false;
                bool following = groundRouteOutbound.HasValue && _soldier.isOutside
                    && _soldier.TryGetMission(out var routeMission) && routeMission.Objectives.Ship != null
                    // #874 S8.5: the follower advances at the contract this issue is made under.
                    && routeMission.Objectives.Ship.TryGroundRouteWaypoint(_soldier.transform.position,
                        routeMission.Objectives.Ship.CorridorGoal(missionDestination, request.GoalAcross), groundRouteOutbound.Value,
                        _nav.GroundRouteArrivalContract.AdvanceRadius, out destination, out isLastWaypoint);
                if (!following) destination = unrouted;
                else if (Vector3.Distance(destination, missionDestination) > BlacksiteArrivalPolicy.MinDestinationChange)
                    leg = "groundRoute";
                // #874 S4.0a: the substitution is itself an issuer. A member walking a ground-route
                // waypoint is not walking to the order's goal, and the log could not tell them apart.
                // #1305 C4: and the raw point is the leg in force, recorded before the latch below.
                if (following) { issuer = "groundRoute"; _groundRouteLeg = destination; }
                // #1127: intermediate waypoints must not brake; only the last keeps autoBraking.
                _nav.UpdateGroundRoute(following, hasFurtherWaypoint: following && !isLastWaypoint);
                // -> OwnedByMotor: the world ladder climb began; the climb is a crossing.
                if (following && _soldier.TryGetMission(out var ladderMission)
                    && _nav.HandleWorldLadder(ladderMission.Objectives.Ship, groundRouteOutbound.Value, ref destination))
                {
                    NoteGoal(request);
                    return Report(BlacksiteMotionOutcome.OwnedByMotor, BlacksiteMotionResult.OwnedByMotor, issuer, "crossing");
                }
                if (!following) _nav.ReleaseWorldLadder();
                float sampleDistance = _soldier.isOutside ? 8f : 3f;
                Vector3 resolved = ResolveNavMeshPosition(destination, sampleDistance);
                // #1233 S5 (rules 4 and 6): the body rests where its route ends short of the goal. A
                // request for that goal is then never folded - not as a follow leg in hand, not as an
                // identical issue, not as the same goal - because the route has to be asked again
                // from live geometry: the carved doorway that ended it may just have been unlocked.
                bool routeEnded = _nav.StandsAtRouteEnd;
                // #955 S12A U2: the issue gate, BEFORE TrySetSmartDestination and independent of
                // _agent.hasPath. #874 P-S1: measured against the GOAL, so a reached leg never makes
                // a request for an unreached goal "identical".
                // -> Moving / Dropped: a byte-identical re-issue of the point this member already has.
                // #874 P-S2: a Follow is never folded by the goal gate - its goal moves by design -
                // only by its own: a re-derived leg that has not moved is the leg the agent has.
                if (follow && _nav._hasIssuedDestination && !routeEnded
                    && BlacksiteMotionPolicy.FollowLegInHand(_hasFollowIssuedLeg, Vector3.Distance(resolved, _followIssuedLeg)))
                {
                    // #874 Q12: the fast path still re-judges its route against a rising flood plane.
                    if (_nav.RefuseFloodedGoal(resolved)) return ReportFloodRefusal(issuer);
                    // #1305 H (C3): and against the live hazard discs; a refused or blocked one is admitted below.
                    if (!_hazardBlock.HasValue && !_nav.HazardRouteInForceRefused())
                        return Report(BlacksiteMotionOutcome.Moving, BlacksiteMotionResult.Unchanged, issuer, "sameGoal");
                }
                if (!follow && _nav.DropIdenticalIssue(missionDestination, resolved, issuer, routeEnded))
                {
                    if (_nav.RefuseFloodedGoal(resolved)) return ReportFloodRefusal(issuer);
                    if (!_hazardBlock.HasValue && !_nav.HazardRouteInForceRefused())
                        return Report(BlacksiteMotionOutcome.Moving, BlacksiteMotionResult.Dropped, issuer, "identicalIssue");
                }
                // -> OwnedByMotor: the interaction coordinator took the destination. #874 Q1: an
                // accepted request like any other - the goal it serves is adopted through NoteGoal
                // (route owner, a stale follow ended), and the point the coordinator issued is
                // recorded as a leg of that goal, so reaching it short of the goal's test reads
                // waiting:interaction, never arrived. 2026-09-23 round 2: this path returned before
                // NoteGoal, the Escort follow of a carrier that had already crossed was never ended,
                // and three members stood 3.76 m from the door as waiting:follow for 165 s.
                if (_nav.TrySetSmartDestination(resolved, issuer))
                {
                    NoteGoal(request);
                    _nav.NoteIssuedGoal(missionDestination, InteractionLeg);
                    return Report(BlacksiteMotionOutcome.OwnedByMotor, BlacksiteMotionResult.OwnedByMotor, issuer, "interaction");
                }

                Vector3 followIssued = resolved;
                if (_nav._interactionCoordinator != null)
                {
                    // #874 S2.1: a melee lane is as precise as an action's standing point.
                    // #874 Q1: and never further than the goal's own test leaves room for.
                    // #874 S4: the claim may substitute a slot as a leg; it never refuses.
                    Vector3 requested = resolved;
                    if (!_nav._interactionCoordinator.TryClaimDestination(
                            requested, out resolved, exact: _nav._approach.HasValue || _nav._combatApproach.HasValue,
                            acceptanceRadius: _nav.ClaimAllowance(requested)))
                        resolved = requested;
                    _nav._claimOffset = Vector3.ProjectOnPlane(resolved - requested, Vector3.up).magnitude;
                    if (leg == null && _nav._claimOffset > .01f) leg = "claim";
                }

                // A moving action anchor (a nudged cart handle) must be re-approached.
                float reissueThreshold = SameLegSqrMetres(groundRoute: following, follow: follow, exact: HoldsExactReach);
                // #874 Q12: a route under the flood plane is refused, never walked - the goal under
                // way included, so a route accepted dry is re-judged as the water rises.
                if (_nav.RefuseFloodedGoal(resolved)) return ReportFloodRefusal(issuer);
                // #1305 H (C3): beside it, the live hazard discs. The route in force is judged as
                // walked; a new one as NavMesh plans it. One that enters a disc is walked by a detour
                // leg - the goal stays the goal - or refused, and the member waits facing the disc.
                bool inForce = _nav._hasLastDestination && agent.hasPath && !routeEnded
                    && Vector3.SqrMagnitude(resolved - _nav._lastDestination) < reissueThreshold;
                if (_nav.RefuseHazardRoute(missionDestination, ref resolved, inForce, out bool detour, out BlacksiteHazardFootprint disc))
                    return ReportHazardRefusal(issuer, disc);
                if (detour)
                {
                    if (disc.HasValue) _nav.LogHazard("detour", disc, missionDestination, issuer, resolved);
                    leg = DetourLeg;
                    inForce = _nav._hasLastDestination && agent.hasPath
                        && Vector3.SqrMagnitude(resolved - _nav._lastDestination) < reissueThreshold;
                }

                // -> Moving / Unchanged: this is the goal the member is already walking to.
                if (inForce) return Report(BlacksiteMotionOutcome.Moving, BlacksiteMotionResult.Unchanged, issuer, "sameGoal");

                try
                {
                    // #874 S4: a dwell keeps the agent stopped; the path computes meanwhile.
                    if (!_nav.InDwell && _nav.BodyWriter == BlacksiteBodyWriter.Agent) ResumeAgent();
                    _soldier.movingTowardsTargetPlayer = false;
                    _soldier.moveTowardsDestination = true;
                    _soldier.SetDestinationToPosition(resolved, checkForPath: false);
                    // #1127 round 2: on the agent NOW, not on vanilla's next AI interval.
                    CommitDestination(agent);
                    _nav.RecordIssuedDestination(resolved, issuer);
                    // #874 P-S1: and the goal that point is a leg of, with the leg named.
                    _nav.NoteIssuedGoal(missionDestination, leg);
                    // #1015 S1 (review Y3b): the goal is recorded HERE, where a destination actually
                    // reached the agent. #874 P-S1: it is the GOAL that is recorded, never the leg.
                    NoteGoal(request);
                    if (follow) { _followIssuedLeg = followIssued; _hasFollowIssuedLeg = true; }
                    if (agent.hasPath || Vector3.SqrMagnitude(_soldier.transform.position - resolved) <= 4f)
                    {
                        _nav._lastDestination = _soldier.destination != Vector3.zero ? _soldier.destination : resolved;
                        _nav._hasLastDestination = true;
                    }
                    // -> Arrived / Moving: the destination reached the agent. The only path that does.
                    // #874 P-S1: Arrived only when the GOAL is reached; a reached leg is Moving.
                    // #874 Q1: and "reached" is the goal's own test - its radius, never a substituted
                    // point's stop distance. #1271 S2: a Follow's goal is judged the same way; there
                    // is no arrival by the anchor.
                    return Report(
                        (leg == null && _nav.HasArrived(resolved)) || _nav.PassesGoalTest()
                            ? BlacksiteMotionOutcome.Arrived : BlacksiteMotionOutcome.Moving,
                        BlacksiteMotionResult.Issued, issuer, "issued");
                }
                catch (Exception e)
                {
                    ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Soldier destination skipped: {e.Message}");
                    // -> Blocked / Dropped: the agent threw while being commanded.
                    return Report(BlacksiteMotionOutcome.Blocked, BlacksiteMotionResult.Dropped, issuer, "exception");
                }
            }

            /// <summary>
            /// #1293 R3: the identity short-circuit before any navmesh or path work: a request equal to
            /// the held one gets the held verdict (counted, not logged, no event) while nothing else was
            /// answered since, the agent writes a body on its route, no world ladder is in force, no
            /// room or corridor change is due, no interaction or detour
            /// leg was reached short of its goal and no route end is due a re-ask. A live flood
            /// re-judges the route per half-metre step. #1305 H (C3): a hazard-disc wait is held until
            /// its exit event (<see cref="ObserveHazardDiscs"/>) - no leg, corridor or route-end fact
            /// re-asks it - and stays a refusal for the progress ledger (<see cref="LastRefusedRoute"/>).
            /// </summary>
            private bool TryAnswerHeld(in BlacksiteGoal request, out BlacksiteMotionVerdict verdict)
            {
                verdict = _lastVerdict;
                var agent = Agent;
                if (!_hasHeld || _heldSeq != _verdictSeq || !SameRequest(request)
                    || agent == null || !agent.enabled || !agent.isOnNavMesh
                    || _nav.BodyWriter != BlacksiteBodyWriter.Agent || _nav._worldLadder != null
                    || (!_hazardBlock.HasValue && (_nav._roomAdvancePending || _nav.WaypointLegReached
                        || GroundRouteAdvanceDue || GroundRouteJoinDue || RouteEndReaskDue())))
                    return false;
                int floodStep = _nav.CurrentFloodStep();
                if (floodStep != _heldFloodStep)
                {
                    if (floodStep != int.MinValue && _nav.RefuseFloodedGoal(_nav._issuedDestination))
                    {
                        verdict = ReportFloodRefusal(request.Issuer);
                        return true;
                    }
                    _heldFloodStep = floodStep;
                }
                bool refused = _refusedVerdictSeq == _verdictSeq;
                _heldSeq = ++_verdictSeq;
                if (refused) _refusedVerdictSeq = _verdictSeq;
                return true;
            }

            /// <summary>
            /// #1293 R3: the body stands where a route that stops short of its goal ends, and that
            /// route has not been asked again from here in this decision against the same facts (path
            /// state, route end). Asking once is what a request at a route end is for - live geometry,
            /// a doorway just unlocked; asking on every frame of a per-frame continuation is a storm.
            /// Recorded when it answers true, so the ladder walk it lets through is the one re-ask.
            /// </summary>
            private bool RouteEndReaskDue()
            {
                if (!_nav.StandsAtRouteEnd) return false;
                int decision = _soldier.Brain.Decisions;
                BlacksitePathState path = _nav.PathState;
                Vector3 end = _nav.RouteEnd;
                if (decision == _routeEndAskedDecision && RouteEndAsked(path, end)) return false;
                _routeEndAskedDecision = decision;
                _routeEndAskedPath = path;
                _routeEndAskedAt = end;
                _routeEndAskedGoal = _nav._issuedGoal;
                return true;
            }

            /// <summary>#1293 R3: a route end of the goal in force, in this path state and ending here,
            /// is the one last asked again from (<see cref="RouteEndReaskDue"/>), so standing at it
            /// again is no new fact.</summary>
            internal bool RouteEndAsked(BlacksitePathState path, Vector3 end)
            {
                const float same = BlacksiteArrivalPolicy.MinDestinationChange * BlacksiteArrivalPolicy.MinDestinationChange;
                return _routeEndAskedDecision != int.MinValue && path == _routeEndAskedPath
                    && (end - _routeEndAskedAt).sqrMagnitude <= same
                    && (_nav._issuedGoal - _routeEndAskedGoal).sqrMagnitude <= same;
            }

            /// <summary>#1293 R3: same issuer, validation and direction, and the same follow or
            /// a point within the leg already in hand.</summary>
            private bool SameRequest(in BlacksiteGoal request)
            {
                if (!string.Equals(request.Issuer, _held.Issuer, StringComparison.Ordinal)
                    || request.ValidatePath != _held.ValidatePath
                    || request.GroundRouteOutbound != _held.GroundRouteOutbound
                    || request.GoalAcross != _held.GoalAcross)
                    return false;
                if (request.Follow.HasValue || _held.Follow.HasValue)
                    return _held.Follow.SameAs(request.Follow) && _follow.SameAs(request.Follow);
                return (request.Position - _held.Position).sqrMagnitude
                    < SameLegSqrMetres(groundRoute: request.GroundRouteOutbound.HasValue, follow: false, exact: HoldsExactReach);
            }

            /// <summary>The squared distance inside which a point is the leg in hand: corridor point
            /// exact, follow leg <see cref="BlacksiteMotionPolicy.FollowReissueMetres"/>, action or
            /// combat lane 0.2 m, else <see cref="BlacksiteArrivalPolicy.ReissueHysteresis"/>.</summary>
            private static float SameLegSqrMetres(bool groundRoute, bool follow, bool exact)
                => groundRoute ? .01f
                    : follow ? BlacksiteMotionPolicy.FollowReissueMetres * BlacksiteMotionPolicy.FollowReissueMetres
                    : exact ? .04f
                    : BlacksiteArrivalPolicy.ReissueHysteresis * BlacksiteArrivalPolicy.ReissueHysteresis;

            /// <summary>
            /// #1293 R3: a ladder answer that left the member under way is the request held - on its
            /// own legs (Moving, Arrived), or on a smart-path step the coordinator issued as a plain
            /// point with no task behind it, which a repeat would only issue again. #1305 H (C3): and
            /// a hazard-disc wait, which only an exit event ends.
            /// </summary>
            private BlacksiteMotionVerdict Hold(in BlacksiteGoal request, BlacksiteMotionVerdict verdict)
            {
                _hasHeld = verdict.Outcome == BlacksiteMotionOutcome.Moving || verdict.Outcome == BlacksiteMotionOutcome.Arrived
                    || _hazardBlock.HasValue
                    || (verdict.Outcome == BlacksiteMotionOutcome.OwnedByMotor
                        && string.Equals(verdict.Reason, InteractionLeg, StringComparison.Ordinal)
                        && _nav._interactionCoordinator != null && !_nav._interactionCoordinator.HasActiveTask);
                if (!_hasHeld) return verdict;
                _held = request;
                _heldSeq = _verdictSeq;
                _heldFloodStep = _nav.CurrentFloodStep();
                return verdict;
            }

            private void NoteGoal(in BlacksiteGoal request)
            {
                // #874 P-S2: a different goal with an owner of its own ends the follow; a raw step
                // (a door, the smart path) is a step of it and leaves it alone.
                if (!request.Follow.HasValue && OwnsRoute(request.Issuer)) EndFollow();
                NoteRouteOwner(request);
            }

            // #1305 C4: the raw corridor point the follower handed out on the request that set the
            // ground-route latch. It replaces the issued destination as the leg GroundRouteAdvanceDue
            // judges. Ladder writes it just before UpdateGroundRoute(true, ..), the latch's only setter
            // (every other caller clears it), so a set latch never pairs with a leg from an earlier ask.
            private Vector3 _groundRouteLeg;

            /// <summary>
            /// #1127 round 2: the corridor advance is due - the leg is an INTERMEDIATE corridor point the
            /// member reached by the follower's leg judge (#1305 C4). TickMaintenance's rising edge asks
            /// for the next point once per point, so no member stands at a consumed one. The leg is the
            /// raw route point the follower judges, never the issued point: that is the agent's
            /// destination after the navmesh sample, a claim slot or a detour moved it, and one nearer
            /// the member was due while the follower handed the same point back, which resolved to the
            /// same issue (Unchanged) and spent the edge. A due leg is now answered by a later raw point,
            /// which re-resolves to the same issue only if two route points sample to one navmesh point.
            /// </summary>
            internal bool GroundRouteAdvanceDue
                => _nav._followingGroundRoute && _nav._groundRouteHasFurtherWaypoint
                   && BlacksiteGroundRouteFollower.LegDue(_soldier.transform.position, _groundRouteLeg,
                       _nav.GroundRouteArrivalContract.AdvanceRadius);

            /// <summary>
            /// #1293 R3: the held ground-route request would join or leave the corridor differently
            /// now, so the leg in hand is not the one it asks for. Outdoors, one corridor scan a frame.
            /// </summary>
            internal bool GroundRouteJoinDue
                => _hasHeld && _held.GroundRouteOutbound.HasValue && _soldier.isOutside
                   && _soldier.TryGetMission(out var mission) && mission.Objectives.Ship != null
                   && mission.Objectives.Ship.TryGroundRouteWaypoint(_soldier.transform.position,
                          mission.Objectives.Ship.CorridorGoal(_held.Position, _held.GoalAcross),
                          _held.GroundRouteOutbound.Value, _nav.GroundRouteArrivalContract.AdvanceRadius, out _, out _)
                      != _nav._followingGroundRoute;

            /// <summary>
            /// #874 Q12: the one verdict for a flood refusal, wherever it was found - the issue
            /// path, the fast-path recheck or a refused smart-path step. The goal is kept; the
            /// planner picks another one if the water stays. #1293 R2 (S5): the refusal is a miss
            /// with evidence, so the refused goal - the request's own goal test, named before any
            /// refusal - is kept with the verdict for the progress ledger.
            /// </summary>
            private BlacksiteMotionVerdict ReportFloodRefusal(string issuer)
            {
                var verdict = Report(BlacksiteMotionOutcome.Blocked, BlacksiteMotionResult.Dropped, issuer, "floodedGoal");
                _refusedGoal = _nav._goalTestCentre;
                _refusedVerdictSeq = _verdictSeq;
                return verdict;
            }

            private Vector3 _refusedGoal;
            private long _refusedVerdictSeq = -1;

            /// <summary>
            /// #1305 H (C3): the one verdict for a route a live hazard disc refuses with no detour -
            /// the issue path or a refused smart-path step. The path is cleared, so the member waits
            /// where it stands (never a self-position destination) facing the disc
            /// (<see cref="BlacksiteSoldierAI.WatchFaces"/>); the wait is the asking job's
            /// (<see cref="BlacksiteMotionVerdict.MotionUnderway"/>) and is held until an exit event
            /// (<see cref="ObserveHazardDiscs"/>). The refused goal is kept for the progress ledger as
            /// a flood refusal's is.
            /// </summary>
            private BlacksiteMotionVerdict ReportHazardRefusal(string issuer, in BlacksiteHazardFootprint disc)
            {
                ClearPath();
                Vector3 goal = _nav._goalTestCentre;
                if (!ReferenceEquals(_hazardBlock.Source, disc.Source)
                    || (goal - _refusedGoal).sqrMagnitude > BlacksiteArrivalPolicy.MinDestinationChange * BlacksiteArrivalPolicy.MinDestinationChange)
                    _nav.LogHazard("blocked", disc, goal, issuer);
                _hazardBlock = disc;
                var verdict = Report(BlacksiteMotionOutcome.Blocked, BlacksiteMotionResult.Dropped, issuer, BlacksiteMotionVerdict.HazardDiscReason);
                _refusedGoal = goal;
                _refusedVerdictSeq = _verdictSeq;
                return verdict;
            }

            /// <summary>#1305 H (C3): the block ends - a route admitted, a raw step, an exit event: one clear line.</summary>
            private void EndHazardBlock(string issuer)
            {
                if (!_hazardBlock.HasValue) return;
                _nav.LogHazard("clear", _hazardBlock, _refusedGoal, issuer);
                _hazardBlock = default;
            }

            /// <summary>The issuer of the way out a member standing in a live disc asks for with no request of its own.</summary>
            internal const string HazardEscapeIssuer = "order:HazardEscape";

            /// <summary>
            /// #1305 H (C3): this member's live-disc events, from the squad's perception pass only
            /// (<see cref="BlacksiteSoldierAI.ObserveHazardRowsServer"/>; R2, no clock of its own). A
            /// block ends when its disc stops being live or leaves the table, or - a slime that moved -
            /// no longer refuses the goal route: one clear line and one Outcome, and the job's next
            /// request is admitted afresh. A live disc across the route in force - it became live, or a
            /// slime moved onto it - re-admits the held request once: a detour or a block, after which
            /// the walked route no longer crosses it. A member standing in a disc with no request held
            /// asks for the way out, or waits in it blocked when there is none.
            /// </summary>
            internal void ObserveHazardDiscs(BlacksiteWorldModel world)
            {
                List<BlacksiteHazardFootprint> discs = BlacksiteHazardAwareness.LiveDiscs(world);
                if (_hazardBlock.HasValue)
                {
                    if (BlockStands(discs)) return;
                    EndHazardBlock(_hasHeld ? _held.Issuer : null);
                    _hasHeld = false;
                    _soldier.RequestDecision(BlacksiteDecisionCause.Outcome);
                    return;
                }
                if (discs.Count == 0 || !_nav.RouteInForceRefused(discs, out BlacksiteHazardFootprint disc)) return;
                if (_hasHeld)
                {
                    BlacksiteGoal request = _held;
                    _hasHeld = false;
                    Hold(request, Ladder(request));
                    return;
                }
                // A task's raw step: a committed crossing is not interrupted by a disc.
                if (Agent.hasPath) return;
                Vector3 here = _soldier.transform.position;
                if (_nav.TryDetour(discs, disc, here, here, false, out Vector3 exit))
                {
                    Request(new BlacksiteGoal(exit, true, null, HazardEscapeIssuer));
                    return;
                }
                _nav.LogHazard("blocked", disc, here, null);
                _hazardBlock = disc;
                _refusedGoal = here;
            }

            // The block's disc is still live in the table and, if it moved (a slime), still refuses
            // the goal route measured again from here.
            private bool BlockStands(List<BlacksiteHazardFootprint> discs)
            {
                int at = -1;
                for (int d = 0; d < discs.Count && at < 0; d++)
                    if (ReferenceEquals(discs[d].Source, _hazardBlock.Source)) at = d;
                if (at < 0) return false;
                if (Vector3.ProjectOnPlane(discs[at].CurrentCentre - _hazardBlock.Centre, Vector3.up).sqrMagnitude
                    <= BlacksiteHazardPolicy.FootprintMargin * BlacksiteHazardPolicy.FootprintMargin) return true;
                Vector3 goal = _refusedGoal;
                if (!_nav.RefuseHazardRoute(goal, ref goal, false, out _, out BlacksiteHazardFootprint still)) return false;
                _hazardBlock = still;
                return true;
            }

            /// <summary>
            /// #1293 R2 (S5): the last verdict refused this member's route to <paramref name="goal"/>.
            /// Cleared by the next verdict and by a raw issue (<see cref="IssueDirect"/>), which issues
            /// a new goal without one.
            /// </summary>
            internal bool LastRefusedRoute(out Vector3 goal)
            {
                goal = _refusedGoal;
                return _refusedVerdictSeq == _verdictSeq;
            }

            private BlacksiteMotionVerdict Report(
                BlacksiteMotionOutcome outcome,
                BlacksiteMotionResult result,
                string issuer,
                string reason)
            {
                var verdict = new BlacksiteMotionVerdict(outcome, result, reason);
                // #1293 R3: a refusal is an Outcome event on the request it first answers, once.
                bool refused = outcome == BlacksiteMotionOutcome.Blocked
                    && (_lastVerdict.Outcome != BlacksiteMotionOutcome.Blocked
                        || !string.Equals(_lastVerdict.Reason, reason, StringComparison.Ordinal));
                _lastVerdict = verdict;
                // #1305 H (C3): any other answer ends a hazard-disc wait.
                if (!string.Equals(reason, BlacksiteMotionVerdict.HazardDiscReason, StringComparison.Ordinal)) EndHazardBlock(issuer);
                _verdictSeq++;
                _nav.ReportMotionRequest(verdict, issuer);
                if (refused) _soldier.RequestDecision(BlacksiteDecisionCause.Outcome);
                return verdict;
            }

            internal void Clear()
            {
                // The generation itself stays monotonic; only the owner it was keyed on goes.
                _routeOwner = null;
                _routeDutyAttempt = _dutyAttempt;
                _hasHeld = false;
                EndHazardBlock(null);
                _nav.ReleaseDestinationClaim();
                EndFollow();
            }

            /// <summary>
            /// #874: the member changed WORLD, so the live goal is a point in the world it left. It
            /// is DROPPED, not re-pointed - this type does not invent goals - and a crossing that
            /// owns the body across the crossing keeps it.
            /// </summary>
            internal void DiscardGoals()
            {
                // #874 P-S2: the follow was taken in the world it left; the planner re-issues it.
                _hasHeld = false;
                EndHazardBlock(null);
                _nav.ReleaseDestinationClaim();
                EndFollow();
            }
        }
        }
    }
}
