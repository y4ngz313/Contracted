using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1016 S2: one per mission, host-side, hung off the mission controller exactly as
    /// <c>RoutePlanner</c> is (<c>BlacksiteRetrievalMissionController.cs:22-23</c>).
    ///
    /// Design note A.4's finding shapes this type: of the eight squad-level concerns, only the
    /// claims are unowned. Everything else already has exactly one owner,
    /// and for the objective record that owner is a <c>NetworkList</c> - moving it would take
    /// replicated state off the wire, which the stage's hard constraints forbid. So this is NOT a
    /// data migration. It is:
    /// <list type="number">
    /// <item>the <see cref="Broker"/>, which gives the nine claim stores one lifetime model;</item>
    /// <item>a typed READ facade over the current owners, so S3's <c>Sense()</c> and S4's planner
    /// have one address to read from while the storage stays where it is until S6;</item>
    /// <item>the emitters for the two new diagnostic tags, because the broker must not know what a
    /// logger is.</item>
    /// </list>
    ///
    /// The blackboard publishes no assignment and writes no <c>duty.Order</c>. #1293 R3: the squad
    /// storey goal (design note C.4) and the support anchor are deleted; R4 deleted their elevator
    /// readers.
    /// </summary>
    internal sealed class BlacksiteSquadBlackboard
    {
        private readonly BlacksiteRetrievalMissionController _mission;
        private readonly BlacksiteResourceBroker _broker = new BlacksiteResourceBroker();

        internal BlacksiteSquadBlackboard(BlacksiteRetrievalMissionController mission)
        {
            _mission = mission;
        }

        internal BlacksiteResourceBroker Broker => _broker;

        // -----------------------------------------------------------------------------------
        // Read facade over the CURRENT owners. No storage moves in S2 (design note A.4).
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// One manifest record by object id. Read straight off
        /// <c>BlacksiteRetrievalMissionController.ObjectiveSnapshots</c>, the replicated list that
        /// IS the claim (design note C.5): the record stays authoritative and on the wire, and the
        /// broker's <c>Objective:&lt;id&gt;</c> lease is a host-side shadow granted and released in
        /// the same statements.
        /// </summary>
        internal bool TryGetObjectiveRecord(ulong objectId, out BlacksiteObjectiveSnapshot record)
        {
            record = default;
            var snapshots = _mission != null ? _mission.ObjectiveSnapshots : null;
            if (snapshots == null) return false;
            for (int i = 0; i < snapshots.Count; i++)
            {
                BlacksiteObjectiveSnapshot candidate = snapshots[i];
                if (candidate.ObjectId != objectId) continue;
                record = candidate;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The non-Loot record in play, which is the subject of the global capacity-of-one gate at
        /// <c>BlacksiteObjectiveManifestController.cs:531</c> - an unnamed resource with no expiry,
        /// no holder identity and no waiting list until S2 names it <c>ObjectiveBoard</c>. A claimed
        /// record wins over an unclaimed one, so the answer is the record the board is closed FOR.
        /// </summary>
        internal bool TryGetPrimaryObjective(out BlacksiteObjectiveSnapshot record)
        {
            record = default;
            var snapshots = _mission != null ? _mission.ObjectiveSnapshots : null;
            if (snapshots == null) return false;
            bool found = false;
            for (int i = 0; i < snapshots.Count; i++)
            {
                BlacksiteObjectiveSnapshot candidate = snapshots[i];
                if (candidate.Kind == BlacksiteObjectiveKind.Loot) continue;
                bool claimed = candidate.Phase == BlacksiteObjectivePhase.Reserved
                    || candidate.Phase == BlacksiteObjectivePhase.Carried;
                if (claimed) { record = candidate; return true; }
                if (found) continue;
                record = candidate;
                found = true;
            }
            return found;
        }

        private BlacksiteSquadRoutePlanner Planner => _mission != null ? _mission.RoutePlanner : null;

        /// <summary>The committed route, lead's tile first. Empty means no plan.</summary>
        internal IReadOnlyList<int> CommittedRoute
        {
            get
            {
                BlacksiteSquadRoutePlanner planner = Planner;
                return planner != null ? planner.CommittedRoute : System.Array.Empty<int>();
            }
        }

        /// <summary>The tile the lead was last observed in, or -1 before the first observation.</summary>
        internal int CurrentTile
        {
            get
            {
                BlacksiteSquadRoutePlanner planner = Planner;
                return planner != null ? planner.CurrentTile : -1;
            }
        }

        internal Vector3 LeadWaypoint
        {
            get
            {
                BlacksiteSquadRoutePlanner planner = Planner;
                return planner != null ? planner.LeadWaypoint : Vector3.zero;
            }
        }

        /// <summary>
        /// False when the planner has no committed route at all, in which case
        /// <see cref="LeadWaypoint"/> is the lead's own position and means "stay put", not "go here".
        /// </summary>
        internal bool HasLeadWaypoint
        {
            get
            {
                BlacksiteSquadRoutePlanner planner = Planner;
                return planner != null && planner.CommittedRoute.Count > 0;
            }
        }

        /// <summary>Increments on every plan and replan, so a consumer can gate on it.</summary>
        internal int RouteVersion
        {
            get
            {
                BlacksiteSquadRoutePlanner planner = Planner;
                return planner != null ? planner.RouteVersion : 0;
            }
        }

        internal bool IsTileExplored(int tile)
        {
            BlacksiteSquadRoutePlanner planner = Planner;
            return planner != null && planner.IsTileExplored(tile);
        }

        /// <summary>How many tiles the squad has opened. <c>BlacksiteSquadRoutePlanner.ExploredCount</c>.</summary>
        internal int ExploredCount
        {
            get
            {
                BlacksiteSquadRoutePlanner planner = Planner;
                return planner != null ? planner.ExploredCount : 0;
            }
        }

        // #1293 R1: the threat facts (contacts, sightings, search points) are not folded here any
        // more; every reader asks the squad's one world model, BlacksiteSquadDirector.World.

        // #1016 S2 review batch 2, F15: TryGetCapturedAlly is deleted. It had no callers, and it was
        // a linear walk of RoundManager.SpawnedEnemies calling into the capture registry for each
        // one - a per-call cost on the read facade with nothing reading it. S3's Sense() adds the
        // rescue fact in the shape S3 actually needs it in, against the memory model it owns.

        // -----------------------------------------------------------------------------------
        // Diagnostics. Both tags are change-gated plus a five-second window, exactly like
        // [UAT][MotionRequest] (#944 S11 U6, #1015 S1).
        // -----------------------------------------------------------------------------------

        private const float BrokerRepeatSeconds = 5f;
        private const float WaitsIntervalSeconds = 1f;

        private readonly BlacksiteChangeGate _brokerGate = new BlacksiteChangeGate();
        private readonly BlacksiteWindowGate _brokerWindow = new BlacksiteWindowGate(BrokerRepeatSeconds);
        private readonly BlacksiteWindowGate _waitsWindow = new BlacksiteWindowGate(WaitsIntervalSeconds);
        private readonly List<BlacksiteSoldierAI> _memberScratch = new List<BlacksiteSoldierAI>();
        private readonly Dictionary<ulong, int> _memberIndices = new Dictionary<ulong, int>();
        /// <summary>The cached <see cref="Label"/> delegate, so the 1 Hz view allocates none (F17).</summary>
        private System.Func<ulong, string> _label;

        /// <summary>
        /// Drain the sweep's events into <c>[UAT][Broker]</c> lines and clear the list.
        ///
        /// Change-gated per <c>(member, resource, action)</c> with a five-second repeat window,
        /// exactly as <c>[UAT][MotionRequest]</c> is
        /// (<c>BlacksiteNavigator.cs:246-248</c>): the 2026-09-12 run's 209 <c>floorChoice</c> and
        /// 888 <c>[Alert]</c> lines are why every new tag in this tree is change-gated by default.
        /// Both gates are evaluated on every report and neither is short-circuited - the change gate
        /// prints the transition and re-arms, the window gate keeps a standing state visible at
        /// 0.2 Hz instead of at the tick rate.
        ///
        /// The list is drained whether or not diagnostics are enabled, so a disabled run cannot grow
        /// it without bound.
        /// </summary>
        internal void ReportBrokerEvents()
        {
            IReadOnlyList<BlacksiteBrokerEvent> events = _broker.Events;
            if (events.Count == 0) return;
            if (!BlacksiteDiagnostics.Enabled) { _broker.ClearEvents(); return; }

            RefreshMemberIndices();
            for (int i = 0; i < events.Count; i++)
            {
                BlacksiteBrokerEvent entry = events[i];
                string resource = entry.Resource.Token;
                string action = BlacksiteResourceLeasePolicy.Token(entry.Action);
                int key = unchecked((int)entry.Member);
                bool changed = _brokerGate.ShouldReport(key, resource, action, out _);
                bool due = _brokerWindow.ShouldReport(key, Time.time, out _);
                if (!changed && !due) continue;
                // Design note E.1: member, resource, action, place, waiting, capacity, holder,
                // heldFor, expiresIn, waitFor, reason - in that order and no other. #1293 R4:
                // waitExpiresIn= is gone with the wait bound it printed.
                BlacksiteDiagnostics.Log("Broker",
                    $"member={Label(entry.Member)}, resource={resource}, action={action}"
                    + $", place={entry.Place}, waiting={entry.Waiting}, capacity={entry.Capacity}"
                    + $", holder={Label(entry.Holder)}, heldFor={entry.HeldFor:0.00}, expiresIn={entry.ExpiresIn:0.00}"
                    + $", waitFor={entry.WaitedFor:0.00}"
                    + $", reason={(string.IsNullOrWhiteSpace(entry.Reason) ? BlacksiteDiagnosticFormat.None : entry.Reason)}");
            }
            _broker.ClearEvents();
        }

        /// <summary>
        /// Design note C.3 and E.2: one <c>[UAT][BrokerWaits]</c> line per second while anything is
        /// outstanding, silent otherwise. It is the answer to the diagnosis's central complaint -
        /// that no line anywhere could say "the objective board is held by member 0, who has held it
        /// for 266 s, and members 1, 2 and 3 are behind it." That sentence is now one grep.
        /// </summary>
        internal void ReportWaits(float now)
        {
            if (!BlacksiteDiagnostics.Enabled) return;
            // #1016 S2 review batch 2, F12: the 1 Hz gate FIRST. OutstandingLeases and
            // OutstandingWaiters each walk every lane, so testing them ahead of the gate ran two
            // full lane scans per server tick to decide whether to skip a line 59 times out of 60.
            // The gate is a dictionary lookup and a compare; the scans belong behind it.
            if (!_waitsWindow.ShouldReport(0, now, out _)) return;
            if (_broker.OutstandingLeases == 0 && _broker.OutstandingWaiters == 0) return;
            // #1016 S2 review batch 2, F17: detail= prints the same member labels [UAT][Broker]
            // does, so the documented "holder=0 ... waiting=1,2,3" reads as squad ordinals in both
            // tags rather than as raw NetworkObjectIds in one of them.
            RefreshMemberIndices();
            string body = _broker.DescribeWaits(now, _label ?? (_label = Label));
            if (string.IsNullOrEmpty(body)) return;
            BlacksiteDiagnostics.Log("BrokerWaits", body);
        }

        internal void ResetDiagnostics()
        {
            _brokerGate.Reset();
            _brokerWindow.Reset();
            _waitsWindow.Reset();
            _memberIndices.Clear();
            _memberScratch.Clear();
        }

        /// <summary>
        /// The broker keys on the member's NetworkObjectId, which is the token every existing queue
        /// already uses; every <c>[UAT]</c> line in this tree prints <c>member=</c> as the squad
        /// ordinal. This maps the one to the other so the new lines join the old ones on the same
        /// value, and falls back to the raw token for anything the roster does not know.
        /// </summary>
        private void RefreshMemberIndices()
        {
            _memberIndices.Clear();
            BlacksiteSquadDirector director = _mission != null ? _mission.SquadDirector : null;
            if (director == null) return;
            _memberScratch.Clear();
            director.CollectLivingMembers(_memberScratch);
            for (int i = 0; i < _memberScratch.Count; i++)
            {
                BlacksiteSoldierAI soldier = _memberScratch[i];
                if (soldier == null || soldier.NetworkObject == null || !soldier.NetworkObject.IsSpawned) continue;
                _memberIndices[soldier.NetworkObject.NetworkObjectId] = soldier.MemberIndex;
            }
            _memberScratch.Clear();
        }

        private string Label(ulong token)
        {
            if (token == 0UL) return BlacksiteDiagnosticFormat.None;
            return _memberIndices.TryGetValue(token, out int index)
                ? index.ToString()
                : token.ToString();
        }
    }
}
