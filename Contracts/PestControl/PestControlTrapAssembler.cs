using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Interior;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// Builds the Pest Control hog trap: a suspended authored corral over a floor platform,
    /// with rope lines to four corner posts and a lever on the entrance-facing side. Missing
    /// or stale local art falls back to the deterministic primitive assembly.
    ///
    /// The GEOMETRY is always client-local — every peer assembles its own copy from the synced
    /// placement parameters (see <see cref="PestControlTrapController"/>). Only the motion state
    /// and the capture outcome cross the wire. The assembler itself knows nothing about
    /// networking; <paramref name="interactive"/> is what separates the real in-world trap
    /// (colliders, NavMesh carving, a working lever) from the collider-free debug preview.
    /// </summary>
    internal static partial class PestControlTrapAssembler
    {
        internal const string RootName = "Y4NGZ_PestControlTrapPreview";
        internal const string NetworkedRootName = "PestControlTrapAssembly";

        // --- geometry constants (Phase 5: config) --------------------------------
        private const float PlatformThickness = 0.10f;
        private const float PostSection = 0.15f;
        private const float PulleySection = 0.22f;
        private const float TargetPanelWidth = 1.00f;
        private const int BarsPerPanel = 5;
        private const float RailThickness = 0.06f;
        private const float BarThickness = 0.05f;
        private const float PanelDepth = 0.08f;

        /// <summary>Dropped height of the corral's bottom edge — resting on the platform's top face.</summary>
        internal const float FenceDroppedBottom = PlatformThickness * 0.5f;
        /// <summary>The authored fence sits 0.15 m inside each platform edge.</summary>
        internal const float FenceInsetMeters = 0.30f;


        // --- adaptive cage sizing ------------------------------------------------
        // The ceiling budget, spent top down: a margin, then the lift rig, then the cage, then
        // the walk-under gap. The rig term is what the earlier "split the headroom in half"
        // version had no concept of, and it is not optional -- a winch head is a real object.
        // Without it a 4.5 m room put the cage top at 4.25 and the post top at 4.35, so the
        // 0.30 m head modelled in authoring/pestcontrol/PestControlTrap.blend hung 0.17 m
        // through the ceiling and its drum sat BELOW the cage's own rope eye, which is
        // geometrically impossible to string. Reserving the rig first is what makes the
        // assembly buildable at every clearance the placer will accept.
        internal const float MinCageHeight = 1.80f;
        internal const float MaxCageHeight = 2.30f;

        /// <summary>Authored height of the winch head that caps each post.</summary>
        internal const float WinchHeadMeters = 0.30f;

        /// <summary>Drum-to-rope-eye slack: enough cable under the drum to read as suspended.</summary>
        private const float CableSlackMeters = 0.16f;

        /// <summary>Everything above the cage top: the winch head plus its visible cable.</summary>
        internal const float RigHeadroomMeters = WinchHeadMeters + CableSlackMeters;

        /// <summary>
        /// Head height the bait player (1.88 m) AND the quarry both have to clear to get under
        /// the raised cage. A cage nobody can walk under cannot be baited, so this never yields.
        /// </summary>
        internal const float MinWalkUnderMeters = 1.95f;

        /// <summary>Past this the cage is just parked at the ceiling; the rest becomes cable.</summary>
        private const float MaxRestBottomMeters = 3.00f;

        /// <summary>Keeps a tall room from growing a pointless length of bare post above the cage.</summary>
        private const float MaxCableRunMeters = 0.85f;

        /// <summary>Headroom the rig never eats into, so the winch heads clear the measured ceiling.</summary>
        private const float CeilingMargin = 0.12f;

        /// <summary>
        /// Lowest ceiling that still fits margin + rig + the shortest usable cage + the
        /// walk-under. <see cref="PestControlTrapPlacer.MinCeilingMeters"/> is derived from this
        /// so the probe can never hand back a tile the assembly cannot actually stand in.
        /// </summary>
        internal const float RequiredCeilingMeters =
            CeilingMargin + RigHeadroomMeters + MinCageHeight + MinWalkUnderMeters;

        /// <summary>
        /// Preferred clearance between the cage edge and the lever, giving the operator room to
        /// stand back from the corral. On the 6 m footprint the probe usually picks this puts the
        /// lever 5.75 m from centre, which in a corridor is through the wall — so
        /// <see cref="PestControlTrapAssembler.TryResolveLeverPose"/> treats it as the first rung
        /// of a ladder and walks inward to <see cref="LeverMinimumOutsetMeters"/>.
        /// </summary>
        internal const float LeverOutsetMeters = 2.75f;

        /// <summary>
        /// Tightest the lever may sit to the cage edge: half the lever reservation (0.75 m) plus a
        /// little, so the interaction envelope still clears the corral it opens.
        /// </summary>
        internal const float LeverMinimumOutsetMeters = 0.85f;
        internal const float LeverReservationSideMeters = 1.50f;
        internal const float LeverReservationHeightMeters = 2.10f;

        internal const int CeilingLampCount = 4;
        internal const float CeilingLampOutsetMeters = 0.95f;
        internal const float CeilingLampReservationSideMeters = 0.70f;
        internal const float CeilingLampReservationHeightMeters = 0.65f;

        /// <summary>
        /// Throw of the lever arm, degrees about its local X, swinging toward the player who
        /// stands on the arm's +Z side. The arm is authored UPRIGHT so pull 0 needs no offset and
        /// the player animation can drive the angle directly. Matches PestTrap_LeverArm and the
        /// PestTrap_LeverPull action in authoring/pestcontrol/PestControlTrap.blend.
        /// </summary>
        internal const float LeverPullDegrees = 78f;

        /// <summary>Height of the arm's pivot above the lever's floor plate.</summary>
        private const float LeverAxleHeight = 0.95f;

        /// <summary>How far in front of the handle the operator is planted for the throw.</summary>
        private const float LeverStandOffsetMeters = 0.85f;
        private const int InteractLayer = 9;
        private const int ScanNodeLayer = 22;

        private static readonly Color PlatformColor = new Color(0.26f, 0.25f, 0.23f, 1f);
        private static readonly Color PostColor = new Color(0.40f, 0.38f, 0.35f, 1f);
        private static readonly Color FrameColor = new Color(0.45f, 0.33f, 0.22f, 1f);
        private static readonly Color BarColor = new Color(0.48f, 0.46f, 0.43f, 1f);
        private static readonly Color LeverColor = new Color(0.55f, 0.30f, 0.16f, 1f);
        private static readonly Color RopeColor = new Color(0.62f, 0.55f, 0.38f, 1f);

        private static readonly Dictionary<Color, Material> MaterialCache = new Dictionary<Color, Material>();
        private static Material _ropeMaterial;

        /// <summary>The local-only debug preview, if one is standing. The networked trap is owned by its controller.</summary>
        internal static PestControlTrapPreview Current { get; private set; }

        internal static bool IsBuilt => Current != null;

        /// <summary>
        /// Single source of truth for the cage proportions. The placer's log, the debug
        /// visualization, and the assembly itself all derive from this so they cannot disagree.
        /// </summary>
        internal static void DeriveCageMetrics(
            float ceilingClearanceMeters,
            out float cageHeight,
            out float restBottom,
            out float postHeight)
        {
            float clearance = Mathf.Max(PestControlTrapPlacer.MinCeilingMeters, ceilingClearanceMeters);

            // Everything under the rig and above the floor is ours to divide.
            float usable = clearance - CeilingMargin - RigHeadroomMeters;

            // The walk-under gets paid first; the cage takes what is left, up to its cap. When a
            // room cannot afford both, the CAGE degrades, never the gap.
            cageHeight = Mathf.Clamp(usable - MinWalkUnderMeters, MinCageHeight, MaxCageHeight);
            restBottom = Mathf.Clamp(usable - cageHeight, FenceDroppedBottom + 0.4f, MaxRestBottomMeters);

            // Post shaft stops far enough down that the winch head still clears the ceiling.
            postHeight = Mathf.Min(
                clearance - CeilingMargin - WinchHeadMeters,
                restBottom + cageHeight + MaxCableRunMeters);
        }

        internal static void Clear()
        {
            if (Current != null)
                Object.Destroy(Current.gameObject);

            Current = null;
        }

        /// <summary>Collider-free local preview, tracked in <see cref="Current"/>. Used when there is no networked trap.</summary>
        internal static PestControlTrapPreview BuildLocalPreview(PestControlTrapPlacement placement)
        {
            Clear();
            if (placement == null || !placement.HasResult)
                return null;

            Current = Build(
                null,
                RootName,
                placement.Center,
                placement.Yaw,
                placement.FootprintMeters,
                placement.CeilingClearanceMeters,
                interactive: false);
            return Current;
        }

        /// <summary>
        /// Assembles the trap under <paramref name="parent"/> (or at the scene root when null).
        /// </summary>
        internal static PestControlTrapPreview Build(
            Transform parent,
            string rootName,
            Vector3 center,
            float yaw,
            float footprintMeters,
            float ceilingClearanceMeters,
            bool interactive)
        {
            DeriveCageMetrics(ceilingClearanceMeters, out float panelHeight, out float restBottom, out float postHeight);
            float side = Mathf.Clamp(
                footprintMeters,
                PestControlTrapPlacer.MinFootprintMeters * 0.5f,
                PestControlTrapPlacer.MaxFootprintMeters);
            float half = side * 0.5f;
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);

            var root = new GameObject(string.IsNullOrEmpty(rootName) ? RootName : rootName);
            if (parent != null)
                root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(center, rotation);
            if (TryBuildAuthored(
                root,
                center,
                yaw,
                side,
                panelHeight,
                restBottom,
                postHeight,
                ceilingClearanceMeters,
                interactive,
                out PestControlTrapPreview authoredPreview))
                return authoredPreview;


            Transform platform = CreateBox(root.transform, "Platform", Vector3.zero,
                Quaternion.identity, new Vector3(side, PlatformThickness, side), PlatformColor);
            if (interactive)
            {
                // The platform must be standable; every other box in the assembly is decoration.
                var platformCollider = platform.gameObject.AddComponent<BoxCollider>();
                platformCollider.isTrigger = false;
            }

            var fence = new GameObject("FenceAssembly");
            fence.transform.SetParent(root.transform, false);
            fence.transform.localPosition = new Vector3(0f, restBottom, 0f);

            int panelsPerSide = Mathf.Max(1, Mathf.RoundToInt(side / TargetPanelWidth));
            var sealColliders = new List<Collider>(4);
            var sealObstacles = new List<NavMeshObstacle>(4);
            BuildFenceSide(fence.transform, "North", new Vector3(0f, 0f, half), 0f, side, panelHeight, panelsPerSide, interactive, sealColliders, sealObstacles);
            BuildFenceSide(fence.transform, "South", new Vector3(0f, 0f, -half), 180f, side, panelHeight, panelsPerSide, interactive, sealColliders, sealObstacles);
            BuildFenceSide(fence.transform, "East", new Vector3(half, 0f, 0f), 90f, side, panelHeight, panelsPerSide, interactive, sealColliders, sealObstacles);
            BuildFenceSide(fence.transform, "West", new Vector3(-half, 0f, 0f), 270f, side, panelHeight, panelsPerSide, interactive, sealColliders, sealObstacles);

            var ropes = new LineRenderer[4];
            var ropeAnchors = new Transform[4];
            var corners = new[]
            {
                new Vector2(half, half),
                new Vector2(half, -half),
                new Vector2(-half, half),
                new Vector2(-half, -half)
            };

            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 postLocal = new Vector3(corners[i].x, 0f, corners[i].y);
                CreateBox(root.transform, "Post" + i,
                    postLocal + new Vector3(0f, postHeight * 0.5f, 0f),
                    Quaternion.identity,
                    new Vector3(PostSection, postHeight, PostSection),
                    PostColor);
                // Stand-in for PestTrap_WinchHead: sits ON the post cap and occupies exactly the
                // rig headroom DeriveCageMetrics reserved, so the placeholder and the authored
                // kit agree about where the ceiling budget goes.
                Transform pulley = CreateBox(root.transform, "Winch" + i,
                    postLocal + new Vector3(0f, postHeight + WinchHeadMeters * 0.5f, 0f),
                    Quaternion.identity,
                    new Vector3(PulleySection, WinchHeadMeters, PulleySection),
                    PostColor);

                var anchor = new GameObject("RopeAnchor" + i);
                anchor.transform.SetParent(fence.transform, false);
                anchor.transform.localPosition = new Vector3(corners[i].x, panelHeight, corners[i].y);
                ropeAnchors[i] = anchor.transform;

                var ropeObject = new GameObject("Rope" + i);
                ropeObject.transform.SetParent(root.transform, false);
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
                line.SetPosition(0, pulley.position);
                line.SetPosition(1, ropeAnchors[i].position);
                // The pulley end never moves, so the preview only refreshes index 1 each frame.
                ropes[i] = line;
            }

            BuildLever(root.transform, center, yaw, side, interactive, out Vector3 leverPosition, out bool leverValidated, out InteractTrigger trigger, out Transform leverArm);

            PestControlTrapPreview preview = root.AddComponent<PestControlTrapPreview>();
            preview.Initialize(fence.transform, ropes, ropeAnchors, sealColliders, sealObstacles, trigger, restBottom, leverArm);
            preview.FootprintMeters = side;
            preview.CageHeightMeters = panelHeight;
            preview.RestBottomMeters = restBottom;
            preview.PanelsPerSide = panelsPerSide;
            preview.LeverPosition = leverPosition;
            preview.LeverValidated = leverValidated;
            return preview;
        }

        /// <summary>One side of the corral: <paramref name="panelsPerSide"/> bar panels, last one stretched to close the span.</summary>
        private static void BuildFenceSide(
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

            float nominal = side / panelsPerSide;
            float cursor = -side * 0.5f;
            for (int p = 0; p < panelsPerSide; p++)
            {
                float width = p == panelsPerSide - 1 ? side * 0.5f - cursor : nominal;
                float center = cursor + width * 0.5f;
                BuildPanel(sideRoot.transform, "Panel" + p, center, width, panelHeight);
                cursor += width;
            }

            if (!interactive)
                return;

            // One solid box per side rather than per bar: the bars are cosmetic and a collider
            // per bar would leave gaps a Thumper could squeeze through anyway.
            var sealCollider = sideRoot.gameObject.AddComponent<BoxCollider>();
            sealCollider.center = new Vector3(0f, panelHeight * 0.5f, 0f);
            sealCollider.size = new Vector3(side, panelHeight, PanelDepth + 0.12f);
            sealCollider.isTrigger = false;
            sealCollider.enabled = false;
            sealColliders.Add(sealCollider);

            NavMeshObstacle obstacle = AddSealObstacle(sideRoot, side, panelHeight);
            if (obstacle != null)
                sealObstacles.Add(obstacle);
        }

        /// <summary>Carving obstacle so a captured monster cannot path out through the bars. Armed only once landed.</summary>
        private static NavMeshObstacle AddSealObstacle(GameObject sideRoot, float side, float panelHeight)
        {
            try
            {
                var obstacle = sideRoot.AddComponent<NavMeshObstacle>();
                obstacle.shape = NavMeshObstacleShape.Box;
                obstacle.center = new Vector3(0f, panelHeight * 0.5f, 0f);
                obstacle.size = new Vector3(side, panelHeight, PanelDepth + 0.20f);
                obstacle.carving = true;
                obstacle.carveOnlyStationary = true;
                obstacle.enabled = false;
                return obstacle;
            }
            catch (System.Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.PestControl] Could not add a trap NavMeshObstacle: {e.Message}");
                return null;
            }
        }

        private static void BuildPanel(Transform sideRoot, string panelName, float localX, float width, float panelHeight)
        {
            var panel = new GameObject(panelName);
            panel.transform.SetParent(sideRoot, false);
            panel.transform.localPosition = new Vector3(localX, 0f, 0f);

            CreateBox(panel.transform, "RailBottom", new Vector3(0f, RailThickness * 0.5f, 0f),
                Quaternion.identity, new Vector3(width, RailThickness, PanelDepth), FrameColor);
            CreateBox(panel.transform, "RailTop", new Vector3(0f, panelHeight - RailThickness * 0.5f, 0f),
                Quaternion.identity, new Vector3(width, RailThickness, PanelDepth), FrameColor);
            // A taller cage needs a mid rail or the bars read as loose sticks.
            CreateBox(panel.transform, "RailMid", new Vector3(0f, panelHeight * 0.5f, 0f),
                Quaternion.identity, new Vector3(width, RailThickness * 0.8f, PanelDepth * 0.85f), FrameColor);

            float barSpan = panelHeight - RailThickness * 2f;
            for (int b = 0; b < BarsPerPanel; b++)
            {
                float t = (b + 0.5f) / BarsPerPanel;
                CreateBox(panel.transform, "Bar" + b,
                    new Vector3(-width * 0.5f + width * t, RailThickness + barSpan * 0.5f, 0f),
                    Quaternion.identity,
                    new Vector3(BarThickness, barSpan, BarThickness),
                    BarColor);
            }
        }

        /// <summary>
        /// Lever sits outside the footprint on the side facing the nearest interior entrance, so
        /// players approach it before walking into the corral. Slides along that edge when the
        /// first spot has no floor under it.
        /// </summary>
        private static void BuildLever(
            Transform root,
            Vector3 center,
            float yaw,
            float side,
            bool interactive,
            out Vector3 leverPosition,
            out bool leverValidated,
            out InteractTrigger trigger,
            out Transform leverArm)
        {
            trigger = null;
            leverArm = null;
            List<Vector3> entrances = InteriorAnchorService.GetInteriorEntrancePositions();
            Vector3 entrance = InteriorAnchorService.GetNearestEntrancePoint(entrances, center);
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 right = rotation * Vector3.right;
            Vector3 forward = rotation * Vector3.forward;

            Vector3 toEntrance = entrance - center;
            toEntrance.y = 0f;
            if (toEntrance.sqrMagnitude < 1e-4f)
                toEntrance = forward;

            Vector3 outward = forward;
            Vector3 tangent = right;
            float bestDot = float.MinValue;
            Vector3[] axes = { forward, -forward, right, -right };
            Vector3[] tangents = { right, right, forward, forward };
            for (int i = 0; i < axes.Length; i++)
            {
                float dot = Vector3.Dot(axes[i], toEntrance);
                if (dot <= bestDot)
                    continue;

                bestDot = dot;
                outward = axes[i];
                tangent = tangents[i];
            }

            float outset = side * 0.5f + LeverOutsetMeters;
            float[] slides = { 0f, 1.0f, -1.0f, 2.0f, -2.0f };
            Vector3 chosen = center + outward * outset;
            leverValidated = false;
            StartOfRound start = StartOfRound.Instance;
            int mask = start != null ? start.collidersAndRoomMaskAndDefault : ~0;

            for (int i = 0; i < slides.Length; i++)
            {
                Vector3 candidate = center + outward * outset + tangent * slides[i];
                if (!Physics.Raycast(candidate + Vector3.up * 2.5f, Vector3.down, out RaycastHit hit, 6f, mask, QueryTriggerInteraction.Ignore))
                    continue;

                chosen = hit.point;
                leverValidated = true;
                break;
            }

            if (!leverValidated)
                chosen = new Vector3(chosen.x, center.y, chosen.z);

            leverPosition = chosen;

            var lever = new GameObject("Lever");
            lever.transform.SetParent(root, true);
            // Faces outward so the operator stands on local +Z, the side the arm throws toward.
            // See TryResolveLeverPose — the authored path derives the same pose.
            lever.transform.SetPositionAndRotation(chosen, Quaternion.LookRotation(outward, Vector3.up));

            CreateBox(lever.transform, "LeverBase", new Vector3(0f, 0.10f, 0f),
                Quaternion.identity, new Vector3(0.36f, 0.20f, 0.36f), PostColor);
            CreateBox(lever.transform, "LeverPost", new Vector3(0f, 0.55f, 0f),
                Quaternion.identity, new Vector3(0.12f, 0.90f, 0.12f), PostColor);

            // The arm hangs off its own pivot at the axle and is authored STRAIGHT UP, so
            // pull 0 is the armed pose with no baked-in offset and the whole throw is one
            // clean rotation about local X. PestControlTrapPreview.SetLeverPull drives it.
            leverArm = new GameObject("LeverArm").transform;
            leverArm.SetParent(lever.transform, false);
            leverArm.localPosition = new Vector3(0f, LeverAxleHeight, 0f);
            leverArm.localRotation = Quaternion.identity;
            CreateBox(leverArm, "LeverHandle", new Vector3(0f, 0.30f, 0f),
                Quaternion.identity, new Vector3(0.07f, 0.60f, 0.07f), LeverColor);
            CreateBox(leverArm, "LeverGrip", new Vector3(0f, 0.62f, 0f),
                Quaternion.identity, new Vector3(0.10f, 0.15f, 0.10f), LeverColor);

            if (!interactive)
                return;

            trigger = BuildLeverTrigger(lever.transform);
            BuildLeverScanNode(lever.transform);
        }

        private static InteractTrigger BuildLeverTrigger(Transform lever)
        {
            var triggerObject = new GameObject("InteractionTrigger");
            triggerObject.transform.SetParent(lever, false);
            triggerObject.transform.localPosition = new Vector3(0f, 0.85f, 0.15f);
            triggerObject.layer = InteractLayer;
            SetTagSafe(triggerObject, "InteractTrigger");

            var collider = triggerObject.AddComponent<BoxCollider>();
            collider.size = new Vector3(0.9f, 1.5f, 0.9f);
            collider.isTrigger = true;

            var trigger = triggerObject.AddComponent<InteractTrigger>();
            trigger.interactable = true;
            trigger.oneHandedItemAllowed = true;
            trigger.twoHandedItemAllowed = false;
            trigger.holdInteraction = false;
            trigger.interactCooldown = true;
            trigger.cooldownTime = 0.6f;
            trigger.hoverTip = "Release trap : [LMB]";
            trigger.disabledHoverTip = "[ Rearming... ]";
            trigger.disableTriggerMesh = true;
            trigger.onInteract = new InteractEvent();
            trigger.onInteractEarly = new InteractEvent();
            trigger.onInteractEarlyOtherClients = new InteractEvent();
            trigger.onStopInteract = new InteractEvent();
            trigger.onCancelAnimation = new InteractEvent();
            trigger.holdingInteractEvent = new InteractEventFloat();

            // Deliberately NOT trigger.specialCharacterAnimation. That path is the natural way to
            // freeze a player, but it opens with InteractTrigger.UpdateUsedByPlayerServerRpc, and
            // this trigger is added to the trap AFTER NetworkObject.Spawn — so it is absent from
            // the NetworkObject's cached ChildNetworkBehaviours and NGO would resolve its
            // behaviour index to 0, misrouting the RPC onto PestControlTrapController. The hold is
            // driven from PestControlLeverPlayerAnimation instead, through PlayerControllerB's own
            // registered RPCs.
            //
            // playerPositionNode carries no meaning to vanilla on the plain path; we set it purely
            // as the anchor the hold reads back.
            trigger.playerPositionNode = BuildLeverStandNode(lever);
            ApplyLeverHoverIcon(trigger);
            return trigger;
        }

        /// <summary>
        /// #756. Gives the lever trigger the vanilla hand icon. A trigger with no <c>hoverIcon</c>
        /// draws Unity's default white texture above the tip, which is the blank square that used
        /// to hang over the lever. The icon is copied from the local player the way every other
        /// code-built trigger in this mod does it; when the player is not up yet (a trap assembled
        /// before the round is live) the preview re-applies it on its next tip refresh.
        /// </summary>
        internal static void ApplyLeverHoverIcon(InteractTrigger trigger)
        {
            if (trigger == null || trigger.hoverIcon != null)
                return;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                                       ?? StartOfRound.Instance?.localPlayerController;
            if (player?.grabItemIcon == null)
                return;

            trigger.hoverIcon = player.grabItemIcon;
            trigger.disabledHoverIcon = player.grabItemIcon;
        }

        /// <summary>
        /// Where the operator is planted for the throw: squarely in front of the handle on the
        /// lever's +Z (operator) side, turned back to face it.
        /// </summary>
        private static Transform BuildLeverStandNode(Transform lever)
        {
            var node = new GameObject("LeverStandNode");
            node.transform.SetParent(lever, false);
            node.transform.localPosition = new Vector3(0f, 0f, LeverStandOffsetMeters);
            node.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            return node.transform;
        }

        private static void BuildLeverScanNode(Transform lever)
        {
            var scanObject = new GameObject("PestTrapLeverScanNode");
            scanObject.transform.SetParent(lever, false);
            scanObject.transform.localPosition = new Vector3(0f, 1.1f, 0f);
            scanObject.layer = ScanNodeLayer;

            var collider = scanObject.AddComponent<SphereCollider>();
            collider.radius = 0.7f;
            collider.isTrigger = true;

            var scanNode = scanObject.AddComponent<ScanNodeProperties>();
            scanNode.headerText = "TRAP RELEASE";
            scanNode.subText = "pest control corral";
            scanNode.maxRange = 24;
            scanNode.minRange = 1;
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = 0;
        }

        private static Transform CreateBox(Transform parent, string boxName, Vector3 localPosition, Quaternion localRotation, Vector3 scale, Color color)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = boxName;
            box.transform.SetParent(parent, false);
            box.transform.localPosition = localPosition;
            box.transform.localRotation = localRotation;
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
                renderer.receiveShadows = true;
            }

            return box.transform;
        }

        // One material per distinct colour: a corral is a few hundred boxes and a material each
        // would leak hundreds of instances per trap.
        private static Material GetMaterial(Color color)
        {
            if (MaterialCache.TryGetValue(color, out Material cached) && cached != null)
                return cached;

            Shader shader = Shader.Find("HDRP/Lit")
                ?? Shader.Find("High Definition Render Pipeline/Lit")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Unlit/Color");
            if (shader == null)
                return null;

            var material = new Material(shader) { name = "PestControlTrapPlaceholder" };
            material.SetOverrideTag("RenderType", "Opaque");
            if (material.HasProperty("_SurfaceType"))
                material.SetFloat("_SurfaceType", 0f);
            if (material.HasProperty("_ZWrite"))
                material.SetFloat("_ZWrite", 1f);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            if (material.HasProperty("_UnlitColor"))
                material.SetColor("_UnlitColor", color);
            if (material.HasProperty("_Metallic"))
                material.SetFloat("_Metallic", 0.18f);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", 0.22f);
            if (material.HasProperty("_Glossiness"))
                material.SetFloat("_Glossiness", 0.22f);
            material.renderQueue = 2000;

            MaterialCache[color] = material;
            return material;
        }

        private static Material GetRopeMaterial()
        {
            if (_ropeMaterial != null)
                return _ropeMaterial;

            Shader shader = Shader.Find("HDRP/Unlit")
                ?? Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Standard");
            if (shader == null)
                return null;

            _ropeMaterial = new Material(shader) { name = "PestControlTrapRope" };
            if (_ropeMaterial.HasProperty("_BaseColor"))
                _ropeMaterial.SetColor("_BaseColor", RopeColor);
            if (_ropeMaterial.HasProperty("_UnlitColor"))
                _ropeMaterial.SetColor("_UnlitColor", RopeColor);
            if (_ropeMaterial.HasProperty("_Color"))
                _ropeMaterial.SetColor("_Color", RopeColor);
            _ropeMaterial.renderQueue = 2000;
            return _ropeMaterial;
        }

        private static void SetTagSafe(GameObject obj, string tag)
        {
            try
            {
                obj.tag = tag;
            }
            catch
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.PestControl] Tag '{tag}' was not available for the trap lever.");
            }
        }
    }
}
