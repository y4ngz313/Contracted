using System;
using System.Text.RegularExpressions;

namespace Y4NGZCompany.Contracts._Shared
{
    internal static class MoonsPlusContractLabelFormatter
    {
        private const string Marker = "y4ngz.moonsplus-contract";
        private const string OrbitingMarker = "Currently Orbiting";
        private static readonly Regex OwnedLabel = new Regex(
            "<link=\"" + Marker + "\">.*?</link>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        internal static string RemoveLabel(string suffix) => OwnedLabel.Replace(suffix ?? string.Empty, string.Empty);

        /// <summary>
        /// Appends the contract's short name inline (#874) when it fits this row; otherwise
        /// the row keeps its own text and <paramref name="dropped"/> reports the measured row
        /// and the fit budget (the footer channel still carries the contract, #1219). A zero,
        /// negative or non-finite width is a drop too. Never wraps a line.
        /// </summary>
        internal static string AddLabel(string prefix, string name, string suffix, string shortTitle,
            float width, Func<string, float> measure, out bool dropped, out float rowWidth, out float budget)
        {
            dropped = false;
            rowWidth = 0f;
            budget = 0f;
            suffix = RemoveLabel(suffix);
            if (string.IsNullOrEmpty(shortTitle) || measure == null) return suffix;
            // OpenLib adds this marker only to the selected row. Budget it for every row so
            // moving the cursor cannot make a previously fitting contract overlap the reel.
            rowWidth = measure(">>> " + prefix + name + suffix + " " + MoonContractCatalogueFormatter.FormatContractTag(shortTitle));
            budget = Finite(width) ? width - measure("  ") : width;
            if (Finite(width) && width > 0f && Finite(rowWidth) && rowWidth >= 0f && rowWidth <= budget)
                return suffix + Own(shortTitle);
            dropped = true;
            return suffix;
        }

        /// <summary>
        /// Inserts the highlighted moon's full contract line before Moons Plus's
        /// "Currently Orbiting" line; a footer without it gets the line prepended.
        /// </summary>
        internal static string AddFooterLine(string footer, string contractDisplayName, string moonName)
        {
            string line = MoonContractCatalogueFormatter.FormatContractLine(contractDisplayName, moonName);
            if (line == null) return footer;
            line = "<color=" + MoonContractCatalogueFormatter.ContractTagColor + ">" + line + "</color>";
            footer = footer ?? string.Empty;
            int orbiting = footer.IndexOf(OrbitingMarker, StringComparison.Ordinal);
            return orbiting >= 0 ? footer.Insert(orbiting, line + "\n") : "\n" + line + "\n" + footer;
        }

        private static string Own(string title) => "<link=\"" + Marker + "\"> "
            + MoonContractCatalogueFormatter.FormatContractTag(title) + "</link>";
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
