using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.AI;

public sealed class BundyChainsawBladeHitbox : MonoBehaviour
{
    public BundyAIBase owner;
    public Transform bladeRoot;
    public float radius = 0.24f;
    public float padding = 0.1f;
    public float segmentTrimFraction = 0.08f;

    private bool _active;
    private int _attackIndex = -1;
    private float _windowEndAt;
    private Vector3 _lastCenter;
    private bool _hasLastCenter;
    private float _nextLogAt;
    private float _nextAimLogAt;
    private float _nextMissingGeometryLogAt;
    private Renderer[] _renderers;

    public bool IsWindowActive => _active && Time.time <= _windowEndAt;

    public void Configure(BundyAIBase ai, Transform root)
    {
        owner = ai;
        bladeRoot = root != null ? root : transform;
    }

    public void BeginWindow(int attackIndex, float windowEndAt)
    {
        _active = true;
        _attackIndex = attackIndex;
        _windowEndAt = windowEndAt;
        _hasLastCenter = false;
        // #276: rebuild the per-player collider cache at the start of every contact window.
        BundyAIBase.InvalidateAttackContactColliderCache();
        Log($"window active attackIndex={attackIndex}, endAt={windowEndAt:F2}");
    }

    public void EndWindow(string reason)
    {
        if (_active)
            Log($"window ended; reason={reason}");

        _active = false;
        _attackIndex = -1;
        _windowEndAt = 0f;
        _hasLastCenter = false;
    }

    public bool TryResolveContact(
        PlayerControllerB[] players,
        out PlayerControllerB hit,
        out Vector3 hitPoint,
        out float distance,
        out Bounds bladeBounds,
        out Vector3 bladeStart,
        out Vector3 bladeEnd)
    {
        hit = null;
        hitPoint = Vector3.zero;
        distance = float.PositiveInfinity;
        bladeBounds = default;
        bladeStart = Vector3.zero;
        bladeEnd = Vector3.zero;

        if (!_active || Time.time > _windowEndAt || players == null || bladeRoot == null)
            return false;

        if (!TryGetBladeSegment(out bladeStart, out bladeEnd, out bladeBounds))
            return false;

        Vector3 center = (bladeStart + bladeEnd) * 0.5f;
        Vector3 sweepStart = _hasLastCenter ? _lastCenter : center;
        _lastCenter = center;
        _hasLastCenter = true;

        float contactRadius = Mathf.Max(0.01f, radius + padding);
        PlayerControllerB closestPlayer = null;
        Vector3 closestPlayerCenter = Vector3.zero;
        Vector3 closestBladePoint = Vector3.zero;
        for (int i = 0; i < players.Length; i++)
        {
            PlayerControllerB player = players[i];
            if (player == null || player.isPlayerDead || !player.gameObject.activeInHierarchy)
                continue;

            Vector3 playerCenter = player.transform.position + Vector3.up * 0.9f;
            Vector3 bladePoint = ClosestPointOnSegment(bladeStart, bladeEnd, playerCenter);
            Vector3 sweptCenterPoint = ClosestPointOnSegment(sweepStart, center, playerCenter);
            float bladeDistance = DistanceToPlayerCollision(player, bladePoint);
            float sweptDistance = DistanceToPlayerCollision(player, sweptCenterPoint);
            float candidateDistance = Mathf.Min(bladeDistance, sweptDistance);
            Vector3 candidatePoint = bladeDistance <= sweptDistance ? bladePoint : sweptCenterPoint;

            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                hitPoint = candidatePoint;
                hit = candidateDistance <= contactRadius ? player : null;
                closestPlayer = player;
                closestPlayerCenter = playerCenter;
                closestBladePoint = candidatePoint;
            }
        }

        MaybeLogBladeAim(
            closestPlayer,
            hit,
            closestPlayerCenter,
            closestBladePoint,
            distance,
            contactRadius,
            bladeStart,
            bladeEnd,
            bladeBounds);

        if (BundyLogGate.Allows(BundyLogCategory.Combat, 2) && Time.time >= _nextLogAt)
        {
            _nextLogAt = Time.time + 0.25f;
            string victim = hit != null ? hit.playerUsername : "<none>";
            Log(
                $"sample attackIndex={_attackIndex}, hit={victim}, dist={distance:F2}, " +
                $"start={Format(bladeStart)}, end={Format(bladeEnd)}, radius={contactRadius:F2}, boundsSize={Format(bladeBounds.size)}");
        }

        return true;
    }

    private void MaybeLogBladeAim(
        PlayerControllerB closestPlayer,
        PlayerControllerB hit,
        Vector3 closestPlayerCenter,
        Vector3 closestBladePoint,
        float closestDistance,
        float contactRadius,
        Vector3 bladeStart,
        Vector3 bladeEnd,
        Bounds bladeBounds)
    {
        if (!BundyLogGate.Allows(BundyLogCategory.Combat, 2) || Time.time < _nextAimLogAt)
            return;

        _nextAimLogAt = Time.time + 0.15f;

        Vector3 targetPoint = closestPlayerCenter;
        Vector3 ownerAimPoint = Vector3.zero;
        bool hasOwnerAimPoint = owner != null && owner.TryGetBasicAttackPropAimPoint(out ownerAimPoint);
        if (hasOwnerAimPoint)
            targetPoint = ownerAimPoint;
        else if (closestPlayer == null)
            targetPoint = bladeBounds.center;

        Vector3 segment = bladeEnd - bladeStart;
        float bladeLength = segment.magnitude;
        Vector3 bladeMid = (bladeStart + bladeEnd) * 0.5f;
        Vector3 toTarget = targetPoint - bladeMid;
        float signedAimDot = 0f;
        float bestAimDot = 0f;
        float aimAngle = 180f;
        float closestT = 0f;
        if (bladeLength > 0.0001f && toTarget.sqrMagnitude > 0.0004f)
        {
            Vector3 segmentDir = segment / bladeLength;
            Vector3 targetDir = toTarget.normalized;
            signedAimDot = Mathf.Clamp(Vector3.Dot(segmentDir, targetDir), -1f, 1f);
            bestAimDot = Mathf.Abs(signedAimDot);
            aimAngle = Mathf.Acos(bestAimDot) * Mathf.Rad2Deg;
            closestT = Mathf.Clamp01(Vector3.Dot(targetPoint - bladeStart, segment) / Mathf.Max(0.0001f, segment.sqrMagnitude));
        }

        float rootDistance = owner != null && closestPlayer != null
            ? Vector3.Distance(owner.transform.position, closestPlayer.transform.position)
            : -1f;

        // Job 1: matches the (Combat, 2) tier the Log helper below already uses. This site had no gate at
        // all — it printed on every aim evaluation regardless of LogVerbosity.
        if (!BundyLogGate.Allows(BundyLogCategory.Combat, 2))
            return;

        Debug.Log(
            $"[BundyBladeAim] attackIndex={_attackIndex}, target={(closestPlayer != null ? closestPlayer.playerUsername : "<none>")}, " +
            $"hit={(hit != null ? hit.playerUsername : "<none>")}, rootDistance={(rootDistance >= 0f ? rootDistance.ToString("F2") : "<n/a>")}m, " +
            $"bladeDistance={closestDistance:F2}m, contactRadius={contactRadius:F2}, signedAimDot={signedAimDot:F3}, " +
            $"bestAimDot={bestAimDot:F3}, aimAngle={aimAngle:F1}, closestT={closestT:F2}, " +
            $"start={Format(bladeStart)}, end={Format(bladeEnd)}, closestPoint={Format(closestBladePoint)}, " +
            $"targetPoint={Format(targetPoint)}, boundsSize={Format(bladeBounds.size)}");
    }

    private bool TryGetBladeSegment(out Vector3 start, out Vector3 end, out Bounds bounds)
    {
        start = Vector3.zero;
        end = Vector3.zero;
        bounds = default;
        if (bladeRoot == null)
            return false;

        Renderer[] renderers = GetBladeRenderers();
        if (renderers == null || renderers.Length == 0)
        {
            LogMissingGeometry("no renderers under blade root");
            return false;
        }

        bool found = false;
        Vector3 axisRight = bladeRoot.right.sqrMagnitude > 0.001f ? bladeRoot.right.normalized : Vector3.right;
        Vector3 axisUp = bladeRoot.up.sqrMagnitude > 0.001f ? bladeRoot.up.normalized : Vector3.up;
        Vector3 axisForward = bladeRoot.forward.sqrMagnitude > 0.001f ? bladeRoot.forward.normalized : Vector3.forward;
        float rightMin = float.PositiveInfinity, rightMax = float.NegativeInfinity;
        float upMin = float.PositiveInfinity, upMax = float.NegativeInfinity;
        float forwardMin = float.PositiveInfinity, forwardMax = float.NegativeInfinity;

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                continue;

            Bounds rendererBounds = renderer.bounds;
            if (!found)
            {
                bounds = rendererBounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(rendererBounds);
            }

            AccumulateBoundsProjection(rendererBounds, axisRight, ref rightMin, ref rightMax);
            AccumulateBoundsProjection(rendererBounds, axisUp, ref upMin, ref upMax);
            AccumulateBoundsProjection(rendererBounds, axisForward, ref forwardMin, ref forwardMax);
        }

        if (!found)
        {
            LogMissingGeometry("renderers unavailable or disabled");
            return false;
        }

        Vector3 axis = axisForward;
        float min = forwardMin;
        float max = forwardMax;
        float rightLength = rightMax - rightMin;
        float upLength = upMax - upMin;
        float forwardLength = forwardMax - forwardMin;
        if (rightLength > forwardLength && rightLength >= upLength)
        {
            axis = axisRight;
            min = rightMin;
            max = rightMax;
        }
        else if (upLength > forwardLength && upLength > rightLength)
        {
            axis = axisUp;
            min = upMin;
            max = upMax;
        }

        float centerProjection = Vector3.Dot(bounds.center, axis);
        float length = Mathf.Max(0f, max - min);
        float trim = length * Mathf.Clamp01(segmentTrimFraction);
        float startProjection = min + trim;
        float endProjection = max - trim;
        if (endProjection < startProjection)
        {
            startProjection = min;
            endProjection = max;
        }

        start = bounds.center + axis * (startProjection - centerProjection);
        end = bounds.center + axis * (endProjection - centerProjection);
        return true;
    }

    private Renderer[] GetBladeRenderers()
    {
        if (_renderers == null || _renderers.Length == 0)
            _renderers = bladeRoot != null ? bladeRoot.GetComponentsInChildren<Renderer>(true) : null;
        return _renderers;
    }

    private static void AccumulateBoundsProjection(Bounds bounds, Vector3 axis, ref float min, ref float max)
    {
        Vector3 center = bounds.center;
        Vector3 extents = bounds.extents;
        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner = center + Vector3.Scale(extents, new Vector3(x, y, z));
                    float projection = Vector3.Dot(corner, axis);
                    if (projection < min)
                        min = projection;
                    if (projection > max)
                        max = projection;
                }
            }
        }
    }

    private void LogMissingGeometry(string reason)
    {
        if (!BundyLogGate.Allows(BundyLogCategory.Combat, 2) || Time.time < _nextMissingGeometryLogAt)
            return;

        _nextMissingGeometryLogAt = Time.time + 1f;
        Log($"geometry unavailable: {reason}");
    }

    private static Vector3 ClosestPointOnSegment(Vector3 start, Vector3 end, Vector3 point)
    {
        Vector3 segment = end - start;
        float lengthSq = segment.sqrMagnitude;
        if (lengthSq <= 0.0001f)
            return end;

        float t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / lengthSq);
        return start + segment * t;
    }

    // #276: identical math to (and now shared with) BundyAIBase.DistanceToPlayerCollision, which
    // caches the per-player collider arrays for the duration of the attack window.
    private static float DistanceToPlayerCollision(PlayerControllerB player, Vector3 point)
    {
        return BundyAIBase.DistanceToPlayerCollision(player, point);
    }

    private static string Format(Vector3 value)
    {
        return $"({value.x:F2},{value.y:F2},{value.z:F2})";
    }

    private static void Log(string message)
    {
        if (BundyLogGate.Allows(BundyLogCategory.Combat, 2))
            Debug.Log($"[BundyBladeHitbox] {message}");
    }
}
