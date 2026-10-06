using System.Collections;
using System.Collections.Generic;
using DunGen;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations._Shared;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Facility.Interior.Placement;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Y4NGZCompany.Contracts.Payload
{
    /// <summary>One procedurally placed Payload cart: pose, provenance, and its live claim.</summary>
    internal sealed class PayloadCartPlacement
    {
        internal Tile Tile;
        internal string TileName = "<none>";

        /// <summary>
        /// #1305 C1 (D6): which rule placed the cart: <see cref="PayloadCartPlacer.SourceRanked"/>,
        /// <see cref="PayloadCartPlacer.SourceEntranceRoom"/> or <see cref="PayloadCartPlacer.SourceEntranceFloor"/>.
        /// </summary>
        internal string Source;

        /// <summary>Cart origin on the floor (the prefab pivot sits between the wheels).</summary>
        internal Vector3 Position;

        /// <summary>Yaw-only rotation; local +Z is the handle / pusher side, so the cart travels along -Z.</summary>
        internal Quaternion Rotation = Quaternion.identity;

        internal string TierName = "full";
        internal float Score;
        /// <summary>#1305 E1: NavMesh walk from the main door to the fitted pose (0 on the entrance floor without a grid).</summary>
        internal float EntrancePathDistance;
        internal int RunwayCells;

        /// <summary>#1305 E1 (R13): the pose's <see cref="PayloadCartRoute.Prove"/> to the main door passed; <see cref="RouteRefusal"/> is its token when it did not (the last resort only, R22).</summary>
        internal bool RouteProved;
        internal int RouteProofs;
        internal string RouteRefusal;

        internal FixtureReservationGroup ReservationGroup;
        internal bool IsReserved;
    }

    /// <summary>
    /// Host-side spawn-site search for the Payload cart (#877): a budgeted direct search over
    /// the shared <see cref="TileClearanceGrid"/> clear-cell masks, modelled on
    /// <see cref="ShadowRaid.BreachContainerPlacer"/>. The old spawn took the selected tile's
    /// bounds center and a NavMesh snap and never asked what stood there, so the cart
    /// regularly started life inside a pillar, a railing, or a machine and its sweep motor —
    /// which has no depenetration step — could never move it. Per candidate tile the
    /// pure <see cref="PayloadCartFitMath"/> fits a scored oriented rectangle (travel lane,
    /// cart body, pusher strip) on cells that have floor, ceiling, no clutter and NavMesh,
    /// keeping a margin from any cell the floor falls away from. #1305 R22: the route proof is the
    /// gate. The fitted poses are tried in one order (<see cref="CompareScans"/>), then the main-entrance
    /// room's; each goes through the pusher standing test, the haul's own <see cref="PayloadCartRoute.Prove"/>
    /// to the main door (whose first step is the cart's one clearance test) and the claim in the
    /// <see cref="FixtureReservationRegistry"/>. A refusal where the cart stands burns the rectangle and
    /// the tile refits; any other route refusal spends the tile. The first proved pose wins. D22: when
    /// none proves, the main-entrance room's first rectangle (else a floor point in it) is taken unproved.
    /// </summary>
    internal static class PayloadCartPlacer
    {
        /// <summary>
        /// Wall-clock cap for one resolution call. The search runs from the contract's
        /// per-frame runtime update right after landing, so a big interior spreads its candidate
        /// collect, its grid builds and its placement (chassis checks, claim) across
        /// several frames instead of draining them in one (#1283 A7). One unit may overrun it.
        /// </summary>
        private const double MaxScanMillisecondsPerCall = 6.0;

        /// <summary>
        /// #1271 D1: the least NavMesh walk from the main door (#1305 E1) to a fitted pose, so the cart
        /// never starts beside the entrance. Poses short of it are tried only after every pose past it.
        /// </summary>
        private const float MinEntranceWalkMeters = 25f;

        /// <summary>#1305 D18: the entrance-walk band ranked first (60-120 m), then deeper, then shallower.</summary>
        private const float SpawnBandNearMeters = 60f;
        private const float SpawnBandFarMeters = 120f;

        /// <summary>
        /// #1271 D1: clear floor demanded around the cart's motion box on every side of the grid
        /// footprint (rounded up to whole cells), so the fitted rectangle keeps off walls and props.
        /// </summary>
        private const float SpawnClearanceMarginMeters = 0.3f;

        /// <summary>Floor behind the handle where the pusher stands; the pusher follows 1.4 m off the origin.</summary>
        private const float PusherStripMeters = 1.5f;

        /// <summary>
        /// Narrowed strip for the last tier: still past the 1.4 m follow distance once the
        /// body's own half length is counted, so the player who grabs the handle has floor.
        /// </summary>
        private const float TightPusherStripMeters = 1.0f;

        /// <summary>Clear floor ahead of the cart so the first push moves it.</summary>
        private const float TravelLaneMeters = 1.5f;

        /// <summary>
        /// Cart top (1.32 m) plus the pusher standing beside it. Rooms are much taller; this
        /// only rules out crawlspaces and the underside of stairs.
        /// </summary>
        private const float MinCeilingMeters = 2.2f;

        /// <summary>Reserved height of the claim: the cart plus a standing pusher.</summary>
        private const float ReservationHeightMeters = 2.0f;

        /// <summary>
        /// How far below the tile's voted floor plane a cell has to sit before it counts as a
        /// drop rather than a step: a kerb or a ramp start is not a pit the cart can be shoved into.
        /// </summary>
        private const float DropDepthMeters = 1.0f;

        internal const string ReservationLabel = "payload-cart";
        internal const string ReservationSource = "payload-spawn";

        /// <summary>#1305 R22: a ranked tile's fitted rectangle whose cart route proved.</summary>
        internal const string SourceRanked = "clearance-scan";

        /// <summary>#1305 R22 (D22): the main-entrance room's rectangle, proved after the ranked tiles or, as the last resort, its first one unproved.</summary>
        internal const string SourceEntranceRoom = "entrance-room";

        /// <summary>#1305 C1 (D6): no rectangle fits even in the main-entrance room; a measured floor point in it.</summary>
        internal const string SourceEntranceFloor = "entrance-floor";

        /// <summary>
        /// #1305 R22: route proofs per placement call, not a cap: every fitted pose may be asked, and a
        /// footprint walk is not cheap, so the proofs spread across frames.
        /// </summary>
        private const int MaxRouteProofs = 8;

        private static readonly PayloadCartRouteScratch RouteScratch = new PayloadCartRouteScratch();

        // The travel lane is dropped first and the pusher strip is only narrowed, never dropped:
        // grabbing the cart puts the player a full follow distance behind the handle, so a
        // body-only rectangle spawns a cart wedged handle-first into a wall (#877).
        private static readonly PayloadCartFitTier[] Tiers =
        {
            new PayloadCartFitTier("full", CellsFor(PusherStripMeters), CellsFor(TravelLaneMeters)),
            new PayloadCartFitTier("no-lane", CellsFor(PusherStripMeters), 0),
            new PayloadCartFitTier("tight-pusher", CellsFor(TightPusherStripMeters), 0),
        };

        // --- resumable scan state -------------------------------------------------
        // #1283 A7: one budgeted walk in three phases, each resumed from its own cursor: the collect
        // (one tile judged per unit, _nextCollectIndex; _scans stays null until it ends), the scan (one
        // grid per unit, _nextCandidateIndex; _scanSettled once it ends) and the placement (one pose per
        // unit, _nextPlaceIndex). #1305 C1 (D6): the main-entrance room (_entrance, its grid and first
        // fit) is scanned after the ranked tiles.
        private static Dungeon _scanDungeon;
        private static IReadOnlyList<Tile> _scanTiles;
        private static IReadOnlyList<Vector3> _scanEntrances;
        private static int _nextCollectIndex;
        private static List<TileCandidate> _scanCandidates;
        private static List<TileScan> _scans;
        private static int _nextCandidateIndex;
        private static IEnumerator _activeBuild;
        private static TileClearanceGrid _activeGrid;
        private static Stopwatch _scanFrame;
        private static bool _scanSettled;
        private static bool _placeStarted;
        private static Vector3 _mainDoor;
        private static Vector3[] _mainDoorFrom;
        private static TileCandidate _entrance;
        private static TileScan _entranceScan;
        private static PayloadCartFitCandidate _entranceFit;
        private static int _nextPlaceIndex;
        // #1305 C1 (D6): refusals in the tile being placed; only the first is logged, the rest summed.
        private static int _tileRefusals;
        private static int _routeProofs;

        /// <summary>Drops any partially completed scan; a later search starts fresh.</summary>
        internal static void ResetScanState()
        {
            _scanDungeon = null;
            _scanTiles = null;
            _scanEntrances = null;
            _nextCollectIndex = 0;
            _scanCandidates = null;
            _scans = null;
            _nextCandidateIndex = 0;
            _activeBuild = null;
            _activeGrid = null;
            _scanFrame = null;
            _scanSettled = false;
            _placeStarted = false;
            _mainDoor = default;
            _mainDoorFrom = null;
            _entrance = default;
            _entranceScan = null;
            _entranceFit = default;
            _nextPlaceIndex = 0;
            _tileRefusals = 0;
            _routeProofs = 0;
        }

        /// <summary>Cart footprint on the grid: motion box plus clearance margin, rounded up to whole cells.</summary>
        internal static void ResolveFootprintCells(out int widthCells, out int bodyLengthCells)
        {
            Vector3 size = PayloadCartController.SpawnClearanceSize;
            widthCells = CellsFor(size.x + 2f * SpawnClearanceMarginMeters);
            bodyLengthCells = CellsFor(size.z + 2f * SpawnClearanceMarginMeters);
        }

        private static int CellsFor(float meters)
        {
            return Mathf.Max(1, Mathf.CeilToInt(meters / TileClearanceGrid.CellSizeMeters));
        }

        /// <summary>
        /// Budgeted resolution of the spawn site, resumable across frames. False while the dungeon is
        /// not generated or the scan is in flight: call again next frame. True once
        /// <paramref name="placement"/> is set, which consumes the scan; #1305 C1 (D6): it is never null.
        /// </summary>
        internal static bool TryResolveBudgeted(out PayloadCartPlacement placement)
        {
            placement = null;
            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null || dungeon.AllTiles.Count == 0)
                return false;

            if (!ReferenceEquals(_scanDungeon, dungeon) || _scanTiles == null)
            {
                ResetScanState();
                _scanDungeon = dungeon;
                BeginScan(dungeon.AllTiles, InteriorAnchorService.GetInteriorEntrancePositions());
            }

            return StepScan(out placement);
        }

        /// <summary>
        /// #1271: <see cref="TryResolveBudgeted"/>'s own resumable walk - candidate collect, clearance
        /// scan, route proofs, claim and the main-entrance last resort - driven to completion over
        /// <paramref name="tiles"/> in one call. Never null (#1305 C1 (D6)). Host-side; drops any scan
        /// in flight.
        /// </summary>
        internal static PayloadCartPlacement ResolveForTiles(IReadOnlyList<Tile> tiles, IReadOnlyList<Vector3> entrances)
        {
            ResetScanState();
            BeginScan(tiles, entrances);
            PayloadCartPlacement placement;
            while (!StepScan(out placement)) { }
            return placement;
        }

        /// <summary>
        /// #1283 A7: one budgeted call of the scan in flight: whole units, in order, until
        /// <see cref="MaxScanMillisecondsPerCall"/> is spent. The collect and the grid scan may share a
        /// call; the placement starts on the call after the scan ends (#1233 S8). True once the cart is
        /// placed, which consumes the scan.
        /// </summary>
        private static bool StepScan(out PayloadCartPlacement placement)
        {
            placement = null;
            ResolveFootprintCells(out int widthCells, out int bodyLengthCells);
            var callClock = Stopwatch.StartNew();
            if (!_scanSettled)
            {
                if (!ContinueCollect(callClock) || !ContinueScan(CreateGridSettings(), widthCells, bodyLengthCells, callClock))
                    return false;
                // #1233 S8: the scan's last slice and the placement (the ordering line, the chassis
                // checks and the claim) are two frames, not one; the 27 September activation frame
                // stacked both with the network spawn.
                _scanSettled = true;
                return false;
            }

            if (!ContinuePlace(widthCells, bodyLengthCells, callClock, out placement))
                return false;

            ResetScanState();
            return true;
        }

        private static TileClearanceGridSettings CreateGridSettings()
        {
            return new TileClearanceGridSettings
            {
                MinCeilingMeters = MinCeilingMeters,
            };
        }

        internal static void ReleaseReservation(PayloadCartPlacement placement)
        {
            if (placement == null || !placement.IsReserved)
                return;

            FixtureReservationRegistry.Release(placement.ReservationGroup);
            placement.IsReserved = false;
            placement.ReservationGroup = default;
        }

        private static void BeginScan(IReadOnlyList<Tile> tiles, IReadOnlyList<Vector3> entrances)
        {
            _scanTiles = tiles;
            _scanEntrances = entrances;
            _scanCandidates = new List<TileCandidate>(tiles.Count);
            _scanFrame = Stopwatch.StartNew();
            // #1305 C1 (D6): the squad's own insideMain resolver names the main-entrance room; #1305 E1
            // (R13): it is also the haul's goal, so every pose's walk and route proof run to it.
            _mainDoor = MoonEscalationState.FindInsideMainEntrancePosition();
            _mainDoorFrom = new[] { _mainDoor };
        }

        /// <summary>
        /// #1283 A7: the candidate collect, one tile per unit (<see cref="CollectCandidate"/>), until
        /// the call's budget is spent. True once every tile is judged; the order is set when the placement starts.
        /// </summary>
        private static bool ContinueCollect(Stopwatch callClock)
        {
            if (_scans != null)
                return true;

            float relaxedMinArea = InteriorPlacementService.MinAreaForRole(InteriorPlacementRole.BreachRoomProp, false);
            while (_nextCollectIndex < _scanTiles.Count)
            {
                CollectCandidate(_scanTiles[_nextCollectIndex++], relaxedMinArea);
                if (callClock.Elapsed.TotalMilliseconds >= MaxScanMillisecondsPerCall)
                    return false;
            }

            _scans = new List<TileScan>(_scanCandidates.Count);
            ResolveFootprintCells(out int widthCells, out int bodyLengthCells);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload] Spawn scan started over {_scanCandidates.Count} candidate tile(s); footprint {widthCells}x{bodyLengthCells} cells; "
                + $"main-entrance room '{(_entrance.Tile != null ? _entrance.Tile.name : "none")}'.");
            return true;
        }

        private static bool ContinueScan(
            TileClearanceGridSettings gridSettings,
            int widthCells,
            int bodyLengthCells,
            Stopwatch callClock)
        {
            _scanFrame.Restart();
            while (true)
            {
                // #1305 C1 (D6): the ranked candidates in order, then the main-entrance room.
                bool ranked = _nextCandidateIndex < _scanCandidates.Count;
                if (_activeBuild == null)
                {
                    if (!ranked && (_nextCandidateIndex > _scanCandidates.Count || _entrance.Tile == null))
                        return true;

                    Tile tile = ranked ? _scanCandidates[_nextCandidateIndex].Tile : _entrance.Tile;
                    _activeGrid = new TileClearanceGrid();
                    _activeBuild = TileClearanceGrid.Build(tile, gridSettings, true, _scanFrame, _activeGrid);
                }

                while (true)
                {
                    if (callClock.Elapsed.TotalMilliseconds >= MaxScanMillisecondsPerCall)
                        return false;
                    if (!_activeBuild.MoveNext())
                        break;
                }

                TileScan scan = FinishScannedGrid(ranked ? _scanCandidates[_nextCandidateIndex] : _entrance, _activeGrid, widthCells, bodyLengthCells);
                if (!ranked)
                {
                    _entranceScan = scan;
                    _entranceFit = scan != null ? scan.BestFit : default;
                }
                else if (scan != null)
                {
                    _scans.Add(scan);
                }
                _nextCandidateIndex++;
                _activeBuild = null;
                _activeGrid = null;
                if (callClock.Elapsed.TotalMilliseconds >= MaxScanMillisecondsPerCall)
                    return false;
            }
        }

        private static TileScan FinishScannedGrid(TileCandidate candidate, TileClearanceGrid grid, int widthCells, int bodyLengthCells)
        {
            if (grid.CellCount <= 0 || !grid.HasFloorPlane)
                return null;

            var scan = new TileScan
            {
                Grid = grid,
                Clear = new bool[grid.CellCount],
                Drop = new bool[grid.CellCount],
                Doorways = ProjectDoorways(candidate.Tile, grid),
                IsRoom = candidate.IsRoom,
            };
            for (int index = 0; index < grid.CellCount; index++)
            {
                scan.Clear[index] = grid.States[index] == TileClearanceCellState.Clear;
                if (!IsDropCell(grid, index))
                    continue;

                scan.Drop[index] = true;
                scan.DropCells++;
            }

            scan.BestFit = FindFit(scan, widthCells, bodyLengthCells);
            // #1305 E1: the walk is from the main door to the fitted pose; no fit is never Walkable.
            if (scan.BestFit.HasFit)
            {
                ResolvePose(grid, scan.BestFit, out Vector3 origin, out _);
                scan.Walkable = InteriorAnchorService.TryGetEntrancePathDistance(origin, out scan.EntranceDistance, _mainDoorFrom);
            }
            return scan;
        }

        private static PayloadCartFitCandidate FindFit(TileScan scan, int widthCells, int bodyLengthCells)
        {
            return PayloadCartFitMath.FindBestFit(
                scan.Clear,
                scan.Grid.CellsX,
                scan.Grid.CellsZ,
                widthCells,
                bodyLengthCells,
                Tiers,
                TileClearanceGrid.CellSizeMeters,
                scan.Doorways,
                scan.Drop);
        }

        /// <summary>
        /// Cells the floor falls away from (#877). Pass A of the grid raycasts 12 m down and marks
        /// a miss <see cref="TileClearanceCellState.NoFloor"/>, which is exactly the walkway lip of a
        /// pit room such as <c>LargeForkTileB</c>; pass B then rejects any hit further than the plane
        /// tolerance from the voted floor as <see cref="TileClearanceCellState.FloorPlane"/>, and the
        /// stored hit point separates a step up (harmless) from a floor a metre or more below.
        /// <see cref="TileClearanceCellState.OutsideTile"/> is never a drop: it is the neighbouring
        /// tile, which the grid simply did not probe.
        /// </summary>
        private static bool IsDropCell(TileClearanceGrid grid, int index)
        {
            TileClearanceCellState state = grid.States[index];
            if (state == TileClearanceCellState.NoFloor)
                return true;
            if (state != TileClearanceCellState.FloorPlane)
                return false;

            return grid.FloorPoints != null
                   && index < grid.FloorPoints.Length
                   && grid.FloorPoints[index].y < grid.DominantFloorY - DropDepthMeters;
        }

        /// <summary>
        /// #1271 D1: whether one tile is a spawn candidate. Entrance tiles and tiles under the room-prop
        /// area floor are left out; transit tiles are ranked after rooms, never dropped. #1305 E1: a tile
        /// the main door cannot walk to is ranked last (<see cref="CompareScans"/>), never dropped. #1305 C1
        /// (D6): of the entrance tiles, the one nearest the main door is kept as the main-entrance room.
        /// </summary>
        private static void CollectCandidate(Tile tile, float relaxedMinArea)
        {
            if (tile == null || InteriorPlacementService.IsTileExcludedFromPlacement(tile))
                return;
            if (PlacementValidator.IsEntranceTile(tile, _scanEntrances))
            {
                if (_entrance.Tile == null || IsNearerMainDoor(tile, _entrance.Tile))
                    _entrance = new TileCandidate(tile, false);
                return;
            }

            if (InteriorAnchorService.GetTileArea(tile) >= relaxedMinArea)
                _scanCandidates.Add(new TileCandidate(tile, TileRoomClassifierRuntime.Classify(tile, strictShape: true).IsRoom));
        }

        /// <summary>
        /// #1305 C1 (D6): whether <paramref name="tile"/> lies nearer the main door than
        /// <paramref name="best"/>: the horizontal distance from the door to the tile's bounds (0 inside),
        /// ties (floors stacked over the door) to the full distance.
        /// </summary>
        private static bool IsNearerMainDoor(Tile tile, Tile best)
        {
            Bounds bounds = tile.Bounds, bestBounds = best.Bounds;
            float offset = HorizontalSqrOffset(bounds), bestOffset = HorizontalSqrOffset(bestBounds);
            return offset != bestOffset ? offset < bestOffset : bounds.SqrDistance(_mainDoor) < bestBounds.SqrDistance(_mainDoor);
        }

        private static float HorizontalSqrOffset(Bounds bounds)
        {
            float dx = Mathf.Max(0f, Mathf.Max(bounds.min.x - _mainDoor.x, _mainDoor.x - bounds.max.x));
            float dz = Mathf.Max(0f, Mathf.Max(bounds.min.z - _mainDoor.z, _mainDoor.z - bounds.max.z));
            return dx * dx + dz * dz;
        }

        /// <summary>
        /// #1305 E1: the scans over <paramref name="tiles"/> in the order the placement walks them, built by
        /// the budgeted collect and scan driven to completion - the test seam for the order. Drops any scan in flight.
        /// </summary>
        private static List<TileScan> RankedScans(IReadOnlyList<Tile> tiles, IReadOnlyList<Vector3> entrances)
        {
            ResetScanState();
            BeginScan(tiles, entrances);
            while (!_scanSettled)
                StepScan(out _);
            _scans.Sort(CompareScans);
            List<TileScan> scans = _scans;
            ResetScanState();
            return scans;
        }

        /// <summary>
        /// #1283 A7: the placement, one pose per unit (<see cref="TryPose"/>), until the call has spent
        /// <see cref="MaxScanMillisecondsPerCall"/> or <see cref="MaxRouteProofs"/> proofs; the order
        /// (<see cref="CompareScans"/>) and its ranking line come first, once per scan. #1305 R22: the ranked
        /// tiles in order, then the main-entrance room; a tile cut off by the budget resumes next call.
        /// D22: with no proved pose, the main-entrance room's first rectangle, else a floor point in it
        /// (<see cref="PlaceOnEntranceFloor"/>), asked the route once. True once placed; never null then.
        /// </summary>
        private static bool ContinuePlace(int widthCells, int bodyLengthCells, Stopwatch callClock, out PayloadCartPlacement placement)
        {
            placement = null;
            int callProofs = _routeProofs;
            if (!_placeStarted)
            {
                _scans.Sort(CompareScans);
                LogRanking(_scans);
                _placeStarted = true;
            }

            while (_nextPlaceIndex <= _scans.Count)
            {
                bool ranked = _nextPlaceIndex < _scans.Count;
                TileScan scan = ranked ? _scans[_nextPlaceIndex] : _entranceScan;
                // Out of rectangles, or (#1305 R22) its route refused: the tile is spent.
                if (scan == null || !scan.BestFit.HasFit)
                {
                    if (_tileRefusals > 1)
                        ContractsBootstrap.Log?.LogInfo(
                            $"[MoonContracts.Payload] Spawn tile '{scan.Grid.Tile?.name}' spent: {_tileRefusals} pose(s) refused by the chassis checks or claims.");
                    _tileRefusals = 0;
                    _nextPlaceIndex++;
                    continue;
                }

                placement = TryPose(scan, ranked ? SourceRanked : SourceEntranceRoom, widthCells, bodyLengthCells);
                if (placement != null)
                    break;
                if (callClock.Elapsed.TotalMilliseconds >= MaxScanMillisecondsPerCall || _routeProofs - callProofs >= MaxRouteProofs)
                    return false;
            }

            if (placement == null)
            {
                // #1305 D22: nothing proved. The last resort is asked once so its token is true.
                if (_entranceFit.HasFit)
                {
                    ResolvePose(_entranceScan.Grid, _entranceFit, out Vector3 origin, out Quaternion rotation);
                    placement = Claim(SourceEntranceRoom, _entranceScan, _entranceFit, origin, rotation, lastResort: true);
                }
                else
                    placement = PlaceOnEntranceFloor();
                _routeProofs++;
                placement.RouteProved = PayloadCartRoute.Prove(placement.Position + Vector3.up * PayloadContract.SpawnVerticalOffset, placement.Rotation,
                    _mainDoor, BlacksiteReachability.SoldierAreaMask, RouteScratch, null, out placement.RouteRefusal);
            }

            placement.RouteProofs = _routeProofs;
            return true;
        }

        /// <summary>
        /// One pose in a tile: its best rectangle, burned and refit at once, through the pusher's stand,
        /// the route proof (#1305 R22) and the claim. Null unless it proves and wins its claim. A refusal
        /// where the cart stands leaves the refit; any other refusal is the tile's route verdict and spends it.
        /// </summary>
        private static PayloadCartPlacement TryPose(TileScan scan, string source, int widthCells, int bodyLengthCells)
        {
            PayloadCartFitCandidate fit = scan.BestFit;
            TileClearanceGrid grid = scan.Grid;
            ResolvePose(grid, fit, out Vector3 origin, out Quaternion rotation);

            // Burn the winning rectangle either way: on rejection the refit must move
            // somewhere new, and on success nothing else should re-propose it.
            BurnRect(scan, fit);
            scan.BestFit = FindFit(scan, widthCells, bodyLengthCells);

            Vector3 spawnPose = origin + Vector3.up * PayloadContract.SpawnVerticalOffset;

            // The rectangle proves the strip behind the handle is clear grid floor, but the
            // grid never asks whether a player fits there: grabbing the cart teleports the
            // pusher a full follow distance back, and a railing or a ledge at that exact
            // spot is the "wedged, hard to grab" report on #877.
            if (!PayloadCartController.IsPusherStandClear(spawnPose, rotation, out string pusherBlocker))
            {
                if (_tileRefusals++ == 0)
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.Payload] Spawn candidate in '{grid.Tile?.name}' at {origin} leaves no standing room behind the handle ('{pusherBlocker}'); retrying elsewhere.");
                return null;
            }

            // #1305 R22 (R13): the haul's own oracle, from the cart origin it hauls from, to the main door.
            _routeProofs++;
            if (PayloadCartRoute.Prove(spawnPose, rotation, _mainDoor, BlacksiteReachability.SoldierAreaMask, RouteScratch, null, out string refusal))
            {
                PayloadCartPlacement proved = Claim(source, scan, fit, origin, rotation, lastResort: false);
                if (proved != null)
                    proved.RouteProved = true;
                return proved;
            }

            // Refused where it stands (the start sample or ProveFootprint's first step): the chassis's, not the route's.
            if (RouteScratch.RefusedAt == spawnPose && (refusal == PayloadCartRoute.RefuseStart || refusal == PayloadCartRoute.RefuseSweep
                    || refusal == PayloadCartRoute.RefuseLedge || refusal == PayloadCartRoute.RefuseStep))
            {
                if (_tileRefusals++ == 0)
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.Payload] Spawn candidate in '{grid.Tile?.name}' at {origin} (drop={scan.DropCells}) cannot stand there: {PayloadCartRoute.Describe(refusal, RouteScratch)}; retrying elsewhere.");
                return null;
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload] Spawn candidate in '{grid.Tile?.name}' at {origin} has no cart route to the main door: {PayloadCartRoute.Describe(refusal, RouteScratch)}.");
            scan.BestFit = default;
            return null;
        }

        /// <summary>The cart origin on the floor under a fitted rectangle's body, and its yaw (local +Z is the handle side).</summary>
        private static void ResolvePose(TileClearanceGrid grid, PayloadCartFitCandidate fit, out Vector3 origin, out Quaternion rotation)
        {
            origin = FloorPointAt(grid, fit.BodyCenterI, fit.BodyCenterJ);
            rotation = Quaternion.LookRotation(grid.Right * fit.HandleI + grid.Forward * fit.HandleJ, Vector3.up);
        }

        /// <summary>
        /// #1305 C1 (D6): no rectangle fits even in the main-entrance room, so the cart goes on a measured
        /// floor point in it: the room's Clear cell nearest its grid centre, square to the grid; with no
        /// grid or no Clear cell, the floor under the main door.
        /// </summary>
        private static PayloadCartPlacement PlaceOnEntranceFloor()
        {
            TileClearanceGrid grid = _entranceScan != null ? _entranceScan.Grid : null;
            int bestI = -1, bestJ = -1;
            float bestOffset = float.MaxValue;
            for (int j = 0; grid != null && j < grid.CellsZ; j++)
            {
                for (int i = 0; i < grid.CellsX; i++)
                {
                    float di = i + 0.5f - grid.CellsX * 0.5f, dj = j + 0.5f - grid.CellsZ * 0.5f;
                    if (!grid.IsClear(i, j) || di * di + dj * dj >= bestOffset)
                        continue;
                    bestOffset = di * di + dj * dj;
                    bestI = i;
                    bestJ = j;
                }
            }

            bool onCell = bestI >= 0;
            Vector3 origin = onCell ? FloorPointAt(grid, bestI + 0.5f, bestJ + 0.5f) : InteriorAnchorService.ResolveFloorPosition(_mainDoor);
            Quaternion rotation = onCell ? Quaternion.LookRotation(grid.Forward, Vector3.up) : Quaternion.identity;
            return Claim(SourceEntranceFloor, _entranceScan, default, origin, rotation, lastResort: true);
        }

        /// <summary>
        /// #1305 C1 (D6): the placement for one chosen pose, with its <paramref name="source"/>, and its
        /// claim: the cart's volume plus a fitted rectangle's pusher strip. A conflict refuses a checked
        /// pose (null); a last-resort pose keeps it, unreserved. <paramref name="scan"/> is null only for
        /// the main-entrance room's floor without a grid.
        /// </summary>
        private static PayloadCartPlacement Claim(string source, TileScan scan, PayloadCartFitCandidate fit,
            Vector3 origin, Quaternion rotation, bool lastResort)
        {
            Tile tile = scan != null ? scan.Grid.Tile : _entrance.Tile;
            string tileName = tile != null ? tile.name : "main-door";
            Vector3 size = PayloadCartController.SpawnClearanceSize;
            float pusherMeters = fit.HasFit ? Tiers[fit.TierIndex].PusherCells * TileClearanceGrid.CellSizeMeters : 0f;
            var cart = new FixtureReservationRequest(
                ReservationLabel,
                ReservationSource,
                CreateVolumeFootprint(size.x, ReservationHeightMeters, size.z),
                origin,
                rotation);
            FixtureReservationRequest[] requests = pusherMeters > 0f
                ? new[]
                {
                    cart,
                    new FixtureReservationRequest(
                        ReservationLabel + "-pusher",
                        ReservationSource,
                        CreateVolumeFootprint(size.x, ReservationHeightMeters, pusherMeters),
                        origin + rotation * Vector3.forward * (size.z * 0.5f + pusherMeters * 0.5f),
                        rotation),
                }
                : new[] { cart };

            bool reserved = FixtureReservationRegistry.TryReserveGroup(requests, out FixtureReservationGroup group, out FixtureReservationConflict conflict);
            if (!reserved && !lastResort)
            {
                if (_tileRefusals++ == 0)
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.Payload] Spawn candidate in '{tileName}' (drop={scan.DropCells}) lost to an existing claim ({conflict.ToDiagnosticString()}); retrying elsewhere.");
                return null;
            }

            return new PayloadCartPlacement
            {
                Tile = tile,
                TileName = tileName,
                Source = source,
                Position = origin,
                Rotation = rotation,
                TierName = fit.HasFit ? Tiers[fit.TierIndex].Name : "none",
                Score = fit.Score,
                EntrancePathDistance = scan != null ? scan.EntranceDistance : 0f,
                RunwayCells = fit.RunwayCells,
                ReservationGroup = group,
                IsReserved = reserved,
            };
        }

        /// <summary>
        /// #1271 D1: the first three scanned tiles with a fit, in the order the gates will try them,
        /// once per scan before any gate runs - the line a playtest reads to see which room the cart
        /// was headed for and how far it walks from the main door (#1305 E1).
        /// </summary>
        private static void LogRanking(List<TileScan> scans)
        {
            int first = -1, second = -1, third = -1;
            for (int i = 0; i < scans.Count && third < 0; i++)
            {
                if (!scans[i].BestFit.HasFit)
                    continue;
                if (first < 0)
                    first = i;
                else if (second < 0)
                    second = i;
                else
                    third = i;
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload] Spawn ranking (minEntrancePath={MinEntranceWalkMeters:0.0}m band={SpawnBandNearMeters:0}-{SpawnBandFarMeters:0}m): 1) {RankEntry(scans, first)} 2) {RankEntry(scans, second)} 3) {RankEntry(scans, third)}.");
        }

        private static string RankEntry(List<TileScan> scans, int index)
        {
            if (index < 0)
                return "none";
            TileScan scan = scans[index];
            string tileName = scan.Grid.Tile != null ? scan.Grid.Tile.name : "<null>";
            return $"'{tileName}' room={scan.IsRoom} entrancePath={scan.EntranceDistance:0.0}m band={SpawnBandName(scan.EntranceDistance)} tier={Tiers[scan.BestFit.TierIndex].Name}";
        }

        /// <summary>
        /// Floor point at fractional cell coordinates: the NavMesh-snapped floor point of the cell
        /// holding it when that cell is Clear, else the voted plane, so a room whose floor sits a few
        /// centimetres off the voted plane still gets the cart resting on its actual surface rather
        /// than hovering or sunk.
        /// </summary>
        private static Vector3 FloorPointAt(TileClearanceGrid grid, float cellI, float cellJ)
        {
            Vector3 point = grid.GridPoint(cellI, cellJ);
            int i = Mathf.Clamp(Mathf.FloorToInt(cellI), 0, grid.CellsX - 1);
            int j = Mathf.Clamp(Mathf.FloorToInt(cellJ), 0, grid.CellsZ - 1);
            int index = j * grid.CellsX + i;
            point.y = grid.FloorPoints != null && index >= 0 && index < grid.FloorPoints.Length
                      && grid.States != null && grid.States[index] == TileClearanceCellState.Clear
                ? grid.FloorPoints[index].y
                : grid.DominantFloorY;
            return point;
        }

        /// <summary>Bottom-anchored volume for the reservation registry; local +Z is the handle side.</summary>
        private static FixtureFootprint CreateVolumeFootprint(float widthMeters, float heightMeters, float depthMeters)
        {
            return new FixtureFootprint(
                Vector3.forward,
                Vector3.back,
                Vector3.up,
                new Vector3(widthMeters, heightMeters, depthMeters),
                0f,
                0f,
                0.5f,
                1.0f);
        }

        private static void BurnRect(TileScan scan, PayloadCartFitCandidate fit)
        {
            int cellsX = scan.Grid.CellsX;
            for (int j = fit.RectOriginJ; j < fit.RectOriginJ + fit.RectHeight; j++)
            {
                for (int i = fit.RectOriginI; i < fit.RectOriginI + fit.RectWidth; i++)
                    scan.Clear[j * cellsX + i] = false;
            }
        }

        private static List<PayloadCartDoorwayCell> ProjectDoorways(Tile tile, TileClearanceGrid grid)
        {
            var doorways = new List<PayloadCartDoorwayCell>();
            if (tile?.UsedDoorways == null)
                return doorways;

            for (int i = 0; i < tile.UsedDoorways.Count; i++)
            {
                Doorway doorway = tile.UsedDoorways[i];
                if (doorway == null || doorway.transform == null)
                    continue;

                Vector3 offset = doorway.transform.position - grid.Pivot;
                doorways.Add(new PayloadCartDoorwayCell(
                    Vector3.Dot(offset, grid.Right) / TileClearanceGrid.CellSizeMeters,
                    Vector3.Dot(offset, grid.Forward) / TileClearanceGrid.CellSizeMeters));
            }

            return doorways;
        }

        /// <summary>
        /// #1271 D1, the one spawn order; #1305 E1: over fitted poses, by the NavMesh walk from the main
        /// door. Poses that walk reaches come first, then poses at least <see cref="MinEntranceWalkMeters"/>
        /// out. #1305 D18: then by band: a walk of 60-120 m first, past 120 m next, short of 60 m last;
        /// rooms before transit tiles; then the walk nearest 90 m (the band centre) first. Ties fall to the
        /// tile name, so the same dungeon always yields the same order.
        /// </summary>
        private static int CompareScans(TileScan a, TileScan b)
        {
            if (a.Walkable != b.Walkable)
                return a.Walkable ? -1 : 1;
            bool aFar = a.EntranceDistance >= MinEntranceWalkMeters;
            bool bFar = b.EntranceDistance >= MinEntranceWalkMeters;
            if (aFar != bFar)
                return aFar ? -1 : 1;
            int byBand = SpawnBand(a.EntranceDistance).CompareTo(SpawnBand(b.EntranceDistance));
            if (byBand != 0)
                return byBand;
            if (a.IsRoom != b.IsRoom)
                return a.IsRoom ? -1 : 1;

            const float bandCentre = (SpawnBandNearMeters + SpawnBandFarMeters) * 0.5f;
            int byDistance = Mathf.Abs(a.EntranceDistance - bandCentre).CompareTo(Mathf.Abs(b.EntranceDistance - bandCentre));
            if (byDistance != 0)
                return byDistance;

            Tile aTile = a.Grid.Tile, bTile = b.Grid.Tile;
            return string.CompareOrdinal(aTile != null ? aTile.name : string.Empty, bTile != null ? bTile.name : string.Empty);
        }

        /// <summary>#1305 D18: band rank of an entrance walk: 0 in band, 1 deeper, 2 shallower.</summary>
        private static int SpawnBand(float walk)
        {
            return walk < SpawnBandNearMeters ? 2 : walk > SpawnBandFarMeters ? 1 : 0;
        }

        internal static string SpawnBandName(float walk)
        {
            int band = SpawnBand(walk);
            return band == 0 ? "in" : band == 1 ? "deep" : "shallow";
        }

        private readonly struct TileCandidate
        {
            internal readonly Tile Tile;
            internal readonly bool IsRoom;

            internal TileCandidate(Tile tile, bool isRoom)
            {
                Tile = tile;
                IsRoom = isRoom;
            }
        }

        private sealed class TileScan
        {
            internal TileClearanceGrid Grid;
            internal float EntranceDistance;
            internal bool IsRoom;
            // #1305 E1: a complete NavMesh walk from the main door reached the fitted pose.
            internal bool Walkable;
            internal bool[] Clear;

            /// <summary>Cells whose floor is missing or a metre-plus below the tile's plane (#877).</summary>
            internal bool[] Drop;

            internal int DropCells;
            internal List<PayloadCartDoorwayCell> Doorways;
            internal PayloadCartFitCandidate BestFit;
        }
    }
}
