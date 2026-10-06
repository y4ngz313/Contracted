using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx.Bootstrap;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Core.Compatibility
{
    /// <summary>
    /// #1195. Read-only reflection bridge to Wesley's Interiors secondary objectives
    /// (<c>WesleysInteriorTools.dll</c>, shipped in Wesley's Shenanigans).
    ///
    /// One <c>OptionalObjectiveController</c> NetworkBehaviour lives in every Wesley interior. About
    /// 2.5 s after it wakes the host picks one of its three <c>ObjectiveEntries</c>; puzzle props call
    /// into it through <c>OptionalObjectivePrefab</c>. The step count (<c>CompletionsAmount</c>) and
    /// <c>RewardSpawned</c> only ever change on the host, so everything past
    /// <see cref="TryReadHost"/> is a host-only read, and clients learn the result from Contracted's
    /// own message instead.
    ///
    /// No compile-time reference: every type and member is resolved by name once and cached; the
    /// value-typed reads go through Harmony field-ref delegates (<see cref="ValueRef{F}"/>) so a poll
    /// does not box. Nothing here writes a Wesley field or invokes a Wesley
    /// method. The one hook is a host-side postfix on two generated RPC handlers, which only reads
    /// the sender (see <see cref="EnsureAttributionPatches"/>). Any failure logs one warning and
    /// stands down only what failed; nothing else in Contracted depends on it. Every body that
    /// touches a resolved member is <see cref="MethodImplOptions.NoInlining"/>, the
    /// <see cref="LategameUpgradesCompat"/> shape.
    /// </summary>
    internal static class WesleysInteriorToolsCompat
    {
        internal const string PluginGuid = "MagicWesley.WesleysInteriorTools";

        private const string LogPrefix = "[MoonContracts.Wesley]";
        private const string ControllerTypeName = "WesleysInteriorTools.Features.OptionalObjectiveController";
        private const string EntryTypeName = "WesleysInteriorTools.Features.OptionalObjectiveEntry";
        private const string PrefabTypeName = "WesleysInteriorTools.Features.OptionalObjectivePrefab";
        private const string IncreaseRpcName = "IncreaseObjectiveCounterServerRpc";
        private const string CaseOpenRpcName = "UpdateTriggerInteractServerRpc";
        private const string RpcHandlerPrefix = "__rpc_handler_";
        private const string VerifiedAgainstVersion = "WesleysInteriorTools 1.0.3 (Shenanigans 1.0.0 and 1.0.16)";

        private static bool _resolved;
        private static bool _usable;

        private static Type _controllerType;
        // Required: without these there is no objective to publish or to look for.
        private static FieldInfo _entriesField;
        private static Func<object, int> _currentIndex;
        private static Func<object, bool> _searched;
        private static FieldInfo _prefabsField;
        private static FieldInfo _entryNameField;
        private static Func<object, int> _entryRequired;
        private static Func<object, int> _entryAmount;
        private static FieldInfo _prefabNameField;
        private static Func<object, bool> _prefabEnableObject;
        // Optional: each one only costs the signal it feeds.
        private static Func<object, bool> _completed;
        private static Func<object, bool> _rewardSpawned;
        private static FieldInfo _triggerColliderField;
        private static FieldInfo _interactTriggerField;
        private static FieldInfo _rewardSpawnTransformField;
        private static FieldInfo _rewardItemBadField;
        private static FieldInfo _rewardItemMediumField;
        private static FieldInfo _rewardItemGoodField;

        private static bool _attributionAttempted;
        private static Harmony _attributionHarmony;

        /// <summary>
        /// Host-only, raised from inside Wesley's <c>IncreaseObjectiveCounterServerRpc</c> handler once
        /// its body has run: the controller, and the NGO client id that sent the RPC. Sender
        /// <see cref="NetworkManager.ServerClientId"/> means the host's own code sent it - the host
        /// player, or a host-side puzzle event - and cannot be told apart here.
        /// </summary>
        internal static Action<NetworkBehaviour, ulong> StepIncreased;

        /// <summary>Host-only, raised from inside <c>UpdateTriggerInteractServerRpc</c>'s handler: the
        /// case was opened, and by whom (same sender rule as <see cref="StepIncreased"/>).</summary>
        internal static Action<NetworkBehaviour, ulong> CaseOpened;

        /// <summary>True when Wesley's tools are loaded and every required member resolved, and no
        /// read has failed since.</summary>
        internal static bool IsUsable
        {
            get
            {
                if (!_resolved)
                    EnsureResolved();
                return _usable;
            }
        }

        /// <summary>One host read of the live controller.</summary>
        internal struct HostReading
        {
            /// <summary>The host has picked an entry and the controller is still spawned.</summary>
            internal bool Chosen;
            internal string EntryName;
            internal int Count;
            internal int Required;
            internal bool Completed;
            internal bool RewardSpawned;
            /// <summary>-1 when the controller has no readable trigger collider, else 0 or 1.</summary>
            internal int TriggerColliderEnabled;
        }

        /// <summary>The live controller, or null. Callers bound how often they ask and cache the answer.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static NetworkBehaviour FindController()
        {
            if (!IsUsable)
                return null;

            try
            {
                // #874 Q11 (F11): the controller is a spawned NetworkBehaviour, so it is looked for
                // among the spawned network objects, not by a scene-wide FindObjectOfType: that scan
                // walks every object in a generated interior, and the 23 September log put
                // SideObjectiveRuntime at 16-44 ms on its search frames (up to eight a round while
                // the controller is still waking). An unspawned controller was never readable
                // anyway (TryReadHost needs IsSpawned). Once found, SideObjectiveWesleyInteriorSource
                // keeps it for the round.
                NetworkManager manager = NetworkManager.Singleton;
                if (manager == null || manager.SpawnManager == null)
                    return null;
                foreach (NetworkObject spawned in manager.SpawnManager.SpawnedObjectsList)
                {
                    if (spawned == null)
                        continue;
                    if (spawned.GetComponentInChildren(_controllerType) is NetworkBehaviour controller)
                        return controller;
                }

                return null;
            }
            catch (Exception e)
            {
                Fail("finding the objective controller", e);
                return null;
            }
        }

        /// <summary>True when <paramref name="behaviour"/> is a Wesley objective controller.</summary>
        internal static bool IsController(NetworkBehaviour behaviour)
        {
            return behaviour != null && _controllerType != null && _controllerType.IsInstanceOfType(behaviour);
        }

        /// <summary>
        /// Host-only. False when unusable or unreadable. A controller the host has despawned
        /// (Wesley's own "Enable secondaries" / "Disable with trials" gates despawn it on pick) or
        /// not picked for yet reads as <c>Chosen = false</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryReadHost(NetworkBehaviour controller, ref HostReading reading)
        {
            reading = default;
            reading.TriggerColliderEnabled = -1;
            if (!IsUsable || controller == null)
                return false;

            try
            {
                if (!controller.IsSpawned || !_searched(controller))
                    return true;

                if (!(_entriesField.GetValue(controller) is IList entries))
                    return true;

                int index = _currentIndex(controller);
                if (index < 0 || index >= entries.Count || entries[index] == null)
                    return true;

                object entry = entries[index];
                reading.Chosen = true;
                reading.EntryName = _entryNameField.GetValue(entry) as string ?? string.Empty;
                reading.Required = _entryRequired(entry);
                reading.Count = _entryAmount(entry);
                reading.Completed = _completed != null && _completed(controller);
                reading.RewardSpawned = _rewardSpawned != null && _rewardSpawned(controller);
                if (_triggerColliderField != null && _triggerColliderField.GetValue(controller) is Collider collider && collider != null)
                    reading.TriggerColliderEnabled = collider.enabled ? 1 : 0;
                return true;
            }
            catch (Exception e)
            {
                Fail("reading the objective controller", e);
                reading = default;
                reading.TriggerColliderEnabled = -1;
                return false;
            }
        }

        /// <summary>Copies the controller's entry keys into <paramref name="names"/>; returns how many.
        /// Serialized data, identical on every peer.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int ReadEntryNames(NetworkBehaviour controller, string[] names)
        {
            if (!IsUsable || controller == null || names == null)
                return 0;

            try
            {
                if (!(_entriesField.GetValue(controller) is IList entries))
                    return 0;

                int count = Math.Min(entries.Count, names.Length);
                for (int i = 0; i < count; i++)
                    names[i] = entries[i] != null ? _entryNameField.GetValue(entries[i]) as string : null;
                return count;
            }
            catch (Exception e)
            {
                Fail("reading the objective entries", e);
                return 0;
            }
        }

        /// <summary>
        /// Appends the live props of <paramref name="entryName"/> from the controller's own
        /// <c>Prefabs</c> list: matching <c>ObjectiveName</c>, <c>EnableObject</c> false, still active.
        /// Every peer fills that list itself when the pick timer fires, so no scene search is needed.
        /// Returns the list's raw length, which moves when Wesley adds late props.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int CollectObjectiveProps(NetworkBehaviour controller, string entryName, List<Component> props)
        {
            if (!IsUsable || controller == null || string.IsNullOrEmpty(entryName))
                return 0;

            try
            {
                if (!(_prefabsField.GetValue(controller) is IList prefabs))
                    return 0;

                for (int i = 0; i < prefabs.Count; i++)
                {
                    if (!(prefabs[i] is Component prefab) || prefab == null || !prefab.gameObject.activeInHierarchy)
                        continue;
                    if (!string.Equals(_prefabNameField.GetValue(prefab) as string, entryName, StringComparison.Ordinal))
                        continue;
                    if (_prefabEnableObject(prefab))
                        continue;
                    // Each prop also re-adds itself five seconds after waking, so the list repeats.
                    if (!props.Contains(prefab))
                        props.Add(prefab);
                }

                return prefabs.Count;
            }
            catch (Exception e)
            {
                Fail("reading the objective props", e);
                return 0;
            }
        }

        /// <summary>The raw length of the controller's <c>Prefabs</c> list, or -1.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int CountObjectiveProps(NetworkBehaviour controller)
        {
            if (!IsUsable || controller == null)
                return -1;

            try
            {
                return _prefabsField.GetValue(controller) is IList prefabs ? prefabs.Count : -1;
            }
            catch (Exception e)
            {
                Fail("reading the objective props", e);
                return -1;
            }
        }

        /// <summary>The reward case's trigger, or null.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static InteractTrigger GetCaseTrigger(NetworkBehaviour controller)
        {
            if (!IsUsable || controller == null || _interactTriggerField == null)
                return null;

            try
            {
                InteractTrigger trigger = _interactTriggerField.GetValue(controller) as InteractTrigger;
                return trigger != null ? trigger : null;
            }
            catch (Exception e)
            {
                Fail("reading the case trigger", e);
                return null;
            }
        }

        /// <summary>Host-only. Where the reward item spawns, or null.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Transform GetRewardSpawn(NetworkBehaviour controller)
        {
            if (!IsUsable || controller == null || _rewardSpawnTransformField == null)
                return null;

            try
            {
                Transform spawn = _rewardSpawnTransformField.GetValue(controller) as Transform;
                return spawn != null ? spawn : null;
            }
            catch (Exception e)
            {
                Fail("reading the reward spawn", e);
                return null;
            }
        }

        /// <summary>True when <paramref name="item"/> is one of the controller's three reward items.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IsRewardItem(NetworkBehaviour controller, Item item)
        {
            if (!IsUsable || controller == null || item == null)
                return false;

            try
            {
                return IsSameItem(_rewardItemBadField, controller, item)
                    || IsSameItem(_rewardItemMediumField, controller, item)
                    || IsSameItem(_rewardItemGoodField, controller, item);
            }
            catch (Exception e)
            {
                Fail("reading the reward items", e);
                return false;
            }
        }

        private static bool IsSameItem(FieldInfo field, NetworkBehaviour controller, Item item)
        {
            return field != null && ReferenceEquals(field.GetValue(controller), item);
        }

        /// <summary>
        /// Installs, once per session, a postfix on the generated RPC handlers of
        /// <c>IncreaseObjectiveCounterServerRpc</c> and <c>UpdateTriggerInteractServerRpc</c>. The RPC
        /// methods themselves take only a name; the sender exists only in the handler's
        /// <c>__RpcParams</c>. Handlers are named by a hash, so they are found by reading each
        /// <c>__rpc_handler_*</c> body for its call to the RPC. On the host NGO runs a host-sent
        /// ServerRpc synchronously through the same handler with sender 0, so the postfix sees every
        /// step. A failure logs once and leaves attribution to the crew-wide fallback; the side
        /// objective itself is unaffected.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void EnsureAttributionPatches()
        {
            if (_attributionAttempted || !IsUsable)
                return;

            _attributionAttempted = true;
            try
            {
                MethodInfo increaseHandler = FindRpcHandler(IncreaseRpcName);
                MethodInfo caseHandler = FindRpcHandler(CaseOpenRpcName);
                MethodInfo increasePostfix = typeof(WesleysInteriorToolsCompat).GetMethod(nameof(IncreaseHandlerPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo casePostfix = typeof(WesleysInteriorToolsCompat).GetMethod(nameof(CaseHandlerPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                if (increaseHandler == null || caseHandler == null)
                {
                    ContractsBootstrap.Log?.LogWarning(
                        $"{LogPrefix} Could not find the RPC handler for "
                        + (increaseHandler == null ? IncreaseRpcName : CaseOpenRpcName)
                        + "; side objective credits go to the whole crew.");
                    return;
                }

                _attributionHarmony = new Harmony(ContractsBootstrap.PLUGIN_GUID + ".wesley.sideobjective");
                _attributionHarmony.Patch(increaseHandler, postfix: new HarmonyMethod(increasePostfix));
                _attributionHarmony.Patch(caseHandler, postfix: new HarmonyMethod(casePostfix));
                ContractsBootstrap.Log?.LogInfo($"{LogPrefix} Attributing side objective steps from {increaseHandler.Name} and {caseHandler.Name}.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Attribution patches failed; side objective credits go to the whole crew: {e.Message}");
                try { _attributionHarmony?.UnpatchSelf(); }
                catch { }
            }
        }

        private static MethodInfo FindRpcHandler(string rpcName)
        {
            MethodInfo[] methods = _controllerType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!method.Name.StartsWith(RpcHandlerPrefix, StringComparison.Ordinal))
                    continue;

                foreach (KeyValuePair<OpCode, object> instruction in PatchProcessor.ReadMethodBody(method))
                {
                    if ((instruction.Key == OpCodes.Call || instruction.Key == OpCodes.Callvirt)
                        && instruction.Value is MethodInfo called
                        && called.DeclaringType == _controllerType
                        && string.Equals(called.Name, rpcName, StringComparison.Ordinal))
                    {
                        return method;
                    }
                }
            }

            return null;
        }

        // Postfixes on Wesley's static handlers: (NetworkBehaviour target, FastBufferReader reader,
        // __RpcParams rpcParams). Bound by position. They must never throw into Wesley's handler.
        private static void IncreaseHandlerPostfix(NetworkBehaviour __0, __RpcParams __2)
        {
            Raise(StepIncreased, __0, __2);
        }

        private static void CaseHandlerPostfix(NetworkBehaviour __0, __RpcParams __2)
        {
            Raise(CaseOpened, __0, __2);
        }

        private static void Raise(Action<NetworkBehaviour, ulong> callback, NetworkBehaviour controller, __RpcParams rpcParams)
        {
            try
            {
                if (callback == null || controller == null || !controller.IsServer)
                    return;
                callback(controller, rpcParams.Server.Receive.SenderClientId);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"{LogPrefix} Side objective attribution skipped: {e.Message}");
            }
        }

        /// <summary>Logs the failure once and stands the feature down for the session.</summary>
        private static void Fail(string what, Exception e)
        {
            if (!_usable)
                return;

            _usable = false;
            ContractsBootstrap.Log?.LogWarning(
                $"{LogPrefix} Failed {what}; Wesley side objectives stand down for this session: {e.GetType().Name}: {e.Message}");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EnsureResolved()
        {
            if (_resolved)
                return;

            _resolved = true;
            if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var info) || info == null)
            {
                ContractsBootstrap.Log?.LogInfo($"{LogPrefix} Wesley's Interior Tools not installed; side objectives stay off.");
                return;
            }

            var missing = new List<string>();
            try
            {
                if (info.Instance == null)
                {
                    ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Wesley's Interior Tools is registered but has no plugin instance; side objectives stay off.");
                    return;
                }

                Assembly assembly = info.Instance.GetType().Assembly;
                const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _controllerType = assembly.GetType(ControllerTypeName, throwOnError: false);
                Type entryType = assembly.GetType(EntryTypeName, throwOnError: false);
                Type prefabType = assembly.GetType(PrefabTypeName, throwOnError: false);

                if (_controllerType == null || !typeof(NetworkBehaviour).IsAssignableFrom(_controllerType))
                {
                    missing.Add(ControllerTypeName);
                    _controllerType = null;
                }
                else
                {
                    _entriesField = Field(_controllerType, "ObjectiveEntries", null, missing, Instance);
                    _currentIndex = ValueRef<int>(_controllerType, "CurrentEnabledObjective", missing, Instance);
                    _searched = ValueRef<bool>(_controllerType, "ObjectivesSearched", missing, Instance);
                    _prefabsField = Field(_controllerType, "Prefabs", null, missing, Instance);
                    _completed = ValueRef<bool>(_controllerType, "IsObjectiveCompleted", missing, Instance);
                    _rewardSpawned = ValueRef<bool>(_controllerType, "RewardSpawned", missing, Instance);
                    _triggerColliderField = Field(_controllerType, "TriggerCollider", null, missing, Instance);
                    _interactTriggerField = Field(_controllerType, "InteractTrigger", null, missing, Instance);
                    _rewardSpawnTransformField = Field(_controllerType, "RewardSpawnTransform", null, missing, Instance);
                    _rewardItemBadField = Field(_controllerType, "RewardItemBad", null, missing, Instance);
                    _rewardItemMediumField = Field(_controllerType, "RewardItemMedium", null, missing, Instance);
                    _rewardItemGoodField = Field(_controllerType, "RewardItemGood", null, missing, Instance);
                }

                if (entryType == null)
                    missing.Add(EntryTypeName);
                else
                {
                    _entryNameField = Field(entryType, "Name", typeof(string), missing, Instance);
                    _entryRequired = ValueRef<int>(entryType, "CompletionsRequired", missing, Instance);
                    _entryAmount = ValueRef<int>(entryType, "CompletionsAmount", missing, Instance);
                }

                if (prefabType == null)
                    missing.Add(PrefabTypeName);
                else
                {
                    _prefabNameField = Field(prefabType, "ObjectiveName", typeof(string), missing, Instance);
                    _prefabEnableObject = ValueRef<bool>(prefabType, "EnableObject", missing, Instance);
                }

                _usable = _controllerType != null
                    && _entriesField != null
                    && _currentIndex != null
                    && _searched != null
                    && _prefabsField != null
                    && _entryNameField != null
                    && _entryRequired != null
                    && _entryAmount != null
                    && _prefabNameField != null
                    && _prefabEnableObject != null;
            }
            catch (Exception e)
            {
                _usable = false;
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Resolving Wesley's Interior Tools failed; side objectives stay off: {e.Message}");
                return;
            }

            if (missing.Count > 0)
            {
                var message = new StringBuilder();
                message.Append(LogPrefix).Append(" Wesley's Interior Tools is present but ").Append(missing.Count)
                    .Append(missing.Count == 1 ? " member did not resolve" : " members did not resolve")
                    .Append(_usable ? " (side objectives still run with less detail): " : " (side objectives stay off): ")
                    .Append(string.Join(", ", missing.ToArray()))
                    .Append(". Verified against ").Append(VerifiedAgainstVersion).Append('.');
                ContractsBootstrap.Log?.LogWarning(message.ToString());
            }
            else
            {
                ContractsBootstrap.Log?.LogInfo($"{LogPrefix} Wesley's Interior Tools detected; side objectives active.");
            }
        }

        /// <summary>Resolves one instance field, checking its type when <paramref name="expected"/> is
        /// given, and records a miss by name.</summary>
        private static FieldInfo Field(Type type, string name, Type expected, List<string> missing, BindingFlags flags)
        {
            FieldInfo field = type.GetField(name, flags);
            if (field != null && (expected == null || field.FieldType == expected))
                return field;

            missing.Add(type.Name + "." + name);
            return null;
        }

        /// <summary>
        /// A reader for one value-typed instance field. Built on a Harmony field-ref delegate so a read
        /// does not box; if the runtime cannot emit one, a reflection read keeps the feature working
        /// at the cost of the box.
        /// </summary>
        private static Func<object, F> ValueRef<F>(Type type, string name, List<string> missing, BindingFlags flags)
        {
            FieldInfo field = Field(type, name, typeof(F), missing, flags);
            if (field == null)
                return null;

            try
            {
                AccessTools.FieldRef<object, F> reference = AccessTools.FieldRefAccess<F>(type, name);
                return instance => reference(instance);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"{LogPrefix} No field-ref accessor for {type.Name}.{name}; reading it by reflection: {e.Message}");
                return instance => (F)field.GetValue(instance);
            }
        }
    }
}
