using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #1002: fixed equipment, independent of the paying-out cable. Measured on blacksite-ship.bundle:
    // the starboard under-deck stringer is at x=1.8, y=-.125; the solid hull beam rises below it.
    // A saddle wraps both, outside the central boarding lane.
    internal sealed class BlacksiteRappelDavit : MonoBehaviour
    {
        internal static readonly Vector3 Mount = new Vector3(1.8f, -.125f, 11.15f);
        internal static readonly Vector3 CableAnchor = BlacksiteRappelGeometry.CableTopLocal + Vector3.up * 2.5f;
        internal const float TextureUvPerMetre = .69f; // measured vanilla support-beam median: .68858
        private readonly List<Mesh> _meshes = new List<Mesh>();

        internal static BlacksiteRappelDavit Create(Transform ship)
        {
            var existing = ship.GetComponentInChildren<BlacksiteRappelDavit>(true);
            if (existing != null) return existing;
            var root = new GameObject("BlacksiteRappelDavit"); root.transform.SetParent(ship, false);
            var davit = root.AddComponent<BlacksiteRappelDavit>(); davit.Build(ship); return davit;
        }

        private void Build(Transform ship)
        {
            // Borrow actual ship maps, UV scale, HDRP settings and colour. Never mutate shared materials.
            Material steel = FindMaterial(ship, "HangarShipRoomMetal");
            Material dark = FindMaterial(ship, "CatwalkRailBase") ?? steel;
            Material paint = FindMaterial(ship, "CatwalkRail") ?? dark;
            if (steel == null) steel = dark;
            var frame = new Parts(); var hardware = new Parts(); var marking = new Parts();
            Vector3 mastBottom = new Vector3(Mount.x, -.09f, Mount.z);
            Vector3 mastTop = new Vector3(Mount.x, 2.7f, Mount.z);
            Vector3 direction = Vector3.ProjectOnPlane(CableAnchor - mastTop, Vector3.up).normalized;
            Vector3 axle = Vector3.Cross(Vector3.up, direction);
            Quaternion heading = Quaternion.LookRotation(direction, Vector3.up);
            Vector3 wheel = CableAnchor - direction * .17f;
            Vector3 boomTip = wheel + Vector3.up * .2f;

            // Top plate intersects the stringer; the lower shoe intersects the solid hull beam.
            // Continuous side straps join them, so the mast does not rest on the grate alone.
            frame.Box(new Vector3(Mount.x, -.065f, Mount.z), new Vector3(.52f, .14f, .68f));
            frame.Box(new Vector3(Mount.x, -.50f, Mount.z), new Vector3(.52f, .12f, .68f));
            foreach (float side in new[] { -1f, 1f })
                frame.Box(new Vector3(Mount.x + side * .22f, -.27f, Mount.z), new Vector3(.08f, .5f, .6f));
            frame.Beam(mastBottom, mastTop, .24f, .24f);
            frame.Beam(mastTop, boomTip, .24f, .22f);
            frame.Beam(mastTop - Vector3.up * .8f, Vector3.Lerp(mastTop, boomTip, .65f), .12f, .12f);
            hardware.Box(mastTop - Vector3.up * .08f, new Vector3(.34f, .35f, .36f), heading);
            frame.Box(mastTop + direction * .07f + Vector3.up * .20f, new Vector3(.4f, .32f, .40f), heading);

            // Low-sided sheave, cheeks and pin. Its outboard tangent is exactly CableAnchor.
            hardware.Cylinder(wheel, axle, .17f, .12f, 12);
            foreach (float side in new[] { -1f, 1f })
            {
                frame.Box(wheel + axle * (side * .11f) + Vector3.up * .035f,
                    new Vector3(.055f, .39f, .41f), heading);
                hardware.Cylinder(wheel + axle * (side * .16f), axle, .055f, .05f, 6);
            }
            marking.Box(boomTip - direction * .22f + Vector3.up * .001f, new Vector3(.245f, .225f, .18f), heading);
            foreach (float x in new[] { -.17f, .17f }) foreach (float z in new[] { -.24f, .24f })
                hardware.Cylinder(new Vector3(Mount.x+x, .02f, Mount.z+z), Vector3.up, .043f, .05f, 6);
            foreach (float side in new[] { -1f, 1f }) foreach (float z in new[] { -.19f, .19f })
                hardware.Cylinder(new Vector3(Mount.x+side*.27f, -.36f, Mount.z+z), Vector3.right, .04f, .035f, 6);
            AddMesh("GalvanizedFrame", frame, steel);
            AddMesh("PulleyAndFasteners", hardware, dark);
            AddMesh("PaintedTip", marking, paint);
        }

        private static Material FindMaterial(Transform ship, string name)
        {
            foreach (var renderer in ship.GetComponentsInChildren<MeshRenderer>(true))
                foreach (var material in renderer.sharedMaterials)
                    if (material != null && (material.name == name || material.name == name + " (Instance)")) return material;
            return null;
        }
        private void AddMesh(string name, Parts parts, Material material)
        {
            var child = new GameObject(name); child.transform.SetParent(transform, false);
            var mesh = parts.Mesh(name); _meshes.Add(mesh);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = child.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On; renderer.receiveShadows = true;
        }
        private void OnDestroy() { foreach (var mesh in _meshes) if (mesh != null) Destroy(mesh); }

        // Hard-edged steel sections. UV density matches the vanilla beams; shared materials supply tiling.
        private sealed class Parts
        {
            private readonly List<Vector3> _vertices = new List<Vector3>();
            private readonly List<Vector2> _uv = new List<Vector2>();
            private readonly List<int> _triangles = new List<int>();
            private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int i = _vertices.Count; _vertices.AddRange(new[] { a, b, c, d });
                float width = Vector3.Distance(a,b), height = Vector3.Distance(b,c);
                _uv.AddRange(new[] { Vector2.zero, new Vector2(width,0), new Vector2(width,height), new Vector2(0,height) });
                _triangles.AddRange(new[] { i,i+1,i+2,i,i+2,i+3 });
            }
            private void Triangle(Vector3 a, Vector3 b, Vector3 c)
            {
                int i = _vertices.Count; _vertices.AddRange(new[] { a, b, c });
                _uv.AddRange(new[] { Vector2.zero, new Vector2(Vector3.Distance(a,b),0), new Vector2(0,Vector3.Distance(a,c)) });
                _triangles.AddRange(new[] { i,i+1,i+2 });
            }
            internal void Box(Vector3 center, Vector3 size) => Box(center, size, Quaternion.identity);
            internal void Box(Vector3 center, Vector3 size, Quaternion rotation)
            {
                Vector3 x = rotation * Vector3.right * size.x * .5f;
                Vector3 y = rotation * Vector3.up * size.y * .5f;
                Vector3 z = rotation * Vector3.forward * size.z * .5f;
                Quad(center-x-y+z,center+x-y+z,center+x+y+z,center-x+y+z);
                Quad(center+x-y-z,center-x-y-z,center-x+y-z,center+x+y-z);
                Quad(center+x-y+z,center+x-y-z,center+x+y-z,center+x+y+z);
                Quad(center-x-y-z,center-x-y+z,center-x+y+z,center-x+y-z);
                Quad(center-x+y+z,center+x+y+z,center+x+y-z,center-x+y-z);
                Quad(center-x-y-z,center+x-y-z,center+x-y+z,center-x-y+z);
            }
            internal void Beam(Vector3 from, Vector3 to, float width, float depth)
                => Box((from+to)*.5f, new Vector3(width,depth,Vector3.Distance(from,to)), Quaternion.LookRotation(to-from));
            internal void Cylinder(Vector3 center, Vector3 axis, float radius, float length, int sides)
            {
                Quaternion rotation = Quaternion.FromToRotation(Vector3.forward,axis);
                Vector3 cap = axis.normalized * length * .5f;
                for (int s=0;s<sides;s++)
                {
                    float a = s * Mathf.PI * 2/sides, b = (s+1) * Mathf.PI * 2/sides;
                    Vector3 p = rotation * new Vector3(Mathf.Cos(a),Mathf.Sin(a),0)*radius;
                    Vector3 q = rotation * new Vector3(Mathf.Cos(b),Mathf.Sin(b),0)*radius;
                    Quad(center+p-cap,center+q-cap,center+q+cap,center+p+cap);
                    Triangle(center-cap,center+q-cap,center+p-cap);
                    Triangle(center+cap,center+p+cap,center+q+cap);
                }
            }
            internal Mesh Mesh(string name)
            {
                var mesh = new Mesh { name = "BlacksiteDavit_"+name };
                for (int i=0;i<_uv.Count;i++) _uv[i] *= TextureUvPerMetre;
                mesh.SetVertices(_vertices); mesh.SetUVs(0,_uv); mesh.SetTriangles(_triangles,0);
                mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds(); return mesh;
            }
        }
    }
}
