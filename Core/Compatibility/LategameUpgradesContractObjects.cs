using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.Lgu;

namespace Y4NGZCompany.Core.Compatibility
{
    /// <summary>The five map objects Lategame Upgrades spawns for its own contracts.</summary>
    internal enum LguContractObject
    {
        Laptop,
        Nest,
        Scavenger,
        Pentagram,
        Device,
    }

    /// <summary>
    /// #804. Reflection-only reader for the state of Lategame Upgrades' contract objects, plus
    /// the Harmony postfixes that latch their progress.
    ///
    /// The design rests on one fact about LGU: every progress transition it has is announced by
    /// a <c>ClientRpc</c>, and a ClientRpc body executes on every peer. A postfix on each one
    /// therefore latches the same progress on every machine, so Contracted derives its
    /// objectives locally and adds no networking, no NetworkBehaviour and no weavable file. The
    /// one gap that leaves is a late joiner, who missed the RPCs; the assignment snapshot
    /// carries a compact progress payload for exactly that case (see
    /// <see cref="LguContractRuntime"/>).
    ///
    /// Everything is optional. A member that does not resolve costs its own objective's detail
    /// and nothing else, and the assignment bridge in <see cref="LategameUpgradesCompat"/> is
    /// never stood down by anything here. Postfix parameters are <c>object</c> or the
    /// game-side <c>Component</c>, never an LGU type, so no signature names MoreShipUpgrades.
    /// </summary>
    internal static class LategameUpgradesContractObjects
    {
        private const string LogPrefix = "[MoonContracts.LGU]";

        /// <summary>How long a failed scan is trusted before another one is allowed. The scan
        /// is a <c>FindObjectsOfType</c>, so it must not run every frame while the object has
        /// not spawned yet.</summary>
        private const float RescanIntervalSeconds = 1f;

        private static bool _resolved;
        private static bool _applied;

        private static Type _dataPcType;
        private static Type _nestType;
        private static Type _scavengerType;
        private static Type _pentagramType;
        private static Type _deviceType;

        private static FieldInfo _currentRitualField;
        private static FieldInfo _demonInstructionsField;
        private static FieldInfo _bombTimerField;

        private static PropertyInfo _upgradeBusInstance;
        private static PropertyInfo _pluginConfiguration;
        private static PropertyInfo _contractsConfiguration;
        private static PropertyInfo _exterminationConfiguration;
        private static PropertyInfo _amountSpawns;

        private static readonly Component[] Cached = new Component[5];
        private static readonly float[] NextScanAt = new float[5];

        /// <summary>Cached spawn count; LGU's own value is a synced config entry that cannot
        /// change mid-round, so one successful read per session is enough.</summary>
        private static int _exterminationSpawnCount = -1;

        /// <summary>Rate limit for the spawn-count read, on the same clock as the object scans.
        /// The lookup is a five-hop reflection chain and the objective list asks for it once a
        /// frame, so a session where <c>UpgradeBus.Instance</c> is null - LGU absent, or not yet
        /// awake - must not re-run it every frame for the rest of the round.</summary>
        private static float _nextExterminationScanAt;

        /// <summary>
        /// The live LGU object of this kind, or null. Cheap on the hot path: the cached
        /// component is returned straight away while it is alive, and a miss re-scans at most
        /// once a second. Callers must gate on being landed with the matching contract active;
        /// this method does not, because it has no opinion about which contract is running.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Component FindActiveObject(LguContractObject kind)
        {
            int slot = (int)kind;
            Component cached = Cached[slot];
            if (cached != null)
                return cached;

            float now = Time.realtimeSinceStartup;
            if (now < NextScanAt[slot])
                return null;
            NextScanAt[slot] = now + RescanIntervalSeconds;

            Type type = ResolveObjectType(kind);
            if (type == null)
                return null;

            try
            {
                UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(type);
                for (int i = 0; i < found.Length; i++)
                {
                    if (found[i] is Component component && component != null)
                    {
                        Cached[slot] = component;
                        return component;
                    }
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Could not scan for LGU's {kind}: {e.Message}");
            }

            return null;
        }

        /// <summary>#1197. The cached live object of this kind, or null. Never scans: the pity
        /// timer reads at its own cadence and must not add a <c>FindObjectsOfType</c>; the
        /// runtime's per-frame locate tracking is what keeps this cache filled.</summary>
        internal static Component GetCachedObject(LguContractObject kind)
        {
            Component cached = Cached[(int)kind];
            return cached != null ? cached : null;
        }

        /// <summary>Records a component an LGU RPC already handed us, so the next objective
        /// pass needs no scan at all and the card can move on the frame LGU announced it.</summary>
        internal static void Seed(LguContractObject kind, Component instance)
        {
            if (instance != null)
                Cached[(int)kind] = instance;
        }

        /// <summary>Drops every cached component and re-arms the scans. Called from the
        /// contract run reset; a stale component from the previous round would otherwise be a
        /// destroyed Unity object that reads as null only through the overloaded operator.</summary>
        internal static void ResetRunState()
        {
            for (int i = 0; i < Cached.Length; i++)
            {
                Cached[i] = null;
                NextScanAt[i] = 0f;
            }

            _nextExterminationScanAt = 0f;
        }

        /// <summary>
        /// Ritual items still to be placed on <paramref name="pentagram"/>, or -1 when the
        /// list is unreadable. A count of zero is ambiguous and this method cannot resolve it:
        /// LGU fills <c>currentRitual</c> only in <c>SyncAltarClientRpc</c>'s execute stage, so
        /// an empty list is either a finished ritual or a peer that never received that RPC.
        /// The caller disambiguates with a local-observation latch it sets only when one of
        /// those RPCs has executed on this peer and shown the list populated
        /// (<c>LguProgressRules</c>); a zero read before that means "unknown".
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int GetRitualRemaining(Component pentagram)
        {
            EnsureResolved();
            if (pentagram == null || _currentRitualField == null)
                return -1;

            try
            {
                return _currentRitualField.GetValue(pentagram) is IList ritual ? ritual.Count : -1;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Could not read the outstanding ritual items: {e.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Resolves the demon LGU's altar named. <paramref name="index"/> is the argument
        /// <c>SyncAltarClientRpc</c> carried; LGU itself resolves it as the nth key of
        /// <c>DemonInstructions</c>, so this does the same and reads the item count off the
        /// same entry rather than assuming one.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryGetDemon(int index, out string name, out int itemCount)
        {
            name = null;
            itemCount = LategameUpgradesMembers.DefaultRitualItemCount;
            EnsureResolved();
            if (index < 0 || _demonInstructionsField == null)
                return false;

            try
            {
                if (!(_demonInstructionsField.GetValue(null) is IDictionary instructions))
                    return false;

                int position = 0;
                foreach (DictionaryEntry entry in instructions)
                {
                    if (position++ != index)
                        continue;

                    name = entry.Key as string;
                    if (entry.Value is ICollection items && items.Count > 0)
                        itemCount = items.Count;
                    return !string.IsNullOrEmpty(name);
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Could not read LGU's demon table: {e.Message}");
            }

            return false;
        }

        /// <summary>Seconds left on the device's own countdown, or -1 when it is unreadable.
        /// The timer is local to each peer - LGU never replicates it - so every machine renders
        /// its own copy, which is also the copy that detonates the device on that machine.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float GetBombTimerSeconds(Component device)
        {
            EnsureResolved();
            if (device == null || _bombTimerField == null)
                return -1f;

            try
            {
                object value = _bombTimerField.GetValue(device);
                return value is float seconds ? seconds : -1f;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Could not read the device countdown: {e.Message}");
                return -1f;
            }
        }

        /// <summary>Hoarding bugs an extermination contract releases, from LGU's own synced
        /// config, falling back to its shipped default.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int GetExterminationSpawnCount()
        {
            if (_exterminationSpawnCount > 0)
                return _exterminationSpawnCount;

            float now = Time.realtimeSinceStartup;
            if (now < _nextExterminationScanAt)
                return LategameUpgradesMembers.DefaultExterminationSpawnCount;
            _nextExterminationScanAt = now + RescanIntervalSeconds;

            EnsureResolved();
            int resolved = -1;
            try
            {
                if (_upgradeBusInstance != null
                    && _pluginConfiguration != null
                    && _contractsConfiguration != null
                    && _exterminationConfiguration != null
                    && _amountSpawns != null)
                {
                    object bus = _upgradeBusInstance.GetValue(null);
                    object configuration = bus == null ? null : _pluginConfiguration.GetValue(bus);
                    object contracts = configuration == null ? null : _contractsConfiguration.GetValue(configuration);
                    object extermination = contracts == null ? null : _exterminationConfiguration.GetValue(contracts);
                    object entry = extermination == null ? null : _amountSpawns.GetValue(extermination);

                    // SyncedEntry<int> is CSync's type, not LGU's, so its Value is read by shape
                    // rather than declared in the member table the regression suite proves.
                    PropertyInfo value = entry?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (value?.GetValue(entry) is int amount && amount > 0)
                        resolved = amount;
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"{LogPrefix} Could not read LGU's hoarding-bug spawn count; using {LategameUpgradesMembers.DefaultExterminationSpawnCount}: {e.Message}");
            }

            // Only a real answer is cached. UpgradeBus is a MonoBehaviour whose Instance is set
            // in Awake, so an early call can legitimately find nothing; caching the fallback
            // there would freeze a wrong bug count in for the rest of the session.
            if (resolved <= 0)
                return LategameUpgradesMembers.DefaultExterminationSpawnCount;

            _exterminationSpawnCount = resolved;
            return resolved;
        }

        /// <summary>
        /// Attaches one postfix per LGU progress ClientRpc. Applied by hand, like the shop
        /// suppression: the targets live in a foreign assembly and cannot be named in a
        /// <c>[HarmonyPatch]</c> attribute without taking a compile reference on it.
        /// </summary>
        internal static void ApplyProgressPatches(Harmony harmony)
        {
            if (_applied || harmony == null || !LategameUpgradesCompat.IsLoaded)
                return;

            _applied = true;
            EnsureResolved();

            Patch(harmony, _dataPcType, LategameUpgradesMembers.InitiateGameClientRpcMethod, nameof(InitiateGame_Postfix));
            Patch(harmony, _dataPcType, LategameUpgradesMembers.ExitGameClientRpcMethod, nameof(ExitGame_Postfix));
            Patch(harmony, _nestType, LategameUpgradesMembers.DisableNestClientRpcMethod, nameof(DisableNest_Postfix));
            Patch(harmony, _scavengerType, LategameUpgradesMembers.HealScavClientRpcMethod, nameof(HealScav_Postfix));
            Patch(harmony, _pentagramType, LategameUpgradesMembers.SyncAltarClientRpcMethod, nameof(SyncAltar_Postfix));
            Patch(harmony, _pentagramType, LategameUpgradesMembers.SyncCurrentRitualClientRpcMethod, nameof(SyncCurrentRitual_Postfix));
            Patch(harmony, _pentagramType, LategameUpgradesMembers.FailRitualClientRpcMethod, nameof(FailRitual_Postfix));
            Patch(harmony, _deviceType, LategameUpgradesMembers.SyncBombDetailsClientRpcMethod, nameof(SyncBombDetails_Postfix));
            Patch(harmony, _deviceType, LategameUpgradesMembers.BombStateChangeClientRpcMethod, nameof(BombStateChange_Postfix));
        }

        private static void Patch(Harmony harmony, Type declaring, string method, string postfixName)
        {
            if (declaring == null)
                return;

            MethodInfo target = AccessTools.Method(declaring, method);
            if (target == null)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} {declaring.Name}.{method} did not resolve; that objective will not update.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(LategameUpgradesContractObjects), postfixName)));
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Could not patch {declaring.Name}.{method}: {e.Message}");
            }
        }

        // Every postfix below runs inside an LGU ClientRpc body, which netcode invokes twice on
        // the host: once to send and once to execute. The runtime's latches are all idempotent
        // "set once" writes, so a doubled call is harmless and no send/execute test is needed.

        private static void InitiateGame_Postfix(Component __instance)
        {
            Guard(() => LguContractRuntime.NotifyLaptopOpened(__instance), "data-retrieval login screen");
        }

        private static void ExitGame_Postfix(Component __instance, bool succeeded)
        {
            Guard(() => LguContractRuntime.NotifyLaptopExited(__instance, succeeded), "data-retrieval exit");
        }

        private static void DisableNest_Postfix()
        {
            Guard(LguContractRuntime.NotifyNestDestroyed, "nest destroyed");
        }

        private static void HealScav_Postfix(Component __instance)
        {
            Guard(() => LguContractRuntime.NotifyScavengerHealed(__instance), "scavenger treated");
        }

        private static void SyncAltar_Postfix(Component __instance, int index)
        {
            Guard(() => LguContractRuntime.NotifyDemonChosen(__instance, index), "altar demon");
        }

        private static void SyncCurrentRitual_Postfix(Component __instance)
        {
            Guard(() => LguContractRuntime.NotifyRitualItemPlaced(__instance), "ritual item placed");
        }

        private static void FailRitual_Postfix()
        {
            Guard(LguContractRuntime.NotifyRitualFailed, "ritual failed");
        }

        private static void SyncBombDetails_Postfix(Component __instance, string serial)
        {
            Guard(() => LguContractRuntime.NotifyDeviceDetails(__instance, serial), "device details");
        }

        private static void BombStateChange_Postfix(Component __instance)
        {
            Guard(() => LguContractRuntime.NotifyDeviceWireCut(__instance), "device wire cut");
        }

        /// <summary>A throw out of a postfix would surface as an LGU exception in the player's
        /// log and, worse, could abort the rest of the RPC chain. Nothing Contracted derives
        /// for its own HUD is worth that, so every latch is wrapped.</summary>
        private static void Guard(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Objective latch failed ({what}): {e.Message}");
            }
        }

        private static Type ResolveObjectType(LguContractObject kind)
        {
            EnsureResolved();
            switch (kind)
            {
                case LguContractObject.Laptop:
                    return _dataPcType;
                case LguContractObject.Nest:
                    return _nestType;
                case LguContractObject.Scavenger:
                    return _scavengerType;
                case LguContractObject.Pentagram:
                    return _pentagramType;
                case LguContractObject.Device:
                    return _deviceType;
                default:
                    return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EnsureResolved()
        {
            if (_resolved)
                return;

            _resolved = true;
            if (!LategameUpgradesCompat.IsLoaded)
                return;

            var missing = new List<string>();
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(LategameUpgradesCompat.PluginGuid, out var info)
                    || info?.Instance == null)
                    return;

                Assembly assembly = info.Instance.GetType().Assembly;
                const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;

                _dataPcType = Resolve(assembly, LategameUpgradesMembers.DataPcType, missing);
                _nestType = Resolve(assembly, LategameUpgradesMembers.BugNestType, missing);
                _scavengerType = Resolve(assembly, LategameUpgradesMembers.ExtractPlayerType, missing);
                _pentagramType = Resolve(assembly, LategameUpgradesMembers.PentagramType, missing);
                _deviceType = Resolve(assembly, LategameUpgradesMembers.BombDefusalType, missing);

                if (_pentagramType != null)
                {
                    _currentRitualField = _pentagramType.GetField(LategameUpgradesMembers.CurrentRitualField, Any | BindingFlags.Instance);
                    _demonInstructionsField = _pentagramType.GetField(LategameUpgradesMembers.DemonInstructionsField, Any | BindingFlags.Static);
                    Require(missing, _currentRitualField, LategameUpgradesMembers.PentagramType, LategameUpgradesMembers.CurrentRitualField);
                    Require(missing, _demonInstructionsField, LategameUpgradesMembers.PentagramType, LategameUpgradesMembers.DemonInstructionsField);
                }

                if (_deviceType != null)
                {
                    _bombTimerField = _deviceType.GetField(LategameUpgradesMembers.BombTimerField, Any | BindingFlags.Instance);
                    Require(missing, _bombTimerField, LategameUpgradesMembers.BombDefusalType, LategameUpgradesMembers.BombTimerField);
                }

                Type upgradeBus = Resolve(assembly, LategameUpgradesMembers.UpgradeBusType, missing);
                Type contracts = Resolve(assembly, LategameUpgradesMembers.ContractsConfigurationType, missing);
                Type extermination = Resolve(assembly, LategameUpgradesMembers.ExterminationConfigurationType, missing);
                Type configuration = Resolve(assembly, LategameUpgradesMembers.LategameConfigurationType, missing);

                if (upgradeBus != null)
                {
                    _upgradeBusInstance = upgradeBus.GetProperty(LategameUpgradesMembers.UpgradeBusInstanceProperty, Any | BindingFlags.Static);
                    _pluginConfiguration = upgradeBus.GetProperty(LategameUpgradesMembers.PluginConfigurationProperty, Any | BindingFlags.Instance);
                    Require(missing, _upgradeBusInstance, LategameUpgradesMembers.UpgradeBusType, LategameUpgradesMembers.UpgradeBusInstanceProperty);
                    Require(missing, _pluginConfiguration, LategameUpgradesMembers.UpgradeBusType, LategameUpgradesMembers.PluginConfigurationProperty);
                }

                if (configuration != null)
                {
                    _contractsConfiguration = configuration.GetProperty(LategameUpgradesMembers.ContractsConfigurationProperty, Any | BindingFlags.Instance);
                    Require(missing, _contractsConfiguration, LategameUpgradesMembers.LategameConfigurationType, LategameUpgradesMembers.ContractsConfigurationProperty);
                }

                if (contracts != null)
                {
                    _exterminationConfiguration = contracts.GetProperty(LategameUpgradesMembers.ExterminationConfigurationProperty, Any | BindingFlags.Instance);
                    Require(missing, _exterminationConfiguration, LategameUpgradesMembers.ContractsConfigurationType, LategameUpgradesMembers.ExterminationConfigurationProperty);
                }

                if (extermination != null)
                {
                    _amountSpawns = extermination.GetProperty(LategameUpgradesMembers.AmountSpawnsProperty, Any | BindingFlags.Instance);
                    Require(missing, _amountSpawns, LategameUpgradesMembers.ExterminationConfigurationType, LategameUpgradesMembers.AmountSpawnsProperty);
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Resolving LGU's contract objects failed; their objectives will stay blank: {e.Message}");
                return;
            }

            if (missing.Count == 0)
                return;

            var message = new StringBuilder();
            message.Append(LogPrefix).Append(" Lategame Upgrades is present but ").Append(missing.Count)
                .Append(missing.Count == 1 ? " objective member did not resolve" : " objective members did not resolve")
                .Append(" (the contract still plays; its card loses that detail): ");
            message.Append(string.Join(", ", missing.ToArray()));
            message.Append(". Verified against MoreShipUpgrades ").Append(LategameUpgradesMembers.VerifiedAgainstVersion).Append('.');
            ContractsBootstrap.Log?.LogWarning(message.ToString());
        }

        private static Type Resolve(Assembly assembly, string fullName, List<string> missing)
        {
            Type type = assembly.GetType(fullName, throwOnError: false);
            if (type == null)
                missing.Add(fullName);
            return type;
        }

        private static void Require(List<string> missing, object handle, string declaringType, string member)
        {
            if (handle == null)
                missing.Add(declaringType + "." + member);
        }
    }
}
