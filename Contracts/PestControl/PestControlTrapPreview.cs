using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// Drives one assembled corral: drop/raise motion, rope follow, idle sway, physical sealing,
    /// and the lever's interact hookup.
    ///
    /// This is the SINGLE implementation of the trap's motion. Both the client-local debug
    /// preview and the networked <see cref="PestControlTrapController"/> attach one of these and
    /// call the same methods — the controller just decides WHEN, from replicated state, and owns
    /// the consequences. Nothing in here touches the network.
    /// </summary>
    internal sealed class PestControlTrapPreview : MonoBehaviour
    {
        internal enum TrapMotionState
        {
            Rest,
            Dropping,
            Dropped,
            Raising
        }

        internal const float DropSeconds = 0.35f;

        /// <summary>
        /// Winch travel. Was 4 s, which read as a stall rather than a machine doing work.
        /// </summary>
        internal const float RaiseSeconds = 2.0f;

        /// <summary>
        /// Length of one throw. This is the authored clip length, not a feel constant: all four
        /// <c>Y4NGZ_PestLever_*</c> clips run 0..0.5 s at 30 fps, and the animation pack's manifest
        /// declares the same 0.5 s as its interaction duration. Changing it desynchronises the arm
        /// from the body unless the clips are re-authored to match.
        /// </summary>
        internal const float LeverAnimationSeconds = 0.5f;

        /// <summary>
        /// Where in the throw the operator's hand reaches the handle and the lever bottoms out,
        /// as a fraction of <see cref="LeverAnimationSeconds"/>.
        ///
        /// Measured from the clips rather than guessed: the animated left-arm IK target in both
        /// third-person clips (<c>Y4NGZ_PestLever_TP_Push</c> and <c>_TP_Pull</c>) peaks at its
        /// furthest excursion from the rest pose at frame 10 of 15 — t = 0.333 s of 0.5 s — and
        /// travels back toward rest after it. The previous constants put contact at 0.175/0.188 s,
        /// roughly a third of the way in, so the handle finished its swing while the arm was still
        /// reaching. That is exactly the reported "the lever is down before the arm is even fully
        /// extended".
        /// </summary>
        internal const float LeverContactNormalized = 0.667f;

        /// <summary>Seconds from the interact to the handle reaching full throw.</summary>
        internal const float LeverContactSeconds = LeverAnimationSeconds * LeverContactNormalized;

        private const float BounceHeight = 0.14f;
        private const float BounceUpSeconds = 0.10f;
        private const float BounceDownSeconds = 0.09f;

        private const float SwayDegrees = 0.45f;

        private Transform _fence;
        private LineRenderer[] _ropes;
        private Transform[] _ropeAnchors;
        private List<Collider> _sealColliders;
        private List<NavMeshObstacle> _sealObstacles;
        private InteractTrigger _trigger;

        /// <summary>The swinging arm only. Its parent holds the fulcrum; this rotates about local X.</summary>
        private Transform _externalFixtureRoot;
        private Transform _leverArm;

        private Coroutine _motion;
        private Coroutine _leverMotion;
        private float _swaySeed;

        /// <summary>Rest height of the corral's bottom edge — adaptive, derived from the measured ceiling.</summary>
        private float _restBottom = 1.70f;

        internal TrapMotionState State { get; private set; } = TrapMotionState.Rest;

        internal bool IsSealed { get; private set; }

        internal float FootprintMeters { get; set; }

        internal float CageHeightMeters { get; set; }

        internal float RestBottomMeters { get; set; }

        internal int PanelsPerSide { get; set; }

        internal Vector3 LeverPosition { get; set; }

        internal bool LeverValidated { get; set; }

        internal bool HasLeverTrigger => _trigger != null;

        /// <summary>Raised on the LOCAL machine the instant the corral reaches the platform.</summary>
        internal Action FenceLanded;

        /// <summary>Raised on the LOCAL machine when the winch finishes returning the corral to rest.</summary>
        internal Action FenceRaised;

        /// <summary>
        /// Raised on the LOCAL machine when the winch actually engages — after the lever's throw
        /// completes, not on the interact frame. Cues that belong to the motion hang off this
        /// rather than off the state change, the same way the slam hangs off <see cref="FenceLanded"/>.
        /// </summary>
        internal Action FenceRaiseBegan;

        /// <summary>Raised on the machine of the player who pulled the lever. Route it to the server.</summary>
        internal Action<PlayerControllerB> LeverPulled;

        internal void Initialize(
            Transform fence,
            LineRenderer[] ropes,
            Transform[] ropeAnchors,
            List<Collider> sealColliders,
            List<NavMeshObstacle> sealObstacles,
            InteractTrigger trigger,
            float restBottom,
            Transform leverArm = null)
        {
            _restBottom = restBottom;
            _fence = fence;
            _ropes = ropes;
            _ropeAnchors = ropeAnchors;
            _sealColliders = sealColliders;
            _sealObstacles = sealObstacles;
            _trigger = trigger;
            _leverArm = leverArm;
            _swaySeed = UnityEngine.Random.value * 10f;
            State = TrapMotionState.Rest;
            SetLeverPull(0f);

            if (_trigger != null)
                _trigger.onInteract.AddListener(OnLeverInteract);
        }


        /// <summary>
        /// Registers the world-rooted lamp fixture. It deliberately does not inherit cage motion,
        /// but the preview owns its lifetime so a despawn cannot orphan ceiling fixtures.
        ///
        /// The lamps are room lighting and nothing else. An earlier revision also drove them, and
        /// every renderer carrying a Trap_LED or Trap_StatusLight material, from a per-state colour
        /// palette; see <see cref="SetSealed"/> for why that is gone.
        /// </summary>
        internal void RegisterLighting(Transform externalFixtureRoot)
        {
            _externalFixtureRoot = externalFixtureRoot;
        }

        private void OnDestroy()
        {
            if (_trigger != null)
                _trigger.onInteract.RemoveListener(OnLeverInteract);
            if (_externalFixtureRoot != null)
                Destroy(_externalFixtureRoot.gameObject);
            _externalFixtureRoot = null;
        }
        /// <summary>
        /// Poses the lever arm. 0 is armed (authored upright), 1 is fully thrown
        /// (<see cref="PestControlTrapAssembler.LeverPullDegrees"/> toward the player).
        ///
        /// This is deliberately a plain setter driven from OUTSIDE rather than its own tween:
        /// the arm has to track the player's pull animation frame for frame, so whoever owns that
        /// animation feeds its normalized time straight in. Nothing here assumes a duration.
        /// </summary>
        internal void SetLeverPull(float pull)
        {
            LeverPull = Mathf.Clamp01(pull);
            if (_leverArm != null)
            {
                _leverArm.localRotation = Quaternion.Euler(
                    PestControlTrapAssembler.LeverPullDegrees * LeverPull, 0f, 0f);
            }
        }

        /// <summary>Current lever throw, 0 armed .. 1 released.</summary>
        internal float LeverPull { get; private set; }

        /// <summary>World-space grip point used to correct small player/lever alignment errors.</summary>
        internal Vector3 LeverGripPosition =>
            _leverArm != null ? _leverArm.position + _leverArm.up * 0.28f : LeverPosition + Vector3.up * 1.2f;

        /// <summary>Where the operator is planted for the throw. Null on a collider-free preview.</summary>
        internal Transform LeverStandNode => _trigger != null ? _trigger.playerPositionNode : null;

        internal void BeginLeverVisual(bool pushingDown, float elapsedSeconds)
        {
            if (_leverMotion != null)
                StopCoroutine(_leverMotion);

            elapsedSeconds = Mathf.Max(0f, elapsedSeconds);
            if (elapsedSeconds >= LeverAnimationSeconds)
            {
                SetLeverPull(pushingDown ? 1f : 0f);
                _leverMotion = null;
                return;
            }

            _leverMotion = StartCoroutine(LeverVisualRoutine(pushingDown, elapsedSeconds));
        }

        private IEnumerator LeverVisualRoutine(bool pushingDown, float elapsedSeconds)
        {
            float from = pushingDown ? 0f : 1f;
            float to = pushingDown ? 1f : 0f;
            const float contact = LeverContactSeconds;
            while (elapsedSeconds < LeverAnimationSeconds)
            {
                float contactT = Mathf.Clamp01(elapsedSeconds / Mathf.Max(0.001f, contact));
                contactT = contactT * contactT * (3f - 2f * contactT);
                SetLeverPull(Mathf.Lerp(from, to, contactT));
                elapsedSeconds += Time.deltaTime;
                yield return null;
            }

            SetLeverPull(to);
            _leverMotion = null;
        }

        private void OnLeverInteract(PlayerControllerB player)
        {
            LeverPulled?.Invoke(player);
        }

        /// <summary>Hover text while the authored art is still being instantiated (#716).</summary>
        private const string AssemblingTip = "[ Assembling rig... ]";

        private bool _assemblyPending;
        private string _requestedTip = "Drop cage : [LMB]";
        private bool _requestedInteractable = true;

        /// <summary>
        /// #716. The rig's shape is final the moment the build returns, but its meshes arrive over
        /// the next few frames. The lever is held shut for that window so nobody drops a cage whose
        /// panels are still appearing; the tip the controller asked for in the meantime is
        /// remembered and applied when the last part lands.
        /// </summary>
        internal void BeginDeferredAssembly()
        {
            _assemblyPending = true;
            ApplyLeverTrigger(AssemblingTip, false);
        }

        internal void CompleteDeferredAssembly()
        {
            if (!_assemblyPending)
                return;

            _assemblyPending = false;
            ApplyLeverTrigger(_requestedTip, _requestedInteractable);
        }

        internal void SetLeverHoverTip(string tip, bool interactable)
        {
            _requestedTip = tip;
            _requestedInteractable = interactable;
            if (_assemblyPending)
                return;

            ApplyLeverTrigger(tip, interactable);
        }

        private void ApplyLeverTrigger(string tip, bool interactable)
        {
            if (_trigger == null)
                return;

            _trigger.interactable = interactable;
            _trigger.hoverTip = tip;
            _trigger.disabledHoverTip = tip;
            PestControlTrapAssembler.ApplyLeverHoverIcon(_trigger);
        }

        internal bool TryDrop(out string reason)
        {
            if (_fence == null)
            {
                reason = "fence assembly missing";
                return false;
            }

            if (State == TrapMotionState.Dropped || State == TrapMotionState.Dropping)
            {
                reason = "already dropped";
                return false;
            }

            StartMotion(DropRoutine());
            reason = string.Empty;
            return true;
        }

        internal bool TryRaise(out string reason)
        {
            if (_fence == null)
            {
                reason = "fence assembly missing";
                return false;
            }

            if (State == TrapMotionState.Rest || State == TrapMotionState.Raising)
            {
                reason = "already raised";
                return false;
            }

            StartMotion(RaiseRoutine());
            reason = string.Empty;
            return true;
        }

        /// <summary>Instant, silent state application — for a client that joined after the trap already moved.</summary>
        internal void SnapTo(TrapMotionState state)
        {
            if (_fence == null)
                return;

            if (_motion != null)
            {
                StopCoroutine(_motion);
                _motion = null;
            }

            if (_leverMotion != null)
            {
                StopCoroutine(_leverMotion);
                _leverMotion = null;
            }

            bool down = state == TrapMotionState.Dropped || state == TrapMotionState.Dropping;
            SetFenceHeight(down ? PestControlTrapAssembler.FenceDroppedBottom : _restBottom);
            _fence.localRotation = Quaternion.identity;
            State = down ? TrapMotionState.Dropped : TrapMotionState.Rest;
            SetSealed(down);
            SetLeverPull(down ? 1f : 0f);
        }

        /// <summary>
        /// Arms or disarms the physical cage: solid side colliders plus carving NavMesh obstacles.
        ///
        /// This used to drive a per-state colour palette as well — blue at rest, orange in motion,
        /// red when dropped — applied two ways, either of which alone tinted the whole trap:
        ///
        /// 1. The four ceiling spots were recoloured. They are authored warm white in
        ///    <c>BuildAuthoredCeilingLights</c> and they are the trap's ONLY light source, so
        ///    repainting them repainted every surface they reached, posts and floor included.
        /// 2. Every renderer holding a Trap_LED or Trap_StatusLight material was driven through
        ///    <c>SetPropertyBlock</c> with no material index, which applies to all of a renderer's
        ///    submeshes. The post carries its LED channel alongside its body material, so the whole
        ///    post glowed rather than the lens.
        ///
        /// Both are removed. The lamps keep their authored warm white and the LED lenses keep the
        /// authored cold blue-white the art kit gives them; nothing about the trap changes colour
        /// with state any more.
        /// </summary>
        internal void SetSealed(bool value)
        {
            IsSealed = value;

            if (_sealColliders != null)
            {
                for (int i = 0; i < _sealColliders.Count; i++)
                {
                    if (_sealColliders[i] != null)
                        _sealColliders[i].enabled = value;
                }
            }

            if (_sealObstacles == null)
                return;

            for (int i = 0; i < _sealObstacles.Count; i++)
            {
                if (_sealObstacles[i] != null)
                    _sealObstacles[i].enabled = value;
            }
        }

        private void StartMotion(IEnumerator routine)
        {
            if (_motion != null)
                StopCoroutine(_motion);

            _motion = StartCoroutine(routine);
        }

        private IEnumerator DropRoutine()
        {
            State = TrapMotionState.Dropping;

            // The ropes let go when the handle bottoms out, not when the operator first touches it.
            // Without this the cage, the lever and the player animation all started together at the
            // interact frame, so the trap had already slammed while the arm was still reaching.
            // Capture resolves at the landing, so this also gives the specimen the extra beat the
            // throw visibly buys it.
            yield return new WaitForSeconds(LeverContactSeconds);

            float from = _fence.localPosition.y;
            float elapsed = 0f;
            while (elapsed < DropSeconds)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / DropSeconds);
                SetFenceHeight(Mathf.Lerp(from, PestControlTrapAssembler.FenceDroppedBottom, t * t));
                yield return null;
            }

            SetFenceHeight(PestControlTrapAssembler.FenceDroppedBottom);
            _fence.localRotation = Quaternion.identity;
            State = TrapMotionState.Dropped;

            // Seal and notify at the LANDING moment, before the cosmetic bounce — the capture
            // check and the slam both belong to the instant the cage meets the platform.
            SetSealed(true);
            FenceLanded?.Invoke();

            elapsed = 0f;
            while (elapsed < BounceUpSeconds)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / BounceUpSeconds);
                SetFenceHeight(PestControlTrapAssembler.FenceDroppedBottom + BounceHeight * Mathf.Sin(t * Mathf.PI * 0.5f));
                yield return null;
            }

            elapsed = 0f;
            while (elapsed < BounceDownSeconds)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / BounceDownSeconds);
                SetFenceHeight(Mathf.Lerp(PestControlTrapAssembler.FenceDroppedBottom + BounceHeight, PestControlTrapAssembler.FenceDroppedBottom, t * t));
                yield return null;
            }

            SetFenceHeight(PestControlTrapAssembler.FenceDroppedBottom);
            _motion = null;
        }

        private IEnumerator RaiseRoutine()
        {
            State = TrapMotionState.Raising;

            // Same linkage as the drop: the winch engages when the handle completes its travel.
            yield return new WaitForSeconds(LeverContactSeconds);

            FenceRaiseBegan?.Invoke();
            SetSealed(false);
            float from = _fence.localPosition.y;
            float elapsed = 0f;
            while (elapsed < RaiseSeconds)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / RaiseSeconds);

                // Continuous, eased at both ends: the drum spins up, pulls, and settles. This used
                // to quantize t to eighteen notches and blend only 35% of the smooth value back,
                // meant to read as a winch pawl. It snapped ~3.6% of the travel instantly at every
                // notch and read as a stutter instead.
                SetFenceHeight(Mathf.Lerp(from, _restBottom, Mathf.SmoothStep(0f, 1f, t)));
                yield return null;
            }

            SetFenceHeight(_restBottom);
            State = TrapMotionState.Rest;
            _motion = null;
            FenceRaised?.Invoke();
        }

        private void SetFenceHeight(float height)
        {
            Vector3 local = _fence.localPosition;
            local.y = height;
            _fence.localPosition = local;
            UpdateRopes();
        }

        private void UpdateRopes()
        {
            if (_ropes == null || _ropeAnchors == null)
                return;

            for (int i = 0; i < _ropes.Length; i++)
            {
                LineRenderer rope = _ropes[i];
                if (rope == null || i >= _ropeAnchors.Length || _ropeAnchors[i] == null)
                    continue;

                rope.SetPosition(1, _ropeAnchors[i].position);
            }
        }

        private void Update()
        {
            if (_fence == null)
                return;

            if (State == TrapMotionState.Rest)
            {
                float t = Time.time + _swaySeed;
                _fence.localRotation = Quaternion.Euler(
                    Mathf.Sin(t * 0.9f) * SwayDegrees,
                    0f,
                    Mathf.Cos(t * 0.7f) * SwayDegrees * 0.7f);
                UpdateRopes();
            }
        }

    }
}
