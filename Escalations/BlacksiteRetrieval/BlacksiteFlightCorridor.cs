using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #1233 S7: the one flight corridor. A pose the ship is sent to - a ground site or a hover station -
    // is proven with a corridor the whole hull was swept through (#1305: a hover with none flies MaxAltitude):
    // straight up from the pose to a proven transit altitude (the descent column), then Metres back
    // along the pose's own heading at that altitude (the leg). The altitude is proven per site, lowest
    // rung first, persisted with the pose (BlacksiteLandingAccess.Altitude), and every flight IS the
    // corridor: the inbound and the recall fly Point at a rising t, the departure and the hover station
    // leave fly it at a falling t. The 2026-09-27 Vow departure lerped to an unproven sky point 150 m
    // ahead of the hull, through the facility; the inbound had entered from an unproven point 185 m back.
    internal static class BlacksiteFlightCorridor
    {
        // Floor: the 35 m column every inbound descent already ended with and the 185 m leg every inbound
        // began from. Ceiling: the old departure's 72 m, which nothing had proven. One column box and
        // one leg box per rung. LegShare is the share of an inbound flight spent on the leg.
        internal const float MinAltitude = 35f, MaxAltitude = 75f, AltitudeStep = 10f, Metres = 185f, LegShare = .65f;
        // The whole moving hull, ship-local: the measured deck footprint (the plate, both ladders and
        // the step probe's margin, BlacksiteHullBlockers) from the gear feet up to the hull top.
        internal static readonly Vector3 EnvelopeHalf = new Vector3(BlacksiteHullBlockers.FootprintHalfWidth, (BlacksiteLandingGeometry.HullTop + BlacksiteLandingGearPolicy.MaxFootToDeck) * .5f, BlacksiteHullBlockers.FootprintHalfLength);
        internal static readonly Vector3 EnvelopeCenter = new Vector3(0, BlacksiteLandingGeometry.HullTop - EnvelopeHalf.y, 0);
        // The envelope's deck-up part, the catwalk underside to the hull top, which a landed pose keeps
        // out of the terrain: the column is swept with this so the ground the gear stands in is never
        // an initial overlap, and the gear rises behind it through air it has just swept.
        internal static readonly Vector3 ColumnHalf = new Vector3(EnvelopeHalf.x, (BlacksiteLandingGeometry.HullTop - BlacksiteLandingGearPolicy.CatwalkUnderside) * .5f, EnvelopeHalf.z);
        internal static readonly Vector3 ColumnCenter = new Vector3(0, BlacksiteLandingGeometry.HullTop - ColumnHalf.y, 0);
        private static readonly RaycastHit[] Hits = new RaycastHit[64];
        internal static Vector3 Heading(Quaternion rotation) => Vector3.ProjectOnPlane(rotation * Vector3.back, Vector3.up).normalized;
        internal static Vector3 Top(Vector3 landing, float altitude) => landing + Vector3.up * altitude;
        /// <summary>The leg's far end: where the inbound starts, the departure ends and a hover ship waits offsite.</summary>
        internal static Vector3 Entry(Vector3 landing, Quaternion rotation, float altitude) => Top(landing, altitude) + Heading(rotation) * Metres;
        /// <summary>The hull at inbound share <paramref name="t"/>: 0 is <see cref="Entry"/>, 1 is the pose. An outbound flight reads 1 - t.</summary>
        internal static Vector3 Point(Vector3 landing, Quaternion rotation, float altitude, float t)
        {
            Vector3 top = Top(landing, altitude);
            return t < LegShare
                ? Vector3.Lerp(Entry(landing, rotation, altitude), top, Mathf.SmoothStep(0, 1, t / LegShare))
                : Vector3.Lerp(top, landing, Mathf.SmoothStep(0, 1, (t - LegShare) / (1f - LegShare)));
        }
        /// <summary>
        /// The full-hull sweep and the altitude it proved: the column once to the ceiling, then the leg
        /// with the whole envelope, gear included, at each rung the column reaches, lowest first.
        /// </summary>
        internal static bool Clear(Vector3 origin, Quaternion rotation, int mask, Transform ignoreRoot, out float altitude, out string reason)
        {
            altitude = 0f;
            float column = Sweep(origin + rotation * ColumnCenter, ColumnHalf, rotation, Vector3.up, MaxAltitude, mask, ignoreRoot, out reason);
            if (column < MinAltitude + .5f) { reason = "descent column " + reason; return false; }
            for (float rung = MinAltitude; rung + .5f <= column && rung <= MaxAltitude + .01f; rung += AltitudeStep)
                if (float.IsPositiveInfinity(Sweep(Top(origin, rung) + rotation * EnvelopeCenter, EnvelopeHalf, rotation, Heading(rotation), Metres, mask, ignoreRoot, out reason)))
                { altitude = rung; reason = null; return true; }
            reason = "flight corridor " + reason; return false;
        }
        // The nearest hit's distance along a box swept `distance` along `direction`, +inf when nothing was
        // hit; a hit at the full distance is the hull touching at the corridor's end. Saturation is a hit at zero.
        private static float Sweep(Vector3 center, Vector3 half, Quaternion rotation, Vector3 direction, float distance, int mask, Transform ignoreRoot, out string reason)
        {
            reason = null; float nearest = float.PositiveInfinity;
            int count = Physics.BoxCastNonAlloc(center, half, direction, Hits, rotation, distance, mask, QueryTriggerInteraction.Ignore);
            if (count == Hits.Length) { reason = "sweep saturated"; return 0f; }
            for (int i = 0; i < count; i++)
            {
                var hit = Hits[i];
                if (hit.collider == null || (ignoreRoot != null && hit.transform.IsChildOf(ignoreRoot)) || hit.distance >= nearest) continue;
                nearest = hit.distance; reason = "blocked by " + hit.collider.name + " at " + hit.distance.ToString("0.0") + "m";
            }
            return nearest;
        }
    }
}
