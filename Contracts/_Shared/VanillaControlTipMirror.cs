using VanillaHudCanvasHost = Y4NGZ.Compatibility.VanillaHudCanvasHost;
using BepInEx.Bootstrap;
using TMPro;
using UnityEngine;
using Y4NGZCore.Modules.Hud;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Experience.UITheme;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #671. Flat, top-right vanilla control tips for a Contracted install without Y4NGZUI.
    ///
    /// The vanilla control-tip lines live on the game's camera-space HUD canvas with the
    /// authored tilt, and both they and the contract card claim the top-right corner. Y4NGZUI's
    /// prompt overlay solves this by disabling the vanilla lines and drawing its own flat stack
    /// below the card's published reservation. Without Y4NGZUI nothing consumed that
    /// reservation, and the first fix (a measured nudge of the vanilla rects) never landed
    /// because the two canvases do not share a coordinate space.
    ///
    /// This mirrors the vanilla lines instead: while it owns them, the vanilla
    /// <c>HUDManager.controlTipLines</c> are disabled and their text is copied every frame
    /// onto flat labels, anchored top-right beneath
    /// <see cref="ContractHudLayoutBridge.GetStackedPromptTop"/>. The vanilla element's fade
    /// (the Tooltips canvas group) and the gameplay-UI visibility signal are honoured, so the
    /// copies come and go exactly when the originals would.
    ///
    /// The labels live INSIDE the vanilla HUD canvas, beneath the pause menu (#822). They used
    /// to sit on a ScreenSpaceOverlay canvas of their own at sortingOrder 99, which draws over
    /// the vanilla camera-space canvas - and over the quick menu inside it - no matter what, so
    /// with the game paused the tips stayed on top of the menu on every Contracted-only profile.
    /// See <see cref="VanillaHudCanvasHost"/>; the authored 1080-unit layout sits on a scaled root.
    ///
    /// Gates, in order:
    /// - <c>Contracts &gt; Flatten Vanilla Control Tips</c> false: the vanilla lines are left
    ///   exactly as authored (rotation, position, style, enabled state).
    /// - Y4NGZUI present: never engages. Its overlay owns the lines, and its own settings decide
    ///   what happens when that overlay is turned off.
    /// </summary>
    internal static class VanillaControlTipMirror
    {
        private const string UiPluginGuid = "com.y4ngz.ui";

        // Same column and rhythm as the Y4NGZUI prompt stack, so the two installs agree on
        // where prompts live (UI_PRESENTATION_GUIDELINES: 24pt, 460-unit column, 224 resting top).
        private const float RightInset = 24f;
        private const float RestingTop = 224f;
        private const float LineWidth = 460f;
        private const float LineHeight = 26f;
        private const float LineSpacing = 27f;
        private const float FontSize = 24f;
        private const int SortingOrder = 99;

        private static bool? _uiPresent;
        private static HUDManager _hud;
        private static TextMeshProUGUI[] _source;
        private static TextMeshProUGUI[] _mirror;
        private static string[] _lastText;
        private static GameObject _canvasGo;
        private static RectTransform _root;
        private static Canvas _hostCanvas;
        private static Transform _pauseMenuAnchor;
        private static float _hostScaleRatio = 1f;
        private static CanvasGroup _group;
        private static bool _owned;
        private static float _lastTop = float.NaN;
        private static float _lastAlpha = -1f;

        private static bool UiPresent
        {
            get
            {
                _uiPresent ??= Chainloader.PluginInfos != null && Chainloader.PluginInfos.ContainsKey(UiPluginGuid);
                return _uiPresent.Value;
            }
        }

        private static bool Enabled
        {
            get
            {
                var entry = ContractsBootstrap.FlattenVanillaControlTips;
                return entry != null && entry.Value && !UiPresent;
            }
        }

        /// <summary>Runs every frame from the contract HUD's Update. Cheap when idle: one
        /// config read and a null check.</summary>
        internal static void Tick()
        {
            if (!Enabled)
            {
                Release();
                return;
            }

            HUDManager hud = HUDManager.Instance;
            TextMeshProUGUI[] lines = hud != null ? hud.controlTipLines : null;
            if (lines == null || lines.Length == 0)
            {
                Release();
                return;
            }

            if (hud != _hud || !ReferenceEquals(lines, _source))
                Acquire(hud, lines);

            if (_canvasGo == null)
                BuildCanvas();
            if (_canvasGo == null)
                return; // no vanilla HUD canvas yet; the next tick retries

            VanillaHudCanvasHost.KeepBeneathPauseMenu(
                _canvasGo.transform as RectTransform, _hostCanvas, ref _pauseMenuAnchor);
            RefreshHostScaleIfNeeded();

            for (int i = 0; i < _source.Length; i++)
            {
                TextMeshProUGUI source = _source[i];
                TextMeshProUGUI mirror = _mirror[i];
                if (source == null || mirror == null)
                    continue;

                // The vanilla line stays off while the copy stands in for it. Vanilla only
                // ever writes text and fades the group, so this never fights the game.
                if (source.enabled)
                    source.enabled = false;

                string text = source.text ?? string.Empty;
                if (!string.Equals(text, _lastText[i], System.StringComparison.Ordinal))
                {
                    _lastText[i] = text;
                    mirror.text = text;
                    bool visible = !string.IsNullOrWhiteSpace(text);
                    if (mirror.enabled != visible)
                        mirror.enabled = visible;
                }
            }

            float alpha = 1f;
            CanvasGroup vanillaGroup = hud.Tooltips != null ? hud.Tooltips.canvasGroup : null;
            if (vanillaGroup != null)
                alpha = vanillaGroup.alpha;
            if (!GameplayUiVisibility.IsVisible)
                alpha = 0f;
            if (Mathf.Abs(alpha - _lastAlpha) > 0.002f)
            {
                _lastAlpha = alpha;
                _group.alpha = alpha;
            }

            float top = Mathf.Max(RestingTop, ContractHudLayoutBridge.GetStackedPromptTop());
            if (float.IsNaN(_lastTop) || Mathf.Abs(top - _lastTop) > 0.01f)
            {
                _lastTop = top;
                for (int i = 0; i < _mirror.Length; i++)
                {
                    if (_mirror[i] != null)
                        _mirror[i].rectTransform.anchoredPosition = new Vector2(-RightInset, -(top + i * LineSpacing));
                }
            }
        }

        /// <summary>Gives the vanilla lines back and hides the copies. Idempotent.</summary>
        internal static void Release()
        {
            if (!_owned)
                return;

            _owned = false;
            if (_source != null)
            {
                for (int i = 0; i < _source.Length; i++)
                {
                    if (_source[i] != null && !_source[i].enabled)
                        _source[i].enabled = true;
                }
            }

            if (_mirror != null)
            {
                for (int i = 0; i < _mirror.Length; i++)
                {
                    if (_mirror[i] != null)
                    {
                        _mirror[i].text = string.Empty;
                        _mirror[i].enabled = false;
                    }
                }
            }

            if (_lastText != null)
            {
                for (int i = 0; i < _lastText.Length; i++)
                    _lastText[i] = null;
            }

            _hud = null;
            _source = null;
        }

        /// <summary>Release plus canvas destruction, for the owning component's teardown.</summary>
        internal static void Teardown()
        {
            Release();
            if (_canvasGo != null)
                Object.Destroy(_canvasGo);
            _canvasGo = null;
            _root = null;
            _hostCanvas = null;
            _pauseMenuAnchor = null;
            _hostScaleRatio = 1f;
            _group = null;
            _mirror = null;
            _lastText = null;
            _lastTop = float.NaN;
            _lastAlpha = -1f;
        }

        private static void Acquire(HUDManager hud, TextMeshProUGUI[] lines)
        {
            Release();
            _hud = hud;
            _source = lines;
            _owned = true;
            _lastTop = float.NaN;
            _lastAlpha = -1f;

            if (_canvasGo == null)
                BuildCanvas();
            if (_canvasGo == null)
                return;
            EnsureMirrorCount(lines.Length);

            for (int i = 0; i < lines.Length; i++)
            {
                TextMeshProUGUI source = lines[i];
                TextMeshProUGUI mirror = _mirror[i];
                if (source == null || mirror == null)
                    continue;

                if (source.font != null && mirror.font != source.font)
                    mirror.font = source.font;
                // The copy keeps the vanilla colour so the tips still read as the game's own.
                mirror.color = source.color;
                _lastText[i] = null;
            }
        }

        private static void BuildCanvas()
        {
            Canvas hostCanvas = VanillaHudCanvasHost.ResolveCanvas();
            if (hostCanvas == null)
                return;

            _hostCanvas = hostCanvas;
            RectTransform host = VanillaHudCanvasHost.CreateHost(
                "Y4NGZContractedControlTips", hostCanvas, ref _pauseMenuAnchor);
            _canvasGo = host.gameObject;
            _group = _canvasGo.AddComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;
            _group.alpha = 0f;
            _lastAlpha = 0f;

            var rootGo = new GameObject("ControlTipRoot", typeof(RectTransform));
            rootGo.layer = _canvasGo.layer;
            _root = rootGo.GetComponent<RectTransform>();
            _root.SetParent(host, false);
            VanillaHudCanvasHost.Stretch(_root);
            // Scale authored text around its screen-edge anchor, not the screen centre.
            // A centred pivot pulls the top-right corner inward when the HUD ratio is < 1.
            _root.pivot = Vector2.one;
            _hostScaleRatio = -1f;
            RefreshHostScaleIfNeeded();
        }

        /// <summary>The 1080-referenced layout under a canvas with its own scale factor: the root
        /// carries the ratio (1 on a canvas that already scales by height to 1080).</summary>
        private static void RefreshHostScaleIfNeeded()
        {
            if (_root == null || _hostCanvas == null)
                return;

            float ratio = VanillaHudCanvasHost.ReferenceScaleRatio(_hostCanvas);
            if (Mathf.Abs(ratio - _hostScaleRatio) <= 0.0005f)
                return;

            _hostScaleRatio = ratio;
            _root.localScale = Vector3.one * ratio;
        }

        private static void EnsureMirrorCount(int count)
        {
            if (_mirror != null && _mirror.Length == count)
                return;

            if (_mirror != null)
            {
                for (int i = 0; i < _mirror.Length; i++)
                {
                    if (_mirror[i] != null)
                        Object.Destroy(_mirror[i].gameObject);
                }
            }

            _mirror = new TextMeshProUGUI[count];
            _lastText = new string[count];
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("ControlTip" + i);
                go.layer = _canvasGo.layer;
                go.transform.SetParent(_root != null ? _root : _canvasGo.transform, false);
                var tmp = go.AddComponent<TextMeshProUGUI>();
                tmp.fontSize = FontSize;
                tmp.fontStyle = FontStyles.Normal;
                tmp.alignment = TextAlignmentOptions.Right;
                tmp.enableWordWrapping = false;
                tmp.overflowMode = TextOverflowModes.Overflow;
                tmp.richText = true;
                tmp.raycastTarget = false;
                tmp.enabled = false;
                RectTransform rect = tmp.rectTransform;
                rect.anchorMin = Vector2.one;
                rect.anchorMax = Vector2.one;
                rect.pivot = Vector2.one;
                rect.sizeDelta = new Vector2(LineWidth, LineHeight);
                rect.localRotation = Quaternion.identity;
                rect.localScale = Vector3.one;
                rect.anchoredPosition = new Vector2(-RightInset, -(RestingTop + i * LineSpacing));
                _mirror[i] = tmp;
            }

            _lastTop = float.NaN;
        }
    }
}
