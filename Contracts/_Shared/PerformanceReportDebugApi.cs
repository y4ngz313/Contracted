namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Reflection-facing debug surface for Y4NGZDebugTools (same pattern as BundyDebugApi).
    /// The preview methods (#1203) do nothing unless <c>Diagnostics / Performance Report
    /// Preview</c> is on; each returns the line it logged, or why it did nothing.
    /// </summary>
    public static class PerformanceReportDebugApi
    {
        public static string GetNoteCatalogAudit()
        {
            return MoonContractRoundStats.BuildNoteCatalogAudit();
        }

        /// <summary>Opens the sample report, or replaces an open one, with <paramref name="cells"/> bottom-row cells (5 or 6).</summary>
        public static string ShowPreview(int cells)
        {
            if (cells != 5 && cells != 6)
                return "Performance report preview: cells must be 5 or 6.";

            return PerformanceReportPreview.Show(cells == 6
                ? PerformanceReportPreviewVariant.SixCells
                : PerformanceReportPreviewVariant.FiveCells);
        }

        public static string ClosePreview()
        {
            return PerformanceReportPreview.Close();
        }
    }
}
