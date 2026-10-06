// #1018 S4: the squad planner - ONE writer of a member's assignment.
//
// It replaces the duty board's OrderFor, which is 221 lines that both READ and PLAN and is reachable
// from five callers (design note A.1), three of which are not decisions at all: the HUD, the raid
// withdrawal and the coil-head observer. After S4, the read is a projection of a record this type
// owns (#1305 S: TryGetOrder). #1293 R3: a member is planned only on a pass that finds it marked or due.
//
// Declared as a nested type of BlacksiteObjectiveManifestController rather than as a top-level
// class, and that is a DELIBERATE deviation from design note B.3. The planner reads about forty
// manifest members that are private today - Records, Snapshots, Ship, Returning, _members, _mission,
// RoutePlanner, _routeRaiders, PlanDutyOrder, TryBackupOrder, TryPlayerResponseOrder, MakeOrder,
// RouteBeatDue, TickSquadRoute, RouteLead, TickInteriorReachability,
// PruneRouteColumns, ReconcileObjectiveClaims, RouteDistance,
// TryFocusRecord, TryEntryDoorway, DoorwayKey, TryHopDoorway, Soldier, RequiresInterior,
// DeferPlayerResponse, Response - and nesting buys every one of them with no edit to
// any existing production file, which is exactly what blocks 1 and 2 are required not to make. A
// nested type resolves through AccessTools.Inner and Prop(manifest, "Planner") identically, so the
// F.3.2 fixture plan is unaffected, and the manifest's own forwarders (block 5) are still one line
// each. Promoting it to a top-level type later is an accessibility batch, not a redesign.
//
// OWNERSHIP (fix batch 1, B2). The planner owns exactly two pieces of per-member state - _duties
// (typed on its own nested Duty) and _sequence - plus reused scratch buffers, its diagnostics
// gates and (#1293 R3) the dirty set its pass drains. EVERY other per-member dictionary is the manifest's and is
// reached through _manifest., because the manifest's own producers are still live and still writing
// them; the full list and the failure each duplicate caused are in BlacksiteSquadPlanner.Idle.cs's
// own header. The 24-per-frame / 4-per-member route budget is the manifest's single
// SpendCandidatePath, called and never copied. Whether any of that state later moves onto the
// planner is the deletion block's decision, not this one's.
//
// #1233 S3 (rule 7): the rolling cohesion budget (fix batch 1, B4) is deleted. A hold is bounded by
// the idle action's own phase clock; an escort's goal is its live anchor, so there is no stationary
// allowance to spend.
using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed partial class BlacksiteObjectiveManifestController
    {
        /// <summary>
        /// #1018 S4: the ONE writer of a member's assignment, and the mission's one planning pass.
        ///
        /// Exit criteria it exists to deliver: one writer of a member's assignment; no idle hold
        /// without an expiry; every idle hold ends in a purposeful successor. The first is this
        /// type's dictionary; the other two are the idle action's phase clock and the successor set
        /// this type proves. #1293 R2: the assignment itself has no expiry - it lives until this
        /// type replaces, drops or releases it.
        /// </summary>
        internal sealed partial class BlacksiteSquadPlanner
        {
            private readonly BlacksiteObjectiveManifestController _manifest;

            internal BlacksiteSquadPlanner(BlacksiteObjectiveManifestController manifest)
            {
                _manifest = manifest;
            }

            // ---------------------------------------------------------------------------------
            // The one truth, and the per-member plan state behind it.
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #1018 S4 (design note C.2): the assignment dictionary. This type is the only one that
            /// writes it, which is the whole exit criterion; <see cref="Release"/> is the only path
            /// that removes a row, and it is idempotent.
            /// </summary>
            private readonly Dictionary<ulong, BlacksiteSquadAssignment> _assignments
                = new Dictionary<ulong, BlacksiteSquadAssignment>();

            /// <summary>Monotonic per member per mission; never reset inside a mission.</summary>
            private readonly Dictionary<ulong, int> _sequence = new Dictionary<ulong, int>();

            private readonly Dictionary<ulong, Duty> _duties = new Dictionary<ulong, Duty>();

            internal int AssignmentCount => _assignments.Count;

            /// <summary>
            /// #1018 S4: the per-member PLAN state - the bookkeeping the planner needs to decide
            /// whether to re-issue: the arrival latch, the budget, the route beat, the idle clock
            /// and (#1293 R3) the duty's own next deadline and the edges that revise its goal.
            ///
            /// It keeps the name <c>Duty</c> and every field name the duty board's own class has,
            /// for two reasons (design note F.3.2): the planner genuinely needs all twenty of them,
            /// and about 300 native-fixture reflection rows name them, so preserving the names turns
            /// a fixture rewrite into a one-argument insertion. <c>AccessTools.Inner(planner,
            /// "Duty")</c> still resolves.
            /// </summary>
            private sealed class Duty
            {
                internal readonly BlacksiteRaidJob Budget = new BlacksiteRaidJob();
                /// <summary>The projection last handed out, kept so the 1.45 m discretionary drop and
                /// the arrival latch measure against the same point the board measured against.</summary>
                internal BlacksiteMemberOrder Order;
                /// <summary>The record - the one truth. A mirror of <c>_assignments[member]</c>,
                /// held here so the per-member pass reads one object.</summary>
                internal BlacksiteSquadAssignment Assignment;
                internal (BlacksiteJobKind Kind, ulong Object, BlacksiteObjectivePhase Phase, bool Outside) Intent;
                /// <summary>
                /// #874 F05: the world the member stood in when <see cref="Issue"/> wrote the
                /// assignment it is holding. Stamped there and read in exactly one place,
                /// <see cref="NoteMemberCrossedWorld"/>, which is what tells an obsolete APPROACH
                /// apart from a legitimate one: an order planned in the world the member has just
                /// arrived in - a retreat back out, an extraction, a renewed pursuit through the
                /// same door - is still current and is never dropped.
                ///
                /// It is a field of its own rather than a read of <c>Intent.Outside</c>, which
                /// happens to carry the same value today, because that tuple is the intent's shape
                /// and this is a property of the ISSUE. Nothing else may write it.
                /// </summary>
                internal bool IssuedOutside;
                /// <summary>#1293 R3: the duty's own next deadline; the pass evaluates the member once it passes (PositiveInfinity: events only).</summary>
                internal float NextPlan = float.PositiveInfinity;
                /// <summary>#1293 R3: when the goal is revised; spent by the replan (PositiveInfinity: none).</summary>
                internal float ReviseAt = float.PositiveInfinity;
                /// <summary>#1293 R3: the world row the goal is planned against and its revision at issue.</summary>
                internal ulong GoalRow = NoCarrier;
                internal int GoalRowRevision;
                /// <summary>#1293 R3: the backup phase the pass last saw for this member, -1 for none.</summary>
                internal int BackupSeen = -1;
                internal bool Retry;
                internal Vector3 ObservedPosition;
                internal float IdleSince;
                internal BlacksiteBlockedCause Blocked;
                // The real duty goal, retained across idle repositions. Progress is always measured
                // against this, so a local idle step to a nearer point cannot renew the budget.
                internal Vector3 TaskGoal; internal bool HasTaskGoal;
                internal BlacksiteIdleGoal IdleGoal; internal int IdleBlocked; internal bool IdleCovering;
                // #874 S4.2: arrival hysteresis. Once a duty has arrived it stays arrived until its
                // goal actually moves; without the latch a member sitting on the boundary flickers
                // between arrived and travelling and replans on every flicker.
                internal bool Arrived; internal Vector3 ArrivedGoal;
                // #955 S12A U2: the lead's goal changed (#1305 S: true when his stall moved it), so this
                // member's next order is worth planning; Pending waits for his beat. #1305 S: the causes printed.
                internal bool? RoomAdvance; internal string IssuedCause;
                internal BlacksiteMemberOrder Pending; internal bool HasPending; internal float PendingAt; internal string PendingCause;
                internal int Sequence;
                /// <summary>
                /// #1018 S4 (fix batch 1, B3): the doorway identity behind this member's proved
                /// FrontierTile successor, or 0 when the frontier is a NODE. Carried here rather
                /// than on the record because proving a frontier must not spend the 90 s stamp;
                /// the idle Action spends it through NoteFrontierTaken when it issues the successor.
                /// </summary>
                internal long FrontierDoorway;
                /// <summary>
                /// #1018 S4 (fix batch 1, SF4): this member's index, stamped at issue time. Release
                /// takes a token and runs after the member has left _members on the casualty path,
                /// so without this the [UAT][Planner] release line could never name the member it
                /// was about.
                /// </summary>
                internal int MemberIndex = -1;
                /// <summary>
                /// #874 P-S2: the Follow the current assignment was issued as, stamped with its
                /// sequence (<see cref="FollowHeld"/>); default for a plain destination. Written only
                /// by <see cref="Issue"/>, cleared with the approach on a world transfer.
                /// </summary>
                internal BlacksiteFollowOrder Follow;
                /// <summary>#1293 R3: where the held Follow's anchor stood when the planner last derived
                /// its slot (<see cref="Issue"/>, <see cref="RefreshInPlace"/>), compared by <see cref="ObserveSquad"/>.</summary>
                internal Vector3 FollowAnchorAt;
                /// <summary>
                /// #874 P-S2: the last paired entrance THIS member cleared into the other world - its
                /// near-side point, id, and which world it left - so a member following it takes the
                /// same door (<see cref="NoteMemberCrossing"/>).
                /// </summary>
                internal bool HasCrossing, CrossedFromOutside;
                internal Vector3 CrossedFrom;
                internal int CrossedEntranceId;
            }

            // ---------------------------------------------------------------------------------
            // The ONE read.
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #1018 S4: a dictionary lookup and a struct copy. No planning, no side effect, safe
            /// from any caller at any cadence - which is what makes design note A.1's five callers
            /// harmless. #1293 R2: nothing expires on time. A row lives until the planner replaces
            /// it, drops it at a world transfer or releases it, and a row that was never issued
            /// (sequence 0) is no assignment.
            /// </summary>
            internal bool TryGetAssignment(ulong member, out BlacksiteSquadAssignment assignment)
            {
                if (!_assignments.TryGetValue(member, out assignment)) return false;
                if (assignment.IsValid) return true;
                assignment = default;
                return false;
            }

            /// <summary>
            /// #1018 S4: the read API, a pure projection of the assignment (design note B.1). #1305 S
            /// (R2): no assignment is no order, never a hold at the member's own feet.
            /// </summary>
            internal bool TryGetOrder(BlacksiteSoldierAI soldier, out BlacksiteMemberOrder order)
            {
                order = default;
                if (soldier == null || !TryGetAssignment(soldier.NetworkObjectId, out var assignment)) return false;
                order = assignment.ToMemberOrder();
                return true;
            }

            /// <summary>
            /// #1018 S4 (block 9): the evaluation of ONE member and its projection - a fixtures-only
            /// seam (production plans through <see cref="Tick"/>), without the pass's squad work.
            /// </summary>
            internal BlacksiteMemberOrder PlanMember(BlacksiteSoldierAI soldier)
            {
                // #1018 S4 (fix batch 2, S13): the same skip Tick applies before the step, so a
                // boarded or transport-lost member with a SURVIVING plan state - the membership
                // sweep has not run yet - is refused on both paths rather than only on the pass.
                // A retired member is answered, never re-enrolled: no plan state, so no plan.
                if (soldier != null && !soldier.HasBoardedShip && !soldier.HasLostTransport)
                    Evaluate(soldier, Time.time);
                TryGetOrder(soldier, out var order);
                return order;
            }

            // ---------------------------------------------------------------------------------
            // The ONE pass.
            // ---------------------------------------------------------------------------------

            private readonly List<ulong> _memberSweep = new List<ulong>();
            // The reused scratch for Release's doorway-stamp sweep (S9).
            private readonly List<(ulong Member, long Doorway)> _doorwaySweep
                = new List<(ulong, long)>();
            // #1293 R3: the members the next pass evaluates, whether the squad was leaving, and the
            // pass's reused scratch: its live members, their deadlines and the positions selected.
            private readonly BlacksitePlannerDirtySet _dirty = new BlacksitePlannerDirtySet();
            private bool _squadLeaving;
            private readonly List<BlacksiteSoldierAI> _passMembers = new List<BlacksiteSoldierAI>();
            private readonly List<(ulong Member, float NextPlan)> _passCandidates = new List<(ulong, float)>();
            private readonly List<int> _passDue = new List<int>();

            /// <summary>#1293 R3: an event marks this member for the planner; safe at any time, mid-pass included.</summary>
            internal void MarkDirty(ulong memberId) => _dirty.Mark(memberId);

            /// <summary>#1293 R3: a squad-wide change marks every member for the next pass.</summary>
            internal void MarkAllDirty() => _dirty.MarkAll();

            /// <summary>
            /// #1018 S4 (C.1), #1293 R3: the manifest's server-tick pass: squad-level work every pass,
            /// members only when <see cref="BlacksitePlannerDirtySet.SelectPass"/> selects them, so a
            /// clean pass plans nobody.
            /// </summary>
            internal void Tick(float now)
            {
                // #1016 S2: the ONE expiry sweep, before anything else in the tick. It runs here
                // because this is where assignments are planned, and a plan must never see a lease
                // that has already expired.
                TickResourceBroker(now);
                // #955 S12A U2: and the committed route, before any assignment is planned against
                // it. It has to be observed here rather than inside the first member's own planner,
                // because a room advance is what makes that member plan at all.
                _manifest.TickSquadRoute(_manifest.RouteLead());
                SweepMembership(now);
                ObserveSquad(now);
                _passMembers.Clear(); _passCandidates.Clear();
                var members = _manifest._members;
                for (int i = 0; i < members.Count; i++)
                {
                    var soldier = members[i];
                    if (soldier == null || soldier.HasBoardedShip || soldier.HasLostTransport) continue;
                    _passMembers.Add(soldier);
                    _passCandidates.Add((soldier.NetworkObjectId,
                        _duties.TryGetValue(soldier.NetworkObjectId, out var held) ? held.NextPlan : float.NegativeInfinity));
                }
                _dirty.SelectPass(_passCandidates, now, _passDue);
                // #874 S12 U4: the scan's verdicts, before any assignment is planned against them.
                if (_passDue.Count > 0) _manifest.TickInteriorReachability();
                for (int i = 0; i < _passDue.Count; i++) Evaluate(_passMembers[_passDue[i]], now);
            }

            /// <summary>#1293 R3: one member's evaluation; the deadline it was due on is spent.</summary>
            private void Evaluate(BlacksiteSoldierAI soldier, float now)
            {
                ulong id = soldier.NetworkObjectId;
                var duty = EnsureDuty(soldier, now, out bool fresh);
                if (duty == null) return;
                duty.NextPlan = float.PositiveInfinity;
                PlanMemberStep(soldier, duty, fresh, now);
                // A revision still ahead is a deadline; one due but not acted on waits for its event.
                if (duty.ReviseAt > now) duty.NextPlan = Mathf.Min(duty.NextPlan, duty.ReviseAt);
                // #1219 E1: an evaluation that did not run the patrol breaks its run.
                if (_patrolTickAt.TryGetValue(id, out float ticked) && ticked != now) _patrolTickAt.Remove(id);
            }

            /// <summary>#1293 R3: squad facts no event carries, read as transitions every pass: leaving
            /// (marks all), a backup phase change, the goal's world row moving (revises the goal), and a
            /// held Follow's anchor moving one row revision quantum from where its slot was derived
            /// (marks the follower, whose evaluation refreshes the slot in place).</summary>
            private void ObserveSquad(float now)
            {
                bool leaving = _manifest.Returning || _manifest.WithdrawalCommitted;
                if (leaving != _squadLeaving) { _squadLeaving = leaving; _dirty.MarkAll(); }
                foreach (var pair in _duties)
                {
                    var duty = pair.Value;
                    int backup = _manifest._backupResponses.TryGetValue(pair.Key, out var response) ? (int)response.Phase : -1;
                    if (backup != duty.BackupSeen) { duty.BackupSeen = backup; _dirty.Mark(pair.Key); }
                    if (FollowHeld(duty))
                    {
                        var anchor = duty.Follow.Anchor;
                        if (anchor != null && (anchor.transform.position - duty.FollowAnchorAt).sqrMagnitude
                            > BlacksiteWorldModel.RevisionMoveMetres * BlacksiteWorldModel.RevisionMoveMetres)
                            _dirty.Mark(pair.Key);
                        continue;
                    }
                    if (!TryGetGoalRow(duty.GoalRow, out var row) || row.Revision == duty.GoalRowRevision) continue;
                    duty.GoalRowRevision = row.Revision;
                    duty.ReviseAt = now;
                    _dirty.Mark(pair.Key);
                }
            }

            /// <summary>#1293 R3: the world row this member's goal is planned against (a player response's
            /// Player row, a moving goal's record row), or <see cref="NoCarrier"/>.</summary>
            private ulong GoalRowOf(ulong member, BlacksiteJobKind kind, ulong intentObject)
            {
                if (kind == BlacksiteJobKind.PlayerResponse)
                    return _manifest._playerAssignments.TryGetValue(member, out var player) ? player.Key : NoCarrier;
                if (!MovingGoal(kind)) return NoCarrier;
                if (kind == BlacksiteJobKind.Objective) return intentObject;
                return _manifest._focusLogged.TryGetValue(member, out var focus) ? focus.Object : NoCarrier;
            }

            private bool TryGetGoalRow(ulong key, out BlacksiteWorldRow row)
            {
                row = null;
                var director = _manifest._mission != null ? _manifest._mission.SquadDirector : null;
                return key != NoCarrier && director != null && director.World.TryGet(key, out row);
            }

            /// <summary>#1293 R3: each member following <paramref name="anchor"/> re-tests its follow.</summary>
            private void MarkFollowersOf(ulong anchor)
            {
                foreach (var pair in _duties)
                    if (pair.Key != anchor && FollowHeld(pair.Value) && pair.Value.Follow.AnchorId == anchor)
                        _dirty.Mark(pair.Key);
            }

            /// <summary>#1293 R3: a response's own gates (TryBackupOrder, TryPlayerResponseOrder) are its member's deadlines.</summary>
            private void ArmResponseDeadlines(Duty duty, ulong member, float now)
            {
                if (_manifest._backupResponses.TryGetValue(member, out var backup)) Arm(duty, backup.NextPlan, now);
                if (!_manifest._playerAssignments.TryGetValue(member, out var player)) return;
                Arm(duty, player.NextPlan, now);
                // TryPlayerResponseOrder's own check: an arrived goal is spent two seconds after arrival.
                if (!float.IsNaN(player.ArrivedAt)) Arm(duty, player.ArrivedAt + 2f, now);
                if (player.HasGoal) Arm(duty, player.GoalUntil, now);
            }

            private static void Arm(Duty duty, float at, float now) { if (at > now && at < duty.NextPlan) duty.NextPlan = at; }

            /// <summary>
            /// #874 S4.2: an assignment is released only on a real membership change - a casualty
            /// (the member leaves <c>_members</c>), boarding, or a lost transport.
            ///
            /// #1018 S4 (design note G10 a, d): the board's version of this is
            /// <c>_duties.Keys.Where(…).ToArray()</c> plus <c>_members.Any(…)</c> per key, which
            /// allocates two objects and a closure every server tick. This one drains into a reused
            /// scratch list and walks <c>_members</c> by index, so a standing squad allocates
            /// nothing, and every swept dictionary is drained before a row is removed so no
            /// enumeration-with-removal exists.
            /// </summary>
            private void SweepMembership(float now)
            {
                _memberSweep.Clear();
                foreach (var pair in _duties)
                    if (NotLive(pair.Key)) _memberSweep.Add(pair.Key);
                // An assignment with no plan state is impossible by construction - Issue creates
                // the Duty first - but the dictionary is the thing readers trust, so it is swept on
                // its own terms rather than on the plan state's.
                foreach (var pair in _assignments)
                    if (!_duties.ContainsKey(pair.Key) && NotLive(pair.Key)) _memberSweep.Add(pair.Key);
                for (int i = 0; i < _memberSweep.Count; i++) Release(_memberSweep[i], "membership");
                _memberSweep.Clear();
            }

            private bool NotLive(ulong member)
            {
                var members = _manifest._members;
                for (int i = 0; i < members.Count; i++)
                {
                    var soldier = members[i];
                    if (soldier == null || soldier.NetworkObjectId != member) continue;
                    return soldier.HasBoardedShip || soldier.HasLostTransport;
                }
                return true;
            }

            /// <summary>
            /// #1016 S2: the one sweep and the two emitters, run once per server tick before
            /// anything plans against a lease. Copied from <c>TickResourceBroker</c> with its clock
            /// taken as a parameter instead of read from <c>Time.time</c>; the two steps that own
            /// manifest state (<c>PruneRouteColumns</c>' route columns and
            /// <c>ReconcileObjectiveClaims</c>' NetworkList) are called through rather than
            /// copied, because duplicating their state would give the mission two answers.
            /// </summary>
            private void TickResourceBroker(float now)
            {
                if (!_manifest._mission.IsServer) return;
                BlacksiteSquadBlackboard blackboard = _manifest._mission.Blackboard;
                // #1016 S2: the ship's two queues are field initialisers, so they are bound from
                // the first server tick that has the mission in hand.
                _manifest.Ship?.BindAccessQueues();
                blackboard.Broker.Tick(now);
                _manifest.PruneRouteColumns(now);
                // #1016 S2 (design note C.5): keep every live objective claim asserted.
                _manifest.ReconcileObjectiveClaims(now);
                blackboard.ReportBrokerEvents();
                blackboard.ReportWaits(now);
            }

            // ---------------------------------------------------------------------------------
            // The intent, and the replan gate.
            // ---------------------------------------------------------------------------------

            /// <summary><c>DutyIntent</c> (<c>…Duties.cs:74-98</c>): the nine <see cref="BlacksiteJobKind"/> values from the
            /// <c>Records</c> tests. #1233 S2: after the holder check the member's INTENDED world decides Infiltrate - the manifest's
            /// one answer (<see cref="BlacksiteObjectiveManifestController.RequiresInterior"/>), the entrance gate's predicate.</summary>
            internal (BlacksiteJobKind Kind, ulong Object, BlacksiteObjectivePhase Phase, bool Outside) ResolveIntent(
                BlacksiteSoldierAI soldier)
            {
                // #1305 H4 (R24): first (the puller stands on the deck): the order's record, so a loss, load or re-election replans.
                int cargo = _manifest.CargoRole(soldier, out bool lever);
                if (cargo >= 0) return (BlacksiteJobKind.Objective, lever ? _manifest.Ship.NetworkObjectId : _manifest.Records[cargo].ObjectId,
                    _manifest.Records[cargo].Phase, soldier.isOutside);
                BlacksiteJobKind kind;
                if (Egressing(soldier))
                    kind = BlacksiteJobKind.Egress;
                else if (_manifest.Returning || soldier.IsRaidWithdrawing) kind = BlacksiteJobKind.Rally;
                else if (soldier.IsShipGuard) kind = BlacksiteJobKind.Guard;
                else
                {
                    int held = HeldRecordIndex(soldier);
                    if (held >= 0)
                    {
                        var r = _manifest.Records[held];
                        return (BlacksiteJobKind.Objective, r.ObjectId, r.Phase, soldier.isOutside);
                    }
                    if (soldier.isOutside && _manifest.RequiresInterior(soldier)) kind = BlacksiteJobKind.Infiltrate;
                    else kind = EscortWorkOutstanding() && !_manifest.Sweeps(soldier) ? BlacksiteJobKind.Escort : BlacksiteJobKind.Sweep;
                }
                return (kind, NoCarrier, default, soldier.isOutside);
            }

            // #1219 C2: the Egress duty is for a member still standing on the ship. Crossings are
            // atomic (#874 S3), so there is no mid-passage stand to cover, and a member that reached
            // the ground without a crossing is off the ship: the ground drop zone (WithinPassage)
            // flipped such a member Egress/Rally every second on his way to the ladder mount.
            // #1233 S4 (rule 1): aboard is the ship's live answer, never a remembered exit.
            private bool Egressing(BlacksiteSoldierAI s) => !s.HasBoardedShip && s.IsAboardShip;

            // #1018 S4 (design note G10 f): the record scan by index. Records is a NetworkList<T>
            // whose GetEnumerator returns the INTERFACE, so a foreach over it boxes an enumerator on
            // every call - once per member per tick here, and this runs inside the pass.
            private int HeldRecordIndex(BlacksiteSoldierAI soldier)
            {
                var records = _manifest.Records;
                for (int i = 0; i < records.Count; i++)
                {
                    var r = records[i];
                    if (r.CarrierId == soldier.NetworkObjectId
                        && (r.Phase == BlacksiteObjectivePhase.Reserved || r.Phase == BlacksiteObjectivePhase.Carried))
                        return i;
                }
                return -1;
            }

            // ---------------------------------------------------------------------------------
            // #1233 S2: the squad's world is the objective's world.
            // ---------------------------------------------------------------------------------

            /// <summary>Per member (network id): world changes counted by the one crossing callback.</summary>
            private readonly Dictionary<ulong, int> _crossings = new Dictionary<ulong, int>();

            /// <summary>
            /// #1233 S2 (rule 1): the entrance gate, asked on the host through
            /// <c>BlacksiteEntranceInteraction.AdmitsWorld</c> - by the route before it crosses (#1305 G2)
            /// and by an entrance task until its transfer - for a doorway that changes the
            /// member's world. The doorway is admitted iff its far side is the member's own world by
            /// <see cref="BlacksiteObjectiveManifestController.RequiresInterior"/> - the mission world,
            /// which is the primary objective's, or the member's duty world - or the passage keeps its
            /// own semantics: extraction (the recall, a raid withdrawal, boarded), Egress, a
            /// transport-lost survivor, a popped-Jester flight, and a player response, which crosses
            /// toward the observed player (<c>TryPlayerResponseOrder</c>). A refusal prints one
            /// rate-limited <c>[UAT][Entrance] event=refused, ..., reason=notMissionWorld</c> line.
            /// </summary>
            internal bool AdmitsWorldCrossing(BlacksiteSoldierAI soldier, bool toOutside)
            {
                if (soldier == null) return true;
                bool exempt = _manifest.Returning || soldier.IsRaidWithdrawing || soldier.HasBoardedShip
                    || soldier.HasLostTransport || Egressing(soldier) || soldier.IsFleeingPoppedJester
                    || _manifest.HasPlayerResponse(soldier);
                bool interior = _manifest.RequiresInterior(soldier);
                if (BlacksiteMissionWorld.AdmitsCrossing(toOutside, interior, exempt)) return true;
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.LogRateLimited($"entrance.refused.{soldier.MemberIndex}", 1f, "Entrance",
                        BlacksiteMissionWorld.RefusedLine(soldier.MemberIndex, toOutside, interior));
                return false;
            }

            /// <summary>#1219 B1: how many world changes the crossing callback has counted for this member.</summary>
            internal int CrossingCount(ulong member) => _crossings.TryGetValue(member, out int count) ? count : 0;

            // #1018 S4 (design note G10 b, f): DutyIntent's own `Snapshots.Any(lambda)` becomes an
            // index loop. `Snapshots` is a yield-based IEnumerable over Records, so the board's
            // version allocated an iterator object AND a closure AND boxed an enumerator over an
            // interface-typed sequence, once per member per tick inside the pass. Same rows, same
            // predicate, same order, nothing on the heap.
            private bool EscortWorkOutstanding()
            {
                var records = _manifest.Records;
                for (int i = 0; i < records.Count; i++)
                {
                    var r = records[i];
                    if (r.Kind != BlacksiteObjectiveKind.Loot
                        && (r.Phase == BlacksiteObjectivePhase.Reserved || r.Phase == BlacksiteObjectivePhase.Carried))
                        return true;
                }
                return false;
            }

            // #1018 S4: the PURE rules live in the headless BlacksiteSquadPlannerRules, so
            // tests/BlacksiteJobLifecycleRegression drives the ones the runtime applies rather than
            // restating them (design note D, F.5). These are the planner's own names for them.
            internal static bool Discretionary(BlacksiteJobKind kind)
                => BlacksiteSquadPlannerRules.Discretionary(kind);

            internal static bool MovingGoal(BlacksiteJobKind kind)
                => BlacksiteSquadPlannerRules.MovingGoal(kind);

            // #874 A4: the response branch's own half of C.2's re-issue question.
            internal static bool RefreshesResponseInPlace(bool fresh, bool newDuty, bool changed,
                bool hasAssignment, float goalMove)
                => BlacksiteSquadPlannerRules.RefreshesResponseInPlace(fresh, newDuty, changed,
                    hasAssignment, goalMove);

            // ---------------------------------------------------------------------------------
            // The per-member plan.
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// This member's plan state, created on FIRST SIGHT of a member the planner owns rather
            /// than because the entry happens to be absent.
            ///
            /// #874 S4.2 (…Duties.cs:104-116): a member the planner has RETIRED gets no entry back.
            /// Member 5 of the 2026-09-09 run boarded the ship, the membership sweep retired its
            /// duty every server tick as it should, and its own think loop re-created the entry
            /// about three times a second; generation went back to 1 on every call, so no progress
            /// budget could ever expire and the member stood at remaining=0.44 for 16.6 s. A
            /// retired member therefore answers `null` here - no entry, no budget, and no plan that
            /// could send it walking again - and <c>TryGetOrder</c> answers no order.
            /// </summary>
            private Duty EnsureDuty(BlacksiteSoldierAI soldier, float now, out bool fresh)
            {
                ulong id = soldier.NetworkObjectId;
                fresh = !_duties.TryGetValue(id, out var duty);
                if (fresh && (soldier.HasBoardedShip || soldier.HasLostTransport)) return null;
                if (fresh)
                    _duties[id] = duty = new Duty
                    {
                        IdleSince = now,
                        ObservedPosition = soldier.transform.position
                    };
                return duty;
            }

            /// <summary>
            /// #874 A5: a new duty attempt is a new owner of the member's goal, so the navigator's
            /// route generation takes it with the next goal the member adopts.
            /// </summary>
            private static void BeginDutyAttempt(BlacksiteSoldierAI soldier, Duty duty, float now, BlacksiteJobKind kind)
            {
                duty.Budget.BeginAttempt(soldier.NetworkObjectId, now, kind);
                soldier.Navigator.NoteDutyAttempt();
            }

            /// <summary>#1018 S4: the ONE per-member planning body, reached only through <see cref="Evaluate"/>.</summary>
            private void PlanMemberStep(BlacksiteSoldierAI soldier, Duty duty, bool fresh, float now)
            {
                ulong id = soldier.NetworkObjectId;
                // #1219 B2: a member mid-crossing is never re-evaluated. While its paired-entrance
                // task has not yet transferred it, the assignment is refreshed in place - no intent
                // change and no Issue - so the crossing it committed to is the one it makes, and the
                // next plan is made in the world it lands in.
                if (!fresh && duty.Assignment.IsValid && soldier.IsCrossingEntrance)
                {
                    RefreshInPlace(soldier, duty, now, "refresh");
                    return;
                }
                var intent = ResolveIntent(soldier);
                // #1293 R2 (rule 3): a stranded member gets no travel; the fact is the answer.
                if (soldier.Navigator.IsStranded)
                {
                    HoldStranded(soldier, duty, intent, now);
                    return;
                }
                bool responseActive = _manifest.TryBackupOrder(soldier, out var response)
                                      || _manifest.TryPlayerResponseOrder(soldier, out response);
                if (responseActive)
                    intent = (response.IsPlayerResponse ? BlacksiteJobKind.PlayerResponse : BlacksiteJobKind.Backup,
                        response.IsPlayerResponse ? NoCarrier : (ulong)_manifest.Response(id).Phase,
                        default, soldier.isOutside);
                if (intent.Kind != BlacksiteJobKind.Guard) EndPatrolDuty(soldier);
                bool changed = fresh || duty.Intent != intent;
                // A different duty class or objective is real different work and opens a fresh
                // attempt; retry, replan and goal churn inside the same duty keep the clock.
                bool newDuty = fresh || BlacksiteRaidJob.OpensNewAttempt(
                    duty.Intent.Kind, duty.Intent.Object, duty.Intent.Outside,
                    intent.Kind, intent.Object, intent.Outside);

                if (responseActive)
                {
                    // #1293 R3: the response's gates are this member's deadlines and it answers any due
                    // revision. #874 A4: handing back the point already held is a REFRESH (no seq bump).
                    float goalMove = fresh ? float.PositiveInfinity
                        : Vector3.Distance(response.Destination, duty.Order.Destination);
                    if (RefreshesResponseInPlace(fresh, newDuty, changed, duty.Assignment.IsValid, goalMove))
                    {
                        RefreshInPlace(soldier, duty, now, "refresh");
                        return;
                    }
                    // #874 A4: and a goal that DID move re-baselines the distance series exactly as
                    // the ordinary issue path below does - the clock does not reset. Without the
                    // Assign, a goal flipping to a nearer support slot read as travel progress in
                    // BlacksiteRaidJob.Tick (`remaining < _bestRemaining - .5f`), and that member
                    // stood still for 158 s.
                    if (newDuty || !duty.Budget.AttemptOpen) BeginDutyAttempt(soldier, duty, now, intent.Kind);
                    else duty.Budget.Assign(id, now, intent.Kind);
                    _manifest.ReleaseLeadGoal(soldier); // #1305 S: a route lead's response is other work.
                    duty.Intent = intent;
                    duty.IdleGoal = BlacksiteIdleGoal.Observe; duty.IdleCovering = false;
                    duty.Retry = false;
                    Issue(soldier, duty, response, intent, now, "duty", "intent");
                    return;
                }

                bool interrupted = soldier.IsObjectiveCombatActive || soldier.Navigator.HasCommittedMotion
                    || soldier.ReplicatedStunned || soldier.SelectedBehaviour == BlacksiteBehaviour.Evade;
                // #874 P-S2: a held Follow's arrival is its own goal's test (#1271 S2), so the latch skips
                // it; #1293 R3: it re-plans when its anchor stops leading it.
                bool followHeld = !fresh && FollowHeld(duty);
                bool discretionary = Discretionary(intent.Kind);
                // #874 S4.2: the arrival latch is the navigator's test plus hysteresis. #1293 R3: it rises
                // only on an uninterrupted evaluation, and the rise is the arrival's one replan.
                bool arrivalRose = false;
                if (!fresh && !followHeld)
                {
                    if (duty.Arrived
                        && Vector3.Distance(duty.ArrivedGoal, duty.Order.Destination) > BlacksiteArrivalPolicy.MinDestinationChange)
                        duty.Arrived = false;
                    if (!duty.Arrived && !interrupted && soldier.Navigator.HasArrived(duty.Order.Destination))
                    { duty.Arrived = true; duty.ArrivedGoal = duty.Order.Destination; arrivalRose = true; }
                }
                bool arrived = !fresh && !followHeld && duty.Arrived;
                // #1293 R3: the goal edge - a due revision, or a held Follow's anchor no longer leading.
                bool goalRevised = now >= duty.ReviseAt
                    || (followHeld && (FollowTargetFor(soldier, duty.Follow.Anchor, duty.Follow.CarriesObjective,
                        out int anchorAttempt) == BlacksiteFollowTarget.None || anchorAttempt != duty.Follow.AnchorAttempt));
                // #955 S12A U2: a room advance is real work, so it plans now (#1305 S: not mid-route).
                bool roomAdvance = duty.RoomAdvance.HasValue && !interrupted;
                bool travelling = !followHeld && !duty.Arrived && soldier.Navigator.HasIssuedDestination && soldier.Navigator.PathState == BlacksitePathState.Complete;
                bool beatReady = duty.HasPending && !interrupted && now >= duty.PendingAt;

                if (BlacksiteSquadPlannerRules.ShouldReplan(changed, roomAdvance, travelling, beatReady, interrupted, arrivalRose, goalRevised, duty.Retry))
                {
                    // #1293 R3: the replan spends the revision (the producers may arm the next one).
                    duty.ReviseAt = float.PositiveInfinity;
                    // #874 S3: the Egress order IS the movement; the ship's links carry it off the deck.
                    _manifest._planningBlocked.Remove(id);
                    BlacksiteFollowOrder follow = default;
                    var order = beatReady
                        ? duty.Pending
                        : intent.Kind == BlacksiteJobKind.Egress
                            ? MakeOrder(BlacksiteSquadOrderKind.Deploy,
                                _manifest.Ship.EgressPosition(soldier.MemberIndex),
                                _manifest.Ship.EgressPosition(soldier.MemberIndex))
                            : ShapeDutyOrder(soldier, intent.Kind, PlanDutyOrder(soldier, out follow));
                    duty.HasPending = false;
                    string cause = beatReady ? duty.PendingCause : BlacksiteSquadPlannerRules.RoomAdvanceCause(arrivalRose, duty.RoomAdvance == true || duty.Retry);
                    // #955 S12A U2 (b): the lead's waypoint is issued on the tick the route advanced
                    // and follower i one HeadwaySeconds per place behind it. The order is HELD, never
                    // discarded, and the member keeps walking to the one it has until its beat.
                    // #874 P-S2: a Follow trails its anchor continuously, so it has no beat to wait
                    // for, and a held order could not carry the follow it was planned as.
                    if (!fresh && !follow.IsFollow && TryHoldForRouteBeat(soldier, duty, order, cause, now))
                    {
                        RefreshInPlace(soldier, duty, now, "beat");
                        ProveSuccessors(soldier, duty, now);
                        return;
                    }
                    // #874 P-S2: a Follow is measured by WHAT it follows, never by where its goal
                    // happened to stand: the same follow is no move at all, a different one - or a
                    // plain order replacing a held follow - is always a new goal.
                    bool followOrder = follow.IsFollow;
                    bool sameFollow = followOrder && followHeld && follow.SameAs(duty.Follow);
                    float goalMove = followOrder ? (sameFollow ? 0f : float.PositiveInfinity)
                        : followHeld ? float.PositiveInfinity
                        : Vector3.Distance(order.Destination, duty.Order.Destination);
                    // #874 S4.2, #1018 S4 (SF1): a discretionary replan under the minimum destination
                    // change is a byte-identical refresh (no seq bump), and a retry that gave it is cleared.
                    if (!followOrder && goalMove <= BlacksiteArrivalPolicy.MinDestinationChange) duty.Retry = false;
                    // #874 P-S2: the same Follow is a refresh: the anchor moving is not a new goal.
                    // #1219 E2: a changed pace is a changed order even on the same goal, so it is
                    // issued rather than refreshed - a refresh keeps the order it had.
                    if (followOrder
                        ? BlacksiteSquadPlannerRules.RefreshesFollowInPlace(followHeld, sameFollow, changed, newDuty, duty.Retry)
                        : discretionary && !changed && !duty.Retry && !newDuty
                          && goalMove <= BlacksiteArrivalPolicy.MinDestinationChange
                          && order.Pace == duty.Order.Pace)
                    {
                        duty.RoomAdvance = null;
                        RefreshInPlace(soldier, duty, now, "refresh");
                        ProveSuccessors(soldier, duty, now);
                        return;
                    }
                    bool newGoal = followOrder || goalMove > BlacksiteArrivalPolicy.MinDestinationChange;
                    string reason = newDuty ? "duty"
                        : duty.Retry ? "retry"
                        : roomAdvance || beatReady ? RoomAdvanceSource
                        : followOrder ? "follow:anchor"
                        : "next goal";
                    if (newDuty || duty.Retry || roomAdvance || followOrder || followHeld || (arrived && newGoal))
                    {
                        // Only real arrival at a resolved goal completes this action. Retry/intent
                        // churn alone retains the unresolved clock.
                        if (!fresh && arrived && newGoal && !duty.Retry) duty.Budget.Finish(true);
                        if (newDuty || !duty.Budget.AttemptOpen) BeginDutyAttempt(soldier, duty, now, intent.Kind);
                        else duty.Budget.Assign(id, now, intent.Kind);
                        // At most one issue line per second per member; the key and the message are
                        // built before the Enabled check (#944 S11 U6 review P12).
                        if (BlacksiteDiagnostics.Enabled)
                            BlacksiteDiagnostics.LogRateLimited($"job.issue.{id}", 1f, "Job",
                                $"member={soldier.MemberIndex}, kind={intent.Kind}, attempt={duty.Budget.Generation}, goal={order.Destination}, reason={reason}"
                                + (intent.Kind == BlacksiteJobKind.Guard ? $", source={_manifest.GuardPostSource(id)}" : string.Empty) + ".");
                    }
                    // A same-class intent change (the Sweep/Escort flicker) still replans, so rebind
                    // the budget to the new goal: the distance baseline resets, the clock does not.
                    else if (changed) duty.Budget.Assign(id, now, intent.Kind);
                    duty.Intent = intent;
                    if (newGoal) duty.Arrived = false;
                    duty.IdleGoal = BlacksiteIdleGoal.Observe; duty.IdleBlocked = 0; duty.IdleCovering = false;
                    duty.Retry = false;
                    Issue(soldier, duty, order, intent, now, reason,
                        arrived ? "arrived" : reason == "retry" ? "retry" : "intent", follow, cause);
                    // #955 S12A U2: the advance is spent by the order it produced, and the navigator
                    // is told here rather than when the planner advanced.
                    if (roomAdvance || beatReady)
                    {
                        duty.RoomAdvance = null;
                        soldier.Navigator?.NoteRoomAdvance();
                    }
                    // #955 S12A U2 (b): the lead taking a new leg IS the squad's step-off (#1305 S: and its followers' advance).
                    if (newGoal) _manifest.NoteRouteStepOff(soldier, duty.Order.Destination, goalMove, reason == "retry");
                }
                else
                {
                    RefreshInPlace(soldier, duty, now, "refresh");
                }

                // #1018 S4 (design note C.3): the idle rule is no longer an ORDER. The 4 s re-arm
                // still records [UAT][Autonomy] and still chooses through BlacksiteIdlePolicy, but
                // what it produces is the assignment's successor set, not a destination: the idle
                // Action reads the set on its own clock and the planner never writes duty.Order for
                // an idle step again (writer 3 of design note A.2, deleted).
                ProveSuccessors(soldier, duty, now);
            }

            /// <summary>
            /// #955 S12A U2, synchronisation guard (b). On a room advance every raider plans at
            /// once, and this is what stops six bodies stepping off on the same frame.
            /// <c>…Duties.cs:332-338</c> verbatim, with the clock and (#1305 S) the cause as parameters.
            /// </summary>
            private bool TryHoldForRouteBeat(BlacksiteSoldierAI soldier, Duty duty, BlacksiteMemberOrder order, string cause, float now)
            {
                if (_manifest.RouteBeatDue(soldier, out float beat)) return false;
                duty.Pending = order; duty.HasPending = true; duty.PendingAt = beat; duty.PendingCause = cause;
                duty.RoomAdvance = null;
                // #1293 R3: the beat is this member's deadline.
                duty.NextPlan = Mathf.Min(duty.NextPlan, beat);
                return true;
            }

            /// <summary>
            /// #1293 R2 (rule 3): a stranded member - its agent cannot bind where its body stands -
            /// holds at its own feet. The hold is issued once and refreshed in place while the fact
            /// stands. #1293 R3: the bind that clears the fact is an Outcome event; Retry makes it plan.
            /// </summary>
            private void HoldStranded(BlacksiteSoldierAI soldier, Duty duty,
                (BlacksiteJobKind Kind, ulong Object, BlacksiteObjectivePhase Phase, bool Outside) intent, float now)
            {
                duty.Retry = true;
                if (duty.Assignment.IsValid && duty.Assignment.Source == StrandedSource)
                {
                    RefreshInPlace(soldier, duty, now, "refresh");
                    return;
                }
                duty.Intent = intent;
                Vector3 at = soldier.transform.position;
                Issue(soldier, duty, MakeOrder(BlacksiteSquadOrderKind.Hold, at, at + soldier.transform.forward),
                    intent, now, StrandedSource, StrandedSource);
            }

            private const string StrandedSource = "stranded", RoomAdvanceSource = "roomAdvance";

            // ---------------------------------------------------------------------------------
            // Issue and refresh (design note C.2).
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #1018 S4: the ONE write. <c>Sequence</c> is <c>++_sequence[member]</c> - per member,
            /// monotonic, never reset inside a mission - and <c>IssuedAt</c> is now (design note
            /// C.2). #1293 R2: no expiry is stamped; the assignment stands until it is replaced.
            /// #1293 R3: a new assignment is the member's Order decision and its followers' event.
            /// </summary>
            private void Issue(BlacksiteSoldierAI soldier, Duty duty, BlacksiteMemberOrder order,
                (BlacksiteJobKind Kind, ulong Object, BlacksiteObjectivePhase Phase, bool Outside) intent,
                float now, string source, string reason, in BlacksiteFollowOrder follow = default, string advanceCause = null)
            {
                ulong id = soldier.NetworkObjectId;
                _sequence.TryGetValue(id, out int sequence);
                _sequence[id] = ++sequence;
                var stamped = ShapeDutyOrder(soldier, intent.Kind, order.WithDutyKind(intent.Kind));
                duty.Order = stamped;
                duty.Sequence = sequence; duty.IssuedCause = advanceCause;
                duty.MemberIndex = soldier.MemberIndex;
                // #874 P-S2: every issue says whether it is a Follow; a plain one ends any follow.
                duty.Follow = follow.IsFollow ? follow.WithSequence(sequence) : default;
                if (follow.IsFollow && follow.Anchor != null) duty.FollowAnchorAt = follow.Anchor.transform.position;
                // #874 F05: the world this approach was planned in, for NoteMemberCrossedWorld.
                duty.IssuedOutside = soldier.isOutside;
                // #874 S8.8 / P-S1: the planner measures the destination the navigator will
                // actually issue, because it IS the order's destination. The guard leash is applied
                // where the Guard duty's goal is chosen (ShapeDutyOrder), not by the navigator, so
                // there is no second point for `remaining=` to drift from; and a Board, Egress or
                // response order is never leashed at all.
                duty.TaskGoal = stamped.Destination;
                duty.HasTaskGoal = true;
                duty.Blocked = _manifest._planningBlocked.TryGetValue(id, out var cause) ? cause : BlacksiteBlockedCause.None;
                var goalKind = ResolveGoalKind(soldier, stamped, duty.Blocked, follow.IsFollow);
                // #1018 S4 (design note C.5): a hold at the member's own feet is a REFUSAL, and the
                // cause travels with it so the observation half and the log both name it. This is
                // the one place the cause is inferred rather than read, and it is inferred from the
                // support pass's own fallback, not from coordinates.
                if (goalKind == BlacksiteAssignmentGoalKind.Hold
                    && duty.Blocked == BlacksiteBlockedCause.None
                    && Vector3.Distance(stamped.Destination, soldier.transform.position)
                        <= BlacksiteArrivalPolicy.MinDestinationChange)
                    duty.Blocked = BlacksiteBlockedCause.NoReachableCandidate;
                // #1293 R3: the goal's world row and its revision now (ObserveSquad compares them).
                duty.GoalRow = GoalRowOf(id, intent.Kind, intent.Object);
                duty.GoalRowRevision = TryGetGoalRow(duty.GoalRow, out var goalRow) ? goalRow.Revision : 0;
                var assignment = new BlacksiteSquadAssignment(
                    id, sequence, now, source,
                    intent.Kind, intent.Object,
                    stamped.SquadOrder.Kind, stamped.SquadOrder.Formation,
                    stamped.SquadOrder.Sequence, stamped.SquadOrder.Anchor, stamped.SquadOrder.Forward,
                    stamped.SquadOrder.Confidence, stamped.SquadOrder.UncertaintyRadius,
                    stamped.SquadOrder.ExactContact, goalKind,
                    stamped.Destination, stamped.PreferredFacing,
                    stamped.ArrivalTolerance, stamped.SeparationRadius,
                    stamped.IsInteractionOperator, stamped.IsPlayerResponse, stamped.TargetOutside,
                    stamped.HasAssignedSweepPoint, stamped.AssignedSweepPoint,
                    stamped.Approach, duty.Blocked, default, stamped.Pace);
                duty.Assignment = assignment;
                _assignments[id] = assignment;
                Report(soldier, assignment, reason, duty.IssuedCause, now);
                soldier.RequestDecision(BlacksiteDecisionCause.Order);
                MarkFollowersOf(id);
            }

            /// <summary>
            /// #1018 S4 (C.2): a refresh - same destination, no <c>Sequence</c> bump. #1233 S3: a held
            /// Follow re-derives its goal from the anchor. #1293 R3: a refresh that changes what the
            /// member is told is an Order decision and its followers' event.
            /// </summary>
            private void RefreshInPlace(BlacksiteSoldierAI soldier, Duty duty, float now, string reason)
            {
                ulong id = soldier.NetworkObjectId;
                if (!duty.Assignment.IsValid) return;
                var was = duty.Assignment;
                duty.Blocked = _manifest._planningBlocked.TryGetValue(id, out var cause) ? cause : duty.Blocked;
                bool follow = FollowHeld(duty);
                if (follow && duty.Follow.Anchor != null)
                {
                    duty.Order = FollowGoal(soldier, duty.Follow.Anchor, duty.Follow.Follow.Rank).WithDutyKind(duty.Intent.Kind);
                    duty.TaskGoal = duty.Order.Destination;
                    duty.FollowAnchorAt = duty.Follow.Anchor.transform.position;
                }
                // #1018 S4 (fix batch 1, SF9): the goal kind is RE-RESOLVED on every refresh. It is
                // a function of where the member is standing relative to its goal and of the blocked
                // cause, and both move under a member that has not been re-issued: a Travel leg
                // whose member walked onto its own goal, or a support slot that has since been
                // refused, would otherwise keep the kind it was issued with for the rest of the
                // assignment's life.
                var goalKind = ResolveGoalKind(soldier, duty.Order, duty.Blocked, follow);
                var refreshed = duty.Assignment.Refreshed(duty.Blocked, goalKind, duty.Order);
                duty.Assignment = refreshed;
                _assignments[id] = refreshed;
                Report(soldier, refreshed, reason, duty.IssuedCause, now);
                if (was.GoalKind == refreshed.GoalKind && was.Blocked == refreshed.Blocked
                    && was.Destination == refreshed.Destination && was.Facing == refreshed.Facing) return;
                soldier.RequestDecision(BlacksiteDecisionCause.Order);
                MarkFollowersOf(id);
            }

            /// <summary>
            /// #1018 S4 / #872 D8 (design note C.5): what the destination MEANS.
            ///
            /// The authoritative source is <c>SupportPoint</c>'s own
            /// <see cref="BlacksiteSupportFallback"/> terminal, which needs an <c>out fallback</c>
            /// parameter it does not have yet - that edit is block 7's, because it cannot be made
            /// without touching <c>…Jobs.cs</c>, which blocks 1 and 2 may not do. Until then the
            /// kind is resolved from the three facts the planner can already see, which cover every
            /// production case:
            /// <list type="bullet">
            /// <item>an order that ends in an action carries its reach contract, so it is
            /// <see cref="BlacksiteAssignmentGoalKind.Interact"/>;</item>
            /// <item><see cref="BlacksiteSquadOrderKind.Hold"/> is produced by the guard post and
            /// the retired-member refusal, so it is a
            /// <see cref="BlacksiteAssignmentGoalKind.Post"/> when the point is somewhere else and a
            /// <see cref="BlacksiteAssignmentGoalKind.Hold"/> when it is the member's own feet;</item>
            /// <item>a refused resolution whose destination IS the member's own feet is a hold with
            /// the hold said out loud, which is what removes the <c>remaining=Infinity</c> reading
            /// that <c>NoReachableCandidate</c> produced in the pre-S4 board
            /// (<c>…Duties.cs:869-870</c> before #1018 S4).</item>
            /// </list>
            ///
            /// Block 7 closed the authoritative half: <c>SupportPoint</c> now reports which of its
            /// two <see cref="BlacksiteSupportFallback"/> answers it gave, per member, cached with
            /// the point it cached. <c>HoldWhereItStands</c> is a
            /// <see cref="BlacksiteAssignmentGoalKind.Hold"/> at the member's position and carries
            /// <see cref="BlacksiteBlockedCause.NoReachableCandidate"/>; <c>Ring</c> is a place to
            /// WALK TO and is <see cref="BlacksiteAssignmentGoalKind.Travel"/>. #1283 S6: the fallback
            /// labels a hold only at the member's own feet, because an escort whose pass answered
            /// <c>HoldWhereItStands</c> falls through to the squad route, which is no hold.
            /// </summary>
            private BlacksiteAssignmentGoalKind ResolveGoalKind(BlacksiteSoldierAI soldier,
                BlacksiteMemberOrder order, BlacksiteBlockedCause blocked, bool follow)
            {
                // #874 P-S2: a Follow is always a travel leg. Its point is where the goal stood at
                // issue, so "at its own feet" says nothing, and the support ring's fallback it may
                // have been planned beside is answered by the follow itself.
                if (follow) return BlacksiteAssignmentGoalKind.Travel;
                bool atOwnFeet = Vector3.Distance(order.Destination, soldier.transform.position)
                                 <= BlacksiteArrivalPolicy.MinDestinationChange;
                if (order.SquadOrder.Kind == BlacksiteSquadOrderKind.Hold)
                    return atOwnFeet ? BlacksiteAssignmentGoalKind.Hold : BlacksiteAssignmentGoalKind.Post;
                if (atOwnFeet && _manifest._supportFallback.TryGetValue(soldier.NetworkObjectId, out var fallback)
                    && fallback == BlacksiteSupportFallback.HoldWhereItStands)
                    return BlacksiteAssignmentGoalKind.Hold;
                if (blocked == BlacksiteBlockedCause.NoReachableCandidate && atOwnFeet)
                    return BlacksiteAssignmentGoalKind.Hold;
                if (order.Approach.HasValue) return BlacksiteAssignmentGoalKind.Interact;
                // A Travel leg whose destination happens to sit within the minimum destination
                // change of the member is an ARRIVED leg, not a refusal, and calling it a hold
                // would give it the 6 s idle bound instead of the travel budget. PickupApproach's
                // own refusal (…Raid.cs:606) writes no blocked cause, so it is the one case this
                // cannot yet tell from an arrival; block 7 resolves it with SupportPoint's
                // out fallback rather than by guessing from coordinates here.
                return BlacksiteAssignmentGoalKind.Travel;
            }

            // ---------------------------------------------------------------------------------
            // The escalations.
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #1009 A1, #1293 R3: a member whose successors are all refused is due a plan NOW: marked for the next
            /// pass with its goal revised.
            /// </summary>
            internal void RequestImmediateReplan(BlacksiteSoldierAI soldier)
            {
                if (soldier == null) return;
                if (_duties.TryGetValue(soldier.NetworkObjectId, out var duty)) duty.ReviseAt = Time.time;
                _dirty.Mark(soldier.NetworkObjectId);
            }

            /// <summary>
            /// #874 F05: this member completed a world transfer, so the APPROACH it holds is obsolete
            /// while its INTENT is not: the assignment is dropped AT the crossing and <see cref="TryGetOrder"/>
            /// answers no order until the next plan; Intent, Budget, Order, TaskGoal and Retry survive.
            /// #1293 R3: every transfer marks the member; a dropped approach is an Order decision.
            /// </summary>
            internal bool NoteMemberCrossedWorld(BlacksiteSoldierAI soldier, string cause)
            {
                if (soldier == null) return false;
                _dirty.Mark(soldier.NetworkObjectId);
                if (!_duties.TryGetValue(soldier.NetworkObjectId, out var duty))
                    return false;
                // Nothing issued, or issued for the world the member is standing in now. The second
                // case is the one that must not be over-reached: an Extract, a retreat or a renewed
                // pursuit back through the same door is a current approach, not a stale one.
                if (!duty.Assignment.IsValid || duty.IssuedOutside == soldier.isOutside)
                    return false;

                ulong id = soldier.NetworkObjectId;
                duty.Assignment = default;
                _assignments.Remove(id);
                // The successors were proved in the other world and went with the assignment; the
                // frontier doorway that named one of them goes with them.
                duty.IdleGoal = BlacksiteIdleGoal.Observe; duty.IdleBlocked = 0; duty.IdleCovering = false;
                duty.FrontierDoorway = 0L;
                // Arrival at, and a held beat toward, a point in the world it left.
                duty.Arrived = false; duty.ArrivedGoal = Vector3.zero;
                duty.HasPending = false; duty.Pending = default; duty.PendingAt = 0f;
                // The blockage was about the old approach. Cleared the same way a re-site clears
                // it (…S12 U4 W15), so the planners that read the map next do not inherit it.
                duty.Blocked = BlacksiteBlockedCause.None;
                // #874 P-S2: a follow taken in the world it left, with the approach it rode on.
                duty.Follow = default;
                _manifest._planningBlocked.Remove(id);
                BlacksiteDiagnostics.Log("Navigation",
                    $"member={soldier.MemberIndex}, world transfer completed, outside={soldier.isOutside}, "
                    + $"issuer=duty:{duty.Intent.Kind}, approach invalidated, cause={cause ?? BlacksiteDiagnosticFormat.None}");
                soldier.RequestDecision(BlacksiteDecisionCause.Order);
                MarkFollowersOf(id);
                return true;
            }

            /// <summary>
            /// #1018 S4: the member's real work goal - the point progress is measured against,
            /// which for an operator is its validated approach point. <c>TryRouteSlotOrder</c> asks
            /// it so the formation anchors on what the LEAD is actually doing.
            /// #1233 S3 (rule 2): where the member walks in its own world - a held Follow's anchor
            /// where it stands now, and for a goal in the other world (an inside Board or Extract, an
            /// outside Infiltrate; #1305 G2) the link's near side, where the route crosses.
            /// </summary>
            internal bool TryGetTaskGoal(BlacksiteSoldierAI member, out Vector3 goal)
            {
                goal = Vector3.zero;
                if (member == null || !_duties.TryGetValue(member.NetworkObjectId, out var duty) || !duty.HasTaskGoal) return false;
                var kind = duty.Assignment.OrderKind;
                if (FollowHeld(duty) && duty.Follow.Anchor != null)
                    goal = FollowGoal(member, duty.Follow.Anchor, duty.Follow.Follow.Rank).Destination;
                else if (duty.Assignment.IsValid && (member.isOutside
                        ? kind == BlacksiteSquadOrderKind.Infiltrate
                        : kind == BlacksiteSquadOrderKind.Extract || kind == BlacksiteSquadOrderKind.Board)
                    && TryGetCrossingLink(member, out Vector3 nearSide, out _))
                    goal = nearSide;
                else goal = duty.TaskGoal;
                return true;
            }

            /// <summary>#1305 S: his task goal, else the goal his issued destination serves - a moving follower keeps the goal he walks, never a search node or his feet.</summary>
            internal Vector3? WalkingGoal(BlacksiteSoldierAI member)
                => TryGetTaskGoal(member, out Vector3 goal) ? goal : member.Navigator.HasIssuedDestination ? member.Navigator.IssuedGoal : (Vector3?)null;

            /// <summary>
            /// #955 S12A U2: whether this member has ARRIVED at the post it was given, by the
            /// planner's own arrival latch rather than by a bare radius. #874 Q6's patrol tick
            /// (<c>TickPatrol</c>) asks it to decide whether the leg to the waypoint was walked,
            /// which is what starts the dwell there.
            /// </summary>
            internal bool ArrivedAtPost(ulong member, Vector3 post)
                => _duties.TryGetValue(member, out var duty) && duty.Arrived
                   && Vector3.Distance(duty.ArrivedGoal, post) <= BlacksiteArrivalPolicy.MinDestinationChange;

            /// <summary>#955 S12A U2: the lead's goal changed, so this member's next assignment is worth planning;
            /// #1293 R3: it marks him. #1305 S: <paramref name="stall"/>: the lead's stall moved the goal.</summary>
            internal void NoteRoomAdvance(ulong member, bool stall)
            {
                if (_duties.TryGetValue(member, out var duty)) duty.RoomAdvance = stall;
                _dirty.Mark(member);
            }

            /// <summary>
            /// #1018 S4 (C.4): every successor refused - recorded as
            /// <see cref="BlacksiteBlockedCause.NoReachableCandidate"/>, and the member is marked.
            /// </summary>
            internal void NoteIdleExhausted(ulong member)
            {
                _dirty.Mark(member);
                if (!_duties.TryGetValue(member, out var duty)) return;
                duty.Blocked = BlacksiteBlockedCause.NoReachableCandidate;
            }

            /// <summary>
            /// #957 S12A U4 / #1018 S4: the facing sweep's target list, for the idle Action's Hold
            /// and for its GuardPost successor. The one-argument entry point exists because the
            /// soldier has no access to the planner's plan state; the list itself is built by the
            /// two-argument rule below, which is the board's own <c>BeginIdleSweep</c> moved here.
            /// A member with no plan state still gets a list - the crossing and its own formation
            /// facing - because a sweep with no targets would leave the hold looking at nothing.
            /// </summary>
            internal void BeginIdleSweep(BlacksiteSoldierAI soldier)
            {
                if (soldier == null) return;
                if (!_duties.TryGetValue(soldier.NetworkObjectId, out var duty)) duty = new Duty();
                BeginIdleSweep(soldier, duty);
            }

            // ---------------------------------------------------------------------------------
            // #874 S4: the planner's answer to the ledger's stalled(goal, pathStatus).
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #874 S4: the ONE answer to <c>stalled</c>. #1293 R2: the progress ledger
            /// (<see cref="BlacksiteMotionMonitor"/>) saw this member's judged commitment make no
            /// progress for <see cref="BlacksiteMotionMonitor.WindowSeconds"/>, or the motion owner
            /// refused its route. The answer is always a DIFFERENT goal, never the same goal re-routed:
            /// <list type="bullet">
            /// <item>every lease the member holds or waits for is released first
            /// (<c>ReleaseAllHeldBy</c>), so each lane hands its slot to the next waiter: a queue
            /// moves when its holder completes or is answered here, and nothing else evicts;</item>
            /// <item>an entrance crossing the member had begun is withdrawn, so its doorway
            /// reservation goes with the goal, and a lead waypoint it was holding is let go;</item>
            /// <item>a record the member holds: dropped or released by the manifest
            /// (<c>AnswerStalledRecord</c>);</item>
            /// <item>Backup: the guard keeps its post (#1271 S3b) and re-plans; PlayerResponse: the
            /// response defers (<c>DeferPlayerResponse</c>) and the next plan is the mission duty;</item>
            /// <item>a patrol leg: the next live leg (<c>TryAnswerStalledPatrol</c>);</item>
            /// <item>any other duty: <see cref="RetargetStalledDuty"/>.</item>
            /// </list>
            /// The signature is fixed (native fixtures reflect on it). #1293 R3: the stall marks the
            /// member, and a closed attempt is its followers' event.
            /// </summary>
            internal void NoteStalled(BlacksiteSoldierAI member, Vector3 goal, BlacksitePathState pathStatus)
            {
                if (member == null) return;
                _dirty.Mark(member.NetworkObjectId);
                AnswerStalled(member, goal, "stalled path=" + pathStatus);
            }

            /// <summary>
            /// #1293 R2 (rule 3): this member entered the stranded fact: the stall answer's releases run
            /// once, and the mark makes the next pass hold him (<see cref="HoldStranded"/>).
            /// </summary>
            internal void NoteStranded(BlacksiteSoldierAI member)
            {
                if (member == null) return;
                _dirty.Mark(member.NetworkObjectId);
                ReleaseHeld(member, StrandedSource, stranded: true);
            }

            private void AnswerStalled(BlacksiteSoldierAI member, Vector3 goal, string cause)
            {
                if (member == null) return;
                if (ReleaseHeld(member, cause, stranded: false)) return;
                ulong id = member.NetworkObjectId;
                if (!_duties.TryGetValue(id, out var duty)) return;
                float now = Time.time;
                var kind = duty.Intent.Kind;
                if (kind == BlacksiteJobKind.Backup || kind == BlacksiteJobKind.PlayerResponse)
                {
                    if (kind == BlacksiteJobKind.PlayerResponse) _manifest.DeferPlayerResponse(member, duty.Blocked);
                    duty.Budget.Finish(false);
                    duty.Retry = true;
                    MarkFollowersOf(id);
                    member.Navigator.InvalidateDestination(false);
                    return;
                }
                if (TryAnswerStalledPatrol(member, goal)) return;
                RetargetStalledDuty(member, duty, goal, cause, now);
            }

            // #1293 R2: every miss releases first - the member's leases, his doorway crossing, the
            // lead waypoint and any record he holds. True when the record answer was the answer.
            private bool ReleaseHeld(BlacksiteSoldierAI member, string cause, bool stranded)
            {
                if (_manifest._mission != null && _manifest._mission.IsServer)
                    _manifest._mission.Blackboard.Broker.ReleaseAllHeldBy(member.NetworkObjectId);
                member.WithdrawEntranceCrossing(stranded ? StrandedSource : "stalled");
                _manifest.ReleaseLeadGoal(member);
                return _manifest.AnswerStalledRecord(member, cause, stranded);
            }

            /// <summary>
            /// #874 S4 (was <c>CloseUnreachable</c>, #874 Q1): the attempt closes
            /// <see cref="BlacksiteJobResult.Unreachable"/> and a goal at least
            /// <see cref="BlacksiteArrivalPolicy.MinDestinationChange"/> from the stalled one is issued
            /// in this same answer (<see cref="BlacksiteSquadPlannerRules.RetargetUnreachable"/>): a
            /// goal in the other world is re-sited to the crossing (<see cref="CrossingFor"/>); otherwise the duty's
            /// producers plan again and must name a different point. With no different goal the
            /// member is refused (<see cref="BlacksiteBlockedCause.NoReachableCandidate"/>, a hold at
            /// its own feet); the Clock and the return rule bound the mission from there.
            /// </summary>
            private void RetargetStalledDuty(BlacksiteSoldierAI soldier, Duty duty, Vector3 stalled, string cause, float now)
            {
                ulong id = soldier.NetworkObjectId;
                var kind = duty.Intent.Kind;
                int attempt = duty.Budget.Generation;
                var closed = duty.Budget.FinishUnreachable();
                _manifest._planningBlocked.Remove(id);
                duty.Arrived = false; duty.Retry = false; duty.ReviseAt = float.PositiveInfinity;
                duty.IdleGoal = BlacksiteIdleGoal.Observe; duty.IdleBlocked = 0; duty.IdleCovering = false;
                bool goalOutside = GoalOutside(duty.Order, out bool known);
                // #1233 S2: the crossing is picked only toward the member's own world by
                // RequiresInterior, the entrance gate's predicate, so the retarget is never a door the
                // gate refuses; otherwise the duty's producers re-plan in this world.
                bool crosses = known && goalOutside != soldier.isOutside && goalOutside != _manifest.RequiresInterior(soldier);
                Vector3 crossing = CrossingFor(soldier);
                var replanned = ShapeDutyOrder(soldier, kind, PlanDutyOrder(soldier, out var follow));
                var pick = BlacksiteSquadPlannerRules.RetargetUnreachable(
                    Vector3.Distance(crossing, stalled), !crosses,
                    Vector3.Distance(replanned.Destination, stalled), false);
                BlacksiteMemberOrder order = replanned;
                if (pick == BlacksiteUnreachableRetarget.Crossing)
                {
                    order = MakeOrder(soldier.isOutside ? BlacksiteSquadOrderKind.Infiltrate : BlacksiteSquadOrderKind.Extract,
                        crossing, duty.Order.Destination);
                    follow = default;
                }
                BlacksiteDiagnostics.Log("Job",
                    $"member={soldier.MemberIndex}, kind={kind}, attempt={attempt}, terminal={closed}, goal={stalled}"
                    + $", cause={cause}, retarget={pick}"
                    + (pick == BlacksiteUnreachableRetarget.None ? string.Empty : $", next={order.Destination}") + ".");
                soldier.Navigator.InvalidateDestination(false);
                if (pick == BlacksiteUnreachableRetarget.None)
                {
                    duty.Blocked = BlacksiteBlockedCause.NoReachableCandidate;
                    duty.Retry = true;
                    MarkFollowersOf(id);
                    return;
                }
                BeginDutyAttempt(soldier, duty, now, kind);
                Issue(soldier, duty, order, duty.Intent, now, BlacksiteSquadPlannerRules.UnreachableReason,
                    BlacksiteSquadPlannerRules.UnreachableReason, follow);
                _manifest.NoteRouteStepOff(soldier, duty.Order.Destination, Vector3.Distance(duty.Order.Destination, stalled), true);
            }

            /// <summary>
            /// #1305 G2 (R16): a crossing order's goal - the far side of the squad's chosen door, in the
            /// world the member is going to; its route takes the doorway.
            /// </summary>
            internal Vector3 CrossingFor(BlacksiteSoldierAI soldier)
                => soldier.isOutside
                    ? _manifest._mission.SquadDirector.InsideMainEntrance
                    : _manifest._mission.SquadDirector.OutsideMainEntrance;

            /// <summary>
            /// Which world an assignment goal stands in: every goal the order producers issue is one of
            /// a small closed set whose world is already known (#1305 G2: a Board or Extract goal is
            /// outside, an Infiltrate goal inside), and every other kind plans in the member's own
            /// world by construction. The order executor reads it too (a corridor walked to its end).
            /// </summary>
            internal static bool GoalOutside(BlacksiteMemberOrder order, out bool known)
            {
                known = true;
                switch (order.SquadOrder.Kind)
                {
                    case BlacksiteSquadOrderKind.Board:
                    case BlacksiteSquadOrderKind.Extract:
                        return true;
                    case BlacksiteSquadOrderKind.Infiltrate:
                        return false;
                    default:
                        known = false; return false;
                }
            }

            // ---------------------------------------------------------------------------------
            // Release - ONE path, four callers (design note C.2).
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #1018 S4: the one idempotent release path (a <c>ulong</c> token: the soldier may be
            /// destroyed). #1293 R3: dropping plan state marks every member and is the released one's Order.
            /// </summary>
            internal void Release(ulong member, string cause)
            {
                bool held = _assignments.TryGetValue(member, out var released) && released.IsValid;
                // SF4: the member INDEX, stamped on the plan state at issue time. Resolving it from
                // _members here could not work on the path that matters - the membership sweep runs
                // after a casualty has already left the list - so the release line named nobody.
                int index = _duties.TryGetValue(member, out var releasedDuty) ? releasedDuty.MemberIndex : -1;
                bool planned = releasedDuty != null || _assignments.ContainsKey(member);
                for (int i = 0; i < _manifest._members.Count; i++)
                    if (_manifest._members[i] != null && _manifest._members[i].NetworkObjectId == member)
                    {
                        _manifest._members[i].Navigator.EndPatrolAlert();
                        if (planned) _manifest._members[i].RequestDecision(BlacksiteDecisionCause.Order);
                    }
                _dirty.Forget(member);
                if (planned) _dirty.MarkAll();
                _duties.Remove(member);
                _assignments.Remove(member);
                _reported.Remove(member);
                // #1219 B4: a released member's crossing count goes with it (NGO recycles network
                // ids, so a stale entry could leak to a new member).
                _crossings.Remove(member);
                // The eleven per-member dictionaries ForgetDuty:958-966 clears, plus this stage's own.
                _manifest._planningBlocked.Remove(member); _manifest._searchRoutes.Remove(member);
                _manifest._operatorLane.Remove(member);
                _manifest._supportPoints.Remove(member); _manifest._supportCenters.Remove(member);
                _manifest._supportUntil.Remove(member); _manifest._supportMisses.Remove(member);
                _manifest._supportCursor.Remove(member); _manifest._blockedSupport.Remove(member);
                _manifest._supportFallback.Remove(member);
                _manifest._patrolPoints.Remove(member); _manifest._guardDuty.Remove(member);
                _manifest._patrolSource.Remove(member);
                _patrolTickAt.Remove(member); _patrolExchangePoints.Remove(member);
                _manifest._focusLogged.Remove(member);
                // #1018 S4 (fix batch 2, S9): the member's 90 s doorway stamps go too. They are
                // keyed by (member, doorway), so a released member left every doorway it had been
                // sent to spent for 90 s against a token nobody would ask about again - and a
                // reused network object id would inherit them. Drained to a scratch list first,
                // because a dictionary is not enumerated while it is being written (design note
                // G10 d).
                _doorwaySweep.Clear();
                foreach (var stamp in _manifest._idleDoorwayVisited)
                    if (stamp.Key.Member == member) _doorwaySweep.Add(stamp.Key);
                for (int i = 0; i < _doorwaySweep.Count; i++) _manifest._idleDoorwayVisited.Remove(_doorwaySweep[i]);
                _doorwaySweep.Clear();
                // #955 S12A U2: a member the planner has retired holds no place in the route's column.
                foreach (var column in _manifest._routeColumns.Values) column.Leave(member);
                if (_manifest._mission != null && _manifest._mission.IsServer)
                    _manifest._mission.Blackboard.Broker.ReleaseAllHeldBy(member);
                // E.1's key list is closed and its `reason` vocabulary carries `release`, so the
                // released assignment is reported in its own terms. Two things about this line are
                // constrained by that closed list and are called out rather than worked around:
                // `member=` is a member INDEX everywhere else in the area, and Release takes a
                // token because every one of its five arrivals can have the Unity object already
                // destroyed (design note G10 e) - so the index is read from the plan state, where
                // Issue stamped it (SF4). And the release CAUSE has no key in the list at all; it is
                // deliberately not smuggled into one, and stays a parameter for the lease and
                // blackboard paths above and for block 5's four callers.
                if (held && BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("Planner",
                        $"member={index}, kind={released.Kind}, seq={released.Sequence}"
                        + $", goalKind={released.GoalKind}, goal={BlacksiteDiagnosticFormat.V(released.Destination)}"
                        + $", source={released.Source ?? BlacksiteDiagnosticFormat.None}, reason=release"
                        + $", blocked={(released.Blocked != BlacksiteBlockedCause.None ? released.Blocked.ToString() : BlacksiteDiagnosticFormat.None)}"
                        + $", wait={BlacksiteDiagnosticFormat.None}.");
            }

            /// <summary>A new mission carries no assignment, no plan state and no line gate.</summary>
            internal void Clear()
            {
                // Only what the planner OWNS (B2). The shared per-member dictionaries are cleared by
                // the manifest's own ClearDutyJobs, which is the one place a new mission resets them.
                _assignments.Clear(); _duties.Clear(); _sequence.Clear();
                // #1293 R3: and nothing is marked or leaving.
                _dirty.Clear(); _squadLeaving = false;
                // #1219 B2: a new mission counts no crossings.
                _crossings.Clear();
                // #874 Q6: the guard patrol loop is proved per rendezvous, but a new mission on the
                // same moon can land on the same rendezvous, and its dead marks are this planner's
                // own.
                ClearPatrolLoop();
                // SF7: the gates and the gate's own mirror. A reused member index inherited the last
                // deployment's last reported (kind, seq) pair, so the first real assignment of a new
                // mission could be folded away as "unchanged".
                _reported.Clear();
                _plannerGate.Reset(); _plannerWindow.Reset();
            }

            // ---------------------------------------------------------------------------------
            // [UAT][Planner] (design note E.1).
            // ---------------------------------------------------------------------------------

            private const float PlannerRepeatSeconds = 5f;
            private readonly BlacksiteChangeGate _plannerGate = new BlacksiteChangeGate();
            private readonly BlacksiteWindowGate _plannerWindow = new BlacksiteWindowGate(PlannerRepeatSeconds);
            // The last (kind, seq) pair a gate key was composed for, per member. Mirrors the change
            // gate's own state so the key string is built only when the pair moves, which is the
            // [UAT][Brain] emitter's pattern and the only allocation on this path.
            private readonly Dictionary<ulong, (BlacksiteJobKind Kind, int Sequence)> _reported
                = new Dictionary<ulong, (BlacksiteJobKind, int)>();

            /// <summary>
            /// #1018 S4 (E.1, G13): keys <c>member, kind, seq, goalKind, goal, source, reason, cause</c> (#1305 S,
            /// source=roomAdvance only) <c>blocked, wait</c>, once per <c>kind|seq</c> and at most every 5 s otherwise.
            /// </summary>
            private void Report(BlacksiteSoldierAI soldier, in BlacksiteSquadAssignment assignment,
                string reason, string cause, float now)
            {
                if (!BlacksiteDiagnostics.Enabled) return;
                int member = soldier.MemberIndex;
                // The window gate is evaluated on EVERY report, never short-circuited: it is what
                // keeps a standing assignment visible at 0.2 Hz, and it counts its own folds.
                bool due = _plannerWindow.ShouldReport(member, now, out _);
                bool changed = false;
                if (!_reported.TryGetValue(assignment.Member, out var was)
                    || was.Kind != assignment.Kind || was.Sequence != assignment.Sequence)
                {
                    _reported[assignment.Member] = (assignment.Kind, assignment.Sequence);
                    changed = _plannerGate.ShouldReport(member, "planner",
                        assignment.Kind + "|" + assignment.Sequence, out _);
                }
                if (!changed && !due) return;
                string wait = BlacksiteDiagnosticFormat.None;
                if (_manifest._mission != null && _manifest._mission.IsServer
                    && _manifest._mission.Blackboard.Broker.TryGetWaitKey(assignment.Member, out BlacksiteResourceKey key))
                    wait = key.Token;
                BlacksiteDiagnostics.Log("Planner",
                    $"member={member}, kind={assignment.Kind}, seq={assignment.Sequence}"
                    + $", goalKind={assignment.GoalKind}, goal={BlacksiteDiagnosticFormat.V(assignment.Destination)}"
                    + $", source={assignment.Source ?? BlacksiteDiagnosticFormat.None}, reason={reason}"
                    + (assignment.Source == RoomAdvanceSource ? $", cause={cause ?? BlacksiteDiagnosticFormat.None}" : string.Empty)
                    + $", blocked={(assignment.Blocked != BlacksiteBlockedCause.None ? assignment.Blocked.ToString() : BlacksiteDiagnosticFormat.None)}"
                    + $", wait={wait}.");
            }
        }
    }
}
