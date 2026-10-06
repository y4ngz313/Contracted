using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.Facility.Interior.Placement.Authored;

namespace Y4NGZCompany.Contracts.WasteDisposal
{
    internal static class WasteDisposalContract
    {
        private const string PlacementRequester = "Y4NGZCompany.WasteDisposal";
        /// <summary>Shaved off each effective collision box before the overlap test, and its
        /// bottom is lifted twice this much, so a slightly uneven floor plane does not read as
        /// clutter.</summary>
        private const float AuthoredCollisionSkinMeters = 0.05f;
        /// <summary>Placement attempts allowed per round before the incinerator is abandoned.
        /// A failing placement runs against a dungeon that does not change for the rest of the
        /// round, so an unbounded retry re-pays the same whole-dungeon scan of pathfinds and
        /// raycasts forever and reprints the same warning with it. Three attempts still cover
        /// the cases that genuinely resolve late, such as a fixture pass that had not reserved
        /// its volumes yet.</summary>
        private const int MaxPlacementAttempts = 3;
        /// <summary>Budget for the other retry path: the placement API refusing to start a
        /// request at all. That precondition is genuinely transient early in a round, so the
        /// budget is generous — roughly two minutes at the backoff below — but finite.</summary>
        private const int MaxPlacementRequestRefusals = 24;
        private const float PlacementRetryBackoffSeconds = 5f;
        private static readonly Collider[] AuthoredOverlapBuffer = new Collider[16];
        private static bool _placementRequested;
        private static float _nextPlacementRetryAt;
        private static int _placementFailureCount;
        private static int _placementRequestRefusalCount;
        private static bool _placementAbandoned;
        private static bool _authoredPlacementAttempted;
        private static float _authoredReadinessWaitStartedAt = -1f;
        private static float _authoredReadinessNextProgressLogSeconds;
        private static int _moonScrapValueSnapshot;
        private static float _targetPercentSnapshot;
        private static int _successfulBurnValueSnapshot;

        /// <summary>
        /// #688, mirroring Pest Control's #672 latch. Sticky "this client has actually walked up
        /// to the incinerator". Objectives are derived locally on every peer, so this is
        /// deliberately per-client: the locate step is a reading aid for the player looking at
        /// the card, not replicated contract state. The incinerator spawns server-side right
        /// after landing, so gating on <c>ActiveIncinerator != null</c> ticked the step before
        /// any player had seen the fixture.
        ///
        /// It can never stall completion: staged scrap or a finished burn implies located below,
        /// so a crew that feeds the chamber without this client ever approaching still finishes.
        /// </summary>
        private static bool _located;

        /// <summary>#1197. The locate row's key, shared with the pity timer's target read.</summary>
        internal const string LocateKey = "waste.locate";

        /// <summary>#1197. Read-only view of <see cref="_located"/> for the pity timer. It moves on the
        /// same objective poll that ticks the card row, so the two never disagree.</summary>
        internal static bool Located => _located;

        /// <summary>
        /// Distance from the fixture centre before it counts as found. Proximity-only, like the
        /// pest rig's ring: a sightline test would leave the step unticked for a player standing
        /// beside the fixture in the dark, which reads as a bug rather than as a rule.
        /// </summary>
        private const float LocateDistanceMeters = 8f;

        internal static WasteDisposalIncineratorController ActiveIncinerator { get; private set; }

        internal static void ResetRunState()
        {
            if (ActiveIncinerator != null)
                WasteDisposalIncineratorRegistry.TryDespawn(ActiveIncinerator);
            ActiveIncinerator = null;
            _placementRequested = false;
            _nextPlacementRetryAt = 0f;
            _placementFailureCount = 0;
            _placementRequestRefusalCount = 0;
            _placementAbandoned = false;
            _authoredPlacementAttempted = false;
            _authoredReadinessWaitStartedAt = -1f;
            _authoredReadinessNextProgressLogSeconds = 0f;
            _moonScrapValueSnapshot = 0;
            _targetPercentSnapshot = 0f;
            _successfulBurnValueSnapshot = 0;
            _located = false;
        }

        internal static void RegisterIncinerator(WasteDisposalIncineratorController controller)
        {
            if (controller != null)
                ActiveIncinerator = controller;
        }

        internal static void UnregisterIncinerator(WasteDisposalIncineratorController controller)
        {
            if (controller != null && controller.IsComplete)
                RecordSuccessfulBurn(controller.BurnedValue);
            if (ActiveIncinerator == controller)
                ActiveIncinerator = null;
        }

        internal static void RecordSuccessfulBurn(int burnedValue)
        {
            _successfulBurnValueSnapshot = Mathf.Max(_successfulBurnValueSnapshot, burnedValue);
        }

        internal static int GetSuccessfulBurnValue()
        {
            WasteDisposalIncineratorController incinerator = ActiveIncinerator;
            if (incinerator != null && incinerator.IsComplete)
                return Mathf.Max(0, incinerator.BurnedValue);
            return Mathf.Max(0, _successfulBurnValueSnapshot);
        }

        internal static void UpdateRuntime()
        {
            if (MoonContractState.GetCurrentContract() != MoonContractType.WasteDisposal)
                return;
            if (ActiveIncinerator != null || _placementRequested || _placementAbandoned || Time.realtimeSinceStartup < _nextPlacementRetryAt)
                return;

            StartOfRound start = StartOfRound.Instance;
            if (!IsServerRuntime() || start == null || !start.shipHasLanded || start.inShipPhase || start.shipIsLeaving)
                return;

            WasteDisposalBalanceConfig balance = ContractsBootstrap.WasteDisposalBalance;
            if (balance == null)
                return;
            if (_moonScrapValueSnapshot <= 0 && !TryGetGeneratedMoonScrapValue(out _moonScrapValueSnapshot))
            {
                _nextPlacementRetryAt = Time.realtimeSinceStartup + 1f;
                return;
            }
            if (_targetPercentSnapshot <= 0f)
                _targetPercentSnapshot = GetTargetPercent();

            // Authored-first, pest-trap precedent (#546): a valid authored pose spawns through
            // the same networked path the procedural callback uses; any rejection is recorded
            // and falls through to the unchanged procedural request below.
            if (!_authoredPlacementAttempted)
            {
                if (HasConfiguredAuthoredRecords() && !IsAuthoredReadinessResolved())
                {
                    _nextPlacementRetryAt = Time.realtimeSinceStartup + 1f;
                    return;
                }

                _authoredPlacementAttempted = true;
                if (TrySpawnFromAuthoredPlacement())
                    return;
            }

            _placementRequested = FacilityFeaturePlacementApi.RequestLargeBackWallPlacement(
                PlacementRequester,
                balance.FixtureWidthMeters.Value,
                balance.FixtureHeightMeters.Value,
                balance.FixtureDepthMeters.Value,
                balance.FrontClearanceMeters.Value,
                balance.MinEntranceDistanceMeters.Value,
                OnPlacementResolved);
            if (!_placementRequested)
                NotePlacementRequestRefused();
        }

        internal static List<MoonContractObjective> GetObjectives()
        {
            WasteDisposalIncineratorController incinerator = ActiveIncinerator;
            bool spawned = incinerator != null;
            int required = spawned ? Mathf.Max(1, incinerator.RequiredValue) : Mathf.Max(1, GetRequiredValue());
            int staged = spawned ? Mathf.Min(required, incinerator.StagedValue) : 0;
            bool complete = (spawned && incinerator.IsComplete) || _successfulBurnValueSnapshot > 0;
            bool committed = complete
                             || (spawned && incinerator.State != WasteDisposalIncineratorController.IncineratorState.Loading);
            bool malfunctioned = spawned
                                 && incinerator.State == WasteDisposalIncineratorController.IncineratorState.Malfunctioned;
            if (complete) staged = required;
            _located |= complete || staged > 0 || (spawned && LocalPlayerHasReachedIncinerator(incinerator));

            return new List<MoonContractObjective>
            {
                new MoonContractObjective("Locate the waste incinerator", _located ? 1 : 0, 1, key: LocateKey),
                // #687: no counter in the text — the HUD renderer already appends [staged/required]
                // to any objective whose Total exceeds one, and this line carried both.
                new MoonContractObjective("Load scrap into the chamber", committed ? required : staged, required, key: "waste.load"),
                // #716 E13: the key stays "waste.burn" through a malfunction. Only the wording
                // changes, so the card rewrites this row in place; swapping the key made the
                // HUD's semantic signature see a different objective and replay the reveal on
                // every fault and every repair.
                new MoonContractObjective(malfunctioned ? "Repair the incinerator and restart the burn" : "Start the burn cycle", complete ? 1 : 0, 1, key: "waste.burn"),
            };
        }

        /// <summary>
        /// #688. True once the local player has stood within <see cref="LocateDistanceMeters"/>
        /// of the incinerator. Measured against the fixture's own transform, which replicates
        /// with the networked spawn, so every peer evaluates the same point.
        /// </summary>
        private static bool LocalPlayerHasReachedIncinerator(WasteDisposalIncineratorController incinerator)
        {
            if (incinerator == null)
                return false;

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null || player.isPlayerDead || !player.isPlayerControlled)
                return false;

            Vector3 offset = player.transform.position - incinerator.transform.position;
            return offset.sqrMagnitude <= LocateDistanceMeters * LocateDistanceMeters;
        }

        private static void OnPlacementResolved(bool ok, Vector3 position, Quaternion rotation, Vector3 interactionPoint, string source)
        {
            _placementRequested = false;
            if (!ok)
            {
                NotePlacementFailure($"placement failed: {source}");
                return;
            }

            StartOfRound start = StartOfRound.Instance;
            if (!IsServerRuntime()
                || MoonContractState.GetCurrentContract() != MoonContractType.WasteDisposal
                || start == null
                || start.inShipPhase
                || start.shipIsLeaving)
                return;

            int moonScrapValue = Mathf.Max(1, _moonScrapValueSnapshot);
            float targetPercent = _targetPercentSnapshot > 0f ? _targetPercentSnapshot : GetTargetPercent();
            WasteDisposalIncineratorController controller = WasteDisposalIncineratorRegistry.TrySpawn(
                position,
                rotation,
                moonScrapValue,
                targetPercent);
            if (controller == null)
            {
                NotePlacementFailure("networked incinerator spawn failed");
                return;
            }

            ActiveIncinerator = controller;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.WasteDisposal] Incinerator spawned via {source} at ({position.x:0.0}, {position.y:0.0}, {position.z:0.0}); " +
                $"moonScrap={controller.MoonScrapValue}, target={controller.TargetPercent:0.#}% (${controller.RequiredValue}).");
        }

        /// <summary>Counts one failed incinerator placement or spawn and schedules the next
        /// attempt, or retires the contract's placement for the round once
        /// <see cref="MaxPlacementAttempts"/> is reached. The terminal log is the only summary a
        /// round gets, so it carries the attempt count and the last reason.</summary>
        private static void NotePlacementFailure(string reason)
        {
            _placementFailureCount++;
            if (_placementFailureCount >= MaxPlacementAttempts)
            {
                _placementAbandoned = true;
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.WasteDisposal] Incinerator placement abandoned for this round after {_placementFailureCount} attempt(s); last reason={reason}. No further attempts will be made.");
                return;
            }

            _nextPlacementRetryAt = Time.realtimeSinceStartup + PlacementRetryBackoffSeconds * _placementFailureCount;
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.WasteDisposal] Incinerator {reason}; attempt {_placementFailureCount}/{MaxPlacementAttempts}, retrying in {PlacementRetryBackoffSeconds * _placementFailureCount:0.#}s.");
        }

        /// <summary>Counts one refusal by the placement API to even start a request — no
        /// coroutine host, no RoundManager, or this client is not the server. Unlike a placement
        /// failure this is legitimately transient, so it gets a far larger budget, but it is not
        /// allowed to be silent and infinite: before this the branch only pushed the retry timer
        /// out, so a permanently unsatisfiable precondition retried for the whole round without
        /// ever printing a line.</summary>
        private static void NotePlacementRequestRefused()
        {
            _placementRequestRefusalCount++;
            _nextPlacementRetryAt = Time.realtimeSinceStartup + PlacementRetryBackoffSeconds;
            if (_placementRequestRefusalCount == 1)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.WasteDisposal] Incinerator placement request could not be started (no coroutine host, no round manager, or not the server); retrying every {PlacementRetryBackoffSeconds:0.#}s for up to {MaxPlacementRequestRefusals} attempts.");
                return;
            }

            if (_placementRequestRefusalCount >= MaxPlacementRequestRefusals)
            {
                _placementAbandoned = true;
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.WasteDisposal] Incinerator placement abandoned for this round: the placement request was refused {_placementRequestRefusalCount} times without ever starting. No further attempts will be made.");
            }
        }

        /// <summary>Cheap probe for whether authored waste-incinerator records exist for THIS
        /// round's generated interior, scoped to the current dungeon flow the same way
        /// TryResolvePoses is. Without records for this flow the readiness barrier protects
        /// nothing, and an install without LethalCCTV (the only assembly that raises the
        /// fixture-pass signals) would stall every round's procedural spawn behind the full
        /// timeout — even on interiors no record was ever authored for.</summary>
        private static bool HasConfiguredAuthoredRecords()
        {
            return AuthoredInteriorPlacementStore.GetRecordsForFlow(
                AuthoredInteriorPlacementKinds.WasteIncinerator,
                ResolveCurrentDungeonFlowName()).Count > 0;
        }

        /// <summary>Mirrors AuthoredInteriorPlacementStore.ResolveFlowName so the probe and the
        /// pose resolution agree on which flow's records count.</summary>
        private static string ResolveCurrentDungeonFlowName()
        {
            try
            {
                DunGen.Graph.DungeonFlow flow = RoundManager.Instance?.dungeonGenerator?.Generator?.DungeonFlow;
                return flow != null && !string.IsNullOrEmpty(flow.name) ? flow.name : "unknown-flow";
            }
            catch
            {
                return "unknown-flow";
            }
        }

        /// <summary>
        /// Shared readiness barrier, mirroring the pest-trap gate: consuming an authored pose
        /// before the CCTV fixtures have reserved their volumes could hand it a spot the
        /// mainframe is about to claim. Returns true once the host fixture pass is complete or
        /// the bounded wait resolves degraded.
        /// </summary>
        private static bool IsAuthoredReadinessResolved()
        {
            float now = Time.realtimeSinceStartup;
            if (_authoredReadinessWaitStartedAt < 0f)
            {
                _authoredReadinessWaitStartedAt = now;
                _authoredReadinessNextProgressLogSeconds = 0f;
            }

            HostFacilityPlacementReadiness readiness = AuthoredPlacementRoundReport.HostFacilityPlacementState;
            float elapsedSeconds = Mathf.Max(0f, now - _authoredReadinessWaitStartedAt);
            switch (AuthoredPlacementReadinessPolicy.Evaluate(
                        readiness,
                        elapsedSeconds,
                        _authoredReadinessNextProgressLogSeconds))
            {
                case AuthoredPlacementReadinessDisposition.Ready:
                    return true;
                case AuthoredPlacementReadinessDisposition.ProceedDegraded:
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.WasteDisposal] Host fixture pass still incomplete after {AuthoredPlacementReadinessPolicy.TimeoutSeconds:0}s; " +
                        "resolving authored incinerator poses degraded.");
                    return true;
                case AuthoredPlacementReadinessDisposition.LogAndWait:
                    ContractsBootstrap.Log?.LogInfo(
                        "[MoonContracts.WasteDisposal] Waiting for the host fixture pass before consuming authored " +
                        $"incinerator poses ({readiness.ToDiagnosticString()} timeout {AuthoredPlacementReadinessPolicy.TimeoutSeconds:0}s).");
                    _authoredReadinessNextProgressLogSeconds =
                        elapsedSeconds + AuthoredPlacementReadinessPolicy.ProgressLogIntervalSeconds;
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Resolves authored waste-incinerator poses and spawns the first one that validates,
        /// through the same networked spawn the procedural callback uses (host-only, pose final
        /// before <c>NetworkObject.Spawn</c>). Returns false when nothing spawned so the caller
        /// falls back to the unchanged procedural request.
        /// </summary>
        private static bool TrySpawnFromAuthoredPlacement()
        {
            if (!AuthoredInteriorPlacementStore.TryResolvePoses(
                    AuthoredInteriorPlacementKinds.WasteIncinerator,
                    out List<AuthoredInteriorPlacementPose> poses)
                || poses == null)
            {
                return false;
            }

            bool spawned = false;
            for (int i = 0; i < poses.Count; i++)
            {
                AuthoredInteriorPlacementPose pose = poses[i];
                if (spawned)
                {
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        pose.Record,
                        "consumer=waste-incinerator reason=single-incinerator-already-spawned");
                    continue;
                }

                if (!TryValidateAuthoredPose(pose, out Vector3 position, out Quaternion rotation, out List<string> reasons))
                {
                    AuthoredPlacementRoundReport.RecordValidationFailed(pose.Record, reasons);
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.WasteDisposal] Authored waste-incinerator id='{pose.Record?.objectId ?? "<unknown>"}' " +
                        $"rejected: {string.Join("; ", reasons)}. Falling back.");
                    continue;
                }

                if (!TryReserveAuthoredFootprint(pose, position, rotation, out FixtureReservationHandle reservation, out string conflictReason))
                {
                    AuthoredPlacementRoundReport.RecordValidationFailed(pose.Record, new List<string> { conflictReason });
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.WasteDisposal] Authored waste-incinerator id='{pose.Record?.objectId ?? "<unknown>"}' " +
                        $"rejected: {conflictReason}. Falling back.");
                    continue;
                }

                int moonScrapValue = Mathf.Max(1, _moonScrapValueSnapshot);
                float targetPercent = _targetPercentSnapshot > 0f ? _targetPercentSnapshot : GetTargetPercent();
                WasteDisposalIncineratorController controller = WasteDisposalIncineratorRegistry.TrySpawn(
                    position,
                    rotation,
                    moonScrapValue,
                    targetPercent);
                if (controller == null)
                {
                    FixtureReservationRegistry.Release(reservation);
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        pose.Record,
                        "consumer=waste-incinerator reason=spawn-failed");
                    ContractsBootstrap.Log?.LogWarning(
                        "[MoonContracts.WasteDisposal] Networked incinerator spawn failed for the authored pose; " +
                        "falling back to procedural placement.");
                    return false;
                }

                ActiveIncinerator = controller;
                AuthoredPlacementRoundReport.RecordSpawned(pose.Record);
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.WasteDisposal] Incinerator spawned via {(string.IsNullOrWhiteSpace(pose.Source) ? "authored" : pose.Source)} " +
                    $"at ({position.x:0.0}, {position.y:0.0}, {position.z:0.0}); " +
                    $"moonScrap={controller.MoonScrapValue}, target={controller.TargetPercent:0.#}% (${controller.RequiredValue}).");
                spawned = true;
            }

            return spawned;
        }

        /// <summary>
        /// Validates the incinerator's EFFECTIVE collision profile at the authored pose: the
        /// prefab's authored <see cref="WasteDisposalCollisionBoxBinding"/> boxes when valid,
        /// else the code-owned semantic compound. Reason tokens follow the pest-trap authored
        /// validation vocabulary so the round report stays consistent.
        /// </summary>
        private static bool TryValidateAuthoredPose(
            AuthoredInteriorPlacementPose pose,
            out Vector3 position,
            out Quaternion rotation,
            out List<string> reasons)
        {
            reasons = new List<string>();
            position = pose.WorldPosition;
            rotation = Quaternion.Euler(0f, pose.WorldRotation.eulerAngles.y, 0f);

            if (pose.Tile == null)
            {
                reasons.Add("resolved-tile-null");
                return false;
            }

            GetFixtureDimensions(out float width, out float height, out float depth, out float frontClearance);
            StartOfRound start = StartOfRound.Instance;
            int mask = start != null ? start.collidersAndRoomMaskAndDefault : ~0;

            // Floor support under the fixture centre; the authored Y is advisory and snaps down
            // to the hit so the base sits on the floor the way the procedural pose does.
            if (!Physics.Raycast(pose.WorldPosition + Vector3.up * 2.5f, Vector3.down, out RaycastHit floorHit, 6f, mask, QueryTriggerInteraction.Ignore))
            {
                reasons.Add("missing-floor");
                return false;
            }
            position = new Vector3(pose.WorldPosition.x, floorHit.point.y, pose.WorldPosition.z);

            WasteDisposalCollisionBoxBinding[] boxes =
                WasteDisposalIncineratorVisualAdapter.ResolveEffectiveCollisionBoxes(width, height, depth);
            for (int i = 0; i < boxes.Length; i++)
            {
                Vector3 half = boxes[i].Size * 0.5f
                               - new Vector3(AuthoredCollisionSkinMeters, AuthoredCollisionSkinMeters, AuthoredCollisionSkinMeters);
                half = Vector3.Max(half, Vector3.one * 0.01f);
                Vector3 center = position + rotation * (boxes[i].Center + Vector3.up * AuthoredCollisionSkinMeters);
                if (HasBlockingOverlap(center, half, rotation, floorHit.collider, mask))
                {
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.WasteDisposal] Authored pose overlap in collision box '{boxes[i].Name}'.");
                    reasons.Add("footprint-cluttered");
                    return false;
                }
            }

            // The procedural request carries FrontClearance and RequireBodyClearance, so the
            // authored pose earns the same open slab in front of the feed door.
            Vector3 frontCenter = position + rotation * new Vector3(0f, height * 0.5f, depth * 0.5f + frontClearance * 0.5f);
            Vector3 frontHalf = new Vector3(width * 0.45f, height * 0.45f, frontClearance * 0.45f);
            if (HasBlockingOverlap(frontCenter, frontHalf, rotation, floorHit.collider, mask))
            {
                reasons.Add("front-cluttered");
                return false;
            }

            // RequireReachableInteractionPoint equivalent: players must be able to stand and
            // path to the loading side.
            Vector3 frontFloorProbe = position + rotation * new Vector3(0f, 0f, depth * 0.5f + Mathf.Min(frontClearance, 1.0f));
            if (!Physics.Raycast(frontFloorProbe + Vector3.up * 2.5f, Vector3.down, out RaycastHit frontFloorHit, 6f, mask, QueryTriggerInteraction.Ignore))
            {
                reasons.Add("front-missing-floor");
                return false;
            }
            if (!NavMesh.SamplePosition(frontFloorHit.point + Vector3.up * 0.1f, out _, 1.0f, NavMesh.AllAreas))
            {
                reasons.Add("front-off-navmesh");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Same reservation the procedural path takes out through
        /// <see cref="FacilityFeaturePlacementApi"/>, so nothing else can be placed on top of the
        /// authored incinerator. Held for the round; the handle is intentionally handed off.
        /// </summary>
        private static bool TryReserveAuthoredFootprint(
            AuthoredInteriorPlacementPose pose,
            Vector3 position,
            Quaternion rotation,
            out FixtureReservationHandle reservation,
            out string conflictReason)
        {
            GetFixtureDimensions(out float width, out float height, out float depth, out float frontClearance);
            FixtureFootprint footprint = FacilityFeaturePlacementApi.BuildLargeBackWallFootprint(
                width,
                height,
                depth,
                frontClearance);
            if (!FixtureReservationRegistry.TryReserve(
                    FacilityFeaturePlacementApi.BuildExternalLargeFixtureLabel(PlacementRequester) + ":authored",
                    string.IsNullOrWhiteSpace(pose.Source) ? "authored" : pose.Source,
                    footprint,
                    position,
                    rotation,
                    out reservation,
                    out FixtureReservationConflict conflict))
            {
                conflictReason = "reserved-volume-conflict " + conflict.ToDiagnosticString();
                return false;
            }

            conflictReason = string.Empty;
            return true;
        }

        /// <summary>Same clamps <see cref="FacilityFeaturePlacementApi.RequestLargeBackWallPlacement"/>
        /// applies, so the authored and procedural paths validate the same fixture.</summary>
        private static void GetFixtureDimensions(out float width, out float height, out float depth, out float frontClearance)
        {
            WasteDisposalBalanceConfig balance = ContractsBootstrap.WasteDisposalBalance;
            FacilityFeaturePlacementApi.ClampLargeBackWallDimensions(
                balance?.FixtureWidthMeters.Value ?? 2.4f,
                balance?.FixtureHeightMeters.Value ?? 2.85f,
                balance?.FixtureDepthMeters.Value ?? 1.75f,
                balance?.FrontClearanceMeters.Value ?? 1.35f,
                out width,
                out height,
                out depth,
                out frontClearance);
        }

        /// <summary>Overlap test against the room mask, ignoring triggers and the floor collider
        /// under the fixture. The incinerator itself does not exist yet, so there is no own
        /// geometry to exclude.</summary>
        private static bool HasBlockingOverlap(Vector3 center, Vector3 halfExtents, Quaternion rotation, Collider floorCollider, int mask)
        {
            int hits = Physics.OverlapBoxNonAlloc(center, halfExtents, AuthoredOverlapBuffer, rotation, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                Collider hit = AuthoredOverlapBuffer[i];
                if (hit == null || ReferenceEquals(hit, floorCollider))
                    continue;

                return true;
            }

            return false;
        }

        private static int GetRequiredValue()
        {
            int moonValue = _moonScrapValueSnapshot;
            if (moonValue <= 0)
                TryGetGeneratedMoonScrapValue(out moonValue);
            return WasteDisposalIncineratorController.CalculateRequiredValue(Mathf.Max(1, moonValue), GetTargetPercent());
        }

        private static float GetTargetPercent()
        {
            WasteDisposalBalanceConfig balance = ContractsBootstrap.WasteDisposalBalance;
            return balance != null
                ? balance.ForRisk(MoonContractState.GetCurrentRiskTier()).RequiredMoonScrapPercent.Value
                : 10f;
        }

        private static bool TryGetGeneratedMoonScrapValue(out int value)
        {
            value = 0;
            RoundManager round = RoundManager.Instance;
            if (round != null && round.totalScrapValueInLevel > 0.5f)
            {
                value = Mathf.Max(1, Mathf.RoundToInt(round.totalScrapValueInLevel));
                return true;
            }

            GrabbableObject[] items = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            for (int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (item == null || item.itemProperties == null || !item.itemProperties.isScrap)
                    continue;
                if (item.isInFactory && !item.isInShipRoom && !item.isInElevator)
                    value += Mathf.Max(0, item.scrapValue);
            }
            return value > 0;
        }

        private static bool IsServerRuntime()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null && manager.IsServer)
                return true;
            RoundManager round = RoundManager.Instance;
            if (round != null && round.IsServer)
                return true;
            return NetRole.IsServerOrHost();
        }
    }
}
