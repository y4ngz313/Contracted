using System;

namespace Y4NGZCompany.Contracts.WasteDisposal
{
    /// <summary>Pure rules shared by runtime code and the focused regression executable.</summary>
    internal static class WasteDisposalIncineratorRules
    {
        internal const float BurnSecondsPerScrapValue = 0.6f;

        internal static float CalculateBurnDurationSeconds(int committedScrapValue)
        {
            return Math.Max(0, committedScrapValue) * BurnSecondsPerScrapValue;
        }

        internal static int CalculateQuotaCredit(int currentQuota, int successfullyBurnedScrapValue)
        {
            // Capped at quota - 1 so the revised target never lands on $0: vanilla treats a
            // $0 quota as already fulfilled and runs the whole end-of-quota pipeline (#517).
            int maxCredit = Math.Max(0, Math.Max(0, currentQuota) - 1);
            return Math.Min(maxCredit, Math.Max(0, successfullyBurnedScrapValue));
        }
    }
}
