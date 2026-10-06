using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Cross-plugin sink that lets a sibling Y4NGZ plugin (Y4NGZUpgrades) tell the
    /// performance report how much XP an action was worth, so the report can decorate
    /// the note line it already writes for that action with a trailing
    /// <c>" (45 XP)"</c>.
    ///
    /// This is deliberately NOT a line source. Recording an amount never creates a
    /// note: it only annotates a line the report would have written anyway. With zero
    /// registrations the report renders byte-identically to a build without this file,
    /// and an amount whose <see cref="LineKeys"/> token matches no line this round is
    /// silently ignored.
    ///
    /// Contract-level outcomes (ContractCompleted / ContractFailed) are intentionally
    /// out of scope: that XP is crew-wide and the DIRECTIVE box already states the
    /// outcome, so there is no per-player line to decorate.
    ///
    /// Lifetime: record any time between round start and
    /// <c>HUDManager.FillEndGameStats</c>; the report reads the amounts one frame later
    /// when <c>MoonContractRoundStats.BuildPlayerNotes</c> runs. Everything is dropped
    /// on <c>MoonContractRoundStats.Reset</c>, alongside
    /// <see cref="PerformanceReportNoteContributions"/>.
    ///
    /// Sibling plugins bind this by reflection:
    /// <c>Y4NGZCompany.Contracts._Shared.PerformanceReportXpAmounts:RecordAmount</c>
    /// — keep the signature and <see cref="ProtocolVersion"/> stable.
    /// </summary>
    public static class PerformanceReportXpAmounts
    {
        /// <summary>
        /// Bumped only for a breaking change to <see cref="RecordAmount"/>'s signature or
        /// to the meaning of a <see cref="LineKeys"/> token. Adding a new token is additive
        /// and does not bump this: an older producer simply never records the new key.
        /// </summary>
        public const int ProtocolVersion = 1;

        /// <summary>
        /// Stable short tokens naming which note line an amount decorates. A producer that
        /// sends anything else is ignored rather than rejected, so a newer Upgrades against
        /// an older Company degrades to undecorated prose instead of throwing.
        /// </summary>
        public static class LineKeys
        {
            /// <summary>Scrap carried into the ship: "> $206 scrap delivered."</summary>
            public const string ScrapDelivered = "scrap.delivered";

            /// <summary>Scrap first picked up but not landed: "> $206 scrap found."</summary>
            public const string ScrapDiscovered = "scrap.discovered";

            /// <summary>Monsters killed and attributed to this player: "> 2 hostiles killed."</summary>
            public const string CombatKills = "combat.kills";

            /// <summary>The Defuse wire/keypad summary line, clean or with mistakes.</summary>
            public const string DefuseInputs = "defuse.inputs";

            /// <summary>Time spent driving the Payload cart on a successful delivery.</summary>
            public const string PayloadPilot = "payload.pilot";

            /// <summary>The per-contract presence line ("> 2m 37s on Shadow Raid.").</summary>
            public const string ContractPresence = "presence.contract";

            /// <summary>The generic time-inside-the-facility line.</summary>
            public const string InteriorTime = "presence.interior";

            /// <summary>The generic distance-covered line.</summary>
            public const string MovementSteps = "movement.steps";

            /// <summary>
            /// The winning note contributed through <see cref="PerformanceReportNoteContributions"/>.
            /// Company authors no line of its own for things Upgrades tracks (body retrieval,
            /// for instance): the producer contributes the sentence through that seam and
            /// records the amount under this key to have it decorated.
            /// </summary>
            public const string EmployeeRecord = "employee.record";

            /// <summary>
            /// Prefix for the per-sub-objective lines. Build the full key with
            /// <see cref="SubObjective"/> rather than concatenating by hand.
            /// </summary>
            public const string SubObjectivePrefix = "subobjective.";

            /// <summary>
            /// #1201: prefix for a crew award's share - XP every player in the round received for
            /// the same occurrence (<c>crew.subobjective.&lt;kind&gt;</c>). It decorates no line;
            /// the report hides it from both its lines and its remainder while the shift total
            /// still counts it. Additive: an older Company stores the key and ignores it, and the
            /// amount then reads as part of its remainder.
            /// </summary>
            public const string CrewWidePrefix = "crew.";
        }

        // playerIndex -> lineKey -> summed amount. Ordinal-ignore-case so a producer
        // that title-cases a key still lands on the right line.
        private static readonly Dictionary<int, Dictionary<string, int>> AmountsByPlayerIndex =
            new Dictionary<int, Dictionary<string, int>>();

        /// <summary>
        /// The line key for a sub-objective credit. <paramref name="eventKindName"/> is the
        /// <c>ContractProgressionEventKind</c> member name exactly as published (for example
        /// <c>SurveyDronePlaced</c> -> <c>subobjective.surveydroneplaced</c>).
        /// </summary>
        public static string SubObjective(string eventKindName)
        {
            return string.IsNullOrWhiteSpace(eventKindName)
                ? string.Empty
                : LineKeys.SubObjectivePrefix + eventKindName.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Records that <paramref name="amount"/> XP was granted to the player in HUD slot
        /// <paramref name="playerIndex"/> for the action the <paramref name="lineKey"/> line
        /// describes. Repeated calls for the same pair SUM: three drone plants worth 15 XP
        /// each decorate one "&gt; Planted 3 survey beacons" line with "(45 XP)".
        ///
        /// Non-positive amounts, negative slots and blank keys are dropped. Never throws.
        /// </summary>
        public static void RecordAmount(int playerIndex, string lineKey, int amount)
        {
            if (playerIndex < 0 || amount <= 0 || string.IsNullOrWhiteSpace(lineKey))
                return;

            string key = lineKey.Trim().ToLowerInvariant();
            if (!AmountsByPlayerIndex.TryGetValue(playerIndex, out Dictionary<string, int> byKey))
            {
                byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                AmountsByPlayerIndex[playerIndex] = byKey;
            }

            byKey.TryGetValue(key, out int existing);
            // Saturate rather than wrap: a runaway producer should not print a negative bonus.
            long total = (long)existing + amount;
            byKey[key] = total > int.MaxValue ? int.MaxValue : (int)total;
        }

        /// <summary>
        /// Total XP recorded for one line of one player, or 0 when nothing was recorded.
        /// 0 means "render the line exactly as it was authored".
        /// </summary>
        internal static int AmountFor(int playerIndex, string lineKey)
        {
            if (playerIndex < 0 || string.IsNullOrWhiteSpace(lineKey))
                return 0;

            return AmountsByPlayerIndex.TryGetValue(playerIndex, out Dictionary<string, int> byKey)
                   && byKey.TryGetValue(lineKey.Trim(), out int amount)
                ? amount
                : 0;
        }

        /// <summary>
        /// #1201: the XP recorded for one player under <see cref="LineKeys.CrewWidePrefix"/> keys,
        /// i.e. the crew awards the report hides.
        /// </summary>
        internal static int CrewWideAmountFor(int playerIndex)
        {
            if (playerIndex < 0 || !AmountsByPlayerIndex.TryGetValue(playerIndex, out Dictionary<string, int> byKey))
                return 0;

            long total = 0;
            foreach (KeyValuePair<string, int> pair in byKey)
            {
                if (pair.Key.StartsWith(LineKeys.CrewWidePrefix, StringComparison.OrdinalIgnoreCase))
                    total += pair.Value;
            }

            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        /// <summary>
        /// Appends the XP suffix to an already word-limited note line, or returns it
        /// untouched when nothing was recorded for that line.
        ///
        /// The 12-word cap governs the WHOLE rendered line, decoration included: a caller
        /// that decorates must first cap the prose at 12 minus <see cref="FormatSuffix"/>'s
        /// word count, which is what <c>MoonContractRoundStats.AddNoteCandidate</c> does.
        /// This method itself only concatenates, so a caller holding text of unknown length -
        /// a note contributed through <see cref="PerformanceReportNoteContributions"/>, which
        /// applies no word limit of its own - must not skip that step.
        /// </summary>
        internal static string Decorate(string line, int playerIndex, string lineKey)
        {
            if (string.IsNullOrWhiteSpace(line))
                return line;

            int amount = AmountFor(playerIndex, lineKey);
            return amount <= 0 ? line : line + FormatSuffix(amount);
        }

        /// <summary>
        /// The rendered decoration, exposed so the audit can measure it.
        ///
        /// #1199: no '+' sign. The report's Notes font carries no plus glyph, so TMP
        /// rendered one from a fallback atlas as a black character on every XP line.
        /// Parsers accept an optional '+' (Upgrades' <c>ProgressionReportNotePlan.ReadXpAmount</c>),
        /// and callers that strip this suffix measure it through this method, so the
        /// width is never assumed anywhere.
        /// </summary>
        internal static string FormatSuffix(int amount)
        {
            return " (" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " XP)";
        }

        /// <summary>
        /// True when no producer registered anything this round, i.e. the report is
        /// guaranteed to render exactly as it did before this seam existed.
        /// </summary>
        internal static bool IsEmpty => AmountsByPlayerIndex.Count == 0;

        /// <summary>Drops every recorded amount. Called from the round reset.</summary>
        public static void Clear(string reason = null)
        {
            _ = reason;
            AmountsByPlayerIndex.Clear();
        }
    }
}
