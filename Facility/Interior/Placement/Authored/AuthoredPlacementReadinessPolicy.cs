namespace Y4NGZCompany.Facility.Interior.Placement.Authored
{
    internal enum AuthoredPlacementReadinessDisposition
    {
        Wait,
        LogAndWait,
        Ready,
        ProceedDegraded
    }

    /// <summary>
    /// Bounded policy for the facility-placement barrier. A timed-out resolver may continue
    /// only because every fixture consumer is required to honor the shared reservation registry.
    /// </summary>
    internal static class AuthoredPlacementReadinessPolicy
    {
        internal const float TimeoutSeconds = 20f;
        internal const float ProgressLogIntervalSeconds = 5f;

        internal static AuthoredPlacementReadinessDisposition Evaluate(
            HostFacilityPlacementReadiness readiness,
            float elapsedSeconds,
            float nextProgressLogSeconds)
        {
            if (readiness.IsReady)
                return AuthoredPlacementReadinessDisposition.Ready;
            if (elapsedSeconds >= TimeoutSeconds)
                return AuthoredPlacementReadinessDisposition.ProceedDegraded;
            if (elapsedSeconds >= nextProgressLogSeconds)
                return AuthoredPlacementReadinessDisposition.LogAndWait;
            return AuthoredPlacementReadinessDisposition.Wait;
        }
    }
}
