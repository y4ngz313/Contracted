using System;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core.Compat;

namespace Y4NGZCompany.Contracts.WasteDisposal
{
    /// <summary>
    /// Registers an art-free network prefab. This hash and wire shape stay stable even when the
    /// bundled incinerator visual is replaced.
    /// </summary>
    internal static class WasteDisposalIncineratorRegistry
    {
        private const string PrefabName = "LGU_WasteDisposalIncinerator";
        private static bool _registrationAttempted;

        internal static GameObject Prefab { get; private set; }
        internal static bool Registered { get; private set; }

        internal static bool Register()
        {
            if (Registered && Prefab != null)
                return true;
            if (_registrationAttempted)
                return Prefab != null;
            _registrationAttempted = true;

            try
            {
                GameObject root = ContractAssetLoader.CreateHiddenPrefabRoot(PrefabName);

                NetworkObject networkObject = root.AddComponent<NetworkObject>();
                networkObject.SynchronizeTransform = true;
                networkObject.SceneMigrationSynchronization = true;
                networkObject.SpawnWithObservers = true;
                networkObject.DontDestroyWithOwner = true;
                networkObject.AutoObjectParentSync = true;
                ContractAssetLoader.AssignStableNetworkHash(networkObject, "WasteDisposal", PrefabName);
                root.AddComponent<WasteDisposalIncineratorController>();

                Prefab = root;
                DawnLibCompat.RegisterNetworkPrefab(Prefab);
                Registered = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.WasteDisposal] Registered the art-free incinerator network prefab.");
                ContractAssetLoader.LogRegisteredPrefab("[MoonContracts.WasteDisposal]", Prefab, registered: true);
                return true;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts.WasteDisposal] Failed to register incinerator prefab: {e}");
                if (Prefab != null)
                    UnityEngine.Object.DestroyImmediate(Prefab);
                Prefab = null;
                return false;
            }
        }

        internal static WasteDisposalIncineratorController TrySpawn(
            Vector3 position,
            Quaternion rotation,
            int moonScrapValue,
            float targetPercent)
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsServer || !Register() || Prefab == null)
                return null;

            GameObject instance = UnityEngine.Object.Instantiate(Prefab, position, rotation);
            instance.name = PrefabName + "_Spawned";
            instance.hideFlags = HideFlags.None;
            instance.SetActive(true);
            NetworkObject networkObject = instance.GetComponent<NetworkObject>();
            WasteDisposalIncineratorController controller = instance.GetComponent<WasteDisposalIncineratorController>();
            if (networkObject == null || controller == null)
            {
                ContractsBootstrap.Log?.LogError("[MoonContracts.WasteDisposal] Incinerator prefab is missing its NetworkObject or controller; it would exist on the host only. Aborting spawn.");
                UnityEngine.Object.Destroy(instance);
                return null;
            }

            networkObject.Spawn(true);
            controller.ServerInitialize(moonScrapValue, targetPercent);
            return controller;
        }

        internal static bool TryDespawn(WasteDisposalIncineratorController controller)
        {
            if (controller == null || NetworkManager.Singleton?.IsServer != true)
                return false;
            NetworkObject networkObject = controller.NetworkObject;
            if (networkObject == null || !networkObject.IsSpawned)
                return false;
            networkObject.Despawn(true);
            return true;
        }
    }
}
