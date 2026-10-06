using System;
using System.Collections;
using System.Collections.Generic;
using DunGen;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.Facility.Interior.Placement.Authored;

namespace Y4NGZCompany.Contracts.PestControl
{
    internal static partial class PestControlTrapPlacer
    {
        private const float LampMinimumClearanceMeters = 2.20f;

        /// <summary>
        /// Production resolver used by the eventual contract lifecycle. It waits for Mainframe,
        /// Stash, and CCTV placement, prefers a valid authored pose, then falls back to the
        /// time-sliced open-area probe. Diagnostic best-effort results are never returned here.
        /// </summary>
        internal static IEnumerator ResolveProductionBudgeted(
            Action<PestControlTrapPlacement> onComplete,
            bool reserveOnSuccess = true)
        {
            float waitStartedAt = Time.realtimeSinceStartup;
            float nextProgressLogSeconds = 0f;
            bool degradedReadiness = false;
            HostFacilityPlacementReadiness readiness = AuthoredPlacementRoundReport.HostFacilityPlacementState;

            while (true)
            {
                readiness = AuthoredPlacementRoundReport.HostFacilityPlacementState;
                StartOfRound start = StartOfRound.Instance;
                if (start == null || start.inShipPhase || start.shipIsLeaving)
                {
                    onComplete?.Invoke(null);
                    yield break;
                }

                float elapsedSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - waitStartedAt);
                AuthoredPlacementReadinessDisposition disposition =
                    AuthoredPlacementReadinessPolicy.Evaluate(
                        readiness,
                        elapsedSeconds,
                        nextProgressLogSeconds);

                if (disposition == AuthoredPlacementReadinessDisposition.Ready)
                    break;

                if (disposition == AuthoredPlacementReadinessDisposition.ProceedDegraded)
                {
                    degradedReadiness = true;
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.PestControl] PEST_READINESS_TIMEOUT elapsed={elapsedSeconds:0.0}s " +
                        $"{readiness.ToDiagnosticString()} recovery=reservation-protected-degraded-placement.");
                    break;
                }

                if (disposition == AuthoredPlacementReadinessDisposition.LogAndWait)
                {
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.PestControl] PEST_READINESS_WAIT elapsed={elapsedSeconds:0.0}s " +
                        $"{readiness.ToDiagnosticString()} timeout={AuthoredPlacementReadinessPolicy.TimeoutSeconds:0}s.");
                    nextProgressLogSeconds =
                        elapsedSeconds + AuthoredPlacementReadinessPolicy.ProgressLogIntervalSeconds;
                }

                yield return null;
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.PestControl] PEST_PLACEMENT_BEGIN readinessMode=" +
                $"{(degradedReadiness ? "degraded-timeout" : "ready")} {readiness.ToDiagnosticString()}.");

            if (TryResolveAuthoredPlacement(out PestControlTrapPlacement authored))
            {
                StampReadiness(authored, readiness, degradedReadiness);
                if (!reserveOnSuccess || TryReservePlacement(authored))
                {
                    LastResult = authored;
                    LogProductionSelection(authored);
                    onComplete?.Invoke(authored);
                    yield break;
                }
            }

            PestControlTrapPlacement procedural = null;
            IEnumerator probe = RunProbe(true, reserveOnSuccess: false, result => procedural = result);
            while (probe.MoveNext())
                yield return probe.Current;

            if (procedural != null)
                StampReadiness(procedural, readiness, degradedReadiness);

            string failure = string.Empty;
            bool accessoriesValid = procedural != null
                                    && procedural.HasResult
                                    && TryFinalizePlacementAccessories(procedural, out failure);
            bool reservationValid = !reserveOnSuccess;
            if (procedural != null
                && procedural.HasResult
                && procedural.MeetsMinimumFootprint
                && accessoriesValid
                && reserveOnSuccess)
            {
                reservationValid = TryReservePlacement(procedural);
            }

            if (procedural == null
                || !procedural.HasResult
                || !procedural.MeetsMinimumFootprint
                || !accessoriesValid
                || !reservationValid)
            {
                var reasons = procedural != null
                    ? procedural.GetProductionRejectionReasons(reserveOnSuccess)
                    : new List<string> { "no-placement-result" };
                if (!string.IsNullOrWhiteSpace(failure) && !reasons.Contains(failure))
                    reasons.Add(failure);
                string rejection = reasons.Count > 0
                    ? string.Join("; ", reasons)
                    : "production-validation-failed";

                if (procedural != null)
                    procedural.ReservationStatus = "production-rejected " + rejection;

                string source = procedural != null ? procedural.PlacementSource : "<none>";
                string tile = procedural != null ? procedural.TileName : "<none>";
                string coordinates = procedural != null && procedural.HasResult
                    ? $"({procedural.Center.x:0.00},{procedural.Center.y:0.00},{procedural.Center.z:0.00})"
                    : "<none>";
                string footprint = procedural != null && procedural.HasResult
                    ? procedural.FootprintMeters.ToString("0.00") + "m"
                    : "<none>";
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.PestControl] PEST_PLACEMENT_REJECTED source='{source}' tile='{tile}' " +
                    $"center={coordinates} footprint={footprint} readiness='{(procedural != null ? procedural.ReadinessStatus : readiness.ToDiagnosticString())}' " +
                    $"reasons=[{rejection}].");
                LastResult = procedural;
                onComplete?.Invoke(null);
                yield break;
            }

            procedural.PlacementSource = "procedural-open-area";
            LastResult = procedural;
            LogProductionSelection(procedural);
            onComplete?.Invoke(procedural);
        }

        private static bool TryResolveAuthoredPlacement(out PestControlTrapPlacement placement)
        {
            placement = null;
            if (!AuthoredInteriorPlacementStore.TryResolvePoses(
                    AuthoredInteriorPlacementKinds.PestTrap,
                    out List<AuthoredInteriorPlacementPose> poses)
                || poses == null)
            {
                return false;
            }

            for (int i = 0; i < poses.Count; i++)
            {
                AuthoredInteriorPlacementPose pose = poses[i];
                if (TryValidateAuthoredPose(pose, out placement, out List<string> reasons))
                    return true;

                AuthoredPlacementRoundReport.RecordValidationFailed(pose.Record, reasons);
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.PestControl] Authored pest-trap id='{pose.Record?.objectId ?? "<unknown>"}' " +
                    $"rejected: {string.Join("; ", reasons)}. Falling back.");
            }

            placement = null;
            return false;
        }

        private static bool TryValidateAuthoredPose(
            AuthoredInteriorPlacementPose pose,
            out PestControlTrapPlacement placement,
            out List<string> reasons)
        {
            reasons = new List<string>();
            placement = new PestControlTrapPlacement
            {
                PlacementSource = string.IsNullOrWhiteSpace(pose.Source) ? "authored" : pose.Source,
                AuthoredRecord = pose.Record,
                Tile = pose.Tile,
                TileName = pose.Tile != null ? pose.Tile.name : "<null>",
                Center = pose.WorldPosition,
                Yaw = pose.WorldRotation.eulerAngles.y,
                FootprintMeters = MinFootprintMeters,
                RingMeters = 0f,
                Score = float.MaxValue,
                MeetsMinimumFootprint = true,
                IsBestEffort = false
            };

            if (pose.Tile == null)
            {
                reasons.Add("resolved-tile-null");
                return false;
            }

            int cells = Mathf.CeilToInt(MinFootprintMeters / CellSizeMeters);
            Quaternion rotation = Quaternion.Euler(0f, placement.Yaw, 0f);
            Vector3 right = rotation * Vector3.right;
            Vector3 forward = rotation * Vector3.forward;
            Bounds bounds = pose.Tile.Bounds;
            StartOfRound start = StartOfRound.Instance;
            int mask = start != null ? start.collidersAndRoomMaskAndDefault : ~0;
            var floors = new List<Vector3>(cells * cells);
            var floorColliders = new List<Collider>(cells * cells);
            var ceilings = new List<float>(cells * cells);

            for (int z = 0; z < cells; z++)
            {
                for (int x = 0; x < cells; x++)
                {
                    float localX = (x + 0.5f) * CellSizeMeters - MinFootprintMeters * 0.5f;
                    float localZ = (z + 0.5f) * CellSizeMeters - MinFootprintMeters * 0.5f;
                    Vector3 probe = pose.WorldPosition + right * localX + forward * localZ;
                    placement.CellsSampled++;

                    if (probe.x < bounds.min.x || probe.x > bounds.max.x || probe.z < bounds.min.z || probe.z > bounds.max.z)
                    {
                        placement.Rejections.Add(PestControlCellRejection.OutsideTile);
                        reasons.Add("footprint-outside-tile");
                        return false;
                    }

                    InteriorAnchorService.RecordPhysicsQuery();
                    if (!Physics.Raycast(probe + Vector3.up * 5f, Vector3.down, out RaycastHit floorHit, 12f, mask, QueryTriggerInteraction.Ignore))
                    {
                        placement.Rejections.Add(PestControlCellRejection.NoFloor);
                        reasons.Add("missing-floor");
                        return false;
                    }

                    floors.Add(floorHit.point);
                    floorColliders.Add(floorHit.collider);
                }
            }

            floors.Sort((a, b) => a.y.CompareTo(b.y));
            float floorY = floors[floors.Count / 2].y;
            placement.Center = new Vector3(pose.WorldPosition.x, floorY, pose.WorldPosition.z);
            for (int i = 0; i < floors.Count; i++)
            {
                Vector3 floor = floors[i];
                if (Mathf.Abs(floor.y - floorY) > FloorPlaneToleranceMeters)
                {
                    placement.Rejections.Add(PestControlCellRejection.FloorPlane);
                    reasons.Add("uneven-floor-plane");
                    return false;
                }

                InteriorAnchorService.RecordPhysicsQuery();
                float clearance = CeilingProbeMeters;
                if (Physics.Raycast(floor + Vector3.up * 0.1f, Vector3.up, out RaycastHit ceilingHit, CeilingProbeMeters, mask, QueryTriggerInteraction.Ignore))
                    clearance = ceilingHit.distance + 0.1f;
                if (clearance < MinCeilingMeters)
                {
                    placement.Rejections.Add(PestControlCellRejection.Ceiling);
                    reasons.Add($"ceiling-below-{MinCeilingMeters:0.00}m");
                    return false;
                }

                if (HasClutter(floor, rotation, floorColliders[i], mask))
                {
                    placement.Rejections.Add(PestControlCellRejection.Clutter);
                    reasons.Add("footprint-cluttered");
                    return false;
                }

                if (!NavMesh.SamplePosition(floor + Vector3.up * 0.1f, out _, NavSampleRadiusMeters, NavMesh.AllAreas))
                {
                    placement.Rejections.Add(PestControlCellRejection.NavMesh);
                    reasons.Add("footprint-off-navmesh");
                    return false;
                }

                ceilings.Add(clearance);
                placement.CellsClear++;
            }

            ceilings.Sort();
            placement.CeilingClearanceMeters = ceilings[0];
            placement.MedianCeilingMeters = ceilings[ceilings.Count / 2];
            placement.HasResult = true;
            if (!TryFinalizePlacementAccessories(placement, out string accessoryFailure))
            {
                reasons.Add(accessoryFailure);
                placement.HasResult = false;
                return false;
            }

            return true;
        }

        private static bool TryFinalizePlacementAccessories(PestControlTrapPlacement placement, out string failure)
        {
            failure = string.Empty;
            if (placement != null)
                placement.AccessoryValidationFailure = string.Empty;

            if (placement == null || !placement.HasResult)
            {
                failure = "placement-missing";
                if (placement != null)
                    placement.AccessoryValidationFailure = failure;
                return false;
            }

            placement.LeverValidated = PestControlTrapAssembler.TryResolveLeverPose(
                placement.Center,
                placement.Yaw,
                placement.FootprintMeters,
                out placement.LeverPosition,
                out placement.LeverRotation);
            if (!placement.LeverValidated)
            {
                failure = "lever-floor-unavailable";
                placement.AccessoryValidationFailure = failure;
                return false;
            }

            placement.CeilingLampPositions.Clear();
            Quaternion rotation = Quaternion.Euler(0f, placement.Yaw, 0f);
            Vector3 right = rotation * Vector3.right;
            Vector3 forward = rotation * Vector3.forward;
            Vector3[] axes = { forward, -forward, right, -right };
            StartOfRound start = StartOfRound.Instance;
            int mask = start != null ? start.collidersAndRoomMaskAndDefault : ~0;

            // Lamps are a lighting accessory, not a requirement: they are reserved so nothing else
            // claims the volume, but no lamp geometry is built from these points. A single
            // unmountable lamp used to reject the whole placement and cancel the contract, which
            // is what kept the trap out of every corridor tile. Mount what the room allows and
            // carry on; the count travels in the diagnostics.
            for (int i = 0; i < axes.Length; i++)
            {
                if (TryResolveCeilingLampMount(placement, axes[i], mask, out Vector3 lampPoint))
                    placement.CeilingLampPositions.Add(lampPoint);
            }

            List<FixtureReservationRequest> requests = BuildReservationRequests(placement);
            if (FixtureReservationRegistry.TryGetConflict(requests, out int requestIndex, out FixtureReservationConflict conflict))
            {
                failure = $"compound-volume-{requestIndex} {conflict.ToDiagnosticString()}";
                placement.AccessoryValidationFailure = failure;
                return false;
            }

            placement.AccessoryValidationFailure = string.Empty;
            return true;
        }

        /// <summary>
        /// Ceiling-lamp ring distances, tried outermost first. The outermost rung is the authored
        /// look; the inner rungs pull the ring back over the cage so a corridor whose walls sit
        /// closer than the authored outset still mounts its lamps instead of losing the contract.
        /// </summary>
        private static readonly float[] CeilingLampOutsetLadderMeters =
        {
            PestControlTrapAssembler.CeilingLampOutsetMeters,
            0.45f,
            0f,
            -0.45f
        };

        private static bool TryResolveCeilingLampMount(
            PestControlTrapPlacement placement,
            Vector3 axis,
            int mask,
            out Vector3 mountPoint)
        {
            mountPoint = Vector3.zero;
            float halfSide = placement.FootprintMeters * 0.5f;
            for (int i = 0; i < CeilingLampOutsetLadderMeters.Length; i++)
            {
                float ring = Mathf.Max(0f, halfSide + CeilingLampOutsetLadderMeters[i]);
                Vector3 floor = placement.Center + axis * ring;
                InteriorAnchorService.RecordPhysicsQuery();
                if (!Physics.Raycast(floor + Vector3.up * 0.1f, Vector3.up, out RaycastHit hit, CeilingProbeMeters, mask, QueryTriggerInteraction.Ignore))
                    continue;
                if (hit.distance + 0.1f < LampMinimumClearanceMeters)
                    continue;

                mountPoint = hit.point;
                return true;
            }

            return false;
        }

        private static List<FixtureReservationRequest> BuildReservationRequests(PestControlTrapPlacement placement)
        {
            Quaternion rotation = Quaternion.Euler(0f, placement.Yaw, 0f);
            var requests = new List<FixtureReservationRequest>(2 + PestControlTrapAssembler.CeilingLampCount)
            {
                new FixtureReservationRequest(
                    ReservationLabel + ":cage",
                    placement.PlacementSource,
                    CreateTrapFootprint(placement.FootprintMeters, placement.CeilingClearanceMeters),
                    placement.Center,
                    rotation),
                new FixtureReservationRequest(
                    ReservationLabel + ":lever",
                    placement.PlacementSource,
                    new FixtureFootprint(
                        Vector3.forward, Vector3.back, Vector3.up,
                        new Vector3(
                            PestControlTrapAssembler.LeverReservationSideMeters,
                            PestControlTrapAssembler.LeverReservationHeightMeters,
                            PestControlTrapAssembler.LeverReservationSideMeters),
                        0f, 0f, 0.8f, 1.0f),
                    placement.LeverPosition,
                    placement.LeverRotation)
            };

            for (int i = 0; i < placement.CeilingLampPositions.Count; i++)
            {
                requests.Add(new FixtureReservationRequest(
                    ReservationLabel + ":lamp-" + i,
                    placement.PlacementSource,
                    new FixtureFootprint(
                        Vector3.forward, Vector3.back, Vector3.up,
                        new Vector3(
                            PestControlTrapAssembler.CeilingLampReservationSideMeters,
                            PestControlTrapAssembler.CeilingLampReservationHeightMeters,
                            PestControlTrapAssembler.CeilingLampReservationSideMeters),
                        0f,
                        -PestControlTrapAssembler.CeilingLampReservationHeightMeters,
                        0.2f,
                        0.2f),
                    placement.CeilingLampPositions[i],
                    rotation));
            }

            return requests;
        }

        private static void LogProductionSelection(PestControlTrapPlacement placement)
        {
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.PestControl] Production placement selected source='{placement.PlacementSource}' " +
                $"tile='{placement.TileName}' center=({placement.Center.x:0.00},{placement.Center.y:0.00},{placement.Center.z:0.00}) " +
                $"footprint={placement.FootprintMeters:0.00}m yaw={placement.Yaw:0.0} " +
                $"ceiling={placement.CeilingClearanceMeters:0.00}m readiness='{placement.ReadinessStatus}' " +
                $"lamps={placement.CeilingLampPositions?.Count ?? 0}/{PestControlTrapAssembler.CeilingLampCount} " +
                $"compoundReservations={(placement.IsReserved ? placement.ReservationGroup.Count : 0)}.");
        }

        private static void StampReadiness(
            PestControlTrapPlacement placement,
            HostFacilityPlacementReadiness readiness,
            bool degraded)
        {
            if (placement == null)
                return;

            placement.UsedDegradedReadiness = degraded;
            placement.ReadinessStatus =
                (degraded ? "degraded-timeout " : "ready ") + readiness.ToDiagnosticString();
        }
    }
}
