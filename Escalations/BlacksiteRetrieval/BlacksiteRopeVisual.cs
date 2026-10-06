using UnityEngine;
using UnityEngine.Rendering;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // A lit, round cable. Simulation endpoints remain unchanged by its small visual sway.
    internal sealed class BlacksiteRopeVisual : MonoBehaviour
    {
        private const int Rings = 40, Sides = 8;
        private Mesh _mesh; private Material _material; private Texture2D _texture;
        private readonly Vector3[] _vertices = new Vector3[Rings * Sides];
        private readonly Vector3[] _normals = new Vector3[Rings * Sides];
        private readonly Vector2[] _uv = new Vector2[Rings * Sides];
        internal void Initialize(float radius = .034f)
        {
            Radius = radius; _mesh = new Mesh { name = "Blacksite braided cable" }; _mesh.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh; var renderer = gameObject.AddComponent<MeshRenderer>();
            var shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
            if (shader != null)
            {
                _material = new Material(shader) { name = "Blacksite cable braid" };
                _texture = new Texture2D(32, 64, TextureFormat.RGBA32, true); _texture.wrapMode = TextureWrapMode.Repeat;
                for (int y = 0; y < 64; y++) for (int x = 0; x < 32; x++)
                { float strand = Mathf.Pow(.5f + .5f * Mathf.Sin((x + y * .7f) * Mathf.PI / 4), 2); _texture.SetPixel(x, y, Color.Lerp(new Color(.075f,.065f,.045f), new Color(.4f,.35f,.23f), strand)); }
                _texture.Apply();
                if (_material.HasProperty("_BaseColorMap")) _material.SetTexture("_BaseColorMap", _texture);
                if (_material.HasProperty("_MainTex")) _material.SetTexture("_MainTex", _texture);
                if (_material.HasProperty("_Smoothness")) _material.SetFloat("_Smoothness", .28f);
                if (_material.HasProperty("_Metallic")) _material.SetFloat("_Metallic", .15f);
                renderer.sharedMaterial = _material;
            }
            renderer.shadowCastingMode = ShadowCastingMode.On;
            var triangles = new int[(Rings - 1) * Sides * 6]; int n = 0;
            for (int r = 0; r < Rings - 1; r++) for (int s = 0; s < Sides; s++)
            { int a=r*Sides+s, c=r*Sides+(s+1)%Sides, d=c+Sides, e=a+Sides; triangles[n++]=a;triangles[n++]=c;triangles[n++]=e;triangles[n++]=c;triangles[n++]=d;triangles[n++]=e; }
            _mesh.vertices = _vertices; _mesh.triangles = triangles;
        }
        internal float Radius { get; private set; }
        internal void Pose(Vector3 top, Vector3 bottom, float time, float extension, bool loaded)
        {
            if (_mesh == null) Initialize();
            bottom = Vector3.Lerp(top, bottom, Mathf.Clamp01(extension));
            Vector3 axis = (bottom-top).normalized;
            var side = Vector3.Cross(axis, Vector3.forward).normalized;
            if (side.sqrMagnitude < .1f) side = Vector3.right;
            var other = Vector3.Cross(axis, side).normalized;
            for (int r=0;r<Rings;r++)
            {
                float t=r/(Rings-1f); float bend=Mathf.Sin(t*Mathf.PI)*(loaded?.012f:.075f);
                Vector3 center=Vector3.Lerp(top,bottom,t)+side*(Mathf.Sin(time*1.6f+t*3)*bend)+other*(Mathf.Cos(time*1.1f+t*2)*bend*.5f);
                for (int s=0;s<Sides;s++) { float angle=s*Mathf.PI*2/Sides; int i=r*Sides+s; Vector3 normal=side*Mathf.Cos(angle)+other*Mathf.Sin(angle);
                    _vertices[i]=center+normal*Radius; _normals[i]=normal; _uv[i]=new Vector2(s/(Sides-1f), t*Vector3.Distance(top,bottom)*4); }
            }
            _mesh.vertices=_vertices; _mesh.normals=_normals; _mesh.uv=_uv; _mesh.RecalculateBounds();
        }
        private void OnDestroy() { if (_mesh != null) Destroy(_mesh); if (_material != null) Destroy(_material); if (_texture != null) Destroy(_texture); }
    }
}
