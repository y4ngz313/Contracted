using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx.Bootstrap;
using Unity.Netcode;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Core.Compatibility
{
    /// <summary>
    /// #802. Reflection-only bridge to Lategame Upgrades' contract singleton.
    ///
    /// Contracted assigns the contract; LGU spawns and plays it. Everything crosses the gap
    /// by reflection so this mod keeps no compile reference to MoreShipUpgrades and stays
    /// installable without it, the same shape <see cref="WeatherChangeAlerts"/> uses.
    ///
    /// Two LGU facts drive the design. First, <c>CommandParser.contracts</c> is the truth for
    /// which contract types exist: LGU removes disabled types from that list, and the index
    /// the sync RPC takes is an index *into that list*, so it is computed with IndexOf at
    /// call time and never cached. Second, <c>SyncContractDetailsClientRpc</c> is the
    /// authoritative writer - it is what makes every client's terminal commands
    /// (<c>bruteforce</c>, <c>lookup</c>, <c>demon</c>) see the contract, because those gate
    /// on the local client's manager state. It also rewrites LGU's save file; that is LGU's
    /// own behaviour on every contract assignment and is expected here too.
    ///
    /// Every method body that names an LGU type is <see cref="MethodImplOptions.NoInlining"/>
    /// so the JIT cannot pull a missing type into a caller's frame, and nothing here throws:
    /// a failure is a logged false.
    /// </summary>
    internal static class LategameUpgradesCompat
    {
        internal const string PluginGuid = LategameUpgradesMembers.PluginGuid;

        private const string LogPrefix = "[MoonContracts.LGU]";

        private static readonly bool _loaded = Chainloader.PluginInfos.ContainsKey(PluginGuid);

        private static bool _resolved;
        private static bool _usable;

        private static Type _contractManagerType;
        private static PropertyInfo _instanceProperty;
        private static FieldInfo _contractLevelField;
        private static FieldInfo _contractTypeField;
        private static FieldInfo _lastContractIndexField;
        private static MethodInfo _syncContractDetailsClientRpc;
        private static FieldInfo _contractsField;

        // #804. Defusal progress lives on the manager, not on the device: LGU's own
        // SyncBombDetailsClientRpc and BombStateChangeClientRpc write it there on every peer,
        // and the device deactivates itself on a detonation. Optional throughout - losing
        // either costs one status line, not the assignment bridge.
        private static FieldInfo _bombOrderField;
        private static FieldInfo _serialNumberField;

        private static Type _commandParserType;
        private static Type _contractApplicationType;
        private static MethodInfo _tryGetContractMethod;
        private static MethodInfo _purchaseRandomMoonContractMethod;
        private static MethodInfo _purchaseSpecifiedMoonContractMethod;

        /// <summary>True when LGU is chainloaded. Says nothing about whether its members resolved.</summary>
        internal static bool IsLoaded => _loaded;

        /// <summary>True when LGU is present and every member the bridge needs resolved.</summary>
        internal static bool IsUsable
        {
            get
            {
                EnsureResolved();
                return _usable;
            }
        }

        /// <summary>The three shop entry points, for the suppression patch. Null members are
        /// skipped there; a missing one costs only that one entry point.</summary>
        internal static MethodInfo TryGetContractMethod
        {
            get
            {
                EnsureResolved();
                return _tryGetContractMethod;
            }
        }

        internal static MethodInfo PurchaseRandomMoonContractMethod
        {
            get
            {
                EnsureResolved();
                return _purchaseRandomMoonContractMethod;
            }
        }

        internal static MethodInfo PurchaseSpecifiedMoonContractMethod
        {
            get
            {
                EnsureResolved();
                return _purchaseSpecifiedMoonContractMethod;
            }
        }

        /// <summary>True when this peer is the session's server. False in the main menu, where
        /// there is no session to be the server of.</summary>
        internal static bool IsServerPeer => IsServer();

        /// <summary>True when a netcode session is running at all, server or client.</summary>
        internal static bool IsInSession
        {
            get
            {
                NetworkManager network = NetworkManager.Singleton;
                return network != null && network.IsListening;
            }
        }

        /// <summary>Reads LGU's current assignment. False when unusable, when no manager is
        /// alive, or when LGU holds no contract.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryGetActiveContract(out string planetName, out string typeString)
        {
            planetName = null;
            typeString = null;
            if (!IsUsable)
                return false;

            try
            {
                // Through the spawned-manager accessor, not Instance: LGU sets Instance in
                // Awake and never clears it, so a destroyed manager is still a live CLR
                // reference whose fields read as the previous session's assignment. Only the
                // Unity-null and IsSpawned test that the apply path uses rejects it.
                object manager = GetSpawnedManager();
                if (manager == null)
                    return false;

                planetName = _contractLevelField.GetValue(manager) as string;
                typeString = _contractTypeField.GetValue(manager) as string;
                return !string.IsNullOrEmpty(typeString)
                    && !string.Equals(typeString, LategameUpgradesMembers.NoneValue, StringComparison.Ordinal);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Could not read the active contract: {e.Message}");
                planetName = null;
                typeString = null;
                return false;
            }
        }

        /// <summary>
        /// #804. Reads the defusal device's replicated progress off LGU's manager.
        /// <paramref name="wiresRemaining"/> is -1 when it is unreadable and
        /// <paramref name="serial"/> is null until LGU has synced one. False when nothing at
        /// all could be read, which is what the objective renders as "PENDING".
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryGetBombProgress(out int wiresRemaining, out string serial)
        {
            wiresRemaining = -1;
            serial = null;
            EnsureResolved();
            if (_bombOrderField == null && _serialNumberField == null)
                return false;

            try
            {
                object manager = GetSpawnedManager();
                if (manager == null)
                    return false;

                if (_bombOrderField != null && _bombOrderField.GetValue(manager) is IList order)
                    wiresRemaining = order.Count;
                if (_serialNumberField != null)
                    serial = _serialNumberField.GetValue(manager) as string;
                return wiresRemaining >= 0 || !string.IsNullOrEmpty(serial);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Could not read the defusal device's progress: {e.Message}");
                return false;
            }
        }

        /// <summary>True when LGU still offers this contract type. LGU deletes disabled types
        /// from <c>CommandParser.contracts</c>, and membership of that list - not any config
        /// key, two of which LGU has swapped - is the only honest answer.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IsContractTypeAvailable(string typeString)
        {
            return IndexOfContractType(typeString) >= 0;
        }

        /// <summary>
        /// Host-only. Points LGU at <paramref name="planetName"/> with
        /// <paramref name="typeString"/> so its map objects survive
        /// <c>ContractObject.Start</c>, and replicates it to every client.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryApplyContract(string planetName, string typeString, string reason)
        {
            if (!IsUsable)
                return false;

            if (!IsServer())
            {
                ContractsBootstrap.Log?.LogDebug($"{LogPrefix} Not applying '{typeString}' on {planetName}: this peer is not the server ({reason}).");
                return false;
            }

            if (string.IsNullOrEmpty(planetName))
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Not applying '{typeString}': no destination moon ({reason}).");
                return false;
            }

            int index = IndexOfContractType(typeString);
            if (index < 0)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Not applying '{typeString}' on {planetName}: LGU does not offer that contract type ({reason}).");
                return false;
            }

            return WriteContract(planetName, typeString, index, reason);
        }

        /// <summary>Host-only. Clears LGU's contract everywhere by sending the RPC's defaults.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryClearContract(string reason)
        {
            if (!IsUsable)
                return false;

            if (!IsServer())
            {
                ContractsBootstrap.Log?.LogDebug($"{LogPrefix} Not clearing the contract: this peer is not the server ({reason}).");
                return false;
            }

            return WriteContract(LategameUpgradesMembers.NoneValue, LategameUpgradesMembers.NoneValue, LategameUpgradesMembers.NoneIndex, reason);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool WriteContract(string planetName, string typeString, int index, string reason)
        {
            try
            {
                object manager = GetSpawnedManager();
                if (manager == null)
                {
                    ContractsBootstrap.Log?.LogDebug($"{LogPrefix} LGU's contract manager is not spawned yet ({reason}).");
                    return false;
                }

                // Written locally first, then replicated: the host receives its own ClientRpc
                // through netcode's loopback, which can be a frame later, and LGU's own
                // purchase paths write the fields ahead of the RPC for the same reason. The
                // dungeon generation that reads these fields starts on this frame.
                _contractLevelField.SetValue(manager, planetName);
                _contractTypeField.SetValue(manager, typeString);

                // lastContractIndex is LGU's repeat-avoidance memory and its RPC only writes it
                // on the assignment branch: the index -1 "clear" branch leaves every client's
                // copy alone. Writing -1 locally would desync the host from its own clients for
                // no gain, so the local write mirrors the RPC exactly.
                if (index != LategameUpgradesMembers.NoneIndex)
                    _lastContractIndexField.SetValue(manager, index);

                _syncContractDetailsClientRpc.Invoke(manager, new object[] { planetName, index });

                ContractsBootstrap.Log?.LogInfo(
                    index == LategameUpgradesMembers.NoneIndex
                        ? $"{LogPrefix} Cleared LGU's contract ({reason})."
                        : $"{LogPrefix} Assigned LGU contract '{typeString}' (index {index}) on {planetName} ({reason}).");
                return true;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Contract write failed ({reason}): {e.Message}");
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int IndexOfContractType(string typeString)
        {
            if (!IsUsable || string.IsNullOrEmpty(typeString))
                return -1;

            try
            {
                if (!(_contractsField.GetValue(null) is IList contracts))
                    return -1;

                for (int i = 0; i < contracts.Count; i++)
                {
                    if (string.Equals(contracts[i] as string, typeString, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Could not read LGU's contract type table: {e.Message}");
            }

            return -1;
        }

        /// <summary>The live manager, or null when it does not exist or is not spawned.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object GetSpawnedManager()
        {
            try
            {
                // ContractManager is a NetworkBehaviour, which is a game-side type: casting to
                // it names nothing from LGU and is how the spawn state is read without a
                // second reflection hop. The UnityEngine.Object comparison is the point of the
                // cast - LGU never nulls Instance, so a destroyed manager passes a CLR null
                // check and only Unity's overloaded operator rejects it.
                if (!(_instanceProperty.GetValue(null) is NetworkBehaviour behaviour))
                    return null;

                return behaviour != null && behaviour.IsSpawned ? behaviour : null;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Could not reach LGU's contract manager: {e.Message}");
                return null;
            }
        }

        private static bool IsServer()
        {
            NetworkManager network = NetworkManager.Singleton;
            return network != null && network.IsListening && network.IsServer;
        }

        /// <summary>Resolves every handle once and reports anything missing in one warning.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EnsureResolved()
        {
            if (_resolved)
                return;

            _resolved = true;
            if (!_loaded)
                return;

            var missing = new List<string>();
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var info) || info?.Instance == null)
                {
                    ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Lategame Upgrades is registered but has no plugin instance; the contract bridge stands down.");
                    return;
                }

                Assembly assembly = info.Instance.GetType().Assembly;
                const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;

                _contractManagerType = assembly.GetType(LategameUpgradesMembers.ContractManagerType, throwOnError: false);
                _commandParserType = assembly.GetType(LategameUpgradesMembers.CommandParserType, throwOnError: false);
                _contractApplicationType = assembly.GetType(LategameUpgradesMembers.ContractApplicationType, throwOnError: false);

                if (_contractManagerType == null)
                    missing.Add(LategameUpgradesMembers.ContractManagerType);
                else
                {
                    _instanceProperty = _contractManagerType.GetProperty(LategameUpgradesMembers.InstanceProperty, Any | BindingFlags.Static);
                    _contractLevelField = _contractManagerType.GetField(LategameUpgradesMembers.ContractLevelField, Any | BindingFlags.Instance);
                    _contractTypeField = _contractManagerType.GetField(LategameUpgradesMembers.ContractTypeField, Any | BindingFlags.Instance);
                    _lastContractIndexField = _contractManagerType.GetField(LategameUpgradesMembers.LastContractIndexField, Any | BindingFlags.Instance);
                    _syncContractDetailsClientRpc = _contractManagerType.GetMethod(
                        LategameUpgradesMembers.SyncContractDetailsClientRpcMethod,
                        Any | BindingFlags.Instance,
                        binder: null,
                        types: new[] { typeof(string), typeof(int) },
                        modifiers: null);

                    AddIfNull(missing, _instanceProperty, LategameUpgradesMembers.ContractManagerType + "." + LategameUpgradesMembers.InstanceProperty);
                    AddIfNull(missing, _contractLevelField, LategameUpgradesMembers.ContractManagerType + "." + LategameUpgradesMembers.ContractLevelField);
                    AddIfNull(missing, _contractTypeField, LategameUpgradesMembers.ContractManagerType + "." + LategameUpgradesMembers.ContractTypeField);
                    AddIfNull(missing, _lastContractIndexField, LategameUpgradesMembers.ContractManagerType + "." + LategameUpgradesMembers.LastContractIndexField);
                    AddIfNull(missing, _syncContractDetailsClientRpc, LategameUpgradesMembers.ContractManagerType + "." + LategameUpgradesMembers.SyncContractDetailsClientRpcMethod);

                    // #804. Optional, and reported alongside the rest rather than separately.
                    _bombOrderField = _contractManagerType.GetField(LategameUpgradesMembers.BombOrderField, Any | BindingFlags.Instance);
                    _serialNumberField = _contractManagerType.GetField(LategameUpgradesMembers.SerialNumberField, Any | BindingFlags.Instance);
                    AddIfNull(missing, _bombOrderField, LategameUpgradesMembers.ContractManagerType + "." + LategameUpgradesMembers.BombOrderField);
                    AddIfNull(missing, _serialNumberField, LategameUpgradesMembers.ContractManagerType + "." + LategameUpgradesMembers.SerialNumberField);
                }

                if (_commandParserType == null)
                    missing.Add(LategameUpgradesMembers.CommandParserType);
                else
                {
                    _contractsField = _commandParserType.GetField(LategameUpgradesMembers.ContractsField, Any | BindingFlags.Static);
                    _tryGetContractMethod = _commandParserType.GetMethod(LategameUpgradesMembers.TryGetContractMethod, Any | BindingFlags.Static);
                    AddIfNull(missing, _contractsField, LategameUpgradesMembers.CommandParserType + "." + LategameUpgradesMembers.ContractsField);
                    AddIfNull(missing, _tryGetContractMethod, LategameUpgradesMembers.CommandParserType + "." + LategameUpgradesMembers.TryGetContractMethod);
                }

                if (_contractApplicationType == null)
                    missing.Add(LategameUpgradesMembers.ContractApplicationType);
                else
                {
                    _purchaseRandomMoonContractMethod = _contractApplicationType.GetMethod(
                        LategameUpgradesMembers.PurchaseRandomMoonContractMethod, Any | BindingFlags.Instance);
                    _purchaseSpecifiedMoonContractMethod = _contractApplicationType.GetMethod(
                        LategameUpgradesMembers.PurchaseSpecifiedMoonContractMethod, Any | BindingFlags.Instance);
                    AddIfNull(missing, _purchaseRandomMoonContractMethod, LategameUpgradesMembers.ContractApplicationType + "." + LategameUpgradesMembers.PurchaseRandomMoonContractMethod);
                    AddIfNull(missing, _purchaseSpecifiedMoonContractMethod, LategameUpgradesMembers.ContractApplicationType + "." + LategameUpgradesMembers.PurchaseSpecifiedMoonContractMethod);
                }

                // Only the assignment path decides usability. The three shop entry points are
                // suppression-only: losing one costs that entry point, not the bridge.
                _usable = _instanceProperty != null
                    && _contractLevelField != null
                    && _contractTypeField != null
                    && _lastContractIndexField != null
                    && _syncContractDetailsClientRpc != null
                    && _contractsField != null;
            }
            catch (Exception e)
            {
                _usable = false;
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Resolving Lategame Upgrades failed; the contract bridge stands down: {e.Message}");
                return;
            }

            if (missing.Count > 0)
            {
                var message = new StringBuilder();
                message.Append(LogPrefix).Append(" Lategame Upgrades is present but ").Append(missing.Count)
                    .Append(missing.Count == 1 ? " member did not resolve" : " members did not resolve")
                    .Append(_usable ? " (contract assignment still works): " : " (contract assignment stands down): ");
                message.Append(string.Join(", ", missing.ToArray()));
                message.Append(". Verified against MoreShipUpgrades ").Append(LategameUpgradesMembers.VerifiedAgainstVersion).Append('.');
                ContractsBootstrap.Log?.LogWarning(message.ToString());
            }
            else
            {
                ContractsBootstrap.Log?.LogInfo($"{LogPrefix} Lategame Upgrades detected; contract bridge active.");
            }
        }

        private static void AddIfNull(List<string> missing, object handle, string name)
        {
            if (handle == null)
                missing.Add(name);
        }
    }
}
