using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Contracts.ShadowRaid;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S6.2: one approach resolver, one goal.
    //
    // The 2026-09-09 Offense / Shadow Raid run reserved the Shadow Drive to a travel goal of
    // (-35.18, -205.80, 113.90): a floor point 0.19 m under the drive, and the drive sat at
    // targetLocal (-0.48, 0.14, -2.03) inside shipping container 111 whose door was at
    // (-33.15, -205.75, 113.42) and was never opened (LogOutput.log:3262). The reserved point was
    // two metres behind a closed container door. The old planner had two branches: a far branch
    // (anything over six metres, or the other world) that sampled the floor under the object and
    // returned it with NO reachability probe, NO body-occupancy probe and no knowledge of the
    // container at all, and a near branch that probed a ring but only inside six metres.
    //
    // The repair is not a longer probe. It is a single resolver used by every caller at every
    // distance, and one rule about what the goal IS: while the container or cache is still closed
    // the goal is the LOCK, not the object behind it. The object only becomes the goal once the
    // barrier that hides it is open.
    //
    // Three validity tests, applied to every candidate, at every distance:
    //   1. it samples onto the navmesh on the object's own storey;
    //   2. it is outside the closed barrier's collider bounds, expanded by the body radius;
    //   3. a body can stand there (BlacksitePhysicalStep.CanOccupy) and the route to it is complete.
    //
    // #1271 S2 (rule 4): route completeness is a veto, decided where the point is chosen
    // (BlacksiteObjectivePolicy.StandRefusal), on the route service
    // (BlacksiteReachability.Route), so a crossing through the mineshaft car is Reachable
    // and a point nobody can walk to is never published. What may never be published is a standing
    // point inside a sealed container, one no body fits in, or one without a complete route.
    internal static class BlacksiteObjectiveApproach
    {
        /// <summary>
        /// Headings and rings, unchanged from the pickup planner's own 12 x 3 probe: twelve 30-degree
        /// headings rotated by the attempt count so a repeat attempt does not re-test the same spokes.
        /// </summary>
        internal const int Headings = 12;
        internal const int Rings = 3;

        /// <summary>The pickup ring the near branch has always used, applied now at every distance.</summary>
        internal const float ObjectRingMin = .8f;
        internal const float ObjectRingStep = .4f;

        /// <summary>
        /// The lock ring's first radius. It is the pickup ring's own first radius, and it is the
        /// first tenth of a metre clear of the two hard floors under any standing point beside a
        /// wall: the navmesh is baked at BlacksiteArrivalPolicy.AgentRadius (0.45 m) and the body
        /// sweep is BlacksitePhysicalStep.BodyRadius (0.32 m), so 0.77 m is the first distance from
        /// a container face that can carry a member at all.
        /// </summary>
        internal const float LockRingMin = .8f;

        /// <summary>
        /// The farthest a standing point may sit from the lock anchor. Derived the S4.2 way, solved
        /// for the ring instead of the stop: the objective interaction's own approach owns a 0.3 m
        /// stopping distance (BlacksiteObjectiveInteraction.OverrideStoppingDistance) and settles
        /// anywhere inside it plus the settle slack, and its align gate is measured to the lock
        /// anchor with the sampled-destination margin on top, so
        ///   1.70 - 0.30 - 0.20 - 0.10 = 1.10 m
        /// is the last radius from which the interaction can still close its own gate.
        /// </summary>
        internal const float LockRingMax = BlacksiteObjectiveManifestController.ContainerLockReachDistance
            - BlacksiteObjectiveManifestController.ContainerLockStopDistance
            - BlacksiteArrivalPolicy.SettleSlack
            - BlacksiteArrivalPolicy.ProfileReachMargin;

        internal const float LockRingStep = (LockRingMax - LockRingMin) / (Rings - 1);

        /// <summary>
        /// How far a candidate may be off the navmesh before it is a different place. Unchanged
        /// from the near branch (0.5 m sample radius, 0.8 m storey tolerance).
        /// </summary>
        internal const float SampleRadius = .5f;
        internal const float FloorTolerance = .8f;

        /// <summary>
        /// A closed barrier's bounds are expanded by the body sweep radius before the standing point
        /// is tested against them: a point that a capsule of that radius would overlap the container
        /// with is inside the container as far as a walking member is concerned.
        /// </summary>
        internal const float BarrierClearance = BlacksitePhysicalStep.BodyRadius;

        /// <summary>
        /// The Shadow Raid barrier that currently hides the drive, if any. Resolved exactly the way
        /// HasPhysicalAccess and ObjectiveGoalKind resolve it, so the three cannot disagree about
        /// whether the container is open.
        /// </summary>
        internal static bool TryResolveBarrier(
            BlacksiteRetrievalMissionController mission,
            BlacksiteObjectiveKind kind,
            out Vector3 lockAnchor,
            out Bounds? bounds)
        {
            lockAnchor = Vector3.zero;
            bounds = null;
            if (mission == null || kind != BlacksiteObjectiveKind.ShadowDrive) return false;

            var container = BreachContainerController.FindByNetworkId(mission.ExpectedTargetNetworkId);
            if (container != null && !container.IsOpen)
            {
                lockAnchor = container.LockAnchor != null ? container.LockAnchor.position : container.transform.position;
                bounds = SolidBounds(container.transform);
                return true;
            }

            var cache = BlacksiteObjectiveManifestController.Resolve(mission.SecondaryTargetNetworkId)
                ?.GetComponent<ShadowRaidCargoCrate>();
            if (cache != null && !cache.Opened)
            {
                lockAnchor = cache.InteractionPosition;
                bounds = SolidBounds(cache.transform);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The union of every non-trigger collider under a barrier root. A shipping container is a
        /// shell of box colliders plus its door leaves rather than one collider, so the single
        /// closest collider is not the volume a standing point has to stay out of.
        /// </summary>
        internal static Bounds? SolidBounds(Transform root)
        {
            if (root == null) return null;
            Bounds total = default;
            bool any = false;
            var colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                var collider = colliders[i];
                if (collider == null || collider.isTrigger || !collider.enabled) continue;
                if (!any) { total = collider.bounds; any = true; }
                else total.Encapsulate(collider.bounds);
            }
            return any ? total : (Bounds?)null;
        }

        /// <summary>
        /// True when a standing point would put a body inside the barrier. Tested horizontally, with
        /// the roof excluded: the barrier stands ON the floor, so a floor point at the container's
        /// own minimum height is inside it, while a point on top of it is a different storey and is
        /// already refused by the storey tolerance.
        /// </summary>
        internal static bool InsideBarrier(Bounds? barrier, Vector3 point)
        {
            if (!barrier.HasValue) return false;
            Bounds bounds = barrier.Value;
            return point.y < bounds.max.y
                && point.x > bounds.min.x - BarrierClearance && point.x < bounds.max.x + BarrierClearance
                && point.z > bounds.min.z - BarrierClearance && point.z < bounds.max.z + BarrierClearance;
        }

        /// <summary>
        /// The one approach resolver. <paramref name="barrierClosed"/> chooses the goal: the lock
        /// anchor while the container or cache is shut, the object's own supporting floor once it is
        /// open. Everything else - the ring, the three tests, the refusal text - is shared.
        /// </summary>
        /// <param name="routeOrigin">
        /// #874 S2 / #1271 S2 (rule 4): the tile's entrance every candidate's route is measured from
        /// (<c>BlacksiteObjectiveManifestController.ReachabilityOrigin</c>), never the operator's own
        /// motor. The unreachable verdict is the holder's own route instead (HolderReach).
        /// </param>
        internal static bool TryResolve(
            Vector3 objectPosition,
            Vector3 supportFloor,
            bool barrierClosed,
            Vector3 lockAnchor,
            Bounds? barrierBounds,
            Vector3 operatorPosition,
            Transform operatorTransform,
            Vector3 routeOrigin,
            int mask,
            int attempt,
            out Vector3 point,
            out string refusal)
        {
            // #874 Q2: the bearing the ring starts from is the one the operator's route ARRIVES on
            // - its last corner before the object - not the straight line to wherever he stands. In
            // round 1 he stood 40 m away through walls, the first spoke faced him, and the published
            // point was the far corner of the apparatus room (log 11720-11892).
            if (barrierClosed)
            {
                // The lock anchor sits on the container's front face, 0.13 m proud of it and at
                // 0.52 of its height (BreachContainerController.RebuildAnchorsAndColliders). Its own
                // navmesh sample is therefore always inside the expanded bounds; the ring around its
                // ground projection is the goal, and the first heading is straight out of the door.
                Vector3 centre = GroundOf(lockAnchor, supportFloor, objectPosition);
                Vector3 arrival = barrierBounds.HasValue ? operatorPosition : LastRouteCorner(operatorPosition, centre);
                Vector3 outward = barrierBounds.HasValue
                    ? Vector3.ProjectOnPlane(lockAnchor - barrierBounds.Value.center, Vector3.up)
                    : Vector3.ProjectOnPlane(arrival - lockAnchor, Vector3.up);
                return TryRing(centre, Normalize(outward, arrival, lockAnchor), LockRingMin, LockRingStep,
                    barrierBounds, operatorPosition, operatorTransform, routeOrigin, mask, attempt,
                    "no standing point outside the closed container within " + LockRingMax.ToString("0.00") + " m of its lock",
                    out point, out refusal);
            }

            // Open: the object itself is the goal, on its own supporting floor, with the same three
            // tests the near branch used to apply only inside six metres.
            Vector3 from = LastRouteCorner(operatorPosition, supportFloor);
            Vector3 away = Vector3.ProjectOnPlane(from - objectPosition, Vector3.up);
            return TryRing(supportFloor, Normalize(away, from, objectPosition), ObjectRingMin, ObjectRingStep,
                null, operatorPosition, operatorTransform, routeOrigin, mask, attempt,
                "no local standing point on target support floor",
                out point, out refusal);
        }

        // One scratch path and corner buffer for the arrival bearing. Main thread only, like StandPath.
        private static readonly NavMeshPath BearingPath = new NavMeshPath();
        private static readonly Vector3[] BearingCorners = new Vector3[32];

        /// <summary>
        /// #874 Q2: the corner the operator's route reaches <paramref name="target"/> from - the last
        /// corner before the end that is not on top of it. A straight route's corner is the operator
        /// himself; no route (another storey, another world) or a route longer than the buffer keeps
        /// the operator's own position, the bearing this resolver used before.
        /// </summary>
        private static Vector3 LastRouteCorner(Vector3 operatorPosition, Vector3 target)
        {
            if (!NavMesh.SamplePosition(operatorPosition, out var start, SampleRadius, NavMesh.AllAreas)
                || !NavMesh.SamplePosition(target, out var end, SampleRadius, NavMesh.AllAreas)
                || BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(false, NavMesh.AllAreas), start.position, end.position,
                    BearingPath).Reach == BlacksiteRouteReach.Unsupported)
                return operatorPosition;
            int count = BearingPath.GetCornersNonAlloc(BearingCorners);
            if (count < 2 || count >= BearingCorners.Length) return operatorPosition;
            for (int i = count - 2; i >= 0; i--)
            {
                Vector3 corner = BearingCorners[i];
                if (Vector3.ProjectOnPlane(corner - end.position, Vector3.up).sqrMagnitude > ObjectRingMin * ObjectRingMin)
                    return corner;
            }
            return operatorPosition;
        }

        /// <summary>
        /// #989 C2: the validity tests above, applied to ONE candidate instead of to a ring.
        ///
        /// A witness is the one objective that is a body rather than a prop, so there is no ring to
        /// resolve around him - the floor under a man is the floor he is walking on - but the point
        /// still has to be a point: his transform is sampled within
        /// <see cref="BlacksiteResponsePolicy.ReportSampleRadius"/> on his own storey.
        ///
        /// #1271 S2 (rule 4): the route from the operator is measured on every call, on the oracle,
        /// and a PartialFrontier or Unsupported route REFUSES the point
        /// (<see cref="BlacksiteObjectivePolicy.StandRefusal"/>). It used to be reported and never a
        /// veto, and on 2026-09-29 round 2 the operator was sent to a witness pen every
        /// witnessPoint line called PathPartial (Player.log 21784 to 41084), walked to the end of
        /// that route and stood there for two minutes. <paramref name="status"/> is the measured
        /// verdict, or null when no point sampled on his storey and there was nothing to route to.
        ///
        /// #1293 R2: a moving target needs a route, not a standing point, so there is no body-fit
        /// veto here (a ring's static prop keeps its occupancy test in TryRing): on pass 7 the stand
        /// beside a live, routable witness was refused "no body fits" at 2.07 m.
        /// </summary>
        internal static bool TryStand(
            Vector3 target,
            BlacksiteSoldierAI operatorMember,
            out Vector3 point,
            out NavMeshPathStatus? status,
            out string refusal)
        {
            point = target;
            status = null;
            refusal = null;
            if (!NavMesh.SamplePosition(target, out var nav, BlacksiteResponsePolicy.ReportSampleRadius, NavMesh.AllAreas))
            { refusal = "no navmesh sample within " + BlacksiteResponsePolicy.ReportSampleRadius.ToString("0.00") + " m of the target"; return false; }
            if (Mathf.Abs(nav.position.y - target.y) > FloorTolerance)
            { refusal = "the nearest navmesh sample is on another storey"; return false; }
            BlacksiteRouteVerdict route = BlacksiteReachability.Route(BlacksiteTraveller.Soldier(operatorMember),
                operatorMember.transform.position, nav.position, StandPath);
            status = route.Reachable ? NavMeshPathStatus.PathComplete
                : route.Reach == BlacksiteRouteReach.PartialFrontier ? NavMeshPathStatus.PathPartial : NavMeshPathStatus.PathInvalid;
            refusal = BlacksiteObjectivePolicy.StandRefusal(route.Reach);
            if (refusal != null) { refusal += " from the operator"; return false; }
            point = nav.position;
            return true;
        }
        // One scratch path for the single-candidate resolver. Main thread only, like every other
        // NavMesh call in this file.
        private static readonly NavMeshPath StandPath = new NavMeshPath();

        private static Vector3 Normalize(Vector3 direction, Vector3 operatorPosition, Vector3 anchor)
        {
            if (direction.sqrMagnitude >= .1f) return direction.normalized;
            direction = Vector3.ProjectOnPlane(operatorPosition - anchor, Vector3.up);
            return direction.sqrMagnitude >= .1f ? direction.normalized : Vector3.forward;
        }

        /// <summary>The lock's own floor: the storey the object sits on, not 1.5 m up its door.</summary>
        private static Vector3 GroundOf(Vector3 anchor, Vector3 supportFloor, Vector3 objectPosition)
        {
            float y = Mathf.Abs(supportFloor.y - objectPosition.y) <= 6f ? supportFloor.y : objectPosition.y;
            return new Vector3(anchor.x, y, anchor.z);
        }

        private static bool TryRing(
            Vector3 centre,
            Vector3 outward,
            float minRadius,
            float step,
            Bounds? barrierBounds,
            Vector3 operatorPosition,
            Transform operatorTransform,
            Vector3 routeOrigin,
            int mask,
            int attempt,
            string exhausted,
            out Vector3 point,
            out string refusal)
        {
            point = centre;
            refusal = null;
            // #1106: the verdict of every candidate, UAT-gated. Four reproductions closed with
            // "no local standing point on target support floor" and nothing said WHICH of the
            // tests below refused the 36 points, so the fix shape (an axis probe past the ring, or
            // the container prefab's navmesh carve) could not be chosen. Printed once per
            // resolution and only when the ring publishes no point; the resolver runs at most once
            // a second per object (TryPickupApproach's _approachRetry), so this is one line a
            // second at worst and none at all on a clean resolution.
            var trace = BlacksiteDiagnostics.Enabled ? RingTrace.Clear() : null;
            for (int ring = 0; ring < Rings; ring++)
            {
                float radius = minRadius + ring * step;
                if (trace != null) trace.Append(ring == 0 ? ", r" : "; r").Append(radius.ToString("0.00")).Append('=');
                for (int i = 0; i < Headings; i++)
                {
                    float heading = (i + attempt * Rings) * (360f / Headings);
                    Vector3 candidate = centre + Quaternion.Euler(0, heading, 0) * outward * radius;
                    string verdict; string detail = null;
                    if (!NavMesh.SamplePosition(candidate, out var nav, SampleRadius, NavMesh.AllAreas))
                        verdict = "navmesh";
                    else if (Mathf.Abs(nav.position.y - centre.y) > FloorTolerance)
                    { verdict = "storey"; detail = (nav.position.y - centre.y).ToString("+0.00;-0.00"); }
                    else if (InsideBarrier(barrierBounds, nav.position))
                        verdict = "barrier";
                    else if (!BlacksitePhysicalStep.CanOccupy(operatorTransform, nav.position, mask, out var blocker))
                    { verdict = "occupy"; detail = blocker != null ? blocker.name : "saturated"; }
                    else
                    {
                        // #874 S2 / #1271 S2 (rule 4): only a complete route from the tile's entrance
                        // publishes a candidate; the old unrouted fallback (#950 A4) is gone, because
                        // the service rides the mineshaft car and a partial route is not a way there.
                        BlacksiteRouteVerdict route = BlacksiteReachability.Route(BlacksiteTraveller.Soldier(false), routeOrigin, nav.position);
                        if (BlacksiteObjectivePolicy.StandRefusal(route.Reach) == null) { point = nav.position; return true; }
                        verdict = "unrouted"; detail = trace != null ? route.PathReason : null;
                    }
                    if (trace == null) continue;
                    if (i > 0) trace.Append(' ');
                    trace.Append(heading % 360f).Append(':').Append(verdict);
                    if (detail != null) trace.Append('(').Append(detail).Append(')');
                }
            }
            if (trace != null)
                BlacksiteDiagnostics.Log("Objective",
                    $"event=ringExhausted, centre={BlacksiteDiagnosticFormat.V(centre)}, outward={BlacksiteDiagnosticFormat.V(outward)}"
                    + $", operator={BlacksiteDiagnosticFormat.V(operatorPosition)}, attempt={attempt}"
                    + $", result=refused{trace}");
            refusal = exhausted;
            return false;
        }
        // Scratch for the #1106 trace. Main thread only, like StandPath.
        private static readonly System.Text.StringBuilder RingTrace = new System.Text.StringBuilder(1024);
    }
}
