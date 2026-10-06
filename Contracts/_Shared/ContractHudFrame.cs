using UnityEngine;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts._Shared
{
    // A borrowed sprite/material, never a cloned vanilla hierarchy or a modified asset.
    // Match the source's projected texel size so card scale cannot turn its frame hairline-thin.
    internal sealed class ContractHudFrame : Image
    {
        private Image _source;
        private bool _loggedSource;
        private Color _normalTint;
        private float _fallbackWidth = 6f;

        internal static ContractHudFrame Create(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var frame = go.AddComponent<ContractHudFrame>();
            var rect = frame.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            frame.raycastTarget = false;
            frame.type = Type.Sliced;
            frame.fillCenter = false;
            frame._normalTint = ContractUiTheme.HudFrame;
            frame.color = frame._normalTint;
            frame.RefreshSource();
            return frame;
        }

        private void LateUpdate() => RefreshSource();

        /// <summary>
        /// #1196 cue A, the card-edge glow on completion or failure. Blends the resting tint toward a
        /// theme colour by <paramref name="amount"/>, keeping the resting opacity. Zero hands the colour
        /// back to the resting tint, which <see cref="RefreshSource"/> then follows again; while the
        /// glow holds a different colour, RefreshSource leaves it alone like any manager-owned tint.
        /// </summary>
        internal void SetGlow(Color toward, float amount)
        {
            Color target = _normalTint;
            if (amount > 0f)
            {
                target = Color.Lerp(_normalTint, toward, amount);
                target.a = _normalTint.a;
            }
            if (color != target) color = target;
        }

        private void RefreshSource()
        {
            var source = ContractUiTheme.ClockFrame;
            if (_source != source)
            {
                _source = source;
                _loggedSource = false;
            }
            var tint = ContractUiTheme.HudFrame;
            // Preserve manager-owned warning pulses/result colors.
            if (color == _normalTint && color != tint) color = tint;
            _normalTint = tint;
            float targetUnits = ScreenUnits(rectTransform, canvas);
            if (_source == null)
            {
                if (sprite != null) sprite = null;
                if (material != defaultMaterial) material = null;
                if (targetUnits <= .001f) return;
                // Only used when a mod removes the native clock frame. At 1080p use 4.5px.
                float width = 4.5f * Screen.height / 1080f / Mathf.Max(.001f, targetUnits);
                if (Mathf.Abs(width - _fallbackWidth) > .01f)
                {
                    _fallbackWidth = width;
                    SetVerticesDirty();
                }
                return;
            }

            if (sprite != _source.sprite) sprite = _source.sprite;
            if (material != _source.material) material = _source.material;
            float sourceUnits = ScreenUnits(_source.rectTransform, _source.canvas);
            if (sourceUnits <= .001f || targetUnits <= .001f || pixelsPerUnit <= 0f) return;
            float multiplier = _source.pixelsPerUnitMultiplier * _source.pixelsPerUnit
                / pixelsPerUnit * targetUnits / sourceUnits;
            if (Mathf.Abs(pixelsPerUnitMultiplier - multiplier) > .001f)
                pixelsPerUnitMultiplier = Mathf.Max(.01f, multiplier);
            if (!_loggedSource)
            {
                _loggedSource = true;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Frame] {name}: source={_source.transform.parent.name}/{_source.name}, "
                    + $"sprite={sprite.name}, tint={_source.color}, material={material.name}, "
                    + $"sourcePPU={_source.pixelsPerUnitMultiplier:F3}, targetPPU={pixelsPerUnitMultiplier:F3}, "
                    + $"sourcePixelsPerUnit={sourceUnits:F3}, targetPixelsPerUnit={targetUnits:F3}");
            }
        }

        private static float ScreenUnits(RectTransform rect, Canvas owner)
        {
            if (owner == null) return 0f;
            var camera = owner.renderMode == RenderMode.ScreenSpaceOverlay ? null : owner.worldCamera;
            var origin = rect.TransformPoint(rect.rect.center);
            var start = RectTransformUtility.WorldToScreenPoint(camera, origin);
            // The clock is tilted: match the heavier edge, while retaining our flat rectangle.
            float horizontal = Vector2.Distance(start, RectTransformUtility.WorldToScreenPoint(camera,
                origin + rect.TransformVector(Vector3.right * 10f))) / 10f;
            float vertical = Vector2.Distance(start, RectTransformUtility.WorldToScreenPoint(camera,
                origin + rect.TransformVector(Vector3.up * 10f))) / 10f;
            return Mathf.Max(horizontal, vertical);
        }

        public override void OnPopulateMesh(VertexHelper vh)
        {
            if (sprite != null) { base.OnPopulateMesh(vh); return; }
            // Transparent rounded ring fallback. No filled panel if the clock is unavailable.
            vh.Clear();
            Rect outer = rectTransform.rect;
            float width = Mathf.Min(_fallbackWidth, Mathf.Min(outer.width, outer.height) * .25f);
            float radius = width * 1.5f;
            const int steps = 6;
            for (int corner = 0; corner < 4; corner++)
            {
                var center = new Vector2(corner == 0 || corner == 3 ? outer.xMax - radius : outer.xMin + radius,
                    corner < 2 ? outer.yMax - radius : outer.yMin + radius);
                for (int step = 0; step <= steps; step++)
                {
                    float angle = (corner * 90f + step * 90f / steps) * Mathf.Deg2Rad;
                    var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                    vh.AddVert(center + direction * radius, color, Vector2.zero);
                    vh.AddVert(center + direction * Mathf.Max(0f, radius - width), color, Vector2.zero);
                }
            }
            int count = vh.currentVertCount;
            for (int i = 0; i < count; i += 2)
            {
                int next = (i + 2) % count;
                vh.AddTriangle(i, next, i + 1);
                vh.AddTriangle(next, (next + 1) % count, i + 1);
            }
        }
    }
}
