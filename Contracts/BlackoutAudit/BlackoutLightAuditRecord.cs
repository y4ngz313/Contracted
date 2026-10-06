using UnityEngine;

namespace Y4NGZCompany.Contracts.BlackoutAudit
{
    internal sealed class BlackoutLightAuditRecord
    {
        internal readonly Light Light;
        internal readonly string Path;
        internal readonly string Tags;
        internal readonly float PlayerDistance;
        internal readonly float CameraDistance;
        internal readonly float ViewDot;
        internal readonly int Priority;

        internal BlackoutLightAuditRecord(
            Light light,
            string path,
            string tags,
            float playerDistance,
            float cameraDistance,
            float viewDot,
            int priority)
        {
            Light = light;
            Path = path;
            Tags = tags;
            PlayerDistance = playerDistance;
            CameraDistance = cameraDistance;
            ViewDot = viewDot;
            Priority = priority;
        }
    }
}
