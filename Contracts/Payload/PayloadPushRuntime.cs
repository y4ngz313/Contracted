using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.Defuse;
namespace Y4NGZCompany.Contracts.Payload
{
    /// <summary>
    /// First-person arm pose for one push (#1305 E5), the one Payload owner of the first-person arm
    /// skeleton: the body's own IK (<see cref="PayloadPushBodyRig.ResolveViewArms"/>) puts these hands
    /// on the grips. Captured local rotations are restored at push end.
    /// </summary>
    internal sealed class PayloadPushViewArms
    {
        private struct BonePair
        {
            public Transform View;
            public Quaternion SavedLocalRotation;
        }

        private readonly List<BonePair> _pairs = new List<BonePair>();
        private PayloadPushBodyRig _rig;
        private bool _taken;

        internal void Take(PlayerControllerB player)
        {
            if (_taken || player == null)
                return;

            SkinnedMeshRenderer viewArms = player.thisPlayerModelArms;
            if (viewArms == null)
            {
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.Payload] First-person push arms skipped: player arms model missing.");
                return;
            }

            foreach (Transform view in viewArms.bones)
                if (view != null && IsInArmChain(view))
                    _pairs.Add(new BonePair { View = view, SavedLocalRotation = view.localRotation });

            _rig = PayloadPushBodyRig.ResolveViewArms(player);
            _taken = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload] First-person push arms taken: {_pairs.Count} bone(s), usableArms={_rig.HasUsableArms}.");
        }

        /// <summary>
        /// Solves the first-person arms onto the grips with the body rig's IK, after the body has posed:
        /// arm bones get rotations only (bone position and scale writes stretched the sleeves in pass 7,
        /// #1293); the reach closure moves only the arms root. Third person hides these renderers.
        /// </summary>
        internal void LateApply(PlayerControllerB player, bool thirdPerson, Transform leftGrip, Transform rightGrip)
        {
            if (!thirdPerson && _rig != null)
                _rig.ApplyLate(player, leftGrip, rightGrip, false);
        }

        internal void Release()
        {
            if (!_taken)
                return;

            // Restored on death too: bone rotations are not visibility, and the animator rewrites them anyway.
            int restored = _pairs.Count;
            foreach (BonePair pair in _pairs)
                if (pair.View != null)
                    pair.View.localRotation = pair.SavedLocalRotation;
            _pairs.Clear();
            _rig.ReleaseReachShift();
            _rig = null;
            _taken = false;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload] First-person push arms released: restored {restored} bone(s).");
        }

        private static bool IsInArmChain(Transform bone)
        {
            for (Transform t = bone; t != null; t = t.parent)
                if (t.name == "shoulder.L" || t.name == "shoulder.R") return true;
            return false;
        }
    }

    /// <summary>
    /// Self-contained port of the Diagnostics force-third-person toggle (DebugGUI.SetThirdPersonCam /
    /// ApplyThirdPersonCamLate — see PAYLOAD_CONTRACT_HANDOFF.md §4c/§8). Pushes the local player's
    /// gameplay camera back off the camera-container pivot each LateUpdate, forces the body renderers
    /// visible (shadows on) and hides the first-person arms. Camera + renderers only — WASD/input untouched.
    /// One instance per cart; only ever drives the LOCAL player while that player is pushing.
    /// </summary>
    internal sealed class PayloadThirdPersonView
    {
        // Handoff §4c: push the camera back -forward*2.5 + up*0.4 from the container pivot.
        internal const float CameraDistanceMeters = 2.5f;
        private const float CameraHeight = 0.4f;
        private const float FreeLookMaxPitch = 55f;
        private const float CameraOcclusionSphereRadius = 0.22f;
        private const float CameraOcclusionSkin = 0.05f;
        private const float CameraOcclusionReturnSpeed = 3f;
        private const float MinCameraDistance = 0.5f;

        // Perf (#716 B7): the occlusion sweep runs in LateUpdate on the pushing client. One shared
        // buffer is safe because only the local player can ever be pushing a cart.
        private const int OcclusionHitBufferSize = 24;
        private static readonly RaycastHit[] s_occlusionHits = new RaycastHit[OcclusionHitBufferSize];

        private sealed class RendererState
        {
            public Renderer Renderer;
            public bool Enabled;
            public ShadowCastingMode ShadowCastingMode;
            public bool ForceVisible;
        }

        private readonly List<RendererState> _bodyRenderers = new List<RendererState>();
        private readonly List<RendererState> _armsRenderers = new List<RendererState>();
        private readonly List<RendererState> _visorRenderers = new List<RendererState>();
        private PlayerControllerB _player;
        private Vector3 _savedCameraLocalPosition;
        private Quaternion _savedCameraLocalRotation;
        private bool _savedCameraLocalPositionValid;
        private bool _applied;
        private float _orbitYaw;
        private float _orbitPitch;
        private float _currentCameraDistance = CameraDistanceMeters;
        private RigWeightAccessor _cameraLookRig1;
        private RigWeightAccessor _cameraLookRig2;
        private float _savedCameraLookRig1Weight;
        private float _savedCameraLookRig2Weight;
        private bool _savedCameraLookRig1WeightValid;
        private bool _savedCameraLookRig2WeightValid;
        private bool _freeLookClampLogged;
        private bool _cameraLookRigSuppressLogged;
        private bool _cameraOcclusionLogged;
        private bool _closeCameraBodyHiddenLogged;

        public bool IsApplied => _applied;

        /// <summary>Capture current camera/renderer state for the player and mark active.</summary>
        public void Enable(PlayerControllerB player)
        {
            if (player == null)
                return;

            if (_applied)
                Disable();

            _player = player;
            _applied = true;
            _savedCameraLocalPositionValid = false;
            _currentCameraDistance = CameraDistanceMeters;
            _bodyRenderers.Clear();
            _armsRenderers.Clear();
            _visorRenderers.Clear();

            if (player.gameplayCamera != null)
            {
                _savedCameraLocalPosition = player.gameplayCamera.transform.localPosition;
                _savedCameraLocalRotation = player.gameplayCamera.transform.localRotation;
                _savedCameraLocalPositionValid = true;
            }

            CaptureCameraLookRigWeights(player);

            var seenBody = new HashSet<Renderer>();
            Capture(player.thisPlayerModel, _bodyRenderers, seenBody, forceVisible: true);
            Capture(player.thisPlayerModelLOD1, _bodyRenderers, seenBody, forceVisible: true);
            Capture(player.thisPlayerModelLOD2, _bodyRenderers, seenBody, forceVisible: true);

            Transform bodyRoot = FindChildDeep(player.transform, "ScavengerModel");
            if (bodyRoot != null)
            {
                foreach (Renderer r in bodyRoot.GetComponentsInChildren<Renderer>(includeInactive: true))
                    Capture(r, _bodyRenderers, seenBody, forceVisible: false);
            }

            var seenArms = new HashSet<Renderer>();
            Capture(player.thisPlayerModelArms, _armsRenderers, seenArms, forceVisible: false);
            Transform armsRoot = FindChildDeep(player.transform, "ScavengerModelArmsOnly");
            if (armsRoot != null)
            {
                foreach (Renderer r in armsRoot.GetComponentsInChildren<Renderer>(includeInactive: true))
                    Capture(r, _armsRenderers, seenArms, forceVisible: false);
            }

            // Hide the first-person helmet visor too, otherwise it frames the third-person view
            // (the curved black bars). player.localVisor is the vanilla visor transform.
            if (player.localVisor != null)
            {
                foreach (Renderer r in player.localVisor.GetComponentsInChildren<Renderer>(includeInactive: true))
                    Capture(r, _visorRenderers, seenArms, forceVisible: false);
            }
        }

        /// <summary>Re-apply the renderer overrides and reposition the camera. Call from LateUpdate.</summary>
        public void LateApply(PlayerControllerB player)
        {
            if (!_applied || player == null || player != _player)
                return;

            Camera camera = player.gameplayCamera;
            if (camera == null)
                return;

            Transform cameraTransform = camera.transform;
            Transform pivot = player.cameraContainerTransform != null
                ? player.cameraContainerTransform
                : cameraTransform.parent;
            Vector3 target = ResolveCameraPivot(player, pivot);
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            UpdateOrbitOffsets();
            ApplyCameraLookRigSuppression();

            float bodyYaw = player.thisPlayerBody != null
                ? player.thisPlayerBody.eulerAngles.y
                : player.transform.eulerAngles.y;
            Quaternion orbitRotation = Quaternion.Euler(_orbitPitch, bodyYaw + _orbitYaw, 0f);
            Vector3 desiredDirection = orbitRotation * Vector3.back;
            float desiredDistance = CameraDistanceMeters;
            float targetDistance = ResolveCameraOccludedDistance(player, target, desiredDirection, desiredDistance);
            if (targetDistance < _currentCameraDistance)
                _currentCameraDistance = targetDistance;
            else
                _currentCameraDistance = Mathf.MoveTowards(_currentCameraDistance, targetDistance, CameraOcclusionReturnSpeed * dt);

            bool hideBodyForCloseCamera = _currentCameraDistance <= MinCameraDistance + 0.05f;
            ApplyRendererVisibility(hideBodyForCloseCamera);

            cameraTransform.position = target + desiredDirection * _currentCameraDistance;
            Vector3 lookDirection = target - cameraTransform.position;
            if (lookDirection.sqrMagnitude > 0.0001f)
                cameraTransform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
        }

        /// <summary>Restore the captured camera/renderer state.</summary>
        public void Disable()
        {
            if (_applied)
            {
                if (_player != null && _player.gameplayCamera != null && _savedCameraLocalPositionValid)
                    _player.gameplayCamera.transform.localPosition = _savedCameraLocalPosition;
                if (_player != null && _player.gameplayCamera != null && _savedCameraLocalPositionValid)
                    _player.gameplayCamera.transform.localRotation = _savedCameraLocalRotation;

                // Death has already handed visibility to vanilla's corpse/spectator path.
                // Do not resurrect the captured living arms or body during teardown.
                if (_player != null && !_player.isPlayerDead)
                {
                    Restore(_bodyRenderers);
                    Restore(_armsRenderers);
                }
                // Vanilla moves this separately rooted visor away on death, then back on revival.
                // Its renderers must be restored even while the player's own arms stay hidden.
                Restore(_visorRenderers);
                RestoreCameraLookRigWeights();
            }

            _bodyRenderers.Clear();
            _armsRenderers.Clear();
            _visorRenderers.Clear();
            _player = null;
            _applied = false;
            _orbitYaw = 0f;
            _orbitPitch = 0f;
            _cameraLookRig1 = null;
            _cameraLookRig2 = null;
            _freeLookClampLogged = false;
            _cameraLookRigSuppressLogged = false;
            _cameraOcclusionLogged = false;
            _closeCameraBodyHiddenLogged = false;
        }

        private void UpdateOrbitOffsets()
        {
            if (Mouse.current == null)
                return;

            Vector2 delta = Mouse.current.delta.ReadValue() * 0.008f;
            if (IngamePlayerSettings.Instance != null)
            {
                delta *= IngamePlayerSettings.Instance.settings.lookSensitivity;
                if (IngamePlayerSettings.Instance.flipCamera)
                    delta.x = -delta.x;
                if (IngamePlayerSettings.Instance.settings.invertYAxis)
                    delta.y *= -1f;
            }

            if (delta.sqrMagnitude < 0.000001f)
                return;

            _orbitYaw += delta.x;
            if (_orbitYaw < -PayloadPushLook.FreeLookMaxYaw || _orbitYaw > PayloadPushLook.FreeLookMaxYaw)
            {
                _orbitYaw = Mathf.Clamp(_orbitYaw, -PayloadPushLook.FreeLookMaxYaw, PayloadPushLook.FreeLookMaxYaw);
                if (!_freeLookClampLogged)
                {
                    _freeLookClampLogged = true;
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Controls] Payload camera orbit yaw clamped to +/-{PayloadPushLook.FreeLookMaxYaw:0.#} degrees.");
                }
            }
            _orbitPitch = Mathf.Clamp(_orbitPitch - delta.y, -FreeLookMaxPitch, FreeLookMaxPitch);
        }

        private void CaptureCameraLookRigWeights(PlayerControllerB player)
        {
            _cameraLookRig1 = RigWeightAccessor.Create(GetFieldValue(player, "cameraLookRig1"));
            _cameraLookRig2 = RigWeightAccessor.Create(GetFieldValue(player, "cameraLookRig2"));
            _savedCameraLookRig1WeightValid = TryGetRigWeight(_cameraLookRig1, out _savedCameraLookRig1Weight);
            _savedCameraLookRig2WeightValid = TryGetRigWeight(_cameraLookRig2, out _savedCameraLookRig2Weight);
            if (_savedCameraLookRig1WeightValid || _savedCameraLookRig2WeightValid)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Payload.Controls] Captured camera look rig weights: " +
                    $"rig1={(_savedCameraLookRig1WeightValid ? _savedCameraLookRig1Weight.ToString("0.###") : "missing")}, " +
                    $"rig2={(_savedCameraLookRig2WeightValid ? _savedCameraLookRig2Weight.ToString("0.###") : "missing")}.");
            }
        }

        private void ApplyCameraLookRigSuppression()
        {
            bool wrote1 = TrySetRigWeight(_cameraLookRig1, 0f);
            bool wrote2 = TrySetRigWeight(_cameraLookRig2, 0f);
            if (!_cameraLookRigSuppressLogged && (wrote1 || wrote2))
            {
                _cameraLookRigSuppressLogged = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.Payload.Controls] Suppressing vanilla camera-look spine/neck rig during payload mouse camera orbit.");
            }
        }

        private void ApplyRendererVisibility(bool hideBodyForCloseCamera)
        {
            if (hideBodyForCloseCamera && !_closeCameraBodyHiddenLogged)
            {
                _closeCameraBodyHiddenLogged = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.Payload.Camera] Hiding local body while payload camera is forced close to the head pivot.");
            }

            // Read-then-write (#716 B7): the setters are native property writes that dirty renderer state
            // every frame. Reading first is cheap and still corrects anything vanilla flipped back.
            for (int i = 0; i < _bodyRenderers.Count; i++)
            {
                RendererState state = _bodyRenderers[i];
                Renderer renderer = state.Renderer;
                if (renderer == null)
                    continue;

                bool desiredEnabled = !hideBodyForCloseCamera && (state.ForceVisible || state.Enabled);
                if (renderer.enabled != desiredEnabled)
                    renderer.enabled = desiredEnabled;
                if (renderer.shadowCastingMode != ShadowCastingMode.On)
                    renderer.shadowCastingMode = ShadowCastingMode.On;
            }

            for (int i = 0; i < _armsRenderers.Count; i++)
            {
                Renderer renderer = _armsRenderers[i].Renderer;
                if (renderer != null && renderer.enabled)
                    renderer.enabled = false;
            }
            for (int i = 0; i < _visorRenderers.Count; i++)
            {
                Renderer renderer = _visorRenderers[i].Renderer;
                if (renderer != null && renderer.enabled)
                    renderer.enabled = false;
            }
        }

        private float ResolveCameraOccludedDistance(PlayerControllerB player, Vector3 origin, Vector3 direction, float desiredDistance)
        {
            if (direction.sqrMagnitude < 0.0001f)
                return desiredDistance;

            direction.Normalize();
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : Physics.DefaultRaycastLayers;
            int hitCount = Physics.SphereCastNonAlloc(
                origin,
                CameraOcclusionSphereRadius,
                direction,
                s_occlusionHits,
                desiredDistance,
                mask,
                QueryTriggerInteraction.Ignore);
            float bestDistance = desiredDistance;

            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = s_occlusionHits[i].collider;
                if (ShouldIgnoreCameraOcclusionHit(player, collider))
                    continue;

                float candidateDistance = Mathf.Max(MinCameraDistance, s_occlusionHits[i].distance - CameraOcclusionSkin);
                if (candidateDistance < bestDistance)
                    bestDistance = candidateDistance;
            }

            if (bestDistance < desiredDistance - 0.01f && !_cameraOcclusionLogged)
            {
                _cameraOcclusionLogged = true;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Payload.Camera] Third-person camera occlusion active: mask=StartOfRound.collidersAndRoomMaskAndDefault, " +
                    $"sphereRadius={CameraOcclusionSphereRadius:0.##}, skin={CameraOcclusionSkin:0.##}.");
            }

            return Mathf.Clamp(bestDistance, MinCameraDistance, desiredDistance);
        }

        private static bool ShouldIgnoreCameraOcclusionHit(PlayerControllerB player, Collider collider)
        {
            if (collider == null)
                return true;

            Transform hitTransform = collider.transform;
            if (player != null && player.transform != null && hitTransform.IsChildOf(player.transform))
                return true;

            PayloadCartController cart = PayloadCartController.LocalAttachedCart;
            if (cart != null && cart.transform != null && (hitTransform == cart.transform || hitTransform.IsChildOf(cart.transform)))
                return true;

            if (collider.GetComponentInParent<EnemyAI>() != null)
                return true;

            return false;
        }

        private static Vector3 ResolveCameraPivot(PlayerControllerB player, Transform pivot)
        {
            if (pivot != null)
                return pivot.position + Vector3.up * CameraHeight;
            return player.transform.position + Vector3.up * 1.6f;
        }

        private void RestoreCameraLookRigWeights()
        {
            if (_savedCameraLookRig1WeightValid)
                TrySetRigWeight(_cameraLookRig1, _savedCameraLookRig1Weight);
            if (_savedCameraLookRig2WeightValid)
                TrySetRigWeight(_cameraLookRig2, _savedCameraLookRig2Weight);
        }

        private static object GetFieldValue(object target, string fieldName)
        {
            if (target == null || string.IsNullOrEmpty(fieldName))
                return null;

            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null ? field.GetValue(target) : null;
        }

        private static bool TryGetRigWeight(RigWeightAccessor rig, out float weight)
        {
            weight = 0f;
            return rig != null && rig.TryGet(out weight);
        }

        private static bool TrySetRigWeight(RigWeightAccessor rig, float weight)
        {
            return rig != null && rig.TrySet(weight);
        }

        /// <summary>
        /// Bound accessor for an Animation Rigging `Rig.weight` (#716 B7). The rig type lives in an
        /// assembly this mod does not reference, so it is still reached by name — but the member lookup
        /// and, where possible, the invocation are resolved once at capture time into a bound delegate
        /// instead of a `GetProperty` + boxing `SetValue` on every LateUpdate.
        /// </summary>
        private sealed class RigWeightAccessor
        {
            private readonly object _target;
            private readonly System.Action<float> _setter;
            private readonly System.Func<float> _getter;
            private readonly PropertyInfo _property;
            private readonly FieldInfo _field;

            private RigWeightAccessor(
                object target,
                System.Action<float> setter,
                System.Func<float> getter,
                PropertyInfo property,
                FieldInfo field)
            {
                _target = target;
                _setter = setter;
                _getter = getter;
                _property = property;
                _field = field;
            }

            internal static RigWeightAccessor Create(object rig)
            {
                if (rig == null)
                    return null;

                System.Type type = rig.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo property = type.GetProperty("weight", flags);
                if (property != null && property.PropertyType != typeof(float))
                    property = null;

                System.Action<float> setter = null;
                System.Func<float> getter = null;
                if (property != null)
                {
                    setter = CreateDelegate<System.Action<float>>(rig, property.GetSetMethod(nonPublic: true));
                    getter = CreateDelegate<System.Func<float>>(rig, property.GetGetMethod(nonPublic: true));
                }

                FieldInfo field = type.GetField("weight", flags);
                if (field != null && field.FieldType != typeof(float))
                    field = null;

                if (property == null && field == null)
                    return null;

                return new RigWeightAccessor(rig, setter, getter, property, field);
            }

            private static T CreateDelegate<T>(object target, MethodInfo method) where T : class
            {
                if (method == null)
                    return null;

                try
                {
                    return method.CreateDelegate(typeof(T), target) as T;
                }
                catch
                {
                    return null;
                }
            }

            internal bool TryGet(out float weight)
            {
                if (_getter != null)
                {
                    weight = _getter();
                    return true;
                }

                if (_property != null && _property.CanRead && _property.GetValue(_target, null) is float propertyValue)
                {
                    weight = propertyValue;
                    return true;
                }

                if (_field != null)
                {
                    weight = (float)_field.GetValue(_target);
                    return true;
                }

                weight = 0f;
                return false;
            }

            internal bool TrySet(float weight)
            {
                if (_setter != null)
                {
                    _setter(weight);
                    return true;
                }

                if (_property != null && _property.CanWrite)
                {
                    _property.SetValue(_target, weight, null);
                    return true;
                }

                if (_field != null)
                {
                    _field.SetValue(_target, weight);
                    return true;
                }

                return false;
            }
        }

        private static void Capture(Renderer renderer, List<RendererState> states, HashSet<Renderer> seen, bool forceVisible)
        {
            if (renderer == null || seen.Contains(renderer))
                return;

            seen.Add(renderer);
            states.Add(new RendererState
            {
                Renderer = renderer,
                Enabled = renderer.enabled,
                ShadowCastingMode = renderer.shadowCastingMode,
                ForceVisible = forceVisible
            });
        }

        private static void Restore(List<RendererState> states)
        {
            for (int i = 0; i < states.Count; i++)
            {
                RendererState state = states[i];
                if (state.Renderer == null)
                    continue;
                state.Renderer.enabled = state.Enabled;
                state.Renderer.shadowCastingMode = state.ShadowCastingMode;
            }
        }

        internal static Transform FindChildDeep(Transform root, string childName)
        {
            if (root == null || string.IsNullOrEmpty(childName))
                return null;
            if (string.Equals(root.name, childName, System.StringComparison.Ordinal))
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildDeep(root.GetChild(i), childName);
                if (found != null)
                    return found;
            }

            return null;
        }
    }

    /// <summary>
    /// Builds an <see cref="InteractTrigger"/> on the cart's HandleGrab child so a player can press [E]
    /// to grab/release the cart (mirrors the DefuseInteractor InteractTrigger build). onInteract fires on
    /// the interacting client's local player; it forwards to <see cref="PayloadCartController"/>.
    /// </summary>
    internal sealed class PayloadHandleInteractable : MonoBehaviour
    {
        private const int InteractLayer = 9; // InteractableObject

        internal PayloadCartController Cart;
        private InteractTrigger _trigger;
        private BoxCollider _collider;
        private bool _stateApplied;
        private bool _lastInteractable;
        private bool _lastAttachedByLocal;
        private bool _lastAttachedByOther;
        private bool _lastFailed;

        internal void Build(PayloadCartController cart)
        {
            Cart = cart;

            _collider = GetComponent<BoxCollider>();
            if (_collider == null)
                _collider = gameObject.AddComponent<BoxCollider>();
            _collider.isTrigger = true;
            _collider.center = Vector3.zero;
            _collider.size = new Vector3(0.8f, 0.8f, 0.8f);

            gameObject.layer = InteractLayer;
            try { gameObject.tag = "InteractTrigger"; } catch { gameObject.tag = "Untagged"; }

            _trigger = GetComponent<InteractTrigger>();
            if (_trigger == null)
                _trigger = gameObject.AddComponent<InteractTrigger>();

            // Reuse the proven Defuse trigger setup (icons, events, non-hold).
            DefuseInteractor.ConfigureCommonTrigger(_trigger, holdInteraction: false, holdSeconds: 0.1f);
            _trigger.onInteract.RemoveListener(OnInteract);
            _trigger.onInteract.AddListener(OnInteract);
            _stateApplied = false;
            RefreshState();
        }

        internal void RefreshState()
        {
            if (_trigger == null)
                return;

            // A failed cart must refuse the grab on every peer, exactly like a delivered one (#716 E9).
            bool failed = Cart != null && Cart.IsFailed;
            bool canGrab = Cart != null && !Cart.IsDelivered && !failed;
            bool attachedByLocal = Cart != null && Cart.IsAttachedToLocalPlayer;
            bool attachedByOther = Cart != null && Cart.IsAttached && !attachedByLocal;

            bool interactable = canGrab && !attachedByOther;
            // RefreshState runs from PayloadCartController.Update on every peer, every frame; the state
            // it writes only changes on a grab/release/deliver/fail edge (#716 B7).
            if (_stateApplied
                && _lastInteractable == interactable
                && _lastAttachedByLocal == attachedByLocal
                && _lastAttachedByOther == attachedByOther
                && _lastFailed == failed)
            {
                return;
            }

            _stateApplied = true;
            _lastInteractable = interactable;
            _lastAttachedByLocal = attachedByLocal;
            _lastAttachedByOther = attachedByOther;
            _lastFailed = failed;

            _trigger.interactable = interactable;
            // [LMB] is vanilla's interact-key token: PlayerControllerB rewrites it to the player's bound
            // key (or the controller glyph) before the prompt renders, where a literal [E] never updates.
            _trigger.hoverTip = attachedByLocal ? "Release cart : [LMB]" : "Push cart : [LMB]";
            _trigger.disabledHoverTip = attachedByOther
                ? "[ In use ]"
                : (failed ? "[ Payload lost ]" : "[ Delivered ]");
            DefuseInteractor.ApplyInteractionIcons(_trigger);
            if (_collider != null)
                _collider.enabled = _trigger.interactable;
        }

        public void OnInteract(PlayerControllerB player)
        {
            Cart?.OnHandleInteract(player);
            RefreshState();
        }
    }
}
