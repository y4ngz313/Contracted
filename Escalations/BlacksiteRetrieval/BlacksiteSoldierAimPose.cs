using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #315: lives on the soldier's Animator object (the only place Unity delivers
    /// <c>OnAnimatorIK</c>) and turns the replicated aim state into three things every peer
    /// renders the same way: the weight of the controller's upper-body "Aim" layer, a head
    /// look-at on the aim point, and a clamped pitch of the spine chain toward it. The soldier
    /// drives it once per frame from <c>UpdateAnimation</c>; nothing here reads gameplay state.
    /// </summary>
    internal sealed class BlacksiteSoldierAimPose : MonoBehaviour
    {
        private static readonly int[] OverlayStateHashes = BuildOverlayStateHashes();
        private static readonly int[] FirePoseHashes = BuildFirePoseHashes();

        private Animator _animator;
        private Transform _root;
        private Transform _eye;
        private Transform _spine;
        private Transform _chest;
        private Transform _upperChest;
        private Transform _head;
        private int _aimLayer = -1;
        private int _carryLayer = -1;
        private float _carryLayerWeight = 1f;
        private float _layerWeight;
        private float _poseWeight;
        private float _pitch;
        private bool _engaged;
        private bool _hasAimPoint;
        private Vector3 _aimPoint;
        private int _ikFrame = -1;
        private bool _configured;
        private RuntimeAnimatorController _configuredController;
        private BlacksiteRiflePose _rifle;
        private Transform _rifleModel;
        private bool _releaseSupport;
        private bool _handsOffWeapon;
        private bool _carryingCargo;

        /// <summary>
        /// #874 S3.2: the two facts the solver and the carry layer need, kept apart because they are not the same
        /// question. <paramref name="handsOffWeapon"/> is any hand owner but Weapon - cargo, releasing, a climb or clip
        /// jump, incapacitated - and stands the rifle solve down; <paramref name="carryingCargo"/> is the
        /// narrower predicate the controller's own "Carrying" bool uses, and it is what raises the carry layer.
        /// </summary>
        internal void SetHandState(bool handsOffWeapon, bool carryingCargo)
        { _handsOffWeapon = handsOffWeapon; _carryingCargo = carryingCargo; }

        private float _lastShotAt = float.NegativeInfinity;

        internal void BindRifle(Transform socket, Transform model)
        {
            if (model == _rifleModel || model == null || socket == null || _animator == null || !_animator.isHuman) return;
            _rifleModel = model;
            _rifle = new BlacksiteRiflePose(_animator, _root, socket, model);
        }

        internal void PrepareEvaluation() { _rifle?.RestoreSocket(); }
        internal void PresentShot() { _lastShotAt = Time.time; }
        internal float SupportGripError => _rifle?.SupportError ?? 0f;
        internal bool IsReadyToFire => FireReadiness == BlacksiteFireReadiness.Ready;
        internal BlacksiteFireReadiness FireReadiness
        {
            get
            {
                if (_animator == null) return BlacksiteFireReadiness.AnimatorMissing;
                if (!_engaged) return BlacksiteFireReadiness.PoseNotEngaged;
                if (_poseWeight < .95f) return BlacksiteFireReadiness.PoseBlending;
                if (_releaseSupport) return BlacksiteFireReadiness.HandsBusy;
                if (SupportGripError > .08f) return BlacksiteFireReadiness.GripMisaligned;
                var state = _animator.GetCurrentAnimatorStateInfo(0);
                bool transitioning = _animator.IsInTransition(0);
                if (!IsSupportedFirePose(state, transitioning)) return BlacksiteFireReadiness.AnimationState;
                // A crossfade between aimed locomotion and recoil must not cancel an aligned
                // burst. Non-aimed actions still veto fire, along with live grip/muzzle checks.
                if (transitioning && !IsSupportedFirePose(_animator.GetNextAnimatorStateInfo(0), true))
                    return BlacksiteFireReadiness.AnimatorTransition;
                return BlacksiteFireReadiness.Ready;
            }
        }

        // #874 Q7: the rule is BlacksiteAimPosePolicy.AcceptsFirePose; this only resolves the state.
        private bool IsSupportedFirePose(AnimatorStateInfo state, bool inTransition)
        {
            var kind = BlacksiteFirePoseKind.Unsupported;
            for (int i = 0; i < FirePoseHashes.Length; i++)
                if (FirePoseHashes[i] == state.shortNameHash) { kind = BlacksiteAimPosePolicy.FirePoseStates[i].Kind; break; }
            return BlacksiteAimPosePolicy.AcceptsFirePose(kind, state.normalizedTime, inTransition,
                _aimLayer >= 0 && _layerWeight >= .95f);
        }

        internal string FireReadinessDetails => $"poseWeight={_poseWeight:0.00}, gripError={SupportGripError:0.000}, animatorState={(_animator != null ? _animator.GetCurrentAnimatorStateInfo(0).shortNameHash : 0)}";

        internal int AimLayerIndex => _aimLayer;
        internal float CarryLayerWeight => _carryLayerWeight;
        internal bool IkPassObserved => _ikFrame >= 0;
        internal float LayerWeight => _layerWeight;
        internal float PitchDegrees => _pitch;

        internal void Configure(Animator animator, Transform root, Transform eye)
        {
            if (animator != null && _configuredController != animator.runtimeAnimatorController) ResetController();
            _animator = animator;
            _configuredController = animator != null ? animator.runtimeAnimatorController : null;
            _root = root;
            _eye = eye;
            _spine = SafeBone(animator, HumanBodyBones.Spine);
            _chest = SafeBone(animator, HumanBodyBones.Chest);
            _upperChest = SafeBone(animator, HumanBodyBones.UpperChest);
            _head = SafeBone(animator, HumanBodyBones.Head);
            _aimLayer = ResolveAimLayer(animator);
            // #874 S3.2: the carry layer is authored at default weight 1 with no runtime writer, so
            // it overrode both arms, the wrists and the fingers in every state and froze whatever
            // pose it last produced. It starts from its authored weight here and is blended from
            // the hand owner every frame below.
            _carryLayer = ResolveLayer(animator, BlacksiteAimPosePolicy.CarryLayerName);
            _carryLayerWeight = _carryLayer >= 0 && _carryLayer < animator.layerCount
                ? animator.GetLayerWeight(_carryLayer) : 0f;
            _configured = animator != null && root != null;
        }

        internal void ResetController()
        {
            _rifle?.RestoreSocket(); _rifle = null; _rifleModel = null;
            _ikFrame = -1; _aimLayer = _carryLayer = -1; _poseWeight = _layerWeight = _pitch = 0f;
            _carryLayerWeight = 1f;
            _engaged = _hasAimPoint = _handsOffWeapon = _carryingCargo = false; _configuredController = null;
        }

        /// <summary>#1283 S3b D1: a baton member bound to the rifle pair for a ladder holds the rungs, so the overlay and the
        /// carry layer start at 0 instead of fading out from the weights the Rebind restored (Carry is authored at 1).</summary>
        internal void ReleaseLayers()
        {
            _layerWeight = _poseWeight = _carryLayerWeight = 0f;
            if (_aimLayer >= 0 && _aimLayer < _animator.layerCount) _animator.SetLayerWeight(_aimLayer, 0f);
            if (_carryLayer >= 0 && _carryLayer < _animator.layerCount) _animator.SetLayerWeight(_carryLayer, 0f);
        }

        internal static int ResolveAimLayer(Animator animator)
            => ResolveLayer(animator, BlacksiteAimPosePolicy.AimLayerName);

        /// <summary>By name, never by index: the two soldier controllers order their layers
        /// differently and an older bundle may not carry one at all, which is the -1 case.</summary>
        internal static int ResolveLayer(Animator animator, string layerName)
        {
            if (animator == null || animator.runtimeAnimatorController == null)
                return -1;
            try
            {
                return animator.GetLayerIndex(layerName);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// One call per rendered frame. <paramref name="engaged"/> is the replicated aiming flag
        /// with death already folded in; <paramref name="aimPoint"/> is the replicated target
        /// point (or the live one on the server).
        /// </summary>
        internal void Drive(bool engaged, bool hasAimPoint, Vector3 aimPoint, float deltaSeconds, bool releaseSupport = false)
        {
            if (!_configured || _animator == null)
                return;

            if (_configuredController != _animator.runtimeAnimatorController) Configure(_animator, _root, _eye);
            _releaseSupport = releaseSupport;
            _hasAimPoint = hasAimPoint;
            _aimPoint = aimPoint;
            _engaged = engaged && hasAimPoint;

            bool inOverlayState = IsInOverlayState();
            _layerWeight = BlacksiteAimPosePolicy.StepWeight(_layerWeight, _engaged && inOverlayState, deltaSeconds);
            _poseWeight = BlacksiteAimPosePolicy.StepWeight(_poseWeight, _engaged, deltaSeconds);

            float targetPitch = 0f;
            if (_engaged)
            {
                Vector3 origin = _eye != null ? _eye.position : _root.position + Vector3.up * 2.1f;
                Vector3 offset = _aimPoint - origin;
                float horizontal = new Vector2(offset.x, offset.z).magnitude;
                targetPitch = BlacksiteAimPosePolicy.ComputePitchDegrees(offset.y, horizontal);
            }
            _pitch = BlacksiteAimPosePolicy.SmoothPitch(_pitch, targetPitch, deltaSeconds);

            if (_aimLayer >= 0 && _aimLayer < _animator.layerCount)
                _animator.SetLayerWeight(_aimLayer, _layerWeight);

            // The carry layer is only wanted while the hands actually hold cargo. Everywhere else
            // it has to be off, or the arms, wrists and fingers it masks never reach the skeleton.
            _carryLayerWeight = BlacksiteAimPosePolicy.StepCarryWeight(_carryLayerWeight, _carryingCargo, deltaSeconds);
            if (_carryLayer >= 0 && _carryLayer < _animator.layerCount)
                _animator.SetLayerWeight(_carryLayer, _carryLayerWeight);
        }

        private bool IsInOverlayState()
        {
            int hash;
            try
            {
                hash = _animator.IsInTransition(0)
                    ? _animator.GetNextAnimatorStateInfo(0).shortNameHash
                    : _animator.GetCurrentAnimatorStateInfo(0).shortNameHash;
            }
            catch
            {
                return false;
            }

            for (int i = 0; i < OverlayStateHashes.Length; i++)
            {
                if (OverlayStateHashes[i] == hash)
                    return true;
            }
            return false;
        }

        private void OnAnimatorIK(int layerIndex)
        {
            using var hitch = BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierAimPose);
            if (layerIndex != 0 || _animator == null)
                return;

            // The IK pass only runs on frames the animator evaluates and writes transforms;
            // LateUpdate keys off this so a culled animator is never pitched twice.
            _ikFrame = Time.frameCount;
            _rifle?.RestoreSocket();

            float weight = _hasAimPoint ? _poseWeight : 0f;
            if (weight <= 0.001f)
            {
                _animator.SetLookAtWeight(0f);
                return;
            }

            _animator.SetLookAtWeight(
                weight,
                BlacksiteAimPosePolicy.LookBodyWeight,
                BlacksiteAimPosePolicy.LookHeadWeight,
                BlacksiteAimPosePolicy.LookEyesWeight,
                BlacksiteAimPosePolicy.LookClampWeight);
            _animator.SetLookAtPosition(_aimPoint);
        }

        private void LateUpdate()
        {
            using var hitch = BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SoldierAimPose);
            if (!_configured || _ikFrame != Time.frameCount || _root == null)
                return;

            EvaluatePose(Time.deltaTime);
        }

        internal void EvaluatePose(float deltaSeconds)
        {
            // #874 S3.2: the solve always runs, so the support weight blends off and back through carry, climb,
            // boarding and death rather than teleporting the hand, and writes no arm once it is 0. A soldier whose
            // hands are elsewhere does not pitch his chest either.
            if (!_handsOffWeapon) ApplyTorsoPitch();
            float recoil = 1.8f * Mathf.Clamp01(1f - (Time.time - _lastShotAt) / .12f);
            _rifle?.Apply(_engaged, _aimPoint, !_handsOffWeapon, _releaseSupport, deltaSeconds, recoil);
        }

        private void ApplyTorsoPitch()
        {
            float pitch = _pitch * _poseWeight;
            if (Mathf.Abs(pitch) < 0.01f)
                return;

            float available = 0f;
            if (_spine != null) available += BlacksiteAimPosePolicy.SpinePitchShare;
            if (_chest != null) available += BlacksiteAimPosePolicy.ChestPitchShare;
            if (_upperChest != null) available += BlacksiteAimPosePolicy.UpperChestPitchShare;
            if (available <= 0f)
                return;

            // Positive Unity rotation about the right axis pitches the nose down, so aiming up
            // is a negative angle about it.
            Vector3 axis = _root.right;
            float applied = 0f;
            applied += ApplyPitch(_spine, pitch * BlacksiteAimPosePolicy.SpinePitchShare / available, axis);
            applied += ApplyPitch(_chest, pitch * BlacksiteAimPosePolicy.ChestPitchShare / available, axis);
            applied += ApplyPitch(_upperChest, pitch * BlacksiteAimPosePolicy.UpperChestPitchShare / available, axis);

            // The head inherited the whole chain pitch; undo it so the look-at the IK pass
            // solved a moment ago still points at the target.
            if (_head != null && Mathf.Abs(applied) > 0.001f)
                _head.rotation = Quaternion.AngleAxis(applied, axis) * _head.rotation;
        }

        private static float ApplyPitch(Transform bone, float degrees, Vector3 axis)
        {
            if (bone == null || Mathf.Abs(degrees) < 0.001f)
                return 0f;
            bone.rotation = Quaternion.AngleAxis(-degrees, axis) * bone.rotation;
            return degrees;
        }

        private static Transform SafeBone(Animator animator, HumanBodyBones bone)
        {
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                return null;
            try
            {
                return animator.GetBoneTransform(bone);
            }
            catch
            {
                return null;
            }
        }

        private static int[] BuildOverlayStateHashes()
        {
            string[] names = BlacksiteAimPosePolicy.OverlayStateNames;
            var hashes = new int[names.Length];
            for (int i = 0; i < names.Length; i++)
                hashes[i] = Animator.StringToHash(names[i]);
            return hashes;
        }

        private static int[] BuildFirePoseHashes()
        {
            var states = BlacksiteAimPosePolicy.FirePoseStates;
            var hashes = new int[states.Length];
            for (int i = 0; i < states.Length; i++)
                hashes[i] = Animator.StringToHash(states[i].Name);
            return hashes;
        }
    }
}
