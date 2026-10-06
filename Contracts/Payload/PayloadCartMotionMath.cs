using System;

namespace Y4NGZCompany.Contracts.Payload
{
    // Engine-independent policy shared by the runtime motor and the headless regression suite.
    internal static class PayloadCartMotionMath
    {
        internal const float Gravity = 18f;
        internal const float TerminalSpeed = 25f;
        internal const float StepHeight = 0.28f;
        internal const float GroundSnap = 0.18f;
        internal const float Acceleration = 3f;
        internal const float Braking = 6f;

        // FL=1, FR=2, RL=4, RR=8. The footprint centre must lie inside the support polygon.
        // Two wheels on one axle/side cannot hold the centre over an edge. A diagonal can.
        internal static bool HasStableSupport(int freshMask)
        {
            return (freshMask & 9) == 9 || (freshMask & 6) == 6;
        }

        internal const int AllWheelsMask = 15;

        /// <summary>
        /// A held cart may not take a step that costs it a wheel it currently has floor under
        /// (#1003). HasStableSupport is the "can it still stand here" test and only fails once the
        /// centre is already past the edge, which is one step too late to keep the cart. The rule is
        /// stated as a loss rather than "all four wheels at the next pose" because a diagonal is a
        /// legal resting state: a cart already hanging a wheel over a lip would otherwise fail every
        /// direction, including the one that takes it back to safety.
        /// </summary>
        internal static bool IsLedgeAhead(int currentFreshMask, bool nextSampled, int nextFreshMask)
            => !nextSampled || (currentFreshMask & ~nextFreshMask & AllWheelsMask) != 0;

        internal static float Approach(float value, float target, float delta)
        {
            return value < target ? Math.Min(value + delta, target) : Math.Max(value - delta, target);
        }

        internal static float DriveSpeed(float current, float target, float dt)
        {
            bool braking = Math.Abs(target) < Math.Abs(current) || current * target < 0f;
            return Approach(current, target, (braking ? Braking : Acceleration) * dt);
        }

        /// <summary>
        /// The share of a haul that reaches the route while the cart is not square to it. The cart
        /// travels along its own axis, so a pair hauling down a heading the chassis is
        /// <paramref name="degrees"/> away from advances by the cosine of that angle - not by the
        /// whole command, and never backwards.
        /// </summary>
        internal static float NpcAlignmentSpeedScale(float degrees)
            => Math.Max(0f, (float)Math.Cos(degrees * Math.PI / 180d));

        /// <summary>
        /// #874 (2026-09-20): the coupled haul's speed law, and the whole of it.
        ///
        /// The retired law was a follow-point controller: the cart chased a point thrown
        /// <see cref="NpcHandleDistanceMetres"/> off the pusher's route yaw, fed his measured speed
        /// forward, corrected the residual with a proportional gain, projected the result through
        /// an alignment cosine and, past a grace, reversed out of an overrun it had produced
        /// itself. Every one of those terms existed because the two bodies moved independently and
        /// the cart had to guess at the gap between them. They do not move independently any more:
        /// the cart takes one step, the operator is carried by the step the cart actually took, and
        /// the only question left is how far the pair may go this step.
        ///
        /// That is the haul speed, projected through the alignment the cart will have AFTER this
        /// step's turn. Taking it after the turn rather than before is what lets a pair that starts
        /// a quarter turn off its route begin moving as soon as the swing has closed the angle
        /// instead of waiting for a band to open; taking the cosine at all is what stops a cart
        /// that is still swinging from driving sideways off the route it was handed. There is no
        /// brake band and no cut-off: at a quarter turn the projection IS zero, and the pair turns
        /// on the spot about the handle, which moves neither body.
        /// </summary>
        /// <param name="cartYawDegrees">The cart's live yaw.</param>
        /// <param name="targetYawDegrees">
        /// The yaw that puts the chassis square on the route heading with the handle behind it -
        /// the route yaw plus PayloadCartController.FollowYawOffset, resolved by the caller.
        /// </param>
        /// <param name="haulSpeed">PayloadCartController.NpcPushSpeed.</param>
        /// <param name="turnRateDegreesPerSecond">The cart's authored yaw rate, which is also the
        /// rate the motor will actually slew at this step.</param>
        internal static float CoupledHaulSpeed(float cartYawDegrees, float targetYawDegrees,
            float haulSpeed, float turnRateDegreesPerSecond, float dt)
        {
            if (!IsFinite(cartYawDegrees) || !IsFinite(targetYawDegrees) || !IsFinite(haulSpeed)
                || !IsFinite(turnRateDegreesPerSecond) || !IsFinite(dt) || haulSpeed <= 0f)
                return 0f;
            float error = YawError(cartYawDegrees, targetYawDegrees);
            float turn = Math.Max(0f, turnRateDegreesPerSecond) * Math.Max(0f, dt);
            return haulSpeed * NpcAlignmentSpeedScale(error <= turn ? 0f : error - turn);
        }

        /// <summary>The angle between two yaws, in degrees: never negative and never past 180.</summary>
        private static float YawError(float from, float to)
        {
            float delta = Math.Abs((to - from) % 360f);
            return delta > 180f ? 360f - delta : delta;
        }

        /// <summary>
        /// #874 (2026-09-22): one pose the coupled planner asks the world about - "may the pair
        /// stand HERE, having come from THERE". The caller supplies the production predicate
        /// (PayloadCartController.CartPoseClear plus the pusher's standing room), so the search
        /// and the step it plans ask exactly the same question; nothing here approximates a fit.
        /// </summary>
        internal delegate bool CoupledPoseProbe(float fromX, float fromZ, float fromYawDegrees,
            float toX, float toZ, float toYawDegrees);

        /// <summary>
        /// #874 (2026-09-22): ONE planned coupled manoeuvre - the handle-anchored swing onto
        /// <paramref name="targetYawDegrees"/>, then a signed straight run of
        /// <paramref name="run"/> metres along the yaw that swing reached.
        ///
        /// This is the primitive the pose planner searches over AND the shape the coupled motor
        /// executes: the speed law transmits the cosine of the residual
        /// (<see cref="CoupledHaulSpeed"/>), so a pair handed a target yaw turns about the handle
        /// with almost no travel and then hauls straight - which is exactly "align, go straight
        /// while the opening constrains you, turn once clear" when the plan is a sequence of
        /// these.
        ///
        /// Every sub-pose is put to <paramref name="clear"/>. The swing is measured from the pose
        /// the manoeuvre STARTS in, because that is the pose the motor's own sweep runs from while
        /// it turns (PayloadCartController.ResolveNpcPivotRotation); the run is measured from the
        /// sub-pose before it, because that is the pose the motor's sweep runs from while it
        /// travels. Neither is an endpoint check: an opening whose two ends fit but whose swept
        /// rotation does not is refused here, which is the whole point of planning poses rather
        /// than corners.
        ///
        /// Returns true only when the WHOLE manoeuvre cleared. A clipped manoeuvre still reports
        /// the pose it actually reached, so the planner may keep it as a lesser node, but it is
        /// never reported as the manoeuvre that was asked for.
        /// </summary>
        internal static bool ResolveCoupledManoeuvre(float x, float z, float yawDegrees,
            float handleDistance, float targetYawDegrees, float pivotStepDegrees,
            float run, float runStepMetres, CoupledPoseProbe clear,
            out float endX, out float endZ, out float endYawDegrees, out float runTaken)
        {
            endX = x; endZ = z; endYawDegrees = yawDegrees; runTaken = 0f;
            if (clear == null || !IsFinite(x) || !IsFinite(z) || !IsFinite(yawDegrees)
                || !IsFinite(handleDistance) || !IsFinite(targetYawDegrees) || !IsFinite(run))
                return false;
            bool turnComplete = true;
            float sweep = DeltaDegrees(yawDegrees, targetYawDegrees);
            if (Math.Abs(sweep) > .0001f)
            {
                CartHandlePoint(x, z, yawDegrees, handleDistance, out float handleX, out float handleZ);
                float granularity = Math.Max(.05f, pivotStepDegrees);
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / granularity - 1e-6));
                for (int i = 1; i <= steps; i++)
                {
                    float delta = sweep * i / steps;
                    PivotAboutHandle(x, z, handleX, handleZ, delta, out float pivotedX, out float pivotedZ);
                    float candidateYaw = yawDegrees + delta;
                    if (!clear(x, z, yawDegrees, pivotedX, pivotedZ, candidateYaw))
                    { turnComplete = false; break; }
                    endX = pivotedX; endZ = pivotedZ; endYawDegrees = candidateYaw;
                }
            }
            if (Math.Abs(run) <= .0001f) return turnComplete;
            // The cart travels along -(its own forward), which the handle offset already folds the
            // 180 degrees of into CartHandlePoint; a reverse run travels the other way.
            double radians = endYawDegrees * Math.PI / 180d;
            float sign = run < 0f ? -1f : 1f;
            float directionX = (float)(-Math.Sin(radians)) * sign;
            float directionZ = (float)(-Math.Cos(radians)) * sign;
            float wanted = Math.Abs(run);
            float stride = Math.Max(.01f, runStepMetres);
            int runSteps = Math.Max(1, (int)Math.Ceiling(wanted / stride - 1e-6));
            float baseX = endX, baseZ = endZ, taken = 0f;
            bool runComplete = true;
            for (int i = 1; i <= runSteps; i++)
            {
                float travel = Math.Min(stride * i, wanted);
                float candidateX = baseX + directionX * travel;
                float candidateZ = baseZ + directionZ * travel;
                if (!clear(endX, endZ, endYawDegrees, candidateX, candidateZ, endYawDegrees))
                { runComplete = false; break; }
                endX = candidateX; endZ = candidateZ; taken = travel;
            }
            runTaken = taken * sign;
            return turnComplete && runComplete;
        }

        /// <summary>The signed shortest angle from one yaw to another, in degrees.</summary>
        internal static float DeltaDegrees(float from, float to)
        {
            float delta = (to - from) % 360f;
            if (delta > 180f) delta -= 360f;
            else if (delta < -180f) delta += 360f;
            return delta;
        }

        /// <summary>
        /// #1219 D1: the chassis yaw a route leg is travelled at. The cart moves along its own
        /// axis either way - hauled forward with the handle (local +Z) trailing, or backed along
        /// it - so a leg is taken at whichever of the two headings is the smaller turn from the
        /// yaw the cart already has, and no corner ever asks for more than a quarter turn.
        /// A zero-length travel keeps the current yaw.
        /// </summary>
        internal static float RouteChassisYaw(float currentYawDegrees, float travelX, float travelZ)
        {
            if (!IsFinite(currentYawDegrees) || !IsFinite(travelX) || !IsFinite(travelZ)
                || travelX * travelX + travelZ * travelZ < 1e-8f)
                return currentYawDegrees;
            float forward = (float)(Math.Atan2(-travelX, -travelZ) * 180d / Math.PI);
            float delta = DeltaDegrees(currentYawDegrees, forward);
            if (delta > 90f) delta -= 180f;
            else if (delta < -90f) delta += 180f;
            return currentYawDegrees + delta;
        }

        /// <summary>
        /// #874 (2026-09-22): whether a planar segment ever comes inside a disc. The coupled
        /// planner's route tests use it against the ground the pair proved it cannot take, which
        /// is the same geometry the navigator's own hazard-footprint refusal measures with.
        /// </summary>
        internal static bool SegmentEntersDisc(float centreX, float centreZ, float radius,
            float fromX, float fromZ, float toX, float toZ)
        {
            if (!IsFinite(centreX) || !IsFinite(centreZ) || !IsFinite(radius)
                || !IsFinite(fromX) || !IsFinite(fromZ) || !IsFinite(toX) || !IsFinite(toZ))
                return false;
            float ax = fromX - centreX, az = fromZ - centreZ;
            float spanX = toX - fromX, spanZ = toZ - fromZ;
            float spanSqr = spanX * spanX + spanZ * spanZ;
            float t = spanSqr < .0001f ? 0f : -(ax * spanX + az * spanZ) / spanSqr;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            float nearX = ax + spanX * t, nearZ = az + spanZ * t;
            return nearX * nearX + nearZ * nearZ < radius * radius;
        }

        /// <summary>
        /// #940/#1054 follow-up (2026-09-16): the handle-anchored pivot.
        ///
        /// A cart rotating about its own centre throws its handle around an arc of
        /// 2 * FollowDistance * sin(turn / 2) - 2.66 m for the 143-degree re-target measured on the
        /// 2026-09-16 host run, 2.80 m for a full reversal - which crosses
        /// PayloadCartController.NpcLeashBreakMetres (2.5 m) at 126.5 degrees and released the
        /// pusher as <c>separated</c> nine times out of nine. A person turning a cart does the
        /// opposite: the handle stays in their hands and the body swings round it. This is that
        /// rotation, about the vertical axis through the handle point, as the plane arithmetic the
        /// suite can integrate without Unity.
        ///
        /// The rotation matches <c>Quaternion.Euler(0, a, 0)</c>, which takes (0,1) to
        /// (sin a, cos a); paired with <see cref="CartHandlePoint"/> the handle is an exact
        /// invariant of the pair (pivot the body by <paramref name="deltaDegrees"/> and advance the
        /// yaw by the same amount and the handle does not move at all), which is the property the
        /// closed-loop fixture asserts.
        /// </summary>
        internal static void PivotAboutHandle(float cartX, float cartZ, float handleX, float handleZ,
            float deltaDegrees, out float pivotedX, out float pivotedZ)
        {
            if (!IsFinite(cartX) || !IsFinite(cartZ) || !IsFinite(handleX) || !IsFinite(handleZ)
                || !IsFinite(deltaDegrees))
            { pivotedX = cartX; pivotedZ = cartZ; return; }
            double radians = deltaDegrees * Math.PI / 180d;
            double cos = Math.Cos(radians), sin = Math.Sin(radians);
            double dx = cartX - handleX, dz = cartZ - handleZ;
            pivotedX = (float)(handleX + dx * cos + dz * sin);
            pivotedZ = (float)(handleZ - dx * sin + dz * cos);
        }

        /// <summary>
        /// The cart's own handle point in the plane, the same derivation as
        /// <c>PayloadCartController.NpcHandlePosition</c>: the handle is thrown
        /// <paramref name="handleDistance"/> off the cart's yaw with the 180-degree follow offset
        /// already folded in, so it reduces to <c>centre + d * (sin yaw, cos yaw)</c>. Height is not
        /// modelled: the pivot is about a vertical axis, which no vertical offset can change.
        /// </summary>
        internal static void CartHandlePoint(float cartX, float cartZ, float cartYawDegrees,
            float handleDistance, out float handleX, out float handleZ)
        {
            if (!IsFinite(cartX) || !IsFinite(cartZ) || !IsFinite(cartYawDegrees) || !IsFinite(handleDistance))
            { handleX = cartX; handleZ = cartZ; return; }
            double radians = cartYawDegrees * Math.PI / 180d;
            handleX = (float)(cartX + handleDistance * Math.Sin(radians));
            handleZ = (float)(cartZ + handleDistance * Math.Cos(radians));
        }

        /// <summary>The cart's authored handle offset, <c>PayloadCartController.FollowDistance</c>.</summary>
        internal const float NpcHandleDistanceMetres = 1.4f;

        /// <summary>
        /// A body that is travelling more than 120 degrees away from its own facing is being pulled
        /// backwards, so its walk cycle has to run backwards too (#1071 follow-up). 120 degrees is
        /// cos -0.5: a pure sideways shuffle is 90 degrees (alignment 0) and stays forward, which is
        /// the ambiguous-direction default. The cart motor drives the pusher along their facing, so
        /// in practice the alignment sits at +1 (W) or -1 (S) and only passes through this band while
        /// steering.
        /// </summary>
        internal const float PushWalkReverseAlignment = 0.5f;

        /// <summary>
        /// Sign for the walk cycle's direction float. <paramref name="forwardSpeed"/> is the body's
        /// planar velocity projected onto its own forward, <paramref name="planarSpeed"/> its planar
        /// magnitude; both in m/s and both smoothed the same way by the caller. Returns +1 whenever the
        /// direction is ambiguous - standing still, or anything that is not clearly backwards.
        /// </summary>
        internal static float PushWalkDirectionSign(float forwardSpeed, float planarSpeed)
        {
            if (planarSpeed <= 0f || !IsFinite(forwardSpeed) || !IsFinite(planarSpeed))
                return 1f;

            float alignment = forwardSpeed / planarSpeed;
            return alignment <= -PushWalkReverseAlignment ? -1f : 1f;
        }

        internal static float FallSpeed(float current, float dt)
        {
            return Math.Max(-TerminalSpeed, current - Gravity * dt);
        }

        internal static bool CanGround(int mask, float rootY, float groundRootY, bool wasGrounded)
        {
            return HasStableSupport(mask) && groundRootY <= rootY + (wasGrounded ? StepHeight : 0.025f)
                && groundRootY >= rootY - GroundSnap;
        }

        internal static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
