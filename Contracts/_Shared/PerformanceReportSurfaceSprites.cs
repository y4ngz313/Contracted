using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Experience.UITheme;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #556: the ledger row is drawn over an opaque scrub mask, so none of the vanilla CRT
    /// art behind it survives. Two vanilla surfaces therefore have to be reproduced by the
    /// row itself, or the bottom strip reads as a clean hole punched in a grimy panel:
    /// the scanline wash across the report, and the distressed (grainy, broken) rules the
    /// vanilla player boxes are outlined with.
    ///
    /// Both prefer the real vanilla asset and fall back to a procedural one. Everything here
    /// is built at most once per process and cached in a static; nothing runs per frame, and
    /// no vanilla asset is ever mutated - the vanilla sprite is referenced, never edited.
    /// </summary>
    internal static class PerformanceReportSurfaceSprites
    {
        private const int ScanlinePeriodPixels = 4;
        private const int DistressStripLengthPixels = 64;
        private const int DistressStripThicknessPixels = 2;
        // Fixed so the grain is the same every round and every session. A grain that
        // re-rolls per report reads as flicker rather than as wear.
        private const int DistressSeed = 0x554E;

        private static bool _primed;
        private static Sprite _vanillaScanlineSprite;
        private static Sprite _vanillaBoxFrameSprite;
        private static Sprite _proceduralScanlineSprite;
        private static Sprite _horizontalDistressSprite;
        private static Sprite _verticalDistressSprite;

        /// <summary>
        /// The 9-sliced sprite the vanilla report outlines its player boxes with, or null
        /// when nothing usable was found. Null is the normal, supported case: the caller
        /// falls back to <see cref="GetDistressSprite"/> on plain border rules.
        /// </summary>
        internal static Sprite VanillaBoxFrameSprite => _vanillaBoxFrameSprite;

        /// <summary>
        /// Looks once for the vanilla art this row wants to match. Safe to call every report;
        /// the search only runs until it has been given a real hierarchy to search.
        /// </summary>
        internal static void PrimeFromVanilla(Transform reportRoot)
        {
            if (_primed || reportRoot == null)
                return;

            _primed = true;
            // Neutralized on the way in. The vanilla art is authored red and we tint it with
            // whatever hue the active theme carries; multiplying an arbitrary hue into red art
            // gives mud. The vanilla sprite itself is only read - never modified.
            _vanillaScanlineSprite = Neutralize(FindVanillaScanlineSprite(reportRoot));
            _vanillaBoxFrameSprite = Neutralize(FindVanillaBoxFrameSprite(reportRoot));
            _vanillaOutlineSheetPixelsPerUnit = GetOutlineSheetPixelsPerUnit(FindVanillaOutlineSheetImage(reportRoot));
            ContractsBootstrap.Log?.LogInfo(
                "[MoonContracts.ReportDiag] Report surface sprite sourcing: " +
                $"scanline={DescribeSprite(_vanillaScanlineSprite)} (fallback=procedural {ScanlinePeriodPixels}px period), " +
                $"boxFrame={DescribeSprite(_vanillaBoxFrameSprite)} at {_vanillaOutlineSheetPixelsPerUnit:0.00}px/unit (fallback=procedural seeded grain).");
        }

        private static float _vanillaOutlineSheetPixelsPerUnit = 1f;

        /// <summary>
        /// The pixelsPerUnitMultiplier that draws a carved box frame's 9-slice border at the
        /// weight vanilla draws the sheet: Image's pixels-per-unit is the sprite's over the
        /// canvas reference, times this, and the border in units is the border in pixels over
        /// that. Solved for the sheet's measured pixels per report unit.
        /// </summary>
        internal static float VanillaBoxFramePixelsPerUnitMultiplier(RectTransform frame)
        {
            if (_vanillaBoxFrameSprite == null || _vanillaOutlineSheetPixelsPerUnit <= 0f)
                return 1f;

            Canvas canvas = frame != null ? frame.GetComponentInParent<Canvas>() : null;
            float reference = canvas != null ? Mathf.Max(1f, canvas.referencePixelsPerUnit) : 100f;
            float spritePixelsPerUnit = Mathf.Max(1f, _vanillaBoxFrameSprite.pixelsPerUnit);
            return _vanillaOutlineSheetPixelsPerUnit * reference / spritePixelsPerUnit;
        }

        /// <summary>The tile for the row-wide CRT wash: vanilla's if we found it, ours if not.</summary>
        internal static Sprite GetScanlineSprite()
        {
            if (_vanillaScanlineSprite != null)
                return _vanillaScanlineSprite;

            if (_proceduralScanlineSprite == null)
                _proceduralScanlineSprite = CreateProceduralScanlineSprite();
            return _proceduralScanlineSprite;
        }

        /// <summary>
        /// A tileable strip of seeded alpha/thickness jitter, used to break up a border rule
        /// so it wears like the vanilla box outlines instead of reading as a clean 2px line.
        /// </summary>
        internal static Sprite GetDistressSprite(bool horizontal)
        {
            if (horizontal)
            {
                if (_horizontalDistressSprite == null)
                    _horizontalDistressSprite = CreateDistressSprite(true);
                return _horizontalDistressSprite;
            }

            if (_verticalDistressSprite == null)
                _verticalDistressSprite = CreateDistressSprite(false);
            return _verticalDistressSprite;
        }

        /// <summary>
        /// Falls back to the source sprite when the neutral copy cannot be made - a sprite on
        /// a compressed or non-blittable texture. A slightly wrong hue beats no surface.
        /// </summary>
        private static Sprite Neutralize(Sprite source)
        {
            if (source == null)
                return null;

            return PerformanceReportThemeMeshEffect.GetNeutralCopy(source) ?? source;
        }

        private static Sprite FindVanillaScanlineSprite(Transform reportRoot)
        {
            Graphic[] graphics = reportRoot.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                Graphic graphic = graphics[i];
                if (graphic == null || IsModOwned(graphic.transform))
                    continue;

                if (graphic is Image image
                    && image.sprite != null
                    && ContainsIgnoreCase(image.sprite.name, "scanline"))
                {
                    return image.sprite;
                }

                // The CRT wash is just as likely to be an unsprited RawImage, in which case
                // its texture is what tiles - wrap it in a sprite of our own rather than
                // touching the RawImage.
                if (graphic is RawImage raw
                    && raw.texture is Texture2D texture
                    && (ContainsIgnoreCase(texture.name, "scanline") || ContainsIgnoreCase(raw.name, "scanline")))
                {
                    return TryWrapTexture(texture);
                }
            }

            return null;
        }

        // #747: the vanilla box outlines are not one sprite each. `EndgameStats/Lines` draws
        // the single `endgameStatsBoxes` sheet (964x864): eight player boxes, the COLLECTED
        // box with its label baked in, and the GRADE label, all on one image stretched over
        // the panel rect. Measured from the texture (2026-09-02): the top box of the right
        // column is columns 356-519 and rows 170-272 from the top, a clean outline with no
        // baked text. That is the box a cell frame is carved from, as fractions of the sheet
        // so a re-exported sheet at another resolution still carves the same box. The rules
        // are about 6 px thick; the 9-slice border keeps a little more than that.
        private const string VanillaBoxSheetSpriteName = "endgameStatsBoxes";
        private const float VanillaBoxSheetLeftFraction = 356f / 964f;
        private const float VanillaBoxSheetWidthFraction = 164f / 964f;
        private const float VanillaBoxSheetBottomFraction = 591f / 864f;
        private const float VanillaBoxSheetHeightFraction = 103f / 864f;
        private const float VanillaBoxBorderFraction = 10f / 164f;

        /// <summary>
        /// One vanilla player-box outline, carved out of the `endgameStatsBoxes` sheet and
        /// given the 9-slice border the sheet lacks. Null when the sheet is not drawn under
        /// the report; the caller then falls back to procedural grain. The previous lookup
        /// searched under "BGBoxes", which is the panel background Image, and so 9-sliced the
        /// panel art as every cell's frame - that is the regression this replaces.
        /// </summary>
        private static Sprite FindVanillaBoxFrameSprite(Transform reportRoot)
        {
            Sprite sheet = FindSpriteByName(reportRoot, VanillaBoxSheetSpriteName);
            if (sheet == null)
            {
                // Info, not Debug: the sourcing line below prints "<none>" for every cause,
                // and a run's log must say which one it was.
                ContractsBootstrap.Log?.LogInfo(
                    "[MoonContracts.ReportDiag] No vanilla outline sheet under " +
                    $"'{(reportRoot != null ? reportRoot.name : "<null>")}': images={DescribeSheetCandidates(reportRoot)}.");
                return null;
            }

            return CarveVanillaBox(sheet);
        }

        /// <summary>
        /// The first vanilla sprite whose name carries the fragment, preferring one we have
        /// not derived ourselves ("_Y4NGZ" in the name marks a trimmed or neutral copy),
        /// so the box is carved from the untouched sheet whenever it is still drawn.
        /// </summary>
        private static Sprite FindSpriteByName(Transform reportRoot, string fragment)
        {
            if (reportRoot == null)
                return null;

            Sprite derived = null;
            Image[] images = reportRoot.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                Image image = images[i];
                if (image == null || image.sprite == null || IsModOwned(image.transform))
                    continue;

                Sprite sprite = image.sprite;
                if (!ContainsIgnoreCase(sprite.name, fragment) || sprite.rect.width < 4f || sprite.rect.height < 4f)
                    continue;

                if (!ContainsIgnoreCase(sprite.name, "_Y4NGZ"))
                    return sprite;
                if (derived == null)
                    derived = sprite;
            }

            return derived;
        }

        private static string DescribeSheetCandidates(Transform reportRoot)
        {
            if (reportRoot == null)
                return "<null root>";

            var names = new List<string>();
            Image[] images = reportRoot.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length && names.Count < 12; i++)
            {
                Image image = images[i];
                if (image != null && !IsModOwned(image.transform))
                    names.Add($"{image.name}:{(image.sprite != null ? image.sprite.name : "<no sprite>")}");
            }

            return names.Count > 0 ? string.Join(", ", names.ToArray()) : "<none>";
        }

        /// <summary>
        /// How thick vanilla's box rules are in report units: about 6 sheet pixels at the
        /// sheet's measured pixels per unit. The procedural fallback rules use this so the
        /// row's lines weigh the same as the panel's whichever frame it ended up with.
        /// </summary>
        internal static float VanillaRuleThicknessUnits()
        {
            const float RulePixels = 6f;
            return _vanillaOutlineSheetPixelsPerUnit > 0.01f ? RulePixels / _vanillaOutlineSheetPixelsPerUnit : 2f;
        }

        /// <summary>
        /// A new Sprite over the box's sub-rect of the sheet's texture, 9-sliced so the
        /// corners and rule weight survive any cell size. The vanilla asset is referenced,
        /// never modified.
        /// </summary>
        private static Sprite CarveVanillaBox(Sprite sheet)
        {
            try
            {
                // Fractions of the whole texture, not of the sprite's rect: the sheet the
                // report is showing may already be the trimmed copy (a sub-rect of the same
                // texture), and the box is where it is in the texture regardless.
                Texture2D texture = sheet.texture;
                if (texture == null)
                {
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.ReportDiag] Vanilla outline sheet '{sheet.name}' has no texture; no box carved.");
                    return null;
                }

                // An untouched sheet is carved by its own rect (an atlas-packed sheet keeps
                // working); a sheet we already trimmed is a sub-rect of the whole texture,
                // and the box is where it is in the texture regardless.
                Rect full = sheet.rect;
                bool wholeTexture = Mathf.Approximately(full.width, texture.width)
                    && Mathf.Approximately(full.height, texture.height);
                float originX = wholeTexture ? full.x : 0f;
                float originY = wholeTexture ? full.y : 0f;
                float sheetWidth = wholeTexture ? full.width : texture.width;
                float sheetHeight = wholeTexture ? full.height : texture.height;
                var box = new Rect(
                    originX + Mathf.Round(sheetWidth * VanillaBoxSheetLeftFraction),
                    originY + Mathf.Round(sheetHeight * VanillaBoxSheetBottomFraction),
                    Mathf.Round(sheetWidth * VanillaBoxSheetWidthFraction),
                    Mathf.Round(sheetHeight * VanillaBoxSheetHeightFraction));
                if (box.width < 8f || box.height < 8f)
                {
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.ReportDiag] Vanilla outline sheet '{sheet.name}' {full.width:0}x{full.height:0} is too small to carve a box from.");
                    return null;
                }

                float border = Mathf.Max(2f, Mathf.Round(box.width * VanillaBoxBorderFraction));
                Sprite carved = Sprite.Create(
                    sheet.texture,
                    box,
                    new Vector2(0.5f, 0.5f),
                    sheet.pixelsPerUnit,
                    0,
                    SpriteMeshType.FullRect,
                    new Vector4(border, border, border, border));
                carved.name = sheet.name + "_Y4NGZBox";
                carved.hideFlags = HideFlags.HideAndDontSave;
                return carved;
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.ReportDiag] Could not carve a box from vanilla sheet '{sheet.name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The vanilla Image drawing the `endgameStatsBoxes` sheet under the report, or null.
        /// Not cached: the row trims and restores it every report.
        /// </summary>
        internal static Image FindVanillaOutlineSheetImage(Transform reportRoot)
        {
            if (reportRoot == null)
                return null;

            Image[] images = reportRoot.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                Image image = images[i];
                if (image != null
                    && image.sprite != null
                    && !IsModOwned(image.transform)
                    && ContainsIgnoreCase(image.sprite.name, VanillaBoxSheetSpriteName))
                {
                    return image;
                }
            }

            return null;
        }

        /// <summary>
        /// The sheet's own pixels per report-root unit as vanilla draws it: the sprite rect
        /// width over the Image rect width. A carved box frame drawn with this as its
        /// pixelsPerUnitMultiplier keeps vanilla's rule weight; without it the 9-slice
        /// border is drawn at canvas scale, about 1.7x heavier on the vanilla layout.
        /// </summary>
        internal static float GetOutlineSheetPixelsPerUnit(Image sheet)
        {
            if (sheet == null || sheet.sprite == null || !(sheet.transform is RectTransform rect) || rect.rect.width <= 0f)
                return 1f;

            return sheet.sprite.rect.width / rect.rect.width;
        }

        private static readonly Dictionary<long, Sprite> OutlineSheetAboveBandBySprite = new Dictionary<long, Sprite>();

        /// <summary>
        /// The sheet with its lower band cut off: everything from <paramref name="cutRowFromBottom"/>
        /// (sprite pixels, bottom-up) to the top. Drawn over a rect shrunk by the same
        /// fraction, the player boxes land exactly where vanilla draws them and the baked
        /// COLLECTED box and GRADE label are simply not there. Cached per sprite and cut;
        /// the vanilla asset is referenced, never modified.
        /// </summary>
        internal static Sprite GetOutlineSheetAboveBand(Sprite sheet, float cutRowFromBottom)
        {
            if (sheet == null)
                return null;

            Rect full = sheet.rect;
            float cut = Mathf.Clamp(Mathf.Round(cutRowFromBottom), 0f, full.height - 1f);
            long key = ((long)sheet.GetInstanceID() << 16) | (long)cut;
            if (OutlineSheetAboveBandBySprite.TryGetValue(key, out Sprite cached) && cached != null)
                return cached;

            try
            {
                Sprite trimmed = Sprite.Create(
                    sheet.texture,
                    new Rect(full.x, full.y + cut, full.width, full.height - cut),
                    new Vector2(0.5f, 0.5f),
                    sheet.pixelsPerUnit,
                    0,
                    SpriteMeshType.FullRect);
                trimmed.name = sheet.name + "_Y4NGZAboveBand";
                trimmed.hideFlags = HideFlags.HideAndDontSave;
                OutlineSheetAboveBandBySprite[key] = trimmed;
                return trimmed;
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.ReportDiag] Could not trim vanilla outline sheet '{sheet.name}': {ex.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------------------------------
        // #747: the drawn extents of the vanilla panel art.
        //
        // `EndgameStats/BGBoxes` draws `endgameStatsBG` (964x864) as a Simple Image over a
        // 559x411.5 rect, and the art does not fill its rect: measured from the texture
        // (2026-09-02) it is three plates of one width - a header plate, the body plate
        // behind the player boxes, and a lower band behind COLLECTED/GRADE whose bottom edge
        // is cut on a slant - inside transparent padding. Every rect-based measurement of
        // the panel therefore overhangs what the player sees. What the ledger row needs is
        // the plates' shared width and the lower band's edges, in sprite pixels, so it can
        // map them through whatever rect the panel Image happens to have (More Company
        // widens that rect; the art stretches with it, and so does this).
        //
        // Measured at runtime from the sprite's own pixels, once per sprite, so a swapped or
        // re-exported panel is measured rather than assumed. The known vanilla values are the
        // fallback when the pixels cannot be read.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Drawn extents of a panel sprite, in pixels of the sprite's rect with the origin at
        /// the rect's bottom-left (Unity texture space). Edges are exclusive on the top and
        /// right, so <c>PlateRight - PlateLeft</c> is the drawn width in pixels.
        /// </summary>
        internal readonly struct VanillaPanelArt
        {
            internal readonly float PlateLeft;
            internal readonly float PlateRight;
            internal readonly float ArtBottom;
            internal readonly float ArtTop;
            /// <summary>Top edge of the lower band plate; equals <see cref="BandArtBottom"/> when there is no separate band.</summary>
            internal readonly float BandTop;
            /// <summary>Lowest row of the band that still spans the full plate width (above the slant).</summary>
            internal readonly float BandFullBottom;
            /// <summary>Lowest drawn row of the band, slant included.</summary>
            internal readonly float BandArtBottom;
            internal readonly string Source;

            internal VanillaPanelArt(
                float plateLeft, float plateRight, float artBottom, float artTop,
                float bandTop, float bandFullBottom, float bandArtBottom, string source)
            {
                PlateLeft = plateLeft;
                PlateRight = plateRight;
                ArtBottom = artBottom;
                ArtTop = artTop;
                BandTop = bandTop;
                BandFullBottom = bandFullBottom;
                BandArtBottom = bandArtBottom;
                Source = source;
            }

            internal bool HasBand => BandTop - BandArtBottom >= 4f && BandFullBottom >= BandArtBottom;

            public override string ToString()
            {
                return $"plateX={PlateLeft:0}-{PlateRight:0} artY={ArtBottom:0}-{ArtTop:0} " +
                    (HasBand ? $"band={BandArtBottom:0}/{BandFullBottom:0}-{BandTop:0}" : "band=<none>") +
                    $" ({Source})";
            }
        }

        private const string VanillaPanelSpriteName = "endgameStatsBG";
        // Measured from the vanilla endgameStatsBG texture, 964x864, bottom-up pixel edges.
        // Plates share columns 60-917; the lower band is rows 76-230 with its full width kept
        // down to row 91; the body plate is rows 246-710 and the header 727-821.
        private static readonly VanillaPanelArt VanillaPanelArtTable =
            new VanillaPanelArt(60f, 918f, 76f, 822f, 231f, 91f, 76f, "vanilla table");

        private const byte PanelAlphaThreshold = 16;
        private const float PanelRowMinimumCoverage = 0.05f;
        private const int PanelRowGapMerge = 4;
        private const int PanelEdgeTolerancePixels = 2;

        private static readonly Dictionary<int, VanillaPanelArt> PanelArtBySprite = new Dictionary<int, VanillaPanelArt>();
        private static readonly Dictionary<int, Sprite> PanelBandSpriteBySprite = new Dictionary<int, Sprite>();

        /// <summary>
        /// The drawn extents of <paramref name="panelSprite"/>, measured once from its pixels
        /// and cached by sprite. False only for a null sprite; a sprite whose pixels cannot be
        /// read gets the vanilla table when it is the vanilla panel, and otherwise its own
        /// <c>textureRect</c> (Unity's alpha-trimmed bounds) with no band.
        /// </summary>
        internal static bool TryGetPanelArt(Sprite panelSprite, out VanillaPanelArt art)
        {
            art = default;
            if (panelSprite == null)
                return false;

            int id = panelSprite.GetInstanceID();
            if (PanelArtBySprite.TryGetValue(id, out art))
                return true;

            if (!TryMeasurePanelArt(panelSprite, out art))
            {
                Rect rect = panelSprite.rect;
                if (ContainsIgnoreCase(panelSprite.name, VanillaPanelSpriteName)
                    && Mathf.Approximately(rect.width, 964f)
                    && Mathf.Approximately(rect.height, 864f))
                {
                    art = VanillaPanelArtTable;
                }
                else
                {
                    Rect trimmed = panelSprite.textureRect;
                    float left = trimmed.x - rect.x;
                    float bottom = trimmed.y - rect.y;
                    art = new VanillaPanelArt(
                        left, left + trimmed.width, bottom, bottom + trimmed.height,
                        bottom, bottom, bottom, "textureRect");
                }
            }

            PanelArtBySprite[id] = art;
            return true;
        }

        /// <summary>
        /// A Sprite over just the lower band plate of <paramref name="panelSprite"/>: the
        /// pixels vanilla draws behind COLLECTED/GRADE, slant included. Drawn Simple into a
        /// rect that maps that band 1:1, it is the panel continued rather than a stretched
        /// copy of the whole padded sprite. Null when the sprite has no band. Cached; the
        /// vanilla asset is referenced, never modified.
        /// </summary>
        internal static Sprite GetPanelBandSprite(Sprite panelSprite)
        {
            if (!TryGetPanelArt(panelSprite, out VanillaPanelArt art) || !art.HasBand)
                return null;

            int id = panelSprite.GetInstanceID();
            if (PanelBandSpriteBySprite.TryGetValue(id, out Sprite cached) && cached != null)
                return cached;

            try
            {
                Rect full = panelSprite.rect;
                var band = new Rect(
                    full.x + art.PlateLeft,
                    full.y + art.BandArtBottom,
                    art.PlateRight - art.PlateLeft,
                    art.BandTop - art.BandArtBottom);
                Sprite sprite = Sprite.Create(
                    panelSprite.texture,
                    band,
                    new Vector2(0.5f, 0.5f),
                    panelSprite.pixelsPerUnit,
                    0,
                    SpriteMeshType.FullRect);
                sprite.name = panelSprite.name + "_Y4NGZBand";
                sprite.hideFlags = HideFlags.HideAndDontSave;
                PanelBandSpriteBySprite[id] = sprite;
                return sprite;
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.ReportDiag] Could not carve the band from vanilla panel sprite '{panelSprite.name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads the sprite's rect back through a temporary RenderTexture (the vanilla
        /// texture is not CPU-readable) and finds the plates: rows are "drawn" when at least
        /// 5% of their pixels pass the alpha threshold, drawn rows within four rows of each
        /// other form one plate, the plate width is the most common row extent, and the
        /// lowest plate is the band. The scanlines baked into the art are far denser than
        /// 5% and never split a plate; a 15-row gap between plates always does.
        /// </summary>
        private static bool TryMeasurePanelArt(Sprite sprite, out VanillaPanelArt art)
        {
            art = default;
            Texture2D texture = sprite != null ? sprite.texture : null;
            Rect rect = sprite != null ? sprite.rect : default;
            int width = Mathf.RoundToInt(rect.width);
            int height = Mathf.RoundToInt(rect.height);
            if (texture == null || width < 8 || height < 8)
                return false;

            RenderTexture previous = RenderTexture.active;
            RenderTexture renderTexture = null;
            Texture2D readable = null;
            try
            {
                renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(
                    texture,
                    renderTexture,
                    new Vector2(rect.width / texture.width, rect.height / texture.height),
                    new Vector2(rect.x / texture.width, rect.y / texture.height));
                RenderTexture.active = renderTexture;
                readable = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                return TryAnalyzePanelPixels(readable.GetPixels32(), width, height, out art);
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.ReportDiag] Could not read vanilla panel sprite '{sprite.name}' pixels: {ex.Message}");
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                if (renderTexture != null)
                    RenderTexture.ReleaseTemporary(renderTexture);
                if (readable != null)
                    UnityEngine.Object.Destroy(readable);
            }
        }

        private static bool TryAnalyzePanelPixels(Color32[] pixels, int width, int height, out VanillaPanelArt art)
        {
            art = default;
            if (pixels == null || pixels.Length < width * height)
                return false;

            int minimumCount = Mathf.Max(1, Mathf.RoundToInt(width * PanelRowMinimumCoverage));
            var rowMin = new int[height];
            var rowMax = new int[height];
            var rowAny = new bool[height];
            var rowOn = new bool[height];
            var extentCounts = new Dictionary<long, int>();
            for (int y = 0; y < height; y++)
            {
                int count = 0;
                int min = int.MaxValue;
                int max = -1;
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (pixels[offset + x].a < PanelAlphaThreshold)
                        continue;
                    count++;
                    if (x < min) min = x;
                    if (x > max) max = x;
                }

                rowAny[y] = count > 0;
                rowOn[y] = count >= minimumCount;
                rowMin[y] = min;
                rowMax[y] = max;
                if (rowOn[y])
                {
                    long key = ((long)min << 32) | (uint)max;
                    extentCounts.TryGetValue(key, out int seen);
                    extentCounts[key] = seen + 1;
                }
            }

            if (extentCounts.Count == 0)
                return false;

            long bestKey = 0;
            int bestCount = -1;
            foreach (KeyValuePair<long, int> pair in extentCounts)
            {
                if (pair.Value > bestCount)
                {
                    bestCount = pair.Value;
                    bestKey = pair.Key;
                }
            }

            int plateLeft = (int)(bestKey >> 32);
            int plateRight = (int)(bestKey & 0xFFFFFFFF) + 1;

            // Runs of drawn rows, bottom-up, merging gaps shorter than a plate gap.
            var runStarts = new List<int>();
            var runEnds = new List<int>();
            int runStart = -1;
            int runEnd = -1;
            for (int y = 0; y < height; y++)
            {
                if (!rowOn[y])
                    continue;
                if (runStart < 0)
                    runStart = y;
                else if (y - runEnd > PanelRowGapMerge)
                {
                    runStarts.Add(runStart);
                    runEnds.Add(runEnd);
                    runStart = y;
                }
                runEnd = y;
            }
            if (runStart >= 0)
            {
                runStarts.Add(runStart);
                runEnds.Add(runEnd);
            }
            if (runStarts.Count == 0)
                return false;

            // The art's outer rows include the slant's thin tip, which fails the coverage
            // cut; extend both outer edges over any drawn row adjoining a plate.
            int artBottom = runStarts[0];
            while (artBottom > 0 && rowAny[artBottom - 1])
                artBottom--;
            int artTop = runEnds[runEnds.Count - 1];
            while (artTop + 1 < height && rowAny[artTop + 1])
                artTop++;
            artTop++;

            if (runStarts.Count < 2)
            {
                art = new VanillaPanelArt(plateLeft, plateRight, artBottom, artTop, artBottom, artBottom, artBottom, "measured, one plate");
                return true;
            }

            int bandStart = runStarts[0];
            int bandEnd = runEnds[0];
            int bandTop = bandEnd + 1;
            int bandFullBottom = -1;
            for (int y = bandStart; y <= bandEnd; y++)
            {
                if (rowMin[y] <= plateLeft + PanelEdgeTolerancePixels
                    && rowMax[y] >= plateRight - 1 - PanelEdgeTolerancePixels)
                {
                    bandFullBottom = y;
                    break;
                }
            }

            if (bandFullBottom < 0)
                bandFullBottom = bandStart;
            art = new VanillaPanelArt(plateLeft, plateRight, artBottom, artTop, bandTop, bandFullBottom, artBottom, $"measured, {runStarts.Count} plates");
            return true;
        }

        private static Sprite CreateProceduralScanlineSprite()
        {
            var texture = new Texture2D(1, ScanlinePeriodPixels, TextureFormat.RGBA32, false)
            {
                name = "Y4NGZ_ReportScanlineTile",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
            };

            var pixels = new Color32[ScanlinePeriodPixels];
            for (int y = 0; y < ScanlinePeriodPixels; y++)
            {
                // One lit row per period. The Image's own alpha decides how subtle the wash
                // is; the tile itself stays a clean on/off mask so it survives any tint.
                byte alpha = y == 0 ? (byte)255 : (byte)0;
                pixels[y] = new Color32(255, 255, 255, alpha);
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return CreateTileSprite(texture, "Y4NGZ_ReportScanline");
        }

        private static Sprite CreateDistressSprite(bool horizontal)
        {
            int length = DistressStripLengthPixels;
            int thickness = DistressStripThicknessPixels;
            int width = horizontal ? length : thickness;
            int height = horizontal ? thickness : length;

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = horizontal ? "Y4NGZ_ReportDistressH" : "Y4NGZ_ReportDistressV",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
            };

            // Seeded per axis so the horizontal and vertical rules do not wear identically,
            // but identically every run - the grain is wear, not noise.
            var random = new System.Random(horizontal ? DistressSeed : DistressSeed + 977);
            var pixels = new Color32[width * height];
            for (int step = 0; step < length; step++)
            {
                byte opacity = PickDistressOpacity(random);
                // Thickness jitter: the outer edge of the rule drops out here and there, which
                // is what makes the line look eaten rather than merely faded.
                bool eatOuterEdge = random.NextDouble() < 0.22;
                for (int across = 0; across < thickness; across++)
                {
                    byte alpha = eatOuterEdge && across == thickness - 1 ? (byte)0 : opacity;
                    int x = horizontal ? step : across;
                    int y = horizontal ? across : step;
                    pixels[y * width + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return CreateTileSprite(texture, texture.name);
        }

        private static byte PickDistressOpacity(System.Random random)
        {
            double roll = random.NextDouble();
            if (roll < 0.05)
                return 0;
            if (roll < 0.18)
                return 115;
            if (roll < 0.38)
                return 190;
            return 255;
        }

        private static Sprite TryWrapTexture(Texture2D texture)
        {
            try
            {
                return Sprite.Create(
                    texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f,
                    0,
                    SpriteMeshType.FullRect);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Sprite CreateTileSprite(Texture2D texture, string name)
        {
            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            sprite.name = name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        private static bool IsModOwned(Transform transform)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                string name = current.name ?? string.Empty;
                if (name.StartsWith("LGU_", StringComparison.Ordinal)
                    || name.StartsWith("Y4NGZ_", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string DescribeSprite(Sprite sprite)
        {
            return sprite == null
                ? "<none>"
                : $"'{sprite.name}' {sprite.rect.width:0}x{sprite.rect.height:0} border={sprite.border}";
        }

        private static bool ContainsIgnoreCase(string value, string fragment)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
