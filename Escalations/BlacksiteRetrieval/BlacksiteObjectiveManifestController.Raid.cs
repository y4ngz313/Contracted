using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Contracts.Survey;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed partial class BlacksiteObjectiveManifestController
    {
        private IEnumerable<BlacksiteObjectiveSnapshot> Snapshots
        { get { for (int i = 0; i < Records.Count; i++) yield return Records[i]; } }
        private bool _withdrawalCommitted;
        // #874 Q10: the fallback last written to [UAT][Raid], so the line fires on a change of
        // answer rather than on every 3 s discovery tick.
        private BlacksiteFallbackMission? _fallbackLogged;
        private int _initialRaiders;
        private readonly Dictionary<ulong, Vector3> _pickupApproaches = new Dictionary<ulong, Vector3>();
        private readonly Dictionary<ulong, int> _pickupFailures = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, float> _pickupLogAt = new Dictionary<ulong, float>();
        private readonly Dictionary<int, float> _visited = new Dictionary<int, float>();
        private GameObject[] _insideSearchNodes, _outsideSearchNodes;
        private readonly float[] _regroupUntil = new float[2];
        private sealed class SearchRoute { internal Vector3 Goal; internal float Until, Next; internal bool Outside, Blocked; }
        private readonly Dictionary<ulong, SearchRoute> _searchRoutes = new Dictionary<ulong, SearchRoute>();
        internal bool WithdrawalCommitted => _withdrawalCommitted;

        /// <summary>
        /// #874 S6.2: the ring rotation the duty board is using for this objective right now.
        ///
        /// <see cref="BlacksiteObjectiveApproach"/>'s ring orders its headings by
        /// <c>(i + attempt * Rings) * 30</c>, so the attempt count decides WHICH legal candidate a
        /// resolution publishes first. <c>TryPickupApproach</c> reads it from
        /// <see cref="_pickupFailures"/>; <see cref="BlacksiteObjectiveInteraction"/> resolves the
        /// same lock's ring for the same operator and used a hard-coded zero, so from the first
        /// failed attempt onwards the two owners published different points for one lock and the
        /// operator walked to the one the attempt's travel budget was not measuring. One reader,
        /// one number. Zero for an objective nobody has failed, which is what both sides already
        /// assumed.
        /// </summary>
        internal int ObjectiveAttemptIndex(ulong objectId)
            => objectId != 0UL && _pickupFailures.TryGetValue(objectId, out int attempt) ? attempt : 0;

        // -------------------------------------------------------------------------------------
        // #955 S12A U2: the squad follows ONE committed room route.
        //
        // Before this unit six members each picked the nearest unvisited AI node, which is why the
        // 2026-09-10 March frontier never left a 30 x 15 m pocket. The lead now walks the planner's
        // waypoint (#954), the followers walk BlacksiteFormationPlanner slots around the lead, and
        // the nearest-node search survives only where no route goal is held (#1305 S: never as an
        // unwalkable slot's alternative). The route is shared; the bodies are not.
        // -------------------------------------------------------------------------------------
        private int _routeTickFrame = -1;
        private int _routeVersionSeen = -1;
        private int _routeObjectiveTile = -1;
        private int _routeEnteredFrom = -1;
        private readonly List<BlacksiteSoldierAI> _routeRaiders = new List<BlacksiteSoldierAI>();
        private readonly List<Vector3> _routeMemberPositions = new List<Vector3>();
        // Synchronisation guard (b). The squad's step-off: armed when the lead takes a new leg of
        // the route and spent when the lead's own order is actually committed, which is not the same
        // instant - the lead may still be holding for a separated rear pair when the route changed
        // under it. Follower i is released one entrance-column headway per place after that.
        private float _routeStepOffAt;
        // One entrance column per doorway per mission, keyed by the doorway's own position.
        private readonly Dictionary<long, BlacksiteEntranceColumn> _routeColumns = new Dictionary<long, BlacksiteEntranceColumn>();
        // #1016 S2 review batch 2, F4 (design note G.6): the idle-route-column prune's scratch list.
        private readonly List<long> _routeColumnScratch = new List<long>();
        // Which member holds which corner of the room the squad is standing in; cleared on advance.
        private readonly Dictionary<int, ulong> _routeCornerClaims = new Dictionary<int, ulong>();

        private BlacksiteSquadRoutePlanner RoutePlanner => _mission != null ? _mission.RoutePlanner : null;

        /// <summary>The committed route's next room, for the <c>route=</c> key on the search line.</summary>
        private string RouteNextRoomToken
        {
            get
            {
                var planner = RoutePlanner;
                int next = planner != null ? planner.NextRoom : -1;
                return next >= 0 ? next.ToString() : BlacksiteDiagnosticFormat.None;
            }
        }

        /// <summary>
        /// #955 S12A U2: the interior squad's lead, by the same rule <c>SupportOrSearchOrder</c>
        /// uses for a member in its own world: the lowest-index raider that is still deployed.
        /// </summary>
        /// <remarks>
        /// #950 A5 (O1): a candidate on another storey is not eligible. On 2026-09-12 m1 rode the
        /// car down alone while m0 stayed on the top landing; m0 is still the lowest index, so the
        /// squad's committed route kept being planned from a storey nobody was working, and the man
        /// who was actually in the objective's world was a follower of a lead 40 m above him. The
        /// election therefore prefers the storey that holds the most raiders and, inside it, the
        /// lowest index - which is exactly today's answer whenever the squad is on one storey.
        /// #1293 R2: a stranded member cannot lead; #1305 G5 (D17): nor, while a primary is held, its holder or escort.
        /// </remarks>
        private BlacksiteSoldierAI RouteLead()
        {
            _leadCandidates.Clear();
            foreach (var member in _members
                .Where(s => s != null && !s.isEnemyDead && !s.IsShipGuard && !s.IsRaidWithdrawing
                    && !s.ReplicatedStunned && !s.HasBoardedShip && !s.HasLostTransport && !s.isOutside
                    && !s.Navigator.IsStranded && !HasOwnJob(s))
                .OrderBy(s => s.MemberIndex))
            { _leadCandidates.Add(member); }
            BlacksiteSoldierAI best = null; int bestPeers = -1;
            for (int i = 0; i < _leadCandidates.Count; i++)
            {
                int peers = 0;
                for (int j = 0; j < _leadCandidates.Count; j++)
                    if (!BlacksiteResponsePolicy.OffStorey(
                            _leadCandidates[j].transform.position.y, _leadCandidates[i].transform.position.y))
                        peers++;
                if (peers <= bestPeers) continue;
                bestPeers = peers; best = _leadCandidates[i];
            }
            return _routeElected = best;
        }
        private readonly List<BlacksiteSoldierAI> _leadCandidates = new List<BlacksiteSoldierAI>();
        private BlacksiteSoldierAI _routeElected; // #1305 S: RouteLead's last answer, the one lead whose route tick moves the hold.

        /// <summary>
        /// #955 S12A U2: one squad tick of the committed route, guarded by <c>Time.frameCount</c>
        /// the way <see cref="SpendCandidatePath"/> guards its budget: every member of a squad plans
        /// in the same planner pass (BlacksiteSquadPlanner.Tick), so the route is observed once
        /// and read six times. Returns whether a lead goal is held (<see cref="HoldLeadGoal"/>).
        ///
        /// Interior only. The planner is a room graph over the dungeon's tiles; outdoors there is
        /// no graph, and an outside member keeps today's frontier and support points exactly.
        /// </summary>
        private bool TickSquadRoute(BlacksiteSoldierAI lead)
        {
            if (lead == null) return _routeHasLeadGoal = false; // #1305 S: Tick passes RouteLead(); no inside lead ends the hold.
            if (lead.isOutside) return false;
            if (_routeTickFrame == Time.frameCount) return _routeHasLeadGoal;
            _routeTickFrame = Time.frameCount;
            var planner = RoutePlanner;
            if (planner == null) return false;

            _routeRaiders.Clear(); _routeMemberPositions.Clear();
            foreach (var member in _members
                .Where(s => s != null && !s.isEnemyDead && !s.IsShipGuard && !s.IsRaidWithdrawing
                    && !s.HasBoardedShip && !s.HasLostTransport && !s.isOutside)
                .OrderBy(s => s.MemberIndex))
            { _routeRaiders.Add(member); _routeMemberPositions.Add(member.transform.position); }

            int wasTile = planner.CurrentTile;
            bool advanced = planner.Observe(lead.transform.position, _routeMemberPositions);
            bool objectiveKnown = TryFocusRecord(out var objective, out _);
            int objectiveTile = objectiveKnown ? planner.TileOf(objective.Position) : -1;
            bool needPlan = planner.CommittedRoute.Count == 0
                || planner.RouteVersion != _routeVersionSeen
                || objectiveTile != _routeObjectiveTile;
            if (needPlan)
            {
                planner.TryPlan(lead.transform.position, objectiveKnown ? objective.Position : (Vector3?)null, out _);
                _routeVersionSeen = planner.RouteVersion;
                _routeObjectiveTile = objectiveTile;
            }

            if (advanced)
            {
                // The doorway crossing itself: which tile the squad came from, and a fresh set of
                // corner claims for the room it is now in.
                _routeEnteredFrom = wasTile;
                _routeCornerClaims.Clear();
            }
            if (ReferenceEquals(lead, _routeElected)) HoldLeadGoal(lead, planner);
            return _routeHasLeadGoal;
        }

        /// <summary>
        /// #955 S12A U2: the lead finishes the room it was sent to before it accepts the next one: <c>Observe</c> advances
        /// the route AT THE DOORWAY (tile bounds touch), and a waypoint taken there has the squad tag thresholds, turn round
        /// and leave the followers walking into a room the lead has left. So it is held until the lead arrives at it.
        /// #1305 S: every release is an event, never a clock or a route verdict (R1): the lead's arrival,
        /// his stall or response (<see cref="ReleaseLeadGoal"/>), other work (<see cref="NoteRouteStepOff"/>),
        /// a new lead, no inside lead. No hold at the lead's own feet (R2); the hold arms no follower.
        /// </summary>
        private Vector3 _routeLeadGoal;
        private bool _routeHasLeadGoal;
        private bool _routeLeadGoalMissed;
        private BlacksiteSoldierAI _routeLeadHolder;

        private void HoldLeadGoal(BlacksiteSoldierAI lead, BlacksiteSquadRoutePlanner planner)
        {
            bool kept = _routeHasLeadGoal && ReferenceEquals(lead, _routeLeadHolder);
            if (kept && !_routeLeadGoalMissed && !lead.Navigator.HasArrived(_routeLeadGoal))
                return;
            _routeLeadGoal = planner.LeadWaypoint;
            _routeHasLeadGoal = planner.CommittedRoute.Count > 0 && _routeLeadGoal != lead.transform.position;
            _routeLeadHolder = lead; _routeLeadGoalMissed = false;
            // An arrival or a stall replans the lead itself; any other new hold arms him once.
            if (!kept && _routeHasLeadGoal) Planner.NoteRoomAdvance(lead.NetworkObjectId, false);
        }

        /// <summary>
        /// #1293 R2: the planner's stall answer (<c>BlacksiteSquadPlanner.AnswerStalled</c>) for the
        /// route lead, and #1305 S his response: the next <see cref="HoldLeadGoal"/> takes the route
        /// planner's current waypoint instead of handing the held one back.
        /// </summary>
        internal void ReleaseLeadGoal(BlacksiteSoldierAI member)
        {
            if (_routeHasLeadGoal && member != null && ReferenceEquals(member, RouteLead()))
                _routeLeadGoalMissed = true;
        }

        /// <summary>
        /// #955 S12A U2 (b): the route lead issued a new goal: his followers' beats run from here and (#1305 S) they re-slot
        /// per <see cref="BlacksiteSquadPlannerRules.ArmsFollowers"/>; a goal off the held one is other work and ends the hold.
        /// </summary>
        private void NoteRouteStepOff(BlacksiteSoldierAI soldier, Vector3 goal, float leadMove, bool stall)
        {
            if (soldier == null || soldier.isOutside || !ReferenceEquals(soldier, RouteLead())) return;
            if (Vector3.Distance(goal, _routeLeadGoal) > BlacksiteArrivalPolicy.MinDestinationChange) _routeHasLeadGoal = false;
            _routeStepOffAt = Time.time;
            for (int i = 0; i < _routeRaiders.Count && BlacksiteSquadPlannerRules.ArmsFollowers(stall, leadMove); i++)
                if (!ReferenceEquals(_routeRaiders[i], soldier)) Planner.NoteRoomAdvance(_routeRaiders[i].NetworkObjectId, stall);
        }

        /// <summary>
        /// Whether this follower's place in the column has come up yet. The lead is never held; a
        /// follower waits until the lead has actually stepped off and then one
        /// <see cref="BlacksiteEntranceColumn.HeadwaySeconds"/> per place behind it.
        /// </summary>
        private bool RouteBeatDue(BlacksiteSoldierAI soldier, out float beat)
        {
            beat = 0f;
            if (Returning || soldier.IsRaidWithdrawing) return true; // #1305 G5: a Rally is not the room route's step-off
            int slot = _routeRaiders.IndexOf(soldier);
            if (slot <= 0) return true;
            beat = _routeStepOffAt + slot * BlacksiteEntranceColumn.HeadwaySeconds;
            return Time.time >= beat;
        }

        /// <summary>
        /// The slot order for a follower: <see cref="BlacksiteFormationPlanner.BuildMemberOrder"/>
        /// around the lead, facing the lead's own waypoint, with the seeded presentation offsets
        /// kept so no two members are given the same point. Dead since #850; this is what revives
        /// it. Returns false when this member is the lead or has no place in the raider order, and
        /// the caller keeps today's support point.
        /// </summary>
        private bool TryRouteSlotOrder(BlacksiteSoldierAI soldier, BlacksiteSoldierAI lead,
            Vector3 waypoint, out BlacksiteMemberOrder order)
        {
            order = default;
            // Formation, not a support ring, wherever the room graph knows where the squad is. A
            // squad that has explored everything and has no route left still stands in formation
            // around its lead; it does not go back to ringing him, which is what produced the
            // support-point churn the March run spent its idle seconds on.
            var planner = RoutePlanner;
            if (soldier.isOutside || lead.isOutside || planner == null || planner.CurrentTile < 0) return false;
            int slot = _routeRaiders.IndexOf(soldier);
            if (slot <= 0) return false;
            int count = Mathf.Max(2, _routeRaiders.Count);
            var role = BlacksiteFormationPlanner.ResolveRole(slot, count);
            // The anchor is ROOM-GRANULAR, and that is the whole of the slot tolerance band. While
            // the lead is still walking to its waypoint the formation is built around the waypoint,
            // so a follower's slot is a place in the room the squad is going to and does not creep
            // a metre and a half every second the lead walks; once the lead is standing in that room
            // the anchor is the lead itself, which moves the slots by less than the arrival
            // tolerance and is therefore dropped by the issue gate rather than re-issued. Measured:
            // anchoring on the lead's live position instead gives a follower 17 to 19 issues a
            // minute on the fixture's five-room interior, because the board replans every time the
            // follower arrives at a point the lead has already left; anchoring on the waypoint
            // gives one issue per room. The formation offsets are behind the anchor along the
            // direction of travel, so a follower still trails the lead rather than overtaking it.
            // The formation anchors on what the LEAD is actually doing, which is its own committed
            // duty goal, not on the abstract waypoint: while the lead is holding for a separated
            // rear pair its goal IS its own position, and a column that formed up on the next room
            // instead would walk past the man it is following.
            Vector3 leadGoal = Planner.TryGetTaskGoal(lead, out Vector3 leadTaskGoal)
                ? leadTaskGoal : waypoint;
            bool leadWorkingRoom = lead.Navigator.HasArrived(leadGoal);
            Vector3 anchor = leadWorkingRoom ? lead.transform.position : leadGoal;
            var squadOrder = new BlacksiteSquadOrder(0, BlacksiteSquadOrderKind.SearchLastKnown,
                BlacksiteFormationPlanner.SelectMovementFormation(soldier.isOutside, anchor),
                anchor, waypoint - lead.transform.position);
            order = BlacksiteFormationPlanner.BuildMemberOrder(squadOrder, role, slot, count,
                soldier.AnimationSeed, false);
            if (TryRouteLocalPoint(soldier, lead, role, waypoint, out Vector3 local, out Vector3 facing))
                order = new BlacksiteMemberOrder(squadOrder, local, facing, order.SeparationRadius, order.ArrivalTolerance, false);
            else if (role == BlacksiteSoldierTacticalRole.RearGuard && lead.Navigator.HasArrived(waypoint)
                && TryEntryDoorway(out Vector3 entry))
                // (d) The rear guard keeps the formation's slot and watches the way the squad came
                // in, which is the doorway nothing ahead of it is covering.
                order = new BlacksiteMemberOrder(squadOrder, order.Destination, entry, order.SeparationRadius, order.ArrivalTolerance, false);
            else if (!RouteSlotWalkable(order.Destination))
            {
                // #1305 S: a point ahead (SlotFallback), and says so; a ray hit off the member-to-slot segment is none.
                Vector3 at = soldier.transform.position;
                NavMesh.Raycast(at, order.Destination, out NavMeshHit toward, NavMesh.AllAreas);
                Vector3 ahead = BlacksiteSquadPlannerRules.SlotFallback(at, soldier.GetThreatVelocity(),
                    Vector3.Distance(at, toward.position) <= Vector3.Distance(at, order.Destination) + BlacksiteArrivalPolicy.AgentRadius ? toward.position : (Vector3?)null,
                    Planner.WalkingGoal(soldier), waypoint);
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.LogRateLimited($"route.slot.{soldier.NetworkObjectId}", 3f, "Raid",
                        $"member={soldier.MemberIndex}, formation slot unwalkable, role={role}, slot={order.Destination}, ahead={ahead}.");
                order = new BlacksiteMemberOrder(squadOrder, ahead, ahead, order.SeparationRadius, order.ArrivalTolerance, false);
            }
            return true;
        }

        private static bool RouteSlotWalkable(Vector3 point)
            => NavMesh.SamplePosition(point, out _, BlacksiteArrivalPolicy.AgentRadius, NavMesh.AllAreas);

        /// <summary>
        /// #955 S12A U2, synchronisation guard (c) and (d). (c) While the route's next hop crosses
        /// a doorway, a member already inside one column spacing of that doorway takes its place in
        /// the existing <see cref="BlacksiteEntranceColumn"/> instead of crowding the mouth: the
        /// lead is served, the followers stand <c>BackMetres(place)</c> back along their own
        /// approach, and <c>QueueStopMetres</c> is the settle each place is spaced for, so two
        /// neighbours end up exactly one claim separation apart. (d) Once the lead is standing at
        /// its waypoint and the route has not advanced, the room is worked rather than stared at:
        /// the pointman peeks the doorway toward the next room, the riflemen take the nearest
        /// unclaimed corners, support holds its formation slot and the rear guard faces the doorway
        /// the squad came in by. U4 (#957) owns the facing sweep at those points; this places the
        /// bodies.
        /// </summary>
        private bool TryRouteLocalPoint(BlacksiteSoldierAI soldier, BlacksiteSoldierAI lead,
            BlacksiteSoldierTacticalRole role, Vector3 waypoint, out Vector3 point, out Vector3 facing)
        {
            point = Vector3.zero; facing = Vector3.zero;
            var planner = RoutePlanner;
            int tile = planner != null ? planner.CurrentTile : -1;
            if (tile < 0) return false;
            int next = planner.NextRoom;
            ulong id = soldier.NetworkObjectId;

            if (next >= 0 && TryHopDoorway(planner, tile, waypoint, out Vector3 doorway))
            {
                var column = RouteColumn(doorway);
                if (Vector3.Distance(soldier.transform.position, doorway) <= BlacksiteEntranceColumn.SpacingMetres)
                {
                    column.Serve(lead.NetworkObjectId, Time.time);
                    int place = column.Place(id, Time.time);
                    Vector3 back = Vector3.ProjectOnPlane(soldier.transform.position - doorway, Vector3.up);
                    if (back.sqrMagnitude < .01f) back = -Vector3.ProjectOnPlane(waypoint - doorway, Vector3.up);
                    if (back.sqrMagnitude < .01f) return false;
                    Vector3 place3 = doorway + back.normalized * BlacksiteEntranceColumn.BackMetres(place);
                    if (!NavMesh.SamplePosition(place3, out var queued, BlacksiteEntranceColumn.QueueStopMetres, NavMesh.AllAreas))
                        return false;
                    point = queued.position; facing = doorway;
                    return true;
                }
                column.Leave(id);
            }

            // The room is only worked while the lead is actually standing in it.
            if (!lead.Navigator.HasArrived(waypoint)) return false;
            switch (role)
            {
                case BlacksiteSoldierTacticalRole.Pointman:
                    if (next < 0 || !TryHopDoorway(planner, tile, waypoint, out Vector3 peek)) return false;
                    point = peek; facing = planner.LeadWaypoint;
                    return NavMesh.SamplePosition(point, out var peeked, BlacksiteArrivalPolicy.AgentRadius * 2f, NavMesh.AllAreas)
                        && Assign(ref point, peeked.position);
                case BlacksiteSoldierTacticalRole.Rifleman:
                {
                    var corners = planner.CornerTargets(tile);
                    int chosen = -1; float best = float.PositiveInfinity;
                    for (int i = 0; i < corners.Count; i++)
                    {
                        if (_routeCornerClaims.TryGetValue(i, out ulong owner) && owner != id) continue;
                        float distance = Vector3.Distance(soldier.transform.position, corners[i]);
                        if (distance >= best) continue;
                        best = distance; chosen = i;
                    }
                    if (chosen < 0) return false;
                    _routeCornerClaims[chosen] = id;
                    point = corners[chosen];
                    // Cover the corner outward, away from the room's own centre.
                    facing = point + (point - planner.LeadWaypoint);
                    return true;
                }
                default:
                    return false;
            }
        }

        private static bool Assign(ref Vector3 target, Vector3 value) { target = value; return true; }

        /// <summary>
        /// The doorway the squad entered the current tile by: the doorway of the current tile that
        /// is nearest a doorway of the tile it came from, which for a real pair is the same world
        /// position on both sides.
        /// </summary>
        private bool TryEntryDoorway(out Vector3 entry)
        {
            entry = Vector3.zero;
            var planner = RoutePlanner;
            if (planner == null || _routeEnteredFrom < 0 || planner.CurrentTile < 0) return false;
            var here = planner.DoorwayTargets(planner.CurrentTile);
            var there = planner.DoorwayTargets(_routeEnteredFrom);
            float best = float.PositiveInfinity;
            for (int i = 0; i < here.Count; i++)
                for (int j = 0; j < there.Count; j++)
                {
                    float distance = Vector3.Distance(here[i], there[j]);
                    if (distance >= best) continue;
                    best = distance; entry = here[i];
                }
            return best < float.PositiveInfinity;
        }

        /// <summary>The doorway of <paramref name="tile"/> nearest the hop the route is taking.</summary>
        private static bool TryHopDoorway(BlacksiteSquadRoutePlanner planner, int tile, Vector3 toward, out Vector3 doorway)
        {
            doorway = Vector3.zero;
            var doorways = planner.DoorwayTargets(tile);
            float best = float.PositiveInfinity;
            for (int i = 0; i < doorways.Count; i++)
            {
                float distance = Vector3.Distance(doorways[i], toward);
                if (distance >= best) continue;
                best = distance; doorway = doorways[i];
            }
            return best < float.PositiveInfinity;
        }

        // One column per doorway per mission. The key is the doorway's own position quantised to
        // half a metre, which is well inside the 2.2 m spacing two places are apart, so one doorway
        // can never resolve to two columns or two doorways to one.
        // #990 S16 D3: extracted so the routeless idle rule stamps a doorway it has visited with
        // the same identity this column keys on, rather than inventing a second quantisation.
        private static long DoorwayKey(Vector3 doorway)
            => ((long)Mathf.RoundToInt(doorway.x * 2f) << 42)
                ^ ((long)Mathf.RoundToInt(doorway.y * 2f) << 21)
                ^ Mathf.RoundToInt(doorway.z * 2f);
        private BlacksiteEntranceColumn RouteColumn(Vector3 doorway)
        {
            long key = DoorwayKey(doorway);
            if (!_routeColumns.TryGetValue(key, out var column))
                _routeColumns[key] = column = new BlacksiteEntranceColumn();
            // #1016 S2 (design note G.6): _routeColumns was never pruned when idle - only Leave on
            // forget (Duties.cs:944) and a wholesale Clear on reset (:973) - unlike the director's
            // interaction columns. The only observable difference once it is is that a doorway
            // revisited after a long gap starts a fresh column at place 0 instead of inheriting one,
            // which is what the interaction columns have always done.
            //
            // #1016 S2 review batch 2, F4: the prune is PruneRouteColumns, on this manifest's own
            // broker cadence, not the broker sweep - Bind REGISTERS this lane and the sweep is
            // forbidden from removing a registered one, because a registered lane is one a facade
            // still holds. Registering it is what puts the doorway under the one sweep for expiry;
            // dropping it when the column goes idle is this type's own job.
            column.Bind(
                _mission != null && _mission.IsServer ? _mission.Blackboard.Broker : null,
                BlacksiteResourceKey.RouteDoorway(key));
            return column;
        }

        /// <summary>
        /// #1016 S2 review batch 2, F4 (decision G6): forget route columns nobody is standing in.
        ///
        /// Same threshold and same shape as the director's interaction-column prune - idle means
        /// the lane is empty and no column is still being measured - and <c>Unbind</c> drops the
        /// registration with the column, so the broker's lane table shrinks with this dictionary
        /// rather than accumulating one permanent lane per doorway the squad has ever routed
        /// through.
        /// </summary>
        private void PruneRouteColumns(float now)
        {
            if (_routeColumns.Count == 0) return;
            _routeColumnScratch.Clear();
            foreach (KeyValuePair<long, BlacksiteEntranceColumn> entry in _routeColumns)
                if (entry.Value.IsIdle(now)) _routeColumnScratch.Add(entry.Key);
            for (int i = 0; i < _routeColumnScratch.Count; i++)
            {
                if (_routeColumns.TryGetValue(_routeColumnScratch[i], out BlacksiteEntranceColumn idle))
                    idle.Unbind();
                _routeColumns.Remove(_routeColumnScratch[i]);
            }
            _routeColumnScratch.Clear();
        }
        // #1072: the highest deployed raid-duty count this manifest has actually SEEN. See
        // UpdateRaidState for why the planned member count cannot be the denominator.
        private int _peakDeployedRaiders;

        /// <summary>
        /// #1072 (rework A1): who the squad-casualty rule is ABOUT, by deployment DUTY.
        ///
        /// <c>IsShipGuard</c> cannot be the predicate on either side of the ratio:
        /// <c>IsShipGuard => IsRifleGuard &amp;&amp; !GuardCalledIn(id)</c>
        /// (BlacksiteSoldierAI.cs:567), so a rifle guard the backup rule has dispatched counts as a
        /// raider for the duration of the call - and TickGuardBackup calls every guard in at once
        /// (Backup.cs:137-138). One such tick would have latched a peak of six on a four-raider
        /// squad, and after the guards returned to post a SINGLE casualty would then have satisfied
        /// fit &lt;= 6/2, where the baseline rule needed two of four.
        ///
        /// <c>BlacksiteDeploymentPolicy.Duty</c> is a pure function of the member index and the
        /// squad size - the same classification BlacksiteSoldierAI assigns its duty from - so it is
        /// immune both to a call-in and to the equipment fact <c>IsRifleGuard</c> reads (without the
        /// Better Armory pack every duty carries a baton and IsRifleGuard is false for guards too).
        /// Numerator and denominator both use it, so a call-in cannot distort the ratio in either
        /// direction, and the peak is safe to sample before the backup suspension below.
        /// </summary>
        private bool IsRaidDuty(BlacksiteSoldierAI soldier)
            => soldier != null && BlacksiteDeploymentPolicy.Duty(soldier.MemberIndex, _mission.MemberCount)
                != BlacksiteDeploymentDuty.ShipGuard;

        private void UpdateRaidState(int raiders)
        {
            // #1072: _initialRaiders is an observation, not a plan.
            //
            // It used to be _mission.MemberCount minus the guard share - the PLANNED squad size,
            // written by ConfigureSquadServer before a single soldier exists - while `fit` was
            // counted off the spawned roster the deploy slice was still filling one soldier per
            // frame. On 2026-09-15 that gave WithdrawSquad(4, 2, 0) on the tick the second soldier
            // registered, on Dine (30280) and again on March (36829), and the squad ordered its own
            // extraction before it had left the ship. The denominator is now the peak raid-duty
            // count actually observed, so it can only ever describe men who really deployed, and it
            // is monotonic so a casualty still shrinks `fit` against the strength the squad reached.
            int deployed = _members.Count(IsRaidDuty);
            if (deployed > _peakDeployedRaiders) _peakDeployedRaiders = deployed;
            _initialRaiders = _peakDeployedRaiders;
            // #874 N4: the hold masks the rule and nothing else. The tick it ends reads the whole
            // rule below, the objective deferral included, instead of committing a casualty count
            // that went stale behind it (round 1: t=139.28 read at t=162.50, log 6212).
            if (_backupGuards.Count > 0 && Time.time - _backupBegan < 35f) return;
            int fit = _members.Count(s => IsRaidDuty(s) && !s.IsRaidWithdrawing);
            // #1072: and the rule is not read at all while the drain is still handing out soldiers.
            // The signal is the drain's own pending flag rather than a count, so a spawn that fails
            // mid-slice clears it with the deployment instead of hanging the raid in "still
            // deploying" forever.
            if (!_withdrawalCommitted && BlacksiteRaidPolicy.WithdrawSquad(_initialRaiders, fit,
                    Time.time - _startedAt, _mission.SoldierDeploymentPending))
            {
                // #988 B5: a withdrawal is not ordered through a live contact. The 2026-09-12 run
                // committed at t=405.38 (`withdrawal committed: fit=2/4`) with the crawler standing
                // between members 2 and 3 and the exit; both took the Withdrawal goal (L11641,
                // L11668), both flipped back to Combat at t=408.79 and t=409.71 when it reached
                // them, and both died. The order is DEFERRED rather than re-routed, which is the
                // conservative half of the brief: the rule the withdrawal is deferred by is the
                // same ThreatRadius that makes a contact a threat for every other consumer, so it
                // clears the moment the monster is dead or WithdrawalContactClearRange out and the
                // commit is then taken on the next raid tick with its own conditions re-tested.
                // Nothing suppresses a withdrawal already committed, and nothing here touches the
                // per-member Combat-over-Withdrawal arbitration that owns a member already walking.
                if (TryFindWithdrawalBlockingContact(out int blockedMember, out float blockedRange))
                {
                    BlacksiteDiagnostics.LogRateLimited("raid.withdrawal.deferred", 2f, "Raid",
                        $"withdrawal deferred: fit={fit}/{_initialRaiders}, elapsed={Time.time-_startedAt:0}s, "
                        + $"blockedBy=member:{blockedMember}, contactRange={blockedRange:0.00}, "
                        + $"clearAt={BlacksiteCombatPolicy.WithdrawalContactClearRange:0.0}m, reason=liveContact.");
                    return;
                }
                // #874 N4 (decision 1): nor while a raider inside is making progress on the primary.
                // Deferred, not cancelled: the rule is read again every raid tick and commits on the
                // first one where the holder has stalled twice, given up, died or left.
                if (TryFindProgressingObjectiveHolder(Time.time - _startedAt, out int holder, out ulong holdObject))
                {
                    BlacksiteDiagnostics.LogRateLimited("raid.withdrawal.deferred.objective", 2f, "Raid",
                        $"withdrawal deferred: fit={fit}/{_initialRaiders}, elapsed={Time.time-_startedAt:0}s, "
                        + $"blockedBy=holder:{holder}, object={holdObject}, reason=objectiveProgress.");
                    return;
                }
                _withdrawalCommitted = true;
                BlacksiteDiagnostics.Log("Raid", $"withdrawal committed: fit={fit}/{_initialRaiders}, elapsed={Time.time-_startedAt:0}s, loaded={LoadedCount}.");
                foreach (var member in _members) member.AnnounceRaidAction();
            }
        }

        // #988 B5: the nearest live monster contact any raider is actually holding. Guards are
        // excluded for the same reason they are excluded from the fit count - they are not the
        // members the withdrawal is ordering - and the range is measured from the member, which is
        // what "the monster is between them and the exit" reduces to at this radius.
        private bool TryFindWithdrawalBlockingContact(out int member, out float range)
        {
            member = -1; range = float.PositiveInfinity;
            foreach (var soldier in _members)
            {
                if (soldier == null || soldier.isEnemyDead || soldier.IsShipGuard) continue;
                if (!soldier.TryGetLiveMonsterContact(out _, out float distance)) continue;
                if (distance > BlacksiteCombatPolicy.WithdrawalContactClearRange || distance >= range) continue;
                member = soldier.MemberIndex; range = distance;
            }
            return member >= 0;
        }
        // #874 N4: the raider inside holding the primary objective and not leaving, if any. The
        // rule is BlacksiteRaidPolicy.DeferWithdrawalForObjective; this only reads the board's rows.
        private bool TryFindProgressingObjectiveHolder(float elapsed, out int member, out ulong objectId)
        {
            member = -1; objectId = 0;
            for (int i = 0; i < Records.Count; i++)
            {
                var r = Records[i];
                if (!IsPrimaryObjective(r.Kind)
                    || (r.Phase != BlacksiteObjectivePhase.Reserved && r.Phase != BlacksiteObjectivePhase.Carried)) continue;
                var holder = Soldier(r.CarrierId);
                if (holder == null || holder.isEnemyDead || !IsRaidDuty(holder)) continue;
                if (!BlacksiteRaidPolicy.DeferWithdrawalForObjective(elapsed, true, !holder.isOutside,
                        holder.IsRaidWithdrawing || holder.HasBoardedShip || holder.HasLostTransport)) continue;
                member = holder.MemberIndex; objectId = r.ObjectId;
                return true;
            }
            return false;
        }

        private void DiscoverFallbackObjectives()
        {
            // #974: a raid with nothing to do is a raid whose squad is on the ground.
            if (Ship == null || !Ship.SquadOnMoon) return;
            // #874 Q10 (Lawson, 2026-09-24, decision 3): the fallback is the work of a raid that
            // has NO contract objective on this moon. A Payload, Shadow Raid or escorting
            // Whistleblower raid never substitutes a mission for its own: on 2026-09-23 round 3 a
            // Blocked witness attempt fell through to "fallback mission: apparatus" and two members
            // carried the apparatus off while the witness was still the contract. Both callers -
            // Discover's tick and the unreachable-record release in Jobs.cs - go through this one
            // test. Without a primary the apparatus is the fallback whenever an eligible one exists,
            // else salvage (DiscoverSalvage); #1305 G5 (D17) salvage while a primary is held is Discover's.
            var mode = BlacksiteContractModes.Mode(_mission.Contract);
            bool hasPrimary = BlacksiteContractModes.HasPrimaryObjective(mode, BlacksiteCustodySettings.EscortEnabled);
            // #1305 E2: settled while an apparatus is still owed (any phase but written off) or once the
            // one salvage batch exists - a bounded set per raid, not repeated harvesting. A written-off
            // apparatus settles nothing and is never selected again, so the raid falls to salvage.
            if (!hasPrimary && Snapshots.Any(r => r.Kind == BlacksiteObjectiveKind.Salvage
                    || r.Kind == BlacksiteObjectiveKind.Apparatus && r.Phase != BlacksiteObjectivePhase.Unavailable))
                return; // deployment intent and a bounded target set, not endless harvesting
            var lung = hasPrimary ? null : UnityEngine.Object.FindObjectsOfType<LungProp>().Where(l => EligibleApparatus(l) && !Existing(l.NetworkObjectId))
                .OrderBy(l => Vector3.Distance(l.transform.position, _mission.SquadDirector.InsideMainEntrance)).FirstOrDefault();
            var fallback = BlacksiteContractModes.Fallback(hasPrimary, lung != null);
            if (_fallbackLogged != fallback)
            {
                _fallbackLogged = fallback;
                BlacksiteDiagnostics.Log("Raid", $"fallback mission selected: {fallback}, mode={mode}, limit={AliveRaiders}.");
            }
            if (fallback == BlacksiteFallbackMission.None) return;
            if (fallback == BlacksiteFallbackMission.Apparatus) { Add(lung.NetworkObject, BlacksiteObjectiveKind.Apparatus); return; }
            DiscoverSalvage();
        }
        private void DiscoverSalvage()
        {
            if (Ship == null || !Ship.SquadOnMoon) return;
            if (Snapshots.Any(r => r.Kind == BlacksiteObjectiveKind.Salvage)) return; // one batch per raid
            // #1096 (WP5), Lawson 2026-09-16: the hurry drops the optional work too. Registering fresh
            // salvage while a Jester is winding up nearby sends members back INTO its room for scrap
            // that was never the mission; the fallback selection and the apparatus retrieval are
            // untouched, and the skip lasts only as long as the crank (the 0.2 s fact the role speed reads).
            if (BlacksiteVanillaMonsters.CrankingJesterKnown) return;
            var nearest = UnityEngine.Object.FindObjectsOfType<GrabbableObject>()
                .Where(p => p != null && p.NetworkObject != null && p.NetworkObject.IsSpawned
                    && p.itemProperties != null && p.itemProperties.isScrap && p.scrapValue > 0
                    && p.isInFactory && p.grabbable && !PlayerHeld(p) && !CrewSecured(p)
                    && !p.isHeldByEnemy && !p.hasBeenHeld && !Existing(p.NetworkObjectId)
                    && !ExcludedSalvageIdentity(p))
                .OrderBy(p => Vector3.Distance(p.transform.position, _mission.SquadDirector.InsideMainEntrance));
            // #1153: distance to the entrance was the whole selection, and distance is not
            // reachability. On 2026-09-20 the three nearest objects in Grand Armory (537, 533, 532)
            // registered and none was ever picked up: 533 refused nine rings on navmesh, 537 sat
            // behind wall geometry four members were blocked against. A target the squad cannot
            // route to is not a target, so the ORDERED list is walked and only a Reachable verdict of
            // the route service (#1305 G1 R15) from the object's island origin (ReachabilityOrigin) registers.
            // #1305 E2 (D11): one item per alive raider (AliveRaiders), each carried in parallel; the
            // solves are bounded to four times it and the walk stops at the limit accepted. The two
            // early-outs above stand in front of every solve, on Discover's 3 s cadence.
            int accepted = 0, solved = 0, limit = AliveRaiders;
            foreach (var item in nearest)
            {
                if (accepted >= limit || solved >= limit * 4) break;
                solved++;
                if (!BlacksiteReachability.Route(BlacksiteTraveller.Soldier(false), ReachabilityOrigin(item.transform.position, false), item.transform.position).Reachable) continue;
                Add(item.NetworkObject, BlacksiteObjectiveKind.Salvage);
                accepted++;
            }
            // #1271 S8: one objective call for the registering pass, not one line per member; the
            // voice director gives it to the Leader.
            if (Snapshots.Any(r => r.Kind == BlacksiteObjectiveKind.Salvage))
            {
                BlacksiteSoldierAI caller = _members.FirstOrDefault(s => s != null);
                if (caller != null) caller.AnnounceObjectiveSighted();
            }
        }
        /// <summary>
        /// #948 S12 U3: the objects this squad never registers as salvage, by identity rather than
        /// by circumstance. The apparatus, the survey drone and the breach drive were already here
        /// inline; the sapsucker egg joins them because vanilla makes a HELD egg an instant,
        /// permanent death sentence (GiantKiwiAI.cs:562-583, :1028) and today it is excluded only
        /// incidentally, by the isInFactory filter an outdoor nest happens to fail. Extracted so the
        /// registration rule and the pickup gate ask one predicate and a fixture can drive it
        /// without a spawned NetworkObject.
        /// </summary>
        private static bool ExcludedSalvageIdentity(GrabbableObject p)
            => p == null || p is LungProp || p.GetComponent<SurveyDroneItem>() != null
                || p.GetComponent<BreachHardDriveItem>() != null
                || BlacksiteHazardAwareness.IsProtectedEgg(p);

        /// <summary>
        /// #948 S12 U3: the pickup gate's half of the same rule. An egg can never be picked up even
        /// if something else has somehow registered it, and the refusal is an ordinary
        /// pickupRejected event with a named reason rather than a silent skip.
        /// </summary>
        private bool ProtectedEggPickup(BlacksiteObjectiveSnapshot record, GrabbableObject prop, BlacksiteSoldierAI carrier)
            => BlacksiteHazardAwareness.IsProtectedEgg(prop)
                && PickupRejected(record, "sapsucker egg is never carried", carrier);

        private static bool EligibleApparatus(LungProp lung) => lung != null && lung.NetworkObject != null
            && lung.NetworkObject.IsSpawned && lung.isInFactory && !PlayerHeld(lung) && !CrewSecured(lung)
            && !lung.isHeldByEnemy && !lung.hasBeenHeld;
        // #874 S4.0a: the rejection is now an event in the S4.0 contract's shape, and it names the
        // member and its distance to the object. The rejection machinery itself is unchanged: the
        // same three-second per-object log throttle, the same false return.
        private bool PickupRejected(BlacksiteObjectiveSnapshot record, string reason, BlacksiteSoldierAI soldier = null)
        {
            if (!_pickupLogAt.TryGetValue(record.ObjectId, out float next) || Time.time >= next)
            {
                _pickupLogAt[record.ObjectId] = Time.time + 3f;
                BlacksiteDiagnostics.Log("Objective",
                    $"event=pickupRejected, object={record.ObjectId}, kind={record.Kind}"
                    + $", member={(soldier != null ? soldier.MemberIndex.ToString() : "none")}"
                    + $", reason={reason}"
                    + $", distance={(soldier != null ? Vector3.Distance(soldier.transform.position, record.Position).ToString("0.00") : "none")}");
            }
            return false;
        }
        private Vector3 PickupApproach(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI soldier)
        {
            // #950 S11H U2: a refused resolution publishes NO destination. The out value on a false
            // return is the record's own position, which is the measurement baseline ObserveJob
            // needs and, for a sealed container, the point inside it that the squad was ordered to
            // walk to for 295 s on 2026-09-10. An operator with no standing point holds where he
            // is until his own close asks his route to it (HolderReach); Unsupported from the second failure writes it off.
            if (!TryPickupApproach(record, soldier, out var destination))
            {
                // #1018 S4 (design note C.5, block 7): the refusal now SAYS SO. The out value is
                // still the record's own position - the measurement baseline ObserveJob needs - but
                // the blocked cause is written, so the planner's goal-kind resolution reads this as
                // a hold at the member's own feet rather than as an ordinary travel leg it happens
                // to be standing on. Nothing about the escalation changes: the objective's own
                // unreachable rule still ends it at the holder's own close (HolderReach).
                _planningBlocked[soldier.NetworkObjectId] = BlacksiteBlockedCause.NoReachableCandidate;
                return soldier.transform.position;
            }
            return destination;
        }
        // #874 S6.4b: the record the squad is organised around. Carried outranks Reserved outranks
        // Available, and an unattainable phase never anchors anything.
        /// <summary>
        /// #950 A4: which objective the squad is working, as one id, for a decision that has to be
        /// taken once per SQUAD OBJECTIVE rather than once per member. 0 when nothing is known,
        /// which is also the one token every memberless caller shares.
        /// </summary>
        internal ulong FocusObjectiveId => TryFocusRecord(out var focus, out _) ? focus.ObjectId : 0UL;

        // #1233 S2: the primary focus record is the mission world's owner (RequiresInterior), so the
        // precedence is the pure BlacksiteMissionWorld.TakesFocus.
        private bool TryFocusRecord(out BlacksiteObjectiveSnapshot record, out bool anyKnownObjective)
        {
            record = default; anyKnownObjective = false; int rank = 0;
            foreach (var r in Records)
            {
                anyKnownObjective |= r.Kind != BlacksiteObjectiveKind.Loot;
                if (BlacksiteMissionWorld.TakesFocus(r.Kind, r.Phase, ref rank)) record = r;
            }
            return rank > 0;
        }
        private BlacksiteSquadFocus ResolveSquadFocus(BlacksiteSoldierAI soldier,
            out BlacksiteObjectiveSnapshot record, out bool hasRecord, out BlacksiteSoldierAI holder)
        {
            hasRecord = TryFocusRecord(out record, out bool anyKnownObjective);
            holder = null;
            if (hasRecord && (record.Phase == BlacksiteObjectivePhase.Reserved || record.Phase == BlacksiteObjectivePhase.Carried))
            {
                var owner = Soldier(record.CarrierId);
                if (owner != null && !owner.isEnemyDead) holder = owner;
            }
            // #1233 S3 (rule 2): the one world term left is the live holder's own, read every plan.
            return BlacksiteSquadFocusPolicy.Resolve(
                Returning || _withdrawalCommitted || soldier.IsRaidWithdrawing,
                anyKnownObjective, hasRecord,
                holder != null && record.Phase == BlacksiteObjectivePhase.Reserved && StandRefusedFromAfar(record, holder),
                holder != null && record.Phase == BlacksiteObjectivePhase.Carried,
                holder != null && record.Phase == BlacksiteObjectivePhase.Reserved,
                holder != null && holder.isOutside == soldier.isOutside, Sweeps(soldier));
        }
        // One line per real change of a member's anchor, never per tick.
        private readonly Dictionary<ulong, (BlacksiteSquadFocusKind Kind, ulong Object)> _focusLogged
            = new Dictionary<ulong, (BlacksiteSquadFocusKind, ulong)>();
        private void NoteSquadFocus(BlacksiteSoldierAI soldier, BlacksiteSquadFocus focus, ulong objectId)
        {
            if (_focusLogged.TryGetValue(soldier.NetworkObjectId, out var was)
                && was.Kind == focus.Kind && was.Object == objectId) return;
            _focusLogged[soldier.NetworkObjectId] = (focus.Kind, objectId);
            BlacksiteDiagnostics.Log("Objective",
                $"event=focus, anchor={focus.Anchor}"
                + $", object={(objectId == NoCarrier ? "none" : objectId.ToString())}"
                + $", member={soldier.MemberIndex}, reason={focus.Reason}");
        }
    }
}
