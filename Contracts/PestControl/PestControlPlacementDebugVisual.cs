using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// Client-local, runtime-spawned visualization of a Pest Control placement probe.
    /// Gizmos are editor-only, so this uses real primitives with an unlit transparent
    /// material — it has to be visible in a shipped game build. Nothing here is networked
    /// and nothing here has a collider.
    /// </summary>
    internal static class PestControlPlacementDebugVisual
    {
        private const string RootName = "Y4NGZ_PestControlPlacementDebug";

        private static GameObject _root;
        private static readonly Dictionary<Color, Material> MaterialCache = new Dictionary<Color, Material>();

        internal static void Clear()
        {
            if (_root != null)
                Object.Destroy(_root);

            _root = null;
        }

        internal static void Build(PestControlTrapPlacement placement)
        {
            Clear();
            if (placement == null)
                return;

            _root = new GameObject(RootName);

            List<PestControlCellSample> cells = placement.Cells;
            if (cells != null)
            {
                for (int i = 0; i < cells.Count; i++)
                {
                    PestControlCellSample cell = cells[i];
                    if (cell.Rejection == PestControlCellRejection.OutsideTile)
                        continue;

                    Color color = ColorFor(cell.Rejection);
                    if (cell.InChosenSquare)
                        color = new Color(0.15f, 1f, 0.95f, 0.75f);

                    CreateBox(
                        "Cell",
                        cell.Center + Vector3.up * 0.03f,
                        Quaternion.Euler(0f, placement.Yaw, 0f),
                        new Vector3(PestControlTrapPlacer.CellSizeMeters * 0.88f, 0.02f, PestControlTrapPlacer.CellSizeMeters * 0.88f),
                        color);
                }
            }

            if (!placement.HasResult)
                return;

            BuildSquareFrame(placement);

            PestControlTrapAssembler.DeriveCageMetrics(
                placement.CeilingClearanceMeters,
                out float cageHeight,
                out float restBottom,
                out float postHeight);

            // Trap centre marker, scaled to the derived post height so headroom is judgeable in-game.
            CreateBox(
                "CenterMarker",
                placement.Center + Vector3.up * (postHeight * 0.5f),
                Quaternion.Euler(0f, placement.Yaw, 0f),
                new Vector3(0.12f, postHeight, 0.12f),
                new Color(1f, 0.92f, 0.2f, 0.9f));

            BuildCageOutline(placement, cageHeight, restBottom, postHeight);
        }

        /// <summary>
        /// Corner posts at the derived height plus a band marking where the cage hangs, so the
        /// probe result shows the ADAPTIVE proportions rather than a fixed-size guess.
        /// </summary>
        private static void BuildCageOutline(PestControlTrapPlacement placement, float cageHeight, float restBottom, float postHeight)
        {
            Quaternion rotation = Quaternion.Euler(0f, placement.Yaw, 0f);
            Vector3 right = rotation * Vector3.right;
            Vector3 forward = rotation * Vector3.forward;
            float half = placement.FootprintMeters * 0.5f;
            var postColor = new Color(0.9f, 0.9f, 0.95f, 0.5f);
            var cageColor = new Color(0.15f, 1f, 0.95f, 0.55f);

            for (int i = 0; i < 4; i++)
            {
                float x = (i & 1) == 0 ? half : -half;
                float z = (i & 2) == 0 ? half : -half;
                Vector3 corner = placement.Center + right * x + forward * z;

                CreateBox("OutlinePost" + i, corner + Vector3.up * (postHeight * 0.5f), rotation,
                    new Vector3(0.08f, postHeight, 0.08f), postColor);

                // Band spanning the suspended cage: bottom edge at rest height, top at rest+cage.
                CreateBox("OutlineCage" + i, corner + Vector3.up * (restBottom + cageHeight * 0.5f), rotation,
                    new Vector3(0.14f, cageHeight, 0.14f), cageColor);
            }
        }

        private static void BuildSquareFrame(PestControlTrapPlacement placement)
        {
            Quaternion rotation = Quaternion.Euler(0f, placement.Yaw, 0f);
            Vector3 right = rotation * Vector3.right;
            Vector3 forward = rotation * Vector3.forward;
            float half = placement.FootprintMeters * 0.5f;
            const float railThickness = 0.10f;
            const float railHeight = 0.30f;
            Color frameColor = placement.MeetsMinimumFootprint
                ? new Color(0.2f, 0.55f, 1f, 0.85f)
                : new Color(1f, 0.45f, 0.05f, 0.85f);

            Vector3 baseCenter = placement.Center + Vector3.up * (railHeight * 0.5f);
            CreateBox("FrameNorth", baseCenter + forward * half, rotation,
                new Vector3(placement.FootprintMeters, railHeight, railThickness), frameColor);
            CreateBox("FrameSouth", baseCenter - forward * half, rotation,
                new Vector3(placement.FootprintMeters, railHeight, railThickness), frameColor);
            CreateBox("FrameEast", baseCenter + right * half, rotation,
                new Vector3(railThickness, railHeight, placement.FootprintMeters), frameColor);
            CreateBox("FrameWest", baseCenter - right * half, rotation,
                new Vector3(railThickness, railHeight, placement.FootprintMeters), frameColor);
        }

        private static Color ColorFor(PestControlCellRejection rejection)
        {
            switch (rejection)
            {
                case PestControlCellRejection.Clear: return new Color(0.15f, 0.9f, 0.2f, 0.45f);
                case PestControlCellRejection.NoFloor: return new Color(0.35f, 0.35f, 0.35f, 0.45f);
                case PestControlCellRejection.FloorPlane: return new Color(0.9f, 0.55f, 0.1f, 0.45f);
                case PestControlCellRejection.Ceiling: return new Color(0.75f, 0.15f, 0.85f, 0.45f);
                case PestControlCellRejection.Clutter: return new Color(0.9f, 0.12f, 0.12f, 0.45f);
                case PestControlCellRejection.NavMesh: return new Color(0.15f, 0.35f, 0.95f, 0.45f);
                case PestControlCellRejection.Reserved: return new Color(1f, 1f, 1f, 0.55f);
                default: return new Color(1f, 1f, 1f, 0.35f);
            }
        }

        internal static string ColorKey =>
            "colour key: green=clear, cyan=chosen square, grey=no-floor, orange=off floor plane, " +
            "purple=low ceiling, red=clutter, blue=off navmesh, white=existing reservation; " +
            "blue frame=chosen footprint " +
            "(orange frame=best-effort), yellow post=trap centre at derived post height, " +
            "white corner posts=derived post height, cyan corner bands=suspended cage span";

        private static void CreateBox(string boxName, Vector3 position, Quaternion rotation, Vector3 scale, Color color)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = boxName;
            box.transform.SetParent(_root.transform, false);
            box.transform.position = position;
            box.transform.rotation = rotation;
            box.transform.localScale = scale;
            box.layer = 0;

            var collider = box.GetComponent<Collider>();
            if (collider != null)
                Object.Destroy(collider);

            var renderer = box.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = GetMaterial(color);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        // One material per distinct colour: a probe can spawn a few thousand cells and a
        // material each would be thousands of leaked instances.
        private static Material GetMaterial(Color color)
        {
            if (MaterialCache.TryGetValue(color, out Material cached) && cached != null)
                return cached;

            Shader shader = Shader.Find("HDRP/Unlit")
                ?? Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Standard");
            if (shader == null)
                return null;

            var material = new Material(shader) { name = "PestControlDebugCell" };
            if (material.HasProperty("_SurfaceType"))
                material.SetFloat("_SurfaceType", 1f);
            if (material.HasProperty("_BlendMode"))
                material.SetFloat("_BlendMode", 0f);
            if (material.HasProperty("_ZWrite"))
                material.SetFloat("_ZWrite", 0f);
            if (material.HasProperty("_AlphaCutoffEnable"))
                material.SetFloat("_AlphaCutoffEnable", 0f);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_UnlitColor"))
                material.SetColor("_UnlitColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            if (material.HasProperty("_EmissiveColor"))
                material.SetColor("_EmissiveColor", color * 1.6f);
            if (material.HasProperty("_EmissionColor"))
                material.SetColor("_EmissionColor", color * 1.6f);
            material.EnableKeyword("_EMISSION");
            material.renderQueue = 3000;

            MaterialCache[color] = material;
            return material;
        }
    }
}
