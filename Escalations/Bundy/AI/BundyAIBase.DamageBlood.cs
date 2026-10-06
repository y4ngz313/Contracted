using Unity.Netcode;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.AI;

public abstract partial class BundyAIBase
{
    private const byte HeavyDamageBloodForce = 4;
    private const float MaxReportedDamageBloodDistanceMeters = 8f;

    private BundyBloodSkeleton _damageBloodSkeleton;
    private float _nextDamageBloodServerAt = float.NegativeInfinity;
    private float _lastDamageBloodLocalAt = float.NegativeInfinity;
    private Vector3 _lastDamageBloodLocalPoint;
    private byte _lastDamageBloodLocalForce;

    internal Collider DamageBloodFallbackCollider =>
        _runtimeBodyHitbox != null ? _runtimeBodyHitbox.GetComponent<Collider>() : null;

    /// <summary>
    /// SpikeRoofTrap bypasses EnemyAICollisionDetect.Hit and enters through
    /// <see cref="OnSpikeTrapSlam"/>, so report its contact explicitly from the authoritative
    /// owner before vanilla's hit RPC fans the reaction out.
    /// </summary>
    private void ReportSpikeTrapDamageBlood(SpikeRoofTrap trap)
    {
        Collider collider = DamageBloodFallbackCollider;
        Vector3 source = trap != null
            ? trap.transform.position
            : transform.position + Vector3.up * 4f;
        Vector3 point = collider != null
            ? collider.ClosestPoint(source)
            : transform.position + Vector3.up;

        ReportDamageBlood(point, Vector3.down, SpikeTrapHitForce);
    }

    /// <summary>
    /// Attacker-side entry point. The server rate-gates the event and fans the exact contact point
    /// out to every client; only prefab choice, roll, and size remain local cosmetics.
    /// </summary>
    internal void ReportDamageBlood(Vector3 point, Vector3 direction, int force)
    {
        if (!DamageBloodEnabled || !IsSpawned)
            return;

        byte packedForce = (byte)Mathf.Clamp(force, 1, byte.MaxValue);
        if (IsServer)
            PublishDamageBlood(point, direction, packedForce);
        else
            ReportDamageBloodServerRpc(point, direction, packedForce);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ReportDamageBloodServerRpc(Vector3 point, Vector3 direction, byte force)
    {
        PublishDamageBlood(point, direction, force);
    }

    private void PublishDamageBlood(Vector3 point, Vector3 direction, byte force)
    {
        if (!IsServer || !DamageBloodEnabled)
            return;

        if (!IsFiniteDamageBloodVector(point) || !IsFiniteDamageBloodVector(direction))
        {
            BundyBloodFXBridge.Trace(
                $"creature body decal REJECTED owner='{name}' reason=non-finite-report.");
            return;
        }

        Collider collider = DamageBloodFallbackCollider;
        Vector3 bodyCenter = collider != null
            ? collider.bounds.center
            : transform.position + Vector3.up;
        if ((point - bodyCenter).sqrMagnitude >
            MaxReportedDamageBloodDistanceMeters * MaxReportedDamageBloodDistanceMeters)
        {
            BundyBloodFXBridge.Trace(
                $"creature body decal REJECTED owner='{name}' reason=report-outside-body " +
                $"point={BundyBloodFXBridge.Format(point)} center={BundyBloodFXBridge.Format(bodyCenter)}.");
            return;
        }

        float now = Time.time;
        if (now < _nextDamageBloodServerAt)
            return;

        _nextDamageBloodServerAt = now + BundyBloodFXBridge.DuplicateSuppressionSeconds;
        ApplyDamageBloodClientRpc(point, direction, force);

        // Match PlayFinisherBloodClientRpc's established host convention exactly. If the runtime
        // also loops the ClientRpc back to the host, ApplyDamageBloodLocal's short dedupe makes the
        // second copy harmless.
        if (IsHost)
            ApplyDamageBloodLocal(point, direction, force);
    }

    [ClientRpc]
    private void ApplyDamageBloodClientRpc(Vector3 point, Vector3 direction, byte force)
    {
        ApplyDamageBloodLocal(point, direction, force);
    }

    private void ApplyDamageBloodLocal(Vector3 point, Vector3 direction, byte force)
    {
        if (!DamageBloodEnabled || !BundyBloodFXBridge.BodyPrefabsAvailable)
            return;

        float now = Time.time;
        if (now - _lastDamageBloodLocalAt < BundyBloodFXBridge.DuplicateSuppressionSeconds &&
            force == _lastDamageBloodLocalForce &&
            (point - _lastDamageBloodLocalPoint).sqrMagnitude <= 0.0001f)
        {
            return;
        }

        _lastDamageBloodLocalAt = now;
        _lastDamageBloodLocalPoint = point;
        _lastDamageBloodLocalForce = force;

        _damageBloodSkeleton ??= new BundyBloodSkeleton();
        if (!_damageBloodSkeleton.TryResolve(creatureAnimator, point, out BundyBloodSkeleton.Match match))
        {
            BundyBloodFXBridge.Trace(
                $"creature body decal ABORTED owner='{name}' reason=no-blood-bones " +
                $"animator={(creatureAnimator != null ? creatureAnimator.name : "<null>")}.");
            return;
        }

        int cap = Mathf.Max(0, BundyBloodFXBridge.CreatureBodyDecalCap);
        if (cap == 0)
            return;

        long channelKey = BundyBloodFXBridge.CreatureChannelFor(GetInstanceID());
        bool final = BundyBloodFXBridge.BodyCount(channelKey) >= cap - 1;
        float effectiveScale = final || force >= HeavyDamageBloodForce
            ? match.Profile.HeavyScale
            : 1f;
        var profile = new BundyBloodProfile(
            match.Profile.Radius,
            match.Profile.MinWidth,
            match.Profile.MaxWidth,
            match.Profile.Depth,
            match.Profile.FrontMargin,
            effectiveScale);

        BundyBloodFXBridge.PlaceBodyDecalAt(
            channelKey,
            match.Anchor,
            point,
            direction,
            in profile,
            final,
            cap,
            $"creature='{name}' source=damage segment='{match.BoneName}' force={force}");
    }

    private static bool DamageBloodEnabled => BundyBloodFXBridge.DamageBloodEnabled;

    private static bool IsFiniteDamageBloodVector(Vector3 value)
    {
        return
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    /// <summary>Despawn/destroy only. KillEnemy deliberately leaves corpse blood in place.</summary>
    private void ClearDamageBloodOnTeardown()
    {
        long channelKey = BundyBloodFXBridge.CreatureChannelFor(GetInstanceID());
        BundyBloodFXBridge.ClearBodyChannel(channelKey);
        _damageBloodSkeleton?.Invalidate();
        _damageBloodSkeleton = null;
        _nextDamageBloodServerAt = float.NegativeInfinity;
        _lastDamageBloodLocalAt = float.NegativeInfinity;
        _lastDamageBloodLocalPoint = Vector3.zero;
        _lastDamageBloodLocalForce = 0;
    }
}
