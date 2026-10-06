using System.Collections;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// Holds the operator through a lever throw the way vanilla holds a player through the ship
    /// lever or the terminal: planted on the stand node, turned to face the handle, movement
    /// surrendered, and an authored clip driving the body.
    ///
    /// There are two routes to that clip and this class owns choosing between them:
    ///
    /// 1. <see cref="PestControlLeverInteractionsBridge"/> plays the authored
    ///    <c>Y4NGZ_PestLever_*</c> pack on the real body through Y4NGZInteractions. This is the
    ///    intended presentation — it has separate push and pull clips, first-person arms, and an
    ///    authored exit that crossfades back to Neutral.
    /// 2. Failing that, the stock <c>SA_PullLever</c> state. Y4NGZInteractions is an optional
    ///    dependency, so this path has to stay working; it is a same-clip-both-directions
    ///    approximation and it looks it.
    ///
    /// Neither route goes through <c>InteractTrigger.specialCharacterAnimation</c>. The trap's
    /// trigger is built after <c>NetworkObject.Spawn</c>, so it is missing from the NetworkObject's
    /// cached ChildNetworkBehaviours and the RPC that path opens with would be misrouted (see
    /// PestControlTrapAssembler.BuildLeverTrigger). Both instead ride
    /// <see cref="PlayerControllerB.UpdateSpecialAnimationValue"/>, which is PlayerControllerB's own
    /// registered RPC pair and therefore replicates the pose to every peer correctly — the same call
    /// vanilla's own SpawnPlayerAnimation uses.
    ///
    /// The previous implementation hand-sampled authored clips onto the rig each LateUpdate and
    /// dragged the IK targets toward the lever in WORLD space. On the first-person arms — which are
    /// parented under the camera — that yanked the hand up to 0.32 m off-camera and parked it in a
    /// screen corner for the duration of the throw. Playing the authored clips properly is the
    /// bridge's job, not a bespoke sampler's.
    /// </summary>
    internal sealed class PestControlLeverPlayerAnimation : MonoBehaviour
    {
        /// <summary>Stock player-animator trigger for a lever throw. Fallback route only.</summary>
        private const string PullTrigger = "SA_PullLever";

        /// <summary>How hard the body is pulled onto the stand node. Matches vanilla's lockPlayerPosition.</summary>
        private const float PlantLerpRate = 20f;

        /// <summary>How long the fallback waits for the animator to actually enter the lever state.</summary>
        private const float LegacyStateResolveSeconds = 0.2f;

        /// <summary>Ceiling on a measured fallback throw, so a mis-read state cannot pin the operator.</summary>
        private const float MaxLegacyThrowSeconds = 1.5f;

        /// <summary>How long the view takes to settle onto the handle at the start of a throw.</summary>
        private const float AimBlendInSeconds = 0.12f;

        /// <summary>And to come off it again. Short enough to finish inside the authored exit.</summary>
        private const float AimBlendOutSeconds = 0.14f;

        /// <summary>Rail on the aimed pitch, kept inside vanilla's own look limits.</summary>
        private const float MaxAimPitchDegrees = 70f;

        /// <summary>
        /// Crossfade into the first-person arms state. Short: the whole throw is 0.5 s and the
        /// handle bottoms out at 0.333 s, so a longer blend would swallow the reach.
        /// </summary>
        private const float FirstPersonCrossfadeSeconds = 0.06f;

        private PlayerControllerB _player;
        private PestControlTrapPreview _preview;
        private Transform _standNode;
        private Coroutine _hold;
        private object _interactionHandle;

        private bool _armsForced;
        private bool _savedArmsEnabled;
        private bool _savedLocalArmsMatchCamera;

        private bool _aimEngaged;
        private float _aimWeight;
        private float _aimTarget;
        private float _savedCameraUp;
        private float _savedMinVerticalClamp;
        private float _savedMaxVerticalClamp;

        internal static void Play(
            ulong playerClientId,
            bool pushingDown,
            float elapsedSeconds,
            PestControlTrapPreview preview)
        {
            PlayerControllerB player = FindPlayer(playerClientId);
            if (player != null)
                Play(player, pushingDown, elapsedSeconds, preview);
        }

        internal static void Play(
            PlayerControllerB player,
            bool pushingDown,
            float elapsedSeconds,
            PestControlTrapPreview preview)
        {
            // The preview carries both the stand node and the live grip point; the throw needs the
            // first to plant the body and the second to aim the view, so it is passed whole rather
            // than reduced to a transform at the call site.
            Transform standNode = preview != null ? preview.LeverStandNode : null;

            // Only the machine that owns the player drives the hold. Every other peer receives the
            // pose through UpdateSpecialAnimationValue's replication.
            if (player == null || player.playerBodyAnimator == null || standNode == null)
                return;
            if (GameNetworkManager.Instance == null || GameNetworkManager.Instance.localPlayerController != player)
                return;
            if (player.isPlayerDead || player.inSpecialInteractAnimation)
                return;

            float remaining = PestControlTrapPreview.LeverAnimationSeconds - Mathf.Max(0f, elapsedSeconds);
            if (remaining <= 0f)
                return;

            PestControlLeverPlayerAnimation driver =
                player.GetComponent<PestControlLeverPlayerAnimation>() ??
                player.gameObject.AddComponent<PestControlLeverPlayerAnimation>();
            driver.Begin(player, preview, standNode, remaining, pushingDown);
        }

        private static PlayerControllerB FindPlayer(ulong playerClientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && player.playerClientId == playerClientId)
                    return player;
            }

            return null;
        }

        private void Begin(
            PlayerControllerB player,
            PestControlTrapPreview preview,
            Transform standNode,
            float holdSeconds,
            bool pushingDown)
        {
            if (_hold != null)
            {
                StopCoroutine(_hold);
                EndInteraction(interrupted: true);
                RestoreAim();
                Release();
            }

            _player = player;
            _preview = preview;
            _standNode = standNode;
            _hold = StartCoroutine(HoldRoutine(holdSeconds, pushingDown));
        }

        private IEnumerator HoldRoutine(float holdSeconds, bool pushingDown)
        {
            _player.ResetFallGravity();
            _player.Crouch(crouch: false);

            // Start the authored session BEFORE asserting inSpecialInteractAnimation. The flag is
            // harmless to the session itself (the bridge registers the pack with
            // stopOnVanillaSpecialAnimation false, precisely because this interaction owns the flag
            // for its whole duration), but starting first means a refused start never leaves the
            // player locked into a special animation with nothing playing.
            bool authored = PestControlLeverInteractionsBridge.TryStartLeverThrow(
                _player, pushingDown, out _interactionHandle);

            // The authored first-person arms only reach the screen if BOTH of these run. Each was
            // a separate reason the throw played nothing in first person; see their summaries.
            if (authored)
            {
                BeginArmsPresentation();
                CrossFadeFirstPersonArms(
                    pushingDown
                        ? PestControlLeverInteractionsBridge.PushState
                        : PestControlLeverInteractionsBridge.PullState);
            }

            // Mirrors PlayerControllerB.SpawnPlayerAnimation: replicate first, then latch the flag
            // locally. The yVal argument is what turns the body to the lever on every other client.
            _player.UpdateSpecialAnimationValue(true, (short)_standNode.eulerAngles.y);
            _player.inSpecialInteractAnimation = true;

            // The plant turns the body to the stand node, which is a floor node: yaw only, level.
            // The axle sits at 0.95 m and the grip finishes near chest height, so a level view
            // looks clean over the top of the handle and the throw happens off-screen. Nothing
            // else aims the camera - the pack declares preserveGameplayCamera, so the Interactions
            // API deliberately leaves it alone, which is exactly what makes it ours to drive.
            BeginAim();

            if (!authored)
            {
                Animator animator = _player.playerBodyAnimator;
                int beforeHash = animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
                animator.ResetTrigger(PullTrigger);
                animator.SetTrigger(PullTrigger);

                // Vanilla's SA_PullLever runs longer than the authored throw, and cutting the hold
                // at LeverAnimationSeconds dropped the flag mid-clip — the arm simply disappearing.
                // Measure the state instead of guessing, but measure it AFTER the transition: a
                // trigger does not take effect until the animator next evaluates, so reading the
                // length on this frame would return whatever the player was already doing (an idle
                // or walk loop) and pin them there for its duration. Clamped at both ends so a
                // missed transition can neither shorten the hold nor strand the operator.
                float waited = 0f;
                while (waited < LegacyStateResolveSeconds)
                {
                    yield return null;
                    waited += Time.deltaTime;
                    if (_player == null || _player.isPlayerDead)
                        break;

                    AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
                    if (state.fullPathHash == beforeHash)
                        continue;

                    float length = state.speed > 0f ? state.length / state.speed : state.length;
                    holdSeconds = Mathf.Clamp(length, holdSeconds, MaxLegacyThrowSeconds);
                    break;
                }

                holdSeconds = Mathf.Max(0f, holdSeconds - waited);
            }

            yield return PlantFor(holdSeconds);

            // Graceful exit: fire the authored exit trigger and let it crossfade back to Neutral.
            // The plant and the flag both have to survive that crossfade, or vanilla locomotion
            // takes the rig back mid-blend and the exit is a hard cut again.
            if (authored && _interactionHandle != null)
            {
                PestControlLeverInteractionsBridge.TryEndLeverThrow(_interactionHandle, interrupted: false);
                _interactionHandle = null;

                // The exit trigger is consumed by the full-body layer exactly like the enter one,
                // so the arms layer has to be walked back to Neutral by hand as well.
                CrossFadeFirstPersonArms(PestControlLeverInteractionsBridge.NeutralState);

                // Start handing the view back now so the ease overlaps the authored exit instead
                // of extending the lock after it.
                _aimTarget = 0f;
                yield return PlantFor(PestControlLeverInteractionsBridge.ExitSeconds);
            }

            // Finish the hand-back before the flag drops. If the view were still aimed when
            // inSpecialInteractAnimation clears, the player's own look input would resume against
            // a pitch it did not choose and snap.
            yield return ReleaseAim();

            Release();
            _hold = null;
        }

        /// <summary>Holds the operator on the stand node for a span, yielding each frame.</summary>
        private IEnumerator PlantFor(float seconds)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                if (_player == null || _player.isPlayerDead || _standNode == null)
                    yield break;

                Transform body = _player.thisPlayerBody;
                if (body != null)
                {
                    body.position = Vector3.Lerp(body.position, _standNode.position, Time.deltaTime * PlantLerpRate);
                    body.rotation = Quaternion.Lerp(body.rotation, _standNode.rotation, Time.deltaTime * PlantLerpRate);
                }

                elapsed += Time.deltaTime;
                yield return null;
            }
        }

        /// <summary>
        /// Drives the authored first-person arms layer directly, because the manifest's triggers
        /// cannot reach it.
        ///
        /// The pack keys the AnyState transitions of BOTH layers on <c>Y4NGZ_PestLever_Enter</c>
        /// (PestControlLeverAnimBuilder.BuildThrowLayer). A Unity trigger is reset by the first
        /// layer whose transition consumes it, so the full-body layer takes it and the arms layer
        /// never fires. Left alone, that layer then sits on its motionless Neutral while the
        /// manifest's startLayerWeight raises it to weight 1 as an Override layer — which pushes
        /// the bind pose straight through the arms. That is the "both arms shoot upward for maybe
        /// 1-3 frames" from the 2026-08-10 playtest, not a clip playing too fast.
        ///
        /// CrossFadeInFixedTime is the remedy the panic-slide/mantle work settled on for the same
        /// collision, and the exit trigger needs the same treatment as the enter one.
        /// </summary>
        private void CrossFadeFirstPersonArms(string stateName)
        {
            Animator animator = _player != null ? _player.playerBodyAnimator : null;
            int layer = animator != null
                ? animator.GetLayerIndex(PestControlLeverInteractionsBridge.FirstPersonArmsLayer)
                : -1;

            PestControlLeverFirstPersonDiagnostics.BeginCrossfade(
                _player, animator, layer, stateName, _armsForced, _savedArmsEnabled);
            if (animator == null || layer < 0)
                return;

            animator.CrossFadeInFixedTime(stateName, FirstPersonCrossfadeSeconds, layer);
            PestControlLeverFirstPersonDiagnostics.CrossfadeQueued();
        }

        /// <summary>
        /// Takes ownership of the first-person arms renderer for the throw.
        ///
        /// Vanilla hides the arms whenever the player is empty-handed, and the lever is thrown
        /// empty-handed — so however well the authored clip plays, it plays on an invisible mesh.
        /// Forcing the renderer on is the other half of making first person exist here.
        ///
        /// The lever also owns the camera-relative arms lease. Vanilla gates both of its normal
        /// arms-glue branches off while <c>inSpecialInteractAnimation</c> is true, leaving the rig
        /// body-aligned while this interaction pitches the gameplay camera toward the grip. The
        /// lease preserves the prior flag and replays vanilla's camera-matched pose after that
        /// pitch is written each LateUpdate.
        /// </summary>
        private void BeginArmsPresentation()
        {
            if (_armsForced || _player == null || _player.thisPlayerModelArms == null)
                return;

            _savedArmsEnabled = _player.thisPlayerModelArms.enabled;
            _savedLocalArmsMatchCamera = _player.localArmsMatchCamera;
            _armsForced = true;

            // Immediately, not just from the next LateUpdate: the whole throw is 0.5 s and the
            // handle bottoms out at 0.333 s, so a dropped opening frame is a visible chunk of it.
            ApplyArmsPresentation();
        }

        /// <summary>Reasserted every LateUpdate, because vanilla rewrites renderer state in Update.</summary>
        private void ApplyArmsPresentation()
        {
            if (_player == null)
                return;

            if (_player.thisPlayerModelArms != null)
                _player.thisPlayerModelArms.enabled = true;
            _player.localArmsMatchCamera = true;
        }

        /// <summary>
        /// Replays PlayerControllerB.LateUpdate's camera-matched arms branch after the lever has
        /// written its aimed pitch. Vanilla only runs this branch when special animation is false;
        /// the lever must do it explicitly while it owns that gate.
        /// </summary>
        private void ApplyCameraMatchedArmsPose()
        {
            if (_player == null || _player.gameplayCamera == null)
                return;

            if (_player.localArmsTransform != null && _player.cameraContainerTransform != null)
            {
                _player.localArmsTransform.position =
                    _player.cameraContainerTransform.position +
                    _player.gameplayCamera.transform.up * -0.5f;
            }

            if (_player.playerModelArmsMetarig != null && _player.localArmsRotationTarget != null)
                _player.playerModelArmsMetarig.rotation = _player.localArmsRotationTarget.rotation;
        }

        private void RestoreArmsPresentation()
        {
            if (!_armsForced)
                return;

            _armsForced = false;
            if (_player != null)
            {
                if (_player.thisPlayerModelArms != null)
                    _player.thisPlayerModelArms.enabled = _savedArmsEnabled;
                _player.localArmsMatchCamera = _savedLocalArmsMatchCamera;
            }
        }

        /// <summary>
        /// Takes ownership of the operator's pitch for the throw. Saved and restored as a set:
        /// <c>cameraUp</c> is re-clamped against the vertical window every frame, so writing the
        /// pitch without moving the window with it is dragged straight back - the same pin-to-target
        /// idiom the station viewpoint uses in <c>MonitorFocus.StationCamera</c>.
        /// </summary>
        private void BeginAim()
        {
            if (_aimEngaged || _player == null || _preview == null || _player.gameplayCamera == null)
                return;

            _savedCameraUp = _player.cameraUp;
            _savedMinVerticalClamp = _player.minVerticalClamp;
            _savedMaxVerticalClamp = _player.maxVerticalClamp;
            _aimWeight = 0f;
            _aimTarget = 1f;
            _aimEngaged = true;
        }

        /// <summary>Eases the view off the handle and waits for it, capped so it cannot strand.</summary>
        private IEnumerator ReleaseAim()
        {
            _aimTarget = 0f;

            float waited = 0f;
            while (_aimEngaged && waited < AimBlendOutSeconds + 0.1f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            RestoreAim();
        }

        private void RestoreAim()
        {
            if (!_aimEngaged)
                return;

            _aimEngaged = false;
            _aimWeight = 0f;
            if (_player == null)
                return;

            _player.cameraUp = _savedCameraUp;
            _player.minVerticalClamp = _savedMinVerticalClamp;
            _player.maxVerticalClamp = _savedMaxVerticalClamp;
        }

        /// <summary>
        /// Pitch, in <c>cameraUp</c>'s convention, that puts the handle in the middle of the view.
        /// Positive is downward: <c>cameraUp</c> is the gameplay camera's local X euler, and a
        /// positive X rotation turns forward toward down. Yaw is left alone because the plant has
        /// already turned the body onto the stand node, which faces the lever.
        /// </summary>
        private float GripPitchDegrees()
        {
            Vector3 toGrip = _preview.LeverGripPosition - _player.gameplayCamera.transform.position;
            float horizontal = new Vector2(toGrip.x, toGrip.z).magnitude;
            if (horizontal < 0.01f && Mathf.Abs(toGrip.y) < 0.01f)
                return _savedCameraUp;

            return -Mathf.Atan2(toGrip.y, horizontal) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Runs after PlayerControllerB has had its Update, so the pitch written here is the one
        /// that renders. The grip is re-read every frame rather than sampled once, so the view
        /// tracks the handle down through the throw instead of staring at where it started.
        /// </summary>
        private void LateUpdate()
        {
            if (_armsForced)
                ApplyArmsPresentation();

            UpdateAim();

            // This must follow UpdateAim: localArmsRotationTarget inherits the final camera pitch,
            // so applying the glue earlier would leave the rendered arms one frame behind it.
            if (_armsForced)
                ApplyCameraMatchedArmsPose();

            PestControlLeverFirstPersonDiagnostics.Tick(_player, _armsForced, _savedArmsEnabled);
        }

        private void UpdateAim()
        {
            if (!_aimEngaged)
                return;

            if (_player == null || _player.isPlayerDead || _preview == null || _player.gameplayCamera == null)
            {
                RestoreAim();
                return;
            }

            float blendSeconds = _aimTarget > _aimWeight ? AimBlendInSeconds : AimBlendOutSeconds;
            _aimWeight = blendSeconds > 0f
                ? Mathf.MoveTowards(_aimWeight, _aimTarget, Time.deltaTime / blendSeconds)
                : _aimTarget;

            if (_aimTarget <= 0f && _aimWeight <= 0f)
            {
                RestoreAim();
                return;
            }

            float aimed = Mathf.Clamp(GripPitchDegrees(), -MaxAimPitchDegrees, MaxAimPitchDegrees);
            float pitch = Mathf.LerpAngle(_savedCameraUp, aimed, Mathf.SmoothStep(0f, 1f, _aimWeight));

            _player.minVerticalClamp = pitch;
            _player.maxVerticalClamp = pitch;
            _player.cameraUp = pitch;
        }

        private void EndInteraction(bool interrupted)
        {
            if (_interactionHandle == null)
                return;

            PestControlLeverInteractionsBridge.TryEndLeverThrow(_interactionHandle, interrupted);
            _interactionHandle = null;
        }

        private void Release()
        {
            // Before the early-out: the arms lease is the one piece of borrowed state that
            // outlives a null player reference badly, since vanilla would keep them forced on.
            RestoreArmsPresentation();
            PestControlLeverFirstPersonDiagnostics.End(
                _player,
                _armsForced,
                _savedArmsEnabled,
                _player == null || _player.isPlayerDead ? "interrupted" : "released");

            if (_player == null)
                return;

            _player.inSpecialInteractAnimation = false;
            _player.UpdateSpecialAnimationValue(false, 0);
        }

        private void OnDisable()
        {
            if (_hold == null)
                return;

            StopCoroutine(_hold);
            _hold = null;
            EndInteraction(interrupted: true);
            RestoreAim();
            Release();
        }
    }
}
