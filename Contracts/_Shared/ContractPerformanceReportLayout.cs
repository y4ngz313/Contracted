using UnityEngine;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #553: every tunable number the bottom-row ledger lays out with. These were inline
    /// literals scattered across the row placement, the cell solver and the per-band text
    /// builder, which made the row impossible to resize without hunting them all down.
    /// Nothing here is a color - hues come from <see cref="UiTheme"/> only.
    /// </summary>
    internal static class ReportLayout
    {
        // --- Row band -------------------------------------------------------------
        // The row grows DOWNWARD from the top of the measured vanilla bottom band into
        // the panel's lower margin. It must never grow upward: the vanilla animator owns
        // the player name/checkmark boxes directly above and we do not move them.
        internal const float RowHeightMin = 56f;
        internal const float RowHeightMax = 110f;
        internal const float RowWidthMin = 160f;
        internal const float RowWidthMax = 1200f;
        internal const float RowTopPadding = 2f;
        internal const float RowPanelBottomPadding = 4f;
        // #747 (Lawson, 2026-09-03): the cell frames sat on, or just past, the band plate's
        // edges. With the plate measured, the row is inset from it on all four sides so
        // every frame sits visibly inside the drawn art. The insets are fractions of the
        // plate (with a floor in units) so More Company's wider panel keeps the proportion,
        // and the row height is capped to the inset plate - RowHeightMin is the fallback
        // path's floor, not permission to overshoot the art.
        internal const float RowPlateInsetXFraction = 0.035f;
        internal const float RowPlateInsetXMin = 8f;
        internal const float RowPlateInsetYFraction = 0.12f;
        internal const float RowPlateInsetYMin = 4f;
        // #747: there is deliberately no "widen the row when the panel is narrow" knob
        // any more. The row is exactly the panel's width; when the panel is narrower than
        // the cells would like, the cells shrink to fit. Growing the row past the panel
        // is what hung the strip outside the report.

        // --- Cell block -----------------------------------------------------------
        // #1204: the cells span the row, always. CellWidthPerRowHeight * CellWidthScale
        // is the cell's ASPECT - its width per unit of its own height - not its width:
        // PerformanceReportCellLayout divides the row's width between the cells and lets
        // that aspect cap the height instead. Before this, width was derived from the row
        // HEIGHT and only ever shrunk to fit, so a five-cell round - one modifier fewer
        // than a six-cell one - drew a small centred block instead of a full row.
        internal const float CellWidthPerRowHeight = 0.92f;
        internal const float CellWidthScale = 1.45f;
        internal const float CellGap = 15f;
        internal const float CellGapMaxRowHeightFraction = 0.2f;
        // Gaps never take more than this share of the row's width: on a narrow panel the
        // width belongs to the cells, not to the air between them.
        internal const float CellGapMaxRowWidthFraction = 0.22f;
        internal const float GradeCellWidthMultiplier = 1.85f;
        internal const float CellVerticalMarginMin = 2f;
        internal const float CellVerticalMarginRowFraction = 0.04f;

        internal static float CellWidthForRowHeight(float rowHeight)
        {
            return Mathf.Max(1f, rowHeight) * CellWidthPerRowHeight * CellWidthScale;
        }

        /// <summary>
        /// The numbers above handed to the pure solver. CellWidthForRowHeight(1f) IS the
        /// aspect, so the ratio keeps exactly one definition.
        /// </summary>
        internal static readonly ReportCellLayoutTuning CellLayout = new ReportCellLayoutTuning(
            cellWidthPerCellHeight: CellWidthForRowHeight(1f),
            gradeWidthMultiplier: GradeCellWidthMultiplier,
            gap: CellGap,
            gapMaxRowHeightFraction: CellGapMaxRowHeightFraction,
            gapMaxRowWidthFraction: CellGapMaxRowWidthFraction,
            verticalMarginMin: CellVerticalMarginMin,
            verticalMarginRowFraction: CellVerticalMarginRowFraction);

        // --- Cell anatomy (normalized inside one cell) ----------------------------
        // Three bands, always in this order: small heading, the primary value, and a
        // qualifier that states the fact behind that value. #556: the qualifier is no
        // longer fine print - it renders at the primary's own weight, so the bottom band
        // is as tall as the middle one and the row reads as one family of cells.
        // #747 (Lawson, 2026-09-03): every band sits inside the frame's rule with a clear
        // margin - the vanilla rule is about 3.5 units on a 60-unit cell, so 10% each way
        // keeps text off the lines - and the heading and the qualifier are the same size,
        // the two small statements above and below one dominant figure.
        internal const float BandInsetX = 0.10f;
        internal const float HeadingMinY = 0.70f;
        internal const float HeadingMaxY = 0.90f;
        internal const float PrimaryMinY = 0.36f;
        internal const float PrimaryMaxY = 0.68f;
        internal const float QualifierMinY = 0.10f;
        internal const float QualifierMaxY = 0.34f;
        // PrimaryMinY is the shared baseline, including cells without a qualifier.
        // TMP's Baseline alignment uses the centre of its text rect, so its lower edge
        // is mirrored below that baseline. The visible glyphs occupy the primary band;
        // the larger quota/grade figures no longer drop toward the bottom of the frame.

        internal const float HeadingFontSize = 10f;
        internal const float PrimaryFontSize = 14f;
        // The heading's size, not the primary's: the top and bottom lines of a cell are
        // the same weight, and the figure between them is the only thing that is larger.
        internal const float QualifierFontSize = HeadingFontSize;
        internal const float PrimaryFontSizeRevisedQuota = 24f;
        internal const float PrimaryFontSizeGrade = 26f;
        internal const float TextAutoSizeMin = 4.5f;

        // #1204: the band sizes above are authored for a cell about FontCellHeightReference
        // tall. A cell now takes its size from the row rather than the other way round, so
        // the type rides with it - bounded both ways, because a bigger cell is not a reason
        // to shout and a shorter one still has the autosize floor underneath it.
        internal const float FontCellHeightReference = 64f;
        internal const float FontScaleMin = 0.85f;
        internal const float FontScaleMax = 1.6f;

        internal static float FontScaleForCellHeight(float cellHeight)
        {
            return Mathf.Clamp(cellHeight / FontCellHeightReference, FontScaleMin, FontScaleMax);
        }

        // #874 S6: the note range follows the height of the note's own rect, in the units
        // TMP measures fontSize in. MoreCompany widens the roster but never shortens a slot,
        // so its slots keep the vanilla [4.2, 10.5]; only a genuinely shorter rect shrinks.
        internal static Vector2 NoteFontRangeForSlotHeight(float noteRectHeight)
        {
            float scale = Mathf.Clamp(noteRectHeight / NoteFontReferenceSlotHeight, NoteFontScaleMin, 1f);
            return new Vector2(NoteFontSizeMin, Mathf.Max(NoteFontSizeMin, NoteFontSizeMax * scale));
        }

        // The revised-quota cell keeps its up/down arrow beside the primary value.
        internal const float RevisedQuotaPrimaryMaxX = 0.74f;
        internal static readonly Vector2 RevisedQuotaArrowAnchorMin = new Vector2(0.78f, 0.3f);
        internal static readonly Vector2 RevisedQuotaArrowAnchorMax = new Vector2(0.94f, 0.68f);

        // --- Connectors -----------------------------------------------------------
        // ASCII on purpose: the vanilla report font is a bitmap terminal face and we
        // cannot verify a typographic chevron is in its atlas from outside the game.
        // #747 (Lawson, 2026-09-03): there is no single chevron between two modifier
        // steps any more - the framed cells already read as one chain and the glyphs
        // cluttered the gaps. The result cell now stands on its own as well.
        internal const string ConnectorResult = "";
        internal const float ConnectorFontSize = 11f;
        internal const float ConnectorMinY = 0.3f;
        internal const float ConnectorMaxY = 0.68f;
        // The bare gap between two cells is only a dozen pixels wide, which would autosize
        // the glyph down to nothing. The connector is allowed to bleed a little into the
        // cells either side; their own text is inset far enough that nothing collides.
        internal const float ConnectorBleedRowFraction = 0.012f;

        // --- Row surfaces (#556) --------------------------------------------------
        // The opaque scrub mask hides the vanilla CRT art behind the row, so the strip
        // has to draw the scanline wash itself or it reads as a hole in the panel.
        internal const float ScanlineOverlayAlpha = 0.16f;
        internal const float BackingOpacityMultiplier = 0.94f;

        // --- Notes column (#555) --------------------------------------------------
        internal const float NoteFontSizeMin = 4.2f;
        internal const float NoteFontSizeMax = 10.5f;
        // The vanilla authored PlayerSlotN/Notes rect is 50.8 tall (55.8 after ReclaimNoteRectPadding,
        // which is the rect both note writers read; ReportDiag dump, 2026-09-25). 50 leaves a rounding
        // band under the authored height so a rect a hair short of it still gets the full range.
        internal const float NoteFontReferenceSlotHeight = 50f;
        internal const float NoteFontScaleMin = 0.5f;
        internal const float NoteLineSpacing = -3f;
        internal const float NoteRectSideReclaim = 2f;
        internal const float NoteRectTopReclaim = 5f;
        // #556: the bottom edge is never reclaimed, and the label keeps a margin inside
        // it, so the last wrapped line cannot land on the vanilla box's boundary rule.
        // The cap stays at the derived range's maximum - the fix is space, not smaller
        // type below the range's readable floor.
        internal const float NoteBottomMargin = 4f;
        // Ceiling on how far the reclaim may move an edge away from the label's own
        // authored offsets. It bounds the delta, not the absolute offset: the label's
        // starting inset is whatever the game (or another HUD mod) authored, and clamping
        // the absolute value would drag a legitimately larger inset back to this number.
        internal const float NoteRectMaxReclaim = 8f;
        // #747 (Lawson, 2026-09-03): a note ran past the panel's right edge. The vanilla
        // note rects overhang the drawn panel the way the BGBoxes rect does, and a wrapped
        // line may run to the rect, so every note label's right edge is pulled back to
        // this margin inside the drawn panel once the panel has been measured.
        internal const float NotePanelRightMargin = 6f;

        // --- Your XP panel (#1200) ------------------------------------------------
        // In report-root units. The panel sits this far right of the drawn panel and keeps
        // this margin inside the screen; its text is the Notes type at the Notes size.
        internal const float XpPanelGap = 8f;
        internal const float XpPanelScreenMargin = 8f;
        // #874 S6: the bottom row keeps this margin inside the camera viewport at both edges.
        internal const float RowScreenMargin = 24f;
        internal const float XpPanelPadding = 7f;
        internal const float XpPanelTitleGap = 4f;
        internal const float XpPanelEntryGap = 2f;
        internal const float XpPanelAmountGap = 6f;
        internal const float XpPanelTotalRuleGap = 5f;
        internal const float XpPanelRuleThickness = 1f;
        // Beside the report the label column may give up this share of the Notes line
        // width to stay on screen. Below it there is no room beside the report, and the
        // panel is pinned to the screen's right edge over the report instead, with the
        // note labels pulled back to its left.
        internal const float XpPanelMinLabelWidthFraction = 0.6f;
        // The Notes lines a slot keeps besides its survival fact: one when the Your XP
        // panel carries the rest, two for a slot without the panel.
        internal const int XpNoteLinesWithPanel = 1;
        internal const int XpNoteLinesWithoutPanel = 2;
    }
}
