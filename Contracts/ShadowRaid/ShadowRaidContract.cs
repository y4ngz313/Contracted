using System.Collections.Generic;
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCore.Modules.Sales;
namespace Y4NGZCompany.Contracts.ShadowRaid
{
    internal static class ShadowRaidContract
    {
        private static bool _initialized;
        private static readonly List<NetworkObject> SpawnedObjects = new List<NetworkObject>();
        // #949 S11H U1: the containers THIS round placed, in placement order. The reward stage
        // reads this rather than the global live-container snapshot, so a round that placed
        // nothing configures nothing. Kept as ids rather than components because the reward
        // stage can run several spawn attempts after the container stage succeeded, and an id
        // survives a destroyed component where a reference does not.
        private static readonly List<ulong> PlacedContainerIdsThisRound = new List<ulong>();
        private static InteriorTileSelection _cachedSelection;
        private static List<InteriorApproachAnchor> _cachedApproaches;
        private static bool _hasCachedPlacement;
        private static bool _containersSpawned;
        private static bool _rewardSpawned;
        private static bool _hardDriveFound;
        private static bool _drillCompleted;
        private static bool _drillDeliveryRequested;
        private static bool _drillDelivered;
        private static bool _drillPendingDropshipSpawn;
        private static bool _drillQueuedInVanillaDropship;
        private static bool _loggedFallbackScanGate;
        // #1107: the two edges the drill fallback poll's object scan is allowed to run on, so the
        // scan can never sit on the 0.5s cadence. Both die with the delivery, beside the delivery
        // latches they gate (ResetRunState and CancelPendingDrillDelivery).
        private static bool _drillFallbackDoorsSeenOpen;
        private static bool _drillDeliveryTimeoutHandled;
        private static bool _loggedVanillaDropshipPrep;
        private static bool _loggedNoAuthoredContainerMatch;
        private static MethodInfo _dropshipLandMethod;
        private static FieldInfo _dropshipDoorsOpenedField;
        private static FieldInfo _dropshipItemsToDeliverField;
        private static ItemDropship _cachedDropship;
        private static Terminal _cachedTerminal;
        private static bool _loggedMissingDropship;
        private static bool _loggedUnavailableDrillItem;
        private static int _drillTerminalItemIndex = -1;
        private static float _drillDeliveryRequestedAt;
        private static float _nextDrillSpawnAttemptAt;
        private static float _nextDrillFallbackPollAt;
        private static float _nextRoomSpawnAttemptAt;
        private static float _nextProceduralContainerSearchAt;
        internal static bool DrillDelivered => _drillDelivered;
        internal static bool DrillAttached => FindActiveDrill()?.Attached ?? false;
        // The reusable drill clears its transient completion in the same Update. The
        // container retains the networked completion, including for late joiners.
        internal static bool DrillCompleted => _drillCompleted
            || (_drillCompleted = BreachContainerController.HasCompletedDrill());
        internal static float DrillRemainingSeconds => FindActiveDrill()?.RemainingSeconds ?? 0f;
        internal static bool HardDriveFound => _hardDriveFound;
        // #1076: the contract's first failure path. A Blacksite ship that leaves with the drive
        // aboard voids this contract exactly the way it voids Whistleblower, and until now the card
        // simply never resolved: "Recover the hard drive" stayed open on an object that had left
        // the moon. Per peer and one-way; it is raised by
        // MoonContractState.NotifyBlacksiteObjectiveDeparted, on the host directly and on every
        // other peer from the progression channel's named message (or its round-keyed replay, for
        // a late joiner). It cannot be read off the drive or the mission: both are despawned in the
        // departure's own call stack, before NGO flushes anything. No new RPC.
        private static bool _driveExtracted;
        internal static bool DriveExtracted => _driveExtracted;

        /// <summary>#1076: raise the departure latch. True on the transition only.</summary>
        internal static bool NoteDriveDeparted()
        {
            if (_driveExtracted)
                return false;
            _driveExtracted = true;
            ContractsBootstrap.Log?.LogInfo(
                "[MoonContracts.ShadowRaid] Hard drive extracted by the recovery team; contract voided.");
            return true;
        }

        /// <summary>
        /// #1076: the squad's hold on the drive, refreshed from the replicated manifest snapshots
        /// on every peer. Called from the card poll (MoonContractState.GetShadowRaidObjectives),
        /// which is the only reader. A departed drive is nobody's possession any more, and the
        /// snapshot list it would come from is gone with the mission, so the latch answers first.
        /// </summary>
        internal static BlacksitePossessionReading RefreshBlacksitePossession()
        {
            return _driveExtracted
                ? default
                : BlacksitePossessionReader.Read(BlacksiteObjectiveKind.ShadowDrive);
        }

        internal static void ResetRunState()
        {
            _initialized = false;
            _cachedSelection = default;
            _cachedApproaches = null;
            _hasCachedPlacement = false;
            _containersSpawned = false;
            _rewardSpawned = false;
            _hardDriveFound = false;
            // #1076: the departure latch dies with the run, beside the objective latches it sits next to.
            _driveExtracted = false;
            _drillCompleted = false;
            _drillDeliveryRequested = false;
            _drillDelivered = false;
            _drillPendingDropshipSpawn = false;
            _drillQueuedInVanillaDropship = false;
            _loggedVanillaDropshipPrep = false;
            _loggedFallbackScanGate = false;
            // #1107
            _drillFallbackDoorsSeenOpen = false;
            _drillDeliveryTimeoutHandled = false;
            _loggedNoAuthoredContainerMatch = false;
            _loggedMissingDropship = false;
            _loggedUnavailableDrillItem = false;
            _cachedTerminal = null;
            _drillDeliveryRequestedAt = 0f;
            _nextDrillSpawnAttemptAt = 0f;
            _nextDrillFallbackPollAt = 0f;
            _nextRoomSpawnAttemptAt = 0f;
            _nextProceduralContainerSearchAt = 0f;
            BreachContainerPlacer.ResetScanState();
            // #949 S11H U1: the objects die with the round. Despawning happens before the list
            // is dropped, because the list is the only handle the host has on them.
            PurgeRoundObjects();
            SpawnedObjects.Clear();
            PlacedContainerIdsThisRound.Clear();
            // The static container registry and the round token advance on every peer, not just
            // the host: a client keeps its own copy of both and asks the same liveness question.
            BreachContainerController.PurgeRound();
            ShadowRaidRoundRegistry.BeginNewRound();
        }

        /// <summary>
        /// #949 S11H U1: despawns everything this round spawned, before ResetRunState drops the
        /// list. Until this existed the list was simply cleared, so an Assurance container and
        /// its crate were still spawned, active instances when the crew landed on March (Test 3,
        /// log 8161-8168 and 11701); the March reward stage found the orphan as the only live
        /// container and put March's drive inside it.
        ///
        /// <para>Every despawn is guarded on NetworkManager.Singleton being present, listening
        /// and the server, and on the object still being spawned. That is the same guard
        /// MoonContractPatches.cs uses on its runtime pass and for the same reason: one of the
        /// two callers of ResetRunState is the GameNetworkManager.Disconnect postfix
        /// (StartOfRoundPatches.cs), which runs while the NetworkManager is shutting down, and
        /// Stage 11 U6 saw exactly that path throw NotListeningException out of
        /// PayloadContract.TrySpawnCart. A skipped despawn is reported, never silent.</para>
        ///
        /// <para>Anything the crew is holding or has stowed in the ship is left alone. The
        /// end-of-round caller is the EndOfGameClientRpc prefix, which runs before vanilla
        /// counts what came home, so despawning a drive out of the ship there would delete
        /// value the crew earned. An object in the ship has left the round; an object lying in
        /// a dungeon that is about to be unloaded has not.</para>
        /// </summary>
        private static void PurgeRoundObjects()
        {
            if (SpawnedObjects.Count == 0)
                return;

            NetworkManager network = NetworkManager.Singleton;
            bool canDespawn = network != null && network.IsListening && network.IsServer;

            int despawned = 0;
            int skippedNetcodeDown = 0;
            int skippedCrewSecured = 0;
            int skippedNotSpawned = 0;
            int containers = 0;
            int crates = 0;
            int drives = 0;
            int drills = 0;

            // Reverse order: crates, drives and drills are appended after the containers they
            // belong to, and despawning a container can take its children with it. Walking
            // backwards despawns the children first, so each count names an object this method
            // actually removed rather than one netcode had already taken.
            for (int i = SpawnedObjects.Count - 1; i >= 0; i--)
            {
                NetworkObject spawnedObject = SpawnedObjects[i];
                if (spawnedObject == null)
                    continue;

                if (!canDespawn)
                {
                    skippedNetcodeDown++;
                    continue;
                }

                if (!spawnedObject.IsSpawned)
                {
                    skippedNotSpawned++;
                    continue;
                }

                if (IsCrewSecured(spawnedObject))
                {
                    skippedCrewSecured++;
                    continue;
                }

                ShadowRaidObjectKind kind = ResolveRoundObjectKind(spawnedObject);
                spawnedObject.Despawn(destroy: true);
                despawned++;
                switch (kind)
                {
                    case ShadowRaidObjectKind.Crate:
                        crates++;
                        break;
                    case ShadowRaidObjectKind.Drive:
                        drives++;
                        break;
                    case ShadowRaidObjectKind.Drill:
                        drills++;
                        break;
                    default:
                        containers++;
                        break;
                }
            }

            int skipped = skippedNetcodeDown + skippedCrewSecured + skippedNotSpawned;
            if (despawned == 0 && skipped == 0)
                return;

            string skippedDetail = skipped == 0
                ? string.Empty
                : $", skipped={skipped}: netcodeDown={skippedNetcodeDown}, crewSecured={skippedCrewSecured}, notSpawned={skippedNotSpawned}";
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Purged {despawned} Shadow Raid object(s) from round {ShadowRaidRoundRegistry.CurrentRound} at run reset (containers={containers}, crates={crates}, drives={drives}, drills={drills}{skippedDetail}).");
        }

        /// <summary>
        /// #949 S11H U1: what the crew is carrying or has stowed is theirs, not the round's.
        /// The same test the objective manifest uses for a crew-secured prop.
        /// </summary>
        private static bool IsCrewSecured(NetworkObject spawnedObject)
        {
            var prop = spawnedObject.GetComponent<GrabbableObject>();
            return prop != null
                   && (prop.isInShipRoom
                       || prop.isInElevator
                       || prop.isHeld
                       || prop.isPocketed
                       || prop.playerHeldBy != null);
        }

        /// <summary>
        /// #949 S11H U1: the registry answers first, because it was told the kind at the moment
        /// the object was tracked. The component probe behind it is the belt for an entry that
        /// predates its stamp, and runs only once per object per reset.
        /// </summary>
        private static ShadowRaidObjectKind ResolveRoundObjectKind(NetworkObject spawnedObject)
        {
            if (ShadowRaidRoundRegistry.TryGetKind(spawnedObject.NetworkObjectId, out ShadowRaidObjectKind kind))
                return kind;

            if (spawnedObject.GetComponent<ShadowRaidCargoCrate>() != null)
                return ShadowRaidObjectKind.Crate;
            if (spawnedObject.GetComponent<BreachHardDriveItem>() != null)
                return ShadowRaidObjectKind.Drive;
            if (spawnedObject.GetComponent<BreachDrillItem>() != null)
                return ShadowRaidObjectKind.Drill;
            return ShadowRaidObjectKind.Container;
        }

        /// <summary>
        /// #949 S11H U1: the one place a Shadow Raid object joins the round. The host's despawn
        /// list and the round token are written together so they can never disagree about what
        /// this round owns.
        /// </summary>
        private static void TrackSpawnedObject(NetworkObject spawnedObject, ShadowRaidObjectKind kind)
        {
            if (spawnedObject == null)
                return;

            if (!SpawnedObjects.Contains(spawnedObject))
                SpawnedObjects.Add(spawnedObject);
            ShadowRaidRoundRegistry.Register(spawnedObject.NetworkObjectId, kind);
        }

        internal static void NotifyHardDriveFound(string source)
        {
            if (_hardDriveFound)
                return;

            _hardDriveFound = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Hard drive objective completed via {source}.");
        }

        internal static bool TryGetBlacksiteRetrievalTarget(
            out BlacksiteRetrievalTargetPose target)
        {
            target = default;
            List<BreachContainerController> containers =
                BreachContainerController.GetActiveContainersSnapshot();
            if (containers.Count == 0)
                return false;

            int objectiveIndex = SelectObjectiveContainerIndex(containers.Count);
            BreachContainerController container = containers[objectiveIndex];
            if (container == null
                || !container.TryGetEmbeddedHardDriveTarget(
                    out ShadowRaidCargoCrate crate,
                    out BreachHardDriveItem drive))
            {
                return false;
            }

            NetworkObject expectedTarget = container.GetComponent<NetworkObject>();
            NetworkObject secondaryTarget = crate.GetComponent<NetworkObject>();
            NetworkObject driveObject = drive != null ? drive.GetComponent<NetworkObject>() : null;
            target = new BlacksiteRetrievalTargetPose(
                crate.transform.position,
                crate.transform.rotation,
                expectedTarget,
                secondaryTarget,
                driveObject,
                "shadow-raid-embedded-cache");
            return true;
        }

        internal static void RemoveReturnedDrillsFromShip(string reason)
        {
            BreachDrillItem.RemoveReturnedDrillsFromShip(reason);
        }

        internal static void UpdateRuntime()
        {
            bool activeByContract = IsShadowRaidContractActive();
            if (activeByContract)
            {
                EnsureDrillDelivery();
                EnsureDrillFallbackSpawn();
            }
            else
            {
                CancelPendingDrillDelivery("inactive contract");
            }

            if (!activeByContract || (_initialized && _containersSpawned && _rewardSpawned))
            {
                return;
            }

            var round = RoundManager.Instance;
            var start = StartOfRound.Instance;
            if (round == null || start == null || !IsServerRuntime() || !start.shipHasLanded || start.inShipPhase || start.shipIsLeaving)
                return;

            if (Time.time < _nextRoomSpawnAttemptAt)
                return;

            _nextRoomSpawnAttemptAt = Time.time + 1.5f;
            if (!TrySpawnPrototype())
                return;

            _initialized = _containersSpawned && _rewardSpawned;
        }

        private static ItemDropship FindDropship()
        {
            // Unity's overloaded == turns a destroyed dropship back into null, so a stale
            // cache self-heals with a rescan on the next call.
            if (_cachedDropship == null)
                _cachedDropship = UnityEngine.Object.FindObjectOfType<ItemDropship>();
            return _cachedDropship;
        }

        private static Terminal FindTerminal()
        {
            // Same self-healing cache as FindDropship: one scene scan instead of one per frame.
            if (_cachedTerminal == null)
                _cachedTerminal = UnityEngine.Object.FindObjectOfType<Terminal>();
            return _cachedTerminal;
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

        private static bool IsShadowRaidContractActive()
        {
            return MoonContractState.GetCurrentContract() == MoonContractType.ShadowRaid;
        }

        internal static void TrySpawnDrillFromDropship(ItemDropship dropship)
        {
            if (dropship == null || !dropship.IsServer)
                return;
            if (!IsShadowRaidContractActive())
            {
                CancelPendingDrillDelivery("dropship opened while contract inactive", dropship, forceIndexScan: true);
                return;
            }
            if (_drillDelivered || !_drillPendingDropshipSpawn)
                return;

            if (TryFinalizeDropshipSpawnedDrill(dropship, "door open"))
                return;

            TrySpawnPendingDrillAtDropship(dropship, "door open fallback");
        }

        internal static void PrepareDropshipDoorOpen(ItemDropship dropship)
        {
            if (dropship == null || !dropship.IsServer)
                return;
            if (!IsShadowRaidContractActive())
            {
                CancelPendingDrillDelivery("dropship door preparation while contract inactive", dropship, forceIndexScan: true);
                return;
            }
            if (!_drillPendingDropshipSpawn || _drillTerminalItemIndex < 0)
                return;

            // Pull the drill out of vanilla's cargo list BEFORE OpenShipDoorsOnServer's spawn loop runs.
            // Vanilla instantiates the (inactive) item prefab and spawns it while still inactive; Netcode's
            // GetComponentInParent<NetworkObject>() then skips the inactive drill and binds its PhysicsProp to
            // the active ship Props container, which makes vanilla grab validation fail. We instead spawn the
            // drill ourselves in the door-open postfix AFTER activating it, so it binds to its own NetworkObject.
            RemoveDrillFromVanillaCargo(dropship);

            if (!_loggedVanillaDropshipPrep)
            {
                _loggedVanillaDropshipPrep = true;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Shadow Raid drill (index {_drillTerminalItemIndex}) pulled from vanilla cargo; will be spawned active via mod path on door open.");
            }
        }

        private static void RemoveDrillFromVanillaCargo(ItemDropship dropship)
        {
            if (dropship == null || _drillTerminalItemIndex < 0)
                return;

            try
            {
                if (_dropshipItemsToDeliverField == null)
                    _dropshipItemsToDeliverField = AccessTools.Field(typeof(ItemDropship), "itemsToDeliver");
                if (_dropshipItemsToDeliverField == null)
                    return;

                if (_dropshipItemsToDeliverField.GetValue(dropship) is List<int> itemsToDeliver)
                    itemsToDeliver.RemoveAll(index => index == _drillTerminalItemIndex);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Failed removing Shadow Raid drill from vanilla cargo: {e.Message}");
            }
        }

        private static void CancelPendingDrillDelivery(string reason, ItemDropship dropship = null, bool forceIndexScan = false)
        {
            if (!IsServerRuntime())
                return;

            if (forceIndexScan && _drillTerminalItemIndex < 0)
                TryResolveDrillTerminalItemIndex();

            bool hadPendingState = _drillDeliveryRequested
                                   || _drillPendingDropshipSpawn
                                   || _drillQueuedInVanillaDropship
                                   || _drillTerminalItemIndex >= 0;
            if (!hadPendingState)
                return;

            int terminalRemoved = RemoveDrillFromTerminalOrder();
            if (dropship == null)
                dropship = FindDropship();
            RemoveDrillFromVanillaCargo(dropship);
            int runtimeRemoved = BreachDrillItem.RemoveAllRuntimeDrills(reason);

            _drillDeliveryRequested = false;
            _drillPendingDropshipSpawn = false;
            _drillQueuedInVanillaDropship = false;
            _loggedVanillaDropshipPrep = false;
            _loggedFallbackScanGate = false;
            // #1107: a cancelled delivery re-arms both scan edges with the rest of the delivery state.
            _drillFallbackDoorsSeenOpen = false;
            _drillDeliveryTimeoutHandled = false;
            _drillDeliveryRequestedAt = 0f;
            _nextDrillSpawnAttemptAt = 0f;
            _nextDrillFallbackPollAt = 0f;
            _drillTerminalItemIndex = -1;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Cancelled Shadow Raid drill delivery because {reason}; " +
                $"terminalOrdersRemoved={terminalRemoved}, runtimeDrillsRemoved={runtimeRemoved}.");
        }

        private static int RemoveDrillFromTerminalOrder()
        {
            if (_drillTerminalItemIndex < 0)
                return 0;

            try
            {
                Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                if (terminal?.orderedItemsFromTerminal == null)
                    return 0;
                if (!TerminalIndexLooksLikeDrill(terminal, _drillTerminalItemIndex))
                    return 0;

                int before = terminal.orderedItemsFromTerminal.Count;
                terminal.orderedItemsFromTerminal.RemoveAll(index => index == _drillTerminalItemIndex);
                return before - terminal.orderedItemsFromTerminal.Count;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Failed removing pending drill terminal order: {e.Message}");
                return 0;
            }
        }

        private static bool TryResolveDrillTerminalItemIndex()
        {
            try
            {
                Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                Item[] buyableItems = terminal?.buyableItemsList;
                if (buyableItems == null)
                    return false;

                for (int i = 0; i < buyableItems.Length; i++)
                {
                    if (TerminalIndexLooksLikeDrill(terminal, i))
                    {
                        _drillTerminalItemIndex = i;
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Failed resolving stale drill terminal index: {e.Message}");
            }

            return false;
        }

        private static bool TerminalIndexLooksLikeDrill(Terminal terminal, int index)
        {
            if (terminal == null || index < 0)
                return false;

            Item[] buyableItems = terminal.buyableItemsList;
            if (buyableItems == null || index >= buyableItems.Length)
                return true;

            Item item = buyableItems[index];
            if (item == null)
                return false;

            return item == BreachDrillItemRegistry.ItemProperties
                   || item.spawnPrefab == BreachDrillItemRegistry.Prefab
                   || string.Equals(item.itemName, "Shadow Raid Drill", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(item.itemName, "Blacksite Drill", StringComparison.OrdinalIgnoreCase);
        }

        internal static void AddObjectiveMarkers(List<MoonContractObjectiveMarker> markers)
        {
            if (markers == null)
                return;
            if (!TryGetPlacement(out InteriorTileSelection selection, out List<InteriorApproachAnchor> approaches, emitLog: false))
                return;

            markers.Add(new MoonContractObjectiveMarker
            {
                Key = "shadow-raid:room",
                IsArea = true,
                Label = "SHADOW RAID ROOM",
                Position = selection.FloorPosition + Vector3.up * 0.85f,
                Radius = 1.75f,
                ColorKind = 1,
                IsComplete = false,
            });

            List<BreachContainerController> activeContainers = BreachContainerController.GetActiveContainersSnapshot();
            if (activeContainers.Count > 0)
            {
                for (int i = 0; i < activeContainers.Count; i++)
                {
                    BreachContainerController container = activeContainers[i];
                    if (container == null)
                        continue;

                    markers.Add(new MoonContractObjectiveMarker
                    {
                        Target = container,
                        Key = "container:" + container.GetInstanceID(),
                        Label = $"SHADOW RAID CONTAINER {i + 1}",
                        Position = container.transform.position + Vector3.up * 0.2f,
                        Radius = 1.2f,
                        ColorKind = 0,
                        IsComplete = container.IsOpen,
                    });
                }
            }
            else if (AuthoredInteriorPlacementStore.TryResolvePoses(
                         AuthoredInteriorPlacementKinds.BlacksiteContainer,
                         out List<AuthoredInteriorPlacementPose> canonicalContainers)
                     && canonicalContainers.Count > 0)
            {
                for (int i = 0; i < canonicalContainers.Count; i++)
                {
                    markers.Add(new MoonContractObjectiveMarker
                    {
                        IsArea = true,
                        Key = $"shadow-raid:placement:{i}",
                        Label = $"SHADOW RAID PLACEMENT {i + 1}",
                        Position = canonicalContainers[i].WorldPosition + Vector3.up * 0.2f,
                        Radius = 1.2f,
                        ColorKind = 0,
                        IsComplete = false,
                    });
                }
            }

            BreachDrillItem drill = FindActiveDrill();
            if (drill != null)
            {
                markers.Add(new MoonContractObjectiveMarker
                {
                    Target = drill,
                    Key = "drill:" + drill.GetInstanceID(),
                    Label = HardDriveFound ? "HARD DRIVE FOUND" : (drill.Attached ? "DRILL" : "DELIVERED DRILL"),
                    Position = drill.transform.position + Vector3.up * 0.7f,
                    Radius = 1.2f,
                    ColorKind = HardDriveFound ? 0 : 1,
                    IsComplete = HardDriveFound,
                });
            }
        }

        private static bool TrySpawnPrototype()
        {
            List<AuthoredInteriorPlacementPose> canonicalContainers = null;
            bool hasCanonicalContainers = AuthoredInteriorPlacementStore.TryResolvePoses(
                AuthoredInteriorPlacementKinds.BlacksiteContainer,
                out canonicalContainers) && canonicalContainers.Count > 0;
            if (!hasCanonicalContainers && !_loggedNoAuthoredContainerMatch)
            {
                _loggedNoAuthoredContainerMatch = true;
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] No authored Shadow Raid container placement matched this generated interior. Falling back to procedural container placement.");
            }

            if (TryGetPlacement(out InteriorTileSelection selection, out List<InteriorApproachAnchor> approaches, emitLog: true))
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.ShadowRaid] Chosen Shadow Raid room '{selection.TileName}' with {approaches.Count} approach(s) at center {FormatVector(selection.FloorPosition)}.");
            }
            else if (hasCanonicalContainers)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.ShadowRaid] Spawning from {canonicalContainers.Count} resolved authored container pose(s); room marker selection was unavailable.");
            }

            bool progressed = false;
            if (!_containersSpawned)
            {
                int spawnedContainers = hasCanonicalContainers
                    ? SpawnContainers(canonicalContainers)
                    : SpawnContainersProcedurally();
                _containersSpawned = spawnedContainers > 0;
                progressed |= _containersSpawned;
                if (!_containersSpawned)
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] No Shadow Raid containers were spawned this attempt; will retry while the contract is active.");
            }

            // #949 S11H U1: the reward stage runs only behind this round's own container stage.
            // It used to run on every attempt, including the attempts where the container stage
            // spawned nothing, and it read the global container registry: on Test 3 that is how
            // March's drive ended up in an Assurance crate and _rewardSpawned latched true with
            // March's own containers still empty.
            if (!_rewardSpawned && _containersSpawned)
            {
                _rewardSpawned = SpawnRewardPropsInsideContainers();
                progressed |= _rewardSpawned;
                if (!_rewardSpawned)
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Shadow Raid container cache prop was not spawned this attempt; will retry while the contract is active.");
            }

            return progressed;
        }

        private static void EnsureDrillDelivery()
        {
            if (!IsShadowRaidContractActive())
            {
                CancelPendingDrillDelivery("delivery requested while contract inactive");
                return;
            }
            if (_drillDelivered || _drillDeliveryRequested)
                return;
            if (Time.time < _nextDrillSpawnAttemptAt)
                return;

            var start = StartOfRound.Instance;
            if (start == null || !start.shipHasLanded)
                return;

            // Every early return below used to re-run on the next frame, so a delivery that had to wait
            // paid a FindObjectOfType<Terminal> and a LogWarning per frame. Back the whole retry off.
            _nextDrillSpawnAttemptAt = Time.time + 0.5f;

            var dropship = FindDropship();
            if (dropship == null)
            {
                if (!_loggedMissingDropship)
                {
                    _loggedMissingDropship = true;
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] ItemDropship was not found; Shadow Raid drill dropship delivery is waiting for a dropship instance.");
                }

                return;
            }

            if (!dropship.IsServer)
                return;

            Terminal terminal = FindTerminal();
            if (!EnsureDrillTerminalDeliveryItem(terminal, out int drillItemIndex))
            {
                if (!_loggedUnavailableDrillItem)
                {
                    _loggedUnavailableDrillItem = true;
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Could not queue Shadow Raid drill through dropship because the drill item was not available.");
                }

                _nextDrillSpawnAttemptAt = Time.time + 2f;
                return;
            }

            _drillDeliveryRequested = true;
            _drillPendingDropshipSpawn = true;
            _drillDeliveryRequestedAt = Time.time;
            _nextDrillSpawnAttemptAt = Time.time + 1f;

            bool dropshipInActiveDelivery = dropship.deliveringOrder || dropship.shipLanded;
            bool pendingTerminalDelivery = terminal != null
                && terminal.orderedItemsFromTerminal != null
                && terminal.orderedItemsFromTerminal.Count > 0;
            bool pendingVehicleDelivery = terminal != null && terminal.orderedVehicleFromTerminal != -1;

            if (dropshipInActiveDelivery)
            {
                if (DropshipDoorsOpen(dropship))
                {
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Dropship doors were already open before Shadow Raid drill cargo could be queued; using fallback dropship spawn.");
                    TrySpawnPendingDrillAtDropship(dropship, "active delivery already open");
                    return;
                }

                if (QueueDrillInDropshipCargoList(dropship, drillItemIndex, "active delivery"))
                    ContractsBootstrap.Log?.LogInfo("[MoonContracts.ShadowRaid] Shadow Raid drill queued into active dropship cargo.");
                return;
            }

            if (pendingVehicleDelivery && !pendingTerminalDelivery)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Dropship is busy with a vehicle delivery; Shadow Raid drill will wait for the item dropship.");
                _drillDeliveryRequested = false;
                _drillPendingDropshipSpawn = false;
                _nextDrillSpawnAttemptAt = Time.time + 2f;
                return;
            }

            QueueDrillInTerminalOrder(terminal, drillItemIndex, pendingTerminalDelivery ? "pending delivery" : "new delivery");

            try
            {
                if (_dropshipLandMethod == null)
                    _dropshipLandMethod = AccessTools.Method(typeof(ItemDropship), "LandShipOnServer");

                _dropshipLandMethod?.Invoke(dropship, null);
                _drillQueuedInVanillaDropship = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.ShadowRaid] Shadow Raid drill dropship delivery requested through vanilla cargo; will spawn on door open.");
            }
            catch (Exception e)
            {
                _drillPendingDropshipSpawn = false;
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Drill dropship request failed: {e.Message}");
            }
        }

        private static void EnsureDrillFallbackSpawn()
        {
            if (!IsShadowRaidContractActive())
            {
                CancelPendingDrillDelivery("fallback spawn requested while contract inactive");
                return;
            }
            if (!_drillDeliveryRequested || _drillDelivered)
                return;

            // Safety-net poll only. The primary delivery path is the door-open postfix
            // (SurveyDropshipOpenPatch -> TrySpawnDrillFromDropship). Running the expensive
            // FindObjectOfType / Resources.FindObjectsOfTypeAll lookups below every frame while
            // the dropship is in the scene is what drove the ~130 -> ~30 FPS drop, so throttle it.
            if (Time.time < _nextDrillFallbackPollAt)
                return;
            _nextDrillFallbackPollAt = Time.time + 0.5f;

            var start = StartOfRound.Instance;
            if (start == null || !start.shipHasLanded)
                return;

            var dropship = FindDropship();
            bool timedOut = _drillDeliveryRequestedAt > 0f && Time.time - _drillDeliveryRequestedAt >= 12f;

            // The vanilla-cargo drill is only instantiated when the dropship doors open, so the
            // Resources.FindObjectsOfTypeAll scan inside TryFinalizeDropshipSpawnedDrill cannot
            // find anything before then. Polling it anyway every 0.5s while the delivery waited
            // owned the sustained 30-48ms UpdateRuntime hitches measured on 2026-08-04.
            // #1107: the 12s timeout used to re-open that same gate PERMANENTLY, so an undelivered
            // drill put the scan straight back on the 0.5s cadence for the rest of the round (60
            // "Slow contract runtime pass" lines at 18.6-65.7ms, 2026-09-16 Test 3). The gate is now
            // edge-triggered instead of level-triggered: the scan runs on the closed->open door
            // transition, and on the single pass in which the timeout first expires. Nothing new
            // can appear between those edges - vanilla instantiates the whole cargo list inside
            // OpenShipDoorsOnServer, before shipDoorsOpened is set - so the steady path keeps only
            // the cheap BreachDrillItem.FindActive() registry check below.
            bool doorsOpen = dropship != null && DropshipDoorsOpen(dropship);
            bool doorsJustOpened = doorsOpen && !_drillFallbackDoorsSeenOpen;
            _drillFallbackDoorsSeenOpen = doorsOpen;
            bool timeoutJustExpired = timedOut && !_drillDeliveryTimeoutHandled;
            _drillDeliveryTimeoutHandled |= timedOut;

            bool finalizeScanAllowed = dropship != null && (doorsJustOpened || timeoutJustExpired);
            if (!finalizeScanAllowed && !doorsOpen && dropship != null && !_loggedFallbackScanGate)
            {
                _loggedFallbackScanGate = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.ShadowRaid] Drill fallback poll idling until the dropship doors open; per-poll object scans suppressed (#267, #1107).");
            }

            if (finalizeScanAllowed && TryFinalizeDropshipSpawnedDrill(dropship, "runtime poll"))
                return;

            if (FindActiveDrill() != null)
                return;

            if (_drillPendingDropshipSpawn && dropship != null)
            {
                if (_drillQueuedInVanillaDropship && !timedOut)
                    return;

                TrySpawnPendingDrillAtDropship(dropship, timedOut ? "dropship timeout" : "runtime poll");
                if (_drillDelivered)
                    return;

                // #1107: the 2026-09-16 round ended with drills=0 and no line saying why. This is
                // deliberately NOT behind BlacksiteDiagnostics: the shipped log has to be able to
                // explain an undelivered drill. It rides the same one-shot timeout edge as the scan
                // above, so it is emitted at most once per delivery request - a cancellation
                // re-arms the edge along with the rest of the delivery state.
                if (timeoutJustExpired)
                {
                    ContractsBootstrap.Log?.LogWarning(
                        "[MoonContracts.ShadowRaid] Shadow Raid drill still undelivered 12s after the delivery request; "
                        + $"doorsOpen={doorsOpen}, landed={dropship.shipLanded}, delivering={dropship.deliveringOrder}, "
                        + $"queuedInVanillaCargo={_drillQueuedInVanillaDropship}, pendingDropshipSpawn={_drillPendingDropshipSpawn}. "
                        + "The fallback spawn needs open dropship doors; when they are open, the placement failure is "
                        + "reported by SpawnDrillAtPositions instead. The poll now waits for the next door-open edge "
                        + "instead of rescanning every 0.5s (#1107).");
                }

                if (_drillPendingDropshipSpawn)
                    return;
            }

            if (timedOut && dropship == null)
            {
                _drillPendingDropshipSpawn = false;
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Shadow Raid drill dropship delivery missed because the ItemDropship disappeared before opening; no fallback drill was spawned.");
            }
        }

        private static bool TrySpawnPendingDrillAtDropship(ItemDropship dropship, string reason)
        {
            if (!IsShadowRaidContractActive())
            {
                CancelPendingDrillDelivery("dropship spawn requested while contract inactive", dropship);
                return false;
            }
            if (dropship == null || !dropship.IsServer || _drillDelivered || !_drillPendingDropshipSpawn)
                return false;

            bool doorOpenCallback = reason != null
                && reason.StartsWith("door open", StringComparison.OrdinalIgnoreCase);
            bool doorsOpen = doorOpenCallback || DropshipDoorsOpen(dropship);
            if (!doorsOpen)
                return false;

            if (Time.time < _nextDrillSpawnAttemptAt && !doorOpenCallback)
                return false;

            _nextDrillSpawnAttemptAt = Time.time + 1.5f;
            LogDropshipSpawnState(dropship, reason);
            bool spawned = SpawnDrillAtPositions(dropship.itemSpawnPositions, StartOfRound.Instance?.propsContainer);
            if (spawned)
            {
                _drillPendingDropshipSpawn = false;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Shadow Raid drill spawned into dropship via {reason}.");
            }

            return spawned;
        }

        private static bool SpawnDrillAtPositions(IList<Transform> spawnPositions, Transform parent)
        {
            if (!IsShadowRaidContractActive())
                return false;
            if (_drillDelivered)
                return true;

            if (!TryGetDrillDeliveryPrefab(out GameObject drillPrefab, out Item drillItemProperties))
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Could not prepare Shadow Raid drill delivery prefab.");
                return false;
            }

            Vector3 fallback = StartOfRound.Instance != null
                ? StartOfRound.Instance.elevatorTransform.position + StartOfRound.Instance.elevatorTransform.forward * 1.8f
                : Vector3.zero;
            Vector3 position = fallback + new Vector3(0f, 0.35f, 0f);
            if (spawnPositions != null && spawnPositions.Count > 0)
            {
                position = GetDropshipItemSpawnPosition(spawnPositions);
            }
            position += Vector3.up * BreachDrillItemRegistry.GroundVerticalOffset;

            if (!TrySpawnDrillItemInstance(drillPrefab, position, Quaternion.identity, parent, out NetworkObject spawnedObject, dynamicDelivery: true))
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Shadow Raid drill spawn attempt failed at {FormatVector(position)}.");
                return false;
            }

            if (spawnedObject != null)
            {
                PrepareSpawnedDrillForPickup(spawnedObject.gameObject, dynamicDelivery: true);
                // #949 S11H U1
                TrackSpawnedObject(spawnedObject, ShadowRaidObjectKind.Drill);
                LogSpawnedDrillPickupState(spawnedObject.gameObject);
            }

            var drill = spawnedObject != null
                ? spawnedObject.GetComponent<BreachDrillItem>()
                : null;
            if (drill != null)
                drill.InitializeRuntime(MoonContractState.GetShadowRaidDrillDurationSecondsForCurrentMoon(), drillItemProperties?.itemName ?? "Shadow Raid Drill");

            _drillDelivered = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Shadow Raid drill delivered at {FormatVector(position)}.");
            return true;
        }

        private static bool TryGetDrillDeliveryPrefab(out GameObject prefab, out Item itemProperties)
        {
            return BreachDrillItemRegistry.TryGet(out prefab, out itemProperties);
        }

        private static bool EnsureDrillTerminalDeliveryItem(Terminal terminal, out int itemIndex)
        {
            itemIndex = -1;
            if (terminal == null)
                return false;
            if (!TryGetDrillDeliveryPrefab(out GameObject drillPrefab, out Item drillItemProperties)
                || drillPrefab == null
                || drillItemProperties == null)
            {
                return false;
            }

            Item[] buyableItems = terminal.buyableItemsList ?? Array.Empty<Item>();
            for (int i = 0; i < buyableItems.Length; i++)
            {
                Item item = buyableItems[i];
                if (item == null)
                    continue;

                if (item == drillItemProperties
                    || item.spawnPrefab == drillPrefab
                    || string.Equals(item.itemName, drillItemProperties.itemName, StringComparison.OrdinalIgnoreCase))
                {
                    item.spawnPrefab = drillPrefab;
                    itemIndex = i;
                    _drillTerminalItemIndex = i;
                    // Someone else may have grown buyableItemsList without widening the sale
                    // table. Absent provider: nothing is resized and the terminal owner's own
                    // SetItemSales guard is the backstop — see ISalesCapacityService.
                    SalesCapacity.EnsureCapacity(
                        buyableItems.Length,
                        terminal.buyableVehicles != null ? terminal.buyableVehicles.Length : 0);
                    return true;
                }
            }

            itemIndex = buyableItems.Length;
            Item[] expandedItems = new Item[itemIndex + 1];
            Array.Copy(buyableItems, expandedItems, buyableItems.Length);
            expandedItems[itemIndex] = drillItemProperties;
            terminal.buyableItemsList = expandedItems;
            // Append-then-ensure: the item is already in buyableItemsList by the time this
            // runs, so an absent provider cannot refuse the append — it only means the item
            // ships without a sale-percentage slot. Core registers both the provider
            // and the rollover guard independently of the optional custom terminal.
            if (!SalesCapacity.EnsureCapacityForAppendedItem(
                    buyableItems.Length,
                    terminal.buyableVehicles != null ? terminal.buyableVehicles.Length : 0))
            {
                ContractsBootstrap.Log?.LogInfo(
                    "[MoonContracts.ShadowRaid] Drill added without a sale-percentage slot; no " +
                    "sales-capacity provider is registered.");
            }
            _drillTerminalItemIndex = itemIndex;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Added Shadow Raid drill to hidden dropship delivery item list at terminal index {itemIndex}.");
            return true;
        }

        private static void QueueDrillInTerminalOrder(Terminal terminal, int itemIndex, string reason)
        {
            if (terminal == null || itemIndex < 0)
                return;

            if (terminal.orderedItemsFromTerminal == null)
                terminal.orderedItemsFromTerminal = new List<int>();

            if (!terminal.orderedItemsFromTerminal.Contains(itemIndex))
                terminal.orderedItemsFromTerminal.Add(itemIndex);

            _drillQueuedInVanillaDropship = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Queued Shadow Raid drill terminal item index {itemIndex} for {reason}.");
        }

        private static bool QueueDrillInDropshipCargoList(ItemDropship dropship, int itemIndex, string reason)
        {
            if (dropship == null || itemIndex < 0)
                return false;

            try
            {
                if (_dropshipItemsToDeliverField == null)
                    _dropshipItemsToDeliverField = AccessTools.Field(typeof(ItemDropship), "itemsToDeliver");

                if (_dropshipItemsToDeliverField == null)
                    return false;

                var itemsToDeliver = _dropshipItemsToDeliverField.GetValue(dropship) as List<int>;
                if (itemsToDeliver == null)
                {
                    itemsToDeliver = new List<int>();
                    _dropshipItemsToDeliverField.SetValue(dropship, itemsToDeliver);
                }

                if (!itemsToDeliver.Contains(itemIndex))
                    itemsToDeliver.Add(itemIndex);

                _drillQueuedInVanillaDropship = true;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Queued Shadow Raid drill dropship cargo item index {itemIndex} for {reason}.");
                return true;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Failed to queue Shadow Raid drill in active dropship cargo: {e.Message}");
                return false;
            }
        }

        private static bool TryFinalizeDropshipSpawnedDrill(ItemDropship dropship, string reason)
        {
            if (dropship == null || _drillDelivered || !_drillPendingDropshipSpawn)
                return false;

            BreachDrillItem drill = FindDropshipDrill(dropship);
            if (drill == null)
                return false;

            GameObject drillObject = drill.gameObject;
            if (!drillObject.activeSelf)
                drillObject.SetActive(true);

            PositionDeliveredDrillInDropship(drillObject, dropship);
            PrepareSpawnedDrillForPickup(drillObject, dynamicDelivery: true);

            NetworkObject networkObject = drillObject.GetComponent<NetworkObject>();
            // #949 S11H U1
            TrackSpawnedObject(networkObject, ShadowRaidObjectKind.Drill);

            if (TryGetDrillDeliveryPrefab(out _, out Item drillItemProperties))
                drill.InitializeRuntime(MoonContractState.GetShadowRaidDrillDurationSecondsForCurrentMoon(), drillItemProperties?.itemName ?? "Shadow Raid Drill");

            _drillPendingDropshipSpawn = false;
            _drillQueuedInVanillaDropship = false;
            _drillDelivered = true;
            LogSpawnedDrillPickupState(drillObject);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Shadow Raid drill delivered through vanilla dropship cargo via {reason} at {FormatVector(drillObject.transform.position)}.");
            return true;
        }

        private static BreachDrillItem FindDropshipDrill(ItemDropship dropship)
        {
            if (dropship == null)
                return null;

            BreachDrillItem best = null;
            float bestDistance = float.MaxValue;
            BreachDrillItem[] drills = Resources.FindObjectsOfTypeAll<BreachDrillItem>();
            for (int i = 0; i < drills.Length; i++)
            {
                BreachDrillItem drill = drills[i];
                if (drill == null || drill.gameObject == BreachDrillItemRegistry.Prefab)
                    continue;
                if (!drill.gameObject.scene.IsValid())
                    continue;

                NetworkObject networkObject = drill.GetComponent<NetworkObject>();
                if (networkObject == null || !networkObject.IsSpawned)
                    continue;

                float distance = Vector3.Distance(drill.transform.position, dropship.transform.position);
                if (distance > 24f || distance >= bestDistance)
                    continue;

                best = drill;
                bestDistance = distance;
            }

            return best;
        }

        private static void PositionDeliveredDrillInDropship(GameObject drillObject, ItemDropship dropship)
        {
            if (drillObject == null || dropship == null)
                return;

            Vector3 position = GetDropshipItemSpawnPosition(dropship.itemSpawnPositions)
                + Vector3.up * BreachDrillItemRegistry.GroundVerticalOffset;
            drillObject.transform.SetPositionAndRotation(
                position,
                Quaternion.Euler(0f, dropship.transform.eulerAngles.y, 0f));
        }

        private static bool TrySpawnDrillItemInstance(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent, out NetworkObject spawnedObject, bool dynamicDelivery = false)
        {
            spawnedObject = null;
            if (prefab == null)
                return false;

            GameObject instance = UnityEngine.Object.Instantiate(prefab, position, rotation, parent);
            if (instance == null)
                return false;

            if (!instance.activeSelf)
                instance.SetActive(true);

            PrepareSpawnedDrillForPickup(instance, dynamicDelivery);
            spawnedObject = instance.GetComponent<NetworkObject>();
            if (spawnedObject == null)
            {
                ContractsBootstrap.Log?.LogError("[MoonContracts.ShadowRaid] Shadow Raid drill prefab has no NetworkObject; the drill would exist on the host only. Aborting spawn.");
                UnityEngine.Object.Destroy(instance);
                return false;
            }

            if (!spawnedObject.IsSpawned)
                spawnedObject.Spawn();
            if (instance.transform.parent != null)
                spawnedObject.TrySetParent(instance.transform.parent);

            return true;
        }

        private static BreachDrillItem FindActiveDrill()
        {
            return BreachDrillItem.FindActive();
        }

        private static bool TryGetPlacement(out InteriorTileSelection selection, out List<InteriorApproachAnchor> approaches, bool emitLog)
        {
            if (_hasCachedPlacement && _cachedApproaches != null)
            {
                selection = _cachedSelection;
                approaches = _cachedApproaches;
                return true;
            }

            selection = default;
            approaches = null;
            if (!InteriorAnchorService.TryGetInteriorTile(InteriorSelectionProfile.ShadowRaid, out selection))
                return false;

            approaches = InteriorApproachDetector.FindApproaches(selection, emitLog);
            _cachedSelection = selection;
            _cachedApproaches = approaches;
            _hasCachedPlacement = true;
            return true;
        }

        private static int SpawnContainers(List<AuthoredInteriorPlacementPose> authoredContainers)
        {
            if (authoredContainers == null || authoredContainers.Count == 0)
                return 0;

            if (!BreachContainerController.TryGetContainerPrefab(ContractsBootstrap.ShadowRaidContainerPrefabName.Value, out GameObject containerPrefab))
            {
                MarkAuthoredContainerPosesUnused(
                    authoredContainers,
                    null,
                    "consumer=blacksite-container reason=prefab-unavailable");
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Failed to resolve the Shadow Raid shipping container prefab.");
                return 0;
            }

            List<AuthoredInteriorPlacementPose> selectedContainers = SelectAuthoredContainerPoses(authoredContainers);
            MarkAuthoredContainerPosesUnused(
                authoredContainers,
                selectedContainers,
                "consumer=blacksite-container reason=seed-selection-or-cap");
            int spawned = 0;
            for (int i = 0; i < selectedContainers.Count; i++)
            {
                AuthoredInteriorPlacementPose pose = selectedContainers[i];
                BlacksiteContainerPlacementMetadata metadata =
                    AuthoredInteriorPlacementMetadata.ReadContainer(pose.Record?.metadataJson);
                string containerSize = BreachContainerController.NormalizeContainerSizeName(metadata.containerSize);
                Vector3 spawnPosition = pose.WorldPosition;
                Quaternion spawnRotation = Quaternion.Euler(0f, pose.WorldRotation.eulerAngles.y, 0f);
                if (!InteriorInjectionService.TrySpawnPrefabInstance(containerPrefab, spawnPosition, spawnRotation, out NetworkObject spawnedObject))
                {
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        pose.Record,
                        "consumer=blacksite-container reason=spawn-failed");
                    continue;
                }

                GameObject containerObject = spawnedObject != null ? spawnedObject.gameObject : null;
                if (containerObject != null)
                {
                    containerObject.SetActive(true);
                    var container = containerObject.GetComponent<BreachContainerController>();
                    if (container != null)
                        container.ConfigureRuntime(containerSize);

                    // #949 S11H U1: this round's placement result, which is the only set the
                    // reward stage may configure a cache into.
                    TrackSpawnedObject(spawnedObject, ShadowRaidObjectKind.Container);
                    if (spawnedObject != null)
                        PlacedContainerIdsThisRound.Add(spawnedObject.NetworkObjectId);
                }

                spawned++;
                AuthoredPlacementRoundReport.RecordSpawned(pose.Record);
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.ShadowRaid] Spawned authored Shadow Raid container {spawned}/{selectedContainers.Count} id='{pose.Record?.objectId}' size={containerSize} source={pose.Source} at {FormatVector(spawnPosition)}.");
            }

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.ShadowRaid] Spawned {spawned} authored Shadow Raid container(s) from {authoredContainers.Count} available placement(s).");
            return spawned;
        }

        /// <summary>
        /// Procedural fallback used only when no authored Blacksite container pose resolved for
        /// this generated interior. The placer reserves body + drill strip before this method
        /// spawns; a failed spawn rolls the claim back. Sizes are chosen by the placer (medium
        /// preferred, downgrading to small only when medium cannot fit; large stays
        /// authored-only) and applied through the same ConfigureRuntime/ApplyContainerSize path
        /// the authored spawns use.
        /// </summary>
        private static int SpawnContainersProcedurally()
        {
            if (Time.time < _nextProceduralContainerSearchAt)
                return 0;

            if (!BreachContainerController.TryGetContainerPrefab(ContractsBootstrap.ShadowRaidContainerPrefabName.Value, out GameObject containerPrefab))
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Failed to resolve the Shadow Raid shipping container prefab.");
                return 0;
            }

            int targetCount = ResolveContainerCountForCurrentMoon(int.MaxValue);
            var placements = new List<BreachContainerPlacement>();
            if (!BreachContainerPlacer.TryResolveBudgeted(targetCount, placements, out bool scanComplete))
            {
                // A still-in-flight scan resumes on the next 1.5 s spawn tick; only a completed
                // scan that produced nothing backs off. The full scan is physics-heavy and a
                // facility with no fitting room will not grow one in 1.5 seconds, so back off
                // well past the room-spawn retry cadence.
                if (!scanComplete)
                    return 0;

                _nextProceduralContainerSearchAt = Time.time + 30f;
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Procedural container search found no valid placement this attempt; next search in 30s.");
                return 0;
            }

            int spawned = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                BreachContainerPlacement placement = placements[i];
                if (!InteriorInjectionService.TrySpawnPrefabInstance(containerPrefab, placement.Position, placement.Rotation, out NetworkObject spawnedObject))
                {
                    BreachContainerPlacer.ReleaseReservation(placement);
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.ShadowRaid] Procedural Shadow Raid container spawn failed in '{placement.TileName}'; reservation released.");
                    continue;
                }

                GameObject containerObject = spawnedObject != null ? spawnedObject.gameObject : null;
                if (containerObject != null)
                {
                    containerObject.SetActive(true);
                    var container = containerObject.GetComponent<BreachContainerController>();
                    if (container != null)
                        container.ConfigureRuntime(placement.SizeName);

                    // #949 S11H U1: see SpawnContainers.
                    TrackSpawnedObject(spawnedObject, ShadowRaidObjectKind.Container);
                    if (spawnedObject != null)
                        PlacedContainerIdsThisRound.Add(spawnedObject.NetworkObjectId);
                }

                spawned++;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.ShadowRaid] Spawned procedural Shadow Raid container {spawned}/{placements.Count} size={placement.SizeName} tile='{placement.TileName}' score={placement.Score:0.0} at {FormatVector(placement.Position)}.");
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Spawned {spawned} procedural Shadow Raid container(s) from {placements.Count} resolved placement(s).");
            // Placements resolved but every networked spawn failed: without the backoff this
            // exit would re-run the physics-heavy scan on every 1.5 s spawn tick.
            if (spawned == 0)
                _nextProceduralContainerSearchAt = Time.time + 30f;
            return spawned;
        }

        private static bool SpawnRewardPropsInsideContainers()
        {
            if (!BreachHardDriveItemRegistry.TryGet(out Item hardDriveItem))
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Failed to resolve the Shadow Raid hard drive item.");
                return false;
            }

            // #949 S11H U1: this round's placement result, not the global snapshot.
            List<BreachContainerController> containers = ResolveRewardCandidateContainers();
            if (containers.Count == 0)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Cannot spawn Shadow Raid loot because round {ShadowRaidRoundRegistry.CurrentRound} has no live container of its own (placed={PlacedContainerIdsThisRound.Count}).");
                return false;
            }

            int objectiveIndex = SelectObjectiveContainerIndex(containers.Count);
            Item goldBarItem = ResolveGoldBarItem();
            if (containers.Count > 1 && goldBarItem == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Cannot configure non-objective embedded caches because the Gold bar item was not found.");
                return false;
            }

            int spawned = 0;
            bool hardDriveConfigured = false;
            bool allCachesConfigured = true;
            for (int i = 0; i < containers.Count; i++)
            {
                BreachContainerController container = containers[i];
                if (container == null)
                    continue;

                ShadowRaidCargoCrate crate = container.EnsureEmbeddedCacheServer(out NetworkObject cacheNetworkObject);
                if (crate == null)
                    continue;

                // #949 S11H U1
                TrackSpawnedObject(cacheNetworkObject, ShadowRaidObjectKind.Crate);

                bool objectiveContainer = i == objectiveIndex;
                Item containedItem = objectiveContainer
                    ? hardDriveItem
                    : goldBarItem;
                bool configured = false;
                if (crate != null && containedItem != null)
                {
                    configured = crate.ConfigureContainedItemServer(containedItem, objectiveContainer);
                    if (objectiveContainer && configured)
                    {
                        hardDriveConfigured = true;
                        // #949 S11H U1: the drive is spawned inside the crate, so this is the
                        // first frame the host knows its id. Only the objective drive is
                        // tracked: the non-objective caches hold vanilla scrap, which the game
                        // owns and the round reset has no business despawning.
                        TrackConfiguredDrive(crate);
                    }
                }
                if (!configured)
                    allCachesConfigured = false;

                spawned++;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.ShadowRaid] Configured embedded military cache in Shadow Raid container {i + 1} with {(objectiveContainer ? "hard drive" : "Gold bar")} (configured={configured}) at {FormatVector(crate.transform.position)}.");
            }

            if (!hardDriveConfigured)
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] Embedded military cache props were prepared, but the objective hard drive was not configured.");
            if (!allCachesConfigured)
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.ShadowRaid] One or more embedded military caches did not receive their required contained item.");

            return spawned > 0 && hardDriveConfigured && allCachesConfigured;
        }

        /// <summary>
        /// #949 S11H U1: the containers the reward stage may configure, resolved through the
        /// pure rule the console fixture pins (ShadowRaidRoundRegistry.RewardCandidates): this
        /// round's own placement result, in placement order, intersected with what is still a
        /// live container. The global snapshot stays where it belongs, on the callers that ask
        /// "which containers exist right now" (TryGetBlacksiteRetrievalTarget and
        /// AddObjectiveMarkers), which the round token now answers correctly on its own.
        /// </summary>
        private static List<BreachContainerController> ResolveRewardCandidateContainers()
        {
            var candidates = new List<BreachContainerController>();
            if (PlacedContainerIdsThisRound.Count == 0)
                return candidates;

            List<BreachContainerController> live = BreachContainerController.GetActiveContainersSnapshot();
            var liveIds = new List<ulong>(live.Count);
            for (int i = 0; i < live.Count; i++)
            {
                if (live[i] != null && live[i].NetworkId != 0UL)
                    liveIds.Add(live[i].NetworkId);
            }

            List<ulong> candidateIds = ShadowRaidRoundRegistry.RewardCandidates(PlacedContainerIdsThisRound, liveIds);
            for (int i = 0; i < candidateIds.Count; i++)
            {
                BreachContainerController container = BreachContainerController.FindByNetworkId(candidateIds[i]);
                if (container != null)
                    candidates.Add(container);
            }

            return candidates;
        }

        /// <summary>
        /// #949 S11H U1: joins the objective drive to the round, so the next run reset despawns
        /// it instead of leaving it lying in an unloaded dungeon. A drive the crew is carrying
        /// or has stowed in the ship is skipped by the purge itself, not here.
        /// </summary>
        private static void TrackConfiguredDrive(ShadowRaidCargoCrate crate)
        {
            ulong driveId = crate != null ? crate.ContainedItemNetworkId : 0UL;
            if (driveId == 0UL)
                return;

            NetworkManager network = NetworkManager.Singleton;
            if (network?.SpawnManager == null
                || !network.SpawnManager.SpawnedObjects.TryGetValue(driveId, out NetworkObject driveObject))
            {
                return;
            }

            TrackSpawnedObject(driveObject, ShadowRaidObjectKind.Drive);
        }

        private static List<AuthoredInteriorPlacementPose> SelectAuthoredContainerPoses(List<AuthoredInteriorPlacementPose> authoredContainers)
        {
            if (authoredContainers == null || authoredContainers.Count == 0)
                return new List<AuthoredInteriorPlacementPose>();

            int targetCount = ResolveContainerCountForCurrentMoon(authoredContainers.Count);
            int seed = GetRoundContainerSeed();
            return authoredContainers
                .Select((pose, index) => new { Pose = pose, Sort = StableHash($"{pose.Record?.objectId}|{pose.Source}|{FormatVector(pose.WorldPosition)}|{index}") ^ seed })
                .OrderBy(entry => entry.Sort)
                .Take(targetCount)
                .Select(entry => entry.Pose)
                .ToList();
        }

        private static void MarkAuthoredContainerPosesUnused(
            IReadOnlyList<AuthoredInteriorPlacementPose> available,
            IReadOnlyList<AuthoredInteriorPlacementPose> selected,
            string reason)
        {
            if (available == null)
                return;

            for (int availableIndex = 0; availableIndex < available.Count; availableIndex++)
            {
                AuthoredInteriorPlacementPose candidate = available[availableIndex];
                bool wasSelected = false;
                if (selected != null)
                {
                    for (int selectedIndex = 0; selectedIndex < selected.Count; selectedIndex++)
                    {
                        if (ReferenceEquals(selected[selectedIndex].Record, candidate.Record))
                        {
                            wasSelected = true;
                            break;
                        }
                    }
                }

                if (!wasSelected)
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(candidate.Record, reason);
            }
        }

        private static int ResolveContainerCountForCurrentMoon(int availableCount)
        {
            if (availableCount <= 0)
                return 0;

            return MoonContractState.GetShadowRaidContainerCountForCurrentMoon(availableCount);
        }

        private static int SelectObjectiveContainerIndex(int containerCount)
        {
            if (containerCount <= 1)
                return 0;

            int seed = GetRoundContainerSeed() ^ 0x5f3759df;
            int positive = seed == int.MinValue ? 0 : Mathf.Abs(seed);
            return positive % containerCount;
        }

        private static int GetRoundContainerSeed()
        {
            var start = StartOfRound.Instance;
            int seed = start?.randomMapSeed ?? 0;
            SelectableLevel level = start?.currentLevel;
            if (level != null)
            {
                seed ^= level.levelID * 73856093;
                seed ^= StableHash(level.PlanetName);
                seed ^= StableHash(level.riskLevel);
            }

            return seed;
        }

        private static Item ResolveGoldBarItem()
        {
            var items = StartOfRound.Instance?.allItemsList?.itemsList;
            if (items == null || items.Count == 0)
                return null;

            Item exact = items.FirstOrDefault(item => IsSpawnableItem(item) && string.Equals(item.itemName, "Gold bar", StringComparison.OrdinalIgnoreCase))
                         ?? items.FirstOrDefault(item => IsSpawnableItem(item) && string.Equals(item.itemName, "GoldBar", StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            Item named = items.FirstOrDefault(item => IsSpawnableItem(item)
                                                      && item.isScrap
                                                      && Contains(item.itemName, "gold")
                                                      && Contains(item.itemName, "bar"));
            if (named != null)
                return named;

            return null;
        }

        private static bool IsSpawnableItem(Item item)
        {
            return item != null
                && item.spawnPrefab != null
                && item.spawnPrefab.GetComponent<GrabbableObject>() != null;
        }

        private static bool Contains(string value, string fragment)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = 23;
                if (value == null)
                    return hash;

                for (int i = 0; i < value.Length; i++)
                    hash = hash * 31 + value[i];

                return hash;
            }
        }

        private static bool DropshipDoorsOpen(ItemDropship dropship)
        {
            if (dropship == null)
                return false;

            try
            {
                if (_dropshipDoorsOpenedField == null)
                    _dropshipDoorsOpenedField = AccessTools.Field(typeof(ItemDropship), "shipDoorsOpened");

                return _dropshipDoorsOpenedField != null
                    && _dropshipDoorsOpenedField.GetValue(dropship) is bool open
                    && open;
            }
            catch
            {
                return false;
            }
        }

        private static Vector3 GetDropshipItemSpawnPosition(IList<Transform> spawnPositions)
        {
            if (spawnPositions == null || spawnPositions.Count == 0)
                return StartOfRound.Instance != null
                    ? StartOfRound.Instance.elevatorTransform.position + StartOfRound.Instance.elevatorTransform.forward * 1.8f + Vector3.up * 0.35f
                    : Vector3.zero;

            for (int i = 0; i < spawnPositions.Count; i++)
            {
                Transform spawn = spawnPositions[i];
                if (spawn == null)
                    continue;

                return spawn.position;
            }

            return StartOfRound.Instance != null
                ? StartOfRound.Instance.elevatorTransform.position + StartOfRound.Instance.elevatorTransform.forward * 1.8f + Vector3.up * 0.35f
                : Vector3.zero;
        }

        private static void LogDropshipSpawnState(ItemDropship dropship, string reason)
        {
            if (dropship == null)
                return;

            try
            {
                string slots = string.Empty;
                if (dropship.itemSpawnPositions != null)
                {
                    for (int i = 0; i < dropship.itemSpawnPositions.Length; i++)
                    {
                        Transform slot = dropship.itemSpawnPositions[i];
                        if (slot == null)
                            continue;

                        slots += string.IsNullOrEmpty(slots)
                            ? $"{i}:{FormatVector(slot.position)}"
                            : $" | {i}:{FormatVector(slot.position)}";
                    }
                }

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.ShadowRaid] Dropship drill spawn state via {reason}: ship={FormatVector(dropship.transform.position)}, landed={dropship.shipLanded}, doors={DropshipDoorsOpen(dropship)}, delivering={dropship.deliveringOrder}, slots={slots}.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.ShadowRaid] Failed logging dropship spawn state: {e.Message}");
            }
        }

        private static void LogSpawnedDrillPickupState(GameObject root)
        {
            if (root == null)
                return;

            PhysicsProp prop = root.GetComponent<PhysicsProp>();
            NetworkObject networkObject = root.GetComponent<NetworkObject>();
            Collider pickupCollider = null;
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || collider.isTrigger)
                    continue;

                pickupCollider = collider;
                break;
            }

            BoxCollider box = pickupCollider as BoxCollider;
            string colliderShape = box != null
                ? $", colliderCenter={FormatVector(box.center)}, colliderSize={FormatVector(box.size)}"
                : string.Empty;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Drill pickup state: pos={FormatVector(root.transform.position)}, parent={root.transform.parent?.name ?? "null"}, netSpawned={networkObject != null && networkObject.IsSpawned}, grabbable={prop != null && prop.grabbable}, heldServer={prop != null && prop.heldByPlayerOnServer}, itemSpawnsOnGround={prop?.itemProperties != null && prop.itemProperties.itemSpawnsOnGround}, verticalOffset={(prop?.itemProperties != null ? prop.itemProperties.verticalOffset : 0f):0.00}, hasHitGround={prop != null && prop.hasHitGround}, collider={(pickupCollider != null ? pickupCollider.name : "null")}, colliderTag={(pickupCollider != null ? pickupCollider.tag : "null")}, colliderLayer={(pickupCollider != null ? pickupCollider.gameObject.layer : -1)}{colliderShape}.");
        }

        private static void ConfigurePhysicsPropIdentity(GameObject root, GrabbableObject templateGrabbable)
        {
            if (root == null)
                return;

            int layer = GetTemplatePhysicsPropLayer(templateGrabbable);
            if (layer < 0)
                layer = 6;

            ApplyPhysicsPropTagAndLayer(root, layer);

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || collider.isTrigger)
                    continue;

                ApplyPhysicsPropTagAndLayer(collider.gameObject, layer);
            }
        }

        private static int GetTemplatePhysicsPropLayer(GrabbableObject templateGrabbable)
        {
            Collider[] templateColliders = templateGrabbable != null
                ? templateGrabbable.GetComponentsInChildren<Collider>(true)
                : null;

            if (templateColliders != null)
            {
                for (int i = 0; i < templateColliders.Length; i++)
                {
                    Collider collider = templateColliders[i];
                    if (collider == null || collider.isTrigger)
                        continue;
                    if (collider.CompareTag("PhysicsProp"))
                        return collider.gameObject.layer;
                }
            }

            if (templateGrabbable != null)
                return templateGrabbable.gameObject.layer;

            int propsLayer = LayerMask.NameToLayer("Props");
            if (propsLayer >= 0)
                return propsLayer;

            int interactableLayer = LayerMask.NameToLayer("InteractableObject");
            return interactableLayer >= 0 ? interactableLayer : 6;
        }

        private static void ApplyPhysicsPropTagAndLayer(GameObject target, int layer)
        {
            if (target == null)
                return;

            target.layer = layer;
            try
            {
                target.tag = "PhysicsProp";
            }
            catch
            {
                // PhysicsProp is a vanilla tag; leave the existing tag if another runtime removed it.
            }
        }

        private static void PrepareSpawnedDrillForPickup(GameObject root, bool dynamicDelivery = false)
        {
            if (root == null)
                return;

            var prop = root.GetComponent<PhysicsProp>();
            if (prop == null)
                return;

            ConfigurePhysicsPropIdentity(root, prop);
            prop.grabbable = true;
            prop.grabbableToEnemies = false;
            prop.isHeld = false;
            prop.isPocketed = false;
            prop.heldByPlayerOnServer = false;
            prop.playerHeldBy = null;
            prop.parentObject = null;
            prop.isInFactory = false;
            prop.scrapValue = 0;
            // Deliver kinematic and already "grounded" so BreachDrillItem.Update snaps it onto the floor via
            // its downward raycast, instead of a non-kinematic gravity body falling straight through the map
            // (which left the drill below the floor and unreachable). Matches how vanilla items rest on spawn.
            prop.fallTime = 1f;
            prop.hasHitGround = true;
            prop.reachedFloorTarget = true;
            prop.startFallingPosition = root.transform.localPosition;
            prop.targetFloorPosition = root.transform.localPosition;
            if (prop.itemProperties != null)
            {
                prop.itemProperties.verticalOffset = BreachDrillItemRegistry.GroundVerticalOffset;
                prop.itemProperties.itemSpawnsOnGround = true;
            }
            prop.EnablePhysics(enable: true);

            if (prop.propBody != null)
            {
                Rigidbody body = prop.propBody;
                if (!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }

                body.isKinematic = true;
                body.useGravity = false;
                body.constraints = RigidbodyConstraints.FreezeRotation;
            }
        }

        private static string FormatVector(Vector3 value)
        {
            return $"{value.x:0.0},{value.y:0.0},{value.z:0.0}";
        }
    }
}
