using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Contracts.WasteDisposal
{
    /// <summary>
    /// Resolves replaceable art into stable gameplay roles. Network state, collision, and
    /// interactions remain code-owned and do not depend on any particular FBX hierarchy.
    /// </summary>
    internal static class WasteDisposalIncineratorVisualAdapter
    {
        private const int InteractLayerFallback = 9;
        private const int ScanNodeLayerFallback = 22;
        private const float DoorInteractionFrontOffsetMeters = 0.28f;
        private const float DoorInteractionMinimumDepthMeters = 0.65f;
        private static readonly string[] FeedDoorAliases = { "INC_FeedDoor", "FeedDoor", "HopperDoor" };
        private static readonly string[] BurnLeverAliases = { "INC_BurnLever", "BurnLever", "StartLever" };
        private static readonly string[] StartButtonAliases = { "INC_ButtonStart", "StartButton", "ButtonStart" };
        private static readonly string[] StopButtonAliases = { "INC_ButtonStop", "StopButton", "ButtonStop" };
        private static readonly string[] GaugeNeedleAliases = { "INC_GaugeNeedle", "GaugeNeedle" };
        private static readonly string[] FanRotorAliases = { "INC_FanRotor", "FanRotor", "BlowerRotor" };
        private static readonly string[] StackDamperAliases = { "INC_StackDamper", "StackDamper" };
        private static readonly string[] AshDoorAliases = { "INC_AshDoor", "AshDoor" };
        private static readonly string[] ScreenAliases = { "INC_Screen", "Screen", "Monitor" };
        private static readonly string[] EmberAliases = { "INC_EmberBed", "EmberBed", "BurnBed" };
        private static readonly string[] FireSocketAliases = { "SOCKET_FireFX", "FireFxSocket", "FireFX" };
        private static readonly string[] SmokeSocketAliases = { "SOCKET_SmokeFX", "SmokeFxSocket", "SmokeFX" };
        private static readonly string[] PassiveLightSocketAliases = { "SOCKET_LightPassive", "PassiveLightSocket" };
        private static readonly string[] BurnLightSocketAliases = { "SOCKET_LightBurn", "BurnLightSocket" };
        private static readonly string[] AudioSocketAliases = { "SOCKET_Audio", "AudioSocket" };
        private static readonly string[] MarkerSocketAliases = { "SOCKET_CCTVMarker", "CctvMarkerSocket", "MarkerSocket" };

        private static Material _darkMetal;
        private static Material _hotMetal;
        private static Material _green;
        private static Material _red;
        private static Material _screen;
        private static Material _ember;

        internal static WasteDisposalIncineratorRig Build(
            Transform networkRoot,
            float width,
            float height,
            float depth,
            Vector3 captureCenter,
            Vector3 captureSize)
        {
            if (networkRoot == null)
                return null;

            Transform visualRoot = new GameObject("IncineratorVisual").transform;
            visualRoot.SetParent(networkRoot, false);
            GameObject authored = InstantiateAuthoredVisual(visualRoot);
            WasteDisposalIncineratorBindings bindings = authored != null
                ? authored.GetComponentInChildren<WasteDisposalIncineratorBindings>(true)
                : null;

            // #716 A4: the alias fallbacks used to run one recursive whole-hierarchy walk each
            // (up to 48 on a prefab with no bindings component), all on the spawn frame. One
            // walk now builds a name map and every alias lookup is a dictionary hit.
            Dictionary<string, Transform> authoredNodes = BuildNodeMap(authored);
            Transform feedDoor = bindings?.FeedDoor ?? FindAny(authoredNodes, FeedDoorAliases);
            Transform burnLever = bindings?.BurnLever ?? FindAny(authoredNodes, BurnLeverAliases);
            Transform startButton = bindings?.StartButton ?? FindAny(authoredNodes, StartButtonAliases);
            Transform stopButton = bindings?.StopButton ?? FindAny(authoredNodes, StopButtonAliases);
            Transform gaugeNeedle = bindings?.GaugeNeedle ?? FindAny(authoredNodes, GaugeNeedleAliases);
            Transform fanRotor = bindings?.FanRotor ?? FindAny(authoredNodes, FanRotorAliases);
            Transform stackDamper = bindings?.StackDamper ?? FindAny(authoredNodes, StackDamperAliases);
            Transform ashDoor = bindings?.AshDoor ?? FindAny(authoredNodes, AshDoorAliases);
            Transform fireSocket = bindings?.FireFxSocket ?? FindAny(authoredNodes, FireSocketAliases);
            Transform smokeSocket = bindings?.SmokeFxSocket ?? FindAny(authoredNodes, SmokeSocketAliases);
            Transform passiveLightSocket = bindings?.PassiveLightSocket ?? FindAny(authoredNodes, PassiveLightSocketAliases);
            Transform burnLightSocket = bindings?.BurnLightSocket ?? FindAny(authoredNodes, BurnLightSocketAliases);
            Transform audioSocket = bindings?.AudioSocket ?? FindAny(authoredNodes, AudioSocketAliases);
            Transform markerSocket = bindings?.CctvMarkerSocket ?? FindAny(authoredNodes, MarkerSocketAliases);
            Renderer screenRenderer = bindings?.ScreenRenderer ?? RendererFor(FindAny(authoredNodes, ScreenAliases));
            Renderer emberRenderer = bindings?.EmberRenderer ?? RendererFor(FindAny(authoredNodes, EmberAliases));

            if (authored == null)
            {
                BuildProceduralVisual(visualRoot, width, height, depth, captureCenter, captureSize,
                    out feedDoor, out burnLever, out startButton, out stopButton, out gaugeNeedle,
                    out fanRotor, out stackDamper, out ashDoor, out fireSocket, out smokeSocket,
                    out passiveLightSocket, out burnLightSocket, out audioSocket, out markerSocket,
                    out screenRenderer, out emberRenderer);
            }

            feedDoor = feedDoor ?? CreateRole(visualRoot, "FallbackFeedDoor", captureCenter + Vector3.up * captureSize.y * 0.5f);
            burnLever = burnLever ?? CreateRole(visualRoot, "FallbackBurnLever", new Vector3(width * 0.38f, height * 0.65f, depth * 0.48f));
            startButton = startButton ?? CreateRole(visualRoot, "FallbackStartButton", new Vector3(width * 0.33f, height * 0.68f, depth * 0.48f));
            stopButton = stopButton ?? CreateRole(visualRoot, "FallbackStopButton", new Vector3(width * 0.43f, height * 0.68f, depth * 0.48f));
            gaugeNeedle = gaugeNeedle ?? CreateRole(visualRoot, "FallbackGaugeNeedle", new Vector3(0f, height * 0.72f, depth * 0.51f));
            fanRotor = fanRotor ?? CreateRole(visualRoot, "FallbackFanRotor", new Vector3(0f, height * 0.55f, -depth * 0.51f));
            stackDamper = stackDamper ?? CreateRole(visualRoot, "FallbackStackDamper", new Vector3(0f, height, -depth * 0.28f));
            ashDoor = ashDoor ?? CreateRole(visualRoot, "FallbackAshDoor", new Vector3(0f, height * 0.2f, depth * 0.51f));
            fireSocket = fireSocket ?? CreateRole(visualRoot, "FallbackFireFxSocket", captureCenter);
            smokeSocket = smokeSocket ?? CreateRole(visualRoot, "FallbackSmokeFxSocket", new Vector3(0f, height, 0f));
            passiveLightSocket = passiveLightSocket ?? CreateRole(visualRoot, "FallbackPassiveLightSocket", captureCenter + new Vector3(0f, 0.2f, depth * 0.5f));
            burnLightSocket = burnLightSocket ?? CreateRole(visualRoot, "FallbackBurnLightSocket", captureCenter + new Vector3(0f, 0.35f, depth * 0.55f));
            audioSocket = audioSocket ?? CreateRole(visualRoot, "FallbackAudioSocket", captureCenter);
            markerSocket = markerSocket ?? CreateRole(visualRoot, "FallbackCctvMarkerSocket", new Vector3(0f, height * 0.7f, depth * 0.5f));

            BuildStableCollision(networkRoot, width, height, depth, bindings?.CollisionBoxes);
            InteractTrigger startTrigger = BuildInteraction(networkRoot, "StartInteraction", startButton, new Vector3(0.24f, 0.24f, 0.32f), "Start incinerator");
            InteractTrigger stopTrigger = BuildInteraction(networkRoot, "StopInteraction", stopButton, new Vector3(0.24f, 0.24f, 0.32f), "Pause incinerator");
            InteractTrigger doorTrigger = BuildDoorInteraction(networkRoot, feedDoor, captureCenter, captureSize);
            Transform repairAnchor = screenRenderer != null ? screenRenderer.transform : burnLever;
            InteractTrigger repairTrigger = BuildInteraction(networkRoot, "RepairInteraction", repairAnchor, new Vector3(0.58f, 0.52f, 0.42f), "Repair incinerator : Hold [LMB]");
            Vector3 placePoint = captureCenter + new Vector3(0f, 0f, depth * 0.46f);
            InteractTrigger placeTrigger = BuildInteraction(networkRoot, "PlaceScrapInteraction", placePoint, new Vector3(captureSize.x * 0.82f, captureSize.y * 0.75f, 0.42f), "Place item");
            BuildScanNode(networkRoot, markerSocket);

            GameObject audioObject = new GameObject("IncineratorAudio");
            audioObject.transform.SetParent(networkRoot, false);
            WasteDisposalIncineratorAudio audio = audioObject.AddComponent<WasteDisposalIncineratorAudio>();
            audio.Initialize(audioSocket);
            var effects = new WasteDisposalIncineratorEffects(fireSocket, smokeSocket, passiveLightSocket, burnLightSocket, emberRenderer);
            WasteDisposalIncineratorMotionProfile motion = bindings != null
                ? new WasteDisposalIncineratorMotionProfile(
                    bindings.FeedDoorOpenEuler,
                    bindings.FeedDoorTravelSeconds,
                    bindings.ButtonPressedLocalOffset,
                    bindings.BurnLeverActivatedEuler,
                    bindings.GaugeColdEuler,
                    bindings.GaugeHotEuler,
                    bindings.StackDamperOpenEuler,
                    bindings.AshDoorCompleteEuler)
                : WasteDisposalIncineratorMotionProfile.Default;

            var rig = new WasteDisposalIncineratorRig(
                visualRoot, feedDoor, burnLever, startButton, stopButton, gaugeNeedle, fanRotor,
                stackDamper, ashDoor, markerSocket, startTrigger, stopTrigger, doorTrigger,
                repairTrigger, placeTrigger, screenRenderer, emberRenderer, effects, audio, motion);
            rig.WireInteraction();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.WasteDisposal] Incinerator rig assembled with {(authored != null ? "authored" : "procedural")} visuals.");
            return rig;
        }

        private static GameObject InstantiateAuthoredVisual(Transform parent)
        {
            GameObject prefab = ContractAssetLoader.WasteDisposalIncineratorPrefab;
            if (prefab == null) return null;
            GameObject instance = UnityEngine.Object.Instantiate(prefab, parent, false);
            instance.name = "AuthoredIncineratorVisual";
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++) if (colliders[i] != null) colliders[i].enabled = false;
            NetworkObject[] networkObjects = instance.GetComponentsInChildren<NetworkObject>(true);
            for (int i = 0; i < networkObjects.Length; i++) if (networkObjects[i] != null) networkObjects[i].enabled = false;
            return instance;
        }

        private static void BuildProceduralVisual(
            Transform parent, float width, float height, float depth, Vector3 captureCenter, Vector3 captureSize,
            out Transform feedDoor, out Transform burnLever, out Transform startButton, out Transform stopButton,
            out Transform gaugeNeedle, out Transform fanRotor, out Transform stackDamper, out Transform ashDoor,
            out Transform fireSocket, out Transform smokeSocket, out Transform passiveLightSocket,
            out Transform burnLightSocket, out Transform audioSocket, out Transform markerSocket,
            out Renderer screenRenderer, out Renderer emberRenderer)
        {
            float wall = Mathf.Clamp(width * 0.075f, 0.12f, 0.22f);
            CreateBox(parent, "BodyBack", new Vector3(0f, height * 0.5f, -depth * 0.42f), new Vector3(width, height, wall), DarkMetal);
            CreateBox(parent, "BodyLeft", new Vector3(-width * 0.44f, height * 0.48f, 0f), new Vector3(wall, height * 0.96f, depth), DarkMetal);
            CreateBox(parent, "BodyRight", new Vector3(width * 0.44f, height * 0.48f, 0f), new Vector3(wall, height * 0.96f, depth), DarkMetal);
            CreateBox(parent, "BodyTop", new Vector3(0f, height * 0.94f, 0f), new Vector3(width * 0.9f, wall, depth), DarkMetal);
            Transform bed = CreateBox(parent, "INC_EmberBed", captureCenter - Vector3.up * captureSize.y * 0.42f, new Vector3(width * 0.62f, 0.04f, depth * 0.55f), Ember);
            emberRenderer = RendererFor(bed);
            feedDoor = CreateRole(parent, "INC_FeedDoor", captureCenter + new Vector3(width * 0.32f, captureSize.y * 0.5f, depth * 0.42f));
            CreateBox(feedDoor, "FeedDoorPanel", new Vector3(-width * 0.32f, -captureSize.y * 0.48f, 0f), new Vector3(width * 0.7f, captureSize.y, wall), HotMetal);
            burnLever = CreateRole(parent, "INC_BurnLever", new Vector3(width * 0.38f, height * 0.68f, depth * 0.48f));
            startButton = CreateBox(parent, "INC_ButtonStart", new Vector3(width * 0.33f, height * 0.68f, depth * 0.49f), new Vector3(0.12f, 0.12f, 0.08f), Green);
            stopButton = CreateBox(parent, "INC_ButtonStop", new Vector3(width * 0.43f, height * 0.68f, depth * 0.49f), new Vector3(0.12f, 0.12f, 0.08f), Red);
            Transform screen = CreateBox(parent, "INC_Screen", new Vector3(width * 0.38f, height * 0.78f, depth * 0.49f), new Vector3(0.46f, 0.31f, 0.025f), Screen);
            screenRenderer = RendererFor(screen);
            gaugeNeedle = CreateRole(parent, "INC_GaugeNeedle", screen.localPosition);
            fanRotor = CreateRole(parent, "INC_FanRotor", new Vector3(0f, height * 0.55f, -depth * 0.51f));
            stackDamper = CreateRole(parent, "INC_StackDamper", new Vector3(0f, height, -depth * 0.28f));
            ashDoor = CreateRole(parent, "INC_AshDoor", new Vector3(0f, height * 0.2f, depth * 0.51f));
            fireSocket = CreateRole(parent, "SOCKET_FireFX", captureCenter);
            smokeSocket = CreateRole(parent, "SOCKET_SmokeFX", new Vector3(0f, height, -depth * 0.25f));
            passiveLightSocket = CreateRole(parent, "SOCKET_LightPassive", captureCenter + new Vector3(0f, 0.15f, depth * 0.52f));
            burnLightSocket = CreateRole(parent, "SOCKET_LightBurn", captureCenter + new Vector3(0f, 0.28f, depth * 0.56f));
            audioSocket = CreateRole(parent, "SOCKET_Audio", captureCenter);
            markerSocket = CreateRole(parent, "SOCKET_CCTVMarker", new Vector3(0f, height * 0.7f, depth * 0.5f));
        }

        private static void BuildStableCollision(
            Transform parent,
            float width,
            float height,
            float depth,
            WasteDisposalCollisionBoxBinding[] boundBoxes)
        {
            Transform collision = new GameObject("IncineratorCollision").transform;
            collision.SetParent(parent, false);
            if (TryBuildBoundCollision(collision, boundBoxes))
                return;

            WasteDisposalCollisionBoxBinding[] semantic = BuildSemanticCollisionBoxes(width, height, depth);
            for (int i = 0; i < semantic.Length; i++)
                AddCollider(collision, semantic[i].Name, semantic[i].Center, semantic[i].Size);
        }

        /// <summary>
        /// The code-owned semantic collision profile, not a box around renderer bounds. The main
        /// furnace sits left of centre while the battery and control pod occupy only small parts
        /// of the right side. Keeping those as separate solids leaves the
        /// visibly open right-front recess traversable. Shared by
        /// <see cref="BuildStableCollision"/> and the authored-pose validation in
        /// <see cref="WasteDisposalContract"/> so they cannot disagree.
        /// </summary>
        internal static WasteDisposalCollisionBoxBinding[] BuildSemanticCollisionBoxes(float width, float height, float depth)
        {
            return new[]
            {
                new WasteDisposalCollisionBoxBinding
                {
                    Name = "FurnaceCoreUpper",
                    Center = new Vector3(-width * 0.102f, height * 0.51f, 0f),
                    Size = new Vector3(width * 0.72f, height * 0.61f, depth * 0.95f)
                },
                new WasteDisposalCollisionBoxBinding
                {
                    Name = "FurnaceCoreBase",
                    Center = new Vector3(-width * 0.102f, height * 0.055f, 0f),
                    Size = new Vector3(width * 0.78f, height * 0.11f, depth * 0.96f)
                },
                new WasteDisposalCollisionBoxBinding
                {
                    Name = "BatteryModule",
                    Center = new Vector3(width * 0.40f, height * 0.09f, -depth * 0.24f),
                    Size = new Vector3(width * 0.19f, height * 0.18f, depth * 0.45f)
                },
                new WasteDisposalCollisionBoxBinding
                {
                    Name = "ControlPod",
                    Center = new Vector3(width * 0.36f, height * 0.65f, depth * 0.015f),
                    Size = new Vector3(width * 0.15f, height * 0.27f, depth * 0.09f)
                }
            };
        }

        /// <summary>
        /// The collision profile the spawned incinerator would actually stand behind at the given
        /// proportions: the authored <see cref="WasteDisposalIncineratorBindings.CollisionBoxes"/>
        /// when the bundled prefab carries a valid set, otherwise the code-owned semantic
        /// compound. Read from the prefab, so authored-pose validation can overlap-test the real
        /// footprint before anything is instantiated or network-spawned.
        /// </summary>
        internal static WasteDisposalCollisionBoxBinding[] ResolveEffectiveCollisionBoxes(float width, float height, float depth)
        {
            GameObject prefab = ContractAssetLoader.WasteDisposalIncineratorPrefab;
            WasteDisposalIncineratorBindings bindings = prefab != null
                ? prefab.GetComponentInChildren<WasteDisposalIncineratorBindings>(true)
                : null;
            WasteDisposalCollisionBoxBinding[] boxes = bindings != null ? bindings.CollisionBoxes : null;
            return AreValidCollisionBoxes(boxes) ? boxes : BuildSemanticCollisionBoxes(width, height, depth);
        }

        private static bool AreValidCollisionBoxes(WasteDisposalCollisionBoxBinding[] boxes)
        {
            if (boxes == null || boxes.Length == 0)
                return false;

            for (int i = 0; i < boxes.Length; i++)
            {
                if (!IsPositiveFinite(boxes[i].Size) || !IsFinite(boxes[i].Center))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Collider-free, local-only ghost of the incinerator for the authored-placement capture
        /// UI: the authored visual when the bundle carries one (already stripped of colliders and
        /// NetworkObjects by <see cref="InstantiateAuthoredVisual"/>), else the same procedural
        /// box compound the runtime rig falls back to. No interactions, collision, or network
        /// state are built — the ghost only has to look right and follow the edit transform.
        /// </summary>
        internal static GameObject BuildPlacementPreview()
        {
            WasteDisposalBalanceConfig balance = ContractsBootstrap.WasteDisposalBalance;
            float width = balance?.FixtureWidthMeters.Value ?? 2.4f;
            float height = balance?.FixtureHeightMeters.Value ?? 2.85f;
            float depth = balance?.FixtureDepthMeters.Value ?? 1.75f;
            Vector3 captureCenter = balance?.CaptureCenterLocal ?? new Vector3(0f, 1.05f, 0.25f);
            Vector3 captureSize = balance?.CaptureSizeMeters ?? new Vector3(1.55f, 1f, 1.1f);

            GameObject root = new GameObject("Y4NGZDebug_WasteIncineratorPreview");
            GameObject authored = InstantiateAuthoredVisual(root.transform);
            if (authored == null)
            {
                BuildProceduralVisual(root.transform, width, height, depth, captureCenter, captureSize,
                    out _, out _, out _, out _, out _, out _, out _, out _,
                    out _, out _, out _, out _, out _, out _, out _, out _);
            }

            return root;
        }

        private static bool TryBuildBoundCollision(
            Transform parent,
            WasteDisposalCollisionBoxBinding[] boxes)
        {
            if (boxes == null || boxes.Length == 0)
                return false;

            for (int i = 0; i < boxes.Length; i++)
            {
                if (!IsPositiveFinite(boxes[i].Size) || !IsFinite(boxes[i].Center))
                {
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.WasteDisposal] Ignoring invalid authored collision profile at index {i}; using semantic fallback.");
                    return false;
                }
            }

            for (int i = 0; i < boxes.Length; i++)
            {
                string name = string.IsNullOrWhiteSpace(boxes[i].Name)
                    ? $"AuthoredCollision_{i:00}"
                    : boxes[i].Name.Trim();
                AddCollider(parent, name, boxes[i].Center, boxes[i].Size);
            }
            return true;
        }

        private static bool IsPositiveFinite(Vector3 value)
        {
            return IsFinite(value)
                   && value.x > 0.001f
                   && value.y > 0.001f
                   && value.z > 0.001f;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                   && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                   && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static InteractTrigger BuildDoorInteraction(
            Transform networkRoot,
            Transform door,
            Vector3 captureCenter,
            Vector3 captureSize)
        {
            if (networkRoot == null || door == null)
                return null;

            Renderer[] renderers = door.GetComponentsInChildren<Renderer>(true);
            Bounds localBounds = default;
            bool hasRendererBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                Bounds bounds = renderer.bounds;
                if (!hasRendererBounds)
                {
                    localBounds = new Bounds(door.InverseTransformPoint(bounds.center), Vector3.zero);
                    hasRendererBounds = true;
                }

                Vector3 min = bounds.min;
                Vector3 max = bounds.max;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 worldCorner = new Vector3(
                        (corner & 1) == 0 ? min.x : max.x,
                        (corner & 2) == 0 ? min.y : max.y,
                        (corner & 4) == 0 ? min.z : max.z);
                    localBounds.Encapsulate(door.InverseTransformPoint(worldCorner));
                }
            }

            Vector3 panelWorldCenter = hasRendererBounds
                ? door.TransformPoint(localBounds.center)
                : door.position;
            Vector3 rootLocalCenter = networkRoot.InverseTransformPoint(panelWorldCenter);
            // The FBX role is a hinge pivot. Force the usable interaction slab over the
            // chamber opening instead of allowing a missing/sparse renderer to collapse
            // the target back onto that pivot.
            rootLocalCenter.x = captureCenter.x;
            rootLocalCenter.y = captureCenter.y;
            panelWorldCenter = networkRoot.TransformPoint(rootLocalCenter)
                               + networkRoot.forward * DoorInteractionFrontOffsetMeters;

            Vector3 absoluteScale = new Vector3(
                Mathf.Max(0.001f, Mathf.Abs(door.lossyScale.x)),
                Mathf.Max(0.001f, Mathf.Abs(door.lossyScale.y)),
                Mathf.Max(0.001f, Mathf.Abs(door.lossyScale.z)));
            Vector3 rendererSize = hasRendererBounds ? localBounds.size : Vector3.zero;
            Vector3 size = new Vector3(
                Mathf.Max(rendererSize.x + 0.24f, captureSize.x * 1.05f / absoluteScale.x),
                Mathf.Max(rendererSize.y + 0.24f, captureSize.y * 1.15f / absoluteScale.y),
                Mathf.Max(rendererSize.z + 0.42f, DoorInteractionMinimumDepthMeters / absoluteScale.z));
            Vector3 localCenter = door.InverseTransformPoint(panelWorldCenter);

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.WasteDisposal] Door interaction centered over chamber: renderers={renderers.Length}, rootCenter={rootLocalCenter}, localSize={size}.");
            return BuildInteraction(door, "DoorInteraction", localCenter, size, "Open feed door");
        }

        private static InteractTrigger BuildInteraction(Transform parent, string name, Transform anchor, Vector3 size, string tip)
        {
            Vector3 localPosition = anchor != null ? parent.InverseTransformPoint(anchor.position) : Vector3.zero;
            return BuildInteraction(parent, name, localPosition, size, tip);
        }

        private static InteractTrigger BuildInteraction(Transform parent, string name, Vector3 localPosition, Vector3 size, string tip)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = localPosition;
            obj.layer = ResolveLayer("InteractableObject", InteractLayerFallback);
            SetTagSafe(obj, "InteractTrigger");
            BoxCollider collider = obj.AddComponent<BoxCollider>();
            collider.size = size;
            collider.isTrigger = true;
            InteractTrigger trigger = obj.AddComponent<InteractTrigger>();
            trigger.interactable = false;
            trigger.oneHandedItemAllowed = true;
            trigger.twoHandedItemAllowed = true;
            trigger.holdInteraction = false;
            trigger.interactCooldown = true;
            trigger.cooldownTime = 0.2f;
            trigger.hoverTip = tip;
            trigger.disabledHoverTip = tip;
            trigger.disableTriggerMesh = true;
            trigger.onInteract = new InteractEvent();
            trigger.onInteractEarly = new InteractEvent();
            trigger.onInteractEarlyOtherClients = new InteractEvent();
            trigger.onStopInteract = new InteractEvent();
            trigger.onCancelAnimation = new InteractEvent();
            trigger.holdingInteractEvent = new InteractEventFloat();
            return trigger;
        }

        private static void BuildScanNode(Transform parent, Transform markerSocket)
        {
            GameObject obj = new GameObject("WasteDisposalScanNode");
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = markerSocket != null ? parent.InverseTransformPoint(markerSocket.position) : Vector3.up * 1.5f;
            obj.layer = ResolveLayer("ScanNode", ScanNodeLayerFallback);
            SphereCollider collider = obj.AddComponent<SphereCollider>();
            collider.radius = 0.7f;
            collider.isTrigger = true;
            ScanNodeProperties node = obj.AddComponent<ScanNodeProperties>();
            node.headerText = "WASTE INCINERATOR";
            node.subText = "Company disposal unit";
            node.maxRange = 28;
            node.minRange = 1;
            node.requiresLineOfSight = false;
            node.nodeType = 0;
        }

        /// <summary>
        /// One depth-first walk of the authored hierarchy, keyed by node name. Pre-order
        /// insertion with first-writer-wins preserves the shallowest-then-first-child result
        /// the previous per-alias recursive search returned.
        /// </summary>
        private static Dictionary<string, Transform> BuildNodeMap(GameObject root)
        {
            var map = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            if (root != null) CollectNodes(root.transform, map);
            return map;
        }

        private static void CollectNodes(Transform node, Dictionary<string, Transform> map)
        {
            if (node == null) return;
            if (!map.ContainsKey(node.name)) map[node.name] = node;
            for (int i = 0; i < node.childCount; i++)
                CollectNodes(node.GetChild(i), map);
        }

        private static Transform FindAny(Dictionary<string, Transform> nodes, string[] aliases)
        {
            if (nodes == null || nodes.Count == 0 || aliases == null) return null;
            for (int i = 0; i < aliases.Length; i++)
            {
                if (nodes.TryGetValue(aliases[i], out Transform found) && found != null) return found;
            }
            return null;
        }

        private static Renderer RendererFor(Transform value) => value != null ? value.GetComponent<Renderer>() ?? value.GetComponentInChildren<Renderer>(true) : null;

        private static Transform CreateRole(Transform parent, string name, Vector3 localPosition)
        {
            Transform role = new GameObject(name).transform;
            role.SetParent(parent, false);
            role.localPosition = localPosition;
            return role;
        }

        private static Transform CreateBox(Transform parent, string name, Vector3 localPosition, Vector3 localScale, Material material)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent, false);
            box.transform.localPosition = localPosition;
            box.transform.localScale = localScale;
            Collider collider = box.GetComponent<Collider>();
            if (collider != null) collider.enabled = false;
            Renderer renderer = box.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = material;
            return box.transform;
        }

        private static void AddCollider(Transform parent, string name, Vector3 localPosition, Vector3 size)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = localPosition;
            obj.AddComponent<BoxCollider>().size = size;
        }

        private static Material CreateMaterial(string name, Color color, float metallic, Color emission)
        {
            Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Material material = new Material(shader) { name = name, color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.3f);
            if (emission.maxColorComponent > 0f)
            {
                material.EnableKeyword("_EMISSION");
                if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", emission);
                if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", emission);
            }
            return material;
        }

        private static Material DarkMetal => _darkMetal ?? (_darkMetal = CreateMaterial("WasteDisposal_DarkMetal", new Color(0.12f, 0.13f, 0.13f), 0.72f, Color.black));
        private static Material HotMetal => _hotMetal ?? (_hotMetal = CreateMaterial("WasteDisposal_HotMetal", new Color(0.25f, 0.22f, 0.18f), 0.65f, Color.black));
        private static Material Green => _green ?? (_green = CreateMaterial("INC_ButtonStartMat", new Color(0.04f, 0.35f, 0.08f), 0.3f, new Color(0.05f, 0.4f, 0.08f)));
        private static Material Red => _red ?? (_red = CreateMaterial("INC_ButtonStopMat", new Color(0.38f, 0.035f, 0.02f), 0.3f, new Color(0.45f, 0.035f, 0.02f)));
        private static Material Screen => _screen ?? (_screen = CreateMaterial("INC_ScreenMat", new Color(0.005f, 0.02f, 0.01f), 0.05f, new Color(0.02f, 0.15f, 0.04f)));
        private static Material Ember => _ember ?? (_ember = CreateMaterial("INC_EmberMat", new Color(0.09f, 0.025f, 0.01f), 0.1f, new Color(0.5f, 0.04f, 0.01f)));

        private static int ResolveLayer(string name, int fallback)
        {
            int layer = LayerMask.NameToLayer(name);
            return layer >= 0 ? layer : fallback;
        }

        private static void SetTagSafe(GameObject obj, string tag)
        {
            try { obj.tag = tag; } catch { }
        }
    }
}
