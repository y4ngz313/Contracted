using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Escalations.Bundy
{
    // Host-only live-spawn helper: Ctrl+Alt+F7 spawns a live Chainsaw Bundy in front of the local
    // player. Attached by BundyBootstrap only when DebugLogs is enabled.
    //
    // #374: a Ctrl+Alt+F3 decal-gate hotkey lived here briefly and did not work in practice. The
    // gate is driven from the Bundy tab in Y4NGZDebugTools instead, through BundyDebugApi — see
    // BundyDebugApi.ForceSkinDecalProbe. Do not re-add it as a key.
    //
    // #277: the F8/F9 side-by-side "comparison proof" half (raw authored pack rig vs live prefab
    // visual, ~550 lines) was permanently disabled at bootstrap since its rig question was answered;
    // it has been deleted.
    internal sealed class BundyChainsawPackProofDebug : MonoBehaviour
    {
        private const float SpawnDistance = 4.5f;

        internal static bool EnableLiveSpawnHotkeys { get; set; } = true;

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (EnableLiveSpawnHotkeys && LiveSpawnModifierPressed(keyboard) && keyboard.f7Key.wasPressedThisFrame)
                SpawnLiveChainsawEnemy();
        }

        private static bool LiveSpawnModifierPressed(Keyboard keyboard)
        {
            return keyboard != null
                && (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed)
                && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed);
        }

        private static void SpawnLiveChainsawEnemy()
        {
            RoundManager round = RoundManager.Instance;
            if (round == null || !round.IsServer)
            {
                LogWarning("BundyChainsawPackProof live spawn skipped; this key only works on the host/server.");
                return;
            }

            if (BundyPlugin.ChainsawEnemyType == null)
            {
                LogWarning("BundyChainsawPackProof live spawn skipped; BundyPlugin.ChainsawEnemyType is not registered.");
                return;
            }

            if (!TryResolveSpawnPose(out Vector3 position, out Quaternion rotation))
            {
                LogWarning("BundyChainsawPackProof live spawn skipped; local player/camera is not available yet.");
                return;
            }

            var spawned = round.SpawnEnemyGameObject(position, rotation.eulerAngles.y, -1, BundyPlugin.ChainsawEnemyType);
            string spawnedAi = "<unresolved>";
            if (spawned.TryGet(out NetworkObject networkObject) && networkObject != null)
            {
                BundyAIChainsaw ai = networkObject.GetComponent<BundyAIChainsaw>() ??
                    networkObject.GetComponentInChildren<BundyAIChainsaw>(true);
                spawnedAi = ai != null ? GetRelativePath(networkObject.transform, ai.transform) : "<missing BundyAIChainsaw>";
            }

            LogInfo(
                "BundyChainsawPackProof spawned live Chainsaw Bundy enemy: " +
                $"position={FormatVector(position)}, yaw={rotation.eulerAngles.y:F1}, ai={spawnedAi}. " +
                "Approach/chase should drive MotionState=1 -> Chainsaw_WalkForward, then MotionState=2 -> Chainsaw_RunForward only in escalated pursuit.");
        }

        private static bool TryResolveSpawnPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            PlayerControllerB player = null;
            if (GameNetworkManager.Instance != null)
                player = GameNetworkManager.Instance.localPlayerController;
            if (player == null && StartOfRound.Instance != null)
                player = StartOfRound.Instance.localPlayerController;
            if (player == null)
                return false;

            Transform basis = player.gameplayCamera != null ? player.gameplayCamera.transform : player.transform;
            Vector3 forward = basis.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = player.transform.forward;
            forward.Normalize();

            position = basis.position + forward * SpawnDistance;
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : Physics.DefaultRaycastLayers;
            if (Physics.Raycast(position + Vector3.up * 6f, Vector3.down, out RaycastHit hit, 20f, mask, QueryTriggerInteraction.Ignore))
                position = hit.point;

            rotation = Quaternion.LookRotation(forward, Vector3.up);
            return true;
        }

        private static string GetRelativePath(Transform root, Transform transform)
        {
            if (root == null || transform == null)
                return "<null>";
            if (transform == root)
                return root.name;

            string path = transform.name;
            Transform cursor = transform.parent;
            while (cursor != null && cursor != root)
            {
                path = cursor.name + "/" + path;
                cursor = cursor.parent;
            }

            return cursor == root ? root.name + "/" + path : transform.name;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3}, {value.y:F3}, {value.z:F3})";
        }

        private static void LogInfo(string message)
        {
            BundyPlugin.Log?.LogInfo("[Bundy] " + message);
        }

        private static void LogWarning(string message)
        {
            BundyPlugin.Log?.LogWarning("[Bundy] " + message);
        }
    }
}
