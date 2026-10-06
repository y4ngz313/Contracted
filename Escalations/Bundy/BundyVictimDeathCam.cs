using System;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.Bundy
{
    internal sealed class BundyVictimDeathCam : MonoBehaviour
    {
        private const string LookActionName = "Look";
        private const float DefaultPitch = 12f;
        private const float MinPitch = -25f;
        private const float MaxPitch = 65f;
        private const float Distance = 2.85f;
        private const float SideOffset = 0.55f;
        private const float HeightOffset = 0.45f;
        private const float MaxRotationDegPerSecond = 180f;

        private static readonly BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static InputAction s_lookAction;
        private static bool s_lookActionResolved;

        private PlayerControllerB _player;
        private EnemyAI _bundy;
        private Transform _target;
        private Vector3 _fallbackPivot;
        private bool _active;
        private Camera _savedActiveCamera;
        private Transform _savedSpectateCameraParent;
        private Vector3 _savedSpectateCameraLocalPosition;
        private Quaternion _savedSpectateCameraLocalRotation;
        private bool _savedSpectateCameraEnabled;
        private bool _savedGameplayCameraEnabled;
        private PlayerControllerB _savedSpectatedPlayerScript;
        private bool _savedArmsEnabled;
        private Vector3 _savedLocalVisorPosition;
        private ShadowCastingMode _savedBodyShadowMode;
        private RigWeightState _savedLookRig1;
        private RigWeightState _savedLookRig2;
        private float _cameraYaw;
        private float _cameraPitch = DefaultPitch;

        internal static bool Begin(
            PlayerControllerB player,
            EnemyAI bundy,
            Transform target,
            Vector3 fallbackPivot,
            Quaternion initialRotation)
        {
            if (!IsLocalPlayer(player))
                return false;

            try
            {
                BundyVictimDeathCam camera = player.GetComponent<BundyVictimDeathCam>();
                if (camera == null)
                    camera = player.gameObject.AddComponent<BundyVictimDeathCam>();

                camera.BeginInternal(player, bundy, target, fallbackPivot, initialRotation);
                return true;
            }
            catch (Exception e)
            {
                LogWarning($"Victim death cam begin failed for {PlayerLabel(player)}: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        internal static void UpdateTarget(
            PlayerControllerB player,
            EnemyAI bundy,
            Transform target,
            Vector3 fallbackPivot,
            Quaternion initialRotation)
        {
            try
            {
                BundyVictimDeathCam camera = player != null ? player.GetComponent<BundyVictimDeathCam>() : null;
                if (camera == null || !camera._active)
                    return;

                if (bundy != null && camera._bundy != null && camera._bundy != bundy)
                    return;

                camera._target = target;
                camera._fallbackPivot = fallbackPivot;
                if (Mathf.Abs(camera._cameraYaw) < 0.001f)
                    camera._cameraYaw = initialRotation.eulerAngles.y;
            }
            catch (Exception e)
            {
                LogWarning($"Victim death cam target update failed for {PlayerLabel(player)}: {e.GetType().Name}: {e.Message}");
            }
        }

        internal static void End(PlayerControllerB player, EnemyAI bundy, string reason)
        {
            try
            {
                BundyVictimDeathCam camera = player != null ? player.GetComponent<BundyVictimDeathCam>() : null;
                if (camera == null || !camera._active)
                    return;

                if (bundy != null && camera._bundy != null && camera._bundy != bundy)
                    return;

                camera.RestoreInternal(reason, player != null && player.isPlayerDead);
            }
            catch (Exception e)
            {
                LogWarning($"Victim death cam end failed for {PlayerLabel(player)}: {e.GetType().Name}: {e.Message}");
            }
        }

        private void BeginInternal(
            PlayerControllerB player,
            EnemyAI bundy,
            Transform target,
            Vector3 fallbackPivot,
            Quaternion initialRotation)
        {
            if (_active)
                RestoreInternal("replaced by new Bundy victim death cam", player != null && player.isPlayerDead);

            _player = player;
            _bundy = bundy;
            _target = target;
            _fallbackPivot = fallbackPivot;
            _cameraYaw = initialRotation.eulerAngles.y;
            _cameraPitch = DefaultPitch;

            try
            {
                StartOfRound startOfRound = StartOfRound.Instance;
                if (startOfRound == null || startOfRound.spectateCamera == null)
                    return;

                Camera spectateCamera = startOfRound.spectateCamera;
                _savedActiveCamera = startOfRound.activeCamera;
                _savedSpectateCameraParent = spectateCamera.transform.parent;
                _savedSpectateCameraLocalPosition = spectateCamera.transform.localPosition;
                _savedSpectateCameraLocalRotation = spectateCamera.transform.localRotation;
                _savedSpectateCameraEnabled = spectateCamera.enabled;
                _savedGameplayCameraEnabled = _player.gameplayCamera != null && _player.gameplayCamera.enabled;
                _savedSpectatedPlayerScript = _player.spectatedPlayerScript;
                _savedArmsEnabled = _player.thisPlayerModelArms != null && _player.thisPlayerModelArms.enabled;
                _savedBodyShadowMode = _player.thisPlayerModel != null
                    ? _player.thisPlayerModel.shadowCastingMode
                    : ShadowCastingMode.Off;
                _savedLocalVisorPosition = _player.localVisor != null ? _player.localVisor.position : Vector3.zero;
                _savedLookRig1 = SuppressRigWeight(_player, "cameraLookRig1");
                _savedLookRig2 = SuppressRigWeight(_player, "cameraLookRig2");

                spectateCamera.transform.SetParent(null, worldPositionStays: true);
                _player.spectatedPlayerScript = null;
                if (_player.thisPlayerModel != null)
                    _player.thisPlayerModel.shadowCastingMode = ShadowCastingMode.On;
                if (_player.thisPlayerModelArms != null)
                    _player.thisPlayerModelArms.enabled = false;
                if (_player.localVisor != null && startOfRound.notSpawnedPosition != null)
                    _player.localVisor.position = startOfRound.notSpawnedPosition.position;

                _active = true;
                UpdateCamera();
                startOfRound.SwitchCamera(spectateCamera);
                if (_player.gameplayCamera != null)
                    _player.gameplayCamera.enabled = false;
                _player.ChangeAudioListenerToObject(spectateCamera.gameObject);

                LogInfo($"Local victim death cam started for {PlayerLabel(_player)}.");
            }
            catch
            {
                RestoreInternal("death cam begin failed", _player != null && _player.isPlayerDead);
                throw;
            }
        }

        private void LateUpdate()
        {
            if (!_active)
                return;

            if (_player == null || _bundy == null || _bundy.isEnemyDead || _player.isPlayerDead)
            {
                RestoreInternal("victim death cam actor invalid", _player != null && _player.isPlayerDead);
                return;
            }

            UpdateCamera();
        }

        private void OnDestroy()
        {
            if (_active)
                RestoreInternal("victim death cam destroyed", _player != null && _player.isPlayerDead);
        }

        private void UpdateCamera()
        {
            if (!_active || _player == null || StartOfRound.Instance == null || StartOfRound.Instance.spectateCamera == null)
                return;

            if (_player.localVisor != null && StartOfRound.Instance.notSpawnedPosition != null)
                _player.localVisor.position = StartOfRound.Instance.notSpawnedPosition.position;

            UpdateOrbitCameraInput();

            Vector3 victimPoint = ResolveVictimPoint();
            Vector3 bundyPoint = _bundy != null
                ? _bundy.transform.position + Vector3.up * 1.15f
                : victimPoint + Quaternion.Euler(0f, _cameraYaw, 0f) * Vector3.forward;
            Vector3 midpoint = (victimPoint + bundyPoint) * 0.5f;
            midpoint.y = Mathf.Max(midpoint.y, victimPoint.y + 0.25f);

            Quaternion orbit = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
            Quaternion yawOnly = Quaternion.Euler(0f, _cameraYaw, 0f);
            Vector3 desired = midpoint +
                orbit * Vector3.back * Distance +
                yawOnly * Vector3.right * SideOffset +
                Vector3.up * HeightOffset;
            Vector3 direction = desired - midpoint;
            float distance = direction.magnitude;
            if (distance > 0.01f)
            {
                Vector3 normalized = direction / distance;
                int mask = StartOfRound.Instance.collidersAndRoomMaskAndDefault;
                if (Physics.Raycast(midpoint, normalized, out RaycastHit hit, distance, mask, QueryTriggerInteraction.Ignore))
                    desired = hit.point - normalized * 0.18f;
            }

            Camera spectateCamera = StartOfRound.Instance.spectateCamera;
            Vector3 lookDirection = midpoint - desired;
            if (lookDirection.sqrMagnitude < 0.001f)
                lookDirection = Vector3.forward;
            spectateCamera.transform.SetPositionAndRotation(
                desired,
                Quaternion.LookRotation(lookDirection.normalized, Vector3.up));
        }

        private Vector3 ResolveVictimPoint()
        {
            if (_target != null)
                return _target.position + Vector3.up * 0.55f;

            if (_player == null)
                return _fallbackPivot + Vector3.up * 0.75f;

            if (_player.lowerSpine != null)
                return _player.lowerSpine.position + Vector3.up * 0.55f;

            if (_player.playerBodyAnimator != null)
                return _player.playerBodyAnimator.transform.position + Vector3.up * 0.75f;

            return _player.transform.position + Vector3.up * 0.75f;
        }

        private void UpdateOrbitCameraInput()
        {
            Vector2 raw = ReadLookInput();
            if (raw.sqrMagnitude < 1e-8f)
                return;

            float sensitivity = 0.008f;
            if (IngamePlayerSettings.Instance != null)
            {
                sensitivity = IngamePlayerSettings.Instance.settings.lookSensitivity * 0.008f;
                if (IngamePlayerSettings.Instance.flipCamera)
                    raw.x = -raw.x;
                if (IngamePlayerSettings.Instance.settings.invertYAxis)
                    raw.y *= -1f;
            }

            Vector2 delta = raw * sensitivity;
            float maxDelta = MaxRotationDegPerSecond * Mathf.Max(Time.unscaledDeltaTime, 1f / 120f);
            if (delta.magnitude > maxDelta)
                delta = delta.normalized * maxDelta;

            _cameraYaw = NormalizeDegrees(_cameraYaw + delta.x);
            _cameraPitch = Mathf.Clamp(_cameraPitch - delta.y, MinPitch, MaxPitch);
        }

        private void RestoreInternal(string reason, bool playerDead)
        {
            if (!_active)
                return;

            PlayerControllerB player = _player;
            try
            {
                RestoreRigWeight(_savedLookRig1);
                RestoreRigWeight(_savedLookRig2);

                if (player != null)
                {
                    if (player.thisPlayerModel != null)
                        player.thisPlayerModel.shadowCastingMode = _savedBodyShadowMode;
                    if (player.thisPlayerModelArms != null)
                        player.thisPlayerModelArms.enabled = _savedArmsEnabled;
                    if (player.localVisor != null)
                        player.localVisor.position = _savedLocalVisorPosition;
                    player.spectatedPlayerScript = _savedSpectatedPlayerScript;
                }

                if (StartOfRound.Instance == null || StartOfRound.Instance.spectateCamera == null)
                    return;

                Camera spectateCamera = StartOfRound.Instance.spectateCamera;
                if (playerDead)
                {
                    spectateCamera.transform.SetParent(null, worldPositionStays: true);
                    spectateCamera.enabled = true;
                    StartOfRound.Instance.SwitchCamera(spectateCamera);
                    if (player != null)
                        player.ChangeAudioListenerToObject(spectateCamera.gameObject);
                    LogInfo($"Local victim death cam yielded to spectate for dead player {PlayerLabel(player)}; reason={reason}.");
                    return;
                }

                if (_savedSpectateCameraParent != null)
                    spectateCamera.transform.SetParent(_savedSpectateCameraParent, worldPositionStays: false);
                spectateCamera.transform.localPosition = _savedSpectateCameraLocalPosition;
                spectateCamera.transform.localRotation = _savedSpectateCameraLocalRotation;
                spectateCamera.enabled = _savedSpectateCameraEnabled;

                if (player != null && player.gameplayCamera != null)
                {
                    StartOfRound.Instance.SwitchCamera(player.gameplayCamera);
                    player.gameplayCamera.enabled =
                        _savedGameplayCameraEnabled || _savedActiveCamera == player.gameplayCamera;
                    player.ChangeAudioListenerToObject(player.gameplayCamera.gameObject);
                }

                LogInfo($"Local victim death cam restored for {PlayerLabel(player)}; reason={reason}.");
            }
            catch (Exception e)
            {
                LogWarning($"Victim death cam restore failed for {PlayerLabel(player)}: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                _active = false;
                _player = null;
                _bundy = null;
                _target = null;
                _savedLookRig1 = default;
                _savedLookRig2 = default;
            }
        }

        private static Vector2 ReadLookInput()
        {
            if (s_lookAction == null && !s_lookActionResolved)
                ResolveLookAction();

            if (s_lookAction != null)
            {
                try
                {
                    Vector2 actionValue = s_lookAction.ReadValue<Vector2>();
                    if (actionValue.sqrMagnitude >= 1e-8f || Mouse.current == null)
                        return actionValue;
                }
                catch
                {
                    s_lookAction = null;
                    s_lookActionResolved = true;
                }
            }

            return Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
        }

        private static void ResolveLookAction()
        {
            s_lookActionResolved = true;
            try
            {
                s_lookAction = InputSystem.actions != null
                    ? InputSystem.actions.FindAction(LookActionName)
                    : null;
            }
            catch
            {
                s_lookAction = null;
            }
        }

        private static float NormalizeDegrees(float angle)
        {
            while (angle > 180f)
                angle -= 360f;
            while (angle < -180f)
                angle += 360f;
            return angle;
        }

        private static RigWeightState SuppressRigWeight(PlayerControllerB player, string fieldName)
        {
            if (player == null || string.IsNullOrEmpty(fieldName))
                return default;

            try
            {
                FieldInfo field = player.GetType().GetField(fieldName, InstanceFlags);
                object rig = field != null ? field.GetValue(player) : null;
                if (rig == null)
                    return default;

                Type type = rig.GetType();
                PropertyInfo property = type.GetProperty("weight", InstanceFlags);
                FieldInfo weightField = type.GetField("weight", InstanceFlags);
                if (!TryGetWeight(rig, property, weightField, out float weight))
                    return default;

                TrySetWeight(rig, property, weightField, 0f);
                return new RigWeightState(rig, property, weightField, weight);
            }
            catch (Exception e)
            {
                LogWarning($"Victim death cam look-rig suppression failed for field '{fieldName}': {e.GetType().Name}: {e.Message}");
                return default;
            }
        }

        private static void RestoreRigWeight(RigWeightState state)
        {
            if (!state.HasValue)
                return;

            try
            {
                TrySetWeight(state.Target, state.Property, state.Field, state.Weight);
            }
            catch (Exception e)
            {
                LogWarning($"Victim death cam look-rig restore failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static bool TryGetWeight(object target, PropertyInfo property, FieldInfo field, out float weight)
        {
            weight = 0f;
            object value = null;
            if (property != null && property.CanRead)
                value = property.GetValue(target, null);
            else if (field != null)
                value = field.GetValue(target);

            if (value is float floatValue)
            {
                weight = floatValue;
                return true;
            }

            return false;
        }

        private static bool TrySetWeight(object target, PropertyInfo property, FieldInfo field, float weight)
        {
            if (target == null)
                return false;

            if (property != null && property.CanWrite)
            {
                property.SetValue(target, weight, null);
                return true;
            }

            if (field != null)
            {
                field.SetValue(target, weight);
                return true;
            }

            return false;
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            return player != null
                && GameNetworkManager.Instance != null
                && GameNetworkManager.Instance.localPlayerController == player;
        }

        private static string PlayerLabel(PlayerControllerB player)
        {
            return player == null
                ? "<null>"
                : $"{player.playerUsername} (clientId={player.actualClientId})";
        }

        private static void LogInfo(string message)
        {
            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogInfo("[Bundy] " + message);
        }

        private static void LogWarning(string message)
        {
            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogWarning("[Bundy] " + message);
        }

        private readonly struct RigWeightState
        {
            internal readonly object Target;
            internal readonly PropertyInfo Property;
            internal readonly FieldInfo Field;
            internal readonly float Weight;

            internal RigWeightState(object target, PropertyInfo property, FieldInfo field, float weight)
            {
                Target = target;
                Property = property;
                Field = field;
                Weight = weight;
            }

            internal bool HasValue => Target != null && (Property != null || Field != null);
        }
    }
}
