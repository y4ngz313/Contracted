// BundyShipSystemsBridge.cs — #611 task 1.3.
//
// Battery-sabotage direction, asked of Y4NGZCore instead of reflected out of the monolith.
//
// This file used to hold `"Y4NGZCompany.ShipSystems.Layout.ShipSystemsApi, Y4NGZCompany"`, six
// cached MethodInfo handles, a two-second retry cadence and six try/catch blocks — all of it to
// call methods in the same DLL it was compiled into. The string names the monolith, so after
// the split it resolves to null forever and Bundy quietly stops besieging the battery, with no
// error anywhere. Every one of those concerns now lives in the contract layer:
// ShipSystemsServices supplies the absence defaults (no target, and release a held interrupt),
// isolates a throwing provider, and de-duplicates the warning.

using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCore.Modules.ShipSystems;

namespace Y4NGZCompany.Escalations.Bundy
{
    internal static class BundyShipSystemsBridge
    {
        internal static bool TryForceBatterySabotageTarget(EnemyAI enemy) =>
            ShipSystemsServices.TryForceBatterySabotageTarget(enemy);

        internal static bool IsBatterySabotageTargetActive(EnemyAI enemy) =>
            ShipSystemsServices.IsBatterySabotageTargetActive(enemy);

        internal static bool TryGetBatterySabotageDestination(EnemyAI enemy, out Vector3 destination)
        {
            bool found = ShipSystemsServices.TryGetBatterySabotageDestination(
                enemy, out float x, out float y, out float z);
            destination = found ? new Vector3(x, y, z) : Vector3.zero;
            return found;
        }

        internal static bool TryGetBatterySabotageInterruptTarget(
            EnemyAI enemy, out PlayerControllerB player)
        {
            player = ShipSystemsServices.TryGetBatterySabotageInterruptTarget(enemy)
                as PlayerControllerB;
            return player != null;
        }

        /// <summary>
        /// True when the interrupt should be dropped. <b>True with no provider registered</b> —
        /// an AI must not hold an interrupt for a system nothing in the profile can ever end.
        /// </summary>
        internal static bool ShouldReleaseBatterySabotageInterrupt(
            EnemyAI enemy, PlayerControllerB player) =>
            ShipSystemsServices.ShouldReleaseBatterySabotageInterrupt(enemy, player);

        internal static void ClearBatterySabotageInterrupt(EnemyAI enemy) =>
            ShipSystemsServices.ClearBatterySabotageInterrupt(enemy);
    }
}
