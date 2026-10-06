using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Congregation;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>#845 F: the legacy lgucontracts Seraphim's materials, built once on the prefab at
    /// asset load. Every slot is HDRP/Lit, Transparent with alpha 1, depth write and the transparent
    /// depth prepass on (so it still sorts and occludes like a solid body) and Receive Fog off.
    /// The validated sidecar keeps its authored materials instead: an opaque body that fogs like
    /// terrain and a fog-free central eye (#845 M2).
    /// Instances share these materials; nothing is cloned per spawn.</summary>
    internal static partial class ContractAssetLoader
    {
        private const string SeraphimLitMaterialPrefix = "SeraphimLit_";
        private const float SeraphimRendererBoundsSize = 240f;
        private const float SeraphimBodySmoothness = 0.34f;
        private const float SeraphimEyeSmoothness = 0.82f;
        private const float SeraphimCutoutThreshold = 0.06f;
        private const float SeraphimCutoutPrepassThreshold = 0.5f;
        private const float SeraphimOverlayDefaultAlpha = 0.16f;
        private static readonly Color SeraphimEyeGlowLdr = new Color(0.68f, 0.86f, 1f, 1f);
        // #1336: the sidecar central eye's 7.5x step (1.6 -> 12). Legacy shares one Eye material across
        // all eyes, so its ring eyes rise with the central eye.
        private const float SeraphimEyeGlowIntensity = 5.625f;
        private static bool _seraphimDegenerateBoundsLogged;

        private enum SeraphimSlotKind
        {
            Body,
            Cutout,
            Eye,
            Overlay,
        }

        private static void PrepareSeraphimMaterials(GameObject prefab)
        {
            if (_seraphimBundle != null)
                return; // Authored sidecar materials were prepared before atomic publication.

            Shader lit = ResolveSeraphimLitShader(prefab);
            if (lit == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] HDRP/Lit not found at load; the Seraphim keeps its bundle materials.");
                return;
            }

            var built = new Dictionary<Material, Material>();
            var expandedMeshes = new HashSet<Mesh>();
            Material builtForMissing = null;
            var slotLog = new StringBuilder();
            int renderers = 0;
            int slots = 0;
            foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer is LineRenderer || renderer is ParticleSystemRenderer)
                    continue;
                if (renderer.GetComponentInParent<ScanNodeProperties>() != null)
                    continue;

                renderer.enabled = true;
                renderer.forceRenderingOff = false;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.allowOcclusionWhenDynamic = false;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    skinned.updateWhenOffscreen = true;
                    skinned.localBounds = ResolveSeraphimRendererBounds(skinned.localBounds, skinned.name);
                }
                else
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    // Renderers can share a mesh; its bounds are widened once.
                    if (filter != null && filter.sharedMesh != null && expandedMeshes.Add(filter.sharedMesh))
                        filter.sharedMesh.bounds = ResolveSeraphimRendererBounds(filter.sharedMesh.bounds, renderer.name);
                }

                Material[] sources = renderer.sharedMaterials;
                if (sources == null || sources.Length == 0)
                    sources = new Material[1];

                var runtime = new Material[sources.Length];
                for (int i = 0; i < sources.Length; i++)
                {
                    Material source = sources[i];
                    Material material;
                    if (source != null && (source.name ?? string.Empty).StartsWith(SeraphimLitMaterialPrefix, StringComparison.Ordinal))
                    {
                        material = source;
                    }
                    else if (source == null)
                    {
                        material = builtForMissing ?? (builtForMissing = BuildSeraphimMaterial(lit, null, slotLog));
                    }
                    else if (!built.TryGetValue(source, out material))
                    {
                        material = BuildSeraphimMaterial(lit, source, slotLog);
                        built[source] = material;
                    }

                    runtime[i] = material;
                }

                renderer.sharedMaterials = runtime;
                renderers++;
                slots += runtime.Length;
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Seraphim materials built at load: renderers={renderers}, slots={slots}, unique={built.Count + (builtForMissing != null ? 1 : 0)}, receiveFog=false;{slotLog}");
        }

        private static void PrepareAuthoredSeraphimMaterials(GameObject prefab)
        {
            // The builder authored and the sidecar validation checked every material: opaque body
            // and cutout cards receive fog, the transparent central eye skips it. Only renderer
            // policy is applied here; spawns continue sharing these authored materials.
            foreach (SkinnedMeshRenderer renderer in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                renderer.enabled = true;
                renderer.forceRenderingOff = false;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.allowOcclusionWhenDynamic = false;
                // Off-screen updates stay on for the rise (it starts underground and the Root
                // correction moves the pose); SeraphimEnemyAI turns them off afterwards and culls
                // against this box.
                renderer.updateWhenOffscreen = true;
                renderer.localBounds = ResolveSeraphimRendererBounds(renderer.localBounds, renderer.name);
            }
        }

        /// <summary>#1336 S3: the authored box, widened by <see cref="SeraphimFrameBudget.RendererBoundsMargin"/>
        /// for the animated rings, replaces the 240 m cube that kept the body from ever being culled.
        /// A degenerate authored box keeps that cube.</summary>
        private static Bounds ResolveSeraphimRendererBounds(Bounds authored, string rendererName)
        {
            Vector3 extents = authored.extents;
            if (SeraphimFrameBudget.IsBoundsExtentUsable(extents.x, extents.y, extents.z))
            {
                var expanded = new Vector3(
                    SeraphimFrameBudget.ExpandExtent(extents.x),
                    SeraphimFrameBudget.ExpandExtent(extents.y),
                    SeraphimFrameBudget.ExpandExtent(extents.z));
                return new Bounds(authored.center, expanded * 2f);
            }

            if (!_seraphimDegenerateBoundsLogged)
            {
                _seraphimDegenerateBoundsLogged = true;
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts] Seraphim renderer '{rendererName}' has a degenerate authored bounds box (extents {extents.ToString("F3")}); it keeps the {SeraphimRendererBoundsSize:0} m local bounds.");
            }
            return new Bounds(Vector3.zero, Vector3.one * SeraphimRendererBoundsSize);
        }

        private static Shader ResolveSeraphimLitShader(GameObject prefab)
        {
            Shader shader = Shader.Find("HDRP/Lit");
            if (shader != null)
                return shader;

            foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                    continue;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null && material.shader != null && string.Equals(material.shader.name, "HDRP/Lit", StringComparison.Ordinal))
                        return material.shader;
                }
            }

            return null;
        }

        private static Material BuildSeraphimMaterial(Shader lit, Material source, StringBuilder slotLog)
        {
            SeraphimSlotKind kind = ClassifySeraphimMaterial(source);
            string sourceName = source != null ? source.name ?? string.Empty : "missing";
            var material = new Material(lit)
            {
                name = SeraphimLitMaterialPrefix + sourceName,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };

            bool overlay = kind == SeraphimSlotKind.Overlay;
            bool eye = kind == SeraphimSlotKind.Eye || overlay;
            bool cutout = kind == SeraphimSlotKind.Cutout;

            Color tint = ResolveSeraphimMaterialTint(source);
            // Decision 3 is alpha 1; the tear/cornea overlay keeps its thin alpha over the iris
            // (it was never solid) and so writes no depth.
            tint.a = overlay ? ResolveSeraphimOverlayAlpha(source) : 1f;
            SetSeraphimColor(material, "_BaseColor", tint);
            SetSeraphimTexture(material, "_BaseColorMap", FindSeraphimAlbedoTexture(source, kind));
            Texture normal = FindSeraphimNormalTexture(source, kind);
            if (normal != null)
            {
                SetSeraphimTexture(material, "_NormalMap", normal);
                material.EnableKeyword("_NORMALMAP");
                material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            }

            SetSeraphimFloat(material, "_Metallic", 0f);
            SetSeraphimFloat(material, "_Smoothness", eye ? SeraphimEyeSmoothness : SeraphimBodySmoothness);

            // Transparent, alpha blend, depth write + prepass, fog off.
            material.SetOverrideTag("RenderType", "Transparent");
            SetSeraphimFloat(material, "_SurfaceType", 1f);
            SetSeraphimFloat(material, "_BlendMode", 0f);
            SetSeraphimFloat(material, "_SrcBlend", (float)BlendMode.SrcAlpha);
            SetSeraphimFloat(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            SetSeraphimFloat(material, "_AlphaSrcBlend", (float)BlendMode.One);
            SetSeraphimFloat(material, "_AlphaDstBlend", (float)BlendMode.OneMinusSrcAlpha);
            SetSeraphimFloat(material, "_ZWrite", overlay ? 0f : 1f);
            SetSeraphimFloat(material, "_TransparentZWrite", overlay ? 0f : 1f);
            SetSeraphimFloat(material, "_TransparentDepthPrepassEnable", overlay ? 0f : 1f);
            SetSeraphimFloat(material, "_EnableFogOnTransparent", 0f);
            SetSeraphimFloat(material, "_DoubleSidedEnable", 1f);
            SetSeraphimFloat(material, "_CullMode", (float)CullMode.Off);
            SetSeraphimFloat(material, "_CullModeForward", (float)CullMode.Off);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_BLENDMODE_ALPHA");
            material.EnableKeyword("_DOUBLESIDED_ON");
            material.DisableKeyword("_ENABLE_FOG_ON_TRANSPARENT");

            // Feather and eyelash cards keep their cutout: without it the prepass would write depth
            // for each card's empty texels and punch holes in the body behind them.
            SetSeraphimFloat(material, "_AlphaCutoffEnable", cutout ? 1f : 0f);
            if (cutout)
            {
                SetSeraphimFloat(material, "_AlphaCutoff", SeraphimCutoutThreshold);
                SetSeraphimFloat(material, "_AlphaCutoffPrepass", SeraphimCutoutPrepassThreshold);
                material.EnableKeyword("_ALPHATEST_ON");
            }
            else
            {
                material.DisableKeyword("_ALPHATEST_ON");
            }

            if (kind == SeraphimSlotKind.Eye)
            {
                Texture emission = SeraphimEyeEmissionTexture ?? SeraphimEyeVar2EmissionTexture ?? SeraphimEyeAlbedoTexture ?? SeraphimEyeVar2AlbedoTexture;
                Color glow = SeraphimEyeGlowLdr * SeraphimEyeGlowIntensity;
                glow.a = 1f;
                SetSeraphimTexture(material, "_EmissiveColorMap", emission);
                SetSeraphimColor(material, "_EmissiveColorLDR", SeraphimEyeGlowLdr);
                SetSeraphimColor(material, "_EmissiveColor", glow);
                // Off, so ValidateMaterial keeps _EmissiveColor as set instead of recomputing it
                // from LDR x intensity; exposure weight 0 keeps the eyes reading at night and noon
                // the way the old unlit eye did.
                SetSeraphimFloat(material, "_UseEmissiveIntensity", 0f);
                SetSeraphimFloat(material, "_EmissiveExposureWeight", 0f);
                if (emission != null)
                    material.EnableKeyword("_EMISSIVE_COLOR_MAP");
            }
            else
            {
                SetSeraphimColor(material, "_EmissiveColor", Color.black);
                SetSeraphimColor(material, "_EmissiveColorLDR", Color.black);
                material.DisableKeyword("_EMISSIVE_COLOR_MAP");
            }

            material.renderQueue = (int)RenderQueue.Transparent;
            try
            {
                HDMaterial.ValidateMaterial(material);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Seraphim material validation skipped for '{material.name}': {e.Message}");
            }

            slotLog.Append(' ').Append(sourceName).Append("->").Append(material.shader != null ? material.shader.name : "none")
                .Append('(').Append(kind).Append(')');
            return material;
        }

        /// <summary>Pre-touches what the first summon would otherwise pay for: shader support, mesh
        /// metadata and the Seraphim's shared beam line resources.</summary>
        private static void PrewarmSeraphimPrefab(GameObject prefab)
        {
            if (prefab == null)
                return;

            var perf = System.Diagnostics.Stopwatch.StartNew();
            int renderers = 0;
            int materials = 0;
            int meshes = 0;
            try
            {
                foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null)
                        continue;

                    renderers++;
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null)
                            continue;
                        materials++;
                        if (material.shader != null)
                            _ = material.shader.isSupported;
                    }

                    if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                    {
                        meshes++;
                        _ = skinned.sharedMesh.vertexCount;
                    }
                    else
                    {
                        MeshFilter filter = renderer.GetComponent<MeshFilter>();
                        if (filter != null && filter.sharedMesh != null)
                        {
                            meshes++;
                            _ = filter.sharedMesh.vertexCount;
                        }
                    }
                }

                SeraphimEnemyAI.PrewarmSharedVisuals();
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Seraphim prefab prewarmed: renderers={renderers} materials={materials} meshes={meshes} elapsed={perf.Elapsed.TotalMilliseconds:0.00}ms.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts] Seraphim prefab prewarm skipped: {e.Message}");
            }
        }

        private static SeraphimSlotKind ClassifySeraphimMaterial(Material material)
        {
            if (IsSeraphimNamedMaterial(material, "eyelash") || IsSeraphimNamedMaterial(material, "feather"))
                return SeraphimSlotKind.Cutout;
            if (IsSeraphimNamedMaterial(material, "tear") || IsSeraphimNamedMaterial(material, "cornea") || IsSeraphimNamedMaterial(material, "lens"))
                return SeraphimSlotKind.Overlay;
            if (IsSeraphimNamedMaterial(material, "eye")
                || IsSeraphimNamedMaterial(material, "iris")
                || IsSeraphimNamedMaterial(material, "pupil")
                || IsSeraphimNamedMaterial(material, "sclera"))
            {
                return SeraphimSlotKind.Eye;
            }

            return SeraphimSlotKind.Body;
        }

        private static Color ResolveSeraphimMaterialTint(Material source)
        {
            if (IsSeraphimNamedMaterial(source, "eyelash"))
                return new Color(0.18f, 0.13f, 0.13f, 1f);
            if (IsSeraphimNamedMaterial(source, "eye"))
                return Color.white;
            if (IsSeraphimNamedMaterial(source, "tear"))
                return new Color(0.75f, 0.9f, 1f, SeraphimOverlayDefaultAlpha);
            if (IsSeraphimNamedMaterial(source, "ring"))
                return new Color(1f, 0.84f, 0.78f, 1f);
            if (IsSeraphimNamedMaterial(source, "feather"))
                return new Color(0.88f, 0.84f, 0.82f, 1f);
            if (IsSeraphimNamedMaterial(source, "body"))
                return new Color(1f, 0.80f, 0.74f, 1f);
            return Color.white;
        }

        private static float ResolveSeraphimOverlayAlpha(Material source)
        {
            float alpha = SeraphimOverlayDefaultAlpha;
            if (source != null && source.HasProperty("_BaseColor"))
                alpha = source.GetColor("_BaseColor").a;
            else if (source != null && source.HasProperty("_Color"))
                alpha = source.GetColor("_Color").a;
            return Mathf.Clamp(alpha, 0.04f, 0.32f);
        }

        private static Texture FindSeraphimAlbedoTexture(Material source, SeraphimSlotKind kind)
        {
            Texture named = null;
            if (IsSeraphimNamedMaterial(source, "feather"))
                named = SeraphimFeatherAlbedoTexture;
            else if (IsSeraphimNamedMaterial(source, "eyelash"))
                named = SeraphimEyelashAlbedoTexture;
            else if (IsSeraphimNamedMaterial(source, "body"))
                named = SeraphimBodyAlbedoTexture ?? SeraphimBodyVar2AlbedoTexture;
            else if (IsSeraphimNamedMaterial(source, "ring"))
                named = SeraphimRingAlbedoTexture ?? SeraphimRingVar2AlbedoTexture;
            else if (kind == SeraphimSlotKind.Eye || kind == SeraphimSlotKind.Overlay)
                named = SeraphimEyeAlbedoTexture ?? SeraphimEyeVar2AlbedoTexture;

            return named != null
                ? named
                : FindSeraphimSourceTexture(source, new[] { "_BaseColorMap", "_BaseMap", "_MainTex", "_ColorMap", "_UnlitColorMap" }, "albedo", "base", "color");
        }

        private static Texture FindSeraphimNormalTexture(Material source, SeraphimSlotKind kind)
        {
            Texture named = null;
            if (IsSeraphimNamedMaterial(source, "feather"))
                named = SeraphimFeatherNormalTexture;
            else if (IsSeraphimNamedMaterial(source, "body"))
                named = SeraphimBodyVar2NormalTexture ?? SeraphimBodyNormalTexture;
            else if (IsSeraphimNamedMaterial(source, "ring"))
                named = SeraphimRingNormalTexture;
            else if (kind == SeraphimSlotKind.Eye || kind == SeraphimSlotKind.Overlay)
                named = SeraphimEyeNormalTexture ?? SeraphimEyeVar2NormalTexture;

            return named != null
                ? named
                : FindSeraphimSourceTexture(source, new[] { "_NormalMap", "_BumpMap" }, "normal");
        }

        /// <summary>The bundle material's own texture: the known slots first, then any texture
        /// property whose name carries one of the tokens.</summary>
        private static Texture FindSeraphimSourceTexture(Material source, string[] properties, params string[] nameTokens)
        {
            if (source == null)
                return null;

            for (int i = 0; i < properties.Length; i++)
            {
                if (source.HasProperty(properties[i]))
                {
                    Texture texture = source.GetTexture(properties[i]);
                    if (texture != null)
                        return texture;
                }
            }

            string[] names;
            try
            {
                names = source.GetTexturePropertyNames();
            }
            catch
            {
                return null;
            }

            foreach (string property in names)
            {
                if (string.IsNullOrEmpty(property))
                    continue;
                for (int t = 0; t < nameTokens.Length; t++)
                {
                    if (property.IndexOf(nameTokens[t], StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    Texture texture = source.GetTexture(property);
                    if (texture != null)
                        return texture;
                }
            }

            return null;
        }

        private static bool IsSeraphimNamedMaterial(Material material, string token)
        {
            return material != null && (material.name ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void SetSeraphimFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private static void SetSeraphimColor(Material material, string property, Color value)
        {
            if (material.HasProperty(property))
                material.SetColor(property, value);
        }

        private static void SetSeraphimTexture(Material material, string property, Texture texture)
        {
            if (texture != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }
    }
}
