using System.Collections;
using System.Collections.Generic;
using System;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Networking;
using UnityEngine.UI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations._Shared;
using Y4NGZCompany.Experience.UITheme;
namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Manages the contract HUD element lifecycle and animations.
    ///
    /// <para>#823. The component lives on a persistent, invisible GameObject. The card itself is
    /// built lazily as a child of the game's own HUD container
    /// (<c>HUDManager.HUDContainer</c>, the <c>IngamePlayerHUD</c> object on the camera-space
    /// <c>Systems/UI/Canvas</c>), so it is rendered by the UI camera with the same
    /// post-processing, group alpha, HUD animator and HideHUD behaviour as every vanilla HUD
    /// element, and it sits beneath the pause menu (a later sibling of the container) exactly
    /// like the clock and quota boxes. The old ScreenSpaceOverlay canvas is now only a fallback
    /// for the moment no HUD container exists. All layout math stays in the card's own
    /// 1920x1080-referenced units through a scale adapter root, so every authored size, the
    /// shared motion offset and the prompt-stack reservation keep their meaning.</para>
    /// </summary>
    internal partial class ContractHUDManager : MonoBehaviour
    {
        internal static ContractHUDManager Instance { get; private set; }

        // #823. The card hangs off the vanilla HUD container through a scale adapter root that
        // maps the card's 1080-tall reference units onto the container's rect. The fallback
        // canvas is created only when no HUD container exists and is destroyed as soon as one
        // does. `_attachedParent` is the transform the current card was built under; a
        // destroyed or replaced parent is detected through Unity's overridden null semantics.
        private RectTransform _adapterRoot;
        private Transform _attachedParent;
        private Canvas _fallbackCanvas;
        private Canvas _hostCanvas;
        private Vector2 _adapterParentSize;
        private CanvasGroup _canvasGroup;
        private RectTransform _cardRect;
        private RectMask2D _arrivalMask;
        private float _arrivalDockedHeight;
        private float _arrivalBaseHeight;
        private bool _arrivalBeltReserved;
        private bool _arrivalFlashing;
        private RectTransform _titleRect;
        private RectTransform _objectiveRect;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _objectiveText;
        // #724: the alert belt replaces both the eyebrow line and the in-body hazard sections.
        private readonly ContractHudAlertBelt _alertBelt = new ContractHudAlertBelt();
        private static readonly List<ContractHudAlertSegment> _beltSegments = new List<ContractHudAlertSegment>(6);
        private static readonly StringBuilder _beltSegmentBuilder = new StringBuilder(96);
        private AudioSource _typewriterAudioSource;
        private AudioClip _typewriterClip;
        private bool _typewriterAudioLoadFailed;
        private static bool _typewriterFallbackWarningLogged;
        private readonly List<Image> _borderImages = new List<Image>();

        private Coroutine _animCoroutine;
        private Coroutine _liveCoroutine;
        private Coroutine _loadTypewriterAudioCoroutine;
        private Coroutine _objectiveTypeCoroutine;
        private Coroutine _layoutTweenCoroutine;
        private Coroutine _resultCoroutine;
        private Coroutine _fadeCoroutine;
        private Coroutine _textSwapFadeCoroutine;
        private bool _vanillaFontVerified;
        private readonly Dictionary<TextMeshProUGUI, Material> _hudTextMaterials = new Dictionary<TextMeshProUGUI, Material>();

        // A1 landing-spike instrumentation. The reveal frame was the one step of the
        // landing hitch cluster with a verified mechanism and no runtime number: the
        // reveal runs inside a coroutine, so the hitch profiler files it as
        // "unexplained", and the RoundManager slow-pass line is rate limited across it.
        // The head of AnimateSequence runs synchronously inside StartCoroutine, so it is
        // contained in the ShowContract total and is reported as its own figure. Cost is
        // a handful of Stopwatch reads on the single frame the card appears, once per
        // round, so there is no steady-state overhead to gate.
        private static double _revealHeadTotalMs;
        private static double _revealHeadTitleMs;
        private static double _revealHeadObjectiveMs;
        private static double _revealHeadSignatureMs;
        private static double _revealHeadResizeMs;
        private static double _revealHeadGatherMs;
        private static bool _revealGlyphProbeBeforeLogged;
        private static bool _revealGlyphProbeAfterLogged;

        // #880: boolean action steps seen complete this round stay complete on the card even if the
        // provider reports them incomplete again; each suppressed regression is logged once per key.
        private readonly HashSet<string> _pinnedCompleteKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _regressionLoggedKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> _regressedKeyScratch = new List<string>();

        // B1: reused across the 0.25s poll so the signature costs no StringBuilder or string
        // allocation while nothing has changed.
        private static readonly StringBuilder _signatureBuilder = new StringBuilder(160);

        // B1: the border/corner children were looked up by name off the card transform on every
        // poll. They are procedurally created once, so the reference list is stable for the
        // lifetime of the card.
        private readonly List<GameObject> _borderObjects = new List<GameObject>();
        private bool _borderObjectsCached;
        private bool _borderVisible;
        private string _cachedTitle;
        private MoonContractType _cachedTitleContract = (MoonContractType)(-1);
        private bool _cachedTitleStruck;

        private static AccessTools.FieldRef<HUDManager, bool> _hudHiddenRef;
        private static bool _hudHiddenRefResolved;

        private bool _isShowing;
        private ContractHudArrivalState _arrivalState;
        private bool _isSettled => _arrivalState.IsSettled;
        private bool _isCompact;
        private bool _showingResult;
        private bool _resultIsWarning;
        private MoonContractType _displayedContract = MoonContractType.None;
        private string _lastLiveText = string.Empty;
        private string _lastLivePlainText = string.Empty;
        private string _lastSemanticSignature = string.Empty;

        private const float SettledScale = 0.75f;
        private const float LiveCardDefaultWidth = 500f;
        private const float LiveCardMinWidth = 430f;
        private const float LiveCardMaxWidth = 580f;
        private const float LiveCardHorizontalPadding = 58f;
        // #724: the eyebrow line is gone, so the minimum heights drop by the 20 units it
        // and its gap used to occupy.
        private const float LiveCardMinHeight = 176f;
        private const float LiveCardCompactMinHeight = 112f;
        private const float LiveCardMaxHeight = 460f;
        private const float LiveCardTopPadding = 16f;
        private const float LiveCardBottomPadding = 14f;
        private const float LiveCardTitleHeight = 40f;
        private const float LiveCardTitleGap = 12f;
        private const float LiveCardBeltGap = 6f;
        private const float LiveCardInnerSidePadding = 20f;
        private const float LiveCardPromptClearance = 18f;
        private const float LayoutTweenDuration = 0.24f;
        private const float TypewriterCharDelay = 0.045f;
        private const float TypewriterPitch = 0.70f;
        private const float TypewriterVolume = 0.55f;
        private const float TextSwapFadeDuration = 0.15f;
        private const float ResultDisplayDuration = 3f;
        private const float CornerMargin = 24f;
        private const float ContractMotionScale = 0.62f;
        private const string TypewriterClipFileName = "digital_text_sequence_terminal_01.wav";

        // #823. The card's own unit space: 1 unit = 1 pixel on a 1080-tall screen, which is what
        // every constant above was authored in and what the prompt overlay's reservation reads.
        private const float ReferenceHeight = 1080f;

        // Fine procedural frame, tinted through the existing palette and vanilla HUD alpha.
        private static Color FrameColor => ContractUiTheme.HudFrame;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            // #823: the card is built on demand under the vanilla HUD container (EnsureCardBuilt);
            // only the typewriter source, which belongs to this persistent object, is created here.
            _typewriterAudioSource = gameObject.AddComponent<AudioSource>();
            _typewriterAudioSource.playOnAwake = false;
            _typewriterAudioSource.loop = true;
            _typewriterAudioSource.spatialBlend = 0f;
            _typewriterAudioSource.volume = TypewriterVolume;
            _typewriterAudioSource.pitch = TypewriterPitch;
            _loadTypewriterAudioCoroutine = StartCoroutine(LoadTypewriterAudio());
            Hide(immediate: true);
            SceneManager.sceneLoaded += OnSceneLoaded;
            UiTheme.ThemeChanged += OnUiThemeChanged;
            // #823: the under-pause-menu channel. The card is beneath the menu by construction, so
            // it keeps drawing while the menu is open and dims with the rest of the vanilla HUD.
            GameplayUiVisibility.UnderPauseMenuVisibilityChanged += OnGameplayUiVisibilityChanged;
            GameplayHudMotion.MotionSampled += OnHudMotionSampled;
            // C4: the card needs both channels. MotionSampled follows the shared offset, which now
            // publishes only when it actually moved; LayoutChanged is the explicit "re-derive your
            // anchors" ping for a resolution or safe-area change, which used to be smuggled through
            // a forced zero-offset motion publish.
            GameplayHudMotion.LayoutChanged += OnHudLayoutChanged;
            ApplyPresentationVisibility(ShouldPresent());
        }

        private void OnDestroy()
        {
            ReleaseHudTextMaterials();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            UiTheme.ThemeChanged -= OnUiThemeChanged;
            GameplayUiVisibility.UnderPauseMenuVisibilityChanged -= OnGameplayUiVisibilityChanged;
            GameplayHudMotion.MotionSampled -= OnHudMotionSampled;
            GameplayHudMotion.LayoutChanged -= OnHudLayoutChanged;
            StopTypewriterSound();
            if (_typewriterClip != null)
            {
                Destroy(_typewriterClip);
                _typewriterClip = null;
            }
            ContractHudLayoutBridge.SetReservedPromptTop(0f);
            VanillaControlTipMirror.Teardown();
            if (Instance == this)
                Instance = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "SampleSceneRelay")
                Hide(immediate: true);
        }

        internal void ShowContract()
        {
            long showStart = System.Diagnostics.Stopwatch.GetTimestamp();
            MoonContractState.RefreshAssignments("hud reveal");
            double refreshMs = RevealMillisecondsSince(showStart);
            var contractType = MoonContractState.GetCurrentContract();
            if (contractType == MoonContractType.None && !MoonContractState.CurrentLevelSupportsSalvageContract())
            {
                ContractsBootstrap.Log.LogDebug("No active moon contract - skipping HUD.");
                return;
            }

            var definition = MoonContractCatalog.Get(contractType);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] HUD reveal contract: {MoonContractState.GetCurrentContractDebugContext()}, title='{definition.Title}'.");
            // A reused result/sections-only card may still be visible here. Close its body
            // before font/theme/layout work or an independent poll can touch the old pixels.
            _arrivalState = _arrivalState.Begin();
            _showingResult = false;
            _resultIsWarning = false;
            _sectionsOnly = false;
            _arrivalBaseHeight = 0f;
            _arrivalDockedHeight = 0f;
            _arrivalBeltReserved = false;
            _sideAppliedHeight = -1f;
            if (_canvasGroup != null) _canvasGroup.alpha = 0f;
            StopSideReveal(keepPending: true);
            StopLayoutTween();
            _alertBelt.Hide();
            _displayedContract = contractType;
            // #880: the completion pins belong to one contract for one round.
            _pinnedCompleteKeys.Clear();
            _regressionLoggedKeys.Clear();
            // #823: the card is (re)built under whatever HUD container exists right now.
            if (!EnsureCardBuilt())
                return;
            long fontStart = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureVanillaFont();
            LogFontAtlasStateOnce(before: true);
            double fontMs = RevealMillisecondsSince(fontStart);
            long themeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            ApplyTheme();
            ApplyPresentationVisibility(ShouldPresent());
            double themeMs = RevealMillisecondsSince(themeStart);

            if (_animCoroutine != null)
            {
                StopCoroutine(_animCoroutine);
                _animCoroutine = null;
            }
            ClearArrivalReveal();
            if (_liveCoroutine != null)
            {
                StopCoroutine(_liveCoroutine);
                _liveCoroutine = null;
            }
            if (_resultCoroutine != null)
            {
                StopCoroutine(_resultCoroutine);
                _resultCoroutine = null;
            }
            if (_fadeCoroutine != null)
            {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }
            StopObjectiveTypewriter();
            if (_textSwapFadeCoroutine != null)
            {
                StopCoroutine(_textSwapFadeCoroutine);
                _textSwapFadeCoroutine = null;
            }
            LayoutPowerFaultRow();
            LayoutSideSection();
            StopTypewriterSound();

            // A1: tells the RoundManager.Update timing pass that this is the reveal frame, so
            // its report escapes the rate limit that used to swallow it. It is armed here, past
            // every bail-out, so a moon with no contract never marks a reveal frame that does
            // not happen.
            MoonContractRuntimePatch.MarkRevealFrame();

            // A1: the head figures below are static scratch written by the synchronous head of
            // AnimateSequence. If StartCoroutine never runs that body — a disabled component or
            // an inactive GameObject — the fields would still hold the previous reveal's
            // numbers, so they are invalidated first and reported as "n/a" when unwritten.
            InvalidateRevealHeadTimings();
            _animCoroutine = StartCoroutine(AnimateSequence(definition.Title, definition.Briefing));

            // StartCoroutine runs the coroutine body up to its first yield synchronously,
            // so by this point the AnimateSequence head has already executed inside the
            // ShowContract total measured here.
            double showMs = RevealMillisecondsSince(showStart);
            LogFontAtlasStateOnce(before: false);
            ContractsBootstrap.Log?.LogInfo(
                "[MoonContracts.Timing] HUD reveal frame: ShowContract="
                + showMs.ToString("0.00") + "ms AnimateSequenceHead="
                + FormatRevealStep(_revealHeadTotalMs) + " [ RefreshAssignments="
                + refreshMs.ToString("0.00") + "ms EnsureVanillaFont="
                + fontMs.ToString("0.00") + "ms ApplyTheme="
                + themeMs.ToString("0.00") + "ms GatherLiveValues="
                + FormatRevealStep(_revealHeadGatherMs) + " BuildLiveTitle="
                + FormatRevealStep(_revealHeadTitleMs) + " BuildLiveObjectiveText="
                + FormatRevealStep(_revealHeadObjectiveMs) + " BuildLiveSemanticSignature="
                + FormatRevealStep(_revealHeadSignatureMs) + " ResizeLiveCard="
                + FormatRevealStep(_revealHeadResizeMs) + " ]");
        }

        /// <summary>A1. Marks every AnimateSequence head figure as unwritten for this reveal.</summary>
        private static void InvalidateRevealHeadTimings()
        {
            _revealHeadTotalMs = -1.0;
            _revealHeadGatherMs = -1.0;
            _revealHeadTitleMs = -1.0;
            _revealHeadObjectiveMs = -1.0;
            _revealHeadSignatureMs = -1.0;
            _revealHeadResizeMs = -1.0;
        }

        /// <summary>A1. Renders one head figure, or "n/a" when the head never ran.</summary>
        private static string FormatRevealStep(double milliseconds)
        {
            return milliseconds < 0.0 ? "n/a" : milliseconds.ToString("0.00") + "ms";
        }

        private static double RevealMillisecondsSince(long startTimestamp)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>
        /// A1 glyph hypothesis probe. The reveal is the first time several glyphs
        /// (notably the separator '·') are ever rasterized, which on a dynamic atlas
        /// forces an atlas render and upload on that frame. Two Debug lines per session
        /// — one before the first reveal, one after its synchronous head — say whether
        /// the atlas actually grew, which is what confirms or rules the theory out.
        /// </summary>
        private void LogFontAtlasStateOnce(bool before)
        {
            if (before ? _revealGlyphProbeBeforeLogged : _revealGlyphProbeAfterLogged)
                return;

            var log = ContractsBootstrap.Log;
            if (log == null)
                return;

            try
            {
                var font = _titleText != null ? _titleText.font : null;
                if (font == null)
                    return;

                if (before)
                    _revealGlyphProbeBeforeLogged = true;
                else
                    _revealGlyphProbeAfterLogged = true;

                var atlas = font.atlasTexture;
                log.LogDebug(
                    "[MoonContracts.Timing] Reveal font atlas "
                    + (before ? "before" : "after") + ": font='" + font.name
                    + "' population=" + font.atlasPopulationMode
                    + " atlas=" + (atlas != null ? atlas.width + "x" + atlas.height : "none")
                    + " characters=" + (font.characterTable != null ? font.characterTable.Count : -1));
            }
            catch (Exception e)
            {
                log.LogDebug($"[MoonContracts.Timing] Reveal font atlas probe failed: {e.Message}");
            }
        }

        internal void Hide(bool immediate = false)
        {
            if (_animCoroutine != null)
            {
                StopCoroutine(_animCoroutine);
                _animCoroutine = null;
            }

            if (_liveCoroutine != null)
            {
                StopCoroutine(_liveCoroutine);
                _liveCoroutine = null;
            }

            if (_resultCoroutine != null)
            {
                StopCoroutine(_resultCoroutine);
                _resultCoroutine = null;
            }

            if (_fadeCoroutine != null)
            {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }

            if (_textSwapFadeCoroutine != null)
            {
                StopCoroutine(_textSwapFadeCoroutine);
                _textSwapFadeCoroutine = null;
            }

            StopObjectiveTypewriter();
            StopTypewriterSound();
            StopLayoutTween();
            ClearArrivalReveal();
            _arrivalDockedHeight = 0f;
            _arrivalBaseHeight = 0f;
            _arrivalBeltReserved = false;
            ResetPresentation();
            // #1195: the SIDE section re-reads Contract S next frame as a first sighting.
            ResetSideSection();
            // #1196: pity-timer and other word-pulse requests belong to one round and one lobby.
            ContractHudWordPulse.Clear();
            _pinnedCompleteKeys.Clear();
            _regressionLoggedKeys.Clear();
            _alertBelt.Hide();
            ContractHudAlerts.Clear();
            Core.Compatibility.WeatherChangeAlerts.Reset();
            _sectionsOnly = false;
            _isShowing = false;
            _arrivalState = _arrivalState.Begin();
            _isCompact = false;
            _showingResult = false;
            _resultIsWarning = false;
            _displayedContract = MoonContractType.None;
            _lastLiveText = string.Empty;
            _lastLivePlainText = string.Empty;
            _lastSemanticSignature = string.Empty;
            ApplyPresentationVisibility(ShouldPresent());
            ContractHudLayoutBridge.SetReservedPromptTop(0f);

            // #823: the card may not exist yet, or its scene may already have taken it.
            if (_canvasGroup == null)
                return;

            if (immediate)
            {
                _canvasGroup.alpha = 0f;
                return;
            }

            _fadeCoroutine = StartCoroutine(FadeOut());
        }

        internal void ShowResult(string title, string body, bool isWarning)
        {
            _sectionsOnly = false;
            // #823: the card is (re)built under whatever HUD container exists right now.
            if (!EnsureCardBuilt())
                return;

            ClearArrivalReveal();
            _arrivalBaseHeight = 0f;
            if (_animCoroutine != null)
            {
                StopCoroutine(_animCoroutine);
                _animCoroutine = null;
            }

            if (_liveCoroutine != null)
            {
                StopCoroutine(_liveCoroutine);
                _liveCoroutine = null;
            }

            if (_resultCoroutine != null)
            {
                StopCoroutine(_resultCoroutine);
                _resultCoroutine = null;
            }

            if (_fadeCoroutine != null)
            {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }

            if (_textSwapFadeCoroutine != null)
            {
                StopCoroutine(_textSwapFadeCoroutine);
                _textSwapFadeCoroutine = null;
            }

            StopObjectiveTypewriter();
            StopTypewriterSound();
            StopLayoutTween();

            _isShowing = true;
            _arrivalState = _arrivalState.Dock();
            _isCompact = false;
            _showingResult = true;
            ResetPresentation();
            // #1196: the result card ends the contract, so no row is left for a pulse request to name.
            ContractHudWordPulse.Clear();
            if (_contractIcon != null) _contractIcon.gameObject.SetActive(false);
            _resultIsWarning = isWarning;
            _alertBelt.Hide();
            _displayedContract = MoonContractType.None;
            _lastLiveText = body ?? string.Empty;
            _lastLivePlainText = _lastLiveText;
            _lastSemanticSignature = string.Empty;

            _cardRect.anchorMin = Vector2.one;
            _cardRect.anchorMax = Vector2.one;
            _cardRect.pivot = Vector2.one;
            _cardRect.anchoredPosition = GetMotionAdjustedSettledPosition();
            _cardRect.localScale = Vector3.one * SettledScale;
            _titleText.text = string.IsNullOrWhiteSpace(title) ? "COMPANY REPORT" : title;
            _objectiveText.alpha = 1f;
            _objectiveText.text = body ?? string.Empty;
            _objectiveText.maxVisibleCharacters = int.MaxValue;
            ResizeLiveCard(_objectiveText.text, animate: false, titleText: _titleText.text);
            SetBorderVisible(true);
            ApplyResultTheme(isWarning);
            UpdateReservedPromptTop();

            _resultCoroutine = StartCoroutine(RunResultCard());
        }

        private IEnumerator RunResultCard()
        {
            const float fadeInDuration = 0.16f;
            const float fadeOutDuration = 0.35f;
            float targetScale = SettledScale;
            float elapsed = 0f;
            _canvasGroup.alpha = 0f;
            _cardRect.localScale = Vector3.one * (targetScale * 0.94f);

            while (elapsed < fadeInDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / fadeInDuration);
                float smooth = 1f - Mathf.Pow(1f - t, 3f);
                _canvasGroup.alpha = smooth;
                _cardRect.localScale = Vector3.one * Mathf.Lerp(targetScale * 0.94f, targetScale, smooth);
                UpdateReservedPromptTop();
                yield return null;
            }

            _canvasGroup.alpha = 1f;
            _cardRect.localScale = Vector3.one * targetScale;
            UpdateReservedPromptTop();
            yield return new WaitForSecondsRealtime(ResultDisplayDuration);

            elapsed = 0f;
            while (elapsed < fadeOutDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / fadeOutDuration);
                _canvasGroup.alpha = 1f - t;
                yield return null;
            }

            _canvasGroup.alpha = 0f;
            _resultCoroutine = null;
            Hide(immediate: true);
        }

        /// <summary>
        /// #883/#1195. Opens the settled card with no moon contract for its in-card sections (the
        /// power-fault row, the SIDE section). Title, icon and body stay off while
        /// <see cref="_sectionsOnly"/> holds; a later ShowContract takes the card over.
        /// </summary>
        private bool OpenSectionsOnlyCard()
        {
            if (!EnsureCardBuilt())
                return false;
            if (_fadeCoroutine != null)
            {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }

            _sectionsOnly = true; _isShowing = true; _arrivalState = _arrivalState.Dock(); _isCompact = true; _showingResult = false;
            _canvasGroup.alpha = 1f; _cardRect.localScale = Vector3.one * SettledScale;
            _titleText.text = string.Empty; _objectiveText.text = string.Empty;
            _cardRect.anchoredPosition = GetMotionAdjustedSettledPosition();
            _liveCoroutine = StartCoroutine(LiveUpdateLoop());
            ApplyUnitAdapter(false);
            ApplyTheme();
            ApplyPresentationVisibility(ShouldPresent());
            return true;
        }

        /// <summary>
        /// #823. Makes sure a card exists under the current vanilla HUD container, building or
        /// rebuilding it when the container appeared, changed or was destroyed with its scene.
        /// Falls back to an overlay canvas of its own only while no HUD container exists, and
        /// tears that fallback down the moment the real container is available. Returns false
        /// only if nothing at all could host the card. The card is built at alpha 0, so the
        /// doors-sequence warm (#1233 S8) can build it long before the reveal shows anything.
        /// </summary>
        internal bool EnsureCardBuilt()
        {
            Transform hudContainer = ResolveVanillaHudContainer();
            Transform target = hudContainer != null ? hudContainer : EnsureFallbackCanvas().transform;

            // Unity's overridden equality: a destroyed adapter root or parent compares equal to
            // null here, so a container that went away with its scene forces a rebuild.
            if (_adapterRoot != null && _attachedParent != null && _attachedParent == target)
                return true;

            if (_adapterRoot != null)
                Destroy(_adapterRoot.gameObject);
            ReleaseHudTextMaterials();
            _adapterRoot = null;
            _canvasGroup = null;
            _cardRect = null;
            _arrivalMask = null;
            _arrivalFlashing = false;
            if (hudContainer != null && _fallbackCanvas != null)
            {
                Destroy(_fallbackCanvas.gameObject);
                _fallbackCanvas = null;
            }

            BuildCard(target);
            _attachedParent = target;
            return _adapterRoot != null;
        }

        private static Transform ResolveVanillaHudContainer()
        {
            HUDManager hud = HUDManager.Instance;
            GameObject container = hud != null ? hud.HUDContainer : null;
            return container != null ? container.transform : null;
        }

        /// <summary>
        /// #823. The pre-#823 canvas, kept only for the case where no vanilla HUD container
        /// exists when a card is requested. Same scaler as before so the card's units are the
        /// same 1080-tall reference space either way.
        /// </summary>
        private Canvas EnsureFallbackCanvas()
        {
            if (_fallbackCanvas != null)
                return _fallbackCanvas;

            var canvasGo = new GameObject("FallbackCanvas");
            canvasGo.transform.SetParent(transform, false);
            _fallbackCanvas = canvasGo.AddComponent<Canvas>();
            _fallbackCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _fallbackCanvas.sortingOrder = 100;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, ReferenceHeight);
            scaler.matchWidthOrHeight = 1f;
            return _fallbackCanvas;
        }

        private void BuildCard(Transform parent)
        {
            // A3: no GraphicRaycaster. The card is presentation only — it has no pointer
            // handlers, no Selectables, and the root CanvasGroup sets blocksRaycasts and
            // interactable false — so it never takes part in an EventSystem pointer query.
            var rootGo = new GameObject("Y4NGZ_ContractHud", typeof(RectTransform), typeof(CanvasGroup));
            rootGo.transform.SetParent(parent, false);
            // Last sibling: over the vanilla HUD's own children, still under QuickMenu, which is
            // a later sibling of the container on the same canvas.
            rootGo.transform.SetAsLastSibling();
            _adapterRoot = rootGo.GetComponent<RectTransform>();
            _adapterRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _adapterRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _adapterRoot.pivot = new Vector2(0.5f, 0.5f);
            _canvasGroup = rootGo.GetComponent<CanvasGroup>();
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.alpha = 0f;

            Canvas host = parent.GetComponentInParent<Canvas>();
            _hostCanvas = host != null ? host.rootCanvas : null;
            _borderImages.Clear();
            _borderObjects.Clear();
            _borderObjectsCached = false;
            _vanillaFontVerified = false;
            ContractHudLayoutBridge.SetReservedPromptTop(0f);
            ApplyUnitAdapter(force: true);

            var cardGo = new GameObject("ContractCard", typeof(RectTransform), typeof(RectMask2D));
            cardGo.transform.SetParent(_adapterRoot, false);
            _cardRect = cardGo.GetComponent<RectTransform>();
            _arrivalMask = cardGo.GetComponent<RectMask2D>();
            _arrivalMask.enabled = false;
            _cardRect.anchorMin = Vector2.one;
            _cardRect.anchorMax = Vector2.one;
            _cardRect.pivot = Vector2.one;
            _cardRect.anchoredPosition = GetSettledPosition();

            // #823: no filled backdrop. The vanilla clock and quota boxes are the DialogueBoxSimple
            // frame over nothing — the world reads straight through them — and the card follows.
            // Reuse the actual clock artwork with its rendered slice width, without its tilt.
            CreateBorder(cardGo.transform);

            var titleGo = new GameObject("TitleText");
            titleGo.transform.SetParent(cardGo.transform, false);
            _titleText = titleGo.AddComponent<TextMeshProUGUI>();
            _titleText.fontSize = 32f;
            // #799: one weight across the card. The vanilla clock and quota boxes set their
            // headers in the regular face, and the bold title was the loudest thing on screen.
            _titleText.fontStyle = FontStyles.Normal;
            _titleText.color = ContractUiTheme.HudAccent;
            _titleText.alignment = TextAlignmentOptions.TopLeft;
            _titleText.enableWordWrapping = false;
            _titleText.overflowMode = TextOverflowModes.Overflow;
            _titleText.raycastTarget = false;
            // #1233 D6: the departed Shadow Raid title is struck with a TMP tag, so rich text is
            // on by intent, not by TMP's default. BuildLiveTitle escapes the name it wraps.
            _titleText.richText = true;
            _titleRect = _titleText.rectTransform;
            AssignVanillaFont(_titleText);

            var objGo = new GameObject("ObjectiveText");
            objGo.transform.SetParent(cardGo.transform, false);
            _objectiveText = objGo.AddComponent<TextMeshProUGUI>();
            _objectiveText.fontSize = 22f;
            _objectiveText.fontStyle = FontStyles.Normal;
            _objectiveText.color = ContractUiTheme.HudAccent;
            _objectiveText.alignment = TextAlignmentOptions.TopLeft;
            _objectiveText.enableWordWrapping = true;
            _objectiveText.overflowMode = TextOverflowModes.Overflow;
            _objectiveText.richText = true;
            _objectiveText.raycastTarget = false;
            _objectiveRect = _objectiveText.rectTransform;
            AssignVanillaFont(_objectiveText);
            // #1196: row cues are vertex edits on this text, so they belong to this card build.
            _rowCues = new ContractHudRowCueRenderer(_objectiveText);
            // #724: the belt sits between the title rule and the objective body. Built after
            // the body so it draws over the backdrop but under nothing that matters.
            BuildPresentationArt(cardGo.transform);
            _alertBelt.Build(cardGo.transform, AssignVanillaFont);
            BuildPowerFaultRow(cardGo.transform);
            BuildSideSection(cardGo.transform);
            ApplyCardLayout(_cardRect.sizeDelta);

            // The UI camera culls to the UI layer alone; a freshly created object is on Default
            // and would never be drawn. Every child inherits the container's layer.
            ApplyLayerRecursively(rootGo, parent.gameObject.layer);
        }

        private static void ApplyLayerRecursively(GameObject root, int layer)
        {
            root.layer = layer;
            Transform t = root.transform;
            for (int i = 0; i < t.childCount; i++)
                ApplyLayerRecursively(t.GetChild(i).gameObject, layer);
        }

        /// <summary>
        /// #823. Sizes and scales the adapter root so the card's 1080-tall reference units map
        /// onto the host rect: on the vanilla HUD container that is the 16:9 rect the game's
        /// aspect fitter maintains (about 900x506 canvas units, so one card unit is 0.469 of
        /// them), on the fallback canvas it is the canvas itself (one to one). Re-run on layout
        /// changes and on the live poll; writes only when the host rect actually changed.
        /// </summary>
        private void ApplyUnitAdapter(bool force)
        {
            if (_adapterRoot == null)
                return;

            var parentRect = _adapterRoot.parent as RectTransform;
            Vector2 parentSize = parentRect != null ? parentRect.rect.size : Vector2.zero;
            if (parentSize.x < 1f || parentSize.y < 1f)
            {
                // Not laid out yet: fall back to the host canvas's own extent in canvas units.
                float canvasScale = _hostCanvas != null && _hostCanvas.scaleFactor > 0.001f
                    ? _hostCanvas.scaleFactor
                    : Mathf.Max(0.001f, Screen.height / ReferenceHeight);
                parentSize = new Vector2(Screen.width / canvasScale, Screen.height / canvasScale);
            }

            if (!force && (parentSize - _adapterParentSize).sqrMagnitude < 0.25f)
                return;

            _adapterParentSize = parentSize;
            float unitScale = parentSize.y / ReferenceHeight;
            _adapterRoot.localScale = new Vector3(unitScale, unitScale, 1f);
            _adapterRoot.anchoredPosition = Vector2.zero;
            _adapterRoot.sizeDelta = new Vector2(parentSize.x / unitScale, ReferenceHeight);

            if (!force && _cardRect != null && _isShowing)
            {
                if (_isSettled)
                    _cardRect.anchoredPosition = GetMotionAdjustedSettledPosition();
                UpdateReservedPromptTop();
            }
        }

        /// <summary>Screen pixels per card unit, for converting the safe-area insets.</summary>
        private float PixelsPerCardUnit()
        {
            float canvasScale = _hostCanvas != null && _hostCanvas.scaleFactor > 0.001f
                ? _hostCanvas.scaleFactor
                : Mathf.Max(0.001f, Screen.height / ReferenceHeight);
            float unitScale = _adapterRoot != null ? _adapterRoot.localScale.y : 1f;
            return Mathf.Max(0.001f, canvasScale * unitScale);
        }





        /// <summary>
        /// The card can be built before HUDManager exists, so the initial
        /// AssignVanillaFont can miss the terminal font. Re-assert it on the first
        /// reveal that runs with a live HUDManager (#520 B4c).
        /// </summary>
        private void EnsureVanillaFont()
        {
            if (_vanillaFontVerified || HUDManager.Instance == null || HUDManager.Instance.clockNumber == null)
                return;

            AssignVanillaFont(_titleText);
            AssignVanillaFont(_objectiveText);
            _alertBelt.AssignFont(AssignVanillaFont);
            if (_powerFaultText != null) AssignVanillaFont(_powerFaultText);
            if (_sideText != null) { AssignVanillaFont(_sideText); _sideTextDirty = true; }
            _vanillaFontVerified = true;
        }

        private void AssignVanillaFont(TextMeshProUGUI tmp)
        {
            if (HUDManager.Instance != null && HUDManager.Instance.clockNumber != null)
            {
                TextMeshProUGUI clock = HUDManager.Instance.clockNumber;
                var vanillaFont = clock.font;
                if (vanillaFont != null)
                {
                    tmp.font = vanillaFont;
                    // Preserve the complete vanilla preset, including its own shadow settings.
                    // Own the copy so TMP never modifies the clock's shared material.
                    if (clock.fontSharedMaterial != null)
                    {
                        if (!_hudTextMaterials.TryGetValue(tmp, out var material))
                        {
                            material = new Material(clock.fontSharedMaterial);
                            material.name = "ContractHUD " + tmp.name;
                            _hudTextMaterials.Add(tmp, material);
                        }
                        else
                            material.CopyPropertiesFromMaterial(clock.fontSharedMaterial);
                        tmp.fontSharedMaterial = material;
                        tmp.UpdateMeshPadding();
                    }
                    return;
                }
            }

            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            foreach (var f in fonts)
            {
                if (f.name.Contains("3270") || f.name.Contains("edunline") || f.name.Contains("EdgeOf"))
                {
                    tmp.font = f;
                    return;
                }
            }
        }

        private void ReleaseHudTextMaterials()
        {
            foreach (var material in _hudTextMaterials.Values)
                if (material != null) Destroy(material);
            _hudTextMaterials.Clear();
        }

        private void CreateBorder(Transform parent)
        {
            _cardFrame = ContractHudFrame.Create(parent, "BorderFrame");
            _borderImages.Add(_cardFrame);
        }

        private IEnumerator AnimateSequence(string title, string objective)
        {
            yield return AnimateApprovedArrival();
        }

        private IEnumerator TypewriterText(TextMeshProUGUI tmp, string fullText, bool playAudio, int startVisibleCharacters = 0)
        {
            tmp.text = fullText ?? string.Empty;
            tmp.maxVisibleCharacters = 0;
            tmp.ForceMeshUpdate();

            int visibleCharacters = tmp.textInfo.characterCount;
            int startVisible = Mathf.Clamp(startVisibleCharacters, 0, visibleCharacters);
            if (visibleCharacters <= 0)
            {
                tmp.maxVisibleCharacters = int.MaxValue;
                yield break;
            }

            tmp.maxVisibleCharacters = startVisible;
            if (startVisible >= visibleCharacters)
            {
                tmp.maxVisibleCharacters = int.MaxValue;
                yield break;
            }

            if (playAudio)
                PlayTypewriterSound();

            for (int i = startVisible + 1; i <= visibleCharacters; i++)
            {
                tmp.maxVisibleCharacters = i;
                yield return new WaitForSecondsRealtime(TypewriterCharDelay);
            }

            tmp.maxVisibleCharacters = int.MaxValue;

            if (playAudio)
                StopTypewriterSound();
        }

        private Vector2 GetCenteredPosition()
        {
            // #823: the adapter root is the card's whole coordinate space, sized in card units.
            float hostWidth = _adapterRoot != null && _adapterRoot.rect.width > 1f
                ? _adapterRoot.rect.width
                : Screen.width / Mathf.Max(0.001f, Screen.height / ReferenceHeight);
            float hostHeight = _adapterRoot != null && _adapterRoot.rect.height > 1f
                ? _adapterRoot.rect.height
                : ReferenceHeight;
            Vector2 scaledSize = _cardRect.sizeDelta * _cardRect.localScale.x;
            return new Vector2(
                -hostWidth * 0.5f + scaledSize.x * 0.5f,
                -hostHeight * 0.5f + scaledSize.y * 0.5f);
        }

        private Vector2 GetSettledPosition()
        {
            float pixelsPerUnit = PixelsPerCardUnit();
            Rect safeArea = Screen.safeArea;
            float rightInset = Mathf.Max(0f, Screen.width - safeArea.xMax) / pixelsPerUnit;
            float topInset = Mathf.Max(0f, Screen.height - safeArea.yMax) / pixelsPerUnit;
            float beltOffset = ((_isSettled && _alertBelt.IsActive) || (!_isSettled && _arrivalBeltReserved))
                ? (ContractHudAlertBelt.Height + LiveCardBeltGap) * SettledScale : 0f;
            return new Vector2(-(CornerMargin + rightInset), -(CornerMargin + topInset + beltOffset));
        }

        private Vector2 GetMotionAdjustedSettledPosition()
        {
            return GetSettledPosition() + GameplayHudMotion.CurrentOffset * ContractMotionScale;
        }

        private IEnumerator LiveUpdateLoop()
        {
            while (_isShowing)
            {
                // E1: vanilla hides its own HUD for cutscenes, the death transition and the
                // fired sequence. The card is a HUD surface, so it follows. The state is
                // re-read every tick, so the card comes back on the next poll once the hide
                // clears; nothing latches.
                bool hudHidden = IsVanillaHudHidden();
                // #823: the host rect can change with the resolution; one rect read per poll.
                ApplyUnitAdapter(force: false);
                ApplyPresentationVisibility(GameplayUiVisibility.IsVisibleUnderPauseMenu && !hudHidden);

                // E1/B1: a dead or spectating local player cannot act on the card, so the whole
                // poll body — including the salvage scans — is skipped until they respawn.
                if (!hudHidden && !IsLocalPlayerSpectating())
                {
                    // #727: two vanilla field reads per poll; posts a belt segment on change.
                    Core.Compatibility.WeatherChangeAlerts.Sample();
                    RenderLivePanel(force: false);
                    // #671. The vanilla tip stack changes with whatever the player is looking at, and
                    // that never routes through a card layout event. Re-checking on the live tick keeps
                    // a tip that lights up mid-round from landing on the card; the call writes only
                    // when the required offset actually moved.
                    UpdateReservedPromptTop();
                }

                yield return new WaitForSecondsRealtime(0.25f);
            }
        }

        /// <summary>
        /// E1. <c>HUDManager.HideHUD(bool)</c> is the vanilla entry point, but the state it
        /// writes (<c>hudHidden</c>) is private, so it is read through a cached field ref
        /// rather than patched. A failed resolve degrades to "not hidden", which is the
        /// pre-E1 behaviour.
        /// </summary>
        private static bool IsVanillaHudHidden()
        {
            var hud = HUDManager.Instance;
            if (hud == null)
                return false;

            if (!_hudHiddenRefResolved)
            {
                _hudHiddenRefResolved = true;
                try
                {
                    _hudHiddenRef = AccessTools.FieldRefAccess<HUDManager, bool>("hudHidden");
                }
                catch (Exception e)
                {
                    _hudHiddenRef = null;
                    ContractsBootstrap.Log?.LogWarning(
                        "[MoonContracts] HUDManager.hudHidden is unavailable; the contract card will not follow vanilla HUD hides: "
                        + e.Message);
                }
            }

            if (_hudHiddenRef == null)
                return false;

            try
            {
                return _hudHiddenRef(hud);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLocalPlayerSpectating()
        {
            var network = GameNetworkManager.Instance;
            var local = network != null ? network.localPlayerController : null;
            if (local == null)
                return false;

            // Spectating always implies isPlayerDead, and vanilla only clears
            // spectatedPlayerScript on revive, so the death flag is the safe signal: a stale
            // spectate reference must never freeze a living player's card.
            return local.isPlayerDead;
        }

        private void RenderLivePanel(bool force)
        {
            if (_showingResult || (_displayedContract == MoonContractType.None && !MoonContractState.CurrentLevelSupportsSalvageContract()))
                return;
            AppendLiveSemanticSignature(_signatureBuilder);
            bool beltChanged = UpdateAlertBelt();
            if (_isSettled) _cardRect.anchoredPosition = GetMotionAdjustedSettledPosition();
            // The ticker and state gathering continue, but volatile counter updates must not
            // interrupt the check/expand/type/collapse transaction.
            var objectives = GetPresentedObjectives();
            // #1196: judged before the busy gate, so the tracker always compares consecutive polls.
            ConsumeChangeDetection(objectives, seedOnly: false);
            var next = ContractHudObjectivePresentation.Select(objectives);
            if (_presentationBusy)
            {
                if (!next.HasValue || !next.Value.IsFailed) return;
                StopObjectiveTypewriter();
                _objectiveText.alpha = 1f;
            }
            var completed = ContractHudObjectivePresentation.CompletedPrevious(objectives, _activeObjective, next);
            _activeObjective = next;
            if (!force && completed.HasValue && ContractHudObjectivePresentation.BlackoutPair(objectives) == null)
            {
                _objectiveTypeCoroutine = StartCoroutine(AnimateObjectiveAdvance(completed.Value, _audioEligibleForLastPoll));
                return;
            }
            _isCompact = true;
            string nextText = BuildLiveObjectiveText(compact: true);
            if (!force && nextText == _lastLiveText)
            {
                // #1233 D6: the struck title follows the selected row, not the body text. The
                // arrival and advance sequences write the body without passing here, so a card
                // that reached the departed row that way strikes its title on the next poll.
                if (ContractHudObjectivePresentation.IsDriveDeparted(next) != _titleStruck)
                    ApplyLiveTitle(next);
                if (beltChanged) { ApplyCardLayout(_cardRect.sizeDelta); UpdateReservedPromptTop(); }
                return;
            }
            ApplyLiveTitle(next);
            ApplyObjectiveText(nextText, force, allowTypewriter: false);
        }

        /// <summary>#1233 D6: writes the live title and its struck state together, then re-tints it.</summary>
        private void ApplyLiveTitle(MoonContractObjective? selected)
        {
            _titleStruck = ContractHudObjectivePresentation.IsDriveDeparted(selected);
            _titleText.text = BuildLiveTitle();
            ApplyTheme();
        }

        private void ApplyObjectiveText(string nextText, bool force, bool allowTypewriter, bool fadeSwap = false)
        {
            string nextPlainText = ToPlainObjectiveText(nextText);
            // E4: a compact <-> full layout swap changes the line count without adding any
            // content, so it must never be read as an addition. The card grows from one line
            // to two purely because it expanded, and typing the "new" line in from the old
            // caret position is what produced the spurious retype on every scrap deposit.
            bool animateAddition = allowTypewriter
                && !force
                && !fadeSwap
                && !string.IsNullOrEmpty(_lastLivePlainText)
                && CountObjectiveLines(nextPlainText) > CountObjectiveLines(_lastLivePlainText);
            // An expansion that a genuine objective change caused still retypes, but from the
            // start of the body rather than mid-line.
            bool retypeWholeBody = allowTypewriter && !force && fadeSwap;
            int startVisible = animateAddition ? GetCurrentObjectiveVisibleCharacters() : 0;

            StopObjectiveTypewriter();
            _lastLiveText = nextText;
            _lastLivePlainText = nextPlainText;
            ResizeLiveCard(nextText, animate: !force);

            if (animateAddition || retypeWholeBody)
            {
                _objectiveText.alpha = 1f;
                _objectiveTypeCoroutine = StartCoroutine(TypewriterObjectiveUpdate(nextText, startVisible));
                return;
            }

            if (fadeSwap && isActiveAndEnabled)
            {
                // Full <-> compact layout swaps cross-fade the body text instead of
                // snapping (#520 B4b).
                if (_textSwapFadeCoroutine != null)
                    StopCoroutine(_textSwapFadeCoroutine);
                _textSwapFadeCoroutine = StartCoroutine(FadeObjectiveSwap(nextText));
                return;
            }

            _objectiveText.alpha = 1f;
            _objectiveText.text = nextText;
            _objectiveText.maxVisibleCharacters = int.MaxValue;
        }

        private IEnumerator FadeObjectiveSwap(string nextText)
        {
            float half = TextSwapFadeDuration * 0.5f;
            float elapsed = 0f;
            while (elapsed < half)
            {
                elapsed += Time.unscaledDeltaTime;
                _objectiveText.alpha = Mathf.Lerp(1f, 0f, Mathf.Clamp01(elapsed / half));
                yield return null;
            }

            _objectiveText.text = nextText;
            _objectiveText.maxVisibleCharacters = int.MaxValue;

            elapsed = 0f;
            while (elapsed < half)
            {
                elapsed += Time.unscaledDeltaTime;
                _objectiveText.alpha = Mathf.Lerp(0f, 1f, Mathf.Clamp01(elapsed / half));
                yield return null;
            }

            _objectiveText.alpha = 1f;
            _textSwapFadeCoroutine = null;
        }

        private IEnumerator TypewriterObjectiveUpdate(string nextText, int startVisible)
        {
            yield return TypewriterText(_objectiveText, nextText, playAudio: true, startVisibleCharacters: startVisible);
            _objectiveTypeCoroutine = null;
        }

        private void StopObjectiveTypewriter()
        {
            _presentationBusy = false;
            if (_objectiveTypeCoroutine != null)
            {
                StopCoroutine(_objectiveTypeCoroutine);
                _objectiveTypeCoroutine = null;
            }

            StopTypewriterSound();
        }

        private int GetCurrentObjectiveVisibleCharacters()
        {
            if (_objectiveText == null)
                return 0;

            _objectiveText.ForceMeshUpdate();
            int characterCount = _objectiveText.textInfo.characterCount;
            if (_objectiveText.maxVisibleCharacters == int.MaxValue)
                return characterCount;

            return Mathf.Clamp(_objectiveText.maxVisibleCharacters, 0, characterCount);
        }

        private static string ToPlainObjectiveText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var sb = new StringBuilder(text.Length);
            bool insideTag = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<')
                {
                    insideTag = true;
                    continue;
                }

                if (insideTag)
                {
                    if (c == '>')
                        insideTag = false;
                    continue;
                }

                if (c == '\r')
                    continue;

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static int CountObjectiveLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            int count = 0;
            bool hasContent = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    if (hasContent)
                        count++;
                    hasContent = false;
                    continue;
                }

                if (!char.IsWhiteSpace(c))
                    hasContent = true;
            }

            if (hasContent)
                count++;

            return count;
        }

        private string BuildLiveTitle()
        {
            // B1: the title depends on nothing but the displayed contract and, since #1233 D6,
            // whether it is struck, so it is built once per pair instead of once per poll.
            if (_cachedTitle != null && _cachedTitleContract == _displayedContract && _cachedTitleStruck == _titleStruck)
                return _cachedTitle;

            _cachedTitleContract = _displayedContract;
            _cachedTitleStruck = _titleStruck;
            _cachedTitle = ContractHudObjectivePresentation.Title(
                _displayedContract == MoonContractType.None
                    ? "SALVAGE"
                    : MoonContractCatalog.Get(_displayedContract).DisplayName.ToUpperInvariant(),
                _titleStruck);
            return _cachedTitle;
        }

        /// <summary>
        /// #880. The displayed contract's rows with the completion pins applied. Every card read
        /// goes through here so the selection, the signature and the advance animation all see the
        /// same monotonic list; a provider that steps a boolean action backwards is logged once.
        /// </summary>
        private List<MoonContractObjective> GetPresentedObjectives()
        {
            var objectives = MoonContractState.GetObjectives(_displayedContract);
            _regressedKeyScratch.Clear();
            ContractHudObjectivePresentation.PinCompletedActions(objectives, _pinnedCompleteKeys, _regressedKeyScratch);
            for (int i = 0; i < _regressedKeyScratch.Count; i++)
            {
                string key = _regressedKeyScratch[i];
                if (_regressionLoggedKeys.Add(key))
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts] Objective '{key}' on {_displayedContract} reported incomplete after completing; " +
                        "the card keeps it complete for the round. The provider should latch it (#880).");
            }
            return objectives;
        }

        private string BuildLiveObjectiveText(bool compact)
        {
            if (_displayedContract == MoonContractType.None)
                return FormatObjectiveLine(new MoonContractObjective("Collect the scrap", 0, 1, key: "salvage.collect"));
            var objectives = GetPresentedObjectives();
            var pair = ContractHudObjectivePresentation.BlackoutPair(objectives);
            if (pair != null)
                return FormatObjectiveLine(pair[0]) + "\n" + FormatObjectiveLine(pair[1]);
            var objective = ContractHudObjectivePresentation.Select(objectives);
            if (!objective.HasValue)
                return "Objectives complete";
            // #1076: a failed row closes the card, so the voided line belongs under it here. Select
            // returns the failed row first and WithStatusContext passes it straight through, so this
            // is the one place the live card can say the contract is over instead of showing a lone
            // red step. #1233 D6: the departed Shadow Raid drive is the exception; its struck title
            // says the contract is over, so its row is the whole body.
            if (objective.Value.IsFailed)
                return ContractHudObjectivePresentation.AppendsVoidedLine(objective.Value)
                    ? FormatObjectiveLine(objective.Value) + "\n" + FormatVoidedLine()
                    : FormatObjectiveLine(objective.Value);
            return FormatObjectiveLine(ContractHudObjectivePresentation.WithStatusContext(objective.Value, objectives));
        }

        /// <summary>#1076: the one wording for a contract that can no longer be completed.</summary>
        private static string FormatVoidedLine() => $"<color={ContractUiTheme.DimHex}>Contract voided.</color>";

        private static string FormatObjectiveLine(MoonContractObjective objective)
        {
            string text = ContractHudObjectivePresentation.Escape(objective.Text);
            // #1076: a failed row is danger red because it is the crew's problem; a failed row that
            // is also LOST is grey, because the object has left the moon and the red would be
            // telling them to act on something that is gone. A live lethal row keeps the danger
            // tier the row type has documented since #520 - "their ship has it" is the last moment
            // the crew can still do something about it, so it reads as loud as it is.
            string tint = objective.IsFailed
                    ? (objective.Lost ? ContractUiTheme.DimHex : ContractUiTheme.DangerHex)
                : objective.Lethal ? ContractUiTheme.DangerHex
                : objective.IsComplete ? ContractUiTheme.DimHex : ContractUiTheme.HudAccentHex;
            if (objective.Kind == MoonContractObjectiveKind.Status)
                return $"<color={tint}>{text}</color>";
            string marker = objective.IsFailed ? "[!]" : objective.IsComplete ? "[X]" : "[ ]";
            if (objective.Total > 1) text += $" {objective.Complete}/{objective.Total}";
            // Invisible marker characters carry row identity/metrics for procedural checkboxes.
            // A TMP indent keeps wrapped lines aligned to the text, never under the box.
            return $"<indent=32><pos=0><alpha=#00>{marker}<alpha=#FF><pos=32><color={tint}>{text}</color></indent>";
        }

        /// <summary>
        /// Builds the semantic signature from every objective list the card reads.
        /// </summary>
        private string BuildLiveSemanticSignature()
        {
            AppendLiveSemanticSignature(_signatureBuilder);
            return _signatureBuilder.ToString();
        }

        /// <summary>
        /// Writes the signature into <paramref name="sb"/> in place. See
        /// <see cref="BuildLiveSemanticSignature"/> for what the signature means.
        /// </summary>
        private void AppendLiveSemanticSignature(StringBuilder sb)
        {
            sb.Length = 0;
            sb.Append((int)_displayedContract).Append('|');
            if (_displayedContract != MoonContractType.None)
            {
                AppendObjectiveSignature(sb, GetPresentedObjectives());
            }

            // Salvage has fixed copy; only escalation changes affect its signature.
            AppendEscalationSignatures(sb);
        }

        /// <summary>Ordinal compare against a built signature without allocating a string.</summary>
        private static bool BuilderEquals(StringBuilder sb, string other)
        {
            if (other == null || sb.Length != other.Length)
                return false;

            for (int i = 0; i < other.Length; i++)
            {
                if (sb[i] != other[i])
                    return false;
            }

            return true;
        }

        private static void AppendObjectiveSignature(StringBuilder sb, IReadOnlyList<MoonContractObjective> objectives)
        {
            if (objectives == null)
                return;

            for (int i = 0; i < objectives.Count; i++)
            {
                MoonContractObjective objective = objectives[i];
                // #520: the signature is computed from the objective's stable key, never its
                // display text. Objectives whose text carries a volatile payload (countdown
                // timers, running totals) declare an explicit key at the provider, which
                // replaces the old literal "DRILL \u00B7"/"DEADLINE \u00B7"/"DETONATION \u00B7" prefix guard.
                string signatureKey = objective.SignatureKey;
                sb.Append('|')
                    .Append((int)objective.Kind).Append(':')
                    .Append(signatureKey).Append(':')
                    .Append(objective.Complete).Append('/')
                    .Append(objective.Total).Append(':')
                    .Append(objective.IsFailed ? 'F' : objective.IsComplete ? 'C' : 'A');
            }
        }

        private static void AppendEscalationSignatures(StringBuilder sb)
        {
            if (MoonEscalationState.HasActiveEscalation)
            {
                sb.Append("|E:").Append(MoonEscalationState.ActiveTitle);
                var objectives = MoonEscalationState.GetActiveObjectives();
                AppendObjectiveSignature(sb, objectives);
            }

            if (CodeRebirthCrashedShipObjectiveState.IsActive)
            {
                sb.Append("|E:").Append(CodeRebirthCrashedShipObjectiveState.ActiveTitle);
                var objectives = CodeRebirthCrashedShipObjectiveState.GetActiveObjectives();
                AppendObjectiveSignature(sb, objectives);
            }

            if (ShipPowerEscalationObjectiveState.IsActive)
            {
                sb.Append("|E:").Append(ShipPowerEscalationObjectiveState.ActiveTitle);
                var objectives = ShipPowerEscalationObjectiveState.GetActiveObjectives();
                AppendObjectiveSignature(sb, objectives);
            }

            // Posted alerts also refresh the layout when they appear or expire. Their
            // local reveal/read pause belongs to the belt, not to the objective card.
            ContractHudAlerts.AppendSignature(sb);
        }

        /// <summary>
        /// #724. Rebuilds the belt's segment list from every active source and hands it to
        /// the belt. Returns true when the belt's content or activity changed. Segment text is
        /// <c>NAME · objective</c>; a counted objective carries its counter inline.
        /// </summary>
        private bool UpdateAlertBelt()
        {
            if (_showingResult)
                return _alertBelt.SetSegments(null);

            _beltSegments.Clear();
            if (MoonEscalationState.HasActiveEscalation)
                AppendBeltSegments(MoonEscalationState.ActiveTitle, MoonEscalationState.GetActiveObjectives());
            if (CodeRebirthCrashedShipObjectiveState.IsActive)
                AppendBeltSegments(CodeRebirthCrashedShipObjectiveState.ActiveTitle, CodeRebirthCrashedShipObjectiveState.GetActiveObjectives());
            // Ship Systems owns a persistent fault panel independently of this card (#889).
            ContractHudAlerts.Collect(_beltSegments);

            // Preflight still collects the segments to reserve the settled destination,
            // but SetSegments must never activate the nested strip before docking.
            return _alertBelt.SetSegments(ContractHudArrival.ContentVisible(_isSettled, _beltSegments.Count > 0)
                ? _beltSegments : null);
        }

        // #1343 S2: read-only world corners of the alert strip, the escalation band's collapse target.
        // False while the strip is not on screen (no card, result card, hidden HUD, no escalation segment).
        internal static bool TryGetAlertBeltWorldCorners(Vector3[] corners)
        {
            ContractHUDManager manager = Instance;
            if (manager == null || corners == null || corners.Length < 4)
                return false;
            if (!manager._isShowing || manager._showingResult)
                return false;
            if (manager._adapterRoot == null || !manager._adapterRoot.gameObject.activeInHierarchy)
                return false;
            if (!manager._alertBelt.IsActive)
                return false;

            RectTransform root = manager._alertBelt.Root;
            if (root == null || !root.gameObject.activeInHierarchy)
                return false;

            root.GetWorldCorners(corners);
            return true;
        }

        // #1343 S7: read-only world corners of the contract card body, the escalation toast's anchor.
        // False while the card is not on screen (no card, result card, hidden HUD, no adapter) or still in its centred arrival.
        internal static bool TryGetCardWorldCorners(Vector3[] corners)
        {
            ContractHUDManager manager = Instance;
            if (manager == null || corners == null || corners.Length < 4)
                return false;
            if (!manager._isShowing || manager._showingResult)
                return false;
            if (!manager._isSettled)
                return false;
            if (manager._adapterRoot == null || !manager._adapterRoot.gameObject.activeInHierarchy)
                return false;

            RectTransform card = manager._cardRect;
            if (card == null || !card.gameObject.activeInHierarchy)
                return false;

            card.GetWorldCorners(corners);
            return true;
        }

        private static void AppendBeltSegments(string title, IReadOnlyList<MoonContractObjective> objectives)
        {
            if (objectives == null || objectives.Count == 0)
                return;

            // One segment per source: the first pending objective, or the last line when
            // everything is settled (a failed disrupt, an extracted drive). The belt is a
            // headline strip, not a checklist, so it never lists every line.
            MoonContractObjective selected = objectives[objectives.Count - 1];
            for (int i = 0; i < objectives.Count; i++)
            {
                if (!objectives[i].IsComplete)
                {
                    selected = objectives[i];
                    break;
                }
            }

            bool lethal = false;
            for (int i = 0; i < objectives.Count; i++)
                lethal |= objectives[i].Lethal;

            _beltSegmentBuilder.Length = 0;
            ContractHudAlertText.Append(_beltSegmentBuilder, title, selected);

            _beltSegments.Add(new ContractHudAlertSegment
            {
                Text = _beltSegmentBuilder.ToString(),
                Key = ContractHudAlertText.Identity(title, selected, lethal),
                Lethal = lethal
            });
        }

        /// <summary>#724. The belt scroll is the one thing on the card that moves every
        /// frame, so it has its own Update rather than riding the quarter-second poll. The
        /// vanilla control-tip mirror (#671) rides the same Update because it must follow the
        /// vanilla lines whether or not a card is showing.</summary>
        private void Update()
        {
            VanillaControlTipMirror.Tick();
            TickPowerFaultRow();
            if (_isShowing) EnsureVanillaFont();
            TickRowCues();
            TickSideSection();

            if (!_isShowing || _showingResult || !_alertBelt.IsActive)
                return;

            if (_adapterRoot == null || !_adapterRoot.gameObject.activeInHierarchy)
                return;

            _alertBelt.Tick(Time.unscaledDeltaTime);
        }

        private Vector2 MeasureLiveCardSize(string objectiveText, bool compact)
        {
            float width = LiveCardDefaultWidth;
            float contentWidth = width - LiveCardInnerSidePadding * 2f;
            float bodyHeight = _objectiveText.GetPreferredValues(objectiveText ?? string.Empty, contentWidth, 4000f).y;
            float minimum = compact ? LiveCardCompactMinHeight : LiveCardMinHeight;
            float height = ContractHudArrival.DockedHeight(bodyHeight, minimum, LiveCardTopPadding,
                LiveCardTitleHeight, LiveCardTitleGap, LiveCardBottomPadding, PowerFaultRowHeight, SideSectionHeight);
            return new Vector2(width, _sectionsOnly
                ? ContractHudSideSectionLayout.SectionsOnlyHeight(LiveCardBottomPadding, PowerFaultRowHeight, SideSectionHeight)
                : height);
        }

        private void ResizeLiveCard(string objectiveText, bool animate, string titleText = null)
        {
            if (_cardRect == null || _titleText == null || _objectiveText == null) return;
            if (_isShowing && !_showingResult && _animCoroutine != null && _arrivalBaseHeight > 0f)
            {
                // The power row may change while the intro, flight or typewriter is active.
                // Preserve the measured first objective; never resize from its still-empty TMP
                // text or start a competing layout tween during arrival.
                _arrivalDockedHeight = ContractHudArrival.ArrivalDestinationHeight(
                    _arrivalBaseHeight, PowerFaultRowHeight, SideSectionHeight);
                _sideAppliedHeight = SideSectionHeight;
                if (_isSettled)
                    ApplyCardLayout(new Vector2(LiveCardDefaultWidth, _arrivalDockedHeight));
                return;
            }
            // Width and text size stay fixed. Long objectives grow vertically instead of shrinking type.
            Vector2 target = MeasureLiveCardSize(objectiveText, _isCompact);
            _sideAppliedHeight = SideSectionHeight;
            if (!animate) { StopLayoutTween(); ApplyCardLayout(target); return; }
            if ((target - _cardRect.sizeDelta).sqrMagnitude <= .01f) return;
            StopLayoutTween();
            _layoutTweenCoroutine = StartCoroutine(AnimateCardLayout(_cardRect.sizeDelta, target));
        }

        private IEnumerator AnimateCardLayout(Vector2 startSize, Vector2 targetSize)
        {
            float elapsed = 0f;
            while (elapsed < LayoutTweenDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / LayoutTweenDuration);
                float smooth = t * t * (3f - 2f * t);
                ApplyCardLayout(Vector2.Lerp(startSize, targetSize, smooth));
                yield return null;
            }

            ApplyCardLayout(targetSize);
            _layoutTweenCoroutine = null;
        }

        private void StopLayoutTween()
        {
            if (_layoutTweenCoroutine != null)
            {
                StopCoroutine(_layoutTweenCoroutine);
                _layoutTweenCoroutine = null;
            }
        }

        private void ApplyCardLayout(Vector2 size)
        {
            if (_cardRect == null || _titleRect == null || _objectiveRect == null) return;
            _cardRect.sizeDelta = size;
            LayoutPowerFaultRow();
            LayoutSideSection();
            ApplyHeaderPose(_headerExpansion);
            // Separate strip above the card; no objective space is consumed by escalation text.
            _alertBelt.Layout(-(ContractHudAlertBelt.Height + LiveCardBeltGap), 0f);
            _objectiveRect.anchorMin = Vector2.zero;
            _objectiveRect.anchorMax = Vector2.one;
            _objectiveRect.pivot = new Vector2(.5f, .5f);
            _objectiveRect.offsetMin = new Vector2(LiveCardInnerSidePadding,
                ContractHudSideSectionLayout.BodyBottom(LiveCardBottomPadding, PowerFaultRowHeight, SideSectionHeight));
            _objectiveRect.offsetMax = new Vector2(-LiveCardInnerSidePadding, -(LiveCardTopPadding + LiveCardTitleHeight + LiveCardTitleGap));
            UpdateReservedPromptTop();
        }

        private void UpdateReservedPromptTop()
        {
            if (_cardRect == null
                || !_isShowing
                || !GameplayUiVisibility.IsVisibleUnderPauseMenu
                || _canvasGroup == null
                || _canvasGroup.alpha <= 0.001f)
            {
                ContractHudLayoutBridge.SetReservedPromptTop(0f);
                return;
            }

            // During arrival reserve the measured first-objective footprint rather than the
            // transient 600x190 intro. Motion is applied separately by the prompt overlay.
            float scale = _isSettled ? _cardRect.localScale.y : SettledScale;
            float effectiveHeight = (_isSettled ? _cardRect.sizeDelta.y : _arrivalDockedHeight) * scale;
            float topInset = Mathf.Abs(GetSettledPosition().y);
            float reservedTop = topInset + effectiveHeight + LiveCardPromptClearance;
            // #671: with Y4NGZUI absent, VanillaControlTipMirror reads this reservation on its
            // own tick and keeps the mirrored vanilla tips below it.
            ContractHudLayoutBridge.SetReservedPromptTop(reservedTop);
        }

        private IEnumerator LoadTypewriterAudio()
        {
            string path = null;
            Exception pathException = null;
            try
            {
                path = ResolveTypewriterAudioPath();
            }
            catch (Exception exception)
            {
                pathException = exception;
            }

            if (pathException != null)
            {
                MarkTypewriterAudioUnavailable(
                    $"path lookup threw {pathException.GetType().Name}: {pathException.Message}");
                yield break;
            }

            if (string.IsNullOrEmpty(path))
            {
                MarkTypewriterAudioUnavailable($"'{TypewriterClipFileName}' is missing.");
                yield break;
            }

            UnityWebRequest request = null;
            Exception requestCreationException = null;
            try
            {
                request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.WAV);
            }
            catch (Exception exception)
            {
                requestCreationException = exception;
            }

            if (requestCreationException != null || request == null)
            {
                string failure = requestCreationException != null
                    ? $"{requestCreationException.GetType().Name}: {requestCreationException.Message}"
                    : "Unity returned no request";
                MarkTypewriterAudioUnavailable($"request creation for '{path}' failed: {failure}");
                yield break;
            }

            using (request)
            {
                UnityWebRequestAsyncOperation sendOperation = null;
                Exception sendException = null;
                try
                {
                    sendOperation = request.SendWebRequest();
                }
                catch (Exception exception)
                {
                    sendException = exception;
                }

                if (sendException != null || sendOperation == null)
                {
                    string failure = sendException != null
                        ? $"{sendException.GetType().Name}: {sendException.Message}"
                        : "Unity returned no asynchronous operation";
                    MarkTypewriterAudioUnavailable($"request for '{path}' could not start: {failure}");
                    yield break;
                }

                yield return sendOperation;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    MarkTypewriterAudioUnavailable($"request for '{path}' failed: {request.error}");
                    yield break;
                }

                AudioClip decodedClip = null;
                Exception decodeException = null;
                try
                {
                    decodedClip = DownloadHandlerAudioClip.GetContent(request);
                    if (decodedClip != null)
                        decodedClip.name = "LGU_ContractTypewriter";
                }
                catch (Exception exception)
                {
                    decodeException = exception;
                }

                if (decodeException != null)
                {
                    if (decodedClip != null)
                        Destroy(decodedClip);
                    MarkTypewriterAudioUnavailable(
                        $"decoding '{path}' threw {decodeException.GetType().Name}: {decodeException.Message}");
                    yield break;
                }

                if (decodedClip == null)
                {
                    MarkTypewriterAudioUnavailable($"decoding '{path}' returned no audio clip.");
                    yield break;
                }

                _typewriterClip = decodedClip;
            }
        }

        private static string ResolveTypewriterAudioPath()
        {
            string assemblyDir = Path.GetDirectoryName(typeof(ContractsBootstrap).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                Path.Combine(assemblyDir, "ContractAudio", "UI", TypewriterClipFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", "ContractAudio", "UI", TypewriterClipFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "ContractAudio", "UI", TypewriterClipFileName),
                Path.Combine(Paths.PluginPath, "ContractAudio", "UI", TypewriterClipFileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ContractAudio", "UI", TypewriterClipFileName)
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private void MarkTypewriterAudioUnavailable(string reason)
        {
            _typewriterAudioLoadFailed = true;
            if (_typewriterFallbackWarningLogged)
                return;

            _typewriterFallbackWarningLogged = true;
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts] Contract typewriter audio unavailable ({reason}); "
                + "using vanilla HUDManager.dialogueBleeps[0] when available.");
        }

        private static AudioClip ResolveVanillaTypewriterClip()
        {
            HUDManager hudManager = HUDManager.Instance;
            if (hudManager == null
                || hudManager.dialogueBleeps == null
                || hudManager.dialogueBleeps.Length == 0)
            {
                return null;
            }

            return hudManager.dialogueBleeps[0];
        }

        private void PlayTypewriterSound()
        {
            if (_typewriterAudioSource == null)
                return;

            AudioClip clip = _typewriterClip;
            if (clip == null && _typewriterAudioLoadFailed)
                clip = ResolveVanillaTypewriterClip();
            if (clip == null)
                return;

            _typewriterAudioSource.Stop();
            _typewriterAudioSource.clip = clip;
            _typewriterAudioSource.pitch = TypewriterPitch;
            _typewriterAudioSource.volume = TypewriterVolume;
            _typewriterAudioSource.Play();
        }

        private void StopTypewriterSound()
        {
            if (_typewriterAudioSource != null)
                _typewriterAudioSource.Stop();
        }

        private IEnumerator FadeOut()
        {
            float duration = 0.5f;
            float start = _canvasGroup.alpha;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                // #823: the card can be destroyed with its scene mid-fade.
                if (_canvasGroup == null)
                {
                    _fadeCoroutine = null;
                    yield break;
                }
                _canvasGroup.alpha = Mathf.Lerp(start, 0f, elapsed / duration);
                yield return null;
            }

            if (_canvasGroup != null)
                _canvasGroup.alpha = 0f;
            _fadeCoroutine = null;
        }

        private void ApplyResultTheme(bool isWarning)
        {
            Color color = isWarning ? ContractUiTheme.Warning : ContractUiTheme.HudAccent;
            if (_titleText != null)
                _titleText.color = color;
            if (_objectiveText != null)
                _objectiveText.color = color;
            for (int i = 0; i < _borderImages.Count; i++)
            {
                if (_borderImages[i] != null)
                    _borderImages[i].color = color;
            }
        }

        private void SetBorderVisible(bool visible, bool reapplyTheme = true)
        {
            if (reapplyTheme)
                ApplyTheme();

            if (_cardRect == null)
                return;

            // B1: the frame (or the four edges) is created once per card build and never
            // reparented, so the name scan over the card's children only runs on the first call
            // after a build instead of on every 0.25s poll.
            if (!_borderObjectsCached)
            {
                _borderObjects.Clear();
                var card = _cardRect.transform;
                for (int i = 0; i < card.childCount; i++)
                {
                    var child = card.GetChild(i);
                    if (child.name.StartsWith("Border"))
                        _borderObjects.Add(child.gameObject);
                }

                _borderObjectsCached = true;
                _borderVisible = !visible;
            }

            if (_borderVisible == visible)
                return;

            _borderVisible = visible;
            for (int i = 0; i < _borderObjects.Count; i++)
            {
                if (_borderObjects[i] != null)
                    _borderObjects[i].SetActive(visible);
            }
        }

        private void ApplyTheme()
        {
            // #1233 D6: a struck title is dim, and the theme keeps it dim rather than repainting it.
            if (_titleText != null)
                _titleText.color = _titleStruck ? ContractUiTheme.AccentDim : ContractUiTheme.HudAccent;
            if (_objectiveText != null)
                _objectiveText.color = ContractUiTheme.HudAccent;
            _alertBelt.ApplyTheme();
            if (_powerFaultText != null) _powerFaultText.color = ContractUiTheme.HudAccent;
            if (_powerFaultDivider != null) _powerFaultDivider.color = ContractUiTheme.HudAccent;
            ApplySideSectionTheme();
            if (_contractIcon != null) _contractIcon.color = ContractUiTheme.HudAccent;
            if (_objectiveMarks != null) _objectiveMarks.color = ContractUiTheme.HudAccent;
            for (int i = 0; i < _borderImages.Count; i++)
            {
                if (_borderImages[i] != null)
                    _borderImages[i].color = FrameColor;
            }
            ReapplyCardGlow();
        }

        private void OnUiThemeChanged(HudColorPreset preset)
        {
            // #1195: the SIDE text carries its tints inline, so it is rebuilt on its next tick.
            _sideTextDirty = true;
            if (_showingResult)
            {
                ApplyResultTheme(_resultIsWarning);
                return;
            }

            ApplyTheme();
            if (_isShowing && _isSettled)
                RenderLivePanel(force: true);
        }

        private void OnGameplayUiVisibilityChanged(bool visible)
        {
            ApplyPresentationVisibility(visible && !IsVanillaHudHidden());
        }

        /// <summary>
        /// E1. The card is presented when the shared gameplay-UI gate allows it and vanilla is
        /// not hiding its own HUD.
        /// </summary>
        private bool ShouldPresent()
        {
            return GameplayUiVisibility.IsVisibleUnderPauseMenu && !IsVanillaHudHidden();
        }

        private void OnHudMotionSampled(Vector2 offset)
        {
            if (!_isShowing || !_isSettled || _cardRect == null)
                return;

            _cardRect.anchoredPosition = GetSettledPosition() + offset * ContractMotionScale;
        }

        /// <summary>
        /// C4. The screen geometry changed, so the settled corner did too: re-anchor against it and
        /// republish the reservation, since <see cref="GetSettledPosition"/> insets by
        /// <c>Screen.safeArea</c> and the prompt stack's clearance is measured from it.
        /// </summary>
        private void OnHudLayoutChanged()
        {
            if (!_isShowing || _cardRect == null)
                return;

            // #823: the host rect follows the screen, so the adapter is re-derived first.
            ApplyUnitAdapter(force: true);
            if (_isSettled)
                _cardRect.anchoredPosition = GetMotionAdjustedSettledPosition();

            UpdateReservedPromptTop();
        }

        private void ApplyPresentationVisibility(bool visible)
        {
            // #823: the whole card root is switched, not a canvas. Under the vanilla HUD
            // container this is the same mechanism HideHUD uses one level up.
            if (_adapterRoot != null && _adapterRoot.gameObject.activeSelf != visible)
                _adapterRoot.gameObject.SetActive(visible);

            if (!visible)
            {
                StopTypewriterSound();
                ContractHudLayoutBridge.SetReservedPromptTop(0f);
                return;
            }

            UpdateReservedPromptTop();
        }

        internal static void EnsureInstance()
        {
            if (Instance != null)
                return;

            var go = new GameObject("LGUContractHUD_Canvas");
            go.AddComponent<ContractHUDManager>();
        }
    }

    public static class ContractHudLayoutBridge
    {
        private static float _contractReservedTop;
        // E2: no caller in this repository, and that is correct — it is written from outside it.
        // Y4NGZUpgrades' ammo counter publishes its own footprint here as it moves
        // (Y4NGZUpgrades/src/Y4NGZUpgrades/HUD/AmmoCounterHud.cs, via SetAmmoReservedPromptTop),
        // so the prompt stack clears the contract card and the ammo row together. Removing it
        // would silently drop the ammo row out of the shared stack.
        private static float _ammoReservedTop;

        /// <summary>The first free top-right Y position below the contract card.</summary>
        public static float GetReservedPromptTop()
        {
            return _contractReservedTop;
        }

        /// <summary>The first free Y position below every currently visible stack item.</summary>
        public static float GetStackedPromptTop()
        {
            return Mathf.Max(_contractReservedTop, _ammoReservedTop);
        }

        /// <summary>Published by the optional Upgrades ammo row as it moves.</summary>
        public static void SetAmmoReservedPromptTop(float value)
        {
            _ammoReservedTop = Mathf.Max(0f, value);
        }

        internal static void SetReservedPromptTop(float value)
        {
            _contractReservedTop = Mathf.Max(0f, value);
        }
    }
}
