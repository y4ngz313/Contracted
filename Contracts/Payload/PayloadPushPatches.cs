using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Contracts.Payload
{
    internal static class PayloadPushPatches
    {
        internal const float SteeringMoveDegreesPerSecond = 55f;
        internal const float SteeringPivotDegreesPerSecond = 30f;
        private const float SteeringInputDeadzone = 0.08f;
        private const float SteeringMovingInputThreshold = 0.05f;

        private static Harmony _harmony;
        private static bool _applied;
        private static PlayerControllerB _steerPlayer;
        private static bool _lookLogged;
        private static bool _moveClampLogged;

        internal static float LastPayloadSteerInput { get; private set; }

        /// <summary>
        /// Applies the payload control patches. Called once from <see cref="ContractsBootstrap"/> at
        /// plugin init so the PlayerControllerB.Update transpiler is never paid on the Payload
        /// activation frame (#299); the cart-side calls remain as idempotent safety nets.
        /// Each patch body early-outs unless the local player is pushing a cart (the damage prefix
        /// only records health), so applying them unconditionally is behaviour-neutral.
        /// </summary>
        internal static void EnsureApplied()
        {
            if (_applied)
                return;

            _applied = true;
            long patchStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _harmony = new Harmony(ContractsBootstrap.PLUGIN_GUID + ".payloadpush");
            TryPatch(typeof(PayloadMoveInputPatch), "PlayerControllerB.Update moveInputVector cart-owned movement transpiler");
            TryPatch(typeof(PayloadLookInputPatch), "PlayerControllerB.PlayerLookInput payload look suppression prefix");
            TryPatch(typeof(PayloadSecondaryUsePatch), "PlayerControllerB.ItemSecondaryUse_performed payload view prefix");
            TryPatch(typeof(PayloadDamageDropPatch), "PlayerControllerB.DamagePlayer payload damage drop postfix");
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Timing] Payload push patches applied in {ToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - patchStart):0.00}ms.");
        }

        private static double ToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        internal static void ResetRuntimeState()
        {
            _steerPlayer = null;
            LastPayloadSteerInput = 0f;
            _lookLogged = false;
            _moveClampLogged = false;
        }

        private static void TryPatch(Type patchType, string label)
        {
            try
            {
                _harmony.PatchAll(patchType);
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Patches] Applied {label}.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload.Patches] Skipped {label}: {e.GetType().Name}: {e.Message}");
            }
        }

        private static IEnumerable<CodeInstruction> InjectMoveClamp(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            FieldInfo moveInputField = AccessTools.Field(typeof(PlayerControllerB), nameof(PlayerControllerB.moveInputVector));
            MethodInfo clampMethod = AccessTools.Method(typeof(PayloadPushPatches), nameof(ClampMoveInputForPayload));
            int injected = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                yield return codes[i];
                if (codes[i].opcode == OpCodes.Stfld && Equals(codes[i].operand, moveInputField))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, clampMethod);
                    injected++;
                }
            }

            if (injected > 0)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Patches] Injected payload cart-owned movement clamp after {injected} moveInputVector write(s).");
            else
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Payload.Patches] PlayerControllerB.Update cart-owned movement transpiler found no moveInputVector writes; cart movement suppression inactive.");
        }

        private static void ClampMoveInputForPayload(PlayerControllerB player)
        {
            if (!PayloadCartController.IsLocalPlayerPushingPayload(player))
                return;

            Vector2 input = player.moveInputVector;
            LastPayloadSteerInput = Mathf.Abs(input.x) > 0.001f
                ? Mathf.Clamp(input.x, -1f, 1f)
                : 0f;
            _steerPlayer = player;
            if (Mathf.Abs(input.x) > 0.001f && !_moveClampLogged)
            {
                _moveClampLogged = true;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Controls] Vanilla movement suppressed for cart motor; rawMove={input}.");
            }

            player.moveInputVector = Vector2.zero;
        }

        private static bool HandlePayloadLook(PlayerControllerB player)
        {
            if (!PayloadCartController.IsLocalPlayerPushingPayload(player))
            {
                if (_steerPlayer == player)
                {
                    _steerPlayer = null;
                    LastPayloadSteerInput = 0f;
                }
                return true;
            }

            if (player == null)
                return true;

            if (ShouldLetVanillaHandleLook(player))
                return true;

            // First person keeps vanilla mouse look and pitch; ApplyPayloadSteering then clamps the
            // body yaw to PayloadPushLook.FreeLookMaxYaw either side of facing the cart.
            if (!PayloadCartController.LocalAttachedCart.IsThirdPersonView)
                return true;

            if ((player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
                || player.inSpecialMenu
                || (StartOfRound.Instance != null && StartOfRound.Instance.newGameIsLoading)
                || player.disableLookInput)
            {
                return false;
            }

            Vector2 look = player.playerActions.Movement.Look.ReadValue<Vector2>() * 0.008f;
            if (IngamePlayerSettings.Instance != null)
            {
                look *= IngamePlayerSettings.Instance.settings.lookSensitivity;
                if (IngamePlayerSettings.Instance.flipCamera)
                    look.x = -look.x;
                if (IngamePlayerSettings.Instance.settings.invertYAxis)
                    look.y *= -1f;
            }

            if (StartOfRound.Instance != null)
                StartOfRound.Instance.playerLookMagnitudeThisFrame = look.magnitude * Time.deltaTime;

            if (!_lookLogged)
            {
                _lookLogged = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.Payload.Controls] Payload look override engaged: mouse drives camera orbit; A/D owns body steering.");
            }

            return false;
        }

        private static bool ShouldLetVanillaHandleLook(PlayerControllerB player)
        {
            if (((!player.IsOwner || !player.isPlayerControlled || (player.IsServer && !player.isHostPlayerObject)) && !player.isTestingPlayer))
                return true;

            if (player.isFreeCamera || player.IsInspectingItem || player.isPlayerDead)
                return true;

            if (player.jetpackControls || player.disablingJetpackControls || player.inShockingMinigame)
                return true;

            if (player.inSpecialInteractAnimation && (player.isClimbingLadder || player.clampLooking || player.inVehicleAnimation))
                return true;

            return false;
        }

        internal static void ApplyPayloadSteering(PlayerControllerB player)
        {
            if (!PayloadCartController.IsLocalPlayerPushingPayload(player) || player == null)
                return;

            if (ShouldLetVanillaHandleLook(player))
                return;

            if ((player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
                || player.inSpecialMenu
                || (StartOfRound.Instance != null && StartOfRound.Instance.newGameIsLoading)
                || player.disableLookInput)
            {
                LastPayloadSteerInput = 0f;
                return;
            }

            // The motor's A/D steering is view-independent. One look rule (PayloadPushLook) holds the
            // body yaw for both views: first person turns at most FreeLookMaxYaw either side of
            // facing the cart, the orbit view stays square to the handle, and neither corrects while
            // the handle is out of follow reach (stale cart yaw). Pitch stays vanilla.
            PayloadCartController cart = PayloadCartController.LocalAttachedCart;
            if (cart != null)
            {
                Transform yawTransform = player.thisPlayerBody != null ? player.thisPlayerBody : player.transform;
                Vector3 euler = yawTransform.eulerAngles;
                float yaw = PayloadPushLook.BodyYaw(euler.y, cart.transform.eulerAngles.y, cart.IsThirdPersonView,
                    cart.TryGetDriverHandleOffset(player, out _));
                if (yaw != euler.y)
                    yawTransform.eulerAngles = new Vector3(euler.x, yaw, euler.z);
            }

            SyncPayloadLook(player);
        }

        internal static void SyncPayloadLook(PlayerControllerB player)
        {
            if (player.isTestingPlayer || (player.IsServer && player.playersManager != null && player.playersManager.connectedPlayersAmount < 1))
                return;

            if (player.updatePlayerLookInterval <= 0.05f)
                return;

            player.updatePlayerLookInterval = 0f;
            Transform yawTransform = player.thisPlayerBody != null ? player.thisPlayerBody : player.transform;
            if (player.playersManager != null
                && !player.playersManager.newGameIsLoading
                && Mathf.Abs(player.oldCameraUp + player.previousYRot - (player.cameraUp + yawTransform.eulerAngles.y)) > 1.5f)
            {
                player.UpdatePlayerRotationServerRpc((short)player.cameraUp, (short)yawTransform.localEulerAngles.y);
                player.oldCameraUp = player.cameraUp;
                player.previousYRot = yawTransform.localEulerAngles.y;
            }
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        private static class PayloadMoveInputPatch
        {
            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return InjectMoveClamp(instructions);
            }
        }

        [HarmonyPatch(typeof(PlayerControllerB), "PlayerLookInput")]
        private static class PayloadLookInputPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(PlayerControllerB __instance)
            {
                return HandlePayloadLook(__instance);
            }
        }

        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.DamagePlayer))]
        private static class PayloadDamageDropPatch
        {
            [HarmonyPrefix]
            private static void Prefix(PlayerControllerB __instance, out int __state)
            {
                __state = __instance != null ? __instance.health : 0;
            }

            // Vanilla DamagePlayer only lowers health on the owner, so this fires on the pusher's own
            // machine; the server's sender check decides the detach. A lethal hit is left to the
            // server's dead-pusher detach (the pusher is no longer pushing once dead).
            [HarmonyPostfix]
            private static void Postfix(PlayerControllerB __instance, int __state)
            {
                if (__instance == null || __instance.health >= __state
                    || !PayloadCartController.IsLocalPlayerPushingPayload(__instance))
                    return;

                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Controls] Local pusher took damage (health {__state}->{__instance.health}); requesting cart detach.");
                PayloadCartController.LocalAttachedCart.RequestDetach();
            }
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ItemSecondaryUse_performed")]
        private static class PayloadSecondaryUsePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(PlayerControllerB __instance, InputAction.CallbackContext context)
            {
                if (!PayloadCartController.IsLocalPlayerPushingPayload(__instance))
                    return true;

                // Intercept the action callback, not a raw key or a second subscriber. The held
                // item's secondary use never sees a performed event while the handle owns it.
                if (context.performed
                    && !__instance.isTypingChat && !__instance.inTerminalMenu
                    && !__instance.inSpecialMenu && !__instance.inSpecialInteractAnimation
                    && !__instance.disableLookInput
                    && (__instance.quickMenuManager == null || !__instance.quickMenuManager.isMenuOpen))
                    PayloadCartController.LocalAttachedCart.ToggleLocalView();
                return false;
            }
        }
    }
}
