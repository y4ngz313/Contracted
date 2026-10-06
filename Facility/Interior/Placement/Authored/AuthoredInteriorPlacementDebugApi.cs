using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Contracts.Defuse;
using Y4NGZCompany.Contracts.PestControl;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Contracts.WasteDisposal;
namespace Y4NGZCompany.Facility.Interior.Placement.Authored
{
    public static class AuthoredInteriorPlacementDebugApi
    {
        private const string DefaultCameraId = "camera1";

        public static string GetStatus()
        {
            return AuthoredInteriorPlacementStore.BuildStatus();
        }

        public static string ExportLocalProfile()
        {
            return ExportLocalProfileForFlow(null);
        }

        public static string ExportLocalProfileForFlow(string flowName)
        {
            return AuthoredInteriorPlacementStore.ExportLocalProfile(
                    flowName,
                    out _,
                    out _,
                    out string message)
                ? message
                : $"export failed: {message}";
        }

        public static string SavePlacement(string objectKind, string objectId, Transform transform, string metadataJson)
        {
            // #582 — ship-scoped kinds never touch the tile-local store; the
            // owning plugin persists them against a ship transform instead.
            if (AuthoredInteriorPlacementKinds.IsShipScoped(objectKind))
                return ShipScopedPlacementHooks.Save(
                    AuthoredInteriorPlacementKinds.Normalize(objectKind), objectId, transform, metadataJson);

            return AuthoredInteriorPlacementStore.RecordPlacement(
                    transform,
                    objectKind,
                    objectId,
                    metadataJson,
                    out string message)
                ? message
                : $"save failed: {message}";
        }

        public static string SaveCameraView(string objectId, Vector3 position, Quaternion rotation)
        {
            string id = string.IsNullOrWhiteSpace(objectId) ? DefaultCameraId : objectId.Trim();
            return AuthoredInteriorPlacementStore.RecordCameraView(id, position, rotation, out string message)
                ? message
                : $"save failed: {message}";
        }

        public static bool TryGetSavedPlacement(string objectKind, string objectId, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            string kind = AuthoredInteriorPlacementKinds.Normalize(objectKind);
            if (AuthoredInteriorPlacementKinds.IsShipScoped(kind))
                return ShipScopedPlacementHooks.TryGetSavedPose(kind, objectId, out position, out rotation);

            string id = ResolveDefaultObjectId(kind, objectId);
            if (!AuthoredInteriorPlacementStore.TryResolvePoses(kind, out List<AuthoredInteriorPlacementPose> poses))
                return false;

            for (int i = 0; i < poses.Count; i++)
            {
                AuthoredInteriorPlacementPose pose = poses[i];
                string poseId = pose.Record != null
                    ? ResolveDefaultObjectId(kind, pose.Record.objectId)
                    : kind;
                if (!string.Equals(poseId, id, StringComparison.OrdinalIgnoreCase))
                    continue;

                position = pose.WorldPosition;
                rotation = pose.WorldRotation;
                return true;
            }

            return false;
        }

        public static GameObject CreatePlacementPreview(string objectKind, string metadataJson)
        {
            string kind = AuthoredInteriorPlacementKinds.Normalize(objectKind);
            switch (kind)
            {
                case AuthoredInteriorPlacementKinds.DeadDropBomb:
                    return DefuseContract.CreatePlacementPreview();
                case AuthoredInteriorPlacementKinds.Mainframe:
                case AuthoredInteriorPlacementKinds.Vault:
                    // Both fixtures moved to LethalCCTV in #393; it registers the factory.
                    // Without LethalCCTV, bootstrap wires CreateFixtureFallbackPreview instead
                    // so the capture UI still gets a ghost (#545).
                    return InteriorFixtureHooks.CreateFixturePreview(kind, metadataJson);
                case AuthoredInteriorPlacementKinds.BlacksiteContainer:
                    return CreateBlacksiteContainerPreview(metadataJson);
                case AuthoredInteriorPlacementKinds.CctvCamera:
                    return CreateCctvCameraPreview();
                case AuthoredInteriorPlacementKinds.CctvNote:
                    // LethalCCTV owns the sticky note; its registered factory
                    // clones the real note prefab into a stripped ghost. Without
                    // LethalCCTV there is no note to author, so null (which aborts
                    // the edit session with a message) is the right answer.
                    return InteriorFixtureHooks.CreateFixturePreview(kind, metadataJson);
                case AuthoredInteriorPlacementKinds.PestTrap:
                    return CreatePestTrapPreview();
                case AuthoredInteriorPlacementKinds.WasteIncinerator:
                    return CreateWasteIncineratorPreview();
                default:
                    return null;
            }
        }

        public static string DeletePlacement(string objectKind, string objectId)
        {
            if (AuthoredInteriorPlacementKinds.IsShipScoped(objectKind))
                return ShipScopedPlacementHooks.Delete(
                    AuthoredInteriorPlacementKinds.Normalize(objectKind), objectId);

            return AuthoredInteriorPlacementStore.DeletePlacement(objectKind, objectId);
        }

        private static string ResolveDefaultObjectId(string kind, string objectId)
        {
            if (!string.IsNullOrWhiteSpace(objectId))
                return objectId.Trim();

            return AuthoredInteriorPlacementKinds.IsCctvCamera(kind) ? DefaultCameraId : kind;
        }

        private static GameObject CreateBlacksiteContainerPreview(string metadataJson)
        {
            BlacksiteContainerPlacementMetadata metadata = AuthoredInteriorPlacementMetadata.ReadContainer(metadataJson);
            if (BreachContainerController.TryGetContainerPrefab(string.Empty, out GameObject prefab) && prefab != null)
            {
                GameObject instance = UnityEngine.Object.Instantiate(prefab);
                if (instance != null)
                {
                    instance.name = "Y4NGZDebug_BlacksiteContainerPreview";
                    BreachContainerController controller = instance.GetComponent<BreachContainerController>();
                    controller?.ConfigureRuntime(metadata.containerSize);
                    StripPlacementPreview(instance);
                    instance.SetActive(true);
                    return instance;
                }
            }

            return CreateBlacksiteContainerProxy(metadata.containerSize);
        }

        private static GameObject CreateBlacksiteContainerProxy(string containerSize)
        {
            GameObject proxy = GameObject.CreatePrimitive(PrimitiveType.Cube);
            proxy.name = "Y4NGZDebug_BlacksiteContainerProxy";
            // #1143: read the container's own dimensions instead of restating them, so a change to
            // the shell height cannot leave this preview a different box than the one
            // BreachContainerController actually spawns.
            BreachContainerController.ResolveContainerDimensions(
                AuthoredInteriorPlacementMetadata.NormalizeContainerSize(containerSize),
                out float width,
                out float height,
                out float length);
            proxy.transform.localScale = new Vector3(width, height, length);

            TintPrimitive(proxy, new Color(0.42f, 0.08f, 0.045f, 1f));
            StripPlacementPreview(proxy);
            return proxy;
        }

        private static GameObject CreateCctvCameraPreview()
        {
            GameObject root = new GameObject("Y4NGZDebug_CctvCameraPreview");

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "CameraBody";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0f, 0f);
            body.transform.localScale = new Vector3(0.32f, 0.22f, 0.22f);
            TintPrimitive(body, new Color(0.08f, 0.09f, 0.10f, 1f));

            GameObject lens = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            lens.name = "CameraLens";
            lens.transform.SetParent(root.transform, false);
            lens.transform.localPosition = new Vector3(0f, 0f, 0.18f);
            lens.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            lens.transform.localScale = new Vector3(0.10f, 0.12f, 0.10f);
            TintPrimitive(lens, new Color(0.02f, 0.03f, 0.04f, 1f));

            GameObject bracket = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bracket.name = "CameraBracket";
            bracket.transform.SetParent(root.transform, false);
            bracket.transform.localPosition = new Vector3(0f, 0.16f, -0.12f);
            bracket.transform.localScale = new Vector3(0.12f, 0.18f, 0.08f);
            TintPrimitive(bracket, new Color(0.34f, 0.36f, 0.34f, 1f));

            StripPlacementPreview(root);
            return root;
        }

        /// <summary>
        /// Collider-free, local-only pest-trap ghost, built through the shared assembler so the
        /// preview matches the real trap's proportions. Bypasses
        /// <see cref="PestControlTrapAssembler.BuildLocalPreview"/> on purpose: that entry point
        /// clears and registers <c>PestControlTrapAssembler.Current</c>, and an edit-session ghost
        /// must never disturb a live trap or diagnostic preview.
        /// </summary>
        private static GameObject CreatePestTrapPreview()
        {
            PestControlTrapPreview preview = PestControlTrapAssembler.Build(
                null,
                "Y4NGZDebug_PestTrapPreview",
                Vector3.zero,
                0f,
                PestControlTrapPlacer.MinFootprintMeters,
                PestControlTrapAssembler.RequiredCeilingMeters,
                interactive: false);
            if (preview == null)
                return null;

            GameObject root = preview.gameObject;
            // Static ghost: no drop/raise simulation, and the preview component refreshes rope
            // ends in WORLD space every frame, which would pin them at the build origin while
            // the edit transform moves the root.
            DestroyUnityObject(preview);
            ConvertLineRenderersToLocalSpace(root);
            StripPlacementPreview(root);
            return root;
        }

        /// <summary>Rewrites every rope into its renderer's local space so the whole assembly
        /// follows the edit transform. Built at the origin with identity rotation, so the
        /// captured world positions convert losslessly.</summary>
        private static void ConvertLineRenderersToLocalSpace(GameObject root)
        {
            if (root == null)
                return;

            LineRenderer[] lines = root.GetComponentsInChildren<LineRenderer>(true);
            for (int i = 0; i < lines.Length; i++)
            {
                LineRenderer line = lines[i];
                if (line == null || !line.useWorldSpace)
                    continue;

                Transform lineTransform = line.transform;
                for (int p = 0; p < line.positionCount; p++)
                    line.SetPosition(p, lineTransform.InverseTransformPoint(line.GetPosition(p)));
                line.useWorldSpace = false;
            }
        }

        private static GameObject CreateWasteIncineratorPreview()
        {
            GameObject instance = WasteDisposalIncineratorVisualAdapter.BuildPlacementPreview();
            if (instance == null)
                return null;

            StripPlacementPreview(instance);
            instance.SetActive(true);
            return instance;
        }

        /// <summary>
        /// Fallback fixture preview factory for mainframe/vault (#545). LethalCCTV owns the real
        /// factory and registers it with the fixtures' actual prefabs; this one exists so an
        /// install without LethalCCTV still gets a proportioned ghost instead of a null preview
        /// that aborts the capture UI's edit session. Registered at bootstrap only while the hook
        /// is unclaimed, and LethalCCTV's later assignment overwrites it.
        /// </summary>
        internal static GameObject CreateFixtureFallbackPreview(string objectKind, string metadataJson)
        {
            switch (AuthoredInteriorPlacementKinds.Normalize(objectKind))
            {
                case AuthoredInteriorPlacementKinds.Mainframe:
                    return CreateFixtureFallbackProxy(
                        "Y4NGZDebug_MainframeProxy",
                        FixtureFootprint.Mainframe(0f).Size,
                        new Color(0.10f, 0.16f, 0.22f, 1f));
                case AuthoredInteriorPlacementKinds.Vault:
                    return CreateFixtureFallbackProxy(
                        "Y4NGZDebug_CompanyStashProxy",
                        FixtureFootprint.Vault(0f).Size,
                        new Color(0.24f, 0.20f, 0.08f, 1f));
                default:
                    return null;
            }
        }

        private static GameObject CreateFixtureFallbackProxy(string proxyName, Vector3 size, Color color)
        {
            GameObject proxy = GameObject.CreatePrimitive(PrimitiveType.Cube);
            proxy.name = proxyName;
            proxy.transform.localScale = size;
            // Footprint sizes are floor-anchored; the cube pivot is centered, so lift it by half.
            GameObject root = new GameObject(proxyName + "Root");
            proxy.transform.SetParent(root.transform, false);
            proxy.transform.localPosition = new Vector3(0f, size.y * 0.5f, 0f);
            TintPrimitive(proxy, color);
            StripPlacementPreview(root);
            return root;
        }

        private static void StripPlacementPreview(GameObject root)
        {
            if (root == null)
                return;

            Component[] components = root.GetComponentsInChildren<Component>(true);
            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null || component is Transform)
                    continue;

                if (component is Collider collider)
                {
                    collider.enabled = false;
                    DestroyUnityObject(collider);
                    continue;
                }

                if (component is NetworkObject ||
                    component is NetworkBehaviour ||
                    component is Rigidbody ||
                    IsNetworkTransformComponent(component))
                {
                    if (component is Behaviour behaviour)
                        behaviour.enabled = false;

                    DestroyUnityObject(component);
                }
            }
        }

        private static bool IsNetworkTransformComponent(Component component)
        {
            Type type = component?.GetType();
            while (type != null)
            {
                if (type.Name.IndexOf("NetworkTransform", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                type = type.BaseType;
            }

            return false;
        }

        private static void TintPrimitive(GameObject go, Color color)
        {
            Renderer renderer = go != null ? go.GetComponent<Renderer>() : null;
            if (renderer == null)
                return;

            Shader shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            Material material = new Material(shader);
            material.hideFlags = HideFlags.HideAndDontSave;
            material.color = color;
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            if (material.HasProperty("_UnlitColor"))
                material.SetColor("_UnlitColor", color);
            renderer.sharedMaterial = material;
        }

        private static void DestroyUnityObject(UnityEngine.Object obj)
        {
            if (obj == null)
                return;

            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
