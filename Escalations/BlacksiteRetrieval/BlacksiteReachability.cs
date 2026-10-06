using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>#1305 G1 (R15): who asks the route service - the areas its walks use, its world, whether it may ride the car.</summary>
    internal readonly struct BlacksiteTraveller
    {
        internal readonly BlacksiteTravellerKind Kind;
        internal readonly int AreaMask;
        /// <summary>The world it is in.</summary>
        internal readonly bool Outside;
        /// <summary>May take the mineshaft car.</summary>
        internal readonly bool MayRide;
        /// <summary>Null for a member-less query.</summary>
        internal readonly BlacksiteSoldierAI Member;

        private BlacksiteTraveller(BlacksiteTravellerKind kind, int areaMask, bool outside, bool mayRide, BlacksiteSoldierAI member)
        {
            Kind = kind; AreaMask = areaMask; Outside = outside; MayRide = mayRide; Member = member;
        }

        /// <summary>The member's own agent mask while his agent is enabled, else the soldier mask, in his world. Null asks member-less, inside.</summary>
        internal static BlacksiteTraveller Soldier(BlacksiteSoldierAI member)
            => new BlacksiteTraveller(BlacksiteTravellerKind.Soldier,
                member != null && member.agent != null && member.agent.enabled ? member.agent.areaMask : BlacksiteReachability.SoldierAreaMask,
                member != null && member.isOutside, true, member);

        /// <summary>A member-less soldier in a known world; landing and ship proofs pass their own mask.</summary>
        internal static BlacksiteTraveller Soldier(bool outside, int areaMask = BlacksiteReachability.SoldierAreaMask)
            => new BlacksiteTraveller(BlacksiteTravellerKind.Soldier, areaMask, outside, true, null);

        /// <summary>The payload cart, inside, on its own mask. It never takes an Entrance or an OperableDoor.</summary>
        internal static BlacksiteTraveller Cart(int areaMask, bool mayRide)
            => new BlacksiteTraveller(BlacksiteTravellerKind.Cart, areaMask, false, mayRide, null);
    }

    /// <summary>
    /// #1305 G1 (R15): the one route service. Every reachability question in Blacksite is asked here, and this is
    /// the only place a NavMesh path is computed for one. A Walk is the navmesh's own answer on one island (#874 S2
    /// rule 4: an off-mesh link the navmesh holds is crossed like vanilla crosses it); a Route joins walks across the
    /// known transitions - the entrance pairs, the mineshaft car and the operable doors. Host only, main thread,
    /// no allocation per call; callers rate-limit.
    /// </summary>
    internal static partial class BlacksiteReachability
    {
        // One walk's scratch, allocated once. A caller that keeps the path passes its own. A route that fills the
        // corner buffer is judged over its first corners and reported PartialFrontier (JudgedWhole).
        private static readonly NavMeshPath Scratch = new NavMeshPath();
        private static readonly Vector3[] Corners = new Vector3[MaxJudgedCorners];
        private static readonly BlacksiteRouteLeg[] Legs = new BlacksiteRouteLeg[MaxJudgedCorners];
        // A closed door this near a frontier is the gap that ended the walk.
        private const float DoorFrontierMetres = 2.5f;
        // M5 rule (a): a walk asked from where a member stands.
        private const float BodyMetres = .5f;

        /// <summary>
        /// Can this traveller get from <paramref name="from"/> to <paramref name="to"/>: walks joined by an entrance
        /// pair (a soldier whose goal is in the other world), the mineshaft car (inside, MayRide) and an operable door
        /// (a soldier, inside). <paramref name="goalOutside"/> null puts the goal in the traveller's world.
        /// <paramref name="path"/>, when given, holds the first walk's corners on return - the leg to walk now.
        /// </summary>
        internal static BlacksiteRouteVerdict Route(in BlacksiteTraveller traveller, Vector3 from, Vector3 to,
            NavMeshPath path = null, bool? goalOutside = null)
        {
            if (!goalOutside.HasValue || goalOutside.Value == traveller.Outside)
                return SameWorld(traveller, from, to, path);
            BlacksiteRouteVerdict best = BlacksiteRouteVerdict.Unsupported(BlacksiteRouteGap.NoTransition);
            if (traveller.Kind != BlacksiteTravellerKind.Soldier)
                return best;
            // Main entrance first, then the fire exits; the head may ride the car to its door.
            BlacksiteTraveller across = BlacksiteTraveller.Soldier(!traveller.Outside, traveller.AreaMask);
            Vector3 held = default, bestFirst = default;
            int count = BlacksiteEntranceInteraction.RouteEntranceCount;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < count; i++)
                {
                    if (!BlacksiteEntranceInteraction.TryGetRoutePair(i, traveller.Outside, pass == 0, from,
                            out EntranceTeleport entrance, out Vector3 approach, out Vector3 exit))
                        continue;
                    BlacksiteRouteVerdict head = SameWorld(traveller, from, approach, path);
                    held = FirstLegEnd(head, approach);
                    if (!head.Reachable) continue;
                    BlacksiteRouteVerdict tail = SameWorld(across, exit, to, null);
                    if (!tail.Planned) continue;
                    BlacksiteRouteVerdict joined = BlacksiteRouteVerdict.Join(head, new BlacksiteRouteTransition(
                        BlacksiteRouteLegKind.Entrance, approach.x, approach.y, approach.z, exit.x, exit.y, exit.z, 0f, entrance), tail);
                    if (!BlacksiteRouteVerdict.Better(joined, best)) continue;
                    best = joined;
                    bestFirst = held;
                    if (best.Reachable) return best;
                }
            if (path != null && best.Planned && held != bestFirst) Walk(traveller, from, bestFirst, path);
            return best;
        }

        /// <summary>
        /// One same-island leg: NavMesh only, no transitions. False, PathInvalid or no corners is Unsupported(NoPath);
        /// a complete path judged whole is Reachable; a partial one is PartialFrontier at its last corner. Outdoors a
        /// flooded leg is Unsupported(Flooded) (#874 Q12). M5: a member parked on a link the elevator owns, asked
        /// from where he stands, and an indoor leg that walks into the car's column are Unsupported(RefusedLink).
        /// The corners are left in <paramref name="path"/>.
        /// </summary>
        internal static BlacksiteRouteVerdict Walk(in BlacksiteTraveller traveller, Vector3 from, Vector3 to, NavMeshPath path = null)
        {
            if (path == null) path = Scratch;
            path.ClearCorners();
            if (OnRefusedLink(traveller.Member, from))
                return BlacksiteRouteVerdict.Unsupported(BlacksiteRouteGap.RefusedLink);
            try
            {
                if (!NavMesh.CalculatePath(from, to, traveller.AreaMask, path) || path.status == NavMeshPathStatus.PathInvalid)
                    return BlacksiteRouteVerdict.Unsupported(BlacksiteRouteGap.NoPath);
            }
            catch
            {
                return BlacksiteRouteVerdict.Unsupported(BlacksiteRouteGap.NoPath);
            }
            int count = path.GetCornersNonAlloc(Corners);
            if (count == 0)
                return BlacksiteRouteVerdict.Unsupported(BlacksiteRouteGap.NoPath);
            if (traveller.Outside ? Flooded(from.y, to.y, count) : EntersCar(count))
                return BlacksiteRouteVerdict.Unsupported(traveller.Outside ? BlacksiteRouteGap.Flooded : BlacksiteRouteGap.RefusedLink);
            int legs = 0;
            for (int i = 0; i < count; i++)
            {
                // A one-corner path (already at the goal) is one empty walked leg.
                Vector3 a = Corners[i > 0 ? i - 1 : 0], b = Corners[i];
                if (i == 0 && count > 1) continue;
                Legs[legs++] = new BlacksiteRouteLeg(a.x, a.y, a.z, b.x, b.y, b.z);
            }
            return BlacksiteRouteVerdict.Judge(Legs, legs,
                JudgedWhole(path.status == NavMeshPathStatus.PathComplete, count), to.x, to.y, to.z);
        }

        // The traveller's own world: the direct walk when Reachable; else the car's two boarding orders (the start's
        // storey first, #1262 P1) and then one operable door beside the frontier, the Better route winning and the
        // first Reachable returned at once. The caller's path is put back to the winner's first walk.
        private static BlacksiteRouteVerdict SameWorld(in BlacksiteTraveller traveller, Vector3 from, Vector3 to, NavMeshPath path)
        {
            BlacksiteRouteVerdict best = Walk(traveller, from, to, path);
            if (best.Reachable || traveller.Outside)
                return best;
            Vector3 held = to, bestFirst = to;
            if (traveller.MayRide && BlacksiteElevatorInteraction.TryGetCar(out Vector3 board, out Vector3 leave, out object car))
            {
                if (Mathf.Abs(from.y - leave.y) < Mathf.Abs(from.y - board.y)) (board, leave) = (leave, board);
                for (int order = 0; order < 2; order++, (board, leave) = (leave, board))
                {
                    BlacksiteRouteVerdict head = Walk(traveller, from, board, path);
                    held = board;
                    if (!head.Reachable) continue;
                    BlacksiteRouteVerdict tail = Walk(traveller, leave, to, null);
                    if (!tail.Planned) continue;
                    BlacksiteRouteVerdict ride = BlacksiteRouteVerdict.Join(head, new BlacksiteRouteTransition(BlacksiteRouteLegKind.ElevatorCar,
                        board.x, board.y, board.z, leave.x, leave.y, leave.z, Mathf.Abs(board.y - leave.y), car), tail);
                    if (!BlacksiteRouteVerdict.Better(ride, best)) continue;
                    best = ride;
                    bestFirst = board;
                    if (best.Reachable) return best;
                }
            }
            if (traveller.Kind == BlacksiteTravellerKind.Soldier && best.Reach == BlacksiteRouteReach.PartialFrontier
                && BlacksiteDoorInteraction.TryGetOperableDoor(new Vector3(best.EndX, best.EndY, best.EndZ), DoorFrontierMetres,
                    out DoorLock door, out Vector3 near, out Vector3 far))
            {
                BlacksiteRouteVerdict head = Walk(traveller, from, near, path);
                held = near;
                BlacksiteRouteVerdict tail = head.Reachable ? Walk(traveller, far, to, null) : head;
                BlacksiteRouteVerdict through = BlacksiteRouteVerdict.Join(head, new BlacksiteRouteTransition(
                    BlacksiteRouteLegKind.OperableDoor, near.x, near.y, near.z, far.x, far.y, far.z, 0f, door), tail);
                if (head.Reachable && tail.Planned && BlacksiteRouteVerdict.Better(through, best)) { best = through; bestFirst = near; }
            }
            if (path != null && best.Planned && held != bestFirst) Walk(traveller, from, bestFirst, path);
            return best;
        }

        // Where a route's first walk ends: at its first transition, or at the goal.
        private static Vector3 FirstLegEnd(in BlacksiteRouteVerdict route, Vector3 to)
            => route.Next.Exists ? new Vector3(route.Next.EntryX, route.Next.EntryY, route.Next.EntryZ) : to;

        // M5 rule (a): the mover's dead end (BlacksiteNavigator.SkipLink, recovery=none): the member's agent is
        // parked on a link the elevator owns and the back-off cannot re-bind it beside the body
        // (BlacksiteNavigator.TryBackOffPoint), so no walk starts from where he stands. A member the back-off
        // recovers is judged by his walk.
        private static bool OnRefusedLink(BlacksiteSoldierAI member, Vector3 from)
        {
            if (member == null) return false;
            NavMeshAgent agent = member.agent;
            Vector3 body = member.transform.position;
            if (agent == null || !agent.enabled || !agent.isOnNavMesh || !agent.isOnOffMeshLink
                || (from - body).sqrMagnitude > BodyMetres * BodyMetres)
                return false;
            try
            {
                OffMeshLinkData link = agent.currentOffMeshLinkData;
                return BlacksiteElevatorInteraction.OwnsOffMeshLink(link, agent.navMeshOwner)
                    && !BlacksiteSoldierAI.BlacksiteNavigator.TryBackOffPoint(link, body, out _);
            }
            catch { return false; }
        }

        // M5 rule (b): a corner past the first inside the car's column while the first is not. Walking into the car
        // is the car's job; a rider's walk out of it starts inside and is not refused.
        private static bool EntersCar(int count)
        {
            if (count < 2 || !BlacksiteElevatorInteraction.TryGetCarColumn(out Bounds column) || column.Contains(Corners[0]))
                return false;
            for (int i = 1; i < count; i++)
                if (column.Contains(Corners[i])) return true;
            return false;
        }

        // #874 Q12: the plane against the start, the goal and the walk's lowest corner. Only in a Flooded round.
        private static bool Flooded(float startY, float goalY, int count)
        {
            if (!BlacksiteHazardAwareness.TryGetFlood(out BlacksiteHazardFootprint water)) return false;
            float lowest = goalY;
            for (int i = 0; i < count; i++) lowest = Mathf.Min(lowest, Corners[i].y);
            return BlacksiteHazardPolicy.FloodRefusesRoute(water.CurrentCentre.y, startY, goalY, lowest);
        }
    }
}
