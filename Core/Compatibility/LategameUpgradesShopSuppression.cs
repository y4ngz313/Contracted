using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Core.Compatibility
{
    /// <summary>
    /// #802. Refuses Lategame Upgrades' own contract purchases while Contracted is the one
    /// handing out contracts.
    ///
    /// The three entry points are LGU's: the interactive shop's random-moon and
    /// specified-moon purchases, and the plain <c>contract</c> terminal command. They are
    /// patched by hand rather than with <c>[HarmonyPatch(typeof(...))]</c>, because naming
    /// the type in an attribute would be a compile reference to MoreShipUpgrades. Nothing is
    /// patched when LGU is absent.
    /// </summary>
    internal static class LategameUpgradesShopSuppression
    {
        private const string LogPrefix = "[MoonContracts.LGU]";

        /// <summary>Shown in LGU's own terminal chrome where the purchase result would be.</summary>
        private const string SuppressedMessage = "Contract assignment is handled by The Company.";

        private static bool _applied;
        private static MethodInfo _errorMessageMethod;
        private static bool _errorMessageResolved;

        /// <summary>
        /// True when this purchase must be refused.
        ///
        /// Two independent reasons. A non-server peer is refused unconditionally: LGU's
        /// purchase paths call <c>ReqSyncContractDetailsServerRpc</c>, which the host executes
        /// without validating anything, so one client with the switch off could overwrite the
        /// host's assignment for everybody. Contracted does not CSync its host-only booleans,
        /// and a per-machine switch cannot be what stands between a client and the host's
        /// state. On the server the switch decides, which is what makes the description's
        /// "Host only." true: the host's value is the only one that lands. The #1219 H2
        /// <c>contracts</c> takeover reads the same predicate, so the word shows our list
        /// exactly where LGU's purchases are refused.
        /// </summary>
        internal static bool Suppressing
        {
            get
            {
                if (LategameUpgradesCompat.IsInSession && !LategameUpgradesCompat.IsServerPeer)
                    return true;

                return ContractsBootstrap.SuppressLguContractShop == null
                    || ContractsBootstrap.SuppressLguContractShop.Value;
            }
        }

        internal static void Apply(Harmony harmony)
        {
            if (_applied || harmony == null || !LategameUpgradesCompat.IsLoaded)
                return;

            _applied = true;
            Patch(harmony, LategameUpgradesCompat.PurchaseRandomMoonContractMethod, nameof(PurchaseRandomMoonContract_Prefix));
            Patch(harmony, LategameUpgradesCompat.PurchaseSpecifiedMoonContractMethod, nameof(PurchaseSpecifiedMoonContract_Prefix));
            Patch(harmony, LategameUpgradesCompat.TryGetContractMethod, nameof(TryGetContract_Prefix));
        }

        private static void Patch(Harmony harmony, MethodInfo target, string prefixName)
        {
            if (target == null)
                return;

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(LategameUpgradesShopSuppression), prefixName)));
                ContractsBootstrap.Log?.LogInfo($"{LogPrefix} Suppression prefix attached to {target.DeclaringType?.Name}.{target.Name}.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Could not patch {target.DeclaringType?.Name}.{target.Name}: {e.Message}");
            }
        }

        private static bool PurchaseRandomMoonContract_Prefix(object __instance, Action backAction)
        {
            return !Refuse(__instance, backAction, "random moon purchase");
        }

        private static bool PurchaseSpecifiedMoonContract_Prefix(object __instance, Action backAction)
        {
            return !Refuse(__instance, backAction, "specified moon purchase");
        }

        /// <summary>LGU's <c>contract</c> terminal command. Its return value is the node the
        /// terminal prints, so the refusal is written into a node of the same shape LGU's own
        /// <c>DisplayTerminalMessage</c> builds.</summary>
        private static bool TryGetContract_Prefix(ref TerminalNode __result)
        {
            if (!Suppressing)
                return true;

            TerminalNode node = ScriptableObject.CreateInstance<TerminalNode>();
            node.displayText = SuppressedMessage + "\n\n";
            node.clearPreviousText = true;
            __result = node;
            ContractsBootstrap.Log?.LogInfo($"{LogPrefix} Refused an LGU contract purchase (terminal command).");
            return false;
        }

        /// <summary>True when the purchase was refused. The message goes through the LGU
        /// application's own error screen when that is reachable, so the refusal reads like
        /// every other message in that menu and <paramref name="backAction"/> still returns
        /// the player to the main screen; a log line is the fallback when it is not.</summary>
        private static bool Refuse(object application, Action backAction, string what)
        {
            if (!Suppressing)
                return false;

            ContractsBootstrap.Log?.LogInfo($"{LogPrefix} Refused an LGU contract purchase ({what}).");
            ShowErrorMessage(application, backAction);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ShowErrorMessage(object application, Action backAction)
        {
            if (application == null)
                return;

            try
            {
                MethodInfo errorMessage = ResolveErrorMessage(application.GetType());
                if (errorMessage == null)
                {
                    ContractsBootstrap.Log?.LogDebug($"{LogPrefix} No reachable error-message screen; the refusal is log-only.");
                    return;
                }

                errorMessage.Invoke(application, new object[] { "Contracts", backAction, SuppressedMessage });
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Could not display the refusal on the terminal: {e.Message}");
            }
        }

        /// <summary>Finds InteractiveTerminalAPI's <c>ErrorMessage(string, Action, string)</c>
        /// on the application's base chain. Resolved by shape, not by naming the API type.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static MethodInfo ResolveErrorMessage(Type applicationType)
        {
            if (_errorMessageResolved)
                return _errorMessageMethod;

            _errorMessageResolved = true;
            for (Type type = applicationType; type != null && type != typeof(object); type = type.BaseType)
            {
                MethodInfo candidate = type.GetMethod(
                    "ErrorMessage",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    binder: null,
                    types: new[] { typeof(string), typeof(Action), typeof(string) },
                    modifiers: null);
                if (candidate != null)
                {
                    _errorMessageMethod = candidate;
                    break;
                }
            }

            return _errorMessageMethod;
        }
    }
}
