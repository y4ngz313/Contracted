using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Y4NGZCore.Modules.ShipSystems;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Escalations._Shared
{
    internal static class ShipPowerEscalationObjectiveState
    {
        // #611 task 1.3: the ship-power reads below used to come through
        // Type.GetType("…ShipSystemsApi, Y4NGZCompany") and four cached MethodInfo handles. That
        // string names the monolith; post-split it resolves to null, the lookup latches its
        // failure, and this escalation reports "no power emergency" for the rest of the session
        // with nothing in the log to say why. They are Y4NGZCore capability reads now, with the
        // absent case defined by the contract (no emergency) rather than by a failed lookup.
        private static bool _active;
        private static bool _restoreInProgress;
        private static bool _apparatusRecovered;
        private static bool _restored;

        internal static bool IsActive => _active;
        internal static string ActiveTitle => "POWER FAILURE";

        internal static void ResetRunState()
        {
            _active = false;
            _restoreInProgress = false;
            _apparatusRecovered = false;
            _restored = false;
        }

        internal static void UpdateRuntime()
        {
            // No early-out on provider presence any more: absent reads false, which drives the
            // same "emergency resolved" path a real recovery does, so a mid-session teardown of
            // the ship-power system closes an open escalation instead of stranding it.
            bool restoreObjectiveActive = ShipSystemsServices.IsRestoreObjectiveActive;
            bool batteryBroken = ShipSystemsServices.IsBatteryBroken;
            bool emergencyActive = restoreObjectiveActive || batteryBroken;

            if (emergencyActive)
            {
                if (!_active || !_restoreInProgress)
                {
                    _active = true;
                    _apparatusRecovered = false;
                    _restored = false;
                    ContractsBootstrap.Log?.LogInfo("[MoonContracts.ShipPower] Ship power emergency escalation activated.");
                }

                _restoreInProgress = true;
                _apparatusRecovered = _apparatusRecovered || IsAnyPlayerHoldingRestoreItem();
                _restored = false;
                return;
            }

            if (_active && _restoreInProgress)
            {
                _restoreInProgress = false;
                _restored = true;
                _active = false;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.ShipPower] Ship power emergency escalation resolved.");
            }
        }

        internal static List<MoonContractObjective> GetActiveObjectives()
        {
            if (!_active)
                return new List<MoonContractObjective>();

            var objectives = new List<MoonContractObjective>
            {
                // Explicit key: the recovery text comes from the Upgrades API and can vary
                // without a state change, so the identity is pinned here (#520).
                new MoonContractObjective(
                    GetRestoreRecoveryObjectiveText(),
                    _apparatusRecovered || _restored ? 1 : 0,
                    1,
                    key: "shippower.recover")
            };

            if (_apparatusRecovered || _restored)
            {
                objectives.Add(new MoonContractObjective(
                    "Install the battery at the exterior socket",
                    _restored ? 1 : 0,
                    1,
                    key: "shippower.install"));
            }

            return objectives;
        }

        /// <summary>
        /// The owner's wording for the recovery step, or this escalation's own when the owner
        /// has none — unchanged behaviour, one read instead of a reflected call.
        /// </summary>
        private static string GetRestoreRecoveryObjectiveText()
        {
            string text = ShipSystemsServices.RestoreRecoveryObjectiveText;
            return string.IsNullOrWhiteSpace(text) ? "Recover the apparatus" : text;
        }

        /// <summary>
        /// Whether anyone is carrying the part that restores power. The local fallback below is
        /// kept deliberately: it is what answers when no ship-power provider is registered, and
        /// the apparatus is a vanilla object this side can see for itself.
        /// </summary>
        private static bool IsAnyPlayerHoldingRestoreItem()
        {
            if (ShipSystemsServices.HasPowerStatus)
                return ShipSystemsServices.IsAnyPlayerHoldingRestoreItem;

            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return false;

            return players.Any(player =>
                player != null &&
                player.isPlayerControlled &&
                !player.isPlayerDead &&
                player.currentlyHeldObjectServer is LungProp);
        }
    }
}
