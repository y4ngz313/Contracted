using System.Collections.Generic;
using System.Linq;
using DunGen;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Facility.Interior
{
    internal readonly struct InteriorApproachAnchor
    {
        internal readonly Vector3 Position;
        internal readonly Vector3 RoomSidePosition;
        internal readonly Vector3 ApproachSidePosition;
        internal readonly Quaternion Rotation;
        internal readonly float Width;
        internal readonly float Height;
        internal readonly string Source;

        internal InteriorApproachAnchor(
            Vector3 position,
            Vector3 roomSidePosition,
            Vector3 approachSidePosition,
            Quaternion rotation,
            float width,
            float height,
            string source)
        {
            Position = position;
            RoomSidePosition = roomSidePosition;
            ApproachSidePosition = approachSidePosition;
            Rotation = rotation;
            Width = width;
            Height = height;
            Source = source;
        }
    }

    internal static class InteriorApproachDetector
    {
        internal static List<InteriorApproachAnchor> FindApproaches(InteriorTileSelection selection, bool emitLog = true)
        {
            var anchors = new List<InteriorApproachAnchor>();
            if (selection.Tile == null)
                return anchors;

            AddDoorwayAnchors(selection, anchors);
            if (anchors.Count == 0)
                AddBoundsFallbackAnchors(selection, anchors);

            if (emitLog)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Interior injection found {anchors.Count} approach anchor(s) for '{selection.TileName}': {string.Join(" | ", anchors.Select(FormatAnchor))}.");
            }
            return anchors;
        }

        private static void AddDoorwayAnchors(InteriorTileSelection selection, List<InteriorApproachAnchor> anchors)
        {
            if (selection.Tile?.UsedDoorways == null)
                return;

            for (int i = 0; i < selection.Tile.UsedDoorways.Count; i++)
            {
                Doorway doorway = selection.Tile.UsedDoorways[i];
                if (doorway == null || doorway.transform == null || doorway.ConnectedDoorway == null || doorway.ConnectedDoorway.transform == null)
                    continue;

                Vector3 roomBase = InteriorAnchorService.ResolveFloorPosition(doorway.transform.position);
                Vector3 approachBase = InteriorAnchorService.ResolveFloorPosition(doorway.ConnectedDoorway.transform.position);
                Vector3 doorwayDirection = roomBase - approachBase;
                doorwayDirection.y = 0f;
                if (doorwayDirection.sqrMagnitude < 0.001f)
                    doorwayDirection = selection.FloorPosition - approachBase;
                doorwayDirection.y = 0f;
                if (doorwayDirection.sqrMagnitude < 0.001f)
                    doorwayDirection = doorway.transform.forward;
                doorwayDirection.y = 0f;
                if (doorwayDirection.sqrMagnitude < 0.001f)
                    doorwayDirection = Vector3.forward;
                doorwayDirection.Normalize();

                Vector3 roomSide = SampleNavMeshOrFallback(roomBase + doorwayDirection * 0.6f, roomBase + doorwayDirection * 0.6f);
                Vector3 approachSide = SampleNavMeshOrFallback(approachBase - doorwayDirection * 0.6f, approachBase - doorwayDirection * 0.6f);
                Vector3 facing = roomSide - approachSide;
                facing.y = 0f;
                if (facing.sqrMagnitude < 0.001f)
                    facing = selection.FloorPosition - approachSide;
                facing.y = 0f;
                Quaternion rotation = facing.sqrMagnitude > 0.001f
                    ? Quaternion.LookRotation(facing.normalized, Vector3.up)
                    : selection.Rotation;

                Vector3 gateCenter = SampleNavMeshOrFallback((roomSide + approachSide) * 0.5f, roomBase);
                float gateWidth = 2.8f;
                float gateHeight = 3.4f;
                TryAddDistinctAnchor(
                    anchors,
                    new InteriorApproachAnchor(gateCenter, roomSide, approachSide, rotation, gateWidth, gateHeight, $"doorway:{i}"));
            }
        }

        private static void AddBoundsFallbackAnchors(InteriorTileSelection selection, List<InteriorApproachAnchor> anchors)
        {
            Bounds bounds = selection.Bounds;
            Vector3 center = selection.FloorPosition;
            var directions = new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left };
            for (int i = 0; i < directions.Length; i++)
            {
                Vector3 direction = directions[i];
                Vector3 edge = bounds.center + Vector3.Scale(bounds.extents + new Vector3(0.8f, 0f, 0.8f), direction);
                edge.y = center.y;
                Vector3 roomSide = SampleNavMeshOrFallback(edge - direction * 1.1f, center);
                Vector3 approachSide = SampleNavMeshOrFallback(edge + direction * 1.4f, edge + direction * 1.4f);
                if (Vector3.Distance(roomSide, approachSide) < 0.75f)
                    continue;

                Quaternion rotation = Quaternion.LookRotation((roomSide - approachSide).normalized, Vector3.up);
                Vector3 gateCenter = SampleNavMeshOrFallback((roomSide + approachSide) * 0.5f, edge);
                TryAddDistinctAnchor(
                    anchors,
                    new InteriorApproachAnchor(gateCenter, roomSide, approachSide, rotation, 2.6f, 3.4f, $"bounds:{i}"));
            }
        }

        private static Vector3 SampleNavMeshOrFallback(Vector3 samplePosition, Vector3 fallback)
        {
            if (NavMesh.SamplePosition(samplePosition, out NavMeshHit hit, 3.25f, NavMesh.AllAreas))
                return hit.position;
            return fallback;
        }

        private static void TryAddDistinctAnchor(List<InteriorApproachAnchor> anchors, InteriorApproachAnchor candidate)
        {
            for (int i = 0; i < anchors.Count; i++)
            {
                if (Vector3.Distance(anchors[i].Position, candidate.Position) < 1.2f)
                    return;
            }

            anchors.Add(candidate);
        }

        private static string FormatAnchor(InteriorApproachAnchor anchor)
        {
            return $"{anchor.Source}@{anchor.Position.x:0.0},{anchor.Position.y:0.0},{anchor.Position.z:0.0}";
        }
    }
}
