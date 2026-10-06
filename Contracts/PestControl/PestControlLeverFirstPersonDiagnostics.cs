using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// Bounded runtime evidence for the Pest Control first-person throw. Issue #381 failed two
    /// playtests without proving whether the layer, renderer, or arms root was responsible; this
    /// trace records each link while keeping normal frame logging capped per throw.
    /// </summary>
    internal static class PestControlLeverFirstPersonDiagnostics
    {
        private const float SampleIntervalSeconds = 0.1f;
        private const int MaxSamplesPerThrow = 8;

        private static readonly int PushStateHash =
            Animator.StringToHash(PestControlLeverInteractionsBridge.PushState);
        private static readonly int PullStateHash =
            Animator.StringToHash(PestControlLeverInteractionsBridge.PullState);
        private static readonly int NeutralStateHash =
            Animator.StringToHash(PestControlLeverInteractionsBridge.NeutralState);

        private static bool _active;
        private static bool _postCrossfadePending;
        private static int _nextThrowId;
        private static int _throwId;
        private static int _layer = -1;
        private static int _crossfadeFrame;
        private static int _sampleCount;
        private static float _nextSampleAt;
        private static string _requestedState = string.Empty;

        internal static void BeginCrossfade(
            PlayerControllerB player,
            Animator animator,
            int layer,
            string stateName,
            bool armsForced,
            bool savedArmsEnabled)
        {
            if (!Enabled)
            {
                Reset();
                return;
            }

            bool beginsThrow = !_active ||
                !string.Equals(stateName, PestControlLeverInteractionsBridge.NeutralState);
            if (beginsThrow)
            {
                _throwId = ++_nextThrowId;
                _sampleCount = 0;
            }

            _active = true;
            _postCrossfadePending = false;
            _layer = layer;
            _requestedState = stateName ?? string.Empty;
            _nextSampleAt = Time.unscaledTime + SampleIntervalSeconds;

            LogSnapshot(
                player,
                animator,
                armsForced,
                savedArmsEnabled,
                $"crossfade.request animator={(animator != null ? "present" : "null")} " +
                $"layerName='{PestControlLeverInteractionsBridge.FirstPersonArmsLayer}' " +
                $"resolvedLayer={layer} layerCount={(animator != null ? animator.layerCount : -1)}");
        }

        internal static void CrossfadeQueued()
        {
            if (!_active || !Enabled)
                return;

            _crossfadeFrame = Time.frameCount;
            _postCrossfadePending = true;
        }

        internal static void Tick(
            PlayerControllerB player,
            bool armsForced,
            bool savedArmsEnabled)
        {
            if (!_active)
                return;
            if (!Enabled)
            {
                Reset();
                return;
            }

            Animator animator = player != null ? player.playerBodyAnimator : null;
            if (_postCrossfadePending && Time.frameCount > _crossfadeFrame)
            {
                _postCrossfadePending = false;
                LogSnapshot(
                    player,
                    animator,
                    armsForced,
                    savedArmsEnabled,
                    "crossfade.one_frame_later");
                _nextSampleAt = Time.unscaledTime + SampleIntervalSeconds;
            }

            if (_sampleCount >= MaxSamplesPerThrow || Time.unscaledTime < _nextSampleAt)
                return;

            _sampleCount++;
            _nextSampleAt = Time.unscaledTime + SampleIntervalSeconds;
            LogSnapshot(
                player,
                animator,
                armsForced,
                savedArmsEnabled,
                $"throw.sample index={_sampleCount}/{MaxSamplesPerThrow}");
        }

        internal static void End(
            PlayerControllerB player,
            bool armsForced,
            bool savedArmsEnabled,
            string reason)
        {
            if (!_active)
                return;

            if (Enabled)
            {
                LogSnapshot(
                    player,
                    player != null ? player.playerBodyAnimator : null,
                    armsForced,
                    savedArmsEnabled,
                    $"throw.end reason={reason}");
            }

            Reset();
        }

        private static bool Enabled =>
            ContractsBootstrap.PestLeverFirstPersonDiagnostics?.Value == true;

        private static void Reset()
        {
            _active = false;
            _postCrossfadePending = false;
            _layer = -1;
            _sampleCount = 0;
            _requestedState = string.Empty;
        }

        private static void LogSnapshot(
            PlayerControllerB player,
            Animator animator,
            bool armsForced,
            bool savedArmsEnabled,
            string stage)
        {
            Renderer armsRenderer = player != null ? player.thisPlayerModelArms : null;
            Transform armsRoot = player != null ? player.playerModelArmsMetarig : null;
            Transform localArms = player != null ? player.localArmsTransform : null;
            Transform rotationTarget = player != null ? player.localArmsRotationTarget : null;
            Camera gameplayCamera = player != null ? player.gameplayCamera : null;
            Transform cameraTransform = gameplayCamera != null ? gameplayCamera.transform : null;

            string currentState = "unavailable";
            string nextState = "none";
            float layerWeight = -1f;
            bool transitioning = false;
            if (animator != null && _layer >= 0 && _layer < animator.layerCount)
            {
                AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(_layer);
                currentState = FormatState(current);
                layerWeight = animator.GetLayerWeight(_layer);
                transitioning = animator.IsInTransition(_layer);
                if (transitioning)
                    nextState = FormatState(animator.GetNextAnimatorStateInfo(_layer));
            }

            int rendererLayer = armsRenderer != null && armsRenderer.gameObject != null
                ? armsRenderer.gameObject.layer
                : -1;
            bool renderedByGameplayCamera = gameplayCamera != null &&
                rendererLayer >= 0 && rendererLayer < 32 &&
                (gameplayCamera.cullingMask & (1 << rendererLayer)) != 0;
            string rendererState = armsRenderer == null
                ? "null"
                : $"enabled={armsRenderer.enabled},active={armsRenderer.gameObject.activeInHierarchy}," +
                  $"isVisible={armsRenderer.isVisible},layer={rendererLayer}," +
                  $"renderedByGameplayCamera={renderedByGameplayCamera}";
            string poseState = armsRoot == null || cameraTransform == null
                ? $"armsRoot={(armsRoot != null ? "present" : "null")}," +
                  $"camera={(cameraTransform != null ? "present" : "null")}"
                : $"armsRootWorld={FormatVector(armsRoot.position)}," +
                  $"cameraWorld={FormatVector(cameraTransform.position)}," +
                  $"rootMinusCamera={FormatVector(armsRoot.position - cameraTransform.position)}," +
                  $"distance={Vector3.Distance(armsRoot.position, cameraTransform.position):0.000}," +
                  $"armsRootLocal={FormatVector(armsRoot.localPosition)}";
            string rotationState = armsRoot == null || rotationTarget == null
                ? $"rotationRoot={(armsRoot != null ? "present" : "null")}," +
                  $"rotationTarget={(rotationTarget != null ? "present" : "null")}"
                : $"armsRootEuler={FormatVector(armsRoot.eulerAngles)}," +
                  $"rotationTargetEuler={FormatVector(rotationTarget.eulerAngles)}," +
                  $"rootTargetAngle={Quaternion.Angle(armsRoot.rotation, rotationTarget.rotation):0.000}";

            string glueState;
            if (player == null)
            {
                glueState = "player=null";
            }
            else if (localArms == null || player.cameraContainerTransform == null || cameraTransform == null)
            {
                glueState = $"localArmsMatchCamera={player.localArmsMatchCamera}," +
                    $"localArms={(localArms != null ? "present" : "null")}," +
                    $"cameraContainer={(player.cameraContainerTransform != null ? "present" : "null")}";
            }
            else
            {
                Vector3 expectedLocalArmsWorld =
                    player.cameraContainerTransform.position + cameraTransform.up * -0.5f;
                glueState = $"localArmsMatchCamera={player.localArmsMatchCamera}," +
                    $"localArmsWorld={FormatVector(localArms.position)}," +
                    $"expectedLocalArmsWorld={FormatVector(expectedLocalArmsWorld)}," +
                    $"localArmsDelta={Vector3.Distance(localArms.position, expectedLocalArmsWorld):0.000}";
            }
            string specialAnimation = player != null
                ? player.inSpecialInteractAnimation.ToString()
                : "null";

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.PestControl.FPDiag] throw={_throwId} " +
                $"frame={Time.frameCount} realtime={Time.unscaledTime:0.000} stage={stage} " +
                $"requestedState='{_requestedState}' resolvedLayer={_layer} " +
                $"currentState={currentState} nextState={nextState} " +
                $"layerWeight={layerWeight:0.000} transitioning={transitioning} " +
                $"renderer={rendererState} armsForced={armsForced} " +
                $"savedRendererEnabled={savedArmsEnabled} specialAnim={specialAnimation} " +
                $"{poseState} {rotationState} {glueState}.");
        }

        private static string FormatState(AnimatorStateInfo state)
        {
            return $"{KnownState(state.shortNameHash)}(shortHash={state.shortNameHash}," +
                   $"fullHash={state.fullPathHash},normalized={state.normalizedTime:0.000})";
        }

        private static string KnownState(int shortNameHash)
        {
            if (shortNameHash == PushStateHash)
                return PestControlLeverInteractionsBridge.PushState;
            if (shortNameHash == PullStateHash)
                return PestControlLeverInteractionsBridge.PullState;
            if (shortNameHash == NeutralStateHash)
                return PestControlLeverInteractionsBridge.NeutralState;
            return "unknown";
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:0.000},{value.y:0.000},{value.z:0.000})";
        }
    }
}
