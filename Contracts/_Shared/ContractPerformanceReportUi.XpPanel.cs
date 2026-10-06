using System;
using System.Collections.Generic;
using System.Text;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1200: the framed "YOUR XP" panel right of the Notes column. It lists every visible XP
    /// source of the local player - Upgrades' snapshot sources (crew awards already left out by
    /// the ledger) and Company's paid act lines - one entry each, the label wrapped to two lines
    /// at the Notes line width and the amount right-aligned in the report accent, then a
    /// "Shift total" equal to the snapshot's awarded XP. What to list and how a label wraps is
    /// <see cref="PerformanceReportXpPanelPlanner"/>; this half measures and draws.
    ///
    /// The panel is a child of the report row, so the row's teardown, the theme pass and every
    /// "skip our own objects" rule of the vanilla heuristics cover it without a second name.
    /// It is placed from the measured drawn panel the way the row is: beside the report's right
    /// edge and as tall as the player grid, kept inside the screen. Only Company renders it; a
    /// report without Company stays with Upgrades' own Notes writer.
    /// </summary>
    internal static partial class ContractPerformanceReportUi
    {
        private const string XpPanelName = "LGU_QuotaReportXpPanel";
        private const string XpPanelLabelName = "XpPanelLabel";
        private const string XpPanelAmountName = "XpPanelAmount";
        private const string XpPanelRuleName = "XpPanelRule";
        private const string XpPanelTitle = "YOUR XP";

        // Resolved through the report family like every other hue in the row (#654).
        private static Color XpPanelFillColor => ThemePanel;
        private static Color XpPanelLabelColor => NeutralValueColor;
        private static Color XpPanelAmountColor => ThemeAccent;

        private static XpPanelHost _xpPanelHost;
        private static RectTransform _xpPanel;
        private static LocalRectBounds? _xpPanelBounds;
        private static string _xpPanelPlacement;
        // Set only when the panel had to sit over the report: the note labels end left of it.
        private static float? _xpPanelNoteLimitX;

        /// <summary>What one report hands the panel: where it lives, and the Notes line it matches.</summary>
        private sealed class XpPanelHost
        {
            internal readonly RectTransform Row;
            internal readonly RectTransform ReportRoot;
            /// <summary>The root canvas: its rect is the screen the panel must stay inside.</summary>
            internal readonly RectTransform ScreenRoot;
            internal readonly TextMeshProUGUI HeadingTemplate;
            internal readonly int LocalSlot;
            /// <summary>
            /// The local Notes line width in report-root units, measured once per report before
            /// the panel can pull the labels back, so a rebuild lays out exactly the same.
            /// </summary>
            internal readonly float NoteLineWidth;
            /// <summary>Note-label units per report-root unit, for the Notes font size.</summary>
            internal readonly float NoteUnitScale;

            internal XpPanelHost(
                RectTransform row,
                RectTransform reportRoot,
                RectTransform screenRoot,
                TextMeshProUGUI headingTemplate,
                int localSlot,
                float noteLineWidth,
                float noteUnitScale)
            {
                Row = row;
                ReportRoot = reportRoot;
                ScreenRoot = screenRoot;
                HeadingTemplate = headingTemplate;
                LocalSlot = localSlot;
                NoteLineWidth = noteLineWidth;
                NoteUnitScale = noteUnitScale;
            }
        }

        /// <summary>
        /// Called once per applied report, after the note labels are measured and clamped and
        /// before any Notes row is written. True when the local slot's panel is on screen.
        /// </summary>
        private static bool BeginXpPanel(
            HUDManager hud,
            ContractPerformanceReportData report,
            RectTransform row,
            RectTransform reportRoot,
            RectTransform canvasRoot,
            TextMeshProUGUI headingTemplate)
        {
            ClearXpPanel();
            int slot = GetLocalReportSlot(report);
            TextMeshProUGUI note = GetCapturedNoteLabel(hud, slot);
            if (row == null || reportRoot == null || canvasRoot == null || note == null)
                return false;

            float rootScale = Mathf.Abs(reportRoot.lossyScale.x);
            float noteScale = Mathf.Abs(note.rectTransform.lossyScale.x);
            float unit = rootScale > 0.0001f && noteScale > 0.0001f ? noteScale / rootScale : 1f;
            float lineWidth = Mathf.Max(1f, note.rectTransform.rect.width - note.margin.x - note.margin.z - 2f) * unit;
            _xpPanelHost = new XpPanelHost(row, reportRoot, GetRootCanvasRect(canvasRoot), headingTemplate, slot, lineWidth, unit);
            return ApplyXpPanel(hud, slot, report != null && report.IsPreview ? GetPreviewXpNoteCandidates(report, slot) : null);
        }

        // The report's canvas can be a nested one; only the root canvas spans the screen.
        private static RectTransform GetRootCanvasRect(RectTransform canvasRoot)
        {
            Canvas canvas = canvasRoot != null ? canvasRoot.GetComponentInParent<Canvas>() : null;
            Canvas root = canvas != null ? canvas.rootCanvas : null;
            return root != null && root.transform is RectTransform rootRect ? rootRect : canvasRoot;
        }

        // The local player's report slot: Upgrades records its ledger under the same index.
        // The preview names the slot its sample put the ledger on.
        private static int GetLocalReportSlot(ContractPerformanceReportData report)
        {
            if (report != null && report.IsPreview)
                return report.PreviewLocalSlot;

            PlayerControllerB local = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            if (local == null && StartOfRound.Instance != null)
                local = StartOfRound.Instance.localPlayerController;
            return local != null && local.playerClientId <= int.MaxValue ? (int)local.playerClientId : -1;
        }

        private static TextMeshProUGUI GetCapturedNoteLabel(HUDManager hud, int slot)
        {
            TextMeshProUGUI[] notes = hud != null && hud.statsUIElements != null ? hud.statsUIElements.playerNotesText : null;
            if (notes == null || slot < 0 || slot >= notes.Length || notes[slot] == null || !NoteLabelStates.ContainsKey(notes[slot]))
                return null;
            return notes[slot];
        }

        /// <summary>
        /// (Re)builds the panel for <paramref name="slot"/>. False, with no panel on screen, when
        /// the report has no host for it, the slot has no ledger snapshot, or the screen and the
        /// player grid cannot be measured.
        /// </summary>
        private static bool ApplyXpPanel(HUDManager hud, int slot, IEnumerable<PerformanceReportNoteEntry> previewCandidates)
        {
            DestroyXpPanelObject();
            XpPanelHost host = _xpPanelHost;
            if (host == null || host.Row == null || host.ReportRoot == null || host.ScreenRoot == null
                || slot != host.LocalSlot || !_vanillaPanelBounds.HasValue)
                return false;

            TextMeshProUGUI note = GetCapturedNoteLabel(hud, slot);
            if (note == null)
                return false;

            IEnumerable<PerformanceReportNoteEntry> candidates = previewCandidates ?? MoonContractRoundStats.BuildXpNoteCandidates(slot);
            if (!PerformanceReportXpLedger.TryGetPanelSources(slot, candidates, out List<PerformanceReportNoteEntry> sources,
                    out int total, out int hidden))
                return false;

            if (!TryGetLocalRectBounds(host.ScreenRoot, host.ReportRoot, out LocalRectBounds screen)
                || !TryGetPlayerGridBounds(hud, host.ReportRoot, out LocalRectBounds grid))
            {
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.ReportDiag] Your XP panel (#1200) not placed: the screen or the player grid could not be measured " +
                    $"(screen='{DescribeTransform(host.ScreenRoot)}', reportRoot='{DescribeTransform(host.ReportRoot)}').");
                return false;
            }

            var obj = new GameObject(XpPanelName);
            obj.transform.SetParent(host.Row, false);
            RectTransform panel = obj.AddComponent<RectTransform>();
            Image fill = obj.AddComponent<Image>();
            fill.color = XpPanelFillColor;
            fill.raycastTarget = false;
            // The cells' own surfaces: the CRT wash under the frame, both named for the theme pass.
            CreateScanlineOverlay(panel);
            CreateCellFrameBorders(panel);

            // The Notes type at the Notes size. The Shift total's label is also the ruler every
            // entry is measured with, so what is measured is exactly what is drawn.
            float entryFontSize = ReportLayout.NoteFontSizeMax * host.NoteUnitScale;
            TextMeshProUGUI totalLabel = CreateXpPanelText(panel, note, XpPanelLabelName,
                PerformanceReportXpPanelPlanner.ShiftTotalLabel, entryFontSize, TextAlignmentOptions.TopLeft, XpPanelLabelColor);
            Func<string, float> measure = value => totalLabel.GetPreferredValues(value, float.PositiveInfinity, float.PositiveInfinity).x;
            float lineHeight = totalLabel.GetPreferredValues("Ag", float.PositiveInfinity, float.PositiveInfinity).y;
            float twoLineHeight = totalLabel.GetPreferredValues("Ag\nAg", float.PositiveInfinity, float.PositiveInfinity).y;
            TextMeshProUGUI title = CreateXpPanelText(panel, host.HeadingTemplate != null ? host.HeadingTemplate : note,
                HeadingTextName, XpPanelTitle, ReportLayout.HeadingFontSize, TextAlignmentOptions.TopLeft, CellChromeColor);
            float titleHeight = title.GetPreferredValues(XpPanelTitle, float.PositiveInfinity, float.PositiveInfinity).y;

            float amountWidth = measure(PerformanceReportXpPanelPlanner.FormatAmount(total));
            amountWidth = Mathf.Max(amountWidth, measure(PerformanceReportXpPanelPlanner.FormatAmount(Mathf.Max(0, total - hidden))));
            foreach (PerformanceReportNoteEntry source in sources)
                amountWidth = Mathf.Max(amountWidth, measure(PerformanceReportXpPanelPlanner.FormatAmount(source.Amount)));

            // Beside the report when it fits, narrowing the label column down to a floor; past
            // that (More Company's wide panel on a narrow screen) pinned to the screen's right
            // edge over the report, with the note labels pulled back to its left.
            float pad = ReportLayout.XpPanelPadding;
            float chrome = 2f * pad + ReportLayout.XpPanelAmountGap + amountWidth;
            float desiredWidth = host.NoteLineWidth + chrome;
            float minWidth = host.NoteLineWidth * ReportLayout.XpPanelMinLabelWidthFraction + chrome;
            float reportRight = Mathf.Max(_vanillaPanelBounds.Value.MaxX, grid.MaxX);
            float left = reportRight + ReportLayout.XpPanelGap;
            float screenRight = screen.MaxX - ReportLayout.XpPanelScreenMargin;
            float minX;
            float maxX;
            if (screenRight - left >= desiredWidth)
            {
                minX = left;
                maxX = left + desiredWidth;
                _xpPanelPlacement = "beside";
            }
            else if (screenRight - left >= minWidth)
            {
                minX = left;
                maxX = screenRight;
                _xpPanelPlacement = "beside-narrowed";
            }
            else
            {
                maxX = screenRight;
                minX = Mathf.Max(screen.MinX + ReportLayout.XpPanelScreenMargin, screenRight - desiredWidth);
                _xpPanelPlacement = "over-report";
                _xpPanelNoteLimitX = minX - ReportLayout.XpPanelGap;
            }

            float maxY = Mathf.Min(grid.MaxY, screen.MaxY - ReportLayout.XpPanelScreenMargin);
            float minY = Mathf.Max(grid.MinY, screen.MinY + ReportLayout.XpPanelScreenMargin);
            if (maxX - minX < 1f || maxY - minY < 1f)
            {
                UnityEngine.Object.Destroy(obj);
                _xpPanelPlacement = null;
                _xpPanelNoteLimitX = null;
                return false;
            }

            PlaceXpPanelRect(panel, host, minX, minY, maxX, maxY);
            float width = panel.sizeDelta.x;
            float height = panel.sizeDelta.y;
            float labelWidth = Mathf.Max(1f, Mathf.Min(host.NoteLineWidth, width - chrome));

            var wraps = new Dictionary<string, PerformanceReportXpPanelLabel>(StringComparer.Ordinal);
            Func<string, PerformanceReportXpPanelLabel> wrap = label =>
            {
                if (!wraps.TryGetValue(label, out PerformanceReportXpPanelLabel wrapped))
                {
                    wrapped = PerformanceReportXpPanelPlanner.Wrap(label, labelWidth, measure);
                    wraps[label] = wrapped;
                }

                return wrapped;
            };
            float totalTop = height - pad - lineHeight;
            float entriesTop = pad + titleHeight + ReportLayout.XpPanelTitleGap;
            PerformanceReportXpPanelPlan plan = PerformanceReportXpPanelPlanner.Plan(
                sources,
                total,
                hidden,
                entry => (wrap(entry.Label).LineCount > 1 ? twoLineHeight : lineHeight) + ReportLayout.XpPanelEntryGap,
                totalTop - ReportLayout.XpPanelTotalRuleGap - entriesTop);

            PlaceXpPanelText(title, pad, pad, width - 2f * pad, titleHeight);
            float amountX = width - pad - amountWidth - 1f;
            float y = entriesTop;
            foreach (PerformanceReportNoteEntry entry in plan.Entries)
            {
                PerformanceReportXpPanelLabel wrapped = wrap(entry.Label);
                float rowHeight = wrapped.LineCount > 1 ? twoLineHeight : lineHeight;
                PlaceXpPanelText(
                    CreateXpPanelText(panel, note, XpPanelLabelName, wrapped.Text, entryFontSize, TextAlignmentOptions.TopLeft, XpPanelLabelColor),
                    pad, y, labelWidth + 1f, rowHeight);
                PlaceXpPanelText(
                    CreateXpPanelText(panel, note, XpPanelAmountName, PerformanceReportXpPanelPlanner.FormatAmount(entry.Amount),
                        entryFontSize, TextAlignmentOptions.TopRight, XpPanelAmountColor),
                    amountX, y, amountWidth + 1f, lineHeight);
                y += rowHeight + ReportLayout.XpPanelEntryGap;
            }

            float ruleY = -(totalTop - ReportLayout.XpPanelTotalRuleGap * 0.5f);
            CreateBorderLine(
                panel,
                XpPanelRuleName,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(pad, ruleY - ReportLayout.XpPanelRuleThickness * 0.5f),
                new Vector2(-pad, ruleY + ReportLayout.XpPanelRuleThickness * 0.5f),
                VanillaBottomRowRuleColor,
                PerformanceReportSurfaceSprites.GetDistressSprite(horizontal: true));
            PlaceXpPanelText(totalLabel, pad, totalTop, labelWidth + 1f, lineHeight);
            PlaceXpPanelText(
                CreateXpPanelText(panel, note, XpPanelAmountName, PerformanceReportXpPanelPlanner.FormatAmount(plan.Total),
                    entryFontSize, TextAlignmentOptions.TopRight, XpPanelAmountColor),
                amountX, totalTop, amountWidth + 1f, lineHeight);

            _xpPanel = panel;
            var bounds = new LocalRectBounds();
            bounds.Encapsulate(new Vector3(minX, minY, 0f));
            bounds.Encapsulate(new Vector3(maxX, maxY, 0f));
            _xpPanelBounds = bounds;
            if (_xpPanelNoteLimitX.HasValue)
                ClampPlayerNotesToPanel(hud, host.ReportRoot);

            ContractsBootstrap.Log?.LogDebug(
                "[MoonContracts.ReportDiag] Your XP panel (#1200) placed: " +
                $"slot={slot}, placement={_xpPanelPlacement}, bounds={FormatBounds(bounds)}, screen={FormatBounds(screen)}, " +
                $"grid={FormatBounds(grid)}, reportRightX={reportRight:0.0}, noteLineWidth={host.NoteLineWidth:0.0}, " +
                $"labelWidth={labelWidth:0.0}, amountWidth={amountWidth:0.0}, listed={plan.Listed.Count}/{sources.Count}, " +
                $"otherWork={plan.OtherWork}, hidden={plan.Hidden}, shiftTotal={plan.Total}.");
            return true;
        }

        /// <summary>The names, state icons and Notes of every live slot, in report-root units.</summary>
        private static bool TryGetPlayerGridBounds(HUDManager hud, RectTransform coordinateRoot, out LocalRectBounds grid)
        {
            grid = new LocalRectBounds();
            EndOfGameStatUIElements ui = hud != null ? hud.statsUIElements : null;
            if (ui == null || coordinateRoot == null)
                return false;

            EncapsulateActiveGraphics(ui.playerNamesText, coordinateRoot, ref grid);
            EncapsulateActiveGraphics(ui.playerNotesText, coordinateRoot, ref grid);
            EncapsulateActiveGraphics(ui.playerStates, coordinateRoot, ref grid);
            return grid.HasAny && IsReasonableBounds(grid, coordinateRoot);
        }

        private static void EncapsulateActiveGraphics<T>(T[] graphics, RectTransform coordinateRoot, ref LocalRectBounds bounds)
            where T : Graphic
        {
            if (graphics == null)
                return;

            for (int i = 0; i < graphics.Length; i++)
            {
                if (graphics[i] != null && graphics[i].gameObject.activeInHierarchy)
                    TryEncapsulateRect(graphics[i].rectTransform, coordinateRoot, ref bounds);
            }
        }

        // The row is an unscaled child of the report root, so report-root units are the row's
        // and the panel's; the corners still go through the transforms so a pivot or a moved
        // row cannot put the panel somewhere else.
        private static void PlaceXpPanelRect(RectTransform panel, XpPanelHost host, float minX, float minY, float maxX, float maxY)
        {
            Vector3 localMin = host.Row.InverseTransformPoint(host.ReportRoot.TransformPoint(new Vector3(minX, minY, 0f)));
            Vector3 localMax = host.Row.InverseTransformPoint(host.ReportRoot.TransformPoint(new Vector3(maxX, maxY, 0f)));
            panel.anchorMin = new Vector2(0.5f, 0.5f);
            panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = Vector2.zero;
            panel.anchoredPosition = new Vector2(localMin.x, localMin.y) - host.Row.rect.center;
            panel.sizeDelta = new Vector2(localMax.x - localMin.x, localMax.y - localMin.y);
        }

        private static TextMeshProUGUI CreateXpPanelText(
            RectTransform panel,
            TextMeshProUGUI template,
            string name,
            string value,
            float fontSize,
            TextAlignmentOptions alignment,
            Color color)
        {
            TextMeshProUGUI text = CreateText(panel, template, name, value, 0f, 1f, 0f, 1f, fontSize, alignment);
            // Measured and laid out by the panel, so TMP neither resizes, wraps nor cuts.
            text.enableAutoSizing = false;
            text.fontSize = fontSize;
            text.richText = false;
            text.lineSpacing = ReportLayout.NoteLineSpacing;
            text.overflowMode = TextOverflowModes.Overflow;
            text.margin = Vector4.zero;
            text.color = color;
            text.rectTransform.pivot = new Vector2(0f, 1f);
            return text;
        }

        // Top-left placement inside the panel, whose own pivot is its bottom-left corner.
        private static void PlaceXpPanelText(TMP_Text text, float x, float top, float width, float height)
        {
            RectTransform rect = text.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -top);
            rect.sizeDelta = new Vector2(Mathf.Max(1f, width), Mathf.Max(1f, height));
        }

        private static void AppendXpPanelDump(StringBuilder builder)
        {
            builder.Append('\n');
            if (_xpPanel != null && _xpPanelBounds.HasValue)
            {
                builder.Append(GetTransformPath(_xpPanel)).Append(" (#1200 Your XP panel)")
                    .Append(" active=").Append(_xpPanel.gameObject.activeInHierarchy ? "Y" : "N")
                    .Append(" bounds=").Append(FormatBounds(_xpPanelBounds.Value))
                    .Append(" placement=").Append(_xpPanelPlacement);
            }
            else
            {
                builder.Append("#1200 Your XP panel: not shown");
            }
        }

        private static void DestroyXpPanelObject()
        {
            if (_xpPanel != null)
                UnityEngine.Object.Destroy(_xpPanel.gameObject);
            _xpPanel = null;
            _xpPanelBounds = null;
            _xpPanelPlacement = null;
            _xpPanelNoteLimitX = null;
        }

        /// <summary>Drops the panel and this report's host for it. Part of <see cref="Restore"/>.</summary>
        private static void ClearXpPanel()
        {
            DestroyXpPanelObject();
            _xpPanelHost = null;
        }
    }
}
