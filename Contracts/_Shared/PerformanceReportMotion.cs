using System;

namespace Y4NGZCompany.Contracts._Shared
{
    internal static class PerformanceReportMotion
    {
        internal static float SweepPosition(float progress, int direction)
        {
            float clamped = Math.Max(0f, Math.Min(1f, progress));
            return direction < 0 ? 0.88f - 0.76f * clamped : 0.12f + 0.76f * clamped;
        }
    }
}
