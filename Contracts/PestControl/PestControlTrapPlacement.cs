using System.Collections.Generic;
using DunGen;
using UnityEngine;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.Facility.Interior.Placement.Authored;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// Why a single clearance cell was not usable. Ordered cheapest-test-first so the
    /// tally reads as "where the grid died"; the debug visualization keys its colours
    /// off this value.
    /// </summary>
    internal enum PestControlCellRejection
    {
        Clear = 0,
        OutsideTile,
        NoFloor,
        FloorPlane,
        Ceiling,
        Clutter,
        NavMesh,

        /// <summary>Overlaps a live <see cref="FixtureReservationRegistry"/> claim (mainframe, breaker, …).</summary>
        Reserved
    }

    internal struct PestControlCellSample
    {
        internal Vector3 Center;
        internal PestControlCellRejection Rejection;
        internal bool InChosenSquare;

        /// <summary>Measured floor-to-ceiling height at this cell; drives the adaptive cage size.</summary>
        internal float CeilingHeight;
    }

    internal struct PestControlRejectionTally
    {
        internal int OutsideTile;
        internal int NoFloor;
        internal int FloorPlane;
        internal int Ceiling;
        internal int Clutter;
        internal int NavMesh;
        internal int Reserved;

        internal void Add(PestControlCellRejection rejection)
        {
            switch (rejection)
            {
                case PestControlCellRejection.OutsideTile: OutsideTile++; break;
                case PestControlCellRejection.NoFloor: NoFloor++; break;
                case PestControlCellRejection.FloorPlane: FloorPlane++; break;
                case PestControlCellRejection.Ceiling: Ceiling++; break;
                case PestControlCellRejection.Clutter: Clutter++; break;
                case PestControlCellRejection.NavMesh: NavMesh++; break;
                case PestControlCellRejection.Reserved: Reserved++; break;
            }
        }

        internal void Accumulate(PestControlRejectionTally other)
        {
            OutsideTile += other.OutsideTile;
            NoFloor += other.NoFloor;
            FloorPlane += other.FloorPlane;
            Ceiling += other.Ceiling;
            Clutter += other.Clutter;
            NavMesh += other.NavMesh;
            Reserved += other.Reserved;
        }

        public override string ToString()
        {
            return $"outside={OutsideTile} no-floor={NoFloor} floor-plane={FloorPlane} " +
                   $"ceiling={Ceiling} clutter={Clutter} navmesh={NavMesh} reserved={Reserved}";
        }
    }

    /// <summary>
    /// Scored shape of one candidate tile. The probe scans every tile in a ring and then picks
    /// the winner, because first-fit kept plugging the nearest hallway-like transition tile
    /// wall-to-wall instead of finding an open room.
    /// </summary>
    internal struct PestControlTileScore
    {
        internal float SquareSideMeters;

        /// <summary>Fraction of the one-cell ring around the chosen square that is clear (0-1). Open space around the trap.</summary>
        internal float BorderClearFraction;

        /// <summary>True when the square is walled in on an opposite pair of sides — the corridor-plug case.</summary>
        internal bool PlugsOppositeWalls;

        internal float MedianCeilingMeters;

        /// <summary>Clear cells over in-tile cells (0-1). Whole-tile openness.</summary>
        internal float ClearFraction;

        /// <summary>Tile bounds long-axis / short-axis. Above <c>HallwayAspectThreshold</c> this is a corridor.</summary>
        internal float AspectRatio;

        internal float EntranceDistance;
        internal float Total;

        internal string Describe(string tileName)
        {
            return $"{tileName} score={Total:0.0} " +
                   $"[side={SquareSideMeters:0.0}m border={BorderClearFraction:0.00} " +
                   $"plug={(PlugsOppositeWalls ? "Y" : "n")} ceil={MedianCeilingMeters:0.0}m " +
                   $"open={ClearFraction:0.00} aspect={AspectRatio:0.00} entrance={EntranceDistance:0.0}m]";
        }
    }

    /// <summary>
    /// Result of the Pest Control open-area probe: a floor-level square of clear,
    /// navigable space near the facility entrance, plus enough diagnostics to explain
    /// the decision without re-running the probe.
    /// </summary>
    internal sealed class PestControlTrapPlacement
    {
        /// <summary>Authored record source or procedural probe source.</summary>
        internal string PlacementSource = "procedural-open-area";

        internal AuthoredInteriorPlacementRecord AuthoredRecord;

        /// <summary>World position on the floor at the centre of the chosen square.</summary>
        internal Vector3 Center;

        /// <summary>Yaw aligned to the chosen tile's local axes, so the trap sits square to the room.</summary>
        internal float Yaw;

        /// <summary>Side length of the square footprint in metres.</summary>
        internal float FootprintMeters;

        internal Tile Tile;
        internal string TileName = "<none>";

        /// <summary>Path distance from the nearest interior entrance to the chosen tile.</summary>
        internal float EntrancePathDistance;

        /// <summary>The entrance-distance ring that produced this result.</summary>
        internal float RingMeters;

        /// <summary>Minimum measured floor-to-ceiling height over the chosen square. Drives the adaptive cage size.</summary>
        internal float CeilingClearanceMeters;

        /// <summary>Median measured ceiling height over the chosen square; the scoring term, less noisy than the minimum.</summary>
        internal float MedianCeilingMeters;

        /// <summary>Selection score of the winning tile (see <see cref="PestControlTileScore"/>).</summary>
        internal float Score;

        /// <summary>One line per top-scoring candidate, best first — why this tile won.</summary>
        internal List<string> ScoreBoard = new List<string>();

        internal bool HasResult;

        /// <summary>True when <see cref="FootprintMeters"/> reached the minimum footprint.</summary>
        internal bool MeetsMinimumFootprint;

        /// <summary>True when no tile in any ring met the minimum and this is the best sub-minimum square.</summary>
        internal bool IsBestEffort;

        internal int TilesConsidered;
        internal int TilesScanned;
        internal int CellsSampled;
        internal int CellsClear;
        internal PestControlRejectionTally Rejections;

        /// <summary>
        /// Per-cell samples for the chosen tile only (the debug visualization draws these).
        /// Other scanned tiles are tallied but not retained.
        /// </summary>
        internal List<PestControlCellSample> Cells = new List<PestControlCellSample>();

        /// <summary>Validated lever pose outside the cage footprint.</summary>
        internal Vector3 LeverPosition;
        internal Quaternion LeverRotation = Quaternion.identity;
        internal bool LeverValidated;

        /// <summary>World-space ceiling mount points for the four independent spot fixtures.</summary>
        internal List<Vector3> CeilingLampPositions = new List<Vector3>(4);

        /// <summary>Live compound claim for cage, lever approach, and ceiling lamps.</summary>
        internal FixtureReservationGroup ReservationGroup;

        internal bool IsReserved;

        /// <summary>Safe for production consideration; diagnostic best-effort results never satisfy this.</summary>
        internal bool IsProductionValid =>
            HasResult
            && MeetsMinimumFootprint
            && FootprintMeters >= PestControlTrapPlacer.MinFootprintMeters
            && CeilingClearanceMeters >= PestControlTrapPlacer.MinCeilingMeters
            && LeverValidated
            && CeilingLampPositions != null;

        /// <summary>True only after a production-valid candidate holds its complete compound claim.</summary>
        internal bool IsSpawnReady => IsProductionValid && IsReserved && ReservationGroup.IsValid;

        /// <summary>Human-readable reservation outcome, surfaced by the debug API.</summary>
        internal string ReservationStatus = "not-reserved";

        /// <summary>Exact accessory or live-reservation rejection from the most recent finalization.</summary>
        internal string AccessoryValidationFailure = string.Empty;

        /// <summary>Facility barrier snapshot captured when production resolution began.</summary>
        internal string ReadinessStatus = "not-evaluated";

        internal bool UsedDegradedReadiness;

        internal List<string> GetProductionRejectionReasons(bool requireReservation = false)
        {
            var reasons = new List<string>();
            if (!HasResult)
            {
                reasons.Add("no-placement-result");
            }
            else
            {
                if (!MeetsMinimumFootprint || FootprintMeters < PestControlTrapPlacer.MinFootprintMeters)
                    reasons.Add($"footprint-below-{PestControlTrapPlacer.MinFootprintMeters:0.00}m");
                if (CeilingClearanceMeters < PestControlTrapPlacer.MinCeilingMeters)
                    reasons.Add($"ceiling-below-{PestControlTrapPlacer.MinCeilingMeters:0.00}m");
                if (!LeverValidated)
                    reasons.Add("lever-floor-unavailable");

                // Lamp shortfall is a downgrade, not a rejection — see
                // PestControlTrapPlacer.TryFinalizePlacementAccessories. The mounted count is
                // reported by ToDiagnosticString and the production selection log.
            }

            if (!string.IsNullOrWhiteSpace(AccessoryValidationFailure)
                && !reasons.Contains(AccessoryValidationFailure))
            {
                reasons.Add(AccessoryValidationFailure);
            }

            if (requireReservation && (!IsReserved || !ReservationGroup.IsValid))
                reasons.Add("compound-reservation-not-held " + ReservationStatus);

            return reasons;
        }

        internal string BuildProductionValiditySummary(bool requireReservation = false)
        {
            List<string> reasons = GetProductionRejectionReasons(requireReservation);
            return reasons.Count == 0 ? "VALID" : "REJECTED [" + string.Join("; ", reasons) + "]";
        }

        internal string BuildSummary()
        {
            if (!HasResult)
            {
                return "Pest Control probe: NO RESULT " +
                       $"(tiles considered={TilesConsidered}, scanned={TilesScanned}, cells={CellsSampled}) " +
                       $"rejects: {Rejections}\n  production = {BuildProductionValiditySummary()}";
            }

            string quality = MeetsMinimumFootprint ? "meets-minimum" : "BEST-EFFORT (below minimum footprint)";
            PestControlTrapAssembler.DeriveCageMetrics(
                CeilingClearanceMeters,
                out float cageHeight,
                out float restBottom,
                out float postHeight);

            var builder = new System.Text.StringBuilder();
            builder.Append("Pest Control probe: ").Append(quality).Append('\n')
                   .Append($"  production = {BuildProductionValiditySummary()}\n")
                   .Append($"  readiness  = {ReadinessStatus} degraded={UsedDegradedReadiness}\n")
                   .Append($"  source    = {PlacementSource}\n")
                   .Append($"  tile      = {TileName} (entrance path {EntrancePathDistance:0.0}m, ring {RingMeters:0}m, score {Score:0.0})\n")
                   .Append($"  center    = ({Center.x:0.00}, {Center.y:0.00}, {Center.z:0.00})\n")
                   .Append($"  footprint = {FootprintMeters:0.00}m square, yaw {Yaw:0.0}deg\n")
                   .Append($"  ceiling   = min {CeilingClearanceMeters:0.00}m, median {MedianCeilingMeters:0.00}m\n")
                   .Append($"  cage      = {cageHeight:0.00}m tall, hangs at {restBottom:0.00}m, posts {postHeight:0.00}m\n")
                   .Append($"  tiles     = considered {TilesConsidered}, scanned {TilesScanned}\n")
                   .Append($"  cells     = sampled {CellsSampled}, clear {CellsClear}\n")
                   .Append($"  rejects   = {Rejections}\n")
                   .Append($"  lever     = {(LeverValidated ? LeverPosition.ToString("F2") : "INVALID")}\n")
                   .Append($"  lamps     = {CeilingLampPositions?.Count ?? 0}/{PestControlTrapAssembler.CeilingLampCount}\n")
                   .Append($"  reserved  = {ReservationStatus}");

            if (ScoreBoard != null)
            {
                for (int i = 0; i < ScoreBoard.Count; i++)
                    builder.Append("\n  cand ").Append(i + 1).Append("   = ").Append(ScoreBoard[i]);
            }

            return builder.ToString();
        }
    }
}
