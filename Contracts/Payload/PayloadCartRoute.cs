using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;

namespace Y4NGZCompany.Contracts.Payload
{
    /// <summary>
    /// Caller-owned working memory for <see cref="PayloadCartRoute"/>. One per caller that may run
    /// in the same frame as another (the NPC planner, the stuck verdict), so no query
    /// ever reads corners another one wrote. Allocated once; a proof allocates nothing.
    /// </summary>
    internal sealed class PayloadCartRouteScratch
    {
        /// <summary>
        /// Created on first use rather than here: a scratch held as a MonoBehaviour field would
        /// otherwise construct a NavMeshPath from the component's constructor, which Unity refuses.
        /// </summary>
        internal NavMeshPath Path;
        // #1262 P1: a riding route's onward leg past the car; created on first use like Path.
        internal NavMeshPath OnwardPath;

        /// <summary>The proven route, cart pose first. Valid for <see cref="CornerCount"/> entries after a proof.</summary>
        internal readonly Vector3[] Corners = new Vector3[PayloadCartRoute.MaxCorners];
        internal int CornerCount;
        /// <summary>
        /// #1262 P1: where the footprint proof ends - the sampled goal, or, when the route rides
        /// the mineshaft car, the car landing on the cart's own storey. Valid after a proof.
        /// </summary>
        internal Vector3 WalkGoal;
        // Allocated only if an unbaked collider requires a route around its bounds, or a riding
        // route's onward leg is read.
        internal Vector3[] DetourLegCorners;

        /// <summary>Where the last refusal was measured; diagnostics only.</summary>
        internal Vector3 RefusedAt;

        /// <summary>
        /// #1283: for a <c>link</c> refusal, the far corner of the unjoined pair (<see cref="RefusedAt"/>
        /// is the near one) and where the forward surface ray from the near corner stopped, so a log
        /// line can name what stands at the gap. Diagnostics only; read only for a <c>link</c> refusal.
        /// </summary>
        internal Vector3 RefusedTo;
        internal Vector3 RefusedHit;

        /// <summary>The collider behind the last geometry refusal, or null. Named only when a line is built.</summary>
        internal Collider Blocker;

        /// <summary>
        /// #1271: why the last proof's detour around <see cref="Blocker"/> failed, one entry per side
        /// tried (side 0 is the side the cart starts on): the leg (1-3) with no link-free route, or 0
        /// when every leg joined and the footprint refused, with that refusal token. No sides when no
        /// detour ran. Diagnostics only, read by <see cref="PayloadCartRoute.Describe"/>.
        /// </summary>
        internal int DetourSides;
        internal readonly int[] DetourLeg = new int[2];
        internal readonly string[] DetourRefusal = new string[2];

        internal readonly RaycastHit[] Hits = new RaycastHit[32];
        internal readonly Collider[] Overlaps = new Collider[32];
    }

    /// <summary>
    /// #1219 D1: the one answer to "can THIS cart be taken from here to there". The NPC haul
    /// planner asks it instead of the pusher agent's route, and the cart's stuck verdict asks it
    /// from the pose it was released at.
    ///
    /// Two halves, in this order, so a route that needs a jump always says <c>link</c>:
    ///
    /// 1. The route the one route service returns for the cart
    ///    (<see cref="BlacksiteReachability.Route"/> on <see cref="BlacksiteTraveller.Cart"/>,
    ///    #1262 P1) on the caller's areas without Unity's Jump area (<see cref="JumpArea"/>),
    ///    Reachable, every corner pair joined by contiguous surface. Most interior tile links are
    ///    authored on Jump, so without it the solver takes a link-free detour where one exists; the
    ///    rest sit on the ordinary walkable area, which no mask can drop without real floor.
    ///    Contiguity is a property of the route the oracle returned, not a second judge: a link is
    ///    two consecutive corners with no surface between them, found by <c>NavMesh.Raycast</c>,
    ///    which walks only connected polygons; a pit, a ladder or a drop is exactly where that walk
    ///    stops. A link laid over continuous floor is not refused, because the cart can roll across
    ///    it. The mineshaft car is the one link the cart may take: it rides the car
    ///    (<see cref="PayloadCartController.CarryNpcWithElevator"/>). A riding route's footprint is
    ///    proved to the car landing on the cart's storey (<see cref="PayloadCartRouteScratch.WalkGoal"/>)
    ///    and its onward leg must be link-free too; the haul re-plans on the far storey.
    /// 2. The cart's own motion rules along that path, stride by stride: the four-wheel ground
    ///    probe the motor uses, the #1003 ledge rule (never lose a wheel the cart has), steps of at
    ///    most <see cref="PayloadCartMotionMath.StepHeight"/> up or down, the structural width of
    ///    the opening against the chassis, the motor's motion box overlap and swept cast, and a
    ///    centre pivot at each corner (the motor's own rotation rule) onto whichever of the two
    ///    chassis headings is the smaller turn. A navmesh corner hugs walls by the agent radius,
    ///    which is less than the chassis; so a stride that fails on the line retries a few small
    ///    lateral offsets and a corner that cannot pivot in place retries a pivot point moved off
    ///    the inside of the turn before either is refused.
    ///    If an unbaked collider blocks that line, both sides of its bounds are candidates.
    ///    Each candidate is three complete, link-free legs, then the SAME continuous footprint
    ///    proof including both turns. The accepted corners, not the blocked line, steer the NPC.
    ///
    /// Door leaves are movable: a collider owned by a <see cref="DoorLock"/> is not geometry, the
    /// frame around it still is. A locked door carves the navmesh, so it refuses as <c>path</c>.
    /// Players, enemies (the Blacksite squad included) and the caller's own cart never block.
    /// Host-side by construction: every caller is host authority and the query reads only
    /// physics and navmesh state, so it replicates nothing.
    /// </summary>
    internal static class PayloadCartRoute
    {
        internal const int MaxCorners = 128;

        /// <summary>Unity's built-in Jump area. The cart's mask is the caller's without it.</summary>
        internal const int JumpArea = 2;

        internal const string RefuseStart = "noCartRoute:start";
        internal const string RefuseGoal = "noCartRoute:goal";
        internal const string RefusePath = "noCartRoute:path";
        internal const string RefuseLink = "noCartRoute:link";
        internal const string RefuseCorners = "noCartRoute:corners";
        internal const string RefuseDoor = "noCartRoute:door";
        internal const string RefuseStep = "noCartRoute:step";
        internal const string RefuseLedge = "noCartRoute:ledge";
        internal const string RefuseSweep = "noCartRoute:sweep";
        internal const string RefuseTurn = "noCartRoute:turn";

        /// <summary>
        /// Stride along a leg. Short enough that a stair tread or a kerb is one measured step and a
        /// wheel crossing a lip is caught before the centre is over it; 0.25 m climbs ramps to 48
        /// degrees at the 0.28 m step rule, steeper than any interior ramp.
        /// </summary>
        internal const float StrideMetres = 0.25f;

        private const float StartSampleMetres = 1f;
        private const float GoalSampleMetres = 2f;
        // The motor pivots in 2 degree steps; the route asks every 5, which still catches a post
        // the chassis corner would clip.
        private const float TurnStepDegrees = 5f;
        // 600 m of strides: past any interior's longest haul, and a hard bound on one proof.
        private const int MaxStrides = 2400;
        private static readonly float[] LateralOffsets = { 0.15f, -0.15f, 0.3f, -0.3f, 0.45f, -0.45f };
        private static readonly float[] PivotOffsets = { 0.2f, 0.4f, 0.6f };

        private static int Mask => StartOfRound.Instance != null
            ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : Physics.DefaultRaycastLayers;

        /// <summary>
        /// Prove the cart at <paramref name="cartPosition"/> (the cart ORIGIN, not the handle) can be
        /// taken to <paramref name="goal"/>. On success <paramref name="scratch"/> holds the proven
        /// corners and their end, <see cref="PayloadCartRouteScratch.WalkGoal"/>, and <paramref name="refusal"/>
        /// is null; otherwise it is one of the interned <c>noCartRoute:*</c> tokens, with
        /// <see cref="PayloadCartRouteScratch.RefusedAt"/> and <see cref="PayloadCartRouteScratch.Blocker"/> saying where and what.
        /// <paramref name="ignore"/> is the live cart's root, whose own colliders never block (or null).
        /// </summary>
        internal static bool Prove(Vector3 cartPosition, Quaternion cartRotation, Vector3 goal, int areaMask,
            PayloadCartRouteScratch scratch, Transform ignore, out string refusal)
        {
            scratch.DetourSides = 0;
            int cartMask = areaMask & ~(1 << JumpArea);
            if (!ReadLinkFreeCorners(cartPosition, goal, cartMask, true, scratch, scratch.Corners,
                    out scratch.CornerCount, out scratch.WalkGoal, out refusal)) return false;
            float yaw = cartRotation.eulerAngles.y;
            if (ProveFootprint(cartPosition, yaw, scratch.WalkGoal, scratch, ignore, out refusal)) return true;
            Collider blocker = scratch.Blocker;
            Vector3 refusedAt = scratch.RefusedAt;
            // An overlapped starting chassis cannot be rescued by choosing another destination.
            if (blocker == null || (refusal == RefuseSweep && refusedAt == cartPosition)) return false;
            if (TryDetour(cartPosition, yaw, scratch.WalkGoal, cartMask, scratch, ignore))
            { refusal = null; return true; }
            scratch.CornerCount = 0;
            scratch.Blocker = blocker;
            scratch.RefusedAt = refusedAt;
            return false;
        }

        /// <summary>
        /// The first half alone: a complete, same-storey path with no off-mesh link (a route that
        /// rides the car refuses <c>path</c>). A cheap connectivity filter for NPC corridor
        /// candidates, never cart-route authority or a fallback for a full <see cref="Prove"/> refusal.
        /// </summary>
        internal static bool ProveLinkFree(Vector3 from, Vector3 to, int areaMask,
            PayloadCartRouteScratch scratch, out string refusal)
            => ReadLinkFreeCorners(from, to, areaMask & ~(1 << JumpArea), false, scratch, scratch.Corners,
                out scratch.CornerCount, out _, out refusal);

        // cartMask is the caller's mask without JumpArea. The corners are the leg to walk now: the
        // whole route, or the leg to the car landing when the service's route rides (mayRide only).
        private static bool ReadLinkFreeCorners(Vector3 from, Vector3 to, int cartMask, bool mayRide,
            PayloadCartRouteScratch scratch, Vector3[] corners, out int count, out Vector3 walkGoal, out string refusal)
        {
            count = 0; walkGoal = to; scratch.Blocker = null; scratch.RefusedAt = from;
            if (!NavMesh.SamplePosition(from, out NavMeshHit start, StartSampleMetres, cartMask))
                return Refuse(scratch, RefuseStart, from, out refusal);
            if (!NavMesh.SamplePosition(to, out NavMeshHit end, GoalSampleMetres, cartMask))
                return Refuse(scratch, RefuseGoal, to, out refusal);
            if (scratch.Path == null) scratch.Path = new NavMeshPath();
            BlacksiteRouteVerdict route = BlacksiteReachability.Route(BlacksiteTraveller.Cart(cartMask, mayRide),
                start.position, end.position, scratch.Path);
            if (!route.Reachable)
                return Refuse(scratch, RefusePath, to, out refusal);
            count = scratch.Path.GetCornersNonAlloc(corners);
            if (count >= corners.Length) return Refuse(scratch, RefuseCorners, from, out refusal);
            if (count < 1) return Refuse(scratch, RefusePath, to, out refusal);
            for (int i = 1; i < count; i++)
            {
                if (!IsSurfaceContiguous(corners[i - 1], corners[i], cartMask))
                    return RefuseLinkBetween(scratch, corners[i - 1], corners[i], cartMask, out refusal);
            }
            walkGoal = route.Rides ? corners[count - 1] : to;
            if (route.Rides)
                return OnwardLinkFree(new Vector3(route.Next.ExitX, route.Next.ExitY, route.Next.ExitZ), end.position, cartMask, scratch, out refusal);
            refusal = null;
            return true;
        }

        // #1262 P1: past the car, from the landing the route leaves it at (its ElevatorCar exit), the
        // onward leg must be link-free as well. Its footprint is proved when the haul re-plans there.
        private static bool OnwardLinkFree(Vector3 leave, Vector3 goal, int cartMask,
            PayloadCartRouteScratch s, out string refusal)
        {
            if (s.OnwardPath == null) s.OnwardPath = new NavMeshPath();
            if (s.DetourLegCorners == null) s.DetourLegCorners = new Vector3[MaxCorners];
            if (!BlacksiteReachability.Walk(BlacksiteTraveller.Cart(cartMask, false), leave, goal, s.OnwardPath).Reachable)
                return Refuse(s, RefusePath, goal, out refusal);
            int count = s.OnwardPath.GetCornersNonAlloc(s.DetourLegCorners);
            if (count >= s.DetourLegCorners.Length) return Refuse(s, RefuseCorners, leave, out refusal);
            for (int i = 1; i < count; i++)
            {
                if (!IsSurfaceContiguous(s.DetourLegCorners[i - 1], s.DetourLegCorners[i], cartMask))
                    return RefuseLinkBetween(s, s.DetourLegCorners[i - 1], s.DetourLegCorners[i], cartMask, out refusal);
            }
            refusal = null;
            return true;
        }

        private static bool TryDetour(Vector3 from, float yaw, Vector3 goal, int cartMask,
            PayloadCartRouteScratch s, Transform ignore)
        {
            Vector3 direction = new Vector3(goal.x - from.x, 0f, goal.z - from.z);
            if (direction.sqrMagnitude < .0001f) return false;
            direction.Normalize();
            Vector3 right = new Vector3(direction.z, 0f, -direction.x);
            Bounds bounds = s.Blocker.bounds;
            Vector3 centre = bounds.center;
            centre.y = s.RefusedAt.y;
            Vector3 half = PayloadCartController.MotionHalfExtents;
            Vector3 boxCentre = PayloadCartController.MotionCenter;
            // Circumscribe the chassis so a turn at either outside corner is a candidate, not
            // a sideways teleport through the obstruction. Physics still proves every stride.
            float radius = Mathf.Sqrt(half.x * half.x + half.z * half.z)
                + Mathf.Sqrt(boxCentre.x * boxCentre.x + boxCentre.z * boxCentre.z)
                + PayloadCartController.MotionSkin * 2f;
            Vector3 along = direction * (Mathf.Abs(direction.x) * bounds.extents.x
                + Mathf.Abs(direction.z) * bounds.extents.z + radius);
            Vector3 across = right * (Mathf.Abs(right.x) * bounds.extents.x
                + Mathf.Abs(right.z) * bounds.extents.z + radius);
            float side = Vector3.Dot(from - centre, right) >= 0f ? 1f : -1f;
            if (s.DetourLegCorners == null) s.DetourLegCorners = new Vector3[MaxCorners];
            for (int attempt = 0; attempt < 2; attempt++, side = -side)
            {
                Vector3 near = centre - along + across * side;
                Vector3 far = centre + along + across * side;
                s.CornerCount = 0;
                s.DetourSides = attempt + 1;
                int leg = 0;
                string refusal;
                if (!AppendLinkFreeLeg(from, near, cartMask, s, out refusal)) leg = 1;
                else if (!AppendLinkFreeLeg(near, far, cartMask, s, out refusal)) leg = 2;
                else if (!AppendLinkFreeLeg(far, goal, cartMask, s, out refusal)) leg = 3;
                else if (ProveFootprint(from, yaw, goal, s, ignore, out refusal)) return true;
                s.DetourLeg[attempt] = leg;
                s.DetourRefusal[attempt] = refusal;
            }
            return false;
        }

        // A leg that does not join the previous one is a link by definition; one that overflows
        // the corner buffer refuses as the whole route would.
        private static bool AppendLinkFreeLeg(Vector3 from, Vector3 to, int cartMask,
            PayloadCartRouteScratch s, out string refusal)
        {
            if (!ReadLinkFreeCorners(from, to, cartMask, false, s, s.DetourLegCorners, out int count, out _, out refusal))
                return false;
            int skip = 0;
            if (s.CornerCount > 0)
            {
                Vector3 previous = s.Corners[s.CornerCount - 1];
                if (!IsSurfaceContiguous(previous, s.DetourLegCorners[0], cartMask)) { refusal = RefuseLink; return false; }
                if (previous == s.DetourLegCorners[0]) skip = 1;
            }
            if (s.CornerCount + count - skip > s.Corners.Length) { refusal = RefuseCorners; return false; }
            for (int i = skip; i < count; i++) s.Corners[s.CornerCount++] = s.DetourLegCorners[i];
            return true;
        }

        /// <summary>
        /// Whether two consecutive path corners are joined by walkable surface rather than by
        /// an off-mesh link. A native surface ray must reach the actual endpoint in either
        /// direction. At a polygon-border corner Unity can refuse one direction immediately even
        /// though the reverse ray reaches the same surface. No border hit or short gap is waived.
        /// </summary>
        internal static bool IsSurfaceContiguous(Vector3 a, Vector3 b, int areaMask)
        {
            if (a.x == b.x && a.z == b.z)
            {
                if (a.y != b.y) return false;
                return NavMesh.SamplePosition(a, out _, 0.001f, areaMask);
            }
            return (!NavMesh.Raycast(a, b, out var forward, areaMask)
                    && forward.mask != 0 && forward.position == b)
                || (!NavMesh.Raycast(b, a, out var reverse, areaMask)
                    && reverse.mask != 0 && reverse.position == a);
        }

        private static bool ProveFootprint(Vector3 cartPosition, float cartYaw, Vector3 goal,
            PayloadCartRouteScratch s, Transform ignore, out string refusal)
        {
            Vector3 pose = cartPosition;
            float yaw = cartYaw;
            // The wheels the cart has where it stands. An unreadable pose demands all four, which
            // is the motor's strict case too.
            int mask = PayloadCartGroundSampler.TrySampleFootprintGround(ignore, pose, Quaternion.Euler(0f, yaw, 0f), out var here)
                ? here.FreshMask : PayloadCartMotionMath.AllWheelsMask;
            string startRefusal = TryStep(pose, yaw, mask, pose, s, ignore, out Vector3 grounded, out int groundedMask);
            if (startRefusal != null)
                return Refuse(s, startRefusal, pose, out refusal);
            pose = grounded;
            mask = groundedMask;
            Vector3 inDirection = Vector3.zero;
            int strides = 0;
            int cornerCount = Mathf.Max(2, s.CornerCount);
            for (int i = 1; i < cornerCount; i++)
            {
                bool last = i == cornerCount - 1;
                Vector3 target = last ? goal : s.Corners[i];
                Vector3 flat = new Vector3(target.x - pose.x, 0f, target.z - pose.z);
                float length = flat.magnitude;
                if (length <= 0.05f)
                {
                    if (last) break;
                    continue;
                }
                Vector3 direction = flat / flat.magnitude;
                float legYaw = PayloadCartMotionMath.RouteChassisYaw(yaw, direction.x, direction.z);
                if (!Turn(ref pose, ref yaw, ref mask, legYaw, inDirection, direction, s, ignore, out refusal))
                    return false;
                // The pivot may have moved the cart off the corner; the leg runs from where it is.
                flat = new Vector3(target.x - pose.x, 0f, target.z - pose.z);
                length = flat.magnitude;
                if (length > 0.05f)
                {
                    direction = flat / flat.magnitude;
                    Vector3 origin = pose;
                    int steps = Mathf.CeilToInt(length / StrideMetres);
                    for (int k = 1; k <= steps; k++)
                    {
                        if (++strides > MaxStrides) return Refuse(s, RefusePath, pose, out refusal);
                        Vector3 onLine = origin + direction * (length * k / steps);
                        if (!Advance(ref pose, yaw, ref mask, onLine, direction, s, ignore, out refusal))
                            return false;
                    }
                }
                inDirection = direction;
            }
            // Goal is a cart-route endpoint, not an operator arrival radius. Neither the
            // portal's endpoint overlap checks nor its handle threshold prove this approach.
            // Lateral stride detours must also rejoin the actual endpoint before success.
            string goalRefusal = TryStep(pose, yaw, mask, goal, s, ignore, out _, out _);
            if (goalRefusal != null) return Refuse(s, goalRefusal, goal, out refusal);
            refusal = null;
            return true;
        }

        /// <summary>One stride: the opening first, then the line, then small lateral offsets.</summary>
        private static bool Advance(ref Vector3 pose, float yaw, ref int mask, Vector3 onLine, Vector3 direction,
            PayloadCartRouteScratch s, Transform ignore, out string refusal)
        {
            if (!OpeningFits(onLine, pose.y, direction, s, ignore))
                return Refuse(s, RefuseDoor, onLine, out refusal);
            string first = TryStep(pose, yaw, mask, onLine, s, ignore, out Vector3 next, out int nextMask);
            if (first == null) { pose = next; mask = nextMask; refusal = null; return true; }
            Collider blocker = s.Blocker;
            Vector3 right = new Vector3(direction.z, 0f, -direction.x);
            for (int o = 0; o < LateralOffsets.Length; o++)
            {
                if (TryStep(pose, yaw, mask, onLine + right * LateralOffsets[o], s, ignore, out next, out nextMask) != null)
                    continue;
                pose = next; mask = nextMask; refusal = null;
                return true;
            }
            // Report what refused the line itself, not the last offset tried.
            s.Blocker = blocker;
            return Refuse(s, first, onLine, out refusal);
        }

        /// <summary>
        /// The motor's own step, from a proven pose to a candidate plan position: ground probe,
        /// ledge rule, step rule, then grounded height and tilt, the lift, the overlap and the
        /// swept box. Returns null when clear, else the refusal token.
        /// </summary>
        private static string TryStep(Vector3 pose, float yaw, int mask, Vector3 candidate,
            PayloadCartRouteScratch s, Transform ignore, out Vector3 next, out int nextMask)
        {
            next = pose; nextMask = mask;
            Quaternion flatYaw = Quaternion.Euler(0f, yaw, 0f);
            Vector3 at = new Vector3(candidate.x, pose.y, candidate.z);
            if (!Ground(at, flatYaw, mask, ignore, out float rootY, out Quaternion rotation, out int freshMask, out string groundRefusal))
            { s.Blocker = null; return groundRefusal; }
            float rise = rootY - pose.y;
            if (rise > PayloadCartMotionMath.StepHeight) { s.Blocker = null; return RefuseStep; }
            if (rise < -PayloadCartMotionMath.StepHeight) { s.Blocker = null; return RefuseLedge; }
            at.y = rootY;
            Vector3 start = pose;
            if (at.y > start.y + PayloadCartController.MotionSkin)
            {
                if (!SweepClear(start, Vector3.up * (at.y - start.y), rotation, s, ignore)) return RefuseSweep;
                start.y = at.y;
            }
            if (!OverlapClear(at, rotation, s, ignore)) return RefuseSweep;
            if (!SweepClear(start, at - start, rotation, s, ignore)) return RefuseSweep;
            next = at; nextMask = freshMask;
            return null;
        }

        /// <summary>Wheel probe, ledge rule and the grounded root height and tilt at one pose.</summary>
        private static bool Ground(Vector3 at, Quaternion flatYaw, int mask, Transform ignore,
            out float rootY, out Quaternion rotation, out int freshMask, out string refusal)
        {
            rootY = at.y; rotation = flatYaw; freshMask = 0;
            bool sampled = PayloadCartGroundSampler.TrySampleFootprintGround(ignore, at, flatYaw, out var ground);
            if (!sampled || PayloadCartMotionMath.IsLedgeAhead(mask, true, ground.FreshMask) || !ground.HasStableSupport)
            { refusal = RefuseLedge; return false; }
            rootY = ground.SupportY + PayloadCartController.GroundedRootHeight;
            rotation = PayloadCartController.GroundRotation(flatYaw, ground);
            freshMask = ground.FreshMask;
            refusal = null;
            return true;
        }

        /// <summary>
        /// A centre pivot onto <paramref name="targetYaw"/>, the motor's rotation rule, tried where
        /// the cart stands and then from pivot points moved off the inside of the turn.
        /// </summary>
        private static bool Turn(ref Vector3 pose, ref float yaw, ref int mask, float targetYaw,
            Vector3 inDirection, Vector3 outDirection, PayloadCartRouteScratch s, Transform ignore, out string refusal)
        {
            float sweep = PayloadCartMotionMath.DeltaDegrees(yaw, targetYaw);
            if (Mathf.Abs(sweep) < 0.5f) { yaw = targetYaw; refusal = null; return true; }
            if (TryPivot(pose, yaw, mask, sweep, s, ignore, out Vector3 pivoted, out int pivotMask))
            { pose = pivoted; yaw = targetYaw; mask = pivotMask; refusal = null; return true; }
            Collider blocker = s.Blocker;
            // Away from the inside of the turn first (the corner the navmesh hugs), then square
            // to the new leg on either side for the cart's first turn, which has no inside.
            Vector3 outward = inDirection - outDirection;
            Vector3 side = new Vector3(outDirection.z, 0f, -outDirection.x);
            for (int d = 0; d < 3; d++)
            {
                Vector3 direction = d == 0 ? outward : d == 1 ? side : -side;
                direction.y = 0f;
                if (direction.sqrMagnitude < 0.01f) continue;
                direction.Normalize();
                for (int o = 0; o < PivotOffsets.Length; o++)
                {
                    if (TryStep(pose, yaw, mask, pose + direction * PivotOffsets[o], s, ignore, out Vector3 moved, out int movedMask) != null)
                        break;
                    if (!TryPivot(moved, yaw, movedMask, sweep, s, ignore, out pivoted, out pivotMask)) continue;
                    pose = pivoted; yaw = targetYaw; mask = pivotMask; refusal = null;
                    return true;
                }
            }
            s.Blocker = blocker;
            return Refuse(s, RefuseTurn, pose, out refusal);
        }

        private static bool TryPivot(Vector3 pose, float yaw, int mask, float sweep,
            PayloadCartRouteScratch s, Transform ignore, out Vector3 pivoted, out int pivotMask)
        {
            pivoted = pose; pivotMask = mask;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(sweep) / TurnStepDegrees));
            for (int i = 1; i <= steps; i++)
            {
                Quaternion flatYaw = Quaternion.Euler(0f, yaw + sweep * i / steps, 0f);
                if (!Ground(pivoted, flatYaw, pivotMask, ignore, out float rootY, out Quaternion rotation, out int freshMask, out _))
                { s.Blocker = null; return false; }
                if (Mathf.Abs(rootY - pivoted.y) > PayloadCartMotionMath.StepHeight) { s.Blocker = null; return false; }
                Vector3 at = new Vector3(pivoted.x, rootY, pivoted.z);
                if (!OverlapClear(at, rotation, s, ignore)) return false;
                pivoted = at; pivotMask = freshMask;
            }
            return true;
        }

        /// <summary>
        /// The structural opening at one stride, measured square to the leg at the motion box's
        /// centre height: the two side distances together must hold the chassis and its skin. A
        /// door leaf is not structure; its frame is.
        /// </summary>
        private static bool OpeningFits(Vector3 onLine, float rootY, Vector3 direction,
            PayloadCartRouteScratch s, Transform ignore)
        {
            float need = PayloadCartController.MotionHalfExtents.x * 2f + PayloadCartController.MotionSkin * 2f;
            Vector3 centre = new Vector3(onLine.x, rootY + PayloadCartController.MotionCenter.y, onLine.z);
            Vector3 right = new Vector3(direction.z, 0f, -direction.x);
            float left = SideClearance(centre, -right, need, s, ignore);
            if (left >= need) return true;
            return left + SideClearance(centre, right, need, s, ignore) >= need;
        }

        private static float SideClearance(Vector3 centre, Vector3 direction, float reach,
            PayloadCartRouteScratch s, Transform ignore)
        {
            int count = Physics.RaycastNonAlloc(centre, direction, s.Hits, reach, Mask, QueryTriggerInteraction.Ignore);
            // A saturated read is not a measurement; the swept box decides instead.
            if (count >= s.Hits.Length) return reach;
            float nearest = reach;
            for (int i = 0; i < count; i++)
            {
                if (IsIgnored(s.Hits[i].collider, ignore)) continue;
                if (s.Hits[i].distance < nearest) { nearest = s.Hits[i].distance; s.Blocker = s.Hits[i].collider; }
            }
            return nearest;
        }

        private static bool OverlapClear(Vector3 position, Quaternion rotation, PayloadCartRouteScratch s, Transform ignore)
        {
            int count = Physics.OverlapBoxNonAlloc(position + rotation * PayloadCartController.MotionCenter,
                PayloadCartController.MotionHalfExtents, s.Overlaps, rotation, Mask, QueryTriggerInteraction.Ignore);
            if (count >= s.Overlaps.Length) { s.Blocker = null; return false; }
            for (int i = 0; i < count; i++)
            {
                Collider hit = s.Overlaps[i];
                s.Overlaps[i] = null;
                if (IsIgnored(hit, ignore)) continue;
                s.Blocker = hit;
                for (int rest = i + 1; rest < count; rest++) s.Overlaps[rest] = null;
                return false;
            }
            return true;
        }

        private static bool SweepClear(Vector3 position, Vector3 movement, Quaternion rotation,
            PayloadCartRouteScratch s, Transform ignore)
        {
            float distance = movement.magnitude;
            if (distance < 0.0001f) return true;
            int count = Physics.BoxCastNonAlloc(position + rotation * PayloadCartController.MotionCenter,
                PayloadCartController.MotionHalfExtents, movement / distance, s.Hits, rotation,
                distance + PayloadCartController.MotionSkin, Mask, QueryTriggerInteraction.Ignore);
            if (count >= s.Hits.Length) { s.Blocker = null; return false; }
            for (int i = 0; i < count; i++)
            {
                if (IsIgnored(s.Hits[i].collider, ignore)) continue;
                s.Blocker = s.Hits[i].collider;
                return false;
            }
            return true;
        }

        private static bool IsIgnored(Collider hit, Transform ignore)
        {
            if (hit == null) return true;
            Transform t = hit.transform;
            if (ignore != null && (t == ignore || t.IsChildOf(ignore))) return true;
            return hit.GetComponentInParent<PlayerControllerB>() != null
                || hit.GetComponentInParent<EnemyAI>() != null
                || hit.GetComponentInParent<DoorLock>() != null;
        }

        private static bool Refuse(PayloadCartRouteScratch s, string token, Vector3 at, out string refusal)
        {
            s.RefusedAt = at;
            refusal = token;
            return false;
        }

        // #1283: a link refusal also keeps the far corner and where the forward surface ray from the
        // near corner stopped. The ray is cast again here, on the refusal path only; the verdict is
        // IsSurfaceContiguous's.
        private static bool RefuseLinkBetween(PayloadCartRouteScratch s, Vector3 near, Vector3 far, int cartMask,
            out string refusal)
        {
            NavMesh.Raycast(near, far, out NavMeshHit forward, cartMask);
            s.RefusedTo = far;
            s.RefusedHit = forward.position;
            return Refuse(s, RefuseLink, near, out refusal);
        }

        /// <summary>
        /// One diagnostic line's worth of a refusal: where (for a <c>link</c>, the near corner, the far
        /// one and the forward ray's stop, #1283), the blocker's name, scene path, layer and world
        /// bounds, and why each detour side around it failed. Allocates; call only when logging.
        /// </summary>
        internal static string Describe(string refusal, PayloadCartRouteScratch s)
        {
            string text = $"{refusal} at {Point(s.RefusedAt)}";
            if (refusal == RefuseLink)
                text += $" to {Point(s.RefusedTo)} hit={Point(s.RefusedHit)}";
            Collider blocker = s.Blocker;
            if (blocker != null)
            {
                int layer = blocker.gameObject.layer;
                Bounds bounds = blocker.bounds;
                text += $" blocker='{blocker.name}' path='{BlacksiteDiagnosticFormat.Path(blocker.transform)}'"
                    + $" layer={layer}:{LayerMask.LayerToName(layer)}"
                    + $" bounds.center={Point(bounds.center)} bounds.size={Point(bounds.size)}";
            }
            for (int i = 0; i < s.DetourSides; i++)
            {
                text += (i == 0 ? " detour=" : ";") + $"side{i}:"
                    + (s.DetourLeg[i] > 0 ? $"leg{s.DetourLeg[i]}" : "footprint")
                    + (s.DetourRefusal[i] != null ? ":" + s.DetourRefusal[i] : string.Empty);
            }
            return text;
        }

        private static string Point(Vector3 v) => $"({v.x:0.0},{v.y:0.0},{v.z:0.0})";
    }
}
