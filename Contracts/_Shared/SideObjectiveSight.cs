using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.ObjectiveHints;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1195. The local player's sight check for the side objective, run on every peer at most
    /// <see cref="SideObjectivePolicy.SightCheckIntervalSeconds"/> apart and only while the
    /// objective is unrevealed.
    ///
    /// A check is: hover first (the local player's <c>hoveringOverTrigger</c>, or the trigger it is
    /// animating with, is the case trigger or sits under an objective prop - counts at once);
    /// otherwise every target inside <see cref="SideObjectivePolicy.SightRangeMeters"/> whose bounds
    /// pass the camera frustum is ranked by <see cref="SideObjectiveSightPick"/>, and the two best,
    /// from different targets, get one linecast each against the room/collider mask (a lone
    /// candidate gets both, to its centre and to a point inside its near face). A hit on the
    /// target's own colliders is a clear line. Sight has to hold for
    /// <see cref="SideObjectiveSightDwell.DwellSeconds"/>.
    ///
    /// The source fills the target set once per objective (<see cref="SideObjectiveWesleyInteriorSource"/>);
    /// the check itself allocates nothing.
    /// </summary>
    internal static class SideObjectiveSight
    {
        private const int MaxRenderersPerProp = 48;
        private const int MaxRenderers = 256;
        /// <summary>How far the lone candidate's second ray lands inside its near face, as a
        /// fraction of the way to the centre, so it cannot graze the bounds surface.</summary>
        private const float NearFaceInset = 0.25f;
        private const float MinimumRaySqrLength = 0.01f;
        private static readonly Vector3 PointTargetSize = new Vector3(1f, 1f, 1f);
        private static readonly Vector3 EnemyFallbackSize = new Vector3(1.5f, 2f, 1.5f);

        private static readonly List<Renderer> Renderers = new List<Renderer>(MaxRenderers);
        private static readonly List<Transform> RendererRoots = new List<Transform>(MaxRenderers);
        private static readonly List<int> RendererGroups = new List<int>(MaxRenderers);
        private static readonly List<Transform> PointTargets = new List<Transform>(16);
        private static readonly List<Transform> HoverRoots = new List<Transform>(16);
        private static readonly List<EnemyType> EnemyTypes = new List<EnemyType>(2);
        private static readonly List<Renderer> RendererScratch = new List<Renderer>(64);
        private static InteractTrigger _caseTrigger;

        private static readonly Plane[] FrustumPlanes = new Plane[6];
        private static readonly SideObjectiveSightDwell Dwell = new SideObjectiveSightDwell();
        private static bool _glintFailureLogged;

        private static SideObjectiveSightPick _pick;
        private static Bounds _firstBounds;
        private static Transform _firstRoot;
        private static Bounds _secondBounds;
        private static Transform _secondRoot;

        internal static void Reset()
        {
            ClearTargets();
            Dwell.Reset();
        }

        internal static void ClearTargets()
        {
            Renderers.Clear();
            RendererRoots.Clear();
            RendererGroups.Clear();
            PointTargets.Clear();
            HoverRoots.Clear();
            EnemyTypes.Clear();
            RendererScratch.Clear();
            _caseTrigger = null;
            _firstRoot = null;
            _secondRoot = null;
        }

        /// <summary>The objective's claim trigger: hovering it counts as a sighting.</summary>
        internal static void SetCaseTrigger(InteractTrigger trigger)
        {
            _caseTrigger = trigger;
        }

        /// <summary>
        /// An objective prop: its renderers are sight targets (strided down to a budget on very large
        /// props) and triggers under it count on hover. A prop with no renderer is sighted at its
        /// transform unless <paramref name="allowPointTarget"/> is false.
        /// </summary>
        internal static void AddProp(Component prop, bool allowPointTarget)
        {
            if (prop == null)
                return;

            Transform root = prop.transform;
            HoverRoots.Add(root);

            RendererScratch.Clear();
            prop.GetComponentsInChildren(true, RendererScratch);
            int count = RendererScratch.Count;
            if (count == 0)
            {
                if (allowPointTarget)
                    PointTargets.Add(root);
                return;
            }

            int group = root.GetInstanceID();
            int stride = (count + MaxRenderersPerProp - 1) / MaxRenderersPerProp;
            for (int i = 0; i < count && Renderers.Count < MaxRenderers; i += stride)
            {
                Renderers.Add(RendererScratch[i]);
                RendererRoots.Add(root);
                RendererGroups.Add(group);
            }

            RendererScratch.Clear();
        }

        /// <summary>Live enemies of this type are sight targets.</summary>
        internal static void AddEnemyType(EnemyType type)
        {
            if (type != null && !EnemyTypes.Contains(type))
                EnemyTypes.Add(type);
        }

        /// <summary>One check. True when the local player has sighted the objective.</summary>
        internal static bool Check(float now)
        {
            StartOfRound start = StartOfRound.Instance;
            PlayerControllerB player = start != null ? start.localPlayerController : null;
            if (player == null || !player.isPlayerControlled || player.isPlayerDead)
            {
                Dwell.Reset();
                return false;
            }

            Transform hoveredRoot = FindInteractingRoot(player.hoveringOverTrigger, _caseTrigger, HoverRoots, null)
                ?? FindInteractingRoot(player.currentTriggerInAnimationWith, _caseTrigger, HoverRoots, null);
            if (hoveredRoot != null)
            {
                Dwell.Reset();
                TryShowGlint(hoveredRoot, player.gameplayCamera);
                return true;
            }

            Camera camera = player.gameplayCamera;
            Transform sightedRoot = null;
            bool seen = camera != null && TrySee(camera, start, out sightedRoot);
            if (!Dwell.Observe(seen, now))
                return false;

            Dwell.Reset();
            TryShowGlint(sightedRoot, camera);
            return true;
        }

        /// <summary>A presentation failure cannot prevent the successful sighting reaching the host.</summary>
        private static void TryShowGlint(Transform root, Camera camera)
        {
            try
            {
                ObjectiveGlintRuntime.TryShow(root, camera, ObjectiveHintRuntime.IsPityBlinkActiveForTarget(root));
            }
            catch (Exception e)
            {
                if (_glintFailureLogged)
                    return;
                _glintFailureLogged = true;
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] First-sight glint failed: {e}");
            }
        }

        /// <summary>
        /// Whether <paramref name="player"/> is hovering, or animating with, the case trigger or a
        /// trigger under one of <paramref name="roots"/> or under <paramref name="extraRoot"/>.
        /// </summary>
        internal static bool IsInteractingWithTarget(PlayerControllerB player, InteractTrigger caseTrigger, List<Transform> roots, Transform extraRoot)
        {
            return FindInteractingRoot(player.hoveringOverTrigger, caseTrigger, roots, extraRoot) != null
                || FindInteractingRoot(player.currentTriggerInAnimationWith, caseTrigger, roots, extraRoot) != null;
        }

        private static Transform FindInteractingRoot(InteractTrigger trigger, InteractTrigger caseTrigger, List<Transform> roots, Transform extraRoot)
        {
            if (trigger == null)
                return null;

            Transform triggerTransform = trigger.transform;
            if (extraRoot != null && triggerTransform.IsChildOf(extraRoot))
                return extraRoot;
            if (roots != null)
            {
                for (int i = 0; i < roots.Count; i++)
                {
                    Transform root = roots[i];
                    if (root != null && triggerTransform.IsChildOf(root))
                        return root;
                }
            }

            return caseTrigger != null && trigger == caseTrigger ? triggerTransform : null;
        }

        private static bool TrySee(Camera camera, StartOfRound start, out Transform sightedRoot)
        {
            sightedRoot = null;
            if (Renderers.Count == 0 && PointTargets.Count == 0 && EnemyTypes.Count == 0)
                return false;

            Transform eyeTransform = camera.transform;
            Vector3 eye = eyeTransform.position;
            Vector3 forward = eyeTransform.forward;
            GeometryUtility.CalculateFrustumPlanes(camera, FrustumPlanes);
            _pick.Reset();

            for (int i = 0; i < Renderers.Count; i++)
            {
                Renderer renderer = Renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                Bounds bounds = renderer.bounds;
                if (bounds.extents.sqrMagnitude < 1e-6f)
                    continue;
                Consider(bounds, RendererRoots[i], RendererGroups[i], eye, forward);
            }

            for (int i = 0; i < PointTargets.Count; i++)
            {
                Transform point = PointTargets[i];
                if (point != null && point.gameObject.activeInHierarchy)
                    Consider(new Bounds(point.position, PointTargetSize), point, point.GetInstanceID(), eye, forward);
            }

            if (EnemyTypes.Count > 0)
            {
                RoundManager round = RoundManager.Instance;
                List<EnemyAI> enemies = round != null ? round.SpawnedEnemies : null;
                if (enemies != null)
                {
                    for (int i = 0; i < enemies.Count; i++)
                    {
                        EnemyAI enemy = enemies[i];
                        if (enemy == null || enemy.isEnemyDead || !IsTargetType(enemy.enemyType))
                            continue;
                        Consider(EnemyBounds(enemy), enemy.transform, enemy.GetInstanceID(), eye, forward);
                    }
                }
            }

            int count = _pick.Count;
            Transform firstRoot = _firstRoot;
            Transform secondRoot = _secondRoot;
            _firstRoot = null;
            _secondRoot = null;
            if (count == 0)
                return false;

            int mask = start != null ? start.collidersAndRoomMask : Physics.DefaultRaycastLayers;
            // SideObjectivePolicy.MaxLinecastsPerCheck: one ray each for the two best targets, or
            // both for a lone one - its centre, then a point just inside the face nearest the eye.
            if (HasClearLine(eye, _firstBounds.center, firstRoot, mask))
            {
                sightedRoot = firstRoot;
                return true;
            }
            if (count >= 2)
            {
                if (!HasClearLine(eye, _secondBounds.center, secondRoot, mask))
                    return false;
                sightedRoot = secondRoot;
                return true;
            }

            Vector3 nearFace = Vector3.Lerp(_firstBounds.ClosestPoint(eye), _firstBounds.center, NearFaceInset);
            if (!HasClearLine(eye, nearFace, firstRoot, mask))
                return false;
            sightedRoot = firstRoot;
            return true;
        }

        private static void Consider(Bounds bounds, Transform root, int group, Vector3 eye, Vector3 forward)
        {
            float sqrDistance = bounds.SqrDistance(eye);
            float maxSqrDistance = SideObjectivePolicy.SightRangeMeters * SideObjectivePolicy.SightRangeMeters;
            // Cheap rejections before the frustum test; Offer repeats them as the pinned rule.
            if (sqrDistance > maxSqrDistance || !GeometryUtility.TestPlanesAABB(FrustumPlanes, bounds))
                return;

            Vector3 toCentre = bounds.center - eye;
            float length = toCentre.magnitude;
            float alignment = length > 1e-4f ? Vector3.Dot(forward, toCentre) / length : 1f;
            bool containsEye = bounds.Contains(eye);

            int previousFirstGroup = _pick.FirstGroup;
            int previousCount = _pick.Count;
            int slot = _pick.Offer(group, alignment, sqrDistance, containsEye, maxSqrDistance);
            if (slot == 0)
            {
                // Mirror the pick's shift: the old first moved to the second slot.
                if (previousCount > 0 && _pick.Count == 2 && _pick.SecondGroup == previousFirstGroup && previousFirstGroup != group)
                {
                    _secondBounds = _firstBounds;
                    _secondRoot = _firstRoot;
                }

                _firstBounds = bounds;
                _firstRoot = root;
            }
            else if (slot == 1)
            {
                _secondBounds = bounds;
                _secondRoot = root;
            }
        }

        private static bool HasClearLine(Vector3 eye, Vector3 point, Transform root, int mask)
        {
            // A ray that does not leave the eye proves nothing (#1195 F1).
            if ((point - eye).sqrMagnitude < MinimumRaySqrLength)
                return false;
            if (!Physics.Linecast(eye, point, out RaycastHit hit, mask, QueryTriggerInteraction.Ignore))
                return true;
            return hit.collider != null && root != null && hit.collider.transform.IsChildOf(root);
        }

        private static bool IsTargetType(EnemyType type)
        {
            if (type == null)
                return false;
            for (int i = 0; i < EnemyTypes.Count; i++)
            {
                if (ReferenceEquals(EnemyTypes[i], type))
                    return true;
            }

            return false;
        }

        private static Bounds EnemyBounds(EnemyAI enemy)
        {
            SkinnedMeshRenderer[] skinned = enemy.skinnedMeshRenderers;
            if (skinned != null)
            {
                for (int i = 0; i < skinned.Length; i++)
                {
                    if (skinned[i] != null && skinned[i].enabled)
                        return skinned[i].bounds;
                }
            }

            MeshRenderer[] meshes = enemy.meshRenderers;
            if (meshes != null)
            {
                for (int i = 0; i < meshes.Length; i++)
                {
                    if (meshes[i] != null && meshes[i].enabled)
                        return meshes[i].bounds;
                }
            }

            return new Bounds(enemy.transform.position + Vector3.up, EnemyFallbackSize);
        }
    }
}
