using System.Collections.Generic;
using DunGen;
using UnityEngine;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Hop distances over the generated dungeon's tile graph, so a contract can reason in
    /// "rooms away" instead of metres through walls (#765). Built on demand from
    /// <c>Dungeon.AllTiles</c> and thrown away with the round; one BFS per start tile.
    /// <see cref="Contracts.BlackoutAudit.BlackoutTileGraph"/> is the zone-shaped
    /// sibling this was lifted from.
    /// </summary>
    internal sealed class FacilityTileGraph
    {
        private readonly List<Tile> _tiles;
        private readonly List<int>[] _adjacency;

        internal int TileCount => _tiles.Count;

        private FacilityTileGraph(List<Tile> tiles)
        {
            _tiles = tiles;
            var index = new Dictionary<Tile, int>(tiles.Count);
            for (int i = 0; i < tiles.Count; i++)
                index[tiles[i]] = i;

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
                        if (other != null && index.TryGetValue(other, out int otherIndex) && otherIndex != i)
                            neighbours.Add(otherIndex);
                    }
                }

                _adjacency[i] = neighbours;
            }
        }

        /// <summary>Null when no dungeon has been generated; callers fall back to a
        /// distance metric.</summary>
        internal static FacilityTileGraph TryBuild()
        {
            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null || dungeon.AllTiles.Count == 0)
                return null;

            var tiles = new List<Tile>(dungeon.AllTiles.Count);
            foreach (Tile tile in dungeon.AllTiles)
            {
                if (tile != null && tile.Placement != null)
                    tiles.Add(tile);
            }

            return tiles.Count > 0 ? new FacilityTileGraph(tiles) : null;
        }

        /// <summary>Index of the tile whose placement bounds contain <paramref name="position"/>,
        /// or the nearest tile when none does. -1 only for an empty graph.</summary>
        internal int FindTile(Vector3 position)
        {
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < _tiles.Count; i++)
            {
                Bounds bounds = _tiles[i].Placement.Bounds;
                if (bounds.Contains(position))
                    return i;

                float distance = bounds.SqrDistance(position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>Breadth-first hop count from <paramref name="start"/> to every tile;
        /// -1 marks tiles the doorway graph cannot reach.</summary>
        internal int[] HopsFrom(int start)
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

        /// <summary>Hops from a precomputed <see cref="HopsFrom"/> table to the tile holding
        /// <paramref name="position"/>; -1 when unreachable or unknown.</summary>
        internal int HopsTo(int[] hops, Vector3 position)
        {
            if (hops == null)
                return -1;
            int tile = FindTile(position);
            return tile < 0 || tile >= hops.Length ? -1 : hops[tile];
        }
    }
}
