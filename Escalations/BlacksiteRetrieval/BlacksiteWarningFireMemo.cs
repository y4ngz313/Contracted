using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #874 N8 (2026-09-23 playtest, decision 7): the Old Bird WARNING FIRE budget, as pure
    /// arithmetic. A rifle member may put at most <see cref="WarningBurstsPerEncounter"/> bursts
    /// into an Old Bird, and none at all once that Old Bird has attacked it or a squadmate; after
    /// either it evades the Old Bird like a baton member does from the first sighting.
    ///
    /// The ENCOUNTER is the round: an Old Bird instance is keyed by its network object id, which a
    /// new round re-issues, and <see cref="Reset"/> runs with the futile-damage memo's round reset.
    /// A member keeps no second budget for the same Old Bird later in the round - once it has
    /// fired its warning or been stomped, that Old Bird is a contact it walks away from.
    ///
    /// Unity-free ON PURPOSE, like <see cref="BlacksiteFutileDamageMemo"/> beside it, so the cap
    /// and the attack latch are pinned by the headless suites. The Unity-bound half - which
    /// contact, which member, which squad, and who may record - is
    /// <c>BlacksiteThreatAdapterRegistry.WarningFireOpen / NoteWarningBurst /
    /// NoteRefusedContactAttack</c>. HOST state: the host fires and the host decides, so nothing
    /// here is replicated and a client reads an empty memo it never consults.
    /// </summary>
    internal sealed class BlacksiteWarningFireMemo
    {
        /// <summary>Decision 7: "may shoot at it once or twice".</summary>
        internal const int WarningBurstsPerEncounter = 2;

        /// <summary>
        /// Bounded by construction, per collection. A round has a handful of Old Birds and a squad
        /// of at most a dozen members, so this is never reached in play; the cap is what keeps a
        /// modded spawner from turning round state into a leak. A FULL burst table closes the
        /// budget for a member it has no row for - an untracked member would otherwise fire
        /// without a count - so the memo fails toward the evade, never toward more rounds.
        /// </summary>
        internal const int MaxTrackedRows = 256;

        private readonly struct Pair : IEquatable<Pair>
        {
            private readonly ulong _contact, _owner;
            internal Pair(ulong contact, ulong owner) { _contact = contact; _owner = owner; }
            public bool Equals(Pair other) => _contact == other._contact && _owner == other._owner;
            public override bool Equals(object obj) => obj is Pair other && Equals(other);
            public override int GetHashCode() => (_contact.GetHashCode() * 397) ^ _owner.GetHashCode();
        }

        // (contact, member) -> bursts committed at that contact by that member.
        private readonly Dictionary<Pair, int> _bursts = new Dictionary<Pair, int>();
        // (contact, squad): that contact has attacked a member of that squad.
        private readonly HashSet<Pair> _attacked = new HashSet<Pair>();

        /// <summary>Live rows in both tables, for the bound's own test.</summary>
        internal int TrackedCount => _bursts.Count + _attacked.Count;

        /// <summary>
        /// This member may still commit a warning burst at this contact: its squad has not been
        /// attacked by it and the member has fired fewer than the cap.
        /// </summary>
        internal bool MayFire(ulong contact, ulong member, ulong squad)
        {
            if (WasAttacked(contact, squad)) return false;
            return _bursts.TryGetValue(new Pair(contact, member), out int bursts)
                ? bursts < WarningBurstsPerEncounter
                : _bursts.Count < MaxTrackedRows;
        }

        /// <summary>This contact has attacked a member of this squad this round.</summary>
        internal bool WasAttacked(ulong contact, ulong squad) => _attacked.Contains(new Pair(contact, squad));

        /// <summary>One burst committed at this contact. Returns the member's count after it.</summary>
        internal int RecordBurst(ulong contact, ulong member)
        {
            var key = new Pair(contact, member);
            _bursts.TryGetValue(key, out int bursts);
            if (bursts == 0 && _bursts.Count >= MaxTrackedRows) return WarningBurstsPerEncounter;
            _bursts[key] = ++bursts;
            return bursts;
        }

        /// <summary>
        /// This contact attacked a member of this squad. Returns true on the call that latches it,
        /// so the caller's log line is once per (contact, squad) by construction.
        /// </summary>
        internal bool RecordAttack(ulong contact, ulong squad)
            => _attacked.Count < MaxTrackedRows && _attacked.Add(new Pair(contact, squad));

        /// <summary>Round reset. The memo is round state and nothing else clears it.</summary>
        internal void Reset()
        {
            _bursts.Clear();
            _attacked.Clear();
        }
    }
}
