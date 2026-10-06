using System.Collections.Generic;

namespace Y4NGZCompany.Contracts.Defuse
{
    /// <summary>
    /// One probed elevated-surface candidate inside a tile's clearance grid. The Unity-side
    /// placer fills in the measured geometry (center hit height above the tile's dominant
    /// floor plane, surface normal, and the four corner-ray heights across the bomb
    /// footprint); the pure math below decides whether the surface is stable and which
    /// acceptable surface the tile should use.
    /// </summary>
    internal struct DefuseElevatedSurfaceSample
    {
        internal int CellI;
        internal int CellJ;

        /// <summary>Center down-cast hit height above the tile's dominant floor plane, in meters.</summary>
        internal float HeightAboveFloor;

        /// <summary>Dot of the center hit normal with world up; 1 is a perfectly level top.</summary>
        internal float NormalDotUp;

        /// <summary>
        /// Down-cast hit heights above the floor plane at the four bomb-footprint corners.
        /// Null or shorter than four means at least one corner ray missed the surface.
        /// </summary>
        internal float[] CornerHeights;
    }

    /// <summary>
    /// Pure stable-surface checks and candidate selection for the elevated Defuse bomb tier —
    /// no UnityEngine, no physics — so the tier's geometry rules are testable headless, the
    /// same way <c>BreachContainerFitMath</c> keeps the Blacksite rectangle search pure. The
    /// placer probes prop tops out of a tile's clearance grid, describes each probe as a
    /// <see cref="DefuseElevatedSurfaceSample"/>, and asks this class which sample (if any)
    /// is a stable, level, correctly-banded resting surface.
    /// </summary>
    internal static class DefuseElevatedFitMath
    {
        /// <summary>A surface steeper than this (dot of its normal with up) cannot hold the bomb.</summary>
        internal const float SurfaceNormalMinDotUp = 0.7f;

        /// <summary>Corner-ray heights across the bomb footprint must agree within this, or the bomb would perch on an edge.</summary>
        internal const float CornerAgreementToleranceMeters = 0.08f;

        /// <summary>Inclusive height band test against the tile's dominant floor plane.</summary>
        internal static bool IsWithinHeightBand(float heightAboveFloor, float minMeters, float maxMeters)
        {
            return heightAboveFloor >= minMeters && heightAboveFloor <= maxMeters;
        }

        /// <summary>The center hit must be near-level; a sloped crate lid sheds the bomb.</summary>
        internal static bool IsLevelEnough(float normalDotUp)
        {
            return normalDotUp > SurfaceNormalMinDotUp;
        }

        /// <summary>
        /// All four corner rays across the bomb footprint must have hit, and their heights
        /// must agree with the center sample within <paramref name="toleranceMeters"/> —
        /// otherwise the footprint overhangs the prop (edge-perch), spans a step, or the
        /// bomb balances on a thin post whose corner rays all land on the surrounding floor.
        /// </summary>
        internal static bool CornersAgree(float centerHeightMeters, float[] cornerHeights, float toleranceMeters)
        {
            if (cornerHeights == null || cornerHeights.Length < 4)
                return false;

            float min = centerHeightMeters;
            float max = centerHeightMeters;
            for (int i = 0; i < 4; i++)
            {
                if (cornerHeights[i] < min)
                    min = cornerHeights[i];
                if (cornerHeights[i] > max)
                    max = cornerHeights[i];
            }

            return max - min <= toleranceMeters;
        }

        /// <summary>Full geometric acceptance: height band, level normal, and corner agreement.</summary>
        internal static bool IsSurfaceAcceptable(in DefuseElevatedSurfaceSample sample, float minMeters, float maxMeters)
        {
            return IsWithinHeightBand(sample.HeightAboveFloor, minMeters, maxMeters)
                   && IsLevelEnough(sample.NormalDotUp)
                   && CornersAgree(sample.HeightAboveFloor, sample.CornerHeights, CornerAgreementToleranceMeters);
        }

        /// <summary>
        /// Index of the best acceptable sample, or -1 when none passes. Mirrors the container
        /// search's dominant term: the surface closest to the tile center wins, so the bomb
        /// reads as placed in the room rather than jammed against a wall. Ties keep the
        /// earlier sample, so scan order stays deterministic.
        /// </summary>
        internal static int SelectBest(
            IReadOnlyList<DefuseElevatedSurfaceSample> samples,
            float minMeters,
            float maxMeters,
            float tileCenterI,
            float tileCenterJ,
            float cellSizeMeters)
        {
            if (samples == null)
                return -1;

            int bestIndex = -1;
            float bestScore = float.NegativeInfinity;
            for (int index = 0; index < samples.Count; index++)
            {
                DefuseElevatedSurfaceSample sample = samples[index];
                if (!IsSurfaceAcceptable(in sample, minMeters, maxMeters))
                    continue;

                float di = (sample.CellI + 0.5f - tileCenterI) * cellSizeMeters;
                float dj = (sample.CellJ + 0.5f - tileCenterJ) * cellSizeMeters;
                float score = -(float)System.Math.Sqrt(di * di + dj * dj);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = index;
                }
            }

            return bestIndex;
        }

        /// <summary>
        /// Which side the bomb's keypad should face: 0=+I, 1=-I, 2=+J, 3=-J in grid axes, or
        /// -1 for "face the tile center" when no open side exists or several are equally
        /// approachable. Among open (clear-floor / NavMesh-backed) sides the one best aligned
        /// with the direction to the tile center wins, so the keypad opens into the room.
        /// </summary>
        internal static int SelectFacingDirection(
            bool openPosI,
            bool openNegI,
            bool openPosJ,
            bool openNegJ,
            float toCenterI,
            float toCenterJ)
        {
            bool[] open = { openPosI, openNegI, openPosJ, openNegJ };
            float[] alignment =
            {
                toCenterI,
                -toCenterI,
                toCenterJ,
                -toCenterJ,
            };

            int bestDirection = -1;
            float bestAlignment = float.NegativeInfinity;
            bool tied = false;
            for (int direction = 0; direction < 4; direction++)
            {
                if (!open[direction])
                    continue;

                if (alignment[direction] > bestAlignment + 1e-4f)
                {
                    bestAlignment = alignment[direction];
                    bestDirection = direction;
                    tied = false;
                }
                else if (alignment[direction] > bestAlignment - 1e-4f && bestDirection >= 0)
                {
                    tied = true;
                }
            }

            return tied ? -1 : bestDirection;
        }
    }
}
