using System;

namespace Y4NGZCompany.Escalations.Congregation
{
    /// <summary>What the Seraphim's central eye looks at (#1336 S6).</summary>
    internal enum SeraphimEyeAimMode
    {
        /// <summary>Hovering in place, or travelling outside the gaze cone: the eye looks where the
        /// body faces, level.</summary>
        Idle,
        /// <summary>Flying inside the gaze cone: the eye looks along the body's travel, climb and
        /// descent included.</summary>
        Travel,
        /// <summary>Attacking: the eye looks at the attack target.</summary>
        Target,
    }

    /// <summary>A Unity-free world or local direction for <see cref="SeraphimEyeAim"/>.</summary>
    internal readonly struct SeraphimEyeVector
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        internal SeraphimEyeVector(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        internal float SqrMagnitude => X * X + Y * Y + Z * Z;

        internal float Magnitude => (float)Math.Sqrt(SqrMagnitude);

        internal static float Dot(SeraphimEyeVector a, SeraphimEyeVector b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        internal static SeraphimEyeVector Cross(SeraphimEyeVector a, SeraphimEyeVector b)
        {
            return new SeraphimEyeVector(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        public static SeraphimEyeVector operator +(SeraphimEyeVector a, SeraphimEyeVector b)
        {
            return new SeraphimEyeVector(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public static SeraphimEyeVector operator -(SeraphimEyeVector a, SeraphimEyeVector b)
        {
            return new SeraphimEyeVector(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        public static SeraphimEyeVector operator *(SeraphimEyeVector a, float scale)
        {
            return new SeraphimEyeVector(a.X * scale, a.Y * scale, a.Z * scale);
        }
    }

    /// <summary>#1336 S6: the central eye's gaze rules - mode choice, travel-velocity filter,
    /// rate-limited turn, the cone about the body's facing and the post-rise blend-in. Unity-free and
    /// allocation-free, so the CongregantAnimationRegression suite runs the rules SeraphimEnemyAI
    /// applies every frame (<see cref="Advance"/>).</summary>
    internal static class SeraphimEyeAim
    {
        /// <summary>The eye turns a quarter circle in half a second.</summary>
        internal const float MaxTurnDegreesPerSecond = 180f;
        /// <summary>Half-angle of the cone about the body's level facing that the gaze stays in, so the
        /// eye never looks through the body. The pupil (7.9 deg radius, its axis 1.5 deg off
        /// Main_eye's +Y) then reaches at most 84.4 deg from the facing.</summary>
        internal const float MaxEyeDeflectionDegrees = 75f;
        /// <summary>Travel starts only with a heading this far inside the cone and holds out to its
        /// edge, so a heading near the edge does not flip Travel and Idle.</summary>
        internal const float TravelConeHysteresisDegrees = 5f;
        // Travel/Idle hysteresis on this peer's filtered root speed. Idle drift plus hover bob peaks
        // at 1.31 m/s on the host; a client's vanilla position sync (0.35 m steps, 0.13 s SmoothDamp)
        // ripples that to 1.71 m/s filtered with 100 ms arrival jitter, so Idle never reads as Travel.
        // A return flight (4.5 m/s, client trough 3.23 m/s) and a chase (9.5 m/s) stay Travel.
        internal const float MovingEnterSpeed = 3f;
        internal const float MovingExitSpeed = 2.25f;
        internal const float VelocitySmoothingSeconds = 0.2f;
        /// <summary>A root jump faster than this (a spawn snapshot or a late join) is not travel; it
        /// resets the filter.</summary>
        internal const float TeleportSpeed = 80f;
        internal const float BlendInSeconds = 0.6f;
        /// <summary>The sidecar model looks along its root's -Z: its root yaw is the facing yaw plus
        /// this. The legacy fullmesh faces +Z and takes no offset.</summary>
        internal const float SidecarFacingYawDegrees = 180f;
        /// <summary>Main_eye's gaze (out of the pupil) in the bone's local space, measured from the
        /// sidecar bundle.</summary>
        internal static readonly SeraphimEyeVector MainEyeLocalGazeAxis = new SeraphimEyeVector(0f, 1f, 0f);

        private const float DegreesToRadians = (float)(Math.PI / 180.0);
        private const float RadiansToDegrees = (float)(180.0 / Math.PI);
        private const float MinUsableSqrMagnitude = 1e-12f;
        // Below this the component of the desired gaze across the current one is lost to rounding.
        private const float AntiparallelTolerance = 1e-5f;

        /// <summary>Target whenever a target point resolves. Otherwise Travel while the travel heading
        /// lies inside the cone: entered at <see cref="MovingEnterSpeed"/> within
        /// <see cref="MaxEyeDeflectionDegrees"/> - <see cref="TravelConeHysteresisDegrees"/> of the
        /// facing, held down to <see cref="MovingExitSpeed"/> and out to the cone's edge. Anything else
        /// is Idle, so a body backing away looks straight ahead.</summary>
        internal static SeraphimEyeAimMode SelectMode(bool hasTargetPoint, float travelSpeed, float travelAngleFromForwardDegrees, SeraphimEyeAimMode previous)
        {
            if (hasTargetPoint)
                return SeraphimEyeAimMode.Target;

            bool held = previous == SeraphimEyeAimMode.Travel;
            float minSpeed = held ? MovingExitSpeed : MovingEnterSpeed;
            float maxAngle = held ? MaxEyeDeflectionDegrees : MaxEyeDeflectionDegrees - TravelConeHysteresisDegrees;
            return travelSpeed >= minSpeed && travelAngleFromForwardDegrees <= maxAngle
                ? SeraphimEyeAimMode.Travel
                : SeraphimEyeAimMode.Idle;
        }

        /// <summary>One frame of the eye, as SeraphimEnemyAI runs it: picks the mode; aims at the
        /// target, along the travel or straight ahead; clamps that into the cone about the level
        /// <paramref name="facingForward"/>; turns <paramref name="gaze"/> toward it (snapping when an
        /// attack starts); and clamps the result into the cone about this frame's facing, so a fast
        /// body turn carries the eye instead of leaving it looking through the body.
        /// <paramref name="targetDirection"/> runs from the eye to the target point and counts only
        /// with <paramref name="hasTargetPoint"/>; <paramref name="travelVelocity"/> is the filtered
        /// root velocity (<see cref="FilterVelocity"/>).</summary>
        internal static SeraphimEyeVector Advance(SeraphimEyeVector gaze, SeraphimEyeAimMode previous, bool hasTargetPoint,
            SeraphimEyeVector targetDirection, SeraphimEyeVector travelVelocity, SeraphimEyeVector facingForward,
            float deltaTime, out SeraphimEyeAimMode mode)
        {
            var forward = new SeraphimEyeVector(facingForward.X, 0f, facingForward.Z);
            mode = SelectMode(hasTargetPoint, travelVelocity.Magnitude, AngleDegrees(travelVelocity, forward), previous);
            SeraphimEyeVector desired = mode == SeraphimEyeAimMode.Target ? targetDirection
                : mode == SeraphimEyeAimMode.Travel ? travelVelocity
                : forward;
            desired = ClampToCone(desired, forward, MaxEyeDeflectionDegrees);
            SeraphimEyeVector stepped = StepGaze(gaze, desired, deltaTime, ShouldSnap(previous, mode));
            return ClampToCone(stepped, forward, MaxEyeDeflectionDegrees);
        }

        /// <summary>The unit <paramref name="direction"/> inside the cone of
        /// <paramref name="halfAngleDegrees"/> about <paramref name="forward"/>; outside it, the point
        /// on the cone's edge on the great circle toward <paramref name="forward"/>. Straight behind
        /// lands on the edge by a yaw turn (StepGaze's half-turn axis); a zero or non-finite direction
        /// gives the forward. Without a usable forward there is no cone and the direction only
        /// normalizes (zero when it cannot).</summary>
        internal static SeraphimEyeVector ClampToCone(SeraphimEyeVector direction, SeraphimEyeVector forward, float halfAngleDegrees)
        {
            bool hasDirection = TryNormalize(direction, out SeraphimEyeVector to);
            if (!TryNormalize(forward, out SeraphimEyeVector axis))
                return hasDirection ? to : default;
            if (!hasDirection)
                return axis;

            float halfAngle = halfAngleDegrees * DegreesToRadians;
            float edgeCosine = (float)Math.Cos(halfAngle);
            float cosine = SeraphimEyeVector.Dot(axis, to);
            if (cosine >= edgeCosine)
                return to;

            SeraphimEyeVector across = to - axis * cosine;
            float acrossLength = across.Magnitude;
            SeraphimEyeVector tangent = acrossLength > AntiparallelTolerance
                ? across * (1f / acrossLength)
                : AntiparallelTangent(axis);
            SeraphimEyeVector edge = axis * edgeCosine + tangent * (float)Math.Sin(halfAngle);
            return TryNormalize(edge, out SeraphimEyeVector result) ? result : axis;
        }

        /// <summary>The gaze jumps to the target, clamped into the cone, on the frame an attack picks
        /// it up; every other mode change turns at <see cref="MaxTurnDegreesPerSecond"/>.</summary>
        internal static bool ShouldSnap(SeraphimEyeAimMode previous, SeraphimEyeAimMode next)
        {
            return next == SeraphimEyeAimMode.Target && previous != SeraphimEyeAimMode.Target;
        }

        /// <summary>Exponential filter of the root's velocity over
        /// <see cref="VelocitySmoothingSeconds"/>. A displacement faster than
        /// <see cref="TeleportSpeed"/> resets it to rest; a frame without time keeps it.</summary>
        internal static SeraphimEyeVector FilterVelocity(SeraphimEyeVector previousFiltered, SeraphimEyeVector displacement, float deltaTime)
        {
            if (!(deltaTime > 0f))
                return previousFiltered;

            float maxStep = TeleportSpeed * deltaTime;
            if (!(displacement.SqrMagnitude <= maxStep * maxStep))
                return default;

            if (!IsFinite(previousFiltered))
                previousFiltered = default;

            SeraphimEyeVector velocity = displacement * (1f / deltaTime);
            float blend = 1f - (float)Math.Exp(-deltaTime / VelocitySmoothingSeconds);
            return previousFiltered + (velocity - previousFiltered) * blend;
        }

        /// <summary>The unit gaze turned from <paramref name="current"/> toward
        /// <paramref name="desired"/> along the great circle by at most
        /// <see cref="MaxTurnDegreesPerSecond"/> x <paramref name="deltaTime"/>; <paramref name="snap"/>
        /// returns the desired direction. A turn straight around goes about world up (about world X
        /// for a vertical gaze). A zero or non-finite desired direction keeps the current gaze.</summary>
        internal static SeraphimEyeVector StepGaze(SeraphimEyeVector current, SeraphimEyeVector desired, float deltaTime, bool snap)
        {
            bool hasCurrent = TryNormalize(current, out SeraphimEyeVector from);
            if (!TryNormalize(desired, out SeraphimEyeVector to))
                return hasCurrent ? from : current;
            if (snap || !hasCurrent)
                return to;

            float maxRadians = MaxTurnDegreesPerSecond * DegreesToRadians * deltaTime;
            if (!(maxRadians > 0f))
                return from;

            float cosine = SeraphimEyeVector.Dot(from, to);
            SeraphimEyeVector across = to - from * cosine;
            float acrossLength = across.Magnitude;
            float angle = (float)Math.Atan2(acrossLength, cosine);
            if (angle <= maxRadians)
                return to;

            SeraphimEyeVector tangent = acrossLength > AntiparallelTolerance
                ? across * (1f / acrossLength)
                : AntiparallelTangent(from);
            SeraphimEyeVector stepped = from * (float)Math.Cos(maxRadians) + tangent * (float)Math.Sin(maxRadians);
            return TryNormalize(stepped, out SeraphimEyeVector result) ? result : from;
        }

        /// <summary>0 while dead and through the rise and its hand-off, then a smoothstep to 1 over
        /// <see cref="BlendInSeconds"/>. Every peer has the same replicated rise parameters, so every
        /// peer ramps the eye in together.</summary>
        internal static float BlendWeight(double now, double riseStart, float riseDuration, float handoffSeconds, bool dead)
        {
            if (dead)
                return 0f;

            double progress = (now - (riseStart + riseDuration + handoffSeconds)) / BlendInSeconds;
            if (!(progress > 0.0))
                return 0f;
            if (progress >= 1.0)
                return 1f;

            float t = (float)progress;
            return t * t * (3f - 2f * t);
        }

        internal static bool TryNormalize(SeraphimEyeVector value, out SeraphimEyeVector normalized)
        {
            float sqr = value.SqrMagnitude;
            if (!(sqr > MinUsableSqrMagnitude) || float.IsInfinity(sqr))
            {
                normalized = default;
                return false;
            }

            normalized = value * (1f / (float)Math.Sqrt(sqr));
            return true;
        }

        private static bool IsFinite(SeraphimEyeVector value)
        {
            float sum = value.X + value.Y + value.Z;
            return !float.IsNaN(sum) && !float.IsInfinity(sum);
        }

        /// <summary>The angle between two directions, 0 when either is zero.</summary>
        private static float AngleDegrees(SeraphimEyeVector a, SeraphimEyeVector b)
        {
            return (float)Math.Atan2(SeraphimEyeVector.Cross(a, b).Magnitude, SeraphimEyeVector.Dot(a, b)) * RadiansToDegrees;
        }

        /// <summary>A unit direction across <paramref name="from"/> for a half turn: the yaw turn
        /// about world up, or about world X when the gaze is vertical.</summary>
        private static SeraphimEyeVector AntiparallelTangent(SeraphimEyeVector from)
        {
            var up = new SeraphimEyeVector(0f, 1f, 0f);
            if (TryNormalize(up - from * from.Y, out SeraphimEyeVector axis)
                && TryNormalize(SeraphimEyeVector.Cross(axis, from), out SeraphimEyeVector tangent))
                return tangent;

            var side = new SeraphimEyeVector(1f, 0f, 0f);
            return TryNormalize(side - from * from.X, out tangent) ? tangent : new SeraphimEyeVector(0f, 0f, 1f);
        }
    }
}
