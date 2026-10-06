// #874 Q6 (F9): the outside ship guard's patrol, as pure rules.
//
// The 23 September guards took "two steps and stop, forever": a 3.5 m ring around the rendezvous,
// the next post 55 degrees round it (~3.3 m of chord), a 1.1 m stop so the body walked ~2.2 m, a new
// post on arrival or after 12 s, and the guard facing the ship it was guarding. What replaces it is a
// LOOP: two to eight standable waypoints proved on the navmesh inside the leash, at least 9.3 m
// apart (five seconds of walking at the patrol pace), with a 10-15 s dwell at each one during which
// the guard looks AWAY from the ship; two waypoints are a back-and-forth beat (#874, 25-Sept
// playtest). Unity-free so BlacksiteBehaviourRegression's PatrolChecks drives the same rules the
// planner applies; the navmesh proof itself (BlacksiteSquadPlanner.Idle.cs) is the Unity half. The
// guard's duty state machine and the dependency-free constants are BlacksitePatrolPolicy.Duty.cs.
using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// One proved waypoint candidate, as an offset from the loop's anchor (the ground rendezvous).
    /// <see cref="RouteMetres"/> is the navmesh route length from the anchor, which is what the leash
    /// is measured on; <see cref="Sightline"/> is an unobstructed line to the entrance or the ship.
    /// </summary>
    internal readonly struct BlacksitePatrolCandidate
    {
        internal readonly float X, Z, RouteMetres;
        internal readonly bool Sightline;

        internal BlacksitePatrolCandidate(float x, float z, float routeMetres, bool sightline)
        {
            X = x; Z = z; RouteMetres = routeMetres; Sightline = sightline;
        }
    }

    internal static partial class BlacksitePatrolPolicy
    {
        /// <summary>
        /// The guard leash, in metres of navmesh route from the rendezvous. It is
        /// <c>BlacksiteDeploymentPolicy.GuardLeash</c>'s own value (that constant forwards here) so
        /// the headless suite can read it without the Vector3-bound deployment policy. One metre
        /// past <see cref="OuterRadiusMetres"/> so the outer ring is inside it by route on open
        /// ground, where the route is the chord.
        /// </summary>
        internal const float LeashMetres = 12f;

        /// <summary>
        /// A loop shorter than this is not a patrol; the guard holds a single post. Two is a
        /// back-and-forth beat: <see cref="NextLive"/> cycles 0-1-0 like any other loop (#874).
        /// </summary>
        internal const int MinWaypoints = 2;
        internal const int MaxWaypoints = 8;

        /// <summary>
        /// #874: the shortest leg on the ground. Five seconds of the 09-25 1.6 m/s stroll - Lawson's
        /// 25-Sept verdict on 4.6 m legs (~2 s of walking): "stand still, take 2 steps, stop" - kept
        /// as the geometry he accepted; the pace itself is now
        /// <see cref="BlacksiteLocomotionSpeedTable.StrollSpeed"/>.
        /// </summary>
        internal const float MinLegMetres = 8f;

        /// <summary>
        /// Every pair of waypoints is at least this far apart on the ground: <see cref="MinLegMetres"/>
        /// (8 m) plus the arrival stop
        /// (<see cref="BlacksiteArrivalPolicy.DefaultStoppingDistance"/> 1.1 m) and the settle slack
        /// (<see cref="BlacksiteArrivalPolicy.SettleSlack"/> 0.2 m) the leg ends inside, 9.3 m.
        /// </summary>
        internal const float WaypointSpacingMetres = MinLegMetres
                                                     + BlacksiteArrivalPolicy.DefaultStoppingDistance
                                                     + BlacksiteArrivalPolicy.SettleSlack;

        /// <summary>
        /// Candidate rings (metres from the anchor) and bearings per ring for the proof. The OUTER
        /// ring is first: <see cref="SelectLoop"/> takes candidates in index order, so the far
        /// points claim the loop before a near one can crowd them out at the 9.3 m spacing.
        /// </summary>
        internal const float OuterRadiusMetres = 11f;
        internal static readonly float[] CandidateRadii = { OuterRadiusMetres, 8f, 5f };
        internal const int CandidateBearings = 12;

        /// <summary>The route test: a waypoint is inside the leash by its navmesh route, not by a snap.</summary>
        internal static bool InsideLeash(float routeMetres) => routeMetres >= 0f && routeMetres <= LeashMetres;

        /// <summary>
        /// How close the body must be to its waypoint for a dwell to start: the arrival stop
        /// (<see cref="BlacksiteArrivalPolicy.DefaultStoppingDistance"/> 1.1 m) plus the slack an
        /// autoBraking agent settles inside (<see cref="BlacksiteArrivalPolicy.SettleSlack"/> 0.2 m).
        /// </summary>
        internal const float ArrivalRadius = BlacksiteArrivalPolicy.DefaultStoppingDistance + BlacksiteArrivalPolicy.SettleSlack;

        /// <summary>
        /// #874 Q6: the dwell's arrival. The planner's arrival latch is
        /// HISTORICAL - it survives until the goal moves - so a guard pushed off the waypoint it
        /// had reached and walking back to the same one still reads latched. The dwell needs the
        /// latch AND the body within <see cref="ArrivalRadius"/> of the waypoint now.
        /// </summary>
        internal static bool ArrivedForDwell(bool latched, float bodyRange) => latched && bodyRange <= ArrivalRadius;

        /// <summary>
        /// #1219 E2: the role speed a pace is calibrated against - the replicated animation role
        /// speed. A stroll is walked over the Normal role, so its blend lands where the table puts it.
        /// </summary>
        internal static float RoleSpeed(BlacksiteOrderPace pace) => BlacksiteLocomotionSpeedTable.RoleSpeed(pace == BlacksiteOrderPace.Urgent);

        /// <summary>
        /// #1219 E2: the agent speed a pace commands. A Stroll is
        /// <see cref="BlacksiteLocomotionSpeedTable.StrollSpeed"/>; Normal and Urgent are their role speeds.
        /// </summary>
        internal static float AgentSpeed(BlacksiteOrderPace pace)
            => pace == BlacksiteOrderPace.Stroll ? BlacksiteLocomotionSpeedTable.StrollSpeed : RoleSpeed(pace);

        /// <summary>
        /// Chooses the loop from proved candidates: drops any candidate outside the leash by route,
        /// takes sightline candidates first and then the rest, each in index order (outer ring
        /// first) and only if it is at least <see cref="WaypointSpacingMetres"/> (9.3 m) from every
        /// waypoint already taken, up to <see cref="MaxWaypoints"/> (8); then orders the loop by
        /// bearing round the anchor so each leg crosses to the next waypoint instead of back through
        /// the ship. Fills <paramref name="loop"/> with candidate indices and returns its length, or
        /// 0 (and an empty list) when fewer than <see cref="MinWaypoints"/> (2) survive.
        /// </summary>
        internal static int SelectLoop(IReadOnlyList<BlacksitePatrolCandidate> candidates, List<int> loop)
        {
            loop.Clear();
            for (int pass = 0; pass < 2 && loop.Count < MaxWaypoints; pass++)
            {
                bool wantSightline = pass == 0;
                for (int i = 0; i < candidates.Count && loop.Count < MaxWaypoints; i++)
                {
                    var c = candidates[i];
                    if (c.Sightline != wantSightline || !InsideLeash(c.RouteMetres)) continue;
                    if (!Spaced(candidates, loop, c)) continue;
                    loop.Add(i);
                }
            }
            if (loop.Count < MinWaypoints) { loop.Clear(); return 0; }
            // Insertion sort by bearing: at most MaxWaypoints (8) entries, and no comparer closure.
            for (int i = 1; i < loop.Count; i++)
            {
                int item = loop[i];
                float bearing = Bearing(candidates[item]);
                int j = i - 1;
                while (j >= 0 && Bearing(candidates[loop[j]]) > bearing) { loop[j + 1] = loop[j]; j--; }
                loop[j + 1] = item;
            }
            return loop.Count;
        }

        private static bool Spaced(IReadOnlyList<BlacksitePatrolCandidate> candidates, List<int> taken,
            in BlacksitePatrolCandidate c)
        {
            float min = WaypointSpacingMetres * WaypointSpacingMetres;
            for (int j = 0; j < taken.Count; j++)
            {
                var t = candidates[taken[j]];
                float dx = c.X - t.X, dz = c.Z - t.Z;
                if (dx * dx + dz * dz < min) return false;
            }
            return true;
        }

        private static float Bearing(in BlacksitePatrolCandidate c) => (float)Math.Atan2(c.X, c.Z);

        // ---------------------------------------------------------------------------------
        // #874 Q6: a waypoint that stops being walkable is DEAD, and the loop re-proves once too
        // few are left alive.
        // ---------------------------------------------------------------------------------

        /// <summary>A loop with fewer than <see cref="MinWaypoints"/> live waypoints is re-proved.</summary>
        internal static bool NeedsReprove(int count, int deadMask) => LiveCount(count, deadMask) < MinWaypoints;

        /// <summary>
        /// Marks waypoint <paramref name="index"/> dead for the whole loop, drops any dwell on it
        /// and moves the member to the next live waypoint no other guard holds
        /// (<paramref name="heldMask"/>, #1219 E3). Returns false when none is left, in which case
        /// the loop must be re-proved (or the member waits postless) before anything is issued.
        /// </summary>
        internal static bool MarkDead(ref int index, ref float dwellLeft, int count, ref int deadMask, int heldMask = 0)
        {
            if (count <= 0 || index < 0 || index >= count) return false;
            deadMask |= 1 << index;
            dwellLeft = -1f;
            int next = NextLive(index, count, deadMask, heldMask);
            if (!IsLive(next, deadMask)) return false;
            index = next;
            return true;
        }

        // ---------------------------------------------------------------------------------
        // #874 Q6: deaths are RECOVERABLE. A stalled leg's death (#1293 R2: the planner's stall
        // answer, TryAnswerStalledPatrol) is a stall, not a fact about the ground, and it comes back.
        // Only an Unreachable closure is durable, and a moved rendezvous clears even that.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The life of a transient death: 20 s, longer than the 15 s re-proof cadence so the
        /// re-proof after a death still sees it once. It is the dead waypoint's retry backoff and
        /// judges no progress.
        /// </summary>
        internal const float TransientDeathSeconds = 20f;

        /// <summary>When a death marked at <paramref name="now"/> expires: never for an Unreachable closure.</summary>
        internal static float DeathExpiry(float now, bool unreachable)
            => unreachable ? float.PositiveInfinity : now + TransientDeathSeconds;

        internal static bool IsDurable(float deadUntil) => float.IsPositiveInfinity(deadUntil);

        /// <summary>The dead mask at <paramref name="now"/>: the waypoints whose death has not expired.</summary>
        internal static int DeadMask(IReadOnlyList<float> deadUntil, float now)
        {
            int mask = 0;
            for (int i = 0; i < deadUntil.Count && i < 31; i++)
                if (deadUntil[i] > now) mask |= 1 << i;
            return mask;
        }

        /// <summary>
        /// Whether a death applies at all. A loop too small to patrol is the single-post fallback,
        /// the last post there is; a transient mark never takes it, only an Unreachable closure.
        /// </summary>
        internal static bool AppliesDeath(int count, bool unreachable) => unreachable || count >= MinWaypoints;
    }
}
