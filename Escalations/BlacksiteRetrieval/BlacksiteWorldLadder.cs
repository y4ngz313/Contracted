using System;
using System.Collections.Generic;
using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Only authored player ladders authorize vertical travel. Enemy jump/climb links alone do not.
    internal sealed class BlacksiteWorldLadder
    {
        internal static readonly List<BlacksiteWorldLadder> All = new List<BlacksiteWorldLadder>();
        private readonly Transform _bottom, _top, _plane;
        private readonly Func<bool> _occupied;
        internal bool Occupied => _occupied?.Invoke() == true;
        internal readonly BlacksitePassageQueue Queue = new BlacksitePassageQueue();
        // #874 S5.1: the queue orders the members waiting at the foot; the column decides how many
        // of them may be on the rungs at once. Its rise is unknown until the ladder validates, and
        // a column with no span is single occupancy, so an unvalidated ladder is never more
        // permissive than the 2026-09-09 build was.
        internal readonly BlacksiteWorldLadderColumn Column = new BlacksiteWorldLadderColumn();
        internal BlacksiteWorldLadder(Transform bottom, Transform top, Transform plane, Func<bool> occupied = null)
        { _bottom = bottom; _top = top; _plane = plane; _occupied = occupied; }
        internal bool Active => _bottom != null && _top != null && _plane != null && _bottom.gameObject.activeInHierarchy;
        internal Vector3 RungFacing { get; private set; }
        private Vector3[] _cached;
        private bool _checked, _valid;
        private string _reason;
        internal bool TryPoints(int mask, out Vector3[] points, out string reason)
        {
            if (!_checked) { _checked = true; _valid = Validate(mask, out _cached, out _reason); }
            points = _cached == null ? null : (Vector3[])_cached.Clone(); reason = _reason; return Active && _valid;
        }
        internal bool Validate(int mask, out Vector3[] points, out string reason)
        {
            points = null; reason = "ladder endpoint has no supported navigation";
            if (!Active || !BlacksiteLandingGeometry.TryFloorNav(_bottom.position, mask, out var bottom, 1.5f)
                || !BlacksiteLandingGeometry.TryFloorNav(_top.position, mask, out var top, 1.5f)) return false;
            if (top.y - bottom.y < 1f || top.y - bottom.y > 35f) { reason = "ladder height outside authored climb bounds"; return false; }
            var low = new Vector3(_plane.position.x, bottom.y, _plane.position.z);
            var high = new Vector3(low.x, top.y, low.z);
            RungFacing = Vector3.ProjectOnPlane(_plane.forward, Vector3.up);
            if (RungFacing.sqrMagnitude < .0001f) RungFacing = low - bottom;
            RungFacing = RungFacing.normalized;
            points = new[] { bottom, low, high, top };
            var actor = BlacksitePhysicalStep.ProbeTransform;
            for (int i = 0; i < points.Length; i++)
            {
                if (!BlacksitePhysicalStep.CanOccupy(actor, points[i], mask, out var blocker))
                { reason = "ladder body blocked by " + blocker?.name + " at " + points[i]; return false; }
                if (i == 0) continue;
                int steps = Mathf.CeilToInt(Vector3.Distance(points[i-1], points[i]) / .12f);
                Vector3 previous = points[i-1];
                for (int step = 1; step <= steps; step++)
                {
                    Vector3 next = Vector3.Lerp(points[i-1], points[i], step/(float)steps);
                    if (!BlacksitePhysicalStep.ClearSegment(actor, previous, next, mask, null, out blocker)
                        || !BlacksitePhysicalStep.ClearSegment(actor, next, previous, mask, null, out blocker))
                    { reason = "ladder sweep blocked by " + blocker?.name + " at " + next; return false; }
                    previous = next;
                }
            }
            // #874 S5.1: points are always authored bottom-first here, so the column's span is
            // direction-independent and every climber measures its headway against the same ends.
            Column.SetSpan(points[0].y, points[points.Length - 1].y);
            reason = null; return true;
        }
        internal static bool TryMatch(Vector3 from, Vector3 to, int mask, out BlacksiteWorldLadder ladder, out Vector3[] points)
        {
            ladder = null; points = null;
            if (Mathf.Abs(to.y-from.y) < 1f) return false;
            foreach (var item in All)
            {
                if (!item.Active) continue;
                bool up = to.y > from.y;
                var begin = up ? item._bottom.position : item._top.position;
                var end = up ? item._top.position : item._bottom.position;
                if (Vector3.Distance(from,begin) > 3f || Vector3.Distance(to,end) > 3f
                    || !item.TryPoints(mask,out points,out _)) continue;
                if (!up) Array.Reverse(points);
                ladder = item; return true;
            }
            return false;
        }
        internal static void ExpandRoute(Vector3[] corners, int mask)
        {
            for (int i = 1; i < corners.Length; i++)
                if (TryMatch(corners[i-1], corners[i], mask, out _, out var points))
                { corners[i-1] = points[0]; corners[i] = points[points.Length-1]; }
        }
    }
}
