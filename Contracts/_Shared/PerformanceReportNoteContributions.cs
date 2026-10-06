using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Extension point letting sibling Y4NGZ plugins put a fact about a player into
    /// the performance report's Notes column instead of drawing their own overlay on
    /// the report canvas.
    ///
    /// Contributions land in a single dedicated note category, so they compete with
    /// each other by priority and fill a leftover slot rather than evicting the
    /// contract facts this repo derives itself. Contribute any time between round
    /// start and <c>HUDManager.FillEndGameStats</c>; the report reads them one frame
    /// later, when <c>MoonContractRoundStats.BuildPlayerNotes</c> runs.
    ///
    /// Sibling plugins bind this by reflection:
    /// <c>Y4NGZCompany.Contracts._Shared.PerformanceReportNoteContributions:Contribute</c>
    /// — keep the signature stable.
    /// </summary>
    internal static class PerformanceReportNoteContributions
    {
        private static readonly Dictionary<int, List<Contribution>> ByPlayerIndex =
            new Dictionary<int, List<Contribution>>();

        /// <summary>
        /// Adds one candidate note line for the given player slot. Higher
        /// <paramref name="priority"/> wins; only the best contribution per player is
        /// ever shown. Text should read as a sentence and start with "&gt; ", matching
        /// the built-in note catalog - the bullet was "* " before #555. The report
        /// re-bullets the leading glyph either way so one slot never mixes two bullets;
        /// the rest of the line is rendered verbatim.
        /// </summary>
        internal static void Contribute(int playerIndex, int priority, string text)
        {
            if (playerIndex < 0 || string.IsNullOrWhiteSpace(text))
                return;

            if (!ByPlayerIndex.TryGetValue(playerIndex, out List<Contribution> list))
            {
                list = new List<Contribution>();
                ByPlayerIndex[playerIndex] = list;
            }

            list.Add(new Contribution(priority, text.Trim()));
        }

        internal static IReadOnlyList<Contribution> ForPlayer(int playerIndex)
        {
            return ByPlayerIndex.TryGetValue(playerIndex, out List<Contribution> list)
                ? (IReadOnlyList<Contribution>)list
                : Array.Empty<Contribution>();
        }

        internal static void Clear(string reason = null)
        {
            _ = reason;
            ByPlayerIndex.Clear();
        }

        internal readonly struct Contribution
        {
            internal readonly int Priority;
            internal readonly string Text;

            internal Contribution(int priority, string text)
            {
                Priority = priority;
                Text = text;
            }
        }
    }
}
