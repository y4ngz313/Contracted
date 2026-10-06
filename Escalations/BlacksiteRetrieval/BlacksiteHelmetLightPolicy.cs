namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteHelmetLightPolicy
    {
        internal const float FullPower = 2f;
        internal const float RangeMultiplier = 1.5f;
        internal static float GetPowerFactor(BlacksiteSoldierTacticalRole role, bool outside) => FullPower;
        // Use the moon's local clock; a moon with no daylight cycle is treated as dark.
        // Hysteresis keeps dusk transitions from flickering near the threshold.
        internal static bool Enabled(bool alive, bool outside, bool hasDaylight, float dayFraction, bool wasOn)
            => alive && (!outside || !hasDaylight || dayFraction >= (wasOn ? .62f : .65f));
    }
}
