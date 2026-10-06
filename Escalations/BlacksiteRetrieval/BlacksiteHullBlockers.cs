using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal readonly struct BlacksiteHullBlockerSpec
    {
        internal readonly string Label;
        // Ship-local centre and size of the carving box.
        internal readonly Vector3 Center, Size;
        internal readonly bool Fixed;
        internal BlacksiteHullBlockerSpec(string label, Vector3 center, Vector3 size, bool isFixed = true)
        { Label = label; Center = center; Size = size; Fixed = isFixed; }
        internal Bounds LocalBounds => new Bounds(Center, Size);
    }
    // #872: which solid ship pieces carve the moon navmesh that survives beneath a landed hull.
    // Pure of networking and diagnostics so the Unity fixture can prove the specs against the
    // actual bundle. Two full-length side boxes and a full-width rear box carve the entire hull
    // footprint under the deck, leaving only the boarding opening (|x| < 2.1) and, forward of the
    // rear box, the aisle corridor the squad walks by hand; the closet and one data-driven box per
    // railing collider (sized to its ship-local bounds) cover the rest. Because the carve reaches
    // the beam on both sides and past the stern, the moon navmesh that survives beneath the hull is
    // a cul-de-sac reachable only through the boarding opening: ground navigation can no longer
    // route a member in under the aft hull and strand it against the rails.
    internal static class BlacksiteHullBlockers
    {
        // #873: the boxes sit entirely below the deck plate (top -0.07), so they carve only the moon
        // navmesh that survives under the raised hull and never the deck, its side catwalks or the
        // ladder tops; the deck is a measured height above the moon ground, so height keeps them apart.
        internal const float BlockerTopY = -.35f, BlockerBottomY = -4.5f, RailPadding = .3f;
        internal const float BlockerCentreY = (BlockerTopY + BlockerBottomY) * .5f, BlockerHeight = BlockerTopY - BlockerBottomY;
        // The boarding opening cut by asset-pipeline/BlacksiteShipBuild.cs (|x| < 2.1 beyond z 11.1).
        internal const float OpeningHalfWidth = 2.1f, OpeningZ = 11.1f;
        // The corridor the squad walks by hand: the aisle and the door, out past the lip to the drop.
        internal const float AisleHalfWidth = 1.7f, AisleRearZ = -8f, AisleFrontZ = 12.45f, AisleBeyondSill = 2.8f;
        // The landed hull's ship-local footprint: the deck plate spans x -6.08..6.12 and
        // z -11.64..11.80, widened by the physical step probe radius (.32) and a little margin.
        internal const float FootprintHalfWidth = 6.6f, FootprintHalfLength = 12.6f;
        // How far a ship guard's patrol point stays clear of that footprint.
        internal const float GuardClearance = 3f;
        // Forward face of the rear box. Kept aft of AisleRearZ so IntersectsAisle rejects it and a
        // full-width stern carve stays legal.
        internal const float RearFrontZ = AisleRearZ - .4f;
        internal static readonly BlacksiteHullBlockerSpec[] Fixed =
        {
            // The hull beams outboard of the boarding opening, bow to stern: x 2.1..6.6, z -12.6..12.6.
            // The boxes stop below the plate, so deck navigation survives. Preflight must reject
            // ground ladder mounts inside these boxes; the port rungs sit inside this footprint.
            new BlacksiteHullBlockerSpec("HullPort", new Vector3(-(FootprintHalfWidth + OpeningHalfWidth) * .5f, BlockerCentreY, 0),
                new Vector3(FootprintHalfWidth - OpeningHalfWidth, BlockerHeight, FootprintHalfLength * 2f)),
            new BlacksiteHullBlockerSpec("HullStarboard", new Vector3((FootprintHalfWidth + OpeningHalfWidth) * .5f, BlockerCentreY, 0),
                new Vector3(FootprintHalfWidth - OpeningHalfWidth, BlockerHeight, FootprintHalfLength * 2f)),
            // The stern, full width: x -6.6..6.6, z -12.6..-8.4.
            new BlacksiteHullBlockerSpec("Rear", new Vector3(0, BlockerCentreY, (RearFrontZ - FootprintHalfLength) * .5f),
                new Vector3(FootprintHalfWidth * 2f, BlockerHeight, RearFrontZ + FootprintHalfLength)),
            new BlacksiteHullBlockerSpec("DoorPostLeft", new Vector3(-2.95f, BlockerCentreY, 8.9f), new Vector3(1.8f, BlockerHeight, .9f)),
            new BlacksiteHullBlockerSpec("DoorPostRight", new Vector3(2.95f, BlockerCentreY, 8.9f), new Vector3(1.8f, BlockerHeight, .9f)),
        };
        // Unity's carving volume must stay upright. A banked box expands vertically during
        // carving and can erase the downhill catwalk, despite a local-space top below the plate.
        // Use an upright box whose top stays below the lowest deck point over its footprint.
        internal static void ConfigureGroundObstacle(Transform ship, NavMeshObstacle obstacle, BlacksiteHullBlockerSpec spec)
        {
            Quaternion yaw = Quaternion.Euler(0, ship.eulerAngles.y, 0);
            Quaternion inverse = Quaternion.Inverse(yaw);
            Vector3 low = Vector3.one * float.PositiveInfinity, high = -low;
            float top = float.PositiveInfinity, bottom = float.PositiveInfinity;
            for (int i = 0; i < 4; i++)
            {
                float x = spec.Center.x + ((i & 1) == 0 ? -1 : 1) * spec.Size.x * .5f;
                float z = spec.Center.z + ((i & 2) == 0 ? -1 : 1) * spec.Size.z * .5f;
                Vector3 projected = inverse * ship.TransformPoint(new Vector3(x, 0, z));
                low = Vector3.Min(low, projected); high = Vector3.Max(high, projected);
                top = Mathf.Min(top, ship.TransformPoint(new Vector3(x, BlockerTopY, z)).y);
                bottom = Mathf.Min(bottom, ship.TransformPoint(new Vector3(x, BlockerBottomY, z)).y);
            }
            Vector3 center = yaw * ((low + high) * .5f);center.y = (top + bottom) * .5f;
            obstacle.transform.SetPositionAndRotation(center, yaw);
            obstacle.size = new Vector3(high.x-low.x, top-bottom, high.z-low.z);
        }
        // Whether a ship-local point lies within the carved hull footprint, inflated by pad.
        internal static bool InsideFootprint(Vector3 local, float pad)
            => Mathf.Abs(local.x) < FootprintHalfWidth + pad && Mathf.Abs(local.z) < FootprintHalfLength + pad;
        internal static bool InsideGroundFootprint(Vector3 origin, Quaternion rotation, Vector3 point, float padding)
        {
            Quaternion inverseYaw = Quaternion.Inverse(Quaternion.Euler(0, rotation.eulerAngles.y, 0));
            Vector3 local = inverseYaw * (point - origin);
            Vector3 extents = Vector3.zero;
            for (int i = 0; i < 4; i++)
            {
                Vector3 corner = inverseYaw * (rotation * new Vector3((i & 1) == 0 ? -FootprintHalfWidth : FootprintHalfWidth, 0,
                    (i & 2) == 0 ? -FootprintHalfLength : FootprintHalfLength));
                extents.x = Mathf.Max(extents.x, Mathf.Abs(corner.x)); extents.z = Mathf.Max(extents.z, Mathf.Abs(corner.z));
            }
            return Mathf.Abs(local.x) < extents.x + padding && Mathf.Abs(local.z) < extents.z + padding;
        }
        // Landing preflight runs before these obstacles exist. A ladder foot inside a fixed
        // ground carve cannot become a valid return route merely because the bare moon has nav.
        internal static bool InsideFixedGroundCarve(Vector3 local)
        {
            foreach (var spec in Fixed)
                if (Mathf.Abs(local.x - spec.Center.x) <= spec.Size.x * .5f
                    && Mathf.Abs(local.z - spec.Center.z) <= spec.Size.z * .5f) return true;
            return false;
        }
        internal static bool IsRailCollider(Collider collider)
        {
            if (collider == null || collider.isTrigger) return false;
            string name = collider.name;
            return name == "BoardingRailSide" || name.Contains("ShipRail") || name.Contains("RailPost");
        }
        // Transform the collider's own bounds once. Transforming its already-expanded world
        // AABB back into a banked ship inflates rail carvings across the doorway.
        internal static Bounds LocalBounds(Transform ship, Collider collider)
        {
            Bounds boundsSource = collider is BoxCollider box ? new Bounds(box.center, box.size)
                : collider is MeshCollider mesh && mesh.sharedMesh != null ? mesh.sharedMesh.bounds : collider.bounds;
            bool localSource = collider is BoxCollider || (collider is MeshCollider mc && mc.sharedMesh != null);
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), max = -min;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3((i & 1) == 0 ? boundsSource.min.x : boundsSource.max.x, (i & 2) == 0 ? boundsSource.min.y : boundsSource.max.y, (i & 4) == 0 ? boundsSource.min.z : boundsSource.max.z);
                Vector3 local = ship.InverseTransformPoint(localSource ? collider.transform.TransformPoint(corner) : corner);
                min = Vector3.Min(min, local); max = Vector3.Max(max, local);
            }
            var bounds = new Bounds(); bounds.SetMinMax(min, max); return bounds;
        }
        internal static bool InsideBoardingOpening(Bounds local)
            => local.min.x > -OpeningHalfWidth && local.max.x < OpeningHalfWidth && local.center.z > OpeningZ;
        internal static bool IntersectsAisle(Bounds local)
            => local.min.x < AisleHalfWidth && local.max.x > -AisleHalfWidth && local.max.z > AisleRearZ && local.min.z < AisleFrontZ + AisleBeyondSill;
        // notes receives one line per railing collider considered (bounds and whether it was used),
        // for the caller to log once.
        internal static List<BlacksiteHullBlockerSpec> Specs(Transform ship, Transform closet, List<string> notes = null)
        {
            var specs = new List<BlacksiteHullBlockerSpec>(Fixed);
            var closetRenderer = closet != null ? closet.GetComponent<Renderer>() : null;
            if (closetRenderer != null)
                specs.Add(new BlacksiteHullBlockerSpec("Closet", ship.InverseTransformPoint(closetRenderer.bounds.center), new Vector3(1.1f, 3f, 2.3f)));
            int index = 0;
            foreach (var collider in ship.GetComponentsInChildren<Collider>(true))
            {
                if (!IsRailCollider(collider) || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                Bounds local = LocalBounds(ship, collider);
                string skip = InsideBoardingOpening(local) ? "inside the boarding opening"
                    : IntersectsAisle(local) ? "intersects the aisle corridor" : null;
                notes?.Add($"rail collider {collider.name}: localMin={local.min}, localMax={local.max}, {(skip ?? "carved")}.");
                if (skip != null) continue;
                specs.Add(new BlacksiteHullBlockerSpec("Rail" + index++ + "_" + collider.name,
                    new Vector3(local.center.x, BlockerCentreY, local.center.z),
                    new Vector3(local.size.x + RailPadding * 2f, BlockerHeight, local.size.z + RailPadding * 2f), isFixed: false));
            }
            return specs;
        }
    }
}
