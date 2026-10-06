using System;
using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Hover sites are chosen from usable ground below the forward cable, not from hull support.
    // The cabin, gear, cable column, staging and the facility route still require physical proof.
    internal static class BlacksiteRappelGeometry
    {
        internal const float MinHeight = 6f, MaxHeight = 18f, HullDepth = 4.5f;
        internal static readonly Vector3 CableTopLocal = new Vector3(0, 0, BlacksiteLandingGearPolicy.DropZ);
        internal static bool TryAccess(Vector3 origin, Quaternion rotation, int mask, float footToDeck, int members,
            out BlacksiteLandingAccess access, out Vector3 exit, out string reason, Transform ignoreRoot = null, Transform attachedCargo = null)
        {
            access = default; exit = default;
            var upper = origin + rotation * CableTopLocal;
            if (!BlacksiteLandingGeometry.ProbeGround(upper - Vector3.up * ((MinHeight + MaxHeight) * .5f),
                (MaxHeight - MinHeight) * .5f + .1f, mask, ignoreRoot, out var floor, out reason)
                || !BlacksiteLandingGeometry.TryFloorNav(floor.point, mask, out var landing, .6f))
            { reason = "rappel foot has no supported navigation"; return false; }
            float height = upper.y - landing.y;
            if (height < MinHeight - .01f || height > MaxHeight + .01f)
            { reason = "rappel height outside 6-18m"; return false; }
            if (!TryPose(origin, rotation, landing, mask, footToDeck, out access, out reason, ignoreRoot, attachedCargo)) return false;
            if (!BlacksiteLandingGeometry.TryFloorNav(landing + rotation * Vector3.forward * 2f, mask, out exit, .6f))
            { reason = "rappel staging: no supported ground"; return false; }
            if (!BlacksiteShipNavigation.TryDropToStaging(landing, exit, mask, out reason)) return false;
            return BlacksiteShipNavigation.TryEgressSlots(rotation, exit, members, mask, out _, out reason);
        }
        // #1305: one hover pose over a known foot: the hull clear and the cable column from the lip
        // to the foot. TryAccess finds its foot under the cable; the landing search fits the pose to
        // a foot it already proved.
        internal static bool TryPose(Vector3 origin, Quaternion rotation, Vector3 foot, int mask, float footToDeck,
            out BlacksiteLandingAccess access, out string reason, Transform ignoreRoot = null, Transform attachedCargo = null)
        {
            access = new BlacksiteLandingAccess { Hover = true, LadderIndex = -1, FootToDeck = footToDeck };
            if (!BlacksiteLandingGeometry.ClearUsableVolume(origin, rotation, mask, ignoreRoot, HullDepth, out reason, attachedCargo)) return false;
            access.DropLandingLocal = access.LadderMountLocal = Quaternion.Inverse(rotation) * (foot - origin);
            access.LadderTopLocal = BlacksiteBoardingGeometry.LipStep;
            access.DropHeight = (origin + rotation * CableTopLocal).y - foot.y;
            return ValidateColumn(Points(origin, rotation, access, false), mask, out reason);
        }
        internal static Vector3[] Points(Vector3 origin, Quaternion rotation, in BlacksiteLandingAccess access, bool inbound)
        {
            var points = new[] { origin + rotation * BlacksiteBoardingGeometry.LipStep,
                origin + rotation * CableTopLocal, origin + rotation * access.DropLandingLocal,
                origin + rotation * access.DropLandingLocal };
            if (inbound) Array.Reverse(points);
            return points;
        }
        internal static bool ValidateColumn(Vector3[] points, int mask, out string reason)
        {
            reason = null; var probe = BlacksitePhysicalStep.ProbeTransform;
            foreach (var p in points)
                if (!BlacksitePhysicalStep.CanOccupy(probe, p, mask, out var blocker))
                { reason = "rappel endpoint blocked by " + (blocker != null ? blocker.name : "saturated query"); return false; }
            for (int direction = 0; direction < 2; direction++)
                for (int i = 1; i < points.Length; i++)
                {
                    var from = points[direction == 0 ? i - 1 : points.Length - i];
                    var to = points[direction == 0 ? i : points.Length - i - 1];
                    int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, to) / .12f));
                    var previous = from;
                    for (int step = 1; step <= steps; step++)
                    {
                        var next = Vector3.Lerp(from, to, step / (float)steps);
                        if (!BlacksitePhysicalStep.ClearSegment(probe, previous, next, mask, null, out var blocker))
                        { reason = "rappel column blocked by " + (blocker != null ? blocker.name : "saturated query"); return false; }
                        previous = next;
                    }
                }
            return true;
        }
    }
}
