using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #954 S12A U1. The squad route's pure core: an undirected tile adjacency graph, a permanent
    /// explored set and two breadth-first plans over it. Nodes are tile ids 0..Count-1, which the
    /// host adapter assigns by the dungeon's own AllTiles index.
    ///
    /// No UnityEngine type appears here on purpose, so tests/BlacksiteRoomGraphRegression can
    /// compile this file on its own and pin the route, the dead-end rule and the frontier order
    /// without a Unity stub. Reachability and every route are by hop count, never by distance: the
    /// straight-line rule this replaces is what kept the March squad's frontier inside one pocket.
    /// #950 A4 (O2) adds one exception, and only to the RANK of a frontier planned toward a known
    /// objective (<see cref="PlanFrontierToward"/>): which unexplored room is nearest HIM is a
    /// question about the world rather than about the graph. The distance arrives as a caller
    /// function so no UnityEngine type appears here either, the route to the chosen tile is still
    /// planned over proven hops, and nothing else here consults it.
    ///
    /// Nothing here expires. The explored set lives for the mission, so a branch the squad has
    /// finished stays finished instead of coming back ninety seconds later.
    /// </summary>
    internal sealed class BlacksiteRoomGraph
    {
        private static readonly int[] NoNeighbours = new int[0];

        private readonly List<int>[] _neighbours;
        private readonly bool[] _explored;
        private int _exploredCount;

        internal BlacksiteRoomGraph(int count)
        {
            count = Math.Max(0, count);
            _neighbours = new List<int>[count];
            _explored = new bool[count];
        }

        /// <summary>Tile ids run 0..Count-1, whether or not a tile has any doorway.</summary>
        internal int Count => _neighbours.Length;

        internal int ExploredCount => _exploredCount;

        internal bool Contains(int tile) => tile >= 0 && tile < _neighbours.Length;

        /// <summary>Records a doorway between two tiles. Self edges and unknown ids are ignored.</summary>
        internal void AddEdge(int a, int b)
        {
            if (a == b || !Contains(a) || !Contains(b))
                return;
            Link(a, b);
            Link(b, a);
        }

        internal void MarkExplored(int tile)
        {
            if (!Contains(tile) || _explored[tile])
                return;
            _explored[tile] = true;
            _exploredCount++;
        }

        internal bool IsExplored(int tile) => Contains(tile) && _explored[tile];

        internal int Degree(int tile)
        {
            List<int> list = Contains(tile) ? _neighbours[tile] : null;
            return list != null ? list.Count : 0;
        }

        /// <summary>Neighbours in ascending tile id, which is what makes every plan deterministic.</summary>
        internal IReadOnlyList<int> Neighbours(int tile)
        {
            List<int> list = Contains(tile) ? _neighbours[tile] : null;
            return (IReadOnlyList<int>)list ?? NoNeighbours;
        }

        /// <summary>
        /// A tile whose graph degree is exhausted: every doorway out of it leads somewhere the
        /// squad has already explored, so there is nothing left to find through it. A tile with no
        /// doorway at all is exhausted by construction.
        /// </summary>
        internal bool IsDeadEnd(int tile)
        {
            if (!Contains(tile))
                return false;
            List<int> list = _neighbours[tile];
            if (list == null)
                return true;
            for (int i = 0; i < list.Count; i++)
                if (!_explored[list[i]])
                    return false;
            return true;
        }

        /// <summary>
        /// Shortest route from one tile to another by hop count, ties broken by the lower tile id.
        /// A hop is admitted only when <paramref name="hopProven"/> accepts it; the caller caches
        /// that verdict because it costs a NavMesh path. The route starts at <paramref name="from"/>
        /// and ends at <paramref name="to"/>; a route of one tile means the squad is already there.
        /// </summary>
        internal bool PlanRoute(int from, int to, Func<int, int, bool> hopProven, out List<int> route)
        {
            route = new List<int>();
            if (!Contains(from) || !Contains(to))
                return false;
            if (from == to)
            {
                route.Add(from);
                return true;
            }

            int[] distance = Distances(from, hopProven);
            if (distance[to] < 0)
                return false;
            return TryWalkBack(distance, from, to, hopProven, route);
        }

        /// <summary>
        /// Route to the nearest unexplored tile by hop count, ties broken by the lower tile id.
        /// This is the plan with no objective to go on. <paramref name="frontier"/> is -1 and the
        /// route empty when every reachable tile has been explored.
        /// </summary>
        internal bool PlanFrontier(int from, Func<int, int, bool> hopProven, out List<int> route, out int frontier)
        {
            route = new List<int>();
            frontier = -1;
            if (!Contains(from))
                return false;

            int[] distance = Distances(from, hopProven);
            int best = int.MaxValue;
            for (int tile = 0; tile < distance.Length; tile++)
            {
                // Ascending tile order, strict improvement: the lowest id wins a hop-count tie.
                if (distance[tile] < 0 || _explored[tile] || distance[tile] >= best)
                    continue;
                best = distance[tile];
                frontier = tile;
            }

            if (frontier < 0)
                return false;
            if (frontier == from)
            {
                route.Add(from);
                return true;
            }
            if (TryWalkBack(distance, from, frontier, hopProven, route))
                return true;

            frontier = -1;
            return false;
        }

        /// <summary>
        /// #989 C3. Route to the unexplored tile NEAREST THE OBJECTIVE's tile, for a known objective
        /// tile that <see cref="PlanRoute"/> cannot reach. This is the plan that turns "we cannot
        /// reach him, so we have no plan" into "we cannot reach him, so we open the next unexplored
        /// room toward him"; on 2026-09-12 the squad's only plan of the mission was a failed
        /// PlanRoute and it explored 11 of 58 tiles by accident in five minutes.
        ///
        /// The bias is measured on the RAW doorway graph, with no hop proof. An objective tile the
        /// proven hops cannot reach is, on an undirected graph with a symmetric proof, in a different
        /// component from <paramref name="from"/>, so every candidate would tie at "unreachable" if
        /// the filter were applied to both ends. The doorway distance is what still answers which
        /// unexplored room is nearest the one he is in, which is exactly the question a refused hop -
        /// a closed door, an elevator on the other floor - leaves open. Reachability and the route
        /// itself are still decided by <paramref name="hopProven"/>, so nothing is planned through a
        /// hop the caller refused.
        ///
        /// Ties are broken by fewer hops from <paramref name="from"/> and then by the lower tile id,
        /// the same two tie-breaks every other plan here uses. An objective tile that is not a tile
        /// at all degrades to <see cref="PlanFrontier"/> rather than refusing.
        ///
        /// #950 A4 (O2): <paramref name="worldDistanceToObjective"/> is the primary rank when the
        /// caller can supply it. Doorway hop count is a statement about the TOPOLOGY, and on a
        /// mineshaft's few huge cave tiles the topology is unrelated to physical proximity: on
        /// 2026-09-12 the objective tile was one doorway from most of the level, so every candidate
        /// tied at one hop and the lowest tile id won, 15 times, while the squad explored 7 of 86
        /// tiles. A world distance from the tile's own representative point to the objective is the
        /// question the squad is actually asking - which unexplored room is NEAR him - and hop
        /// count becomes what it should always have been here, the tie-break. The function is
        /// optional: pass null and the rank is the doorway distance exactly as before, which is what
        /// keeps a caller with no geometry (and the pure fixture) on the old rule.
        /// </summary>
        internal bool PlanFrontierToward(int from, int toward, Func<int, int, bool> hopProven,
            out List<int> route, out int frontier, Func<int, float> worldDistanceToObjective = null)
        {
            route = new List<int>();
            frontier = -1;
            if (!Contains(from))
                return false;
            if (!Contains(toward))
                return PlanFrontier(from, hopProven, out route, out frontier);

            int[] distance = Distances(from, hopProven);
            int[] doorways = worldDistanceToObjective == null ? Distances(toward, null) : null;
            float bestToward = float.MaxValue;
            int bestFrom = int.MaxValue;
            for (int tile = 0; tile < distance.Length; tile++)
            {
                // Ascending tile order, strict improvement: the lowest id wins a full tie.
                if (distance[tile] < 0 || _explored[tile])
                    continue;
                float toObjective;
                if (worldDistanceToObjective != null)
                {
                    toObjective = worldDistanceToObjective(tile);
                    // A tile with no measurable point is ranked last rather than refused, so a
                    // frontier still exists when the geometry is missing.
                    if (float.IsNaN(toObjective) || float.IsInfinity(toObjective) || toObjective < 0f)
                        toObjective = float.MaxValue;
                }
                else toObjective = doorways[tile] < 0 ? float.MaxValue : doorways[tile];
                if (toObjective > bestToward || (toObjective == bestToward && distance[tile] >= bestFrom))
                    continue;
                bestToward = toObjective;
                bestFrom = distance[tile];
                frontier = tile;
            }

            if (frontier < 0)
                return false;
            if (frontier == from)
            {
                route.Add(from);
                return true;
            }
            if (TryWalkBack(distance, from, frontier, hopProven, route))
                return true;

            frontier = -1;
            return false;
        }

        private void Link(int from, int to)
        {
            List<int> list = _neighbours[from] ?? (_neighbours[from] = new List<int>(4));
            int index = list.BinarySearch(to);
            if (index < 0)
                list.Insert(~index, to);
        }

        /// <summary>Hop counts from one tile over admitted hops only; -1 where the tile is unreachable.</summary>
        private int[] Distances(int from, Func<int, int, bool> hopProven)
        {
            var distance = new int[_neighbours.Length];
            for (int i = 0; i < distance.Length; i++)
                distance[i] = -1;

            var queue = new Queue<int>();
            distance[from] = 0;
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                List<int> list = _neighbours[current];
                if (list == null)
                    continue;
                for (int i = 0; i < list.Count; i++)
                {
                    int next = list[i];
                    if (distance[next] >= 0)
                        continue;
                    if (hopProven != null && !hopProven(current, next))
                        continue;
                    distance[next] = distance[current] + 1;
                    queue.Enqueue(next);
                }
            }

            return distance;
        }

        /// <summary>
        /// Walks the hop counts back from the target, taking the lowest-id predecessor at every
        /// step, and writes the result forwards. The predecessor's hop is re-proven because the
        /// delegate is the caller's cache: asking it again costs a dictionary lookup, and it is
        /// what keeps the returned route made only of hops the caller admitted.
        /// </summary>
        private bool TryWalkBack(int[] distance, int from, int target, Func<int, int, bool> hopProven, List<int> route)
        {
            var reversed = new List<int> { target };
            int current = target;
            while (current != from)
            {
                int previous = -1;
                List<int> list = _neighbours[current];
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        int candidate = list[i];
                        if (distance[candidate] != distance[current] - 1)
                            continue;
                        if (hopProven != null && !hopProven(candidate, current))
                            continue;
                        previous = candidate;
                        break;
                    }

                if (previous < 0 || reversed.Count > _neighbours.Length)
                {
                    route.Clear();
                    return false;
                }

                current = previous;
                reversed.Add(current);
            }

            for (int i = reversed.Count - 1; i >= 0; i--)
                route.Add(reversed[i]);
            return true;
        }
    }
}
