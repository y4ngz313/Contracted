using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1083 (2026-09-16 playtest, WP I): the COMBAT GIVE-UP CLOCK, as pure arithmetic over one
    /// number the squad already reads - the contact's own <c>enemyHP</c> before and after a hit it
    /// actually applied.
    ///
    /// WP B answered the three classes that are known today to be undamageable by ANYONE (PufferAI,
    /// JesterAI, DressGirlAI never override <c>EnemyAI.HitEnemy</c>). This is the general mechanism
    /// behind that list: a class arm is a claim about vanilla source, while this is an OBSERVATION
    /// about the body in front of the member. Three confirmed hits on one instance that move its HP
    /// by nothing, and the squad stops spending the round on it. The Old Bird (RadMechAI) is NOT left
    /// to this clock: its immunity is a class fact (<c>BlacksiteThreatAdapterRegistry
    /// .EngagementRefusedFor</c>), #874 N8 bounds the rifle's rounds at it with the warning budget
    /// (<see cref="BlacksiteWarningFireMemo"/>), and ObserveDamageAttempt never counts it here - a
    /// give-up would fold it into the weapons exclusion, which also blinds the sight gate, and the
    /// squad would lose sight of the thing it is evading.
    ///
    /// Unity-free ON PURPOSE, like BlacksiteThreatResponse and BlacksiteMonsterSupportLevel beside
    /// it: the decision is a count and a comparison, so it is pinned by the headless suites rather
    /// than only by a live run. The Unity-bound half - which contact this is, when a hit counts as
    /// confirmed, and who is allowed to record one - is
    /// <c>BlacksiteThreatAdapterRegistry.ObserveDamageAttempt</c>, and the memo it owns is HOST
    /// state: damage is applied on the host and the soldier decides on the host, so nothing here is
    /// replicated and no new traffic exists.
    /// </summary>
    internal sealed class BlacksiteFutileDamageMemo
    {
        /// <summary>
        /// Three, because two is a coincidence and three is a pattern the log already showed 25
        /// times on one spore lizard (hp=3->3). It is also cheap to be wrong about in the one
        /// direction that matters: a contact whose HP does move resets its own count on the very
        /// next landed hit, so a monster with an invulnerability WINDOW is never given up on for
        /// having one - only a monster that never takes damage at all runs the count out.
        /// </summary>
        internal const int FutileHitsBeforeGivingUp = 3;

        /// <summary>
        /// Bounded by construction. A round has far fewer live contacts than this, and the cap is
        /// what keeps a long round (or a modded spawner) from turning the memo into a leak. A
        /// COUNTING row is what gets evicted when the cap is reached; a row that has already given
        /// up is a decision the round is holding and is never evicted for a newcomer.
        /// </summary>
        internal const int MaxTrackedContacts = 64;

        private struct Entry
        {
            internal int Hits;
            internal bool GaveUp;
        }

        private readonly Dictionary<ulong, Entry> _entries = new Dictionary<ulong, Entry>();

        /// <summary>Live rows, for the bound's own test.</summary>
        internal int TrackedCount => _entries.Count;

        /// <summary>
        /// The answer <c>WeaponsExcludedFor</c> folds in: this instance has spent its clock and is
        /// treated exactly as a weapons-excluded class for the rest of the round.
        /// </summary>
        internal bool IsExcluded(ulong contact)
            => _entries.TryGetValue(contact, out Entry entry) && entry.GaveUp;

        /// <summary>Confirmed futile hits standing against this instance right now.</summary>
        internal int HitsAgainst(ulong contact)
            => _entries.TryGetValue(contact, out Entry entry) ? entry.Hits : 0;

        /// <summary>
        /// One CONFIRMED hit - our code invoked the vanilla damage entry point on this instance and
        /// read its HP on both sides of the call. Returns true on the ONE call that crosses the
        /// threshold, so the caller's log line is once per instance by construction rather than by
        /// a clock.
        /// </summary>
        internal bool RecordHit(ulong contact, int healthBefore, int healthAfter)
        {
            bool known = _entries.TryGetValue(contact, out Entry entry);
            // Already given up: the decision stands for the round, and a later hit (there should be
            // none - every weapon path asks the predicate first) changes nothing and re-logs nothing.
            if (known && entry.GaveUp) return false;
            if (healthAfter != healthBefore)
            {
                // The weapon works on this body. The count is the count of CONSECUTIVE futile hits,
                // so one that lands resets it and a monster that is merely tough is never dropped.
                if (known && entry.Hits != 0) { entry.Hits = 0; _entries[contact] = entry; }
                return false;
            }
            if (!known)
            {
                if (!TryMakeRoom()) return false;
                entry = new Entry();
            }
            entry.Hits++;
            entry.GaveUp = entry.Hits >= FutileHitsBeforeGivingUp;
            _entries[contact] = entry;
            return entry.GaveUp;
        }

        /// <summary>Round reset. The memo is round state and nothing else clears it.</summary>
        internal void Reset() => _entries.Clear();

        // Deterministic eviction: the row with the fewest hits, lowest key first, and never a row
        // that has given up. Returns false only when every tracked row is a live decision, in which
        // case the newcomer simply is not tracked this round - the squad keeps fighting it, which
        // is the safe direction to fail in.
        private bool TryMakeRoom()
        {
            if (_entries.Count < MaxTrackedContacts) return true;
            bool found = false;
            ulong victim = 0;
            int fewest = int.MaxValue;
            foreach (KeyValuePair<ulong, Entry> row in _entries)
            {
                if (row.Value.GaveUp) continue;
                if (found && (row.Value.Hits > fewest || (row.Value.Hits == fewest && row.Key > victim))) continue;
                found = true;
                victim = row.Key;
                fewest = row.Value.Hits;
            }
            if (!found) return false;
            _entries.Remove(victim);
            return true;
        }
    }
}
