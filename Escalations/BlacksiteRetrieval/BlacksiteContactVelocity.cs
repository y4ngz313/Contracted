using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #874 S10.1, #1293 R3: what a combat contact is doing, read where the melee decision is
    /// taken. Unity-free, so the melee fixture and the console regression drive it directly.
    /// </summary>
    /// <remarks>
    /// The 2026-09-09 v13 run committed every swing on the instantaneous range and struck 0.8-0.97 s
    /// later. Two quantities fix that: where the contact will BE at impact (the lane; its row's
    /// <c>Velocity</c>), and what the RANGE will be (whether it connects; the RELATIVE velocity
    /// projected on the line between the bodies, since the actor's own chase is half of it).
    /// </remarks>
    internal static class BlacksiteContactVelocity
    {
        /// <summary>
        /// A velocity above this is not locomotion (twice the vanilla 6.6 m/s sprint): a teleport,
        /// respawn, warp or fixture placement is read as no velocity.
        /// </summary>
        internal const float MaximumTrackedSpeed = 13.2f;

        /// <summary>True when a measured velocity is one a body moving under its own power can have.</summary>
        internal static bool IsLocomotion(float vx, float vy, float vz)
            => vx * vx + vy * vy + vz * vz <= MaximumTrackedSpeed * MaximumTrackedSpeed;

        /// <summary>
        /// The rate the range between the actor and the contact is changing, positive when it opens:
        /// the relative velocity (contact minus actor) projected on the unit line from the actor to
        /// the contact. Zero when the two bodies coincide, where no line exists.
        /// </summary>
        internal static float RadialSpeed(
            float contactVx, float contactVy, float contactVz,
            float selfVx, float selfVy, float selfVz,
            float towardX, float towardY, float towardZ)
        {
            float length = (float)Math.Sqrt(towardX * towardX + towardY * towardY + towardZ * towardZ);
            if (length <= 1e-4f) return 0f;
            return ((contactVx - selfVx) * towardX + (contactVy - selfVy) * towardY
                + (contactVz - selfVz) * towardZ) / length;
        }

        /// <summary>The range the swing will be taken at: <c>range + radialSpeed * lead</c>, never below zero.</summary>
        internal static float PredictRange(float range, float radialSpeed, float leadSeconds)
        {
            float predicted = range + radialSpeed * (leadSeconds > 0f ? leadSeconds : 0f);
            return predicted > 0f ? predicted : 0f;
        }
    }
}
