using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Y4NGZCompany.Contracts.Defuse
{
    // Shared by the game and asset-pipeline/DefusePresentationReview.cs. The insert follows
    // the upper lining panel of DeadDrop_BriefcaseOpen_NoOriginalWires; no runtime mesh
    // readback is required (the shipped mesh deliberately has Read/Write disabled).
    internal static class DefuseBombPresentation
    {
        internal const string LiningName = "Defuse Matte Lid Lining";
        private static Mesh _liningMesh;
        private static readonly Dictionary<Material, Material> Linings = new Dictionary<Material, Material>();
        private static readonly Dictionary<Material, Material> Panels = new Dictionary<Material, Material>();

        internal static bool Apply(GameObject root)
        {
            if (root == null) return false;
            Transform shell = root.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t.name == "Clean ASGS Briefcase Base");
            MeshFilter shellMesh = shell != null ? shell.GetComponent<MeshFilter>() : null;
            if (shellMesh?.sharedMesh == null ||
                shellMesh.sharedMesh.name != "DeadDrop_BriefcaseOpen_NoOriginalWires") return false;
            Renderer shellRenderer = shell.GetComponent<Renderer>();
            if (shellRenderer == null || shellRenderer.sharedMaterial == null) return false;

            if (shell.Find(LiningName) == null)
            {
                var lining = new GameObject(LiningName);
                lining.transform.SetParent(shell, false);
                lining.AddComponent<MeshFilter>().sharedMesh = LiningMesh();
                var renderer = lining.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = LiningMaterial(shellRenderer.sharedMaterial);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = true;
                lining.layer = shell.gameObject.layer;
            }

            Transform keypad = root.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t.name == "Defuse Keypad");
            if (keypad == null) return true;
            MeshFilter panel = keypad.GetComponentsInChildren<MeshFilter>(true)
                .FirstOrDefault(m => m.name == "panel" && m.sharedMesh != null);
            if (panel == null) return true;

            // Fit the entire keypad, including its eleven button meshes and their child
            // interaction nodes, to the central deck. Its rear edge meets the terminal block.
            keypad.localScale = Vector3.one * 0.72f;
            Vector3 center = root.transform.InverseTransformPoint(panel.transform.TransformPoint(panel.sharedMesh.bounds.center));
            Vector3 delta = new Vector3(0.065f - center.x, 0f, -center.z);
            keypad.position += root.transform.TransformVector(delta);
            Vector3 local = keypad.localPosition;
            local.y = -0.015f;
            keypad.localPosition = local;

            var panelRenderer = panel.GetComponent<Renderer>();
            if (panelRenderer != null)
            {
                Material[] materials = panelRenderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material source = materials[i];
                    if (source == null || source.name.EndsWith("_DefuseMattePanel", StringComparison.Ordinal)) continue;
                    if (!Panels.TryGetValue(source, out Material material) || material == null)
                    {
                        material = new Material(source) { name = source.name + "_DefuseMattePanel" };
                        DisableEmission(material);
                        SetFloat(material, "_Smoothness", 0.25f);
                        SetFloat(material, "_Glossiness", 0.25f);
                        SetFloat(material, "_NormalScale", 0.7f);
                        SetFloat(material, "_BumpScale", 0.7f);
                        Panels[source] = material;
                    }
                    materials[i] = material;
                }
                panelRenderer.sharedMaterials = materials;
            }
            return true;
        }

        private static Material LiningMaterial(Material source)
        {
            if (Linings.TryGetValue(source, out Material material) && material != null) return material;
            material = new Material(source) { name = "M_Defuse_MatteLining" };
            foreach (string property in new[] { "_BaseColorMap", "_BaseMap", "_MainTex", "_NormalMap", "_BumpMap", "_MaskMap", "_DetailMap", "_HeightMap" })
                if (material.HasProperty(property)) material.SetTexture(property, null);
            foreach (string keyword in new[] { "_NORMALMAP", "_NORMALMAP_TANGENT_SPACE", "_MASKMAP", "_DETAIL_MAP", "_HEIGHTMAP" })
                material.DisableKeyword(keyword);
            foreach (string property in new[] { "_BaseColor", "_Color" })
                if (material.HasProperty(property)) material.SetColor(property, new Color(0.045f, 0.042f, 0.035f, 1f));
            SetFloat(material, "_Metallic", 0f);
            SetFloat(material, "_Smoothness", 0.15f);
            SetFloat(material, "_Glossiness", 0.15f);
            DisableEmission(material);
            Linings[source] = material;
            return material;
        }

        private static void DisableEmission(Material material)
        {
            foreach (string property in new[] { "_EmissiveColorMap", "_EmissionMap" })
                if (material.HasProperty(property)) material.SetTexture(property, null);
            foreach (string property in new[] { "_EmissiveColor", "_EmissiveColorLDR", "_EmissionColor" })
                if (material.HasProperty(property)) material.SetColor(property, Color.black);
            SetFloat(material, "_EmissiveIntensity", 0f);
            material.DisableKeyword("_EMISSIVE_COLOR_MAP");
            material.DisableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        private static Mesh LiningMesh()
        {
            if (_liningMesh != null) return _liningMesh;
            // The shallow pad has closed edges: an oblique view must not reveal studs behind it.
            // Coordinates are in the imported shell's 100x model space.
            Vector3 min = new Vector3(-.002585f, -.002385f, .003380f);
            Vector3 max = new Vector3(-.002375f,  .002385f, .004510f);
            var corners = new[]
            {
                new Vector3(min.x, min.y, min.z), new Vector3(min.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z), new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, max.z)
            };
            int[] faces = { 4, 5, 6, 7, 0, 2, 1, 3, 2, 6, 3, 7, 0, 1, 4, 5, 0, 4, 2, 6, 1, 3, 5, 7 };
            var vertices = new Vector3[24];
            var triangles = new int[36];
            for (int face = 0; face < 6; face++)
            {
                int v = face * 4, t = face * 6;
                for (int j = 0; j < 4; j++) vertices[v + j] = corners[faces[v + j]];
                triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 2; triangles[t + 4] = v + 1; triangles[t + 5] = v + 3;
            }
            _liningMesh = new Mesh { name = "Defuse_LidLining" };
            _liningMesh.vertices = vertices;
            _liningMesh.triangles = triangles;
            _liningMesh.RecalculateNormals();
            _liningMesh.RecalculateBounds();
            return _liningMesh;
        }
    }
}
