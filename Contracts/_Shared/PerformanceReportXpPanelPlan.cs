using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1200: the Unity-free half of the performance report's "YOUR XP" panel - which sources it
    /// lists, in what order, what collapses into "Other work" when the panel runs out of height,
    /// and how one label wraps. The report measures text and space and hands the numbers in, so
    /// every decision here is covered by <c>tests/PerformanceReportRegression</c> without a game.
    ///
    /// The totals reconcile as: listed amounts + Other work + hidden crew-wide XP = Shift total,
    /// where the Shift total is the snapshot's awarded XP, the same figure the XP bar adds. The
    /// one exception is a producer whose listed amounts already exceed the total minus the hidden
    /// XP: Other work is then 0 rather than negative, exactly as the Notes remainder always was.
    /// </summary>
    internal static class PerformanceReportXpPanelPlanner
    {
        /// <summary>The remainder's words, shared with Upgrades' own remainder line (#1202).</summary>
        internal const string OtherWorkLabel = "Other work";
        internal const string ShiftTotalLabel = "Shift total";
        internal const string Ellipsis = "...";

        // A few hundredths of a unit of float noise must not collapse an entry that fits.
        private const float HeightTolerance = 0.5f;

        /// <summary>
        /// The panel's reading order: every paid source, highest amount first. Lawson asked for
        /// personal lines first and lines credited to several players after them; nothing a
        /// snapshot or an act line carries can tell those apart - crew awards, the one kind of
        /// shared credit the data marks, never reach this list - so the order is by amount, with
        /// the notes' own priority (paid act, contract, other) breaking a tie.
        /// </summary>
        internal static List<PerformanceReportNoteEntry> Order(IEnumerable<PerformanceReportNoteEntry> sources)
        {
            return (sources ?? Array.Empty<PerformanceReportNoteEntry>())
                .Where(source => source.Amount > 0)
                .OrderByDescending(source => source.Amount)
                .ThenByDescending(source => source.Priority)
                .ToList();
        }

        /// <summary>
        /// Orders <paramref name="sources"/> and drops the lowest-value entries into one Other
        /// work line until the entries fit <paramref name="availableHeight"/>. Other work also
        /// carries whatever the snapshot's total holds that no source names; hidden crew-wide XP
        /// is never listed and never part of Other work.
        /// </summary>
        internal static PerformanceReportXpPanelPlan Plan(
            IEnumerable<PerformanceReportNoteEntry> sources,
            int total,
            int hidden,
            Func<PerformanceReportNoteEntry, float> entryHeight,
            float availableHeight)
        {
            List<PerformanceReportNoteEntry> listed = Order(sources);
            long owed = Math.Max(0L, (long)Math.Max(0, total) - Math.Max(0, hidden));
            var heights = new float[listed.Count];
            float used = 0f;
            long shown = 0;
            for (int i = 0; i < listed.Count; i++)
            {
                heights[i] = entryHeight(listed[i]);
                used += heights[i];
                shown += listed[i].Amount;
            }

            while (true)
            {
                int other = (int)Math.Max(0L, owed - shown);
                float otherHeight = other > 0 ? entryHeight(OtherWork(other)) : 0f;
                if (listed.Count == 0 || used + otherHeight <= availableHeight + HeightTolerance)
                    return new PerformanceReportXpPanelPlan(listed, other, total, hidden);

                int last = listed.Count - 1;
                used -= heights[last];
                shown -= listed[last].Amount;
                listed.RemoveAt(last);
            }
        }

        internal static PerformanceReportNoteEntry OtherWork(int amount)
        {
            return new PerformanceReportNoteEntry(OtherWorkLabel, amount, 0);
        }

        /// <summary>The amount column's text: "45 XP". No sign - the report font has no '+' (#1199).</summary>
        internal static string FormatAmount(int amount)
        {
            return Math.Max(0, amount).ToString(CultureInfo.InvariantCulture) + " XP";
        }

        /// <summary>
        /// Wraps one label to at most two lines of <paramref name="width"/>, breaking between
        /// words, and inside a word only when the word alone is wider than a line. "..." appears
        /// only when the label would need a third line: the second line then keeps what fits
        /// with the ellipsis.
        /// </summary>
        internal static PerformanceReportXpPanelLabel Wrap(string label, float width, Func<string, float> measure)
        {
            string text = (label ?? string.Empty).Trim();
            if (text.Length == 0 || measure(text) <= width)
                return new PerformanceReportXpPanelLabel(text, null, truncated: false);

            string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string first = string.Empty;
            int taken = 0;
            while (taken < words.Length)
            {
                string candidate = taken == 0 ? words[0] : first + " " + words[taken];
                if (measure(candidate) > width)
                    break;
                first = candidate;
                taken++;
            }

            string rest;
            if (taken == 0)
            {
                int cut = LongestFittingPrefix(words[0], width, measure);
                first = words[0].Substring(0, cut);
                rest = (words[0].Substring(cut) + " " + string.Join(" ", words, 1, words.Length - 1)).Trim();
            }
            else
            {
                rest = string.Join(" ", words, taken, words.Length - taken);
            }

            if (measure(rest) <= width)
                return new PerformanceReportXpPanelLabel(first, rest, truncated: false);

            string second = rest;
            while (second.Length > 0 && measure(second.TrimEnd() + Ellipsis) > width)
                second = second.Substring(0, second.Length - 1);
            return new PerformanceReportXpPanelLabel(first, second.TrimEnd() + Ellipsis, truncated: true);
        }

        // At least one character, so a line never comes out empty however narrow the column.
        private static int LongestFittingPrefix(string word, float width, Func<string, float> measure)
        {
            for (int length = word.Length - 1; length > 1; length--)
            {
                if (measure(word.Substring(0, length)) <= width)
                    return length;
            }

            return 1;
        }
    }

    /// <summary>#1200: what the Your XP panel shows, top to bottom, above its Shift total line.</summary>
    internal sealed class PerformanceReportXpPanelPlan
    {
        /// <summary>The sources listed one per entry, highest amount first.</summary>
        internal readonly IReadOnlyList<PerformanceReportNoteEntry> Listed;
        /// <summary>The collapsed and unnamed remainder; 0 when nothing is left over.</summary>
        internal readonly int OtherWork;
        /// <summary>The Shift total: the snapshot's awarded XP, hidden crew-wide XP included.</summary>
        internal readonly int Total;
        /// <summary>Crew-wide XP inside <see cref="Total"/> that no entry shows.</summary>
        internal readonly int Hidden;

        internal PerformanceReportXpPanelPlan(IReadOnlyList<PerformanceReportNoteEntry> listed, int otherWork, int total, int hidden)
        {
            Listed = listed;
            OtherWork = otherWork;
            Total = total;
            Hidden = hidden;
        }

        /// <summary>Every entry the panel draws: the listed sources, then Other work when it is owed.</summary>
        internal IEnumerable<PerformanceReportNoteEntry> Entries
        {
            get
            {
                foreach (PerformanceReportNoteEntry entry in Listed)
                    yield return entry;
                if (OtherWork > 0)
                    yield return PerformanceReportXpPanelPlanner.OtherWork(OtherWork);
            }
        }
    }

    /// <summary>#1200: one wrapped panel label, one or two lines.</summary>
    internal readonly struct PerformanceReportXpPanelLabel
    {
        internal readonly string First;
        /// <summary>Null for a one-line label.</summary>
        internal readonly string Second;
        /// <summary>True when the label needed a third line and the second ends in "...".</summary>
        internal readonly bool Truncated;

        internal PerformanceReportXpPanelLabel(string first, string second, bool truncated)
        {
            First = first ?? string.Empty;
            Second = second;
            Truncated = truncated;
        }

        internal int LineCount => Second == null ? 1 : 2;
        internal string Text => Second == null ? First : First + "\n" + Second;
    }
}
