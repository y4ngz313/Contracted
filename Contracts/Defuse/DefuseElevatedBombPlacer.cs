using System.Collections;
using System.Collections.Generic;
using DunGen;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Facility.Interior.Placement;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Y4NGZCompany.Contracts.Defuse
{
    /// <summary>
    /// Host-side elevated placement tier for the Defuse bomb: rests the bomb on a stable prop
    /// top (crate, desk, shelf — anything inert in the 0.65–1.30 m band) inside a proper room
    /// tile. Sits between the authored tier and the floor fallbacks in
    /// <c>DefuseContract.TryResolveSpawnPose</c>; when no acceptable surface exists the floor
    /// ladder proceeds untouched.
    ///
    /// The scan reuses the shared <see cref="TileClearanceGrid"/>: cells the grid rejects for
    /// <see cref="TileClearanceCellState.FloorPlane"/> (the down-ray landed on a prop top, off
    /// the dominant floor plane) or <see cref="TileClearanceCellState.Clutter"/> (something
    /// occupies the floor-to-band volume beside the ray) are exactly the cells that may hold a
    /// candidate prop. Each is probed with a center down-cast plus four corner rays across the
    /// bomb footprint, and the pure <see cref="DefuseElevatedFitMath"/> decides stability and
    /// picks the most room-centered surface, mirroring the Blacksite container search.
    /// </summary>
    internal static class DefuseElevatedBombPlacer
    {
        /// <summary>Half the container placer's envelope; one bomb needs far fewer rooms.</summary>
        private const int MaxTilesScanned = 12;

        /// <summary>
        /// Wall-clock cap for one resolution call. The scan runs on the live gameplay path from
        /// DefuseContract's spawn attempt, so a pathological interior must not drain dozens of
        /// 64x64 grids in a single frame; the scan pauses here (even mid-grid) and the next
        /// spawn tick resumes it exactly where it stopped, the way BreachContainerPlacer does.
        /// </summary>
        private const double MaxScanMillisecondsPerCall = 8.0;

        /// <summary>
        /// Safety valve across the whole resumable scan. However large the interior, the bomb
        /// must never be delayed indefinitely waiting for the elevated tier: once the scan has
        /// been in flight this long the verdict is forced complete and the floor ladder runs.
        /// </summary>
        private const double MaxScanWallClockSeconds = 10.0;

        // The bomb root collider floors at roughly 1.2 x 0.8 m
        // (DefuseContract.ConfigureBoundsCollider's fallback box); the corner rays and the
        // reservation volume use the same extents.
        private const float BombFootprintWidthMeters = 1.2f;
        private const float BombFootprintDepthMeters = 0.8f;
        private const float BombReservedHeightMeters = 0.8f;

        /// <summary>Corner rays sit slightly inside the footprint so a flush prop edge still passes.</summary>
        private const float CornerInsetMeters = 0.05f;

        /// <summary>Down-cast origin height above the tile's dominant floor plane.</summary>
        private const float SurfaceProbeHeightMeters = 2.0f;

        /// <summary>The volume above the surface must be open this far up — bomb plus a standing player's reach.</summary>
        private const float ClearAboveMeters = 1.6f;

        /// <summary>A NavMesh point must exist within this radius of the surface's approach edge.</summary>
        private const float NavReachRadiusMeters = 1.2f;

        /// <summary>Rooms shorter than this are too cramped to stand at an elevated bomb.</summary>
        private const float MinRoomCeilingMeters = 3.0f;

        internal const string ReservationLabel = "defuse-bomb";
        internal const string ReservationSource = "defuse-elevated";

        // --- resumable scan state -------------------------------------------------
        // One elevated search may span several of DefuseContract's spawn ticks; the per-call
        // budget pauses the scan (even mid-grid) and the next call resumes it. Without this the
        // budget abandoned the whole tier after two or three rooms on a large interior, so the
        // bomb hit the floor ladder because the search ran out of time, not because no prop top
        // existed.
        private static Dungeon _scanDungeon;
        private static List<TileCandidate> _scanCandidates;
        private static int _nextCandidateIndex;
        private static int _tilesExamined;
        private static IEnumerator _activeBuild;
        private static TileClearanceGrid _activeGrid;
        private static Stopwatch _scanFrame;
        private static Stopwatch _scanWallClock;

        // #716 (WP-A4): the surface probe over a finished grid used to run unbudgeted after the
        // budgeted build, so one 64x64 tile could spend five center down-casts plus four corner
        // rays per candidate cell in a single frame. The probe now carries its own cursor and
        // yields against the same per-call clock as the build.
        private static TileClearanceGrid _placeGrid;
        private static Tile _placeTile;
        private static int _placeCellIndex;
        private static List<DefuseElevatedSurfaceSample> _placeSamples;
        private static List<SurfacePose> _placePoses;

        /// <summary>Drops any partially completed scan; a later search starts fresh.</summary>
        internal static void ResetScanState()
        {
            _scanDungeon = null;
            _scanCandidates = null;
            _nextCandidateIndex = 0;
            _tilesExamined = 0;
            _activeBuild = null;
            _activeGrid = null;
            _scanFrame = null;
            _scanWallClock = null;
            ClearPlacementState();
        }

        /// <summary>Drops the in-flight surface probe for one tile, keeping the outer scan.</summary>
        private static void ClearPlacementState()
        {
            _placeGrid = null;
            _placeTile = null;
            _placeCellIndex = 0;
            _placeSamples = null;
            _placePoses = null;
        }

        /// <summary>
        /// Budgeted resolution for the Defuse spawn path, resumable across the contract's spawn
        /// ticks (the BreachContainerPlacer.TryResolveBudgeted pattern, expressed as retained
        /// state because the caller is a plain host-side method rather than a coroutine).
        /// Tri-state for the caller:
        /// <list type="bullet">
        /// <item>true — a pose was found; the bomb footprint is already reserved in the
        /// <see cref="FixtureReservationRegistry"/> and <paramref name="position"/> sits on the
        /// surface plus the shared vertical offset.</item>
        /// <item>false with <paramref name="scanComplete"/> false — the per-call budget paused
        /// the scan; call again next tick, and do NOT fall through to the floor ladder.</item>
        /// <item>false with <paramref name="scanComplete"/> true — every candidate room was
        /// examined (or the <see cref="MaxScanWallClockSeconds"/> safety valve fired) and no
        /// surface exists; the floor ladder owns the pose.</item>
        /// </list>
        /// </summary>
        internal static bool TryResolveElevated(out Vector3 position, out Quaternion rotation, out string tileName, out bool scanComplete)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            tileName = "<none>";
            scanComplete = true;

            // Both early guards can fire while a scan is paused (the balance object is rebuilt on
            // a config reload, and the dungeon reference goes null between rounds), so they drop
            // the retained state rather than abandoning it with a wall clock still running.
            if (ContractsBootstrap.DefuseBalance == null || !ContractsBootstrap.DefuseBalance.ElevatedBombPlacement)
            {
                ResetScanState();
                return false;
            }

            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null || dungeon.AllTiles.Count == 0)
            {
                ResetScanState();
                return false;
            }

            float minHeight = DefuseBalanceConfig.ElevatedMinHeightMeters;
            float maxHeight = DefuseBalanceConfig.ElevatedMaxHeightMeters;

            var gridSettings = new TileClearanceGridSettings
            {
                MinCeilingMeters = MinRoomCeilingMeters,
                // Anything occupying the floor-to-band volume marks its cell Clutter, which
                // is exactly the "there might be a prop here" signal this tier probes.
                ClutterProbeHeightMeters = maxHeight,
            };

            // Unity's overloaded != is deliberate: a destroyed dungeon compares equal to null but
            // is not reference-null, and ReferenceEquals would happily resume a scan against it.
            if (_scanDungeon != dungeon || _scanCandidates == null)
            {
                ResetScanState();
                _scanDungeon = dungeon;
                _scanCandidates = CollectCandidateTiles(dungeon);
                _scanFrame = Stopwatch.StartNew();
                _scanWallClock = Stopwatch.StartNew();
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Defuse] Elevated bomb scan started over {_scanCandidates.Count} candidate room(s).");
            }

            // The grid build yields against _scanFrame roughly every FrameBudgetMilliseconds of
            // physics; restarting it here keeps stale elapsed time from a previous call out of
            // this call's slices.
            _scanFrame.Restart();
            var callClock = Stopwatch.StartNew();
            bool timedOut = _scanWallClock.Elapsed.TotalSeconds >= MaxScanWallClockSeconds;
            while (!timedOut)
            {
                // Build phase, skipped entirely while a surface probe is still in flight for the
                // tile whose grid finished on an earlier call.
                if (_placeGrid == null)
                {
                    if (_activeBuild == null)
                    {
                        if (_nextCandidateIndex >= _scanCandidates.Count || _tilesExamined >= MaxTilesScanned)
                            break;

                        // A tile destroyed since the list was collected compares equal to null under
                        // Unity's operator; feeding it to Build would throw every call and stall the
                        // scan on the same index forever. Advance past it instead - it burned nothing,
                        // so it does not count toward the scan cap.
                        if (_scanCandidates[_nextCandidateIndex].Tile == null)
                        {
                            _nextCandidateIndex++;
                            continue;
                        }

                        // Every examined tile counts toward the cap — a grid that dies on CellCount
                        // or the floor-plane vote still burned its share of raycasts.
                        _tilesExamined++;
                        _activeGrid = new TileClearanceGrid();
                        _activeBuild = TileClearanceGrid.Build(_scanCandidates[_nextCandidateIndex].Tile, gridSettings, true, _scanFrame, _activeGrid);
                    }

                    bool budgetSpent = false;
                    while (true)
                    {
                        // The budgeted build hands back control every ~3ms of physics; the half-built
                        // grid survives in _activeBuild/_activeGrid so no raycast is ever wasted.
                        if (callClock.Elapsed.TotalMilliseconds >= MaxScanMillisecondsPerCall)
                        {
                            budgetSpent = true;
                            break;
                        }

                        if (!_activeBuild.MoveNext())
                            break;
                    }

                    if (budgetSpent)
                    {
                        if (_scanWallClock.Elapsed.TotalSeconds >= MaxScanWallClockSeconds)
                        {
                            timedOut = true;
                            break;
                        }

                        Tile paused = _scanCandidates[_nextCandidateIndex].Tile;
                        ContractsBootstrap.Log?.LogInfo(
                            $"[MoonContracts.Defuse] Elevated bomb scan spent its {MaxScanMillisecondsPerCall:0}ms budget " +
                            $"mid-grid in '{(paused != null ? paused.name : "<null>")}' after {_tilesExamined} room(s); resuming next tick.");
                        scanComplete = false;
                        return false;
                    }

                    Tile built = _scanCandidates[_nextCandidateIndex].Tile;
                    TileClearanceGrid builtGrid = _activeGrid;
                    _nextCandidateIndex++;
                    _activeBuild = null;
                    _activeGrid = null;

                    if (builtGrid.CellCount <= 0 || !builtGrid.HasFloorPlane)
                        continue;

                    _placeGrid = builtGrid;
                    _placeTile = built;
                    _placeCellIndex = 0;
                    _placeSamples = new List<DefuseElevatedSurfaceSample>();
                    _placePoses = new List<SurfacePose>();
                }

                // Probe phase, charged against the same per-call clock as the build.
                if (!ProbeSurfacesBudgeted(callClock, minHeight, maxHeight))
                {
                    if (_scanWallClock.Elapsed.TotalSeconds >= MaxScanWallClockSeconds)
                    {
                        timedOut = true;
                        break;
                    }

                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.Defuse] Elevated bomb scan spent its {MaxScanMillisecondsPerCall:0}ms budget " +
                        $"mid-probe in '{(_placeTile != null ? _placeTile.name : "<null>")}' after {_tilesExamined} room(s); resuming next tick.");
                    scanComplete = false;
                    return false;
                }

                Tile tile = _placeTile;
                TileClearanceGrid grid = _placeGrid;
                bool placed = TryClaimBestSurface(grid, minHeight, maxHeight, out position, out rotation);
                ClearPlacementState();
                if (!placed)
                    continue;

                tileName = tile != null ? tile.name : "<null>";
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Defuse] Elevated bomb surface in tile='{tileName}' " +
                    $"pos=({position.x:0.0},{position.y:0.0},{position.z:0.0}) " +
                    $"height={position.y - grid.DominantFloorY:0.00}m yaw={rotation.eulerAngles.y:0.0}.");
                // The scan is consumed on success; a later search starts fresh against the
                // then-current reservations.
                ResetScanState();
                return true;
            }

            if (timedOut)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Defuse] Elevated bomb scan hit its {MaxScanWallClockSeconds:0}s safety valve after " +
                    $"{_tilesExamined} room(s); using the floor ladder.");
            }
            else
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Defuse] No elevated bomb surface across {_tilesExamined} scanned room(s); using the floor ladder.");
            }

            ResetScanState();
            return false;
        }

        /// <summary>
        /// The candidate room list for one dungeon, collected once per scan and then walked
        /// across resumed calls. #550: a sealed tile carries its own NavMesh island, so the
        /// tile-level reachability verdict has to gate the list itself.
        /// </summary>
        private static List<TileCandidate> CollectCandidateTiles(Dungeon dungeon)
        {
            List<Vector3> entrances = InteriorAnchorService.GetInteriorEntrancePositions();

            var candidates = new List<TileCandidate>(dungeon.AllTiles.Count);
            for (int i = 0; i < dungeon.AllTiles.Count; i++)
            {
                Tile tile = dungeon.AllTiles[i];
                if (tile == null || InteriorPlacementService.IsTileExcludedFromPlacement(tile))
                    continue;
                if (PlacementValidator.IsEntranceTile(tile, entrances))
                    continue;

                // Identity first, reachability second: the room verdict is a cached read off
                // the tile, while the reachability verdict can cost a NavMesh path query on a
                // cold cache. #686: a corridor, junction, stair, or catwalk is never an elevated
                // bomb room while the interior has a real room; it is kept only as the fallback
                // list for a room-less interior.
                bool isRoom = TileRoomClassifierRuntime.IsRoom(tile, strictShape: true);
                if (!InteriorAnchorService.IsTileReachableFromEntrance(tile, entrances))
                    continue;

                // Engine A's fixture-separation rule as a ring demotion: rooms whose center sits
                // inside the shared separation radius of an existing registry claim (mainframe,
                // stash, incinerator) sort after every uncrowded room, so the bomb prefers space
                // of its own. The final surface position must also pass the 25 m gate.
                float nearestFixture = FixtureReservationRegistry.GetNearestReservationCenterDistance(tile.Bounds.center);
                bool crowdedByFixture = nearestFixture >= 0f
                    && nearestFixture < InteriorPlacementRequest.DefaultMinSeparationFromOtherFixturesMeters;

                candidates.Add(new TileCandidate(
                    tile,
                    InteriorAnchorService.EstimateTileEntrancePathDistance(tile, entrances),
                    crowdedByFixture,
                    isRoom));
            }

            bool anyRoom = false;
            for (int i = 0; i < candidates.Count && !anyRoom; i++)
                anyRoom = candidates[i].IsRoom;
            if (anyRoom)
                candidates.RemoveAll(candidate => !candidate.IsRoom);
            else if (candidates.Count > 0)
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Defuse] No room tile in this interior; the elevated bomb scan will consider {candidates.Count} transit tile(s).");

            candidates.Sort(CompareCandidates);
            return candidates;
        }

        /// <summary>
        /// Rolls the elevated tier's footprint claim back when the spawn that consumed the
        /// pose fails downstream (the BreachContainerPlacer.ReleaseReservation pattern; the
        /// label alone suffices here because this tier holds at most one claim at a time).
        /// Without this, every failed spawn attempt would leak one reservation.
        /// </summary>
        internal static void ReleaseReservation()
        {
            FixtureReservationRegistry.Release(ReservationLabel);
        }

        /// <summary>
        /// Probes the prop-candidate cells of the tile held in <see cref="_placeGrid"/>, resuming
        /// from <see cref="_placeCellIndex"/> and pausing when the shared per-call budget is
        /// spent. Returns true when the tile is fully probed, false when it paused mid-tile.
        /// </summary>
        private static bool ProbeSurfacesBudgeted(Stopwatch callClock, float minHeight, float maxHeight)
        {
            TileClearanceGrid grid = _placeGrid;
            if (grid == null)
                return true;

            StartOfRound start = StartOfRound.Instance;
            int mask = start != null ? start.collidersAndRoomMaskAndDefault : ~0;

            int cellCount = grid.States != null
                ? Mathf.Min(grid.CellsX * grid.CellsZ, grid.States.Length)
                : 0;
            while (_placeCellIndex < cellCount)
            {
                // Each probe is a center down-cast plus up to four corner rays, so the budget is
                // checked per cell rather than per row; the cursor survives the pause.
                if (callClock.Elapsed.TotalMilliseconds >= MaxScanMillisecondsPerCall)
                    return false;

                int index = _placeCellIndex++;
                int i = index % grid.CellsX;
                int j = index / grid.CellsX;

                TileClearanceCellState state = grid.States[index];
                if (state != TileClearanceCellState.FloorPlane && state != TileClearanceCellState.Clutter)
                    continue;

                if (!TryProbeCell(grid, i, j, mask, minHeight, maxHeight, out DefuseElevatedSurfaceSample sample, out SurfacePose pose))
                    continue;

                _placeSamples.Add(sample);
                _placePoses.Add(pose);
            }

            return true;
        }

        /// <summary>
        /// Ranks the surfaces the budgeted probe collected for one tile and claims the best one
        /// that clears the reservation registry. Pure arithmetic plus registry calls; no rays.
        /// </summary>
        private static bool TryClaimBestSurface(
            TileClearanceGrid grid,
            float minHeight,
            float maxHeight,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            List<DefuseElevatedSurfaceSample> samples = _placeSamples;
            List<SurfacePose> poses = _placePoses;
            if (grid == null || samples == null || poses == null)
                return false;

            // Rank with the pure selector, then walk down the ranking whenever a winning
            // surface loses to an existing reservation — the same burn-and-retry idea the
            // container placer uses, expressed by removing the burned sample.
            while (samples.Count > 0)
            {
                int best = DefuseElevatedFitMath.SelectBest(
                    samples,
                    minHeight,
                    maxHeight,
                    grid.CellsX * 0.5f,
                    grid.CellsZ * 0.5f,
                    TileClearanceGrid.CellSizeMeters);
                if (best < 0)
                    return false;

                SurfacePose winner = poses[best];
                if (!DefusePlacementPolicy.IsSeparated(FixtureReservationRegistry.GetNearestReservationCenterDistance(winner.SurfacePoint)))
                {
                    samples.RemoveAt(best);
                    poses.RemoveAt(best);
                    continue;
                }
                if (!FixtureReservationRegistry.TryReserve(
                        ReservationLabel,
                        ReservationSource,
                        CreateBombFootprint(),
                        winner.SurfacePoint,
                        winner.Rotation,
                        out _,
                        out FixtureReservationConflict conflict))
                {
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.Defuse] Elevated bomb surface in '{grid.Tile?.name}' lost to an existing claim " +
                        $"({conflict.ToDiagnosticString()}); trying the next surface.");
                    samples.RemoveAt(best);
                    poses.RemoveAt(best);
                    continue;
                }

                position = winner.SurfacePoint + Vector3.up * DefuseBalanceConfig.BombVerticalOffsetMeters;
                rotation = winner.Rotation;
                return true;
            }

            return false;
        }

        /// <summary>
        /// One cell's full probe: center down-cast into the height band, inert-surface
        /// classification, footprint corner rays, floor-level reachability beside the
        /// approach edge, and headroom above the surface. Fills the pure-math sample (the
        /// geometry verdict is made there) and the world pose used if the sample wins.
        /// </summary>
        private static bool TryProbeCell(
            TileClearanceGrid grid,
            int i,
            int j,
            int mask,
            float minHeight,
            float maxHeight,
            out DefuseElevatedSurfaceSample sample,
            out SurfacePose pose)
        {
            sample = default;
            pose = default;

            Vector3 center = grid.CellCenter(i, j);
            Vector3 probeOrigin = new Vector3(center.x, grid.DominantFloorY + SurfaceProbeHeightMeters, center.z);
            InteriorAnchorService.RecordPhysicsQuery();
            if (!Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit hit, SurfaceProbeHeightMeters + 0.1f, mask, QueryTriggerInteraction.Ignore))
                return false;

            float height = hit.point.y - grid.DominantFloorY;
            if (!DefuseElevatedFitMath.IsWithinHeightBand(height, minHeight, maxHeight))
                return false;
            if (!DefuseElevatedFitMath.IsLevelEnough(Vector3.Dot(hit.normal, Vector3.up)))
                return false;
            if (!IsInertSupportSurface(hit.collider))
                return false;

            Vector3 forward = ResolveFacing(grid, i, j);
            Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);
            Vector3 surfacePoint = hit.point;

            float halfWidth = BombFootprintWidthMeters * 0.5f - CornerInsetMeters;
            float halfDepth = BombFootprintDepthMeters * 0.5f - CornerInsetMeters;
            var cornerHeights = new float[4];
            for (int corner = 0; corner < 4; corner++)
            {
                float x = (corner & 1) == 0 ? -halfWidth : halfWidth;
                float z = (corner & 2) == 0 ? -halfDepth : halfDepth;
                Vector3 cornerPoint = surfacePoint + rotation * new Vector3(x, 0f, z);
                Vector3 cornerOrigin = new Vector3(cornerPoint.x, grid.DominantFloorY + SurfaceProbeHeightMeters, cornerPoint.z);
                InteriorAnchorService.RecordPhysicsQuery();
                if (!Physics.Raycast(cornerOrigin, Vector3.down, out RaycastHit cornerHit, SurfaceProbeHeightMeters + 0.1f, mask, QueryTriggerInteraction.Ignore))
                    return false;

                cornerHeights[corner] = cornerHit.point.y - grid.DominantFloorY;
            }

            sample = new DefuseElevatedSurfaceSample
            {
                CellI = i,
                CellJ = j,
                HeightAboveFloor = height,
                NormalDotUp = Vector3.Dot(hit.normal, Vector3.up),
                CornerHeights = cornerHeights,
            };
            if (!DefuseElevatedFitMath.IsSurfaceAcceptable(in sample, minHeight, maxHeight))
                return false;

            // The defuse interactors are child InteractTriggers — a player must be able to
            // stand on the floor beside the surface. Sample the NavMesh at the approach edge.
            Vector3 approachEdge = new Vector3(surfacePoint.x, grid.DominantFloorY, surfacePoint.z)
                                   + forward * (BombFootprintDepthMeters * 0.5f + 0.15f);
            if (!NavMesh.SamplePosition(approachEdge + Vector3.up * 0.1f, out _, NavReachRadiusMeters, NavMesh.AllAreas))
                return false;

            // Headroom: the volume from just above the surface to the player's standing
            // reach must be open, or the bomb ends up under a shelf nobody can work at.
            float clearHalfHeight = (ClearAboveMeters - 0.1f) * 0.5f;
            Vector3 clearCenter = surfacePoint + Vector3.up * (0.1f + clearHalfHeight);
            var clearHalfExtents = new Vector3(
                BombFootprintWidthMeters * 0.45f,
                clearHalfHeight,
                BombFootprintDepthMeters * 0.45f);
            InteriorAnchorService.RecordPhysicsQuery();
            if (Physics.CheckBox(clearCenter, clearHalfExtents, rotation, mask, QueryTriggerInteraction.Ignore))
                return false;

            pose = new SurfacePose
            {
                SurfacePoint = surfacePoint,
                Rotation = rotation,
            };
            return true;
        }

        /// <summary>
        /// Keypad-side yaw: the pure math picks the open (clear-floor) grid side best aligned
        /// with the room center; when no side is open or the choice is ambiguous the bomb
        /// faces the tile center directly.
        /// </summary>
        private static Vector3 ResolveFacing(TileClearanceGrid grid, int i, int j)
        {
            float toCenterI = grid.CellsX * 0.5f - (i + 0.5f);
            float toCenterJ = grid.CellsZ * 0.5f - (j + 0.5f);
            int direction = DefuseElevatedFitMath.SelectFacingDirection(
                grid.IsClear(i + 1, j),
                grid.IsClear(i - 1, j),
                grid.IsClear(i, j + 1),
                grid.IsClear(i, j - 1),
                toCenterI,
                toCenterJ);

            switch (direction)
            {
                case 0: return grid.Right;
                case 1: return -grid.Right;
                case 2: return grid.Forward;
                case 3: return -grid.Forward;
            }

            Vector3 toCenter = grid.Right * toCenterI + grid.Forward * toCenterJ;
            toCenter.y = 0f;
            return toCenter.sqrMagnitude > 1e-4f ? toCenter.normalized : grid.Forward;
        }

        /// <summary>
        /// The elevated tier's own surface classifier, deliberately the inverse split of
        /// <c>PlacementValidator.IsPropSupportSurface</c>: that helper treats every prop as a
        /// forbidden support because fixtures may only stand on room structure, while the bomb
        /// WANTS inert props — it must only avoid surfaces that move or are interactive
        /// (physics bodies, grabbables, doors, terminal objects, anything with an
        /// InteractTrigger in its parents).
        /// </summary>
        private static bool IsInertSupportSurface(Collider collider)
        {
            if (collider == null) return false;
            if (collider.attachedRigidbody != null) return false;
            if (collider.GetComponentInParent<GrabbableObject>() != null) return false;
            if (collider.GetComponentInParent<InteractTrigger>() != null) return false;
            if (collider.GetComponentInParent<DoorLock>() != null) return false;
            if (collider.GetComponentInParent<TerminalAccessibleObject>() != null) return false;
            return true;
        }

        /// <summary>Bottom-anchored bomb volume for the reservation registry; local +Z is the keypad face.</summary>
        internal static FixtureFootprint CreateBombFootprint()
        {
            return new FixtureFootprint(
                Vector3.forward,
                Vector3.back,
                Vector3.up,
                new Vector3(BombFootprintWidthMeters, BombReservedHeightMeters, BombFootprintDepthMeters),
                0f,
                0f,
                0.5f,
                1.0f);
        }

        private static int CompareCandidates(TileCandidate a, TileCandidate b)
        {
            if (a.CrowdedByFixture != b.CrowdedByFixture)
                return a.CrowdedByFixture ? 1 : -1;

            int byDistance = a.EntranceDistance.CompareTo(b.EntranceDistance);
            if (byDistance != 0)
                return byDistance;

            return string.CompareOrdinal(a.Tile != null ? a.Tile.name : string.Empty, b.Tile != null ? b.Tile.name : string.Empty);
        }

        private readonly struct TileCandidate
        {
            internal readonly Tile Tile;
            internal readonly float EntranceDistance;
            internal readonly bool CrowdedByFixture;

            /// <summary>#686: identity verdict from <see cref="TileRoomClassifierRuntime"/>; transit tiles are dropped whenever any room exists.</summary>
            internal readonly bool IsRoom;

            internal TileCandidate(Tile tile, float entranceDistance, bool crowdedByFixture, bool isRoom)
            {
                Tile = tile;
                EntranceDistance = entranceDistance;
                CrowdedByFixture = crowdedByFixture;
                IsRoom = isRoom;
            }
        }

        private struct SurfacePose
        {
            internal Vector3 SurfacePoint;
            internal Quaternion Rotation;
        }
    }
}
