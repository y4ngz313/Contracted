using System;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed partial class BlacksiteObjectiveManifestController
    {
        private sealed class PlayerSearch
        {
            internal int Revision;
            internal float RevisitAt;
            internal readonly HashSet<int> Checked = new HashSet<int>();
            internal readonly Dictionary<int, float> BlockedUntil = new Dictionary<int, float>();
        }
        private sealed class PlayerAssignment
        {
            // #1293 R1: the Player row of the squad's world model this response is about, and its
            // player's client id. The row is read from the table on every use, never held.
            internal ulong Key, ClientId;
            internal bool Tracker, HasGoal, Outside, Cover, WasVisible;
            internal int Revision, GoalKey;
            internal Vector3 Goal, Center;
            internal float NextPlan, ArrivedAt = float.NaN, GoalUntil;
        }
        private readonly Dictionary<ulong, ulong> _playerTrackers = new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, PlayerAssignment> _playerAssignments = new Dictionary<ulong, PlayerAssignment>();
        private readonly Dictionary<ulong, PlayerSearch> _playerSearches = new Dictionary<ulong, PlayerSearch>();
        private float _nextPlayerAssignments;
        // #1293 R1: the table's ChangeCount at the last refresh; a new, reacquired or forgotten row refreshes at once.
        private int _playerRowsChangeCount;
        private readonly Dictionary<ulong, float> _playerResponseRetry = new Dictionary<ulong, float>();
        // #1293 R1: RefreshPlayerAssignments' scratch, refilled on every refresh instead of LINQ arrays.
        private readonly List<BlacksiteWorldRow> _playerRows = new List<BlacksiteWorldRow>();
        private readonly List<BlacksiteSoldierAI> _playerResponders = new List<BlacksiteSoldierAI>();
        private readonly HashSet<ulong> _usedResponders = new HashSet<ulong>();
        private readonly HashSet<ulong> _wantedResponders = new HashSet<ulong>();
        private readonly List<ulong> _staleResponseKeys = new List<ulong>();
        private static readonly Comparison<BlacksiteWorldRow> NewestEvidenceFirst = (a, b) =>
        {
            int seen = b.SeenAt.CompareTo(a.SeenAt);
            return seen != 0 ? seen : ClientOf(a).CompareTo(ClientOf(b));
        };
        private static readonly Comparison<BlacksiteSoldierAI> ByMemberIndex = (a, b) => a.MemberIndex.CompareTo(b.MemberIndex);
        private static ulong ClientOf(BlacksiteWorldRow row) => ((PlayerControllerB)row.Subject).actualClientId;
        private void DeferPlayerResponse(BlacksiteSoldierAI soldier, BlacksiteBlockedCause cause)
        {
            ulong id = soldier.NetworkObjectId;
            if (_playerAssignments.TryGetValue(id, out var assignment))
            {
                if (assignment.HasGoal && _playerSearches.TryGetValue(assignment.ClientId, out var search))
                    search.BlockedUntil[assignment.GoalKey] = Time.time + 15f;
                _playerAssignments.Remove(id);
            }
            _playerResponseRetry[id] = Time.time + 8f;
            _planningBlocked.Remove(id);
            BlacksiteDiagnostics.Log("PlayerSearch", $"member={soldier.MemberIndex}, result=Invalidated, cause={cause}, retryAfter=8s; contact knowledge retained.");
        }

        private bool CanRespondToPlayer(BlacksiteSoldierAI soldier)
        {
            // A carried object or an extraction commitment retains its existing safety policy.
            // An ordinary sweep or guard patrol can yield without losing its job; an operator with
            // an open reservation attempt and a member holding a Coil-Head still cannot, because
            // pulling either one starves work that nobody else can take over. #1293 R2: a stranded
            // member cannot walk to a player, so he is not a responder.
            return soldier != null && soldier.isActiveAndEnabled && !Returning && !soldier.isEnemyDead && !soldier.HasBoardedShip
                && !soldier.HasLostTransport && !soldier.IsRaidWithdrawing && !IsCarrier(soldier.NetworkObjectId)
                && !soldier.IsWatchingMonster && !soldier.Navigator.IsStranded
                && !HoldsOpenReservationAttempt(soldier.NetworkObjectId)
                && !(_playerResponseRetry.TryGetValue(soldier.NetworkObjectId, out float retry) && Time.time < retry)
                && !soldier.IsAboardShip
                && !PickedUpInLastTwoSeconds(soldier.NetworkObjectId)
                && !(soldier.HasActiveWorldInteraction && HoldsAnyRecord(soldier.NetworkObjectId));
        }

        // #1219 B4: CanRespondToPlayer's three record scans as index loops (the #1018 S4 G10 pattern
        // EscortWorkOutstanding uses). The planner now asks it several times per member per pass,
        // and `Snapshots.Any(lambda)` allocated an iterator and a closure on every call. Same rows,
        // same predicates, same order.
        private bool HoldsOpenReservationAttempt(ulong member)
        {
            for (int i = 0; i < Records.Count; i++)
            {
                var r = Records[i];
                if (r.CarrierId == member && r.Phase == BlacksiteObjectivePhase.Reserved
                    && _jobs.TryGetValue(r.ObjectId, out var open) && open.AttemptOpen)
                    return true;
            }
            return false;
        }

        private bool PickedUpInLastTwoSeconds(ulong member)
        {
            for (int i = 0; i < Records.Count; i++)
            {
                var r = Records[i];
                if (r.CarrierId == member && _pickupAt.TryGetValue(r.ObjectId, out var pickup) && Time.time - pickup.Ready < 2f)
                    return true;
            }
            return false;
        }

        private bool HoldsAnyRecord(ulong member)
        {
            for (int i = 0; i < Records.Count; i++)
                if (Records[i].CarrierId == member) return true;
            return false;
        }

        private void RefreshPlayerAssignments()
        {
            var world = _mission.SquadDirector.World;
            if (Time.time < _nextPlayerAssignments && world.ChangeCount == _playerRowsChangeCount) return;
            _nextPlayerAssignments = Time.time + .5f; _playerRowsChangeCount = world.ChangeCount;
            // #1293 R1: the contacts are the Player rows of the squad's world model, of any source,
            // newest evidence first. A response lasts while its row does.
            var rows = world.Rows;
            _playerRows.Clear();
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Class == BlacksiteWorldClass.Player) _playerRows.Add(rows[i]);
            _playerRows.Sort(NewestEvidenceFirst);
            _playerResponders.Clear();
            foreach (var member in _members) if (CanRespondToPlayer(member)) _playerResponders.Add(member);
            _playerResponders.Sort(ByMemberIndex);
            _usedResponders.Clear(); _wantedResponders.Clear(); _staleResponseKeys.Clear();
            foreach (ulong client in _playerTrackers.Keys) if (PlayerRowOf(client) == null) _staleResponseKeys.Add(client);
            foreach (ulong client in _staleResponseKeys) _playerTrackers.Remove(client);
            // Reserve existing trackers first: a new sighting must not steal another player's owner.
            foreach (var row in _playerRows)
            {
                ulong client = ClientOf(row);
                if (!_playerTrackers.TryGetValue(client, out ulong owner)) continue;
                if (!RespondsAsRaider(owner)) _playerTrackers.Remove(client);
                else _usedResponders.Add(owner);
            }
            foreach (var row in _playerRows)
            {
                ulong client = ClientOf(row);
                if (!_playerTrackers.TryGetValue(client, out ulong owner))
                {
                    var tracker = NearestTracker(row);
                    if (tracker == null) continue;
                    tracker.AnnounceSearch(false);
                    owner = tracker.NetworkObjectId; _playerTrackers[client] = owner; _usedResponders.Add(owner);
                    BlacksiteDiagnostics.Log("PlayerSearch", $"tracker member={tracker.MemberIndex}, client={client}, observed={row.Position}.");
                }
                AssignPlayerResponse(owner, row, true, false); _wantedResponders.Add(owner);
            }
            int helper = 0;
            foreach (var member in _playerResponders)
            {
                ulong id = member.NetworkObjectId;
                if (_usedResponders.Contains(id)) continue;
                _playerAssignments.TryGetValue(id, out var previous);
                var kept = previous != null && !previous.Tracker ? PlayerRowOf(previous.ClientId) : null;
                var contact = kept != null && member.AllowsPlayerResponse(kept.Position, kept.Outside) ? kept : NearestAssist(member);
                if (contact == null) continue;
                // Keep a covering pair while other helpers check separate nearby frontiers.
                bool cover = member.IsShipGuard || (kept == contact ? previous.Cover : helper % 2 == 0);
                helper++;
                AssignPlayerResponse(id, contact, false, cover); _wantedResponders.Add(id);
            }
            _staleResponseKeys.Clear();
            foreach (ulong id in _playerAssignments.Keys) if (!_wantedResponders.Contains(id)) _staleResponseKeys.Add(id);
            foreach (ulong id in _staleResponseKeys)
            {
                BlacksiteDiagnostics.Log("PlayerSearch", $"memberId={id}, response ended; resume retained mission duty while alert.");
                _playerAssignments.Remove(id);
            }
            _staleResponseKeys.Clear();
            foreach (ulong client in _playerSearches.Keys) if (PlayerRowOf(client) == null) _staleResponseKeys.Add(client);
            foreach (ulong client in _staleResponseKeys) _playerSearches.Remove(client);
        }

        // RefreshPlayerAssignments' own snapshot of the table, so only valid inside a refresh.
        private BlacksiteWorldRow PlayerRowOf(ulong client)
        {
            foreach (var row in _playerRows) if (ClientOf(row) == client) return row;
            return null;
        }

        private bool RespondsAsRaider(ulong member)
        {
            foreach (var s in _playerResponders) if (s.NetworkObjectId == member) return !s.IsShipGuard;
            return false;
        }

        // The reporting member, else the nearest free raider (another world costs 200 m); the lower index on a tie.
        private BlacksiteSoldierAI NearestTracker(BlacksiteWorldRow row)
        {
            BlacksiteSoldierAI tracker = null; float best = float.PositiveInfinity;
            foreach (var s in _playerResponders)
            {
                if (s.IsShipGuard || s.ReplicatedStunned || _usedResponders.Contains(s.NetworkObjectId)) continue;
                float score = s.NetworkObjectId == row.SeenBy ? -1f
                    : Vector3.Distance(s.transform.position, row.Position) + (s.isOutside == row.Outside ? 0f : 200f);
                if (score < best) { best = score; tracker = s; }
            }
            return tracker;
        }

        // The nearest contact this member may answer (another world costs 200 m); the lower client id on a tie.
        private BlacksiteWorldRow NearestAssist(BlacksiteSoldierAI member)
        {
            BlacksiteWorldRow nearest = null; float best = float.PositiveInfinity;
            foreach (var row in _playerRows)
            {
                if (!member.AllowsPlayerResponse(row.Position, row.Outside)) continue;
                float score = Vector3.Distance(member.transform.position, row.Position) + (member.isOutside == row.Outside ? 0f : 200f);
                if (score < best || (nearest != null && score == best && ClientOf(row) < ClientOf(nearest))) { best = score; nearest = row; }
            }
            return nearest;
        }

        private void AssignPlayerResponse(ulong owner, BlacksiteWorldRow row, bool tracker, bool cover)
        {
            ulong client = ClientOf(row);
            if (!_playerSearches.TryGetValue(client, out var search) || search.Revision != row.Revision)
                _playerSearches[client] = search = new PlayerSearch { Revision = row.Revision, RevisitAt = Time.time + 90f };
            if (_playerAssignments.TryGetValue(owner, out var assignment) && assignment.Key == row.Key
                && assignment.Tracker == tracker && assignment.Cover == cover) return;
            _playerAssignments[owner] = new PlayerAssignment { Key = row.Key, ClientId = client, Tracker = tracker, Cover = cover };
            BlacksiteDiagnostics.Log("PlayerSearch", $"memberId={owner}, client={client}, role={(tracker ? "tracker" : cover ? "cover" : "assist search")}.");
        }

        internal bool HasPlayerResponse(BlacksiteSoldierAI soldier) => TryPlayerResponse(soldier, out _, out _);

        // #1293 R1: an assignment is live while its Player row is in the squad's world model.
        private bool TryPlayerResponse(BlacksiteSoldierAI soldier, out PlayerAssignment assignment, out BlacksiteWorldRow row)
        {
            RefreshPlayerAssignments();
            assignment = null; row = null;
            return CanRespondToPlayer(soldier) && _playerAssignments.TryGetValue(soldier.NetworkObjectId, out assignment)
                && _mission.SquadDirector.World.TryGet(assignment.Key, out row);
        }

        private bool TryPlayerResponseOrder(BlacksiteSoldierAI soldier, out BlacksiteMemberOrder order)
        {
            order = default;
            if (!TryPlayerResponse(soldier, out var assignment, out var row)) return false;
            _planningBlocked.Remove(soldier.NetworkObjectId);
            if (soldier.isOutside != row.Outside)
            {
                // #1305 G2 (R16): the far side of the squad's door, in the player's world; the route crosses.
                Vector3 entrance = Planner.CrossingFor(soldier);
                order = PlayerOrder(row, entrance, entrance);
                return true;
            }
            var search = _playerSearches[assignment.ClientId];
            bool changed = assignment.Revision != row.Revision || assignment.Outside != soldier.isOutside
                || assignment.WasVisible != row.InSight;
            if (assignment.Tracker && !assignment.WasVisible && row.InSight) soldier.AnnounceSearch(true);
            assignment.WasVisible = row.InSight;
            if (changed) { assignment.HasGoal = false; assignment.ArrivedAt = float.NaN; assignment.NextPlan = 0f; }
            bool arrived = assignment.HasGoal && Vector3.Distance(soldier.transform.position, assignment.Goal) < 1.1f;
            bool blocked = assignment.HasGoal && !arrived && Time.time > assignment.GoalUntil;
            bool combat = soldier.IsObjectiveCombatActive || soldier.ReplicatedStunned || soldier.Navigator.HasCommittedMotion;
            if (arrived && !combat && !row.InSight && !assignment.Cover)
            {
                if (float.IsNaN(assignment.ArrivedAt)) assignment.ArrivedAt = Time.time;
                if (Time.time - assignment.ArrivedAt >= 2f)
                {
                    search.Checked.Add(assignment.GoalKey); assignment.HasGoal = false;
                    assignment.ArrivedAt = float.NaN;
                }
            }
            else assignment.ArrivedAt = float.NaN;
            if (blocked && !combat)
            {
                search.BlockedUntil[assignment.GoalKey] = Time.time + 15f;
                assignment.HasGoal = false;
            }
            bool moved = Vector3.Distance(assignment.Center, row.Position) > 3f;
            if ((!combat || row.InSight) && Time.time >= assignment.NextPlan && (!assignment.HasGoal || moved))
            {
                assignment.NextPlan = Time.time + 2f;
                assignment.Center = row.Position; assignment.Outside = soldier.isOutside; assignment.Revision = row.Revision;
                bool cover = assignment.Cover || row.InSight;
                if (TryPlayerSearchGoal(soldier, assignment, row, search, cover, out var goal, out int key))
                {
                    assignment.HasGoal = true; assignment.Goal = goal; assignment.GoalKey = key;
                    assignment.GoalUntil = Time.time + 22f;
                    BlacksiteDiagnostics.Log("PlayerSearch", $"member={soldier.MemberIndex}, client={assignment.ClientId}, action={(cover ? "cover" : "check")}, goal={goal}, evidenceAge={row.Age(Time.time):0.0}s.");
                }
            }
            if (!assignment.HasGoal)
            {
                if (_planningBlocked.TryGetValue(soldier.NetworkObjectId, out var cause) && cause == BlacksiteBlockedCause.PlanningBudget)
                    assignment.NextPlan = Time.time + .2f;
                else _planningBlocked[soldier.NetworkObjectId] = BlacksiteBlockedCause.NoReachableCandidate;
            }
            Vector3 destination = assignment.HasGoal ? assignment.Goal : soldier.transform.position;
            order = PlayerOrder(row, destination, row.Position + Vector3.up);
            return true;
        }

        private static BlacksiteMemberOrder PlayerOrder(BlacksiteWorldRow row, Vector3 point, Vector3 facing)
        {
            var squad = new BlacksiteSquadOrder(row.Revision,
                row.InSight ? BlacksiteSquadOrderKind.Contact : BlacksiteSquadOrderKind.SearchLastKnown,
                BlacksiteFormationKind.SearchSectors, row.Position, facing - point, row.Confidence(Time.time),
                Mathf.Min(24f, row.Age(Time.time) * .8f));
            // #874 S4.2: a 0.8 m arrival gate under a 1.1 m movement default is a gate the agent
            // was never told to satisfy, so the order carries the reach contract that makes it
            // reachable. The stop is derived, not chosen: the gate less the agent's settle slack
            // (0.2) less the sampled-destination margin (0.1) is 0.5 m. Facing and the operator
            // lane stay off - the responder faces the contact itself in ExecuteSquadOrder, and a
            // player responder is not an interaction operator, so avoidance priority is unchanged.
            var reach = new BlacksiteApproachProfile(
                BlacksitePlayerResponseStopDistance, PlayerResponseReachDistance, facing,
                requiresFacing: false, operatorLane: false);
            return new BlacksiteMemberOrder(squad, point, facing, 1.8f, PlayerResponseReachDistance, false, true, row.Outside, reach);
        }

        private bool TryPlayerSearchGoal(BlacksiteSoldierAI soldier, PlayerAssignment assignment, BlacksiteWorldRow row,
            PlayerSearch search, bool cover, out Vector3 goal, out int key)
        {
            goal = default; key = 0;
            // #874 S10.2, #1293 R1: the search centre is the Player row carried forward at its measured
            // velocity (BlacksiteWorldRow.PredictedPosition), projected onto the navmesh at the S7.6 3 m
            // sample, so a member walks to where the contact went rather than to where it was.
            Vector3 center = row.PredictedPosition(Time.time);
            if (NavMesh.SamplePosition(center, out var centerHit, 3f, NavMesh.AllAreas)) center = centerHit.position;
            if (!cover && assignment.Tracker && !search.Checked.Contains(0)
                && PlayerSearchPoint(soldier, assignment, search, center, 0, out goal)) return true;
            if (cover)
            {
                // Score all twelve existing slots before spending route queries. Baton helpers
                // approach to melee reach; rifle helpers retain a useful firing lane.
                var candidates = new List<(Vector3 Point, int Key, float Score)>();
                bool melee = soldier.UsesBaton;
                // #874 S10.4: the ring is the melee gate's own stand-off, not a number of its own.
                // 1.6 m was 0.02 m INSIDE BatonAttackMinRange 1.62, so a melee member that reached
                // its cover goal was immediately BatonInsideMinimumReach and told to back out again:
                // 53 action=cover lines in the 2026-09-09 run. BlacksiteCombatPolicy.CoverRingRadius
                // holds the derivation, including why a melee ring has no second, wider circle.
                float coverRadius = BlacksiteCombatPolicy.CoverRingRadius(melee, 0);
                // #874 S11 U2 (I6): the ring keeps its golden-angle bearings, its radii and its
                // travel and radius terms; only the cover term changes. It used to be ONE linecast
                // from 1.6 m to the contact's chest, penalised +12 when blocked - a firing-lane
                // test, so the ring preferred a slot standing in the open on a circle around the
                // player to a slot behind a crate. What a cover ring wants is the opposite, and the
                // scorer for it already existed and was proven in the combat reposition search:
                // body occluded +2.5, a clear peek from one shoulder's step +3.5, a blind slot -4
                // (BlacksiteCombatController.PeekCoverScore). Both shoulders are probed because a
                // ring slot, unlike a lateral reposition candidate, has no side of its own; the
                // better of the two is the slot's cover, and the best a slot can score is the 6.0
                // that beats an open one's 3.5 by two and a half. Subtracted, because this ring
                // orders by ascending cost.
                Vector3 chest = center + Vector3.up * 1.2f;
                for (int step = 0; step < BlacksiteCombatPolicy.CoverRingSlots; step++)
                {
                    float angle = soldier.MemberIndex * 137.5f + step * 30f;
                    Vector3 candidate = center + Quaternion.Euler(0, angle, 0) * Vector3.forward
                        * BlacksiteCombatPolicy.CoverRingRadius(melee, step);
                    Vector3 body = candidate + Vector3.up * BlacksiteCombatPolicy.CoverBodySampleHeight;
                    Vector3 shoulder = Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(chest - body, Vector3.up).normalized)
                        * BlacksiteCombatPolicy.CoverPeekOffset;
                    float slotCover = Mathf.Max(
                        BlacksiteCombatController.PeekCoverScore(body, chest, shoulder),
                        BlacksiteCombatController.PeekCoverScore(body, chest, -shoulder));
                    float score = Vector3.Distance(soldier.transform.position, candidate) * .25f
                        + Mathf.Abs(Vector3.Distance(candidate, center) - coverRadius) - slotCover;
                    candidates.Add((candidate, int.MinValue + soldier.MemberIndex * 12 + step, score));
                }
                foreach (var candidate in candidates.OrderBy(c => c.Score))
                {
                    // #874 S10.4: the mission ring and Combat's lane sweep are two placements around
                    // one contact. A slot another melee attacker already stands on or has claimed is
                    // not available to this one either, on the same BatonBodySeparation the sweep
                    // uses, so the two never converge on the same point.
                    if (melee && soldier.IsMeleeLaneTaken(candidate.Point)) continue;
                    if (PlayerSearchPoint(soldier, assignment, search, candidate.Point, candidate.Key, out goal))
                    { key = candidate.Key; return true; }
                }
                return false;
            }
            var nodes = soldier.isOutside
                ? (_outsideSearchNodes ?? (_outsideSearchNodes = GameObject.FindGameObjectsWithTag("OutsideAINode")))
                : (_insideSearchNodes ?? (_insideSearchNodes = GameObject.FindGameObjectsWithTag("AINode")));
            float best = float.PositiveInfinity;
            if (Time.time >= search.RevisitAt && search.Checked.Count > 0)
            { search.Checked.Clear(); search.RevisitAt = Time.time + 90f; }
            float radius = Mathf.Min(70f, 8f + row.Age(Time.time) * .4f);
            foreach (var node in nodes)
            {
                if (node == null) continue;
                int nodeKey = node.GetInstanceID();
                Vector3 offset = node.transform.position - center;
                if (offset.magnitude > radius || search.Checked.Contains(nodeKey)) continue;
                float score = offset.magnitude + Vector3.Distance(soldier.transform.position, node.transform.position) * .35f
                    - Vector3.Dot(offset.normalized, Vector3.ProjectOnPlane(row.Velocity, Vector3.up).normalized) * 3f;
                if (score >= best || !PlayerSearchPoint(soldier, assignment, search, node.transform.position, nodeKey, out var candidate)) continue;
                best = score; goal = candidate; key = nodeKey;
            }
            return !float.IsPositiveInfinity(best);
        }

        private bool PlayerSearchPoint(BlacksiteSoldierAI soldier, PlayerAssignment assignment, PlayerSearch search,
            Vector3 candidate, int key, out Vector3 goal)
        {
            goal = default;
            if (search.BlockedUntil.TryGetValue(key, out float until) && Time.time < until) return false;
            if (!NavMesh.SamplePosition(candidate, out var nav, .75f, NavMesh.AllAreas)
                || Mathf.Abs(nav.position.y - candidate.y) > .6f
                || !soldier.AllowsPlayerResponse(nav.position, soldier.isOutside)) return false;
            foreach (var other in _playerAssignments.Values)
                if (other != assignment && other.HasGoal && other.Outside == soldier.isOutside
                    && Vector3.Distance(other.Goal, nav.position) < 1.8f) return false;
            if (BlacksiteHazardAwareness.Nearby(nav.position, 12f) != null) return false;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            if (!SpendCandidatePath(soldier.NetworkObjectId)) return false;
            if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(soldier), soldier.transform.position, nav.position).Reachable
                || !BlacksitePhysicalStep.CanOccupy(soldier.transform, nav.position, mask))
            { search.BlockedUntil[key] = Time.time + 15f; return false; }
            goal = nav.position; return true;
        }

        private void ClearPlayerResponses()
        {
            _playerResponseRetry.Clear(); _playerAssignments.Clear(); _playerTrackers.Clear(); _playerSearches.Clear(); _nextPlayerAssignments = 0f;
        }
    }
}

