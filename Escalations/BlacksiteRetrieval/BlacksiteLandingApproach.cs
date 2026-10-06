using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Verify the same ground corridor in both directions. A shortest reverse NavMesh path can
    // select a different, physically blocked shortcut, so retain the proven corridor for walkers.
    internal sealed class BlacksiteLandingApproach
    {
        private readonly Vector3 _origin, _start;
        private readonly BlacksiteEntranceApproach _entrance;
        private readonly Quaternion _rotation;
        private readonly int _mask;
        private readonly bool _hover;
        private readonly List<Vector3> _samples = new List<Vector3>();
        private BlacksiteLandingGeometry.RouteCheck _leg;
        private Vector3[] _stops;
        private Vector3 _legFrom;
        private int _option, _stop;
        private bool _returning;
        private readonly List<string> _failedOptions = new List<string>();
        internal bool Complete { get; private set; }
        internal bool Success { get; private set; }
        internal string Reason { get; private set; }
        internal float Metres { get; private set; }
        internal Vector3[] Waypoints { get; private set; }
        // #874 plan B: the goal is the job's pre-bound entrance approach, never the raw anchor, so a
        // pose cannot re-derive a door the search already resolved (or already refused).
        internal BlacksiteLandingApproach(Vector3 origin, Quaternion rotation, Vector3 start, BlacksiteEntranceApproach entrance, int mask, bool hover = false)
        { _origin = origin; _rotation = rotation; _start = start; _entrance = entrance; _mask = mask; _hover = hover; }
        private bool Forbidden(Vector3 point)
            => !_hover && BlacksiteHullBlockers.InsideGroundFootprint(_origin, _rotation, point, .5f);
        // #1233 S8: whether the next Advance constructs a leg - a NavMesh path and its endpoint
        // binds, the one indivisible unit of this job - rather than walking samples of one.
        internal bool NextAdvanceBuildsLeg => !Complete && _leg == null;
        // #1233 S8: <paramref name="samples"/> is the batch of physical route samples one call
        // walks. The landing search's slice loop asks for one at a time, because a sample is the
        // indivisible unit of this leg and its slice re-reads the clock between Steps; the
        // synchronous callers keep the batch of twelve. A leg is BUILT by its own Advance (the
        // NavMesh path and the endpoint binds), never inside the call that completed the one
        // before it, so the search can price the two apart.
        internal void Advance(int samples = 12)
        {
            if (Complete) return;
            if (_leg == null)
            {
                if (_stops == null)
                {
                    if (_option >= (_hover ? 1 : 5)) { Complete = true; return; }
                    _samples.Clear(); _stop = 0; _returning = false; _legFrom = _start;
                    float side = _option % 2 == 1 ? -1 : 1;
                    var locals = _option == 0 ? Array.Empty<Vector3>() : _option <= 2
                        ? new[] { new Vector3(side * 9f, 0, 16f) }
                        : new[] { new Vector3(side * 9f, 0, 16f), new Vector3(side * 9f, 0, -16f) };
                    var stops = new List<Vector3>();
                    foreach (var local in locals)
                    {
                        var point = _origin + _rotation * local; point.y = _start.y;
                        if (!BlacksiteLandingGeometry.Ground(point, 5f, _mask, null, out var ground)
                            || !BlacksiteLandingGeometry.TryFloorNav(ground.point, _mask, out var floor, .6f))
                        { FailOption("no supported approach beside the planned hull"); return; }
                        stops.Add(floor);
                    }
                    stops.Add(_entrance.Nav); _stops = stops.ToArray();
                }
                if (_returning)
                {
                    var reverse = _samples.AsEnumerable().Reverse().ToArray();
                    _leg = new BlacksiteLandingGeometry.RouteCheck(BlacksitePhysicalStep.ProbeTransform,
                        reverse[0], _start, _mask, forbidden: Forbidden, physicalCorners: reverse, areaMask: ~BlacksiteShipNavigation.DeckAreaMask, groundNavTolerance: .6f, allowWorldLadders: true, followed: true);
                }
                else _leg = Leg(_legFrom, _stops[_stop], _stop == _stops.Length - 1);
                return;
            }
            _leg.Advance(samples);
            if (!_leg.Complete) return;
            if (!_leg.Success) { FailOption((_returning ? "return: " : "approach: ") + _leg.Reason); return; }
            if (_returning)
            {
                Waypoints = Compact(_samples); Reason = null; Success = true; Complete = true; return;
            }
            Metres += _leg.WalkedMetres;
            _samples.AddRange(_leg.Samples.Skip(_samples.Count == 0 ? 0 : 1));
            _legFrom = _leg.EndPosition; _leg = null;
            if (++_stop < _stops.Length) return;
            _returning = true;
        }
        // #1305: every leg is followed - its samples become the sealed GroundRoute members walk on navigation.
        private BlacksiteLandingGeometry.RouteCheck Leg(Vector3 from, Vector3 to, bool entrance) => entrance
            ? new BlacksiteLandingGeometry.RouteCheck(BlacksitePhysicalStep.ProbeTransform, from, _entrance, _mask, forbidden: Forbidden, areaMask: ~BlacksiteShipNavigation.DeckAreaMask, groundNavTolerance: .6f, allowWorldLadders: true, followed: true)
            : new BlacksiteLandingGeometry.RouteCheck(BlacksitePhysicalStep.ProbeTransform, from, to, _mask, forbidden: Forbidden, areaMask: ~BlacksiteShipNavigation.DeckAreaMask, groundNavTolerance: .6f, allowWorldLadders: true, followed: true);
        private void FailOption(string reason) { _failedOptions.Add("option=" + _option + ": " + reason); Reason = string.Join("; ", _failedOptions); _option++; _leg = null; _stops = null; Metres = 0; }
        private static Vector3[] Compact(List<Vector3> samples)
        {
            var points = new List<Vector3> { samples[0] }; float distance = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                distance += Vector3.Distance(samples[i-1], samples[i]);
                bool corner = i + 1 < samples.Count && Vector3.Angle(samples[i] - samples[i-1], samples[i+1] - samples[i]) > 12f;
                if (distance >= 1f || corner || i == samples.Count - 1) { points.Add(samples[i]); distance = 0; }
            }
            return points.ToArray();
        }
        internal static bool TryUpcomingLadder(Vector3[] points, Vector3 current, bool outbound, int mask,
            out BlacksiteWorldLadder ladder, out Vector3[] climb)
        {
            ladder = null; climb = null;
            if (points == null) return false;
            int nearest = -1; float distance = 36f;
            for (int i = 0; i < points.Length; i++)
            { float d = (current - points[i]).sqrMagnitude; if (d < distance) { distance = d; nearest = i; } }
            if (nearest < 0) return false;
            int direction = outbound ? 1 : -1;
            for (int i = nearest; i + direction >= 0 && i + direction < points.Length; i += direction)
            {
                if (Vector3.Distance(current, points[i]) > 5f) return false;
                if (BlacksiteWorldLadder.TryMatch(points[i], points[i + direction], mask, out ladder, out climb)) return true;
            }
            return false;
        }
    }
}
