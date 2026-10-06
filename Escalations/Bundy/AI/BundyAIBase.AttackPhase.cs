using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;
using Y4NGZCompany.Facility.Elevators;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.Bundy.AI;

public abstract partial class BundyAIBase
{
    private void UpdateAttackPhase()
    {
        if (_actionKind != ActionKind.Attack)
            return;

        UpdateChainsawBladeHitboxWindow();

        float now = Time.time;
        if (!_attackHitConfirmed && RequiresPhysicalBasicAttackContact && !_activeAttackCountsAsGrab)
        {
            if (TryLatchPhysicalBasicAttackContact("lunge-contact"))
            {
                EnterAttackPhase(AttackPhase.Active, "lunge-contact");
                ResolveBasicAttackImpact("lunge-contact", finalizeMissImmediately: false);
                return;
            }

            if (TryResolveChainsawWallImpact())
                return;

            if (_attackPhase == AttackPhase.Windup && now >= _attackActiveWindowStartAt)
                EnterAttackPhase(AttackPhase.Active, "window start");

            if (_attackPhase == AttackPhase.Active)
            {
                if (now > _attackActiveWindowEndAt)
                {
                    if (TryGetLatchedBasicAttackContactTarget(out _))
                        ResolveBasicAttackImpact("latched-window-contact", finalizeMissImmediately: false);

                    if (!_attackHitConfirmed)
                        FinalizeAttackMiss("active-window-expired", now - _actionStartedAt);
                }
                else
                {
                    ResolveBasicAttackImpact("active-window", finalizeMissImmediately: false);
                }

                if (!_attackHitConfirmed && now >= _attackActiveWindowEndAt && !_actionImpactResolved)
                    FinalizeAttackMiss("active-window-expired", now - _actionStartedAt);
            }
        }

        if (_actionImpactResolved && _attackPhase != AttackPhase.Recovery)
            EnterAttackPhase(AttackPhase.Recovery, "impact resolved");
    }

    private void UpdateChainsawBladeHitboxWindow()
    {
        if (_chainsawBladeHitbox == null)
            return;

        bool shouldBeActive =
            _actionKind == ActionKind.Attack &&
            !_attackHitConfirmed &&
            !_activeAttackCountsAsGrab &&
            RequiresPhysicalBasicAttackContact &&
            Time.time >= _attackActiveWindowStartAt &&
            Time.time <= _attackActiveWindowEndAt;

        if (shouldBeActive && !_chainsawBladeWindowActive)
        {
            _chainsawBladeWindowActive = true;
            _chainsawBladeHitbox.BeginWindow(_activeAttackIndex, _attackActiveWindowEndAt + BasicAttackContactLatchSeconds);
        }
        else if (!shouldBeActive && _chainsawBladeWindowActive)
        {
            ClearChainsawBladeHitboxWindow("window closed");
        }
    }

    private void ClearChainsawBladeHitboxWindow(string reason)
    {
        if (_chainsawBladeHitbox != null)
            _chainsawBladeHitbox.EndWindow(reason);
        _chainsawBladeWindowActive = false;
    }

    private bool TryResolveChainsawWallImpact()
    {
        if (!CanResolveChainsawWallImpact())
            return false;

        if (Time.time < _attackActiveWindowStartAt)
        {
            if (DebugLogs && Time.time >= _nextWallImpactSuppressedLogAt)
            {
                _nextWallImpactSuppressedLogAt = Time.time + 0.25f;
                LogAnimEvent(
                    nameof(OnAttackImpact),
                    $"wall impact probe suppressed before active window; remaining={_attackActiveWindowStartAt - Time.time:F2}s");
            }

            return false;
        }

        if (!TryGetChainsawWallImpact(out Vector3 hitPoint, out Vector3 hitNormal, out Collider hitCollider))
            return false;

        ResolveChainsawWallImpact(hitPoint, hitNormal, hitCollider, "chainsaw wall probe");
        return true;
    }

    private bool CanResolveChainsawWallImpact()
    {
        return !_chainsawWallImpactResolved &&
            _actionKind == ActionKind.Attack &&
            !_attackHitConfirmed &&
            !_actionImpactResolved &&
            !_activeAttackCountsAsGrab &&
            RequiresPhysicalBasicAttackContact &&
            _lungeActive &&
            Time.time >= _actionStartedAt + 0.08f &&
            Time.time >= _lastChainsawWallImpactAt + ChainsawWallImpactCooldownSeconds;
    }

    private void ResolveChainsawWallImpact(Vector3 hitPoint, Vector3 hitNormal, Collider hitCollider, string source)
    {
        _chainsawWallImpactResolved = true;
        _lastChainsawWallImpactAt = Time.time;
        _lungeActive = false;
        _actionImpactResolved = true;
        _actionRecoverResolved = false;
        ClearChainsawBladeHitboxWindow($"chainsaw wall impact: {source}");
        bool usesStallCycle = UsesSawStallCycle && UsesPullStart;
        // Stall cycle supersedes the old animator-speed micro-stagger: the stall dwell + pull
        // restart IS the recovery read, so don't stack both.
        _actionRecoverFallbackAt = Mathf.Max(
            _actionRecoverFallbackAt,
            Time.time + (usesStallCycle ? SawStallDwellSeconds : ChainsawWallImpactStuckPauseSeconds));
        ClearBasicAttackContactLatch();
        HoldAgentWithoutReset();

        if (usesStallCycle)
            BeginSawStall(source);
        bool metalSurface = IsChainsawMetalSurface(hitCollider);
        DispatchChainsawWallImpactEffects(hitPoint, hitNormal, metalSurface);
        EnterAttackPhase(AttackPhase.Recovery, "chainsaw wall impact");
        LogAnimEvent(
            nameof(OnAttackImpact),
            $"chainsaw wall impact miss; source={source}, collider={ColliderLabel(hitCollider)}, metal={metalSurface}, point={FormatVector(hitPoint)}, normal={FormatVector(hitNormal)}");
    }

    private bool TryGetChainsawWallImpact(out Vector3 hitPoint, out Vector3 hitNormal, out Collider hitCollider)
    {
        hitPoint = Vector3.zero;
        hitNormal = -transform.forward;
        hitCollider = null;

        Vector3 direction = GetCommittedAttackDirection();
        if (direction.sqrMagnitude < 0.001f)
            return false;

        Vector3 origin = transform.position +
            Vector3.up * BasicAttackContactVerticalOffset +
            direction * BasicAttackContactForwardOffset;
        float radius = ChainsawWallImpactProbeRadius;

        if (TryGetRuntimeChainsawBounds(out Bounds sawBounds))
        {
            Vector3 reachPoint = transform.position +
                Vector3.up * BasicAttackContactVerticalOffset +
                direction * MaxPhysicalBasicAttackRootReach;
            origin = sawBounds.ClosestPoint(reachPoint);
            radius = Mathf.Clamp(
                Mathf.Min(sawBounds.extents.x, Mathf.Min(sawBounds.extents.y, sawBounds.extents.z)) * 0.85f,
                0.16f,
                ChainsawWallImpactProbeRadius);
        }

        // 2026-08-04 audit fix (#284): probe the room/collider/default layers instead of ~0 so the
        // sweep stops paying for trigger-heavy layers; IsChainsawWallImpactSurface still post-filters.
        int mask = GetWorldCollisionMask();
        Vector3 castOrigin = origin - direction * 0.12f;
        float castDistance = ChainsawWallImpactProbeDistance + 0.12f;
        int hitCount = Physics.SphereCastNonAlloc(
            castOrigin,
            radius,
            direction,
            ChainsawWallImpactHits,
            castDistance,
            mask,
            QueryTriggerInteraction.Ignore);

        int bestIndex = -1;
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit candidate = ChainsawWallImpactHits[i];
            ChainsawWallImpactHits[i] = default;
            if (!IsChainsawWallImpactSurface(candidate.collider, candidate.normal, direction))
                continue;

            if (candidate.distance >= bestDistance)
                continue;

            bestDistance = candidate.distance;
            bestIndex = i;
            hitPoint = candidate.point;
            hitNormal = candidate.normal.sqrMagnitude > 0.001f ? candidate.normal.normalized : -direction;
            hitCollider = candidate.collider;
        }

        if (bestIndex >= 0)
            return true;

        return false;
    }

    private Vector3 GetCommittedAttackDirection()
    {
        Vector3 direction = LungeAimActive && _hasLungeAttackDirection
            ? _lungeAttackDirection
            : LungeAimActive
            ? _lungeTargetPoint - transform.position
            : transform.forward;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f)
            direction = transform.forward;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f)
            direction = Vector3.forward;

        return direction.normalized;
    }

    private bool IsChainsawWallImpactSurface(Collider collider, Vector3 hitNormal, Vector3 attackDirection)
    {
        if (!IsBundyMovementBlockingCollider(collider))
            return false;

        Vector3 normal = hitNormal.sqrMagnitude > 0.001f ? hitNormal.normalized : -attackDirection;
        if (Mathf.Abs(normal.y) > ChainsawWallImpactMaxVerticalNormal)
            return false;

        Vector3 planarNormal = normal;
        planarNormal.y = 0f;
        Vector3 planarAttack = attackDirection;
        planarAttack.y = 0f;
        if (planarNormal.sqrMagnitude < 0.001f || planarAttack.sqrMagnitude < 0.001f)
            return false;

        float facingDot = Vector3.Dot(planarNormal.normalized, -planarAttack.normalized);
        if (facingDot < ChainsawWallImpactMinFacingDot)
            return false;

        return true;
    }

    private float GetChainsawWallImpactPauseSeconds()
    {
        // The stall cycle replaces the old animator-speed micro-stagger; pause 0 disables it.
        return UsesSawStallCycle && UsesPullStart ? 0f : ChainsawWallImpactStuckPauseSeconds;
    }

    // Only the server sees the hit collider, so metal-vs-dust is classified there and shipped to
    // clients as a flag on the existing wall-impact replication point (#284).
    private static bool IsChainsawMetalSurface(Collider collider)
    {
        if (collider == null)
            return false;

        // Vanilla tags its footstep surfaces ("Metal", "Catwalk", ...) on the colliders themselves,
        // so the tag is the primary signal — the same one the footstep pools key off.
        if (BundyAudioCatalog.IsMetalSurfaceTag(collider.tag))
            return true;

        // Untagged metal props (railings, pipes, vents) usually carry the material in their names.
        if (BundyAudioCatalog.IsMetalSurfaceTag(collider.name) ||
            ContainsMetalPropKeyword(collider.name))
        {
            return true;
        }

        PhysicMaterial physicMaterial = collider.sharedMaterial;
        if (physicMaterial != null && BundyAudioCatalog.IsMetalSurfaceTag(physicMaterial.name))
            return true;

        // StartOfRound.footstepSurfaces deliberately NOT consulted (#373): the table carries no
        // metal-ness signal beyond its surfaceTag string, so any lookup still reduces to
        // IsMetalSurfaceTag on the collider tag — which the first check above already is. Vanilla's
        // metal tags ("Metal", "Catwalk", "Aluminum") are all substring-covered there.

        // Interior colliders are often untagged and generically named ("Cube"), so fall back to the
        // rendered material's name. Impact-only path (throttled), so the parent walk is acceptable.
        Renderer hitRenderer = collider.GetComponentInParent<Renderer>();
        Material rendererMaterial = hitRenderer != null ? hitRenderer.sharedMaterial : null;
        if (rendererMaterial == null)
            return false;

        return BundyAudioCatalog.IsMetalSurfaceTag(rendererMaterial.name) ||
            ContainsMetalPropKeyword(rendererMaterial.name);
    }

    private static readonly string[] MetalPropNameKeywords = { "railing", "pipe", "vent", "girder", "shipinside", "shiphull" };

    private static bool ContainsMetalPropKeyword(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        for (int i = 0; i < MetalPropNameKeywords.Length; i++)
        {
            if (name.IndexOf(MetalPropNameKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private void DispatchChainsawWallImpactEffects(Vector3 hitPoint, Vector3 hitNormal, bool metalSurface)
    {
        // Wall impacts are detected by the server-owned attack sweep; a non-server peer reaching
        // this means the server-ownership invariant broke — log instead of relaying (issue #275).
        if (!IsServer)
        {
            LogError("DispatchChainsawWallImpactEffects ran on a non-server peer; dropping wall impact.");
            return;
        }

        PlayChainsawWallImpactClientRpc(hitPoint, hitNormal, GetChainsawWallImpactPauseSeconds(), metalSurface);
    }

    [ClientRpc]
    private void PlayChainsawWallImpactClientRpc(Vector3 hitPoint, Vector3 hitNormal, float pauseSeconds, bool metalSurface)
    {
        if (metalSurface)
            SpawnChainsawMetalSparksLocal(hitPoint, hitNormal);
        else
            SpawnChainsawWallImpactDustLocal(hitPoint, hitNormal);
        PlayBundyAudioLocal(
            BundyAudioCue.ChainsawWallImpact,
            0,
            ChainsawAudioVolume,
            UnityEngine.Random.Range(0.92f, 1.06f));
        if (pauseSeconds > 0f)
            BeginChainsawWallStuckPauseLocal(pauseSeconds);
    }

    private void SpawnChainsawWallImpactDustLocal(Vector3 hitPoint, Vector3 hitNormal)
    {
        Material dustMaterial = ResolveChainsawWallDustMaterial();
        if (dustMaterial == null)
        {
            LogDiagnostic("Chainsaw wall dust skipped; no supported particle material was available.");
            return;
        }

        Vector3 normal = hitNormal.sqrMagnitude > 0.001f ? hitNormal.normalized : -transform.forward;
        GameObject dustObject = new GameObject("BundyChainsawWallDust");
        dustObject.transform.position = hitPoint + normal * 0.045f;
        dustObject.transform.rotation = Quaternion.LookRotation(normal, Vector3.up);

        ParticleSystem dust = dustObject.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = dust.main;
        main.duration = 0.45f;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.32f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.85f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.13f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.68f, 0.62f, 0.52f, 0.62f));
        main.gravityModifier = 0.12f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 32;

        ParticleSystem.EmissionModule emission = dust.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)ChainsawWallImpactDustBurstCount) });

        ParticleSystem.VelocityOverLifetimeModule velocity = dust.velocityOverLifetime;
        velocity.enabled = false;
        velocity.x = new ParticleSystem.MinMaxCurve(0f);
        velocity.y = new ParticleSystem.MinMaxCurve(0f);
        velocity.z = new ParticleSystem.MinMaxCurve(0f);

        ParticleSystem.LimitVelocityOverLifetimeModule limitVelocity = dust.limitVelocityOverLifetime;
        limitVelocity.enabled = false;

        ParticleSystem.ShapeModule shape = dust.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 32f;
        shape.radius = 0.055f;

        ParticleSystemRenderer renderer = dustObject.GetComponent<ParticleSystemRenderer>();
        if (renderer != null)
        {
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortingFudge = 1f;
            renderer.sharedMaterial = dustMaterial;
        }

        dust.Play();
        Destroy(dustObject, 1.4f);
    }

    // Metal wall hits kick sparks instead of masonry dust (#284): hot, fast, gravity-pulled
    // streaks in a cone off the surface normal, stretched along their velocity. Same
    // runtime-created lifecycle as the dust burst — one short-lived object per impact event,
    // nothing per-frame.
    private void SpawnChainsawMetalSparksLocal(Vector3 hitPoint, Vector3 hitNormal)
    {
        Material sparkMaterial = ResolveChainsawSparkMaterial();
        if (sparkMaterial == null)
        {
            LogDiagnostic("Chainsaw metal sparks skipped; no supported particle material was available.");
            return;
        }

        Vector3 normal = hitNormal.sqrMagnitude > 0.001f ? hitNormal.normalized : -transform.forward;
        GameObject sparkObject = new GameObject("BundyChainsawMetalSparks");
        sparkObject.transform.position = hitPoint + normal * 0.03f;
        sparkObject.transform.rotation = Quaternion.LookRotation(normal, Vector3.up);

        ParticleSystem sparks = sparkObject.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = sparks.main;
        main.duration = 0.3f;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.42f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(2.6f, 5.4f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
        main.startColor = new ParticleSystem.MinMaxGradient(
            new Color(1f, 0.87f, 0.5f, 1f),
            new Color(1f, 0.55f, 0.16f, 1f));
        main.gravityModifier = 1.1f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 48;

        ParticleSystem.EmissionModule emission = sparks.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)ChainsawMetalSparkBurstCount) });

        ParticleSystem.ShapeModule shape = sparks.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 48f;
        shape.radius = 0.03f;

        ParticleSystemRenderer renderer = sparkObject.GetComponent<ParticleSystemRenderer>();
        if (renderer != null)
        {
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 5.5f;
            renderer.velocityScale = 0.04f;
            renderer.sortingFudge = 1f;
            renderer.sharedMaterial = sparkMaterial;
        }

        sparks.Play();
        Destroy(sparkObject, 1.2f);
    }

    // HDRP/Unlit does not multiply particle vertex color, so per-particle startColor tints are
    // ignored there (#373). The intended tint therefore lives on the material itself: one cached
    // material per effect, dust tinted masonry-grey and sparks tinted hot orange.
    private static readonly Color ChainsawWallDustMaterialColor = new Color(0.68f, 0.62f, 0.52f, 0.62f);
    private static readonly Color ChainsawMetalSparkMaterialColor = new Color(1f, 0.55f, 0.18f, 1f);

    private static Material ResolveChainsawWallDustMaterial()
    {
        if (!_searchedChainsawWallDustMaterial)
        {
            _searchedChainsawWallDustMaterial = true;
            _chainsawWallDustMaterial = CreateChainsawImpactParticleMaterial(
                "BundyChainsawWallDustMaterial", ChainsawWallDustMaterialColor);
        }

        return _chainsawWallDustMaterial;
    }

    private static Material ResolveChainsawSparkMaterial()
    {
        if (!_searchedChainsawSparkMaterial)
        {
            _searchedChainsawSparkMaterial = true;
            _chainsawSparkMaterial = CreateChainsawImpactParticleMaterial(
                "BundyChainsawSparkMaterial", ChainsawMetalSparkMaterialColor);
        }

        return _chainsawSparkMaterial;
    }

    private static Material CreateChainsawImpactParticleMaterial(string materialName, Color color)
    {
        string[] shaderNames =
        {
            "HDRP/Unlit",
            "High Definition Render Pipeline/Unlit",
            "Unlit/Color",
            "Sprites/Default",
            "Standard",
            "Legacy Shaders/Particles/Alpha Blended",
        };

        for (int i = 0; i < shaderNames.Length; i++)
        {
            Shader shader = Shader.Find(shaderNames[i]);
            if (shader == null || !shader.isSupported)
                continue;

            Material material = new Material(shader)
            {
                name = materialName,
                hideFlags = HideFlags.HideAndDontSave
            };
            ConfigureChainsawImpactParticleMaterial(material, color);
            return material;
        }

        try
        {
            Material[] materials = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null || material.shader == null || !material.shader.isSupported)
                    continue;

                string foundName = material.name ?? string.Empty;
                string shaderName = material.shader.name ?? string.Empty;
                if (foundName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) < 0 &&
                    shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                // Copy before tinting; the found material is live scene data shared with its owners.
                Material copy = new Material(material)
                {
                    name = materialName,
                    hideFlags = HideFlags.HideAndDontSave
                };
                ConfigureChainsawImpactParticleMaterial(copy, color);
                return copy;
            }
        }
        catch
        {
            // If resource scanning is blocked by Unity's load state, skip dust instead of spawning pink particles.
        }

        return null;
    }

    private static void ConfigureChainsawImpactParticleMaterial(Material material, Color color)
    {
        if (material == null)
            return;

        if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        if (material.HasProperty("_UnlitColor"))
            material.SetColor("_UnlitColor", color);

        // HDRP/Unlit defaults to opaque; flip it to alpha-blended transparent so the tint alpha
        // (dust at ~0.62) actually blends instead of rendering as a solid quad.
        if (material.HasProperty("_SurfaceType"))
        {
            material.SetFloat("_SurfaceType", 1f);
            if (material.HasProperty("_BlendMode"))
                material.SetFloat("_BlendMode", 0f);
            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite"))
                material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        material.renderQueue = 3000;
    }

    private void BeginChainsawWallStuckPauseLocal(float seconds)
    {
        if (_chainsawWallStuckPauseRoutine != null)
            StopCoroutine(_chainsawWallStuckPauseRoutine);

        _chainsawWallStuckPauseRoutine = StartCoroutine(ChainsawWallStuckPauseRoutine(seconds));
    }

    private IEnumerator ChainsawWallStuckPauseRoutine(float seconds)
    {
        ResolveRuntimeReferences();
        Animator animator = creatureAnimator;
        if (animator == null)
        {
            _chainsawWallStuckPauseRoutine = null;
            yield break;
        }

        float originalSpeed = animator.speed;
        animator.speed = ChainsawWallImpactAnimatorSpeed;
        yield return new WaitForSeconds(Mathf.Max(0.05f, seconds));

        if (animator != null && Mathf.Abs(animator.speed - ChainsawWallImpactAnimatorSpeed) <= 0.02f)
            animator.speed = originalSpeed > 0.01f ? originalSpeed : 1f;

        _chainsawWallStuckPauseRoutine = null;
    }

    private void EnterAttackPhase(AttackPhase phase, string source)
    {
        if (_attackPhase == phase)
            return;

        _attackPhase = phase;
        if (phase == AttackPhase.Active)
            _hasLastAttackContactPoint = false;
        else if (phase == AttackPhase.Recovery)
            BeginAttackRecoveryHold(source);

        if (DebugLogs)
            LogDiagnostic($"BundyAttackPhase phase={phase}, source={source}, activeWindow=({_attackActiveWindowStartAt - Time.time:F2},{_attackActiveWindowEndAt - Time.time:F2}), lunge={_lungeActive}, finisherLunge={_finisherLungeActive}");
    }

    private void BeginAttackRecoveryHold(string source)
    {
        if (_actionKind != ActionKind.Attack)
            return;

        bool wasLunging = _lungeActive || _finisherLungeActive;
        _lungeActive = false;
        _finisherLungeActive = false;
        ClearLungeAim();

        if (_attackVisualExitArmed && !_attackVisualExitApplied)
            _attackVisualReturnStateHash = BaseLayerIdleStateHash;

        HoldAgentWithoutReset();
        UpdateLocomotionSpeedMultiplier(false, false);
        SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);

        if (DebugLogs)
        {
            LogDiagnostic(
                $"BundyAttackRecoveryHold source={source}, wasLunging={wasLunging}, " +
                $"returnStateHash={_attackVisualReturnStateHash}, agentStopped={(agent != null && agent.enabled && agent.isOnNavMesh ? agent.isStopped.ToString() : "<n/a>")}");
        }
    }

}
