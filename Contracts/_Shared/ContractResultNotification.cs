using UnityEngine;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Routes end-of-contract feedback through the existing contract card. This keeps a single
    /// visual language and avoids creating a second full-screen notification canvas.
    /// </summary>
    internal static class ContractResultNotification
    {
        internal static void ShowCompleted(MonoBehaviour host, string contractName, int rewardAmount, int newQuota)
        {
            // #716 (audit E6): every line on this card is ledger telemetry - uppercase, one
            // fact per line, middle dot between label and value (docs/UI_PRESENTATION_GUIDELINES.md,
            // "Contract copy"). A prose sentence in the middle of that read as a different screen.
            string body = rewardAmount > 0
                ? $"QUOTA REDUCED \u00B7 ${rewardAmount}\nNEW QUOTA \u00B7 ${newQuota}"
                : "CONTRACT SETTLED \u00B7 NO QUOTA CHANGE";

            ShowCompanyReport(
                host,
                $"{contractName.ToUpperInvariant()} \u00B7 SUCCESSFUL",
                body,
                isWarning: false);
        }

        internal static void ShowFailed(MonoBehaviour host, string contractName)
        {
            ShowCompanyReport(
                host,
                $"{contractName.ToUpperInvariant()} \u00B7 FAILURE",
                "CONTRACT FAILURE \u00B7 RECOVER SCRAP AND EXTRACT",
                isWarning: true);
        }

        internal static void ShowCompanyReport(MonoBehaviour host, string title, string body, bool isWarning)
        {
            if (HUDManager.Instance == null)
                return;

            ContractHUDManager.EnsureInstance();
            ContractHUDManager.Instance?.ShowResult(title, body, isWarning);
        }

    }
}
