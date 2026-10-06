using System;
using System.Collections;
using System.Collections.Generic;
using DunGen;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Facility.Interior.Placement;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// Host-side probe that finds an OPEN patch of floor near the facility main entrance
    /// big enough to hold the Pest Control suspended corral trap.
    ///
    /// It runs strictly after dungeon generation and the NavMesh bake (see
    /// docs/interior-placement/02-round-lifecycle-order-matters.md step 5) because the NavMesh gate is what
    /// guarantees monsters can walk onto the platform. It never hard-fails: if no tile in
    /// any entrance ring can host the minimum footprint, the best sub-minimum square is
    /// returned flagged best-effort, so a later phase can decide whether to shrink the trap
    /// or fall back.
    ///
    /// Phase 1 constants live here; Phase 5 moves them to config.
    /// </summary>
    internal static partial class PestControlTrapPlacer
    {
        // --- placement constants (Phase 5: config) -------------------------------
        internal const float MinFootprintMeters = 3.0f;
        internal const float MaxFootprintMeters = 6.0f;
        /// <summary>Aliases the shared grid's cell size; the probe's math must never drift from it.</summary>
        internal const float CellSizeMeters = TileClearanceGrid.CellSizeMeters;

        /// <summary>
        /// Derived, not chosen: the assembly decides what it needs (margin + winch rig + the
        /// shortest usable cage + the walk-under gap) and the probe rejects anything shorter.
        /// The old hand-picked 3.5 m let the probe win tiles the trap could not physically be
        /// built in. Raising it to ~4.33 m shrinks the eligible tile set, which is the honest
        /// trade -- a tile that cannot fit the rig was never a real candidate.
        /// </summary>
        internal const float MinCeilingMeters = PestControlTrapAssembler.RequiredCeilingMeters;
        internal const float MinTileAreaSquareMeters = 30f;

        /// <summary>Registry label the trap footprint is claimed under.</summary>
        internal const string ReservationLabel = "pest-control-trap";

        internal const string ReservationSource = "pest-control-probe";

        /// <summary>Reserved volume height — the corral's suspended rest position lives inside it.</summary>
        internal const float TrapVolumeHeightMeters = 3.5f;

        /// <summary>Burn-and-retry attempts when the winning square collides with an existing reservation.</summary>
        private const int MaxReservationRetries = 6;

        /// <summary>Entrance path-distance rings, widened in order until a tile qualifies.</summary>
        private static readonly float[] EntranceRingsMeters = { 40f, 65f, 95f, float.MaxValue };

        /// <summary>A cell whose floor sits further than this from the tile's dominant floor plane is not the same surface.</summary>
        private const float FloorPlaneToleranceMeters = 0.25f;

        /// <summary>Clutter box reaches from the floor to roughly head height — anything in there blocks the trap.</summary>
        private const float ClutterProbeHeightMeters = 2.0f;

        /// <summary>How far up the ceiling ray reaches. Anything beyond is treated as this height.</summary>
        private const float CeilingProbeMeters = 9.0f;

        // --- tile scoring weights -------------------------------------------------
        // First-fit picked the nearest tile that merely cleared MIN_FOOTPRINT, which in practice
        // was a hallway-like transition tile the trap then plugged wall-to-wall. Every tile in a
        // ring is now scanned and scored, and only a ring with no qualifying tile widens.
        private const float ScoreSquareSideWeight = 10.0f;
        private const float ScoreBorderClearWeight = 12.0f;
        private const float ScorePlugPenalty = 9.0f;
        private const float ScoreCeilingWeight = 2.2f;
        private const float ScoreOpennessWeight = 14.0f;
        private const float ScoreAspectPenaltyWeight = 16.0f;
        private const float ScoreEntranceWeight = 0.06f;

        /// <summary>
        /// Engine A's fixture-separation rule expressed in this probe's own vocabulary: a
        /// qualifying square whose center sits inside the shared separation radius of an
        /// existing registry claim (mainframe, stash, incinerator) is demoted per meter of
        /// shortfall, never vetoed — the probe stays entrance-biased and best-effort. At the
        /// 25 m radius a square on top of a fixture loses ~37, less than the max square-side
        /// term but enough to lose to any comparable uncrowded square.
        /// </summary>
        private const float ScoreFixtureCrowdingWeight = 1.5f;

        /// <summary>Shared radius so the probe and Engine A agree on what "spread apart" means.</summary>
        private const float FixtureSeparationMeters = InteriorPlacementRequest.DefaultMinSeparationFromOtherFixturesMeters;

        /// <summary>Tile bounds ratio above which the tile reads as a corridor rather than a room.</summary>
        private const float HallwayAspectThreshold = 2.2f;

        /// <summary>A ring row this blocked counts as a wall for the opposite-pair plug test.</summary>
        private const float PlugBlockedFraction = 0.8f;

        /// <summary>Candidate score lines carried into the log and the debug summary.</summary>
        private const int ScoreBoardDepth = 3;

        private const float NavSampleRadiusMeters = 0.5f;

        /// <summary>Caps total tiles scanned across all rings.</summary>
        private const int MaxTilesScanned = 24;

        private const double FrameBudgetMilliseconds = 3.0;

        private static readonly Collider[] OverlapBuffer = new Collider[16];

        /// <summary>
        /// Cell classification thresholds handed to the shared <see cref="TileClearanceGrid"/>.
        /// The values are this probe's own constants, so extraction changed nothing.
        /// </summary>
        private static readonly TileClearanceGridSettings GridSettings = new TileClearanceGridSettings
        {
            MinCeilingMeters = MinCeilingMeters,
            CeilingProbeMeters = CeilingProbeMeters,
            ClutterProbeHeightMeters = ClutterProbeHeightMeters,
            FloorPlaneToleranceMeters = FloorPlaneToleranceMeters,
            NavSampleRadiusMeters = NavSampleRadiusMeters,
            FrameBudgetMilliseconds = FrameBudgetMilliseconds,
        };

        internal static PestControlTrapPlacement LastResult { get; private set; }

        /// <summary>
        /// Budgeted variant for eventual contract use — time-sliced to ~3 ms/frame, matching
        /// InteriorPlacementService's spawn budget.
        /// </summary>
        internal static IEnumerator ResolveBudgeted(Action<PestControlTrapPlacement> onComplete, bool reserveOnSuccess = false)
        {
            IEnumerator probe = RunProbe(true, reserveOnSuccess, result => { LastResult = result; onComplete?.Invoke(result); });
            while (probe.MoveNext())
                yield return probe.Current;
        }

        /// <summary>Synchronous variant for the debug command; drains the same enumerator in one frame.</summary>
        internal static bool TryResolveImmediate(out PestControlTrapPlacement result, bool reserveOnSuccess = false)
        {
            PestControlTrapPlacement captured = null;
            IEnumerator probe = RunProbe(false, reserveOnSuccess, value => captured = value);
            while (probe.MoveNext())
            {
            }

            LastResult = captured;
            result = captured;
            if (result != null && result.HasResult)
                TryFinalizePlacementAccessories(result, out _);
            return result != null && result.HasResult;
        }

        /// <summary>
        /// Bottom-anchored square volume the trap occupies. Height covers the corral at its
        /// suspended rest position, which is why it matches the probe's ceiling guarantee.
        /// </summary>
        internal static FixtureFootprint CreateTrapFootprint(float sideMeters)
        {
            return CreateTrapFootprint(sideMeters, MinCeilingMeters);
        }

        internal static FixtureFootprint CreateTrapFootprint(float sideMeters, float heightMeters)
        {
            return new FixtureFootprint(
                Vector3.forward,
                Vector3.back,
                Vector3.up,
                new Vector3(sideMeters, Mathf.Max(MinCeilingMeters, heightMeters), sideMeters),
                0f,
                0f,
                0.5f,
                1.0f);
        }

        /// <summary>Claims the resolved footprint. Idempotent: re-claiming the same OBB reuses the entry.</summary>
        internal static bool TryReservePlacement(PestControlTrapPlacement placement)
        {
            if (placement == null || !placement.HasResult)
                return false;

            if (placement.IsReserved && placement.ReservationGroup.IsValid)
                return true;

            bool finalized = TryFinalizePlacementAccessories(placement, out string finalizeFailure);
            bool productionValid = finalized && placement.IsProductionValid;
            FixtureReservationGroup group = default;
            FixtureReservationConflict conflict = default;
            bool reserved = productionValid
                            && FixtureReservationRegistry.TryReserveGroup(
                                BuildReservationRequests(placement),
                                out group,
                                out conflict);
            if (!reserved)
            {
                placement.IsReserved = false;
                placement.ReservationGroup = default;

                string rejection;
                if (!productionValid)
                {
                    List<string> reasons = placement.GetProductionRejectionReasons();
                    if (!string.IsNullOrWhiteSpace(finalizeFailure) && !reasons.Contains(finalizeFailure))
                        reasons.Add(finalizeFailure);
                    rejection = reasons.Count > 0 ? string.Join("; ", reasons) : "production-validation-failed";
                }
                else
                {
                    rejection = conflict.ToDiagnosticString();
                }

                placement.ReservationStatus = "REJECTED " + rejection;
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.PestControl] Trap reservation rejected: {rejection}.");
                return false;
            }

            placement.ReservationGroup = group;
            placement.IsReserved = true;
            placement.ReservationStatus = $"held ({group.Count} volumes, {placement.FootprintMeters:0.00}m cage x " +
                                          $"{placement.CeilingClearanceMeters:0.00}m high)";
            return true;
        }

        internal static bool ReleaseReservation(PestControlTrapPlacement placement)
        {
            if (placement == null || !placement.IsReserved)
                return false;

            bool released = FixtureReservationRegistry.Release(placement.ReservationGroup);

            placement.IsReserved = false;
            placement.ReservationGroup = default;
            placement.ReservationStatus = released ? "released" : "not-reserved";
            return released;
        }

        private static IEnumerator RunProbe(bool budgeted, bool reserveOnSuccess, Action<PestControlTrapPlacement> onComplete)
        {
            var result = new PestControlTrapPlacement();
            var frame = Stopwatch.StartNew();

            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null || dungeon.AllTiles.Count == 0)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.PestControl] Placement probe ran with no generated dungeon.");
                onComplete?.Invoke(result);
                yield break;
            }

            List<Vector3> entrances = InteriorAnchorService.GetInteriorEntrancePositions();
            var candidates = new List<TileCandidate>(dungeon.AllTiles.Count);
            for (int i = 0; i < dungeon.AllTiles.Count; i++)
            {
                // Charged at the top of the body, not after a successful add: the filters below
                // cost NavMesh work of their own, so a dungeon whose tiles are mostly rejected
                // used to run the whole loop without ever reaching the yield.
                if (budgeted && frame.Elapsed.TotalMilliseconds >= FrameBudgetMilliseconds)
                {
                    frame.Restart();
                    yield return null;
                }

                Tile tile = dungeon.AllTiles[i];
                if (tile == null || InteriorPlacementService.IsTileExcludedFromPlacement(tile))
                    continue;

                // Cheapest filter first: the footprint test is arithmetic on bounds the tile
                // already carries, while the reachability verdict can cost a path query.
                Bounds bounds = tile.Bounds;
                if (Mathf.Min(bounds.size.x, bounds.size.z) < MinFootprintMeters)
                    continue;

                if (InteriorAnchorService.GetTileArea(tile) < MinTileAreaSquareMeters)
                    continue;

                if (!InteriorAnchorService.IsTileReachableFromEntrance(tile, entrances))
                    continue;

                candidates.Add(new TileCandidate(
                    tile,
                    InteriorAnchorService.EstimateTileEntrancePathDistance(tile, entrances),
                    TileRoomClassifierRuntime.IsRoom(tile, strictShape: true)));
            }

            // #686: the trap belongs in a room. Corridor, junction, stair, and catwalk tiles only
            // enter the ring scan when the interior has no room at all, so the scan cap and the
            // per-ring best score are spent on rooms whenever one exists.
            bool anyRoom = false;
            for (int i = 0; i < candidates.Count && !anyRoom; i++)
                anyRoom = candidates[i].IsRoom;
            if (anyRoom)
                candidates.RemoveAll(candidate => !candidate.IsRoom);
            else if (candidates.Count > 0)
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.PestControl] No room tile in this interior; the trap probe will consider {candidates.Count} transit tile(s).");

            candidates.Sort(CompareByEntranceDistance);
            result.TilesConsidered = candidates.Count;

            var scanned = new Dictionary<Tile, TileScan>(candidates.Count);
            var allScans = new List<TileScan>(candidates.Count);
            TileScan biggest = null;

            for (int ringIndex = 0; ringIndex < EntranceRingsMeters.Length; ringIndex++)
            {
                float ring = EntranceRingsMeters[ringIndex];
                var qualifying = new List<TileScan>(candidates.Count);

                // Scan EVERY candidate in this ring before choosing. Widening only happens when
                // the whole ring produced nothing that meets the minimum footprint.
                for (int i = 0; i < candidates.Count; i++)
                {
                    TileCandidate candidate = candidates[i];
                    if (candidate.EntranceDistance > ring)
                        continue;
                    if (scanned.ContainsKey(candidate.Tile))
                        continue;
                    if (result.TilesScanned >= MaxTilesScanned)
                        break;

                    var scan = new TileScan();
                    IEnumerator scanRoutine = ScanTile(candidate, budgeted, frame, scan);
                    while (scanRoutine.MoveNext())
                        yield return scanRoutine.Current;

                    scanned[candidate.Tile] = scan;
                    allScans.Add(scan);
                    result.TilesScanned++;
                    result.CellsSampled += scan.CellsSampled;
                    result.CellsClear += scan.CellsClear;
                    result.Rejections.Accumulate(scan.Rejections);
                    scan.RingMeters = ring;
                    ScoreTile(scan);

                    if (biggest == null || scan.SquareCells > biggest.SquareCells)
                        biggest = scan;

                    if (scan.SquareCells * CellSizeMeters >= MinFootprintMeters)
                        qualifying.Add(scan);
                }

                if (qualifying.Count > 0)
                {
                    qualifying.Sort(CompareByScoreDescending);
                    BuildScoreBoard(result, qualifying);
                    Apply(result, qualifying[0], meetsMinimum: true);
                    if (reserveOnSuccess)
                        TryReservePlacement(result);
                    LogResult(result);
                    onComplete?.Invoke(result);
                    yield break;
                }

                if (result.TilesScanned >= MaxTilesScanned)
                    break;
            }

            if (biggest != null && biggest.SquareCells > 0)
            {
                // Best-effort still goes through the score, so the reported scoreboard and the
                // applied tile can never disagree; `biggest` is only the fallback if scoring
                // somehow produced nothing usable.
                allScans.Sort(CompareByScoreDescending);
                BuildScoreBoard(result, allScans);

                TileScan chosen = biggest;
                for (int i = 0; i < allScans.Count; i++)
                {
                    if (allScans[i].SquareCells <= 0)
                        continue;

                    chosen = allScans[i];
                    break;
                }

                Apply(result, chosen, meetsMinimum: false);
                if (reserveOnSuccess)
                    TryReservePlacement(result);
            }

            LogResult(result);
            onComplete?.Invoke(result);
        }

        private static int CompareByEntranceDistance(TileCandidate a, TileCandidate b)
        {
            int byDistance = a.EntranceDistance.CompareTo(b.EntranceDistance);
            if (byDistance != 0)
                return byDistance;

            return string.CompareOrdinal(a.Tile != null ? a.Tile.name : string.Empty, b.Tile != null ? b.Tile.name : string.Empty);
        }

        private static int CompareByScoreDescending(TileScan a, TileScan b)
        {
            int byScore = b.Score.Total.CompareTo(a.Score.Total);
            if (byScore != 0)
                return byScore;

            return string.CompareOrdinal(a.Tile != null ? a.Tile.name : string.Empty, b.Tile != null ? b.Tile.name : string.Empty);
        }

        private static void BuildScoreBoard(PestControlTrapPlacement result, List<TileScan> ranked)
        {
            result.ScoreBoard.Clear();
            int depth = Mathf.Min(ScoreBoardDepth, ranked.Count);
            for (int i = 0; i < depth; i++)
            {
                TileScan scan = ranked[i];
                if (scan.SquareCells <= 0)
                    continue;

                result.ScoreBoard.Add(scan.Score.Describe(scan.Tile != null ? scan.Tile.name : "<null>"));
            }
        }

        /// <summary>
        /// Turns a completed scan into a comparable score. Square size dominates, but openness
        /// around and within the tile is what stops the trap from being planned into a corridor.
        /// </summary>
        private static void ScoreTile(TileScan scan)
        {
            var score = new PestControlTileScore
            {
                SquareSideMeters = Mathf.Min(scan.SquareCells * CellSizeMeters, MaxFootprintMeters),
                BorderClearFraction = scan.BorderClearFraction,
                PlugsOppositeWalls = scan.PlugsOppositeWalls,
                MedianCeilingMeters = scan.MedianCeiling,
                ClearFraction = scan.ClearFraction,
                AspectRatio = scan.AspectRatio,
                EntranceDistance = scan.EntranceDistance
            };

            float total = ScoreSquareSideWeight * score.SquareSideMeters
                          + ScoreBorderClearWeight * score.BorderClearFraction
                          + ScoreCeilingWeight * score.MedianCeilingMeters
                          + ScoreOpennessWeight * score.ClearFraction
                          - ScoreEntranceWeight * score.EntranceDistance;

            if (score.PlugsOppositeWalls)
                total -= ScorePlugPenalty;

            if (score.AspectRatio > HallwayAspectThreshold)
                total -= ScoreAspectPenaltyWeight * (score.AspectRatio - HallwayAspectThreshold);

            if (scan.SquareCells > 0)
            {
                float nearestFixture = FixtureReservationRegistry.GetNearestReservationCenterDistance(scan.SquareCenter);
                if (nearestFixture >= 0f && nearestFixture < FixtureSeparationMeters)
                    total -= ScoreFixtureCrowdingWeight * (FixtureSeparationMeters - nearestFixture);
            }

            score.Total = total;
            scan.Score = score;
        }

        private static void Apply(PestControlTrapPlacement result, TileScan scan, bool meetsMinimum)
        {
            result.HasResult = true;
            result.Tile = scan.Tile;
            result.TileName = scan.Tile != null ? scan.Tile.name : "<null>";
            result.Center = scan.SquareCenter;
            result.Yaw = scan.Yaw;
            result.FootprintMeters = Mathf.Min(scan.SquareCells * CellSizeMeters, MaxFootprintMeters);
            result.EntrancePathDistance = scan.EntranceDistance;
            result.RingMeters = scan.RingMeters;
            result.MeetsMinimumFootprint = meetsMinimum;
            result.IsBestEffort = !meetsMinimum;
            result.Cells = scan.Cells;
            result.CeilingClearanceMeters = scan.MinCeiling;
            result.MedianCeilingMeters = scan.MedianCeiling;
            result.Score = scan.Score.Total;
        }

        /// <summary>
        /// Lays a CELL_SIZE grid over the tile in the tile's own local axes (so the grid and
        /// the final yaw are square to the room) via the shared <see cref="TileClearanceGrid"/>,
        /// then takes the largest inscribed square of clear cells. The cell classification
        /// (floor-plane vote, ceiling, clutter, NavMesh) lives in the grid; this probe keeps
        /// its scoring, inscribed-square DP, and reservation retry.
        /// </summary>
        private static IEnumerator ScanTile(TileCandidate candidate, bool budgeted, Stopwatch frame, TileScan scan)
        {
            Tile tile = candidate.Tile;
            Bounds bounds = tile.Bounds;

            scan.Tile = tile;
            scan.EntranceDistance = candidate.EntranceDistance;

            Vector3 size = bounds.size;
            float spanX = Mathf.Max(0.01f, Mathf.Min(size.x, size.z));
            float spanZ = Mathf.Max(0.01f, Mathf.Max(size.x, size.z));
            scan.AspectRatio = spanZ / spanX;

            var grid = new TileClearanceGrid();
            IEnumerator build = TileClearanceGrid.Build(tile, GridSettings, budgeted, frame, grid);
            while (build.MoveNext())
                yield return build.Current;

            scan.Yaw = grid.Yaw;
            if (grid.CellCount <= 0 || !grid.HasFloorPlane)
                yield break;

            Quaternion tileRotation = grid.TileRotation;
            Vector3 right = grid.Right;
            Vector3 forward = grid.Forward;
            Vector3 pivot = grid.Pivot;
            int cellsX = grid.CellsX;
            int cellsZ = grid.CellsZ;
            int cellCount = grid.CellCount;
            Vector3[] floorPoints = grid.FloorPoints;
            float[] ceilings = grid.Ceilings;
            float dominantFloorY = grid.DominantFloorY;
            scan.DominantFloorY = dominantFloorY;

            var rejections = new PestControlCellRejection[cellCount];
            for (int index = 0; index < cellCount; index++)
                rejections[index] = ToRejection(grid.States[index]);

            scan.CellsSampled = cellCount;

            // Largest inscribed square of clear cells (classic DP over the boolean grid), then a
            // registry check on the WINNING square only. Querying FixtureReservationRegistry per
            // cell would cost thousands of OBB/SAT tests per tile; burning the overlapped cells and
            // re-running the DP costs at most MaxReservationRetries queries plus one square's worth.
            var dp = new int[cellCount];
            int bestSide = 0;
            int bestEndI = 0;
            int bestEndJ = 0;
            bool reservationClear = false;
            for (int attempt = 0; attempt <= MaxReservationRetries; attempt++)
            {
                FindLargestSquare(rejections, dp, cellsX, cellsZ, out bestSide, out bestEndI, out bestEndJ);
                if (bestSide == 0)
                {
                    reservationClear = true;
                    break;
                }

                int attemptOriginI = bestEndI - bestSide + 1;
                int attemptOriginJ = bestEndJ - bestSide + 1;
                Vector3 attemptCenter = pivot
                                        + right * ((attemptOriginI + bestSide * 0.5f) * CellSizeMeters)
                                        + forward * ((attemptOriginJ + bestSide * 0.5f) * CellSizeMeters);
                attemptCenter = new Vector3(attemptCenter.x, dominantFloorY, attemptCenter.z);

                if (!BurnReservedCells(
                        rejections,
                        floorPoints,
                        cellsX,
                        tileRotation,
                        dominantFloorY,
                        bestSide,
                        attemptOriginI,
                        attemptOriginJ,
                        attemptCenter))
                {
                    reservationClear = true;
                    break;
                }
            }

            // Retry budget exhausted with a still-conflicting square: drop the tile rather than
            // hand back a footprint that overlaps someone else's claim.
            if (!reservationClear)
                bestSide = 0;

            scan.Cells = new List<PestControlCellSample>(cellCount);
            int inTileCells = 0;
            for (int index = 0; index < cellCount; index++)
            {
                if (rejections[index] != PestControlCellRejection.OutsideTile)
                    inTileCells++;

                if (rejections[index] == PestControlCellRejection.Clear)
                    scan.CellsClear++;
                else
                    scan.Rejections.Add(rejections[index]);
            }

            scan.ClearFraction = inTileCells > 0 ? scan.CellsClear / (float)inTileCells : 0f;
            scan.SquareCells = bestSide;
            if (bestSide > 0)
            {
                int originI = bestEndI - bestSide + 1;
                int originJ = bestEndJ - bestSide + 1;
                Vector3 center = pivot
                                 + right * ((originI + bestSide * 0.5f) * CellSizeMeters)
                                 + forward * ((originJ + bestSide * 0.5f) * CellSizeMeters);
                scan.SquareCenter = new Vector3(center.x, dominantFloorY, center.z);

                MeasureSquareCeiling(ceilings, rejections, cellsX, bestSide, originI, originJ, scan);
                MeasureSquareBorder(rejections, cellsX, cellsZ, bestSide, originI, originJ, scan);

                for (int j = originJ; j <= bestEndJ; j++)
                {
                    for (int i = originI; i <= bestEndI; i++)
                        dp[j * cellsX + i] = -1;
                }
            }

            for (int index = 0; index < cellCount; index++)
            {
                scan.Cells.Add(new PestControlCellSample
                {
                    Center = floorPoints[index],
                    Rejection = rejections[index],
                    InChosenSquare = dp[index] == -1,
                    CeilingHeight = ceilings[index]
                });
            }
        }

        private static void MeasureSquareCeiling(
            float[] ceilings,
            PestControlCellRejection[] rejections,
            int cellsX,
            int side,
            int originI,
            int originJ,
            TileScan scan)
        {
            var samples = new List<float>(side * side);
            float min = float.MaxValue;
            for (int j = originJ; j < originJ + side; j++)
            {
                for (int i = originI; i < originI + side; i++)
                {
                    int index = j * cellsX + i;
                    if (rejections[index] != PestControlCellRejection.Clear)
                        continue;

                    float value = ceilings[index];
                    samples.Add(value);
                    if (value < min)
                        min = value;
                }
            }

            if (samples.Count == 0)
            {
                scan.MinCeiling = MinCeilingMeters;
                scan.MedianCeiling = MinCeilingMeters;
                return;
            }

            samples.Sort();
            scan.MinCeiling = min;
            scan.MedianCeiling = samples[samples.Count / 2];
        }

        /// <summary>
        /// Measures the one-cell ring around the chosen square. A fully clear ring means the trap
        /// stands in open space; a ring blocked on an opposite pair of sides means the square
        /// spans the room wall-to-wall, which is the corridor-plug shape we now penalize.
        /// </summary>
        private static void MeasureSquareBorder(
            PestControlCellRejection[] rejections,
            int cellsX,
            int cellsZ,
            int side,
            int originI,
            int originJ,
            TileScan scan)
        {
            int total = 0;
            int clear = 0;
            var sideClear = new int[4];
            var sideTotal = new int[4];

            for (int step = 0; step < side; step++)
            {
                AccumulateBorderCell(rejections, cellsX, cellsZ, originI + step, originJ - 1, 0, sideClear, sideTotal, ref total, ref clear);
                AccumulateBorderCell(rejections, cellsX, cellsZ, originI + step, originJ + side, 1, sideClear, sideTotal, ref total, ref clear);
                AccumulateBorderCell(rejections, cellsX, cellsZ, originI - 1, originJ + step, 2, sideClear, sideTotal, ref total, ref clear);
                AccumulateBorderCell(rejections, cellsX, cellsZ, originI + side, originJ + step, 3, sideClear, sideTotal, ref total, ref clear);
            }

            scan.BorderClearFraction = total > 0 ? clear / (float)total : 0f;
            scan.PlugsOppositeWalls =
                IsSideBlocked(sideClear, sideTotal, 0) && IsSideBlocked(sideClear, sideTotal, 1)
                || IsSideBlocked(sideClear, sideTotal, 2) && IsSideBlocked(sideClear, sideTotal, 3);
        }

        private static void AccumulateBorderCell(
            PestControlCellRejection[] rejections,
            int cellsX,
            int cellsZ,
            int i,
            int j,
            int sideIndex,
            int[] sideClear,
            int[] sideTotal,
            ref int total,
            ref int clear)
        {
            total++;
            sideTotal[sideIndex]++;

            // Off-grid counts as blocked: the square already reaches the edge of the tile there.
            if (i < 0 || j < 0 || i >= cellsX || j >= cellsZ)
                return;

            if (rejections[j * cellsX + i] != PestControlCellRejection.Clear)
                return;

            clear++;
            sideClear[sideIndex]++;
        }

        private static bool IsSideBlocked(int[] sideClear, int[] sideTotal, int index)
        {
            if (sideTotal[index] <= 0)
                return true;

            float blocked = 1f - sideClear[index] / (float)sideTotal[index];
            return blocked >= PlugBlockedFraction;
        }

        private static void FindLargestSquare(
            PestControlCellRejection[] rejections,
            int[] dp,
            int cellsX,
            int cellsZ,
            out int bestSide,
            out int bestEndI,
            out int bestEndJ)
        {
            bestSide = 0;
            bestEndI = 0;
            bestEndJ = 0;

            for (int j = 0; j < cellsZ; j++)
            {
                for (int i = 0; i < cellsX; i++)
                {
                    int index = j * cellsX + i;
                    if (rejections[index] != PestControlCellRejection.Clear)
                    {
                        dp[index] = 0;
                        continue;
                    }

                    if (i == 0 || j == 0)
                    {
                        dp[index] = 1;
                    }
                    else
                    {
                        int left = dp[j * cellsX + (i - 1)];
                        int down = dp[(j - 1) * cellsX + i];
                        int diagonal = dp[(j - 1) * cellsX + (i - 1)];
                        dp[index] = Mathf.Min(Mathf.Min(left, down), diagonal) + 1;
                    }

                    if (dp[index] <= bestSide)
                        continue;

                    bestSide = dp[index];
                    bestEndI = i;
                    bestEndJ = j;
                }
            }
        }

        /// <summary>
        /// Returns true when the candidate square overlapped a live reservation and cells were
        /// burned, meaning the DP must run again.
        /// </summary>
        private static bool BurnReservedCells(
            PestControlCellRejection[] rejections,
            Vector3[] floorPoints,
            int cellsX,
            Quaternion tileRotation,
            float dominantFloorY,
            int side,
            int originI,
            int originJ,
            Vector3 squareCenter)
        {
            float footprint = Mathf.Min(side * CellSizeMeters, MaxFootprintMeters);
            if (!FixtureReservationRegistry.TryGetConflict(
                    CreateTrapFootprint(footprint),
                    squareCenter,
                    tileRotation,
                    out _))
                return false;

            FixtureFootprint cellFootprint = CreateTrapFootprint(CellSizeMeters);
            bool burnedAny = false;
            for (int j = originJ; j < originJ + side; j++)
            {
                for (int i = originI; i < originI + side; i++)
                {
                    int index = j * cellsX + i;
                    if (rejections[index] != PestControlCellRejection.Clear)
                        continue;

                    Vector3 cellFloor = new Vector3(floorPoints[index].x, dominantFloorY, floorPoints[index].z);
                    if (!FixtureReservationRegistry.TryGetConflict(cellFootprint, cellFloor, tileRotation, out _))
                        continue;

                    rejections[index] = PestControlCellRejection.Reserved;
                    burnedAny = true;
                }
            }

            if (burnedAny)
                return true;

            // The claim clips the square's padding but no single cell centre; burn the whole square
            // so the retry cannot keep proposing the same footprint.
            for (int j = originJ; j < originJ + side; j++)
            {
                for (int i = originI; i < originI + side; i++)
                {
                    int index = j * cellsX + i;
                    if (rejections[index] == PestControlCellRejection.Clear)
                        rejections[index] = PestControlCellRejection.Reserved;
                }
            }

            return true;
        }

        /// <summary>Maps a shared grid cell state onto this probe's tally/visualization enum.</summary>
        private static PestControlCellRejection ToRejection(TileClearanceCellState state)
        {
            switch (state)
            {
                case TileClearanceCellState.OutsideTile: return PestControlCellRejection.OutsideTile;
                case TileClearanceCellState.NoFloor: return PestControlCellRejection.NoFloor;
                case TileClearanceCellState.FloorPlane: return PestControlCellRejection.FloorPlane;
                case TileClearanceCellState.Ceiling: return PestControlCellRejection.Ceiling;
                case TileClearanceCellState.Clutter: return PestControlCellRejection.Clutter;
                case TileClearanceCellState.NavMesh: return PestControlCellRejection.NavMesh;
                default: return PestControlCellRejection.Clear;
            }
        }

        private static bool HasClutter(Vector3 floor, Quaternion tileRotation, Collider floorCollider, int mask)
        {
            Vector3 center = floor + Vector3.up * (ClutterProbeHeightMeters * 0.5f + 0.05f);
            var halfExtents = new Vector3(CellSizeMeters * 0.45f, ClutterProbeHeightMeters * 0.5f, CellSizeMeters * 0.45f);
            InteriorAnchorService.RecordPhysicsQuery();
            int hits = Physics.OverlapBoxNonAlloc(center, halfExtents, OverlapBuffer, tileRotation, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                Collider hit = OverlapBuffer[i];
                if (hit == null || ReferenceEquals(hit, floorCollider))
                    continue;

                return true;
            }

            return false;
        }

        private static void LogResult(PestControlTrapPlacement result)
        {
            if (!result.HasResult)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.PestControl] PEST_PLACEMENT no-result tilesConsidered={result.TilesConsidered} " +
                    $"tilesScanned={result.TilesScanned} cells={result.CellsSampled} rejects[{result.Rejections}]");
                return;
            }

            PestControlTrapAssembler.DeriveCageMetrics(
                result.CeilingClearanceMeters,
                out float cageHeight,
                out float restBottom,
                out float postHeight);

            // "pre-accessory", not "production", because this runs inside the probe — before
            // TryFinalizePlacementAccessories has walked the lever outset ladder or the lamp ring.
            // The lever pose is still unresolved here, so this line reports
            // REJECTED [lever-floor-unavailable] on runs that go on to place and spawn perfectly.
            // The accurate verdict is the one the registry and the debug API log after finalization.
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.PestControl] PEST_PLACEMENT source='{result.PlacementSource}' " +
                $"pre-accessory={result.BuildProductionValiditySummary()} tile='{result.TileName}' score={result.Score:0.0} " +
                $"center=({result.Center.x:0.0},{result.Center.y:0.0},{result.Center.z:0.0}) " +
                $"footprint={result.FootprintMeters:0.00}m yaw={result.Yaw:0.0} " +
                $"ceiling=min{result.CeilingClearanceMeters:0.00}m/med{result.MedianCeilingMeters:0.00}m " +
                $"cage={cageHeight:0.00}m rest={restBottom:0.00}m posts={postHeight:0.00}m " +
                $"entrancePath={result.EntrancePathDistance:0.0}m ring={result.RingMeters:0} " +
                $"tilesConsidered={result.TilesConsidered} tilesScanned={result.TilesScanned} " +
                $"cells={result.CellsSampled} clear={result.CellsClear} rejects[{result.Rejections}] " +
                $"{(result.MeetsMinimumFootprint ? "meets-minimum" : "BEST-EFFORT")}");

            if (result.ScoreBoard == null)
                return;

            for (int i = 0; i < result.ScoreBoard.Count; i++)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.PestControl] PEST_PLACEMENT_CAND #{i + 1} {result.ScoreBoard[i]}");
            }
        }

        private readonly struct TileCandidate
        {
            internal readonly Tile Tile;
            internal readonly float EntranceDistance;

            /// <summary>#686: identity verdict from <see cref="TileRoomClassifierRuntime"/>; transit tiles are dropped whenever any room exists.</summary>
            internal readonly bool IsRoom;

            internal TileCandidate(Tile tile, float entranceDistance, bool isRoom)
            {
                Tile = tile;
                EntranceDistance = entranceDistance;
                IsRoom = isRoom;
            }
        }

        private sealed class TileScan
        {
            internal Tile Tile;
            internal float EntranceDistance;
            internal float RingMeters;
            internal float Yaw;
            internal float DominantFloorY;
            internal int SquareCells;
            internal Vector3 SquareCenter;
            internal int CellsSampled;
            internal int CellsClear;
            internal float ClearFraction;
            internal float AspectRatio = 1f;
            internal float MinCeiling = MinCeilingMeters;
            internal float MedianCeiling = MinCeilingMeters;
            internal float BorderClearFraction;
            internal bool PlugsOppositeWalls;
            internal PestControlTileScore Score;
            internal PestControlRejectionTally Rejections;
            internal List<PestControlCellSample> Cells = new List<PestControlCellSample>();
        }
    }
}
