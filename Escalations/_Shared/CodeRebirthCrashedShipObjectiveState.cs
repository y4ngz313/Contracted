using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Escalations._Shared
{
    internal static class CodeRebirthCrashedShipObjectiveState
    {
        private const string CrashShipTypeName = "CodeRebirth.src.Content.Maps.OxydeCrashingShip";
        private const string TargetBackingFieldName = "<_target>k__BackingField";
        private const float SearchRadius = 14f;
        private const float TypeLookupRetrySeconds = 10f;

        private static Type _crashShipType;
        private static FieldInfo _targetField;
        private static float _nextTypeLookupTime;
        private const int MaxTypeLookupAttempts = 3;
        private static int _typeLookupAttempts;
        private static bool _typeLookupAbandoned;
        private static bool _impactPatchInstalled;
        private static bool _impactPatchFailed;
        private static Harmony _impactHarmony;
        private static bool _active;
        private static bool _searched;
        private static bool _hasKnownPosition;
        private static Vector3 _lastKnownPosition;

        internal static bool IsActive => _active;
        internal static string ActiveTitle => "CRASHED SHIP";

        internal static void ResetRunState()
        {
            // Per round, not per session: three assembly walks at the start of a round is cheap, and
            // a session-permanent latch would miss a CodeRebirth loaded between rounds.
            _typeLookupAttempts = 0;
            _typeLookupAbandoned = false;
            _nextTypeLookupTime = 0f;
            _active = false;
            _searched = false;
            _hasKnownPosition = false;
            _lastKnownPosition = Vector3.zero;
        }

        internal static void UpdateRuntime()
        {
            // WP-A5: the patch probe used to run ahead of the runtime gate, so a session without
            // CodeRebirth walked every loaded assembly every 10 seconds forever, in orbit included
            // (measured 4.38ms). It now runs only while a round is live, and the type lookup latches
            // off after a couple of early misses.
            if (!IsRoundRuntimeActive())
                return;

            EnsureImpactPatch();

            if (_active && !_searched)
                UpdateSearchState();
        }

        private static void EnsureImpactPatch()
        {
            if (_impactPatchInstalled || _impactPatchFailed)
                return;

            Type type = GetCrashShipType();
            if (type == null)
                return;

            MethodInfo onImpact = FindMethodInHierarchy(type, "OnImpact");
            MethodInfo postfix = AccessTools.Method(typeof(CodeRebirthCrashedShipObjectiveState), nameof(OxydeCrashingShipOnImpactPostfix));
            if (onImpact == null || postfix == null)
            {
                _impactPatchFailed = true;
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.CodeRebirth] Could not patch OxydeCrashingShip.OnImpact; crashed ship objective disabled.");
                return;
            }

            try
            {
                _impactHarmony = new Harmony(ContractsBootstrap.PLUGIN_GUID + ".coderebirth.crashedship");
                _impactHarmony.Patch(onImpact, postfix: new HarmonyMethod(postfix));
                _impactPatchInstalled = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.CodeRebirth] Patched OxydeCrashingShip.OnImpact for crashed ship objectives.");
            }
            catch (Exception e)
            {
                _impactPatchFailed = true;
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.CodeRebirth] Failed to patch OxydeCrashingShip.OnImpact; crashed ship objective disabled: {e.Message}");
            }
        }

        internal static List<MoonContractObjective> GetActiveObjectives()
        {
            return new List<MoonContractObjective>
            {
                new MoonContractObjective("Search the wreckage for Company property", _searched ? 1 : 0, 1, key: "crashship.search")
            };
        }

        private static void OxydeCrashingShipOnImpactPostfix(object __instance)
        {
            NotifyCrashShipImpacted(__instance as UnityEngine.Object);
        }

        private static void NotifyCrashShipImpacted(UnityEngine.Object crashShip)
        {
            if (crashShip == null || !TryGetSearchPosition(crashShip, out Vector3 searchPosition))
                return;

            _lastKnownPosition = searchPosition;
            _hasKnownPosition = true;
            if (!_active)
            {
                _active = true;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.CodeRebirth] Crashed ship impact confirmed at {FormatVector(searchPosition)}; appended contract objective.");
            }

            UpdateSearchState();
        }

        private static Type GetCrashShipType()
        {
            if (_crashShipType != null)
                return _crashShipType;
            if (_typeLookupAbandoned)
                return null;
            if (Time.time < _nextTypeLookupTime)
                return null;

            // CodeRebirth is loaded before we ever run, so a miss means it is absent. A couple of
            // early retries cover a late-loading assembly; after that the probe latches off for the
            // rest of the round instead of walking the whole AppDomain every ten seconds forever.
            // ResetRunState re-arms it, so each round gets its own three probes.
            if (++_typeLookupAttempts > MaxTypeLookupAttempts)
            {
                _typeLookupAbandoned = true;
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.CodeRebirth] OxydeCrashingShip not present after "
                    + MaxTypeLookupAttempts + " probes; crashed ship objective stays off for this round.");
                return null;
            }

            _nextTypeLookupTime = Time.time + TypeLookupRetrySeconds;
            Assembly[] assemblies;
            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch
            {
                return null;
            }

            foreach (Assembly assembly in assemblies)
            {
                if (assembly == null)
                    continue;

                Type type;
                try
                {
                    type = assembly.GetType(CrashShipTypeName, throwOnError: false);
                }
                catch
                {
                    continue;
                }

                if (type == null)
                    continue;

                _crashShipType = type;
                _targetField = FindFieldInHierarchy(type, TargetBackingFieldName);
                return _crashShipType;
            }

            return null;
        }

        private static MethodInfo FindMethodInHierarchy(Type type, string methodName)
        {
            while (type != null)
            {
                MethodInfo method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null)
                    return method;
                type = type.BaseType;
            }

            return null;
        }

        private static FieldInfo FindFieldInHierarchy(Type type, string fieldName)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                    return field;
                type = type.BaseType;
            }

            return null;
        }

        private static bool TryGetSearchPosition(UnityEngine.Object crashShip, out Vector3 position)
        {
            position = Vector3.zero;
            if (!(crashShip is Component component) || component.transform == null)
                return false;

            Vector3 currentPosition = component.transform.position;
            if (IsCodeRebirthHiddenParkingPosition(currentPosition))
                return false;

            position = currentPosition;
            if (TryReadTargetPosition(crashShip, out Vector3 targetPosition) && targetPosition != Vector3.zero)
                position = targetPosition;

            return true;
        }

        private static bool TryReadTargetPosition(UnityEngine.Object crashShip, out Vector3 targetPosition)
        {
            targetPosition = Vector3.zero;
            if (_targetField == null)
                return false;

            try
            {
                object value = _targetField.GetValue(crashShip);
                if (value is Vector3 vector)
                {
                    targetPosition = vector;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static string FormatVector(Vector3 vector)
        {
            return $"({vector.x:0.##}, {vector.y:0.##}, {vector.z:0.##})";
        }

        private static bool IsCodeRebirthHiddenParkingPosition(Vector3 position)
        {
            return Mathf.Abs(position.x) > 5000f
                || Mathf.Abs(position.y) > 5000f
                || Mathf.Abs(position.z) > 5000f;
        }

        private static void UpdateSearchState()
        {
            if (_searched || !_active || !_hasKnownPosition)
                return;

            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return;

            foreach (PlayerControllerB player in players)
            {
                if (player == null || !player.isPlayerControlled || player.isPlayerDead)
                    continue;
                if (Vector3.Distance(player.transform.position, _lastKnownPosition) > SearchRadius)
                    continue;

                _searched = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.CodeRebirth] Crashed ship search objective completed.");
                return;
            }
        }

        private static bool IsRoundRuntimeActive()
        {
            StartOfRound start = StartOfRound.Instance;
            return start != null
                && start.shipHasLanded
                && !start.inShipPhase
                && !start.travellingToNewLevel;
        }
    }
}
