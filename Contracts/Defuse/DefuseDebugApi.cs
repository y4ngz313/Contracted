using System;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using UnityEngine;

namespace Y4NGZCompany.Contracts.Defuse
{
    public static class DefuseDebugApi
    {
        public static GameObject CreatePlacementPreview()
        {
            return DefuseContract.CreatePlacementPreview();
        }

        public static string GetStatus()
        {
            if (AuthoredInteriorPlacementStore.TryResolveFirstPose(AuthoredInteriorPlacementKinds.DeadDropBomb, out AuthoredInteriorPlacementPose pose))
                return $"Defuse placement resolves to {pose.Record?.flowName} / {pose.Tile?.name}.";

            return AuthoredInteriorPlacementStore.BuildStatus();
        }

        public static bool TryGetSavedPlacement(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (AuthoredInteriorPlacementStore.TryResolveFirstPose(AuthoredInteriorPlacementKinds.DeadDropBomb, out AuthoredInteriorPlacementPose canonical))
            {
                position = canonical.WorldPosition;
                rotation = canonical.WorldRotation;
                return true;
            }

            return false;
        }

        public static string SavePlacement(Transform transform)
        {
            return AuthoredInteriorPlacementStore.RecordPlacement(
                    transform,
                    AuthoredInteriorPlacementKinds.DeadDropBomb,
                    "bomb1",
                    "{}",
                    out string message)
                ? message
                : "save failed: " + message;
        }
    }

    [Obsolete("Use DefuseDebugApi.")]
    public static class DeadDropDebugApi
    {
        public static GameObject CreatePlacementPreview()
        {
            return DefuseDebugApi.CreatePlacementPreview();
        }

        public static string GetStatus()
        {
            return DefuseDebugApi.GetStatus();
        }

        public static bool TryGetSavedPlacement(out Vector3 position, out Quaternion rotation)
        {
            return DefuseDebugApi.TryGetSavedPlacement(out position, out rotation);
        }

        public static string SavePlacement(Transform transform)
        {
            return DefuseDebugApi.SavePlacement(transform);
        }
    }
}
