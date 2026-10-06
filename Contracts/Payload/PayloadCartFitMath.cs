using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Contracts.Payload
{
    /// <summary>A doorway position projected into fractional grid-cell coordinates.</summary>
    internal readonly struct PayloadCartDoorwayCell
    {
        internal readonly float I;
        internal readonly float J;

        internal PayloadCartDoorwayCell(float i, float j)
        {
            I = i;
            J = j;
        }
    }

    /// <summary>
    /// One clearance tier the fit may settle for. Tiers are tried preferred-first; a
    /// lower tier drops the travel lane and then narrows the pusher strip so a cramped room
    /// still gets a cart on genuinely clear floor rather than the old tile-center guess. The
    /// strip is never dropped outright: the player grabs the cart from behind the handle (#877).
    /// </summary>
    internal readonly struct PayloadCartFitTier
    {
        internal readonly string Name;

        /// <summary>Clear cells behind the handle where the pusher stands.</summary>
        internal readonly int PusherCells;

        /// <summary>Clear cells ahead of the cart so the first push does not hit a wall.</summary>
        internal readonly int LaneCells;

        internal PayloadCartFitTier(string name, int pusherCells, int laneCells)
        {
            Name = name ?? string.Empty;
            PusherCells = Math.Max(0, pusherCells);
            LaneCells = Math.Max(0, laneCells);
        }
    }

    /// <summary>
    /// One oriented cart placement inside a tile's clear-cell mask. Coordinates are
    /// fractional cell units in the grid's own axes (i along the tile's right, j along its
    /// forward); the caller converts them back to world space.
    /// </summary>
    internal struct PayloadCartFitCandidate
    {
        internal bool HasFit;

        /// <summary>Index into the tier list handed to the search (0 = preferred).</summary>
        internal int TierIndex;

        /// <summary>Tile-local yaw quadrant the handle (cart local +Z) points to: 0=+J, 1=+I, 2=-J, 3=-I.</summary>
        internal int Quadrant;

        /// <summary>Cart origin — the body center on the floor — in fractional cell coords.</summary>
        internal float BodyCenterI;
        internal float BodyCenterJ;

        /// <summary>Unit direction from the body toward the pusher strip, in grid axes. The cart travels the other way.</summary>
        internal float HandleI;
        internal float HandleJ;

        /// <summary>Axis-aligned extent of the combined lane+body+strip rectangle in whole cells.</summary>
        internal int RectOriginI;
        internal int RectOriginJ;
        internal int RectWidth;
        internal int RectHeight;

        /// <summary>Clear cells beyond the required lane along the travel direction (capped).</summary>
        internal int RunwayCells;

        internal float Score;
        internal float CenteringScore;
        internal float DoorwayScore;
        internal float BorderScore;
        internal float RunwayScore;
        internal float TierBonus;
    }

    /// <summary>
    /// Pure rectangle-fit and scoring over a boolean clear-cell mask — no UnityEngine, no
    /// physics — so the Payload cart spawn search is testable headless. The cart is an
    /// oriented rectangle of <c>widthCells x bodyLengthCells</c>; behind its handle (local +Z)
    /// a further <c>PusherCells</c>-deep strip must be clear for the player, and ahead of it
    /// <c>LaneCells</c> of clear floor guarantee the first push moves. Orientations are the
    /// four tile-local yaw quadrants so every candidate sits square to the room, and the
    /// combined rectangle stays axis-aligned in grid space.
    /// </summary>
    internal static class PayloadCartFitMath
    {
        /// <summary>
        /// Pull toward the room centre (#1271: the cart spawns in the middle of the room, clear of
        /// every wall and object). It dominates the pose: one metre off centre outweighs the whole
        /// swing of the other terms, doorway facing (-1.5..+1.5) plus border (0..1) plus runway
        /// (0..2), which is 6.0; their largest positive sum is 4.5. The tier bonus never competes
        /// because the search stops at the first tier that fits. Those terms only break ties between
        /// equally centred poses, such as which way the cart faces.
        /// </summary>
        internal const float CenteringWeightPerMeter = 6.5f;

        /// <summary>Travel direction pointing toward a doorway: the cart leaves the room the way it faces.</summary>
        internal const float DoorwayFacingWeight = 1.5f;

        /// <summary>Fraction of the one-cell ring around the rectangle that is clear.</summary>
        internal const float BorderClearWeight = 1.0f;

        /// <summary>Per extra clear cell of runway beyond the required lane.</summary>
        internal const float RunwayWeightPerCell = 0.25f;

        /// <summary>Extra runway cells counted toward the score; more open floor than this is not better.</summary>
        internal const int MaxRunwayCells = 8;

        /// <summary>Each tier below the preferred one loses this much, so a lane is dropped only when it cannot fit anywhere.</summary>
        internal const float TierPenalty = 6f;

        /// <summary>
        /// A doorway this close (in cells) to the rectangle disqualifies it: the cart must never
        /// spawn in or against the opening the crew (or the cart itself) has to pass through.
        /// </summary>
        internal const float DoorwayExclusionCells = 1f;

        /// <summary>
        /// Chebyshev margin in cells between the rectangle and any cell that drops away (#877).
        /// The runway measurement stops at a ledge instead of rejecting it, so nothing else keeps
        /// the cart off a walkway above a pit; two cells is a metre of floor between the widest
        /// part of the rectangle and the lip, which survives the shove a Blacksite operator gives it.
        /// </summary>
        internal const int DropExclusionCells = 2;

        // #1305 C1 (D6): one clear-sum buffer for every refit; the fit runs on the main thread only.
        private static int[] _clearSums = new int[0];

        /// <summary>
        /// Finds the best-scoring oriented rectangle of fully clear cells across all four yaw
        /// quadrants and every tier in <paramref name="tiersPreferredFirst"/>. Returns a
        /// candidate with <see cref="PayloadCartFitCandidate.HasFit"/> false when nothing fits.
        /// <paramref name="drop"/> is an optional per-cell mask of floor that falls away; a
        /// rectangle within <see cref="DropExclusionCells"/> of one is rejected.
        /// </summary>
        internal static PayloadCartFitCandidate FindBestFit(
            bool[] clear,
            int cellsX,
            int cellsZ,
            int widthCells,
            int bodyLengthCells,
            IReadOnlyList<PayloadCartFitTier> tiersPreferredFirst,
            float cellSizeMeters,
            IReadOnlyList<PayloadCartDoorwayCell> doorways,
            bool[] drop = null)
        {
            var best = default(PayloadCartFitCandidate);
            if (clear == null || cellsX <= 0 || cellsZ <= 0 || clear.Length < cellsX * cellsZ)
                return best;
            if (widthCells <= 0 || bodyLengthCells <= 0 || tiersPreferredFirst == null || tiersPreferredFirst.Count == 0)
                return best;

            int[] sums = BuildClearSums(clear, cellsX, cellsZ);

            // A mask with no drop at all is the common case; resolving that once keeps the
            // per-candidate margin scan out of every ordinary room.
            if (drop != null && (drop.Length < cellsX * cellsZ || !HasAnyDrop(drop, cellsX * cellsZ)))
                drop = null;

            float tileCenterI = cellsX * 0.5f;
            float tileCenterJ = cellsZ * 0.5f;

            for (int tierIndex = 0; tierIndex < tiersPreferredFirst.Count; tierIndex++)
            {
                PayloadCartFitTier tier = tiersPreferredFirst[tierIndex];
                int along = tier.LaneCells + bodyLengthCells + tier.PusherCells;
                float tierBonus = -TierPenalty * tierIndex;

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

                            if (TouchesDoorway(doorways, i0, j0, rectW, rectH))
                                continue;

                            if (TouchesDrop(drop, cellsX, cellsZ, i0, j0, rectW, rectH))
                                continue;

                            PayloadCartFitCandidate candidate = DescribeCandidate(
                                quadrant, tierIndex, i0, j0, widthCells, bodyLengthCells, tier);
                            ScoreCandidate(
                                ref candidate,
                                clear,
                                sums,
                                cellsX,
                                cellsZ,
                                i0,
                                j0,
                                rectW,
                                rectH,
                                widthCells,
                                tileCenterI,
                                tileCenterJ,
                                cellSizeMeters,
                                doorways,
                                tierBonus);

                            if (!best.HasFit || candidate.Score > best.Score)
                                best = candidate;
                        }
                    }
                }

                // A fit at the preferred tier always beats a lower tier by construction of the
                // penalty, so stop descending once one exists.
                if (best.HasFit)
                    break;
            }

            return best;
        }

        /// <summary>
        /// Geometry of one placement. Along the quadrant axis the rectangle reads
        /// lane, body, pusher strip; the body center is the cart origin.
        /// </summary>
        private static PayloadCartFitCandidate DescribeCandidate(
            int quadrant,
            int tierIndex,
            int i0,
            int j0,
            int widthCells,
            int bodyLengthCells,
            PayloadCartFitTier tier)
        {
            bool alongJ = quadrant == 0 || quadrant == 2;
            int along = tier.LaneCells + bodyLengthCells + tier.PusherCells;
            var candidate = new PayloadCartFitCandidate
            {
                HasFit = true,
                TierIndex = tierIndex,
                Quadrant = quadrant,
                RectOriginI = i0,
                RectOriginJ = j0,
                RectWidth = alongJ ? widthCells : along,
                RectHeight = alongJ ? along : widthCells,
            };

            float halfWidth = widthCells * 0.5f;
            float halfLength = bodyLengthCells * 0.5f;
            switch (quadrant)
            {
                case 0: // handle points +J: lane at low J, then body, then pusher strip at high J.
                    candidate.HandleI = 0f;
                    candidate.HandleJ = 1f;
                    candidate.BodyCenterI = i0 + halfWidth;
                    candidate.BodyCenterJ = j0 + tier.LaneCells + halfLength;
                    break;
                case 1: // handle points +I.
                    candidate.HandleI = 1f;
                    candidate.HandleJ = 0f;
                    candidate.BodyCenterI = i0 + tier.LaneCells + halfLength;
                    candidate.BodyCenterJ = j0 + halfWidth;
                    break;
                case 2: // handle points -J: pusher strip at low J, then body, then lane at high J.
                    candidate.HandleI = 0f;
                    candidate.HandleJ = -1f;
                    candidate.BodyCenterI = i0 + halfWidth;
                    candidate.BodyCenterJ = j0 + tier.PusherCells + halfLength;
                    break;
                default: // 3: handle points -I.
                    candidate.HandleI = -1f;
                    candidate.HandleJ = 0f;
                    candidate.BodyCenterI = i0 + tier.PusherCells + halfLength;
                    candidate.BodyCenterJ = j0 + halfWidth;
                    break;
            }

            return candidate;
        }

        private static void ScoreCandidate(
            ref PayloadCartFitCandidate candidate,
            bool[] clear,
            int[] sums,
            int cellsX,
            int cellsZ,
            int i0,
            int j0,
            int rectW,
            int rectH,
            int widthCells,
            float tileCenterI,
            float tileCenterJ,
            float cellSizeMeters,
            IReadOnlyList<PayloadCartDoorwayCell> doorways,
            float tierBonus)
        {
            float di = (candidate.BodyCenterI - tileCenterI) * cellSizeMeters;
            float dj = (candidate.BodyCenterJ - tileCenterJ) * cellSizeMeters;
            candidate.CenteringScore = -CenteringWeightPerMeter * (float)Math.Sqrt(di * di + dj * dj);

            // Travel direction is away from the handle. The best doorway it points at scores.
            float travelI = -candidate.HandleI;
            float travelJ = -candidate.HandleJ;
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

                    float facing = (travelI * toDoorI + travelJ * toDoorJ) / length;
                    if (facing > bestFacing)
                        bestFacing = facing;
                }

                if (bestFacing < -1f)
                    bestFacing = 0f;
            }

            candidate.DoorwayScore = DoorwayFacingWeight * bestFacing;
            candidate.BorderScore = BorderClearWeight * MeasureBorderClearFraction(clear, cellsX, cellsZ, i0, j0, rectW, rectH);
            candidate.RunwayCells = MeasureRunway(sums, cellsX, cellsZ, i0, j0, rectW, rectH, widthCells, travelI, travelJ);
            candidate.RunwayScore = RunwayWeightPerCell * candidate.RunwayCells;
            candidate.TierBonus = tierBonus;
            candidate.Score = candidate.CenteringScore + candidate.DoorwayScore + candidate.BorderScore + candidate.RunwayScore + candidate.TierBonus;
        }

        /// <summary>
        /// True when any doorway lies inside the rectangle grown by <see cref="DoorwayExclusionCells"/>
        /// on every side: the cart would sit in, or hard against, the opening.
        /// </summary>
        internal static bool TouchesDoorway(
            IReadOnlyList<PayloadCartDoorwayCell> doorways,
            int i0,
            int j0,
            int rectW,
            int rectH)
        {
            if (doorways == null)
                return false;

            float minI = i0 - DoorwayExclusionCells;
            float maxI = i0 + rectW + DoorwayExclusionCells;
            float minJ = j0 - DoorwayExclusionCells;
            float maxJ = j0 + rectH + DoorwayExclusionCells;
            for (int d = 0; d < doorways.Count; d++)
            {
                PayloadCartDoorwayCell door = doorways[d];
                if (door.I >= minI && door.I <= maxI && door.J >= minJ && door.J <= maxJ)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when any cell that drops away lies within <see cref="DropExclusionCells"/>
        /// (Chebyshev) of the rectangle. Off-grid cells never count as a drop: the tile border is
        /// a wall or the next tile, not a pit, and treating it as one would empty small rooms.
        /// </summary>
        internal static bool TouchesDrop(
            bool[] drop,
            int cellsX,
            int cellsZ,
            int i0,
            int j0,
            int rectW,
            int rectH)
        {
            if (drop == null || cellsX <= 0 || cellsZ <= 0 || drop.Length < cellsX * cellsZ)
                return false;

            int minI = Math.Max(0, i0 - DropExclusionCells);
            int maxI = Math.Min(cellsX - 1, i0 + rectW - 1 + DropExclusionCells);
            int minJ = Math.Max(0, j0 - DropExclusionCells);
            int maxJ = Math.Min(cellsZ - 1, j0 + rectH - 1 + DropExclusionCells);
            for (int j = minJ; j <= maxJ; j++)
            {
                int row = j * cellsX;
                for (int i = minI; i <= maxI; i++)
                {
                    if (drop[row + i])
                        return true;
                }
            }

            return false;
        }

        private static bool HasAnyDrop(bool[] drop, int cellCount)
        {
            for (int index = 0; index < cellCount; index++)
            {
                if (drop[index])
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Counts whole rows of clear cells (width <paramref name="widthCells"/>) continuing past
        /// the rectangle's travel-side edge, up to <see cref="MaxRunwayCells"/>.
        /// </summary>
        internal static int MeasureRunway(
            int[] sums,
            int cellsX,
            int cellsZ,
            int i0,
            int j0,
            int rectW,
            int rectH,
            int widthCells,
            float travelI,
            float travelJ)
        {
            int runway = 0;
            for (int step = 1; step <= MaxRunwayCells; step++)
            {
                int i;
                int j;
                int w;
                int h;
                if (travelJ > 0.5f)
                {
                    i = i0; j = j0 + rectH - 1 + step; w = widthCells; h = 1;
                }
                else if (travelJ < -0.5f)
                {
                    i = i0; j = j0 - step; w = widthCells; h = 1;
                }
                else if (travelI > 0.5f)
                {
                    i = i0 + rectW - 1 + step; j = j0; w = 1; h = widthCells;
                }
                else
                {
                    i = i0 - step; j = j0; w = 1; h = widthCells;
                }

                if (i < 0 || j < 0 || i + w > cellsX || j + h > cellsZ)
                    break;
                if (CountClear(sums, cellsX, i, j, w, h) != w * h)
                    break;
                runway++;
            }

            return runway;
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
            int length = (cellsX + 1) * (cellsZ + 1);
            int[] sums = _clearSums.Length >= length ? _clearSums : (_clearSums = new int[length]);
            Array.Clear(sums, 0, length);
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

        private static int CountClear(int[] sums, int cellsX, int i0, int j0, int w, int h)
        {
            int stride = cellsX + 1;
            int i1 = i0 + w;
            int j1 = j0 + h;
            return sums[j1 * stride + i1] - sums[j0 * stride + i1] - sums[j1 * stride + i0] + sums[j0 * stride + i0];
        }
    }
}
