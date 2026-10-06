using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Experience.UITheme;
namespace Y4NGZCompany.Contracts._Shared
{
    internal sealed class ContractPerformanceReportData
    {
        internal string Title;
        internal bool HasContract;
        internal bool Completed;
        internal ContractPerformanceReportBox Directive;
        internal ContractPerformanceReportBox HazardPay;
        internal ContractPerformanceReportBox CrewLosses;
        internal ContractPerformanceReportBox RevisedQuota;
        internal IReadOnlyList<string> PlayerNotes;
        internal string GradeOverride;

        /// <summary>
        /// #1203: set only by the debug report preview. The preview renders through
        /// <see cref="ContractPerformanceReportUi.TryApply"/> like a real report but owns no
        /// round-end state: see <c>PerformanceReportPreview</c>.
        /// </summary>
        internal bool IsPreview;

        /// <summary>
        /// #1203: per-slot stand-ins for this round's XP note candidates, used instead of the
        /// round stats when <see cref="IsPreview"/> is set.
        /// </summary>
        internal IReadOnlyList<PerformanceReportNoteEntry>[] PreviewXpNoteCandidates;

        /// <summary>
        /// #1200: the slot the preview's Your XP panel reads - the sample's ledger slot, which is
        /// the local player's when it fits. A real report asks the local player controller.
        /// </summary>
        internal int PreviewLocalSlot = -1;
    }

    internal sealed class ContractPerformanceReportBox
    {
        internal string Heading;
        internal string Detail;
        internal int Amount;
        internal int StartAmount;
        internal float ImpactPercent;
        internal string MiddleText;
        internal bool HasStartAmount;
        internal bool HasSign;
        internal bool IsIncrease;
        internal bool Animate;
        internal bool PlayConfirmWhenStatic;
        internal bool IsFinalQuota;
        internal string ValueOverride;
        internal string SecondaryValue;
        internal ReportPerformanceBoxKind Kind;

        /// <summary>
        /// Text for the cell's bottom qualifier band. Falls back to <see cref="MiddleText"/>
        /// and then <see cref="Detail"/>, so the impact boxes keep working unchanged. A box
        /// that resolves to nothing renders no bottom band at all.
        /// </summary>
        internal string Qualifier;

        /// <summary>
        /// #556: normally the qualifier states a fact about the value and rests at the muted
        /// weight. Set this where the qualifier IS the fact the valence hue belongs to - the
        /// casualties cell's body count - so the count is colored rather than the percent
        /// alone. See docs/UI_PRESENTATION_GUIDELINES.md.
        /// </summary>
        internal bool QualifierCarriesValence;

        /// <summary>
        /// Renders a zero impact with its sign ("+0%") so a cell that always states its
        /// effect on the quota states it the same way whether or not it fired.
        /// </summary>
        internal bool ShowSignAtZero;

        /// <summary>
        /// Leave at <see cref="ReportValence.Auto"/> to have the report derive it from the
        /// box's own numbers; set it only when the derivation cannot know better.
        /// </summary>
        internal ReportValence Valence;
    }

    internal enum ReportPerformanceBoxKind
    {
        Amount,
        Collected,
        Impact,
        RevisedQuota,
        Grade,
    }

    /// <summary>
    /// What a cell's primary value means for the crew, which is what colors it. The sign
    /// convention across the whole row is "effect on the quota": a value that raises the
    /// quota is a penalty, a value that lowers it is a benefit.
    /// </summary>
    internal enum ReportValence
    {
        /// <summary>Derive from the box's numbers.</summary>
        Auto,
        /// <summary>The raw figure the math starts from - neither good nor bad.</summary>
        Lead,
        Neutral,
        Benefit,
        Penalty,
    }

    internal static partial class ContractPerformanceReportUi
    {
        private const string RowName = "LGU_ContractPerformanceQuotaSummaryRow";
        // Every mod-owned object under the row is re-colored by name in
        // ApplyActiveReportTheme, so a new object needs its name known there or it keeps the
        // hue it was born with when the player switches theme mid-report.
        private const string CellFrameNamePrefix = "LGU_QuotaReportCellFrame_";
        private const string CellTextNamePrefix = "LGU_QuotaReportCellText_";
        private const string ConnectorNamePrefix = "LGU_QuotaReportConnector_";
        private const string HeadingTextName = "Heading";
        private const string PrimaryTextName = "Primary";
        private const string QualifierTextName = "Qualifier";
        // #556: the row's own scanline wash and the distressed cell frame. Both are Images
        // under the row, so both have to be named here for ApplyActiveReportTheme to find.
        private const string ScanlineOverlayName = "LGU_QuotaReportScanlineOverlay";
        // #747: the row's background is one of two objects. Preferred is a clone of the
        // vanilla panel graphic itself - the strip is then literally the panel, continued,
        // and follows the source's live color so the vanilla animator's fade and any theme
        // retint of the panel carry over. The opaque scrub is the fallback when no panel
        // graphic could be identified. Only the scrub is re-colored by the theme pass; the
        // clone mirrors its source instead.
        private const string VanillaBottomScrubName = "LGU_QuotaReportVanillaBottomScrub";
        private const string VanillaPanelCloneName = "LGU_QuotaReportVanillaPanelClone";
        private const string CellFrameSliceName = "Frame";
        // #716 (audit E12): named here for the same reason as everything above - the revised
        // quota's direction arrow is re-colored by name in ApplyActiveReportTheme.
        private const string DirectionArrowUpName = "FinalQuotaDirectionArrowUp";
        private const string DirectionArrowDownName = "FinalQuotaDirectionArrowDown";
        private const string PerformanceReportAudioFolderName = "PerformanceReport";
        private const string PerformanceReportImageFolderName = "PerformanceReport";
        private const string CountdownClipFileName = "Countdown.wav";
        private const string CountdownConfirmClipFileName = "Countdown Confirm.mp3";
        private const string FinalQuotaConfirmClipFileName = "Final Quota Confirm.mp3";
        private const string FinalQuotaDecreaseClipFileName = "soundshelfstudio-ui-chime-notification-sound-553111.mp3";
        private const string FinalQuotaIncreaseClipFileName = "soundshelfstudio-ui-drop-error-521939.mp3";
        private const string FinalQuotaIncreaseArrowFileName = "final_quota_arrow_up.png";
        private const string FinalQuotaDecreaseArrowFileName = "final_quota_arrow_down.png";
        private const float ReportSequenceStartDelaySeconds = 1.35f;
        private const float ReportSequencePreAnimationHoldSeconds = 2f;
        private const float ReportSequencePostAnimationHoldSeconds = 1.5f;
        private const float ReportCloseAnimatorNormalizedTime = 0.92f;
        private const float ReportClosedAnimatorNormalizedTime = 0.99f;
        private const float ReportCloseXpBufferSeconds = 0.05f;
        private const float PostReportXpPopupBufferSeconds = 0.75f;
        private const float PerformanceReportPendingDelaySeconds = 4f;
        private const float PerformanceReportDelayFailsafeSeconds = 25f;
        private const float XpPopupMinimumHoldSeconds = 1.1f;
        private const float XpPopupValueDurationMaximumSeconds = 2.5f;
        private const float XpPopupRankOrMarksBeatSeconds = 0.4f;
        private const float XpPopupAudioTailSeconds = 1f;
        private const float XpPopupVanillaSetPlayerLevelSeconds = 8f;
        private const float XpPopupVanillaPostPlayerLevelHoldSeconds = 3f;
        private const float XpPopupVanillaEntranceLeadSeconds = 0.75f;
        private const float XpPopupEntranceVisibilityTimeoutSeconds = 4f;
        // The endgame clip is held at speed 0 for the whole XP hold window, so it
        // cannot fade the level box in or out around the meter. We drive that fade
        // ourselves through a CanvasGroup we own on the level box.
        private const float XpPopupOwnedFadeSeconds = 0.35f;
        private const float XpPopupExitFailsafeSeconds = 3.5f;
        private const float XpPopupReplayFailsafeExtraSeconds = 4f;
        private const float PostXpDaysLeftBufferSeconds = 1.5f;
        private const float DaysLeftPopupHudChromeHoldSeconds = 4f;
        private const float ConfirmBeatSeconds = 0.2f;
        private const float PerformanceReportAudioVolume = 1f;
        private const float PerformanceReportConfirmVolumeScale = 1.35f;
        private const string VanillaEndgameStatsStateName = "EndgameStats";
        private const string VanillaEndgameStatsChallengeStateName = "EndgameStatsChallenge";
        private const string VanillaEndgameStatsDisplayTriggerName = "displayStats";
        private const string VanillaEndgameStatsDisplayChallengeTriggerName = "displayStatsChallenge";
        private const string VanillaEndgameStatsPenaltyTriggerName = "displayPenalty";
        private const string VanillaLevelUpShakeTriggerName = "Shake";

        // #654: when vanilla recoloring sits at Default the vanilla report keeps its stock
        // red family and nothing retints it, so a strip drawn from the orange theme palette
        // reads as a different screen. In that state every hue below swaps to a vanilla-red
        // report family; any configured preset keeps the shared theme palette, whose
        // VanillaUiThemePolish retint makes the two halves match from the other side.
        private static bool UseVanillaReportFamily => UiTheme.CurrentPreset == HudColorPreset.Default;

        // #747: the two hues the vanilla report actually draws its text in, read from the
        // hierarchy dump: player names, the quota figures and the grade letter are #FF3200,
        // the notes are #FF521F. Under the vanilla family the row uses those and nothing
        // else - values in the name hue, chrome (headings, connectors, qualifiers) in the
        // notes hue. A separate danger red and a brownish dim were the "random colors".
        private static readonly Color VanillaReportAccent = new Color(1.00f, 0.196f, 0.00f, 1f);
        private static readonly Color VanillaReportDim = new Color(1.00f, 0.32f, 0.12f, 1f);
        private static readonly Color VanillaReportMuted = VanillaReportDim;
        private static readonly Color VanillaReportDanger = VanillaReportAccent;
        private static readonly Color VanillaReportPanel = new Color(0.17f, 0.035f, 0.02f, 0.94f);

        private static Color ThemeAccent => UseVanillaReportFamily ? VanillaReportAccent : UiTheme.Accent;
        private static Color ThemeDim => UseVanillaReportFamily ? VanillaReportDim : UiTheme.Dim;
        private static Color ThemeMuted => UseVanillaReportFamily ? VanillaReportMuted : UiTheme.Muted;
        private static Color ThemeDanger => UseVanillaReportFamily ? VanillaReportDanger : UiTheme.Danger;
        private static Color ThemePanel => UseVanillaReportFamily ? VanillaReportPanel : UiTheme.Panel;

        private static Color NeutralColor => ThemeAccent;
        private static Color ActiveColor => Color.Lerp(ThemeAccent, UiTheme.White, 0.22f);
        private static Color PanelColor
        {
            get
            {
                Color color = ThemePanel;
                color.a = 0f;
                return color;
            }
        }
        private static Color VanillaBottomRowScrubColor
        {
            get
            {
                Color color = ThemePanel;
                color.a = 1f;
                return color;
            }
        }
        private static Color VanillaBottomRowRuleColor => UiPalette.WithAlpha(ThemeDim, 0.38f);

        // #554: the ledger reads left-to-right as quota math, so a cell's primary value is
        // tinted by what it does to the quota rather than by which cell it sits in. Every
        // hue here is a report-family lookup - see docs/UI_PRESENTATION_GUIDELINES.md.
        // #747: white is a palette slot only a colored preset owns. Vanilla's report has no
        // white in it, so under its family the lead value takes the accent like every other
        // value and the row stops introducing a hue the panel above it never uses.
        private static Color LeadValueColor => UseVanillaReportFamily ? ThemeAccent : UiTheme.White;
        private static Color BenefitValueColor => ThemeAccent;
        private static Color PenaltyValueColor => ThemeDanger;
        // Also the resting hue of the qualifier band: a qualifier states a fact about the
        // value rather than being one, so it reads at the same weight as a neutral value.
        private static Color NeutralValueColor => ThemeMuted;
        // The cell's structural glyphs - the heading above the value and the connector that
        // points at the cell. Neither is a value, so neither carries a valence.
        private static Color CellChromeColor => ThemeDim;

        // An arrow pointing up is the quota rising, which is the row's Penalty sign; down is
        // Benefit. Derived from the glyph's own direction so the theme pass can re-resolve it
        // from the object's name alone, with no per-image box lookup.
        private static Color DirectionArrowColor(bool pointsUp) =>
            pointsUp ? PenaltyValueColor : BenefitValueColor;

        private static readonly Vector2 RowAnchorMin = new Vector2(0.135f, 0.035f);
        private static readonly Vector2 RowAnchorMax = new Vector2(0.865f, 0.135f);

        private static readonly List<GameObject> HiddenVanillaBottomRowObjects = new List<GameObject>();
        private static AudioClip _countdownClip;
        private static AudioClip _countdownConfirmClip;
        private static AudioClip _finalQuotaConfirmClip;
        private static AudioClip _finalQuotaDecreaseClip;
        private static AudioClip _finalQuotaIncreaseClip;
        private static Sprite _finalQuotaIncreaseArrowSprite;
        private static Sprite _finalQuotaDecreaseArrowSprite;
        private static bool _audioLoadAttempted;
        private static bool _imageLoadAttempted;
        private static bool _performanceReportDelayArmed;
        private static bool _reportSequenceActive;
        private static bool _xpPopupSequenceActive;
        private static bool _xpEndgameStatsAnimatorPaused;
        private static float _xpEndgameStatsAnimatorOriginalSpeed = 1f;
        private static CanvasGroup _xpPopupFadeGroup;
        private static float _xpPopupFadeGroupRestoreAlpha = 1f;
        private static XpPopupReplayPlan _xpPopupReplayPlan;
        private static string[] _foreignPlayerNotes;
        private static HUDManager _foreignPlayerNotesHud;
        private static float _performanceReportDelayFailsafeUntilRealtime;
        private static float _performanceReportCloseObservedAtRealtime;
        private static bool _performanceReportCloseObserved;
        private static bool _performanceReportDelayFailsafeLogged;
        private static float _delayXpPopupUntilRealtime;
        private static float _delayDaysLeftUntilRealtime;
        private static float _suppressRoundEndHudChromeUntilRealtime;
        private static Coroutine _tooltipRestoreCoroutine;
        private static bool _tooltipRestoreRunning;
        // How long the keep-alive tolerates a missing HUDManager before it decides
        // the round-end presentation is never coming back and hands the HUD over.
        private const float HudMissingKeepAliveGiveUpSeconds = 2f;
        // #451: what the report *claims*, not what it snapshotted. The
        // alpha/activeSelf snapshots live in HudSuppressionRegistry, refcounted
        // against the monitor takeover, which hides the very same CanvasGroups and
        // the very same hotbar icons.
        private static readonly List<GameObject> SuppressedReportTooltips = new List<GameObject>();
        private static readonly List<CanvasGroup> SuppressedReportHudCanvasGroups = new List<CanvasGroup>();
        private static bool _themeSubscribed;

        // #716 (audit B12): the three lookups below are polled from per-frame gates for the
        // whole round-end presentation (WaitForPerformanceReportXpReplayClear,
        // WaitForXpPopupEntrance, and the round-end patch's own wait), and each one used to
        // walk every RectTransform or GameObject Unity has loaded - hidden and inactive
        // objects included - on every one of those frames. Each is resolved once and held by
        // reference. Unity's == reports a destroyed object as null, so a scene change
        // re-resolves on its own; a miss is retried on an interval rather than every frame,
        // which is what bounds the cost when the object genuinely does not exist yet.
        private const float RoundEndLookupRetrySeconds = 0.5f;
        private static bool _keepAliveStopFailureLogged;
        private static RectTransform _cachedReportRow;
        private static float _reportRowLookupRetryAtRealtime;
        private static RectTransform _cachedLevelUpBox;
        private static float _levelUpBoxLookupRetryAtRealtime;
        private static readonly List<RectTransform> CachedUpgradeTokenStrips = new List<RectTransform>();
        private static float _upgradeTokenStripLookupRetryAtRealtime;

        private static RectTransform _activeReportRow;
        private static TMP_Text _activeReportTitle;
        // #747: the vanilla panel graphic the row measured itself against and clones for its
        // own fill. Resolved per apply, dropped in Restore.
        private static Graphic _vanillaPanelGraphic;
        private static ReportBackingOpacity _reportBackingOpacity;
        // #747: where the vanilla panel art's lower band plate (the COLLECTED/GRADE band) is
        // drawn, in report-root units, for the report being shown. Null when the panel art
        // could not be measured; the row then falls back to rect-based placement.
        private static VanillaBandPlacement? _vanillaPanelBand;
        // #747: the drawn panel's bounds in report-root units, kept so the note labels can be
        // clamped inside the art after the row has been placed.
        private static LocalRectBounds? _vanillaPanelBounds;
        // The measured art the band came from, kept for the outline-sheet trim below.
        private static PerformanceReportSurfaceSprites.VanillaPanelArt? _vanillaPanelArt;
        private static float _vanillaPanelSpriteHeight;

        // #747: the vanilla outline sheet (`EndgameStats/Lines`) bakes the COLLECTED box and
        // the GRADE label into the same image as the player boxes, and the panel plate under
        // them is translucent, so nothing drawn on top can hide them. Instead the sheet is
        // swapped for a copy cut off at the band's top edge and its rect shrunk by the same
        // amount, which leaves the player boxes exactly where vanilla draws them and the
        // band bare for the row. Restored on every Restore.
        private static Image _trimmedOutlineSheet;
        private static Sprite _trimmedOutlineSheetOriginalSprite;
        private static Sprite _trimmedOutlineSheetSprite;
        // #747: objects the suppressor has had to re-hide this report, each logged once.
        private static readonly HashSet<GameObject> ReassertedVanillaBottomRowObjects = new HashSet<GameObject>();
        private static Vector2 _trimmedOutlineSheetOriginalSizeDelta;
        private static Vector2 _trimmedOutlineSheetOriginalAnchoredPosition;
        private static bool OutlineSheetTrimmed => _trimmedOutlineSheet != null;

        private struct VanillaBandPlacement
        {
            internal float MinX;
            internal float MaxX;
            /// <summary>Top edge of the band plate.</summary>
            internal float TopY;
            /// <summary>Lowest edge at which the band still spans its full width (above the slant).</summary>
            internal float FullBottomY;
            /// <summary>Lowest drawn edge of the band, slant included.</summary>
            internal float ArtBottomY;
            internal float Width => MaxX - MinX;
            internal float CenterX => (MinX + MaxX) * 0.5f;
        }
        // #747: one EndgameStats hierarchy dump per report, at Debug. It is what lets the
        // vanilla art lookups be corrected from a single run instead of guessed at.
        private static bool _hierarchyDumped;
        // #554: a primary value is colored by what it does to the quota, which the label's
        // own name cannot say. The live row keeps the value->box link so a theme change can
        // re-derive the valence hue instead of flattening every label to one color.
        private static readonly Dictionary<TMP_Text, ContractPerformanceReportBox> ActiveReportPrimaryValues =
            new Dictionary<TMP_Text, ContractPerformanceReportBox>();
        // #555: the note labels belong to the vanilla report, which also renders without a
        // contract. What the report borrows it has to give back, so the first touch of each
        // label is snapshotted here and replayed in Restore().
        private static readonly Dictionary<TextMeshProUGUI, NoteLabelState> NoteLabelStates =
            new Dictionary<TextMeshProUGUI, NoteLabelState>();
        private static readonly Vector3[] XpPopupWorldCorners = new Vector3[4];

        internal static bool IsRoundEndPresentationActive =>
            _reportSequenceActive
            || _performanceReportDelayArmed
            || _xpPopupSequenceActive
            || Time.realtimeSinceStartup < _suppressRoundEndHudChromeUntilRealtime;

        internal static bool TryApply(HUDManager hud, ContractPerformanceReportData report, bool logMissing = true)
        {
            EnsureThemeSubscription();
            if (hud == null || hud.statsUIElements == null || report == null)
                return false;

            var titleText = FindTitleText(hud);
            var canvasRoot = FindCanvasRoot(hud, titleText);
            var reportRoot = FindReportContentRoot(hud, titleText);
            if (titleText == null || canvasRoot == null || reportRoot == null)
            {
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.ReportDiag] TryApply blocked: " +
                    $"titleFound={titleText != null}, canvasFound={canvasRoot != null}, " +
                    $"reportRootFound={reportRoot != null}, " +
                    $"endOfRunStats={DescribeText(hud.EndOfRunStatsText)}, " +
                    $"quota={DescribeText(hud.statsUIElements?.quotaNumerator)}, " +
                    $"animator={DescribeTransform(hud.endgameStatsAnimator != null ? hud.endgameStatsAnimator.transform : null)}.");
                if (logMissing)
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts] Performance report title/root not found; contract report overlay fallback remains available.");
                else
                    ContractsBootstrap.Log?.LogDebug("[MoonContracts] Performance report title/root not ready; retrying.");
                return false;
            }

            string titleBefore = titleText.text;
            titleText.text = string.IsNullOrWhiteSpace(report.Title) ? "PERFORMANCE REPORT" : report.Title;
            // #716 (audit E12): the palette's white slot, not the literal. ApplyActiveReportTheme
            // already re-derives this title through UiTheme.White on a theme change, so a literal
            // here was simply the one frame of the report that ignored the palette.
            titleText.color = UiTheme.White;
            titleText.fontSize = Mathf.Min(titleText.fontSize, 22f);
            titleText.enableWordWrapping = false;
            titleText.overflowMode = TextOverflowModes.Ellipsis;

            DestroyChild(canvasRoot, RowName);
            var legacyRoot = FindReportRoot(hud, titleText);
            if (legacyRoot != null && legacyRoot != canvasRoot && legacyRoot != reportRoot)
            {
                DestroyChild(legacyRoot, RowName);
            }

            var boxes = BuildBottomRowBoxes(hud, report);
            var animated = new List<AnimatedAmount>();
            RectTransform row = GetOrCreateReportRow(reportRoot);
            TextMeshProUGUI textTemplate = FindBoxTextTemplate(hud, titleText);
            if (textTemplate == null)
            {
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.ReportDiag] TryApply blocked: " +
                    $"textTemplateFound=False, title={DescribeText(titleText)}, row={DescribeTransform(row)}.");
                if (logMissing)
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts] Performance report text template not found; contract report overlay fallback remains available.");
                else
                    ContractsBootstrap.Log?.LogDebug("[MoonContracts] Performance report text template not ready; retrying.");
                return false;
            }

            ApplyPlayerNotes(hud, report, textTemplate);

            bool placedFromVanillaBottomRow = TryPlaceReportRowInVanillaBottomRow(row, hud, reportRoot, boxes);
            if (!placedFromVanillaBottomRow)
            {
                DestroyChild(reportRoot, RowName);
                RestoreVanillaBottomRowBlock();
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.ReportDiag] Performance report bottom-row placement not ready; " +
                    "leaving custom quota cells hidden until vanilla bottom-row rects can be measured.");
                return false;
            }

            ClearChildren(row);
            ApplyReportBackingOpacity(_vanillaPanelGraphic);
            ClampPlayerNotesToPanel(hud, reportRoot);
            // #556: sourced before any cell is built, because the cell frames ask for the
            // vanilla box sprite while they are being created. #747: and before the hide
            // pass, which swaps the outline sheet for its trimmed copy - carving the box
            // frame from the trimmed sheet landed on the wrong rows. #1200: and before the
            // Your XP panel, whose frame is the cells' frame.
            PerformanceReportSurfaceSprites.PrimeFromVanilla(FindVanillaBottomRowCleanupRoot(hud, reportRoot));
            // #1200: the panel first, so a panel that has to sit over the report's right edge
            // has pulled the note labels back before any Notes row is fitted to them.
            bool xpPanelShown = BeginXpPanel(hud, report, row, reportRoot, canvasRoot, textTemplate);
            if (hud.statsUIElements?.playerNotesText != null)
                for (int playerIndex = 0; playerIndex < hud.statsUIElements.playerNotesText.Length; playerIndex++)
                    ApplyXpNoteRow(hud, playerIndex, report.IsPreview ? GetPreviewXpNoteCandidates(report, playerIndex) : null,
                        xpPanelShown && playerIndex == _xpPanelHost.LocalSlot);
            // Dumped before the hide pass so the record shows vanilla's own state.
            DumpEndgameStatsHierarchyOnce(hud, reportRoot);
            HideVanillaBottomRowBlock(hud, reportRoot);
            EnsureVanillaBottomRowSuppressor(row);
            CreateVanillaBottomRowScrubMask(row, _vanillaPanelGraphic);

            ClearPrimaryValueRegistry();
            var visuals = new Dictionary<ContractPerformanceReportBox, ReportCellVisual>();
            for (int i = 0; i < boxes.Count; i++)
                visuals[boxes[i]] = CreateCompactCategoryFrame(row, boxes[i], i, boxes);
            for (int i = 0; i < boxes.Count; i++)
                CreateCompactCategoryText(row, textTemplate, boxes[i], i, boxes, visuals, animated);
            foreach (ReportCellVisual visual in visuals.Values)
                SetCellVisible(visual, false);

            _activeReportRow = row;
            _activeReportTitle = titleText;
            ApplyActiveReportTheme();
            row.gameObject.SetActive(boxes.Count > 0);
            if (report.IsPreview)
                StartPreviewSequence(hud, animated, boxes.Count);
            else if (boxes.Count > 0)
                hud.StartCoroutine(AnimateQuotaNumbers(hud, animated, boxes.Count, preview: false));
            else
                CancelPerformanceReportDelay(hud, "report applied with no boxes");

            // #716 (audit F5): the one Info line the report emits per round. Every other
            // [ReportDiag] line - this one included - is Debug: they run to several hundred
            // characters each and there are a dozen of them per round end, which is a
            // diagnostic dump rather than a log the player's console should carry by default.
            // #1203: a preview is not a round's report; it logs its own ReportDiag line and
            // must not add a marker the playtest-log round splitter counts.
            if (!report.IsPreview)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Performance report applied: cells={boxes.Count}, title='{titleText.text}'.");
            }

            ContractsBootstrap.Log?.LogDebug(
                "[MoonContracts.ReportDiag] TryApply success: " +
                $"titleBefore='{titleBefore}', titleAfter='{titleText.text}', titlePath='{DescribeTransform(titleText.transform)}', " +
                $"canvas='{DescribeTransform(canvasRoot)}', reportRoot='{DescribeTransform(reportRoot)}', row='{DescribeTransform(row)}', placedFromVanillaBottomRow={placedFromVanillaBottomRow}, layout=bottomRowCells, " +
                $"rowActive={row.gameObject.activeSelf}, rowAnchors=({row.anchorMin.x:0.###},{row.anchorMin.y:0.###})-({row.anchorMax.x:0.###},{row.anchorMax.y:0.###}), " +
                $"rowPos=({row.anchoredPosition.x:0.0},{row.anchoredPosition.y:0.0}), rowSize=({row.rect.width:0.0},{row.rect.height:0.0}), " +
                $"boxes={boxes.Count}, children={row.childCount}, textTemplate='{DescribeTransform(textTemplate.transform)}'.");

            // #1160: Y4NGZUI re-runs its own report restyle from this one success - the same
            // retry that got here. Its FillEndGameStats postfix fires on the frame the panel
            // appears, which is routinely before this hierarchy is measurable, so the vanilla
            // player grid it recolours could stay un-themed for the whole report. Null when
            // Y4NGZUI is absent; a handler must not call back into this method.
            Action<HUDManager> restyle = Y4NGZCore.Modules.Hud.UiPresentationSeams.PerformanceReportRestyle;
            if (restyle != null)
            {
                try { restyle(hud); }
                catch (Exception e)
                {
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts] Performance report restyle failed: {e.Message}");
                }
            }
            return true;
        }

        internal static void Restore(HUDManager hud)
        {
            var titleText = FindTitleText(hud);
            if (titleText != null && IsContractTitle(titleText.text))
            {
                titleText.text = "PERFORMANCE REPORT";
                titleText.color = NeutralColor;
            }

            var canvasRoot = FindCanvasRoot(hud, titleText);
            var reportContentRoot = FindReportContentRoot(hud, titleText);
            RestoreVanillaBottomRowBlock();
            RestoreXpPopupFadeGroup();
            RestoreNoteLabelStates();
            RestoreReportBackingOpacity();
            ClearForeignPlayerNotes();
            ContractsBootstrap.Log?.LogDebug(
                "[MoonContracts.ReportDiag] Restore requested: " +
                $"title={DescribeText(titleText)}, canvas='{DescribeTransform(canvasRoot)}', " +
                $"reportRoot='{DescribeTransform(reportContentRoot)}', " +
                $"canvasRowPresent={canvasRoot != null && canvasRoot.Find(RowName) != null}, " +
                $"reportRowPresent={reportContentRoot != null && reportContentRoot.Find(RowName) != null}.");
            if (canvasRoot != null)
            {
                DestroyChild(canvasRoot, RowName);
            }

            if (reportContentRoot != null)
            {
                DestroyChild(reportContentRoot, RowName);
            }

            var reportRoot = FindReportRoot(hud, titleText);
            if (reportRoot != null && reportRoot != canvasRoot && reportRoot != reportContentRoot)
            {
                DestroyChild(reportRoot, RowName);
            }
            // #1200: the panel is a child of the row and goes with it; this also drops the
            // panel's host and the note limit it imposed, which RestoreNoteLabelStates undid.
            ClearXpPanel();
            _activeReportRow = null;
            _vanillaPanelBounds = null;
            _activeReportTitle = null;
            _vanillaPanelGraphic = null;
            _vanillaPanelBand = null;
            _vanillaPanelArt = null;
            _reportGlyphFont = null;
            _hierarchyDumped = false;
            ClearPrimaryValueRegistry();
            ResetRoundEndLookupCaches();
        }


        private static void EnsureThemeSubscription()
        {
            if (_themeSubscribed)
                return;
            _themeSubscribed = true;
            UiTheme.ThemeChanged += OnThemeChanged;
        }

        private static void OnThemeChanged(HudColorPreset preset)
        {
            ApplyActiveReportTheme();
        }

        private static void ApplyActiveReportTheme()
        {
            if (_activeReportTitle != null)
                _activeReportTitle.color = UiTheme.White;
            if (_activeReportRow == null)
                return;

            Image[] images = _activeReportRow.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                Image image = images[i];
                if (image == null)
                    continue;

                string name = image.name ?? string.Empty;
                Color color;
                // #747: the panel clone mirrors the vanilla graphic's own color every frame,
                // and that graphic is what the theme retints; recoloring the clone here would
                // fight the mirror.
                if (name == VanillaPanelCloneName)
                    continue;
                else if (name == VanillaBottomScrubName)
                    color = VanillaBottomRowScrubColor;
                else if (name == "TopRule" || name == "BottomRule")
                    color = VanillaBottomRowRuleColor;
                // #1200: the Your XP panel's own fill and the rule above its Shift total.
                else if (name == XpPanelName)
                    color = XpPanelFillColor;
                else if (name == XpPanelRuleName)
                    color = VanillaBottomRowRuleColor;
                // #556: the row's own CRT wash. It stands in for vanilla art the scrub mask
                // covers, so it tracks the theme's chrome hue rather than the active accent -
                // and it must be tested before the generic "Scanline" sweep match below.
                else if (name == ScanlineOverlayName)
                    color = CellChromeColor;
                else if (name == DirectionArrowUpName)
                    color = DirectionArrowColor(true);
                else if (name == DirectionArrowDownName)
                    color = DirectionArrowColor(false);
                else if (name.IndexOf("Accent", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Scanline", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Tick", StringComparison.OrdinalIgnoreCase) >= 0)
                    color = ActiveColor;
                else if (name == "Top" || name == "Bottom" || name == "Left" || name == "Right"
                    || name == CellFrameSliceName)
                    color = NeutralColor;
                else if (name.StartsWith(CellFrameNamePrefix, StringComparison.Ordinal))
                    color = PanelColor;
                else
                    continue;

                color.a = image.color.a;
                image.color = color;
            }

            TMP_Text[] texts = _activeReportRow.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text == null)
                    continue;

                string name = text.name ?? string.Empty;
                Color color;
                if (name == HeadingTextName)
                    color = CellChromeColor;
                else if (name == QualifierTextName && !IsRegisteredPrimaryValue(text))
                    color = NeutralValueColor;
                else if (name.StartsWith(ConnectorNamePrefix, StringComparison.Ordinal))
                    color = CellChromeColor;
                else if (name == XpPanelLabelName)
                    color = XpPanelLabelColor;
                else if (name == XpPanelAmountName)
                    color = XpPanelAmountColor;
                else if (ActiveReportPrimaryValues.TryGetValue(text, out ContractPerformanceReportBox box))
                    color = GetValenceColor(box);
                else
                    color = NeutralColor;

                color.a = text.color.a;
                text.color = color;
            }
        }

        private static void RegisterPrimaryValue(TMP_Text text, ContractPerformanceReportBox box)
        {
            if (text == null || box == null)
                return;

            ActiveReportPrimaryValues[text] = box;
            Color color = GetValenceColor(box);
            color.a = text.color.a;
            text.color = color;
        }

        private static bool IsRegisteredPrimaryValue(TMP_Text text)
        {
            return text != null && ActiveReportPrimaryValues.ContainsKey(text);
        }

        private static void ClearPrimaryValueRegistry()
        {
            ActiveReportPrimaryValues.Clear();
        }

        private static Color GetValenceColor(ContractPerformanceReportBox box)
        {
            switch (ResolveValence(box))
            {
                case ReportValence.Lead:
                    return LeadValueColor;
                case ReportValence.Benefit:
                    return BenefitValueColor;
                case ReportValence.Penalty:
                    return PenaltyValueColor;
                default:
                    return NeutralValueColor;
            }
        }

        /// <summary>
        /// Reads the box's own numbers as an effect on the quota: raising it is a penalty,
        /// lowering it is a benefit, and a modifier that did not fire is neither. Only the
        /// grade, whose letter is not a number, needs its own reading.
        /// </summary>
        private static ReportValence ResolveValence(ContractPerformanceReportBox box)
        {
            if (box == null)
                return ReportValence.Neutral;
            if (box.Valence != ReportValence.Auto)
                return box.Valence;

            switch (box.Kind)
            {
                case ReportPerformanceBoxKind.Collected:
                    return ReportValence.Lead;
                case ReportPerformanceBoxKind.Grade:
                    return ResolveGradeValence(box.ValueOverride);
                case ReportPerformanceBoxKind.RevisedQuota:
                    if (!box.HasStartAmount || box.Amount == box.StartAmount)
                        return ReportValence.Neutral;
                    return box.Amount > box.StartAmount ? ReportValence.Penalty : ReportValence.Benefit;
                case ReportPerformanceBoxKind.Impact:
                    if (Mathf.Abs(box.ImpactPercent) <= 0.001f)
                        return ReportValence.Neutral;
                    return box.IsIncrease ? ReportValence.Penalty : ReportValence.Benefit;
                default:
                    if (box.Amount == 0)
                        return ReportValence.Neutral;
                    return box.HasSign && box.IsIncrease ? ReportValence.Penalty : ReportValence.Benefit;
            }
        }

        private static ReportValence ResolveGradeValence(string grade)
        {
            if (string.IsNullOrWhiteSpace(grade))
                return ReportValence.Neutral;

            switch (char.ToUpperInvariant(grade.Trim()[0]))
            {
                case 'S':
                case 'A':
                case 'B':
                    return ReportValence.Benefit;
                case 'D':
                case 'F':
                    return ReportValence.Penalty;
                default:
                    return ReportValence.Neutral;
            }
        }
        private static List<ContractPerformanceReportBox> BuildBottomRowBoxes(HUDManager hud, ContractPerformanceReportData report)
        {
            var boxes = new List<ContractPerformanceReportBox>();
            AddBox(boxes, CreateCollectedBox(hud));
            if (report != null)
            {
                AddBox(boxes, report.Directive);
                AddBox(boxes, report.HazardPay);
                AddBox(boxes, report.CrewLosses);
                AddBox(boxes, report.RevisedQuota);
            }

            AddBox(boxes, CreateGradeBox(hud, report));
            return boxes;
        }

        private static ContractPerformanceReportBox CreateCollectedBox(HUDManager hud)
        {
            string numeratorText = NormalizeCollectedText(hud?.statsUIElements?.quotaNumerator?.text);
            string denominatorText = NormalizeCollectedText(hud?.statsUIElements?.quotaDenominator?.text);
            bool parsedNumerator = int.TryParse(numeratorText, out int numerator);

            return new ContractPerformanceReportBox
            {
                Heading = "SCRAP",
                Detail = string.Empty,
                // #554: the collected figure is the ledger's lead value, so it fills the
                // primary band and the qualifier only names what the figure is.
                Qualifier = "RECOVERED",
                Amount = Mathf.Max(0, numerator),
                Animate = parsedNumerator && numerator > 0,
                PlayConfirmWhenStatic = true,
                Kind = ReportPerformanceBoxKind.Collected,
                SecondaryValue = denominatorText,
                ValueOverride = parsedNumerator ? null : GetCollectedValue(hud),
            };
        }

        private static string GetCollectedValue(HUDManager hud)
        {
            string numerator = NormalizeCollectedText(hud?.statsUIElements?.quotaNumerator?.text);
            string denominator = NormalizeCollectedText(hud?.statsUIElements?.quotaDenominator?.text);

            if (string.IsNullOrWhiteSpace(numerator) && string.IsNullOrWhiteSpace(denominator))
                return "-----";

            if (string.IsNullOrWhiteSpace(denominator))
                return numerator;

            if (string.IsNullOrWhiteSpace(numerator))
                return denominator;

            return $"{numerator}/{denominator}";
        }

        private static string NormalizeCollectedText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string result = value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace(":", string.Empty)
                .Trim();
            result = RemoveIgnoreCase(result, "COLLECTED").Trim();
            return string.IsNullOrWhiteSpace(result) ? string.Empty : result;
        }

        private static string RemoveIgnoreCase(string value, string token)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(token))
                return value ?? string.Empty;

            int index = value.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                value = value.Remove(index, token.Length);
                index = value.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            }

            return value;
        }

        private static ContractPerformanceReportBox CreateGradeBox(HUDManager hud, ContractPerformanceReportData report)
        {
            string grade = !string.IsNullOrWhiteSpace(report?.GradeOverride)
                ? report.GradeOverride
                : NormalizeGradeText(hud?.statsUIElements?.gradeLetter?.text);
            // #554: the grade cell carries no qualifier. "FINAL" restated what the row's
            // position already said and competed with the letter for the eye.
            return new ContractPerformanceReportBox
            {
                Heading = "GRADE",
                Detail = string.Empty,
                Animate = true,
                PlayConfirmWhenStatic = true,
                Kind = ReportPerformanceBoxKind.Grade,
                ValueOverride = string.IsNullOrWhiteSpace(grade) ? "-----" : grade,
            };
        }

        private static string NormalizeGradeText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string result = value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace(":", string.Empty)
                .Trim();
            result = RemoveIgnoreCase(result, "GRADE").Trim();
            return result;
        }

        /// <summary>
        /// Snapshots the notes column before we write to it, so a slot another mod has already
        /// filled — Coroner's cause of death — survives into the composed note. Taken once per
        /// report because the apply pass retries until the report row is placeable, and by the
        /// second attempt the slot holds our own text rather than theirs.
        /// </summary>
        internal static void CaptureForeignPlayerNotes(HUDManager hud)
        {
            var noteTexts = hud?.statsUIElements?.playerNotesText;
            if (noteTexts == null || noteTexts.Length == 0)
            {
                ClearForeignPlayerNotes();
                return;
            }

            _foreignPlayerNotesHud = hud;
            _foreignPlayerNotes = new string[noteTexts.Length];
            for (int i = 0; i < noteTexts.Length; i++)
                _foreignPlayerNotes[i] = PerformanceReportNoteSlots.StripHudHeader(noteTexts[i]?.text);
        }

        internal static void ClearForeignPlayerNotes()
        {
            _foreignPlayerNotes = null;
            _foreignPlayerNotesHud = null;
        }

        private static string GetForeignPlayerNote(HUDManager hud, int index)
        {
            if (_foreignPlayerNotes == null
                || _foreignPlayerNotesHud != hud
                || index < 0
                || index >= _foreignPlayerNotes.Length)
            {
                return null;
            }

            return _foreignPlayerNotes[index];
        }

        /// <summary>
        /// Upgrades' entry, through <see cref="PerformanceReportXpPresenter"/>, once it has
        /// recorded its ledger - which can land after the report was applied. #1200: for the
        /// local slot the Your XP panel is rebuilt from the ledger as it now stands before the
        /// slot's Notes row is written.
        /// </summary>
        internal static bool TryApplyXpNotes(HUDManager hud, int playerIndex)
        {
            bool xpPanelShown = _xpPanelHost != null
                && playerIndex == _xpPanelHost.LocalSlot
                && ApplyXpPanel(hud, playerIndex, null);
            return ApplyXpNoteRow(hud, playerIndex, null, xpPanelShown);
        }

        /// <param name="previewCandidates">
        /// #1203: the debug preview's stand-in for this round's Company XP note candidates.
        /// Null reads the round stats, which is every real report.
        /// </param>
        /// <param name="xpPanelShown">
        /// #1200: the slot's Your XP panel is on screen and lists every source, so its Notes row
        /// keeps only its top line. A slot without the panel keeps its top two - every remote
        /// slot with a snapshot (#458).
        /// </param>
        private static bool ApplyXpNoteRow(
            HUDManager hud,
            int playerIndex,
            IEnumerable<PerformanceReportNoteEntry> previewCandidates,
            bool xpPanelShown)
        {
            var slots = hud?.statsUIElements?.playerNotesText;
            if (!PerformanceReportXpLedger.HasPlayer(playerIndex) || slots == null
                || playerIndex < 0 || playerIndex >= slots.Length || slots[playerIndex] == null
                || !NoteLabelStates.ContainsKey(slots[playerIndex]) || !_vanillaPanelBounds.HasValue)
                return false;

            TextMeshProUGUI text = slots[playerIndex];
            IReadOnlyList<PerformanceReportNoteEntry> entries = PerformanceReportXpLedger.ComposeRow(
                playerIndex,
                previewCandidates ?? MoonContractRoundStats.BuildXpNoteCandidates(playerIndex),
                GetForeignPlayerNote(hud, playerIndex),
                xpPanelShown ? ReportLayout.XpNoteLinesWithPanel : ReportLayout.XpNoteLinesWithoutPanel);
            // ComposeRow puts the survival fact, when the slot has one, first.
            bool hasSurvivalFact = entries.Count > 0 && entries[0].IsSurvivalFact;

            text.richText = false;
            // A survival fact is never shortened. It wraps - every other line is fitted to one
            // line below, so it is the only line wrapping can reach - the autosize may shrink
            // the row within the Notes' own size range, and past the smallest size the row
            // overflows rather than cutting it. A row without one keeps its ellipsis.
            text.enableWordWrapping = hasSurvivalFact;
            text.overflowMode = hasSurvivalFact ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            text.enableAutoSizing = true;
            // fontSize and rect height share the label's local units: no canvas-scale conversion.
            Vector2 noteFontRange = ReportLayout.NoteFontRangeForSlotHeight(text.rectTransform.rect.height);
            text.fontSizeMin = noteFontRange.x;
            text.fontSizeMax = noteFontRange.y;
            text.fontSize = noteFontRange.y;
            float width = Mathf.Max(1f, text.rectTransform.rect.width - text.margin.x - text.margin.z - 2f);
            int count = entries.Count;
            if (hasSurvivalFact)
            {
                // The fact takes its whole wrapped height first, at the largest size, and the
                // XP lines give way to it.
                float wrapWidth = Mathf.Max(1f, text.rectTransform.rect.width - text.margin.x - text.margin.z);
                float available = text.rectTransform.rect.height - text.margin.y - text.margin.w;
                float factHeight = text.GetPreferredValues("Notes:\n" + entries[0].Render(), wrapWidth, float.PositiveInfinity).y;
                float lineHeight = text.GetPreferredValues("A\nA", wrapWidth, float.PositiveInfinity).y
                    - text.GetPreferredValues("A", wrapWidth, float.PositiveInfinity).y;
                count = 1 + PerformanceReportXpLedger.PersonalLinesUnderSurvivalFact(
                    available, factHeight, lineHeight, entries.Count - 1);
            }

            var lines = new List<string>(count);
            for (int i = 0; i < count; i++)
                lines.Add(entries[i].IsSurvivalFact ? entries[i].Render() : FitXpNoteLine(text, entries[i], width, noteFontRange.y));
            text.text = "Notes:\n" + string.Join("\n", lines);
            return true;
        }

        private static string FitXpNoteLine(TextMeshProUGUI text, PerformanceReportNoteEntry entry, float width, float fontSizeMax)
        {
            // Shorten only the prose; the paid amount must remain readable at the right.
            // Measure at the largest allowed size, then let TMP fit the combined row height.
            text.fontSize = fontSizeMax;
            return PerformanceReportXpLedger.FitLine(entry, width,
                line => text.GetPreferredValues(line, float.PositiveInfinity, float.PositiveInfinity).x);
        }

        private static void ApplyPlayerNotes(HUDManager hud, ContractPerformanceReportData report, TextMeshProUGUI template)
        {
            IReadOnlyList<string> notes = report?.PlayerNotes;
            var noteTexts = hud?.statsUIElements?.playerNotesText;
            if (notes == null || notes.Count == 0 || noteTexts == null || noteTexts.Length == 0)
                return;

            for (int i = 0; i < noteTexts.Length; i++)
            {
                TextMeshProUGUI text = noteTexts[i];
                if (text == null || i >= notes.Count || notes[i] == null)
                    continue;

                // Everything below writes to a label the vanilla animator also uses for the
                // non-contract report, so the first touch snapshots what it is about to
                // overwrite and Restore() puts it back.
                CaptureNoteLabelState(text);
                text.text = PerformanceReportNoteSlots.FormatHudText(notes[i], GetForeignPlayerNote(hud, i));
                text.enableWordWrapping = true;
                text.overflowMode = TextOverflowModes.Ellipsis;
                text.alignment = TextAlignmentOptions.TopLeft;
                // #555: the notes were capped at 7.2pt and read as fine print. The cap goes
                // up, the lines are tightened so three of them still stack, and the label
                // reclaims the padding its own rect was wasting. Everything touched here is
                // a property of the TMP we write to - the vanilla animator's transforms are
                // left alone.
                text.enableAutoSizing = true;
                text.lineSpacing = ReportLayout.NoteLineSpacing;
                // #556: TMP margin is (left, top, right, bottom). Only the bottom is spent,
                // so a third line that wraps to a fourth cannot sit on the vanilla box's
                // boundary rule. Anything past that ellipsizes rather than overflowing.
                text.margin = new Vector4(0f, 0f, 0f, ReportLayout.NoteBottomMargin);
                text.color = NeutralColor;
                ReclaimNoteRectPadding(text);
                // #874 S6: the range reads the reclaimed rect, the same rect ApplyXpNoteRow reads,
                // so a slot with an XP row and one without size their notes alike.
                Vector2 noteFontRange = ReportLayout.NoteFontRangeForSlotHeight(text.rectTransform.rect.height);
                text.fontSizeMin = noteFontRange.x;
                text.fontSizeMax = noteFontRange.y;
                text.fontSize = noteFontRange.y;
            }
        }

        /// <summary>
        /// Snapshots a note label's authored presentation the first time this report touches
        /// it. Idempotent on purpose: the apply pass retries for up to 90 frames, and a
        /// snapshot taken on the second pass would record our own mutations as the original.
        /// </summary>
        private static void CaptureNoteLabelState(TextMeshProUGUI note)
        {
            if (note == null || NoteLabelStates.ContainsKey(note))
                return;

            RectTransform rect = note.rectTransform;
            NoteLabelStates[note] = new NoteLabelState
            {
                OffsetMin = rect != null ? rect.offsetMin : Vector2.zero,
                OffsetMax = rect != null ? rect.offsetMax : Vector2.zero,
                HasRect = rect != null,
                RichText = note.richText,
                WordWrapping = note.enableWordWrapping,
                OverflowMode = note.overflowMode,
                Alignment = note.alignment,
                AutoSizing = note.enableAutoSizing,
                FontSizeMin = note.fontSizeMin,
                FontSizeMax = note.fontSizeMax,
                FontSize = note.fontSize,
                LineSpacing = note.lineSpacing,
                Margin = note.margin,
                Color = note.color,
            };
        }

        /// <summary>
        /// Hands every note label back the presentation it had before the contract report
        /// borrowed it. Without this a following vanilla (non-contract) endgame report renders
        /// with our offsets, our negative line spacing and our zeroed margin still applied.
        /// </summary>
        private static void RestoreNoteLabelStates()
        {
            foreach (KeyValuePair<TextMeshProUGUI, NoteLabelState> pair in NoteLabelStates)
            {
                TextMeshProUGUI note = pair.Key;
                if (note == null)
                    continue;

                NoteLabelState original = pair.Value;
                note.richText = original.RichText;
                note.enableWordWrapping = original.WordWrapping;
                note.overflowMode = original.OverflowMode;
                note.alignment = original.Alignment;
                note.enableAutoSizing = original.AutoSizing;
                note.fontSizeMin = original.FontSizeMin;
                note.fontSizeMax = original.FontSizeMax;
                note.fontSize = original.FontSize;
                note.lineSpacing = original.LineSpacing;
                note.margin = original.Margin;
                note.color = original.Color;

                RectTransform rect = note.rectTransform;
                if (original.HasRect && rect != null)
                {
                    rect.offsetMin = original.OffsetMin;
                    rect.offsetMax = original.OffsetMax;
                }
            }

            NoteLabelStates.Clear();
        }

        /// <summary>
        /// Gives the note label back the inset it holds inside its own parent. The reclaim is
        /// always measured from the label's snapshotted original offsets rather than from
        /// wherever it currently sits, so the retried apply pass and every later round land on
        /// the same rect instead of walking it a little further out each time - and a label
        /// another HUD mod already inset past the reclaim keeps its own inset. Only the leaf
        /// label moves; the boxes the endgame animator drives do not.
        ///
        /// #556: the bottom edge is deliberately never reclaimed. Pushing it down was what
        /// put the last wrapped line on top of the vanilla box's boundary; the room the notes
        /// need is taken from the top and the sides instead.
        /// </summary>
        private static void ReclaimNoteRectPadding(TextMeshProUGUI note)
        {
            if (note == null || !NoteLabelStates.TryGetValue(note, out NoteLabelState original))
                return;

            RectTransform noteRect = note.rectTransform;
            if (noteRect == null || !original.HasRect)
                return;
            if (!(noteRect.parent is RectTransform parent) || parent == noteRect)
                return;

            float side = Mathf.Min(ReportLayout.NoteRectSideReclaim, ReportLayout.NoteRectMaxReclaim);
            float top = Mathf.Min(ReportLayout.NoteRectTopReclaim, ReportLayout.NoteRectMaxReclaim);
            noteRect.offsetMin = new Vector2(original.OffsetMin.x - side, original.OffsetMin.y);
            noteRect.offsetMax = new Vector2(original.OffsetMax.x + side, original.OffsetMax.y + top);
        }

        /// <summary>
        /// #747: pulls every note label this report wrote back inside the drawn panel. The
        /// labels' authored rects overhang the art like the BGBoxes rect does, and TMP wraps
        /// at the rect, so a long line ran past the panel's right edge. Only the right edge
        /// moves, only for labels whose snapshot exists (the ones this report rewrote), only
        /// once the panel has been measured, and always from the label's current rect - which
        /// <see cref="ReclaimNoteRectPadding"/> has just rebuilt from the snapshot, so the
        /// retried apply pass lands on the same edge every time. Restore puts the snapshot back.
        /// </summary>
        private static void ClampPlayerNotesToPanel(HUDManager hud, RectTransform coordinateRoot)
        {
            if (coordinateRoot == null || !_vanillaPanelBounds.HasValue || !_vanillaPanelBounds.Value.HasAny)
                return;

            var noteTexts = hud?.statsUIElements?.playerNotesText;
            if (noteTexts == null)
                return;

            float limit = _vanillaPanelBounds.Value.MaxX - ReportLayout.NotePanelRightMargin;
            // #1200: a Your XP panel with no room beside the report sits over its right edge,
            // and the Notes column then ends left of it.
            if (_xpPanelNoteLimitX.HasValue)
                limit = Mathf.Min(limit, _xpPanelNoteLimitX.Value);
            var clamped = new List<string>();
            for (int i = 0; i < noteTexts.Length; i++)
            {
                TextMeshProUGUI text = noteTexts[i];
                if (text == null || !NoteLabelStates.ContainsKey(text))
                    continue;

                RectTransform rect = text.rectTransform;
                if (rect == null
                    || !(rect.parent is RectTransform parent)
                    || !TryGetLocalRectBounds(rect, coordinateRoot, out LocalRectBounds bounds)
                    || !bounds.HasAny)
                {
                    continue;
                }

                float overshoot = bounds.MaxX - limit;
                // A label that starts past the limit cannot be pulled back without inverting
                // its rect; it stays where the game put it.
                if (overshoot <= 0.5f || bounds.MinX >= limit)
                    continue;

                // The bounds are in the report root's units; the offset is in the label's
                // parent's. The two differ only by the scale between them.
                float rootScale = Mathf.Abs(coordinateRoot.lossyScale.x);
                float parentScale = Mathf.Abs(parent.lossyScale.x);
                float localOvershoot = parentScale > 0.0001f && rootScale > 0.0001f
                    ? overshoot * rootScale / parentScale
                    : overshoot;
                rect.offsetMax = new Vector2(rect.offsetMax.x - localOvershoot, rect.offsetMax.y);
                clamped.Add($"slot {i} by {overshoot:0.0}u");
            }

            if (clamped.Count > 0)
            {
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.ReportDiag] Note labels clamped inside the drawn panel: " +
                    $"limitX={limit:0.0}, {string.Join(", ", clamped.ToArray())}.");
            }
        }

        private static List<ContractPerformanceReportBox> BuildBoxes(ContractPerformanceReportData report)
        {
            var boxes = new List<ContractPerformanceReportBox>();
            AddBox(boxes, report.Directive);
            AddBox(boxes, report.HazardPay);
            AddBox(boxes, report.CrewLosses);
            AddBox(boxes, report.RevisedQuota);
            return boxes;
        }

        private static void AddBox(List<ContractPerformanceReportBox> boxes, ContractPerformanceReportBox box)
        {
            if (boxes == null || box == null)
                return;

            if (string.IsNullOrWhiteSpace(box.Heading))
                return;

            boxes.Add(box);
        }

        private static RectTransform GetOrCreateReportRow(RectTransform reportRoot)
        {
            var existing = reportRoot.Find(RowName);
            if (existing != null && existing is RectTransform existingRect)
                return existingRect;

            var obj = new GameObject(RowName);
            obj.transform.SetParent(reportRoot, false);
            var rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = RowAnchorMin;
            rect.anchorMax = RowAnchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            return rect;
        }

        private static bool TryPlaceReportRowInVanillaBottomRow(
            RectTransform row,
            HUDManager hud,
            RectTransform coordinateRoot,
            IReadOnlyList<ContractPerformanceReportBox> boxes)
        {
            ApplyFallbackRowAnchors(row);
            if (row == null || hud?.statsUIElements == null || coordinateRoot == null)
                return false;

            if (!TryGetVanillaBottomRowBounds(hud, coordinateRoot, out var collectedBounds, out var gradeBounds, out var lowerBounds))
                return false;

            if (!IsReasonableBounds(lowerBounds, coordinateRoot))
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.ReportDiag] Rejected quota report lower bounds as unreasonable: " +
                    $"lower={FormatBounds(lowerBounds)}, coordinate='{DescribeTransform(coordinateRoot)}'.");
                return false;
            }

            if (!lowerBounds.HasAny)
                return false;

            // #874 S6: the clamp is what the camera shows, not the Canvas rect: a row fitted to
            // the Canvas rect put cell 0 on its edge and the camera clipped it. The viewport keeps
            // RowScreenMargin clear on both sides.
            if (!TryGetViewportX(coordinateRoot, out Canvas canvas, out float viewportMinX, out float viewportMaxX))
                return false;

            bool hasFullPanelBounds = FindFullReportPanelBounds(
                hud, coordinateRoot, lowerBounds, out var panelBounds, out string panelSource);
            int itemCount = boxes != null ? boxes.Count : 0;

            // #553: the taller ledger row grows DOWNWARD only. Its top edge is pinned just
            // above the measured vanilla bottom band, so the extra height is spent in the
            // panel's lower margin and can never reach the player boxes the vanilla animator
            // owns. Note that the bottom-row hiding heuristics
            // (IsLikelyVanillaBottomRowGraphic) measure against lowerBounds, not against this
            // row, so they are unaffected by how tall we end up drawing.
            // #747: with the panel art measured, the row IS the lower band plate of that art:
            // the plates' shared width, and the band's full-width rows for its height. That
            // is the drawn panel, and the row follows it through the panel Image's rect when
            // More Company widens it. Without the art (a swapped, non-Simple, or
            // unreadable panel) the row falls back to the measured vanilla text band, growing
            // downward into the panel's lower margin as before.
            float bandTop;
            float bandBottom;
            float minX;
            float maxX;
            if (_vanillaPanelBand.HasValue)
            {
                VanillaBandPlacement band = _vanillaPanelBand.Value;
                float insetX = Mathf.Max(ReportLayout.RowPlateInsetXMin, band.Width * ReportLayout.RowPlateInsetXFraction);
                float insetY = Mathf.Max(
                    ReportLayout.RowPlateInsetYMin,
                    (band.TopY - band.FullBottomY) * ReportLayout.RowPlateInsetYFraction);
                bandTop = band.TopY - insetY;
                bandBottom = band.FullBottomY + insetY;
                minX = band.MinX + insetX;
                maxX = band.MaxX - insetX;
            }
            else
            {
                bandTop = lowerBounds.MaxY + ReportLayout.RowTopPadding;
                bandBottom = hasFullPanelBounds
                    ? Mathf.Min(lowerBounds.MinY, panelBounds.MinY + ReportLayout.RowPanelBottomPadding)
                    : lowerBounds.MinY;
                minX = hasFullPanelBounds ? panelBounds.MinX : lowerBounds.MinX;
                maxX = hasFullPanelBounds ? panelBounds.MaxX : lowerBounds.MaxX;
            }

            // The measured panel/bottom row is the preferred width, but it is not allowed to
            // outrun the viewport. This intersection is shared by the drawn-panel and
            // measured-vanilla fallback paths; only the preferred vertical band differs.
            if (!PerformanceReportCellLayout.TryFitRowToViewport(
                    minX,
                    maxX,
                    viewportMinX + ReportLayout.RowScreenMargin,
                    viewportMaxX - ReportLayout.RowScreenMargin,
                    ReportLayout.RowWidthMin,
                    ReportLayout.RowWidthMax,
                    out ReportRowPlacement horizontalPlacement))
            {
                return false;
            }

            float width = horizontalPlacement.Width;
            float heightForWidth = itemCount > 0
                ? PerformanceReportCellLayout.RowHeightForWidth(
                    ReportLayout.CellLayout, width, itemCount, GetGradeCellIndex(boxes))
                : ReportLayout.RowHeightMax;
            float height = Mathf.Clamp(
                Mathf.Min(bandTop - bandBottom, heightForWidth),
                ReportLayout.RowHeightMin,
                ReportLayout.RowHeightMax);
            // On the measured plate the inset band is the ceiling, whatever the minimum says:
            // a row taller than the inset plate is a row back on the plate's edges.
            if (_vanillaPanelBand.HasValue)
                height = Mathf.Min(height, Mathf.Max(1f, bandTop - bandBottom));
            // On the band plate the row sits centered in the plate's full-width rows, the way
            // vanilla's COLLECTED box does; the band fill (the panel clone) covers the whole
            // plate regardless of how tall the cells came out. Otherwise it grows downward
            // from the top of the text band so the extra height is spent in the panel's
            // lower margin and never reaches the player boxes the animator owns.
            float centerY = _vanillaPanelBand.HasValue
                ? (bandTop + bandBottom) * 0.5f
                : bandTop - height * 0.5f;
            float centerX = horizontalPlacement.CenterX;
            float anchoredX = PerformanceReportCellLayout.AnchoredPositionForLocalCenter(
                centerX, coordinateRoot.rect.width, coordinateRoot.pivot.x);
            float anchoredY = PerformanceReportCellLayout.AnchoredPositionForLocalCenter(
                centerY, coordinateRoot.rect.height, coordinateRoot.pivot.y);

            row.anchorMin = new Vector2(0.5f, 0.5f);
            row.anchorMax = new Vector2(0.5f, 0.5f);
            row.pivot = new Vector2(0.5f, 0.5f);
            row.anchoredPosition = new Vector2(anchoredX, anchoredY);
            row.sizeDelta = new Vector2(width, height);

            ContractsBootstrap.Log?.LogDebug(
                "[MoonContracts.ReportDiag] Placed quota report in vanilla bottom row: " +
                $"collected={FormatBounds(collectedBounds)}, grade={FormatBounds(gradeBounds)}, lower={FormatBounds(lowerBounds)}, " +
                $"fullPanel={(hasFullPanelBounds ? FormatBounds(panelBounds) : "<none>")} from {panelSource}, " +
                $"viewport=({viewportMinX:0.0}..{viewportMaxX:0.0}), " +
                $"heightForWidth={heightForWidth:0.0}, " +
                $"center=({centerX:0.0},{centerY:0.0}), size=({width:0.0},{height:0.0}), items={itemCount}, parentSize=({coordinateRoot.rect.width:0.0},{coordinateRoot.rect.height:0.0}), " +
                $"canvas='{DescribeTransform(canvas)}'.");
            return true;
        }

        /// <summary>
        /// #874 S6: the root canvas's screen rect, mapped into <paramref name="coordinateRoot"/>
        /// local units through the camera that renders it.
        /// </summary>
        private static bool TryGetViewportX(RectTransform coordinateRoot, out Canvas canvas, out float minX, out float maxX)
        {
            minX = float.PositiveInfinity;
            maxX = float.NegativeInfinity;
            canvas = FindCanvas(coordinateRoot);
            if (canvas == null)
                return false;

            canvas = canvas.rootCanvas;
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Rect screenRect = CanvasScreenRect(canvas);
            for (int i = 0; i < 4; i++)
            {
                var corner = new Vector2(i < 2 ? screenRect.xMin : screenRect.xMax, (i & 1) == 0 ? screenRect.yMin : screenRect.yMax);
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(coordinateRoot, corner, camera, out Vector2 local))
                    return false;
                minX = Mathf.Min(minX, local.x);
                maxX = Mathf.Max(maxX, local.x);
            }

            return maxX > minX;
        }

        private static Rect CanvasScreenRect(Canvas canvas)
        {
            Rect screenRect = canvas.pixelRect;
            return screenRect.width > 0f && screenRect.height > 0f
                ? screenRect
                : new Rect(0f, 0f, Screen.width, Screen.height);
        }


        /// <summary>
        /// #747: the bounds the row sizes itself to. Preferred source is the vanilla panel
        /// graphic itself - the thing the player actually sees - found by
        /// <see cref="FindVanillaPanelGraphic"/>. The BGBoxes/Lines container rects are the
        /// fallback; on the live build they measured about 1.4x the drawn panel in both axes,
        /// which is exactly the overhang this replaces.
        /// </summary>
        private static bool FindFullReportPanelBounds(
            HUDManager hud,
            RectTransform coordinateRoot,
            LocalRectBounds lowerBounds,
            out LocalRectBounds panelBounds,
            out string source)
        {
            panelBounds = new LocalRectBounds();
            source = "<none>";
            if (coordinateRoot == null)
                return false;

            RectTransform cleanupRoot = FindVanillaBottomRowCleanupRoot(hud, coordinateRoot);
            Graphic panel = FindVanillaPanelGraphic(cleanupRoot, coordinateRoot, lowerBounds);
            _vanillaPanelGraphic = null;
            _vanillaPanelBand = null;
            _vanillaPanelBounds = null;
            _vanillaPanelArt = null;
            if (panel != null)
            {
                // The drawn art, not the rect: endgameStatsBG carries transparent padding
                // inside its rect (about 11% of the width and 14% of the height on the
                // vanilla asset), so the rect overhangs what the player sees on every side.
                if (TryGetDrawnPanelBounds(panel, coordinateRoot, out LocalRectBounds drawnBounds, out VanillaBandPlacement? band, out string artSource)
                    && IsReasonableBounds(drawnBounds, coordinateRoot))
                {
                    _vanillaPanelGraphic = panel;
                    _vanillaPanelBand = band;
                    _vanillaPanelBounds = drawnBounds;
                    panelBounds = drawnBounds;
                    source = $"drawn art of '{GetTransformPath(panel.transform)}' [{artSource}]";
                    return true;
                }

                if (TryGetLocalRectBounds(panel.rectTransform, coordinateRoot, out LocalRectBounds graphicBounds)
                    && IsReasonableBounds(graphicBounds, coordinateRoot))
                {
                    _vanillaPanelGraphic = panel;
                    _vanillaPanelBounds = graphicBounds;
                    panelBounds = graphicBounds;
                    source = $"panel graphic rect '{GetTransformPath(panel.transform)}' ({panel.GetType().Name})";
                    return true;
                }
            }

            TryEncapsulateRect(FindChildRectByName(cleanupRoot, "BGBoxes"), coordinateRoot, ref panelBounds);
            TryEncapsulateRect(FindChildRectByName(cleanupRoot, "Lines"), coordinateRoot, ref panelBounds);
            source = "BGBoxes/Lines";
            return panelBounds.HasAny && IsReasonableBounds(panelBounds, coordinateRoot);
        }

        /// <summary>
        /// #747: the bounds of what the panel Image actually draws, mapped through its rect.
        /// A Simple Image stretches its sprite's whole rect over its RectTransform (fitted
        /// when preserveAspect is set), so a drawn edge measured in sprite pixels lands at the
        /// same fraction of the rect - and follows the rect when More Company widens it. The
        /// band placement is the lower plate of the art (behind COLLECTED/GRADE) when the art
        /// has one, which is where the row goes.
        /// </summary>
        private static bool TryGetDrawnPanelBounds(
            Graphic panel,
            RectTransform coordinateRoot,
            out LocalRectBounds drawnBounds,
            out VanillaBandPlacement? band,
            out string artSource)
        {
            drawnBounds = new LocalRectBounds();
            band = null;
            artSource = "<none>";
            if (!(panel is Image image)
                || image.sprite == null
                || image.type != Image.Type.Simple
                || coordinateRoot == null
                || !PerformanceReportSurfaceSprites.TryGetPanelArt(image.sprite, out PerformanceReportSurfaceSprites.VanillaPanelArt art))
            {
                return false;
            }

            Rect spriteRect = image.sprite.rect;
            if (spriteRect.width < 1f || spriteRect.height < 1f)
                return false;

            RectTransform rect = image.rectTransform;
            Rect local = rect.rect;
            if (image.preserveAspect)
                local = FitSpriteRect(local, spriteRect.size, rect.pivot);

            _vanillaPanelArt = art;
            _vanillaPanelSpriteHeight = spriteRect.height;
            float scaleX = local.width / spriteRect.width;
            float scaleY = local.height / spriteRect.height;
            float left = local.xMin + art.PlateLeft * scaleX;
            float right = local.xMin + art.PlateRight * scaleX;
            drawnBounds = TransformLocalRect(rect, coordinateRoot,
                left, local.yMin + art.ArtBottom * scaleY,
                right, local.yMin + art.ArtTop * scaleY);
            if (art.HasBand)
            {
                LocalRectBounds bandBounds = TransformLocalRect(rect, coordinateRoot,
                    left, local.yMin + art.BandArtBottom * scaleY,
                    right, local.yMin + art.BandTop * scaleY);
                LocalRectBounds fullBounds = TransformLocalRect(rect, coordinateRoot,
                    left, local.yMin + art.BandFullBottom * scaleY,
                    right, local.yMin + art.BandTop * scaleY);
                band = new VanillaBandPlacement
                {
                    MinX = bandBounds.MinX,
                    MaxX = bandBounds.MaxX,
                    TopY = bandBounds.MaxY,
                    FullBottomY = fullBounds.MinY,
                    ArtBottomY = bandBounds.MinY,
                };
            }

            artSource = art.ToString();
            return drawnBounds.HasAny;
        }

        // Image.PreserveSpriteAspectRatio, reproduced: the sprite is fitted inside the rect
        // and the slack is distributed by the pivot.
        private static Rect FitSpriteRect(Rect rect, Vector2 spriteSize, Vector2 pivot)
        {
            if (spriteSize.x <= 0f || spriteSize.y <= 0f || rect.width <= 0f || rect.height <= 0f)
                return rect;

            float spriteRatio = spriteSize.x / spriteSize.y;
            float rectRatio = rect.width / rect.height;
            if (spriteRatio > rectRatio)
            {
                float oldHeight = rect.height;
                rect.height = rect.width * (1f / spriteRatio);
                rect.y += (oldHeight - rect.height) * pivot.y;
            }
            else
            {
                float oldWidth = rect.width;
                rect.width = rect.height * spriteRatio;
                rect.x += (oldWidth - rect.width) * pivot.x;
            }

            return rect;
        }

        private static LocalRectBounds TransformLocalRect(
            RectTransform rect,
            RectTransform coordinateRoot,
            float minX,
            float minY,
            float maxX,
            float maxY)
        {
            var bounds = new LocalRectBounds();
            bounds.Encapsulate(coordinateRoot.InverseTransformPoint(rect.TransformPoint(new Vector3(minX, minY, 0f))));
            bounds.Encapsulate(coordinateRoot.InverseTransformPoint(rect.TransformPoint(new Vector3(maxX, minY, 0f))));
            bounds.Encapsulate(coordinateRoot.InverseTransformPoint(rect.TransformPoint(new Vector3(minX, maxY, 0f))));
            bounds.Encapsulate(coordinateRoot.InverseTransformPoint(rect.TransformPoint(new Vector3(maxX, maxY, 0f))));
            return bounds;
        }

        // #747: what qualifies as "the panel". A drawn (active, enabled, non-transparent)
        // non-text graphic under EndgameStats that spans the vanilla bottom boxes
        // horizontally and is at least this many bottom-box heights tall - which rules out
        // the title bar and the box outlines - and, among those, the smallest one, which
        // rules out any full-screen backdrop that also happens to contain the boxes.
        private const float PanelMinimumHeightInLowerBoxHeights = 2f;

        private static Graphic FindVanillaPanelGraphic(
            RectTransform cleanupRoot,
            RectTransform coordinateRoot,
            LocalRectBounds lowerBounds)
        {
            if (cleanupRoot == null || coordinateRoot == null || !lowerBounds.HasAny)
                return null;

            Graphic best = null;
            float bestArea = float.MaxValue;
            Graphic[] graphics = cleanupRoot.GetComponentsInChildren<Graphic>(false);

            // The vanilla panel art is the sprite named endgameStatsBG (see
            // docs/UI_PRESENTATION_GUIDELINES.md). When an Image draws it, that is the panel
            // and no geometry needs guessing. The geometric pass below is for a build or a
            // HUD mod that renamed it.
            for (int i = 0; i < graphics.Length; i++)
            {
                if (graphics[i] is Image image
                    && image.enabled
                    && image.sprite != null
                    && image.sprite.name.IndexOf("endgameStatsBG", StringComparison.OrdinalIgnoreCase) >= 0
                    && !IsCustomReportTransform(image.transform))
                {
                    return image;
                }
            }

            for (int i = 0; i < graphics.Length; i++)
            {
                Graphic graphic = graphics[i];
                // Alpha is deliberately not a criterion: the first apply attempt lands one
                // frame after FillEndGameStats, while the endgame animator may still have
                // the panel at the start of its fade-in.
                if (graphic == null
                    || !graphic.enabled
                    || graphic is TMP_Text
                    || IsTextMeshProSubMeshGraphic(graphic)
                    || IsCustomReportTransform(graphic.transform))
                {
                    continue;
                }

                if (!(graphic.transform is RectTransform rect)
                    || !TryGetLocalRectBounds(rect, coordinateRoot, out LocalRectBounds bounds)
                    || !bounds.HasAny)
                {
                    continue;
                }

                if (bounds.MinX > lowerBounds.MinX + 1f || bounds.MaxX < lowerBounds.MaxX - 1f)
                    continue;
                if (bounds.Height < lowerBounds.Height * PanelMinimumHeightInLowerBoxHeights)
                    continue;

                float area = bounds.Width * bounds.Height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = graphic;
                }
            }

            return best;
        }

        /// <summary>
        /// #747: one Debug dump of everything under EndgameStats - path, activity, bounds in
        /// the report root's units, and for a graphic its type, color, sprite (with 9-slice
        /// border) and texture. This is the record the vanilla art lookups and the panel
        /// heuristic above are corrected against; nothing else in the repo describes the
        /// hierarchy. Once per report, so a 90-attempt retry loop cannot flood the log.
        /// </summary>
        private static void DumpEndgameStatsHierarchyOnce(HUDManager hud, RectTransform coordinateRoot)
        {
            if (_hierarchyDumped || coordinateRoot == null)
                return;

            _hierarchyDumped = true;
            RectTransform root = FindVanillaBottomRowCleanupRoot(hud, coordinateRoot);
            if (root == null)
                return;

            const int MaxEntries = 400;
            var builder = new StringBuilder();
            builder.Append("[MoonContracts.ReportDiag] EndgameStats hierarchy (#747), bounds in '")
                .Append(GetTransformPath(coordinateRoot))
                .Append("' units:");
            RectTransform[] rects = root.GetComponentsInChildren<RectTransform>(true);
            int written = 0;
            for (int i = 0; i < rects.Length && written < MaxEntries; i++)
            {
                RectTransform rect = rects[i];
                if (rect == null || IsCustomReportTransform(rect))
                    continue;

                written++;
                builder.Append('\n').Append(GetTransformPath(rect));
                builder.Append(" active=").Append(rect.gameObject.activeInHierarchy ? "Y" : "N");
                if (TryGetLocalRectBounds(rect, coordinateRoot, out LocalRectBounds bounds))
                    builder.Append(" bounds=").Append(FormatBounds(bounds));
                builder.Append(" size=(").Append(rect.rect.width.ToString("0.#")).Append(',')
                    .Append(rect.rect.height.ToString("0.#")).Append(')');
                builder.Append(" scale=").Append(rect.localScale.x.ToString("0.##"));

                Graphic graphic = rect.GetComponent<Graphic>();
                if (graphic == null)
                    continue;

                builder.Append(" graphic=").Append(graphic.GetType().Name);
                builder.Append(graphic.enabled ? "" : "(disabled)");
                builder.Append(" color=#").Append(ColorUtility.ToHtmlStringRGBA(graphic.color));
                if (graphic is Image image)
                {
                    builder.Append(" type=").Append(image.type);
                    if (image.sprite != null)
                    {
                        builder.Append(" sprite='").Append(image.sprite.name).Append('\'');
                        builder.Append(" spriteRect=(").Append(image.sprite.rect.width.ToString("0"))
                            .Append('x').Append(image.sprite.rect.height.ToString("0")).Append(')');
                        builder.Append(" border=").Append(image.sprite.border);
                        if (image.sprite.texture != null)
                            builder.Append(" tex='").Append(image.sprite.texture.name).Append('\'');
                    }
                }
                else if (graphic is RawImage raw)
                {
                    builder.Append(" tex='").Append(raw.texture != null ? raw.texture.name : "<null>").Append('\'');
                    builder.Append(" uv=").Append(raw.uvRect);
                }

                if (graphic.material != null)
                    builder.Append(" mat='").Append(graphic.material.name).Append('\'');
            }

            if (written >= MaxEntries)
                builder.Append("\n... (truncated)");
            // #1200: the walk skips everything under the row, the Your XP panel included, so
            // its bounds are stated here in the same units.
            AppendXpPanelDump(builder);
            ContractsBootstrap.Log?.LogDebug(builder.ToString());
        }

        private static RectTransform FindChildRectByName(RectTransform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
                return null;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child is RectTransform rect
                    && string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return rect;
                }
            }

            return null;
        }

        private static bool TryGetVanillaBottomRowBounds(
            HUDManager hud,
            RectTransform canvasRoot,
            out LocalRectBounds collectedBounds,
            out LocalRectBounds gradeBounds,
            out LocalRectBounds lowerBounds)
        {
            collectedBounds = new LocalRectBounds();
            gradeBounds = new LocalRectBounds();
            lowerBounds = new LocalRectBounds();

            if (hud?.statsUIElements == null || canvasRoot == null)
                return false;

            TryEncapsulateRect(FindUsefulParentRect(hud.statsUIElements.quotaNumerator, canvasRoot), canvasRoot, ref collectedBounds);
            TryEncapsulateRect(FindUsefulParentRect(hud.statsUIElements.quotaDenominator, canvasRoot), canvasRoot, ref collectedBounds);
            TryEncapsulateRect(FindUsefulParentRect(hud.statsUIElements.gradeLetter, canvasRoot), canvasRoot, ref gradeBounds);

            lowerBounds.Encapsulate(collectedBounds);
            lowerBounds.Encapsulate(gradeBounds);
            return lowerBounds.HasAny;
        }

        private static void ApplyFallbackRowAnchors(RectTransform row)
        {
            if (row == null)
                return;

            row.anchorMin = RowAnchorMin;
            row.anchorMax = RowAnchorMax;
            row.pivot = new Vector2(0.5f, 0.5f);
            row.offsetMin = Vector2.zero;
            row.offsetMax = Vector2.zero;
        }

        private static void HideVanillaBottomRowBlock(HUDManager hud, RectTransform reportRoot)
        {
            RestoreVanillaBottomRowBlock();
            if (hud?.statsUIElements == null || reportRoot == null)
                return;

            var hiddenDescriptions = new List<string>();
            var textCandidateDescriptions = new List<string>();
            TryGetVanillaBottomRowBounds(hud, reportRoot, out _, out _, out var lowerBounds);
            RectTransform cleanupRoot = FindVanillaBottomRowCleanupRoot(hud, reportRoot);

            HideVanillaBottomRowLabelsByText(cleanupRoot, reportRoot, lowerBounds, hiddenDescriptions, textCandidateDescriptions);
            HideVanillaBottomRowGraphicsByBounds(cleanupRoot, reportRoot, lowerBounds, hiddenDescriptions);
            HideVanillaBottomRowElement(hud.statsUIElements.quotaNumerator, reportRoot, hud.statsUIElements.gradeLetter, hiddenDescriptions);
            HideVanillaBottomRowElement(hud.statsUIElements.quotaDenominator, reportRoot, hud.statsUIElements.gradeLetter, hiddenDescriptions);
            HideVanillaBottomRowElement(hud.statsUIElements.gradeLetter, reportRoot, hud.statsUIElements.quotaNumerator, hiddenDescriptions);
            TrimVanillaOutlineSheet(cleanupRoot, reportRoot, hiddenDescriptions);

            ContractsBootstrap.Log?.LogDebug(
                "[MoonContracts.ReportDiag] Hidden vanilla bottom-row objects: " +
                $"count={hiddenDescriptions.Count}, lower={FormatBounds(lowerBounds)}, cleanupRoot='{DescribeTransform(cleanupRoot)}', " +
                $"textCandidates={(textCandidateDescriptions.Count > 0 ? string.Join("; ", textCandidateDescriptions.Take(8).ToArray()) : "<none>")}, " +
                $"objects={(hiddenDescriptions.Count > 0 ? string.Join("; ", hiddenDescriptions.Take(12).ToArray()) : "<none>")}" +
                (hiddenDescriptions.Count > 12 ? "; ..." : string.Empty));
        }

        private static RectTransform FindVanillaBottomRowCleanupRoot(HUDManager hud, RectTransform reportRoot)
        {
            if (hud?.endgameStatsAnimator != null
                && hud.endgameStatsAnimator.transform is RectTransform animatorRoot
                && reportRoot != null
                && reportRoot.IsChildOf(animatorRoot))
            {
                return animatorRoot;
            }

            for (Transform current = reportRoot != null ? reportRoot.parent : null; current != null; current = current.parent)
            {
                if (current is RectTransform rect
                    && current.name.IndexOf("EndgameStats", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return rect;
                }
            }

            return reportRoot;
        }

        private static void HideVanillaBottomRowLabelsByText(
            RectTransform cleanupRoot,
            RectTransform coordinateRoot,
            LocalRectBounds lowerBounds,
            List<string> hiddenDescriptions,
            List<string> textCandidateDescriptions)
        {
            if (cleanupRoot == null || coordinateRoot == null)
                return;

            TextMeshProUGUI[] texts = cleanupRoot.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                TextMeshProUGUI text = texts[i];
                if (text == null || IsCustomReportTransform(text.transform) || !IsVanillaBottomRowLabel(text.text))
                    continue;

                bool overlaps = true;
                LocalRectBounds textBounds = default;
                if (lowerBounds.HasAny
                    && TryGetLocalRectBounds(text.rectTransform, coordinateRoot, out textBounds))
                {
                    overlaps = textBounds.Overlaps(lowerBounds, 18f);
                }

                textCandidateDescriptions?.Add(
                    $"'{SanitizeLogText(text.text)}' overlap={overlaps} bounds={FormatBounds(textBounds)} path={DescribeTransform(text.transform)}");
                if (!overlaps)
                    continue;

                TryHideVanillaBottomRowObject(text.gameObject, hiddenDescriptions, $"text '{SanitizeLogText(text.text)}'");
            }
        }

        private static bool IsVanillaBottomRowLabel(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string value = text.Trim();
            return value.IndexOf("COLLECTED", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("GRADE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void HideVanillaBottomRowGraphicsByBounds(
            RectTransform cleanupRoot,
            RectTransform coordinateRoot,
            LocalRectBounds lowerBounds,
            List<string> hiddenDescriptions)
        {
            if (cleanupRoot == null || coordinateRoot == null || !lowerBounds.HasAny)
                return;

            Graphic[] graphics = cleanupRoot.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                Graphic graphic = graphics[i];
                if (graphic == null
                    || graphic is TMP_Text
                    || IsTextMeshProSubMeshGraphic(graphic)
                    || IsCustomReportTransform(graphic.transform))
                {
                    continue;
                }

                if (!(graphic.transform is RectTransform rect)
                    || !TryGetLocalRectBounds(rect, coordinateRoot, out var graphicBounds)
                    || !graphicBounds.Overlaps(lowerBounds, 4f)
                    || !IsLikelyVanillaBottomRowGraphic(graphicBounds, lowerBounds))
                {
                    continue;
                }

                TryHideVanillaBottomRowObject(graphic.gameObject, hiddenDescriptions, $"graphic {graphic.GetType().Name}");
            }
        }

        private static bool IsTextMeshProSubMeshGraphic(Graphic graphic)
        {
            return graphic != null
                && graphic.GetType().Name.IndexOf("TMP_SubMeshUI", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsLikelyVanillaBottomRowGraphic(LocalRectBounds graphicBounds, LocalRectBounds lowerBounds)
        {
            if (!graphicBounds.HasAny || !lowerBounds.HasAny)
                return false;

            float width = graphicBounds.Width;
            float height = graphicBounds.Height;
            if (width <= 0.5f || height <= 0.5f)
                return false;

            float maxDecorativeWidth = Mathf.Max(lowerBounds.Width * 1.75f, lowerBounds.Height * 4f);
            if (width > maxDecorativeWidth)
                return false;

            if (height > lowerBounds.Height * 1.65f)
                return false;

            return true;
        }

        private static void HideVanillaBottomRowElement(
            TextMeshProUGUI text,
            RectTransform reportRoot,
            TextMeshProUGUI protectedSibling,
            List<string> hiddenDescriptions)
        {
            if (text == null || reportRoot == null)
                return;

            for (Transform current = text.transform; current != null && current != reportRoot; current = current.parent)
            {
                if (!(current is RectTransform rect))
                    continue;

                if (!IsSafeBottomRowHideTarget(rect, reportRoot, protectedSibling))
                    continue;

                TryHideVanillaBottomRowObject(rect.gameObject, hiddenDescriptions, $"field '{SanitizeLogText(text.text)}'");
            }
        }

        private static bool IsSafeBottomRowHideTarget(
            RectTransform target,
            RectTransform reportRoot,
            TextMeshProUGUI protectedSibling)
        {
            if (target == null || reportRoot == null)
                return false;

            if (target == reportRoot
                || target.GetComponent<Canvas>() != null
                || string.Equals(target.name, RowName, StringComparison.Ordinal))
                return false;

            if (protectedSibling != null && protectedSibling.transform.IsChildOf(target))
                return false;

            return target.IsChildOf(reportRoot);
        }

        private static bool TryHideVanillaBottomRowObject(
            GameObject obj,
            List<string> hiddenDescriptions,
            string reason)
        {
            if (obj == null || HiddenVanillaBottomRowObjects.Contains(obj))
                return false;

            HiddenVanillaBottomRowObjects.Add(obj);
            hiddenDescriptions?.Add($"{reason}: {DescribeTransform(obj.transform)}");
            obj.SetActive(false);
            return true;
        }

        private static void RestoreVanillaBottomRowBlock()
        {
            for (int i = HiddenVanillaBottomRowObjects.Count - 1; i >= 0; i--)
            {
                var obj = HiddenVanillaBottomRowObjects[i];
                if (obj != null)
                    obj.SetActive(true);
            }

            HiddenVanillaBottomRowObjects.Clear();
            ReassertedVanillaBottomRowObjects.Clear();
            RestoreVanillaOutlineSheet();
        }

        /// <summary>
        /// #747 (Lawson, 2026-09-04): the hide pass ran once, and vanilla's endgame animator
        /// activates the grade letter - and can activate the quota texts and other band
        /// graphics - late in its own timeline, which undid the one-shot SetActive(false) and
        /// drew a second grade over the GRADE cell. One component on the row re-asserts every
        /// hidden object, and the trimmed outline sheet, in LateUpdate - after the animator has
        /// written for the frame - for as long as the row exists. It dies with the row, and
        /// Restore has already re-activated and forgotten the objects before that.
        /// </summary>
        private static void EnsureVanillaBottomRowSuppressor(RectTransform row)
        {
            if (row == null || row.GetComponent<VanillaBottomRowSuppressor>() != null)
                return;

            row.gameObject.AddComponent<VanillaBottomRowSuppressor>();
        }

        private static void ReassertVanillaBottomRowHidden()
        {
            for (int i = 0; i < HiddenVanillaBottomRowObjects.Count; i++)
            {
                GameObject obj = HiddenVanillaBottomRowObjects[i];
                if (obj == null || !obj.activeSelf)
                    continue;

                obj.SetActive(false);
                if (ReassertedVanillaBottomRowObjects.Add(obj))
                {
                    ContractsBootstrap.Log?.LogDebug(
                        "[MoonContracts.ReportDiag] Re-hid a vanilla bottom-row object the animator " +
                        $"re-activated: {DescribeTransform(obj.transform)}.");
                }
            }

            if (_trimmedOutlineSheet != null
                && _trimmedOutlineSheetSprite != null
                && _trimmedOutlineSheet.sprite != _trimmedOutlineSheetSprite)
            {
                _trimmedOutlineSheet.sprite = _trimmedOutlineSheetSprite;
            }
        }

        private sealed class VanillaBottomRowSuppressor : MonoBehaviour
        {
            private void LateUpdate()
            {
                ReassertVanillaBottomRowHidden();
            }
        }

        // The sheet and the panel must share their vertical edges to share pixel rows; a HUD
        // mod that moves one up or down without the other makes the cut land in the wrong
        // place. Widths are free (#928): More Company stretches the panel and not the sheet.
        private const float OutlineSheetRectToleranceUnits = 2f;

        /// <summary>
        /// #747: swaps the vanilla outline sheet for a copy cut off at the measured band's
        /// top edge and shrinks its rect from the bottom by the same amount, so the player
        /// boxes stay put and the baked COLLECTED box and GRADE label are gone. Needs the
        /// measured panel art; a sheet whose rect does not match the panel's is left alone
        /// (the band clone then covers what it can).
        /// </summary>
        private static void TrimVanillaOutlineSheet(
            RectTransform cleanupRoot,
            RectTransform coordinateRoot,
            List<string> hiddenDescriptions)
        {
            RestoreVanillaOutlineSheet();
            if (cleanupRoot == null
                || coordinateRoot == null
                || !_vanillaPanelArt.HasValue
                || !_vanillaPanelArt.Value.HasBand
                || _vanillaPanelGraphic == null
                || _vanillaPanelSpriteHeight <= 0f)
            {
                return;
            }

            Image sheet = PerformanceReportSurfaceSprites.FindVanillaOutlineSheetImage(cleanupRoot);
            string skipReason = DescribeOutlineSheetTrimBlocker(sheet, coordinateRoot, out RectTransform sheetRect);
            if (skipReason != null)
            {
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.ReportDiag] Vanilla outline sheet left untrimmed: " +
                    $"sheet={(sheet != null ? DescribeTransform(sheet.transform) : "<none>")}, " +
                    $"type={(sheet != null ? sheet.type.ToString() : "-")}, reason={skipReason}.");
                return;
            }

            float cutRows = _vanillaPanelArt.Value.BandTop;
            Sprite trimmed = PerformanceReportSurfaceSprites.GetOutlineSheetAboveBand(sheet.sprite, cutRows);
            if (trimmed == null)
                return;

            float cutUnits = sheetRect.rect.height * (cutRows / _vanillaPanelSpriteHeight);
            _trimmedOutlineSheet = sheet;
            _trimmedOutlineSheetOriginalSprite = sheet.sprite;
            _trimmedOutlineSheetOriginalSizeDelta = sheetRect.sizeDelta;
            _trimmedOutlineSheetOriginalAnchoredPosition = sheetRect.anchoredPosition;

            // Shrinking from the bottom: the height drops by the cut and the pivot moves up
            // by the pivot's share of it, which keeps the top edge exactly where it was for
            // any anchor setup.
            sheet.sprite = trimmed;
            _trimmedOutlineSheetSprite = trimmed;
            sheetRect.sizeDelta = new Vector2(sheetRect.sizeDelta.x, sheetRect.sizeDelta.y - cutUnits);
            sheetRect.anchoredPosition = new Vector2(
                sheetRect.anchoredPosition.x,
                sheetRect.anchoredPosition.y + (1f - sheetRect.pivot.y) * cutUnits);
            hiddenDescriptions?.Add(
                $"outline sheet trimmed {cutRows:0}px/{cutUnits:0.0}u: {DescribeTransform(sheet.transform)}");
        }

        /// <summary>
        /// #928: why the outline sheet cannot be trimmed, or null when it can. The cut is
        /// vertical - it removes rows from the bottom of the sheet's own sprite and shrinks the
        /// sheet's own rect by the same fraction - so it depends on the sheet sharing the panel
        /// sprite's height and the panel's vertical edges, never on the sheet's width. More
        /// Company scales `BGBoxes` 2.5x on X and leaves `Lines` at scale 1, which made the
        /// former four-edge equality check bail and left the baked GRADE label showing through
        /// the row; the sheet only has to sit horizontally inside the panel.
        /// </summary>
        private static string DescribeOutlineSheetTrimBlocker(Image sheet, RectTransform coordinateRoot, out RectTransform sheetRect)
        {
            sheetRect = null;
            if (sheet == null)
                return "no vanilla outline sheet under the cleanup root";
            if (sheet.type != Image.Type.Simple)
                return $"sheet image type {sheet.type} is not Simple";

            sheetRect = sheet.transform as RectTransform;
            if (sheetRect == null)
                return "sheet has no RectTransform";
            if (!Mathf.Approximately(sheet.sprite.rect.height, _vanillaPanelSpriteHeight))
                return $"sheet sprite height {sheet.sprite.rect.height:0} differs from panel sprite height {_vanillaPanelSpriteHeight:0}";
            if (!TryGetLocalRectBounds(sheetRect, coordinateRoot, out LocalRectBounds sheetBounds))
                return "sheet bounds could not be measured";
            if (!TryGetLocalRectBounds(_vanillaPanelGraphic.rectTransform, coordinateRoot, out LocalRectBounds panelBounds))
                return "panel bounds could not be measured";

            float tolerance = OutlineSheetRectToleranceUnits;
            string boundsText = $"sheet={FormatBounds(sheetBounds)}, panel={FormatBounds(panelBounds)}";
            if (Mathf.Abs(sheetBounds.MinY - panelBounds.MinY) > tolerance
                || Mathf.Abs(sheetBounds.MaxY - panelBounds.MaxY) > tolerance)
            {
                return "sheet vertical edges do not match the panel's (" + boundsText + ")";
            }

            if (sheetBounds.MinX < panelBounds.MinX - tolerance
                || sheetBounds.MaxX > panelBounds.MaxX + tolerance)
            {
                return "sheet extends horizontally past the panel (" + boundsText + ")";
            }

            return null;
        }

        private static void RestoreVanillaOutlineSheet()
        {
            if (_trimmedOutlineSheet != null)
            {
                _trimmedOutlineSheet.sprite = _trimmedOutlineSheetOriginalSprite;
                if (_trimmedOutlineSheet.transform is RectTransform rect)
                {
                    rect.sizeDelta = _trimmedOutlineSheetOriginalSizeDelta;
                    rect.anchoredPosition = _trimmedOutlineSheetOriginalAnchoredPosition;
                }
            }

            _trimmedOutlineSheet = null;
            _trimmedOutlineSheetOriginalSprite = null;
            _trimmedOutlineSheetSprite = null;
        }

        private static ReportCellVisual CreateCompactCategoryFrame(
            RectTransform row,
            ContractPerformanceReportBox box,
            int index,
            IReadOnlyList<ContractPerformanceReportBox> boxes)
        {
            GetCompactCategoryAnchors(row, index, boxes, out Vector2 anchorMin, out Vector2 anchorMax);

            var obj = new GameObject(CellFrameNamePrefix + SanitizeObjectNameFragment(box.Heading));
            obj.transform.SetParent(row, false);

            var rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var image = obj.AddComponent<Image>();
            image.color = PanelColor;
            image.raycastTarget = false;

            List<Image> borders = CreateCellFrameBorders(rect);
            Image sweep = CreateActiveScanlineSweep(rect);
            GameObject directionArrow = box.IsFinalQuota
                ? CreateFinalQuotaDirectionArrow(rect, box)
                : null;
            return new ReportCellVisual(rect, image, borders, sweep, directionArrow);
        }

        /// <summary>
        /// #556: the vanilla player boxes are outlined with a grungy, broken rule and ours
        /// were clean 2px lines, which is what made the strip read as a different program's
        /// UI. Preferred source is vanilla's own 9-sliced box sprite; where that cannot be
        /// found the four rules carry a seeded grain tile instead, which wears the same way
        /// without depending on an asset we cannot verify from outside the game.
        ///
        /// Either shape returns a list of Images, because every later pass - the reveal fade,
        /// the active highlight, the confirm flash - drives the whole border through that one
        /// list and must not care which shape it got.
        /// </summary>
        private static List<Image> CreateCellFrameBorders(RectTransform rect)
        {
            Sprite vanillaFrame = PerformanceReportSurfaceSprites.VanillaBoxFrameSprite;
            if (vanillaFrame != null)
            {
                var frame = CreateBorderLine(
                    rect,
                    CellFrameSliceName,
                    Vector2.zero,
                    Vector2.one,
                    Vector2.zero,
                    Vector2.zero,
                    NeutralColor,
                    vanillaFrame,
                    Image.Type.Sliced);
                // The cell already has its own fill behind this; the sprite contributes the
                // outline only, so its centre is dropped rather than doubling the panel.
                frame.fillCenter = false;
                // #747: the 9-slice border is in sheet pixels, and Image draws it at canvas
                // scale unless told otherwise - about 1.7x heavier than the sheet as vanilla
                // stretches it. Draw it at the sheet's own pixels-per-unit instead.
                frame.pixelsPerUnitMultiplier = PerformanceReportSurfaceSprites.VanillaBoxFramePixelsPerUnitMultiplier(rect);
                return new List<Image> { frame };
            }

            // #747: the fallback rules are as thick as vanilla's, measured from the sheet
            // (about 6 px at about 1.72 px per unit), not a fixed 2 units.
            float rule = PerformanceReportSurfaceSprites.VanillaRuleThicknessUnits();
            Sprite horizontalGrain = PerformanceReportSurfaceSprites.GetDistressSprite(true);
            Sprite verticalGrain = PerformanceReportSurfaceSprites.GetDistressSprite(false);
            return new List<Image>
            {
                CreateBorderLine(rect, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -rule), new Vector2(0f, 0f), NeutralColor, horizontalGrain),
                CreateBorderLine(rect, "Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(0f, rule), NeutralColor, horizontalGrain),
                CreateBorderLine(rect, "Left", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(rule, 0f), NeutralColor, verticalGrain),
                CreateBorderLine(rect, "Right", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-rule, 0f), new Vector2(0f, 0f), NeutralColor, verticalGrain),
            };
        }

        private static GameObject CreateFinalQuotaDirectionArrow(RectTransform parent, ContractPerformanceReportBox box)
        {
            if (parent == null || box == null || !box.HasStartAmount || box.Amount == box.StartAmount)
                return null;

            bool pointsUp = box.Amount > box.StartAmount;
            var obj = new GameObject(pointsUp ? DirectionArrowUpName : DirectionArrowDownName);
            obj.transform.SetParent(parent, false);
            var rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = ReportLayout.RevisedQuotaArrowAnchorMin;
            rect.anchorMax = ReportLayout.RevisedQuotaArrowAnchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var image = obj.AddComponent<Image>();
            image.raycastTarget = false;
            image.preserveAspect = true;
            // #716 (audit E12): the arrow states the sign of the quota move, so it takes the
            // sign convention's hue like every other value in the row rather than a literal
            // white that no preset and no theme change could reach.
            image.color = DirectionArrowColor(pointsUp);
            AssignFinalQuotaDirectionArrowSprite(obj, box);

            obj.SetActive(false);
            return obj;
        }

        private static void AssignFinalQuotaDirectionArrowSprite(GameObject directionArrow, ContractPerformanceReportBox box)
        {
            var image = directionArrow != null ? directionArrow.GetComponent<Image>() : null;
            if (image == null)
                return;

            image.sprite = GetFinalQuotaDirectionArrowSprite(box);
            image.enabled = image.sprite != null;
        }

        private static Sprite GetFinalQuotaDirectionArrowSprite(ContractPerformanceReportBox box)
        {
            if (box == null || !box.HasStartAmount || box.Amount == box.StartAmount)
                return null;

            return box.Amount > box.StartAmount
                ? _finalQuotaIncreaseArrowSprite
                : _finalQuotaDecreaseArrowSprite;
        }

        private static void SetFinalQuotaDirectionArrowActive(
            ReportCellVisual visual,
            ContractPerformanceReportBox box,
            bool active)
        {
            if (visual?.DirectionArrow == null)
                return;

            bool visible = active
                && box != null
                && box.IsFinalQuota
                && box.HasStartAmount
                && box.Amount != box.StartAmount;

            if (visible)
            {
                AssignFinalQuotaDirectionArrowSprite(visual.DirectionArrow, box);
                var image = visual.DirectionArrow.GetComponent<Image>();
                visible = image != null && image.sprite != null;
            }

            visual.DirectionArrow.SetActive(visible);
        }

        private static void CreateVanillaBottomRowScrubMask(RectTransform row, Graphic vanillaPanel)
        {
            if (row == null)
                return;

            // #747: with the outline sheet trimmed there is nothing under the row but the
            // vanilla band plate itself, which is the one fill that cannot fail to match it.
            // The plate is translucent, so even a 1:1 clone drawn over it doubles its alpha
            // and reads as a darker strip; the row draws no fill at all.
            if (OutlineSheetTrimmed)
                return;

            // Otherwise the band plate is cloned 1:1 over the sheet to cover what it can:
            // same pixels, same material, same live color.
            if (TryCreateVanillaPanelClone(row, vanillaPanel))
                return;

            var obj = new GameObject(VanillaBottomScrubName);
            obj.transform.SetParent(row, false);

            var rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(-4f, -2f);
            rect.offsetMax = new Vector2(4f, 2f);

            var image = obj.AddComponent<Image>();
            image.color = VanillaBottomRowScrubColor;
            image.raycastTarget = false;

            CreateScanlineOverlay(rect);
            CreateBottomRowScrubRule(rect, "TopRule", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -0.75f), new Vector2(0f, 0.75f));
            CreateBottomRowScrubRule(rect, "BottomRule", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, -0.75f), new Vector2(0f, 0.75f));
        }

        /// <summary>
        /// #747: clones the vanilla panel graphic as the row's fill. The clone copies what
        /// the source draws with (sprite or texture, image type, material) and then tracks the
        /// source's color every frame through <see cref="VanillaPanelCloneFollower"/>, so the
        /// endgame animator's fade-in and any theme retint of the panel reach the strip too.
        /// If Y4NGZUI has hung a theme mesh effect on the source, the same effect is mirrored
        /// onto the clone, or a colored preset would retint the panel and leave the strip red.
        /// The native graphic's color/material remain animator-owned; the same small mesh
        /// opacity adjustment used on its backing is applied independently to the clone.
        /// </summary>
        private static bool TryCreateVanillaPanelClone(RectTransform row, Graphic source)
        {
            // Only the band plate is cloned, and only 1:1. The previous pass stretched the
            // whole padded endgameStatsBG sprite into the row, which squeezed its baked
            // scanlines and its transparent padding into a washed band; without the measured
            // band there is nothing honest to clone, so the opaque scrub takes over.
            if (row == null
                || !(source is Image sourceImage)
                || sourceImage.sprite == null
                || !_vanillaPanelBand.HasValue)
            {
                return false;
            }

            Sprite bandSprite = PerformanceReportSurfaceSprites.GetPanelBandSprite(sourceImage.sprite);
            if (bandSprite == null)
                return false;

            var obj = new GameObject(VanillaPanelCloneName);
            obj.transform.SetParent(row, false);

            // Sized to the whole band plate - slant included - and positioned from the row's
            // own placement, so it covers the vanilla COLLECTED box and labels on the Lines
            // sheet even when the cells made the row shorter than the plate. The row is
            // centre-anchored and centre-pivoted, so its anchoredPosition is its centre in
            // the same units the band was measured in.
            VanillaBandPlacement band = _vanillaPanelBand.Value;
            float rowTopY = row.anchoredPosition.y + row.sizeDelta.y * 0.5f;
            var rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(band.CenterX - row.anchoredPosition.x, band.TopY - rowTopY);
            rect.sizeDelta = new Vector2(band.Width, band.TopY - band.ArtBottomY);

            var image = obj.AddComponent<Image>();
            image.sprite = bandSprite;
            image.type = Image.Type.Simple;
            image.preserveAspect = false;
            image.material = source.material;
            image.color = source.color;
            image.raycastTarget = false;
            PerformanceReportThemeMeshEffect.TryMirror(source, image);
            if (_reportBackingOpacity != null && _reportBackingOpacity.enabled)
                obj.AddComponent<ReportBackingOpacity>();
            obj.AddComponent<VanillaPanelCloneFollower>().Bind(source, image);
            return true;
        }

        /// <summary>
        /// Keeps a panel clone's color equal to its source's. One component per report, one
        /// color copy per frame, and it removes its own object if the source goes away.
        /// </summary>
        private sealed class VanillaPanelCloneFollower : MonoBehaviour
        {
            private Graphic _source;
            private Graphic _target;

            internal void Bind(Graphic source, Graphic target)
            {
                _source = source;
                _target = target;
            }

            private void LateUpdate()
            {
                if (_target == null)
                    return;

                if (_source == null)
                {
                    Destroy(gameObject);
                    return;
                }

                if (_target.color != _source.color)
                    _target.color = _source.color;
            }
        }

        private static void ApplyReportBackingOpacity(Graphic backing)
        {
            // Only known background art. A HUD replacement selected by the placement
            // heuristic might combine text/outlines in one graphic, so leave it untouched.
            if (!(backing is Image image) || image.sprite == null
                || image.sprite.name.IndexOf("endgameStatsBG", StringComparison.OrdinalIgnoreCase) < 0)
            {
                RestoreReportBackingOpacity();
                return;
            }
            var effect = backing.GetComponent<ReportBackingOpacity>();
            if (_reportBackingOpacity != effect) RestoreReportBackingOpacity();
            _reportBackingOpacity = effect ?? backing.gameObject.AddComponent<ReportBackingOpacity>();
            _reportBackingOpacity.enabled = true;
            backing.SetVerticesDirty();
        }

        private static void RestoreReportBackingOpacity()
        {
            if (_reportBackingOpacity != null) _reportBackingOpacity.enabled = false;
            _reportBackingOpacity = null;
        }

        // Rebuilt from the graphic's original vertices on each Canvas pass, so neither
        // retries nor the animator's fade multiply opacity repeatedly. Disabling the effect
        // restores the native mesh; RGB, sprite scanlines, frames and text are untouched.
        private sealed class ReportBackingOpacity : BaseMeshEffect
        {
            public override void ModifyMesh(VertexHelper vertices)
            {
                if (!IsActive() || vertices == null) return;
                UIVertex vertex = default;
                for (int i = 0; i < vertices.currentVertCount; i++)
                {
                    vertices.PopulateUIVertex(ref vertex, i);
                    Color color = vertex.color;
                    color.a *= ReportLayout.BackingOpacityMultiplier;
                    vertex.color = color;
                    vertices.SetUIVertex(vertex, i);
                }
            }
        }

        private static void CreateBottomRowScrubRule(
            RectTransform parent,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            CreateBorderLine(parent, name, anchorMin, anchorMax, offsetMin, offsetMax, VanillaBottomRowRuleColor);
        }

        /// <summary>
        /// #556: the CRT wash the scrub mask covered up, put back over the mask and under the
        /// cells. It is a child of the mask so it is guaranteed to sit above the opaque fill
        /// and below every cell without depending on sibling ordering elsewhere in the row.
        /// The tile is cached process-wide, so this costs one Image and no per-frame work.
        /// </summary>
        private static void CreateScanlineOverlay(RectTransform parent)
        {
            Sprite tile = PerformanceReportSurfaceSprites.GetScanlineSprite();
            if (tile == null)
                return;

            Image overlay = CreateBorderLine(
                parent,
                ScanlineOverlayName,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero,
                CellChromeColor,
                tile);
            overlay.color = UiPalette.WithAlpha(CellChromeColor, ReportLayout.ScanlineOverlayAlpha);
        }

        private static Image CreateActiveScanlineSweep(RectTransform parent)
        {
            var sweep = CreateBorderLine(
                parent,
                "ActiveScanlineSweep",
                new Vector2(0.08f, 0f),
                new Vector2(0.92f, 0f),
                new Vector2(0f, -0.35f),
                new Vector2(0f, 0.35f),
                NeutralColor,
                PerformanceReportSurfaceSprites.GetDistressSprite(horizontal: true));
            sweep.color = new Color(NeutralColor.r, NeutralColor.g, NeutralColor.b, 0.30f);
            sweep.gameObject.SetActive(false);
            return sweep;
        }

        private static Image CreateBorderLine(
            RectTransform parent,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax,
            Color color,
            Sprite sprite = null,
            Image.Type spriteType = Image.Type.Tiled)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            var rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            var image = obj.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = spriteType;
            }

            return image;
        }

        private static void CreateCompactCategoryText(
            RectTransform row,
            TextMeshProUGUI template,
            ContractPerformanceReportBox box,
            int index,
            IReadOnlyList<ContractPerformanceReportBox> boxes,
            Dictionary<ContractPerformanceReportBox, ReportCellVisual> visuals,
            List<AnimatedAmount> animated)
        {
            GetCompactCategoryAnchors(row, index, boxes, out Vector2 anchorMin, out Vector2 anchorMax);

            var obj = new GameObject(CellTextNamePrefix + SanitizeObjectNameFragment(box.Heading));
            obj.transform.SetParent(row, false);
            var rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            ReportCellVisual visual = null;
            visuals?.TryGetValue(box, out visual);
            if (visual != null)
                visual.TextRoot = obj;

            // #554/#556: one anatomy for every cell - small heading, the primary value, and a
            // qualifier at the primary's own weight. Nothing about the cell's role changes
            // which band a string lands in, only what the strings are and whether the cell
            // has a bottom band at all.
            bool isGrade = box.Kind == ReportPerformanceBoxKind.Grade;
            bool isRevisedQuota = box.Kind == ReportPerformanceBoxKind.RevisedQuota;
            string qualifier = ResolveQualifierText(box);
            bool hasQualifier = !string.IsNullOrWhiteSpace(qualifier);
            float primaryMaxX = isRevisedQuota
                ? ReportLayout.RevisedQuotaPrimaryMaxX
                : 1f - ReportLayout.BandInsetX;
            // #1204: the cell takes its size from the row, so the type takes its size from the
            // cell. Without this a wide five-cell row carried the same 10pt heading a narrow
            // six-cell one does and the bands read as fine print lost in the frame. The bands
            // themselves are fractions of the cell, so they already follow it.
            float cellHeight = (row != null ? row.rect.height : 0f) * (anchorMax.y - anchorMin.y);
            float fontScale = ReportLayout.FontScaleForCellHeight(cellHeight);
            float headingFontSize = ReportLayout.HeadingFontSize * fontScale;
            float qualifierFontSize = ReportLayout.QualifierFontSize * fontScale;
            float primaryFontSize = fontScale * (isGrade
                ? ReportLayout.PrimaryFontSizeGrade
                : isRevisedQuota
                    ? ReportLayout.PrimaryFontSizeRevisedQuota
                    : ReportLayout.PrimaryFontSize);
            // Reserve enough vertical room above the common baseline for the actual font.
            // This keeps the larger quota/grade from touching the heading on a short row.
            if (template.font != null && rect.rect.height > 0f)
            {
                var face = template.font.faceInfo;
                float ascentPerPoint = face.pointSize > 0f ? face.ascentLine * face.scale / face.pointSize : 0f;
                if (ascentPerPoint > 0f)
                    primaryFontSize = Mathf.Min(primaryFontSize,
                        rect.rect.height * (ReportLayout.PrimaryMaxY - ReportLayout.PrimaryMinY) / ascentPerPoint);
            }

            var headingText = CreateText(
                rect,
                template,
                HeadingTextName,
                box.Heading,
                ReportLayout.BandInsetX,
                ReportLayout.HeadingMinY,
                1f - ReportLayout.BandInsetX,
                ReportLayout.HeadingMaxY,
                headingFontSize,
                TextAlignmentOptions.Center);
            headingText.color = CellChromeColor;

            var primaryText = CreateText(
                rect,
                template,
                PrimaryTextName,
                FormatInitialAmount(box),
                ReportLayout.BandInsetX,
                2f * ReportLayout.PrimaryMinY - ReportLayout.PrimaryMaxY,
                primaryMaxX,
                ReportLayout.PrimaryMaxY,
                primaryFontSize,
                TextAlignmentOptions.Baseline);
            RegisterPrimaryValue(primaryText, box);

            // #556: no qualifier means no bottom band or dash placeholder. Every value
            // keeps the same baseline regardless of whether it has a footer.
            TextMeshProUGUI qualifierText = null;
            if (hasQualifier)
            {
                qualifierText = CreateText(
                    rect,
                    template,
                    QualifierTextName,
                    qualifier,
                    ReportLayout.BandInsetX,
                    ReportLayout.QualifierMinY,
                    1f - ReportLayout.BandInsetX,
                    ReportLayout.QualifierMaxY,
                    qualifierFontSize,
                    TextAlignmentOptions.Center);
                qualifierText.color = NeutralValueColor;
                if (box.QualifierCarriesValence)
                    RegisterPrimaryValue(qualifierText, box);
            }

            CreateCategoryConnector(row, template, box, index, boxes, visual);
            animated?.Add(new AnimatedAmount(primaryText, box, visual, qualifierText));
        }

        /// <summary>
        /// Drops a chevron into the gap before this cell when the two cells either side of it
        /// are consecutive steps of the same piece of quota math. The glyph belongs to the
        /// cell that follows it, so it reveals on that cell's beat instead of appearing early.
        /// </summary>
        private static void CreateCategoryConnector(
            RectTransform row,
            TextMeshProUGUI template,
            ContractPerformanceReportBox box,
            int index,
            IReadOnlyList<ContractPerformanceReportBox> boxes,
            ReportCellVisual visual)
        {
            if (row == null || template == null || index <= 0)
                return;

            string glyph = GetConnectorGlyph(boxes, index - 1);
            if (string.IsNullOrEmpty(glyph))
                return;

            GetConnectorAnchors(row, index - 1, boxes, out Vector2 anchorMin, out Vector2 anchorMax);
            var connector = CreateText(
                row,
                template,
                ConnectorNamePrefix + SanitizeObjectNameFragment(box.Heading),
                glyph,
                0f,
                0f,
                1f,
                1f,
                ReportLayout.ConnectorFontSize,
                TextAlignmentOptions.Center);
            connector.rectTransform.anchorMin = anchorMin;
            connector.rectTransform.anchorMax = anchorMax;
            connector.rectTransform.offsetMin = Vector2.zero;
            connector.rectTransform.offsetMax = Vector2.zero;
            connector.color = CellChromeColor;

            if (visual != null)
                visual.Connector = connector;
        }

        private static string ResolveQualifierText(ContractPerformanceReportBox box)
        {
            if (box == null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(box.Qualifier))
                return box.Qualifier;
            if (!string.IsNullOrWhiteSpace(box.MiddleText))
                return box.MiddleText;
            return box.Detail ?? string.Empty;
        }

        /// <summary>
        /// Puts the shared qualifier band back after the sequence's pre-pass blanked it, from
        /// the same resolver the cell was built with. Reading <c>MiddleText</c> directly here
        /// is what made a box that qualifies itself through
        /// <see cref="ContractPerformanceReportBox.Qualifier"/> - SCRAP's "DELIVERED", the
        /// exact-credit directive's - go permanently blank after one frame.
        ///
        /// #556: there is no dash placeholder any more. A cell with no qualifier never builds
        /// a band to restore, so this only ever runs where there is real text to put back.
        /// </summary>
        private static void RestoreQualifierBand(AnimatedAmount item)
        {
            if (item.MiddleText == null)
                return;

            item.MiddleText.text = ResolveQualifierText(item.Box);
        }

        private static string SanitizeObjectNameFragment(string value)
        {
            return string.IsNullOrEmpty(value) ? "Cell" : value.Replace(" ", string.Empty);
        }

        /// <summary>
        /// Where cell <paramref name="index"/> goes inside the row. The geometry itself is
        /// <see cref="PerformanceReportCellLayout"/>, a pure function of the row rect and the
        /// cell count: the cells always span the row, so a five-cell round fills the bottom
        /// row exactly the way a six-cell one does (#1204).
        /// </summary>
        private static void GetCompactCategoryAnchors(
            RectTransform row,
            int index,
            IReadOnlyList<ContractPerformanceReportBox> boxes,
            out Vector2 anchorMin,
            out Vector2 anchorMax)
        {
            ReportCellRect cell = PerformanceReportCellLayout.Solve(
                ReportLayout.CellLayout,
                row != null ? row.rect.width : 1f,
                row != null ? row.rect.height : 1f,
                boxes != null ? boxes.Count : 0,
                GetGradeCellIndex(boxes),
                index);

            anchorMin = new Vector2(cell.MinX, cell.MinY);
            anchorMax = new Vector2(cell.MaxX, cell.MaxY);
        }

        /// <summary>
        /// The gap between two neighbouring cells, which is where a connector glyph goes.
        /// Reuses the cell solver rather than duplicating it, so a connector cannot drift
        /// away from the cells it joins when the row is resized or auto-shrunk.
        /// </summary>
        private static void GetConnectorAnchors(
            RectTransform row,
            int leftIndex,
            IReadOnlyList<ContractPerformanceReportBox> boxes,
            out Vector2 anchorMin,
            out Vector2 anchorMax)
        {
            GetCompactCategoryAnchors(row, leftIndex, boxes, out _, out Vector2 leftMax);
            GetCompactCategoryAnchors(row, leftIndex + 1, boxes, out Vector2 rightMin, out _);

            float minX = Mathf.Min(leftMax.x, rightMin.x) - ReportLayout.ConnectorBleedRowFraction;
            float maxX = Mathf.Max(leftMax.x, rightMin.x) + ReportLayout.ConnectorBleedRowFraction;
            anchorMin = new Vector2(Mathf.Clamp01(minX), ReportLayout.ConnectorMinY);
            anchorMax = new Vector2(Mathf.Clamp01(maxX), ReportLayout.ConnectorMaxY);
        }

        /// <summary>
        /// The glyph that joins cell <paramref name="leftIndex"/> to the one after it: the
        /// result connector where a modifier step hands over to the revised quota, null
        /// everywhere else (#747: modifier-to-modifier gaps carry no glyph).
        /// </summary>
        private static string GetConnectorGlyph(IReadOnlyList<ContractPerformanceReportBox> boxes, int leftIndex)
        {
            if (boxes == null || leftIndex < 0 || leftIndex + 1 >= boxes.Count)
                return null;

            ContractPerformanceReportBox left = boxes[leftIndex];
            ContractPerformanceReportBox right = boxes[leftIndex + 1];
            if (left == null || right == null || !IsQuotaModifierBox(left))
                return null;

            return right.Kind == ReportPerformanceBoxKind.RevisedQuota
                ? ReportLayout.ConnectorResult
                : null;
        }

        private static bool IsQuotaModifierBox(ContractPerformanceReportBox box)
        {
            return box != null
                && (box.Kind == ReportPerformanceBoxKind.Impact || box.Kind == ReportPerformanceBoxKind.Amount);
        }

        /// <summary>
        /// The GRADE cell's position in the row, or -1 when the round has none. It is the one
        /// cell that is wider than its neighbours (<see cref="ReportLayout.GradeCellWidthMultiplier"/>),
        /// so the solver only needs to know where it is.
        /// </summary>
        private static int GetGradeCellIndex(IReadOnlyList<ContractPerformanceReportBox> boxes)
        {
            if (boxes == null)
                return -1;

            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i] != null && boxes[i].Kind == ReportPerformanceBoxKind.Grade)
                    return i;
            }

            return -1;
        }

        private static TextMeshProUGUI CreateText(
            RectTransform parent,
            TextMeshProUGUI template,
            string name,
            string value,
            float minX,
            float minY,
            float maxX,
            float maxY,
            float fontSize,
            TextAlignmentOptions alignment)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            var rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(minX, minY);
            rect.anchorMax = new Vector2(maxX, maxY);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var text = obj.AddComponent<TextMeshProUGUI>();
            text.text = value ?? string.Empty;
            text.font = template.font;
            if (template.fontSharedMaterial != null)
                text.fontSharedMaterial = template.fontSharedMaterial;
            text.fontStyle = FontStyles.Normal;
            text.fontSize = fontSize;
            text.enableAutoSizing = true;
            text.fontSizeMin = ReportLayout.TextAutoSizeMin;
            text.fontSizeMax = fontSize;
            text.alignment = alignment;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            text.color = NeutralColor;
            text.characterSpacing = template.characterSpacing;
            return text;
        }

        internal static void BeginPerformanceReportDelay(HUDManager hud, string reason, string pendingDescription)
        {
            bool alreadyArmed = _performanceReportDelayArmed || _reportSequenceActive;
            ArmPerformanceReportXpDelay();
            _reportSequenceActive = true;
            _delayXpPopupUntilRealtime = Mathf.Max(
                _delayXpPopupUntilRealtime,
                Time.realtimeSinceStartup + PerformanceReportPendingDelaySeconds);
            _delayDaysLeftUntilRealtime = Mathf.Max(
                _delayDaysLeftUntilRealtime,
                Time.realtimeSinceStartup + PerformanceReportPendingDelaySeconds + PostReportXpPopupBufferSeconds);
            HoldRoundEndHudChrome(hud, PerformanceReportPendingDelaySeconds + PostReportXpPopupBufferSeconds);
            SuppressControlTipLinesForReport(hud, true);

            string message =
                "[MoonContracts.ReportDiag] Armed performance report XP/token delay: " +
                $"reason='{reason ?? "unspecified"}', alreadyArmed={alreadyArmed}, " +
                $"pendingHold={PerformanceReportPendingDelaySeconds:0.00}s, " +
                $"failsafe={PerformanceReportDelayFailsafeSeconds:0.00}s, pending={pendingDescription ?? "<none>"}.";
            // #716 (audit F5): Debug either way now - the report's single Info summary is
            // emitted by TryApply, and this line's own reason/failsafe detail is diagnostic.
            ContractsBootstrap.Log?.LogDebug(message);
        }

        internal static void CancelPerformanceReportDelay(HUDManager hud, string reason)
        {
            bool wasArmed = _performanceReportDelayArmed || _reportSequenceActive;
            _performanceReportDelayArmed = false;
            _reportSequenceActive = false;
            _performanceReportDelayFailsafeUntilRealtime = 0f;
            _performanceReportCloseObserved = false;
            _performanceReportCloseObservedAtRealtime = 0f;
            _performanceReportDelayFailsafeLogged = false;
            if (!_xpPopupSequenceActive)
            {
                _delayXpPopupUntilRealtime = 0f;
                _delayDaysLeftUntilRealtime = 0f;
            }

            RestoreControlTipLinesAfterReportClose(hud);
            if (wasArmed)
            {
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.ReportDiag] Cancelled performance report XP/token delay: " +
                    $"reason='{reason ?? "unspecified"}'.");
            }
        }

        /// <param name="preview">
        /// #1203: the debug preview plays the same sequence but owns no round-end state. It
        /// never arms the XP/token gate, never hides the control tips, and leaves the endgame
        /// animator paused at the end so the panel holds until the preview is closed.
        /// </param>
        private static IEnumerator AnimateQuotaNumbers(HUDManager hud, List<AnimatedAmount> amounts, int visibleBoxCount, bool preview)
        {
            float originalAnimatorSpeed = 1f;
            bool animatorPaused = false;
            AudioSource audioSource = GetOrCreatePerformanceReportAudioSource(hud);
            int sequenceItemCount = amounts != null ? amounts.Count : 0;
            int animatedValueCount = CountAnimatedReportValueCells(amounts);
            bool staticHold = animatedValueCount == 0;
            if (!preview)
            {
                ArmPerformanceReportXpDelay();
                _reportSequenceActive = true;
                SuppressControlTipLinesForReport(hud, true);
            }
            ContractsBootstrap.Log?.LogDebug(
                "[MoonContracts.ReportDiag] Performance report sequence engaged: " +
                $"boxes={visibleBoxCount}, sequenceItems={sequenceItemCount}, " +
                $"animatedValues={animatedValueCount}, staticHold={staticHold}, preview={preview}.");

            yield return LoadPerformanceReportAudio();
            yield return LoadPerformanceReportImages();

            for (int i = 0; i < sequenceItemCount; i++)
            {
                var item = amounts[i];
                if (item.Text != null && item.Box != null)
                    item.Text.text = FormatInitialAmount(item.Box);
                // The qualifier band is blanked here so it reveals on its own cell's beat.
                // Every branch of the sequence below has to put it back through
                // RestoreQualifierBand, or a cell whose qualifier is not an impact string
                // renders one frame and then stays blank for the rest of the report.
                if (item.MiddleText != null)
                    item.MiddleText.text = string.Empty;
            }

            yield return WaitForPerformanceReportIntro(hud);
            animatorPaused = SetEndgameStatsAnimatorPaused(hud, true, ref originalAnimatorSpeed);
            // #716 (audit E12): every beat and every tween in this sequence runs on unscaled
            // time. The report is presentation over a game that has already ended, and the
            // guidelines require unscaled time for presentation-only animation - on scaled
            // time, opening the pause menu mid-report parked the ledger on whatever cell it
            // had reached. The endgame animator and the vanilla XP audio keep their own
            // clocks; nothing here touches either.
            yield return new WaitForSecondsRealtime(ReportSequencePreAnimationHoldSeconds);

            try
            {
                for (int i = 0; i < sequenceItemCount; i++)
                {
                    var item = amounts[i];
                    if (item.Text == null || item.Box == null)
                        continue;

                    yield return RevealCell(item.Visual);
                    SetCellActive(item.Visual, true);
                    RestoreQualifierBand(item);
                    if (item.Box.Kind == ReportPerformanceBoxKind.Grade)
                    {
                        yield return AnimateGradeLetter(item);
                    }
                    else if (item.Box.Kind == ReportPerformanceBoxKind.Impact)
                    {
                        yield return AnimateImpactBox(item, audioSource);
                    }
                    else if (ShouldAnimateNumeric(item.Box))
                    {
                        float duration = GetCountdownDuration(item.Box);
                        PlayCountdown(audioSource);
                        yield return AnimateNumericAmount(item, duration);
                        StopCountdown(audioSource);
                    }
                    else
                    {
                        item.Text.text = FormatAmount(item.Box, GetTargetAmount(item.Box));
                        yield return new WaitForSecondsRealtime(0.05f);
                    }

                    if (item.Box.IsFinalQuota)
                        PlayFinalQuotaConfirm(audioSource, item.Box);
                    else if (ShouldPlayConfirm(item.Box))
                        PlayConfirm(audioSource);

                    yield return FlashCellConfirm(item.Visual, item.Box.IsFinalQuota);
                    SetCellActive(item.Visual, false);
                    yield return new WaitForSecondsRealtime(ConfirmBeatSeconds);
                }

                yield return new WaitForSecondsRealtime(ReportSequencePostAnimationHoldSeconds);
            }
            finally
            {
                StopCountdown(audioSource);
                if (!preview)
                {
                    SetEndgameStatsAnimatorPaused(hud, false, ref originalAnimatorSpeed, animatorPaused);
                    _delayXpPopupUntilRealtime = Mathf.Max(
                        _delayXpPopupUntilRealtime,
                        Time.realtimeSinceStartup + ReportCloseXpBufferSeconds);
                    _delayDaysLeftUntilRealtime = Mathf.Max(
                        _delayDaysLeftUntilRealtime,
                        Time.realtimeSinceStartup + ReportCloseXpBufferSeconds + PostReportXpPopupBufferSeconds);
                    HoldRoundEndHudChrome(hud, ReportCloseXpBufferSeconds + PostReportXpPopupBufferSeconds);
                    _reportSequenceActive = false;
                    XpPopupGateSnapshot gate = GetXpPopupGateSnapshot(hud);
                    ContractsBootstrap.Log?.LogDebug(
                        "[MoonContracts.ReportDiag] Performance report sequence handing off to the existing XP window: " +
                        gate.FormatForLog());
                    RestoreControlTipLinesAfterReportClose(hud);
                }
            }
        }

        private static int CountAnimatedReportValueCells(List<AnimatedAmount> amounts)
        {
            if (amounts == null)
                return 0;

            int count = 0;
            for (int i = 0; i < amounts.Count; i++)
            {
                var box = amounts[i].Box;
                if (box == null || box.Kind == ReportPerformanceBoxKind.Grade)
                    continue;

                if (box.Kind == ReportPerformanceBoxKind.Impact)
                {
                    if (box.Animate && Mathf.Abs(box.ImpactPercent) > 0.001f)
                        count++;
                    continue;
                }

                if (ShouldAnimateNumeric(box))
                    count++;
            }

            return count;
        }

        private static IEnumerator WaitForPerformanceReportIntro(HUDManager hud)
        {
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < ReportSequenceStartDelaySeconds)
                yield return null;

            float waitUntil = Time.realtimeSinceStartup + 1.5f;
            while (Time.realtimeSinceStartup < waitUntil && !IsPerformanceReportIntroVisible(hud))
                yield return null;
        }

        private static bool IsPerformanceReportIntroVisible(HUDManager hud)
        {
            Animator animator = hud != null ? hud.endgameStatsAnimator : null;
            if (animator == null || !animator.isActiveAndEnabled)
                return true;

            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
            return state.normalizedTime >= 0.12f;
        }

        private static void ArmPerformanceReportXpDelay()
        {
            float now = Time.realtimeSinceStartup;
            _performanceReportDelayArmed = true;
            _performanceReportDelayFailsafeUntilRealtime = Mathf.Max(
                _performanceReportDelayFailsafeUntilRealtime,
                now + PerformanceReportDelayFailsafeSeconds);
            _performanceReportCloseObserved = false;
            _performanceReportCloseObservedAtRealtime = 0f;
            _performanceReportDelayFailsafeLogged = false;
        }

        private static void ConsumePerformanceReportXpDelay()
        {
            _performanceReportDelayArmed = false;
            _performanceReportDelayFailsafeUntilRealtime = 0f;
            _performanceReportCloseObserved = false;
            _performanceReportCloseObservedAtRealtime = 0f;
            _performanceReportDelayFailsafeLogged = false;
            _delayXpPopupUntilRealtime = 0f;
        }

        private static bool IsPerformanceReportDelayTimedOut(float now)
        {
            return _performanceReportDelayArmed
                && _performanceReportDelayFailsafeUntilRealtime > 0f
                && now >= _performanceReportDelayFailsafeUntilRealtime;
        }

        internal static bool ShouldDelayDaysLeftPopup()
        {
            float now = Time.realtimeSinceStartup;
            return _reportSequenceActive
                || _xpPopupSequenceActive
                || (_performanceReportDelayArmed && !IsPerformanceReportDelayTimedOut(now))
                || now < _delayXpPopupUntilRealtime
                || now < _delayDaysLeftUntilRealtime;
        }

        internal static bool ShouldDelayDaysLeftPopup(HUDManager hud)
        {
            return ShouldDelayDaysLeftPopup()
                || IsXpMeterAnimating(hud)
                || (_xpPopupSequenceActive && IsXpPopupVisible(hud))
                || (hud != null && hud.displayingNewQuota);
        }

        internal static bool ShouldDelayXpPopup(HUDManager hud)
        {
            return GetXpPopupGateSnapshot(hud).ShouldDelay;
        }

        // Resolved by Y4NGZUpgrades (ProgressionXpBarUi) via reflection: its
        // XP/token coroutine parks on this before playing any audio or meter
        // movement. Deliberately based on ReplayAllowed rather than
        // ShouldDelay — BeginXpPopupSequence consumes the delay before the
        // sanctioned replay runs, so the replay path never parks here, while a
        // call that leaks in under the report holds until the report (plus
        // close buffer) is done. The gate's own failsafe timeout bounds the
        // hold.
        internal static bool ShouldHoldXpPresentation(HUDManager hud)
        {
            return !GetXpPopupGateSnapshot(hud).ReplayAllowed;
        }

        internal static XpPopupGateSnapshot GetXpPopupGateSnapshot(HUDManager hud)
        {
            float now = Time.realtimeSinceStartup;
            RectTransform row = FindPerformanceReportRow();
            bool rowPresent = row != null && row.gameObject != null;
            bool rowActiveSelf = rowPresent && row.gameObject.activeSelf;
            bool rowActiveInHierarchy = rowPresent && row.gameObject.activeInHierarchy;

            Animator animator = hud != null ? hud.endgameStatsAnimator : null;
            bool animatorPresent = animator != null;
            bool animatorActive = animatorPresent && animator.isActiveAndEnabled;
            float animatorNormalizedTime = -1f;
            bool animatorPastClosed = !animatorActive;
            if (animatorActive)
            {
                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
                animatorNormalizedTime = state.normalizedTime;
                animatorPastClosed = state.normalizedTime >= ReportClosedAnimatorNormalizedTime;
            }

            bool rowClosed = !rowPresent || !rowActiveInHierarchy;
            bool chromeClosed = rowClosed || animatorPastClosed;
            if (_performanceReportDelayArmed
                && !_reportSequenceActive
                && chromeClosed
                && !_performanceReportCloseObserved)
            {
                _performanceReportCloseObserved = true;
                _performanceReportCloseObservedAtRealtime = now;
            }

            bool closeBuffered = _performanceReportCloseObserved
                && now - _performanceReportCloseObservedAtRealtime >= ReportCloseXpBufferSeconds;
            bool timedOut = IsPerformanceReportDelayTimedOut(now);
            if (timedOut && !_performanceReportDelayFailsafeLogged)
            {
                _performanceReportDelayFailsafeLogged = true;
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.ReportDiag] Performance report XP/token delay failsafe expired; " +
                    $"releasing gate after {PerformanceReportDelayFailsafeSeconds:0.00}s.");
            }

            bool reportGateActive = !timedOut
                && (_reportSequenceActive
                    || now < _delayXpPopupUntilRealtime
                    || _performanceReportDelayArmed);
            bool shouldDelay = _xpPopupSequenceActive || reportGateActive;
            bool replayAllowed;
            if (_reportSequenceActive)
                replayAllowed = false;
            else if (timedOut)
                replayAllowed = true;
            else if (_performanceReportDelayArmed)
                replayAllowed = now >= _delayXpPopupUntilRealtime;
            else
                replayAllowed = now >= _delayXpPopupUntilRealtime;

            return new XpPopupGateSnapshot
            {
                ShouldDelay = shouldDelay,
                ReplayAllowed = replayAllowed,
                Realtime = now,
                ReportSequenceActive = _reportSequenceActive,
                PerformanceReportDelayArmed = _performanceReportDelayArmed,
                XpPopupSequenceActive = _xpPopupSequenceActive,
                TimedOut = timedOut,
                RemainingDelayWindowSeconds = Mathf.Max(0f, _delayXpPopupUntilRealtime - now),
                RemainingFailsafeSeconds = _performanceReportDelayFailsafeUntilRealtime > 0f
                    ? Mathf.Max(0f, _performanceReportDelayFailsafeUntilRealtime - now)
                    : 0f,
                ReportCloseObserved = _performanceReportCloseObserved,
                ReportCloseBuffered = closeBuffered,
                ReportChromeClosed = chromeClosed,
                ReportRowPresent = rowPresent,
                ReportRowActiveSelf = rowActiveSelf,
                ReportRowActiveInHierarchy = rowActiveInHierarchy,
                AnimatorPresent = animatorPresent,
                AnimatorActive = animatorActive,
                AnimatorNormalizedTime = animatorNormalizedTime,
            };
        }

        internal static IEnumerator WaitForPerformanceReportXpReplayClear(HUDManager hud, string owner)
        {
            float start = Time.realtimeSinceStartup;
            XpPopupGateSnapshot gate = GetXpPopupGateSnapshot(hud);
            while (hud != null)
            {
                gate = GetXpPopupGateSnapshot(hud);
                if (gate.ReplayAllowed)
                    break;

                HoldRoundEndHudChrome(hud, 0.25f);
                yield return null;
            }

            gate = GetXpPopupGateSnapshot(hud);
            ContractsBootstrap.Log?.LogDebug(
                "[MoonContracts.ReportDiag] Delayed XP/token replay gate clear: " +
                $"owner='{owner ?? "unspecified"}', elapsed={Time.realtimeSinceStartup - start:0.00}s, " +
                gate.FormatForLog());
        }

        private static RectTransform FindPerformanceReportRow()
        {
            // The row this assembly built is the row this assembly is looking for, so the
            // scene walk is only ever a fallback for a row an earlier report left behind.
            if (_activeReportRow != null)
            {
                _cachedReportRow = _activeReportRow;
                return _activeReportRow;
            }

            if (_cachedReportRow != null)
                return _cachedReportRow;

            float now = Time.realtimeSinceStartup;
            if (now < _reportRowLookupRetryAtRealtime)
                return null;

            _reportRowLookupRetryAtRealtime = now + RoundEndLookupRetrySeconds;
            RectTransform[] rects = Resources.FindObjectsOfTypeAll<RectTransform>();
            for (int i = 0; i < rects.Length; i++)
            {
                RectTransform rect = rects[i];
                if (rect != null && string.Equals(rect.name, RowName, StringComparison.Ordinal))
                {
                    _cachedReportRow = rect;
                    return rect;
                }
            }

            return null;
        }

        /// <summary>
        /// Drops every held reference from the round-end lookups. Called wherever the
        /// presentation ends, so the next round never reads a row or a level-up box that
        /// belonged to the last one - a destroyed object would resolve to null on its own,
        /// but one that merely got re-used would not.
        /// </summary>
        private static void ResetRoundEndLookupCaches()
        {
            _cachedReportRow = null;
            _cachedLevelUpBox = null;
            CachedUpgradeTokenStrips.Clear();
            _reportRowLookupRetryAtRealtime = 0f;
            _levelUpBoxLookupRetryAtRealtime = 0f;
            _upgradeTokenStripLookupRetryAtRealtime = 0f;
        }

        internal static void BeginXpPopupSequence(HUDManager hud, string owner, object xpBreakdownLog)
        {
            BeginXpPopupSequence(hud, CreateXpPopupReplayPlan(hud, owner, xpBreakdownLog));
        }

        internal static void BeginXpPopupSequence(
            HUDManager hud,
            string owner,
            bool isDead,
            bool mostProfitable,
            bool allPlayersDead)
        {
            BeginXpPopupSequence(hud, CreateVanillaXpPopupReplayPlan(hud, owner, isDead, mostProfitable, allPlayersDead));
        }

        private static void BeginXpPopupSequence(HUDManager hud, XpPopupReplayPlan plan)
        {
            _xpPopupSequenceActive = true;
            ConsumePerformanceReportXpDelay();
            _xpPopupReplayPlan = plan ?? CreateFallbackXpPopupReplayPlan("unknown");
            PrepareXpPopupWindowForContinuation(hud);
            ContinueEndgameStatsAnimatorIntoXpEntrance(hud, _xpPopupReplayPlan);
            float chromeHold = _xpPopupReplayPlan.EntranceSeconds
                + _xpPopupReplayPlan.HoldSeconds
                + XpPopupExitFailsafeSeconds
                + 0.5f;
            HoldRoundEndHudChrome(hud, chromeHold);
            ContractsBootstrap.Log?.LogDebug(
                "[MoonContracts.ReportDiag] XP/token presentation attached to the existing endgame pass: " +
                _xpPopupReplayPlan.FormatForLog());
        }

        internal static IEnumerator WaitForXpPopupEntrance(HUDManager hud)
        {
            XpPopupReplayPlan plan = _xpPopupReplayPlan ?? CreateFallbackXpPopupReplayPlan("unknown");
            float start = Time.realtimeSinceStartup;
            float visibleSince = -1f;
            float timeout = Mathf.Max(
                XpPopupEntranceVisibilityTimeoutSeconds,
                plan.EntranceSeconds + 0.75f);

            while (hud != null && Time.realtimeSinceStartup - start < timeout)
            {
                if (!plan.AnimatorStarted)
                    break;

                Animator animator = hud.endgameStatsAnimator;
                if (animator == null || !animator.isActiveAndEnabled)
                    break;

                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
                if (state.fullPathHash != plan.AnimatorStateHash && state.shortNameHash != plan.AnimatorShortStateHash)
                    break;

                if (IsXpPopupVisible(hud))
                {
                    if (visibleSince < 0f)
                        visibleSince = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup - visibleSince >= plan.EntranceSeconds)
                    {
                        plan.EntranceReady = true;
                        break;
                    }
                }
                else
                {
                    visibleSince = -1f;
                }

                if (state.normalizedTime >= ReportCloseAnimatorNormalizedTime)
                    break;

                HoldRoundEndHudChrome(hud, 0.25f);
                yield return null;
            }

            PauseXpPopupAnimatorAtValueStart(hud, plan);

            // Freeze first, then run our own entrance so the box actually animates in
            // rather than popping at whatever frame the held clip was parked on.
            if (plan.EntranceReady)
                yield return FadeXpPopup(hud, 0f, 1f);
            else
                SetXpPopupFadeAlpha(_xpPopupFadeGroup, 1f);
        }

        internal static bool IsXpPopupEntranceReady(HUDManager hud)
        {
            return _xpPopupReplayPlan != null
                && _xpPopupReplayPlan.EntranceReady
                && IsXpPopupVisible(hud);
        }

        internal static IEnumerator WaitForXpPopupSequence(
            HUDManager hud,
            Func<bool> externalPresentationActive = null)
        {
            XpPopupReplayPlan plan = _xpPopupReplayPlan ?? CreateFallbackXpPopupReplayPlan("unknown");
            float start = Time.realtimeSinceStartup;
            float failsafeSeconds = Mathf.Max(
                plan.HoldSeconds + XpPopupReplayFailsafeExtraSeconds,
                XpPopupMinimumHoldSeconds + XpPopupReplayFailsafeExtraSeconds);
            bool timedOut = false;

            yield return null;

            while (hud != null)
            {
                float elapsed = Time.realtimeSinceStartup - start;
                bool externalActive = false;
                if (externalPresentationActive != null)
                {
                    try
                    {
                        externalActive = externalPresentationActive();
                    }
                    catch
                    {
                        externalActive = false;
                    }
                }

                if (elapsed >= plan.HoldSeconds && !externalActive)
                    break;

                if (elapsed >= failsafeSeconds)
                {
                    timedOut = true;
                    break;
                }

                HoldRoundEndHudChrome(hud, 0.25f);
                yield return null;
            }

            ContractsBootstrap.Log?.LogDebug(
                "[MoonContracts.ReportDiag] Delayed XP/token UI wait finished: " +
                $"elapsed={Time.realtimeSinceStartup - start:0.00}s, hold={plan.HoldSeconds:0.00}s, " +
                $"failsafe={failsafeSeconds:0.00}s, timedOut={timedOut}.");
        }

        internal static IEnumerator EndXpPopupSequence(HUDManager hud)
        {
            XpPopupReplayPlan plan = _xpPopupReplayPlan ?? CreateFallbackXpPopupReplayPlan("unknown");
            yield return FadeXpPopup(hud, _xpPopupFadeGroup != null ? _xpPopupFadeGroup.alpha : 1f, 0f);
            yield return PlayXpPopupExitAnimator(hud, plan);
            ReleaseXpPopupWindowAfterReplay(hud);
            _xpPopupSequenceActive = false;
            _xpPopupReplayPlan = null;
            _delayDaysLeftUntilRealtime = Mathf.Max(
                _delayDaysLeftUntilRealtime,
                Time.realtimeSinceStartup + PostXpDaysLeftBufferSeconds);
            HoldRoundEndHudChrome(hud, 0.75f);
            RestoreControlTipLinesAfterReportClose(hud);
        }

        internal static void HoldDaysLeftPopupSequence(HUDManager hud)
        {
            HoldRoundEndHudChrome(hud, DaysLeftPopupHudChromeHoldSeconds);
            RestoreControlTipLinesAfterReportClose(hud);
        }

        private static XpPopupReplayPlan CreateXpPopupReplayPlan(HUDManager hud, string owner, object xpBreakdownLog)
        {
            int awardedTotal = ReadIntMember(xpBreakdownLog, "AwardedTotal", 0);
            int oldXp = ReadIntMember(xpBreakdownLog, "OldXp", 0);
            int newXp = ReadIntMember(xpBreakdownLog, "NewXp", oldXp);
            int ranksGained = Mathf.Max(0, ReadIntMember(xpBreakdownLog, "RanksGained", 0));
            int currencyGranted = Mathf.Max(0, ReadIntMember(xpBreakdownLog, "CurrencyGranted", 0));
            int gain = Mathf.Max(0, awardedTotal);
            if (gain == 0 && newXp > oldXp)
                gain = newXp - oldXp;

            float valueSeconds = Mathf.Clamp(gain / 120f, XpPopupMinimumHoldSeconds, XpPopupValueDurationMaximumSeconds);
            int beatCount = Mathf.Max(ranksGained, currencyGranted > 0 ? 1 : 0);
            float beatSeconds = beatCount * XpPopupRankOrMarksBeatSeconds;
            float holdSeconds = valueSeconds + beatSeconds + XpPopupAudioTailSeconds;
            string timingSource = xpBreakdownLog != null
                ? $"y4ngz awarded={awardedTotal}, oldXp={oldXp}, newXp={newXp}, gain={gain}, ranks={ranksGained}, currency={currencyGranted}, value={valueSeconds:0.00}s, beats={beatSeconds:0.00}s, tail={XpPopupAudioTailSeconds:0.00}s"
                : $"fallback-y4ngz noLog value={valueSeconds:0.00}s, tail={XpPopupAudioTailSeconds:0.00}s";

            return CreateBaseXpPopupReplayPlan(owner, holdSeconds, timingSource);
        }

        private static XpPopupReplayPlan CreateVanillaXpPopupReplayPlan(
            HUDManager hud,
            string owner,
            bool isDead,
            bool mostProfitable,
            bool allPlayersDead)
        {
            int gain = isDead ? -3 : 10;
            if (mostProfitable)
                gain += 15;
            if (allPlayersDead)
                gain -= 5;

            float scrapRatio = 1f;
            if (gain > 0 && RoundManager.Instance != null)
            {
                float totalScrap = Mathf.Max(1f, RoundManager.Instance.totalScrapValueInLevel);
                scrapRatio = Mathf.Clamp01(RoundManager.Instance.scrapCollectedInLevel / totalScrap);
                gain = (int)(gain * scrapRatio);
            }

            int oldXp = hud != null ? Mathf.Max(0, hud.localPlayerXP) : 0;
            int targetXp = Mathf.Max(oldXp + gain, 0);
            int levelTransitions = CountVanillaLevelTransitions(hud, oldXp, targetXp, gain);
            float valueSeconds = gain == 0
                ? XpPopupMinimumHoldSeconds
                : Mathf.Min(5f, Mathf.Abs(gain) / 15f + levelTransitions * XpPopupRankOrMarksBeatSeconds);
            valueSeconds = Mathf.Max(XpPopupMinimumHoldSeconds, valueSeconds);
            float holdSeconds = Mathf.Max(
                valueSeconds + XpPopupAudioTailSeconds,
                XpPopupVanillaPostPlayerLevelHoldSeconds);
            string timingSource =
                $"vanilla gain={gain}, oldXp={oldXp}, targetXp={targetXp}, scrapRatio={scrapRatio:0.00}, " +
                $"levelTransitions={levelTransitions}, value={valueSeconds:0.00}s, tail={XpPopupAudioTailSeconds:0.00}s, vanillaPostHold={XpPopupVanillaPostPlayerLevelHoldSeconds:0.00}s, " +
                $"isDead={isDead}, mostProfitable={mostProfitable}, allPlayersDead={allPlayersDead}";

            return CreateBaseXpPopupReplayPlan(owner, holdSeconds, timingSource);
        }

        private static XpPopupReplayPlan CreateFallbackXpPopupReplayPlan(string owner)
        {
            return CreateBaseXpPopupReplayPlan(
                owner,
                XpPopupMinimumHoldSeconds + XpPopupAudioTailSeconds,
                $"fallback value={XpPopupMinimumHoldSeconds:0.00}s, tail={XpPopupAudioTailSeconds:0.00}s");
        }

        private static XpPopupReplayPlan CreateBaseXpPopupReplayPlan(string owner, float holdSeconds, string timingSource)
        {
            return new XpPopupReplayPlan
            {
                Owner = string.IsNullOrWhiteSpace(owner) ? "unspecified" : owner,
                HoldSeconds = Mathf.Max(XpPopupMinimumHoldSeconds, holdSeconds),
                TimingSource = timingSource ?? string.Empty,
                EntranceSeconds = XpPopupVanillaEntranceLeadSeconds,
                AnimatorStateName = StartOfRound.Instance != null && StartOfRound.Instance.isChallengeFile
                    ? VanillaEndgameStatsChallengeStateName
                    : VanillaEndgameStatsStateName,
                AnimatorTriggerName = StartOfRound.Instance != null && StartOfRound.Instance.isChallengeFile
                    ? VanillaEndgameStatsDisplayChallengeTriggerName
                    : VanillaEndgameStatsDisplayTriggerName,
                LevelAnimatorStateDescription = $"LevelUp/New State + {VanillaLevelUpShakeTriggerName} trigger",
            };
        }

        private static int ReadIntMember(object instance, string name, int fallback)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
                return fallback;

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = instance.GetType();
            try
            {
                FieldInfo field = type.GetField(name, Flags);
                if (field != null)
                {
                    object value = field.GetValue(instance);
                    if (value is int intValue)
                        return intValue;
                }

                PropertyInfo property = type.GetProperty(name, Flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    object value = property.GetValue(instance, null);
                    if (value is int intValue)
                        return intValue;
                }
            }
            catch
            {
            }

            return fallback;
        }

        private static int CountVanillaLevelTransitions(HUDManager hud, int oldXp, int targetXp, int gain)
        {
            if (hud == null || hud.playerLevels == null || hud.playerLevels.Length == 0 || gain == 0)
                return 0;

            int level = Mathf.Clamp(hud.localPlayerLevel, 0, hud.playerLevels.Length - 1);
            int transitions = 0;
            if (gain > 0)
            {
                while (level + 1 < hud.playerLevels.Length && targetXp >= hud.playerLevels[level].XPMax)
                {
                    transitions++;
                    level++;
                }
            }
            else
            {
                while (level - 1 >= 0 && targetXp < hud.playerLevels[level].XPMin)
                {
                    transitions++;
                    level--;
                }
            }

            return transitions;
        }

        private static void ContinueEndgameStatsAnimatorIntoXpEntrance(HUDManager hud, XpPopupReplayPlan plan)
        {
            if (hud == null || plan == null)
                return;

            Animator animator = hud.endgameStatsAnimator;
            if (animator == null || !animator.isActiveAndEnabled)
            {
                plan.AnimatorDescription = "endgameStatsAnimator=<missing/inactive>";
                return;
            }

            int expectedStateHash = ResolveAnimatorStateHash(animator, plan.AnimatorStateName);
            AnimatorStateInfo currentState = animator.GetCurrentAnimatorStateInfo(0);
            int expectedShortStateHash = Animator.StringToHash(plan.AnimatorStateName);
            bool isExpectedState = expectedStateHash != 0
                && (currentState.fullPathHash == expectedStateHash
                    || currentState.shortNameHash == expectedShortStateHash);
            if (!isExpectedState || currentState.normalizedTime >= ReportCloseAnimatorNormalizedTime)
            {
                plan.AnimatorDescription =
                    $"endgameStatsAnimator could not attach to active state='{plan.AnimatorStateName}', " +
                    $"currentHash={currentState.fullPathHash}, currentNormalized={currentState.normalizedTime:0.00}";
                return;
            }

            plan.AnimatorStateHash = currentState.fullPathHash;
            plan.AnimatorShortStateHash = currentState.shortNameHash;
            plan.AnimatorOriginalSpeed = animator.speed;
            if (plan.AnimatorOriginalSpeed <= 0f)
                plan.AnimatorOriginalSpeed = 1f;

            SetLevelUpHierarchyActive(hud);
            animator.speed = plan.AnimatorOriginalSpeed;
            plan.AnimatorStateLength = Mathf.Max(0.01f, currentState.length);
            float entranceStartSeconds = Mathf.Max(0f, XpPopupVanillaSetPlayerLevelSeconds - XpPopupVanillaEntranceLeadSeconds);
            plan.EntranceStartNormalized = Mathf.Clamp01(entranceStartSeconds / plan.AnimatorStateLength);
            plan.ValueStartNormalized = Mathf.Clamp01(XpPopupVanillaSetPlayerLevelSeconds / plan.AnimatorStateLength);
            if (plan.ValueStartNormalized >= ReportCloseAnimatorNormalizedTime)
                plan.ValueStartNormalized = Mathf.Max(plan.EntranceStartNormalized, ReportCloseAnimatorNormalizedTime - 0.05f);

            float currentNormalizedTime = Mathf.Clamp01(currentState.normalizedTime);
            bool advancedToEntrance = currentNormalizedTime < plan.EntranceStartNormalized;
            if (advancedToEntrance)
            {
                animator.Play(plan.AnimatorStateHash, 0, plan.EntranceStartNormalized);
                animator.Update(0f);
            }

            plan.AnimatorStarted = true;
            plan.AnimatorDescription =
                $"endgameStatsAnimator single-pass continuation state='{plan.AnimatorStateName}', " +
                $"stateLength={plan.AnimatorStateLength:0.00}s, entranceStart={plan.EntranceStartNormalized:0.00}, " +
                $"valueStart={plan.ValueStartNormalized:0.00}, attachedAt={currentNormalizedTime:0.00}, " +
                $"advancedToEntrance={advancedToEntrance}, exitTarget={ReportClosedAnimatorNormalizedTime:0.00}; " +
                $"playerLevelBoxAnimator='{plan.LevelAnimatorStateDescription}'";
        }

        private static int ResolveAnimatorStateHash(Animator animator, string stateName)
        {
            if (animator == null || string.IsNullOrWhiteSpace(stateName))
                return 0;

            int fullPathHash = Animator.StringToHash("Base Layer." + stateName);
            if (animator.HasState(0, fullPathHash))
                return fullPathHash;

            int shortHash = Animator.StringToHash(stateName);
            return animator.HasState(0, shortHash) ? shortHash : 0;
        }

        private static void PauseXpPopupAnimatorAtValueStart(HUDManager hud, XpPopupReplayPlan plan)
        {
            if (hud == null || plan == null || !plan.AnimatorStarted || !plan.EntranceReady)
                return;

            Animator animator = hud.endgameStatsAnimator;
            if (animator == null)
                return;

            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
            if (state.fullPathHash == plan.AnimatorStateHash || state.shortNameHash == plan.AnimatorShortStateHash)
                plan.ValueStartNormalized = Mathf.Clamp01(state.normalizedTime);

            _xpEndgameStatsAnimatorOriginalSpeed = plan.AnimatorOriginalSpeed;
            if (!_xpEndgameStatsAnimatorPaused)
                _xpEndgameStatsAnimatorPaused = SetEndgameStatsAnimatorPaused(hud, true, ref _xpEndgameStatsAnimatorOriginalSpeed);
            plan.AnimatorPausedForHold = _xpEndgameStatsAnimatorPaused;
        }

        private static IEnumerator PlayXpPopupExitAnimator(HUDManager hud, XpPopupReplayPlan plan)
        {
            if (hud == null || plan == null || !plan.AnimatorStarted)
                yield break;

            Animator animator = hud.endgameStatsAnimator;
            if (animator == null || !animator.isActiveAndEnabled)
                yield break;

            SetEndgameStatsAnimatorPaused(hud, false, ref _xpEndgameStatsAnimatorOriginalSpeed, _xpEndgameStatsAnimatorPaused);
            _xpEndgameStatsAnimatorPaused = false;
            animator.speed = plan.AnimatorOriginalSpeed <= 0f ? 1f : plan.AnimatorOriginalSpeed;

            float start = Time.realtimeSinceStartup;
            bool reachedNaturalClose = false;
            while (hud != null && Time.realtimeSinceStartup - start < XpPopupExitFailsafeSeconds)
            {
                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
                if ((state.fullPathHash == plan.AnimatorStateHash || state.shortNameHash == plan.AnimatorShortStateHash)
                    && state.normalizedTime >= ReportClosedAnimatorNormalizedTime)
                {
                    reachedNaturalClose = true;
                    break;
                }

                HoldRoundEndHudChrome(hud, 0.25f);
                yield return null;
            }

            if (!reachedNaturalClose)
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.ReportDiag] Endgame XP window did not reach its natural close before the exit wait elapsed; " +
                    "leaving the animator running without seeking or replaying it.");
            }
        }

        private static bool IsXpMeterAnimating(HUDManager hud)
        {
            return (hud?.LevellingAudio != null && hud.LevellingAudio.isPlaying)
                || IsUpgradeTokenStripVisible();
        }

        private static bool IsXpPopupReplayActive(HUDManager hud)
        {
            return IsXpMeterAnimating(hud)
                || IsXpPopupVisible(hud);
        }

        private static bool IsXpPopupVisible(HUDManager hud)
        {
            RectTransform levelUpBox = FindVisibleVanillaLevelUpBox(hud);
            if (levelUpBox == null || levelUpBox.gameObject == null || !levelUpBox.gameObject.activeInHierarchy)
                return false;

            Canvas canvas = levelUpBox.GetComponentInParent<Canvas>();
            if (canvas == null || !canvas.isActiveAndEnabled || !IsXpCanvasGroupPathVisible(levelUpBox))
                return false;

            Graphic[] graphics = levelUpBox.GetComponentsInChildren<Graphic>(false);
            bool hasVisibleGraphic = false;
            for (int i = 0; i < graphics.Length; i++)
            {
                Graphic graphic = graphics[i];
                if (graphic != null
                    && graphic.enabled
                    && graphic.gameObject.activeInHierarchy
                    && graphic.color.a > 0.03f)
                {
                    hasVisibleGraphic = true;
                    break;
                }
            }

            return hasVisibleGraphic && IsXpRectOnScreen(levelUpBox, canvas);
        }

        private static bool IsXpCanvasGroupPathVisible(Transform transform)
        {
            float effectiveAlpha = 1f;
            for (Transform current = transform; current != null; current = current.parent)
            {
                CanvasGroup[] groups = current.GetComponents<CanvasGroup>();
                bool ignoreParents = false;
                for (int i = 0; i < groups.Length; i++)
                {
                    CanvasGroup group = groups[i];
                    if (group == null || !group.isActiveAndEnabled)
                        continue;

                    // Our own entrance/exit fade must not read as "the window closed".
                    if (group == _xpPopupFadeGroup)
                        continue;

                    effectiveAlpha *= group.alpha;
                    if (effectiveAlpha <= 0.03f)
                        return false;
                    ignoreParents |= group.ignoreParentGroups;
                }

                if (ignoreParents)
                    break;
            }

            return true;
        }

        private static bool IsXpRectOnScreen(RectTransform rect, Canvas canvas)
        {
            Rect screenRect = CanvasScreenRect(canvas);
            if (screenRect.width <= 0f || screenRect.height <= 0f)
                return true;

            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : canvas.worldCamera;
            rect.GetWorldCorners(XpPopupWorldCorners);

            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;
            for (int i = 0; i < XpPopupWorldCorners.Length; i++)
            {
                Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, XpPopupWorldCorners[i]);
                minX = Mathf.Min(minX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxX = Mathf.Max(maxX, point.x);
                maxY = Mathf.Max(maxY, point.y);
            }

            return maxX >= screenRect.xMin
                && minX <= screenRect.xMax
                && maxY >= screenRect.yMin
                && minY <= screenRect.yMax;
        }

        private static RectTransform FindVisibleVanillaLevelUpBox(HUDManager hud)
        {
            // #716 (audit B12): IsXpPopupVisible calls this every frame of the XP window, and
            // this used to be a GameObject.Find backed by a walk of every loaded GameObject.
            // The box is a fixed part of the endgame canvas, so one resolve holds for the
            // life of the scene.
            if (_cachedLevelUpBox != null)
                return _cachedLevelUpBox;

            float now = Time.realtimeSinceStartup;
            if (now < _levelUpBoxLookupRetryAtRealtime)
                return null;

            _levelUpBoxLookupRetryAtRealtime = now + RoundEndLookupRetrySeconds;

            GameObject levelUpBox = GameObject.Find("/Systems/UI/Canvas/EndgameStats/LevelUp/LevelUpBox");
            if (levelUpBox != null && levelUpBox.TryGetComponent(out RectTransform directRect))
            {
                _cachedLevelUpBox = directRect;
                return directRect;
            }

            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                GameObject obj = all[i];
                if (obj == null || obj.name != "LevelUpBox")
                    continue;

                if (obj.TryGetComponent(out RectTransform rect))
                {
                    _cachedLevelUpBox = rect;
                    return rect;
                }
            }

            return null;
        }

        private static RectTransform FindVanillaLevelUpBox(HUDManager hud)
        {
            RectTransform levelUpBox = FindVisibleVanillaLevelUpBox(hud);
            if (levelUpBox != null)
                return levelUpBox;

            RectTransform fieldRoot = FindCommonRankPanelRoot(hud);
            if (fieldRoot != null)
                return fieldRoot;

            return null;
        }

        private static RectTransform FindCommonRankPanelRoot(HUDManager hud)
        {
            if (hud == null || hud.playerLevelMeter == null)
                return null;

            RectTransform meter = hud.playerLevelMeter.rectTransform;
            for (Transform current = meter; current != null; current = current.parent)
            {
                if (!(current is RectTransform rect))
                    continue;

                if (ContainsTransform(rect, hud.playerLevelMeter)
                    && ContainsTransform(rect, hud.playerLevelText)
                    && ContainsTransform(rect, hud.playerLevelXPCounter))
                {
                    return rect;
                }
            }

            return null;
        }

        private static bool ContainsTransform(Transform root, Component child)
        {
            if (root == null || child == null)
                return false;

            Transform childTransform = child.transform;
            return childTransform == root || childTransform.IsChildOf(root);
        }

        private static bool IsUpgradeTokenStripVisible()
        {
            // #716 (audit B12): reached every frame from IsXpMeterAnimating, which the XP gate
            // polls. The three strips are Y4NGZUpgrades objects with fixed names, so the
            // answer is an activeInHierarchy read on cached references. Only a negative
            // re-scans - a strip created after the last scan is picked up within the retry
            // interval - so the walk runs at most twice a second instead of once a frame.
            for (int i = CachedUpgradeTokenStrips.Count - 1; i >= 0; i--)
            {
                RectTransform cached = CachedUpgradeTokenStrips[i];
                if (cached == null || cached.gameObject == null)
                {
                    CachedUpgradeTokenStrips.RemoveAt(i);
                    continue;
                }

                if (cached.gameObject.activeInHierarchy)
                    return true;
            }

            float now = Time.realtimeSinceStartup;
            if (now < _upgradeTokenStripLookupRetryAtRealtime)
                return false;

            _upgradeTokenStripLookupRetryAtRealtime = now + RoundEndLookupRetrySeconds;
            CachedUpgradeTokenStrips.Clear();

            bool visible = false;
            RectTransform[] rects = Resources.FindObjectsOfTypeAll<RectTransform>();
            for (int i = 0; i < rects.Length; i++)
            {
                RectTransform rect = rects[i];
                if (rect == null || rect.gameObject == null || !IsUpgradeTokenStripName(rect.name))
                    continue;

                CachedUpgradeTokenStrips.Add(rect);
                visible |= rect.gameObject.activeInHierarchy;
            }

            return visible;
        }

        private static bool IsUpgradeTokenStripName(string name)
        {
            return string.Equals(name, "Y4NGZ_TokensGrantedExtension", StringComparison.Ordinal)
                || string.Equals(name, "Y4NGZ_MarksGrantedStrip", StringComparison.Ordinal)
                || string.Equals(name, "Y4NGZ_MarksGrantedExtension", StringComparison.Ordinal);
        }

        private static void SuppressControlTipLinesForReport(HUDManager hud, bool suppress)
        {
            if (!suppress)
            {
                RestoreSuppressedControlTipLines();
                return;
            }

            EnsureRoundEndHudChromeSuppressed(hud);
        }

        internal static void HoldRoundEndHudChrome(HUDManager hud, float seconds)
        {
            _suppressRoundEndHudChromeUntilRealtime = Mathf.Max(
                _suppressRoundEndHudChromeUntilRealtime,
                Time.realtimeSinceStartup + Mathf.Max(0f, seconds));
            EnsureRoundEndHudChromeSuppressed(hud);
        }

        private static bool ShouldSuppressRoundEndHudChrome()
        {
            return _reportSequenceActive
                || _xpPopupSequenceActive
                || Time.realtimeSinceStartup < _suppressRoundEndHudChromeUntilRealtime;
        }

        private static void EnsureRoundEndHudChromeSuppressed(HUDManager hud)
        {
            SuppressRoundEndHudChromeObjects(hud);

            // #451: the handle alone is not proof of life. The keep-alive used to
            // run on HUDManager, so a scene transition destroyed it mid-flight
            // while _tooltipRestoreCoroutine stayed non-null — after which this
            // early-returned forever and the chrome never came back. The running
            // flag is cleared by the coroutine's own finally, including when it is
            // disposed by a StopCoroutine or a destroyed host.
            if (_tooltipRestoreRunning)
                return;

            _tooltipRestoreCoroutine = null;
            StartRoundEndHudChromeKeepAlive(hud);
        }

        /// <summary>
        /// Runs the keep-alive on the plugin's own DontDestroyOnLoad host so the
        /// scene transition that ends the round cannot kill it. HUDManager is only
        /// a fallback for the (impossible in practice) case of the host being gone.
        /// </summary>
        private static void StartRoundEndHudChromeKeepAlive(HUDManager hud)
        {
            MonoBehaviour host = Plugin.CoroutineHost;
            if (host == null)
                host = hud;
            if (host == null)
                return;

            _tooltipRestoreRunning = true;
            _tooltipRestoreCoroutine = host.StartCoroutine(MaintainSuppressedRoundEndHudChrome());
        }

        private static IEnumerator MaintainSuppressedRoundEndHudChrome()
        {
            try
            {
                // HUDManager is re-read every frame rather than captured: the
                // coroutine now outlives any single instance of it.
                float hudMissingSince = -1f;
                while (ShouldSuppressRoundEndHudChrome())
                {
                    HUDManager hud = HUDManager.Instance;
                    if (hud == null)
                    {
                        if (hudMissingSince < 0f)
                            hudMissingSince = Time.realtimeSinceStartup;
                        else if (Time.realtimeSinceStartup - hudMissingSince > HudMissingKeepAliveGiveUpSeconds)
                        {
                            ContractsBootstrap.Log?.LogWarning(
                                "[MoonContracts.ReportDiag] Round-end HUD keep-alive gave up: no HUDManager for " +
                                $"{HudMissingKeepAliveGiveUpSeconds:0.#}s; force-restoring suppressed chrome.");
                            // Drop our own handle before the force-restore: it
                            // stops the keep-alive, and that would be this very
                            // coroutine stopping itself mid-iteration.
                            _tooltipRestoreCoroutine = null;
                            ForceRestoreRoundEndHudSuppression("keep-alive lost HUDManager");
                            yield break;
                        }

                        yield return null;
                        continue;
                    }

                    hudMissingSince = -1f;
                    SuppressRoundEndHudChromeObjects(hud);
                    yield return null;
                }

                yield return new WaitForSecondsRealtime(0.15f);
                if (!ShouldSuppressRoundEndHudChrome())
                {
                    RestoreSuppressedControlTipLines();
                    // Failsafe: if the XP sequence died mid-fade the level box would stay
                    // invisible for the rest of the session. Nothing holds it now, so undo.
                    RestoreXpPopupFadeGroup();
                }
            }
            finally
            {
                _tooltipRestoreCoroutine = null;
                _tooltipRestoreRunning = false;
            }
        }

        /// <summary>
        /// Unwind everything the round-end presentation holds, whatever state it
        /// believes it is in. Reached when an outcome is discarded unrendered or a
        /// new round starts (#451): those are exactly the paths where no sequence
        /// is left alive to release its own claims, and a pinned
        /// <see cref="IsRoundEndPresentationActive"/> would hide the gameplay HUD
        /// for the rest of the session.
        /// </summary>
        internal static void ForceRestoreRoundEndHudSuppression(string reason)
        {
            int canvasGroups = SuppressedReportHudCanvasGroups.Count;
            int tooltips = SuppressedReportTooltips.Count;
            bool hadFlags = _reportSequenceActive
                || _performanceReportDelayArmed
                || _xpPopupSequenceActive
                || Time.realtimeSinceStartup < _suppressRoundEndHudChromeUntilRealtime
                || _xpPopupFadeGroup != null;

            if (canvasGroups == 0 && tooltips == 0 && !hadFlags
                && _tooltipRestoreCoroutine == null && !_tooltipRestoreRunning)
                return;

            bool reportSequenceActive = _reportSequenceActive;
            bool delayArmed = _performanceReportDelayArmed;
            bool xpPopupActive = _xpPopupSequenceActive;

            // Clear the gates first: the keep-alive loops on them, so it must be
            // able to see the release before it is asked to stop.
            _reportSequenceActive = false;
            _performanceReportDelayArmed = false;
            _xpPopupSequenceActive = false;
            _suppressRoundEndHudChromeUntilRealtime = 0f;

            if (_tooltipRestoreCoroutine != null)
            {
                MonoBehaviour host = Plugin.CoroutineHost;
                try
                {
                    host?.StopCoroutine(_tooltipRestoreCoroutine);
                }
                catch (Exception ex)
                {
                    // #716 (audit G5): swallowed on purpose - this is the failsafe, and a host
                    // that cannot stop its own coroutine must not stop the HUD being handed
                    // back. Named once at Debug so it is at least provable from a log.
                    if (!_keepAliveStopFailureLogged)
                    {
                        _keepAliveStopFailureLogged = true;
                        ContractsBootstrap.Log?.LogDebug(
                            "[MoonContracts.ReportDiag] Round-end keep-alive stop threw " +
                            $"{ex.GetType().Name}; continuing the force-restore.");
                    }
                }

                _tooltipRestoreCoroutine = null;
            }

            _tooltipRestoreRunning = false;

            RestoreSuppressedControlTipLines();
            RestoreXpPopupFadeGroup();
            ResetRoundEndLookupCaches();

            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.ReportDiag] Force-restored round-end HUD suppression ({reason}): " +
                $"canvasGroups={canvasGroups}, tooltipObjects={tooltips}, " +
                $"reportSequenceActive={reportSequenceActive}, performanceReportDelayArmed={delayArmed}, " +
                $"xpPopupSequenceActive={xpPopupActive}, " +
                $"registryRemaining: groups={HudSuppressionRegistry.SuppressedGroupCount}, " +
                $"objects={HudSuppressionRegistry.SuppressedObjectCount}.");
        }

        private static void SuppressRoundEndHudChromeObjects(HUDManager hud)
        {
            TextMeshProUGUI[] lines = hud != null ? hud.controlTipLines : null;
            if (lines != null)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i] == null || lines[i].gameObject == null)
                        continue;

                    TrySuppressControlTipObject(lines[i].gameObject);
                }
            }

            if (hud == null)
                return;

            SuppressHudElement(hud.Inventory);
            SuppressHudElement(hud.Chat);
            if (ShouldSuppressPlayerInfoDuringRoundEndHudChrome())
                SuppressHudElement(hud.PlayerInfo);
            SuppressHudElement(hud.Tooltips);
            SuppressHudElement(hud.InstabilityCounter);
            SuppressHudElement(hud.Clock);
            SuppressHudElement(hud.Compass);
            SuppressCanvasGroup(hud.selfRedCanvasGroup);
            SuppressCanvasGroup(hud.holdInteractionCanvasGroup);

            if (hud.buildModeControlTip != null)
                TrySuppressControlTipObject(hud.buildModeControlTip.gameObject);

            if (hud.itemOnlySlotIconFrame != null)
                TrySuppressControlTipObject(hud.itemOnlySlotIconFrame.gameObject);

            if (hud.itemSlotIconFrames != null)
            {
                for (int i = 0; i < hud.itemSlotIconFrames.Length; i++)
                {
                    if (hud.itemSlotIconFrames[i] != null)
                        TrySuppressControlTipObject(hud.itemSlotIconFrames[i].gameObject);
                }
            }

            if (hud.itemSlotIcons != null)
            {
                for (int i = 0; i < hud.itemSlotIcons.Length; i++)
                {
                    if (hud.itemSlotIcons[i] != null)
                        TrySuppressControlTipObject(hud.itemSlotIcons[i].gameObject);
                }
            }
        }

        private static void SuppressHudElement(HUDElement element)
        {
            if (element == null || element.canvasGroup == null)
                return;

            SuppressCanvasGroup(element.canvasGroup, element);
        }

        private static bool ShouldSuppressPlayerInfoDuringRoundEndHudChrome()
        {
            return _reportSequenceActive
                || (!_xpPopupSequenceActive && Time.realtimeSinceStartup < _suppressRoundEndHudChromeUntilRealtime);
        }

        private static void RestoreXpHudElementsBeforeReplay(HUDManager hud)
        {
            if (hud == null)
                return;

            RestoreSuppressedHudElement(hud.PlayerInfo);
            RestoreSuppressedCanvasGroup(hud.playerLevelMeter != null ? hud.playerLevelMeter.GetComponentInParent<CanvasGroup>(true) : null);
            RestoreSuppressedCanvasGroup(hud.playerLevelText != null ? hud.playerLevelText.GetComponentInParent<CanvasGroup>(true) : null);
            RestoreSuppressedCanvasGroup(hud.playerLevelXPCounter != null ? hud.playerLevelXPCounter.GetComponentInParent<CanvasGroup>(true) : null);

            RectTransform levelUpBox = FindVanillaLevelUpBox(hud);
            if (levelUpBox != null && levelUpBox.gameObject != null)
                levelUpBox.gameObject.SetActive(true);
        }

        private static void PrepareXpPopupWindowForContinuation(HUDManager hud)
        {
            if (hud == null)
                return;

            RestoreXpHudElementsBeforeReplay(hud);
            SetLevelUpHierarchyActive(hud);
            CanvasGroup fadeGroup = EnsureXpPopupFadeGroup(hud);
            SetXpPopupFadeAlpha(fadeGroup, 0f);
        }

        private static void ReleaseXpPopupWindowAfterReplay(HUDManager hud)
        {
            SetEndgameStatsAnimatorPaused(hud, false, ref _xpEndgameStatsAnimatorOriginalSpeed, _xpEndgameStatsAnimatorPaused);
            _xpEndgameStatsAnimatorPaused = false;
            RestoreXpPopupFadeGroup();
            if (hud != null && hud.LevellingAudio != null && hud.LevellingAudio.isPlaying)
                hud.LevellingAudio.Stop();
        }

        private static CanvasGroup EnsureXpPopupFadeGroup(HUDManager hud)
        {
            RectTransform levelUpBox = FindVanillaLevelUpBox(hud);
            if (levelUpBox == null || levelUpBox.gameObject == null)
                return null;

            if (_xpPopupFadeGroup != null && _xpPopupFadeGroup.gameObject == levelUpBox.gameObject)
                return _xpPopupFadeGroup;

            // A different box than last time - never leave the old one faded out.
            RestoreXpPopupFadeGroup();

            CanvasGroup group = levelUpBox.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = levelUpBox.gameObject.AddComponent<CanvasGroup>();
                _xpPopupFadeGroupRestoreAlpha = 1f;
            }
            else if (HudSuppressionRegistry.TryGetSnapshotAlpha(group, out float suppressedAlpha))
            {
                // The monitor takeover is hiding this very group, so the live
                // alpha is its zero, not the scene's. Borrow the registry's
                // first-observer snapshot instead (#451).
                _xpPopupFadeGroupRestoreAlpha = suppressedAlpha;
            }
            else
            {
                // Borrowed, not ours: hand back exactly what the scene had.
                _xpPopupFadeGroupRestoreAlpha = group.alpha;
            }

            _xpPopupFadeGroup = group;
            return group;
        }

        private static void RestoreXpPopupFadeGroup()
        {
            if (_xpPopupFadeGroup == null)
                return;

            SetXpPopupFadeAlpha(_xpPopupFadeGroup, _xpPopupFadeGroupRestoreAlpha);
            _xpPopupFadeGroup = null;
            _xpPopupFadeGroupRestoreAlpha = 1f;
        }

        /// <summary>
        /// The XP box fade is an animation this file owns outright, but the same
        /// CanvasGroup can also be under monitor-takeover suppression. Writing
        /// through that would un-hide the box mid-takeover and desynchronise the
        /// registry's snapshot, so the registry wins while it holds the group and
        /// hands back the right value itself on release (#451).
        /// </summary>
        private static void SetXpPopupFadeAlpha(CanvasGroup group, float alpha)
        {
            if (group == null || HudSuppressionRegistry.IsSuppressed(group))
                return;

            group.alpha = alpha;
        }

        private static IEnumerator FadeXpPopup(HUDManager hud, float from, float to)
        {
            CanvasGroup group = _xpPopupFadeGroup;
            if (group == null)
                yield break;

            SetXpPopupFadeAlpha(group, from);
            float start = Time.realtimeSinceStartup;
            while (group != null && Time.realtimeSinceStartup - start < XpPopupOwnedFadeSeconds)
            {
                float t = Mathf.Clamp01((Time.realtimeSinceStartup - start) / XpPopupOwnedFadeSeconds);
                SetXpPopupFadeAlpha(group, Mathf.Lerp(from, to, t * t * (3f - 2f * t)));
                HoldRoundEndHudChrome(hud, 0.25f);
                yield return null;
            }

            SetXpPopupFadeAlpha(group, to);
        }

        private static void SetLevelUpHierarchyActive(HUDManager hud)
        {
            RectTransform levelUpBox = FindVanillaLevelUpBox(hud);
            if (levelUpBox == null)
                return;

            Transform current = levelUpBox;
            for (int depth = 0; current != null && depth < 3; depth++, current = current.parent)
            {
                if (current.gameObject != null && !current.gameObject.activeSelf)
                    current.gameObject.SetActive(true);

                if (string.Equals(current.gameObject.name, "LevelUp", StringComparison.Ordinal))
                    break;
            }
        }

        private static void RestoreSuppressedHudElement(HUDElement element)
        {
            if (element == null || element.canvasGroup == null)
                return;

            RestoreSuppressedCanvasGroup(element.canvasGroup);
        }

        private static void SuppressCanvasGroup(CanvasGroup canvasGroup, HUDElement element = null)
        {
            if (canvasGroup == null)
                return;

            // #716 (audit B12): SuppressRoundEndHudChromeObjects re-asserts about twenty of
            // these every frame for the length of the report, and each assert was a registry
            // lookup plus four component writes. The re-assert exists for the frames where
            // vanilla writes an element's target alpha back, so it is kept - but only as a
            // write when something actually moved. The first claim always goes through,
            // because that is the call the registry captures the pre-suppression snapshot and
            // the HUDElement half from.
            if (!SuppressedReportHudCanvasGroups.Contains(canvasGroup))
            {
                SuppressedReportHudCanvasGroups.Add(canvasGroup);
            }
            else if (canvasGroup.alpha <= 0f
                && !canvasGroup.interactable
                && !canvasGroup.blocksRaycasts
                && (element == null || element.targetAlpha <= 0f))
            {
                return;
            }

            HudSuppressionRegistry.Suppress(canvasGroup, HudSuppressionHolders.RoundEndReport, element);
        }

        private static void RestoreSuppressedCanvasGroup(CanvasGroup canvasGroup)
        {
            if (canvasGroup == null)
                return;

            SuppressedReportHudCanvasGroups.Remove(canvasGroup);
            HudSuppressionRegistry.Release(canvasGroup, HudSuppressionHolders.RoundEndReport);
        }

        private static void TrySuppressControlTipObject(GameObject obj)
        {
            if (obj == null)
                return;

            // Same write-on-change rule as SuppressCanvasGroup above: an object we already
            // claimed and that is still inactive needs no SetActive(false) this frame.
            if (!SuppressedReportTooltips.Contains(obj))
                SuppressedReportTooltips.Add(obj);
            else if (!obj.activeSelf)
                return;

            HudSuppressionRegistry.SuppressObject(obj, HudSuppressionHolders.RoundEndReport);
        }

        private static void RestoreControlTipLinesAfterReportClose(HUDManager hud)
        {
            if (hud == null)
            {
                RestoreSuppressedControlTipLines();
                return;
            }

            if (_tooltipRestoreRunning)
                return;

            _tooltipRestoreCoroutine = null;
            StartRoundEndHudChromeKeepAlive(hud);
        }

        private static void RestoreSuppressedControlTipLines()
        {
            // Release by holder, not by list: anything the takeover still holds
            // stays hidden until it releases too, and the original state is
            // written back exactly once, by whoever releases last (#451).
            HudSuppressionRegistry.ReleaseAll(HudSuppressionHolders.RoundEndReport);
            HudSuppressionRegistry.ReleaseAllObjects(HudSuppressionHolders.RoundEndReport);
            SuppressedReportHudCanvasGroups.Clear();
            SuppressedReportTooltips.Clear();
        }

        private static int GetSettlingAmount(int target, float progress)
        {
            return GetSettlingAmount(0, Mathf.Abs(target), progress);
        }

        private static int GetSettlingAmount(int start, int target, float progress)
        {
            if (start == target)
                return target;

            float eased = 1f - Mathf.Pow(1f - Mathf.Clamp01(progress), 3f);
            return Mathf.RoundToInt(Mathf.Lerp(start, target, eased));
        }

        private static string FormatInitialAmount(ContractPerformanceReportBox box)
        {
            if (box == null)
                return string.Empty;

            if (box.Kind == ReportPerformanceBoxKind.Grade)
                return string.Empty;

            if (box.Kind == ReportPerformanceBoxKind.Impact)
                return string.IsNullOrWhiteSpace(box.ValueOverride) ? FormatImpactPercent(box, 0f) : box.ValueOverride;

            if (!string.IsNullOrWhiteSpace(box.ValueOverride))
                return box.ValueOverride;

            if (box.HasStartAmount)
                return FormatAmount(box, box.StartAmount);

            if (box.Animate)
                return FormatAmount(box, 0);

            return FormatAmount(box, box.Amount);
        }

        private static string FormatAmount(ContractPerformanceReportBox box, int amount)
        {
            if (box == null)
                return string.Empty;

            if (box.Kind == ReportPerformanceBoxKind.Grade)
                return string.IsNullOrWhiteSpace(box.ValueOverride) ? "-----" : box.ValueOverride;

            // #747: an impact cell with nothing to report states a plain figure instead of a
            // signed zero percent - CASUALTIES over "0", not over "+0%".
            if (box.Kind == ReportPerformanceBoxKind.Impact)
                return string.IsNullOrWhiteSpace(box.ValueOverride) ? FormatImpactPercent(box, amount) : box.ValueOverride;

            int value = Mathf.Max(0, amount);
            if (box.Kind == ReportPerformanceBoxKind.Collected)
            {
                if (string.IsNullOrWhiteSpace(box.SecondaryValue))
                    return value.ToString();

                return $"{value}/{box.SecondaryValue}";
            }

            // #747: a bare figure. The report font's currency glyph draws as a block even
            // though the font reports it present, and the revised quota's direction is the
            // arrow beside it, so neither a sign nor a currency mark is written.
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool ShouldAnimateNumeric(ContractPerformanceReportBox box)
        {
            if (box == null || box.Kind == ReportPerformanceBoxKind.Grade)
                return false;

            if (box.Kind == ReportPerformanceBoxKind.Impact)
                return false;

            if (box.Kind == ReportPerformanceBoxKind.RevisedQuota)
                return box.HasStartAmount && box.StartAmount != box.Amount;

            return box.Animate
                && string.IsNullOrWhiteSpace(box.ValueOverride)
                && Mathf.Abs(box.Amount) > 0;
        }

        private static bool ShouldPlayConfirm(ContractPerformanceReportBox box)
        {
            return box != null
                && (box.Animate
                    || box.PlayConfirmWhenStatic
                    || box.Kind == ReportPerformanceBoxKind.Grade);
        }

        private static int GetStartAmount(ContractPerformanceReportBox box)
        {
            if (box == null)
                return 0;

            return box.HasStartAmount ? Mathf.Max(0, box.StartAmount) : 0;
        }

        private static int GetTargetAmount(ContractPerformanceReportBox box)
        {
            if (box == null)
                return 0;

            if (box.Kind == ReportPerformanceBoxKind.Impact)
                return Mathf.RoundToInt(Mathf.Abs(box.ImpactPercent));

            return Mathf.Max(0, box.Amount);
        }

        /// <summary>
        /// A percent here always reads as the effect on the quota: "+15%" means the quota
        /// rose, which is a penalty; "-15%" means it fell, which is a benefit. #556: no cell
        /// names the moved thing inline any more - the heading above already does, and the
        /// bottom band states the fact behind the figure.
        /// </summary>
        private static string FormatImpactPercent(ContractPerformanceReportBox box, float percent)
        {
            float value = Mathf.Max(0f, percent);
            string sign = FigureGlyph(box != null && box.IsIncrease ? "+" : "-");
            if (value <= 0.001f)
                return box != null && box.ShowSignAtZero ? sign + "0%" : "0%";

            string formatted = Mathf.Approximately(value, Mathf.Round(value))
                ? Mathf.RoundToInt(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            return $"{sign}{formatted}%";
        }

        private static float GetCountdownDuration(ContractPerformanceReportBox box)
        {
            float clipLength = _countdownClip != null && _countdownClip.length > 0.05f
                ? _countdownClip.length
                : 1.433f;
            if (box == null)
                return Mathf.Min(clipLength, 0.5f);

            if (box.Kind == ReportPerformanceBoxKind.Impact)
            {
                float percent = Mathf.Abs(box.ImpactPercent);
                if (percent <= 0.001f)
                    return 0f;
                if (percent < 10f)
                    return Mathf.Min(clipLength, 0.5f);
                if (percent < 25f)
                    return Mathf.Min(clipLength, 0.75f);
                return Mathf.Min(clipLength, 1.0f);
            }

            int span = Mathf.Abs(GetTargetAmount(box) - GetStartAmount(box));
            if (box.IsFinalQuota)
                return clipLength;

            if (span <= 0)
                return 0f;
            if (span < 10)
                return Mathf.Min(clipLength, 0.5f);
            if (span < 100)
                return Mathf.Min(clipLength, 0.75f);
            if (span < 1000)
                return Mathf.Min(clipLength, 1.0f);

            return clipLength;
        }

        private static IEnumerator AnimateNumericAmount(AnimatedAmount item, float duration)
        {
            if (item.Text == null || item.Box == null)
                yield break;

            int start = GetStartAmount(item.Box);
            int target = GetTargetAmount(item.Box);
            int direction = Math.Sign(target - start);
            bool showFinalQuotaDirection = item.Box.IsFinalQuota
                && item.Box.HasStartAmount
                && item.Box.Amount != item.Box.StartAmount;
            SetFinalQuotaDirectionArrowActive(item.Visual, item.Box, showFinalQuotaDirection);
            if (duration <= 0.01f)
            {
                item.Text.text = FormatAmount(item.Box, target);
                SetActiveScanlineProgress(item.Visual, 1f, direction);
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < duration && item.Text != null)
            {
                float progress = Mathf.Clamp01(elapsed / duration);
                item.Text.text = FormatAmount(item.Box, GetSettlingAmount(start, target, progress));
                SetActiveScanlineProgress(item.Visual, progress, direction);
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (item.Text != null)
                item.Text.text = FormatAmount(item.Box, target);
            SetActiveScanlineProgress(item.Visual, 1f, direction);
        }

        private static IEnumerator AnimateImpactBox(AnimatedAmount item, AudioSource audioSource)
        {
            if (item.Text == null || item.Box == null)
                yield break;

            RestoreQualifierBand(item);
            if (!string.IsNullOrWhiteSpace(item.Box.ValueOverride))
            {
                item.Text.text = item.Box.ValueOverride;
                yield break;
            }

            yield return new WaitForSecondsRealtime(0.18f);

            float targetPercent = Mathf.Abs(item.Box.ImpactPercent);
            float duration = GetCountdownDuration(item.Box);
            if (duration <= 0.01f || targetPercent <= 0.001f)
            {
                item.Text.text = FormatImpactPercent(item.Box, targetPercent);
                SetActiveScanlineProgress(item.Visual, 1f);
                yield break;
            }

            PlayCountdown(audioSource);
            float elapsed = 0f;
            while (elapsed < duration && item.Text != null)
            {
                float progress = Mathf.Clamp01(elapsed / duration);
                float eased = 1f - Mathf.Pow(1f - progress, 3f);
                item.Text.text = FormatImpactPercent(item.Box, Mathf.Lerp(0f, targetPercent, eased));
                SetActiveScanlineProgress(item.Visual, progress);
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            StopCountdown(audioSource);
            if (item.Text != null)
                item.Text.text = FormatImpactPercent(item.Box, targetPercent);
            SetActiveScanlineProgress(item.Visual, 1f);
        }

        private static IEnumerator AnimateGradeLetter(AnimatedAmount item)
        {
            if (item.Text == null || item.Box == null)
                yield break;

            // #716 (audit E12): the grade cell is a bare letter (guidelines, "The LGU_ report
            // row"), so a missing grade is one glyph wide like every grade that resolves - a
            // five-dash rule read as a redacted field rather than as "no grade".
            string finalGrade = string.IsNullOrWhiteSpace(item.Box.ValueOverride)
                ? "-"
                : item.Box.ValueOverride.Trim();
            string[] flicker = { "F", "D", "C", "B", "A", "S" };
            float elapsed = 0f;
            const float duration = 0.48f;
            while (elapsed < duration && item.Text != null)
            {
                int index = Mathf.Clamp(Mathf.FloorToInt((elapsed / duration) * flicker.Length), 0, flicker.Length - 1);
                item.Text.text = flicker[index];
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (item.Text != null)
                item.Text.text = finalGrade;
        }

        private static IEnumerator FlashCellConfirm(ReportCellVisual visual, bool strong)
        {
            if (visual == null)
                yield break;

            float duration = strong ? 0.28f : 0.16f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float pulse = 1f - Mathf.Clamp01(elapsed / duration);
                Color border = Color.Lerp(NeutralColor, ActiveColor, pulse);
                ApplyBorderColor(visual, border);
                if (visual.Fill != null)
                visual.Fill.color = new Color(ActiveColor.r, ActiveColor.g, ActiveColor.b, strong ? 0.20f * pulse : 0.12f * pulse);
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        private static void SetCellActive(ReportCellVisual visual, bool active)
        {
            if (visual == null)
                return;

            ApplyBorderColor(visual, active ? ActiveColor : NeutralColor);
            if (visual.Fill != null)
                visual.Fill.color = active
                    ? new Color(ActiveColor.r, ActiveColor.g, ActiveColor.b, 0.04f)
                    : PanelColor;
            if (visual.Sweep != null)
                visual.Sweep.gameObject.SetActive(false);
        }

        private static void SetCellVisible(ReportCellVisual visual, bool visible)
        {
            if (visual == null)
                return;

            if (visual.Rect != null)
                visual.Rect.gameObject.SetActive(visible);
            if (visual.TextRoot != null)
                visual.TextRoot.SetActive(visible);
            if (visual.Connector != null)
                visual.Connector.gameObject.SetActive(visible);
            if (visual.DirectionArrow != null)
                visual.DirectionArrow.SetActive(false);
            if (visible)
                ApplyCellAlpha(visual, 0f);
        }

        private static IEnumerator RevealCell(ReportCellVisual visual)
        {
            if (visual == null)
                yield break;

            SetCellVisible(visual, true);
            const float duration = 0.14f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float progress = Mathf.Clamp01(elapsed / duration);
                ApplyCellAlpha(visual, progress);
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            ApplyCellAlpha(visual, 1f);
        }

        private static void ApplyCellAlpha(ReportCellVisual visual, float alpha)
        {
            if (visual == null)
                return;

            alpha = Mathf.Clamp01(alpha);
            if (visual.Fill != null)
            {
                visual.Fill.color = new Color(PanelColor.r, PanelColor.g, PanelColor.b, PanelColor.a * alpha);
            }

            if (visual.Borders != null)
            {
                for (int i = 0; i < visual.Borders.Count; i++)
                {
                    Image border = visual.Borders[i];
                    if (border == null)
                        continue;

                    Color color = border.color;
                    color.a = NeutralColor.a * alpha;
                    border.color = color;
                }
            }

            if (visual.TextRoot != null)
            {
                var texts = visual.TextRoot.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i] == null)
                        continue;

                    Color color = texts[i].color;
                    color.a = NeutralColor.a * alpha;
                    texts[i].color = color;
                }
            }

            if (visual.Connector != null)
            {
                Color color = visual.Connector.color;
                color.a = CellChromeColor.a * alpha;
                visual.Connector.color = color;
            }
        }

        private static void SetActiveScanlineProgress(ReportCellVisual visual, float progress, int direction = 1)
        {
            if (visual?.Sweep == null)
                return;

            visual.Sweep.gameObject.SetActive(direction != 0);
            if (visual.Sweep.rectTransform != null)
            {
                float y = PerformanceReportMotion.SweepPosition(progress, direction);
                visual.Sweep.rectTransform.anchorMin = new Vector2(0.08f, y);
                visual.Sweep.rectTransform.anchorMax = new Vector2(0.92f, y);
                visual.Sweep.rectTransform.offsetMin = new Vector2(0f, -0.35f);
                visual.Sweep.rectTransform.offsetMax = new Vector2(0f, 0.35f);
            }
        }

        private static void ApplyBorderColor(ReportCellVisual visual, Color color)
        {
            if (visual?.Borders == null)
                return;

            for (int i = 0; i < visual.Borders.Count; i++)
            {
                if (visual.Borders[i] != null)
                    visual.Borders[i].color = color;
            }
        }

        private static bool SetEndgameStatsAnimatorPaused(
            HUDManager hud,
            bool paused,
            ref float originalSpeed,
            bool restoreAllowed = true)
        {
            Animator animator = hud != null ? hud.endgameStatsAnimator : null;
            if (animator == null)
                return false;

            if (paused)
            {
                originalSpeed = animator.speed;
                animator.speed = 0f;
                return true;
            }

            if (restoreAllowed)
                animator.speed = originalSpeed;
            return false;
        }

        private static AudioSource GetOrCreatePerformanceReportAudioSource(HUDManager hud)
        {
            if (hud == null)
                return null;

            const string AudioSourceName = "LGU_PerformanceReportAudio";
            Transform existing = hud.transform.Find(AudioSourceName);
            if (existing != null && existing.TryGetComponent(out AudioSource existingSource))
                return existingSource;

            var obj = new GameObject(AudioSourceName);
            obj.transform.SetParent(hud.transform, false);
            var source = obj.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.volume = PerformanceReportAudioVolume;
            return source;
        }

        private static IEnumerator LoadPerformanceReportAudio()
        {
            if (_audioLoadAttempted)
                yield break;

            _audioLoadAttempted = true;
            yield return LoadPerformanceReportClip(CountdownClipFileName, AudioType.WAV, clip => _countdownClip = clip);
            yield return LoadPerformanceReportClip(CountdownConfirmClipFileName, AudioType.MPEG, clip => _countdownConfirmClip = clip);
            yield return LoadPerformanceReportClip(FinalQuotaConfirmClipFileName, AudioType.MPEG, clip => _finalQuotaConfirmClip = clip);
            yield return LoadPerformanceReportClip(FinalQuotaDecreaseClipFileName, AudioType.MPEG, clip => _finalQuotaDecreaseClip = clip);
            yield return LoadPerformanceReportClip(FinalQuotaIncreaseClipFileName, AudioType.MPEG, clip => _finalQuotaIncreaseClip = clip);
        }

        private static IEnumerator LoadPerformanceReportImages()
        {
            if (_imageLoadAttempted)
                yield break;

            _imageLoadAttempted = true;
            yield return LoadPerformanceReportImageSprite(FinalQuotaIncreaseArrowFileName, sprite => _finalQuotaIncreaseArrowSprite = sprite);
            yield return LoadPerformanceReportImageSprite(FinalQuotaDecreaseArrowFileName, sprite => _finalQuotaDecreaseArrowSprite = sprite);
        }

        private static IEnumerator LoadPerformanceReportClip(string fileName, AudioType audioType, Action<AudioClip> assign)
        {
            string path = ResolvePerformanceReportAudioPath(fileName);
            if (string.IsNullOrWhiteSpace(path))
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Performance report audio missing: {fileName}");
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, audioType))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Failed loading performance report audio '{path}': {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip != null)
                    clip.name = $"LGU_PerformanceReport_{Path.GetFileNameWithoutExtension(fileName)}";
                assign?.Invoke(clip);
            }
        }

        private static string ResolvePerformanceReportAudioPath(string fileName)
        {
            string assemblyDir = Path.GetDirectoryName(typeof(ContractsBootstrap).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                Path.Combine(assemblyDir, "ContractAudio", PerformanceReportAudioFolderName, fileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", "ContractAudio", PerformanceReportAudioFolderName, fileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "ContractAudio", PerformanceReportAudioFolderName, fileName),
                Path.Combine(Paths.PluginPath, "ContractAudio", PerformanceReportAudioFolderName, fileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ContractAudio", PerformanceReportAudioFolderName, fileName)
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                    return candidates[i];
            }

            return null;
        }

        private static IEnumerator LoadPerformanceReportImageSprite(string fileName, Action<Sprite> assign)
        {
            string path = ResolvePerformanceReportImagePath(fileName);
            if (string.IsNullOrWhiteSpace(path))
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Performance report image missing: {fileName}");
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(new Uri(path).AbsoluteUri))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Failed loading performance report image '{path}': {request.error}");
                    yield break;
                }

                Texture2D texture = DownloadHandlerTexture.GetContent(request);
                if (texture == null)
                    yield break;

                texture.name = $"LGU_PerformanceReport_{Path.GetFileNameWithoutExtension(fileName)}";
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Point;

                var sprite = Sprite.Create(
                    texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
                sprite.name = texture.name;
                assign?.Invoke(sprite);
            }
        }

        private static string ResolvePerformanceReportImagePath(string fileName)
        {
            string assemblyDir = Path.GetDirectoryName(typeof(ContractsBootstrap).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                Path.Combine(assemblyDir, "ContractImages", PerformanceReportImageFolderName, fileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", "ContractImages", PerformanceReportImageFolderName, fileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "ContractImages", PerformanceReportImageFolderName, fileName),
                Path.Combine(Paths.PluginPath, "ContractImages", PerformanceReportImageFolderName, fileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ContractImages", PerformanceReportImageFolderName, fileName)
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                    return candidates[i];
            }

            return null;
        }

        private static void PlayCountdown(AudioSource source)
        {
            if (source == null || _countdownClip == null)
                return;

            source.clip = _countdownClip;
            source.Play();
        }

        private static void StopCountdown(AudioSource source)
        {
            if (source == null || source.clip != _countdownClip)
                return;

            source.Stop();
            source.clip = null;
        }

        private static void PlayConfirm(AudioSource source)
        {
            if (source != null && _countdownConfirmClip != null)
                source.PlayOneShot(_countdownConfirmClip, PerformanceReportConfirmVolumeScale);
        }

        private static void PlayFinalQuotaConfirm(AudioSource source, ContractPerformanceReportBox box)
        {
            if (source == null)
                return;

            AudioClip clip = _finalQuotaConfirmClip;
            if (box != null && box.HasStartAmount)
            {
                if (box.Amount < box.StartAmount && _finalQuotaDecreaseClip != null)
                    clip = _finalQuotaDecreaseClip;
                else if (box.Amount > box.StartAmount && _finalQuotaIncreaseClip != null)
                    clip = _finalQuotaIncreaseClip;
            }

            if (clip != null)
                source.PlayOneShot(clip, PerformanceReportConfirmVolumeScale);
        }

        private static TextMeshProUGUI FindTitleText(HUDManager hud)
        {
            if (hud == null)
                return null;

            return FindPerformanceReportTitleText(hud);
        }

        private static TextMeshProUGUI FindPerformanceReportTitleText(HUDManager hud)
        {
            var visited = new HashSet<int>();
            foreach (Transform root in EnumerateReportSearchRoots(hud))
            {
                if (root == null || !visited.Add(root.GetInstanceID()))
                    continue;

                var texts = root.GetComponentsInChildren<TextMeshProUGUI>(true);
                var title = texts.FirstOrDefault(IsPerformanceReportTitleCandidate);
                if (title != null)
                    return title;
            }

            return null;
        }

        // #747: the glyphs the row's figures need beyond letters and digits. The vanilla
        // report font does not carry all of them - "+$240" rendered as a fallback-font plus
        // and a missing-glyph box - so the template is the first vanilla report text whose
        // own font (no fallbacks) has every one, and any glyph still missing is dropped
        // from the figure rather than drawn by a font the panel never uses.
        private const string ReportFigureGlyphs = "$+-%/";
        private static TMP_FontAsset _reportGlyphFont;

        private static TextMeshProUGUI FindBoxTextTemplate(HUDManager hud, TextMeshProUGUI fallback)
        {
            var candidates = new List<TextMeshProUGUI>();
            var playerNames = hud?.statsUIElements?.playerNamesText;
            if (playerNames != null)
            {
                for (int i = 0; i < playerNames.Length; i++)
                {
                    if (playerNames[i] != null)
                        candidates.Add(playerNames[i]);
                }
            }

            if (hud?.statsUIElements?.quotaNumerator != null)
                candidates.Add(hud.statsUIElements.quotaNumerator);
            if (hud?.statsUIElements?.gradeLetter != null)
                candidates.Add(hud.statsUIElements.gradeLetter);
            if (fallback != null)
                candidates.Add(fallback);
            if (candidates.Count == 0)
                return null;

            TextMeshProUGUI chosen = candidates[0];
            for (int i = 0; i < candidates.Count; i++)
            {
                if (FontHasGlyphs(candidates[i].font, ReportFigureGlyphs))
                {
                    chosen = candidates[i];
                    break;
                }
            }

            if (_reportGlyphFont != chosen.font)
            {
                _reportGlyphFont = chosen.font;
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.ReportDiag] Report text template: " +
                    $"'{DescribeTransform(chosen.transform)}' font='{(chosen.font != null ? chosen.font.name : "<none>")}', " +
                    $"missing figure glyphs='{MissingGlyphs(chosen.font, ReportFigureGlyphs)}'.");
            }

            return chosen;
        }

        private static bool FontHasGlyphs(TMP_FontAsset font, string glyphs)
        {
            return font != null && MissingGlyphs(font, glyphs).Length == 0;
        }

        private static string MissingGlyphs(TMP_FontAsset font, string glyphs)
        {
            if (font == null)
                return glyphs;

            var missing = new StringBuilder();
            for (int i = 0; i < glyphs.Length; i++)
            {
                if (!font.HasCharacter(glyphs[i], false, false))
                    missing.Append(glyphs[i]);
            }

            return missing.ToString();
        }

        /// <summary>A figure with every glyph the report font cannot draw removed.</summary>
        private static string FigureGlyph(string glyph)
        {
            if (string.IsNullOrEmpty(glyph) || _reportGlyphFont == null)
                return glyph;

            var kept = new StringBuilder(glyph.Length);
            for (int i = 0; i < glyph.Length; i++)
            {
                char c = glyph[i];
                if (ReportFigureGlyphs.IndexOf(c) < 0 || _reportGlyphFont.HasCharacter(c, false, false))
                    kept.Append(c);
            }

            return kept.ToString();
        }

        private static RectTransform FindCanvasRoot(HUDManager hud, TextMeshProUGUI titleText)
        {
            Canvas canvas = FindCanvas(titleText)
                ?? FindCanvas(hud?.statsUIElements?.quotaNumerator)
                ?? FindCanvas(hud?.endgameStatsAnimator);
            if (canvas != null && canvas.transform is RectTransform canvasRect)
                return canvasRect;

            if (hud?.endgameStatsAnimator != null && hud.endgameStatsAnimator.transform is RectTransform animatorRect)
                return animatorRect;

            var reportRoot = FindReportRoot(hud, titleText);
            if (reportRoot != null)
                return reportRoot;

            return titleText != null ? titleText.rectTransform : null;
        }

        private static RectTransform FindReportContentRoot(HUDManager hud, TextMeshProUGUI titleText)
        {
            var reportRoot = FindReportRoot(hud, titleText);
            if (reportRoot != null && !IsCanvasRoot(reportRoot))
                return reportRoot;

            if (titleText?.transform?.parent is RectTransform titleParent && !IsCanvasRoot(titleParent))
                return titleParent;

            if (hud?.endgameStatsAnimator != null && hud.endgameStatsAnimator.transform is RectTransform animatorRoot)
                return animatorRoot;

            return reportRoot;
        }

        private static bool IsCanvasRoot(RectTransform rect)
        {
            return rect != null && rect.GetComponent<Canvas>() != null;
        }

        private static Canvas FindCanvas(Component component)
        {
            return component != null ? component.GetComponentInParent<Canvas>(true) : null;
        }

        private static IEnumerable<Transform> EnumerateReportSearchRoots(HUDManager hud)
        {
            if (hud == null)
                yield break;

            if (hud.endgameStatsAnimator != null)
                yield return hud.endgameStatsAnimator.transform;

            Canvas statsCanvas = FindCanvas(hud.statsUIElements?.quotaNumerator);
            if (statsCanvas != null)
                yield return statsCanvas.transform;

            Canvas titleCanvas = FindCanvas(hud.EndOfRunStatsText);
            if (titleCanvas != null)
                yield return titleCanvas.transform;

            if (hud.HUDContainer != null)
                yield return hud.HUDContainer.transform;

            yield return hud.transform;
        }

        private static bool IsPerformanceReportTitleCandidate(TextMeshProUGUI text)
        {
            return text != null
                && !string.IsNullOrWhiteSpace(text.text)
                && (text.text.IndexOf("PERFORMANCE REPORT", StringComparison.OrdinalIgnoreCase) >= 0
                    || IsContractTitle(text.text));
        }

        private static RectTransform FindUsefulParentRect(TextMeshProUGUI text, RectTransform canvasRoot)
        {
            if (text == null)
                return null;

            Transform current = text.transform.parent;
            for (int i = 0; current != null && current != canvasRoot && i < 4; i++, current = current.parent)
            {
                var rect = current as RectTransform;
                if (IsUsefulRect(rect))
                    return rect;
            }

            return text.rectTransform;
        }

        private static bool IsUsefulRect(RectTransform rect)
        {
            return rect != null
                && rect.rect.width > 20f
                && rect.rect.height > 10f;
        }

        private static void EncapsulateTextArray(TextMeshProUGUI[] texts, RectTransform canvasRoot, ref LocalRectBounds bounds)
        {
            if (texts == null)
                return;

            for (int i = 0; i < texts.Length; i++)
                TryEncapsulateRect(FindUsefulParentRect(texts[i], canvasRoot), canvasRoot, ref bounds);
        }

        private static void EncapsulateImageArray(Image[] images, RectTransform canvasRoot, ref LocalRectBounds bounds)
        {
            if (images == null)
                return;

            for (int i = 0; i < images.Length; i++)
            {
                if (images[i] != null)
                    TryEncapsulateRect(images[i].rectTransform, canvasRoot, ref bounds);
            }
        }

        private static bool TryEncapsulateRect(RectTransform rect, RectTransform canvasRoot, ref LocalRectBounds bounds)
        {
            if (rect == null || canvasRoot == null)
                return false;

            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            for (int i = 0; i < corners.Length; i++)
                bounds.Encapsulate(canvasRoot.InverseTransformPoint(corners[i]));
            return true;
        }

        private static bool TryGetLocalRectBounds(
            RectTransform rect,
            RectTransform canvasRoot,
            out LocalRectBounds bounds)
        {
            bounds = new LocalRectBounds();
            return TryEncapsulateRect(rect, canvasRoot, ref bounds);
        }

        private static RectTransform FindReportRoot(HUDManager hud, TextMeshProUGUI titleText)
        {
            if (hud?.statsUIElements == null)
                return null;

            Transform quota = hud.statsUIElements.quotaNumerator != null
                ? hud.statsUIElements.quotaNumerator.transform
                : null;
            Transform grade = hud.statsUIElements.gradeLetter != null
                ? hud.statsUIElements.gradeLetter.transform
                : null;

            if (titleText != null && quota != null && grade != null)
            {
                for (Transform current = titleText.transform.parent; current != null; current = current.parent)
                {
                    if (quota.IsChildOf(current) && grade.IsChildOf(current))
                        return current as RectTransform;
                }
            }

            if (quota == null || grade == null)
                return titleText != null ? titleText.transform.parent as RectTransform : null;

            for (Transform current = quota; current != null; current = current.parent)
            {
                if (grade.IsChildOf(current))
                    return current as RectTransform;
            }

            return null;
        }

        private static void ClearChildren(RectTransform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
        }

        private static void DestroyChild(RectTransform parent, string childName)
        {
            if (parent == null || string.IsNullOrWhiteSpace(childName))
                return;

            var child = parent.Find(childName);
            if (child != null)
                UnityEngine.Object.Destroy(child.gameObject);
        }

        private static bool IsContractTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string value = text.Trim();
            // #749: the live format is "<CONTRACT> \u00B7 SUCCESSFUL/FAILURE" - subject first,
            // so it is a suffix match. The #716 FULFILLED/VOIDED suffixes and the status-first
            // prefixes before them stay recognized: Restore has to hand back a title written
            // by an older build of this assembly that a save/session carried across.
            return value.EndsWith(" \u00B7 SUCCESSFUL", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(" \u00B7 FAILURE", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(" \u00B7 FULFILLED", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(" \u00B7 VOIDED", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("CONTRACT COMPLETE:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("CONTRACT FAILED:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("DIRECTIVE COMPLETE:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("DIRECTIVE FAILED:", StringComparison.OrdinalIgnoreCase);
        }

        private static string DescribeText(TextMeshProUGUI text)
        {
            if (text == null)
                return "<null>";

            string value = text.text ?? string.Empty;
            value = value.Replace("\r\n", "\\n").Replace('\n', ' ').Replace('\r', ' ');
            if (value.Length > 80)
                value = value.Substring(0, 80) + "...";

            return $"'{value}' at '{DescribeTransform(text.transform)}'";
        }

        private static bool IsCustomReportTransform(Transform transform)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (string.Equals(current.name, RowName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string SanitizeLogText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
            return value.Length > 40 ? value.Substring(0, 40) + "..." : value;
        }

        private static string DescribeTransform(Component component)
        {
            return component != null ? DescribeTransform(component.transform) : "<null>";
        }

        private static string DescribeTransform(Transform transform)
        {
            return transform != null ? GetTransformPath(transform) : "<null>";
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            var parts = new List<string>();
            for (Transform current = transform; current != null; current = current.parent)
                parts.Add(current.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static string FormatBounds(LocalRectBounds bounds)
        {
            if (!bounds.HasAny)
                return "<empty>";

            return $"({bounds.MinX:0.0},{bounds.MinY:0.0})-({bounds.MaxX:0.0},{bounds.MaxY:0.0})";
        }

        private static bool IsReasonableBounds(LocalRectBounds bounds, RectTransform canvasRoot)
        {
            if (!bounds.HasAny || canvasRoot == null)
                return false;

            float canvasWidth = Mathf.Max(1f, canvasRoot.rect.width);
            float canvasHeight = Mathf.Max(1f, canvasRoot.rect.height);
            float width = bounds.MaxX - bounds.MinX;
            float height = bounds.MaxY - bounds.MinY;
            float maxSpan = Mathf.Max(4096f, Mathf.Max(canvasWidth, canvasHeight) * 8f);
            return width > 1f
                && height > 1f
                && width <= maxSpan
                && height <= maxSpan;
        }

        private struct LocalRectBounds
        {
            internal bool HasAny;
            internal float MinX;
            internal float MaxX;
            internal float MinY;
            internal float MaxY;
            internal float Width => HasAny ? MaxX - MinX : 0f;
            internal float Height => HasAny ? MaxY - MinY : 0f;

            internal void Encapsulate(Vector3 point)
            {
                if (!HasAny)
                {
                    HasAny = true;
                    MinX = MaxX = point.x;
                    MinY = MaxY = point.y;
                    return;
                }

                MinX = Mathf.Min(MinX, point.x);
                MaxX = Mathf.Max(MaxX, point.x);
                MinY = Mathf.Min(MinY, point.y);
                MaxY = Mathf.Max(MaxY, point.y);
            }

            internal void Encapsulate(LocalRectBounds other)
            {
                if (!other.HasAny)
                    return;

                Encapsulate(new Vector3(other.MinX, other.MinY, 0f));
                Encapsulate(new Vector3(other.MaxX, other.MaxY, 0f));
            }

            internal bool Overlaps(LocalRectBounds other, float padding)
            {
                if (!HasAny || !other.HasAny)
                    return false;

                return MaxX >= other.MinX - padding
                    && MinX <= other.MaxX + padding
                    && MaxY >= other.MinY - padding
                    && MinY <= other.MaxY + padding;
            }
        }

        internal struct XpPopupGateSnapshot
        {
            internal bool ShouldDelay;
            internal bool ReplayAllowed;
            internal float Realtime;
            internal bool ReportSequenceActive;
            internal bool PerformanceReportDelayArmed;
            internal bool XpPopupSequenceActive;
            internal bool TimedOut;
            internal float RemainingDelayWindowSeconds;
            internal float RemainingFailsafeSeconds;
            internal bool ReportCloseObserved;
            internal bool ReportCloseBuffered;
            internal bool ReportChromeClosed;
            internal bool ReportRowPresent;
            internal bool ReportRowActiveSelf;
            internal bool ReportRowActiveInHierarchy;
            internal bool AnimatorPresent;
            internal bool AnimatorActive;
            internal float AnimatorNormalizedTime;

            internal string FormatForLog()
            {
                return $"shouldDelay={ShouldDelay}, replayAllowed={ReplayAllowed}, " +
                    $"reportSequenceActive={ReportSequenceActive}, delayArmed={PerformanceReportDelayArmed}, " +
                    $"xpSequenceActive={XpPopupSequenceActive}, remainingDelay={RemainingDelayWindowSeconds:0.00}s, " +
                    $"failsafeRemaining={RemainingFailsafeSeconds:0.00}s, realtime={Realtime:0.00}, " +
                    $"timedOut={TimedOut}, closeObserved={ReportCloseObserved}, closeBuffered={ReportCloseBuffered}, " +
                    $"chromeClosed={ReportChromeClosed}, rowPresent={ReportRowPresent}, " +
                    $"rowActiveSelf={ReportRowActiveSelf}, rowActiveInHierarchy={ReportRowActiveInHierarchy}, " +
                    $"animatorPresent={AnimatorPresent}, animatorActive={AnimatorActive}, " +
                    $"animatorNormalized={AnimatorNormalizedTime:0.00}";
            }
        }

        private sealed class XpPopupReplayPlan
        {
            internal string Owner;
            internal float HoldSeconds;
            internal float EntranceSeconds;
            internal string TimingSource;
            internal string AnimatorStateName;
            internal string AnimatorTriggerName;
            internal string AnimatorDescription;
            internal string LevelAnimatorStateDescription;
            internal int AnimatorStateHash;
            internal int AnimatorShortStateHash;
            internal float AnimatorStateLength;
            internal float AnimatorOriginalSpeed = 1f;
            internal float EntranceStartNormalized;
            internal float ValueStartNormalized;
            internal bool AnimatorStarted;
            internal bool EntranceReady;
            internal bool AnimatorPausedForHold;

            internal string FormatForLog()
            {
                string animator = string.IsNullOrWhiteSpace(AnimatorDescription)
                    ? $"endgameStatsAnimator trigger='{AnimatorTriggerName}', state='{AnimatorStateName}', playerLevelBoxAnimator='{LevelAnimatorStateDescription}'"
                    : AnimatorDescription;
                return $"owner='{Owner}', hold={HoldSeconds:0.00}s, entrance={EntranceSeconds:0.00}s, " +
                    $"timing=({TimingSource}), animator=({animator}), pausedForHold={AnimatorPausedForHold}.";
            }
        }

        /// <summary>
        /// Everything <see cref="ApplyPlayerNotes"/> overwrites on one vanilla note label.
        /// </summary>
        private sealed class NoteLabelState
        {
            internal Vector2 OffsetMin;
            internal Vector2 OffsetMax;
            internal bool HasRect;
            internal bool RichText;
            internal bool WordWrapping;
            internal TextOverflowModes OverflowMode;
            internal TextAlignmentOptions Alignment;
            internal bool AutoSizing;
            internal float FontSizeMin;
            internal float FontSizeMax;
            internal float FontSize;
            internal float LineSpacing;
            internal Vector4 Margin;
            internal Color Color;
        }

        private readonly struct AnimatedAmount
        {
            internal readonly TextMeshProUGUI Text;
            internal readonly ContractPerformanceReportBox Box;
            internal readonly ReportCellVisual Visual;
            internal readonly TextMeshProUGUI MiddleText;

            internal AnimatedAmount(
                TextMeshProUGUI text,
                ContractPerformanceReportBox box,
                ReportCellVisual visual,
                TextMeshProUGUI middleText = null)
            {
                Text = text;
                Box = box;
                Visual = visual;
                MiddleText = middleText;
            }
        }

        private sealed class ReportCellVisual
        {
            internal readonly RectTransform Rect;
            internal readonly Image Fill;
            internal readonly List<Image> Borders;
            internal readonly Image Sweep;
            internal readonly GameObject DirectionArrow;
            internal GameObject TextRoot;
            // The connector that precedes this cell. It belongs to the cell it points at so
            // it reveals and fades on that cell's beat rather than sitting there alone.
            internal TMP_Text Connector;

            internal ReportCellVisual(RectTransform rect, Image fill, List<Image> borders, Image sweep, GameObject directionArrow = null)
            {
                Rect = rect;
                Fill = fill;
                Borders = borders;
                Sweep = sweep;
                DirectionArrow = directionArrow;
            }
        }
    }
}
