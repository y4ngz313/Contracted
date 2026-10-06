using UnityEngine;

namespace Y4NGZCompany.Contracts.Payload
{
    /// <summary>
    /// #1305 H1: a ship magnet the Payload cart's one magnet rule (<see cref="MagnetRule"/>) reads:
    /// the player ship's (<see cref="PlayerShipMagnetHost"/>) and the Blacksite hull's.
    /// </summary>
    internal interface IMagnetHost
    {
        /// <summary>The lever state that powers the pull.</summary>
        bool LeverOn { get; }
        Transform MagnetPoint { get; }
        /// <summary>World unit, horizontal: the magnet's forward on the ground plane, away from the hull.</summary>
        Vector3 OutwardSide { get; }
        float Reach { get; }
        /// <summary>Where the cart's own MagnetPoint sits when docked.</summary>
        Pose DockPose { get; }
    }

    /// <summary>
    /// #1305 H1 (R20): one magnet rule for both ships. A magnet's field is the outward half-space
    /// within <see cref="Reach"/> of its point. With the lever on, a free cart whose MagnetPoint is in
    /// the field rolls along the ground toward the dock at <see cref="PullSpeed"/>; within
    /// <see cref="DockRadius"/> of it (horizontally) it rises straight up onto the magnet at
    /// <see cref="AttachSpeed"/>. Anywhere else nothing happens and nothing is said.
    /// </summary>
    internal static class MagnetRule
    {
        internal const float Reach = 10f, PullSpeed = 3f, DockRadius = 1f, AttachSpeed = 6f;
        private const float DockClearance = .04f;

        internal static Vector3 Outward(Transform magnetPoint) => Outward(magnetPoint.rotation);

        /// <summary>Zero for a magnet facing straight up or down, which then has no field.</summary>
        internal static Vector3 Outward(Quaternion magnetRotation)
            => Vector3.ProjectOnPlane(magnetRotation * Vector3.forward, Vector3.up).normalized;

        internal static Pose Dock(Transform magnetPoint)
        {
            Vector3 outward = Outward(magnetPoint);
            return new Pose(magnetPoint.position + outward * DockClearance, Quaternion.LookRotation(-outward, Vector3.up));
        }

        internal static bool InField(Vector3 magnetPosition, Vector3 outward, float reach, Vector3 point)
        {
            Vector3 offset = point - magnetPosition;
            return Vector3.Dot(offset, outward) > 0f && offset.sqrMagnitude <= reach * reach;
        }

        internal static bool InField(IMagnetHost host, Vector3 point)
        {
            Transform magnet = host.MagnetPoint;
            return magnet != null && InField(magnet.position, host.OutwardSide, host.Reach, point);
        }
    }

    /// <summary>#1305 H1: the player ship's vanilla magnet and lever (<c>StartOfRound.magnetOn</c>).</summary>
    internal sealed class PlayerShipMagnetHost : IMagnetHost
    {
        internal static readonly PlayerShipMagnetHost Instance = new PlayerShipMagnetHost();

        private PlayerShipMagnetHost() { }

        public bool LeverOn => StartOfRound.Instance != null && StartOfRound.Instance.magnetOn && MagnetPoint != null;

        public Transform MagnetPoint
        {
            get
            {
                StartOfRound round = StartOfRound.Instance;
                return round != null ? round.magnetPoint : null;
            }
        }

        public Vector3 OutwardSide => MagnetRule.Outward(MagnetPoint);
        public float Reach => MagnetRule.Reach;
        public Pose DockPose => MagnetRule.Dock(MagnetPoint);
    }
}
