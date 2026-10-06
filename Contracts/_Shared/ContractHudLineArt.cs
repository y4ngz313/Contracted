using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZCompany.Contracts._Shared
{
    // Native UI geometry: no bundle, sprite atlas, fallback font or per-frame GameObjects.
    internal sealed class ContractHudLineArt : MaskableGraphic
    {
        internal enum Shape { Beacon, Case, Bolt, Target, Warning, Document, Shield }
        internal Shape Icon;
        internal TMP_Text ObjectiveText;
        internal float CheckProgress = 1f;
        private string _lastText;
        private Vector2 _lastSize;
        private int _lastVisible = -1;
        private float _lastProgress = -1f;
        private float _lastAlpha = -1f;
        private TMP_FontAsset _lastFont;

        private void LateUpdate()
        {
            if (ObjectiveText == null) return;
            var size = rectTransform.rect.size;
            if (_lastText == ObjectiveText.text && _lastVisible == ObjectiveText.maxVisibleCharacters
                && _lastSize == size && Mathf.Approximately(_lastProgress, CheckProgress)
                && Mathf.Approximately(_lastAlpha, ObjectiveText.alpha) && _lastFont == ObjectiveText.font) return;
            _lastText = ObjectiveText.text;
            _lastVisible = ObjectiveText.maxVisibleCharacters;
            _lastSize = size;
            _lastProgress = CheckProgress;
            _lastAlpha = ObjectiveText.alpha;
            _lastFont = ObjectiveText.font;
            ObjectiveText.ForceMeshUpdate();
            SetVerticesDirty();
        }

        public override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (ObjectiveText != null)
            {
                var info = ObjectiveText.textInfo;
                for (int i = 0; i + 2 < info.characterCount; i++)
                {
                    if (info.characterInfo[i].character != '[' || info.characterInfo[i + 2].character != ']') continue;
                    char marker = info.characterInfo[i + 1].character;
                    if (marker != ' ' && marker != 'X' && marker != '!') continue;
                    if (i >= ObjectiveText.maxVisibleCharacters) continue;
                    var ch = info.characterInfo[i];
                    if (ch.color.a != 0) continue; // only our invisible marker, never literal player-facing brackets
                    float left = ch.origin, top = ch.baseLine + ObjectiveText.fontSize * 0.72f;
                    const float side = 18f;
                    Color tint = color;
                    tint.a *= ObjectiveText.alpha;
                    Box(vh, new Rect(left, top - side, side, side), tint);
                    if (marker == 'X')
                    {
                        Vector2 a = new Vector2(left + 3, top - 9), b = new Vector2(left + 7, top - 14), c = new Vector2(left + 16, top - 3);
                        Line(vh, a, Vector2.Lerp(a, b, Mathf.Clamp01(CheckProgress / .4f)), tint);
                        if (CheckProgress > .4f) Line(vh, b, Vector2.Lerp(b, c, (CheckProgress - .4f) / .6f), tint);
                    }
                    else if (marker == '!')
                    {
                        Line(vh, new Vector2(left + 9, top - 3), new Vector2(left + 9, top - 11), tint);
                        Line(vh, new Vector2(left + 9, top - 14), new Vector2(left + 9, top - 16), tint);
                    }
                }
                return;
            }
            Rect r = rectTransform.rect;
            // Icon coordinates are authored on a 40-unit square.
            if (Icon == Shape.Beacon)
            {
                Segment(vh, r, 20, 9, 20, 35); Segment(vh, r, 12, 35, 28, 35);
                Arc(vh, r, 20, 10, 3, 0, 360);
                Arc(vh, r, 20, 10, 9, 120, 240); Arc(vh, r, 20, 10, 9, -60, 60);
                Arc(vh, r, 20, 10, 15, 120, 240); Arc(vh, r, 20, 10, 15, -60, 60);
            }
            else if (Icon == Shape.Bolt)
            {
                Segment(vh,r,23,3,10,23); Segment(vh,r,10,23,21,23);
                Segment(vh,r,21,23,16,37); Segment(vh,r,16,37,31,16); Segment(vh,r,31,16,21,16); Segment(vh,r,21,16,23,3);
            }
            else if (Icon == Shape.Warning)
            {
                Segment(vh,r,20,4,3,35); Segment(vh,r,3,35,37,35); Segment(vh,r,37,35,20,4);
                Segment(vh,r,20,14,20,25); Segment(vh,r,20,29,20,31);
            }
            else if (Icon == Shape.Target)
            {
                Arc(vh,r,20,20,12,0,360); Segment(vh,r,20,2,20,12); Segment(vh,r,20,28,20,38);
                Segment(vh,r,2,20,12,20); Segment(vh,r,28,20,38,20);
            }
            else if (Icon == Shape.Document)
            {
                Segment(vh,r,9,4,30,4); Segment(vh,r,30,4,30,36); Segment(vh,r,30,36,9,36); Segment(vh,r,9,36,9,4);
                for(int y=13;y<29;y+=7) Segment(vh,r,14,y,25,y);
            }
            else if (Icon == Shape.Shield)
            {
                Segment(vh,r,20,3,34,9); Segment(vh,r,34,9,31,26); Segment(vh,r,31,26,20,37);
                Segment(vh,r,20,37,9,26); Segment(vh,r,9,26,6,9); Segment(vh,r,6,9,20,3);
            }
            else
            {
                Segment(vh,r,5,13,35,13); Segment(vh,r,35,13,35,34); Segment(vh,r,35,34,5,34); Segment(vh,r,5,34,5,13);
                Segment(vh,r,13,13,13,7); Segment(vh,r,13,7,27,7); Segment(vh,r,27,7,27,13);
            }
        }

        private void Segment(VertexHelper vh, Rect r, float x1, float y1, float x2, float y2)
            => Line(vh, new Vector2(r.xMin+x1*r.width/40,r.yMax-y1*r.height/40),new Vector2(r.xMin+x2*r.width/40,r.yMax-y2*r.height/40),color);
        private void Arc(VertexHelper vh, Rect r, float cx,float cy,float radius,float start,float end)
        {
            const int steps=20;
            for(int i=0;i<steps;i++)
            {
                float a=Mathf.Lerp(start,end,(float)i/steps)*Mathf.Deg2Rad,b=Mathf.Lerp(start,end,(float)(i+1)/steps)*Mathf.Deg2Rad;
                Segment(vh,r,cx+Mathf.Cos(a)*radius,cy+Mathf.Sin(a)*radius,cx+Mathf.Cos(b)*radius,cy+Mathf.Sin(b)*radius);
            }
        }
        private static void Box(VertexHelper vh, Rect r, Color c)
        {
            var a=new Vector2(r.xMin,r.yMin); var b=new Vector2(r.xMin,r.yMax); var d=new Vector2(r.xMax,r.yMin); var e=new Vector2(r.xMax,r.yMax);
            Line(vh,a,b,c); Line(vh,b,e,c); Line(vh,e,d,c); Line(vh,d,a,c);
        }
        private static void Line(VertexHelper vh, Vector2 a, Vector2 b, Color c)
        {
            if((b-a).sqrMagnitude<.0001f) return;
            Vector2 n=new Vector2(-(b-a).y,(b-a).x).normalized*(ContractUiTheme.HudSymbolThickness * .5f);
            int i=vh.currentVertCount;
            vh.AddVert(a-n,c,Vector2.zero); vh.AddVert(a+n,c,Vector2.zero); vh.AddVert(b+n,c,Vector2.zero); vh.AddVert(b-n,c,Vector2.zero);
            vh.AddTriangle(i,i+1,i+2); vh.AddTriangle(i,i+2,i+3);
        }
    }
}
