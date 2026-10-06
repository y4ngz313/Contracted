using System;
using System.Collections.Generic;
using System.Linq;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Presentation-only contract data for one moon. Aliases are the names that terminal
    /// implementations may render for the same <c>SelectableLevel</c>; the contract was
    /// already resolved from that level before this transport object was created.
    /// </summary>
    internal sealed class MoonContractCatalogueLabel
    {
        internal MoonContractCatalogueLabel(string contractName, string shortName, IEnumerable<string> aliases)
        {
            ContractName = contractName?.Trim() ?? string.Empty;
            ShortName = string.IsNullOrWhiteSpace(shortName) ? ContractName : shortName.Trim();
            Aliases = aliases?
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
        }

        /// <summary>The full contract name; identifies the assignment.</summary>
        internal string ContractName { get; }
        /// <summary>Lawson's short name, the only name a catalogue tag shows (#874).</summary>
        internal string ShortName { get; }
        internal IReadOnlyList<string> Aliases { get; }
    }

    /// <summary>One line of the <c>contracts</c> command, already resolved from its level.</summary>
    internal readonly struct MoonContractCommandRow
    {
        internal MoonContractCommandRow(string moonName, string contractName)
        {
            MoonName = moonName;
            ContractName = contractName;
        }

        internal string MoonName { get; }
        internal string ContractName { get; }
    }

    /// <summary>
    /// Adds contract labels to already-rendered moon rows by moon name, never by row index.
    /// This lets vanilla, LLL, General Improvements, TerminalUtils, and TerminalFormatter
    /// reorder or filter the catalogue without attaching a contract to the wrong moon.
    /// </summary>
    internal static class MoonContractCatalogueFormatter
    {
        /// <summary>
        /// Colour for a contract tag on the vanilla terminal. The vanilla body text is
        /// green, so a green tag reads as one more weather note; cyan separates by hue and
        /// by luminance, which keeps it distinct under red-green colour blindness too. It
        /// is the same accent the ship monitor already uses for contract text.
        /// </summary>
        internal const string ContractTagColor = "#00D7E6";

        /// <summary>
        /// Brackets rather than parentheses, because vanilla's own weather tag is
        /// parenthesised and the two were indistinguishable. Only the appended tag is
        /// decorated: the moon name itself is left byte-for-byte intact, so other terminal
        /// mods that re-parse the catalogue by moon name still match it.
        /// </summary>
        internal static string FormatContractTag(string contractName)
        {
            return "<color=" + ContractTagColor + ">[" + contractName + "]</color>";
        }

        /// <summary>
        /// The full-width contract channel shared by the Moons Plus footer and the
        /// <c>contracts</c> command: <c>Contract: &lt;full display name&gt; — &lt;moon&gt;</c>.
        /// Plain text without colour tags so each surface decides its own styling. Returns
        /// null when either name is blank, so callers never print an empty contract.
        /// </summary>
        internal static string FormatContractLine(string contractDisplayName, string moonName)
        {
            if (string.IsNullOrWhiteSpace(contractDisplayName) || string.IsNullOrWhiteSpace(moonName))
                return null;
            return "Contract: " + contractDisplayName.Trim() + " \u2014 " + moonName.Trim();
        }

        /// <summary>
        /// The body of the renderer-independent <c>contracts</c> command: one
        /// <see cref="FormatContractLine"/> per moon, in the order given. Rows without a
        /// moon or contract name are skipped rather than printed half-empty.
        /// </summary>
        internal static string FormatContractsCommand(IEnumerable<MoonContractCommandRow> rows)
        {
            var lines = new List<string>();
            if (rows != null)
            {
                foreach (MoonContractCommandRow row in rows)
                {
                    string line = FormatContractLine(row.ContractName, row.MoonName);
                    if (line != null) lines.Add(line);
                }
            }

            string body = lines.Count > 0
                ? string.Join("\n", lines)
                : "No moon contracts are listed right now.";
            return "\nCONTRACTS\n\n" + body + "\n\n";
        }

        /// <summary>
        /// The catalogue spelling of a moon: <c>41 Experimentation</c> reads
        /// <c>Experimentation</c>. Only a purely numeric first word is dropped.
        /// </summary>
        internal static string NumberlessMoonName(string planetName)
        {
            if (string.IsNullOrWhiteSpace(planetName))
                return string.Empty;

            string trimmed = planetName.Trim();
            int firstSpace = trimmed.IndexOf(' ');
            if (firstSpace > 0 && trimmed.Substring(0, firstSpace).All(char.IsDigit))
                trimmed = trimmed.Substring(firstSpace + 1).Trim();
            return trimmed;
        }

        /// <summary>
        /// Labels the catalogue with short names and lays it out in measured columns. A page
        /// that cannot fit them uses the detail-line format, so no moon row exceeds the width.
        /// </summary>
        internal static string AddArrangedLabels(string text, IReadOnlyCollection<MoonContractCatalogueLabel> labels,
            float availableWidth, Func<string, float> measure)
            => TerminalCompatibility.MoonCatalogueColumns.Arrange(Label(text, labels, false), availableWidth, measure);

        internal static string AddLabels(string text, IEnumerable<MoonContractCatalogueLabel> labels,
            bool detailRows = false)
            => Label(text, labels, detailRows);

        private static string Label(string text, IEnumerable<MoonContractCatalogueLabel> labels, bool detailRows)
        {
            if (string.IsNullOrEmpty(text) || labels == null)
                return text;

            text = TerminalCompatibility.MoonCatalogueColumns.RemoveLayout(text);
            List<AliasCandidate> candidates = BuildCandidates(labels);
            int NameEnd(string line)
            {
                int start = FindMoonNameStart(line);
                foreach (AliasCandidate candidate in candidates)
                {
                    int at = FindAliasAtMoonName(line, start, candidate.Alias);
                    if (at >= 0) return at + candidate.Alias.Length;
                }
                return -1;
            }
            if (detailRows || TerminalCompatibility.MoonCatalogueDetailRow.HasDetails(text)
                || TerminalCompatibility.MoonCatalogueColumns.RequiresDetails(text, NameEnd))
            {
                text = TerminalCompatibility.MoonCatalogueColumns.PromoteToDetails(text);
                return TerminalCompatibility.MoonCatalogueDetailRow.Annotate(text, line =>
                {
                    int start = FindMoonNameStart(line);
                    foreach (AliasCandidate candidate in candidates)
                    {
                        if (FindAliasAtMoonName(line, start, candidate.Alias) >= 0)
                            return candidate.Label.ShortName;
                    }
                    return null;
                }, contract: true);
            }

            if (candidates.Count == 0)
                return text;

            string[] lines = text.Split('\n');
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int moonNameStart = FindMoonNameStart(line);
                if (moonNameStart < 0)
                    continue;

                foreach (AliasCandidate candidate in candidates)
                {
                    int aliasIndex = FindAliasAtMoonName(line, moonNameStart, candidate.Alias);
                    if (aliasIndex < 0)
                        continue;

                    int insertAt = aliasIndex + candidate.Alias.Length;
                    // A cached catalogue may already contain last orbit's assignment. Remove
                    // only our colour-tagged annotations; weather and other mods' tags survive.
                    string clean = RemovePreviousLabels(line, insertAt);
                    string name = candidate.Label.ShortName;
                    lines[i] = InsertPreservingColumns(clean, insertAt, " " + FormatContractTag(name), name);
                    if (lines[i] == line) break;
                    changed = true;
                    break;
                }
            }

            return changed ? string.Join("\n", lines) : text;
        }

        /// <summary>
        /// A run of two or more spaces directly after the moon name is column padding from a
        /// catalogue that aligns credits/risk columns with spaces (TerminalFormatter,
        /// TerminalUtils); inserting the tag without consuming padding would push those
        /// columns right by the tag's visible width. Vanilla separates the name from its
        /// weather note with a single space, so vanilla rows are left untouched.
        /// </summary>
        private static string InsertPreservingColumns(
            string line,
            int insertAt,
            string annotation,
            string contractName)
        {
            int padding = 0;
            while (insertAt + padding < line.Length && line[insertAt + padding] == ' ')
                padding++;

            // Leading space plus brackets plus name; the colour tags render zero-width.
            int visibleWidth = contractName.Length + 3;
            int consumed = padding >= 2 ? Math.Min(visibleWidth, padding - 1) : 0;
            return line.Remove(insertAt, consumed).Insert(insertAt, annotation);
        }

        private static string RemovePreviousLabels(string line, int insertAt)
        {
            string prefix = " <color=" + ContractTagColor + ">[";
            const string suffix = "]</color>";
            int end = insertAt;
            int visibleWidth = 0;
            while (line.IndexOf(prefix, end, StringComparison.OrdinalIgnoreCase) == end)
            {
                int close = line.IndexOf(suffix, end + prefix.Length, StringComparison.OrdinalIgnoreCase);
                if (close < 0) break;
                visibleWidth += close - (end + prefix.Length) + 3;
                end = close + suffix.Length;
            }
            if (end == insertAt) return line;
            int padding = 0;
            while (end + padding < line.Length && line[end + padding] == ' ') padding++;
            // Reconstitute space-aligned columns before adding the new tag.
            string restored = padding >= 2 ? new string(' ', visibleWidth) : string.Empty;
            return line.Remove(insertAt, end - insertAt).Insert(insertAt, restored);
        }

        private static List<AliasCandidate> BuildCandidates(IEnumerable<MoonContractCatalogueLabel> labels)
        {
            var candidates = labels
                .Where(label => label != null && !string.IsNullOrWhiteSpace(label.ContractName))
                .SelectMany(label => label.Aliases.Select(alias => new AliasCandidate(alias, label)))
                .Where(candidate => candidate.Alias.Length >= 2)
                .GroupBy(candidate => candidate.Alias, StringComparer.OrdinalIgnoreCase)
                // A duplicate display alias can exist in very large moon packs. If those
                // levels have different assignments, showing no label is safer than showing
                // the wrong level's contract.
                .Where(group => group
                    .Select(candidate => candidate.Label.ContractName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() == 1)
                .Select(group => group.First())
                .OrderByDescending(candidate => candidate.Alias.Length)
                .ThenBy(candidate => candidate.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return candidates;
        }

        private static int FindMoonNameStart(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return -1;

            int index = SkipWhitespaceAndRichTextTags(line, 0);
            if (index >= line.Length || line[index] != '*')
                return -1;

            index = SkipWhitespaceAndRichTextTags(line, index + 1);
            return index < line.Length ? index : -1;
        }

        private static int SkipWhitespaceAndRichTextTags(string line, int index)
        {
            while (index < line.Length)
            {
                while (index < line.Length && char.IsWhiteSpace(line[index]))
                    index++;

                if (index >= line.Length || line[index] != '<')
                    return index;

                int tagEnd = line.IndexOf('>', index + 1);
                if (tagEnd < 0)
                    return line.Length;

                index = tagEnd + 1;
            }

            return index;
        }

        private static int FindAliasAtMoonName(string line, int moonNameStart, string alias)
        {
            if (moonNameStart < 0 || moonNameStart + alias.Length > line.Length)
                return -1;
            if (line.IndexOf(alias, moonNameStart, StringComparison.OrdinalIgnoreCase) != moonNameStart)
                return -1;

            int end = moonNameStart + alias.Length;
            bool endsAtBoundary = end >= line.Length || !IsNameCharacter(line[end]);
            return endsAtBoundary ? moonNameStart : -1;
        }

        private static bool IsNameCharacter(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_' || value == '-';
        }

        private readonly struct AliasCandidate
        {
            internal AliasCandidate(string alias, MoonContractCatalogueLabel label)
            {
                Alias = alias;
                Label = label;
            }

            internal string Alias { get; }
            internal MoonContractCatalogueLabel Label { get; }
        }
    }
}
