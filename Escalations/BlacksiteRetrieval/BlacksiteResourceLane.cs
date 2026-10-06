using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1016 S2: ONE implementation of "a FIFO with a headway, a capacity and a stale window",
    /// replacing two.
    ///
    /// <c>BlacksitePassageQueue</c> (104 lines) and <c>BlacksiteEntranceColumn</c> (240 lines) each
    /// carry their own ordered list, their own <c>IndexOf</c>, their own <c>Prune</c> and their own
    /// copy of the same <c>private const float StaleSeconds = 2f</c>. Every behaviour they pin is
    /// reproduced here, named against the fixture line that pins it.
    ///
    /// #1293 R4: a lane times only presence (the stale heartbeat) and spacing (the headway). The
    /// held ceiling (<c>claim ceiling</c>), the paused-holder freeze and the wait bound
    /// (<c>wait expired after</c>) are deleted, and the
    /// constructor has no parameter for any of them: a holder leaves on its own event, the stale
    /// heartbeat or the progress ledger's answer, and a waiter moves when the holder in front does.
    ///
    /// <list type="bullet">
    /// <item><b>Headway.</b> Nobody is admitted inside <see cref="HeadwaySeconds"/> of the last
    /// clear, with the same one-millisecond slack - <c>BlacksitePassageQueue.cs:63</c>, pinned at
    /// <c>tests/BlacksiteTierTwoThreeRegression/Program.cs:506-509</c>.</item>
    /// <item><b>Stale prune.</b> A holder or waiter that has not re-asserted for
    /// <c>BlacksiteResourceLeasePolicy.StaleSeconds</c> is forgotten - it judges presence, not
    /// progress - and a vanished holder frees the resource with no headway penalty -
    /// <c>BlacksitePassageQueue.Prune:91-102</c>, pinned at
    /// <c>…/TierTwoThreeRegression/Program.cs:512</c>.</item>
    /// <item><b>Serve.</b> The member holding the reservation leads the column it is about to walk
    /// through - <c>BlacksiteEntranceColumn.Serve:169-187</c>.</item>
    /// <item><b>Cleared members.</b> A member that has ADVANCED keeps walking outside the queue for
    /// <c>ClearedSeconds</c> from the moment it cleared - <c>BlacksitePassageQueue.cs:57</c>, pinned
    /// at <c>…/TierTwoThreeRegression/Program.cs:506</c>.</item>
    /// </list>
    ///
    /// <b>#1016 S2 review batch 1, decision 5:</b> <c>BlacksiteWorldLadderColumn</c> is NOT one of
    /// the types this replaces. Its two refusal strings ("column full at N of M spans", the lead
    /// climber's distance) and its geometry members (<c>Report</c>, <c>SpaceFree</c>,
    /// <c>CanRetreat</c>, <c>TryLead</c>, <c>TryLeadAhead</c>, <c>SpanOccupants</c>) need a
    /// per-occupant up/height the lane does not store, and the lane's capacity-before-rule ordering
    /// cannot produce them without a duplicate occupant list. Only <c>BlacksiteWorldLadder.Queue</c>
    /// - a plain <c>BlacksitePassageQueue()</c>, the 0.6 s default headway - goes behind a <c>WorldLadder</c> lane.
    ///
    /// Unity-free, so the headless regressions drive it directly.
    /// </summary>
    internal sealed class BlacksiteResourceLane
    {
        private struct Holder
        {
            internal ulong Member;
            internal int LeaseId;
            internal string Reason;
            internal float GrantedAt;
            /// <summary>The last re-assert of any kind, for the stale window. <c>_seen</c>.</summary>
            internal float SeenAt;
        }

        private struct Waiter
        {
            internal ulong Member;
            /// <summary>When this member first joined the order. Survives a re-assert: <c>waitFor=</c> is the whole wait.</summary>
            internal float Since;
            internal float SeenAt;
        }

        /// <summary>
        /// #1016 S2 review batch 1, item 9: the queue-position refusal, precomputed. A five-member
        /// standing queue at 60 Hz produced 19 KB/s of garbage from this string alone. The ceiling
        /// is <c>BlacksiteEntranceColumn.ColumnCeiling</c> doubled, which is more places than a
        /// six-member squad can occupy; anything deeper shares the last string.
        /// </summary>
        private static readonly string[] WaitingBehind =
        {
            "waiting behind the head",
            "waiting behind 1 member(s)",
            "waiting behind 2 member(s)",
            "waiting behind 3 member(s)",
            "waiting behind 4 member(s)",
            "waiting behind 5 member(s)",
            "waiting behind 6 member(s)",
            "waiting behind 7 member(s)"
        };

        private const string DeepInTheColumn = "waiting deep in the column";

        private readonly BlacksiteResourceKey _key;
        private readonly int _capacity;
        private readonly float _headwaySeconds;
        private readonly Func<ulong, float, string> _admissionRule;

        // Precomputed refusal text (item 9). None of these vary at run time: a lane's capacity and
        // headway are fixed at construction, and _holders.Count equals _capacity exactly when the
        // capacity refusal fires.
        private readonly string _capacityRefusal;
        private readonly string _headwayRefusal;

        private readonly List<Holder> _holders = new List<Holder>();
        private readonly List<Waiter> _waiting = new List<Waiter>();
        private readonly Dictionary<ulong, float> _cleared = new Dictionary<ulong, float>();
        private readonly List<ulong> _scratch = new List<ulong>();

        /// <summary>
        /// #1016 S2 review batch 1, item 9: the last (action, reason) recorded for each member, so a
        /// standing queue that re-asks every think records one event rather than one per think. The
        /// blackboard's change gate still applies on top; this keeps the list from growing between
        /// drains in the first place.
        /// </summary>
        private readonly Dictionary<ulong, LastRecord> _lastRecorded = new Dictionary<ulong, LastRecord>();

        private struct LastRecord
        {
            internal BlacksiteBrokerAction Action;
            internal string Reason;
        }

        private float _lastClearedAt = float.NegativeInfinity;
        private int _nextLeaseId = 1;

        /// <summary>
        /// #1016 S2 review batch 1, item 10: the last clock any entry point saw.
        /// <c>Release(member)</c> has no clock of its own - no facade signature does - and its event
        /// still has to carry a truthful <c>heldFor=</c>, so it measures against this. Updated by
        /// <see cref="Request"/>, <see cref="Advance"/>, <see cref="Serve"/>, <see cref="Sweep"/>
        /// and <see cref="Tick"/>, i.e. by everything that runs on a tick.
        /// </summary>
        private float _lastNow;

        /// <summary>
        /// The broker's one reusable event list, attached after construction so note B's
        /// constructor signature is exactly as designed and a fixture can still build a bare lane.
        /// </summary>
        private List<BlacksiteBrokerEvent> _events;
        private Action _onEventDropped;
        private int _eventCap = int.MaxValue;

        internal BlacksiteResourceLane(
            BlacksiteResourceKey key,
            int capacity,
            float headwaySeconds,
            Func<ulong, float, string> admissionRule = null)
        {
            _key = key;
            _capacity = capacity < 1 ? 1 : capacity;
            _headwaySeconds = headwaySeconds > 0f ? headwaySeconds : 0f;
            _admissionRule = admissionRule;
            _capacityRefusal = "at capacity " + _capacity;
            _headwayRefusal = "headway of " + _headwaySeconds.ToString("0.00") + "s not spent";
        }

        internal BlacksiteResourceKey Key => _key;
        internal int Capacity => _capacity;
        internal float HeadwaySeconds => _headwaySeconds;

        /// <summary>Bodies holding the resource right now, the one being served included.</summary>
        internal int Occupants => _holders.Count;

        /// <summary>Members asking for it that are not holding it.</summary>
        internal int Waiting => _waiting.Count;

        /// <summary>Members that cleared the threshold and are still walking on outside the order.</summary>
        internal int Cleared => _cleared.Count;

        /// <summary>
        /// What the lane last did, in the closed <c>action=</c> vocabulary. This is a convenience
        /// for a caller that has
        /// just made one call and wants a one-word answer; the authoritative record of WHAT
        /// happened - including several expiries in one sweep - is the event list, and a caller that
        /// needs the cause of an expiry reads that.
        /// </summary>
        internal BlacksiteBrokerAction LastAction { get; private set; }

        internal string LastActionReason { get; private set; }

        /// <summary>Attach the broker's shared event list, its cap and its overflow counter.</summary>
        internal void AttachEvents(List<BlacksiteBrokerEvent> sink, int cap, Action onDropped)
        {
            _events = sink;
            _eventCap = cap > 0 ? cap : int.MaxValue;
            _onEventDropped = onDropped;
        }

        // -----------------------------------------------------------------------------------
        // Requests
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// The one grant path. A caller already holding renews; spare capacity plus the headway
        /// grants; the admission rule's veto refuses outright; anything else queues the caller and
        /// hands back its place.
        ///
        /// A queued or refused caller gets a DEFAULT lease, so nothing it can do afterwards looks
        /// like holding.
        /// </summary>
        internal BlacksiteBrokerVerdict Request(ulong member, float now, string reason)
        {
            _lastNow = now;
            Sweep(now);

            // A member that has cleared the threshold keeps walking; it occupies nothing and is not
            // in the order. BlacksitePassageQueue.cs:57, which returns true without re-queuing.
            //
            // #1016 S2 review batch 1, item 3: the stamp is the FIRST clear, never re-stamped. The
            // original re-stamped on every request, so ClearedSeconds could not bound a member that
            // kept asking, and the lane never became idle.
            if (_cleared.ContainsKey(member))
                return new BlacksiteBrokerVerdict(
                    BlacksiteWaitOutcome.Granted, default, -1, _waiting.Count, _capacity, 0f, "cleared");

            // The geometry or direction veto is evaluated first and independently of queue position:
            // it is a property of this member against this resource, not of where it stands, so a
            // vetoed member is never put in the order.
            if (_admissionRule != null)
            {
                string veto = _admissionRule(member, now);
                if (veto != null)
                {
                    int standing = WaiterIndex(member);
                    if (standing >= 0) _waiting.RemoveAt(standing);
                    Record(now, member, BlacksiteBrokerAction.Refused, -1, 0f, veto);
                    return new BlacksiteBrokerVerdict(
                        BlacksiteWaitOutcome.Refused, default, -1, _waiting.Count, _capacity, 0f, veto);
                }
            }

            int held = HolderIndex(member);
            if (held >= 0)
                return Reassert(held, now, reason);

            int waiter = WaiterIndex(member);
            if (waiter < 0)
            {
                _waiting.Add(new Waiter { Member = member, Since = now, SeenAt = now });
                waiter = _waiting.Count - 1;
            }
            else
            {
                Waiter standing = _waiting[waiter];
                standing.SeenAt = now;
                _waiting[waiter] = standing;
            }

            string refusal = TryAdmit(waiter, now);
            return refusal == null ? Grant(waiter, member, now, reason) : Queue(member, waiter, now, refusal);
        }

        /// <summary>
        /// The member has cleared the threshold: it stops occupying the resource and the next may
        /// start after the headway. <c>BlacksitePassageQueue.Advance:73-77</c>, verbatim.
        ///
        /// #1016 S2 review batch 1, item 16: the passage queue advances <c>_waiting[0]</c> whether
        /// or not it was admitted, so a member at the FRONT of the order that is not yet a holder is
        /// cleared too.
        /// </summary>
        internal void Advance(ulong member, float now)
        {
            _lastNow = now;
            int held = HolderIndex(member);
            if (held >= 0)
            {
                _holders.RemoveAt(held);
            }
            else
            {
                if (_holders.Count > 0 || _waiting.Count == 0 || _waiting[0].Member != member) return;
                _waiting.RemoveAt(0);
            }
            if (!_cleared.ContainsKey(member)) _cleared[member] = now;
            _lastClearedAt = now;
            Record(now, member, BlacksiteBrokerAction.Advanced, -1, 0f, "cleared the threshold");
        }

        /// <summary>
        /// This member has stopped asking: it finished, failed, died or was re-tasked. Removal keeps
        /// the order of everyone behind it, so a casualty in the middle of a column does not
        /// reshuffle the members already walking to their places
        /// (<c>BlacksiteEntranceColumn.Leave:194-198</c>).
        ///
        /// A holder that releases hands the resource on WITHOUT a headway penalty - it never used
        /// the threshold - which is <c>BlacksitePassageQueue.Release:78-82</c> and is pinned as
        /// "a failed head hands the doorway on without a headway penalty"
        /// (<c>TierTwoThreeRegression/Program.cs:516</c>).
        /// </summary>
        internal void Release(ulong member)
        {
            int held = HolderIndex(member);
            if (held >= 0)
            {
                // #1016 S2 review batch 1, item 10: recorded BEFORE the removal, or holder= and
                // heldFor= would describe whoever is promoted next instead of the member that left.
                Record(_lastNow, member, BlacksiteBrokerAction.Released, -1, 0f, "released");
                _holders.RemoveAt(held);
                _lastClearedAt = float.NegativeInfinity;
            }
            int waiter = WaiterIndex(member);
            if (waiter >= 0) _waiting.RemoveAt(waiter);
            _cleared.Remove(member);
            _lastRecorded.Remove(member);
        }

        /// <summary>
        /// A preferred operator leads the column it is about to walk through.
        ///
        /// #1016 S2 review batch 1, item 4: the production call site is
        /// <c>BlacksiteSquadDirector.TryAcquireInteraction:1223</c>, which reaches
        /// <c>column?.Serve(ownerToken, Time.time)</c> only AFTER both refusal branches have
        /// returned - the preferred-operator deferral at <c>:1195-1202</c> and the
        /// foreign-reservation branch at <c>:1206-1220</c>. So <b>the facade serves only after the
        /// grant is decided</b>: Serve is never reached while another member holds the resource,
        /// and the member it names is the one about to hold it. This implementation is written to
        /// that contract - it promotes within the waiting order and within the holder order, but it
        /// never moves a waiter ahead of an existing holder, because the call site cannot ask it to
        /// and doing so would hand out a place number today's column never produces.
        /// </summary>
        internal void Serve(ulong member, float now)
        {
            _lastNow = now;
            Sweep(now);

            // #1016 S2 review batch 1, item 4(b): a cleared member is outside the order. Inserting
            // it as a waiter would make a phantom head that never advances and wedges the lane.
            if (_cleared.ContainsKey(member)) return;

            int held = HolderIndex(member);
            if (held >= 0)
            {
                Holder holder = _holders[held];
                holder.SeenAt = now;
                if (held == 0) { _holders[0] = holder; return; }
                _holders.RemoveAt(held);
                _holders.Insert(0, holder);
                return;
            }

            // Never ahead of a holder: see the contract above.
            if (_holders.Count >= _capacity) return;

            int waiter = WaiterIndex(member);
            if (waiter < 0)
            {
                _waiting.Insert(0, new Waiter { Member = member, Since = now, SeenAt = now });
                return;
            }
            Waiter standing = _waiting[waiter];
            standing.SeenAt = now;
            if (waiter == 0) { _waiting[0] = standing; return; }
            _waiting.RemoveAt(waiter);
            _waiting.Insert(0, standing);
        }

        /// <summary>
        /// This member's index in the single ordered set "holders first, then waiters" - exactly
        /// <c>BlacksiteEntranceColumn._order</c>'s index, so <c>BackMetres(place)</c> keeps working
        /// unchanged. 0 is the member being served; -1 is "not in this lane".
        ///
        /// #1016 S2 review batch 1, item 7: pure. It prunes nothing and admits nobody - a
        /// diagnostic or a geometry query must not be able to change who is admitted, the rule
        /// <c>BlacksitePassageQueue.cs:40-44</c> already states. Call <see cref="Sweep"/> first if
        /// a fresh answer matters.
        ///
        /// #1016 S2 review batch 2, F13: it takes no clock, because a pure read has nothing to do
        /// with one. <c>BlacksiteEntranceColumn.Place(member, now)</c> keeps its own two-argument
        /// signature - it sweeps and then enrols, and both of those need the clock.
        /// </summary>
        internal int Place(ulong member)
        {
            int held = HolderIndex(member);
            if (held >= 0) return held;
            int waiter = WaiterIndex(member);
            return waiter < 0 ? -1 : _holders.Count + waiter;
        }

        /// <summary>
        /// #1016 S2 step 3: register or refresh this member in the waiting order and return its
        /// place, granting NOTHING. This is <c>BlacksiteEntranceColumn.Place:153-166</c> exactly -
        /// a column orders bodies, it does not admit them - and it is what the entrance-column
        /// facade calls, so a member that will go on to be refused a reservation still keeps its
        /// place across the frames it spends walking to it.
        ///
        /// The distinction matters: <c>BlacksiteSquadDirector.TryAcquireInteraction:1186</c> calls
        /// <c>column.Place</c> for EVERY requester, before either refusal branch. Routing that
        /// through <see cref="Request"/> would hand the first caller a lease the reservation logic
        /// had not agreed to.
        ///
        /// <b>#1062 WP1: a place-stamp is NOT a re-assert.</b> This used to refresh a HOLDER's
        /// <c>SeenAt</c> as well, which made the 2 s stale window unreachable for the one member
        /// the sweep exists to catch. <c>BlacksiteSquadDirector.ColumnFor</c> and
        /// <c>LaneFor</c> resolve an <c>Entrance</c> to the SAME <c>Doorway</c> key, so the column
        /// and the reservation are one lane object: a holder that was refused a renewal before it
        /// ever reached <see cref="Request"/> still kept its own claim alive from its own refused
        /// poll, forever (2026-09-15 host log, <c>expiresIn=0.00</c> on every later tick). Only
        /// <see cref="Reassert"/> - i.e. a real <see cref="Request"/> by the holder - and
        /// <see cref="Serve"/>, which the director reaches only once the grant is decided, may
        /// move a holder's liveness stamp. A WAITER is still refreshed here: a waiter's liveness
        /// is exactly "it is still asking", and enrolling is asking.
        /// </summary>
        internal int Enrol(ulong member, float now)
        {
            _lastNow = now;
            int held = HolderIndex(member);
            if (held >= 0) return held;
            int waiter = WaiterIndex(member);
            if (waiter < 0)
            {
                _waiting.Add(new Waiter { Member = member, Since = now, SeenAt = now });
                return _holders.Count + _waiting.Count - 1;
            }
            Waiter standing = _waiting[waiter];
            standing.SeenAt = now;
            _waiting[waiter] = standing;
            return _holders.Count + waiter;
        }

        /// <summary>The member at the head of the lane, which is the one a refusal names.</summary>
        internal bool TryGetHolder(out BlacksiteResourceLease lease)
        {
            lease = default;
            if (_holders.Count == 0) return false;
            lease = LeaseOf(_holders[0]);
            return true;
        }

        internal bool TryGetHolder(ulong member, out BlacksiteResourceLease lease)
        {
            lease = default;
            int held = HolderIndex(member);
            if (held < 0) return false;
            lease = LeaseOf(_holders[held]);
            return true;
        }

        internal bool Holds(ulong member) => HolderIndex(member) >= 0;

        internal bool IsWaiting(ulong member) => WaiterIndex(member) >= 0;

        internal bool HasCleared(ulong member) => _cleared.ContainsKey(member);

        /// <summary>Seconds the oldest outstanding wait has stood; 0 when nobody is waiting.</summary>
        internal float LongestWaitSeconds(float now)
        {
            float longest = 0f;
            for (int i = 0; i < _waiting.Count; i++)
            {
                float waited = now - _waiting[i].Since;
                if (waited > longest) longest = waited;
            }
            return longest;
        }

        /// <summary>Seconds the oldest outstanding claim has stood; 0 when nobody holds it.</summary>
        internal float LongestHoldSeconds(float now)
        {
            float longest = 0f;
            for (int i = 0; i < _holders.Count; i++)
            {
                float held = now - _holders[i].GrantedAt;
                if (held > longest) longest = held;
            }
            return longest;
        }

        /// <summary>Read-only enumeration for the <c>[UAT][BrokerWaits]</c> view. Prunes nothing.</summary>
        internal ulong WaiterAt(int index) => index >= 0 && index < _waiting.Count ? _waiting[index].Member : 0UL;

        /// <summary>Read-only. When this member joined the order, or 0 when it is not in it.</summary>
        internal float WaitStartedAt(ulong member)
        {
            int waiter = WaiterIndex(member);
            return waiter < 0 ? 0f : _waiting[waiter].Since;
        }

        /// <summary>Read-only. Every member still walking on after a clear, with the time it cleared.</summary>
        internal void CollectCleared(List<ulong> destination, List<float> clearedAt)
        {
            foreach (var pair in _cleared)
            {
                destination.Add(pair.Key);
                clearedAt.Add(pair.Value);
            }
        }

        // -----------------------------------------------------------------------------------
        // The sweep
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// #1016 S2 review batch 1, item 7: the stale prune, as a real entry point.
        ///
        /// It was the private <c>Prune</c> that every read accidentally ran; now the reads are pure
        /// and the writes call this explicitly. A facade that needs a fresh answer to a read - and
        /// <c>BlacksitePassageQueue.HasOccupant:39</c> is one, it prunes before it answers - calls
        /// this first.
        /// </summary>
        internal void Sweep(float now)
        {
            _lastNow = now;
            for (int i = _holders.Count - 1; i >= 0; i--)
            {
                if (now - _holders[i].SeenAt <= BlacksiteResourceLeasePolicy.StaleSeconds) continue;
                Record(now, _holders[i].Member, BlacksiteBrokerAction.Expired, -1, 0f, "holder timeout");
                _holders.RemoveAt(i);
                // A holder that vanished never used the threshold, so the next member owes no
                // headway. BlacksitePassageQueue.Prune:96.
                _lastClearedAt = float.NegativeInfinity;
            }
            for (int i = _waiting.Count - 1; i >= 0; i--)
            {
                if (now - _waiting[i].SeenAt <= BlacksiteResourceLeasePolicy.StaleSeconds) continue;
                Record(now, _waiting[i].Member, BlacksiteBrokerAction.Expired, -1,
                    now - _waiting[i].Since, "waiter timeout");
                _waiting.RemoveAt(i);
            }
            PruneCleared(now);
        }

        /// <summary>
        /// One step of the stale sweep. Reports at most one expiry per call and returns false when
        /// there is nothing left to expire, so a caller drains the lane with
        /// <c>while (lane.Tick(now, out lease, out waiter)) { … }</c> and several simultaneous
        /// expiries are all reported. The cause of each one travels on the event list, which is what
        /// the <c>[UAT][Broker]</c> line is written from.
        ///
        /// <list type="number">
        /// <item>A holder that has not re-asserted for <c>StaleSeconds</c> is gone: <c>expired,
        /// holder timeout</c>, removed, no headway owed.</item>
        /// <item>A waiter that has not re-asserted: <c>expired, waiter timeout</c>.</item>
        /// </list>
        ///
        /// #1293 R4: those are the only two expiries. The held ceiling (<c>expired, claim
        /// ceiling</c>) and the wait bound (<c>evicted, wait expired after &lt;s&gt;s</c>) are deleted.
        /// </summary>
        internal bool Tick(float now, out BlacksiteResourceLease expired, out ulong expiredWaiter)
        {
            expired = default;
            expiredWaiter = 0UL;
            LastAction = BlacksiteBrokerAction.None;
            LastActionReason = null;
            _lastNow = now;

            PruneCleared(now);

            for (int i = 0; i < _holders.Count; i++)
            {
                Holder holder = _holders[i];
                if (now - holder.SeenAt <= BlacksiteResourceLeasePolicy.StaleSeconds) continue;

                expired = LeaseOf(holder);
                LastAction = BlacksiteBrokerAction.Expired;
                LastActionReason = "holder timeout";
                Record(now, holder.Member, BlacksiteBrokerAction.Expired, -1, 0f, LastActionReason);
                _holders.RemoveAt(i);
                _lastClearedAt = float.NegativeInfinity;
                return true;
            }

            for (int i = 0; i < _waiting.Count; i++)
            {
                Waiter waiter = _waiting[i];
                if (now - waiter.SeenAt <= BlacksiteResourceLeasePolicy.StaleSeconds) continue;

                expiredWaiter = waiter.Member;
                LastAction = BlacksiteBrokerAction.Expired;
                LastActionReason = "waiter timeout";
                Record(now, waiter.Member, BlacksiteBrokerAction.Expired, -1, now - waiter.Since, LastActionReason);
                _waiting.RemoveAt(i);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Nobody holding, nobody waiting and nothing still walking on: the broker may forget this
        /// lane's state. Mirrors <c>BlacksiteEntranceColumn.IsIdle:222-226</c>, which is what lets a
        /// doorway revisited after a long gap start a fresh column at place 0 rather than inherit
        /// one (design note G.6).
        ///
        /// #1016 S2 review batch 1, item 7: pure. <see cref="Tick"/> sweeps before it asks.
        /// </summary>
        internal bool IsIdle(float now)
            => _holders.Count == 0 && _waiting.Count == 0 && _cleared.Count == 0;

        internal void Clear()
        {
            _holders.Clear();
            _waiting.Clear();
            _cleared.Clear();
            _lastRecorded.Clear();
            _lastClearedAt = float.NegativeInfinity;
            LastAction = BlacksiteBrokerAction.None;
            LastActionReason = null;
        }

        // -----------------------------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------------------------

        private BlacksiteBrokerVerdict Reassert(int index, float now, string reason)
        {
            Holder holder = _holders[index];
            holder.SeenAt = now;
            _holders[index] = holder;

            BlacksiteResourceLease lease = LeaseOf(holder);
            Record(now, holder.Member, BlacksiteBrokerAction.Renewed, index, 0f, reason);
            return new BlacksiteBrokerVerdict(
                BlacksiteWaitOutcome.Renewed, lease, index, _waiting.Count, _capacity, 0f, reason);
        }

        /// <summary>
        /// May the member at <paramref name="waiter"/> be admitted now? Returns null to admit, or
        /// the refusal string the caller prints as <c>reason=</c>. The admission rule is NOT
        /// consulted here: a veto is a refusal, not a queue place, and <see cref="Request"/> settles
        /// it before anyone joins the order.
        /// </summary>
        private string TryAdmit(int waiter, float now)
        {
            // Only the head of the order may be admitted, whatever the capacity. That is what makes
            // the lane FIFO and what makes a capacity-of-one lane byte-identical to
            // BlacksitePassageQueue, whose only admissible member is _waiting[0].
            if (waiter != 0)
                return waiter < WaitingBehind.Length ? WaitingBehind[waiter] : DeepInTheColumn;

            if (_holders.Count >= _capacity)
                return _capacityRefusal;

            if (_headwaySeconds > 0f
                && now - _lastClearedAt < _headwaySeconds - BlacksiteResourceLeasePolicy.HeadwaySlack)
                return _headwayRefusal;

            return null;
        }

        private BlacksiteBrokerVerdict Grant(int waiter, ulong member, float now, string reason)
        {
            _waiting.RemoveAt(waiter);
            var holder = new Holder
            {
                Member = member,
                LeaseId = NextLeaseId(),
                Reason = reason,
                GrantedAt = now,
                SeenAt = now
            };
            _holders.Add(holder);
            int place = _holders.Count - 1;
            BlacksiteResourceLease lease = LeaseOf(holder);
            Record(now, member, BlacksiteBrokerAction.Granted, place, 0f, reason);
            return new BlacksiteBrokerVerdict(
                BlacksiteWaitOutcome.Granted, lease, place, _waiting.Count, _capacity, 0f, reason);
        }

        private BlacksiteBrokerVerdict Queue(ulong member, int waiter, float now, string refusal)
        {
            float waitedFor = now - _waiting[waiter].Since;
            int place = _holders.Count + waiter;
            Record(now, member, BlacksiteBrokerAction.Queued, place, waitedFor, refusal);
            return new BlacksiteBrokerVerdict(
                BlacksiteWaitOutcome.Queued, default, place, _waiting.Count, _capacity, waitedFor, refusal);
        }

        private int NextLeaseId()
        {
            int id = _nextLeaseId++;
            if (_nextLeaseId == 0) _nextLeaseId = 1;
            return id;
        }

        /// <summary>The lease ends at the stale heartbeat from the holder's last re-assert: a lane has no other clock.</summary>
        private BlacksiteResourceLease LeaseOf(in Holder holder)
            => new BlacksiteResourceLease(
                holder.LeaseId, _key, holder.Member, holder.Reason, holder.GrantedAt,
                holder.SeenAt + BlacksiteResourceLeasePolicy.StaleSeconds);

        private void PruneCleared(float now)
        {
            if (_cleared.Count == 0) return;
            _scratch.Clear();
            foreach (var pair in _cleared)
                if (now - pair.Value > BlacksiteResourceLeasePolicy.ClearedSeconds) _scratch.Add(pair.Key);
            for (int i = 0; i < _scratch.Count; i++)
            {
                _cleared.Remove(_scratch[i]);
                _lastRecorded.Remove(_scratch[i]);
            }
            _scratch.Clear();
        }

        private int HolderIndex(ulong member)
        {
            for (int i = 0; i < _holders.Count; i++) if (_holders[i].Member == member) return i;
            return -1;
        }

        private int WaiterIndex(ulong member)
        {
            for (int i = 0; i < _waiting.Count; i++) if (_waiting[i].Member == member) return i;
            return -1;
        }

        /// <summary>
        /// Append one event for the Unity side to print. The lane knows nothing about a logger: it
        /// fills in the eleven keys of the <c>[UAT][Broker]</c> line and the broker's drain does the rest.
        /// <paramref name="now"/> is what turns the lease's two absolute stamps into the
        /// <c>heldFor=</c> and <c>expiresIn=</c> durations the line carries.
        ///
        /// #1016 S2 review batch 1, item 9: a repeated identical (action, reason) for the same
        /// member on consecutive calls is skipped, and the list is capped. A standing queue of five
        /// members re-asking at 60 Hz produced 19 KB/s of garbage; it now produces one event per
        /// member per state change.
        /// </summary>
        private void Record(
            float now,
            ulong member,
            BlacksiteBrokerAction action,
            int place,
            float waitedFor,
            string reason)
        {
            if (_events == null) return;

            if (_lastRecorded.TryGetValue(member, out LastRecord previous)
                && previous.Action == action
                && string.Equals(previous.Reason, reason, StringComparison.Ordinal))
                return;

            // #1016 S2 review batch 2, F11: the change gate is armed only by an event that was
            // actually recorded. Stamping it above the cap check meant a transition dropped at the
            // cap suppressed itself FOREVER - the next identical (action, reason) matched a
            // last-record that no event list ever carried, so a grant lost to one storm could never
            // be reported again.
            if (_events.Count >= _eventCap)
            {
                _onEventDropped?.Invoke();
                return;
            }
            _lastRecorded[member] = new LastRecord { Action = action, Reason = reason };

            ulong holder = 0UL;
            float heldFor = 0f, expiresIn = 0f;
            if (_holders.Count > 0)
            {
                BlacksiteResourceLease lease = LeaseOf(_holders[0]);
                holder = lease.Holder;
                heldFor = lease.HeldFor(now);
                expiresIn = lease.ExpiresIn(now);
            }
            _events.Add(new BlacksiteBrokerEvent(
                member, _key, action, place, _waiting.Count, _capacity, holder,
                heldFor, expiresIn, waitedFor, reason));
        }
    }
}
