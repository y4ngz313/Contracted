namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// The tunables the bottom-row cell solver reads. They live as named constants on
    /// <c>ContractPerformanceReportUi.ReportLayout</c> (one place to tune the row) and are
    /// handed to this solver as data so the geometry itself stays free of Unity and can be
    /// exercised by <c>tests/PerformanceReportRegression</c>.
    /// </summary>
    internal readonly struct ReportCellLayoutTuning
    {
        /// <summary>Width of a 1x cell per unit of its own height - the cell's aspect.</summary>
        internal readonly float CellWidthPerCellHeight;
        /// <summary>How much wider the GRADE cell is than a 1x cell.</summary>
        internal readonly float GradeWidthMultiplier;
        internal readonly float Gap;
        internal readonly float GapMaxRowHeightFraction;
        internal readonly float GapMaxRowWidthFraction;
        internal readonly float VerticalMarginMin;
        internal readonly float VerticalMarginRowFraction;

        internal ReportCellLayoutTuning(
            float cellWidthPerCellHeight,
            float gradeWidthMultiplier,
            float gap,
            float gapMaxRowHeightFraction,
            float gapMaxRowWidthFraction,
            float verticalMarginMin,
            float verticalMarginRowFraction)
        {
            CellWidthPerCellHeight = cellWidthPerCellHeight;
            GradeWidthMultiplier = gradeWidthMultiplier;
            Gap = gap;
            GapMaxRowHeightFraction = gapMaxRowHeightFraction;
            GapMaxRowWidthFraction = gapMaxRowWidthFraction;
            VerticalMarginMin = verticalMarginMin;
            VerticalMarginRowFraction = verticalMarginRowFraction;
        }
    }

    /// <summary>
    /// One cell's box as fractions of the row rect, ready to be used as anchors.
    /// </summary>
    internal readonly struct ReportCellRect
    {
        internal readonly float MinX;
        internal readonly float MinY;
        internal readonly float MaxX;
        internal readonly float MaxY;

        internal ReportCellRect(float minX, float minY, float maxX, float maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }
    }

    /// <summary>
    /// A row's horizontal placement after its preferred bounds are fitted to the visible
    /// canvas. This is pure geometry so the regression suite can exercise the same clamp used
    /// by the Unity report presenter.
    /// </summary>
    internal readonly struct ReportRowPlacement
    {
        internal readonly float MinX;
        internal readonly float MaxX;
        internal readonly float Width;
        internal readonly float CenterX;

        internal ReportRowPlacement(float minX, float maxX, float width, float centerX)
        {
            MinX = minX;
            MaxX = maxX;
            Width = width;
            CenterX = centerX;
        }
    }


    /// <summary>
    /// #1204: where the bottom-row cells go, as a pure function of the row's own rect.
    ///
    /// The old solver derived cell width from the row HEIGHT (height * aspect), shrank the
    /// block only when it overflowed the row, and centred whatever came out. Width therefore
    /// followed height, not the row: a five-cell round - one fewer modifier than a six-cell
    /// one - left the same row width to five narrower-than-needed cells and rendered as a
    /// small block floating in the middle of the report.
    ///
    /// Here the cells always span the row: the row's width minus its gaps is divided among
    /// the cells by their width multipliers, so five cells and six cells both reach both
    /// edges and only the individual cell gets wider or narrower. Height is the dependent
    /// term - a cell is as tall as its own width allows (<see cref="ReportCellLayoutTuning.CellWidthPerCellHeight"/>),
    /// capped by the row's inset band so it can never spill over the band plate, and centred
    /// in the band. That makes the proportion identical on the measured band plate, on the
    /// fallback anchors and under More Company's wider panel, because every one of them is
    /// only a different row rect.
    /// </summary>
    internal static class PerformanceReportCellLayout
    {
        /// <summary>
        /// Fits preferred horizontal bounds to the visible viewport (already inset by its
        /// margin). The result keeps the preferred span when it fits, falls back to the full
        /// viewport when it does not overlap, and clamps both width and center so the row
        /// cannot overrun either edge.
        /// </summary>
        internal static bool TryFitRowToViewport(
            float preferredMinX,
            float preferredMaxX,
            float viewportMinX,
            float viewportMaxX,
            float rowWidthMin,
            float rowWidthMax,
            out ReportRowPlacement placement)
        {
            placement = default;
            float minX = preferredMinX > viewportMinX ? preferredMinX : viewportMinX;
            float maxX = preferredMaxX < viewportMaxX ? preferredMaxX : viewportMaxX;
            if (maxX <= minX)
            {
                minX = viewportMinX;
                maxX = viewportMaxX;
            }

            float viewportWidth = viewportMaxX - viewportMinX;
            if (viewportWidth <= 0f)
                return false;

            float width = Clamp(maxX - minX, rowWidthMin, rowWidthMax);
            if (width > viewportWidth)
                width = viewportWidth;

            float minCenterX = viewportMinX + width * 0.5f;
            float maxCenterX = viewportMaxX - width * 0.5f;
            float centerX = Clamp((minX + maxX) * 0.5f, minCenterX, maxCenterX);
            placement = new ReportRowPlacement(centerX - width * 0.5f, centerX + width * 0.5f, width, centerX);
            return true;
        }

        /// <summary>
        /// Converts a desired local centre into the anchored-position coordinate used by a
        /// centred anchor pair when the parent pivot is not centred.
        /// </summary>
        internal static float AnchoredPositionForLocalCenter(
            float localCenter,
            float parentSize,
            float parentPivot)
        {
            float parentRectCenter = (0.5f - parentPivot) * parentSize;
            return localCenter - parentRectCenter;
        }

        /// <summary>
        /// The box for cell <paramref name="index"/> of <paramref name="cellCount"/>, in row
        /// fractions. <paramref name="gradeIndex"/> is the GRADE cell's position, or a value
        /// outside the row when there is none.
        /// </summary>
        internal static ReportCellRect Solve(
            in ReportCellLayoutTuning tuning,
            float rowWidth,
            float rowHeight,
            int cellCount,
            int gradeIndex,
            int index)
        {
            int count = cellCount > 1 ? cellCount : 1;
            index = index < 0 ? 0 : (index > count - 1 ? count - 1 : index);
            float width = rowWidth > 1f ? rowWidth : 1f;
            float height = rowHeight > 1f ? rowHeight : 1f;

            float gap = GapFor(tuning, width, height, count);
            float totalMultiplier = MultiplierBefore(tuning, count, gradeIndex);
            float unit = totalMultiplier > 0f
                ? (width - gap * (count - 1)) / totalMultiplier
                : width;
            if (unit < 1f)
                unit = 1f;

            float cellLeft = unit * MultiplierBefore(tuning, index, gradeIndex) + gap * index;
            float cellWidth = unit * (index == gradeIndex ? tuning.GradeWidthMultiplier : 1f);

            float band = BandHeight(tuning, height);
            float natural = tuning.CellWidthPerCellHeight > 0f ? unit / tuning.CellWidthPerCellHeight : band;
            float cellHeight = natural > 0f && natural < band ? natural : band;
            float minY = (height - cellHeight) * 0.5f / height;

            return new ReportCellRect(
                Clamp01(cellLeft / width),
                Clamp01(minY),
                Clamp01((cellLeft + cellWidth) / width),
                Clamp01(1f - minY));
        }

        /// <summary>
        /// The row height at which a row <paramref name="rowWidth"/> wide holds cells at
        /// their natural aspect - the tallest the row is worth being. Taller than this and
        /// <see cref="Solve"/> caps the cells and leaves band margin above and below them;
        /// shorter and the cells stay full-width but flatter. Returns 0 when the row holds
        /// no cells, which is the caller's signal to keep its own default.
        ///
        /// Both the gap and the vertical margin depend on the row height they help decide,
        /// so this settles them by substitution; three passes are far past convergence for
        /// terms that are each bounded by a fraction of the height.
        /// </summary>
        internal static float RowHeightForWidth(
            in ReportCellLayoutTuning tuning,
            float rowWidth,
            int cellCount,
            int gradeIndex)
        {
            if (cellCount <= 0 || tuning.CellWidthPerCellHeight <= 0f)
                return 0f;

            float width = rowWidth > 1f ? rowWidth : 1f;
            float totalMultiplier = MultiplierBefore(tuning, cellCount, gradeIndex);
            if (totalMultiplier <= 0f)
                return 0f;

            float height = width;
            for (int pass = 0; pass < 3; pass++)
            {
                float gap = GapFor(tuning, width, height, cellCount);
                float unit = (width - gap * (cellCount - 1)) / totalMultiplier;
                if (unit < 1f)
                    unit = 1f;

                float cellHeight = unit / tuning.CellWidthPerCellHeight;
                height = cellHeight + 2f * VerticalMargin(tuning, height);
            }

            return height;
        }

        /// <summary>
        /// The height available to a cell inside the row: the row minus the margin that keeps
        /// the frames off the band plate's own edges, top and bottom.
        /// </summary>
        internal static float BandHeight(in ReportCellLayoutTuning tuning, float rowHeight)
        {
            float height = rowHeight > 1f ? rowHeight : 1f;
            float band = height - 2f * VerticalMargin(tuning, height);
            return band > 1f ? band : height;
        }

        private static float VerticalMargin(in ReportCellLayoutTuning tuning, float rowHeight)
        {
            float byFraction = rowHeight * tuning.VerticalMarginRowFraction;
            return byFraction > tuning.VerticalMarginMin ? byFraction : tuning.VerticalMarginMin;
        }

        /// <summary>
        /// The gap between two cells: the authored gap, never more than a fraction of the row
        /// height (a short row gets a proportionally tighter gap) and never more than a
        /// fraction of the row width in total, so a narrow row spends its width on cells
        /// rather than on air.
        /// </summary>
        private static float GapFor(in ReportCellLayoutTuning tuning, float rowWidth, float rowHeight, int cellCount)
        {
            if (cellCount < 2)
                return 0f;

            float gap = tuning.Gap;
            float byHeight = rowHeight * tuning.GapMaxRowHeightFraction;
            if (byHeight < gap)
                gap = byHeight;
            if (gap < 0f)
                gap = 0f;

            float ceiling = rowWidth * tuning.GapMaxRowWidthFraction;
            if (gap * (cellCount - 1) > ceiling)
                gap = ceiling / (cellCount - 1);

            return gap;
        }

        /// <summary>
        /// Total width multiplier of the first <paramref name="cells"/> cells. Called with the
        /// cell count it is the row's total; called with an index it is everything left of
        /// that cell.
        /// </summary>
        private static float MultiplierBefore(in ReportCellLayoutTuning tuning, int cells, int gradeIndex)
        {
            if (cells <= 0)
                return 0f;

            return gradeIndex >= 0 && gradeIndex < cells
                ? cells - 1f + tuning.GradeWidthMultiplier
                : cells;
        }

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }
    }
}
