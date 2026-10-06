using System.Collections.Generic;
using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874: the deployed cargo container, the lip-drop landing, the passage corridor out of the
    // ship, both ladder feet, the ladder staging, the cart staging, the entrance door and the
    // outgoing entrance route are ONE layout. A crate site is a box reserved out of that layout,
    // never a ring sample that merely has navigation beside it. Free of netcode and diagnostics so
    // the Unity fixture copies this file untouched; the plan geometry lives in
    // BlacksiteCargoLayout.Lanes.cs.
    //
    // #874 Q5 (2026-09-25): a site is proven BEFORE the crate is set down on it and never after.
    internal static partial class BlacksiteCargoLayout
    {
        // How far the solid clearance probe is lifted off the floor the container stands on.
        internal const float GroundSkin = .08f;
        // Where the player/loader stands relative to the crate, and how many of the eight compass
        // approaches around it must stay occupiable so the cache remains player-accessible.
        internal const float AccessRing = .8f;
        internal const int AccessApproaches = 8, MinAccessApproaches = 3;
        // Candidate rings, widened per attempt, and the bounded retry schedule.
        internal static readonly float[] Rings = { 3f, 4f, 5f, 6f };
        internal const int CandidateLanes = 12, MaxAttempts = 10, ShortlistSize = 6;
        internal const float RingWiden = .75f, MaxRing = 12f, RetrySeconds = .5f, DeferredRetrySeconds = 10f;
        // Discrete lanes whose tier is at or above this are released: attempts 0-5 reserve every
        // lane, 6-7 release the cart staging, 8-9 also the ladder staging. The lip drop, the
        // passage corridor, both ladder feet, the entrance door and the entrance route are tier 0
        // and never released.
        internal static int SacrificedFrom(int attempt) => attempt < 6 ? 3 : attempt < 8 ? 2 : 1;
        internal static string Sacrificed(int attempt)
        {
            int from = SacrificedFrom(attempt);
            return from <= 1 ? "cart staging, ladder staging" : from <= 2 ? "cart staging" : "none";
        }

        // The crate's real footprint, in prefab-root local space: the union of its solid colliders,
        // falling back to its authored meshes only when the prefab carries no collider at all.
        internal static bool TryFootprint(GameObject prefab, out Vector3 center, out Vector3 half)
        {
            center = default; half = default;
            if (prefab == null) return false;
            Vector3 min = Vector3.one * float.PositiveInfinity, max = -min;
            foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
                if (collider != null && !collider.isTrigger && collider.enabled) Accumulate(prefab.transform, collider.transform, LocalSource(collider), ref min, ref max);
            if (min.x > max.x)
                foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                    if (filter != null && filter.sharedMesh != null && filter.GetComponent<Renderer>() != null)
                        Accumulate(prefab.transform, filter.transform, filter.sharedMesh.bounds, ref min, ref max);
            if (min.x > max.x) return false;
            center = (min + max) * .5f; half = (max - min) * .5f; return true;
        }
        private static Bounds LocalSource(Collider collider)
        {
            if (collider is BoxCollider box) return new Bounds(box.center, box.size);
            if (collider is SphereCollider sphere) return new Bounds(sphere.center, Vector3.one * sphere.radius * 2f);
            if (collider is CapsuleCollider capsule)
            {
                var size = Vector3.one * capsule.radius * 2f;
                size[capsule.direction] = Mathf.Max(capsule.height, capsule.radius * 2f);
                return new Bounds(capsule.center, size);
            }
            if (collider is MeshCollider mesh && mesh.sharedMesh != null) return mesh.sharedMesh.bounds;
            return new Bounds(Vector3.zero, Vector3.zero);
        }
        private static void Accumulate(Transform root, Transform source, Bounds local, ref Vector3 min, ref Vector3 max)
        {
            if (local.size.sqrMagnitude <= 0f) return;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? local.min.x : local.max.x, (i & 2) == 0 ? local.min.y : local.max.y, (i & 4) == 0 ? local.min.z : local.max.z);
                Vector3 point = root.InverseTransformPoint(source.TransformPoint(corner));
                min = Vector3.Min(min, point); max = Vector3.Max(max, point);
            }
        }

        // Every traversable lane a GROUND layout owes the squad, in world space: the hull's own
        // lanes (lip drop, passage, both ladder feet and their staging) and the lanes both modes
        // share (AddSharedLanes). A hover station reads CableLanes instead.
        internal static List<BlacksiteCargoLane> Reserve(Transform ship, in BlacksiteLandingAccess access, Vector3 rendezvous, Vector3 exit,
            Vector3[] groundRoute, Vector3 cartStaging, Vector3 entrance)
        {
            var lanes = new List<BlacksiteCargoLane>();
            Vector3 landing = ship.TransformPoint(access.DropLandingLocal), forward = Vector3.ProjectOnPlane(ship.forward, Vector3.up).normalized;
            lanes.Add(new BlacksiteCargoLane(BlacksiteCargoLaneKind.DropLanding, "lip-drop landing", landing, landing + forward * .9f, 1.3f, 0));
            lanes.Add(new BlacksiteCargoLane(BlacksiteCargoLaneKind.Passage, "passage corridor", landing, exit, 1.2f, 0));
            // Both ladder alternatives, not only the validated one: the re-landing proof may pick the other.
            for (int i = 0; i < BlacksiteLandingGearPolicy.Ladders.Length; i++)
            {
                var spec = BlacksiteLandingGearPolicy.Ladders[i];
                Vector3 mount = ship.TransformPoint(new Vector3(spec.X + spec.Outward * BlacksiteLandingGearPolicy.LadderMountOut, access.DropLandingLocal.y, spec.Z));
                Vector3 outward = Vector3.ProjectOnPlane(ship.right * spec.Outward, Vector3.up).normalized;
                lanes.Add(new BlacksiteCargoLane(BlacksiteCargoLaneKind.LadderFoot, "ladder " + spec.Side + " foot", mount, mount + outward * 1.2f, AgentRadius + LaneClearance, 0));
                lanes.Add(new BlacksiteCargoLane(BlacksiteCargoLaneKind.LadderStaging, "ladder " + spec.Side + " staging", mount + outward * 1.2f, mount + outward * 3.2f, 1.6f, 1));
            }
            AddSharedLanes(lanes, forward, rendezvous, exit, groundRoute, cartStaging, entrance);
            return lanes;
        }
        // Whether an oriented crate box intrudes into a lane's clearance envelope.
        internal static bool Intrudes(in BlacksiteCargoLane lane, Vector3 center, Quaternion rotation, Vector3 half)
        {
            float length = Vector3.Distance(lane.A, lane.B);
            int steps = Mathf.Clamp(Mathf.CeilToInt(length / .25f), 1, 240);
            Quaternion inverse = Quaternion.Inverse(rotation);
            for (int i = 0; i <= steps; i++)
            {
                Vector3 local = inverse * (Vector3.Lerp(lane.A, lane.B, i / (float)steps) - center);
                var clamped = new Vector3(Mathf.Clamp(local.x, -half.x, half.x), Mathf.Clamp(local.y, -half.y, half.y), Mathf.Clamp(local.z, -half.z, half.z));
                if ((local - clamped).sqrMagnitude < lane.Radius * lane.Radius) return true;
            }
            return false;
        }
        // #874 (2026-09-22, Rend): a loading route from the rendezvous must not run through the
        // crate's own footprint (Full11 log 5702 retained the cache on one). The footprint is widened
        // by the probe's own radius, the same body ClearSegment sweeps along that route.
        internal static Vector3 SweptFootprint(Vector3 footHalf)
            => new Vector3(footHalf.x + AgentRadius + .05f, footHalf.y, footHalf.z + AgentRadius + .05f);
        internal static bool RouteCrossesFootprint(Vector3[] corners, Vector3 center, Quaternion rotation, Vector3 half)
        {
            if (corners == null || corners.Length < 2) return false;
            Quaternion inverse = Quaternion.Inverse(rotation);
            for (int i = 1; i < corners.Length; i++)
                if (SegmentCrossesBox(inverse * (corners[i - 1] - center), inverse * (corners[i] - center), half)) return true;
            return false;
        }
        // A slab test in the box's own XZ plane. Height is ignored on purpose: every corner is a
        // floor point and the swept body stands taller than the crate, so any plan overlap is a hit.
        private static bool SegmentCrossesBox(Vector3 a, Vector3 b, Vector3 half)
        {
            float t0 = 0f, t1 = 1f;
            return Slab(a.x, b.x - a.x, half.x, ref t0, ref t1) && Slab(a.z, b.z - a.z, half.z, ref t0, ref t1);
        }
        private static bool Slab(float origin, float delta, float half, ref float t0, ref float t1)
        {
            if (Mathf.Abs(delta) < 1e-6f) return Mathf.Abs(origin) <= half;
            float near = (-half - origin) / delta, far = (half - origin) / delta;
            if (near > far) { float swap = near; near = far; far = swap; }
            t0 = Mathf.Max(t0, near); t1 = Mathf.Min(t1, far);
            return t0 <= t1;
        }

        // The hover cache and the soldiers use this one touchdown. They occupy it in sequence:
        // squad down, cache down, cache up, squad up. Never search another ring for a second cable.
        //
        // #874 Q5: the touchdown is the station's rope foot, so it is proven against the station's
        // lanes (CableLanes) by the same rule the landing search refused the station with. A foot
        // that fails is refused here, before the crate leaves the deck - never lowered and then
        // hauled back up.
        internal static bool TryCableSite(Vector3 touchdown, Quaternion rotation, Vector3 rendezvous, List<BlacksiteCargoLane> lanes,
            Vector3 footCenter, Vector3 footHalf, int mask, out Vector3 position, out string reason)
        {
            // Align the bottom of the measured crate, not its authored pivot, with the soldiers' feet.
            position = touchdown + Vector3.up * (footHalf.y - footCenter.y);
            if (!CableSiteClear(touchdown + rotation * footCenter, Diagonal(footHalf), lanes, out string blocked))
            { reason = "the rope-foot cargo footprint blocks " + blocked; return false; }
            for (int i = 0; i < 4; i++)
            {
                Vector3 corner = touchdown + rotation * new Vector3(
                    footCenter.x + ((i & 1) == 0 ? -footHalf.x : footHalf.x) * .8f, 0f,
                    footCenter.z + ((i & 2) == 0 ? -footHalf.z : footHalf.z) * .8f);
                if (!BlacksiteLandingGeometry.Ground(corner, GroundSkin * 2f, mask, null, out _))
                { reason = "shared cable touchdown does not support the cargo footprint"; return false; }
            }
            Vector3 center = position + rotation * footCenter;
            if (Physics.CheckBox(center + Vector3.up * GroundSkin, footHalf + Vector3.one * .05f,
                rotation, mask, QueryTriggerInteraction.Ignore))
            { reason = "shared cable touchdown is occupied"; return false; }
            // The touchdown and passage are deliberately occupied only while no soldier uses them.
            // Loading and player access still need actual floor and a route around the grounded box.
            return LoadingUsable(position, rotation, null, footCenter, footHalf, mask, rendezvous, true, out reason);
        }
        internal static Vector3 LoadingPoint(Vector3 site, Quaternion rotation, bool sharedCable)
            => site + rotation * (sharedCable ? Vector3.forward : Vector3.back) * LoadingReach;

        // The best site for this attempt, or the reason no site exists; on success reason names the
        // pick. sacrificed reports which lanes the attempt band released; the caller logs it.
        internal static bool TrySite(Transform ship, Quaternion rotation, Vector3 rendezvous, List<BlacksiteCargoLane> lanes,
            Vector3 footCenter, Vector3 footHalf, int mask, int attempt, out Vector3 floor, out string sacrificed, out string reason)
        {
            floor = default; sacrificed = Sacrificed(attempt); reason = "no ground within the widened rings";
            int from = SacrificedFrom(attempt);
            Vector3 reserved = Reserved(footHalf), solid = footHalf + Vector3.one * .05f;
            float diagonal = Diagonal(footHalf);
            var shortlist = new List<(Vector3 Point, float Score)>();
            // From the second attempt the base rings AND a widened copy: a more permissive band may take a near site.
            for (int ring = 0; ring < Rings.Length * (attempt == 0 ? 1 : 2); ring++)
                for (int lane = 0; lane < CandidateLanes; lane++)
                {
                    float radius = Mathf.Min(MaxRing, Rings[ring % Rings.Length] + (ring < Rings.Length ? 0f : attempt * RingWiden));
                    Vector3 desired = rendezvous + rotation * Quaternion.Euler(0, lane * (360f / CandidateLanes) + attempt * 7.5f, 0) * Vector3.forward * radius;
                    if (!BlacksiteLandingGeometry.TryFloorNav(desired, mask, out var hit, 1f)) continue;
                    Vector3 center = hit + rotation * footCenter;
                    // Lift the solid probe off the floor it stands on, which the crate box always touches.
                    if (Physics.CheckBox(center + Vector3.up * GroundSkin, solid, rotation, mask, QueryTriggerInteraction.Ignore))
                    { reason = "no ring bearing has room for the measured container"; continue; }
                    if (BlacksiteHullBlockers.InsideGroundFootprint(ship.position, ship.rotation, hit, diagonal + .3f))
                    { reason = "the reachable bearings lie inside the carved hull footprint"; continue; }
                    bool clear = true;
                    foreach (var reservation in lanes)
                    {
                        if (reservation.Tier >= from) continue;
                        if (Intrudes(reservation, center, rotation, reserved)) { clear = false; reason = "every ring bearing intrudes into a reserved lane (last: " + reservation.Label + ")"; break; }
                    }
                    if (!clear) continue;
                    if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(true), rendezvous, hit).Reachable)
                    { reason = "no complete route from the rendezvous point to the site"; continue; }
                    shortlist.Add((hit, -Vector3.Distance(hit, rendezvous)));
                }
            shortlist.Sort((a, b) => b.Score.CompareTo(a.Score));
            for (int i = 0; i < shortlist.Count && i < ShortlistSize; i++)
            {
                if (!LoadingUsable(shortlist[i].Point, rotation, lanes, footCenter, footHalf, mask, rendezvous, false, out reason)) continue;
                floor = shortlist[i].Point;
                reason = "shortlist=" + i;
                return true;
            }
            if (shortlist.Count > 0 && reason == null) reason = "shortlisted sites have no usable loading approach";
            return false;
        }
        // The crate must sit beside its loading lane, not on it: the loading position stays
        // occupiable, walkable from the rendezvous point without crossing the crate's own footprint,
        // out of every reserved lane, and the crate keeps enough open approaches for a player to
        // reach and open it.
        private static bool LoadingUsable(Vector3 site, Quaternion rotation, List<BlacksiteCargoLane> lanes, Vector3 footCenter, Vector3 footHalf, int mask, Vector3 rendezvous, bool sharedCable, out string reason)
        {
            Vector3 loading = LoadingPoint(site, rotation, sharedCable);
            if (!BlacksiteLandingGeometry.TryFloorNav(loading, mask, out loading, .6f)) { reason = "loading approach has no navigable floor"; return false; }
            if (!BlacksitePhysicalStep.CanOccupy(BlacksitePhysicalStep.ProbeTransform, loading, mask, out var blocker))
            { reason = "loading approach obstructed by " + (blocker != null ? blocker.name : "saturated query"); return false; }
            if (lanes != null)
                foreach (var lane in lanes)
                    if (lane.Tier == 0 && Intrudes(lane, loading, rotation, Vector3.one * AgentRadius))
                    { reason = "loading approach stands in " + lane.Label; return false; }
            if (!BlacksiteLandingGeometry.WalkRoute(BlacksitePhysicalStep.ProbeTransform, rendezvous, loading, mask, out var corners, out reason))
            { reason = "loading approach unreachable from the rendezvous point: " + reason; return false; }
            if (RouteCrossesFootprint(corners, site + rotation * footCenter, rotation, SweptFootprint(footHalf)))
            { reason = "loading approach crosses the container footprint"; return false; }
            if (CountApproaches(site, rotation, footHalf, mask) < MinAccessApproaches)
            { reason = "fewer than " + MinAccessApproaches + " open approaches around the container"; return false; }
            reason = null; return true;
        }
        internal static int CountApproaches(Vector3 site, Quaternion rotation, Vector3 footHalf, int mask)
        {
            int open = 0; float radius = Diagonal(footHalf) + AccessRing;
            for (int i = 0; i < AccessApproaches; i++)
            {
                Vector3 desired = site + rotation * Quaternion.Euler(0, i * (360f / AccessApproaches), 0) * Vector3.forward * radius;
                if (BlacksiteLandingGeometry.TryFloorNav(desired, mask, out var point, .6f)
                    && BlacksitePhysicalStep.CanOccupy(BlacksitePhysicalStep.ProbeTransform, point, mask)) open++;
            }
            return open;
        }
    }
}
