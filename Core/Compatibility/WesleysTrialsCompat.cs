using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Core.Compatibility
{
    /// <summary>
    /// #1198. Read-only reflection bridge to the two Wesley types the Wesley's Moons trials are built
    /// on. Both ship in <c>WesleysInteriorTools.dll</c> (Wesley's Shenanigans), which Wesley's Moons
    /// depends on:
    /// <list type="bullet">
    /// <item><c>TrialObject</c>: the marker on every gated trial prop. The host despawns it when
    /// Wesley's [Trials] gates are off, so a live one means the trial runs this round.</item>
    /// <item><c>TriggerCounter</c>: an "(n/m)" counter; <c>TriggeredAmount</c> is raised by its own
    /// server RPC, so the host's value is the count every peer's hover tip shows.</item>
    /// </list>
    /// No compile-time reference, nothing written, no method invoked. A failure logs one warning and
    /// stands down only what failed: without the marker type there are no marker trials; without
    /// the counter members no counter probe resolves, which drops every trial with a counter step to
    /// reveal plus completion (0/1).
    /// </summary>
    internal static class WesleysTrialsCompat
    {
        private const string LogPrefix = "[MoonContracts.Trials]";
        private const string TrialObjectTypeName = "WesleysInteriorTools.Features.TrialObject";
        private const string TriggerCounterTypeName = "WesleysInteriorTools.Features.TriggerCounter";
        private const string VerifiedAgainstVersion = "WesleysInteriorTools 1.0.3 (Shenanigans 1.0.16), Wesley's Moons 7.0.13";

        private static bool _resolved;
        private static bool _usable;
        private static bool _countersUsable;

        private static Type _trialObjectType;
        private static Type _counterType;
        private static Func<object, int> _counterAmount;
        private static FieldInfo _counterEntries;

        /// <summary>Wesley's tools are loaded and the trial marker type resolved.</summary>
        internal static bool IsUsable
        {
            get
            {
                if (!_resolved)
                    EnsureResolved();
                return _usable;
            }
        }

        /// <summary>Every live <c>TrialObject</c>, into <paramref name="results"/>. A scene search:
        /// callers bound how often they ask.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void FindTrialObjects(List<Component> results)
        {
            results.Clear();
            if (!IsUsable)
                return;

            try
            {
                UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(_trialObjectType);
                for (int i = 0; i < found.Length; i++)
                {
                    if (found[i] is Component component && component != null)
                        results.Add(component);
                }
            }
            catch (Exception e)
            {
                _usable = false;
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Finding trial objects failed; trials stand down for this session: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>The counter on <paramref name="transform"/>, or null.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Component GetCounter(Transform transform)
        {
            if (!IsUsable || !_countersUsable || transform == null)
                return null;
            return transform.GetComponent(_counterType);
        }

        /// <summary>Host read: the count and the number of entries. False when unreadable.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryReadCounter(Component counter, out int amount, out int entries)
        {
            amount = 0;
            entries = 0;
            if (!_countersUsable || counter == null)
                return false;

            try
            {
                amount = _counterAmount(counter);
                entries = _counterEntries.GetValue(counter) is IList list ? list.Count : 0;
                return true;
            }
            catch (Exception e)
            {
                _countersUsable = false;
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Reading a trial counter failed; counter steps keep their last value this round, and trials found later fall back to reveal plus completion: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EnsureResolved()
        {
            if (_resolved)
                return;

            _resolved = true;
            if (!Chainloader.PluginInfos.TryGetValue(WesleysInteriorToolsCompat.PluginGuid, out var info) || info == null || info.Instance == null)
                return;

            var missing = new List<string>();
            try
            {
                Assembly assembly = info.Instance.GetType().Assembly;
                const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _trialObjectType = assembly.GetType(TrialObjectTypeName, throwOnError: false);
                if (_trialObjectType == null || !typeof(Component).IsAssignableFrom(_trialObjectType))
                {
                    missing.Add(TrialObjectTypeName);
                    _trialObjectType = null;
                }

                _counterType = assembly.GetType(TriggerCounterTypeName, throwOnError: false);
                if (_counterType == null || !typeof(Component).IsAssignableFrom(_counterType))
                {
                    missing.Add(TriggerCounterTypeName);
                    _counterType = null;
                }
                else
                {
                    FieldInfo amount = _counterType.GetField("TriggeredAmount", Instance);
                    _counterEntries = _counterType.GetField("InteractEntries", Instance);
                    if (amount == null || amount.FieldType != typeof(int))
                        missing.Add("TriggerCounter.TriggeredAmount");
                    else
                        _counterAmount = IntRef(amount);
                    if (_counterEntries == null)
                        missing.Add("TriggerCounter.InteractEntries");
                }

                _usable = _trialObjectType != null;
                _countersUsable = _counterAmount != null && _counterEntries != null;
            }
            catch (Exception e)
            {
                _usable = false;
                _countersUsable = false;
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Resolving Wesley's trial types failed; trials stay off: {e.Message}");
                return;
            }

            if (missing.Count > 0)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"{LogPrefix} Wesley's Interior Tools is present but {string.Join(", ", missing.ToArray())} did not resolve"
                    + (_usable ? " (trials run; a trial with a counter step falls back to reveal plus completion)" : " (trials stay off)")
                    + $". Verified against {VerifiedAgainstVersion}.");
            }
        }

        /// <summary>A non-boxing reader for one int field; a reflection read if the runtime cannot emit
        /// the field-ref delegate.</summary>
        private static Func<object, int> IntRef(FieldInfo field)
        {
            try
            {
                AccessTools.FieldRef<object, int> reference = AccessTools.FieldRefAccess<int>(field.DeclaringType, field.Name);
                return instance => reference(instance);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"{LogPrefix} No field-ref accessor for {field.Name}; reading it by reflection: {e.Message}");
                return instance => (int)field.GetValue(instance);
            }
        }
    }
}
