using System.Collections.Generic;
using DunGen;
using UnityEngine;

namespace Y4NGZCompany.Contracts.BlackoutAudit
{
    /// <summary>
    /// #172 item 2. Hop distances over the generated dungeon's tile graph, one BFS per
    /// breaker zone, so fixtures can be grouped by the wing they are actually in rather
    /// than by straight-line distance through walls.
    ///
    /// Built once at zone init (or a client rebuild) and thrown away. The cost is one pass
    /// over <c>AllTiles</c> per zone plus one tile lookup per fixture; on the largest
    /// interiors that is a few thousand bounds tests, well under a millisecond.
    /// </summary>
    internal sealed class BlackoutTileGraph
    {
        private readonly List<Tile> _tiles;
        private readonly Dictionary<Tile, int> _tileIndex;
        private readonly List<int>[] _adjacency;
        private readonly Dictionary<int, int[]> _hopsByZone = new Dictionary<int, int[]>();
        private readonly Dictionary<Vector3, int> _tileForPosition = new Dictionary<Vector3, int>();

        internal int TileCount => _tiles.Count;
        internal string Method => "tile-graph";

        private BlackoutTileGraph(List<Tile> tiles)
        {
            _tiles = tiles;
            _tileIndex = new Dictionary<Tile, int>(tiles.Count);
            for (int i = 0; i < tiles.Count; i++)
                _tileIndex[tiles[i]] = i;

            _adjacency = new List<int>[tiles.Count];
            for (int i = 0; i < tiles.Count; i++)
            {
                var neighbours = new List<int>(4);
                Tile tile = tiles[i];
                if (tile.UsedDoorways != null)
                {
                    foreach (Doorway doorway in tile.UsedDoorways)
                    {
                        Tile other = doorway?.ConnectedDoorway?.Tile;
                        if (other != null && _tileIndex.TryGetValue(other, out int otherIndex) && otherIndex != i)
                            neighbours.Add(otherIndex);
                    }
                }

                _adjacency[i] = neighbours;
            }
        }

        /// <summary>Null when no dungeon has been generated, which makes the caller fall back
        /// to straight-line assignment.</summary>
        internal static BlackoutTileGraph TryBuild(List<BlackoutBreakerZone> zones)
        {
            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null || dungeon.AllTiles.Count == 0 || zones == null || zones.Count == 0)
                return null;

            var tiles = new List<Tile>(dungeon.AllTiles.Count);
            foreach (Tile tile in dungeon.AllTiles)
            {
                if (tile != null)
                    tiles.Add(tile);
            }

            if (tiles.Count == 0)
                return null;

            var graph = new BlackoutTileGraph(tiles);
            foreach (BlackoutBreakerZone zone in zones)
            {
                int start = graph.FindTile(zone.Position);
                graph._hopsByZone[zone.Index] = graph.BreadthFirst(start);
            }

            return graph;
        }

        /// <summary>Tile hops from the zone's breaker to the tile holding <paramref name="position"/>.
        /// Unreachable or unknown tiles report a large finite value so straight-line distance
        /// still orders them.</summary>
        internal int HopsFromZone(int zoneIndex, Vector3 position)
        {
            if (!_hopsByZone.TryGetValue(zoneIndex, out int[] hops))
                return 100000;

            int tile = FindTile(position);
            if (tile < 0 || tile >= hops.Length)
                return 100000;

            int value = hops[tile];
            return value < 0 ? 100000 : value;
        }

        private int FindTile(Vector3 position)
        {
            if (_tileForPosition.TryGetValue(position, out int cached))
                return cached;

            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < _tiles.Count; i++)
            {
                Bounds bounds = _tiles[i].Placement.Bounds;
                if (bounds.Contains(position))
                {
                    best = i;
                    break;
                }

                float distance = bounds.SqrDistance(position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            _tileForPosition[position] = best;
            return best;
        }

        private int[] BreadthFirst(int start)
        {
            var hops = new int[_tiles.Count];
            for (int i = 0; i < hops.Length; i++)
                hops[i] = -1;

            if (start < 0 || start >= hops.Length)
                return hops;

            var queue = new Queue<int>();
            hops[start] = 0;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                List<int> neighbours = _adjacency[current];
                for (int i = 0; i < neighbours.Count; i++)
                {
                    int next = neighbours[i];
                    if (hops[next] >= 0)
                        continue;

                    hops[next] = hops[current] + 1;
                    queue.Enqueue(next);
                }
            }

            return hops;
        }
    }
}
