using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>Optional, per-viewer XP snapshot. Replacing a snapshot is idempotent.</summary>
    public static class PerformanceReportXpLedger
    {
        public const int ProtocolVersion = 1;
        private static readonly Dictionary<int, Snapshot> Players = new Dictionary<int, Snapshot>();

        /// <summary>
        /// Replaces one slot's snapshot. #1201: a key under
        /// <see cref="PerformanceReportXpAmounts.LineKeys.CrewWidePrefix"/> is a crew award - XP
        /// every player in the round received for the same occurrence - and becomes part of the
        /// slot's hidden total instead of an entry, so it is neither listed nor counted in the
        /// remainder. Every amount, hidden or not, still has to fit inside <paramref name="total"/>.
        /// </summary>
        public static bool RecordSources(int playerIndex, string[] keys, string[] labels, int[] amounts, int total)
        {
            if (playerIndex < 0 || keys == null || labels == null || amounts == null
                || keys.Length != labels.Length || keys.Length != amounts.Length || total < 0)
                return false;
            var sources = new List<PerformanceReportNoteEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long hidden = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(keys[i]) || amounts[i] <= 0)
                    continue;
                bool crewWide = IsCrewWideKey(keys[i]);
                if ((!crewWide && string.IsNullOrWhiteSpace(labels[i])) || !seen.Add(keys[i]))
                    continue;
                if (crewWide)
                {
                    hidden += amounts[i];
                    continue;
                }
                sources.Add(new PerformanceReportNoteEntry(labels[i], amounts[i],
                    keys[i].StartsWith("contract.", StringComparison.Ordinal) ? 100 : 80));
            }
            if (sources.Sum(source => (long)source.Amount) + hidden > total)
                return false;
            Players[playerIndex] = new Snapshot(sources, total, (int)hidden);
            return true;
        }

        /// <summary>
        /// #1201: the crew-award XP one slot's report hides - the snapshot's crew sources plus the
        /// crew amounts recorded through <see cref="PerformanceReportXpAmounts"/>. Part of the
        /// shift total, never of a line or the remainder. The #1200 Your XP panel reads this.
        /// </summary>
        internal static int HiddenCrewWideXp(int playerIndex)
        {
            long hidden = PerformanceReportXpAmounts.CrewWideAmountFor(playerIndex);
            if (Players.TryGetValue(playerIndex, out Snapshot snapshot))
                hidden += snapshot.Hidden;
            return hidden > int.MaxValue ? int.MaxValue : (int)hidden;
        }

        private static bool IsCrewWideKey(string key)
        {
            return key.StartsWith(PerformanceReportXpAmounts.LineKeys.CrewWidePrefix, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasPlayer(int playerIndex) => Players.ContainsKey(playerIndex);
        public static void Clear(string reason = null) => Players.Clear();

        /// <summary>
        /// #1203: the debug report preview writes sample snapshots through
        /// <see cref="RecordSources"/> and must hand back exactly what was there before, so a
        /// real round afterwards sees none of it. Snapshots are immutable, so a shallow copy
        /// of the per-slot map is a complete save.
        /// </summary>
        internal static SavedState Save() => new SavedState(new Dictionary<int, Snapshot>(Players));

        internal static void Restore(SavedState state)
        {
            Players.Clear();
            if (state == null)
                return;
            foreach (KeyValuePair<int, Snapshot> pair in state.Players)
                Players[pair.Key] = pair.Value;
        }

        internal sealed class SavedState
        {
            internal readonly IReadOnlyDictionary<int, Snapshot> Players;
            internal SavedState(IReadOnlyDictionary<int, Snapshot> players) { Players = players; }
        }

        /// <summary>
        /// #1200: one slot's Notes row. The survival fact - a Coroner cause of death, or else
        /// Company's own death or disconnection line - always stays and comes first; after it
        /// come the <paramref name="personalLines"/> highest-ranked other lines. There is no
        /// remainder line: the local player's full account is the Your XP panel, and a remote
        /// slot shows its top lines only.
        /// </summary>
        internal static IReadOnlyList<PerformanceReportNoteEntry> ComposeRow(
            int playerIndex, IEnumerable<PerformanceReportNoteEntry> companyNotes, string foreignNote, int personalLines)
        {
            if (!Players.TryGetValue(playerIndex, out Snapshot snapshot))
                return Array.Empty<PerformanceReportNoteEntry>();

            var candidates = new List<PerformanceReportNoteEntry>();
            // Coroner's final body line carries the cause, not its heading. Ignore our own
            // old tagged XP lines captured during a legacy producer's initial pass.
            string foreign = (foreignNote ?? string.Empty).Replace("\r", "").Split('\n')
                .Select(line => line.Trim())
                .LastOrDefault(line => line.Length > 0 && !line.EndsWith(":", StringComparison.Ordinal)
                    && !line.Contains("XP)</color>"));
            bool hasCause = !string.IsNullOrEmpty(foreign) && (foreignNote ?? string.Empty).IndexOf("Cause of Death", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!string.IsNullOrEmpty(foreign))
                candidates.Add(new PerformanceReportNoteEntry(foreign, 0, hasCause ? PerformanceReportNoteEntry.ForeignCausePriority : 50));
            if (companyNotes != null)
                candidates.AddRange(companyNotes.Where(note => !hasCause || !note.IsSurvivalFact));
            candidates.AddRange(snapshot.Sources);

            var ranked = candidates.OrderByDescending(note => note.Priority)
                .ThenByDescending(note => note.Amount).ToList();
            var row = ranked.Where(note => note.IsSurvivalFact).Take(1).ToList();
            row.AddRange(ranked.Where(note => !note.IsSurvivalFact).Take(Math.Max(0, personalLines)));
            return row;
        }

        /// <summary>
        /// #1200: how many of <paramref name="wanted"/> one-line entries still fit on a Notes
        /// row under its survival fact. The fact is never shortened, so it takes its whole
        /// wrapped height (<paramref name="survivalFactHeight"/>, the "Notes:" header included)
        /// first and the XP lines give way: a cause that fills the row leaves none. All three
        /// heights are measured at the Notes' largest size.
        /// </summary>
        internal static int PersonalLinesUnderSurvivalFact(
            float availableHeight, float survivalFactHeight, float lineHeight, int wanted)
        {
            // Half a unit of float noise must not cost a line that fits.
            const float Tolerance = 0.5f;
            if (wanted <= 0 || lineHeight <= 0f)
                return 0;
            float left = availableHeight - survivalFactHeight + Tolerance;
            return left < lineHeight ? 0 : Math.Min(wanted, (int)Math.Floor(left / lineHeight));
        }

        /// <summary>
        /// #1200: every source the Your XP panel may list for one slot - the snapshot's visible
        /// sources (crew awards were kept out by <see cref="RecordSources"/>) and Company's paid
        /// act lines - with the snapshot's total and the slot's hidden crew-wide XP. False when
        /// the slot has no snapshot.
        /// </summary>
        internal static bool TryGetPanelSources(
            int playerIndex,
            IEnumerable<PerformanceReportNoteEntry> companyNotes,
            out List<PerformanceReportNoteEntry> sources,
            out int total,
            out int hidden)
        {
            sources = null;
            total = 0;
            hidden = 0;
            if (!Players.TryGetValue(playerIndex, out Snapshot snapshot))
                return false;

            sources = new List<PerformanceReportNoteEntry>();
            if (companyNotes != null)
                sources.AddRange(companyNotes.Where(note => note.Amount > 0 && !note.IsSurvivalFact));
            sources.AddRange(snapshot.Sources);
            total = snapshot.Total;
            hidden = HiddenCrewWideXp(playerIndex);
            return true;
        }

        internal static string FitLine(PerformanceReportNoteEntry entry, float width, Func<string, float> measure)
        {
            string rendered = entry.Render();
            if (measure(rendered) <= width) return rendered;
            string label = entry.Label;
            while (label.Length > 1)
            {
                label = label.Substring(0, label.Length - 1).TrimEnd();
                rendered = entry.Render(label + "...");
                if (measure(rendered) <= width) return rendered;
            }
            return entry.Amount > 0 ? entry.Render("XP") : rendered;
        }

        internal sealed class Snapshot
        {
            internal readonly IReadOnlyList<PerformanceReportNoteEntry> Sources;
            internal readonly int Total;
            /// <summary>#1201: crew-award XP inside <see cref="Total"/> that no line or remainder shows.</summary>
            internal readonly int Hidden;
            internal Snapshot(IReadOnlyList<PerformanceReportNoteEntry> sources, int total, int hidden)
            { Sources = sources; Total = total; Hidden = hidden; }
        }
    }

    internal readonly struct PerformanceReportNoteEntry
    {
        internal readonly string Label;
        internal readonly int Amount;
        internal readonly int Priority;
        internal PerformanceReportNoteEntry(string label, int amount, int priority)
        {
            string plain = System.Net.WebUtility.HtmlDecode(
                System.Text.RegularExpressions.Regex.Replace(label ?? string.Empty, "<[^>]*>", string.Empty));
            Label = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ")
                .Trim().TrimStart('>', '*', ' ').TrimEnd('.');
            Amount = Math.Max(0, amount);
            Priority = priority;
        }

        /// <summary>Company's death and disconnection lines (<c>BuildXpNoteCandidates</c>).</summary>
        internal const int SurvivalFactPriority = 900;
        /// <summary>A Coroner cause of death, which replaces Company's generic death line.</summary>
        internal const int ForeignCausePriority = 1000;

        /// <summary>
        /// #1200: a death, a disconnection or a cause of death. It stays on the Notes row
        /// whatever the line budget and is never shortened.
        /// </summary>
        internal bool IsSurvivalFact => Priority >= SurvivalFactPriority;
        // #1199: no '+'. The report Notes font has no plus glyph, so TMP drew it from a
        // fallback atlas as a black character. The amount reads " (45 XP)".
        internal string Suffix => Amount > 0 ? " (" + Amount.ToString(CultureInfo.InvariantCulture) + " XP)" : string.Empty;
        internal string Render(string label = null) => "> " + (label ?? Label) + Suffix;
    }
}
