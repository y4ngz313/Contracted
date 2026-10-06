using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZCompany.Escalations._Shared
{
    /// <summary>
    /// #1343 S2. The escalation toast's danger edge vignette as native UI geometry, in the same
    /// family as <c>ContractHudLineArt</c>: no texture, no bundle. A mitred frame of four trapezoids
    /// runs from the rect's edge at the graphic's colour to an inner rect, inset by
    /// <see cref="EscalationBandTimeline.VignetteInset"/> (#1343 S7: 10% of the shorter side), at
    /// alpha 0; the trapezoids meet on the corner diagonals, so nothing overlaps. The toast animates
    /// only <c>color.a</c>.
    /// </summary>
    internal sealed class EscalationBandVignette : MaskableGraphic
    {
        public override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            float inset = EscalationBandTimeline.VignetteInset(r.width, r.height);
            if (inset <= 0f)
                return;

            Color edge = color;
            Color clear = edge;
            clear.a = 0f;
            // Outer corners 0-3, then inner corners 4-7, both clockwise from bottom left.
            vh.AddVert(new Vector3(r.xMin, r.yMin), edge, Vector2.zero);
            vh.AddVert(new Vector3(r.xMin, r.yMax), edge, Vector2.zero);
            vh.AddVert(new Vector3(r.xMax, r.yMax), edge, Vector2.zero);
            vh.AddVert(new Vector3(r.xMax, r.yMin), edge, Vector2.zero);
            vh.AddVert(new Vector3(r.xMin + inset, r.yMin + inset), clear, Vector2.zero);
            vh.AddVert(new Vector3(r.xMin + inset, r.yMax - inset), clear, Vector2.zero);
            vh.AddVert(new Vector3(r.xMax - inset, r.yMax - inset), clear, Vector2.zero);
            vh.AddVert(new Vector3(r.xMax - inset, r.yMin + inset), clear, Vector2.zero);
            AddQuad(vh, 0, 1, 5, 4); // left
            AddQuad(vh, 1, 2, 6, 5); // top
            AddQuad(vh, 2, 3, 7, 6); // right
            AddQuad(vh, 3, 0, 4, 7); // bottom
        }

        private static void AddQuad(VertexHelper vh, int a, int b, int c, int d)
        {
            vh.AddTriangle(a, b, c);
            vh.AddTriangle(a, c, d);
        }
    }
}
