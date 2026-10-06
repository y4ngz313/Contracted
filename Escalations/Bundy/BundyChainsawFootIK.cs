using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy;

/// <summary>
/// R39b per-foot terrain conformance. BundyChainsawFootGrounding is a
/// whole-body, downward-only vertical offset (min of both foot deltas,
/// maxUpOffset = 0), so it can never lift an individual foot out of rising
/// terrain — feet clip through slopes and steps. This pass runs after it
/// (execution order) and, per foot, raycasts under the animated ankle and
/// LIFTS the foot to the surface with a two-bone thigh/calf solve when the
/// surface is above the animated ankle. Lift-only by design: lowering a
/// swing-phase foot into dips needs plant-phase detection tuned against the
/// final lower-half clips (R39b rebake), and the whole-body pass already
/// settles the body down onto the lowest foot. Foot world rotation is
/// preserved through the solve, optionally pitched toward the ground slope
/// while lifted. Locomotion-only, config-gated, weight-ramped at the gates so
/// enabling/disabling never pops.
/// </summary>
[DefaultExecutionOrder(120)]
public sealed class BundyChainsawFootIK : MonoBehaviour
{
    private const float MinCastHitDistance = 0.001f;
    private const float MinGroundNormalSqrMagnitude = 0.5f;

    public Animator animator;
    public BundyAIBase ai;
    public string leftFootPath = "Armature/root/pelvis/thigh_l/calf_l/foot_l";
    public string rightFootPath = "Armature/root/pelvis/thigh_r/calf_r/foot_r";
    // Same planted-ankle height model as BundyChainsawFootGrounding.
    public float footBoneGroundHeight = 0.236f;
    public float visualGroundBias = -0.055f;
    public float probeHeight = 0.8f;
    public float probeDistance = 1.7f;
    public float footRadius = 0.1f;
    public float liftDeadzone = 0.015f;
    public float smoothSpeed = 10f;
    public float weightRampSpeed = 6f;
    public float maxSlopeAngle = 58f;
    public float slopePitchMaxDegrees = 25f;

    private readonly RaycastHit[] _groundHits = new RaycastHit[8];
    private Foot _left;
    private Foot _right;
    private float _weight;
    private float _nextLogAt;

    private sealed class Foot
    {
        public Transform thigh;
        public Transform calf;
        public Transform ankle;
        public float smoothedLift;
        public float appliedLift;
        public float groundSlope;
        public Vector3 groundNormal = Vector3.up;
    }

    private void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (ai == null)
            ai = GetComponentInParent<BundyAIBase>();
        _left = ResolveFoot(leftFootPath);
        _right = ResolveFoot(rightFootPath);
    }

    private void LateUpdate()
    {
        if (animator == null || _left == null || _right == null)
            return;

        float maxLift = BundyPlugin.ModConfig?.ChainsawFootIKMaxLift?.Value ?? 0.45f;
        bool slopeAlign = BundyPlugin.ModConfig?.ChainsawFootIKSlopeAlign?.Value ?? true;
        float targetWeight = ShouldSolve() ? 1f : 0f;
        _weight = Mathf.MoveTowards(_weight, targetWeight, weightRampSpeed * Time.deltaTime);
        if (_weight <= 0.0001f)
        {
            _left.smoothedLift = 0f;
            _left.appliedLift = 0f;
            _right.smoothedLift = 0f;
            _right.appliedLift = 0f;
            return;
        }

        SolveFoot(_left, maxLift, slopeAlign);
        SolveFoot(_right, maxLift, slopeAlign);

        if (BundyPlugin.DebugLogsEnabled && Time.time >= _nextLogAt &&
            (_left.appliedLift > 0.001f || _right.appliedLift > 0.001f))
        {
            _nextLogAt = Time.time + 0.5f;
            BundyPlugin.Log?.LogInfo(
                $"[BundyFootIK] weight={_weight:F2}, " +
                $"liftL={_left.appliedLift:F3} (slope {_left.groundSlope:F1}), " +
                $"liftR={_right.appliedLift:F3} (slope {_right.groundSlope:F1})");
        }
    }

    private bool ShouldSolve()
    {
        if (!(BundyPlugin.ModConfig?.ChainsawFootIKEnabled?.Value ?? false))
            return false;
        if (ai != null && !ai.AllowsChainsawFootGrounding)
            return false;

        try
        {
            if (ai != null && BundyAttackerPlayableController.IsActive(ai))
                return false;

            int motion = animator.GetInteger(BundyAnimatorParams.MotionState);
            return motion == BundyAnimatorParams.MotionIdle ||
                motion == BundyAnimatorParams.MotionWalk ||
                motion == BundyAnimatorParams.MotionRun;
        }
        catch
        {
            return false;
        }
    }

    private void SolveFoot(Foot foot, float maxLift, bool slopeAlign)
    {
        float liftTarget = 0f;
        if (TrySampleGround(foot, maxLift, out float surfaceDelta, out Vector3 normal, out float slope))
        {
            foot.groundNormal = normal;
            foot.groundSlope = slope;
            if (surfaceDelta > liftDeadzone)
                liftTarget = Mathf.Min(surfaceDelta, maxLift);
        }

        float smoothing = 1f - Mathf.Exp(-smoothSpeed * Time.deltaTime);
        foot.smoothedLift = Mathf.Lerp(foot.smoothedLift, liftTarget, smoothing);
        float effectiveLift = foot.smoothedLift * _weight;
        foot.appliedLift = effectiveLift;
        if (effectiveLift < 0.0005f)
            return;

        Vector3 targetAnkle = foot.ankle.position + Vector3.up * effectiveLift;
        Quaternion animatedFootRotation = foot.ankle.rotation;
        SolveTwoBone(foot, targetAnkle);

        Quaternion footRotation = animatedFootRotation;
        if (slopeAlign && foot.groundSlope > 0.5f && foot.groundSlope <= maxSlopeAngle)
        {
            // Pitch the sole toward the surface while the foot is conforming; the
            // conformance fraction keeps flat-ground steps untouched.
            float alignWeight = Mathf.Clamp01(effectiveLift / 0.1f);
            Quaternion slopeRotation = Quaternion.FromToRotation(Vector3.up, foot.groundNormal);
            slopeRotation = ClampRotation(slopeRotation, slopePitchMaxDegrees);
            footRotation = Quaternion.Slerp(Quaternion.identity, slopeRotation, alignWeight) * footRotation;
        }
        foot.ankle.rotation = footRotation;
    }

    private void SolveTwoBone(Foot foot, Vector3 targetAnkle)
    {
        float thighLength = Vector3.Distance(foot.thigh.position, foot.calf.position);
        float calfLength = Vector3.Distance(foot.calf.position, foot.ankle.position);

        Vector3 hip = foot.thigh.position;
        Vector3 toTarget = targetAnkle - hip;
        float reach = toTarget.magnitude;
        if (reach < 0.0001f)
            return;
        float minReach = Mathf.Abs(thighLength - calfLength) + 0.0005f;
        float maxReach = thighLength + calfLength - 0.0005f;
        float clamped = Mathf.Clamp(reach, minReach, maxReach);
        Vector3 axis = toTarget / reach;
        Vector3 clampedTarget = hip + axis * clamped;

        Vector3 kneeLever = foot.calf.position - hip;
        Vector3 pole = kneeLever - Vector3.Project(kneeLever, axis);
        if (pole.sqrMagnitude < 0.0000001f)
        {
            pole = Vector3.Cross(axis, Vector3.up);
            if (pole.sqrMagnitude < 0.0000001f)
                pole = Vector3.Cross(axis, Vector3.forward);
        }
        pole.Normalize();

        float cosHip = Mathf.Clamp(
            (thighLength * thighLength + clamped * clamped - calfLength * calfLength) /
            (2f * thighLength * clamped),
            -1f, 1f);
        float sinHip = Mathf.Sqrt(Mathf.Max(0f, 1f - cosHip * cosHip));
        Vector3 newKnee = hip +
            axis * (thighLength * cosHip) +
            pole * (thighLength * sinHip);

        foot.thigh.rotation =
            Quaternion.FromToRotation(foot.calf.position - hip, newKnee - hip) *
            foot.thigh.rotation;
        foot.calf.rotation =
            Quaternion.FromToRotation(foot.ankle.position - foot.calf.position, clampedTarget - foot.calf.position) *
            foot.calf.rotation;
    }

    private bool TrySampleGround(Foot foot, float maxLift, out float surfaceDelta, out Vector3 normal, out float slope)
    {
        surfaceDelta = 0f;
        normal = Vector3.up;
        slope = 0f;

        Vector3 origin = foot.ankle.position + Vector3.up * probeHeight;
        int hitCount = Physics.SphereCastNonAlloc(
            origin,
            footRadius,
            Vector3.down,
            _groundHits,
            probeDistance,
            ResolveGroundMask(),
            QueryTriggerInteraction.Ignore);

        Transform ownerRoot = ai != null ? ai.transform : transform.root;
        bool found = false;
        float closestDistance = float.PositiveInfinity;
        RaycastHit bestHit = default;
        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = _groundHits[i];
            if (hit.collider == null)
                continue;

            // SphereCast reports unusable point/normal data when its origin overlaps a collider.
            // That was producing a fake ~1.9 m "ground" delta near combat geometry and the solver
            // converted it into a persistent max-lift knee bend. Only accept a real downward hit.
            if (hit.distance <= MinCastHitDistance || hit.normal.sqrMagnitude < MinGroundNormalSqrMagnitude)
                continue;

            Transform hitTransform = hit.collider.transform;
            if (ownerRoot != null && (hitTransform == ownerRoot || hitTransform.IsChildOf(ownerRoot)))
                continue;

            float hitSlope = Vector3.Angle(hit.normal, Vector3.up);
            float candidateDelta =
                hit.point.y + footBoneGroundHeight + visualGroundBias - foot.ankle.position.y;
            // A surface above the configured reach is not a safe leg-IK target. Reject it rather
            // than clamping a physically unrelated collider to maxLift and deforming the leg.
            if (hitSlope > maxSlopeAngle ||
                candidateDelta > Mathf.Max(0f, maxLift) + liftDeadzone ||
                hit.distance >= closestDistance)
                continue;

            found = true;
            closestDistance = hit.distance;
            bestHit = hit;
        }

        if (!found)
            return false;

        normal = bestHit.normal;
        slope = Vector3.Angle(bestHit.normal, Vector3.up);
        float desiredAnkleY = bestHit.point.y + footBoneGroundHeight + visualGroundBias;
        surfaceDelta = desiredAnkleY - foot.ankle.position.y;
        return true;
    }

    private static int ResolveGroundMask()
    {
        int mask = StartOfRound.Instance != null
            ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
            : Physics.DefaultRaycastLayers;
        return mask != 0 ? mask : Physics.DefaultRaycastLayers;
    }

    private static Quaternion ClampRotation(Quaternion rotation, float maxDegrees)
    {
        rotation.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f)
            angle -= 360f;
        if (Mathf.Abs(angle) <= maxDegrees || float.IsInfinity(axis.sqrMagnitude))
            return rotation;
        return Quaternion.AngleAxis(Mathf.Sign(angle) * maxDegrees, axis);
    }

    private Foot ResolveFoot(string path)
    {
        Transform ankle = FindByPath(path);
        if (ankle == null || ankle.parent == null || ankle.parent.parent == null)
        {
            BundyPlugin.Log?.LogWarning($"[BundyFootIK] Could not resolve foot chain at '{path}'.");
            return null;
        }
        return new Foot
        {
            ankle = ankle,
            calf = ankle.parent,
            thigh = ankle.parent.parent,
        };
    }

    private Transform FindByPath(string path)
    {
        if (animator == null || string.IsNullOrWhiteSpace(path))
            return null;

        Transform root = animator.transform;
        Transform direct = root.Find(path);
        if (direct != null)
            return direct;

        string leafName = path;
        int slash = leafName.LastIndexOf('/');
        if (slash >= 0 && slash < leafName.Length - 1)
            leafName = leafName.Substring(slash + 1);

        return FindChildRecursive(root, leafName);
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
}
