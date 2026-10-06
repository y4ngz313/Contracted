using UnityEngine;

namespace Y4NGZCompany.Contracts.BlackoutAudit
{
    internal readonly struct BlackoutBreakerRuntimeMountPose
    {
        internal readonly Vector3 Position;
        internal readonly Quaternion Rotation;
        internal readonly Vector3 Forward;
        internal readonly float MountHeight;
        internal readonly float WallInset;
        internal readonly string Source;

        internal BlackoutBreakerRuntimeMountPose(
            Vector3 position,
            Quaternion rotation,
            Vector3 forward,
            float mountHeight,
            float wallInset,
            string source)
        {
            Position = position;
            Rotation = rotation;
            Forward = forward;
            MountHeight = mountHeight;
            WallInset = wallInset;
            Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
        }
    }
}
