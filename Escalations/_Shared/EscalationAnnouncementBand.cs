using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Experience.UITheme;

namespace Y4NGZCompany.Escalations._Shared
{
    /// <summary>
    /// #1343 S2. Every escalation announcement goes through here instead of vanilla's tip popup:
    /// the host calls it at the event, each client on the replicated state's rising edge
    /// (<see cref="EscalationAnnouncementEdges"/>). Like the contract HUD the band is exempt from
    /// Enable Global UI Overhaul, and there is no fallback to the vanilla popup.
    /// </summary>
    internal static class EscalationAnnouncements
    {
        /// <summary>Plays the band for <paramref name="kind"/>. A band already playing restarts at
        /// t = 0 with the new copy (latest wins; the strip already carries the current state), so
        /// there is no queue. Null-safe and never throws into gameplay.</summary>
        internal static void Show(EscalationAnnouncementKind kind)
        {
            if (kind == EscalationAnnouncementKind.None)
                return;

            try
            {
                EscalationAnnouncementBand band = EscalationAnnouncementBand.Ensure();
                if (band == null)
                    return;

                EscalationAnnouncementCopy copy = EscalationAnnouncementCopyTable.For(kind);
                band.Play(copy);
                OnAnnouncementShown(copy.Tone);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Escalation] Escalation band {kind} skipped: {e.Message}");
            }
        }

        /// <summary>Hides the band now. Safe when it was never built.</summary>
        internal static void Clear()
        {
            try
            {
                EscalationAnnouncementBand.HideCurrent();
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Escalation] Escalation band clear skipped: {e.Message}");
            }
        }

        private static void OnAnnouncementShown(EscalationAnnouncementTone tone)
        {
            ContractHudAudio.Play(ContractHudAudioPolicy.ForEscalation(tone));
        }
    }

    /// <summary>
    /// #1343 S2/S7. The escalation toast: a compact two-line box directly below the contract card,
    /// as wide as the card (<see cref="EscalationToastLayout"/>). It slides in from the right,
    /// decodes its header and body, pulses its rules twice, holds, then collapses into the card's
    /// alert strip (or a strip-sized corner rect when the strip is not on screen) and goes idle.
    /// Without a settled card it takes the strip's fallback corner; it is never large and never
    /// centred. It hangs off the vanilla HUD container, the same parent the contract card uses,
    /// inside its own nested canvas so its motion never re-batches the vanilla HUD, and its root is
    /// inactive while idle, so an idle toast costs nothing per frame. The timeline itself is
    /// <see cref="EscalationBandTimeline"/>.
    /// </summary>
    internal sealed class EscalationAnnouncementBand : MonoBehaviour
    {
        // The backing is the toast's colour at ~27% value and 70% alpha (mock #3a0008 @ 70%).
        private const float BackingValueScale = 0.27f;
        private const float BackingAlpha = 0.7f;
        private const float FlashWhiteBlend = 0.85f;
        private const float SweepWidthOfTarget = 0.16f;
        private const float SweepWhiteBlend = 0.5f;
        // Collapse target when the strip is not on screen: 250x20 at 1280x720, top right inside
        // the safe area (the toast's own fallback width and inset).
        private const float FallbackTargetHeightOfHeight = 0.028f;
        // The body shrinks to fit its two lines, never below this (card units), in this many steps.
        private const float BodyMinFontUnits = 12f;
        private const int BodyFitIterations = 10;
        // TMP measures a line from the font's face metrics, so any glyph gives the line height.
        private const string OneLineReference = "A";
        private const string TwoLineReference = "A\nA";
        // A toast narrower than this (root units per card unit) is degenerate; keep the last scale.
        private const float MinCardUnit = 0.0001f;
        // ContractHUDManager's fallback canvas, one order above it.
        private const float FallbackCanvasReferenceWidth = 1920f;
        private const float FallbackCanvasReferenceHeight = 1080f;
        private const int FallbackCanvasSortingOrder = 101;

        private static EscalationAnnouncementBand _current;
        private static Canvas _fallbackCanvas;
        private static TMP_FontAsset _fallbackFont;
        private static bool _fallbackFontSearched;

        private RectTransform _root;
        private CanvasGroup _rootGroup;
        private EscalationBandVignette _vignette;
        private RectTransform _band;
        private CanvasGroup _bandGroup;
        private Image _backing;
        private Image _ruleTop;
        private Image _ruleBottom;
        // The text block is laid out in card units and scaled by cardUnit as a whole.
        private RectTransform _text;
        private CanvasGroup _textGroup;
        private ContractHudLineArt _icon;
        private TextMeshProUGUI _header;
        private TextMeshProUGUI _body;
        private Image _sweep;

        // One buffer for both accessors; each call's corners are mapped before the next.
        private readonly Vector3[] _corners = new Vector3[4];
        private char[] _headerBuffer = Array.Empty<char>();
        private char[] _bodyBuffer = Array.Empty<char>();

        private EscalationAnnouncementCopy _copy;
        private float _t = EscalationBandTimeline.TotalSeconds;
        private float _naturalHeightUnits;
        private float _lastCardUnit;
        private bool _decodeDone;
        private int _lastDecodeFrame;
        private int _lastHeaderResolved;
        private int _lastBodyResolved;

        // Read once per Show from ContractUiTheme.
        private Color _primary;
        private Color _bodyColor;
        private Color _backingColor;
        private Color _accent;
        private Color _accentBacking;
        private Color _danger;
        private Color _white;

        private Vector2 _lastBandPosition;
        private Vector2 _lastBandSize;
        private Color _lastRuleColor;
        private Color _lastBackingColor;
        private float _lastBandAlpha;
        private float _lastTextAlpha;
        private float _lastVignetteAlpha;
        // The root group's alpha is the gameplay UI gate alone (1 or 0); it starts at 1.
        private float _lastRootAlpha = 1f;
        private bool _sweepVisible;

        /// <summary>The live band, built or rebuilt under the current parent: the vanilla HUD
        /// container, else a fallback overlay canvas that goes as soon as the container exists.</summary>
        internal static EscalationAnnouncementBand Ensure()
        {
            Transform hudContainer = ResolveVanillaHudContainer();
            Transform parent = hudContainer != null ? hudContainer : EnsureFallbackCanvas().transform;

            // Unity's overridden equality: a band that went away with its scene compares null.
            if (_current != null && _current.transform.parent == parent)
                return _current;

            if (_current != null)
                Destroy(_current.gameObject);
            _current = null;

            if (hudContainer != null && _fallbackCanvas != null)
            {
                Destroy(_fallbackCanvas.gameObject);
                _fallbackCanvas = null;
            }

            _current = Build(parent);
            return _current;
        }

        internal static void HideCurrent()
        {
            if (_current != null)
                _current.GoIdle();
        }

        private static Transform ResolveVanillaHudContainer()
        {
            HUDManager hud = HUDManager.Instance;
            GameObject container = hud != null ? hud.HUDContainer : null;
            return container != null ? container.transform : null;
        }

        private static Canvas EnsureFallbackCanvas()
        {
            if (_fallbackCanvas != null)
                return _fallbackCanvas;

            var canvasGo = new GameObject("Y4NGZ_EscalationBandCanvas");
            DontDestroyOnLoad(canvasGo);
            _fallbackCanvas = canvasGo.AddComponent<Canvas>();
            _fallbackCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _fallbackCanvas.sortingOrder = FallbackCanvasSortingOrder;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(FallbackCanvasReferenceWidth, FallbackCanvasReferenceHeight);
            scaler.matchWidthOrHeight = 1f;
            return _fallbackCanvas;
        }

        private static EscalationAnnouncementBand Build(Transform parent)
        {
            int layer = parent.gameObject.layer;
            var rootGo = new GameObject("Y4NGZ_EscalationBand", typeof(RectTransform));
            rootGo.layer = layer;
            rootGo.transform.SetParent(parent, false);
            var root = (RectTransform)rootGo.transform;
            Stretch(root);
            // Re-batch isolation like the alert belt. No overrideSorting and no GraphicRaycaster:
            // the band is presentation only and never takes part in a pointer query.
            rootGo.AddComponent<Canvas>();
            var group = rootGo.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            var band = rootGo.AddComponent<EscalationAnnouncementBand>();
            try
            {
                band.BuildChildren(root, layer);
            }
            catch
            {
                // A half-built band would tick against missing children every frame.
                Destroy(rootGo);
                throw;
            }
            rootGo.SetActive(false);
            return band;
        }

        private void BuildChildren(RectTransform root, int layer)
        {
            _root = root;
            _rootGroup = root.GetComponent<CanvasGroup>();

            _vignette = CreateGraphic<EscalationBandVignette>(root, "Vignette", layer);
            Stretch(_vignette.rectTransform);
            _vignette.enabled = false;

            _band = CreateRect(root, "Band", layer);
            _band.anchorMin = _band.anchorMax = _band.pivot = new Vector2(.5f, .5f);
            _bandGroup = _band.gameObject.AddComponent<CanvasGroup>();
            _bandGroup.interactable = false;
            _bandGroup.blocksRaycasts = false;
            // Clips the text to the shrinking box during the collapse. The backing and both rules
            // already sit inside the box rect; the vignette and sweep are root children, unclipped.
            _band.gameObject.AddComponent<RectMask2D>();

            _backing = CreateGraphic<Image>(_band, "Backing", layer);
            Stretch(_backing.rectTransform);

            _ruleTop = CreateGraphic<Image>(_band, "RuleTop", layer);
            RectTransform ruleTop = _ruleTop.rectTransform;
            ruleTop.anchorMin = new Vector2(0f, 1f);
            ruleTop.anchorMax = Vector2.one;
            ruleTop.pivot = new Vector2(.5f, 1f);
            ruleTop.anchoredPosition = Vector2.zero;

            _ruleBottom = CreateGraphic<Image>(_band, "RuleBottom", layer);
            RectTransform ruleBottom = _ruleBottom.rectTransform;
            ruleBottom.anchorMin = Vector2.zero;
            ruleBottom.anchorMax = new Vector2(1f, 0f);
            ruleBottom.pivot = new Vector2(.5f, 0f);
            ruleBottom.anchoredPosition = Vector2.zero;

            // The text block hangs from the box's top-left corner in card units and is scaled by
            // cardUnit as a whole, so the card's height tween, scale and motion and the collapse
            // move it without a TMP relayout every frame.
            _text = CreateRect(_band, "Text", layer);
            _text.anchorMin = _text.anchorMax = _text.pivot = new Vector2(0f, 1f);
            _text.anchoredPosition = Vector2.zero;
            _textGroup = _text.gameObject.AddComponent<CanvasGroup>();
            _textGroup.interactable = false;
            _textGroup.blocksRaycasts = false;

            _icon = CreateGraphic<ContractHudLineArt>(_text, "Icon", layer);
            _icon.Icon = ContractHudLineArt.Shape.Warning;
            RectTransform icon = _icon.rectTransform;
            icon.anchorMin = icon.anchorMax = new Vector2(0f, 1f);
            icon.pivot = new Vector2(0f, .5f);
            icon.sizeDelta = new Vector2(EscalationToastLayout.IconSizeUnits, EscalationToastLayout.IconSizeUnits);

            _header = CreateText(_text, "Header", layer, wrap: false);
            _body = CreateText(_text, "Body", layer, wrap: true);

            _sweep = CreateGraphic<Image>(root, "Sweep", layer);
            RectTransform sweep = _sweep.rectTransform;
            sweep.anchorMin = sweep.anchorMax = sweep.pivot = new Vector2(.5f, .5f);
            _sweep.enabled = false;
        }

        /// <summary>Starts the toast at t = 0 with <paramref name="copy"/>.</summary>
        internal void Play(EscalationAnnouncementCopy copy)
        {
            _copy = copy;
            ReadColours(copy.Tone);
            AssignFont(_header);
            AssignFont(_body);
            EnsureCapacity(ref _headerBuffer, copy.Header.Length);
            EnsureCapacity(ref _bodyBuffer, copy.Body.Length);
            _header.color = _primary;
            _body.color = _bodyColor;
            _icon.color = _primary;

            _t = 0f;
            _decodeDone = false;
            _lastDecodeFrame = int.MinValue;
            _lastHeaderResolved = -1;
            _lastBodyResolved = -1;
            _lastBandPosition = new Vector2(float.NaN, float.NaN);
            _lastBandSize = new Vector2(float.NaN, float.NaN);
            _lastRuleColor = _lastBackingColor = new Color(-1f, -1f, -1f, -1f);
            _lastBandAlpha = _lastTextAlpha = _lastVignetteAlpha = -1f;
            _lastCardUnit = -1f;
            _sweepVisible = false;
            _sweep.enabled = false;

            KeepOnTop();
            if (!_root.gameObject.activeSelf)
                _root.gameObject.SetActive(true);
            // Fitted once per Play, after activation, in card units (see FitText).
            _naturalHeightUnits = FitText();
            ApplyGameplayGate(GameplayUiVisibility.IsVisibleUnderPauseMenu);
            Apply(-1f, 0f);
        }

        private void Update()
        {
            float previous = _t;
            _t += Time.unscaledDeltaTime;
            try
            {
                // The band sits on the vanilla HUD canvas, so it follows the under-pause-menu gate
                // (docs/UI_PRESENTATION_GUIDELINES.md). Hidden, the timeline keeps running.
                ApplyGameplayGate(GameplayUiVisibility.IsVisibleUnderPauseMenu);
                Apply(previous, _t);
            }
            catch (Exception e)
            {
                // Presentation only: a failed frame ends this band rather than erroring every frame.
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Escalation] Escalation band stopped: {e.Message}");
                GoIdle();
            }
        }

        private void OnDestroy()
        {
            if (_current == this)
                _current = null;
        }

        private void GoIdle()
        {
            _t = EscalationBandTimeline.TotalSeconds;
            if (_vignette != null)
                _vignette.enabled = false;
            if (_sweep != null)
                _sweep.enabled = false;
            _sweepVisible = false;
            if (_root != null && _root.gameObject.activeSelf)
                _root.gameObject.SetActive(false);
        }

        private void ApplyGameplayGate(bool visible)
        {
            SetAlpha(_rootGroup, visible ? 1f : 0f, ref _lastRootAlpha);
        }

        private void Apply(float previousT, float t)
        {
            if (EscalationBandTimeline.IsIdle(t))
            {
                GoIdle();
                return;
            }

            KeepOnTop();
            Rect area = _root.rect;
            EscalationToastBox safe = SafeArea(area);

            ApplyVignette(EscalationBandTimeline.VignetteAlpha(t, _copy.Tone));
            ApplyDecode(t);

            // Re-read every frame: the toast follows the card's height tween, scale and HUD motion.
            bool hasCard = TryRootRect(ContractHUDManager.TryGetCardWorldCorners(_corners), out Rect card);
            EscalationToastBox toast = EscalationToastLayout.ToastRect(hasCard, Box(card), Box(area), safe, _naturalHeightUnits);
            ApplyCardUnit(EscalationToastLayout.CardUnit(toast));

            Rect home = new Rect(toast.X, toast.Y, toast.Width, toast.Height);
            Rect rect = home;
            Rect target = default;
            bool onStrip = false;
            if (t < EscalationBandTimeline.CollapseStart)
            {
                rect.x += EscalationBandTimeline.SlideOffset(t) * home.width;
            }
            else
            {
                // Re-read every frame: the card can move or resize while the toast collapses.
                onStrip = ResolveTarget(area, safe, out target);
                rect = LerpRect(home, target, EscalationBandTimeline.CollapseProgress(t));
            }
            ApplyBandRect(area, rect);

            float accent = EscalationBandTimeline.AccentBlend(t);
            Color rule = Brighten(Color.Lerp(_primary, _accent, accent), EscalationBandTimeline.RuleBrightness(t));
            if (EscalationBandTimeline.FlashFrame(previousT, t))
                rule = Color.Lerp(rule, _white, FlashWhiteBlend);
            if (rule != _lastRuleColor)
            {
                _ruleTop.color = rule;
                _ruleBottom.color = rule;
                _lastRuleColor = rule;
            }

            Color backing = Color.Lerp(_backingColor, _accentBacking, accent);
            if (backing != _lastBackingColor)
            {
                _backing.color = backing;
                _lastBackingColor = backing;
            }

            SetAlpha(_bandGroup, EscalationBandTimeline.BandAlpha(t), ref _lastBandAlpha);
            SetAlpha(_textGroup, EscalationBandTimeline.TextAlpha(t), ref _lastTextAlpha);
            ApplySweep(t, onStrip, target, area);
        }

        /// <summary>
        /// Fits the copy once per Play and returns the natural height, all in card units. The text
        /// block is scaled by cardUnit as a whole, so font and width scale together and the fit
        /// holds at every card size. The header is one line at the card title's resting size,
        /// shrunk to the header width; the body wraps at the objective text's size and shrinks only
        /// when it needs more than <see cref="EscalationToastLayout.BodyMaxLines"/> lines.
        /// </summary>
        private float FitText()
        {
            FitSingleLine(_header, _copy.Header, EscalationToastLayout.HeaderFontUnits, EscalationToastLayout.HeaderWidthUnits);
            float headerLine = string.IsNullOrEmpty(_copy.Header) ? 0f : _header.GetPreferredValues(OneLineReference).y;
            FitBody(_body, _copy.Body);
            float bodyHeight = string.IsNullOrEmpty(_copy.Body)
                ? 0f
                : _body.GetPreferredValues(_copy.Body, EscalationToastLayout.BodyWidthUnits, float.PositiveInfinity).y;
            float natural = EscalationToastLayout.NaturalHeightUnits(headerLine, bodyHeight);

            float top = EscalationToastLayout.TopPaddingUnits;
            _text.sizeDelta = new Vector2(EscalationToastLayout.CardBodyWidthUnits, natural);
            _icon.rectTransform.anchoredPosition = new Vector2(EscalationToastLayout.SidePaddingUnits, -(top + headerLine * .5f));
            RectTransform header = _header.rectTransform;
            header.anchoredPosition = new Vector2(EscalationToastLayout.HeaderLeftUnits, -top);
            header.sizeDelta = new Vector2(EscalationToastLayout.HeaderWidthUnits, headerLine);
            RectTransform body = _body.rectTransform;
            body.anchoredPosition = new Vector2(EscalationToastLayout.SidePaddingUnits,
                -(top + headerLine + EscalationToastLayout.HeaderBodyGapUnits));
            body.sizeDelta = new Vector2(EscalationToastLayout.BodyWidthUnits, bodyHeight);
            return natural;
        }

        /// <summary>Sets the nominal size, shrunk once if the copy would not fit on one line.</summary>
        private static void FitSingleLine(TextMeshProUGUI tmp, string text, float fontSize, float width)
        {
            tmp.fontSize = fontSize;
            if (string.IsNullOrEmpty(text))
                return;
            float preferred = tmp.GetPreferredValues(text).x;
            if (preferred > width && preferred > 0f)
                tmp.fontSize = Mathf.Max(1f, fontSize * width / preferred);
        }

        /// <summary>The nominal body size, or the largest size at which the copy wraps to at most
        /// <see cref="EscalationToastLayout.BodyMaxLines"/> lines, found by bisection. Wrapping is
        /// not linear in the size, so a single proportional shrink could still leave a third line.</summary>
        private static void FitBody(TextMeshProUGUI tmp, string text)
        {
            float nominal = EscalationToastLayout.BodyFontUnits;
            tmp.fontSize = nominal;
            if (string.IsNullOrEmpty(text) || FitsBodyLines(tmp, text))
                return;

            float fits = BodyMinFontUnits;
            float overflows = nominal;
            for (int i = 0; i < BodyFitIterations; i++)
            {
                float size = (fits + overflows) * .5f;
                tmp.fontSize = size;
                if (FitsBodyLines(tmp, text))
                    fits = size;
                else
                    overflows = size;
            }
            tmp.fontSize = fits;
        }

        private static bool FitsBodyLines(TextMeshProUGUI tmp, string text)
        {
            float height = tmp.GetPreferredValues(text, EscalationToastLayout.BodyWidthUnits, float.PositiveInfinity).y;
            // Two lines are one line plus one line advance; a third line adds a whole advance.
            float twoLines = tmp.GetPreferredValues(TwoLineReference).y;
            return height <= twoLines + .5f;
        }

        private void ApplyDecode(float t)
        {
            if (_decodeDone)
                return;

            if (EscalationBandTimeline.DecodeProgress(t) >= 1f)
            {
                _header.text = _copy.Header;
                _body.text = _copy.Body;
                _decodeDone = true;
                return;
            }

            int frame = EscalationBandDecode.ScrambleFrame(t);
            int headerResolved = EscalationBandDecode.ResolvedCount(_copy.Header.Length, t);
            int bodyResolved = EscalationBandDecode.ResolvedCount(_copy.Body.Length, t);
            if (frame == _lastDecodeFrame && headerResolved == _lastHeaderResolved && bodyResolved == _lastBodyResolved)
                return;

            _lastDecodeFrame = frame;
            _lastHeaderResolved = headerResolved;
            _lastBodyResolved = bodyResolved;
            WriteDecoded(_header, _copy.Header, t, frame, _headerBuffer);
            WriteDecoded(_body, _copy.Body, t, frame, _bodyBuffer);
        }

        private static void WriteDecoded(TextMeshProUGUI tmp, string target, float t, int frame, char[] buffer)
        {
            int length = EscalationBandDecode.Write(target, t, frame, buffer);
            // SetCharArray ignores an empty array, so an empty line is cleared through text.
            if (length == 0)
                tmp.text = string.Empty;
            else
                tmp.SetCharArray(buffer, 0, length);
        }

        private void ApplyVignette(float alpha)
        {
            if (Mathf.Abs(alpha - _lastVignetteAlpha) <= .0001f)
                return;

            _lastVignetteAlpha = alpha;
            bool visible = alpha > .0001f;
            if (visible)
                _vignette.color = new Color(_danger.r, _danger.g, _danger.b, alpha);
            if (_vignette.enabled != visible)
                _vignette.enabled = visible;
        }

        private void ApplyBandRect(Rect area, Rect rect)
        {
            Vector2 position = rect.center - area.center;
            Vector2 size = rect.size;
            if (position != _lastBandPosition)
            {
                _band.anchoredPosition = position;
                _lastBandPosition = position;
            }
            if (size != _lastBandSize)
            {
                _band.sizeDelta = size;
                _lastBandSize = size;
            }
        }

        /// <summary>A bright accent quad crossing the real strip once the toast has landed on it.
        /// Not drawn over the corner fallback, where there is no strip to confirm.</summary>
        private void ApplySweep(float t, bool onStrip, Rect target, Rect area)
        {
            bool visible = onStrip && EscalationBandTimeline.SweepActive(t);
            if (visible)
            {
                float progress = EscalationBandTimeline.SweepProgress(t);
                float width = target.width * SweepWidthOfTarget;
                float x = Mathf.Lerp(target.xMin + width * .5f, target.xMax - width * .5f, progress);
                RectTransform sweep = _sweep.rectTransform;
                sweep.anchoredPosition = new Vector2(x, target.center.y) - area.center;
                sweep.sizeDelta = new Vector2(width, target.height);
                Color tint = Color.Lerp(_accent, _white, SweepWhiteBlend);
                tint.a = Mathf.Sin(Mathf.PI * progress);
                _sweep.color = tint;
            }
            if (visible != _sweepVisible)
            {
                _sweep.enabled = visible;
                _sweepVisible = visible;
            }
        }

        /// <summary>Scales the card-unit text block and the rules by <paramref name="cardUnit"/>
        /// (root units per card unit) when it changes: during the card's arrival scale, on a
        /// resolution change and in the narrowed safe-area case, otherwise never.</summary>
        private void ApplyCardUnit(float cardUnit)
        {
            if (cardUnit <= MinCardUnit || Mathf.Abs(cardUnit - _lastCardUnit) <= .00001f)
                return;

            _lastCardUnit = cardUnit;
            _text.localScale = new Vector3(cardUnit, cardUnit, 1f);
            var rule = new Vector2(0f, EscalationToastLayout.RuleThicknessUnits * cardUnit);
            _ruleTop.rectTransform.sizeDelta = rule;
            _ruleBottom.rectTransform.sizeDelta = rule;
        }

        /// <summary>Keeps the toast the last sibling of its parent while it plays, so it draws above
        /// the card's adapter root (also made a last sibling when the card is built) while it
        /// collapses upward over the card into the strip.</summary>
        private void KeepOnTop()
        {
            Transform parent = _root.parent;
            if (parent != null && _root.GetSiblingIndex() != parent.childCount - 1)
                _root.SetAsLastSibling();
        }

        /// <summary>The HUD safe area in root space. The root stretches over the HUD container,
        /// which covers the screen, so the screen's safe-area fractions map onto it.</summary>
        private static EscalationToastBox SafeArea(Rect area)
        {
            Rect safe = Screen.safeArea;
            float screenWidth = Mathf.Max(1f, Screen.width);
            float screenHeight = Mathf.Max(1f, Screen.height);
            float left = area.xMin + area.width * Mathf.Clamp01(safe.xMin / screenWidth);
            float right = area.xMin + area.width * Mathf.Clamp01(safe.xMax / screenWidth);
            float bottom = area.yMin + area.height * Mathf.Clamp01(safe.yMin / screenHeight);
            float top = area.yMin + area.height * Mathf.Clamp01(safe.yMax / screenHeight);
            return new EscalationToastBox(left, bottom, right - left, top - bottom);
        }

        private static EscalationToastBox Box(Rect rect)
        {
            return new EscalationToastBox(rect.xMin, rect.yMin, rect.width, rect.height);
        }

        /// <summary>Maps <see cref="_corners"/>, just filled by a ContractHUDManager accessor that
        /// returned <paramref name="filled"/>, into root space. The card and its strip sit on the
        /// same root canvas as the toast, so the world corners map straight in.</summary>
        private bool TryRootRect(bool filled, out Rect rect)
        {
            rect = default;
            if (!filled)
                return false;

            Vector3 first = _root.InverseTransformPoint(_corners[0]);
            float minX = first.x, maxX = first.x, minY = first.y, maxY = first.y;
            for (int i = 1; i < _corners.Length; i++)
            {
                Vector3 point = _root.InverseTransformPoint(_corners[i]);
                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxY = Mathf.Max(maxY, point.y);
            }
            if (maxX - minX <= 1f || maxY - minY <= .5f)
                return false;

            rect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        /// <summary>The collapse target in root space: the alert strip when it is on screen
        /// (true), else a strip-sized rect in the safe area's top-right corner (false).</summary>
        private bool ResolveTarget(Rect area, EscalationToastBox safe, out Rect target)
        {
            if (TryRootRect(ContractHUDManager.TryGetAlertBeltWorldCorners(_corners), out target))
                return true;

            float margin = area.height * EscalationToastLayout.FallbackInsetOfRootHeight;
            float width = area.width * EscalationToastLayout.FallbackWidthOfRootWidth;
            float height = area.height * FallbackTargetHeightOfHeight;
            target = new Rect(safe.XMax - margin - width, safe.YMax - margin - height, width, height);
            return false;
        }

        private void ReadColours(EscalationAnnouncementTone tone)
        {
            _white = ContractUiTheme.White;
            _accent = ContractUiTheme.HudAccent;
            _danger = ContractUiTheme.Danger;
            _primary = tone == EscalationAnnouncementTone.Calm ? _accent : _danger;
            _bodyColor = tone == EscalationAnnouncementTone.Lethal ? _danger : _white;
            _backingColor = Backing(_primary);
            _accentBacking = Backing(_accent);
        }

        private static Color Backing(Color tint)
        {
            return new Color(tint.r * BackingValueScale, tint.g * BackingValueScale, tint.b * BackingValueScale, BackingAlpha);
        }

        private static Color Brighten(Color tint, float gain)
        {
            return new Color(Mathf.Min(1f, tint.r * gain), Mathf.Min(1f, tint.g * gain), Mathf.Min(1f, tint.b * gain), tint.a);
        }

        private static Rect LerpRect(Rect from, Rect to, float t)
        {
            return Rect.MinMaxRect(
                Mathf.LerpUnclamped(from.xMin, to.xMin, t),
                Mathf.LerpUnclamped(from.yMin, to.yMin, t),
                Mathf.LerpUnclamped(from.xMax, to.xMax, t),
                Mathf.LerpUnclamped(from.yMax, to.yMax, t));
        }

        private static void SetAlpha(CanvasGroup group, float alpha, ref float last)
        {
            if (Mathf.Abs(alpha - last) <= .0001f)
                return;
            group.alpha = alpha;
            last = alpha;
        }

        private static void EnsureCapacity(ref char[] buffer, int length)
        {
            if (buffer.Length < length)
                buffer = new char[length];
        }

        /// <summary>The vanilla HUD font, as the card uses it: the clock's font and its shared
        /// material, assigned and never mutated (the band only changes vertex colour and group
        /// alpha). Without a clock, the 3270 font asset, searched once.</summary>
        private static void AssignFont(TextMeshProUGUI tmp)
        {
            HUDManager hud = HUDManager.Instance;
            TextMeshProUGUI clock = hud != null ? hud.clockNumber : null;
            if (clock != null && clock.font != null)
            {
                if (tmp.font != clock.font)
                    tmp.font = clock.font;
                Material shared = clock.fontSharedMaterial;
                if (shared != null && tmp.fontSharedMaterial != shared)
                    tmp.fontSharedMaterial = shared;
                return;
            }

            TMP_FontAsset fallback = ResolveFallbackFont();
            if (fallback != null && tmp.font != fallback)
                tmp.font = fallback;
        }

        private static TMP_FontAsset ResolveFallbackFont()
        {
            if (_fallbackFontSearched)
                return _fallbackFont;

            _fallbackFontSearched = true;
            TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            for (int i = 0; i < fonts.Length; i++)
            {
                if (fonts[i] != null && fonts[i].name.Contains("3270"))
                {
                    _fallbackFont = fonts[i];
                    break;
                }
            }
            return _fallbackFont;
        }

        private static RectTransform CreateRect(Transform parent, string name, int layer)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = layer;
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static T CreateGraphic<T>(Transform parent, string name, int layer) where T : Graphic
        {
            RectTransform rect = CreateRect(parent, name, layer);
            T graphic = rect.gameObject.AddComponent<T>();
            graphic.raycastTarget = false;
            return graphic;
        }

        /// <summary>A text hanging from the text block's top-left corner: the header never wraps,
        /// the body word-wraps inside its fitted width.</summary>
        private static TextMeshProUGUI CreateText(Transform parent, string name, int layer, bool wrap)
        {
            TextMeshProUGUI tmp = CreateGraphic<TextMeshProUGUI>(parent, name, layer);
            tmp.richText = false;
            tmp.enableWordWrapping = wrap;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.fontStyle = FontStyles.Normal;
            tmp.characterSpacing = 0f;
            tmp.text = string.Empty;
            RectTransform rect = tmp.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            return tmp;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(.5f, .5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
