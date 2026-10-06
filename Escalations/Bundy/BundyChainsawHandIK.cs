using System;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Escalations.Bundy;

public class BundyChainsawHandIK : MonoBehaviour
{
    public static bool DiagnosticDisableAllIK = false;

    public Animator animator;
    public Transform rightHandTarget;
    public Transform chainsawProp;
    public bool attackGripDiagnostics = true;
    public bool disableBasicAttackIK = false;
    [Range(0f, 1f)] public float rightHandPositionWeight = 0f;
    [Range(0f, 1f)] public float rightHandRotationWeight = 0f;
    [Range(0f, 1f)] public float basicAttackPositionWeight = 0f;
    [Range(0f, 1f)] public float basicAttackRotationWeight = 0f;
    [Range(0f, 1f)] public float basicAttackPeakPositionWeight = 0.72f;
    [Range(0f, 1f)] public float basicAttackPeakRotationWeight = 0.45f;
    public float basicAttackBlendSeconds = 0.12f;
    // Legacy setup values retained because the runtime prefab configurator still
    // assigns/logs them. The generic LateUpdate head aim intentionally ignores
    // both; its live controls come from BundyConfig.
    [HideInInspector] public float lookAtHeadWeight = 0.85f;
    [HideInInspector] public float lookAtBodyWeight = 0f;
    public bool stateGatedIK = true;
    public bool directBoneFallback = false;
    public float maxIkReach = 0.75f;
    [Header("Canonical generic rig")]
    public bool genericTwoBoneGrip = false;
    public bool overrideAnimatedRightHandTarget = false;
    public Vector3 rightHandTargetLocalAnchor = new Vector3(0.0689f, 0.1238f, -0.2321f);

    private const int IkCategoryNone = 0;
    private const int IkCategoryBlocked = 1;
    private const int IkCategoryLocomotion = 2;
    private const int IkCategoryBasicAttack = 3;

    // Measured on the canonical rig at rest (BodyCant probe, 2026-07-16).
    private static readonly Vector3 HeadLocalLookAxis =
        new Vector3(0.02f, -1f, 0f).normalized;

    private bool _warnedMissingSetup;
    private bool _loggedSetup;
    private bool _loggedIkCallback;
    private bool _loggedDirectFallbackDisabled;
    private Transform _rightHandBone;
    private Transform _leftHandBone;
    private Transform _rightClavicleBone;
    private Transform _rightUpperArmBone;
    private Transform _rightLowerArmBone;
    private Transform _leftClavicleBone;
    private Transform _leftUpperArmBone;
    private bool _loggedGenericSolver;
    private bool _loggedClavicleProtraction;
    private bool _loggedElbowsOut;

    // Generic LateUpdate facing/head-aim state.
    private BundyAIBase _ai;
    private UnityEngine.AI.NavMeshAgent _agent;
    private Transform _pelvisBone;
    private bool _pelvisBoneResolved;
    private Transform[] _spineBones;
    private bool _spineBonesResolved;
    private float _currentFacingYawOffsetDegrees;
    // R33: natural torso twist puts more of the rotation into the upper spine.
    // Order matches _spineBones: spine_01, spine_02, spine_03.
    private static readonly float[] SpineYawDistribution = { 0.25f, 0.35f, 0.40f };
    private bool _lastFacingOffsetActive;
    private string _lastFacingOffsetReason = string.Empty;
    private float _nextFacingOffsetLogTime;
    private Transform _neckBone;
    private Transform _headBone;
    private bool _headBonesResolved;
    private Quaternion _smoothedHeadAimLocalCorrection = Quaternion.identity;
    private float _headAimBlend = 1f;
    private bool _lastLookActive;
    private string _lastLookReason = string.Empty;
    private float _nextLookLogTime;
    private const float HeadAimGateSmoothingSeconds = 0.2f;
    // #319: the yaw clamp used to PIN the head at the rail, so a target that walked
    // behind Bundy left him staring sideways, and the signed yaw flipping ±180° at
    // the rear made the pin jump rail-to-rail in one frame ("owl neck"). Fading the
    // correction out over the last 20° before the clamp means the head is already at
    // weight 0 (authored pose) by the time the sign can flip, so the flip is invisible.
    // The band is wide enough to double as the hysteresis against edge flicker.
    private const float HeadAimYawFadeDegrees = 20f;
    private const float HeadAimMovementSpeedThreshold = 0.1f;

    // IK diagnostic logging state.
    //
    // Job 1: rig/IK diagnostics belong to the Anim workstream at the per-tick tier. Every site below
    // used to guard on the DebugLogs master alone, so LogVerbosity and LogAnim had no effect on them —
    // the same bypass that let DirLoco put 4,095 lines into run 7.
    private static bool RigDiagnosticsEnabled => BundyLogGate.Allows(BundyLogCategory.Anim, 2);
    private int _lastLoggedCategory = -2;
    // Frame-count cache for ResolveCurrentIkCategory (#276): the category is queried 5-6x per
    // frame (OnAnimatorIK + the LateUpdate offsets) and GetCurrentAnimatorClipInfo allocates.
    // Same pattern as BundyAIBase.Audio.cs GetBaseLayerClip.
    private int _ikCategoryFrame = -1;
    private int _ikCategoryCached = IkCategoryNone;
    private string _ikCategoryClipName = "<none>";
    private float _lastLoggedPosWeight = -1f;
    private float _lastLoggedRotWeight = -1f;
    private float _nextThrottleTime;
    private const float IkLogThrottleSeconds = 0.25f;
    private bool _loggedEntry;
    private bool _loggedDisabled;
    private bool _loggedMissingSetup;
    private float _nextReachRejectLogAt;
    private float _nextGripDiagnosticLogAt;
    private float _basicAttackIkBlend;
    private const float GripDiagnosticLogSeconds = 0.15f;

    private void Reset()
    {
        animator = GetComponent<Animator>();
    }

    private void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (rightHandTarget == null)
            rightHandTarget = FindChildRecursive(transform, "BundyChainsaw_RightHandIKTarget");
        ResolveChainsawProp();
        ResolveRightHandBone();
        ResolveLeftHandBone();
        ResolveHeadBones();
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (RigDiagnosticsEnabled && !_loggedEntry)
        {
            _loggedEntry = true;
            string ctrl = animator != null && animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "<null>";
            string avatar = animator != null && animator.avatar != null ? animator.avatar.name : "<null>";
            LogIkInfo($"[BundyIK] OnAnimatorIK_ENTERED layer={layerIndex}, animator={animator != null}, ctrl={ctrl}, avatar={avatar}, human={animator?.avatar?.isHuman}");
        }

        if (DiagnosticDisableAllIK)
        {
            if (!_loggedDisabled)
            {
                _loggedDisabled = true;
                LogIkWarning("[BundyIK] EARLY_EXIT: DiagnosticDisableAllIK=true");
            }
            return;
        }

        if (animator == null || rightHandTarget == null || animator.avatar == null || !animator.avatar.isHuman)
        {
            if (!_loggedMissingSetup)
            {
                _loggedMissingSetup = true;
                string details = $"animator={animator != null}, target={rightHandTarget != null}, avatar={animator?.avatar != null}, human={animator?.avatar?.isHuman}";
                LogIkWarning($"[BundyIK] EARLY_EXIT: missing setup; {details}");
            }
            return;
        }

        ResolveRightHandBone();
        ResolveLeftHandBone();
        ResolveChainsawProp();

        string clipName;
        int category = ResolveCurrentIkCategory(out clipName);

        float positionWeight = 0f;
        float rotationWeight = 0f;
        UpdateBasicAttackBlend(category);

        if (!stateGatedIK)
        {
            positionWeight = rightHandPositionWeight;
            rotationWeight = rightHandRotationWeight;
        }
        else if (category == IkCategoryLocomotion)
        {
            positionWeight = 0f;
            rotationWeight = 0f;
        }
        else if (category == IkCategoryBasicAttack)
        {
            positionWeight = Mathf.Lerp(
                Mathf.Clamp01(basicAttackPositionWeight),
                Mathf.Clamp01(basicAttackPeakPositionWeight),
                _basicAttackIkBlend);
            rotationWeight = Mathf.Lerp(
                Mathf.Clamp01(basicAttackRotationWeight),
                Mathf.Clamp01(basicAttackPeakRotationWeight),
                _basicAttackIkBlend);
        }

        bool attackIkSuppressed = category == IkCategoryBasicAttack && disableBasicAttackIK;
        if (attackIkSuppressed)
        {
            positionWeight = 0f;
            rotationWeight = 0f;
        }

        animator.SetIKPositionWeight(AvatarIKGoal.RightHand, positionWeight);
        animator.SetIKRotationWeight(AvatarIKGoal.RightHand, rotationWeight);
        MaybeLogAttackGripDiagnostic(category, clipName, positionWeight, rotationWeight, attackIkSuppressed);

        bool debugLogs = RigDiagnosticsEnabled;
        bool shouldLog = false;
        string logReason = "";

        if (debugLogs && category != _lastLoggedCategory)
        {
            shouldLog = true;
            logReason = "category_change";
        }
        else if (debugLogs &&
                 (Mathf.Abs(positionWeight - _lastLoggedPosWeight) > 0.001f ||
                  Mathf.Abs(rotationWeight - _lastLoggedRotWeight) > 0.001f))
        {
            shouldLog = true;
            logReason = "weight_change";
        }
        else if (debugLogs && (positionWeight > 0f || rotationWeight > 0f))
        {
            if (Time.time >= _nextThrottleTime)
            {
                shouldLog = true;
                logReason = "throttle";
            }
        }

        if (positionWeight <= 0f && rotationWeight <= 0f)
        {
            if (shouldLog)
            {
                LogIkState(category, clipName, positionWeight, rotationWeight, logReason, earlyExit: true);
                _lastLoggedCategory = category;
                _lastLoggedPosWeight = positionWeight;
                _lastLoggedRotWeight = rotationWeight;
                _nextThrottleTime = Time.time + IkLogThrottleSeconds;
            }
            return;
        }

        if (!IsRightHandTargetReachable(category, clipName, positionWeight, rotationWeight))
        {
            _lastLoggedCategory = category;
            _lastLoggedPosWeight = 0f;
            _lastLoggedRotWeight = 0f;
            _nextThrottleTime = Time.time + IkLogThrottleSeconds;
            return;
        }

        animator.SetIKPosition(AvatarIKGoal.RightHand, rightHandTarget.position);
        animator.SetIKRotation(AvatarIKGoal.RightHand, rightHandTarget.rotation);

        if (shouldLog)
        {
            LogIkState(category, clipName, positionWeight, rotationWeight, logReason, earlyExit: false);
            _lastLoggedCategory = category;
            _lastLoggedPosWeight = positionWeight;
            _lastLoggedRotWeight = rotationWeight;
            _nextThrottleTime = Time.time + IkLogThrottleSeconds;
        }

        if (debugLogs && !_loggedIkCallback)
        {
            _loggedIkCallback = true;
            LogIkInfo(
                $"[Bundy] Chainsaw right-hand Animator IK callback is active: " +
                $"stateGated={stateGatedIK}, category={CategoryName(category)}, clip='{clipName}', " +
                $"positionWeight={positionWeight:F2}, rotationWeight={rotationWeight:F2}.");
        }
    }

    private void ResolveAiAndAgent()
    {
        if (_ai == null)
            _ai = GetComponentInParent<BundyAIBase>();
        if (_agent == null && _ai != null)
            _agent = _ai.GetComponent<UnityEngine.AI.NavMeshAgent>();
    }

    private void ApplyFacingYawOffset()
    {
        ResolvePelvisBone();
        ResolveAiAndAgent();

        string clipName;
        int category = ResolveCurrentIkCategory(out clipName);
        bool enabled = BundyPlugin.ModConfig?.ChainsawFacingYawOffsetEnabled?.Value ?? true;
        bool squaredLocomotion =
            BundyPlugin.ModConfig?.ChainsawSquaredLocomotionClips?.Value ?? true;
        bool playableActive = false;
        try { playableActive = _ai != null && BundyAttackerPlayableController.IsActive(_ai); }
        catch { playableActive = false; }

        Vector3 horizontalVelocity = _agent != null && _agent.enabled
            ? _agent.velocity
            : Vector3.zero;
        horizontalVelocity.y = 0f;
        float horizontalSpeed = horizontalVelocity.magnitude;

        float targetOffsetDegrees = 0f;
        string reason;
        if (!enabled)
        {
            reason = "config-disabled";
        }
        else if (playableActive)
        {
            reason = "playable-active";
        }
        else if (_pelvisBone == null)
        {
            reason = "pelvis-missing";
        }
        else if (category != IkCategoryLocomotion)
        {
            reason = "non-locomotion";
        }
        else if (squaredLocomotion)
        {
            // R39b: Idle/Walk/Run/Turn are squared offline in the approved
            // controller. Applying the old spine correction here would rotate
            // the upper body a second time and reintroduce the facing error.
            reason = "squared-locomotion";
        }
        else if (horizontalSpeed >= HeadAimMovementSpeedThreshold)
        {
            targetOffsetDegrees =
                BundyPlugin.ModConfig?.ChainsawFacingYawOffsetDegrees?.Value ?? -46f;
            reason = "locomotion-moving";
        }
        else
        {
            targetOffsetDegrees =
                BundyPlugin.ModConfig?.ChainsawFacingYawOffsetIdleDegrees?.Value ?? 0f;
            reason = "locomotion-idle";
        }

        float smoothingSeconds = Mathf.Clamp(
            BundyPlugin.ModConfig?.ChainsawFacingYawOffsetSmoothingSeconds?.Value ?? 0.25f,
            0.01f,
            2f);
        _currentFacingYawOffsetDegrees = Mathf.Lerp(
            _currentFacingYawOffsetDegrees,
            targetOffsetDegrees,
            DampingAlpha(Mathf.Max(0f, Time.deltaTime), smoothingSeconds));
        if (Mathf.Abs(targetOffsetDegrees) < 0.0001f &&
            Mathf.Abs(_currentFacingYawOffsetDegrees) < 0.0001f)
        {
            _currentFacingYawOffsetDegrees = 0f;
        }

        if (Mathf.Abs(_currentFacingYawOffsetDegrees) > 0.01f)
        {
            // R33: rotating the PELVIS carries the legs with it, so the authored
            // gait (feet stepping along travel) ends up ~46° off the actual travel
            // direction — the in-game crab-walk/tilt Lawson recorded. Applying the
            // yaw across the spine chain squares the torso/shoulders to travel
            // while the legs keep the authored bladed gait. The arms/saw hang off
            // spine_03's subtree, so the grip rotates rigidly with the twist.
            ResolveSpineBones();
            bool spineMode =
                (BundyPlugin.ModConfig?.ChainsawFacingYawOffsetAtSpine?.Value ?? true) &&
                _spineBones != null;
            if (spineMode)
            {
                for (int i = 0; i < _spineBones.Length; i++)
                {
                    _spineBones[i].rotation =
                        Quaternion.AngleAxis(
                            _currentFacingYawOffsetDegrees * SpineYawDistribution[i],
                            Vector3.up) *
                        _spineBones[i].rotation;
                }
            }
            else if (_pelvisBone != null)
            {
                _pelvisBone.rotation =
                    Quaternion.AngleAxis(_currentFacingYawOffsetDegrees, Vector3.up) *
                    _pelvisBone.rotation;
            }
        }

        MaybeLogFacingYawOffset(
            category,
            clipName,
            horizontalSpeed,
            targetOffsetDegrees,
            _currentFacingYawOffsetDegrees,
            reason);
    }

    private void MaybeLogFacingYawOffset(
        int category,
        string clipName,
        float horizontalSpeed,
        float targetOffsetDegrees,
        float currentOffsetDegrees,
        string reason)
    {
        bool active = Mathf.Abs(currentOffsetDegrees) > 0.01f;
        bool changed = active != _lastFacingOffsetActive ||
            !string.Equals(reason, _lastFacingOffsetReason, StringComparison.Ordinal);
        bool due = active && Time.time >= _nextFacingOffsetLogTime;
        if (!changed && !due)
            return;

        _lastFacingOffsetActive = active;
        _lastFacingOffsetReason = reason;
        _nextFacingOffsetLogTime = Time.time + IkLogThrottleSeconds;
        if (!RigDiagnosticsEnabled)
            return;

        LogIkInfo(
            $"[Bundy] Facing yaw offset category={CategoryName(category)}, clip='{clipName}', " +
            $"active={active}, reason={reason}, speed={horizontalSpeed:F2}m/s, " +
            $"target={targetOffsetDegrees:F1}, current={currentOffsetDegrees:F1}");
    }

    // Manual head steering for the Avatar-less canonical rig. Animator has already
    // sampled this frame's authored pose when LateUpdate runs, including the pelvis
    // offset above, so the measured local look axis can be corrected in world space.
    private void ApplyGenericHeadAim()
    {
        ResolveHeadBones();
        if (animator == null || _headBone == null)
            return;

        ResolveAiAndAgent();

        string clipName;
        int category = ResolveCurrentIkCategory(out clipName);
        bool enabled = BundyPlugin.ModConfig?.ChainsawHeadAimEnabled?.Value ?? true;
        bool playableActive = false;
        try { playableActive = _ai != null && BundyAttackerPlayableController.IsActive(_ai); }
        catch { playableActive = false; }

        bool allowed = enabled && category == IkCategoryLocomotion && !playableActive;
        bool hasTarget = false;
        Vector3 point = Vector3.zero;
        string reason;

        if (!enabled)
        {
            reason = "config-disabled";
        }
        else if (category != IkCategoryLocomotion)
        {
            reason = "non-locomotion";
        }
        else if (playableActive)
        {
            reason = "playable-active";
        }
        else if (_ai != null && _ai.TryGetChaseLookAtPoint(out point))
        {
            hasTarget = true;
            reason = "chase-target";
        }
        else
        {
            Vector3 horizontalVelocity = _agent != null && _agent.enabled
                ? _agent.velocity
                : Vector3.zero;
            horizontalVelocity.y = 0f;
            if (horizontalVelocity.sqrMagnitude >=
                HeadAimMovementSpeedThreshold * HeadAimMovementSpeedThreshold)
            {
                point = _headBone.position + horizontalVelocity.normalized;
                hasTarget = true;
                reason = "movement-direction";
            }
            else
            {
                reason = "authored-pose";
            }
        }

        Quaternion authoredHeadRotation = _headBone.rotation;
        Quaternion targetLocalCorrection = Quaternion.identity;
        float appliedYaw = 0f;
        float appliedPitch = 0f;
        float aimWeight = 0f;
        if (allowed && hasTarget)
        {
            Vector3 toTarget = point - _headBone.position;
            if (toTarget.sqrMagnitude > 0.000001f)
            {
                Vector3 desired = toTarget.normalized;
                // #319: the clamp has to be measured against the body the player can
                // SEE, not the AI root. BundyAIBase.ApplyVisualYawCorrection yaws (and
                // rolls) the animator child relative to the root every frame, so a root
                // basis let the head sit up to that yaw off-centre before the clamp even
                // engaged. The animator transform is the one that correction writes to and
                // the whole rendered body hangs off it, so its forward IS the rendered
                // facing. Deliberately NOT the spine chain: this rig's bone axes are
                // non-standard (see HeadLocalLookAxis, measured as -Y) so there is no
                // verified spine forward, and the spine is both the head's parent and the
                // target of ApplyFacingYawOffset immediately above — a moving basis the
                // fade band would chase.
                Transform referenceTransform = animator.transform;
                Vector3 referenceForward = referenceTransform != null
                    ? referenceTransform.forward
                    : Vector3.forward;
                referenceForward.y = 0f;
                if (referenceForward.sqrMagnitude < 0.000001f && _ai != null)
                {
                    referenceForward = _ai.transform.forward;
                    referenceForward.y = 0f;
                }
                if (referenceForward.sqrMagnitude < 0.000001f)
                    referenceForward = Vector3.forward;
                referenceForward.Normalize();

                Vector3 desiredHorizontal = Vector3.ProjectOnPlane(desired, Vector3.up);
                float desiredYaw = desiredHorizontal.sqrMagnitude > 0.000001f
                    ? Vector3.SignedAngle(
                        referenceForward,
                        desiredHorizontal.normalized,
                        Vector3.up)
                    : 0f;
                float desiredPitch = Mathf.Atan2(
                    desired.y,
                    Mathf.Max(desiredHorizontal.magnitude, 0.0001f)) * Mathf.Rad2Deg;

                float yawClamp = Mathf.Clamp(
                    BundyPlugin.ModConfig?.ChainsawHeadAimYawClampDegrees?.Value ?? 60f,
                    0f,
                    120f);
                float pitchClamp = Mathf.Clamp(
                    BundyPlugin.ModConfig?.ChainsawHeadAimPitchClampDegrees?.Value ?? 25f,
                    0f,
                    60f);
                // The clamp still decides the DIRECTION the head is pointed; the weight
                // decides how much of that correction is worth applying. Full strength up
                // to (clamp - fade), linearly out to zero at the clamp itself.
                aimWeight = 1f - Mathf.InverseLerp(
                    yawClamp - HeadAimYawFadeDegrees,
                    yawClamp,
                    Mathf.Abs(desiredYaw));
                float clampedTargetYaw = Mathf.Clamp(desiredYaw, -yawClamp, yawClamp);
                float clampedTargetPitch = Mathf.Clamp(desiredPitch, -pitchClamp, pitchClamp);

                Vector3 clampedDesiredHorizontal =
                    Quaternion.AngleAxis(clampedTargetYaw, Vector3.up) * referenceForward;
                float clampedPitchRadians = clampedTargetPitch * Mathf.Deg2Rad;
                Vector3 clampedDesired = (
                    clampedDesiredHorizontal * Mathf.Cos(clampedPitchRadians) +
                    Vector3.up * Mathf.Sin(clampedPitchRadians)).normalized;

                Vector3 currentLook = authoredHeadRotation * HeadLocalLookAxis;
                currentLook.Normalize();
                Vector3 currentHorizontal = Vector3.ProjectOnPlane(currentLook, Vector3.up);
                Vector3 clampedHorizontal =
                    Vector3.ProjectOnPlane(clampedDesired, Vector3.up);
                if (currentHorizontal.sqrMagnitude > 0.000001f &&
                    clampedHorizontal.sqrMagnitude > 0.000001f)
                {
                    appliedYaw = Vector3.SignedAngle(
                        currentHorizontal.normalized,
                        clampedHorizontal.normalized,
                        Vector3.up);
                }

                Quaternion yawCorrection = Quaternion.AngleAxis(appliedYaw, Vector3.up);
                Vector3 postYawLook = (yawCorrection * currentLook).normalized;
                Vector3 postYawHorizontal =
                    Vector3.ProjectOnPlane(postYawLook, Vector3.up);
                float postYawElevation = Mathf.Atan2(
                    postYawLook.y,
                    Mathf.Max(postYawHorizontal.magnitude, 0.0001f)) * Mathf.Rad2Deg;
                float desiredElevation = Mathf.Atan2(
                    clampedDesired.y,
                    Mathf.Max(clampedHorizontal.magnitude, 0.0001f)) * Mathf.Rad2Deg;
                appliedPitch = desiredElevation - postYawElevation;

                Vector3 postYawRight = postYawHorizontal.sqrMagnitude > 0.000001f
                    ? Vector3.Cross(Vector3.up, postYawHorizontal.normalized)
                    : Vector3.Cross(Vector3.up, referenceForward);
                if (postYawRight.sqrMagnitude < 0.000001f)
                    postYawRight = Vector3.right;
                postYawRight.Normalize();

                // Apply yaw first, then pitch about the post-yaw horizontal right
                // axis. Omitting a twist term keeps the correction roll-free.
                Quaternion targetWorldCorrection =
                    Quaternion.AngleAxis(-appliedPitch, postYawRight) * yawCorrection;
                targetLocalCorrection =
                    Quaternion.Inverse(authoredHeadRotation) *
                    targetWorldCorrection *
                    authoredHeadRotation;
                // Pitch rides the same weight rather than getting its own band: the two
                // axes are already composed into one correction here, and a head still
                // craned up/down at a target it has stopped tracking reads as broken.
                // The existing ChainsawHeadAimSmoothingSeconds slerp below carries the
                // fade in time; this only sets where it is heading.
                targetLocalCorrection = Quaternion.Slerp(
                    Quaternion.identity,
                    targetLocalCorrection,
                    aimWeight);
            }
            else
            {
                hasTarget = false;
                reason = "target-too-close";
            }
        }

        float deltaTime = Mathf.Max(0f, Time.deltaTime);
        if (allowed)
        {
            float smoothingSeconds = Mathf.Clamp(
                BundyPlugin.ModConfig?.ChainsawHeadAimSmoothingSeconds?.Value ?? 0.2f,
                0.01f,
                2f);
            _smoothedHeadAimLocalCorrection = Quaternion.Slerp(
                _smoothedHeadAimLocalCorrection,
                targetLocalCorrection,
                DampingAlpha(deltaTime, smoothingSeconds));
        }

        _headAimBlend = Mathf.Lerp(
            _headAimBlend,
            allowed ? 1f : 0f,
            DampingAlpha(deltaTime, HeadAimGateSmoothingSeconds));
        if (_headAimBlend < 0.0001f)
            _headAimBlend = 0f;
        else if (_headAimBlend > 0.9999f)
            _headAimBlend = 1f;

        Quaternion appliedLocalCorrection = Quaternion.Slerp(
            Quaternion.identity,
            _smoothedHeadAimLocalCorrection,
            _headAimBlend);
        Quaternion worldCorrection =
            authoredHeadRotation * appliedLocalCorrection * Quaternion.Inverse(authoredHeadRotation);

        if (_neckBone != null)
        {
            _neckBone.rotation =
                Quaternion.Slerp(Quaternion.identity, worldCorrection, 0.35f) *
                _neckBone.rotation;
            _headBone.rotation =
                Quaternion.Slerp(Quaternion.identity, worldCorrection, 0.65f) *
                _headBone.rotation;
        }
        else
        {
            _headBone.rotation = worldCorrection * _headBone.rotation;
        }

        MaybeLogHeadAim(
            category,
            clipName,
            point,
            allowed && hasTarget,
            reason,
            appliedYaw,
            appliedPitch,
            aimWeight);
    }

    private static float DampingAlpha(float deltaTime, float timeConstant)
    {
        if (deltaTime <= 0f)
            return 0f;
        return 1f - Mathf.Exp(-deltaTime / Mathf.Max(0.0001f, timeConstant));
    }

    private void MaybeLogHeadAim(
        int category,
        string clipName,
        Vector3 point,
        bool active,
        string reason,
        float yaw,
        float pitch,
        float aimWeight)
    {
        bool changed = active != _lastLookActive ||
            !string.Equals(reason, _lastLookReason, StringComparison.Ordinal);
        bool due = active && Time.time >= _nextLookLogTime;
        if (!changed && !due)
            return;

        _lastLookActive = active;
        _lastLookReason = reason;
        _nextLookLogTime = Time.time + IkLogThrottleSeconds;
        // Job 1: 888 lines in run 5 — 30% of everything Bundy printed, and it never appeared as its own
        // row in the run-5 breakdown because it lands under the generic [Bundy] prefix rather than a
        // dedicated tag. Head aim is finished rig work; LogAnim / verbosity 2.
        if (!BundyLogGate.Allows(BundyLogCategory.Anim, 2))
            return;

        string distance = active
            ? Vector3.Distance(_headBone.position, point).ToString("F2") + "m"
            : "<n/a>";
        LogIkInfo(
            $"[Bundy] Head aim category={CategoryName(category)}, clip='{clipName}', active={active}, reason={reason}, " +
            $"blend={_headAimBlend:F2}, aimWeight={aimWeight:F2}, yaw={yaw:F1}, pitch={pitch:F1}, " +
            $"target={FormatVector(point)}, headToTarget={distance}");
    }

    private void ResolvePelvisBone()
    {
        if (_pelvisBoneResolved || animator == null)
            return;

        Transform canonicalArmature = FindChildRecursive(animator.transform, "Armature");
        if (canonicalArmature != null)
            _pelvisBone = FindChildRecursive(canonicalArmature, "pelvis");

        _pelvisBoneResolved = _pelvisBone != null;
    }

    private void ResolveSpineBones()
    {
        if (_spineBonesResolved || animator == null)
            return;

        Transform canonicalArmature = FindChildRecursive(animator.transform, "Armature");
        Transform searchRoot = canonicalArmature != null ? canonicalArmature : animator.transform;
        Transform spine1 = FindChildRecursive(searchRoot, "spine_01");
        Transform spine2 = FindChildRecursive(searchRoot, "spine_02");
        Transform spine3 = FindChildRecursive(searchRoot, "spine_03");
        // All three or nothing: a partial chain would concentrate the twist in
        // the wrong place; the caller falls back to the pelvis path instead.
        _spineBones = spine1 != null && spine2 != null && spine3 != null
            ? new[] { spine1, spine2, spine3 }
            : null;
        _spineBonesResolved = true;
    }

    private void ResolveHeadBones()
    {
        if (_headBonesResolved || animator == null)
            return;

        Transform canonicalArmature = FindChildRecursive(animator.transform, "Armature");
        Transform searchRoot = canonicalArmature != null ? canonicalArmature : animator.transform;
        _neckBone = FindChildRecursive(searchRoot, "neck_01");
        _headBone = FindChildRecursive(searchRoot, "head");

        // Preserve compatibility with any old Humanoid test prefab, but the
        // production canonical rig resolves the exact generic names above.
        if (_headBone == null && animator.avatar != null && animator.avatar.isHuman)
            _headBone = animator.GetBoneTransform(HumanBodyBones.Head);
        if (_neckBone == null && animator.avatar != null && animator.avatar.isHuman)
            _neckBone = animator.GetBoneTransform(HumanBodyBones.Neck);

        _headBonesResolved = _headBone != null;
    }

    private void LateUpdate()
    {
        // The humanoid OnAnimatorIK path already honors this static, but the
        // canonical generic rig runs entirely through LateUpdate. Without this
        // gate the "disable all IK" diagnostic never actually disabled the
        // generic two-bone grip or the target anchor restore.
        if (DiagnosticDisableAllIK)
        {
            if (!_loggedDisabled)
            {
                _loggedDisabled = true;
                LogIkWarning("[BundyIK] EARLY_EXIT: DiagnosticDisableAllIK=true (generic LateUpdate path)");
            }
            return;
        }

        if (animator == null)
            animator = GetComponent<Animator>();
        if (animator == null)
        {
            WarnMissingSetupOnce();
            return;
        }

        // The body-facing correction must precede head aim so the head solver sees
        // the pelvis-adjusted authored pose. Both run before any hand or prop work.
        ApplyFacingYawOffset();
        ApplyGenericHeadAim();

        if (rightHandTarget == null)
            rightHandTarget = FindChildRecursive(transform, "BundyChainsaw_RightHandIKTarget");
        ResolveChainsawProp();
        RestoreCanonicalTargetAnchor();
        ResolveRightHandBone();
        ResolveLeftHandBone();

        // Locomotion shoulder shaping stays upstream of the grip solver so it
        // can restore the authored hands-on-saw contact after both offsets.
        ApplyClavicleProtractionOffset();
        ApplyElbowsOutOffset();

        if (rightHandTarget == null)
        {
            WarnMissingSetupOnce();
            return;
        }

        LogSetupOnce();

        bool humanoid = animator.avatar != null && animator.avatar.isHuman;
        if (!humanoid)
        {
            if (genericTwoBoneGrip)
                ApplyGenericTwoBoneGrip();
            else if (!(BundyPlugin.ModConfig?.ChainsawDisableGenericGripSolver?.Value ?? false))
                WarnMissingSetupOnce();
            return;
        }

        // Direct hand-bone forcing produced stretched arms and underground bodies (see
        // Decisions/2026-05-28). Use Animator IK only, with state gating, so finishers
        // and non-chainsaw reactions keep their authored paired motion.
        // Refuse to write the right-hand bone regardless of inspector toggles.
        if (directBoneFallback && !_loggedDirectFallbackDisabled)
        {
            _loggedDirectFallbackDisabled = true;
            LogIkWarning(
                "[Bundy] Chainsaw right-hand direct bone fallback was requested but is permanently disabled in code.");
        }
    }

    private void ResolveRightHandBone()
    {
        if (_rightHandBone != null || animator == null)
            return;

        Transform canonicalArmature = FindChildRecursive(animator.transform, "Armature");
        if (canonicalArmature != null)
            _rightHandBone = FindChildRecursive(canonicalArmature, "hand_r");
        if (_rightHandBone == null && animator.avatar != null && animator.avatar.isHuman)
            _rightHandBone = animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (_rightHandBone == null)
            _rightHandBone = FindChildRecursive(transform, "mixamorig:RightHand");
        if (_rightHandBone == null)
            _rightHandBone = FindChildRecursive(transform, "CC_Base_R_Hand");
    }

    private void ResolveLeftHandBone()
    {
        if (_leftHandBone != null || animator == null)
            return;

        Transform canonicalArmature = FindChildRecursive(animator.transform, "Armature");
        if (canonicalArmature != null)
            _leftHandBone = FindChildRecursive(canonicalArmature, "hand_l");
        if (_leftHandBone == null && animator.avatar != null && animator.avatar.isHuman)
            _leftHandBone = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        if (_leftHandBone == null)
            _leftHandBone = FindChildRecursive(transform, "mixamorig:LeftHand");
        if (_leftHandBone == null)
            _leftHandBone = FindChildRecursive(transform, "CC_Base_L_Hand");
    }

    private void ResolveChainsawProp()
    {
        if (chainsawProp != null)
            return;

        chainsawProp = FindAncestorNamed(rightHandTarget, "BundyProp_Chainsaw");
        if (chainsawProp == null)
            chainsawProp = FindChildRecursive(transform, "BundyProp_Chainsaw");
    }

    private void LogSetupOnce()
    {
        // #716 F3: check the gate before burning the once-flag, so a session that reopens
        // diagnostics later still gets the setup receipt.
        if (_loggedSetup || !RigDiagnosticsEnabled)
            return;

        _loggedSetup = true;
        string boneName = _rightHandBone != null ? _rightHandBone.name : "<missing>";
        string leftBoneName = _leftHandBone != null ? _leftHandBone.name : "<missing>";
        string avatarName = animator.avatar != null ? animator.avatar.name : "<generic/no-avatar>";
        string targetPath = rightHandTarget != null ? GetPath(transform, rightHandTarget) : "<missing>";
        string propPath = chainsawProp != null ? GetPath(transform, chainsawProp) : "<missing>";
        LogIkInfo(
            $"[Bundy] Chainsaw hand IK setup: animator='{animator.name}', avatar='{avatarName}', " +
            $"leftHandBone='{leftBoneName}', rightHandBone='{boneName}', target='{targetPath}', prop='{propPath}', stateGated={stateGatedIK}, " +
            $"locoWeights=({rightHandPositionWeight:F2},{rightHandRotationWeight:F2}), " +
            $"attackWeights=({basicAttackPositionWeight:F2},{basicAttackRotationWeight:F2}), " +
            $"attackPeak=({basicAttackPeakPositionWeight:F2},{basicAttackPeakRotationWeight:F2}), attackBlend={basicAttackBlendSeconds:F2}s, " +
            $"maxReach={maxIkReach:F2}m, " +
            $"attackGripDiagnostics={attackGripDiagnostics}, disableBasicAttackIK={disableBasicAttackIK}, " +
            $"genericTwoBone={genericTwoBoneGrip}, overrideAnimatedTarget={overrideAnimatedRightHandTarget}, " +
            $"targetLocalAnchor={FormatVector(rightHandTargetLocalAnchor)}, directFallback={directBoneFallback}.");
    }

    private void RestoreCanonicalTargetAnchor()
    {
        if (!overrideAnimatedRightHandTarget || rightHandTarget == null)
            return;

        // The direct-sampled clips contain stale transform curves for this helper
        // object. Restore the measured canonical grip after Animator evaluation so
        // every state presents the same reachable handle target to the solver.
        rightHandTarget.localPosition = rightHandTargetLocalAnchor;
    }

    // Attacks already read cleanly, so clavicle protraction is restricted to
    // locomotion and leaves their authored shoulder contact untouched.
    private void ApplyClavicleProtractionOffset()
    {
        float leftDegrees =
            BundyPlugin.ModConfig?.ChainsawClavicleProtractLeftDegrees?.Value ?? 0f;
        float rightDegrees =
            BundyPlugin.ModConfig?.ChainsawClavicleProtractRightDegrees?.Value ?? 0f;
        if ((Mathf.Abs(leftDegrees) < 0.01f && Mathf.Abs(rightDegrees) < 0.01f) ||
            animator == null)
        {
            return;
        }

        string clipName;
        int category = ResolveCurrentIkCategory(out clipName);
        if (category != IkCategoryLocomotion)
            return;

        ResolveClavicleBones();
        ResolveGenericRightArmChain();
        ResolveLeftUpperArm();

        Vector3 axis = animator.transform.up;
        Vector3 forward = animator.transform.forward;
        float leftRawDegrees = ResolveProtractionRawDegrees(
            _leftClavicleBone,
            _leftUpperArmBone,
            leftDegrees,
            axis,
            forward);
        float rightRawDegrees = ResolveProtractionRawDegrees(
            _rightClavicleBone,
            _rightUpperArmBone,
            rightDegrees,
            axis,
            forward);

        bool leftApplied = Mathf.Abs(leftRawDegrees) >= 0.01f;
        bool rightApplied = Mathf.Abs(rightRawDegrees) >= 0.01f;
        if (leftApplied)
        {
            _leftClavicleBone.RotateAround(
                _leftClavicleBone.position,
                axis,
                leftRawDegrees);
        }
        if (rightApplied)
        {
            _rightClavicleBone.RotateAround(
                _rightClavicleBone.position,
                axis,
                rightRawDegrees);
        }

        if ((leftApplied || rightApplied) &&
            !_loggedClavicleProtraction &&
            RigDiagnosticsEnabled)
        {
            _loggedClavicleProtraction = true;
            LogIkInfo(
                $"[BundyIK] Clavicle protraction active: left={leftDegrees:F1} " +
                $"(raw={leftRawDegrees:F1}), right={rightDegrees:F1} " +
                $"(raw={rightRawDegrees:F1}), clip='{clipName}'.");
        }
    }

    private static float ResolveProtractionRawDegrees(
        Transform clavicle,
        Transform upperArm,
        float configuredDegrees,
        Vector3 axis,
        Vector3 forward)
    {
        if (clavicle == null || upperArm == null || Mathf.Abs(configuredDegrees) < 0.01f)
            return 0f;

        Vector3 radius = upperArm.position - clavicle.position;
        if (radius.sqrMagnitude < 0.000001f)
            return 0f;

        // About world up, d(radius)/d(angle) = up x radius; the +/- probe with
        // the larger forward projection is therefore protraction on that side.
        float positiveForward = Vector3.Dot(
            Quaternion.AngleAxis(1f, axis) * radius,
            forward);
        float negativeForward = Vector3.Dot(
            Quaternion.AngleAxis(-1f, axis) * radius,
            forward);
        float rawSign = positiveForward >= negativeForward ? 1f : -1f;
        return configuredDegrees * rawSign;
    }

    private void ResolveClavicleBones()
    {
        if ((_leftClavicleBone != null && _rightClavicleBone != null) || animator == null)
            return;

        Transform canonicalArmature = FindChildRecursive(animator.transform, "Armature");
        if (canonicalArmature == null)
            return;

        if (_leftClavicleBone == null)
            _leftClavicleBone = FindChildRecursive(canonicalArmature, "clavicle_l");
        if (_rightClavicleBone == null)
            _rightClavicleBone = FindChildRecursive(canonicalArmature, "clavicle_r");
    }

    // The chainsaw clips were authored for the bulkier original pack body and
    // read too adducted (arms pinched against the torso) on the slimmer R29
    // body. This rotates both upper arms away from the torso around the
    // character's forward axis, after Animator evaluation, during locomotion
    // and basic attacks only — paired finishers and staggers keep their
    // authored arm contact. The chainsaw is parented under hand_l, so the saw
    // travels coherently with the offset left arm. Degrees come live from
    // config so the Bundy debug tab can dial it in-game.
    private void ApplyElbowsOutOffset()
    {
        float degrees = BundyPlugin.ModConfig?.ChainsawArmElbowsOutDegrees?.Value ?? 0f;
        if (Mathf.Abs(degrees) < 0.01f || animator == null)
            return;

        string clipName;
        int category = ResolveCurrentIkCategory(out clipName);
        // Locomotion only (was locomotion+basic-attack): attacks read authored
        // and the R37 pass proved these offsets separate the hands from the
        // grip with the solver disabled — keep them away from attack poses.
        if (category != IkCategoryLocomotion)
            return;

        ResolveGenericRightArmChain();
        ResolveLeftUpperArm();
        if (_rightUpperArmBone == null && _leftUpperArmBone == null)
            return;

        Vector3 axis = animator.transform.forward;
        // Positive rotation about forward lifts the +X (right) side outward;
        // the left arm mirrors with the opposite sign.
        if (_rightUpperArmBone != null)
            _rightUpperArmBone.rotation = Quaternion.AngleAxis(degrees, axis) * _rightUpperArmBone.rotation;
        if (_leftUpperArmBone != null)
            _leftUpperArmBone.rotation = Quaternion.AngleAxis(-degrees, axis) * _leftUpperArmBone.rotation;

        if (!_loggedElbowsOut && RigDiagnosticsEnabled)
        {
            _loggedElbowsOut = true;
            LogIkInfo(
                $"[BundyIK] Elbows-out offset active: degrees={degrees:F1}, clip='{clipName}', " +
                $"left={_leftUpperArmBone != null}, right={_rightUpperArmBone != null}.");
        }
    }

    private void ResolveLeftUpperArm()
    {
        if (_leftUpperArmBone != null || animator == null)
            return;

        Transform canonicalArmature = FindChildRecursive(animator.transform, "Armature");
        if (canonicalArmature != null)
            _leftUpperArmBone = FindChildRecursive(canonicalArmature, "upperarm_l");
    }

    private void ResolveGenericRightArmChain()
    {
        if (_rightUpperArmBone != null && _rightLowerArmBone != null && _rightHandBone != null)
            return;

        Transform canonicalArmature = animator != null
            ? FindChildRecursive(animator.transform, "Armature")
            : null;
        if (canonicalArmature == null)
            return;

        _rightUpperArmBone = FindChildRecursive(canonicalArmature, "upperarm_r");
        _rightLowerArmBone = FindChildRecursive(canonicalArmature, "lowerarm_r");
        _rightHandBone = FindChildRecursive(canonicalArmature, "hand_r");
    }

    private void ApplyGenericTwoBoneGrip()
    {
        ResolveGenericRightArmChain();
        if (_rightUpperArmBone == null || _rightLowerArmBone == null ||
            _rightHandBone == null || rightHandTarget == null)
        {
            WarnMissingSetupOnce();
            return;
        }

        string clipName;
        int category = ResolveCurrentIkCategory(out clipName);
        float positionWeight = 0f;
        if (!stateGatedIK || category == IkCategoryLocomotion)
            positionWeight = Mathf.Clamp01(rightHandPositionWeight);
        else if (category == IkCategoryBasicAttack && !disableBasicAttackIK)
        {
            UpdateBasicAttackBlend(category);
            positionWeight = Mathf.Lerp(
                Mathf.Clamp01(basicAttackPositionWeight),
                Mathf.Clamp01(basicAttackPeakPositionWeight),
                _basicAttackIkBlend);
        }
        else
        {
            UpdateBasicAttackBlend(category);
        }

        if (positionWeight <= 0.001f)
            return;

        Vector3 shoulder = _rightUpperArmBone.position;
        Vector3 elbow = _rightLowerArmBone.position;
        Vector3 hand = _rightHandBone.position;
        Vector3 requested = Vector3.Lerp(hand, rightHandTarget.position, positionWeight);
        float upperLength = Vector3.Distance(shoulder, elbow);
        float lowerLength = Vector3.Distance(elbow, hand);
        if (upperLength < 0.001f || lowerLength < 0.001f)
            return;

        Vector3 toTarget = requested - shoulder;
        float distance = toTarget.magnitude;
        if (distance < 0.001f)
            return;

        float minimum = Mathf.Abs(upperLength - lowerLength) + 0.001f;
        float maximum = upperLength + lowerLength - 0.001f;
        distance = Mathf.Clamp(distance, minimum, maximum);
        Vector3 direction = toTarget.normalized;
        Vector3 clampedTarget = shoulder + direction * distance;

        Vector3 bendNormal = Vector3.Cross(elbow - shoulder, hand - elbow);
        if (bendNormal.sqrMagnitude < 0.000001f)
            bendNormal = Vector3.Cross(direction, animator.transform.up);
        if (bendNormal.sqrMagnitude < 0.000001f)
            bendNormal = Vector3.Cross(direction, animator.transform.right);
        bendNormal.Normalize();

        Vector3 bendDirection = Vector3.Cross(bendNormal, direction).normalized;
        float along = (upperLength * upperLength - lowerLength * lowerLength + distance * distance) /
            (2f * distance);
        float height = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
        Vector3 elbowA = shoulder + direction * along + bendDirection * height;
        Vector3 elbowB = shoulder + direction * along - bendDirection * height;
        Vector3 desiredElbow = Vector3.SqrMagnitude(elbowA - elbow) <= Vector3.SqrMagnitude(elbowB - elbow)
            ? elbowA
            : elbowB;

        Vector3 upperCurrent = _rightLowerArmBone.position - _rightUpperArmBone.position;
        Vector3 upperDesired = desiredElbow - _rightUpperArmBone.position;
        if (upperCurrent.sqrMagnitude > 0.000001f && upperDesired.sqrMagnitude > 0.000001f)
            _rightUpperArmBone.rotation = Quaternion.FromToRotation(upperCurrent, upperDesired) *
                _rightUpperArmBone.rotation;

        Vector3 lowerCurrent = _rightHandBone.position - _rightLowerArmBone.position;
        Vector3 lowerDesired = clampedTarget - _rightLowerArmBone.position;
        if (lowerCurrent.sqrMagnitude > 0.000001f && lowerDesired.sqrMagnitude > 0.000001f)
            _rightLowerArmBone.rotation = Quaternion.FromToRotation(lowerCurrent, lowerDesired) *
                _rightLowerArmBone.rotation;

        float rotationWeight = category == IkCategoryBasicAttack
            ? Mathf.Clamp01(basicAttackRotationWeight)
            : Mathf.Clamp01(rightHandRotationWeight);
        if (rotationWeight > 0.001f)
            _rightHandBone.rotation = Quaternion.Slerp(_rightHandBone.rotation, rightHandTarget.rotation, rotationWeight);

        // #716 F3: gate first, so the once-flag is only spent on a line that printed.
        if (!_loggedGenericSolver && RigDiagnosticsEnabled)
        {
            _loggedGenericSolver = true;
            LogIkInfo(
                $"[BundyIK] Canonical generic two-bone grip active: clip='{clipName}', " +
                $"category={CategoryName(category)}, upperLength={upperLength:F3}m, lowerLength={lowerLength:F3}m, " +
                $"handToTarget={Vector3.Distance(hand, rightHandTarget.position):F3}m, weight={positionWeight:F2}.");
        }
    }

    private void UpdateBasicAttackBlend(int category)
    {
        float blendSeconds = Mathf.Max(0.01f, basicAttackBlendSeconds);
        float target = category == IkCategoryBasicAttack ? 1f : 0f;
        _basicAttackIkBlend = Mathf.MoveTowards(_basicAttackIkBlend, target, Time.deltaTime / blendSeconds);
    }

    private bool IsRightHandTargetReachable(int category, string clipName, float positionWeight, float rotationWeight)
    {
        if (maxIkReach <= 0f || (positionWeight <= 0f && rotationWeight <= 0f))
            return true;

        ResolveRightHandBone();
        if (_rightHandBone == null || rightHandTarget == null)
            return true;

        float distance = Vector3.Distance(_rightHandBone.position, rightHandTarget.position);
        if (distance <= maxIkReach)
            return true;

        animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 0f);
        animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 0f);

        // #716 F3: REACH_REJECT is a per-second diagnostic of a normal outcome (the clip moved the
        // hand past maxIkReach and IK stood down), not a fault - so it is gated Info with the rest
        // of the rig diagnostics rather than a warning in a clean log.
        if (RigDiagnosticsEnabled && Time.time >= _nextReachRejectLogAt)
        {
            _nextReachRejectLogAt = Time.time + 1f;
            LogIkInfo(
                $"[BundyIK] REACH_REJECT category={CategoryName(category)}, clip='{clipName}', " +
                $"handToTarget={distance:F3}m > maxReach={maxIkReach:F3}m, " +
                $"targetWorld={FormatVector(rightHandTarget.position)}, handWorld={FormatVector(_rightHandBone.position)}");
        }

        return false;
    }

    private void WarnMissingSetupOnce()
    {
        if (_warnedMissingSetup)
            return;

        _warnedMissingSetup = true;
        LogIkWarning(
            "[Bundy] Chainsaw right-hand IK is disabled because its Animator, target, or required arm chain is missing.");
    }

    private static Transform FindChildRecursive(Transform parent, string childName)
    {
        if (parent == null)
            return null;

        foreach (Transform child in parent)
        {
            if (child.name == childName)
                return child;

            Transform nested = FindChildRecursive(child, childName);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static Transform FindAncestorNamed(Transform child, string targetName)
    {
        Transform current = child;
        while (current != null)
        {
            if (current.name == targetName)
                return current;
            current = current.parent;
        }

        return null;
    }

    private static string GetPath(Transform root, Transform target)
    {
        if (root == null || target == null)
            return "<null>";
        if (root == target)
            return "<root>";

        string path = target.name;
        Transform current = target.parent;
        while (current != null && current != root)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }

    private int ResolveCurrentIkCategory(out string clipName)
    {
        if (_ikCategoryFrame == Time.frameCount)
        {
            clipName = _ikCategoryClipName;
            return _ikCategoryCached;
        }

        _ikCategoryFrame = Time.frameCount;
        _ikCategoryCached = ResolveCurrentIkCategoryUncached(out _ikCategoryClipName);
        clipName = _ikCategoryClipName;
        return _ikCategoryCached;
    }

    private int ResolveCurrentIkCategoryUncached(out string clipName)
    {
        clipName = "<none>";
        if (animator == null || animator.layerCount <= 0)
            return IkCategoryBlocked;

        if (!stateGatedIK)
            return IkCategoryLocomotion;

        if (animator.IsInTransition(0))
        {
            int nextCategory = ClassifyClipInfos(animator.GetNextAnimatorClipInfo(0), out clipName);
            if (nextCategory != IkCategoryNone)
                return nextCategory;
        }

        return ClassifyClipInfos(animator.GetCurrentAnimatorClipInfo(0), out clipName);
    }

    private static int ClassifyClipInfos(AnimatorClipInfo[] clips, out string clipName)
    {
        clipName = "<none>";
        if (clips == null || clips.Length == 0)
            return IkCategoryNone;

        bool sawClip = false;
        bool sawLocomotion = false;
        string locomotionClip = "<none>";

        for (int i = 0; i < clips.Length; i++)
        {
            AnimationClip clip = clips[i].clip;
            if (clip == null)
                continue;

            sawClip = true;
            if (clipName == "<none>")
                clipName = clip.name;

            int category = ClassifyClipName(clip.name);
            if (category == IkCategoryBasicAttack)
            {
                clipName = clip.name;
                return IkCategoryBasicAttack;
            }

            if (category == IkCategoryLocomotion)
            {
                sawLocomotion = true;
                locomotionClip = clip.name;
            }
        }

        if (sawLocomotion)
        {
            clipName = locomotionClip;
            return IkCategoryLocomotion;
        }

        return sawClip ? IkCategoryBlocked : IkCategoryNone;
    }

    private static int ClassifyClipName(string clipName)
    {
        if (string.IsNullOrEmpty(clipName))
            return IkCategoryBlocked;

        if (clipName.StartsWith("Atk_Attack_", StringComparison.OrdinalIgnoreCase) ||
            clipName.IndexOf("Chainsaw_Attack_", StringComparison.OrdinalIgnoreCase) >= 0 ||
            clipName.IndexOf("_Attack_", StringComparison.OrdinalIgnoreCase) >= 0)
            return IkCategoryBasicAttack;

        // Chainsaw_Taunt_* belongs here, not in the "blocked" fallthrough below. The prop is parented
        // to the LEFT hand, so with the right-hand grip solver at weight 0 the taunt flung a heavy
        // two-handed saw around one-handed — the "weightless balloon" read that kept
        // BundyAIChainsaw.TauntsEnabled off. Locomotion is the correct bucket: it is the full-grip
        // (0.92) case, and like locomotion the taunt is an authored upper-body clip that should keep
        // both hands on the tool. Attacks deliberately stay on their own lower blended weights.
        if (clipName.IndexOf("Chainsaw_Idle", StringComparison.OrdinalIgnoreCase) >= 0 ||
            clipName.IndexOf("Chainsaw_Walk", StringComparison.OrdinalIgnoreCase) >= 0 ||
            clipName.IndexOf("Chainsaw_Run", StringComparison.OrdinalIgnoreCase) >= 0 ||
            clipName.IndexOf("Chainsaw_Turn", StringComparison.OrdinalIgnoreCase) >= 0 ||
            clipName.IndexOf("Chainsaw_Taunt", StringComparison.OrdinalIgnoreCase) >= 0)
            return IkCategoryLocomotion;

        return IkCategoryBlocked;
    }

    private static string CategoryName(int category)
    {
        switch (category)
        {
            case IkCategoryLocomotion:
                return "idle/loco";
            case IkCategoryBasicAttack:
                return "basic-attack";
            case IkCategoryBlocked:
                return "blocked";
            default:
                return "none";
        }
    }

    private void LogIkState(int category, string clipName, float posWeight, float rotWeight, string reason, bool earlyExit)
    {
        if (animator == null || rightHandTarget == null)
            return;

        Vector3 targetWorldPos = rightHandTarget.position;
        Vector3 handWorldPos = _rightHandBone != null ? _rightHandBone.position : Vector3.zero;
        float handToTargetDist = Vector3.Distance(handWorldPos, targetWorldPos);
        bool controllerNull = animator.runtimeAnimatorController == null;

        var ai = GetComponentInParent<BundyAIBase>();
        bool attackerPlayableActive = false;
        try
        {
            attackerPlayableActive = ai != null && BundyAttackerPlayableController.IsActive(ai);
        }
        catch { }

        string targetPath = GetPath(transform, rightHandTarget);
        string handBoneName = _rightHandBone != null ? _rightHandBone.name : "<missing>";

        string prefix = earlyExit ? "[BundyIK] EARLY_EXIT" : "[BundyIK] ACTIVE";
        LogIkInfo(
            $"{prefix} reason={reason}, category={CategoryName(category)}, clip='{clipName}', " +
            $"posW={posWeight:F2}, rotW={rotWeight:F2}, " +
            $"target='{targetPath}', targetWorld={FormatVector(targetWorldPos)}, " +
            $"handBone='{handBoneName}', handWorld={FormatVector(handWorldPos)}, " +
            $"handToTarget={handToTargetDist:F3}m, controllerNull={controllerNull}, attackerPlayableActive={attackerPlayableActive}");
    }

    private void MaybeLogAttackGripDiagnostic(int category, string clipName, float posWeight, float rotWeight, bool attackIkSuppressed)
    {
        if (!RigDiagnosticsEnabled ||
            !attackGripDiagnostics ||
            Time.time < _nextGripDiagnosticLogAt)
        {
            return;
        }

        bool attackClipPresent = category == IkCategoryBasicAttack || AnimatorHasBasicAttackClip();
        if (!attackClipPresent)
            return;

        _nextGripDiagnosticLogAt = Time.time + GripDiagnosticLogSeconds;
        ResolveLeftHandBone();
        ResolveRightHandBone();
        ResolveChainsawProp();

        Vector3 leftWorld = _leftHandBone != null ? _leftHandBone.position : Vector3.zero;
        Vector3 rightWorld = _rightHandBone != null ? _rightHandBone.position : Vector3.zero;
        Vector3 targetWorld = rightHandTarget != null ? rightHandTarget.position : Vector3.zero;
        bool hasLeft = _leftHandBone != null;
        bool hasRight = _rightHandBone != null;
        bool hasTarget = rightHandTarget != null;
        bool hasProp = chainsawProp != null;

        float leftToTarget = hasLeft && hasTarget ? Vector3.Distance(leftWorld, targetWorld) : -1f;
        float rightToTarget = hasRight && hasTarget ? Vector3.Distance(rightWorld, targetWorld) : -1f;
        float handSeparation = hasLeft && hasRight ? Vector3.Distance(leftWorld, rightWorld) : -1f;

        string propLeftLocal = hasProp && hasLeft ? FormatVector(chainsawProp.InverseTransformPoint(leftWorld)) : "<n/a>";
        string propRightLocal = hasProp && hasRight ? FormatVector(chainsawProp.InverseTransformPoint(rightWorld)) : "<n/a>";
        string propTargetLocal = hasProp && hasTarget ? FormatVector(chainsawProp.InverseTransformPoint(targetWorld)) : "<n/a>";
        string targetLocal = hasTarget ? FormatVector(rightHandTarget.localPosition) : "<n/a>";
        string propPath = hasProp ? GetPath(transform, chainsawProp) : "<missing>";

        AnimatorStateInfo state = default;
        bool haveState = false;
        bool inTransition = false;
        string currentClips = "<none>";
        string nextClips = "<not-in-transition>";
        try
        {
            state = animator.GetCurrentAnimatorStateInfo(0);
            haveState = true;
            inTransition = animator.IsInTransition(0);
            currentClips = FormatClipInfo(animator.GetCurrentAnimatorClipInfo(0));
            nextClips = inTransition ? FormatClipInfo(animator.GetNextAnimatorClipInfo(0)) : "<not-in-transition>";
        }
        catch
        {
        }

        if (_ai == null)
            _ai = GetComponentInParent<BundyAIBase>();

        string aiPhase = _ai != null ? _ai.AttackPhaseForDiagnostics : "<none>";
        int attackIndex = _ai != null ? _ai.ActiveAttackIndexForDiagnostics : -1;
        float attackElapsed = _ai != null ? _ai.AttackElapsedForDiagnostics : -1f;
        float activeWindowRemaining = _ai != null ? _ai.AttackActiveWindowRemainingForDiagnostics : 0f;
        bool lungeActive = _ai != null && _ai.AttackLungeActiveForDiagnostics;

        LogIkInfo(
            $"[BundyIKGrip] category={CategoryName(category)}, clip='{clipName}', norm={(haveState ? state.normalizedTime.ToString("F3") : "<n/a>")}, " +
            $"inTransition={inTransition}, currentClips={currentClips}, nextClips={nextClips}, " +
            $"attackIndex={attackIndex}, aiPhase={aiPhase}, attackElapsed={attackElapsed:F3}, activeWindowRemaining={activeWindowRemaining:F3}, lunge={lungeActive}, " +
            $"blend={_basicAttackIkBlend:F2}, posW={posWeight:F2}, rotW={rotWeight:F2}, attackIkSuppressed={attackIkSuppressed}, " +
            $"leftWorld={(hasLeft ? FormatVector(leftWorld) : "<missing>")}, rightWorld={(hasRight ? FormatVector(rightWorld) : "<missing>")}, " +
            $"targetWorld={(hasTarget ? FormatVector(targetWorld) : "<missing>")}, prop='{propPath}', " +
            $"leftToTarget={FormatDistance(leftToTarget)}, rightToTarget={FormatDistance(rightToTarget)}, handSeparation={FormatDistance(handSeparation)}, " +
            $"propLeftLocal={propLeftLocal}, propRightLocal={propRightLocal}, propTargetLocal={propTargetLocal}, targetLocal={targetLocal}");
    }

    private bool AnimatorHasBasicAttackClip()
    {
        if (animator == null || animator.layerCount <= 0)
            return false;

        try
        {
            if (ClipInfosContainBasicAttack(animator.GetCurrentAnimatorClipInfo(0)))
                return true;

            return animator.IsInTransition(0) &&
                ClipInfosContainBasicAttack(animator.GetNextAnimatorClipInfo(0));
        }
        catch
        {
            return false;
        }
    }

    private static bool ClipInfosContainBasicAttack(AnimatorClipInfo[] clipInfos)
    {
        if (clipInfos == null)
            return false;

        for (int i = 0; i < clipInfos.Length; i++)
        {
            AnimationClip clip = clipInfos[i].clip;
            if (clip != null && ClassifyClipName(clip.name) == IkCategoryBasicAttack)
                return true;
        }

        return false;
    }

    private static string FormatClipInfo(AnimatorClipInfo[] clipInfos)
    {
        if (clipInfos == null || clipInfos.Length == 0)
            return "<none>";

        string result = "";
        for (int i = 0; i < clipInfos.Length; i++)
        {
            AnimationClip clip = clipInfos[i].clip;
            if (clip == null)
                continue;

            if (result.Length > 0)
                result += ",";

            result += clip.name + "@" + clipInfos[i].weight.ToString("F2");
        }

        return result.Length > 0 ? result : "<none>";
    }

    private static string FormatDistance(float distance)
    {
        return distance >= 0f ? distance.ToString("F3") + "m" : "<n/a>";
    }

    private static string FormatVector(Vector3 v)
    {
        return $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
    }

    // #716 F3: the IK diagnostics wrote straight to UnityEngine.Debug, which put them in the log
    // under Unity's own source with no Bundy gate of their own - a handful of the once-per-session
    // lines had no gate at all. They now go through BepInEx's Bundy logger, and every Info line
    // sits behind the same Anim/verbosity-2 tier RigDiagnosticsEnabled already used. The
    // "[BundyIK]"/"[Bundy]" prefixes inside the messages are deliberately left as they are; a later
    // pass unifies prefixes repo-wide.
    private static void LogIkInfo(string message)
    {
        if (!RigDiagnosticsEnabled)
            return;

        if (BundyPlugin.Log != null)
            BundyPlugin.Log.LogInfo(message);
        else
            Debug.Log(message);
    }

    private static void LogIkWarning(string message)
    {
        if (BundyPlugin.Log != null)
            BundyPlugin.Log.LogWarning(message);
        else
            Debug.LogWarning(message);
    }
}
