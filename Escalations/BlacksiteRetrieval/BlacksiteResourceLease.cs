using System;
using System.Globalization;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1016 S2: the closed vocabulary of contended things in the Blacksite runtime.
    ///
    /// Before S2 there were nine independent claim stores - three queue types with three private
    /// copies of the same 2 s stale window, an interaction reservation dictionary, a column
    /// dictionary, a route-column dictionary, four elevator statics and the manifest's unnamed
    /// capacity-of-one gate at <c>BlacksiteObjectiveManifestController.cs:531</c> - with eight
    /// prune implementations between them, seven cadences and no shared view. This enum names the
    /// resources so one lifetime model and one log line can cover all of them (twelve in #1016 S2;
    /// #1233 S5 deleted the facility Door lane, #1293 R2 the landing post and #1293 R4 the
    /// CabinLip, which is why the enum has nine now).
    ///
    /// The order is the order of the S2 design note section B and is load-bearing only in that the
    /// note's table is read against it; nothing is serialised, so no value is on the wire.
    ///
    /// Unity-free on purpose: the headless
    /// regressions drive <see cref="BlacksiteResourceLane"/> and
    /// <see cref="BlacksiteResourceBroker"/> directly, so the grant, queue and expire rules
    /// are proven against the production state machine rather than against a copy of it.
    /// </summary>
    internal enum BlacksiteResourceKind
    {
        None,
        /// <summary>A paired entrance / internal teleport threshold (was: interaction reservation + <c>_interactionColumns</c>).</summary>
        Doorway,
        /// <summary>A committed-route hop mouth (was: <c>_routeColumns</c>, <c>…Raid.cs:50</c>).</summary>
        RouteDoorway,
        /// <summary>A container override / cache override (was: interaction reservation kind=*Override).</summary>
        ObjectiveLock,
        /// <summary>#1305 G3: the mineshaft car's command lease, one commander per car; riders need none (<c>BlacksiteElevatorInteraction</c>).</summary>
        Elevator,
        /// <summary>One authored ladder column (was: <c>BlacksiteWorldLadder.Queue</c> + <c>.Column</c>).</summary>
        WorldLadder,
        /// <summary>#874 S3: the ship's one FIFO over its two deck links (<c>BlacksiteShipController.Ladder</c>).</summary>
        ShipLadder,
        /// <summary>One manifest record's carrier slot (was: <c>Snapshot.CarrierId</c>).</summary>
        Objective,
        /// <summary>The global non-Loot capacity of one (was: <c>…Controller.cs:531</c>).</summary>
        ObjectiveBoard
    }

    /// <summary>
    /// #1016 S2: the closed <c>action=</c> vocabulary of the <c>[UAT][Broker]</c> line (design note
    /// E.1): a closed set so a score script can
    /// switch on it, and <c>None</c> so "nothing happened" is representable without a nullable.
    /// #1293 R4: <c>Evicted</c> is deleted with the wait bound and the held ceiling, its only
    /// producers; the stale heartbeat's <c>Expired</c> is the one way a lane drops a member.
    /// </summary>
    internal enum BlacksiteBrokerAction
    {
        None,
        Granted,
        Renewed,
        Released,
        Expired,
        Refused,
        Queued,
        Advanced
    }

    /// <summary>
    /// #1016 S2: what one <c>Request</c> did, from the caller's point of view.
    ///
    /// <see cref="Queued"/> is the verdict that did not exist before S2 and is the reason the stage
    /// exists: a refused elevator candidate used to get a null session
    /// (<c>BlacksiteElevatorInteraction.GetOrCreateSession</c>) and a refused entrance requester
    /// used to walk to a queue point and re-ask, and neither of them could say how long it had been
    /// waiting or when the wait would end.
    /// </summary>
    internal enum BlacksiteWaitOutcome
    {
        Granted,
        Renewed,
        Queued,
        Refused
    }

    /// <summary>
    /// #1016 S2: the identity of one contended thing. <see cref="Id"/> is TODAY'S key, unchanged,
    /// so a log line keeps the identity the existing lines already print:
    /// <list type="bullet">
    /// <item>Doorway/ObjectiveLock - <c>BlacksiteSquadDirector.GetInteractionKey(kind, target)</c></item>
    /// <item>RouteDoorway - <c>BlacksiteObjectiveManifestController.DoorwayKey(doorway)</c></item>
    /// <item>Elevator - <c>RuntimeHelpers.GetHashCode(car)</c> as an unsigned 32-bit id: one lane per car</item>
    /// <item>WorldLadder - <c>RuntimeHelpers.GetHashCode(ladder)</c></item>
    /// <item>Objective - <c>(long)record.ObjectId</c></item>
    /// <item>ObjectiveBoard / ShipLadder - 0, because there is exactly one of each</item>
    /// </list>
    /// </summary>
    internal readonly struct BlacksiteResourceKey : IEquatable<BlacksiteResourceKey>
    {
        internal readonly BlacksiteResourceKind Kind;
        internal readonly long Id;

        internal BlacksiteResourceKey(BlacksiteResourceKind kind, long id = 0L)
        {
            Kind = kind;
            Id = id;
        }

        internal bool IsValid => Kind != BlacksiteResourceKind.None;

        /// <summary>
        /// The printed identity, <c>"&lt;kind&gt;:&lt;Id&gt;"</c>. This is the <c>resource=</c> value on
        /// <c>[UAT][Broker]</c> and the leading token of each <c>detail=</c> segment on
        /// <c>[UAT][BrokerWaits]</c>, so it is computed the same way in both places and nowhere else.
        /// </summary>
        internal string Token => Kind == BlacksiteResourceKind.None
            ? "none"
            : Kind.ToString() + ":" + Id.ToString(CultureInfo.InvariantCulture);

        // -------------------------------------------------------------------------------
        // #1016 S2 review batch 1, item 14: the composers.
        //
        // Six production construction sites would otherwise each spell the id their own way, so
        // each kind's id is composed here and nowhere else.
        // -------------------------------------------------------------------------------

        /// <summary>A paired entrance or internal teleport threshold, from the director's interaction key.</summary>
        internal static BlacksiteResourceKey Doorway(int interactionKey)
            => new BlacksiteResourceKey(BlacksiteResourceKind.Doorway, interactionKey);

        /// <summary>A container or cache override, from the same interaction key.</summary>
        internal static BlacksiteResourceKey ObjectiveLock(int interactionKey)
            => new BlacksiteResourceKey(BlacksiteResourceKind.ObjectiveLock, interactionKey);

        /// <summary>A committed-route hop mouth, from <c>BlacksiteObjectiveManifestController.DoorwayKey</c>.</summary>
        internal static BlacksiteResourceKey RouteDoorway(long doorwayKey)
            => new BlacksiteResourceKey(BlacksiteResourceKind.RouteDoorway, doorwayKey);

        /// <summary>
        /// #1293 R4: the mineshaft car - one capacity-1 lane per car. The car's hash as an unsigned
        /// 32-bit id; the high word that once named a squad for the planner's car-lease call is gone
        /// with that call (#1293 R3).
        /// </summary>
        internal static BlacksiteResourceKey Elevator(object adapterKey)
            => new BlacksiteResourceKey(
                BlacksiteResourceKind.Elevator,
                adapterKey == null ? 0L : (uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(adapterKey));

        /// <summary>One authored ladder, by reference identity.</summary>
        internal static BlacksiteResourceKey WorldLadder(object ladder)
            => new BlacksiteResourceKey(
                BlacksiteResourceKind.WorldLadder,
                ladder == null ? 0L : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ladder));

        /// <summary>One manifest record's carrier slot, by the record's own object id.</summary>
        internal static BlacksiteResourceKey Objective(ulong objectId)
            => new BlacksiteResourceKey(BlacksiteResourceKind.Objective, unchecked((long)objectId));

        /// <summary>The global non-Loot gate. There is exactly one, so its id is 0.</summary>
        internal static BlacksiteResourceKey ObjectiveBoard()
            => new BlacksiteResourceKey(BlacksiteResourceKind.ObjectiveBoard, 0L);

        /// <summary>The ship ladder. Exactly one per mission, so id 0.</summary>
        internal static BlacksiteResourceKey ShipLadder()
            => new BlacksiteResourceKey(BlacksiteResourceKind.ShipLadder, 0L);

        public bool Equals(BlacksiteResourceKey other) => Kind == other.Kind && Id == other.Id;

        public override bool Equals(object obj) => obj is BlacksiteResourceKey other && Equals(other);

        public override int GetHashCode()
        {
            // Deterministic and allocation-free: the kind is a small enum and the id is a hash or a
            // NetworkObjectId, so the two are folded rather than combined through a tuple.
            unchecked
            {
                return ((int)Kind * 397) ^ (int)(Id ^ (Id >> 32));
            }
        }

        public override string ToString() => Token;

        // Operators on a user-defined type must be public; the type itself stays internal, so the
        // reach is unchanged.
        public static bool operator ==(BlacksiteResourceKey a, BlacksiteResourceKey b) => a.Equals(b);

        public static bool operator !=(BlacksiteResourceKey a, BlacksiteResourceKey b) => !a.Equals(b);
    }

    /// <summary>
    /// #1016 S2: the handle a holder keeps (Id / Owner / Reason / ExpiresAt / IsValid). An
    /// objective claim does not stop the body.
    ///
    /// <see cref="Id"/> 0 is "not held". A refused or queued caller is handed a default lease, so a
    /// caller that was refused cannot renew, release or act on somebody else's claim. That is the
    /// whole of "a refused caller must never be treated as holding".
    /// </summary>
    internal readonly struct BlacksiteResourceLease
    {
        internal readonly int Id;
        internal readonly BlacksiteResourceKey Resource;
        /// <summary>The member's NetworkObjectId - the token every existing queue already uses.</summary>
        internal readonly ulong Holder;
        /// <summary>Today's reservation or refusal reason string, verbatim.</summary>
        internal readonly string Reason;
        internal readonly float GrantedAt;
        internal readonly float ExpiresAt;

        internal BlacksiteResourceLease(
            int id,
            BlacksiteResourceKey resource,
            ulong holder,
            string reason,
            float grantedAt,
            float expiresAt)
        {
            Id = id;
            Resource = resource;
            Holder = holder;
            Reason = reason;
            GrantedAt = grantedAt;
            ExpiresAt = expiresAt;
        }

        internal bool IsValid => Id != 0 && Resource.Kind != BlacksiteResourceKind.None;

        /// <summary>Seconds this claim has stood, which is <c>heldFor=</c> on the two new lines.</summary>
        internal float HeldFor(float now)
        {
            if (!IsValid) return 0f;
            float held = now - GrantedAt;
            return held > 0f ? held : 0f;
        }

        /// <summary>
        /// Seconds until the lane forgets it, which is <c>expiresIn=</c>: the stale heartbeat from the
        /// holder's last re-assert, the only clock a lane has (#1293 R4). Never negative.
        /// </summary>
        internal float ExpiresIn(float now)
        {
            if (!IsValid) return 0f;
            float remaining = ExpiresAt - now;
            return remaining > 0f ? remaining : 0f;
        }
    }

    /// <summary>
    /// #1016 S2: the answer to one <c>Request</c>, carrying everything the <c>[UAT][Broker]</c>
    /// line needs so a caller never has to ask the lane a second question to print its own refusal.
    ///
    /// #1293 R4: a verdict carries no wait deadline. Every wait is a declared wait (a Queue or Dwell
    /// commitment) the progress ledger never judges; it moves exactly when its holder completes or
    /// the ledger answers that holder, so <see cref="WaitedFor"/> is the only wait it reports.
    /// </summary>
    internal readonly struct BlacksiteBrokerVerdict
    {
        internal readonly BlacksiteWaitOutcome Outcome;
        /// <summary>Valid only on <see cref="BlacksiteWaitOutcome.Granted"/> or <see cref="BlacksiteWaitOutcome.Renewed"/>.</summary>
        internal readonly BlacksiteResourceLease Lease;
        /// <summary>
        /// The member's index in the single ordered set "holders first, then waiters", which is
        /// exactly <c>BlacksiteEntranceColumn._order</c>'s index and therefore exactly what
        /// <c>BlacksiteEntranceColumn.BackMetres</c> already expects. 0 is the member being served;
        /// -1 is "not in this lane at all".
        /// </summary>
        internal readonly int Place;
        internal readonly int Waiting;
        internal readonly int Capacity;
        internal readonly float WaitedFor;
        /// <summary>Today's refusal text, verbatim (the ladder's <c>refusal=</c>, the trip's capacity text).</summary>
        internal readonly string Reason;

        internal BlacksiteBrokerVerdict(
            BlacksiteWaitOutcome outcome,
            BlacksiteResourceLease lease,
            int place,
            int waiting,
            int capacity,
            float waitedFor,
            string reason)
        {
            Outcome = outcome;
            Lease = lease;
            Place = place;
            Waiting = waiting;
            Capacity = capacity;
            WaitedFor = waitedFor;
            Reason = reason;
        }

        /// <summary>
        /// The one test every call site makes: may this member proceed through the thing it asked
        /// for? True for a fresh grant and for a renewal, and for nothing else - a
        /// <see cref="BlacksiteWaitOutcome.Queued"/> caller waits.
        /// </summary>
        internal bool Admitted =>
            Outcome == BlacksiteWaitOutcome.Granted || Outcome == BlacksiteWaitOutcome.Renewed;
    }

    /// <summary>
    /// #1016 S2: one expiry, grant, release or refusal, recorded so the Unity side can print it
    /// without the broker ever knowing what a logger is. The broker appends these to one reusable
    /// list that <c>BlacksiteSquadBlackboard</c> drains each server tick.
    ///
    /// Field order is the <c>[UAT][Broker]</c> key order of design note E.1, so the emitter is a
    /// straight transcription and cannot silently reorder a key.
    /// </summary>
    internal readonly struct BlacksiteBrokerEvent
    {
        internal readonly ulong Member;
        internal readonly BlacksiteResourceKey Resource;
        internal readonly BlacksiteBrokerAction Action;
        internal readonly int Place;
        internal readonly int Waiting;
        internal readonly int Capacity;
        /// <summary>Who holds the resource at the moment of the event; 0 when nobody does.</summary>
        internal readonly ulong Holder;
        internal readonly float HeldFor;
        internal readonly float ExpiresIn;
        internal readonly float WaitedFor;
        internal readonly string Reason;

        internal BlacksiteBrokerEvent(
            ulong member,
            BlacksiteResourceKey resource,
            BlacksiteBrokerAction action,
            int place,
            int waiting,
            int capacity,
            ulong holder,
            float heldFor,
            float expiresIn,
            float waitedFor,
            string reason)
        {
            Member = member;
            Resource = resource;
            Action = action;
            Place = place;
            Waiting = waiting;
            Capacity = capacity;
            Holder = holder;
            HeldFor = heldFor;
            ExpiresIn = expiresIn;
            WaitedFor = waitedFor;
            Reason = reason;
        }
    }

    /// <summary>
    /// #1016 S2: the closed lifetime table, approved on #1016 before any code (design note C.1 and
    /// section G, all ten recommendations approved by Lawson 2026-09-13).
    ///
    /// #1293 R2 / R4: a lane has a capacity, a headway and the stale heartbeat, and nothing else: the
    /// held ceiling (<c>MaxHeldSeconds</c>) and the wait bound (<c>MaxWaitSeconds</c>) are deleted,
    /// and no lane constructor takes either. A holder releases on its own event
    /// (complete, drop, crossing ended, death or despawn, an explicit Release) or through the stale
    /// heartbeat when it stops re-asserting; a holder that stops progressing is answered by the
    /// progress ledger (<c>BlacksiteSquadPlanner.AnswerStalled</c> releases every lease it holds or
    /// waits for); a waiter is a declared wait the ledger never judges, so a queue moves exactly when
    /// its holder completes or is answered.
    /// </summary>
    internal static class BlacksiteResourceLeasePolicy
    {
        /// <summary>
        /// The ONE copy of today's three identical windows:
        /// <c>BlacksiteEntranceColumn.StaleSeconds:115</c>,
        /// <c>BlacksitePassageQueue.StaleSeconds:14</c> and
        /// <c>BlacksiteWorldLadderColumn.StaleSeconds:68</c>, all of which claim to equal
        /// <c>BlacksiteSquadDirector.InteractionReservationSeconds:16</c> by comment and by nothing
        /// else. A holder or a waiter that has not re-asserted for this long has died, been stunned
        /// out of the approach, been re-tasked or simply walked away.
        /// </summary>
        internal const float StaleSeconds = 2f;

        /// <summary>
        /// How long a member that has ADVANCED past a threshold keeps walking without re-entering
        /// the queue behind itself. <c>BlacksitePassageQueue.ClearedSeconds:14</c>, unchanged: the
        /// TierTwoThree fixture pins it as "cleared member keeps walking outside the queue".
        /// </summary>
        internal const float ClearedSeconds = 12f;

        /// <summary>
        /// Slack on the headway compare, in seconds. <c>BlacksitePassageQueue.cs:63</c> applies
        /// exactly this and <c>BlacksiteWorldLadderColumn.HeadwaySlack:84</c> applies its metre
        /// equivalent, both for the same single-precision reason.
        /// </summary>
        internal const float HeadwaySlack = .001f;

        /// <summary>
        /// How many members may hold this kind at once, as a STATIC default. The ladder's capacity is
        /// geometry rather than policy - <c>BlacksiteWorldLadderColumn.MaxOccupants</c>, which depends
        /// on the measured rise - so the value here is the default and
        /// <see cref="BlacksiteResourceLane"/>'s constructor takes the real one.
        /// </summary>
        internal static int Capacity(BlacksiteResourceKind kind)
        {
            switch (kind)
            {
                // BlacksiteEntranceColumn.ColumnCeiling:96 - the deployment allocates at most four
                // raiders to one route, so a route column carries at most four bodies in order.
                case BlacksiteResourceKind.RouteDoorway: return 4;
                // #1016 S2 review batch 1, decision 5: BlacksiteWorldLadderColumn is NOT converted
                // to a facade in S2 - its two refusal strings and its per-occupant geometry need an
                // up/height the lane does not store. Only BlacksiteWorldLadder.Queue goes behind
                // this lane, and that is a plain BlacksitePassageQueue: one admitted head.
                // The column keeps its own MaxOccupants, its own occupant list and its own stale
                // window, and stays the thing that decides how many bodies are on the rungs.
                case BlacksiteResourceKind.WorldLadder: return 1;
                case BlacksiteResourceKind.None: return 0;
                // Every other resource is exclusive and stays exclusive: one reservation, one
                // admitted passage head, one commander per mineshaft car (riders need no lease,
                // #1305 G3), one carrier per record, one board.
                default: return 1;
            }
        }

        /// <summary>
        /// Seconds between one member clearing a threshold and the next being admitted through it.
        /// <c>BlacksitePassageQueue.HeadwaySeconds:11</c> is 0.6 s and
        /// <c>BlacksiteEntranceColumn.HeadwaySeconds:88</c> is defined AS that constant, so the
        /// doorway and the aisle answer "how far behind is the next man" the same way.
        ///
        /// The world ladder's separation is metres, not seconds
        /// (<c>BlacksiteWorldLadderColumn.HeadwayMetres:33</c>), so its lane carries no time
        /// headway. Its geometry stays where it is: <c>BlacksiteWorldLadderColumn</c> is not a
        /// facade over a lane in S2 (review batch 1, decision 5) and keeps answering the
        /// direction, span and lead-climber questions itself.
        /// </summary>
        internal static float HeadwaySeconds(BlacksiteResourceKind kind)
        {
            switch (kind)
            {
                case BlacksiteResourceKind.Doorway:
                case BlacksiteResourceKind.RouteDoorway:
                    return .6f;
                // A door has no column today (BlacksiteSquadDirector.IsColumnKind:1451 excludes it,
                // and DescribeColumn prints "column=none, headway=none"), so it gains none here.
                default:
                    return 0f;
            }
        }

        internal static readonly BlacksiteResourceKind[] Kinds =
            (BlacksiteResourceKind[])Enum.GetValues(typeof(BlacksiteResourceKind));

        /// <summary>The <c>action=</c> token. Closed, lower-case, exactly design note E.1's list.</summary>
        internal static string Token(BlacksiteBrokerAction action)
        {
            switch (action)
            {
                case BlacksiteBrokerAction.Granted: return "granted";
                case BlacksiteBrokerAction.Renewed: return "renewed";
                case BlacksiteBrokerAction.Released: return "released";
                case BlacksiteBrokerAction.Expired: return "expired";
                case BlacksiteBrokerAction.Refused: return "refused";
                case BlacksiteBrokerAction.Queued: return "queued";
                case BlacksiteBrokerAction.Advanced: return "advanced";
                default: return "none";
            }
        }

        /// <summary>The verdict as the same closed vocabulary, so a caller prints one spelling.</summary>
        internal static BlacksiteBrokerAction Action(BlacksiteWaitOutcome outcome)
        {
            switch (outcome)
            {
                case BlacksiteWaitOutcome.Granted: return BlacksiteBrokerAction.Granted;
                case BlacksiteWaitOutcome.Renewed: return BlacksiteBrokerAction.Renewed;
                case BlacksiteWaitOutcome.Queued: return BlacksiteBrokerAction.Queued;
                case BlacksiteWaitOutcome.Refused: return BlacksiteBrokerAction.Refused;
                default: return BlacksiteBrokerAction.None;
            }
        }
    }
}
