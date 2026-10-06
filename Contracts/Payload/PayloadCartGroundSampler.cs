using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Contracts.Payload
{
    internal static class PayloadCartGroundSampler
    {
        internal const float DefaultWheelHalfLength = 0.65f;
        internal const float DefaultWheelHalfWidth = 0.36f;
        internal const float GroundProbeOriginHeight = 0.5f;
        internal const float GroundProbeDistance = 2.5f;

        private const float MinWheelBase = 0.35f;
        private const float MinTrackWidth = 0.25f;

        // Reuse query buffers and the immutable wheel footprint. Physical contact is always fresh:
        // even two fixed ticks within a render frame can observe different terrain/platform poses.
        private const int GroundHitBufferSize = 32;
        private static readonly RaycastHit[] s_groundHits = new RaycastHit[GroundHitBufferSize];
        private static readonly Vector3[] s_boundsCorners = new Vector3[8];

        // Cached wheel-child footprint. The cart hierarchy is authored on the prefab and never changes
        // during a round, so the scan runs once per cart; a failed scan retries at most once a second so
        // a cart whose visual child spawns late still resolves.
        private const float FootprintRetrySeconds = 1f;
        private static int s_footprintRootId;
        private static bool s_footprintCached;
        private static bool s_footprintResolved;
        private static Vector3 s_footprintLocalCenter;
        private static float s_footprintHalfWidth;
        private static float s_footprintHalfLength;
        private static float s_footprintRetryAt;

        internal static bool TrySampleCartGround(Transform cartRoot, SampleState state, out GroundSample sample)
        {
            if (cartRoot == null)
            {
                sample = default;
                return false;
            }

            return TrySampleCartGround(cartRoot, cartRoot.position, cartRoot.rotation, state, out sample);
        }

        internal static bool TrySampleCartGround(
            Transform cartRoot,
            Vector3 rootPosition,
            Quaternion rootRotation,
            SampleState state,
            out GroundSample sample)
        {
            sample = default;
            if (cartRoot == null)
                return false;

            // Fixed ticks and moving platforms may change contacts within one render frame.
            return SampleCartGroundUncached(cartRoot, rootPosition, rootRotation, state, out sample);
        }

        /// <summary>
        /// #1219 D1: the same four-wheel probe for a pose the cart may never have stood in - the
        /// route proof's strides and a spawn candidate before any cart exists. A live cart passes
        /// its root, so its real wheel footprint is sampled and its own colliders are ignored; a
        /// null root samples the authored default footprint.
        /// </summary>
        internal static bool TrySampleFootprintGround(
            Transform cartRoot,
            Vector3 rootPosition,
            Quaternion rootRotation,
            out GroundSample sample)
            => SampleCartGroundUncached(cartRoot, rootPosition, rootRotation, null, out sample);

        private static bool SampleCartGroundUncached(
            Transform cartRoot,
            Vector3 rootPosition,
            Quaternion rootRotation,
            SampleState state,
            out GroundSample sample)
        {
            sample = default;
            CartFootprint footprint = ResolveFootprint(cartRoot, rootPosition, rootRotation);

            CornerHeight frontLeft = SampleCorner(footprint.FrontLeft, cartRoot);
            CornerHeight frontRight = SampleCorner(footprint.FrontRight, cartRoot);
            CornerHeight rearLeft = SampleCorner(footprint.RearLeft, cartRoot);
            CornerHeight rearRight = SampleCorner(footprint.RearRight, cartRoot);

            int freshHits = FreshCount(frontLeft, frontRight, rearLeft, rearRight);
            if (freshHits == 0)
                return false;
            int freshMask = (frontLeft.Fresh ? 1 : 0) | (frontRight.Fresh ? 2 : 0)
                | (rearLeft.Fresh ? 4 : 0) | (rearRight.Fresh ? 8 : 0);
            float freshAverage = ((frontLeft.Fresh ? frontLeft.Y : 0f) + (frontRight.Fresh ? frontRight.Y : 0f)
                + (rearLeft.Fresh ? rearLeft.Y : 0f) + (rearRight.Fresh ? rearRight.Y : 0f)) / freshHits;
            // Only current contacts may fill a missing corner for visual tilt; never historical heights.
            ResolveCornerHeight(ref frontLeft, frontRight, rearLeft, rearRight, freshAverage);
            ResolveCornerHeight(ref frontRight, frontLeft, rearRight, rearLeft, freshAverage);
            ResolveCornerHeight(ref rearLeft, rearRight, frontLeft, frontRight, freshAverage);
            ResolveCornerHeight(ref rearRight, rearLeft, frontRight, frontLeft, freshAverage);

            sample = new GroundSample(
                rootPosition.y,
                footprint.Forward,
                footprint.Right,
                footprint.WheelBase,
                footprint.TrackWidth,
                frontLeft.Y,
                frontRight.Y,
                rearLeft.Y,
                rearRight.Y,
                freshHits, freshMask,
                // On a three-wheel plane the diagonal passes through the centre; the three-height
                // average would bias the root toward the remaining axle on a slope.
                freshMask == 15 ? freshAverage : (freshMask & 9) == 9 ? (frontLeft.Y + rearRight.Y) * .5f
                    : (freshMask & 6) == 6 ? (frontRight.Y + rearLeft.Y) * .5f : freshAverage);
            return true;
        }

        private static CornerHeight SampleCorner(Vector3 point, Transform ignoreRoot)
        {
            if (TryRaycastGround(point, ignoreRoot, out RaycastHit hit))
                return new CornerHeight(hit.point.y, fresh: true, hasHeight: true);

            return default;
        }

        private static bool TryRaycastGround(Vector3 position, Transform ignoreRoot, out RaycastHit hit)
        {
            hit = default;
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : Physics.DefaultRaycastLayers;
            Vector3 origin = position + Vector3.up * GroundProbeOriginHeight;
            float distance = GroundProbeOriginHeight + GroundProbeDistance;
            int hitCount = Physics.RaycastNonAlloc(
                origin,
                Vector3.down,
                s_groundHits,
                distance,
                mask,
                QueryTriggerInteraction.Ignore);

            // #874/#1054: a saturated query is not a reading, and "no floor" is the most dangerous
            // answer it could give. A corner with no fresh hit drops its bit out of the support
            // mask, and PayloadCartMotionMath.IsLedgeAhead turns a lost bit into a ledge, so a
            // wheel over a crowded column - a stairwell with railings, treads and room colliders
            // all crossing one 3 m ray - refuses the step as a drop that is not there. Fall back to
            // the engine's own first-hit ray, which cannot saturate, rather than inventing floor or
            // growing the buffer until some other geometry overruns it too. Not the cause of the
            // 2026-09-21 Corrosion refusals, which were measured at turns: this is the wrong-probe
            // path beside them, closed so a real edge stays the only thing that reads as one.
            if (hitCount >= s_groundHits.Length)
                return Physics.Raycast(origin, Vector3.down, out hit, distance, mask, QueryTriggerInteraction.Ignore)
                    && hit.normal.y >= 0.5f && !IsIgnoredPayloadGroundCollider(hit.collider, ignoreRoot);
            float bestDistance = float.MaxValue;
            bool found = false;
            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = s_groundHits[i].collider;
                if (IsIgnoredPayloadGroundCollider(collider, ignoreRoot) || s_groundHits[i].normal.y < 0.5f)
                    continue;

                if (s_groundHits[i].distance < bestDistance)
                {
                    bestDistance = s_groundHits[i].distance;
                    hit = s_groundHits[i];
                    found = true;
                }
            }

            return found;
        }

        private static bool IsIgnoredPayloadGroundCollider(Collider collider, Transform ignoreRoot)
        {
            if (collider == null)
                return true;

            Transform colliderTransform = collider.transform;
            if (ignoreRoot != null && (colliderTransform == ignoreRoot || colliderTransform.IsChildOf(ignoreRoot)))
                return true;

            return collider.GetComponentInParent<PlayerControllerB>() != null || collider.GetComponentInParent<EnemyAI>() != null;
        }

        private static void ResolveCornerHeight(
            ref CornerHeight corner,
            CornerHeight mirror,
            CornerHeight adjacent,
            CornerHeight diagonal,
            float fallbackY)
        {
            if (corner.HasHeight)
                return;

            if (mirror.HasHeight)
            {
                corner = new CornerHeight(mirror.Y, fresh: false, hasHeight: true);
                return;
            }

            if (adjacent.HasHeight)
            {
                corner = new CornerHeight(adjacent.Y, fresh: false, hasHeight: true);
                return;
            }

            if (diagonal.HasHeight)
                corner = new CornerHeight(diagonal.Y, fresh: false, hasHeight: true);
            else
                corner = new CornerHeight(fallbackY, fresh: false, hasHeight: true);
        }

        private static int FreshCount(CornerHeight a, CornerHeight b, CornerHeight c, CornerHeight d)
        {
            int count = 0;
            if (a.Fresh)
                count++;
            if (b.Fresh)
                count++;
            if (c.Fresh)
                count++;
            if (d.Fresh)
                count++;
            return count;
        }

        private static CartFootprint ResolveFootprint(Transform root, Vector3 rootPosition, Quaternion rootRotation)
        {
            if (root != null && TryResolveCachedWheelChildFootprint(root, rootPosition, rootRotation, out CartFootprint wheelFootprint))
                return wheelFootprint;

            if (root != null && TryResolveColliderFootprint(root, rootPosition, rootRotation, out CartFootprint colliderFootprint))
                return colliderFootprint;

            return CartFootprint.FromLocalExtents(rootPosition, rootRotation, Vector3.zero, DefaultWheelHalfWidth, DefaultWheelHalfLength);
        }

        /// <summary>
        /// Wheel scan, cached per cart root (#716 B7). The wheel children are authored on the prefab, so
        /// the hierarchy walk and the per-child name compare only need to run once; a scan that finds no
        /// wheels is retried at most once a second in case the visual child had not spawned yet.
        /// </summary>
        private static bool TryResolveCachedWheelChildFootprint(
            Transform root,
            Vector3 rootPosition,
            Quaternion rootRotation,
            out CartFootprint footprint)
        {
            footprint = default;
            int rootId = root.GetInstanceID();
            bool cacheUsable = s_footprintCached
                && s_footprintRootId == rootId
                && (s_footprintResolved || Time.unscaledTime < s_footprintRetryAt);

            if (!cacheUsable)
            {
                s_footprintRootId = rootId;
                s_footprintCached = true;
                s_footprintResolved = TryResolveWheelChildLocalFootprint(
                    root,
                    out s_footprintLocalCenter,
                    out s_footprintHalfWidth,
                    out s_footprintHalfLength);
                if (!s_footprintResolved)
                    s_footprintRetryAt = Time.unscaledTime + FootprintRetrySeconds;
            }

            if (!s_footprintResolved)
                return false;

            footprint = CartFootprint.FromLocalExtents(
                rootPosition,
                rootRotation,
                s_footprintLocalCenter,
                s_footprintHalfWidth,
                s_footprintHalfLength);
            return true;
        }

        /// <summary>Invalidate the cached wheel footprint (call when a cart's visual hierarchy is rebuilt).</summary>
        internal static void InvalidateFootprintCache()
        {
            s_footprintCached = false;
            s_footprintResolved = false;
            s_footprintRetryAt = 0f;
        }

        /// <summary>
        /// Drop the footprint identity and collider references between rounds.
        /// </summary>
        internal static void ResetSessionCaches()
        {
            InvalidateFootprintCache();
            s_footprintRootId = 0;
            s_footprintLocalCenter = Vector3.zero;
            s_footprintHalfWidth = 0f;
            s_footprintHalfLength = 0f;
            System.Array.Clear(s_groundHits, 0, s_groundHits.Length);
        }

        private static bool TryResolveWheelChildLocalFootprint(
            Transform root,
            out Vector3 localCenter,
            out float halfWidth,
            out float halfLength)
        {
            localCenter = Vector3.zero;
            halfWidth = 0f;
            halfLength = 0f;
            Transform[] children = root.GetComponentsInChildren<Transform>(includeInactive: true);
            bool initialized = false;
            float minX = 0f;
            float maxX = 0f;
            float minZ = 0f;
            float maxZ = 0f;
            int count = 0;

            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null || child == root || child.name == null)
                    continue;

                if (child.name.IndexOf("wheel", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Vector3 local = root.InverseTransformPoint(child.position);
                if (!initialized)
                {
                    minX = maxX = local.x;
                    minZ = maxZ = local.z;
                    initialized = true;
                }
                else
                {
                    minX = Mathf.Min(minX, local.x);
                    maxX = Mathf.Max(maxX, local.x);
                    minZ = Mathf.Min(minZ, local.z);
                    maxZ = Mathf.Max(maxZ, local.z);
                }

                count++;
            }

            if (count < 4)
                return false;

            halfWidth = (maxX - minX) * 0.5f;
            halfLength = (maxZ - minZ) * 0.5f;
            if (halfWidth < MinTrackWidth * 0.5f || halfLength < MinWheelBase * 0.5f)
                return false;

            localCenter = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
            return true;
        }

        private static bool TryResolveColliderFootprint(
            Transform root,
            Vector3 rootPosition,
            Quaternion rootRotation,
            out CartFootprint footprint)
        {
            footprint = default;
            BoxCollider box = root.GetComponent<BoxCollider>();
            if (box != null)
            {
                Vector3 size = box.size;
                float halfWidth = Mathf.Max(MinTrackWidth * 0.5f, Mathf.Abs(size.x) * 0.5f);
                float halfLength = Mathf.Max(MinWheelBase * 0.5f, Mathf.Abs(size.z) * 0.5f);
                Vector3 localCenter = new Vector3(box.center.x, 0f, box.center.z);
                footprint = CartFootprint.FromLocalExtents(rootPosition, rootRotation, localCenter, halfWidth, halfLength);
                return true;
            }

            Collider collider = root.GetComponent<Collider>();
            if (collider == null)
                return false;

            Bounds bounds = collider.bounds;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            Vector3[] corners = s_boundsCorners;
            corners[0] = new Vector3(min.x, min.y, min.z);
            corners[1] = new Vector3(min.x, min.y, max.z);
            corners[2] = new Vector3(max.x, min.y, min.z);
            corners[3] = new Vector3(max.x, min.y, max.z);
            corners[4] = new Vector3(min.x, max.y, min.z);
            corners[5] = new Vector3(min.x, max.y, max.z);
            corners[6] = new Vector3(max.x, max.y, min.z);
            corners[7] = new Vector3(max.x, max.y, max.z);

            bool initialized = false;
            float minX = 0f;
            float maxX = 0f;
            float minZ = 0f;
            float maxZ = 0f;
            Matrix4x4 worldToRequestedLocal = Matrix4x4.TRS(rootPosition, rootRotation, root.lossyScale).inverse;
            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 local = worldToRequestedLocal.MultiplyPoint3x4(corners[i]);
                if (!initialized)
                {
                    minX = maxX = local.x;
                    minZ = maxZ = local.z;
                    initialized = true;
                }
                else
                {
                    minX = Mathf.Min(minX, local.x);
                    maxX = Mathf.Max(maxX, local.x);
                    minZ = Mathf.Min(minZ, local.z);
                    maxZ = Mathf.Max(maxZ, local.z);
                }
            }

            float colliderHalfWidth = Mathf.Max(MinTrackWidth * 0.5f, (maxX - minX) * 0.5f);
            float colliderHalfLength = Mathf.Max(MinWheelBase * 0.5f, (maxZ - minZ) * 0.5f);
            Vector3 colliderLocalCenter = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
            footprint = CartFootprint.FromLocalExtents(rootPosition, rootRotation, colliderLocalCenter, colliderHalfWidth, colliderHalfLength);
            return true;
        }

        internal sealed class SampleState { }

        internal readonly struct GroundSample
        {
            internal readonly float RootY;
            internal readonly Vector3 Forward;
            internal readonly Vector3 Right;
            internal readonly float WheelBase;
            internal readonly float TrackWidth;
            internal readonly float FrontLeftY;
            internal readonly float FrontRightY;
            internal readonly float RearLeftY;
            internal readonly float RearRightY;
            internal readonly float FrontY;
            internal readonly float RearY;
            internal readonly float LeftY;
            internal readonly float RightY;
            internal readonly float AverageY;
            internal readonly float AverageHeightOffset;
            internal readonly float SignedPitchDegrees;
            internal readonly float SignedRollDegrees;
            internal readonly int FreshHitCount;
            internal readonly int FreshMask;
            internal readonly float SupportY;
            internal bool HasStableSupport => PayloadCartMotionMath.HasStableSupport(FreshMask);

            internal GroundSample(
                float rootY,
                Vector3 forward,
                Vector3 right,
                float wheelBase,
                float trackWidth,
                float frontLeftY,
                float frontRightY,
                float rearLeftY,
                float rearRightY,
                int freshHitCount, int freshMask, float supportY)
            {
                RootY = rootY;
                Forward = FlattenNormalized(forward, Vector3.forward);
                Right = FlattenNormalized(right, Vector3.right);
                WheelBase = Mathf.Max(MinWheelBase, wheelBase);
                TrackWidth = Mathf.Max(MinTrackWidth, trackWidth);
                FrontLeftY = frontLeftY;
                FrontRightY = frontRightY;
                RearLeftY = rearLeftY;
                RearRightY = rearRightY;
                FrontY = (frontLeftY + frontRightY) * 0.5f;
                RearY = (rearLeftY + rearRightY) * 0.5f;
                LeftY = (frontLeftY + rearLeftY) * 0.5f;
                RightY = (frontRightY + rearRightY) * 0.5f;
                AverageY = (frontLeftY + frontRightY + rearLeftY + rearRightY) * 0.25f;
                AverageHeightOffset = AverageY - rootY;
                SignedPitchDegrees = Mathf.Atan2(FrontY - RearY, WheelBase) * Mathf.Rad2Deg;
                SignedRollDegrees = Mathf.Atan2(RightY - LeftY, TrackWidth) * Mathf.Rad2Deg;
                FreshHitCount = freshHitCount;
                FreshMask = freshMask;
                SupportY = supportY;
            }

            internal float ForwardGrade => (FrontY - RearY) / Mathf.Max(MinWheelBase, WheelBase);
            internal float RightGrade => (RightY - LeftY) / Mathf.Max(MinTrackWidth, TrackWidth);
            internal float SlopeAngleDegrees => Mathf.Atan(Mathf.Sqrt(ForwardGrade * ForwardGrade + RightGrade * RightGrade)) * Mathf.Rad2Deg;

            internal Vector3 DownhillDirection
            {
                get
                {
                    Vector3 uphill = Forward * ForwardGrade + Right * RightGrade;
                    uphill.y = 0f;
                    if (uphill.sqrMagnitude < 0.0001f)
                        return Vector3.zero;
                    return -uphill.normalized;
                }
            }

            internal float VerticalGradeAlong(Vector3 flatDirection)
            {
                flatDirection.y = 0f;
                if (flatDirection.sqrMagnitude < 0.0001f)
                    return 0f;

                Vector3 normalized = flatDirection.normalized;
                return Vector3.Dot(normalized, Forward) * ForwardGrade + Vector3.Dot(normalized, Right) * RightGrade;
            }

            internal float SignedSlopeAngleAlong(Vector3 flatDirection)
            {
                return Mathf.Atan(VerticalGradeAlong(flatDirection)) * Mathf.Rad2Deg;
            }
        }

        private readonly struct CornerHeight
        {
            internal readonly float Y;
            internal readonly bool Fresh;
            internal readonly bool HasHeight;

            internal CornerHeight(float y, bool fresh, bool hasHeight)
            {
                Y = y;
                Fresh = fresh;
                HasHeight = hasHeight;
            }
        }

        private readonly struct CartFootprint
        {
            internal readonly Vector3 FrontLeft;
            internal readonly Vector3 FrontRight;
            internal readonly Vector3 RearLeft;
            internal readonly Vector3 RearRight;
            internal readonly Vector3 Forward;
            internal readonly Vector3 Right;
            internal readonly float WheelBase;
            internal readonly float TrackWidth;

            private CartFootprint(Vector3 frontLeft, Vector3 frontRight, Vector3 rearLeft, Vector3 rearRight, Vector3 forward, Vector3 right)
            {
                FrontLeft = frontLeft;
                FrontRight = frontRight;
                RearLeft = rearLeft;
                RearRight = rearRight;
                Forward = FlattenNormalized(forward, Vector3.forward);
                Right = FlattenNormalized(right, Vector3.right);
                WheelBase = Mathf.Max(MinWheelBase, Vector3.Distance((frontLeft + frontRight) * 0.5f, (rearLeft + rearRight) * 0.5f));
                TrackWidth = Mathf.Max(MinTrackWidth, Vector3.Distance((frontLeft + rearLeft) * 0.5f, (frontRight + rearRight) * 0.5f));
            }

            internal static CartFootprint FromLocalExtents(
                Vector3 rootPosition,
                Quaternion rootRotation,
                Vector3 localCenter,
                float halfWidth,
                float halfLength)
            {
                Vector3 forward = FlattenNormalized(rootRotation * Vector3.forward, Vector3.forward);
                Vector3 right = FlattenNormalized(rootRotation * Vector3.right, Vector3.right);
                Vector3 center = rootPosition + right * localCenter.x + forward * localCenter.z;
                halfWidth = Mathf.Max(MinTrackWidth * 0.5f, Mathf.Abs(halfWidth));
                halfLength = Mathf.Max(MinWheelBase * 0.5f, Mathf.Abs(halfLength));

                return new CartFootprint(
                    center + forward * halfLength - right * halfWidth,
                    center + forward * halfLength + right * halfWidth,
                    center - forward * halfLength - right * halfWidth,
                    center - forward * halfLength + right * halfWidth,
                    forward,
                    right);
            }
        }

        private static Vector3 FlattenNormalized(Vector3 value, Vector3 fallback)
        {
            value.y = 0f;
            if (value.sqrMagnitude < 0.0001f)
            {
                fallback.y = 0f;
                return fallback.sqrMagnitude > 0.0001f ? fallback.normalized : Vector3.forward;
            }

            return value.normalized;
        }
    }
}
