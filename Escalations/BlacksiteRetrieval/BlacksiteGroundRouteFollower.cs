using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Engine-light corridor selection, shared by production movement and headless route replays.
    internal static class BlacksiteGroundRouteFollower
    {
        /// <summary>
        /// Supplies a corridor waypoint only while the route still leads toward the requested
        /// destination. Intermediate points use the caller's advance radius and do not brake.
        /// </summary>
        internal static bool TryWaypoint(Vector3[] points, Vector3 current, Vector3 destination,
            bool outbound, float advanceRadius, out Vector3 waypoint, out bool isLast)
        {
            waypoint = default; isLast = false;
            if (points == null || points.Length < 2) return false;
            int end = outbound ? points.Length - 1 : 0;
            if (ShouldYieldToDestination(points, current, destination, outbound, advanceRadius,
                    out float progress, out float gap) || gap >= 64f) return false;
            // #1305 C4: the first point at or ahead of the actor's place on the route, past every point
            // LegDue counts as reached, so a leg the motion owner declared due is never handed back.
            int next = outbound ? (int)System.Math.Ceiling(progress) : (int)progress;
            while (next != end && LegDue(current, points[next], advanceRadius))
                next += outbound ? 1 : -1;
            waypoint = points[next];
            isLast = next == end;
            return true;
        }

        /// <summary>
        /// #1305 C4: the one leg judge, for the motion owner's advance edge and the next point alike.
        /// Horizontal, like every arrival test on a member: one on the ramp below a point has reached it.
        /// </summary>
        internal static bool LegDue(Vector3 current, Vector3 point, float advanceRadius)
        {
            float x = point.x - current.x, z = point.z - current.z;
            return x * x + z * z <= advanceRadius * advanceRadius;
        }

        /// <summary>
        /// #1146: an inbound off-corridor cargo goal belongs to the mission. An outbound entrance
        /// can lie beyond the corridor, so retain that leg until its closest approach to the goal.
        /// Yield when the member reaches or passes the goal's ordered segment projection, rather
        /// than an unbounded terminal plane that could discard an earlier hairpin leg.
        /// No completion latch survives a new trip. The member's own projection feeds the selection.
        /// </summary>
        private static bool ShouldYieldToDestination(Vector3[] points, Vector3 current, Vector3 destination,
            bool outbound, float advanceRadius, out float currentProgress, out float currentDistance)
        {
            currentProgress = 0f; currentDistance = float.PositiveInfinity;
            int end = outbound ? points.Length - 1 : 0;
            if (LegDue(current, points[end], advanceRadius) || LegDue(current, destination, advanceRadius)) return true;

            float destinationDistance = float.PositiveInfinity, destinationProgress = 0f;
            for (int i = 0; i < points.Length - 1; i++)
            {
                Vector3 segment = points[i + 1] - points[i];
                float lengthSquared = segment.sqrMagnitude;
                float inverseLengthSquared = lengthSquared > 0f ? 1f / lengthSquared : 0f;
                Vector3 currentDelta = current - points[i], destinationDelta = destination - points[i];
                float currentFraction = Mathf.Clamp01(Vector3.Dot(currentDelta, segment) * inverseLengthSquared);
                float destinationFraction = Mathf.Clamp01(Vector3.Dot(destinationDelta, segment) * inverseLengthSquared);
                float currentGap = (currentDelta - segment * currentFraction).sqrMagnitude;
                float destinationGap = (destinationDelta - segment * destinationFraction).sqrMagnitude;
                if (currentGap < currentDistance)
                {
                    currentDistance = currentGap;
                    currentProgress = i + currentFraction;
                }
                if (destinationGap < destinationDistance)
                {
                    destinationDistance = destinationGap;
                    destinationProgress = i + destinationFraction;
                }
            }
            return (!outbound && destinationDistance > advanceRadius * advanceRadius)
                || (outbound ? currentProgress >= destinationProgress : currentProgress <= destinationProgress);
        }
    }
}
