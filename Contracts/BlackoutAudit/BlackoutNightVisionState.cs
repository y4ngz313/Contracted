using UnityEngine;

namespace Y4NGZCompany.Contracts.BlackoutAudit
{
    internal readonly struct BlackoutNightVisionState
    {
        internal readonly bool Enabled;
        internal readonly float Intensity;
        internal readonly float Range;

        internal BlackoutNightVisionState(Light light)
        {
            Enabled = light != null && light.enabled;
            Intensity = light != null ? light.intensity : 0f;
            Range = light != null ? light.range : 0f;
        }
    }
}
