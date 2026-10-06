using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Facility.Interior;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// Runtime composition for the visual-only Blender art kit. Physics and interaction remain
    /// code-owned so the bundle can be absent or replaced without changing network behaviour.
    /// </summary>
    internal static partial class PestControlTrapAssembler
    {
        private static readonly string[] RequiredArtParts =
        {
            "PestTrap_Rail",
            "PestTrap_Stile",
            "PestTrap_MeshPanel",
            "PestTrap_Post",
            "PestTrap_PostBase",
            "PestTrap_WinchHead",
            "PestTrap_WinchControlBox",
            "PestTrap_PlatformTile",
            "PestTrap_PlatformEdge",
            "PestTrap_RopeEye",
            "PestTrap_LeverBase",
            "PestTrap_LeverArm",
            "PestTrap_CeilingLampMount",
            "PestTrap_CeilingLampHead"
        };

        /// <summary>
        /// Fractions of the lamp ring radius to probe for a ceiling, outermost first. The ring
        /// itself overhangs the fence, so the inner samples are the fallback that keeps a lamp
        /// attached to geometry the placement check already proved exists over the platform.
        /// </summary>
        private static readonly float[] CeilingProbeInsets = { 1f, 0.8f, 0.6f };

        private static bool _loggedMissingArtKit;
        private static bool _loggedAuthoredFailure;
        private static bool _loggedOpenCeilingLamp;

        private static bool TryBuildAuthored(
            GameObject root,
            Vector3 center,
            float yaw,
            float side,
            float panelHeight,
            float restBottom,
            float postHeight,
            float ceilingClearance,
            bool interactive,
            out PestControlTrapPreview preview)
        {
            preview = null;
            GameObject artKit = ContractAssetLoader.PestControlTrapArtKitPrefab;
            if (artKit == null)
            {
                if (!_loggedMissingArtKit)
                {
                    _loggedMissingArtKit = true;
                    ContractsBootstrap.Log?.LogWarning(
                        "[MoonContracts.PestControl] Authored trap art is unavailable; using primitive visuals.");
                }
                return false;
            }

            var parts = new Dictionary<string, Transform>(StringComparer.Ordinal);
            for (int i = 0; i < RequiredArtParts.Length; i++)
            {
                Transform part = FindArtPart(artKit.transform, RequiredArtParts[i]);
                if (part == null)
                {
                    if (!_loggedAuthoredFailure)
                    {
                        _loggedAuthoredFailure = true;
                        ContractsBootstrap.Log?.LogWarning(
                            $"[MoonContracts.PestControl] Art kit is missing {RequiredArtParts[i]}; using primitive visuals.");
                    }
                    return false;
                }
                parts.Add(RequiredArtParts[i], part);
            }

            var authoredRoot = new GameObject("AuthoredVisuals");
            authoredRoot.transform.SetParent(root.transform, false);
            GameObject externalLights = null;
            // #716: from here on CloneArtPart only places host transforms; the ~150 template
            // instantiations are queued and drained under a per-frame budget below.
            BeginDeferredArtParts();
            var keptColliders = new HashSet<Collider>();
            try
            {
                float fenceSide = Mathf.Max(1f, side - FenceInsetMeters);
                float halfFence = fenceSide * 0.5f;
                int panelsPerSide = Mathf.Max(1, Mathf.RoundToInt(fenceSide / TargetPanelWidth));

                CloneArtPart(
                    parts["PestTrap_PlatformTile"], authoredRoot.transform, "Platform",
                    Vector3.zero, Quaternion.identity, new Vector3(side, side, 1f), new Vector2(side, side));
                if (interactive)
                    keptColliders.Add(BuildAuthoredPlatformCollider(authoredRoot.transform, side));

                BuildAuthoredPlatformEdges(parts, authoredRoot.transform, side);

                var fence = new GameObject("FenceAssembly");
                fence.transform.SetParent(authoredRoot.transform, false);
                fence.transform.localPosition = new Vector3(0f, restBottom, 0f);
                var sealColliders = new List<Collider>(4);
                var sealObstacles = new List<NavMeshObstacle>(4);
                BuildAuthoredFenceSide(parts, fence.transform, "North", new Vector3(0f, 0f, halfFence), 0f, fenceSide, panelHeight, panelsPerSide, interactive, sealColliders, sealObstacles);
                BuildAuthoredFenceSide(parts, fence.transform, "South", new Vector3(0f, 0f, -halfFence), 180f, fenceSide, panelHeight, panelsPerSide, interactive, sealColliders, sealObstacles);
                BuildAuthoredFenceSide(parts, fence.transform, "East", new Vector3(halfFence, 0f, 0f), 90f, fenceSide, panelHeight, panelsPerSide, interactive, sealColliders, sealObstacles);
                BuildAuthoredFenceSide(parts, fence.transform, "West", new Vector3(-halfFence, 0f, 0f), 270f, fenceSide, panelHeight, panelsPerSide, interactive, sealColliders, sealObstacles);

                var ropes = new LineRenderer[4];
                var ropeAnchors = new Transform[4];
                var corners = new[]
                {
                    new Vector2(halfFence, halfFence),
                    new Vector2(halfFence, -halfFence),
                    new Vector2(-halfFence, halfFence),
                    new Vector2(-halfFence, -halfFence)
                };
                for (int i = 0; i < corners.Length; i++)
                {
                    Vector3 postLocal = new Vector3(corners[i].x, 0f, corners[i].y);
                    Vector3 inward = -postLocal.normalized;
                    Quaternion inwardRotation = Quaternion.LookRotation(-inward, Vector3.up);
                    CloneArtPart(parts["PestTrap_PostBase"], authoredRoot.transform, "PostBase" + i,
                        postLocal, inwardRotation, Vector3.one, Vector2.one);
                    CloneArtPart(parts["PestTrap_Post"], authoredRoot.transform, "Post" + i,
                        postLocal, inwardRotation, new Vector3(1f, 1f, postHeight), new Vector2(1f, postHeight));
                    Transform pulley = CloneArtPart(parts["PestTrap_WinchHead"], authoredRoot.transform, "Winch" + i,
                        postLocal + Vector3.up * postHeight, inwardRotation, Vector3.one, Vector2.one);
                    CloneArtPart(parts["PestTrap_WinchControlBox"], authoredRoot.transform, "WinchControl" + i,
                        postLocal + Vector3.up * Mathf.Max(0.8f, postHeight - 0.8f), inwardRotation, Vector3.one, Vector2.one);

                    Transform anchor = CloneArtPart(parts["PestTrap_RopeEye"], fence.transform, "RopeAnchor" + i,
                        new Vector3(corners[i].x, panelHeight, corners[i].y), inwardRotation, Vector3.one, Vector2.one);
                    ropeAnchors[i] = anchor;
                    ropes[i] = BuildAuthoredRope(authoredRoot.transform, "Rope" + i, pulley.position, anchor.position);
                }

                bool leverValidated = TryResolveLeverPose(center, yaw, side, out Vector3 leverPosition, out Quaternion leverRotation);
                var lever = new GameObject("Lever");
                lever.transform.SetParent(authoredRoot.transform, true);
                lever.transform.SetPositionAndRotation(leverPosition, leverRotation);
                CloneArtPart(parts["PestTrap_LeverBase"], lever.transform, "LeverBase",
                    Vector3.zero, Quaternion.identity, Vector3.one, Vector2.one);
                Transform leverArm = new GameObject("LeverArmPivot").transform;
                leverArm.SetParent(lever.transform, false);
                leverArm.localPosition = new Vector3(0f, LeverAxleHeight, 0f);
                CloneArtPart(parts["PestTrap_LeverArm"], leverArm, "LeverArm",
                    Vector3.zero, Quaternion.identity, Vector3.one, Vector2.one);

                InteractTrigger trigger = null;
                if (interactive)
                {
                    trigger = BuildLeverTrigger(lever.transform);
                    BuildLeverScanNode(lever.transform);
                    // The lever's art parts are still empty hosts at this point, so everything this
                    // walk finds is code-owned: the interact box and the scan sphere. Collecting it
                    // here is what lets the post-drain sweep destroy the art kit's colliders wholesale.
                    foreach (Collider leverCollider in lever.GetComponentsInChildren<Collider>(true))
                        keptColliders.Add(leverCollider);
                    for (int i = 0; i < sealColliders.Count; i++)
                        keptColliders.Add(sealColliders[i]);
                }

                externalLights = BuildAuthoredCeilingLights(parts, center, yaw, side, ceilingClearance);

                preview = root.AddComponent<PestControlTrapPreview>();
                preview.Initialize(fence.transform, ropes, ropeAnchors, sealColliders, sealObstacles, trigger, restBottom, leverArm);
                preview.RegisterLighting(externalLights != null ? externalLights.transform : null);
                preview.FootprintMeters = side;
                preview.CageHeightMeters = panelHeight;
                preview.RestBottomMeters = restBottom;
                preview.PanelsPerSide = panelsPerSide;
                preview.LeverPosition = leverPosition;
                preview.LeverValidated = leverValidated;

                List<DeferredArtPart> queued = TakeDeferredArtParts();
                if (queued != null && queued.Count > 0)
                {
                    preview.BeginDeferredAssembly();
                    preview.StartCoroutine(DrainDeferredArtParts(
                        queued, preview, authoredRoot, externalLights, keptColliders));
                }

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.PestControl] Authored trap visual assembled: side={side:0.00}m panels={panelsPerSide} " +
                    $"deferredParts={(queued != null ? queued.Count : 0)}.");
                return true;
            }
            catch (Exception e)
            {
                CancelDeferredArtParts();
                if (externalLights != null)
                    UnityEngine.Object.Destroy(externalLights);
                UnityEngine.Object.Destroy(authoredRoot);
                if (!_loggedAuthoredFailure)
                {
                    _loggedAuthoredFailure = true;
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.PestControl] Authored trap assembly failed; using primitive visuals: {e.Message}");
                }
                preview = null;
                return false;
            }
        }

        /// <summary>
        /// Standable deck collision. This deliberately does NOT live on the cloned art part.
        ///
        /// Every PestTrap_* part carries the FBX's own Lcl Scaling of 100 (Blender exported with
        /// Apply Transform off, and the importer keeps it because the mesh is authored at 0.01 —
        /// the pair cancels out, which is why the VISUAL is correctly sized). A BoxCollider's
        /// size is a local-space number multiplied by that same lossy scale, so authoring it in
        /// metres on the part produced a 100x box: at a 6 m footprint, a 600 x 600 x 10 m slab
        /// that swallowed the entire interior and could only be crossed with noclip.
        ///
        /// A plain unit-scale child keeps the collider honest, the way the fence seal boxes
        /// already do. The deck's pivot is its TOP face at the trap origin (the tile mesh spans
        /// local Z -0.10..0), so the box hangs below y=0 to match the geometry rather than
        /// sitting a full thickness above it.
        /// </summary>
        private static BoxCollider BuildAuthoredPlatformCollider(Transform parent, float side)
        {
            var host = new GameObject("PlatformCollision");
            host.transform.SetParent(parent, false);

            var collider = host.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, -PlatformThickness * 0.5f, 0f);
            collider.size = new Vector3(side, PlatformThickness, side);
            collider.isTrigger = false;
            return collider;
        }

        private static void BuildAuthoredPlatformEdges(Dictionary<string, Transform> parts, Transform parent, float side)
        {
            float half = side * 0.5f;
            CloneArtPart(parts["PestTrap_PlatformEdge"], parent, "PlatformEdgeNorth", new Vector3(0f, 0f, half), Quaternion.identity, new Vector3(side, 1f, 1f), new Vector2(side, 1f));
            CloneArtPart(parts["PestTrap_PlatformEdge"], parent, "PlatformEdgeSouth", new Vector3(0f, 0f, -half), Quaternion.Euler(0f, 180f, 0f), new Vector3(side, 1f, 1f), new Vector2(side, 1f));
            CloneArtPart(parts["PestTrap_PlatformEdge"], parent, "PlatformEdgeEast", new Vector3(half, 0f, 0f), Quaternion.Euler(0f, 90f, 0f), new Vector3(side, 1f, 1f), new Vector2(side, 1f));
            CloneArtPart(parts["PestTrap_PlatformEdge"], parent, "PlatformEdgeWest", new Vector3(-half, 0f, 0f), Quaternion.Euler(0f, 270f, 0f), new Vector3(side, 1f, 1f), new Vector2(side, 1f));
        }

        private static void BuildAuthoredFenceSide(
            Dictionary<string, Transform> parts,
            Transform fenceRoot,
            string sideName,
            Vector3 localCenter,
            float yaw,
            float side,
            float panelHeight,
            int panelsPerSide,
            bool interactive,
            List<Collider> sealColliders,
            List<NavMeshObstacle> sealObstacles)
        {
            var sideRoot = new GameObject("Fence" + sideName);
            sideRoot.transform.SetParent(fenceRoot, false);
            sideRoot.transform.localPosition = localCenter;
            sideRoot.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            float panelWidth = side / panelsPerSide;
            for (int panel = 0; panel < panelsPerSide; panel++)
            {
                float x = -side * 0.5f + panelWidth * (panel + 0.5f);
                CloneArtPart(parts["PestTrap_MeshPanel"], sideRoot.transform, "MeshPanel" + panel,
                    new Vector3(x, 0f, 0f), Quaternion.identity,
                    new Vector3(panelWidth, 1f, panelHeight), new Vector2(panelWidth, panelHeight));
                CloneArtPart(parts["PestTrap_Rail"], sideRoot.transform, "RailBottom" + panel,
                    new Vector3(x, 0f, 0f), Quaternion.identity,
                    new Vector3(panelWidth, 1f, 1f), new Vector2(panelWidth, 1f));
                CloneArtPart(parts["PestTrap_Rail"], sideRoot.transform, "RailMiddle" + panel,
                    new Vector3(x, panelHeight * 0.5f, 0f), Quaternion.identity,
                    new Vector3(panelWidth, 1f, 1f), new Vector2(panelWidth, 1f));
                CloneArtPart(parts["PestTrap_Rail"], sideRoot.transform, "RailTop" + panel,
                    new Vector3(x, panelHeight, 0f), Quaternion.identity,
                    new Vector3(panelWidth, 1f, 1f), new Vector2(panelWidth, 1f));
            }
            for (int stile = 0; stile <= panelsPerSide; stile++)
            {
                float x = -side * 0.5f + panelWidth * stile;
                CloneArtPart(parts["PestTrap_Stile"], sideRoot.transform, "Stile" + stile,
                    new Vector3(x, 0f, 0f), Quaternion.identity,
                    new Vector3(1f, 1f, panelHeight), new Vector2(1f, panelHeight));
            }

            if (!interactive)
                return;
            var collider = sideRoot.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, panelHeight * 0.5f, 0f);
            collider.size = new Vector3(side, panelHeight, PanelDepth + 0.12f);
            collider.enabled = false;
            sealColliders.Add(collider);
            NavMeshObstacle obstacle = AddSealObstacle(sideRoot, side, panelHeight);
            if (obstacle != null)
                sealObstacles.Add(obstacle);
        }

        private static LineRenderer BuildAuthoredRope(Transform parent, string name, Vector3 start, Vector3 end)
        {
            var ropeObject = new GameObject(name);
            ropeObject.transform.SetParent(parent, false);
            var line = ropeObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.widthMultiplier = 0.035f;
            line.numCapVertices = 2;
            line.alignment = LineAlignment.View;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.startColor = RopeColor;
            line.endColor = RopeColor;
            line.sharedMaterial = GetRopeMaterial();
            line.SetPosition(0, start);
            line.SetPosition(1, end);
            return line;
        }

        private static GameObject BuildAuthoredCeilingLights(
            Dictionary<string, Transform> parts,
            Vector3 center,
            float yaw,
            float side,
            float ceilingClearance)
        {
            var root = new GameObject("PestControlTrapCeilingLights");
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3[] axes = { rotation * Vector3.forward, rotation * Vector3.back, rotation * Vector3.right, rotation * Vector3.left };
            float ring = side * 0.5f + CeilingLampOutsetMeters;
            StartOfRound start = StartOfRound.Instance;
            int mask = start != null ? start.collidersAndRoomMaskAndDefault : ~0;
            for (int i = 0; i < axes.Length; i++)
            {
                // The ring sits CeilingLampOutsetMeters OUTSIDE the fence, so a trap that cleared
                // its own headroom check can still have spokes reaching past the roofed area - at
                // a room edge, or under a gap. The probe then found nothing and the fallback
                // height was used anyway, hanging a lamp in open air with no ceiling above it.
                // Walk the probe back toward the centre until it finds real geometry: the inner
                // samples land over the platform, which placement already proved is roofed.
                Vector3 mount = center + axes[i] * ring
                    + Vector3.up * Mathf.Max(PestControlTrapPlacer.MinCeilingMeters, ceilingClearance);
                bool mounted = false;
                for (int p = 0; p < CeilingProbeInsets.Length && !mounted; p++)
                {
                    Vector3 probe = center + axes[i] * (ring * CeilingProbeInsets[p]);
                    InteriorAnchorService.RecordPhysicsQuery();
                    if (!Physics.Raycast(probe + Vector3.up * 0.1f, Vector3.up, out RaycastHit hit, 12f, mask, QueryTriggerInteraction.Ignore))
                        continue;

                    mount = hit.point;
                    mounted = true;
                }

                var fixture = new GameObject("CeilingLamp" + i);
                fixture.transform.SetParent(root.transform, false);
                fixture.transform.position = mount;

                // No ceiling anywhere along the spoke. Light the trap anyway - it still has to
                // read at night - but do not hang a housing off nothing.
                if (!mounted && !_loggedOpenCeilingLamp)
                {
                    _loggedOpenCeilingLamp = true;
                    ContractsBootstrap.Log?.LogWarning(
                        "[MoonContracts.PestControl] No ceiling above a trap lamp spoke; lighting it without a fixture.");
                }

                if (mounted)
                    CloneArtPart(parts["PestTrap_CeilingLampMount"], fixture.transform, "Mount", Vector3.zero, Quaternion.identity, Vector3.one, Vector2.one);
                Vector3 headPosition = mount + Vector3.down * 0.28f;
                Vector3 toCenter = center + Vector3.up * 0.35f - headPosition;
                bool aimable = toCenter.sqrMagnitude > 1e-4f;
                Vector3 aim = aimable ? toCenter.normalized : Vector3.down;

                // The authored head emits from its Trap_LampGlass lens down the mesh's local -Z,
                // and the kit's FBX carries the Blender-to-Y-up conversion as a -90 degree X
                // rotation on each part, which CloneArtPart composes in. Either way round - the
                // conversion left on the node, or baked into the mesh by the importer - the lens
                // lands on the head's local -Y, never its +Z. LookRotation only orients +Z and
                // +Y, so aiming the head with it directly never touched the lens at all: the
                // housing hung pointing roughly straight down while the spot below it aimed
                // correctly inward, which is the mismatch that got reported. Pre-rotating -90
                // degrees about X maps the head's -Y onto LookRotation's +Z, so the lens tracks
                // the aim and the mount knuckle (+Y) stays turned back toward the ceiling arm.
                Quaternion headWorldRotation = aimable
                    ? Quaternion.LookRotation(aim, Vector3.up) * Quaternion.Euler(-90f, 0f, 0f)
                    : Quaternion.identity;
                // The head's position is converted into fixture space, so its rotation has to be
                // too. Passing a world rotation only ever worked because the fixture inherits an
                // identity rotation from a root created at the origin; anything that rotated that
                // root would have silently skewed every lamp.
                Quaternion headRotation = Quaternion.Inverse(fixture.transform.rotation) * headWorldRotation;
                if (mounted)
                    CloneArtPart(parts["PestTrap_CeilingLampHead"], fixture.transform, "Head",
                        fixture.transform.InverseTransformPoint(headPosition), headRotation, Vector3.one, Vector2.one);

                var lightObject = new GameObject("SpotLight");
                lightObject.transform.SetParent(fixture.transform, false);
                lightObject.transform.position = headPosition;
                // A Unity spot emits along +Z, so unlike the head it is aimed directly. Both are
                // derived from the same `aim` so they cannot drift apart again.
                lightObject.transform.rotation = aimable
                    ? Quaternion.LookRotation(aim, Vector3.up)
                    : Quaternion.Euler(90f, 0f, 0f);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Spot;
                light.color = new Color(1f, 0.83f, 0.58f);
                light.intensity = 9f;
                light.range = Mathf.Max(7f, ceilingClearance + 2f);
                light.spotAngle = 62f;
                light.innerSpotAngle = 36f;
                light.shadows = LightShadows.None;
            }

            return root;
        }

        /// <summary>
        /// #716. Always creates the part's host transform immediately - carrying the exact pose,
        /// rotation and scale the single-object clone used to carry - and defers only the template
        /// instantiation when a build queue is open (see
        /// <c>PestControlTrapAssembler.Deferred.cs</c>). Callers get a usable transform back on the
        /// build frame either way, so rope endpoints, parenting and the fence hierarchy are final
        /// before the meshes exist.
        /// </summary>
        private static Transform CloneArtPart(
            Transform template,
            Transform parent,
            string name,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 scale,
            Vector2 tiling)
        {
            var host = new GameObject(name);
            host.transform.SetParent(parent, false);
            host.transform.localPosition = localPosition;
            host.transform.localRotation = localRotation * template.localRotation;
            host.transform.localScale = Vector3.Scale(template.localScale, scale);

            var part = new DeferredArtPart(template, host.transform, name, tiling);
            if (_deferredParts != null)
            {
                _deferredParts.Add(part);
                return host.transform;
            }

            InstantiateDeferredArtPart(part);
            StripArtKitColliders(host, null);
            return host.transform;
        }

        private static void ApplyAuthoredTiling(GameObject instance, Vector2 tiling)
        {
            var block = new MaterialPropertyBlock();
            block.SetVector("_BaseColorMap_ST", new Vector4(tiling.x, tiling.y, 0f, 0f));
            block.SetVector("_MainTex_ST", new Vector4(tiling.x, tiling.y, 0f, 0f));
            block.SetVector("_NormalMap_ST", new Vector4(tiling.x, tiling.y, 0f, 0f));
            block.SetVector("_BumpMap_ST", new Vector4(tiling.x, tiling.y, 0f, 0f));
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
                renderer.SetPropertyBlock(block);
        }

        private static Transform FindArtPart(Transform root, string name)
        {
            if (root == null)
                return null;
            if (string.Equals(root.name, name, StringComparison.Ordinal))
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindArtPart(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
