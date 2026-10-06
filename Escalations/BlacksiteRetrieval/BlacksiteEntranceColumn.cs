using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S5.4 (#873): where the raiders waiting for one paired entrance stand, in what order, and
    // how long the whole group takes to clear.
    //
    // On the 2026-09-09 run the four raiders cleared the paired main entrance 9.9 s apart (L4081,
    // L4219, L4364, L4528). That spread was inherited from the single-occupancy world ladder above
    // it, not produced at the door: the entrance held its reservation for 1.06 to 2.93 s per member
    // and the run contains zero "reservation deferred" or "reservation occupied" lines of
    // kind=Entrance, so nobody ever queued. With S5.1's column the raiders reach the door about
    // 2.0 to 2.65 s apart, which is still longer than the 1.00 s the threshold is actually owned
    // for, so the door remains ahead of its arrivals; what changes is that a queue can now form at
    // all when a member is slow, and the shape it forms in has to be a column rather than the
    // two-abreast huddle BlacksiteSquadDirector.BuildInteractionQueuePosition used to fan out.
    //
    // This type owns the waiting order, the standing places and the cleared-group bookkeeping. It
    // is deliberately free of UnityEngine so the headless regression can pin all three, and it
    // holds no reference to a soldier, an EntranceTeleport or a transform: positions are expressed
    // as metres back along the approach direction and the caller composes the world point.
    internal sealed class BlacksiteEntranceColumn
    {
        /// <summary>
        /// Facing-and-gesture time at the threshold, in seconds. Unchanged from the value
        /// <c>BlacksiteEntranceInteraction</c> has always used; it lives here so the service model
        /// the regression pins and the phase the runtime actually runs cannot drift apart.
        /// </summary>
        internal const float AlignSeconds = .45f;

        /// <summary>
        /// The near-side settle after the vanilla teleport is issued, in seconds. Unchanged, moved
        /// here for the same reason as <see cref="AlignSeconds"/>.
        /// </summary>
        internal const float TransferSettleSeconds = .55f;

        /// <summary>
        /// The far-side hold a transferred member takes before it rejoins the interior route, in
        /// seconds. Unchanged; <c>BlacksiteSoldierAI.EntrancePostTeleportSettleSeconds</c> reads it
        /// from here. It runs on the far side of the doorway and therefore does not occupy the
        /// threshold: it is not part of <see cref="ThresholdServiceSeconds"/>.
        /// </summary>
        internal const float FarSideSettleSeconds = .75f;

        /// <summary>
        /// The minimum time one member owns the threshold, in seconds: <see cref="AlignSeconds"/>
        /// 0.45 plus <see cref="TransferSettleSeconds"/> 0.55, the transfer itself being a single
        /// tick. Measured against the 2026-09-09 run, whose shortest entrance reservation was
        /// L4353 to L4372, 19 lines at 15 to 18 lines/s, i.e. 1.06 to 1.27 s: the model floor plus
        /// one think frame.
        /// </summary>
        internal const float ThresholdServiceSeconds = AlignSeconds + TransferSettleSeconds;

        /// <summary>
        /// The acceptance bound for one column, in seconds, first clear to last. Four members at
        /// <see cref="ThresholdServiceSeconds"/> is 4.00 s of pure service; 6 s leaves 2 s of
        /// walk-in jitter, and it is also exactly three intervals of S5.1's 2.0 s derived mount
        /// interval (<c>BlacksiteWorldLadderColumn.HeadwayMetres</c> 2.4 /
        /// <c>BlacksiteLandingGearPolicy.ClimbSpeed</c> 1.2), so a column fed by the ladder at its
        /// design cadence meets the bound with the entrance contributing nothing.
        ///
        /// It doubles as the window that separates one column from the next: a member arriving more
        /// than a whole target spread after the previous one cleared is by definition not part of
        /// the same group.
        /// </summary>
        internal const float TargetSpreadSeconds = 6f;

        /// <summary>
        /// Metres between two waiting places along the approach direction. It has to clear two
        /// independent bounds: two bodies need 2 x
        /// <c>BlacksiteArrivalPolicy.AgentRadius</c> = 0.90 m, and an advance of one place has to
        /// move the issued destination further than
        /// <c>BlacksiteArrivalPolicy.MinDestinationChange</c> 1.45 m or S4.2's arrival latch
        /// refuses to re-issue it and the member never steps up. 2.2 m clears the tighter of the
        /// two by 0.75 m.
        /// </summary>
        internal const float SpacingMetres = 2.2f;

        /// <summary>
        /// Seconds of separation one place in the column represents:
        /// <see cref="SpacingMetres"/> 2.2 / <c>BlacksitePhysicalStep.WalkSpeed</c> 3.6 = 0.61 s,
        /// which is <c>BlacksitePassageQueue.HeadwaySeconds</c> to within 0.01 s. The shared
        /// constant is used rather than a second number so every doorway column answers "how far
        /// behind is the next man" the same way. Reported as <c>headway=</c> on the
        /// reservation lines.
        /// </summary>
        internal const float HeadwaySeconds = BlacksitePassageQueue.HeadwaySeconds;

        /// <summary>
        /// The deepest standing place the column issues. The deployment allocates at most four
        /// raiders to one route (<c>BlacksiteDeploymentPolicy</c>), so a fifth waiter at one
        /// doorway cannot occur on a squad entrance; beyond the ceiling every further member shares
        /// the last place and S4.3's crowd claim separates them laterally.
        /// </summary>
        internal const int ColumnCeiling = 4;

        /// <summary>
        /// Stop distance for a member walking to its waiting place, in metres. Two neighbours that
        /// both settle toward each other are <see cref="SpacingMetres"/> - 2 x (stop + settle)
        /// apart, and that has to stay at least one body diameter: 2.2 - 2 x (stop + 0.20) &gt;= 0.90
        /// gives stop &lt;= 0.45, so 0.45 is the exact bound and two neighbours settle exactly
        /// <c>BlacksiteSquadDirector.ClaimSeparationBase</c> apart, which is the separation S4.3
        /// already guarantees. The settle term is <c>BlacksiteArrivalPolicy.SettleSlack</c> 0.20.
        /// </summary>
        internal const float QueueStopMetres =
            (SpacingMetres - 2f * BlacksiteArrivalPolicy.AgentRadius) / 2f - BlacksiteArrivalPolicy.SettleSlack;

        /// <summary>
        /// #1016 S2: the 2 s stale window moved to
        /// <c>BlacksiteResourceLeasePolicy.StaleSeconds</c>, which is the ONE copy of what used to
        /// be three private constants that all claimed to equal
        /// <c>BlacksiteSquadDirector.InteractionReservationSeconds:16</c> by comment and by nothing
        /// else. The value and the schedule are unchanged; a member still drops out of the waiting
        /// order exactly when its reservation would have expired.
        /// </summary>
        private BlacksiteResourceLane _lane;
        private BlacksiteResourceBroker _broker;
        private BlacksiteResourceKey _key;

        private float _columnStartedAt, _lastClearedAt = float.NegativeInfinity;
        private int _cleared;
        // #1219 B4: the distinct members that cleared toward each world in the column being
        // measured.
        private readonly HashSet<ulong> _clearedOutward = new HashSet<ulong>();
        private readonly HashSet<ulong> _clearedInward = new HashSet<ulong>();

        /// <summary>
        /// #1016 S2: register this column's lane with a mission broker. See
        /// <c>BlacksitePassageQueue.Bind</c> - same contract, same reason, and the same silence for
        /// a column nobody binds, which is every headless fixture construction.
        /// </summary>
        internal void Bind(BlacksiteResourceBroker broker, in BlacksiteResourceKey key)
        {
            if (broker == null || !key.IsValid) return;
            // #1016 S2 review batch 2, F3(a): see BlacksitePassageQueue.Bind - "already bound" has
            // to include "and the broker still holds that lane object", or a column keeps writing
            // into an orphan the sweep cannot reach.
            if (_broker == broker && _key == key && broker.Holds(_lane)) return;
            if (_lane != null && !_lane.IsIdle(0f)) return;
            _broker = broker;
            _key = key;
            _lane = broker.Lane(
                key,
                BlacksiteResourceLeasePolicy.Capacity(key.Kind),
                BlacksiteResourceLeasePolicy.HeadwaySeconds(key.Kind));
        }

        /// <summary>
        /// #1016 S2 review batch 2, F4: this column is being forgotten, so its lane goes with it.
        ///
        /// The director and the manifest both keep a dictionary of columns keyed by doorway, and
        /// both prune an idle entry out of it - that is what stops a squad that uses a dozen
        /// doorways from accumulating a dozen columns. A registered lane is never removed by the
        /// sweep, so the prune has to say so explicitly or the dictionary shrinks while the broker's
        /// lane table does not. See <c>BlacksitePassageQueue.Unbind</c>.
        /// </summary>
        internal void Unbind()
        {
            if (_broker != null && _key.IsValid) _broker.Forget(_key, dropRegistered: true);
            _broker = null;
            _key = default;
            _lane = null;
        }

        /// <summary>
        /// The lane this column orders through. Unbound, it is a pure ordering lane: as many places
        /// as <see cref="ColumnCeiling"/> and no headway, which is exactly what this type was before
        /// S2 - it published standing places and admitted nobody.
        /// </summary>
        private BlacksiteResourceLane Lane => _lane ?? (_lane = new BlacksiteResourceLane(
            new BlacksiteResourceKey(BlacksiteResourceKind.RouteDoorway, 0L), ColumnCeiling, 0f));

        /// <summary>The lane, for a caller that needs the lease or the wait.</summary>
        internal BlacksiteResourceLane Resource => Lane;

        /// <summary>Members currently asking for this doorway, the one being served included.</summary>
        internal int Size => Lane.Occupants + Lane.Waiting;

        /// <summary>Members that have cleared the doorway in the column still being measured.</summary>
        internal int ClearedCount => _cleared;

        /// <summary>
        /// Metres behind the approach point for the given place in the column. Place 0 is the
        /// member being served and stands on the approach point itself. Deeper places are one
        /// <see cref="SpacingMetres"/> apart along the approach direction, with no lateral term at
        /// all: the fan this replaced put two members at the same point and made an advance a 3.35 m
        /// sideways crossing of the doorway mouth.
        /// </summary>
        internal static float BackMetres(int place)
        {
            if (place <= 0) return 0f;
            int capped = place < ColumnCeiling ? place : ColumnCeiling - 1;
            return SpacingMetres * capped;
        }

        /// <summary>
        /// Register or refresh this member and return its place in the waiting order. First asked,
        /// first served; the member holding the reservation is moved to place 0 by
        /// <see cref="Serve"/>, so a preferred operator that arrives late still leads the column it
        /// is about to walk through.
        ///
        /// #1016 S2: <c>BlacksiteResourceLane.Enrol</c> - registration WITHOUT a grant, because the
        /// director calls this for every requester including the ones both refusal branches are
        /// about to turn away.
        /// </summary>
        internal int Place(ulong member, float now)
        {
            Lane.Sweep(now);
            return Lane.Enrol(member, now);
        }

        /// <summary>The member now holding the reservation leads the column.</summary>
        internal void Serve(ulong member, float now) => Lane.Serve(member, now);

        /// <summary>
        /// This member has stopped asking: it cleared the doorway, failed, died or was re-tasked.
        /// Removal keeps the order of everyone behind it, so a casualty in the middle of a column
        /// does not reshuffle the members already walking to their places.
        /// </summary>
        internal void Leave(ulong member) => Lane.Release(member);

        internal bool IsWaiting(ulong member) => Lane.Holds(member) || Lane.IsWaiting(member);

        /// <summary>
        /// A member has reached the far side. Returns <paramref name="spread"/>, the seconds since
        /// the first clear of this column, and <paramref name="columnSize"/>, the number of
        /// DISTINCT members that cleared in this member's direction (toward outside or toward
        /// inside) in this column - one member clearing twice counts once, and the two directions
        /// count separately (#1219 B4: round 3's <c>columnSize</c> reached 47 on four members
        /// looping through one door). Together they are the <c>[UAT][Entrance] event=cleared</c>
        /// line. A clear more than one <see cref="TargetSpreadSeconds"/> after the previous one
        /// starts a new column, and its counts.
        ///
        /// Stays on the facade: this is the column's SPREAD measurement, which is a diagnostic
        /// about how a group drained, not a statement about who may go next.
        /// </summary>
        internal void NoteCleared(float now, ulong member, bool towardOutside, out float spread, out int columnSize)
        {
            if (_cleared == 0 || now - _lastClearedAt > TargetSpreadSeconds)
            {
                _columnStartedAt = now;
                _cleared = 0;
                _clearedOutward.Clear();
                _clearedInward.Clear();
            }
            _cleared++;
            _lastClearedAt = now;
            var direction = towardOutside ? _clearedOutward : _clearedInward;
            direction.Add(member);
            spread = now - _columnStartedAt;
            columnSize = direction.Count;
        }

        /// <summary>Nobody waiting and no column still being measured: the director may forget it.</summary>
        internal bool IsIdle(float now)
        {
            Lane.Sweep(now);
            return Lane.IsIdle(now) && (_cleared == 0 || now - _lastClearedAt > TargetSpreadSeconds);
        }
    }
}
