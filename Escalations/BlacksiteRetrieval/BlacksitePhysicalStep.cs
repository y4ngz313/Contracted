using System;
using UnityEngine;
using UnityEngine.AI;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Why a hand-driven step was refused, so a stalled passage names the geometry instead of
    // reporting an anonymous eight-second block.
    internal struct BlacksiteStepFailure
    {
        internal string Reason;
        internal Collider Blocker;
        internal string Describe() => (Reason ?? "no step attempted") + " (" + (Blocker != null ? Blocker.name : "no collider") + ")";
    }
    internal static class BlacksitePhysicalStep
    {
        // Physical walking speed for every hand-driven step (ship passage, cabin and entrance
        // thresholds). A brisk walk that the velocity-driven animation blend reads as walking.
        internal const float WalkSpeed = 3.6f;
        // Matches the game's 55.9-degree native agent slope limit. A floor is not clutter
        // merely because it is steeper than the old 45-degree landing probe cutoff.
        internal const float WalkableNormalY = .56f;
        // #874 S6.2: the swept body radius, named. It was already the literal on the probe capsule
        // and on the occupancy overlap below; naming it lets a planner that has to keep a standing
        // point out of a solid volume expand that volume by the same radius the sweep will use,
        // instead of repeating 0.32 in a second file where it could drift.
        internal const float BodyRadius = .32f;
        // #874 S9.4: the swept body column, named. It was the literal on the probe capsule below and
        // is the one number that says "same storey by construction". The value lives on the
        // Unity-free BlacksiteWorldLadderColumn.BodyColumnMetres so the headless regressions can
        // read it; the payload coupling's waypoint sample, S9.1's cabin-height bound and S9.5's
        // report drop all resolve to that one constant.
        internal const float CapsuleHeight = BlacksiteWorldLadderColumn.BodyColumnMetres;
        // The lift TryStep gives its own ground ray, named so a planner probing for the ground a
        // step will find uses the same origin the step will.
        internal const float GroundProbeLift = .55f;
        private static readonly RaycastHit[] Hits = new RaycastHit[32];
        private static readonly Collider[] Overlaps = new Collider[32];
        // #1170: the colliders standing in the body column ABOVE the climb band. Kept apart from
        // Hits and Overlaps because a step decision is taken while both of those are being iterated.
        private static readonly Collider[] BodyColumn = new Collider[32];
        private static CapsuleCollider _probe;
        private static CapsuleCollider Probe
        {
            get
            {
                if (_probe != null) return _probe;
                var go = new GameObject("BlacksiteCollisionProbe") { hideFlags = HideFlags.HideAndDontSave };
                go.layer = 2; go.transform.position = new Vector3(0, -100000f, 0);
                _probe = go.AddComponent<CapsuleCollider>();
                _probe.radius = BodyRadius; _probe.height = CapsuleHeight; _probe.center = Vector3.up * 1.14f; _probe.isTrigger = true;
                return _probe;
            }
        }
        internal static Transform ProbeTransform => Probe.transform;
        private static bool Penetrates(Collider collider, Vector3 position, out Vector3 direction, out float depth)
        { BlacksiteHitchTracer.CountPhysicsQuery(); return Physics.ComputePenetration(Probe, position, Quaternion.identity, collider, collider.transform.position, collider.transform.rotation, out direction, out depth); }
        private static bool BlocksAt(Collider collider, Vector3 from, Vector3 to, Vector3 supportPoint, int mask, ref int column)
        {
            if (Steppable(collider, supportPoint, from, to, mask, ref column)) return false;
            if (!Penetrates(collider, to, out var normal, out float depth) || depth < .003f) return false;
            if (normal.y > WalkableNormalY && depth < .2f) return false; // Supported step/floor, not its whole mesh bounds.
            if (Penetrates(collider, from, out _, out float oldDepth) && depth < oldDepth - .001f) return false;
            return true;
        }
        /// <summary>
        /// #1170: true for a contact the actor walks ONTO rather than around. Five measured
        /// conditions, none of them a collider name and none of them the whole mesh's bounds:
        /// <list type="bullet">
        /// <item>the contact sits inside <see cref="StepClimb"/> above the destination foot;</item>
        /// <item>the collider is geometry the navmesh bake itself saw
        /// (<see cref="BlacksiteClutterObstacle.IsStaticLevelGeometry"/>), so a prop, a cart or an
        /// animated cabin lip is still refused;</item>
        /// <item>it does not ALSO stand in the body column above that band, which is what keeps a
        /// wall, a rail at chest height, a boulder or a flight whose rise beats the climb refusing
        /// the step - on a 0.4375 m rise the next tread is inside the band and the one above it is
        /// a tread run away horizontally, so a climbable flight is clear of the raised column by
        /// construction and an unclimbable one is not;</item>
        /// <item>it carries walkable ground of its own at the contact, within the climb, so the
        /// thing in the way is a tread to stand on rather than a face to walk through;</item>
        /// <item>and it presents no walkable lid over the foot the actor is standing on, so a pose
        /// buried inside a thick slab is not read as a step merely because the slab's top is within
        /// the climb the way a tread is.</item>
        /// </list>
        /// Uses the collider's actual surfaces, not its whole mesh bounds. The old 0.2 m
        /// penetration allowance refused Platform27's non-convex stair mesh with 0.4375 m
        /// rises over 0.5 m treads despite a complete navigation path.
        /// </summary>
        private static bool Steppable(Collider collider, Vector3 contact, Vector3 from, Vector3 to, int mask, ref int column)
        {
            float climb = StepClimb;
            if (contact.y > to.y + climb || !BlacksiteClutterObstacle.IsStaticLevelGeometry(collider)) return false;
            if (column < 0)
            {
                BlacksiteHitchTracer.CountPhysicsQuery();
                column = Physics.OverlapCapsuleNonAlloc(to + Vector3.up * (climb + BodyRadius),
                    to + Vector3.up * 1.8f, BodyRadius, BodyColumn, mask, QueryTriggerInteraction.Ignore);
            }
            if (column >= BodyColumn.Length) return false; // A saturated query refuses, as every other one here does.
            for (int i = 0; i < column; i++) if (ReferenceEquals(BodyColumn[i], collider)) return false;
            if (Surface(collider, from, climb, out float lid) && lid > from.y + StepHeight) return false;
            return Surface(collider, contact, climb, out float tread) && tread <= to.y + climb;
        }
        // This collider's own walkable surface over a point, reached from one climb above it. Per
        // collider and ray-based: Physics.ClosestPoint does not support a non-convex MeshCollider,
        // and the usual fallback to its bounds would treat a whole stair flight as solid here.
        private static bool Surface(Collider collider, Vector3 point, float climb, out float y)
        {
            y = 0f;
            BlacksiteHitchTracer.CountPhysicsQuery();
            if (!collider.Raycast(new Ray(new Vector3(point.x, point.y + climb, point.z), Vector3.down),
                    out var hit, climb * 2f)
                || hit.normal.y <= WalkableNormalY) return false;
            y = hit.point.y; return true;
        }
        /// <summary>
        /// #978: a squadmate is not geometry a hand-driven step has to avoid. Members finishing the
        /// same lip drop walk onto the SAME published exit point, so the second one to
        /// arrive would be refused the spot by the first - and each of them in turn - with nothing in
        /// the world actually in the way. Any collider belonging to another Blacksite soldier answers
        /// true: its body capsule (<see cref="BlacksiteBodyCollision"/>, on EnemiesNotRendered, which
        /// is outside <c>collidersAndRoomMaskAndDefault</c> today and so never reaches a sweep) and
        /// anything parented to it, such as a carried prop, which can. The actor's OWN colliders are
        /// already skipped by <c>CanOccupy</c> and <c>ClearSegment</c> before an ignore
        /// predicate is consulted, so this only ever removes other members.
        /// </summary>
        internal static bool IsSquadBody(Collider collider)
            => collider != null && collider.GetComponentInParent<BlacksiteSoldierAI>() != null;
        internal static readonly Predicate<Collider> IgnoreSquadBodies = IsSquadBody;
        // The cart this member is pushing is his, not geometry: its handle stands inside his
        // capsule and the Manual hold parks it beside him, so it never refuses his own entrance or
        // cabin step. Every other cart still does.
        internal static bool IsCartPushedBy(Collider collider, BlacksiteSoldierAI soldier)
        {
            var cart = collider != null ? collider.GetComponentInParent<Y4NGZCompany.Contracts.Payload.PayloadCartController>() : null;
            return cart != null && cart.IsPushedBy(soldier);
        }
        internal static bool CanOccupy(Transform actor, Vector3 position, int mask, Predicate<Collider> ignore = null)
            => CanOccupy(actor, position, mask, out _, ignore);
        // #873: names the collider that refuses the spot; a saturated query reports null.
        internal static bool CanOccupy(Transform actor, Vector3 position, int mask, out Collider blocker, Predicate<Collider> ignore = null)
        {
            blocker = null;
            BlacksiteHitchTracer.CountPhysicsQuery();
            int count = Physics.OverlapCapsuleNonAlloc(position + Vector3.up * .48f, position + Vector3.up * 1.8f, BodyRadius, Overlaps, mask, QueryTriggerInteraction.Ignore);
            if (count >= Overlaps.Length) return false;
            int column = -1;
            for (int i = 0; i < count; i++)
            {
                var c = Overlaps[i];
                if (c == null || c.transform.IsChildOf(actor) || (ignore != null && ignore(c))) continue;
                if (BlocksAt(c, position, position, position, mask, ref column)) { blocker = c; return false; }
            }
            return true;
        }
        internal const float StepHeight = .32f;
        // #1170: the band a step may cross, and the reason a stair stopped being a wall. The bound
        // is the native navigation authority's own climb rather than a chosen number: the extracted
        // project's ProjectSettings/NavMeshAreas.asset carries radius 0.35, height 1, slope 55.9 and
        // climb 0.8 (tests/BlacksiteLandingCombatChecks/README.md), and WalkableNormalY above is
        // already that same asset's slope limit read the same way. The baked settings win when the
        // game has them, including a configured zero climb. Never widen a tighter bake; use the
        // existing manual-step limit only when no agent settings are registered.
        //
        // StepHeight above is NOT this and does not move: it stays the hand walk's supporting-ground
        // bound, so a riser this sweep stops refusing is not automatically one a hand-driven
        // walk will climb.
        internal const float NativeAgentClimb = .8f;
        private static float _stepClimb = -1f;
        internal static float StepClimb
        {
            get
            {
                if (_stepClimb >= 0f) return _stepClimb;
                return _stepClimb = NavMesh.GetSettingsCount() > 0
                    ? Mathf.Clamp(NavMesh.GetSettingsByIndex(0).agentClimb, 0f, NativeAgentClimb)
                    : StepHeight;
            }
        }
        internal static bool TryStep(Transform actor, Vector3 target, float step, int mask, Predicate<Collider> ignore, out Vector3 next)
            => TryStep(actor, target, step, mask, ignore, out next, out _);
        internal static bool TryStep(Transform actor, Vector3 target, float step, int mask, Predicate<Collider> ignore, out Vector3 next, out BlacksiteStepFailure failure)
            => TryStepFrom(actor, actor.position, target, step, mask, ignore, out next, out failure);
        // #874 N5: the same step taken from a stated pose rather than the actor's own, so a planner
        // can ask whether a walk would be refused before committing a body to it (ProbeWalk).
        private static bool TryStepFrom(Transform actor, Vector3 from, Vector3 target, float step, int mask, Predicate<Collider> ignore, out Vector3 next, out BlacksiteStepFailure failure)
        {
            next = Vector3.MoveTowards(from, new Vector3(target.x, from.y, target.z), Mathf.Min(step, MaxStride));
            Vector3 origin = next + Vector3.up * GroundProbeLift;
            if (!TrySupportingGround(origin, from.y, mask, out var ground, out failure))
            {
                // A foot can bridge the narrow bevel/seam between two supported surfaces.
                // This remains smaller than the actor footprint and cannot bridge a terrain gap.
                BlacksiteHitchTracer.CountPhysicsQuery();
                if(!Physics.SphereCast(origin,.08f,Vector3.down,out var bevel,1.1f,mask,QueryTriggerInteraction.Ignore)
                    || bevel.normal.y<WalkableNormalY || Mathf.Abs(bevel.point.y-from.y)>StepHeight)return false;
                ground = bevel; failure = default;
            }
            next.y = ground.point.y;
            if (ClearSegment(actor,from,next,mask,ignore,out var blocker)) return true;
            failure = new BlacksiteStepFailure { Reason = "swept body path obstructed", Blocker = blocker };
            return false;
        }
        // The longest single hand step TryStep takes, whatever step length it is handed.
        private const float MaxStride = .15f;
        // #874 N5: a probe walk's budget. 64 strides is 9.6 m, four times the 2.4 m commit range a
        // hand-stepped threshold starts from, so a probe that runs out is a walk nobody takes.
        private const int MaxProbeStrides = 64;
        /// <summary>
        /// #874 N5: walk <paramref name="from"/> to <paramref name="target"/> with exactly the steps
        /// <see cref="TryStep"/> would take - same stride, same supporting-ground rule, same swept
        /// body - without moving anything, and report the first step that would be refused.
        /// </summary>
        internal static bool ProbeWalk(Transform actor, Vector3 from, Vector3 target, int mask, Predicate<Collider> ignore, out BlacksiteStepFailure failure)
        {
            failure = default;
            Vector3 position = from;
            for (int i = 0; i < MaxProbeStrides; i++)
            {
                float dx = target.x - position.x, dz = target.z - position.z;
                if (dx * dx + dz * dz <= .0001f) return true;
                if (!TryStepFrom(actor, position, target, MaxStride, mask, ignore, out position, out failure)) return false;
            }
            failure = new BlacksiteStepFailure { Reason = "probe walk exceeded its stride budget" };
            return false;
        }
        // A single first-hit downward ray takes only the topmost surface. When that surface is a
        // bevel or seam face in a non-convex deck mesh the step is refused outright, even though a
        // supported floor sits a centimetre behind it. Collect every hit instead and keep the
        // highest supported one still within a step of the foot: moon navigation and terrain often
        // survive within centimetres beneath a landed deck, and dropping to those would put the
        // member under the catwalk it is standing on.
        internal static bool TrySupportingGround(Vector3 origin, float footY, int mask, out RaycastHit ground, out BlacksiteStepFailure failure, float distance = 1.1f, float maxDelta = StepHeight, Transform ignoreRoot = null)
        {
            ground = default; failure = default;
            BlacksiteHitchTracer.CountPhysicsQuery();
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, Hits, distance, mask, QueryTriggerInteraction.Ignore);
            if (count >= Hits.Length) { failure.Reason = "ground query saturated"; return false; }
            float best = float.NegativeInfinity, nearestDrop = 0f; Collider nearest = null;
            for (int i = 0; i < count; i++)
            {
                var hit = Hits[i];
                if (hit.collider == null || hit.normal.y < WalkableNormalY || (ignoreRoot != null && hit.transform.IsChildOf(ignoreRoot))) continue;
                float drop = Mathf.Abs(hit.point.y - footY);
                if (drop > maxDelta) { if (nearest == null || drop < nearestDrop) { nearest = hit.collider; nearestDrop = drop; } continue; }
                if (hit.point.y > best) { best = hit.point.y; ground = hit; }
            }
            if (best > float.NegativeInfinity) return true;
            failure = nearest != null
                ? new BlacksiteStepFailure { Reason = $"nearest supported ground is {nearestDrop:0.00}m from the foot", Blocker = nearest }
                : new BlacksiteStepFailure { Reason = "no supported ground under the step" };
            return false;
        }
        // Native outdoor navigation is a height approximation. Test the supported body poses
        // against clutter, rather than treating a shallowly buried navmesh foot as a collision.
        internal static bool ClearNavigationSegment(Transform actor, Vector3 from, Vector3 to, int mask, Predicate<Collider> ignore, out Collider blocker)
        {
            if (BlacksiteLandingGeometry.Ground(from, .6f, mask, null, out var start)
                && BlacksiteLandingGeometry.Ground(to, .6f, mask, null, out var end)
                && Mathf.Abs(start.point.y - from.y) <= .6f && Mathf.Abs(end.point.y - to.y) <= .6f)
                return ClearSegment(actor, start.point, end.point, mask, ignore, out blocker);
            return ClearSegment(actor, from, to, mask, ignore, out blocker);
        }

        internal static bool ClearSegment(Transform actor, Vector3 from, Vector3 to, int mask, Predicate<Collider> ignore, out Collider blocker)
        {
            // Sweep along the supported three-dimensional step. A horizontal sweep on an
            // uphill path intersects the hillside even when both body poses are clear.
            blocker = null; Vector3 delta = to - from;
            if (delta.sqrMagnitude < .000001f) return true;
            Vector3 direction = delta.normalized; int column = -1;
            Vector3 supportPoint = to + direction * BodyRadius;
            BlacksiteHitchTracer.CountPhysicsQuery();
            int count = Physics.CapsuleCastNonAlloc(from+Vector3.up*.48f,from+Vector3.up*1.8f,
                .32f,direction,Hits,delta.magnitude,mask,QueryTriggerInteraction.Ignore);
            if (count >= Hits.Length) return false;
            for (int i=0;i<count;i++)
            {
                var hit=Hits[i]; var c=hit.collider;
                if(c==null || c.transform.IsChildOf(actor) || hit.normal.y>WalkableNormalY || (ignore!=null && ignore(c))) continue;
                // The capsule reaches a riser before its foot centre reaches the tread. An
                // initial overlap has no usable hit point, so probe the capsule's forward reach.
                if (Steppable(c, hit.distance > .001f ? hit.point + direction * BodyRadius : supportPoint, from, to, mask, ref column)) continue;
                if ((hit.distance <= .001f || hit.point.y <= Mathf.Max(from.y,to.y)+.18f) && !BlocksAt(c, from, to, supportPoint, mask, ref column)) continue;
                blocker=c;return false;
            }
            BlacksiteHitchTracer.CountPhysicsQuery();
            int overlaps = Physics.OverlapCapsuleNonAlloc(to+Vector3.up*.48f,to+Vector3.up*1.8f,.32f,Overlaps,mask,QueryTriggerInteraction.Ignore);
            if (overlaps >= Overlaps.Length) return false;
            for(int i=0;i<overlaps;i++)
            {
                var c=Overlaps[i];
                if(c==null || c.transform.IsChildOf(actor) || c.bounds.max.y<=to.y+.18f || (ignore!=null && ignore(c))) continue;
                if (!BlocksAt(c, from, to, supportPoint, mask, ref column)) continue;
                blocker=c;return false;
            }
            return true;
        }
    }
}
