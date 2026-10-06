using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy;

public sealed class BundyChainsawFootGrounding : MonoBehaviour
{
    private const float MinCastHitDistance = 0.001f;
    private const float MinGroundNormalSqrMagnitude = 0.5f;

    public Animator animator;
    public BundyAIBase ai;
    public bool useGenericFootFallback = true;
    public string genericLeftFootPath = "Armature/root/pelvis/thigh_l/calf_l/foot_l";
    public string genericRightFootPath = "Armature/root/pelvis/thigh_r/calf_r/foot_r";
    public float genericFootBoneGroundHeight = 0.236f;
    public float visualGroundBias = -0.055f;
    public float probeHeight = 0.8f;
    public float probeDistance = 1.7f;
    public float footRadius = 0.12f;
    public float groundClearance = 0.035f;
    public float maxDownOffset = 0.28f;
    public float maxUpOffset = 0f;
    public float smoothSpeed = 8f;
    public float maxSlopeAngle = 58f;

    private readonly RaycastHit[] _groundHits = new RaycastHit[8];
    private Transform _genericLeftFoot;
    private Transform _genericRightFoot;
    private float _currentOffset;
    private float _appliedOffset;
    private float _nextLogAt;

    private void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (ai == null)
            ai = GetComponentInParent<BundyAIBase>();

        ResolveGenericFeet();
    }

    private void LateUpdate()
    {
        if (animator == null)
        {
            ClearAppliedOffset();
            return;
        }

        if (!ShouldGround())
        {
            ClearAppliedOffset();
            return;
        }

        float targetOffset = TryComputeOffset(out float offset)
            ? offset
            : 0f;
        float smoothing = 1f - Mathf.Exp(-smoothSpeed * Time.deltaTime);
        _currentOffset = Mathf.Lerp(_currentOffset, targetOffset, smoothing);
        ApplyOffset(_currentOffset);
    }

    private void ApplyOffset(float offset)
    {
        Vector3 localPosition = transform.localPosition;
        localPosition.y = localPosition.y - _appliedOffset + offset;
        transform.localPosition = localPosition;
        _appliedOffset = offset;
    }

    private void ClearAppliedOffset()
    {
        if (Mathf.Abs(_appliedOffset) > 0.0001f)
        {
            Vector3 localPosition = transform.localPosition;
            localPosition.y -= _appliedOffset;
            transform.localPosition = localPosition;
        }

        _currentOffset = 0f;
        _appliedOffset = 0f;
    }

    private bool ShouldGround()
    {
        if (animator == null)
            return false;

        if (!(BundyPlugin.ModConfig?.ChainsawFootGroundingEnabled?.Value ?? false))
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

    private bool TryComputeOffset(out float offset)
    {
        offset = 0f;
        bool useHumanBones = animator.avatar != null && animator.avatar.isHuman;
        bool useGenericFeet = false;
        Transform left;
        Transform right;
        float footGroundHeight = 0f;

        if (useHumanBones)
        {
            left = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        }
        else
        {
            if (!useGenericFootFallback)
                return false;

            // Direct-sampled transform bundles are avatarless. Use the baked rig's foot transforms instead
            // of humanoid bone lookup, which returns null when animator.avatar == null.
            ResolveGenericFeet();
            left = _genericLeftFoot;
            right = _genericRightFoot;
            footGroundHeight = genericFootBoneGroundHeight + visualGroundBias;
            useGenericFeet = true;
        }

        bool hasLeft = TrySampleFoot(left, footGroundHeight, out float leftDelta, out float leftSlope);
        bool hasRight = TrySampleFoot(right, footGroundHeight, out float rightDelta, out float rightSlope);
        if (!hasLeft && !hasRight)
            return false;

        // This grounding pass is only allowed to lower the visual. Near curved terrain edges a foot
        // raycast can hit a higher lip and otherwise lift Bundy into a visible float.
        float wanted = hasLeft && hasRight ? Mathf.Min(leftDelta, rightDelta) : (hasLeft ? leftDelta : rightDelta);
        if (wanted > 0f)
            wanted = 0f;
        offset = Mathf.Clamp(wanted, -maxDownOffset, maxUpOffset);

        if (BundyPlugin.DebugLogsEnabled && Time.time >= _nextLogAt)
        {
            _nextLogAt = Time.time + 0.5f;
            // BepInEx logger, not Debug.Log — Unity info lines are filtered from
            // this profile's LogOutput, so Debug.Log here is invisible in the field.
            BundyPlugin.Log?.LogInfo(
                $"[BundyGrounding] offset={offset:F3}, " +
                $"leftDelta={(hasLeft ? leftDelta.ToString("F3") : "<miss>")}, " +
                $"rightDelta={(hasRight ? rightDelta.ToString("F3") : "<miss>")}, " +
                $"leftSlope={leftSlope:F1}, rightSlope={rightSlope:F1}, generic={useGenericFeet}");
        }

        return true;
    }

    private bool TrySampleFoot(Transform foot, float footGroundHeight, out float delta, out float slope)
    {
        delta = 0f;
        slope = 0f;
        if (foot == null)
            return false;

        Vector3 origin = foot.position + Vector3.up * probeHeight;
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

            if (hit.distance <= MinCastHitDistance || hit.normal.sqrMagnitude < MinGroundNormalSqrMagnitude)
                continue;

            Transform hitTransform = hit.collider.transform;
            if (ownerRoot != null && (hitTransform == ownerRoot || hitTransform.IsChildOf(ownerRoot)))
                continue;

            float hitSlope = Vector3.Angle(hit.normal, Vector3.up);
            if (hitSlope > maxSlopeAngle || hit.distance >= closestDistance)
                continue;

            found = true;
            closestDistance = hit.distance;
            bestHit = hit;
        }

        if (!found)
            return false;

        slope = Vector3.Angle(bestHit.normal, Vector3.up);
        float desiredFootY = bestHit.point.y + groundClearance + footGroundHeight;
        delta = desiredFootY - foot.position.y;
        return true;
    }

    private static int ResolveGroundMask()
    {
        int mask = StartOfRound.Instance != null
            ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
            : Physics.DefaultRaycastLayers;
        return mask != 0 ? mask : Physics.DefaultRaycastLayers;
    }

    private void ResolveGenericFeet()
    {
        if (!useGenericFootFallback || animator == null)
            return;

        if (_genericLeftFoot == null)
            _genericLeftFoot = FindGenericFoot(genericLeftFootPath);
        if (_genericRightFoot == null)
            _genericRightFoot = FindGenericFoot(genericRightFootPath);
    }

    private Transform FindGenericFoot(string path)
    {
        if (animator == null || string.IsNullOrWhiteSpace(path))
            return null;

        Transform root = animator.transform;
        Transform direct = root.Find(path);
        if (direct != null)
            return direct;

        string prefixed = root.name + "/";
        if (path.StartsWith(prefixed, System.StringComparison.Ordinal))
        {
            direct = root.Find(path.Substring(prefixed.Length));
            if (direct != null)
                return direct;
        }

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
