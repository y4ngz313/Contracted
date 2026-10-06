using System.Collections.Generic;

namespace Y4NGZCompany.Contracts.ShadowRaid
{
    /// <summary>
    /// #949 S11H U1: the kinds of Shadow Raid object whose lifetime is one round. The value is
    /// never written to the wire or to a save; it exists so a purge can say what it removed.
    /// </summary>
    internal enum ShadowRaidObjectKind
    {
        Container = 0,
        Crate = 1,
        Drive = 2,
        Drill = 3,
    }

    /// <summary>
    /// #949 S11H U1: what the round that just ended left behind, grouped so the caller can
    /// despawn it and report it by kind.
    /// </summary>
    internal readonly struct ShadowRaidStaleRoundObjects
    {
        private static readonly List<ulong> None = new List<ulong>();

        private readonly List<ulong> _containers;
        private readonly List<ulong> _crates;
        private readonly List<ulong> _drives;
        private readonly List<ulong> _drills;

        internal ShadowRaidStaleRoundObjects(
            List<ulong> containers,
            List<ulong> crates,
            List<ulong> drives,
            List<ulong> drills)
        {
            _containers = containers;
            _crates = crates;
            _drives = drives;
            _drills = drills;
        }

        internal IReadOnlyList<ulong> Containers => _containers ?? None;
        internal IReadOnlyList<ulong> Crates => _crates ?? None;
        internal IReadOnlyList<ulong> Drives => _drives ?? None;
        internal IReadOnlyList<ulong> Drills => _drills ?? None;

        internal int Count => Containers.Count + Crates.Count + Drives.Count + Drills.Count;

        internal IReadOnlyList<ulong> ByKind(ShadowRaidObjectKind kind)
        {
            switch (kind)
            {
                case ShadowRaidObjectKind.Crate:
                    return Crates;
                case ShadowRaidObjectKind.Drive:
                    return Drives;
                case ShadowRaidObjectKind.Drill:
                    return Drills;
                default:
                    return Containers;
            }
        }
    }

    /// <summary>
    /// #949 S11H U1: the round a Shadow Raid object belongs to.
    ///
    /// <para>Before this, nothing asked the question. ShadowRaidContract.ResetRunState dropped
    /// its SpawnedObjects list without despawning anything and BreachContainerController left
    /// its static registry only in OnDestroy, so an object spawned on one moon was still a
    /// live, spawned, active instance on the next one. On 2026-09-10 Test 3 an Assurance
    /// container and its crate survived into March, the March reward stage found the orphan as
    /// the only live container, put the drive inside it and latched itself done; March's own
    /// containers never received a cache and the squad walked at a point in rock 42 m outside
    /// its own dungeon.</para>
    ///
    /// <para>The token is a plain integer that starts at 1 and only ever goes up. Deliberately
    /// not a level name or a scene handle: a session that lands the same moon twice is two
    /// rounds, and the second one must not inherit the first one's objects.</para>
    ///
    /// <para>Unity-free and netcode-free on purpose, so the console fixture
    /// (tests/ShadowRaidLifecycleChecks) compiles this file on its own and pins the rule the
    /// production wrappers run. The wrappers keep every Unity-side concern: the host registers
    /// what it spawns and despawns what this file hands back, and each container and crate
    /// carries its own copy of the token so a client can answer the same question without the
    /// host's list.</para>
    /// </summary>
    internal static class ShadowRaidRoundRegistry
    {
        private readonly struct RoundStamp
        {
            internal RoundStamp(int round, ShadowRaidObjectKind kind)
            {
                Round = round;
                Kind = kind;
            }

            internal int Round { get; }
            internal ShadowRaidObjectKind Kind { get; }
        }

        private static readonly Dictionary<ulong, RoundStamp> Stamps = new Dictionary<ulong, RoundStamp>();
        private static int _currentRound = 1;

        /// <summary>The round now running. Starts at 1 and is only ever incremented.</summary>
        internal static int CurrentRound => _currentRound;

        /// <summary>
        /// Stamps a spawned object with the round now running. Re-registering an id is the
        /// same as registering it, which is what a re-used NetworkObjectId needs: the stamp
        /// follows the object that holds the id now, not the one that held it last round.
        /// </summary>
        internal static void Register(ulong id, ShadowRaidObjectKind kind)
        {
            if (id == 0UL)
                return;

            Stamps[id] = new RoundStamp(_currentRound, kind);
        }

        /// <summary>
        /// True only for an object stamped with the round now running. An id nobody registered
        /// is not live: the registry never guesses on behalf of a caller.
        /// </summary>
        internal static bool IsLive(ulong id)
        {
            return id != 0UL
                   && Stamps.TryGetValue(id, out RoundStamp stamp)
                   && stamp.Round == _currentRound;
        }

        internal static bool TryGetKind(ulong id, out ShadowRaidObjectKind kind)
        {
            if (id != 0UL && Stamps.TryGetValue(id, out RoundStamp stamp))
            {
                kind = stamp.Kind;
                return true;
            }

            kind = ShadowRaidObjectKind.Container;
            return false;
        }

        /// <summary>Every id stamped with the round now running.</summary>
        internal static List<ulong> GetLiveIds()
        {
            var live = new List<ulong>();
            foreach (KeyValuePair<ulong, RoundStamp> entry in Stamps)
            {
                if (entry.Value.Round == _currentRound)
                    live.Add(entry.Key);
            }

            return live;
        }

        /// <summary>
        /// Ends the round now running and starts the next one. Returns everything the old
        /// round left stamped, grouped by kind, and forgets it: the caller despawns it and
        /// reports it, and the dictionary does not grow across a session. The token only ever
        /// increments, so a reset that arrives twice (a disconnect followed by the next ship
        /// landing) costs a token value and nothing else.
        /// </summary>
        internal static ShadowRaidStaleRoundObjects BeginNewRound()
        {
            int endedRound = _currentRound;
            _currentRound++;

            List<ulong> containers = null;
            List<ulong> crates = null;
            List<ulong> drives = null;
            List<ulong> drills = null;
            List<ulong> removed = null;

            foreach (KeyValuePair<ulong, RoundStamp> entry in Stamps)
            {
                if (entry.Value.Round > endedRound)
                    continue;

                switch (entry.Value.Kind)
                {
                    case ShadowRaidObjectKind.Crate:
                        (crates ?? (crates = new List<ulong>())).Add(entry.Key);
                        break;
                    case ShadowRaidObjectKind.Drive:
                        (drives ?? (drives = new List<ulong>())).Add(entry.Key);
                        break;
                    case ShadowRaidObjectKind.Drill:
                        (drills ?? (drills = new List<ulong>())).Add(entry.Key);
                        break;
                    default:
                        (containers ?? (containers = new List<ulong>())).Add(entry.Key);
                        break;
                }

                (removed ?? (removed = new List<ulong>())).Add(entry.Key);
            }

            if (removed != null)
            {
                for (int i = 0; i < removed.Count; i++)
                    Stamps.Remove(removed[i]);
            }

            return new ShadowRaidStaleRoundObjects(containers, crates, drives, drills);
        }

        /// <summary>
        /// The containers the reward stage may configure a cache into: this round's own
        /// placement result, in placement order, filtered to what is still live.
        ///
        /// <para>The substitution this unit makes is here. The reward stage used to read the
        /// global container snapshot, so a round that placed nothing still had a candidate as
        /// long as any container anywhere was spawned. An empty placement result now yields an
        /// empty candidate set, which is the only answer that cannot put this round's cache in
        /// another round's container.</para>
        /// </summary>
        internal static List<ulong> RewardCandidates(
            IReadOnlyList<ulong> placementThisRound,
            IReadOnlyList<ulong> liveIds)
        {
            var candidates = new List<ulong>();
            if (placementThisRound == null || placementThisRound.Count == 0)
                return candidates;
            if (liveIds == null || liveIds.Count == 0)
                return candidates;

            var live = new HashSet<ulong>();
            for (int i = 0; i < liveIds.Count; i++)
                live.Add(liveIds[i]);

            for (int i = 0; i < placementThisRound.Count; i++)
            {
                ulong id = placementThisRound[i];
                if (id == 0UL || !live.Contains(id) || !IsLive(id))
                    continue;
                if (candidates.Contains(id))
                    continue;

                candidates.Add(id);
            }

            return candidates;
        }
    }
}
