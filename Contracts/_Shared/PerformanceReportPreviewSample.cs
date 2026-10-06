using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>#1203: which bottom row the debug report preview renders.</summary>
    internal enum PerformanceReportPreviewVariant
    {
        /// <summary>SCRAP, CONTRACT, HAZARD PAY, CASUALTIES, REVISED QUOTA, GRADE.</summary>
        SixCells,
        /// <summary>The same row without HAZARD PAY.</summary>
        FiveCells,
    }

    /// <summary>The vanilla crew-state icon a preview row shows.</summary>
    internal enum PerformanceReportPreviewPlayerState
    {
        Alive,
        Dead,
        Missing,
    }

    internal sealed class PerformanceReportPreviewRow
    {
        internal int Slot;
        internal string Name;
        internal PerformanceReportPreviewPlayerState State;
        /// <summary>What vanilla, and Coroner after it, left in the slot before the report pass.</summary>
        internal string VanillaNotes;
        /// <summary>The note block Company composes for the slot (the report's PlayerNotes entry).</summary>
        internal string CompanyNote;
        /// <summary>Stands in for Company's round-stat XP note candidates on a slot with a ledger.</summary>
        internal IReadOnlyList<PerformanceReportNoteEntry> XpNoteCandidates = Array.Empty<PerformanceReportNoteEntry>();
        /// <summary>A protocol-1 ledger snapshot, or null keys when the slot has none.</summary>
        internal string[] SourceKeys;
        internal string[] SourceLabels;
        internal int[] SourceAmounts;
        internal int SourceTotal;

        internal bool HasLedger => SourceKeys != null;
    }

    /// <summary>
    /// #1203: the sample a debug-only preview renders through the real performance report,
    /// so a layout change can be checked in seconds rather than a full round. Pure data - no
    /// Unity and no shared state - so it is covered by the report regression tests; the
    /// in-game half lives in <c>PerformanceReportPreview</c>.
    ///
    /// The sample exercises what layout work on the report needs: a contract title, four
    /// named crew rows, Notes with XP amounts, a Coroner-style cause of death on a dead crew
    /// member, one slot (the local player's, as in a real round) whose XP ledger fills the
    /// #1200 Your XP panel - ten entries with long labels, a hidden crew award and an Other
    /// work remainder - and a bottom row with or without HAZARD PAY. The
    /// revised quota rises in one variant and falls in the other, so both arrows are seen.
    /// </summary>
    internal sealed class PerformanceReportPreviewSample
    {
        internal const int MaxRows = 4;
        internal const string CoronerNotes = "Notes: \nCause of Death:\n* Mauled by a Bracken.";
        private const string VanillaNotesHeader = "Notes: \n";

        internal PerformanceReportPreviewVariant Variant;
        internal string ContractName;
        internal int DirectivePercent;
        internal bool HazardPayApplied;
        internal string HazardPayWeather;
        internal int HazardPayPercent;
        internal int PlayersDead;
        internal float CrewLossPenaltyFraction;
        internal int QuotaBeforeModifiers;
        internal int RevisedQuota;
        internal int ScrapCollected;
        internal int ScrapInLevel;
        internal string Grade;
        /// <summary>The slot carrying the ledger; the local player's slot when it fits.</summary>
        internal int XpSlot;
        internal IReadOnlyList<PerformanceReportPreviewRow> Rows;

        /// <summary>
        /// Builds the sample for a report with <paramref name="slotCount"/> vanilla crew slots.
        /// The ledger lands on <paramref name="localSlot"/> (named <paramref name="localName"/>)
        /// when that slot is one of the sample rows, and on slot 0 otherwise.
        /// </summary>
        internal static PerformanceReportPreviewSample Build(
            PerformanceReportPreviewVariant variant,
            int slotCount,
            int localSlot,
            string localName)
        {
            int rowCount = Math.Max(0, Math.Min(MaxRows, slotCount));
            int xpSlot = localSlot >= 0 && localSlot < rowCount ? localSlot : 0;
            bool hazardPay = variant == PerformanceReportPreviewVariant.SixCells;
            const int directivePercent = 20;
            const int hazardPayPercent = 10;
            const float crewLossPenaltyPerPlayer = 0.12f;
            const int quotaBefore = 1200;

            var templates = new List<PerformanceReportPreviewRow>
            {
                BuildLedgerRow(string.IsNullOrWhiteSpace(localName) ? "Employee" : localName.Trim()),
                BuildCoronerRow(),
                BuildNotesRow(),
                BuildMissingRow(),
            };

            var rows = new List<PerformanceReportPreviewRow>(rowCount);
            int nextTemplate = 1;
            for (int slot = 0; slot < rowCount; slot++)
            {
                PerformanceReportPreviewRow row = slot == xpSlot ? templates[0] : templates[nextTemplate++];
                row.Slot = slot;
                rows.Add(row);
            }

            int playersDead = 0;
            foreach (PerformanceReportPreviewRow row in rows)
            {
                if (row.State != PerformanceReportPreviewPlayerState.Alive)
                    playersDead++;
            }

            // Directive and hazard pay lower the quota, casualties raise it. With the full four
            // rows (two dead) that is 1200 * 0.94 with hazard pay and 1200 * 1.04 without, so
            // the two variants' revised-quota arrows point opposite ways.
            float crewLossPenaltyFraction = playersDead * crewLossPenaltyPerPlayer;
            float factor = 1f - directivePercent / 100f - (hazardPay ? hazardPayPercent / 100f : 0f) + crewLossPenaltyFraction;

            return new PerformanceReportPreviewSample
            {
                Variant = variant,
                ContractName = "Containment Breach",
                DirectivePercent = directivePercent,
                HazardPayApplied = hazardPay,
                HazardPayWeather = hazardPay ? "Eclipsed" : string.Empty,
                HazardPayPercent = hazardPay ? hazardPayPercent : 0,
                PlayersDead = playersDead,
                CrewLossPenaltyFraction = crewLossPenaltyFraction,
                QuotaBeforeModifiers = quotaBefore,
                RevisedQuota = (int)Math.Round(quotaBefore * factor),
                ScrapCollected = 1284,
                ScrapInLevel = 1690,
                Grade = hazardPay ? "A" : "B",
                XpSlot = xpSlot,
                Rows = rows,
            };
        }

        /// <summary>The report's PlayerNotes, one entry per vanilla slot.</summary>
        internal string[] BuildPlayerNotes(int slotCount)
        {
            var notes = new string[Math.Max(0, slotCount)];
            foreach (PerformanceReportPreviewRow row in Rows)
            {
                if (row.Slot >= 0 && row.Slot < notes.Length)
                    notes[row.Slot] = row.CompanyNote;
            }

            return notes;
        }

        // The local player's slot, as a real round leaves it: Upgrades' snapshot with nine
        // visible sources, one crew award it hides (#1201) and an unlisted remainder, plus a
        // paid Company act line. The #1200 Your XP panel then has ten entries, an "Other work"
        // line and a Shift total above what it lists; the Notes row keeps its top line. Three
        // labels are long enough to wrap, and one to need the "..." on its second line.
        private static PerformanceReportPreviewRow BuildLedgerRow(string name)
        {
            string[] keys =
            {
                "contract.completion",
                "subobjective.containmentbreachwavesurvived",
                "scrap.delivered",
                "combat.kills",
                "employee.record",
                "presence.interior",
                "rank.streak",
                "movement.steps",
                "scrap.discovered",
                PerformanceReportXpAmounts.LineKeys.CrewWidePrefix + "subobjective.containmentbreachwavesurvived",
            };
            string[] labels =
            {
                "Containment Breach completed",
                "Survived the third and final containment breach wave",
                "$1,284 scrap delivered to the Company ship",
                "3 hostiles killed",
                "Recovered a crewmate's body from the lower maintenance tunnels behind the reactor",
                "8m 12s inside the facility",
                "Five days in a row without losing a single crew member",
                "2,140 steps covered",
                "$406 scrap found",
                "Crew share of a breach wave",
            };
            int[] amounts = { 120, 45, 80, 36, 25, 18, 15, 12, 10, 30 };
            // Company's act line holds its own amount; the snapshot total includes it.
            var cracked = new PerformanceReportNoteEntry("Cracked the container", 30, 110);
            int total = 7 + cracked.Amount;
            for (int i = 0; i < amounts.Length; i++)
                total += amounts[i];

            var hardDrive = new PerformanceReportNoteEntry("Recovered the hard drive", 0, 40);
            return new PerformanceReportPreviewRow
            {
                Name = name,
                State = PerformanceReportPreviewPlayerState.Alive,
                VanillaNotes = VanillaNotesHeader,
                // Replaced by the composed XP notes once the ledger is applied; it is also what
                // captures the slot so the report can restore it.
                CompanyNote = NoteLine("$1,284 scrap delivered.", 80),
                XpNoteCandidates = new[] { cracked, hardDrive },
                SourceKeys = keys,
                SourceLabels = labels,
                SourceAmounts = amounts,
                SourceTotal = total,
            };
        }

        // A remote crew member killed with Coroner installed: Company's own note first, then
        // Coroner's heading and cause, under one "Notes:" header.
        private static PerformanceReportPreviewRow BuildCoronerRow()
        {
            return new PerformanceReportPreviewRow
            {
                Name = "Mira Castellanos-Whitfield",
                State = PerformanceReportPreviewPlayerState.Dead,
                VanillaNotes = CoronerNotes,
                CompanyNote = NoteLine("Died to Bracken inside.", 0),
            };
        }

        // A remote crew member with a full three-line block of decorated notes.
        private static PerformanceReportPreviewRow BuildNotesRow()
        {
            return new PerformanceReportPreviewRow
            {
                Name = "Dex",
                State = PerformanceReportPreviewPlayerState.Alive,
                VanillaNotes = VanillaNotesHeader,
                CompanyNote = string.Join("\n",
                    NoteLine("$206 scrap delivered.", 45),
                    NoteLine("Planted 3 survey beacons", 45),
                    NoteLine("2m 37s through the breach.", 0)),
            };
        }

        // A crew member left behind: vanilla's missing icon and an undecorated survival line.
        private static PerformanceReportPreviewRow BuildMissingRow()
        {
            return new PerformanceReportPreviewRow
            {
                Name = "xX_NightShiftJanitor_Xx",
                State = PerformanceReportPreviewPlayerState.Missing,
                VanillaNotes = VanillaNotesHeader,
                CompanyNote = NoteLine("Died outside; cause unknown.", 0),
            };
        }

        // One line as MoonContractRoundStats authors it: bullet, catalog stem, and the XP
        // suffix when an amount was recorded for it.
        private static string NoteLine(string text, int amount)
        {
            return "> " + text + (amount > 0 ? PerformanceReportXpAmounts.FormatSuffix(amount) : string.Empty);
        }
    }
}
