using System;

namespace Y4NGZCompany.Contracts.Payload
{
    /// <summary>
    /// #1283: the look rule for a player pushing the Payload cart. The pusher faces the cart's
    /// -forward (the handle side). First person may turn the body <see cref="FreeLookMaxYaw"/>
    /// degrees either way from that facing; the third-person orbit view keeps the body square to
    /// the handle, because its camera orbits separately within the same limit. Unity-free so the
    /// headless motion suite can drive it.
    /// </summary>
    internal static class PayloadPushLook
    {
        internal const float FreeLookMaxYaw = 100f;

        /// <summary>
        /// The body yaw to keep this frame. A yaw inside the view's limit, or any non-finite
        /// input, returns <paramref name="currentYaw"/> unchanged (the same value), so the caller
        /// writes only a real correction. So does a handle out of reach, in both views: after an
        /// entrance crossing the cart yaw is stale until the server re-places the cart.
        /// </summary>
        internal static float BodyYaw(float currentYaw, float cartYaw, bool thirdPerson, bool handleInReach)
        {
            if (!handleInReach || !PayloadCartMotionMath.IsFinite(currentYaw) || !PayloadCartMotionMath.IsFinite(cartYaw))
                return currentYaw;

            float facing = cartYaw + 180f;
            float limit = thirdPerson ? 0f : FreeLookMaxYaw;
            float offset = PayloadCartMotionMath.DeltaDegrees(facing, currentYaw);
            if (Math.Abs(offset) <= limit)
                return currentYaw;

            return facing + Math.Max(-limit, Math.Min(limit, offset));
        }
    }
}
