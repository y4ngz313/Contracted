using System;

namespace Y4NGZCompany.Escalations.Bundy;

internal static class BundyFinisherClock
{
    // A Play request evaluates at the supplied normalized time. Both actors sample the
    // replicated elapsed time directly, including on a late-arriving client's first frame.
    internal static float NormalizedSample(double now, double start, double duration)
    {
        if (!Finite(now) || !Finite(start) || !Finite(duration) || duration <= 0)
            return 0f;
        return (float)Math.Max(0, Math.Min(0.999999, (now - start) / duration));
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
