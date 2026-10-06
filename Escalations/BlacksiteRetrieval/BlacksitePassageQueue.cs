namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #874 F04: which phase of the shared cable the cache is in. Pure, so the native fixtures
    /// pin the rule instead of restating the five motion codes: <c>_cargoMotion</c> is 0 standing
    /// hooked at the cable foot, 1 lowering, 2 raising, 3 aboard, 4 parked (unhooked on the moon
    /// while a hover ship is away).
    /// </summary>
    internal static class BlacksiteCablePhase
    {
        internal const byte Ground = 0, Lowering = 1, Raising = 2, Aboard = 3, Parked = 4;

        /// <summary>The crate is on the rope: exclusive with any body on the same rope.</summary>
        internal static bool OnCable(byte motion) => motion == Lowering || motion == Raising;

        /// <summary>The crate is standing HOOKED on the touchdown at the foot of the rope.</summary>
        internal static bool AtCableFoot(byte motion) => motion == Ground;

        /// <summary>
        /// The crate is standing on the moon, hooked or parked: the round's ground cache, the one a
        /// carrier loads into and the cargo term of the departure rule reads as decided.
        /// </summary>
        internal static bool OnMoon(byte motion) => motion == Ground || motion == Parked;

        /// <summary>
        /// Anything that stops a player using this rope end to end: the crate on it, or hooked at
        /// its foot. A parked crate has been let go and is neither.
        /// </summary>
        internal static bool BlocksPersonnel(byte motion) => OnCable(motion) || AtCableFoot(motion);

        /// <summary>
        /// The phase a hover ship leaves the crate in when it leaves station. Only a crate standing
        /// hooked at the foot is let go; a crate on the rope is never dropped, and the station rule
        /// holds the ship while one is (<c>BlacksiteStationVeto.CargoOnCable</c>).
        /// </summary>
        internal static byte Unhook(byte motion) => motion == Ground ? Parked : motion;

        /// <summary>
        /// The phase the ship picks a parked crate up in when it is back over the touchdown: hooked
        /// at the foot, the one phase the lift starts from.
        /// </summary>
        internal static byte Rehook(byte motion) => motion == Parked ? Ground : motion;

        /// <summary>
        /// A LOWERING load owes the whole touchdown footprint its clearance - it is coming down
        /// onto whatever stands there. A RISING one leaves the ground, so a body beside it is not
        /// in its way.
        /// </summary>
        internal static bool OwesFootprintClearance(bool lowering) => lowering;
    }

    // #1016 S2: a named facade over one BlacksiteResourceLane - the world ladder and the ship's one
    // FIFO. Every ordering rule (capacity, headway, stale prune) lives in the lane; this type keeps
    // its name and signatures because headless fixtures construct it directly and WorldLadderChecks
    // resolves it by name.
    //
    // #874 S3 (plan §3 rule 3): the ship constructs it with no headway, so its lane is a pure
    // capacity-1 FIFO whose holder is released by the end of its crossing. #1293 R2 / R4: a lane has
    // no budget to give a queue; a holder that stops progressing is answered by the ledger.
    internal sealed class BlacksitePassageQueue
    {
        internal const float HeadwaySeconds = .6f;

        private readonly float _headwaySeconds;
        private BlacksiteResourceLane _lane;
        private BlacksiteResourceBroker _broker;
        private BlacksiteResourceKey _key;

        internal BlacksitePassageQueue(float headwaySeconds = HeadwaySeconds) { _headwaySeconds = headwaySeconds; }

        /// <summary>
        /// #1219 A3: the ship's one FIFO, built in one place: no headway, bound by
        /// <see cref="BindShipLadder"/>. <c>BlacksiteShipController.Ladder</c> is built and bound
        /// here, and the headless ShipLadder lane row drives the same two calls. #1293 R2: its
        /// holder releases on the end of its crossing, the crate's or hook's trip, the stale
        /// heartbeat, or the ledger's answer to a holder that stops progressing.
        /// </summary>
        internal static BlacksitePassageQueue ShipLadder()
            => new BlacksitePassageQueue(headwaySeconds: 0f);

        /// <summary>#1219 A3: the ship FIFO's binding: <see cref="Bind"/> to the ShipLadder key. A
        /// separate name, not a Bind overload, because native fixtures resolve Bind by name.</summary>
        internal void BindShipLadder(BlacksiteResourceBroker broker) => Bind(broker, BlacksiteResourceKey.ShipLadder());

        /// <summary>
        /// #1016 S2: register this queue's lane with a mission broker, so the one sweep expires it
        /// and the one view prints it.
        ///
        /// Lazy on purpose: the ship's queue is a field initialiser and a world ladder does not know
        /// its mission, so binding happens on the first request that has a mission in hand. A queue
        /// that has already admitted somebody keeps the lane it admitted them into. A queue that is
        /// never bound - every headless fixture construction - owns a private lane.
        /// </summary>
        internal void Bind(BlacksiteResourceBroker broker, in BlacksiteResourceKey key)
        {
            if (broker == null || !key.IsValid) return;
            // #1016 S2 review batch 2, F3(a): "already bound" means the same broker, the same key
            // AND a lane that broker still holds; a post-Clear orphan is re-taken here.
            if (_broker == broker && _key == key && broker.Holds(_lane)) return;
            // IsIdle is a pure emptiness test, so the clock is not read; a queue with anyone in it
            // keeps the lane they are in. An orphan is empty by construction.
            if (_lane != null && !_lane.IsIdle(0f)) return;
            _broker = broker;
            _key = key;
            _lane = broker.Lane(key, BlacksiteResourceLeasePolicy.Capacity(key.Kind), _headwaySeconds);
        }

        /// <summary>
        /// #1016 S2 review batch 2, F4: this queue is being discarded, so its lane goes with it.
        /// After this the broker knows nothing about the key and this queue falls back to a private
        /// lane, so a stray call on a discarded facade is harmless.
        /// </summary>
        internal void Unbind()
        {
            if (_broker != null && _key.IsValid) _broker.Forget(_key, dropRegistered: true);
            _broker = null;
            _key = default;
            _lane = null;
        }

        /// <summary>
        /// The lane this queue orders through. Unbound, it is a private capacity-1 lane with this
        /// queue's own headway.
        /// </summary>
        private BlacksiteResourceLane Lane => _lane ?? (_lane = new BlacksiteResourceLane(
            BlacksiteResourceKey.WorldLadder(null), 1, _headwaySeconds));

        /// <summary>The lane, for a caller that needs the verdict rather than the boolean.</summary>
        internal BlacksiteResourceLane Resource => Lane;

        internal bool HasOccupant(float now) { Lane.Sweep(now); return Lane.Occupants > 0; }
        // #874 S4.0b: read-only views of the queue's real state for diagnostics. None of these
        // prune, admit or evict: a diagnostic must not be able to change who is admitted.
        internal int OccupantCount => Lane.Occupants;
        internal int WaitingCount => Lane.Waiting;
        internal bool TryGetAdmittedHead(out ulong head)
        {
            head = 0UL;
            if (!Lane.TryGetHolder(out BlacksiteResourceLease lease)) return false;
            head = lease.Holder; return true;
        }

        /// <summary>
        /// #1016 S2: the verdict behind <see cref="Request"/>, for the call sites that print why they
        /// are waiting and for how long.
        /// </summary>
        internal BlacksiteBrokerVerdict RequestVerdict(ulong member, float now)
            => Lane.Request(member, now, "queue");

        internal bool Request(ulong member, float now) => RequestVerdict(member, now).Admitted;

        // The admitted member has cleared the doorway; the next member may start after the headway.
        internal void Advance(ulong member, float now) => Lane.Advance(member, now);

        internal void Release(ulong member) => Lane.Release(member);
    }
}
