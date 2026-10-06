using System;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Contracts.ShadowRaid
{
    // Drives the drill's physical screen mesh by rendering a countdown UI to a RenderTexture and pushing
    // it into the screen material's emissive map (same technique as LGUShipSystems.ShipPowerDisplay).
    // The screen is powered off (dark) by default and only lights up while the drill is actively
    // breaching a container.
    internal sealed class BreachDrillScreenDisplay
    {
        private const int RenderWidth = 512;
        private const int RenderHeight = 256;
        private const float PowerOnFlickerDuration = 0.6f;
        private const float OnEmissiveIntensity = 2.1f;

        private static readonly Color ScreenTint = new Color(0.55f, 1f, 0.7f, 1f);
        // Far from ShipPowerDisplay's render rig (0,-10000,0); each drill rig is offset again so multiple
        // off-screen cameras never film one another's canvases.
        private static readonly Vector3 RigBaseOrigin = new Vector3(7777f, -12000f, 7777f);
        private static int _rigCounter;

        private readonly Renderer _screenRenderer;
        private readonly int _materialIndex;

        private Material _runtimeMaterial;
        private bool _materialBound;

        private GameObject _rigRoot;
        private RenderTexture _renderTexture;
        private Camera _camera;
        private TextMeshProUGUI _countdownText;
        private RectTransform _barFill;
        private bool _rigBuilt;

        private bool _powered;
        private float _flickerUntil;
        private float _lastIntensity = -1f;
        private string _lastText;
        private float _lastProgress = -1f;
        private bool _contentDirty;

        internal BreachDrillScreenDisplay(Renderer screenRenderer)
        {
            _screenRenderer = screenRenderer;
            _materialIndex = ResolveScreenMaterialIndex(screenRenderer);
        }

        internal bool IsValid => _screenRenderer != null && _materialIndex >= 0;

        internal void Tick(bool powered, string text, float progress01)
        {
            if (!IsValid)
                return;

            EnsureMaterialBound();

            if (powered != _powered)
            {
                _powered = powered;
                if (powered)
                {
                    BuildRig();
                    BindRenderTexture();
                    _flickerUntil = Time.time + PowerOnFlickerDuration;
                    _contentDirty = true;
                }
                else
                {
                    ApplyOff();
                }
            }

            if (!_powered)
                return;

            if (!string.Equals(_lastText, text, StringComparison.Ordinal)
                || Mathf.Abs(_lastProgress - progress01) > 0.004f)
            {
                _lastText = text;
                _lastProgress = progress01;
                _contentDirty = true;
            }

            if (_contentDirty)
            {
                if (_countdownText != null)
                    _countdownText.text = text ?? string.Empty;
                if (_barFill != null)
                    _barFill.anchorMax = new Vector2(Mathf.Clamp01(progress01), 1f);
                RenderNow();
                _contentDirty = false;
            }

            // Brief CRT-style power-on flicker, then steady.
            float intensity = Time.time < _flickerUntil ? UnityEngine.Random.Range(0.12f, 1.25f) : 1f;
            ApplyEmissiveIntensity(intensity);
        }

        internal void Dispose()
        {
            if (_runtimeMaterial != null)
            {
                UnityEngine.Object.Destroy(_runtimeMaterial);
                _runtimeMaterial = null;
            }

            if (_rigRoot != null)
            {
                UnityEngine.Object.Destroy(_rigRoot);
                _rigRoot = null;
            }

            _camera = null;
            _countdownText = null;
            _barFill = null;

            if (_renderTexture != null)
            {
                if (_renderTexture.IsCreated())
                    _renderTexture.Release();
                UnityEngine.Object.Destroy(_renderTexture);
                _renderTexture = null;
            }

            _materialBound = false;
            _rigBuilt = false;
        }

        private void EnsureMaterialBound()
        {
            if (_materialBound)
                return;

            BuildRig();

            if (_screenRenderer == null || _materialIndex < 0)
            {
                _materialBound = true;
                return;
            }

            Material[] materials;
            try { materials = _screenRenderer.materials; }
            catch { return; }

            if (_materialIndex < 0 || _materialIndex >= materials.Length || materials[_materialIndex] == null)
                return;

            Material source = materials[_materialIndex];
            _runtimeMaterial = new Material(source) { name = source.name + "_LGUBreachDrill" };
            materials[_materialIndex] = _runtimeMaterial;
            try { _screenRenderer.materials = materials; }
            catch { }

            _materialBound = true;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.ShadowRaid] Drill screen countdown material bound: renderer='{_screenRenderer.name}', " +
                $"slot={_materialIndex}, sourceMaterial='{source.name}', shader='{(source.shader != null ? source.shader.name : "null")}'.");

            if (_countdownText != null)
                _countdownText.text = "READY";
            if (_barFill != null)
                _barFill.anchorMax = new Vector2(0f, 1f);
            _lastText = "READY";
            _lastProgress = 0f;
            BindRenderTexture();

            // Default to powered-off/dim, but keep the runtime Blacksite UI texture bound so the authored
            // placeholder image is no longer the visible screen source.
            ApplyOff();
        }

        private void ApplyOff()
        {
            if (_runtimeMaterial == null)
                return;

            if (_runtimeMaterial.HasProperty("_EmissiveColor"))
                _runtimeMaterial.SetColor("_EmissiveColor", Color.black);
            if (_runtimeMaterial.HasProperty("_EmissionColor"))
                _runtimeMaterial.SetColor("_EmissionColor", Color.black);
            if (_runtimeMaterial.HasProperty("_EmissiveColorLDR"))
                _runtimeMaterial.SetColor("_EmissiveColorLDR", Color.black);
            _runtimeMaterial.DisableKeyword("_EMISSION");
            _lastIntensity = -1f;
        }

        private void BindRenderTexture()
        {
            if (_runtimeMaterial == null || _renderTexture == null)
                return;

            try { _runtimeMaterial.mainTexture = _renderTexture; }
            catch { }

            if (_runtimeMaterial.HasProperty("_MainTex"))
                _runtimeMaterial.SetTexture("_MainTex", _renderTexture);
            if (_runtimeMaterial.HasProperty("_EmissiveColorMap"))
                _runtimeMaterial.SetTexture("_EmissiveColorMap", _renderTexture);
            if (_runtimeMaterial.HasProperty("_EmissionMap"))
                _runtimeMaterial.SetTexture("_EmissionMap", _renderTexture);
            if (_runtimeMaterial.HasProperty("_UnlitColorMap"))
                _runtimeMaterial.SetTexture("_UnlitColorMap", _renderTexture);
            if (_runtimeMaterial.HasProperty("_BaseColorMap"))
                _runtimeMaterial.SetTexture("_BaseColorMap", _renderTexture);
            if (_runtimeMaterial.HasProperty("_BaseColor"))
                _runtimeMaterial.SetColor("_BaseColor", Color.white);
            if (_runtimeMaterial.HasProperty("_Color"))
                _runtimeMaterial.SetColor("_Color", Color.white);
            _runtimeMaterial.EnableKeyword("_EMISSION");
            _runtimeMaterial.EnableKeyword("_EMISSIVE_COLOR_MAP");
            _runtimeMaterial.EnableKeyword("_MATERIAL_FEATURE_EMISSION");
            RenderNow();
        }

        private void ApplyEmissiveIntensity(float k)
        {
            if (Mathf.Abs(_lastIntensity - k) < 0.01f)
                return;

            _lastIntensity = k;
            Color bright = ScreenTint * (OnEmissiveIntensity * k);
            if (_runtimeMaterial != null)
            {
                _runtimeMaterial.EnableKeyword("_EMISSION");
                _runtimeMaterial.EnableKeyword("_EMISSIVE_COLOR_MAP");
                _runtimeMaterial.EnableKeyword("_MATERIAL_FEATURE_EMISSION");
                if (_runtimeMaterial.HasProperty("_EmissiveColor"))
                    _runtimeMaterial.SetColor("_EmissiveColor", bright);
                if (_runtimeMaterial.HasProperty("_EmissionColor"))
                    _runtimeMaterial.SetColor("_EmissionColor", bright);
                if (_runtimeMaterial.HasProperty("_EmissiveColorLDR"))
                    _runtimeMaterial.SetColor("_EmissiveColorLDR", ScreenTint);
                if (_runtimeMaterial.HasProperty("_EmissiveIntensity"))
                    _runtimeMaterial.SetFloat("_EmissiveIntensity", OnEmissiveIntensity * k);
                if (_runtimeMaterial.HasProperty("_BaseColor"))
                    _runtimeMaterial.SetColor("_BaseColor", Color.white);
                if (_runtimeMaterial.HasProperty("_Color"))
                    _runtimeMaterial.SetColor("_Color", Color.white);
            }

        }

        private static void SetMaterialColorIfPresent(Material material, string property, Color value)
        {
            if (material != null && material.HasProperty(property))
                material.SetColor(property, value);
        }

        private static void SetMaterialFloatIfPresent(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private void RenderNow()
        {
            if (_camera == null || _renderTexture == null)
                return;
            try { _camera.Render(); }
            catch { }
        }

        private void BuildRig()
        {
            if (_rigBuilt)
                return;
            _rigBuilt = true;

            _rigRoot = new GameObject("LGU_BreachDrillScreenRig");
            UnityEngine.Object.DontDestroyOnLoad(_rigRoot);
            _rigRoot.transform.position = RigBaseOrigin + new Vector3(_rigCounter++ * 5000f, 0f, 0f);

            _renderTexture = new RenderTexture(RenderWidth, RenderHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "LGU_BreachDrillScreenRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                antiAliasing = 1
            };
            _renderTexture.Create();

            GameObject cameraGo = new GameObject("ScreenCamera");
            cameraGo.transform.SetParent(_rigRoot.transform, false);
            cameraGo.transform.localPosition = new Vector3(0f, 0f, -10f);
            _camera = cameraGo.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.01f, 0.04f, 0.03f, 1f);
            _camera.orthographic = true;
            _camera.orthographicSize = RenderHeight * 0.5f;
            _camera.aspect = RenderWidth / (float)RenderHeight;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 30f;
            _camera.cullingMask = 1 << 5;
            _camera.targetTexture = _renderTexture;
            _camera.enabled = false;

            // Manually driven (enabled = false, rendered via explicit Camera.Render()
            // calls). Under HDRP a camera without HDAdditionalCameraData cannot resolve
            // into its assigned RenderTexture and instead stomps the backbuffer with its
            // clear color. Mirrors ShipTurretController.Core.cs's manual-camera pattern.
            HDAdditionalCameraData hdrp = cameraGo.AddComponent<HDAdditionalCameraData>();
            hdrp.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hdrp.backgroundColorHDR = _camera.backgroundColor;

            GameObject canvasGo = new GameObject("ScreenCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(_rigRoot.transform, false);
            canvasGo.transform.localPosition = Vector3.zero;
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = _camera;
            RectTransform canvasRt = canvasGo.GetComponent<RectTransform>();
            canvasRt.sizeDelta = new Vector2(RenderWidth, RenderHeight);

            Image backplate = canvasGo.AddComponent<Image>();
            backplate.color = new Color(0.02f, 0.07f, 0.05f, 1f);

            TextMeshProUGUI header = CreateText(canvasRt, "Header", 30f, TextAlignmentOptions.Center, new Color(0.45f, 0.95f, 0.6f, 1f));
            header.fontStyle = FontStyles.Bold;
            header.rectTransform.anchorMin = new Vector2(0f, 1f);
            header.rectTransform.anchorMax = new Vector2(1f, 1f);
            header.rectTransform.pivot = new Vector2(0.5f, 1f);
            header.rectTransform.offsetMin = new Vector2(18f, -54f);
            header.rectTransform.offsetMax = new Vector2(-18f, -8f);
            header.text = "BLACKSITE";

            _countdownText = CreateText(canvasRt, "Countdown", 112f, TextAlignmentOptions.Center, new Color(0.85f, 1f, 0.9f, 1f));
            _countdownText.fontStyle = FontStyles.Bold;
            _countdownText.enableAutoSizing = true;
            _countdownText.fontSizeMin = 52f;
            _countdownText.fontSizeMax = 112f;
            _countdownText.rectTransform.anchorMin = new Vector2(0f, 0f);
            _countdownText.rectTransform.anchorMax = new Vector2(1f, 1f);
            _countdownText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _countdownText.rectTransform.offsetMin = new Vector2(18f, 45f);
            _countdownText.rectTransform.offsetMax = new Vector2(-18f, -58f);
            _countdownText.text = string.Empty;

            GameObject barBackGo = new GameObject("BarBack", typeof(RectTransform));
            barBackGo.transform.SetParent(canvasRt, false);
            RectTransform barBackRt = barBackGo.GetComponent<RectTransform>();
            barBackRt.anchorMin = new Vector2(0f, 0f);
            barBackRt.anchorMax = new Vector2(1f, 0f);
            barBackRt.pivot = new Vector2(0.5f, 0f);
            barBackRt.offsetMin = new Vector2(18f, 18f);
            barBackRt.offsetMax = new Vector2(-18f, 36f);
            Image barBack = barBackGo.AddComponent<Image>();
            barBack.color = new Color(0.05f, 0.14f, 0.1f, 1f);

            GameObject fillGo = new GameObject("BarFill", typeof(RectTransform));
            fillGo.transform.SetParent(barBackRt, false);
            _barFill = fillGo.GetComponent<RectTransform>();
            _barFill.anchorMin = new Vector2(0f, 0f);
            _barFill.anchorMax = new Vector2(0f, 1f);
            _barFill.pivot = new Vector2(0f, 0.5f);
            _barFill.offsetMin = Vector2.zero;
            _barFill.offsetMax = Vector2.zero;
            Image fill = fillGo.AddComponent<Image>();
            fill.color = new Color(0.45f, 1f, 0.55f, 1f);

            SetLayerRecursive(_rigRoot, 5);
        }

        private static TextMeshProUGUI CreateText(Transform parent, string name, float size, TextAlignmentOptions alignment, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.alignment = alignment;
            text.enableWordWrapping = false;
            text.richText = true;
            text.color = color;
            AssignVanillaFont(text);
            return text;
        }

        private static void AssignVanillaFont(TextMeshProUGUI tmp)
        {
            try
            {
                if (HUDManager.Instance != null && HUDManager.Instance.clockNumber != null && HUDManager.Instance.clockNumber.font != null)
                {
                    tmp.font = HUDManager.Instance.clockNumber.font;
                    return;
                }

                if (TMP_Settings.defaultFontAsset != null)
                {
                    tmp.font = TMP_Settings.defaultFontAsset;
                    return;
                }

                TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                for (int i = 0; i < fonts.Length; i++)
                {
                    if (fonts[i] != null)
                    {
                        tmp.font = fonts[i];
                        return;
                    }
                }
            }
            catch
            {
                // Leave the default font if lookup fails.
            }
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            if (go == null)
                return;
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
                SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
        }

        private static int ResolveScreenMaterialIndex(Renderer renderer)
        {
            if (renderer == null)
                return -1;

            Material[] materials;
            try { materials = renderer.sharedMaterials; }
            catch { return -1; }

            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material != null && material.name.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }

            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null && materials[i].HasProperty("_EmissiveColorMap"))
                    return i;
            }

            return materials.Length > 0 ? 0 : -1;
        }
    }
}
