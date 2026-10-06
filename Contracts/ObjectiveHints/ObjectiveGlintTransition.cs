using System;

namespace Y4NGZCompany.Contracts.ObjectiveHints
{
    /// <summary>#1343. A Locate row completes only when the same sampled, unfound row turns found.</summary>
    internal static class ObjectiveGlintTransition
    {
        internal static bool IsCompleted(string previousKey, bool previousUnfound, int previousFoundCount,
            string currentKey, bool currentFound, int currentFoundCount)
        {
            return previousUnfound && previousKey != null && currentKey != null
                && string.Equals(previousKey, currentKey, StringComparison.Ordinal)
                && currentFound && currentFoundCount > previousFoundCount;
        }
    }
}
