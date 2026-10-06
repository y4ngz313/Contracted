using System;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;

namespace Y4NGZCompany.Contracts.WasteDisposal
{
    /// <summary>
    /// Renders the incinerator's compact status UI into the authored screen material. The screen
    /// renderer is a semantic binding, so changing the FBX only requires rebinding that role.
    /// </summary>
    internal sealed class WasteDisposalIncineratorScreenDisplay
    {
        private const int RenderWidth = 512;
        private const int RenderHeight = 347;
        private const float EmissiveIntensity = 2.0f;

        /// <summary>
        /// #716 A4: the render texture, its HDRP camera and the whole canvas rig used to be
        /// built on the first frame the incinerator ticked, which is the spawn frame on every
        /// peer. Construction now waits for the screen to be on camera, or for this grace
        /// period if it never reports visible (the renderer can be culled on a peer who never
        /// walks into the room).
        /// </summary>
        private const float DeferredBuildSeconds = 2f;

        /// <summary>Force a pending redraw through after this long even while off camera.</summary>
        private const float StaleRenderSeconds = 2f;

        private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int EmissiveColorLdrId = Shader.PropertyToID("_EmissiveColorLDR");
        private static readonly int EmissiveIntensityId = Shader.PropertyToID("_EmissiveIntensity");
        private static readonly Vector3 RigOrigin = new Vector3(7777f, -13500f, 7777f);
        private static readonly Color ScreenTint = new Color(0.45f, 1f, 0.58f, 1f);
        private static int _rigCounter;

        private readonly Renderer _renderer;
        private readonly int _materialIndex;
        private Material _material;
        private GameObject _root;
        private RenderTexture _texture;
        private Camera _camera;
        private TextMeshProUGUI _status;
        private TextMeshProUGUI _value;
        private TextMeshProUGUI _detail;
        private RectTransform _barFill;
        private string _lastStatus;
        private string _lastValue;
        private string _lastDetail;
        private float _lastProgress = -1f;
        private float _flickerUntil;
        private float _lastIntensity = -1f;
        private bool _built;
        private bool _hasEmissiveColor;
        private bool _hasEmissionColor;
        private bool _hasEmissiveColorLdr;
        private bool _hasEmissiveIntensity;
        private float _firstTickTime = -1f;
        private bool _pendingRender;
        private float _pendingRenderSince;

        internal WasteDisposalIncineratorScreenDisplay(Renderer renderer)
        {
            _renderer = renderer;
            _materialIndex = ResolveScreenMaterialIndex(renderer);
        }

        internal bool IsValid => _renderer != null && _materialIndex >= 0;

        internal void Tick(string status, string value, string detail, float progress01)
        {
            if (!IsValid)
                return;

            if (!_built && !TryBuildWhenReady())
                return;

            bool dirty = !string.Equals(_lastStatus, status, StringComparison.Ordinal)
                         || !string.Equals(_lastValue, value, StringComparison.Ordinal)
                         || !string.Equals(_lastDetail, detail, StringComparison.Ordinal)
                         || Mathf.Abs(_lastProgress - progress01) > 0.003f;
            if (dirty)
            {
                _lastStatus = status ?? string.Empty;
                _lastValue = value ?? string.Empty;
                _lastDetail = detail ?? string.Empty;
                _lastProgress = Mathf.Clamp01(progress01);
                if (_status != null) _status.text = _lastStatus;
                if (_value != null) _value.text = _lastValue;
                if (_detail != null) _detail.text = _lastDetail;
                if (_barFill != null) _barFill.anchorMax = new Vector2(_lastProgress, 1f);
                RequestRender();
            }

            FlushPendingRender();

            float intensity = Time.time < _flickerUntil ? UnityEngine.Random.Range(0.25f, 1.2f) : 1f;
            ApplyIntensity(intensity);
        }

        /// <summary>
        /// Defers the render-target build off the spawn frame. Returns true once the rig exists.
        /// </summary>
        private bool TryBuildWhenReady()
        {
            if (_firstTickTime < 0f)
            {
                _firstTickTime = Time.unscaledTime;
                return false;
            }

            bool visible;
            try { visible = _renderer.isVisible; }
            catch { visible = true; }
            if (!visible && Time.unscaledTime - _firstTickTime < DeferredBuildSeconds)
                return false;

            EnsureBuilt();
            return _built;
        }

        private void RequestRender()
        {
            if (_pendingRender)
                return;
            _pendingRender = true;
            _pendingRenderSince = Time.unscaledTime;
        }

        /// <summary>
        /// #716 B9: a redraw only has to reach the render texture before somebody looks at the
        /// screen. Off-camera peers coalesce their pending redraws instead of paying a manual
        /// Camera.Render for each one; the stale timeout keeps a screen that never reports
        /// visible from freezing on old text.
        /// </summary>
        private void FlushPendingRender()
        {
            if (!_pendingRender)
                return;
            bool visible;
            try { visible = _renderer.isVisible; }
            catch { visible = true; }
            if (!visible && Time.unscaledTime - _pendingRenderSince < StaleRenderSeconds)
                return;
            _pendingRender = false;
            RenderNow();
        }

        internal void Dispose()
        {
            if (_material != null) UnityEngine.Object.Destroy(_material);
            if (_root != null) UnityEngine.Object.Destroy(_root);
            if (_texture != null)
            {
                if (_texture.IsCreated()) _texture.Release();
                UnityEngine.Object.Destroy(_texture);
            }
            _material = null;
            _root = null;
            _texture = null;
            _camera = null;
            _built = false;
        }

        private void EnsureBuilt()
        {
            if (_built)
                return;
            _built = true;
            _flickerUntil = Time.time + 0.45f;

            _root = new GameObject("LGU_WasteDisposalScreenRig");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.transform.position = RigOrigin + Vector3.right * (_rigCounter++ * 5000f);

            _texture = new RenderTexture(RenderWidth, RenderHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "LGU_WasteDisposalScreenRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                antiAliasing = 1,
            };
            _texture.Create();

            GameObject cameraObject = new GameObject("ScreenCamera");
            cameraObject.transform.SetParent(_root.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 0f, -10f);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.008f, 0.028f, 0.018f, 1f);
            _camera.orthographic = true;
            _camera.orthographicSize = RenderHeight * 0.5f;
            _camera.aspect = RenderWidth / (float)RenderHeight;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 30f;
            _camera.cullingMask = 1 << 5;
            _camera.targetTexture = _texture;
            _camera.enabled = false;

            // Manually driven (enabled = false, rendered via explicit Camera.Render()
            // calls). Under HDRP a camera without HDAdditionalCameraData cannot resolve
            // into its assigned RenderTexture and instead stomps the backbuffer with its
            // clear color. Mirrors ShipTurretController.Core.cs's manual-camera pattern.
            HDAdditionalCameraData hdrp = cameraObject.AddComponent<HDAdditionalCameraData>();
            hdrp.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hdrp.backgroundColorHDR = _camera.backgroundColor;

            GameObject canvasObject = new GameObject("ScreenCanvas", typeof(RectTransform));
            canvasObject.transform.SetParent(_root.transform, false);
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = _camera;
            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(RenderWidth, RenderHeight);
            Image backplate = canvasObject.AddComponent<Image>();
            backplate.color = new Color(0.012f, 0.055f, 0.033f, 1f);

            TextMeshProUGUI header = CreateText(canvasRect, "Header", 26f, TextAlignmentOptions.Center, ScreenTint);
            header.fontStyle = FontStyles.Bold;
            SetRect(header.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(16f, -51f), new Vector2(-16f, -8f));
            header.text = "WASTE DISPOSAL";

            _status = CreateText(canvasRect, "Status", 63f, TextAlignmentOptions.Center, new Color(0.82f, 1f, 0.87f, 1f));
            _status.fontStyle = FontStyles.Bold;
            _status.enableAutoSizing = true;
            _status.fontSizeMin = 34f;
            _status.fontSizeMax = 63f;
            SetRect(_status.rectTransform, new Vector2(0f, 0.56f), new Vector2(1f, 0.86f), new Vector2(16f, 0f), new Vector2(-16f, 0f));

            _value = CreateText(canvasRect, "Value", 48f, TextAlignmentOptions.Center, ScreenTint);
            _value.fontStyle = FontStyles.Bold;
            _value.enableAutoSizing = true;
            _value.fontSizeMin = 30f;
            _value.fontSizeMax = 48f;
            SetRect(_value.rectTransform, new Vector2(0f, 0.32f), new Vector2(1f, 0.58f), new Vector2(14f, 0f), new Vector2(-14f, 0f));

            _detail = CreateText(canvasRect, "Detail", 22f, TextAlignmentOptions.Center, new Color(0.62f, 0.9f, 0.7f, 1f));
            _detail.enableAutoSizing = true;
            _detail.fontSizeMin = 16f;
            _detail.fontSizeMax = 22f;
            _detail.enableWordWrapping = true;
            SetRect(_detail.rectTransform, new Vector2(0f, 0.09f), new Vector2(1f, 0.34f), new Vector2(14f, 0f), new Vector2(-14f, 0f));

            GameObject barBackObject = new GameObject("BarBack", typeof(RectTransform));
            barBackObject.transform.SetParent(canvasRect, false);
            RectTransform barBackRect = barBackObject.GetComponent<RectTransform>();
            SetRect(barBackRect, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(18f, 17f), new Vector2(-18f, 35f));
            barBackObject.AddComponent<Image>().color = new Color(0.035f, 0.13f, 0.075f, 1f);

            GameObject fillObject = new GameObject("BarFill", typeof(RectTransform));
            fillObject.transform.SetParent(barBackRect, false);
            _barFill = fillObject.GetComponent<RectTransform>();
            _barFill.anchorMin = Vector2.zero;
            _barFill.anchorMax = new Vector2(0f, 1f);
            _barFill.pivot = new Vector2(0f, 0.5f);
            _barFill.offsetMin = Vector2.zero;
            _barFill.offsetMax = Vector2.zero;
            fillObject.AddComponent<Image>().color = new Color(0.36f, 1f, 0.5f, 1f);

            SetLayerRecursive(_root, 5);
            BindMaterial();
            RenderNow();
        }

        private void BindMaterial()
        {
            // #716 A4: read the shared array so only the assignment below instantiates a copy.
            Material[] materials;
            try
            {
                Material[] shared = _renderer.sharedMaterials;
                materials = new Material[shared.Length];
                Array.Copy(shared, materials, shared.Length);
            }
            catch { return; }
            if (_materialIndex < 0 || _materialIndex >= materials.Length || materials[_materialIndex] == null)
                return;

            Material source = materials[_materialIndex];
            _material = new Material(source) { name = source.name + "_LGUWasteDisposal" };
            materials[_materialIndex] = _material;
            try { _renderer.materials = materials; }
            catch { }

            SetTexture("_MainTex", _texture);
            SetTexture("_BaseColorMap", _texture);
            SetTexture("_EmissiveColorMap", _texture);
            SetTexture("_EmissionMap", _texture);
            SetTexture("_UnlitColorMap", _texture);
            try { _material.mainTexture = _texture; } catch { }
            if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", Color.white);
            if (_material.HasProperty("_Color")) _material.SetColor("_Color", Color.white);
            _material.EnableKeyword("_EMISSION");
            _material.EnableKeyword("_EMISSIVE_COLOR_MAP");
            _material.EnableKeyword("_MATERIAL_FEATURE_EMISSION");
            _hasEmissiveColor = _material.HasProperty(EmissiveColorId);
            _hasEmissionColor = _material.HasProperty(EmissionColorId);
            _hasEmissiveColorLdr = _material.HasProperty(EmissiveColorLdrId);
            _hasEmissiveIntensity = _material.HasProperty(EmissiveIntensityId);
        }

        private void ApplyIntensity(float value)
        {
            if (_material == null || Mathf.Abs(_lastIntensity - value) < 0.01f)
                return;
            _lastIntensity = value;
            Color bright = ScreenTint * (EmissiveIntensity * value);
            if (_hasEmissiveColor) _material.SetColor(EmissiveColorId, bright);
            if (_hasEmissionColor) _material.SetColor(EmissionColorId, bright);
            if (_hasEmissiveColorLdr) _material.SetColor(EmissiveColorLdrId, ScreenTint);
            if (_hasEmissiveIntensity) _material.SetFloat(EmissiveIntensityId, EmissiveIntensity * value);
        }

        private void SetTexture(string property, Texture texture)
        {
            if (_material != null && _material.HasProperty(property))
                _material.SetTexture(property, texture);
        }

        private void RenderNow()
        {
            if (_camera == null || _texture == null)
                return;
            try { _camera.Render(); } catch { }
        }

        private static TextMeshProUGUI CreateText(Transform parent, string name, float size, TextAlignmentOptions alignment, Color color)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            TextMeshProUGUI text = obj.AddComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.alignment = alignment;
            text.enableWordWrapping = false;
            text.richText = true;
            text.color = color;
            AssignVanillaFont(text);
            return text;
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static void AssignVanillaFont(TextMeshProUGUI text)
        {
            try
            {
                if (HUDManager.Instance?.clockNumber?.font != null)
                    text.font = HUDManager.Instance.clockNumber.font;
                else if (TMP_Settings.defaultFontAsset != null)
                    text.font = TMP_Settings.defaultFontAsset;
            }
            catch { }
        }

        private static void SetLayerRecursive(GameObject obj, int layer)
        {
            if (obj == null) return;
            obj.layer = layer;
            for (int i = 0; i < obj.transform.childCount; i++)
                SetLayerRecursive(obj.transform.GetChild(i).gameObject, layer);
        }

        internal static int ResolveScreenMaterialIndex(Renderer renderer)
        {
            if (renderer == null) return -1;
            Material[] materials;
            try { materials = renderer.sharedMaterials; }
            catch { return -1; }
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null && materials[i].name.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            return materials.Length > 0 ? 0 : -1;
        }
    }
}
