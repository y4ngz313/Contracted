using System;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy;

public sealed class BundySkinMaterialDiagnostics : MonoBehaviour
{
    public bool diagnosticsEnabled = true;
    public bool lowSpecTest = false;
    public bool normalOffTest = false;
    public bool matteSkinTest = false;
    public bool headMatchTest = false;

    private bool _ran;

    private void Start()
    {
        RunOnce("component-start", force: false);
    }

    public void RunNow(string source, bool force = false)
    {
        RunOnce(source, force);
    }

    private void RunOnce(string source, bool force)
    {
        if (_ran && !force)
            return;

        _ran = true;
        RunMaterialPass(
            transform,
            message => Debug.Log(message),
            diagnosticsEnabled,
            lowSpecTest,
            normalOffTest,
            matteSkinTest,
            source,
            headMatchTest);
    }

    public static int RunMaterialPass(
        Transform root,
        Action<string> log,
        bool diagnosticsEnabled,
        bool lowSpecTest,
        bool normalOffTest,
        bool matteSkinTest,
        string source,
        bool headMatchTest = false)
    {
        bool applyTest = lowSpecTest || normalOffTest || matteSkinTest || headMatchTest;
        bool shouldLog = BundyPlugin.DebugLogsEnabled || applyTest;
        if (!diagnosticsEnabled && !applyTest)
            return 0;

        if (root == null)
        {
            if (shouldLog)
                log?.Invoke(
                    $"[BundyMaterialDiag] complete source={source}, root='<null>', matchedMaterials=0, " +
                    $"runtimeTestMaterials=0, lowSpecTest={lowSpecTest}, normalOffTest={normalOffTest}, " +
                    $"matteSkinTest={matteSkinTest}, diagnosticsEnabled={diagnosticsEnabled}");
            return 0;
        }

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        int matched = 0;
        int runtimeTestMaterials = 0;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
                continue;

            Material[] sharedMaterials = renderer.sharedMaterials;
            if (sharedMaterials == null || sharedMaterials.Length == 0)
                continue;

            for (int slot = 0; slot < sharedMaterials.Length; slot++)
            {
                Material material = sharedMaterials[slot];
                if (material == null || !IsBundySkinMaterial(renderer, material))
                    continue;

                matched++;
                if (shouldLog && diagnosticsEnabled)
                    LogMaterial(log, "shared", renderer, slot, material);
            }

            if (applyTest && RendererHasBundySkinMaterial(renderer, sharedMaterials))
                runtimeTestMaterials += ApplyRuntimeMaterialTests(renderer, log, lowSpecTest, normalOffTest, matteSkinTest, headMatchTest);
        }

        if (shouldLog)
        {
            log?.Invoke(
                $"[BundyMaterialDiag] complete source={source}, root='{root.name}', matchedMaterials={matched}, " +
                $"runtimeTestMaterials={runtimeTestMaterials}, rendererCount={renderers.Length}, " +
                $"lowSpecTest={lowSpecTest}, normalOffTest={normalOffTest}, matteSkinTest={matteSkinTest}, " +
                $"diagnosticsEnabled={diagnosticsEnabled}");
        }

        return matched;
    }

    private static int ApplyRuntimeMaterialTests(
        Renderer renderer,
        Action<string> log,
        bool lowSpecTest,
        bool normalOffTest,
        bool matteSkinTest,
        bool headMatchTest)
    {
        Material[] materials = renderer.materials;
        if (materials == null || materials.Length == 0)
            return 0;

        int applied = 0;
        for (int slot = 0; slot < materials.Length; slot++)
        {
            Material material = materials[slot];
            if (material == null || !IsBundySkinMaterial(renderer, material))
                continue;

            if (lowSpecTest)
                ApplyLowSpecTest(material);
            if (normalOffTest)
                ApplyNormalOffTest(material);
            if (matteSkinTest)
                ApplyMatteSkinTest(material);
            if (headMatchTest && IsBundyHeadMaterial(material))
                ApplyHeadMatchTest(material);

            if (BundyPlugin.DebugLogsEnabled || lowSpecTest || normalOffTest || matteSkinTest || headMatchTest)
                LogMaterial(log, "runtime-test", renderer, slot, material);
            applied++;
        }

        return applied;
    }

    private static void ApplyLowSpecTest(Material material)
    {
        SetFloatIfPresent(material, "_Metallic", 0f);
        SetFloatIfPresent(material, "_Smoothness", 0.28f);
        SetFloatIfPresent(material, "_SmoothnessRemapMin", 0f);
        SetFloatIfPresent(material, "_SmoothnessRemapMax", 0.28f);
        SetFloatIfPresent(material, "_Glossiness", 0.28f);
        SetFloatIfPresent(material, "_SpecularHighlights", 0f);
        SetFloatIfPresent(material, "_EnvironmentReflections", 0f);
    }

    private static void ApplyNormalOffTest(Material material)
    {
        SetFloatIfPresent(material, "_NormalScale", 0f);
        SetFloatIfPresent(material, "_BumpScale", 0f);
    }

    private static void ApplyMatteSkinTest(Material material)
    {
        SetFloatIfPresent(material, "_Metallic", 0f);
        SetFloatIfPresent(material, "_Smoothness", 0.12f);
        SetFloatIfPresent(material, "_SmoothnessRemapMin", 0f);
        SetFloatIfPresent(material, "_SmoothnessRemapMax", 0.16f);
        SetFloatIfPresent(material, "_Glossiness", 0.12f);
        SetFloatIfPresent(material, "_SpecularHighlights", 0f);
        SetFloatIfPresent(material, "_EnvironmentReflections", 0f);
    }

    // 2026-07-22 editor audit: head 'Std_Skin_Head' ships smoothness 0.5 /
    // remap max 1.0 / normal scale 1.0 while the body skin runs 0.22 / 0.22 /
    // 0.45 — the glossier, busier head is a large part of the neck graft seam.
    // This test drags the head response down onto the measured body values.
    private static void ApplyHeadMatchTest(Material material)
    {
        SetFloatIfPresent(material, "_Metallic", 0f);
        SetFloatIfPresent(material, "_Smoothness", 0.22f);
        SetFloatIfPresent(material, "_SmoothnessRemapMin", 0f);
        SetFloatIfPresent(material, "_SmoothnessRemapMax", 0.22f);
        SetFloatIfPresent(material, "_Glossiness", 0.22f);
        SetFloatIfPresent(material, "_NormalScale", 0.45f);
        SetFloatIfPresent(material, "_BumpScale", 0.45f);
    }

    private static bool IsBundyHeadMaterial(Material material)
    {
        if (material == null)
            return false;
        string materialName = material.name ?? string.Empty;
        return materialName.IndexOf("Std_Skin_Head", StringComparison.OrdinalIgnoreCase) >= 0 ||
            materialName.IndexOf("Bundy_CC5_HeadSkin", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool RendererHasBundySkinMaterial(Renderer renderer, Material[] materials)
    {
        for (int i = 0; i < materials.Length; i++)
        {
            if (IsBundySkinMaterial(renderer, materials[i]))
                return true;
        }

        return false;
    }

    internal static bool IsBundyBodySkinMaterial(Renderer renderer, Material material)
    {
        if (renderer == null || material == null)
            return false;

        string rendererName = renderer.name ?? string.Empty;
        string materialName = material.name ?? string.Empty;
        return rendererName.IndexOf("Bundy_WorkingMesh_REWEIGHT_ME", StringComparison.OrdinalIgnoreCase) >= 0 ||
            rendererName.IndexOf("Bundy_WorkingMesh", StringComparison.OrdinalIgnoreCase) >= 0 ||
            rendererName.IndexOf("_R33_Bundy_Body", StringComparison.OrdinalIgnoreCase) >= 0 ||
            rendererName.IndexOf("_R27_Bundy_Body", StringComparison.OrdinalIgnoreCase) >= 0 ||
            materialName.IndexOf("Bundy_Mat", StringComparison.OrdinalIgnoreCase) >= 0 ||
            materialName.IndexOf("Bundy_AccuRig", StringComparison.OrdinalIgnoreCase) >= 0 ||
            materialName.IndexOf("Bundy_R27_Skin", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsBundySkinMaterial(Renderer renderer, Material material)
    {
        if (renderer == null || material == null)
            return false;

        string rendererName = renderer.name ?? string.Empty;
        string materialName = material.name ?? string.Empty;
        return IsBundyBodySkinMaterial(renderer, material) ||
            rendererName.IndexOf("BUNDY_PROOF_CC_Base_Head", StringComparison.OrdinalIgnoreCase) >= 0 ||
            materialName.IndexOf("Bundy_CC5_HeadSkin", StringComparison.OrdinalIgnoreCase) >= 0 ||
            materialName.IndexOf("Std_Skin_Head", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void LogMaterial(Action<string> log, string mode, Renderer renderer, int slot, Material material)
    {
        string shaderName = material.shader != null ? material.shader.name : "<missing>";
        log?.Invoke(
            $"[BundyMaterialDiag] mode={mode}, renderer='{GetPath(renderer.transform.root, renderer.transform)}', slot={slot}, " +
            $"material='{material.name}', shader='{shaderName}', " +
            $"baseMap={DescribeTexture(material, "_BaseColorMap", "_BaseMap", "_MainTex")}, " +
            $"normalMap={DescribeTexture(material, "_NormalMap", "_BumpMap")}, " +
            $"maskMap={DescribeTexture(material, "_MaskMap", "_MetallicGlossMap")}, " +
            $"metallic={GetFloatValue(material, "_Metallic")}, smoothness={GetFloatValue(material, "_Smoothness")}, " +
            $"smoothnessRemapMin={GetFloatValue(material, "_SmoothnessRemapMin")}, " +
            $"smoothnessRemapMax={GetFloatValue(material, "_SmoothnessRemapMax")}, " +
            $"glossiness={GetFloatValue(material, "_Glossiness")}, " +
            $"normalScale={GetFloatValue(material, "_NormalScale", "_BumpScale")}, " +
            $"specularHighlights={GetFloatValue(material, "_SpecularHighlights")}, " +
            $"lowSpecTest={BundyPlugin.ModConfig?.ChainsawMaterialLowSpecTest?.Value ?? false}, " +
            $"normalOffTest={BundyPlugin.ModConfig?.ChainsawMaterialNormalOffTest?.Value ?? false}, " +
            $"matteSkinTest={BundyPlugin.ModConfig?.ChainsawMaterialMatteSkinTest?.Value ?? false}, " +
            $"headMatchTest={BundyPlugin.ModConfig?.ChainsawMaterialHeadMatchTest?.Value ?? false}");
    }

    private static string DescribeTexture(Material material, params string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            string prop = names[i];
            if (!material.HasProperty(prop))
                continue;

            Texture texture = material.GetTexture(prop);
            if (texture != null)
                return prop + ":" + FormatTexture(texture);
        }

        return "<none>";
    }

    private static string FormatTexture(Texture texture)
    {
        if (texture == null)
            return "<null>";

        Texture2D texture2D = texture as Texture2D;
        string format = texture2D != null ? texture2D.format.ToString() : texture.GetType().Name;
        string mipmapCount = texture2D != null ? texture2D.mipmapCount.ToString() : "<n/a>";
        return $"{texture.name}[{texture.width}x{texture.height}, format={format}, mipmapCount={mipmapCount}, " +
            $"filter={texture.filterMode}, aniso={texture.anisoLevel}]";
    }

    private static string GetFloatValue(Material material, params string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            string prop = names[i];
            if (material.HasProperty(prop))
                return prop + ":" + material.GetFloat(prop).ToString("F3");
        }

        return "<missing>";
    }

    private static void SetFloatIfPresent(Material material, string prop, float value)
    {
        if (material.HasProperty(prop))
            material.SetFloat(prop, value);
    }

    private static string GetPath(Transform root, Transform target)
    {
        if (root == null || target == null)
            return "<null>";
        if (root == target)
            return target.name;

        string path = target.name;
        Transform current = target.parent;
        while (current != null && current != root)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        if (current == root)
            path = root.name + "/" + path;

        return path;
    }
}
