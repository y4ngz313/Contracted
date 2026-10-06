using System;

namespace Y4NGZCompany.Contracts.Whistleblower
{
    // Shared by the live route planner and headless geometry regression checks.
    internal static class WhistleblowerFugitivePolicy
    {
        internal const float PlayerClearance = 1.8f;
        internal const float AssessmentSeconds = 0.65f;
        internal const float RouteRetrySeconds = 0.75f;
        internal const float StuckSeconds = 1.5f;

        internal static float PunchContactTime(bool left, float sourceDuration, float playbackRate)
            => sourceDuration * (left ? 0.55f : 0.4f) / playbackRate;

        internal static bool SegmentClearsPlayer(
            float ax, float ay, float az, float bx, float by, float bz,
            float px, float py, float pz)
        {
            float dx = bx - ax, dz = bz - az;
            float lengthSquared = dx * dx + dz * dz;
            float t = lengthSquared > 0.0001f
                ? Math.Max(0f, Math.Min(1f, ((px - ax) * dx + (pz - az) * dz) / lengthSquared))
                : 0f;
            if (Math.Abs(ay + (by - ay) * t - py) > 1.75f)
                return true;
            float startSquared = (px - ax) * (px - ax) + (pz - az) * (pz - az);
            float cx = ax + dx * t - px, cz = az + dz * t - pz;
            float closestSquared = cx * cx + cz * cz;
            float clearanceSquared = PlayerClearance * PlayerClearance;
            // Starting within reach must not trap him by definition: moving away or
            // tangentially is allowed, but a path through the nearby player is not.
            return closestSquared + 0.0001f >= Math.Min(clearanceSquared, startSquared);
        }
    }
}
