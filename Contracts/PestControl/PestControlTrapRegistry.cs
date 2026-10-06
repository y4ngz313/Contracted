using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core.Compat;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// Registers the Pest Control trap network prefab. Unlike the Shadow Raid drill this prefab
    /// carries NO art: the trap's geometry is built at runtime on every peer from the replicated
    /// footprint, so the prefab is just a NetworkObject plus
    /// <see cref="PestControlTrapController"/>. Keeping it empty is also what makes the prefab
    /// hash stable without a bundle dependency.
    /// </summary>
    internal static class PestControlTrapRegistry
    {
        private const string PrefabName = "LGU_PestControlTrap";

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
                AssignStableNetworkHash(networkObject);

                root.AddComponent<PestControlTrapController>();

                Prefab = root;
                DawnLibCompat.RegisterNetworkPrefab(Prefab);
                Registered = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.PestControl] Registered the Pest Control trap network prefab.");
                ContractAssetLoader.LogRegisteredPrefab("[MoonContracts.PestControl]", Prefab, registered: true);
                return true;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts.PestControl] Failed to register the Pest Control trap prefab: {e}");
                if (Prefab != null)
                    UnityEngine.Object.DestroyImmediate(Prefab);
                Prefab = null;
                return false;
            }
        }

        /// <summary>Host-only. Spawns the trap at a resolved placement and returns its controller.</summary>
        internal static PestControlTrapController TrySpawn(PestControlTrapPlacement placement)
        {
            if (placement == null || !placement.IsSpawnReady)
            {
                string rejection = placement == null
                    ? "placement-null"
                    : placement.BuildProductionValiditySummary(requireReservation: true);
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.PestControl] Trap spawn refused: {rejection}.");
                return null;
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsServer)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.PestControl] Trap spawn refused: not the server.");
                return null;
            }

            if (!Register() || Prefab == null)
                return null;

            GameObject instance = UnityEngine.Object.Instantiate(
                Prefab,
                placement.Center,
                Quaternion.Euler(0f, placement.Yaw, 0f));
            instance.name = PrefabName + "_Spawned";
            instance.hideFlags = HideFlags.None;
            instance.SetActive(true);

            NetworkObject networkObject = instance.GetComponent<NetworkObject>();
            var controller = instance.GetComponent<PestControlTrapController>();
            if (networkObject == null || controller == null)
            {
                ContractsBootstrap.Log?.LogError("[MoonContracts.PestControl] Trap prefab is missing its NetworkObject or controller; the trap would exist on the host only. Aborting spawn.");
                UnityEngine.Object.Destroy(instance);
                return null;
            }

            networkObject.Spawn(true);
            // The footprint is written after Spawn on purpose: a NetworkVariable has no
            // NetworkManager to validate against until the object is spawned. The controller
            // assembles lazily off the OnValueChanged callback, so clients that received a spawn
            // payload with the default 0 still build the moment the real value lands.
            controller.ServerInitialize(placement.FootprintMeters, placement.CeilingClearanceMeters);

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.PestControl] PEST_TRAP_SPAWN id={networkObject.NetworkObjectId} " +
                $"center=({placement.Center.x:0.0},{placement.Center.y:0.0},{placement.Center.z:0.0}) " +
                $"footprint={placement.FootprintMeters:0.00}m yaw={placement.Yaw:0.0}");
            return controller;
        }

        internal static bool TryDespawn(PestControlTrapController controller)
        {
            if (controller == null)
                return false;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsServer)
                return false;

            NetworkObject networkObject = controller.NetworkObject;
            if (networkObject == null || !networkObject.IsSpawned)
                return false;

            networkObject.Despawn(true);
            return true;
        }

        private static void AssignStableNetworkHash(NetworkObject networkObject)
        {
            if (networkObject == null)
                return;

            try
            {
                uint hash;
                using (MD5 md5 = MD5.Create())
                {
                    byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ContractsBootstrap.PLUGIN_GUID + ".PestControl." + PrefabName));
                    hash = BitConverter.ToUInt32(bytes, 0);
                }

                FieldInfo field = typeof(NetworkObject).GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? typeof(NetworkObject).GetField("<GlobalObjectIdHash>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(networkObject, hash);
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.PestControl] Trap NetworkObject hash set: {hash}.");
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.PestControl] Failed to assign the trap NetworkObject hash: {e.Message}");
            }
        }
    }
}
