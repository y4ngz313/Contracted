using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Contracts._Shared
{
    internal readonly struct IndexedPerformanceNote
    {
        internal readonly int PlayerIndex;
        internal readonly string Text;

        internal IndexedPerformanceNote(int playerIndex, string text)
        {
            PlayerIndex = playerIndex;
            Text = text;
        }
    }

    internal static class PerformanceReportNoteSlots
    {
        internal static IReadOnlyList<string> Build(
            int minimumSlotCount,
            IEnumerable<IndexedPerformanceNote> indexedNotes)
        {
            int slotCount = Math.Max(0, minimumSlotCount);
            var notes = indexedNotes != null
                ? new List<IndexedPerformanceNote>(indexedNotes)
                : new List<IndexedPerformanceNote>();

            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i].PlayerIndex >= 0)
                    slotCount = Math.Max(slotCount, notes[i].PlayerIndex + 1);
            }

            var slots = new string[slotCount];
            for (int i = 0; i < notes.Count; i++)
            {
                IndexedPerformanceNote note = notes[i];
                if (note.PlayerIndex < 0 || note.PlayerIndex >= slots.Length)
                    continue;

                slots[note.PlayerIndex] = note.Text ?? string.Empty;
            }

            return slots;
        }

        private const string HudHeader = "Notes: \n";

        internal static string FormatHudText(string note)
        {
            return string.IsNullOrWhiteSpace(note)
                ? HudHeader
                : HudHeader + note.Trim();
        }

        /// <summary>
        /// Composes our note with whatever another mod already wrote into the same slot —
        /// Coroner puts its cause of death there. Both lines live under one "Notes:" header,
        /// ours first, so the slot reads as one note rather than two competing ones.
        /// </summary>
        internal static string FormatHudText(string note, string foreignNote)
        {
            string body = StripHudHeader(foreignNote);
            if (string.IsNullOrWhiteSpace(body))
                return FormatHudText(note);

            return string.IsNullOrWhiteSpace(note)
                ? HudHeader + body
                : HudHeader + note.Trim() + "\n" + body;
        }

        /// <summary>
        /// Returns a slot's text without the "Notes:" header, or empty when there is no body.
        /// The header is vanilla's, so anything that rewrote the slot in place still carries it.
        /// </summary>
        internal static string StripHudHeader(string slotText)
        {
            if (string.IsNullOrWhiteSpace(slotText))
                return string.Empty;

            string trimmed = slotText.Trim();
            int newline = trimmed.IndexOf('\n');
            // #654: vanilla seeds each slot with a bare "Notes: " header and no body. After
            // Trim that is a single line with no newline, and treating it as body text made
            // FormatHudText stack a second header on top of it.
            if (newline < 0)
            {
                return trimmed.EndsWith("Notes:", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : trimmed;
            }

            if (trimmed.Substring(0, newline).TrimEnd().EndsWith("Notes:", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(newline + 1);

            return trimmed.Trim();
        }
    }
}
