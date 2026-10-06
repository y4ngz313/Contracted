using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Contracts.Defuse;
using Y4NGZCompany.Contracts.Payload;
using Y4NGZCompany.Facility.Interior;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteHardDriveObjectiveService
    {
        private const float ExpectedTargetRadius = 1.75f;
        private const float DriveVerticalOffset = 0.14f;
        // #314 D8: the deterministic pose scan walks every inside AI node with a NavMesh sample and
        // a path-distance estimate. While the drive is unresolvable that ran at the mission refresh
        // rate, so a failure now backs off and a success is cached for the round.
        private const float TargetResolveRetrySeconds = 5f;
        // The resolve backoff is per mission: two missions sharing one static clock would let the
        // first one's failed attempt silence the second's retry for the whole window.
        private static readonly Dictionary<ulong, float> NextTargetResolveAttemptAt =
            new Dictionary<ulong, float>();
        private static float _nextDeterministicPoseAttemptAt;
        private static bool _hasCachedInteriorPose;
        private static int _cachedInteriorPoseSeed;
        private static int _cachedInteriorPoseNodeCount;
        private static Vector3 _cachedInteriorPosePosition;
        private static Quaternion _cachedInteriorPoseRotation = Quaternion.identity;
        private static readonly List<BlacksiteRetrievalMissionController> Missions =
            new List<BlacksiteRetrievalMissionController>();
        private static readonly List<BreachHardDriveItem> Drives =
            new List<BreachHardDriveItem>();

        internal static BlacksiteRetrievalMissionController ActiveMission
        {
            get
            {
                PruneRegistrations();
                for (int i = Missions.Count - 1; i >= 0; i--)
                {
                    BlacksiteRetrievalMissionController mission = Missions[i];
                    if (mission != null && mission.IsSpawned && mission.IsInitialized)
                        return mission;
                }
                return null;
            }
        }

        internal static void RegisterMission(BlacksiteRetrievalMissionController mission)
        {
            if (mission != null && !Missions.Contains(mission))
                Missions.Add(mission);
        }

        internal static void UnregisterMission(BlacksiteRetrievalMissionController mission)
        {
            Missions.Remove(mission);
        }

        internal static void RegisterDrive(BreachHardDriveItem drive)
        {
            if (drive != null && !Drives.Contains(drive))
                Drives.Add(drive);
        }

        internal static void UnregisterDrive(BreachHardDriveItem drive)
        {
            Drives.Remove(drive);
        }

        internal static bool TryGetDrive(
            BlacksiteRetrievalMissionController mission,
            out BreachHardDriveItem drive)
        {
            drive = null;
            return mission != null && TryResolveDrive(mission.DriveNetworkId, out drive);
        }

        internal static bool TryGetDriveForCarrier(
            BlacksiteSoldierAI carrier,
            out BreachHardDriveItem drive)
        {
            drive = null;
            if (carrier == null
                || !carrier.TryGetMission(out BlacksiteRetrievalMissionController mission)
                || mission.CarrierNetworkId == 0UL
                || carrier.NetworkObject == null
                || !carrier.NetworkObject.IsSpawned
                || mission.CarrierNetworkId != carrier.NetworkObject.NetworkObjectId)
            {
                return false;
            }

            return TryGetDrive(mission, out drive)
                && drive.SoldierCarrierNetworkId == mission.CarrierNetworkId;
        }

        internal static void InitializeMissionServer(BlacksiteRetrievalMissionController mission)
        {
            if (mission == null || !mission.IsServer || !mission.IsInitialized)
                return;
            RegisterMission(mission);
            TryResolveMissionTargetAndDriveServer(mission);
        }

        internal static void UpdateMissionServer(BlacksiteRetrievalMissionController mission)
        {
            if (mission == null || !mission.IsServer || mission.IsTerminal || !mission.IsInitialized)
                return;

            MoonContractType current = MoonContractState.GetCurrentContract();
            // #1075: "the contract changed" is only a fact when the reading is a real contract. A
            // None read can now also mean the assignment table is momentarily unresolved (no current
            // level mid-transition), and tearing a live mission down over that would be a
            // false abort, so an unresolved tick simply skips the comparison and re-asks next tick.
            // Host-side: the guard above already returned off the server, so this is not the client
            // sync case - it is the host's own transient.
            if (MoonContractState.IsCurrentContractResolved()
                && (current != mission.Contract || !BlacksiteRetrievalRules.IsContractEligible(current)))
            {
                CleanupMissionServer(mission, "contract changed");
                mission.ResolveServer(BlacksiteRetrievalOutcome.Aborted);
                return;
            }

            if (!TryResolveDrive(mission.DriveNetworkId, out BreachHardDriveItem drive))
            {
                ulong missionKey = mission.NetworkObjectId;
                if (!NextTargetResolveAttemptAt.TryGetValue(missionKey, out float nextAttemptAt)
                    || Time.time >= nextAttemptAt)
                {
                    NextTargetResolveAttemptAt[missionKey] = Time.time + TargetResolveRetrySeconds;
                    TryResolveMissionTargetAndDriveServer(mission);
                }
                if (!TryResolveDrive(mission.DriveNetworkId, out drive))
                {
                    mission.SetDriveStateServer(BlacksiteHardDriveState.Missing, 0UL);
                    return;
                }
            }

            BlacksiteHardDriveState state = drive.GetPhysicalState(out ulong carrierId);
            // #1219 B3: a soldier-held drive the manifest owns is reported by the manifest's
            // ReconcileDriveCarriers alone (the record is the custody truth); every other state keeps
            // this owner.
            if (state == BlacksiteHardDriveState.SoldierHeld && mission.Objectives != null
                && mission.Objectives.ContainsObject(drive.NetworkObjectId))
                return;
            if (state == BlacksiteHardDriveState.Loose
                && !drive.HasPlayerTouched
                && !drive.HasBeenSoldierCarriedServer)
            {
                // An untouched, never-carried drive cannot displace itself. Latch the
                // expected-target assessment instead of re-deriving it from the item
                // transform, which jitters across the radius during fall/settle cycles
                // and previously flapped the mission state and contract HUD every tick.
                float distance = Vector3.Distance(drive.transform.position, mission.TargetPosition);
                if (distance <= ExpectedTargetRadius
                    || mission.DriveState == BlacksiteHardDriveState.AtExpectedTarget)
                {
                    state = BlacksiteHardDriveState.AtExpectedTarget;
                    if (distance > ExpectedTargetRadius)
                    {
                        BlacksiteDiagnostics.LogRateLimited(
                            "drive-expected-target-latch",
                            10f,
                            "Drive",
                            $"expected-target latch held; physical distance {distance:0.00}m exceeds radius {ExpectedTargetRadius:0.00}m at {drive.transform.position}.");
                    }
                }
            }
            mission.SetDriveStateServer(state, carrierId);
        }

        internal static void NotifyHardDriveFoundServer(BreachHardDriveItem drive, string source)
        {
            if (drive == null)
                return;
            if (drive.OriginContract == MoonContractType.ShadowRaid)
                ShadowRaidContract.NotifyHardDriveFound(source);

            BlacksiteRetrievalMissionController mission = FindMission(drive.MissionNetworkId);
            if (mission != null && mission.IsServer)
                UpdateMissionServer(mission);
        }

        internal static void ResetRunState()
        {
            PruneRegistrations();
            for (int i = Missions.Count - 1; i >= 0; i--)
            {
                BlacksiteRetrievalMissionController mission = Missions[i];
                if (mission == null || !mission.IsServer)
                    continue;
                CleanupMissionServer(mission, "run reset");
                mission.ResolveServer(BlacksiteRetrievalOutcome.Aborted);
            }
            Missions.Clear();
            NextTargetResolveAttemptAt.Clear();
        }

        internal static void CleanupMissionServer(
            BlacksiteRetrievalMissionController mission,
            string reason)
        {
            if (mission == null || !mission.IsServer)
                return;

            // Every static cache here describes one interior: the deterministic pose, its seed and
            // node-count guards, and the resolve/pose backoff clocks. They must all clear on the way
            // out -- including on the paths below that return early -- or the next round starts
            // holding the previous interior's answer. Clearing them is deterministic: the pose is
            // re-derived from the same map seed and node set.
            _hasCachedInteriorPose = false;
            _cachedInteriorPoseSeed = 0;
            _cachedInteriorPoseNodeCount = 0;
            _cachedInteriorPosePosition = Vector3.zero;
            _cachedInteriorPoseRotation = Quaternion.identity;
            _nextDeterministicPoseAttemptAt = 0f;
            NextTargetResolveAttemptAt.Remove(mission.NetworkObjectId);

            if (!TryResolveDrive(mission.DriveNetworkId, out BreachHardDriveItem drive))
                return;

            if (drive.Extracted)
            {
                NetworkObject extractedObject = drive.NetworkObject;
                if (extractedObject != null && extractedObject.IsSpawned)
                    extractedObject.Despawn(destroy: true);
                else
                    UnityEngine.Object.Destroy(drive.gameObject);
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Removed extracted hard drive during {reason}; it left with the aircraft.");
                return;
            }

            if (drive.ShouldCleanupUntouchedEventDrive())
            {
                NetworkObject networkObject = drive.NetworkObject;
                if (networkObject != null && networkObject.IsSpawned)
                    networkObject.Despawn(destroy: true);
                else
                    UnityEngine.Object.Destroy(drive.gameObject);
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Removed untouched event hard drive during {reason}.");
                return;
            }

            drive.DetachMissionServer();
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Preserved touched or contract-owned hard drive during {reason}.");
        }

        internal static bool TryResolveContractTarget(
            Transform contractRoot,
            NetworkObject expectedTarget,
            string source,
            out BlacksiteRetrievalTargetPose target)
        {
            target = default;
            if (contractRoot == null || expectedTarget == null || !expectedTarget.IsSpawned)
                return false;

            if (TryFindSecondaryAnchor(contractRoot, out Transform anchor))
            {
                target = new BlacksiteRetrievalTargetPose(
                    anchor.position,
                    anchor.rotation,
                    expectedTarget,
                    expectedTarget,
                    null,
                    source + ":authored-secondary-anchor");
                return true;
            }

            if (!TryResolveDeterministicInteriorPose(out Vector3 position, out Quaternion rotation))
                return false;

            target = new BlacksiteRetrievalTargetPose(
                position,
                rotation,
                expectedTarget,
                null,
                null,
                source + ":deterministic-ai-node");
            return true;
        }

        private static void TryResolveMissionTargetAndDriveServer(
            BlacksiteRetrievalMissionController mission)
        {
            if (!TryResolveTarget(mission.Contract, out BlacksiteRetrievalTargetPose target))
                return;

            mission.SetTargetServer(target);
            BreachHardDriveItem drive = target.Drive != null
                ? target.Drive.GetComponent<BreachHardDriveItem>()
                : FindDriveForMission(mission);

            if (BlacksiteRetrievalRules.UsesEmbeddedHardDrive(mission.Contract))
            {
                if (drive == null)
                    return;
                drive.ConfigureObjectiveServer(
                    mission.Contract,
                    mission.MissionNetworkId,
                    eventCreated: false);
            }
            else if (BlacksiteRetrievalRules.CreatesEventHardDrive(mission.Contract))
            {
                drive = drive ?? SpawnEventHardDriveServer(mission, target);
            }
            else
            {
                return;
            }

            if (drive == null)
                return;
            mission.SetDriveServer(drive);
            UpdateMissionServer(mission);
        }

        private static bool TryResolveTarget(
            MoonContractType contract,
            out BlacksiteRetrievalTargetPose target)
        {
            switch (contract)
            {
                case MoonContractType.Payload:
                    return PayloadContract.TryGetBlacksiteRetrievalTarget(out target);
                case MoonContractType.ShadowRaid:
                    return ShadowRaidContract.TryGetBlacksiteRetrievalTarget(out target);
                // #941 S11 U3: the Defuse arm is gone with the Defuse adapter. It only supplied a
                // first-approach pose; UsesEmbeddedHardDrive already rejected Defuse above, and
                // Defuse now runs the default raid, whose apparatus comes from the filtered
                // fallback in BlacksiteObjectiveManifestController.Raid.cs.
                default:
                    target = default;
                    return false;
            }
        }

        private static BreachHardDriveItem SpawnEventHardDriveServer(
            BlacksiteRetrievalMissionController mission,
            BlacksiteRetrievalTargetPose target)
        {
            BreachHardDriveItem existing = FindDriveForMission(mission);
            if (existing != null)
                return existing;

            if (!BreachHardDriveItemRegistry.TryGet(out Item item) || item?.spawnPrefab == null)
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.Blacksite] Hard-drive item prefab is unavailable.");
                return null;
            }

            Transform props = StartOfRound.Instance?.propsContainer;
            GameObject spawned = UnityEngine.Object.Instantiate(
                item.spawnPrefab,
                target.Position + Vector3.up * DriveVerticalOffset,
                target.Rotation,
                props);
            if (spawned == null)
                return null;

            spawned.name = $"BlacksiteRetrieval_HardDrive_{mission.MissionNetworkId}";
            if (!spawned.activeSelf)
                spawned.SetActive(true);

            PhysicsProp prop = spawned.GetComponent<PhysicsProp>();
            if (prop != null)
            {
                prop.itemProperties = item;
                prop.isInFactory = true;
                prop.fallTime = 0f;
                prop.startFallingPosition = spawned.transform.localPosition;
                prop.targetFloorPosition = spawned.transform.localPosition;
                prop.SetScrapValue(BreachHardDriveItemRegistry.ScrapValue);
            }

            NetworkObject networkObject = spawned.GetComponent<NetworkObject>();
            BreachHardDriveItem drive = spawned.GetComponent<BreachHardDriveItem>();
            if (networkObject == null || drive == null)
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.Blacksite] Hard-drive prefab lacks required network components.");
                UnityEngine.Object.Destroy(spawned);
                return null;
            }

            if (!networkObject.IsSpawned)
                networkObject.Spawn();
            if (props != null)
                networkObject.TrySetParent(props);
            drive.ConfigureObjectiveServer(mission.Contract, mission.MissionNetworkId, true);
            RegisterDrive(drive);
            if (RoundManager.Instance != null)
                RoundManager.Instance.totalScrapValueInLevel += BreachHardDriveItemRegistry.ScrapValue;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Spawned {mission.Contract} mission hard drive " +
                $"at {target.Position} via {target.Source}.");
            return drive;
        }

        private static BreachHardDriveItem FindDriveForMission(
            BlacksiteRetrievalMissionController mission)
        {
            if (mission == null)
                return null;

            PruneRegistrations();
            for (int i = 0; i < Drives.Count; i++)
            {
                BreachHardDriveItem drive = Drives[i];
                if (drive != null && drive.MissionNetworkId == mission.MissionNetworkId)
                    return drive;
            }
            return null;
        }

        private static BlacksiteRetrievalMissionController FindMission(ulong missionNetworkId)
        {
            if (missionNetworkId == 0UL)
                return null;

            PruneRegistrations();
            for (int i = 0; i < Missions.Count; i++)
            {
                BlacksiteRetrievalMissionController mission = Missions[i];
                if (mission != null && mission.MissionNetworkId == missionNetworkId)
                    return mission;
            }
            return null;
        }

        private static bool TryResolveDrive(ulong networkObjectId, out BreachHardDriveItem drive)
        {
            drive = null;
            if (networkObjectId == 0UL)
                return false;

            PruneRegistrations();
            for (int i = 0; i < Drives.Count; i++)
            {
                BreachHardDriveItem candidate = Drives[i];
                if (candidate?.NetworkObject != null
                    && candidate.NetworkObject.NetworkObjectId == networkObjectId)
                {
                    drive = candidate;
                    return true;
                }
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager?.SpawnManager?.SpawnedObjects != null
                && manager.SpawnManager.SpawnedObjects.TryGetValue(
                    networkObjectId,
                    out NetworkObject networkObject)
                && networkObject != null)
            {
                drive = networkObject.GetComponent<BreachHardDriveItem>();
                if (drive != null)
                {
                    RegisterDrive(drive);
                    return true;
                }
            }
            return false;
        }

        private static bool TryFindSecondaryAnchor(Transform root, out Transform anchor)
        {
            anchor = null;
            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null)
                    continue;
                if (string.Equals(child.name, "BlacksiteDriveAnchor", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(child.name, "BlacksiteHardDriveAnchor", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(child.name, "BlacksiteSecondaryAnchor", StringComparison.OrdinalIgnoreCase))
                {
                    anchor = child;
                    return true;
                }
            }
            return false;
        }

        private static bool TryResolveDeterministicInteriorPose(
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            GameObject[] nodes = RoundManager.Instance?.insideAINodes;
            if (nodes == null || nodes.Length == 0)
                return false;

            int mapSeed = StartOfRound.Instance?.randomMapSeed ?? 0;
            // The scan is a pure function of the map seed and the node set, so re-running it can
            // only ever produce the same answer. Cache it and back off after a failure.
            if (_hasCachedInteriorPose
                && _cachedInteriorPoseSeed == mapSeed
                && _cachedInteriorPoseNodeCount == nodes.Length)
            {
                position = _cachedInteriorPosePosition;
                rotation = _cachedInteriorPoseRotation;
                return true;
            }

            if (Time.time < _nextDeterministicPoseAttemptAt)
                return false;

            List<Vector3> entrances = InteriorAnchorService.GetInteriorEntrancePositions();
            Vector3 ship = StartOfRound.Instance?.elevatorTransform != null
                ? StartOfRound.Instance.elevatorTransform.position
                : Vector3.zero;
            int seed = mapSeed;
            bool found = false;
            float bestScore = float.MinValue;
            Vector3 bestPosition = Vector3.zero;

            for (int i = 0; i < nodes.Length; i++)
            {
                GameObject node = nodes[i];
                if (node == null
                    || !NavMesh.SamplePosition(
                        node.transform.position,
                        out NavMeshHit hit,
                        3f,
                        NavMesh.AllAreas))
                {
                    continue;
                }

                float path = InteriorAnchorService.EstimateNearestPathDistance(
                    entrances,
                    hit.position);
                float horizontal = InteriorAnchorService.EstimateNearestHorizontalDistance(
                    entrances,
                    hit.position);
                float shipDistance = HorizontalDistance(ship, hit.position);
                int tie = StableHash(seed, i, hit.position) & 1023;
                float score = path * 2f
                              + horizontal
                              + Mathf.Min(200f, shipDistance) * 0.1f
                              + tie * 0.0001f;
                if (found && score <= bestScore)
                    continue;

                found = true;
                bestScore = score;
                bestPosition = hit.position;
            }

            if (!found)
            {
                _nextDeterministicPoseAttemptAt = Time.time + TargetResolveRetrySeconds;
                return false;
            }

            Vector3 entrance = InteriorAnchorService.GetNearestEntrancePoint(
                entrances,
                bestPosition);
            Vector3 away = bestPosition - entrance;
            away.y = 0f;
            position = InteriorAnchorService.ResolveFloorPosition(bestPosition);
            rotation = away.sqrMagnitude > 0.01f
                ? Quaternion.LookRotation(away.normalized, Vector3.up)
                : Quaternion.identity;
            _hasCachedInteriorPose = true;
            _cachedInteriorPoseSeed = mapSeed;
            _cachedInteriorPoseNodeCount = nodes.Length;
            _cachedInteriorPosePosition = position;
            _cachedInteriorPoseRotation = rotation;
            return true;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private static int StableHash(int seed, int index, Vector3 position)
        {
            unchecked
            {
                int hash = seed;
                hash = hash * 31 + index;
                hash = hash * 31 + Mathf.RoundToInt(position.x * 10f);
                hash = hash * 31 + Mathf.RoundToInt(position.y * 10f);
                hash = hash * 31 + Mathf.RoundToInt(position.z * 10f);
                return hash;
            }
        }

        private static void PruneRegistrations()
        {
            for (int i = Missions.Count - 1; i >= 0; i--)
            {
                if (Missions[i] == null)
                    Missions.RemoveAt(i);
            }
            for (int i = Drives.Count - 1; i >= 0; i--)
            {
                if (Drives[i] == null)
                    Drives.RemoveAt(i);
            }
        }
    }
}
