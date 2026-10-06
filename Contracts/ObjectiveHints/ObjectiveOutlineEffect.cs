using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts.ObjectiveHints
{
    /// <summary>
    /// #1197. Contracted's copy of the outline pass LethalCCTV ships
    /// (<c>src/LethalCCTV/ShipSystems/Surveillance/OutlineEffect/OutlineEffect.cs</c>), in its own
    /// namespace and under its own type names so the two registries never cross. It uses the same
    /// buffer and overlay shaders, the same mask materials and the same HDRP composite, so a hint
    /// reads exactly like a squad ping.
    ///
    /// <para>Trimmed to what the local gameplay camera needs: HDRP only (the game ships no other
    /// pipeline, so the built-in-pipeline blit and its legacy composite material are gone), no
    /// scene search when enabled (outlines register themselves), a <see cref="List{T}"/> instead of
    /// <c>LinkedSet</c> so the per-frame draw loop allocates nothing, and a <see cref="Lit"/> gate
    /// the hint driver flips on the card's word-pulse clock. The mask draws every registered
    /// renderer whether or not it is enabled, which is what keeps a renderer CullFactory has
    /// switched off outlined, and the mask has no scene depth, which is what draws it through
    /// walls.</para>
    ///
    /// <para>Created for one blink window. Destroying the component destroys everything it made:
    /// the custom pass volume (targeted at the gameplay camera only), the render texture and every
    /// material.</para>
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class ObjectiveOutlineEffect : MonoBehaviour
    {
        private static readonly List<ObjectiveOutline> Outlines = new List<ObjectiveOutline>();

        internal static void RegisterOutline(ObjectiveOutline outline)
        {
            if (outline != null && !Outlines.Contains(outline))
                Outlines.Add(outline);
        }

        internal static void UnregisterOutline(ObjectiveOutline outline)
        {
            Outlines.Remove(outline);
        }

        /// <summary>Drops every registration; used when the live effect dies with its camera.</summary>
        internal static void ClearRegistry()
        {
            Outlines.Clear();
        }

        private static readonly int LineThicknessXId = Shader.PropertyToID("_LineThicknessX");
        private static readonly int LineThicknessYId = Shader.PropertyToID("_LineThicknessY");
        private static readonly int LineIntensityId = Shader.PropertyToID("_LineIntensity");
        private static readonly int LineColor1Id = Shader.PropertyToID("_LineColor1");
        private static readonly int LineColor2Id = Shader.PropertyToID("_LineColor2");
        private static readonly int LineColor3Id = Shader.PropertyToID("_LineColor3");
        private static readonly int CornerOutlinesId = Shader.PropertyToID("_CornerOutlines");
        private static readonly int OutlineAlphaCutoffId = Shader.PropertyToID("_OutlineAlphaCutoff");
        private static readonly int OutlineSourceId = Shader.PropertyToID("_OutlineSource");
        private static readonly int CullingId = Shader.PropertyToID("_Culling");
        private static readonly int CullId = Shader.PropertyToID("_Cull");

        private static readonly string[] MaskPassNames =
        {
            "ForwardOnly",
            "Forward",
            "SRPDefaultUnlit",
            "DepthForwardOnly"
        };

        internal float lineThickness = 1.25f;
        internal float lineIntensity = 0.5f;
        internal Color lineColor0 = Color.red;
        internal Color lineColor1 = Color.green;
        internal Color lineColor2 = Color.blue;
        internal bool backfaceCulling = true;
        internal bool cornerOutlines;
        internal bool scaleWithScreenSize = true;
        internal float alphaCutoff = 0.5f;
        internal Camera sourceCamera;

        /// <summary>The blink gate. False draws nothing; the driver sets it every frame of the window.</summary>
        internal bool Lit;

        private RenderTexture _renderTexture;
        private Material _overlayMaterial;
        private Material _outline1Material;
        private Material _outline2Material;
        private Material _outline3Material;
        private Material _outlineEraseMaterial;
        private Shader _bufferShader;
        private Shader _overlayShader;
        private GameObject _customPassVolumeObject;
        private CustomPassVolume _customPassVolume;
        private Y4NGZHDRPObjectiveOutlinePass _customPass;
        private bool _outlineTextureValid;
        /// <summary>Every mask material shares the buffer shader, so its pass is resolved once.</summary>
        private int _maskPassIndex = -1;

        private void Start()
        {
            CreateMaterialsIfNeeded();
            UpdateMaterialsPublicProperties();
            if (sourceCamera == null)
                sourceCamera = GetComponent<Camera>();

            RecreateRenderTextureIfNeeded();
            EnsureHdrpCustomPass();
        }

        private void OnEnable()
        {
            EnsureHdrpCustomPass();
        }

        private void OnDisable()
        {
            if (_customPassVolume != null)
                _customPassVolume.enabled = false;
        }

        private void OnDestroy()
        {
            if (_customPassVolumeObject != null)
            {
                Destroy(_customPassVolumeObject);
                _customPassVolumeObject = null;
                _customPassVolume = null;
                _customPass = null;
            }

            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Destroy(_renderTexture);
                _renderTexture = null;
            }

            DestroyMaterials();
            // If this is the live hint effect dying with its camera rather than through the driver's
            // Clear, the driver removes the outlines, the registry entries and the camera bit too.
            ObjectiveHintOutlines.NotifyEffectDestroyed(this);
        }

        private bool HasActiveOutlines()
        {
            for (int i = 0; i < Outlines.Count; i++)
            {
                ObjectiveOutline outline = Outlines[i];
                if (outline != null && outline.enabled && outline.Renderer != null)
                    return true;
            }

            return false;
        }

        private void EnsureHdrpCustomPass()
        {
            if (GraphicsSettings.currentRenderPipeline == null)
                return;

            Camera camera = ResolveSourceCamera();
            if (camera == null)
                return;

            if (_customPassVolume != null && _customPass != null)
            {
                _customPass.Owner = this;
                _customPassVolume.targetCamera = camera;
                _customPassVolume.enabled = isActiveAndEnabled;
                return;
            }

            if (_customPassVolumeObject == null)
            {
                _customPassVolumeObject = new GameObject("Y4NGZ_ObjectiveOutlineCustomPassVolume")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                DontDestroyOnLoad(_customPassVolumeObject);
            }

            _customPassVolume = _customPassVolumeObject.GetComponent<CustomPassVolume>();
            if (_customPassVolume == null)
                _customPassVolume = _customPassVolumeObject.AddComponent<CustomPassVolume>();

            _customPassVolume.isGlobal = true;
            _customPassVolume.priority = 1000f;
            _customPassVolume.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;
            _customPassVolume.targetCamera = camera;
            _customPassVolume.customPasses.Clear();

            CustomPass pass = _customPassVolume.AddPassOfType(typeof(Y4NGZHDRPObjectiveOutlinePass));
            _customPass = pass as Y4NGZHDRPObjectiveOutlinePass;
            if (_customPass == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Hints] The objective outline could not register its HDRP custom pass; only the card word will pulse.");
                return;
            }

            _customPass.name = "Y4NGZ Objective Outline Composite";
            _customPass.enabled = true;
            _customPass.targetColorBuffer = CustomPass.TargetBuffer.Camera;
            _customPass.targetDepthBuffer = CustomPass.TargetBuffer.Camera;
            _customPass.clearFlags = ClearFlag.None;
            _customPass.Owner = this;
            _customPassVolume.enabled = isActiveAndEnabled;
            ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Hints] Objective outline custom pass registered for '{camera.name}'.");
        }

        internal void UpdateMaterialsPublicProperties()
        {
            if (!CreateMaterialsIfNeeded())
                return;

            float scalingFactor = 1f;
            if (scaleWithScreenSize)
                scalingFactor = Screen.height / 360f;

            float thicknessScale = scaleWithScreenSize && scalingFactor >= 1f ? scalingFactor : 1f;
            Material material = _overlayMaterial;
            material.SetFloat(LineThicknessXId, thicknessScale * (lineThickness / Mathf.Max(1f, Screen.width)));
            material.SetFloat(LineThicknessYId, thicknessScale * (lineThickness / Mathf.Max(1f, Screen.height)));
            material.SetFloat(LineIntensityId, lineIntensity);
            material.SetColor(LineColor1Id, lineColor0);
            material.SetColor(LineColor2Id, lineColor1);
            material.SetColor(LineColor3Id, lineColor2);
            material.SetInt(CornerOutlinesId, cornerOutlines ? 1 : 0);
            Shader.SetGlobalFloat(OutlineAlphaCutoffId, alphaCutoff);
        }

        private bool RenderOutlineBuffer(CommandBuffer commandBuffer)
        {
            if (commandBuffer == null || ResolveSourceCamera() == null)
                return false;

            if (!CreateMaterialsIfNeeded())
                return false;

            RecreateRenderTextureIfNeeded();
            UpdateMaterialsPublicProperties();
            if (_renderTexture == null)
                return false;

            commandBuffer.SetRenderTarget(_renderTexture);
            commandBuffer.SetViewport(new Rect(0f, 0f, _renderTexture.width, _renderTexture.height));
            commandBuffer.ClearRenderTarget(true, true, Color.clear);
            commandBuffer.SetViewProjectionMatrices(sourceCamera.worldToCameraMatrix, sourceCamera.projectionMatrix);

            for (int i = 0; i < Outlines.Count; i++)
                DrawOutline(commandBuffer, Outlines[i]);

            _outlineTextureValid = true;
            return true;
        }

        private Camera ResolveSourceCamera()
        {
            if (sourceCamera == null)
                sourceCamera = GetComponent<Camera>();

            return sourceCamera;
        }

        private void DrawOutline(CommandBuffer commandBuffer, ObjectiveOutline outline)
        {
            if (outline == null || outline.Renderer == null || !outline.enabled)
                return;

            Material material = outline.eraseRenderer ? _outlineEraseMaterial : GetMaterialFromId(outline.color);
            if (material == null)
                return;

            if (material.HasProperty(CullingId))
                material.SetInt(CullingId, backfaceCulling ? (int)CullMode.Back : (int)CullMode.Off);
            if (material.HasProperty(CullId))
                material.SetInt(CullId, backfaceCulling ? (int)CullMode.Back : (int)CullMode.Off);

            if (_maskPassIndex < 0)
                _maskPassIndex = ResolveMaskMaterialPass(material);
            int passIndex = _maskPassIndex;
            Material[] sharedMaterials = outline.SharedMaterials;
            MeshFilter meshFilter = outline.MeshFilter;
            SkinnedMeshRenderer skinned = outline.SkinnedMeshRenderer;
            SpriteRenderer sprite = outline.SpriteRenderer;
            for (int i = 0; i < sharedMaterials.Length; i++)
            {
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    if (i < meshFilter.sharedMesh.subMeshCount)
                        commandBuffer.DrawRenderer(outline.Renderer, material, i, passIndex);
                }
                else if (skinned != null && skinned.sharedMesh != null)
                {
                    if (i < skinned.sharedMesh.subMeshCount)
                        commandBuffer.DrawRenderer(outline.Renderer, material, i, passIndex);
                }
                else if (sprite != null)
                {
                    commandBuffer.DrawRenderer(outline.Renderer, material, i, passIndex);
                }
            }
        }

        private Material GetMaterialFromId(int id)
        {
            switch (id)
            {
                case 1: return _outline2Material;
                case 2: return _outline3Material;
                default: return _outline1Material;
            }
        }

        private void RecreateRenderTextureIfNeeded()
        {
            if (sourceCamera == null)
                return;

            int width = Mathf.Max(1, sourceCamera.pixelWidth);
            int height = Mathf.Max(1, sourceCamera.pixelHeight);
            if (_renderTexture != null && _renderTexture.width == width && _renderTexture.height == height)
                return;

            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Destroy(_renderTexture);
            }

            _outlineTextureValid = false;
            _renderTexture = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        private bool CreateMaterialsIfNeeded()
        {
            if (_bufferShader == null)
                _bufferShader = ObjectiveOutlineAssets.LoadShader(ObjectiveOutlineAssets.BufferShaderName);
            if (_overlayShader == null)
                _overlayShader = ObjectiveOutlineAssets.LoadShader(ObjectiveOutlineAssets.OverlayShaderName);
            if (_bufferShader == null || _overlayShader == null)
                return false;

            if (_overlayMaterial == null)
                _overlayMaterial = new Material(_overlayShader) { hideFlags = HideFlags.HideAndDontSave };
            if (_outlineEraseMaterial == null)
                _outlineEraseMaterial = CreateMaskMaterial(new Color(0f, 0f, 0f, 0f));
            if (_outline1Material == null)
                _outline1Material = CreateMaskMaterial(new Color(1f, 0f, 0f, 1f));
            if (_outline2Material == null)
                _outline2Material = CreateMaskMaterial(new Color(0f, 1f, 0f, 1f));
            if (_outline3Material == null)
                _outline3Material = CreateMaskMaterial(new Color(0f, 0f, 1f, 1f));

            return _overlayMaterial != null
                && _outlineEraseMaterial != null
                && _outline1Material != null
                && _outline2Material != null
                && _outline3Material != null;
        }

        /// <summary>The shipped emissive RGB mask, as LethalCCTV uses in both pipelines; the channel
        /// colour tells the overlay which line colour to draw.</summary>
        private Material CreateMaskMaterial(Color emissionColor)
        {
            if (_bufferShader == null)
                return null;

            Material material = new Material(_bufferShader)
            {
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = 2500
            };
            ConfigureMaskMaterial(material, emissionColor);
            return material;
        }

        private static void ConfigureMaskMaterial(Material material, Color color)
        {
            SetColorIfPresent(material, "_Color", color);
            SetColorIfPresent(material, "_BaseColor", color);
            SetColorIfPresent(material, "_UnlitColor", color);
            SetColorIfPresent(material, "_EmissiveColor", color);
            SetFloatIfPresent(material, "_SurfaceType", 0f);
            SetFloatIfPresent(material, "_AlphaCutoffEnable", 0f);
            SetFloatIfPresent(material, "_ZWrite", 0f);
            SetFloatIfPresent(material, "_ZTestDepthEqualForOpaque", (float)CompareFunction.Always);
            SetIntIfPresent(material, "_SrcBlend", (int)BlendMode.One);
            SetIntIfPresent(material, "_DstBlend", (int)BlendMode.Zero);
            SetIntIfPresent(material, "_Cull", (int)CullMode.Off);
            SetIntIfPresent(material, "_Culling", (int)CullMode.Off);
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        }

        private static int ResolveMaskMaterialPass(Material material)
        {
            if (material == null)
                return 0;

            for (int i = 0; i < MaskPassNames.Length; i++)
            {
                int pass = material.FindPass(MaskPassNames[i]);
                if (pass >= 0)
                    return pass;
            }

            return 0;
        }

        private static void SetColorIfPresent(Material material, string name, Color value)
        {
            if (material.HasProperty(name))
                material.SetColor(name, value);
        }

        private static void SetFloatIfPresent(Material material, string name, float value)
        {
            if (material.HasProperty(name))
                material.SetFloat(name, value);
        }

        private static void SetIntIfPresent(Material material, string name, int value)
        {
            if (material.HasProperty(name))
                material.SetInt(name, value);
        }

        private void DestroyMaterials()
        {
            if (_overlayMaterial != null) Destroy(_overlayMaterial);
            if (_outlineEraseMaterial != null) Destroy(_outlineEraseMaterial);
            if (_outline1Material != null) Destroy(_outline1Material);
            if (_outline2Material != null) Destroy(_outline2Material);
            if (_outline3Material != null) Destroy(_outline3Material);
            _overlayMaterial = null;
            _outlineEraseMaterial = null;
            _outline1Material = null;
            _outline2Material = null;
            _outline3Material = null;
        }

        public sealed class Y4NGZHDRPObjectiveOutlinePass : CustomPass
        {
            internal ObjectiveOutlineEffect Owner;

#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            public override void Execute(CustomPassContext ctx)
#else
            protected override void Execute(CustomPassContext ctx)
#endif
            {
                ObjectiveOutlineEffect owner = Owner;
                if (owner == null || !owner.Lit || ctx.cmd == null || ctx.hdCamera == null)
                    return;

                // The local gameplay camera only: the volume targets it, and the check stays in case
                // the source camera is swapped under a live volume.
                Camera camera = ctx.hdCamera.camera;
                if (camera == null || camera != owner.ResolveSourceCamera())
                    return;

                if (!owner.HasActiveOutlines())
                    return;

                owner.sourceCamera = camera;
                if (!owner.RenderOutlineBuffer(ctx.cmd))
                    return;
                if (owner._overlayMaterial == null || owner._renderTexture == null || !owner._outlineTextureValid)
                    return;

                owner._overlayMaterial.SetTexture(OutlineSourceId, owner._renderTexture);
                HDUtils.DrawFullScreen(
                    ctx.cmd,
                    owner._overlayMaterial,
                    ctx.cameraColorBuffer,
                    ctx.cameraDepthBuffer,
                    null,
                    0);
            }
        }
    }
}
