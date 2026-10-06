using System;
using System.Collections.Generic;
using System.Text;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1016 S2: one per mission, host-side. The nine claim stores listed in design note A.4 get one
    /// lifetime model, one expiry sweep and one view.
    ///
    /// The exit criteria of the stage are three sentences, and each of them is a member on this
    /// type:
    /// <list type="bullet">
    /// <item><b>No claim outlives its holder.</b> <see cref="ReleaseAllHeldBy"/> is the primitive.
    /// It replaces five partial implementations -
    /// <c>BlacksiteSquadDirector.ReleaseInteractionsOwnedBy:1635</c> (which misses the ladder, the
    /// passage and the objective), <c>…Duties.cs:944</c> (route columns only), <c>Release</c> on
    /// each of the three queues, <c>RemoveParticipant</c> on the elevator session, and nothing at
    /// all for the objective claim.</item>
    /// <item><b>Every claim ends on an event.</b> <see cref="Tick"/> is the one sweep (the stale
    /// heartbeat), and every grant, refusal, release and expiry it or a lane produces lands in
    /// <see cref="Events"/> for the Unity side to print as <c>[UAT][Broker]</c>. #1293 R2: a holder
    /// that stops progressing is the progress ledger's to answer, through
    /// <see cref="ReleaseAllHeldBy"/>.</item>
    /// <item><b>The elevator is a broker decision.</b> #1293 R4: the mineshaft car is one
    /// capacity-1 <c>Elevator</c> lane per car: its holder is the car's one commander, its waiters
    /// the members waiting to command it (#1305 G3: riders board a parked-open car without a
    /// lease), and it is forgotten with the mission.</item>
    /// </list>
    ///
    /// Mission-scoped rather than round-scoped, per design note G.9: a round-scoped static outlives
    /// the mission that wrote it. <c>BlacksiteElevatorInteraction.ResetRoundSessions</c> - the
    /// interaction coordinator's round reset - forwards to <see cref="ClearAll"/>.
    ///
    /// Unity-free and logger-free on purpose. It produces events; it does not know what a log line
    /// looks like. <c>BlacksiteSquadBlackboard</c> drains them.
    /// </summary>
    internal sealed class BlacksiteResourceBroker
    {
        private readonly Dictionary<BlacksiteResourceKey, BlacksiteResourceLane> _lanes =
            new Dictionary<BlacksiteResourceKey, BlacksiteResourceLane>();

        /// <summary>
        /// Lanes a facade object registered through <see cref="Lane"/>. The sweep never removes one
        /// of these from the registry - see <see cref="Tick"/> for why that matters.
        /// </summary>
        private readonly HashSet<BlacksiteResourceKey> _registered = new HashSet<BlacksiteResourceKey>();

        /// <summary>
        /// #1016 S2 review batch 1, item 9: the cap on the undrained event list. The blackboard
        /// drains it every server tick, so this is only ever reached by a run with diagnostics
        /// disabled and no drain at all, or by a storm; either way an unbounded list on a
        /// host-side type is a leak, and dropping with a counter is honest where silence is not.
        /// </summary>
        internal const int EventCap = 256;

        /// <summary>One reusable list; the caller drains it and calls <see cref="ClearEvents"/>.</summary>
        private readonly List<BlacksiteBrokerEvent> _events = new List<BlacksiteBrokerEvent>(EventCap);

        private readonly Action _onEventDropped;
        private int _droppedEvents;

        private readonly List<BlacksiteResourceKey> _laneScratch = new List<BlacksiteResourceKey>();
        private readonly List<BlacksiteResourceKey> _forgetScratch = new List<BlacksiteResourceKey>();
        private readonly List<ulong> _clearedScratch = new List<ulong>();
        private readonly List<float> _clearedAtScratch = new List<float>();
        private readonly StringBuilder _describe = new StringBuilder();

        /// <summary>
        /// #1016 S2 step 3 (amendment E): every live broker, so a STATIC round reset can reach
        /// mission-scoped state.
        ///
        /// <c>BlacksiteElevatorInteraction.ResetRoundSessions</c> is the coordinator's round reset,
        /// and it is static because the coordinator has no mission in hand at that point. The broker
        /// is mission-scoped by design (note G.9). A registry of live brokers is how the static reset
        /// reaches the per-mission ledgers without either of them having to know about the other.
        ///
        /// #1016 S2 review batch 2, F3(c): an entry is added on construction and dropped by
        /// <see cref="Retire"/>, which is the ONE place a broker leaves the registry, and is called
        /// when the mission that owns it despawns. <see cref="ClearAll"/> empties every live
        /// ledger's state and leaves the registry populated: a round reset is not a mission end,
        /// and a broker whose mission is still alive must still be reachable by the next one. A
        /// mission whose controller was destroyed without despawning costs one dead reference,
        /// which is cleared with the round.
        /// </summary>
        private static readonly List<BlacksiteResourceBroker> Live = new List<BlacksiteResourceBroker>();

        internal BlacksiteResourceBroker()
        {
            _onEventDropped = () => _droppedEvents++;
            Live.Add(this);
        }

        /// <summary>
        /// Clear every live mission's ledger. <c>BlacksiteElevatorInteraction.ResetRoundSessions</c>
        /// forwards here.
        ///
        /// #1016 S2 review batch 2, F3(c): it does NOT empty <see cref="Live"/>. Emptying it made a
        /// round reset unregister brokers whose missions were still running, so the next reset
        /// reached none of them; <see cref="Retire"/> is the only thing that leaves the registry,
        /// and it is driven by the mission's own despawn.
        /// </summary>
        internal static void ClearAll()
        {
            for (int i = 0; i < Live.Count; i++) Live[i].Clear();
        }

        /// <summary>Everything one sweep produced, in order. Read then <see cref="ClearEvents"/>.</summary>
        internal IReadOnlyList<BlacksiteBrokerEvent> Events => _events;

        /// <summary>How many events were dropped at <see cref="EventCap"/> since the last drain.</summary>
        internal int DroppedEvents => _droppedEvents;

        internal void ClearEvents()
        {
            _events.Clear();
            _droppedEvents = 0;
        }

        internal int LaneCount => _lanes.Count;

        internal int OutstandingLeases
        {
            get
            {
                int total = 0;
                foreach (var pair in _lanes) total += pair.Value.Occupants;
                return total;
            }
        }

        internal int OutstandingWaiters
        {
            get
            {
                int total = 0;
                foreach (var pair in _lanes) total += pair.Value.Waiting;
                return total;
            }
        }

        // -----------------------------------------------------------------------------------
        // Registration
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Get or create the lane for this key. This is the entry point a facade uses at the six
        /// production construction sites, and calling it marks the lane REGISTERED: the sweep will
        /// clear its state when it goes idle but will never remove the object from the registry, so
        /// a facade that holds a reference to it cannot be handed a lane the broker has forgotten.
        ///
        /// A lane created implicitly by <see cref="Request"/> has no facade behind it and IS removed
        /// when idle. That is design note G.6 in one rule: <c>_routeColumns</c> is never pruned when
        /// idle today, and the only observable difference after the sweep prunes it is that a
        /// doorway revisited after a long gap starts a fresh column - <c>place=0</c> instead of an
        /// inherited place - which is what the interaction columns already do.
        ///
        /// The capacity is a parameter rather than a policy lookup because one kind has a capacity
        /// that is geometry: the ladder's <c>MaxOccupants</c>, which depends on the measured rise.
        /// #1293 R4: there is no held-ceiling or wait-bound parameter; a lane has neither.
        /// </summary>
        internal BlacksiteResourceLane Lane(
            in BlacksiteResourceKey key,
            int capacity,
            float headway,
            Func<ulong, float, string> rule = null)
        {
            if (!key.IsValid) return null;
            _registered.Add(key);
            if (_lanes.TryGetValue(key, out BlacksiteResourceLane existing)) return existing;
            var lane = new BlacksiteResourceLane(key, capacity, headway, rule);
            lane.AttachEvents(_events, EventCap, _onEventDropped);
            _lanes[key] = lane;
            return lane;
        }

        /// <summary>
        /// #1016 S2 review batch 2, F3(a): is this the lane THIS broker currently holds for its own
        /// key? A facade tests it before deciding that its binding is still good, because a lane
        /// object can be the right shape and still be an orphan - one the broker dropped when the
        /// mission ended - and an orphan is a lane nobody sweeps and nobody prints.
        /// </summary>
        internal bool Holds(BlacksiteResourceLane lane)
            => lane != null
                && _lanes.TryGetValue(lane.Key, out BlacksiteResourceLane current)
                && ReferenceEquals(current, lane);

        /// <summary>
        /// The lane for this key, created from <see cref="BlacksiteResourceLeasePolicy"/>'s static
        /// table when nobody has registered a geometry-aware one. Not registered: a lane nobody
        /// owns is forgotten when it goes idle.
        /// </summary>
        private BlacksiteResourceLane Implicit(in BlacksiteResourceKey key)
        {
            if (!key.IsValid) return null;
            if (_lanes.TryGetValue(key, out BlacksiteResourceLane existing)) return existing;
            var lane = new BlacksiteResourceLane(
                key,
                BlacksiteResourceLeasePolicy.Capacity(key.Kind),
                BlacksiteResourceLeasePolicy.HeadwaySeconds(key.Kind));
            lane.AttachEvents(_events, EventCap, _onEventDropped);
            _lanes[key] = lane;
            return lane;
        }

        internal bool TryGetLane(in BlacksiteResourceKey key, out BlacksiteResourceLane lane)
            => _lanes.TryGetValue(key, out lane);

        /// <summary>
        /// Forget this resource's state.
        ///
        /// #1016 S2 review batch 1, item 8: a REGISTERED lane keeps its object. A facade holds that
        /// object for the life of the thing it models - a ship controller's <c>Ladder</c> field, a
        /// ladder's <c>Queue</c> - so removing it from the registry would leave the facade writing
        /// into a lane the broker no longer sweeps, which is a silent leak rather than a forget.
        /// Its state is cleared, which is all "forget" ever meant. An UNREGISTERED lane has no
        /// facade behind it and is removed outright.
        ///
        /// #1016 S2 review batch 2, F4: <paramref name="dropRegistered"/> is the one case where a
        /// registered lane DOES go - the facade holding it is being discarded in the same
        /// statement, which is what <c>BlacksitePassageQueue.Unbind</c> and
        /// <c>BlacksiteEntranceColumn.Unbind</c> do when an idle column is pruned out of the
        /// director's or the manifest's dictionary. Without it a squad that used a dozen doorways
        /// accumulated a dozen permanently registered lanes that <see cref="Tick"/> is explicitly
        /// forbidden from removing.
        /// </summary>
        internal void Forget(in BlacksiteResourceKey key, bool dropRegistered = false)
        {
            if (!_lanes.TryGetValue(key, out BlacksiteResourceLane lane)) return;
            lane.Clear();
            if (dropRegistered) _registered.Remove(key);
            else if (_registered.Contains(key)) return;
            _lanes.Remove(key);
        }

        // -----------------------------------------------------------------------------------
        // The claim surface
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Ask for one resource. The verdict carries everything the caller needs to print its own
        /// refusal - place, waiting, capacity and how long it has waited - so nobody has to ask the
        /// lane a second question to explain itself.
        /// </summary>
        internal BlacksiteBrokerVerdict Request(
            in BlacksiteResourceKey key,
            ulong member,
            float now,
            string reason)
        {
            BlacksiteResourceLane lane = Implicit(key);
            if (lane == null)
                return new BlacksiteBrokerVerdict(
                    BlacksiteWaitOutcome.Refused, default, -1, 0, 0, 0f, "no such resource");

            // #1016 S2 review batch 1, item 1: nothing is indexed here. The facades hold the lane
            // object and call lane.Request/Advance/Release directly, so a side index written only
            // on this path would be empty in production and the C.6 tie would be unprovable. The
            // lanes ARE the index; TryGetWaitKey scans them.
            return lane.Request(member, now, reason);
        }

        /// <summary>
        /// #1016 S2 review batch 2, F5: take a place in the order and NOTHING else.
        ///
        /// This is <see cref="BlacksiteResourceLane.Enrol"/> reached by key, for the call sites that
        /// must record who is behind the holder without ever being able to become the holder. The
        /// objective board is the one that matters: only the CARRIER requests it, and every other
        /// free member is enrolled, because a non-carrier that reached the head of that lane while
        /// the board's own lease happened to be between holders would otherwise be GRANTED a board
        /// it has no record to carry.
        /// </summary>
        internal int Enrol(in BlacksiteResourceKey key, ulong member, float now)
        {
            BlacksiteResourceLane lane = Implicit(key);
            return lane == null ? -1 : lane.Enrol(member, now);
        }

        /// <summary>The member cleared the threshold; the next may start after the headway.</summary>
        internal void Advance(in BlacksiteResourceKey key, ulong member, float now)
        {
            if (_lanes.TryGetValue(key, out BlacksiteResourceLane lane)) lane.Advance(member, now);
        }

        internal void Release(in BlacksiteResourceKey key, ulong member)
        {
            if (_lanes.TryGetValue(key, out BlacksiteResourceLane lane)) lane.Release(member);
        }

        /// <summary>
        /// Design note C.1 mechanism 2: death, boarding, capture and re-tasking are ONE call. It is
        /// called from the one place a member leaves the roster, and after it no lane anywhere can
        /// still be holding a claim for this member. #1293 R2: it is also the first step of the
        /// ledger's stall answer (<c>BlacksiteSquadPlanner.AnswerStalled</c>), so a holder that stops
        /// progressing hands every slot it holds to the waiter behind it.
        /// </summary>
        internal void ReleaseAllHeldBy(ulong member)
        {
            foreach (var pair in _lanes) pair.Value.Release(member);
        }

        internal bool TryGetHolder(in BlacksiteResourceKey key, out BlacksiteResourceLease lease)
        {
            lease = default;
            return _lanes.TryGetValue(key, out BlacksiteResourceLane lane) && lane.TryGetHolder(out lease);
        }

        /// <summary>
        /// What this member is waiting on, found by scanning the lanes.
        ///
        /// #1016 S2 review batch 1, item 1: the scan is the point. A side index would only ever see
        /// the waits that came through <see cref="Request"/>, and every production wait comes
        /// through a facade holding the lane object instead.
        /// A mission holds a dozen lanes at most and <see cref="DescribeWaits"/> already walks all
        /// of them once a second, so the scan costs nothing worth indexing away.
        ///
        /// The note's four-argument form is gone: its two durations could never be measured without
        /// a clock, and dead out-parameters get read. #1293 R3: so is the form with a clock - its
        /// wait bound was always 0 after R4 and no caller read its wait length.
        /// Strictly read-only.
        /// </summary>
        internal bool TryGetWaitKey(ulong member, out BlacksiteResourceKey key)
        {
            key = default;
            foreach (var pair in _lanes)
            {
                if (!pair.Value.IsWaiting(member)) continue;
                key = pair.Key;
                return true;
            }
            return false;
        }

        /// <summary>Is this member queued for anything at all?</summary>
        internal bool IsWaitingForAnything(ulong member) => TryGetWaitKey(member, out _);

        // -----------------------------------------------------------------------------------
        // The one sweep
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Expiry is one sweep (design note C.2). It replaces
        /// <c>BlacksiteSquadDirector.CleanupClaims:1603</c>,
        /// the elevator's own session cleanup, <c>PassageQueue.Prune</c> (four
        /// instances), <c>EntranceColumn.Prune</c> (N instances), <c>WorldLadderColumn.Prune</c> (N
        /// instances) and the support anchor's inline expiry: eight prune implementations on
        /// seven cadences become one, run from the manifest's existing server tick BEFORE any duty
        /// is planned against a lease, so a plan never sees a lease that has already expired.
        ///
        /// Idle lanes are pruned here (G.6), and "pruned" means two different things by design.
        /// An UNREGISTERED lane - one nobody holds, created implicitly by <see cref="Request"/> -
        /// is REMOVED from the registry. A REGISTERED lane is LEFT IN PLACE: a facade holds the
        /// object for the life of the thing it models, so removing it would orphan the facade. It
        /// is already empty - that is what idle means - so a registered idle lane and a freshly
        /// created one are indistinguishable, and a doorway revisited after a long gap still starts
        /// at <c>place=0</c>. Nothing is cleared, because there is nothing left to clear.
        /// </summary>
        internal void Tick(float now)
        {
            _laneScratch.Clear();
            foreach (var pair in _lanes) _laneScratch.Add(pair.Key);

            _forgetScratch.Clear();
            for (int i = 0; i < _laneScratch.Count; i++)
            {
                if (!_lanes.TryGetValue(_laneScratch[i], out BlacksiteResourceLane lane)) continue;
                lane.Sweep(now);
                // Each call reports at most one expiry, so the loop is what makes several
                // simultaneous expiries all arrive rather than one per frame.
                while (lane.Tick(now, out _, out _)) { }
                if (lane.IsIdle(now) && !_registered.Contains(lane.Key)) _forgetScratch.Add(lane.Key);
            }

            for (int i = 0; i < _forgetScratch.Count; i++) _lanes.Remove(_forgetScratch[i]);
            _forgetScratch.Clear();
            _laneScratch.Clear();
        }

        /// <summary>
        /// Design note C.3: the single who-is-waiting-on-what view, as the body of one
        /// <c>[UAT][BrokerWaits]</c> line. Returns null when nothing is outstanding, so the emitter
        /// is silent rather than printing an empty line every second.
        ///
        /// Strictly read-only: it prunes nothing and admits nobody. That rule is copied from
        /// <c>BlacksitePassageQueue.cs:40-44</c>'s existing comment, which S2 keeps.
        ///
        /// This is the sentence the 2026-09-12 log could not produce: "the objective board is held
        /// by member 0, who has held it for 266 s, and members 1, 2 and 3 are behind it."
        ///
        /// #1016 S2 review batch 2, F17: <paramref name="label"/> is how a member token is printed.
        /// <c>[UAT][Broker]</c> maps every token through the squad ordinal, so a detail= segment
        /// printing raw <c>NetworkObjectId</c>s could not produce the documented
        /// <c>holder=0 … waiting=1,2,3</c> example and the two tags disagreed about who member 0
        /// was. The mapping is passed IN rather than looked up, so the broker stays Unity-free; null
        /// prints the raw token, which is what an unbound fixture wants.
        /// </summary>
        internal string DescribeWaits(float now, Func<ulong, string> label = null)
        {
            int resources = 0, leases = 0, waiters = 0;
            float longestWait = 0f, longestHold = 0f;
            _describe.Length = 0;

            foreach (var pair in _lanes)
            {
                BlacksiteResourceLane lane = pair.Value;
                int occupants = lane.Occupants, waiting = lane.Waiting, cleared = lane.Cleared;
                if (occupants == 0 && waiting == 0 && cleared == 0) continue;

                resources++;
                leases += occupants;
                waiters += waiting;

                float laneWait = lane.LongestWaitSeconds(now);
                float laneHold = lane.LongestHoldSeconds(now);
                if (laneWait > longestWait) longestWait = laneWait;
                if (laneHold > longestHold) longestHold = laneHold;

                if (_describe.Length > 0) _describe.Append(" | ");
                _describe.Append(lane.Key.Token);
                if (lane.TryGetHolder(out BlacksiteResourceLease lease))
                    _describe.Append(" holder=").Append(Name(label, lease.Holder))
                        .Append(" heldFor=").Append(lease.HeldFor(now).ToString("0.00"))
                        .Append(" expiresIn=").Append(lease.ExpiresIn(now).ToString("0.00"));
                else
                    _describe.Append(" holder=none heldFor=0.00 expiresIn=0.00");

                _describe.Append(" waiting=");
                if (waiting == 0) _describe.Append("none");
                else
                    for (int i = 0; i < waiting; i++)
                    {
                        if (i > 0) _describe.Append(',');
                        _describe.Append(Name(label, lane.WaiterAt(i)));
                    }
                _describe.Append(" longestWait=").Append(laneWait.ToString("0.00"));

                // #1016 S2 review batch 1, item 3: a member that has cleared the threshold is still
                // this resource's business - it is walking on through it and the ClearedSeconds
                // window is what will let the lane go idle - so the one view has to see it too.
                if (cleared == 0) continue;
                _clearedScratch.Clear();
                _clearedAtScratch.Clear();
                lane.CollectCleared(_clearedScratch, _clearedAtScratch);
                for (int i = 0; i < _clearedScratch.Count; i++)
                    _describe.Append(" | ").Append(lane.Key.Token)
                        .Append(" holder=").Append(Name(label, _clearedScratch[i]))
                        .Append(" heldFor=").Append((now - _clearedAtScratch[i]).ToString("0.00"))
                        .Append(" (cleared)");
                _clearedScratch.Clear();
                _clearedAtScratch.Clear();
            }

            if (resources == 0) return null;

            // #1016 S2 review batch 2, F17: FIVE fixed fields - resources, leases, waiters,
            // longestWait, longestHold - come first, and detail= is deliberately last, so a parser
            // keys on the fixed head and never has to escape the free text. Same convention as
            // [UAT][Landing]'s reason=.
            string detail = _describe.ToString();
            _describe.Length = 0;
            return "resources=" + resources
                + ", leases=" + leases
                + ", waiters=" + waiters
                + ", longestWait=" + longestWait.ToString("0.00")
                + ", longestHold=" + longestHold.ToString("0.00")
                + ", detail=" + detail;
        }

        /// <summary>
        /// Every claim, wait and tie this ledger holds goes; the REGISTERED lane objects stay.
        /// <c>ResetRoundSessions</c> keeps its name and forwards to <see cref="ClearAll"/>, so
        /// <c>BlacksiteInteractionCoordinator.EnsureRoundRegistry</c>'s call site is unchanged.
        ///
        /// #1016 S2 review batch 2, F3(b): keeping the registered objects is the whole fix. A facade
        /// binds ONCE, at the construction site of the thing it models, and then holds that lane
        /// object for the life of that thing. Emptying <c>_lanes</c> here left every one of those
        /// facades writing into a lane the broker no longer knew about - the sweep never reached it,
        /// <c>[UAT][BrokerWaits]</c> never printed it, and <c>ReleaseAllHeldBy</c> could not clear
        /// it - which is the same silent leak <see cref="Forget"/> already refuses to create. The
        /// lanes are cleared, which is all "the mission is over" ever meant; an UNREGISTERED lane
        /// has no facade behind it and is removed outright, exactly as <see cref="Tick"/> removes
        /// it when it goes idle.
        /// </summary>
        internal void Clear()
        {
            _laneScratch.Clear();
            foreach (var pair in _lanes)
            {
                pair.Value.Clear();
                if (!_registered.Contains(pair.Key)) _laneScratch.Add(pair.Key);
            }
            for (int i = 0; i < _laneScratch.Count; i++) _lanes.Remove(_laneScratch[i]);
            _laneScratch.Clear();
            _events.Clear();
            _droppedEvents = 0;
            _forgetScratch.Clear();
            _clearedScratch.Clear();
            _clearedAtScratch.Clear();
            _describe.Length = 0;
        }

        /// <summary>
        /// #1016 S2 review batch 2, F3(c): the mission that owns this ledger is gone.
        ///
        /// <see cref="Clear"/> is a state reset a live mission survives; this is the end of the
        /// broker itself, so it also leaves the static <see cref="Live"/> registry. Called from the
        /// mission controller's despawn, which is the one place a mission ends. Splitting the two
        /// is what lets <see cref="ClearAll"/> - a ROUND reset, which missions outlive - stop
        /// unregistering brokers that are still in use.
        /// </summary>
        internal void Retire()
        {
            Clear();
            // Registered lanes survive Clear by design; a retired broker keeps none of them.
            _lanes.Clear();
            _registered.Clear();
            Live.Remove(this);
        }

        /// <summary>The printed name of one member token, through the caller's own mapping (F17).</summary>
        private static string Name(Func<ulong, string> label, ulong token)
        {
            if (label == null) return token.ToString();
            string named = label(token);
            return string.IsNullOrEmpty(named) ? token.ToString() : named;
        }
    }
}
