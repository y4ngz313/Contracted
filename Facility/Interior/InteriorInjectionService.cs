using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Facility.Interior
{
    internal static class InteriorInjectionService
    {
        private static bool _debugSpawnAttempted;
        private static NetworkObject _debugSpawnedObject;

        internal static void ResetRunState()
        {
            _debugSpawnAttempted = false;
            _debugSpawnedObject = null;
        }

        internal static void UpdateRuntime()
        {
            if (!ContractsBootstrap.InteriorInjectionDebugEnabled.Value)
                return;
            if (MoonContractState.GetCurrentContract() == MoonContractType.ShadowRaid)
                return;
            if (_debugSpawnAttempted || _debugSpawnedObject != null)
                return;

            MoonContractType currentContract = MoonContractState.GetCurrentContract();
            string prefabName = ContractsBootstrap.InteriorInjectionDebugPrefabName.Value;
            if (IsContractOwnedDebugPrefabBlocked(prefabName, currentContract))
            {
                _debugSpawnAttempted = true;
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts] Interior injection debug spawn for '{prefabName}' was blocked because the current contract is {currentContract}.");
                return;
            }

            var round = RoundManager.Instance;
            var start = StartOfRound.Instance;
            if (round == null || start == null || !round.IsServer || !start.shipHasLanded || start.inShipPhase || start.shipIsLeaving)
                return;

            _debugSpawnAttempted = true;

            if (!TrySpawnBundledPrefabInPrimaryRoom(
                    ContractsBootstrap.InteriorInjectionDebugPrefabName.Value,
                    out _debugSpawnedObject,
                    out _,
                    out _))
            {
                _debugSpawnAttempted = false;
            }
        }

        private static bool IsContractOwnedDebugPrefabBlocked(string assetName, MoonContractType currentContract)
        {
            if (string.IsNullOrWhiteSpace(assetName))
                return false;

            if (currentContract != MoonContractType.ShadowRaid && IsShadowRaidPrefabName(assetName))
                return true;
            if (currentContract != MoonContractType.Defuse && IsDefusePrefabName(assetName))
                return true;

            return false;
        }

        private static bool IsShadowRaidPrefabName(string assetName)
        {
            return MatchesConfiguredPrefab(assetName, ContractsBootstrap.ShadowRaidContainerPrefabName?.Value)
                   || MatchesConfiguredPrefab(assetName, ContractsBootstrap.ShadowRaidRewardPrefabName?.Value)
                   || MatchesConfiguredPrefab(assetName, ContractsBootstrap.ShadowRaidDrillPrefabName?.Value)
                   || ContractConfigRules.NameMatches(assetName, "Shipping Container")
                   || ContractConfigRules.NameMatches(assetName, "BlacksiteCargoCrate")
                   || ContractConfigRules.NameMatches(assetName, "BreachDrill");
        }

        private static bool IsDefusePrefabName(string assetName)
        {
            return MatchesConfiguredPrefab(assetName, DefuseBalanceConfig.BombPrefabName)
                   || ContractConfigRules.NameMatches(assetName, "DeadDrop")
                   || ContractConfigRules.NameMatches(assetName, "BombPreview");
        }

        private static bool MatchesConfiguredPrefab(string assetName, string configuredName)
        {
            return !string.IsNullOrWhiteSpace(configuredName)
                   && ContractConfigRules.NameMatches(assetName, configuredName);
        }

        internal static bool TrySpawnBundledPrefabInPrimaryRoom(
            string assetName,
            out NetworkObject spawnedObject,
            out InteriorTileSelection selection,
            out List<InteriorApproachAnchor> approaches)
        {
            spawnedObject = null;
            approaches = null;
            selection = default;

            if (!InteriorAnchorService.TryGetPrimaryInteriorTile(out selection))
                return false;

            approaches = InteriorApproachDetector.FindApproaches(selection);
            GameObject prefab = ContractAssetLoader.GetPreparedInteriorSupportPrefab(assetName);
            if (prefab == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Interior injection could not load bundled prefab '{assetName}'.");
                return false;
            }

            Vector3 spawnPosition = selection.FloorPosition + Vector3.up * 0.05f;
            if (NavMesh.SamplePosition(spawnPosition, out NavMeshHit hit, 4f, NavMesh.AllAreas))
                spawnPosition = hit.position + Vector3.up * 0.05f;

            if (!TrySpawnPrefabInstance(prefab, spawnPosition, selection.Rotation, out spawnedObject))
                return false;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Interior injection spawned '{assetName}' in '{selection.TileName}' at {FormatVector(spawnPosition)} with {approaches.Count} approach anchor(s): {string.Join(" | ", approaches.ConvertAll(FormatAnchorPosition))}.");
            return true;
        }

        internal static bool TrySpawnPrefabInstance(GameObject prefab, Vector3 position, Quaternion rotation, out NetworkObject spawnedObject)
        {
            spawnedObject = null;
            if (prefab == null)
                return false;

            GameObject instance = UnityEngine.Object.Instantiate(prefab, position, rotation);
            if (instance == null)
                return false;

            if (!instance.activeSelf)
                instance.SetActive(true);

            spawnedObject = instance.GetComponent<NetworkObject>();
            if (spawnedObject == null)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts] Interior injection prefab '{prefab.name}' has no NetworkObject; the instance would exist on the host only. Aborting spawn.");
                UnityEngine.Object.Destroy(instance);
                return false;
            }

            if (!spawnedObject.IsSpawned)
            {
                spawnedObject.Spawn();
                if (StartOfRound.Instance?.propsContainer != null)
                    spawnedObject.TrySetParent(StartOfRound.Instance.propsContainer);
            }

            return true;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"{value.x:0.0},{value.y:0.0},{value.z:0.0}";
        }

        private static string FormatAnchorPosition(InteriorApproachAnchor anchor)
        {
            return $"{anchor.Source}@{FormatVector(anchor.Position)}";
        }
    }
}
