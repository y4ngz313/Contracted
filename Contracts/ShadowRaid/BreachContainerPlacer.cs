using System.Collections;
using System.Collections.Generic;
using DunGen;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Facility.Interior.Placement;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Y4NGZCompany.Contracts.ShadowRaid
{
    /// <summary>One procedurally placed Blacksite container: pose, size, and its live claim.</summary>
    internal sealed class BreachContainerPlacement
    {
        internal Tile Tile;
        internal string TileName = "<none>";

        /// <summary>Drill-face origin on the floor — the container prefab's pivot convention.</summary>
        internal Vector3 Position;

        /// <summary>Yaw-only rotation; local +Z is the drill face.</summary>
        internal Quaternion Rotation = Quaternion.identity;

        internal string SizeName = "medium";
        internal float Score;
        internal float EntrancePathDistance;

        /// <summary>
        /// The route width, in metres, the winning fit was actually judged at — the requested
        /// body width, or the narrower one the tile itself carries. Logged so a degraded tile
        /// is visible in the placement line rather than silent.
        /// </summary>
        internal float RouteClearanceMeters;
        internal FixtureReservationGroup ReservationGroup;
        internal bool IsReserved;
    }

    /// <summary>
    /// Host-side procedural fallback for the Blacksite (Shadow Raid) shipping container: a
    /// budgeted direct search over the shared <see cref="TileClearanceGrid"/> clear-cell masks.
    /// Authored poses always win; this runs only when no authored pose resolved for the
    /// generated interior. Per candidate tile the pure <see cref="BreachContainerFitMath"/>
    /// fits a scored oriented rectangle (container body plus the drill-operator strip off the
    /// +Z face), the best-scoring candidate across all scanned tiles wins, and body + strip
    /// are claimed as one group in the <see cref="FixtureReservationRegistry"/> with the same
    /// burn-and-retry pattern the Pest Control probe uses.
    ///
    /// Deliberately NOT an Engine A (<see cref="InteriorPlacementService"/>) request: floor
    /// candidates there carry no wall normal and the breach-prop role would dead-end in
    /// wall-contact validation. The mask search is the intended design.
    /// </summary>
    internal static class BreachContainerPlacer
    {
        /// <summary>Matches the Pest Control probe's scan envelope.</summary>
        private const int MaxTilesScanned = 24;

        /// <summary>
        /// Wall-clock cap for one resolution call. The scan runs on the live gameplay path from
        /// the contract's 1.5 s spawn ticks, so a big interior spreads its grid builds across
        /// several ticks (via the resumable state below) instead of draining dozens of 64x64
        /// grids in a single frame.
        /// </summary>
        private const double MaxScanMillisecondsPerCall = 8.0;

        /// <summary>Burn-and-retry attempts when a winning rectangle collides with an existing reservation.</summary>
        private const int MaxReservationRetries = 6;

        /// <summary>Headroom above <c>BreachContainerController.BaseHeight</c> a cell's ceiling must clear.</summary>
        private const float CeilingHeadroomMeters = 0.12f;

        /// <summary>Reserved height over the drill strip — drill, operator, and swing space.</summary>
        private const float DrillStripHeightMeters = 2.0f;

        internal const string ReservationLabel = "blacksite-container";
        internal const string ReservationSource = "shadow-raid-procedural";

        /// <summary>
        /// Engine A's fixture-separation rule at selection time: a fit whose body center sits
        /// inside the shared separation radius of an existing registry claim is demoted per
        /// meter of shortfall. A penalty rather than a veto — the fit math and the
        /// burn-and-retry loop stay untouched, and a cramped interior still places its
        /// containers as far from the fixtures as its geometry allows. At the 25 m radius a
        /// fit on top of a fixture loses 7.5, several times the doorway/border/size terms.
        /// </summary>
        private const float FixtureCrowdingPenaltyPerMeter = 0.3f;

        private static readonly string[] ProceduralSizeNamesPreferredFirst = { "medium", "small" };

        // --- resumable scan state -------------------------------------------------
        // One procedural search may span several of the contract's 1.5 s spawn ticks; the
        // per-call budget pauses the scan (even mid-grid) and the next call resumes it.
        private static Dungeon _scanDungeon;
        private static List<TileCandidate> _scanCandidates;

        /// <summary>Length of the room-tile prefix of <see cref="_scanCandidates"/>; the list is sorted rooms first.</summary>
        private static int _roomCandidateCount;

        /// <summary>
        /// #1155: this round's verdict that no room tile of this interior can hold a container,
        /// so the scan runs its transit tiles as well. It deliberately outlives the per-attempt
        /// scan state: the contract retries the search every 30 s, and without the memory every
        /// retry would spend its whole tile cap re-scanning the same fitless rooms (Level3Flow:
        /// three cave tiles, 0/2 placed, all round). Cleared by a round reset or a regenerated
        /// dungeon, both of which reach <see cref="ResetScanState"/>.
        /// </summary>
        private static bool _widenedToTransit;

        private static List<TileScan> _scans;
        private static int _nextCandidateIndex;
        private static int _tilesExamined;
        private static IEnumerator _activeBuild;
        private static TileClearanceGrid _activeGrid;
        private static Stopwatch _scanFrame;

        /// <summary>How far into the candidate list this pass may scan: the room prefix, or all of it once widened.</summary>
        private static int ActiveCandidateLimit =>
            _widenedToTransit || _roomCandidateCount <= 0 ? _scanCandidates.Count : _roomCandidateCount;

        /// <summary>Drops any partially completed scan and this round's widening verdict; a later search starts fresh.</summary>
        internal static void ResetScanState()
        {
            ClearScanCursor();
            _widenedToTransit = false;
        }

        /// <summary>
        /// Drops the consumed scan but keeps the round-scoped widening verdict, so the next
        /// attempt re-scans against the then-current reservations without forgetting that the
        /// rooms already failed.
        /// </summary>
        private static void ClearScanCursor()
        {
            _scanDungeon = null;
            _scanCandidates = null;
            _roomCandidateCount = 0;
            _scans = null;
            _nextCandidateIndex = 0;
            _tilesExamined = 0;
            _activeBuild = null;
            _activeGrid = null;
            _scanFrame = null;
        }

        /// <summary>
        /// Budgeted resolution for the Shadow Raid spawn site, resumable across the contract's
        /// 1.5 s spawn ticks (PestControlTrapPlacer.ResolveBudgeted's time-slicing, expressed
        /// as retained state because the caller is a plain host-side method rather than a
        /// coroutine). While the scan is still in flight the method returns false with
        /// <paramref name="scanComplete"/> false; once every candidate tile is examined it
        /// appends up to <paramref name="count"/> reserved placements to
        /// <paramref name="placements"/>.
        /// </summary>
        internal static bool TryResolveBudgeted(int count, List<BreachContainerPlacement> placements, out bool scanComplete)
        {
            scanComplete = true;
            if (placements == null || count <= 0)
                return false;

            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null || dungeon.AllTiles.Count == 0)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Procedural container search ran with no generated dungeon.");
                return false;
            }

            BreachContainerController.ResolveContainerDimensions("medium", out float width, out float height, out float mediumLength);
            BreachContainerController.ResolveContainerDimensions("small", out _, out _, out float smallLength);
            int widthCells = Mathf.CeilToInt(width / TileClearanceGrid.CellSizeMeters);
            var lengthCells = new[]
            {
                Mathf.CeilToInt(mediumLength / TileClearanceGrid.CellSizeMeters),
                Mathf.CeilToInt(smallLength / TileClearanceGrid.CellSizeMeters),
            };

            // The drill strip mirrors BreachDrillItem's attach search: the configured attach
            // distance widened for the drill body, never under three meters.
            float attachDistance = ContractsBootstrap.ShadowRaidDrillAttachDistance?.Value ?? 1.4f;
            float stripMeters = Mathf.Max(attachDistance + 1.25f, 3f);
            int stripCells = Mathf.CeilToInt(stripMeters / TileClearanceGrid.CellSizeMeters);

            // #874 R1: the route a walking body has to keep past the placement, in cells. One
            // agent diameter - the 0.45 m radius BlacksiteNavigator.ConfigureAgent writes, which
            // is also what the interior navmesh is baked at - rounded up to whole cells. It is
            // the fit search's legality gate, not a score: the 2026-09-20 Collateral raid put a
            // nominally fitting container across the hallway its own operator had to walk.
            int routeClearanceCells = Mathf.CeilToInt(
                Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteArrivalPolicy.AgentRadius * 2f
                / TileClearanceGrid.CellSizeMeters);

            var gridSettings = new TileClearanceGridSettings
            {
                MinCeilingMeters = height + CeilingHeadroomMeters,
            };

            if (!ContinueScan(dungeon, gridSettings, widthCells, lengthCells, stripCells, routeClearanceCells))
            {
                scanComplete = false;
                return false;
            }

            List<TileScan> scans = _scans;
            if (scans == null || scans.Count == 0)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Procedural container search found no candidate tiles to scan.");
                ClearScanCursor();
                return false;
            }

            int placedBefore = placements.Count;
            for (int containerIndex = 0; containerIndex < count; containerIndex++)
            {
                BreachContainerPlacement placement = PlaceOneContainer(
                    scans,
                    placements.Count,
                    widthCells,
                    lengthCells,
                    stripCells,
                    routeClearanceCells,
                    width,
                    height,
                    mediumLength,
                    smallLength,
                    stripMeters);
                if (placement == null)
                    break;

                placements.Add(placement);
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.ShadowRaid] BLACKSITE_PLACEMENT procedural container {placements.Count}/{count} " +
                    $"tile='{placement.TileName}' size={placement.SizeName} score={placement.Score:0.0} " +
                    $"pos=({placement.Position.x:0.0},{placement.Position.y:0.0},{placement.Position.z:0.0}) " +
                    $"yaw={placement.Rotation.eulerAngles.y:0.0} entrancePath={placement.EntrancePathDistance:0.0}m " +
                    $"routeClearance={placement.RouteClearanceMeters:0.0}m " +
                    $"(requested {routeClearanceCells * TileClearanceGrid.CellSizeMeters:0.0}m)");
            }

            int placed = placements.Count - placedBefore;
            if (placed < count)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.ShadowRaid] Procedural container search placed {placed}/{count} container(s) " +
                    $"across {scans.Count} scanned tile(s) (widened={_widenedToTransit}, " +
                    $"routeClearance={routeClearanceCells * TileClearanceGrid.CellSizeMeters:0.0}m).");
            }

            // The scan is consumed either way: a later search (after the contract's backoff)
            // must start fresh against the then-current reservations. The widening verdict is
            // not part of that: it belongs to the round (#1155).
            ClearScanCursor();
            return placed > 0;
        }

        internal static void ReleaseReservation(BreachContainerPlacement placement)
        {
            if (placement == null || !placement.IsReserved)
                return;

            FixtureReservationRegistry.Release(placement.ReservationGroup);
            placement.IsReserved = false;
            placement.ReservationGroup = default;
        }

        /// <summary>
        /// Advances the candidate scan under the per-call millisecond budget, retaining partial
        /// state — including a half-built clearance grid — between calls. Returns true once
        /// every candidate has been examined or the tile cap is reached; the finished scans sit
        /// in <see cref="_scans"/>.
        /// </summary>
        private static bool ContinueScan(
            Dungeon dungeon,
            TileClearanceGridSettings gridSettings,
            int widthCells,
            int[] lengthCells,
            int stripCells,
            int routeClearanceCells)
        {
            if (_scanCandidates == null || !ReferenceEquals(_scanDungeon, dungeon))
            {
                // A regenerated dungeon is a different interior, so its rooms have not failed
                // yet; an ordinary new attempt (cursor already cleared, _scanDungeon null)
                // keeps this round's widening verdict.
                if (_scanDungeon != null && !ReferenceEquals(_scanDungeon, dungeon))
                    _widenedToTransit = false;

                ClearScanCursor();
                _scanDungeon = dungeon;
                _scanCandidates = CollectCandidateTiles(dungeon, out _roomCandidateCount);
                _scans = new List<TileScan>(Mathf.Min(_scanCandidates.Count, MaxTilesScanned));
                _scanFrame = Stopwatch.StartNew();
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.ShadowRaid] Procedural container scan started over {ActiveCandidateLimit} of " +
                    $"{_scanCandidates.Count} candidate tile(s) ({_roomCandidateCount} room); widened={_widenedToTransit}.");
            }

            // The grid build yields against _scanFrame roughly every FrameBudgetMilliseconds of
            // physics; restarting it here keeps stale elapsed time from a previous call out of
            // this call's slices.
            _scanFrame.Restart();
            var callClock = Stopwatch.StartNew();
            while (true)
            {
                if (_activeBuild == null)
                {
                    if (_tilesExamined >= MaxTilesScanned)
                        return true;
                    if (_nextCandidateIndex >= ActiveCandidateLimit && !TryWidenToTransitTiles())
                        return true;

                    Tile tile = _scanCandidates[_nextCandidateIndex].Tile;
                    // Every examined tile counts toward the cap — a grid that dies on CellCount
                    // or the floor-plane vote still burned its share of raycasts.
                    _tilesExamined++;
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

                FinishScannedGrid(_scanCandidates[_nextCandidateIndex], _activeGrid, widthCells, lengthCells, stripCells, routeClearanceCells);
                _nextCandidateIndex++;
                _activeBuild = null;
                _activeGrid = null;
                // FindBestFit inside FinishScannedGrid used to run outside the slice budget, so a call
                // whose grids all completed cheaply could still stack several fit searches in one frame.
                // Charge it against the same budget and hand the rest to the next call.
                if (callClock.Elapsed.TotalMilliseconds >= MaxScanMillisecondsPerCall)
                    return false;
            }
        }

        /// <summary>Post-build classification of one completed grid into a <see cref="TileScan"/>.</summary>
        private static void FinishScannedGrid(
            TileCandidate candidate,
            TileClearanceGrid grid,
            int widthCells,
            int[] lengthCells,
            int stripCells,
            int routeClearanceCells)
        {
            if (grid.CellCount <= 0 || !grid.HasFloorPlane)
                return;

            var scan = new TileScan
            {
                Grid = grid,
                EntranceDistance = candidate.EntranceDistance,
                Clear = new bool[grid.CellCount],
                Doorways = ProjectDoorways(candidate.Tile, grid),
                IsRoom = candidate.IsRoom,
            };
            for (int index = 0; index < grid.CellCount; index++)
                scan.Clear[index] = grid.States[index] == TileClearanceCellState.Clear;

            scan.BestFit = BreachContainerFitMath.FindBestFit(
                scan.Clear,
                grid.CellsX,
                grid.CellsZ,
                widthCells,
                lengthCells,
                stripCells,
                TileClearanceGrid.CellSizeMeters,
                scan.Doorways,
                routeClearanceCells);
            _scans.Add(scan);
        }

        /// <summary>
        /// #1155: the room pass is over and nothing fits in any of its tiles, so the same scan
        /// continues into this interior's transit tiles under the same tile cap and the same
        /// per-call budget — a cave interior whose only "room" is a Y tunnel (Level3Flow) then
        /// places its container in a tunnel instead of placing nothing at all. False when the
        /// set is already widened, when there is no transit tile to add, or when a room fit
        /// still exists.
        /// </summary>
        private static bool TryWidenToTransitTiles()
        {
            if (_widenedToTransit || _roomCandidateCount <= 0 || _roomCandidateCount >= _scanCandidates.Count)
                return false;

            for (int i = 0; i < _scans.Count; i++)
            {
                if (_scans[i].BestFit.HasFit)
                    return false;
            }

            _widenedToTransit = true;
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.ShadowRaid] No container fits any of this interior's {_roomCandidateCount} room tile(s); " +
                $"widening the procedural search to {_scanCandidates.Count - _roomCandidateCount} transit tile(s) for the rest of the round.");
            return true;
        }

        /// <summary>
        /// Every scannable tile of this interior, sorted rooms first (#686). The room prefix
        /// length comes back in <paramref name="roomCount"/>: the scan spends its cap on that
        /// prefix and only reaches past it through <see cref="TryWidenToTransitTiles"/>.
        /// </summary>
        private static List<TileCandidate> CollectCandidateTiles(Dungeon dungeon, out int roomCount)
        {
            List<Vector3> entrances = InteriorAnchorService.GetInteriorEntrancePositions();
            float strictMinArea = InteriorPlacementService.MinAreaForRole(InteriorPlacementRole.BreachRoomProp, true);
            float relaxedMinArea = InteriorPlacementService.MinAreaForRole(InteriorPlacementRole.BreachRoomProp, false);

            var candidates = new List<TileCandidate>(dungeon.AllTiles.Count);
            for (int i = 0; i < dungeon.AllTiles.Count; i++)
            {
                Tile tile = dungeon.AllTiles[i];
                if (tile == null || InteriorPlacementService.IsTileExcludedFromPlacement(tile))
                    continue;
                if (PlacementValidator.IsEntranceTile(tile, entrances))
                    continue;
                // Area and identity first, reachability second: both are cheap reads off the
                // tile, while the reachability verdict can cost a NavMesh path query on a cold
                // cache. Most tiles fail the cheap tests, so pay them first.
                float area = InteriorAnchorService.GetTileArea(tile);
                if (area < relaxedMinArea)
                    continue;

                // #686: a real room (by tile identity, with the bounds rule as fallback) is the
                // strict candidate; a corridor, junction, stair, or catwalk is only scanned when
                // the interior has no room, or when no room can hold a container (#1155), and it
                // never outranks a room that fits.
                TileRoomVerdict verdict = TileRoomClassifierRuntime.Classify(tile, strictShape: true);
                bool isRoom = verdict.IsRoom;
                bool strictQualified = isRoom && area >= strictMinArea;

                if (!InteriorAnchorService.IsTileReachableFromEntrance(tile, entrances))
                    continue;

                candidates.Add(new TileCandidate(
                    tile,
                    InteriorAnchorService.EstimateTileEntrancePathDistance(tile, entrances),
                    strictQualified,
                    isRoom));
            }

            candidates.Sort(CompareCandidates);

            // Rooms sort first, so the room candidates are a prefix of the list. The scan runs
            // that prefix alone (#686: the tile cap is spent on rooms) and releases the rest
            // only once the prefix has proven it cannot hold a container (#1155).
            roomCount = 0;
            while (roomCount < candidates.Count && candidates[roomCount].IsRoom)
                roomCount++;

            if (roomCount == 0 && candidates.Count > 0)
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.ShadowRaid] No room tile in this interior; the procedural container search will consider {candidates.Count} transit tile(s).");

            return candidates;
        }

        private static BreachContainerPlacement PlaceOneContainer(
            List<TileScan> scans,
            int placementIndex,
            int widthCells,
            int[] lengthCells,
            int stripCells,
            int routeClearanceCells,
            float width,
            float height,
            float mediumLength,
            float smallLength,
            float stripMeters)
        {
            for (int attempt = 0; attempt <= MaxReservationRetries; attempt++)
            {
                // #686's room-first preference, kept explicit now that a widened scan (#1155)
                // can hold transit tiles too: a transit fit is taken only when no scanned room
                // tile has one left.
                TileScan bestScan = SelectBestScan(scans, roomsOnly: true) ?? SelectBestScan(scans, roomsOnly: false);
                if (bestScan == null)
                    return null;

                BreachContainerFitCandidate fit = bestScan.BestFit;
                TileClearanceGrid grid = bestScan.Grid;
                string sizeName = ProceduralSizeNamesPreferredFirst[fit.SizeIndex];
                float lengthMeters = fit.SizeIndex == 0 ? mediumLength : smallLength;
                float floorY = grid.DominantFloorY;

                Vector3 forward = (grid.Right * fit.ForwardI + grid.Forward * fit.ForwardJ).normalized;
                Vector3 facePoint = grid.GridPoint(fit.FaceCenterI, fit.FaceCenterJ);
                Vector3 origin = new Vector3(facePoint.x, floorY, facePoint.z);
                Vector3 bodyCenterPoint = grid.GridPoint(fit.BodyCenterI, fit.BodyCenterJ);
                Vector3 bodyCenter = new Vector3(bodyCenterPoint.x, floorY, bodyCenterPoint.z);
                Vector3 stripCenter = origin + forward * (stripMeters * 0.5f);
                Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);

                var requests = new[]
                {
                    new FixtureReservationRequest(
                        $"{ReservationLabel}-{placementIndex}",
                        ReservationSource,
                        CreateVolumeFootprint(width, height, lengthMeters),
                        bodyCenter,
                        rotation),
                    new FixtureReservationRequest(
                        $"{ReservationLabel}-{placementIndex}-drill",
                        ReservationSource,
                        CreateVolumeFootprint(width, DrillStripHeightMeters, stripMeters),
                        stripCenter,
                        rotation),
                };

                // Burn the winning rectangle either way: on success the next container must not
                // re-propose it, and on conflict the retry must move somewhere new.
                BurnRect(bestScan, fit);
                bestScan.BestFit = BreachContainerFitMath.FindBestFit(
                    bestScan.Clear,
                    grid.CellsX,
                    grid.CellsZ,
                    widthCells,
                    lengthCells,
                    stripCells,
                    TileClearanceGrid.CellSizeMeters,
                    bestScan.Doorways,
                    routeClearanceCells);

                if (!FixtureReservationRegistry.TryReserveGroup(
                        requests,
                        out FixtureReservationGroup group,
                        out FixtureReservationConflict conflict))
                {
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.ShadowRaid] Procedural container candidate in '{grid.Tile?.name}' " +
                        $"lost to an existing claim ({conflict.ToDiagnosticString()}); retrying elsewhere.");
                    continue;
                }

                return new BreachContainerPlacement
                {
                    Tile = grid.Tile,
                    TileName = grid.Tile != null ? grid.Tile.name : "<null>",
                    Position = origin,
                    Rotation = rotation,
                    SizeName = sizeName,
                    Score = fit.Score,
                    EntrancePathDistance = bestScan.EntranceDistance,
                    RouteClearanceMeters = fit.RouteClearanceCells * TileClearanceGrid.CellSizeMeters,
                    ReservationGroup = group,
                    IsReserved = true,
                };
            }

            return null;
        }

        /// <summary>
        /// Highest-scoring scan that still has a fit, after the fixture-crowding demotion;
        /// <paramref name="roomsOnly"/> restricts the choice to room tiles.
        /// </summary>
        private static TileScan SelectBestScan(List<TileScan> scans, bool roomsOnly)
        {
            TileScan bestScan = null;
            float bestEffectiveScore = 0f;
            for (int i = 0; i < scans.Count; i++)
            {
                TileScan scan = scans[i];
                if (!scan.BestFit.HasFit || (roomsOnly && !scan.IsRoom))
                    continue;
                float effectiveScore = scan.BestFit.Score - FixtureCrowdingPenalty(scan);
                if (bestScan == null || effectiveScore > bestEffectiveScore)
                {
                    bestScan = scan;
                    bestEffectiveScore = effectiveScore;
                }
            }

            return bestScan;
        }

        /// <summary>
        /// Shortfall-proportional demotion for a fit crowding an existing fixture claim; 0 when
        /// the fit's body center clears the shared separation radius (or nothing is reserved).
        /// </summary>
        private static float FixtureCrowdingPenalty(TileScan scan)
        {
            BreachContainerFitCandidate fit = scan.BestFit;
            TileClearanceGrid grid = scan.Grid;
            Vector3 bodyPoint = grid.GridPoint(fit.BodyCenterI, fit.BodyCenterJ);
            var bodyCenter = new Vector3(bodyPoint.x, grid.DominantFloorY, bodyPoint.z);
            float nearestFixture = FixtureReservationRegistry.GetNearestReservationCenterDistance(bodyCenter);
            if (nearestFixture < 0f || nearestFixture >= InteriorPlacementRequest.DefaultMinSeparationFromOtherFixturesMeters)
                return 0f;
            return FixtureCrowdingPenaltyPerMeter
                   * (InteriorPlacementRequest.DefaultMinSeparationFromOtherFixturesMeters - nearestFixture);
        }

        /// <summary>Bottom-anchored volume for the reservation registry; local +Z is the drill face.</summary>
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

        private static void BurnRect(TileScan scan, BreachContainerFitCandidate fit)
        {
            int cellsX = scan.Grid.CellsX;
            for (int j = fit.RectOriginJ; j < fit.RectOriginJ + fit.RectHeight; j++)
            {
                for (int i = fit.RectOriginI; i < fit.RectOriginI + fit.RectWidth; i++)
                    scan.Clear[j * cellsX + i] = false;
            }
        }

        private static List<BreachContainerDoorwayCell> ProjectDoorways(Tile tile, TileClearanceGrid grid)
        {
            var doorways = new List<BreachContainerDoorwayCell>();
            if (tile?.UsedDoorways == null)
                return doorways;

            for (int i = 0; i < tile.UsedDoorways.Count; i++)
            {
                Doorway doorway = tile.UsedDoorways[i];
                if (doorway == null || doorway.transform == null)
                    continue;

                Vector3 offset = doorway.transform.position - grid.Pivot;
                doorways.Add(new BreachContainerDoorwayCell(
                    Vector3.Dot(offset, grid.Right) / TileClearanceGrid.CellSizeMeters,
                    Vector3.Dot(offset, grid.Forward) / TileClearanceGrid.CellSizeMeters));
            }

            return doorways;
        }

        private static int CompareCandidates(TileCandidate a, TileCandidate b)
        {
            if (a.IsRoom != b.IsRoom)
                return a.IsRoom ? -1 : 1;
            if (a.StrictQualified != b.StrictQualified)
                return a.StrictQualified ? -1 : 1;

            int byDistance = a.EntranceDistance.CompareTo(b.EntranceDistance);
            if (byDistance != 0)
                return byDistance;

            return string.CompareOrdinal(a.Tile != null ? a.Tile.name : string.Empty, b.Tile != null ? b.Tile.name : string.Empty);
        }

        private readonly struct TileCandidate
        {
            internal readonly Tile Tile;
            internal readonly float EntranceDistance;
            internal readonly bool StrictQualified;

            /// <summary>#686/#1155: identity verdict from <see cref="TileRoomClassifierRuntime"/>; rooms sort first and transit tiles stay at the tail of the list, which the scan enters only when the room prefix produced no fit.</summary>
            internal readonly bool IsRoom;

            internal TileCandidate(Tile tile, float entranceDistance, bool strictQualified, bool isRoom)
            {
                Tile = tile;
                EntranceDistance = entranceDistance;
                StrictQualified = strictQualified;
                IsRoom = isRoom;
            }
        }

        private sealed class TileScan
        {
            internal TileClearanceGrid Grid;
            internal float EntranceDistance;

            /// <summary>#686/#1155: room tiles win the pick outright; transit scans exist only in a widened scan.</summary>
            internal bool IsRoom;

            internal bool[] Clear;
            internal List<BreachContainerDoorwayCell> Doorways;
            internal BreachContainerFitCandidate BestFit;
        }
    }
}
