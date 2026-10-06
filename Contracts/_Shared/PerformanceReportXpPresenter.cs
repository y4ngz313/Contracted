namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>Optional reflection entry point; Company owns the final paired-report layout.</summary>
    public static class PerformanceReportXpPresenter
    {
        public static bool TryApplyNotes(object hud, int playerIndex)
        {
            return hud is HUDManager manager && ContractPerformanceReportUi.TryApplyXpNotes(manager, playerIndex);
        }
    }
}
