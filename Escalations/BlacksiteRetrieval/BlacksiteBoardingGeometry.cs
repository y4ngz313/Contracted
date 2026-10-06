using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #873: the ship stands on its gear with the deck a measured height above the ground. The
    // squad leaves by dropping off the catwalk lip onto the moon ground and returns by climbing
    // one of the hull's short ladders; there is no plate, ramp or sill.
    internal static class BlacksiteBoardingGeometry
    {
        internal static readonly Vector3 Lip = new Vector3(0, -.03f, 11.75f);
        // Ship-local z where an outbound walk ends before the drop: on the plate, short of the edge.
        internal static readonly Vector3 LipStep = new Vector3(0, 0, 11.55f);
        private static readonly Collider[] Overlaps = new Collider[96];
        private static readonly RaycastHit[] Hits = new RaycastHit[32];
        private static readonly float[] DropColumns = { -1.2f, 0f, 1.2f };
        internal const string LegacyPlateName = "BlacksiteAccessRamp";
        internal const int Options = 1;
        // Drop landing, ladder foot and ground staging for a supported origin. ignoreRoot is the
        // landed ship when it exists; the search runs before it does.
        internal static bool TryAccess(Vector3 origin, Quaternion rotation, int mask, float footToDeck, out BlacksiteLandingAccess access, out Vector3 exit, out string reason, Transform ignoreRoot = null, int members = 6, bool deferGroundRoutes = false)
        {
            access = default; exit = default; access.LadderIndex = -1; access.FootToDeck = footToDeck;
            if (!TryDropZone(origin, rotation, mask, footToDeck, ignoreRoot, out var landing, out access.DropHeight, out reason)) return false;
            Vector3 mount = default; string portWhy = null, starboardWhy = null;
            foreach (int ladder in new[] { 1, 0 })
            {
                if (TryLadderFoot(origin, rotation, ladder, mask, footToDeck, ignoreRoot, out mount, out var why)) { access.LadderIndex = ladder; break; }
                if (ladder == 1) portWhy = why; else starboardWhy = why;
            }
            if (!access.HasLadder) { reason = "no ladder foot on walkable ground (port: " + portWhy + "; starboard: " + starboardWhy + ")"; return false; }
            var inverse = Quaternion.Inverse(rotation);
            var ladderSpec = BlacksiteLandingGearPolicy.Ladders[access.LadderIndex];
            access.DropLandingLocal = inverse * (landing - origin);
            access.LadderMountLocal = inverse * (mount - origin);
            access.LadderTopLocal = new Vector3(ladderSpec.X - ladderSpec.Outward * BlacksiteLandingGearPolicy.LadderTopIn, 0, ladderSpec.Z);
            Vector3 outside = landing + rotation * Vector3.forward * 2f;
            if (!BlacksiteLandingGeometry.TryFloorNav(outside, mask, out exit, .6f)) { reason = "no physical ground beyond the drop landing"; return false; }
            // #1305: touchdown's drop-to-staging leg, proven at the candidate pose (Landslide's Cube (28)).
            if (!BlacksiteShipNavigation.TryDropToStaging(landing, exit, mask, out reason)) return false;
            if (deferGroundRoutes) { reason = null; return true; }
            if (!BlacksiteShipNavigation.TryEgressSlots(rotation, exit, members, mask, out _, out reason)) return false;
            if (!BlacksiteShipNavigation.TryLadderGroundRoute(origin, rotation, BlacksitePhysicalStep.ProbeTransform, exit, access, mask, out _, out reason)) return false;
            reason = null; return true;
        }
        internal static bool TryDropZone(Vector3 origin, Quaternion rotation, int mask, float footToDeck, Transform ignoreRoot, out Vector3 landing, out float height, out string reason)
        {
            landing = default; height = 0f;
            float high = float.NegativeInfinity, low = float.PositiveInfinity;
            foreach (float x in DropColumns)
            {
                Vector3 expected = origin + rotation * new Vector3(x, -footToDeck, BlacksiteLandingGearPolicy.DropZ);
                if (!BlacksiteLandingGeometry.ProbeGround(expected, 2.5f, mask, ignoreRoot, out var ground, out reason)) { reason = "drop landing has no ground"; return false; }
                if (ground.normal.y < .8f) { reason = "drop landing slope exceeds 37 degrees"; return false; }
                high = Mathf.Max(high, ground.point.y); low = Mathf.Min(low, ground.point.y);
                if (x == 0f) landing = ground.point;
            }
            if (high - low > BlacksiteLandingGearPolicy.DropUnevenMax) { reason = "drop landing uneven"; return false; }
            height = (origin + rotation * Lip).y - landing.y;
            if (height < BlacksiteLandingGearPolicy.DropMinHeight) { reason = "ground rises to the lip at the drop point"; return false; }
            float ceiling = BlacksiteLandingGearPolicy.DropCeiling(footToDeck);
            if (height > ceiling) { reason = "lip drop " + height.ToString("0.00") + "m exceeds " + ceiling.ToString("0.00") + "m"; return false; }
            // Something standing on the landing is named first; the fall column encloses it.
            if (!BlacksitePhysicalStep.CanOccupy(BlacksitePhysicalStep.ProbeTransform, landing, mask, out var blocker, Ignoring(ignoreRoot)))
            { reason = "drop landing occupied by " + (blocker != null ? blocker.name : "saturated query"); return false; }
            // The fall column: from a skin above the highest landing probe (a cross-slope raises the
            // ground across the column's width) up to head height over the lip, inside the front
            // railing remnants. It proves nothing hangs in the fall path; the probes tested the ground.
            float bottom = high - origin.y + .15f, top = Lip.y + 1.9f;
            int count = Physics.OverlapBoxNonAlloc(origin + rotation * new Vector3(0, (bottom + top) * .5f, 12.2f),
                new Vector3(1.3f, (top - bottom) * .5f, .7f), Overlaps, rotation, mask, QueryTriggerInteraction.Ignore);
            if (count == Overlaps.Length) { reason = "drop column clearance query saturated"; return false; }
            for (int i = 0; i < count; i++) if (Overlaps[i] != null && (ignoreRoot == null || !Overlaps[i].transform.IsChildOf(ignoreRoot)))
            { reason = "drop column intersects " + Overlaps[i].name; return false; }
            if (!BlacksiteLandingGeometry.TryFloorNav(landing, mask, out landing, .6f)) { reason = "drop landing has no navigation"; return false; }
            reason = null; return true;
        }
        // The ship's own colliders are never an obstruction to its drop landing; the ladder foot is
        // the exception, where the catwalk plate over a mount is exactly what must be found.
        private static System.Predicate<Collider> Ignoring(Transform root) => root == null ? null : c => c.transform.IsChildOf(root);
        internal static bool TryLadderFoot(Vector3 origin, Quaternion rotation, int ladder, int mask, float footToDeck, Transform ignoreRoot, out Vector3 mount, out string reason)
        {
            var spec = BlacksiteLandingGearPolicy.Ladders[ladder];
            mount = origin + rotation * new Vector3(spec.X + spec.Outward * BlacksiteLandingGearPolicy.LadderMountOut, -footToDeck, spec.Z);
            if (!BlacksiteLandingGeometry.ProbeGround(mount, 2.5f, mask, ignoreRoot, out var ground, out reason)) { reason = "ladder " + spec.Side + " foot has no ground"; return false; }
            if (ground.normal.y < .8f) { reason = "ladder " + spec.Side + " foot slope exceeds 37 degrees"; return false; }
            mount = ground.point;
            float localHeight = (Quaternion.Inverse(rotation) * (mount - origin)).y;
            if (localHeight < BlacksiteLandingGearPolicy.LowestRung - .05f)
            { reason = "ladder " + spec.Side + " ground lies below the physical bottom rung"; return false; }
            if (!BlacksitePhysicalStep.CanOccupy(BlacksitePhysicalStep.ProbeTransform, mount, mask, out var blocker))
            { reason = "ladder " + spec.Side + " foot obstructed by " + (blocker != null ? blocker.name : "saturated query"); return false; }
            // Prove the route against the hull that will land here, not just bare moon nav.
            // At tall deck heights the port foot is physically occupiable but its navigation
            // is still removed by the fixed hull carve once the ship touches down.
            if (BlacksiteHullBlockers.InsideFixedGroundCarve(Quaternion.Inverse(rotation) * (mount - origin)))
            { reason = "ladder " + spec.Side + " foot lies inside planned hull navigation carve"; return false; }
            if (!BlacksiteLandingGeometry.TryFloorNav(mount, mask, out mount, .6f)) { reason = "ladder " + spec.Side + " foot has no navigation"; return false; }
            reason = null; return true;
        }
        // Whether a ship surface (the catwalk plate) hangs over a ground point: the fixture's
        // physical answer to the port-ladder cutout question.
        internal static bool ShipSurfaceAbove(Transform ship, Vector3 point, int mask, out string name)
        {
            name = null;
            int count = Physics.RaycastNonAlloc(point + Vector3.up * 2.4f, Vector3.down, Hits, 2.3f, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count && i < Hits.Length; i++)
                if (Hits[i].collider != null && Hits[i].normal.y > .5f && Hits[i].transform.IsChildOf(ship)) { name = Hits[i].collider.name; return true; }
            return false;
        }
        // Bundles authored before the sill landing still carry the plate; it must never render or collide.
        internal static void Apply(Transform ship)
        {
            if (ship == null) return;
            if (ship.GetComponent<BlacksiteShipNavigationRoot>() == null)
                ship.gameObject.AddComponent<BlacksiteShipNavigationRoot>();
            var plate = ship.Find(LegacyPlateName);
            if (plate != null && plate.gameObject.activeSelf) plate.gameObject.SetActive(false);
        }
        // The deck strip the squad walks by hand: aisle, door and porch.
        internal static bool OnDeck(Vector3 local)
            => Mathf.Abs(local.x) < 2.8f && local.z > -8f && local.z < 12f && Mathf.Abs(local.y) < .65f;
        // Anywhere a hand-driven passage may stand: the deck strip, the drop zone or the ladder zone.
        internal static bool WithinPassage(Vector3 local, in BlacksiteLandingAccess access)
        {
            if (OnDeck(local)) return true;
            if (Mathf.Abs(local.x) < 2.8f && local.z > Lip.z - .5f && local.z < BlacksiteLandingGearPolicy.DropZ + 2.8f
                && Mathf.Abs(local.y - access.DropLandingLocal.y) < .65f) return true;
            if (!access.HasLadder) return false;
            Vector3 toMount = local - access.LadderMountLocal;
            return Mathf.Abs(toMount.y) < .8f && new Vector2(toMount.x, toMount.z).magnitude < BlacksiteLandingGearPolicy.LadderZoneRadius;
        }
    }
}
