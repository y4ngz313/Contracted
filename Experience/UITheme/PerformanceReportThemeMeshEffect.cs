using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZCompany.Experience.UITheme
{
    internal enum PerformanceReportThemeRole
    {
        Accent,
        Dim,
        Danger,
        DynamicGrade,
        DynamicPlayerState,
    }

    /// <summary>
    /// Re-hues the vanilla performance report after its Animator has authored the
    /// current vertex colors. Known red source sprites are neutralized once and
    /// cached, allowing every local palette to tint them without changing shared
    /// assets or fighting the Animator with recurring Graphic.color writes.
    /// </summary>
    [RequireComponent(typeof(Graphic))]
    internal sealed class PerformanceReportThemeMeshEffect : BaseMeshEffect
    {
        private sealed class NeutralSpriteState
        {
            internal Sprite Sprite;
            internal Texture2D Texture;
        }

        private static readonly Dictionary<int, NeutralSpriteState> NeutralSprites =
            new Dictionary<int, NeutralSpriteState>();

        private PerformanceReportThemeRole _role;
        private bool _neutralizeSprite;
        private Sprite _sourceSprite;
        private Sprite _neutralSprite;
        private UIVertex _vertex;

        internal static PerformanceReportThemeMeshEffect Ensure(
            Graphic graphic,
            PerformanceReportThemeRole role,
            bool neutralizeSprite)
        {
            if (graphic == null)
                return null;

            PerformanceReportThemeMeshEffect effect = graphic.GetComponent<PerformanceReportThemeMeshEffect>();
            if (effect == null)
                effect = graphic.gameObject.AddComponent<PerformanceReportThemeMeshEffect>();

            effect._role = role;
            effect._neutralizeSprite = neutralizeSprite;
            effect.CaptureLiveSprite();
            effect.ApplySpriteForCurrentTheme();
            effect.Refresh();
            return effect;
        }

        /// <summary>
        /// #747: gives <paramref name="target"/> the same effect <paramref name="source"/>
        /// carries, if any. The ledger row clones the vanilla panel graphic for its own fill,
        /// and under a colored preset that source is retinted through this effect; without
        /// the mirror the clone would stay vanilla red beside a retinted panel. Returns
        /// false when the source has no effect, which is the Default preset's normal state.
        /// </summary>
        internal static bool TryMirror(Graphic source, Graphic target)
        {
            if (source == null || target == null)
                return false;

            PerformanceReportThemeMeshEffect effect = source.GetComponent<PerformanceReportThemeMeshEffect>();
            if (effect == null)
                return false;

            Ensure(target, effect._role, effect._neutralizeSprite);
            return true;
        }

        /// <summary>
        /// The grayscale copy of a vanilla sprite, cached process-wide. Exposed for #556: the
        /// contract ledger reuses vanilla art (the box outline, the scanline wash) on objects
        /// it owns and tints itself, and the source art is authored red - tinting red by an
        /// arbitrary theme hue multiplies to mud. The vanilla asset is never modified.
        /// </summary>
        internal static Sprite GetNeutralCopy(Sprite source)
        {
            return source == null ? null : GetOrCreateNeutralSprite(source);
        }

        internal void Refresh()
        {
            if (graphic != null)
                graphic.SetVerticesDirty();
        }

        public override void OnEnable()
        {
            base.OnEnable();
            UiTheme.ThemeChanged += OnThemeChanged;
            CaptureLiveSprite();
            ApplySpriteForCurrentTheme();
            Refresh();
        }

        public override void OnDisable()
        {
            UiTheme.ThemeChanged -= OnThemeChanged;
            RestoreSourceSprite();
            base.OnDisable();
        }

        public override void ModifyMesh(VertexHelper vertexHelper)
        {
            if (!IsActive() || vertexHelper == null || UiTheme.CurrentPreset == HudColorPreset.Default)
                return;

            Color target = ResolveTargetColor();
            int count = vertexHelper.currentVertCount;
            for (int i = 0; i < count; i++)
            {
                vertexHelper.PopulateUIVertex(ref _vertex, i);
                Color source = _vertex.color;
                if (source.a <= 0f)
                    continue;

                float value = Mathf.Max(source.r, Mathf.Max(source.g, source.b));
                _vertex.color = new Color(
                    target.r * value,
                    target.g * value,
                    target.b * value,
                    source.a);
                vertexHelper.SetUIVertex(_vertex, i);
            }
        }

        private Color ResolveTargetColor()
        {
            switch (_role)
            {
                case PerformanceReportThemeRole.Dim:
                    return UiTheme.BlueMode ? UiTheme.White : UiTheme.Dim;
                case PerformanceReportThemeRole.Danger:
                    return UiTheme.Danger;
                case PerformanceReportThemeRole.DynamicGrade:
                    return graphic is TMP_Text grade
                        && string.Equals(grade.text?.Trim(), "F", StringComparison.OrdinalIgnoreCase)
                            ? UiTheme.Danger
                            : UiTheme.Accent;
                case PerformanceReportThemeRole.DynamicPlayerState:
                    return IsDangerStateSprite() ? UiTheme.Danger : UiTheme.Accent;
                default:
                    return UiTheme.Accent;
            }
        }

        private bool IsDangerStateSprite()
        {
            if (!(graphic is Image image) || image.sprite == null)
                return false;

            string name = _sourceSprite != null ? _sourceSprite.name : image.sprite.name;
            return ContainsIgnoreCase(name, "dead")
                || ContainsIgnoreCase(name, "deceased")
                || ContainsIgnoreCase(name, "missing")
                || ContainsIgnoreCase(name, "skull");
        }

        private void CaptureLiveSprite()
        {
            if (!(graphic is Image image))
                return;

            Sprite live = image.sprite;
            if (live == null || live == _neutralSprite || live == _sourceSprite)
                return;

            _sourceSprite = live;
            _neutralSprite = null;
        }

        private void ApplySpriteForCurrentTheme()
        {
            if (!(graphic is Image image))
                return;

            if (UiTheme.CurrentPreset == HudColorPreset.Default || !_neutralizeSprite)
            {
                RestoreSourceSprite();
                return;
            }

            if (_sourceSprite == null)
                _sourceSprite = image.sprite;
            if (_sourceSprite == null)
                return;

            if (_neutralSprite == null)
                _neutralSprite = GetOrCreateNeutralSprite(_sourceSprite);
            if (_neutralSprite != null && image.sprite != _neutralSprite)
                image.sprite = _neutralSprite;
        }

        private void RestoreSourceSprite()
        {
            if (graphic is Image image && _sourceSprite != null && image.sprite == _neutralSprite)
                image.sprite = _sourceSprite;
        }

        private void OnThemeChanged(HudColorPreset preset)
        {
            CaptureLiveSprite();
            ApplySpriteForCurrentTheme();
            Refresh();
        }

        private static Sprite GetOrCreateNeutralSprite(Sprite source)
        {
            int id = source.GetInstanceID();
            if (NeutralSprites.TryGetValue(id, out NeutralSpriteState cached)
                && cached.Sprite != null)
            {
                return cached.Sprite;
            }

            NeutralSpriteState created = CreateNeutralSprite(source);
            if (created == null)
                return null;

            NeutralSprites[id] = created;
            return created.Sprite;
        }

        private static NeutralSpriteState CreateNeutralSprite(Sprite source)
        {
            Texture2D sourceTexture = source != null ? source.texture : null;
            Rect sourceRect = source != null ? source.rect : default;
            int width = Mathf.RoundToInt(sourceRect.width);
            int height = Mathf.RoundToInt(sourceRect.height);
            if (sourceTexture == null || width <= 0 || height <= 0)
                return null;

            RenderTexture previous = RenderTexture.active;
            RenderTexture renderTexture = null;
            Texture2D neutralTexture = null;
            try
            {
                renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Vector2 scale = new Vector2(
                    sourceRect.width / sourceTexture.width,
                    sourceRect.height / sourceTexture.height);
                Vector2 offset = new Vector2(
                    sourceRect.x / sourceTexture.width,
                    sourceRect.y / sourceTexture.height);
                Graphics.Blit(sourceTexture, renderTexture, scale, offset);

                RenderTexture.active = renderTexture;
                neutralTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    name = source.name + "_Y4NGZNeutral",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = sourceTexture.filterMode,
                    wrapMode = TextureWrapMode.Clamp,
                };
                neutralTexture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);

                Color32[] pixels = neutralTexture.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 pixel = pixels[i];
                    byte value = Math.Max(pixel.r, Math.Max(pixel.g, pixel.b));
                    pixels[i] = new Color32(value, value, value, pixel.a);
                }

                neutralTexture.SetPixels32(pixels);
                neutralTexture.Apply(false, true);

                Vector2 pivot = new Vector2(
                    source.pivot.x / sourceRect.width,
                    source.pivot.y / sourceRect.height);
                Sprite neutralSprite = Sprite.Create(
                    neutralTexture,
                    new Rect(0f, 0f, width, height),
                    pivot,
                    source.pixelsPerUnit,
                    0,
                    SpriteMeshType.FullRect,
                    source.border);
                neutralSprite.name = source.name + "_Y4NGZNeutral";
                neutralSprite.hideFlags = HideFlags.HideAndDontSave;

                return new NeutralSpriteState
                {
                    Sprite = neutralSprite,
                    Texture = neutralTexture,
                };
            }
            catch (Exception)
            {
                if (neutralTexture != null)
                    UnityEngine.Object.Destroy(neutralTexture);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (renderTexture != null)
                    RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static bool ContainsIgnoreCase(string value, string fragment)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal static class PerformanceReportThemeApplicator
    {
        private const string CustomReportRowName = "LGU_ContractPerformanceQuotaSummaryRow";

        internal static void Apply(HUDManager hud)
        {
            Transform root = hud != null && hud.endgameStatsAnimator != null
                ? hud.endgameStatsAnimator.transform
                : null;
            if (root == null)
                return;

            EndOfGameStatUIElements stats = hud.statsUIElements;
            Graphic[] graphics = root.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                Graphic graphic = graphics[i];
                if (graphic == null || IsCustomReportGraphic(graphic.transform, root))
                    continue;
                if (graphic is RawImage && !HasCrtName(graphic.transform, root))
                    continue;

                PerformanceReportThemeRole role = ResolveRole(graphic, stats);
                bool neutralizeSprite = graphic is Image image && ShouldNeutralizeSprite(image.sprite);
                PerformanceReportThemeMeshEffect.Ensure(graphic, role, neutralizeSprite);
            }
        }

        private static PerformanceReportThemeRole ResolveRole(Graphic graphic, EndOfGameStatUIElements stats)
        {
            if (stats == null)
                return PerformanceReportThemeRole.Accent;

            if (graphic == stats.penaltyAddition
                || graphic == stats.penaltyTotal
                || graphic == stats.allPlayersDeadOverlay)
            {
                return PerformanceReportThemeRole.Danger;
            }

            if (graphic == stats.gradeLetter)
                return PerformanceReportThemeRole.DynamicGrade;
            if (Contains(stats.playerStates, graphic))
                return PerformanceReportThemeRole.DynamicPlayerState;
            if (graphic == stats.quotaDenominator
                || graphic == stats.challengeCollectedText
                || Contains(stats.playerNotesText, graphic))
            {
                return PerformanceReportThemeRole.Dim;
            }

            return PerformanceReportThemeRole.Accent;
        }

        private static bool ShouldNeutralizeSprite(Sprite sprite)
        {
            string name = sprite != null ? sprite.name : string.Empty;
            return ContainsIgnoreCase(name, "endgameStats")
                || ContainsIgnoreCase(name, "RedUIPanel")
                || ContainsIgnoreCase(name, "scanline");
        }

        private static bool IsCustomReportGraphic(Transform transform, Transform root)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                string name = current.name ?? string.Empty;
                if (string.Equals(name, CustomReportRowName, StringComparison.Ordinal)
                    || name.StartsWith("LGU_", StringComparison.Ordinal)
                    || name.StartsWith("Y4NGZ_", StringComparison.Ordinal))
                {
                    return true;
                }

                if (current == root)
                    break;
            }

            return false;
        }

        private static bool HasCrtName(Transform transform, Transform root)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                string name = current.name ?? string.Empty;
                if (ContainsIgnoreCase(name, "scanline")
                    || ContainsIgnoreCase(name, "scan line")
                    || ContainsIgnoreCase(name, "crt")
                    || ContainsIgnoreCase(name, "glitch"))
                {
                    return true;
                }

                if (current == root)
                    break;
            }

            return false;
        }

        private static bool Contains<T>(T[] values, object candidate) where T : UnityEngine.Object
        {
            if (values == null || candidate == null)
                return false;

            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == candidate)
                    return true;
            }

            return false;
        }

        private static bool ContainsIgnoreCase(string value, string fragment)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
