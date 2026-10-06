using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Contracts.ShadowRaid
{
    /// <summary>A doorway position projected into fractional grid-cell coordinates.</summary>
    internal readonly struct BreachContainerDoorwayCell
    {
        internal readonly float I;
        internal readonly float J;

        internal BreachContainerDoorwayCell(float i, float j)
        {
            I = i;
            J = j;
        }
    }

    /// <summary>
    /// One oriented container placement inside a tile's clear-cell mask. Coordinates are
    /// fractional cell units in the grid's own axes (i along the tile's right, j along its
    /// forward); the caller converts them back to world space.
    /// </summary>
    internal struct BreachContainerFitCandidate
    {
        internal bool HasFit;

        /// <summary>Index into the body-length list handed to the search (0 = preferred/longest).</summary>
        internal int SizeIndex;

        /// <summary>Tile-local yaw quadrant the drill face points to: 0=+J, 1=+I, 2=-J, 3=-I.</summary>
        internal int Quadrant;

        /// <summary>Container origin — the drill-face center on the floor — in fractional cell coords.</summary>
        internal float FaceCenterI;
        internal float FaceCenterJ;

        internal float BodyCenterI;
        internal float BodyCenterJ;

        /// <summary>Unit drill-face direction in grid axes.</summary>
        internal float ForwardI;
        internal float ForwardJ;

        /// <summary>Axis-aligned extent of the combined body+strip rectangle in whole cells.</summary>
        internal int RectOriginI;
        internal int RectOriginJ;
        internal int RectWidth;
        internal int RectHeight;

        /// <summary>Axis-aligned extent of the container body alone — the solid part.</summary>
        internal int BodyOriginI;
        internal int BodyOriginJ;
        internal int BodyWidthCells;
        internal int BodyHeightCells;

        /// <summary>Axis-aligned extent of the drill strip alone — reserved floor a body walks on.</summary>
        internal int StripOriginI;
        internal int StripOriginJ;
        internal int StripWidthCells;
        internal int StripHeightCells;

        /// <summary>
        /// The route width, in cells, this candidate was actually judged at: the caller's
        /// requested body width, or the narrower one the empty tile itself carries. Reported so
        /// a degraded tile names itself in the placer's log instead of passing silently.
        /// </summary>
        internal int RouteClearanceCells;

        internal float Score;
        internal float CenteringScore;
        internal float DoorwayScore;
        internal float BorderScore;
        internal float SizeBonus;
    }

    /// <summary>
    /// Pure rectangle-fit and scoring over a boolean clear-cell mask — no UnityEngine, no
    /// physics — so the Blacksite container search is testable headless. The container is an
    /// oriented rectangle of <c>widthCells x bodyLengthCells</c> whose drill face must have a
    /// further <c>stripCells</c>-deep strip of clear cells in front of it (the drill and its
    /// operator stand there). Orientations are the four tile-local yaw quadrants, so every
    /// candidate sits square to the room, and the combined body+strip rectangle stays
    /// axis-aligned in grid space.
    /// </summary>
    internal static class BreachContainerFitMath
    {
        // Scoring weights. Centering dominates (measured in meters, so it spans several units
        // on a big tile); the doorway-facing and border terms tilt near-central candidates
        // toward opening usefully into the room, and the preferred-size bonus keeps the search
        // from downgrading to a shorter container unless the longer one genuinely cannot fit
        // or fits only somewhere much worse.
        //
        // #1141: BorderClearWeight is set so that a fully clear one-cell ring (1.0 × weight)
        // outweighs 2 metres of centering (2.0 × CenteringWeightPerMeter). With typical
        // corridor border fractions of 0.2–0.4, an open-room pose beats a corridor pose of
        // equal centering by at least weight × 0.6 – weight × 0.8.
        internal const float CenteringWeightPerMeter = 1.0f;
        internal const float DoorwayFacingWeight = 1.5f;
        internal const float BorderClearWeight = 3.0f;
        internal const float PreferredSizeBonus = 1.5f;

        /// <summary>
        /// Finds the best-scoring oriented body+strip rectangle of fully clear cells, trying
        /// each body length in <paramref name="bodyLengthCellsPreferredFirst"/> (preferred
        /// first) across all four yaw quadrants. Returns a candidate with
        /// <see cref="BreachContainerFitCandidate.HasFit"/> false when nothing fits.
        ///
        /// Legality is primary, scoring secondary. A rectangle that merely fits the mask is
        /// taken only when it ALSO leaves, at <paramref name="routeClearanceCells"/> of width:
        /// every tile doorway connected to every other one past the whole footprint, and the
        /// drill face itself reachable from a doorway past the body. Centering, doorway facing,
        /// border and size then rank the candidates that survived that gate, so no score can
        /// buy a pose that seals the hallway it stands in.
        /// </summary>
        /// <param name="routeClearanceCells">
        /// How many cells wide a route has to stay for a walking body. The caller derives it
        /// from the agent diameter the navmesh is baked at. A tile whose own doorways cannot
        /// carry a route that wide with NOTHING placed falls back to one cell: there the tile,
        /// not the container, is what is narrow, and refusing every candidate would silently
        /// drop the requested container instead of placing it.
        /// </param>
        internal static BreachContainerFitCandidate FindBestFit(
            bool[] clear,
            int cellsX,
            int cellsZ,
            int widthCells,
            IReadOnlyList<int> bodyLengthCellsPreferredFirst,
            int stripCells,
            float cellSizeMeters,
            IReadOnlyList<BreachContainerDoorwayCell> doorways,
            int routeClearanceCells)
        {
            var best = default(BreachContainerFitCandidate);
            if (clear == null || cellsX <= 0 || cellsZ <= 0 || clear.Length < cellsX * cellsZ)
                return best;
            if (widthCells <= 0 || stripCells < 0 || bodyLengthCellsPreferredFirst == null)
                return best;

            // Summed-area table over the mask: any axis-aligned rectangle check is O(1).
            var sums = BuildClearSums(clear, cellsX, cellsZ);
            float tileCenterI = cellsX * 0.5f;
            float tileCenterJ = cellsZ * 0.5f;

            // #1141 / #874 R1: scratch buffers for the route floods and anchors. Allocated once
            // per tile so the per-candidate inner loops allocate nothing. baseAnchors holds each
            // doorway's own standing room measured on the EMPTY tile - the identity every
            // per-candidate anchor is bounded to, so no placement can satisfy the gate by
            // re-snapping a doorway it sealed to the far side of itself.
            int cellCount = cellsX * cellsZ;
            byte[] visited = null;
            int[] floodQueue = null;
            int[] baseAnchors = null;
            int[] doorwayAnchors = null;
            bool checkRoutes = doorways != null && doorways.Count > 0;
            int clearance = 1;
            bool baseConnected = false;
            if (checkRoutes)
            {
                visited = new byte[cellCount];
                floodQueue = new int[cellCount];
                baseAnchors = new int[doorways.Count];
                doorwayAnchors = new int[doorways.Count];
                clearance = ResolveRouteClearance(sums, cellsX, cellsZ, routeClearanceCells, doorways,
                    visited, floodQueue, baseAnchors, out baseConnected);
            }

            for (int sizeIndex = 0; sizeIndex < bodyLengthCellsPreferredFirst.Count; sizeIndex++)
            {
                int lengthCells = bodyLengthCellsPreferredFirst[sizeIndex];
                if (lengthCells <= 0)
                    continue;

                int along = lengthCells + stripCells;
                float sizeBonus = sizeIndex == 0 ? PreferredSizeBonus : 0f;

                for (int quadrant = 0; quadrant < 4; quadrant++)
                {
                    bool alongJ = quadrant == 0 || quadrant == 2;
                    int rectW = alongJ ? widthCells : along;
                    int rectH = alongJ ? along : widthCells;
                    if (rectW > cellsX || rectH > cellsZ)
                        continue;

                    for (int j0 = 0; j0 + rectH <= cellsZ; j0++)
                    {
                        for (int i0 = 0; i0 + rectW <= cellsX; i0++)
                        {
                            if (CountClear(sums, cellsX, i0, j0, rectW, rectH) != rectW * rectH)
                                continue;

                            BreachContainerFitCandidate candidate = DescribeCandidate(
                                quadrant, sizeIndex, i0, j0, widthCells, lengthCells, stripCells);
                            ScoreCandidate(
                                ref candidate,
                                clear,
                                cellsX,
                                cellsZ,
                                i0,
                                j0,
                                rectW,
                                rectH,
                                tileCenterI,
                                tileCenterJ,
                                cellSizeMeters,
                                doorways,
                                sizeBonus);

                            if (best.HasFit && candidate.Score <= best.Score)
                                continue;
                            // #1141 / #874 R1: the legality gate — each doorway keeps its own
                            // standing room, circulation survives past the footprint, and the
                            // drill face stays walkable to. Tested only for a candidate that
                            // would become the best: the gates walk the whole grid, and a scan
                            // of a 19-tile interior tries thousands of clear rectangles per tile
                            // inside the contract's budgeted slice, so they are paid once per
                            // improvement rather than once per rectangle. Scoring never bypasses
                            // them: `best` only ever holds a candidate that passed.
                            if (checkRoutes
                                && !RoutesSurvive(sums, cellsX, cellsZ, clearance, baseConnected, in candidate,
                                    doorways, visited, floodQueue, baseAnchors, doorwayAnchors))
                                continue;
                            candidate.RouteClearanceCells = checkRoutes ? clearance : 0;
                            best = candidate;
                        }
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// Geometry of one placement. The drill face sits on the cell boundary between the body
        /// rectangle and the clear strip in front of it; the body extends backward from the face.
        /// The two sub-rectangles are kept because they are not interchangeable to a route: the
        /// body is solid, while the strip is reserved floor the drill operator stands on.
        /// </summary>
        private static BreachContainerFitCandidate DescribeCandidate(
            int quadrant,
            int sizeIndex,
            int i0,
            int j0,
            int widthCells,
            int lengthCells,
            int stripCells)
        {
            bool alongJ = quadrant == 0 || quadrant == 2;
            int along = lengthCells + stripCells;
            var candidate = new BreachContainerFitCandidate
            {
                HasFit = true,
                SizeIndex = sizeIndex,
                Quadrant = quadrant,
                RectOriginI = i0,
                RectOriginJ = j0,
                RectWidth = alongJ ? widthCells : along,
                RectHeight = alongJ ? along : widthCells,
            };

            float halfWidth = widthCells * 0.5f;
            float halfLength = lengthCells * 0.5f;
            switch (quadrant)
            {
                case 0: // drill face points +J; body occupies the low-J part, strip beyond the face.
                    candidate.ForwardI = 0f;
                    candidate.ForwardJ = 1f;
                    candidate.FaceCenterI = i0 + halfWidth;
                    candidate.FaceCenterJ = j0 + lengthCells;
                    candidate.BodyCenterI = i0 + halfWidth;
                    candidate.BodyCenterJ = j0 + halfLength;
                    SetRects(ref candidate, i0, j0, widthCells, lengthCells, i0, j0 + lengthCells, widthCells, stripCells);
                    break;
                case 1: // drill face points +I.
                    candidate.ForwardI = 1f;
                    candidate.ForwardJ = 0f;
                    candidate.FaceCenterI = i0 + lengthCells;
                    candidate.FaceCenterJ = j0 + halfWidth;
                    candidate.BodyCenterI = i0 + halfLength;
                    candidate.BodyCenterJ = j0 + halfWidth;
                    SetRects(ref candidate, i0, j0, lengthCells, widthCells, i0 + lengthCells, j0, stripCells, widthCells);
                    break;
                case 2: // drill face points -J; strip occupies the low-J part of the rectangle.
                    candidate.ForwardI = 0f;
                    candidate.ForwardJ = -1f;
                    candidate.FaceCenterI = i0 + halfWidth;
                    candidate.FaceCenterJ = j0 + stripCells;
                    candidate.BodyCenterI = i0 + halfWidth;
                    candidate.BodyCenterJ = j0 + stripCells + halfLength;
                    SetRects(ref candidate, i0, j0 + stripCells, widthCells, lengthCells, i0, j0, widthCells, stripCells);
                    break;
                default: // 3: drill face points -I.
                    candidate.ForwardI = -1f;
                    candidate.ForwardJ = 0f;
                    candidate.FaceCenterI = i0 + stripCells;
                    candidate.FaceCenterJ = j0 + halfWidth;
                    candidate.BodyCenterI = i0 + stripCells + halfLength;
                    candidate.BodyCenterJ = j0 + halfWidth;
                    SetRects(ref candidate, i0 + stripCells, j0, lengthCells, widthCells, i0, j0, stripCells, widthCells);
                    break;
            }

            return candidate;
        }

        private static void SetRects(
            ref BreachContainerFitCandidate candidate,
            int bodyI, int bodyJ, int bodyW, int bodyH,
            int stripI, int stripJ, int stripW, int stripH)
        {
            candidate.BodyOriginI = bodyI;
            candidate.BodyOriginJ = bodyJ;
            candidate.BodyWidthCells = bodyW;
            candidate.BodyHeightCells = bodyH;
            candidate.StripOriginI = stripI;
            candidate.StripOriginJ = stripJ;
            candidate.StripWidthCells = stripW;
            candidate.StripHeightCells = stripH;
        }

        private static void ScoreCandidate(
            ref BreachContainerFitCandidate candidate,
            bool[] clear,
            int cellsX,
            int cellsZ,
            int i0,
            int j0,
            int rectW,
            int rectH,
            float tileCenterI,
            float tileCenterJ,
            float cellSizeMeters,
            IReadOnlyList<BreachContainerDoorwayCell> doorways,
            float sizeBonus)
        {
            // Dominant term: keep the body centered in the room rather than jammed into a corner.
            float di = (candidate.BodyCenterI - tileCenterI) * cellSizeMeters;
            float dj = (candidate.BodyCenterJ - tileCenterJ) * cellSizeMeters;
            candidate.CenteringScore = -CenteringWeightPerMeter * (float)Math.Sqrt(di * di + dj * dj);

            // Doorway-facing bonus: the best doorway the drill face opens toward.
            float bestFacing = 0f;
            if (doorways != null && doorways.Count > 0)
            {
                bestFacing = -1f;
                for (int d = 0; d < doorways.Count; d++)
                {
                    float toDoorI = doorways[d].I - candidate.BodyCenterI;
                    float toDoorJ = doorways[d].J - candidate.BodyCenterJ;
                    float length = (float)Math.Sqrt(toDoorI * toDoorI + toDoorJ * toDoorJ);
                    if (length < 1e-4f)
                        continue;

                    float facing = (candidate.ForwardI * toDoorI + candidate.ForwardJ * toDoorJ) / length;
                    if (facing > bestFacing)
                        bestFacing = facing;
                }

                if (bestFacing < -1f)
                    bestFacing = 0f;
            }

            candidate.DoorwayScore = DoorwayFacingWeight * bestFacing;
            candidate.BorderScore = BorderClearWeight * MeasureBorderClearFraction(clear, cellsX, cellsZ, i0, j0, rectW, rectH);
            candidate.SizeBonus = sizeBonus;
            candidate.Score = candidate.CenteringScore + candidate.DoorwayScore + candidate.BorderScore + candidate.SizeBonus;
        }

        /// <summary>Fraction of the one-cell ring around the rectangle that is clear; off-grid counts blocked.</summary>
        internal static float MeasureBorderClearFraction(
            bool[] clear,
            int cellsX,
            int cellsZ,
            int i0,
            int j0,
            int rectW,
            int rectH)
        {
            int total = 0;
            int clearCount = 0;

            for (int i = i0 - 1; i <= i0 + rectW; i++)
            {
                AccumulateBorderCell(clear, cellsX, cellsZ, i, j0 - 1, ref total, ref clearCount);
                AccumulateBorderCell(clear, cellsX, cellsZ, i, j0 + rectH, ref total, ref clearCount);
            }

            for (int j = j0; j < j0 + rectH; j++)
            {
                AccumulateBorderCell(clear, cellsX, cellsZ, i0 - 1, j, ref total, ref clearCount);
                AccumulateBorderCell(clear, cellsX, cellsZ, i0 + rectW, j, ref total, ref clearCount);
            }

            return total > 0 ? clearCount / (float)total : 0f;
        }

        private static void AccumulateBorderCell(
            bool[] clear,
            int cellsX,
            int cellsZ,
            int i,
            int j,
            ref int total,
            ref int clearCount)
        {
            total++;
            if (i < 0 || j < 0 || i >= cellsX || j >= cellsZ)
                return;

            if (clear[j * cellsX + i])
                clearCount++;
        }

        private static int[] BuildClearSums(bool[] clear, int cellsX, int cellsZ)
        {
            var sums = new int[(cellsX + 1) * (cellsZ + 1)];
            int stride = cellsX + 1;
            for (int j = 0; j < cellsZ; j++)
            {
                int rowSum = 0;
                for (int i = 0; i < cellsX; i++)
                {
                    if (clear[j * cellsX + i])
                        rowSum++;
                    sums[(j + 1) * stride + (i + 1)] = sums[j * stride + (i + 1)] + rowSum;
                }
            }

            return sums;
        }

        private static int CountClear(int[] sums, int cellsX, int i0, int j0, int rectW, int rectH)
        {
            int stride = cellsX + 1;
            return sums[(j0 + rectH) * stride + (i0 + rectW)]
                   - sums[j0 * stride + (i0 + rectW)]
                   - sums[(j0 + rectH) * stride + i0]
                   + sums[j0 * stride + i0];
        }

        // --- route legality (#1141 doorway connectivity, #874 R1 body width and face approach) ---

        /// <summary>
        /// What the EMPTY tile itself offers, measured before any candidate exists: the widest
        /// route up to <paramref name="requestedCells"/> at which every doorway has standing
        /// room, whether those doorways reach each other at that width
        /// (<paramref name="baseConnected"/>), and where each doorway's own standing room is
        /// (<paramref name="baseAnchors"/>).
        ///
        /// All three are BASELINES, never verdicts: the candidate is judged on whether it
        /// preserves them. That is what stops a tile narrower than a body from licensing a pose
        /// that seals it further - the same three gates still run, at the width the tile
        /// actually carries and against the doorway support the tile actually had - and the
        /// width travels out on the winning candidate, so a degraded tile names itself in the
        /// placer's log instead of passing silently.
        /// </summary>
        private static int ResolveRouteClearance(
            int[] sums,
            int cellsX,
            int cellsZ,
            int requestedCells,
            IReadOnlyList<BreachContainerDoorwayCell> doorways,
            byte[] visited,
            int[] queue,
            int[] baseAnchors,
            out bool baseConnected)
        {
            int widest = Math.Max(1, Math.Min(requestedCells, Math.Min(cellsX, cellsZ)));
            for (int k = widest; k >= 1; k--)
            {
                bool anchored = true;
                for (int d = 0; d < doorways.Count && anchored; d++)
                {
                    baseAnchors[d] = NearestTraversableAnchor(sums, cellsX, cellsZ, k, doorways[d].I, doorways[d].J, 0, 0, 0, 0);
                    anchored = baseAnchors[d] >= 0;
                }

                if (!anchored)
                    continue;
                baseConnected = doorways.Count < 2
                    || AnchorsConnected(sums, cellsX, cellsZ, k, 0, 0, 0, 0, baseAnchors, doorways.Count, visited, queue);
                return k;
            }

            // No doorway of this tile has a standing cell at all: the mask carries no floor for
            // the gates to reason about, and every candidate is refused by the missing anchor.
            baseConnected = false;
            return 1;
        }

        /// <summary>
        /// The three route gates for one candidate, cheapest first: every doorway keeps its own
        /// standing room, the circulation the tile HAD survives past the whole footprint, and
        /// the drill face stays walkable to past the body.
        /// </summary>
        private static bool RoutesSurvive(
            int[] sums,
            int cellsX,
            int cellsZ,
            int clearance,
            bool preserveConnectivity,
            in BreachContainerFitCandidate candidate,
            IReadOnlyList<BreachContainerDoorwayCell> doorways,
            byte[] visited,
            int[] queue,
            int[] baseAnchors,
            int[] anchors)
        {
            int n = doorways.Count;
            int rectI = candidate.RectOriginI, rectJ = candidate.RectOriginJ;
            int rectW = candidate.RectWidth, rectH = candidate.RectHeight;
            for (int d = 0; d < n; d++)
            {
                anchors[d] = AnchorNear(sums, cellsX, cellsZ, clearance, baseAnchors[d], rectI, rectJ, rectW, rectH);
                // The placement has taken this doorway's own standing room and left no
                // body-width window within one body of it. That is a sealed doorway, whatever
                // the rest of the tile looks like.
                if (anchors[d] < 0)
                    return false;
            }

            // Circulation is a preservation rule: a tile whose doorways were never connected in
            // this mask has none for the container to take away, and demanding it there would
            // refuse every candidate for a defect in the floor sampling rather than in the pose.
            if (preserveConnectivity && n >= 2
                && !AnchorsConnected(sums, cellsX, cellsZ, clearance, rectI, rectJ, rectW, rectH, anchors, n, visited, queue))
                return false;

            return DrillFaceReachable(sums, cellsX, cellsZ, clearance, in candidate, n, visited, queue, baseAnchors, anchors);
        }

        /// <summary>
        /// Every anchor reachable from the first, through routes <paramref name="clearance"/>
        /// cells wide that clear the given rectangle. Anchors that resolve to one cell are
        /// trivially the same standing room and skip the walk. The scratch buffers are
        /// caller-allocated for reuse — no per-candidate allocation.
        /// </summary>
        private static bool AnchorsConnected(
            int[] sums,
            int cellsX,
            int cellsZ,
            int clearance,
            int rectI0,
            int rectJ0,
            int rectW,
            int rectH,
            int[] anchors,
            int count,
            byte[] visited,
            int[] queue)
        {
            bool allSame = true;
            for (int d = 1; d < count && allSame; d++)
                allSame = anchors[d] == anchors[0];
            if (allSame)
                return true;

            Flood(sums, cellsX, cellsZ, clearance, rectI0, rectJ0, rectW, rectH, anchors, 1, visited, queue);
            for (int d = 1; d < count; d++)
            {
                if (visited[anchors[d]] == 0)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// #874 R1: the drill face has to be approachable. A container whose face opens into a
        /// wall, into its own blocked corner, or into a pocket no doorway reaches is a container
        /// nobody can drill, however well it scores on centering. Only the body is solid here;
        /// the operator's strip is where he ends up standing, so an anchor whose body-width
        /// window meets the strip is the approach. The seeds are the doorways' own standing
        /// room, moved by at most one body width, exactly as the circulation gate's are.
        /// </summary>
        private static bool DrillFaceReachable(
            int[] sums,
            int cellsX,
            int cellsZ,
            int clearance,
            in BreachContainerFitCandidate candidate,
            int doorwayCount,
            byte[] visited,
            int[] queue,
            int[] baseAnchors,
            int[] anchors)
        {
            int stripW = candidate.StripWidthCells, stripH = candidate.StripHeightCells;
            if (stripW <= 0 || stripH <= 0)
                return true;

            int bodyI = candidate.BodyOriginI, bodyJ = candidate.BodyOriginJ;
            int bodyW = candidate.BodyWidthCells, bodyH = candidate.BodyHeightCells;
            int seeds = 0;
            for (int d = 0; d < doorwayCount; d++)
            {
                int anchor = AnchorNear(sums, cellsX, cellsZ, clearance, baseAnchors[d], bodyI, bodyJ, bodyW, bodyH);
                if (anchor >= 0)
                    anchors[seeds++] = anchor;
            }

            if (seeds == 0)
                return false;

            // One multi-source flood: the question is whether SOME doorway reaches the face, so
            // every doorway seeds the same walk instead of paying one walk each.
            Flood(sums, cellsX, cellsZ, clearance, bodyI, bodyJ, bodyW, bodyH, anchors, seeds, visited, queue);

            int iMin = candidate.StripOriginI - (clearance - 1), iMax = candidate.StripOriginI + stripW - 1;
            int jMin = candidate.StripOriginJ - (clearance - 1), jMax = candidate.StripOriginJ + stripH - 1;
            for (int j = Math.Max(0, jMin); j <= jMax && j + clearance <= cellsZ; j++)
            {
                for (int i = Math.Max(0, iMin); i <= iMax && i + clearance <= cellsX; i++)
                {
                    if (visited[j * cellsX + i] != 0)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The traversable anchor nearest <paramref name="baseAnchor"/> with the candidate
        /// rectangle in place, searched outward in Chebyshev rings and bounded to
        /// <paramref name="clearance"/> of them — one body width of slack around the standing
        /// room the EMPTY tile gave this doorway. The bound is the point of the method: an
        /// unbounded re-snap can hop a sealed doorway's anchor over the container or through a
        /// wall to the far side, where the flood then reports two sealed doorways as connected
        /// (or as the same anchor). A body crosses neither a wall nor a 3 m container inside one
        /// body width, so nothing this returns is on the other side of either. -1 means the
        /// placement has taken this doorway's standing room.
        /// </summary>
        private static int AnchorNear(
            int[] sums,
            int cellsX,
            int cellsZ,
            int clearance,
            int baseAnchor,
            int rectI0,
            int rectJ0,
            int rectW,
            int rectH)
        {
            if (baseAnchor < 0)
                return -1;
            int baseI = baseAnchor % cellsX, baseJ = baseAnchor / cellsX;
            for (int r = 0; r <= clearance; r++)
            {
                int iMin = baseI - r, iMax = baseI + r, jMin = baseJ - r, jMax = baseJ + r;
                for (int j = jMin; j <= jMax; j++)
                {
                    for (int i = iMin; i <= iMax; i++)
                    {
                        if (r > 0 && i > iMin && i < iMax && j > jMin && j < jMax)
                            continue;
                        if (Traversable(sums, cellsX, cellsZ, clearance, i, j, rectI0, rectJ0, rectW, rectH))
                            return j * cellsX + i;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// Breadth-first walk over the anchors of a <paramref name="clearance"/>-square body
        /// window, four-connected, treating the given rectangle as solid. An anchor is the
        /// low-corner cell of the window, so the anchor grid is indexed in the cell grid's own
        /// flat coordinates and shares its scratch buffers.
        /// </summary>
        private static void Flood(
            int[] sums,
            int cellsX,
            int cellsZ,
            int clearance,
            int rectI0,
            int rectJ0,
            int rectW,
            int rectH,
            int[] seeds,
            int seedCount,
            byte[] visited,
            int[] queue)
        {
            Array.Clear(visited, 0, cellsX * cellsZ);
            int head = 0, tail = 0;
            for (int s = 0; s < seedCount; s++)
            {
                int seed = seeds[s];
                if (visited[seed] != 0)
                    continue;
                visited[seed] = 1;
                queue[tail++] = seed;
            }

            while (head < tail)
            {
                int cell = queue[head++];
                int ci = cell % cellsX;
                int cj = cell / cellsX;

                Enqueue(sums, cellsX, cellsZ, clearance, rectI0, rectJ0, rectW, rectH, ci - 1, cj, visited, queue, ref tail);
                Enqueue(sums, cellsX, cellsZ, clearance, rectI0, rectJ0, rectW, rectH, ci + 1, cj, visited, queue, ref tail);
                Enqueue(sums, cellsX, cellsZ, clearance, rectI0, rectJ0, rectW, rectH, ci, cj - 1, visited, queue, ref tail);
                Enqueue(sums, cellsX, cellsZ, clearance, rectI0, rectJ0, rectW, rectH, ci, cj + 1, visited, queue, ref tail);
            }
        }

        private static void Enqueue(
            int[] sums,
            int cellsX,
            int cellsZ,
            int clearance,
            int rectI0,
            int rectJ0,
            int rectW,
            int rectH,
            int ci,
            int cj,
            byte[] visited,
            int[] queue,
            ref int tail)
        {
            if (!Traversable(sums, cellsX, cellsZ, clearance, ci, cj, rectI0, rectJ0, rectW, rectH))
                return;

            int idx = cj * cellsX + ci;
            if (visited[idx] != 0)
                return;

            visited[idx] = 1;
            queue[tail++] = idx;
        }

        /// <summary>
        /// True when a <paramref name="clearance"/>-square body window anchored at (i, j) lies
        /// inside the grid, is entirely clear mask, and touches none of the blocked rectangle.
        /// The blocked rectangle is always a fully clear rectangle of this same mask (that is
        /// what the fit search selects), so "does not overlap it" is the whole test — no
        /// second summed-area table is needed to subtract it out.
        /// </summary>
        private static bool Traversable(
            int[] sums,
            int cellsX,
            int cellsZ,
            int clearance,
            int i,
            int j,
            int rectI0,
            int rectJ0,
            int rectW,
            int rectH)
        {
            if (i < 0 || j < 0 || i + clearance > cellsX || j + clearance > cellsZ)
                return false;
            if (Overlaps(i, j, clearance, clearance, rectI0, rectJ0, rectW, rectH))
                return false;
            return CountClear(sums, cellsX, i, j, clearance, clearance) == clearance * clearance;
        }

        private static bool Overlaps(int ai, int aj, int aw, int ah, int bi, int bj, int bw, int bh)
        {
            if (bw <= 0 || bh <= 0)
                return false;
            return ai < bi + bw && bi < ai + aw && aj < bj + bh && bj < aj + ah;
        }

        /// <summary>
        /// The traversable anchor nearest the fractional doorway position, searching outward in
        /// expanding Chebyshev rings from the window that would be centred on it. Returns the
        /// flat anchor index or -1 when the doorway has no body-width standing room at all.
        /// </summary>
        private static int NearestTraversableAnchor(
            int[] sums,
            int cellsX,
            int cellsZ,
            int clearance,
            float di,
            float dj,
            int rectI0,
            int rectJ0,
            int rectW,
            int rectH)
        {
            int startI = (int)Math.Floor(di - clearance * 0.5f);
            int startJ = (int)Math.Floor(dj - clearance * 0.5f);
            int lastI = cellsX - clearance, lastJ = cellsZ - clearance;
            if (lastI < 0 || lastJ < 0)
                return -1;
            if (startI < 0) startI = 0;
            if (startI > lastI) startI = lastI;
            if (startJ < 0) startJ = 0;
            if (startJ > lastJ) startJ = lastJ;

            int maxR = Math.Max(cellsX, cellsZ);
            for (int r = 0; r <= maxR; r++)
            {
                int iMin = Math.Max(0, startI - r), iMax = Math.Min(lastI, startI + r);
                int jMin = Math.Max(0, startJ - r), jMax = Math.Min(lastJ, startJ + r);
                for (int j = jMin; j <= jMax; j++)
                {
                    for (int i = iMin; i <= iMax; i++)
                    {
                        // Only the perimeter of the expanding box; the inside belongs to a
                        // smaller ring that has already been tested.
                        if (r > 0 && i > iMin && i < iMax && j > jMin && j < jMax)
                            continue;
                        if (Traversable(sums, cellsX, cellsZ, clearance, i, j, rectI0, rectJ0, rectW, rectH))
                            return j * cellsX + i;
                    }
                }
            }

            return -1;
        }
    }
}
