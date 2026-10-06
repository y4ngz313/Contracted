using System;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.Bundy.AI;

public abstract partial class BundyAIBase
{
    private const string BloodyChainsawMaterialAssetPath =
        "Assets/Chainsaw/Materials/ChainsawMatAltBlood.mat";

    private static readonly string[] FinisherSelfBloodBones =
    {
        "lowerarm_r",
        "hand_r",
        "spine_01"
    };

    // #716 G6: the bloody-chainsaw material was resolved per Bundy instance, so every new Bundy's
    // first cut paid a synchronous AssetBundle.LoadAsset (and, on a miss, a GetAllAssetNames scan)
    // mid-finisher. The bundle is a process-wide static, so one resolved material serves the
    // session; the null check re-resolves if the asset is ever destroyed under us.
    private static Material _bloodyChainsawMaterial;
    private static bool _bloodyChainsawMaterialUnavailable;
    private static bool _bloodyChainsawBundleWarned;
    private bool _bloodyChainsawApplied;

    /// <summary>Client-local persistent aftermath of a successful BloodFX finisher start.</summary>
    private void ApplyFinisherBloodConsequencesLocal(PlayerControllerB victim, int bodyIndex)
    {
        ApplyFinisherSelfBloodLocal(victim, bodyIndex);
        ApplyBloodyChainsawMaterialLocal();
    }

    private void ApplyFinisherSelfBloodLocal(PlayerControllerB victim, int bodyIndex)
    {
        if (!BundyBloodFXBridge.BodyDecalsReady)
            return;

        int cap = Mathf.Max(0, BundyBloodFXBridge.CreatureBodyDecalCap);
        if (cap == 0)
            return;

        _damageBloodSkeleton ??= new BundyBloodSkeleton();

        Vector3 outward = victim != null
            ? victim.transform.position - transform.position
            : transform.forward;
        outward.y = 0f;
        if (outward.sqrMagnitude <= 0.0001f)
            outward = transform.forward;
        if (outward.sqrMagnitude <= 0.0001f)
            outward = Vector3.forward;
        outward.Normalize();

        long channelKey = BundyBloodFXBridge.CreatureChannelFor(GetInstanceID());
        for (int i = 0; i < FinisherSelfBloodBones.Length; i++)
        {
            string boneName = FinisherSelfBloodBones[i];
            if (!_damageBloodSkeleton.TryResolveNamed(
                    creatureAnimator,
                    boneName,
                    out BundyBloodSkeleton.Match match))
            {
                BundyBloodFXBridge.Trace(
                    $"creature finisher self decal SKIPPED owner='{name}' bone='{boneName}' reason=unresolved.");
                continue;
            }

            bool final = BundyBloodFXBridge.BodyCount(channelKey) >= cap - 1;
            Vector3 surface = match.Anchor.position + outward * match.Profile.Radius;
            BundyBloodFXBridge.PlaceBodyDecalAt(
                channelKey,
                match.Anchor,
                surface,
                -outward,
                in match.Profile,
                final,
                cap,
                $"creature='{name}' source=finisher-self bodyIndex={bodyIndex} segment='{match.BoneName}'");
        }
    }

    private void ApplyBloodyChainsawMaterialLocal()
    {
        if (_bloodyChainsawApplied)
            return;

        Material bloodyMaterial = ResolveBloodyChainsawMaterial();
        if (bloodyMaterial == null)
            return;

        Transform prop = GetRuntimeChainsawProp();
        Renderer[] renderers = GetRuntimeChainsawRenderers(prop);
        if (prop == null || renderers == null || renderers.Length == 0)
        {
            LogWarning("Bloody chainsaw material not applied: runtime chainsaw renderers were unavailable.");
            return;
        }

        int changedRenderers = 0;
        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            Renderer renderer = renderers[rendererIndex];
            if (renderer == null || !IsRuntimeChainsawContactRenderer(renderer))
                continue;

            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                renderer.sharedMaterial = bloodyMaterial;
                changedRenderers++;
                continue;
            }

            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                materials[materialIndex] = bloodyMaterial;
            renderer.sharedMaterials = materials;
            changedRenderers++;
        }

        _bloodyChainsawApplied = changedRenderers > 0;
        if (_bloodyChainsawApplied)
        {
            LogInfo(
                $"Finisher chainsaw blood material applied: material='{bloodyMaterial.name}', " +
                $"renderers={changedRenderers}, prop='{prop.name}'.");
        }
        else
        {
            LogWarning("Bloody chainsaw material not applied: no mesh renderer accepted the swap.");
        }
    }

    private Material ResolveBloodyChainsawMaterial()
    {
        if (_bloodyChainsawMaterial != null)
            return _bloodyChainsawMaterial;

        if (_bloodyChainsawMaterialUnavailable)
            return null;

        AssetBundle bundle = BundyPlugin.ModAssets;
        if (bundle == null)
        {
            // Not latched: the bundle can still finish loading, but the warning is once per session.
            if (!_bloodyChainsawBundleWarned)
            {
                _bloodyChainsawBundleWarned = true;
                LogWarning("Bloody chainsaw material unavailable: Bundy's asset bundle is not loaded.");
            }

            return null;
        }

        _bloodyChainsawMaterial = bundle.LoadAsset<Material>(BloodyChainsawMaterialAssetPath);
        if (_bloodyChainsawMaterial == null)
            _bloodyChainsawMaterial = bundle.LoadAsset<Material>("ChainsawMatAltBlood");

        if (_bloodyChainsawMaterial == null)
        {
            string[] assetNames = bundle.GetAllAssetNames();
            for (int i = 0; i < assetNames.Length; i++)
            {
                string assetName = assetNames[i];
                if (assetName.IndexOf("ChainsawMatAltBlood", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                _bloodyChainsawMaterial = bundle.LoadAsset<Material>(assetName);
                if (_bloodyChainsawMaterial != null)
                    break;
            }
        }

        if (_bloodyChainsawMaterial == null)
        {
            // The bundle is loaded and does not contain the asset: latch so no later cut repeats
            // the load attempt or the warning.
            _bloodyChainsawMaterialUnavailable = true;
            LogWarning(
                $"Bloody chainsaw material unavailable: '{BloodyChainsawMaterialAssetPath}' was not in the bundle.");
        }

        return _bloodyChainsawMaterial;
    }
}
