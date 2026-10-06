using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Contracts.ObjectiveHints
{
    /// <summary>
    /// #1197. The gameplay-camera-only replacement for LethalCCTV's <c>CctvOutlineEffectBridge</c>.
    /// <see cref="Show"/> puts an <see cref="ObjectiveOutlineEffect"/> on the local gameplay camera
    /// and an <see cref="ObjectiveOutline"/> on every eligible renderer under each target;
    /// <see cref="SetLit"/> blinks them; <see cref="Clear"/> removes every component it added and
    /// releases the camera's custom-pass bit (<see cref="ObjectiveHintCustomPassBit"/>). If the
    /// camera goes away first, the effect's own teardown does the same. Between windows nothing of
    /// it exists.
    ///
    /// <para>Renderers are taken whether or not they are enabled, the way the squad-ping path of
    /// the bridge does, so a renderer CullFactory has switched off stays outlined. The filters are
    /// the bridge's own, minus its string-built helper test: particle, line and trail renderers,
    /// shadows-only renderers, layers the gameplay camera does not draw (map dots, radar and scan
    /// geometry), scan nodes, anything under a player or a different grabbable, and LOD levels past
    /// the first.</para>
    /// </summary>
    internal static class ObjectiveHintOutlines
    {
        private static readonly List<ObjectiveOutline> Created = new List<ObjectiveOutline>();
        private static readonly List<Renderer> RendererBuffer = new List<Renderer>();
        private static readonly List<LODGroup> LodGroupBuffer = new List<LODGroup>();
        private static readonly HashSet<Renderer> LodManaged = new HashSet<Renderer>();
        private static readonly HashSet<Renderer> LodPrimary = new HashSet<Renderer>();

        private static ObjectiveOutlineEffect _effect;
        private static bool _shaderFailureLogged;

        /// <summary>
        /// Outlines every eligible renderer under <paramref name="targets"/> on
        /// <paramref name="gameplayCamera"/>. False when nothing could be outlined (no camera, the
        /// shaders are unavailable, no eligible renderer); the card word still pulses then. A
        /// missing shader is logged once per session.
        /// </summary>
        internal static bool Show(Camera gameplayCamera, List<Component> targets)
        {
            Clear();
            if (gameplayCamera == null || targets == null || targets.Count == 0)
                return false;

            if (!ObjectiveOutlineAssets.TryResolveShaders(out _, out _))
            {
                // The one warning for the whole missing-shader path; the asset loader logs nothing.
                if (!_shaderFailureLogged)
                {
                    _shaderFailureLogged = true;
                    string detail = ObjectiveOutlineAssets.BundleFailure ?? "no loaded bundle has them";
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Hints] The outline shaders could not be resolved ({detail}); objective hints pulse the card word only.");
                }
                return false;
            }

            int cullingMask = gameplayCamera.cullingMask;
            for (int i = 0; i < targets.Count; i++)
            {
                Component target = targets[i];
                if (target != null)
                    OutlineTarget(target, cullingMask);
            }

            if (Created.Count == 0)
                return false;

            _effect = gameplayCamera.gameObject.AddComponent<ObjectiveOutlineEffect>();
            ConfigureEffect(_effect, gameplayCamera);
            ObjectiveHintCustomPassBit.Acquire(gameplayCamera);
            return true;
        }

        /// <summary>The blink. Called every frame of an open window with the word-pulse clock.</summary>
        internal static void SetLit(bool lit)
        {
            if (_effect != null)
                _effect.Lit = lit;
        }

        /// <summary>Removes every outline and the effect, and releases the camera's custom-pass bit.</summary>
        internal static void Clear()
        {
            DestroyOutlines();
            if (_effect != null)
            {
                _effect.Lit = false;
                _effect.enabled = false;
                UnityEngine.Object.Destroy(_effect);
            }
            _effect = null;
            ObjectiveHintCustomPassBit.Release();
        }

        /// <summary>
        /// Called from the effect's OnDestroy. When the live effect dies with its camera (a scene
        /// change, a replaced player) rather than through <see cref="Clear"/>, nothing else would
        /// remove the outlines it drew, so they go here, with the registry and the camera bit.
        /// </summary>
        internal static void NotifyEffectDestroyed(ObjectiveOutlineEffect effect)
        {
            // Reference identity: the dying object may already compare equal to null.
            if (_effect is null || !ReferenceEquals(effect, _effect))
                return;

            _effect = null;
            DestroyOutlines();
            ObjectiveOutlineEffect.ClearRegistry();
            ObjectiveHintCustomPassBit.Release();
        }

        private static void DestroyOutlines()
        {
            for (int i = 0; i < Created.Count; i++)
            {
                ObjectiveOutline outline = Created[i];
                if (outline == null)
                    continue;
                // Disable first so it leaves the registry now; Destroy lands at the end of the frame.
                outline.enabled = false;
                UnityEngine.Object.Destroy(outline);
            }
            Created.Clear();
        }

        private static void ConfigureEffect(ObjectiveOutlineEffect effect, Camera camera)
        {
            // The squad ping's line on LethalCCTV's gameplay-camera effect, in the card's own accent
            // so the outline and the pulsing word read as one signal.
            Color accent = ContractUiTheme.HudAccent;
            accent.a = 1f;
            effect.sourceCamera = camera;
            effect.lineThickness = 0.85f;
            effect.lineIntensity = 1.35f;
            effect.lineColor0 = accent;
            effect.lineColor1 = accent;
            effect.lineColor2 = accent;
            effect.backfaceCulling = false;
            effect.cornerOutlines = true;
            effect.Lit = false;
            effect.UpdateMaterialsPublicProperties();
        }

        private static void OutlineTarget(Component target, int cullingMask)
        {
            Transform root = target.transform;
            // The target's own item, if it is (or sits on) one: an LGU object's component need not be
            // the GrabbableObject itself, and its renderers must not read as a different item's.
            GrabbableObject targetItem = target.GetComponentInParent<GrabbableObject>();
            bool targetIsPlayer = target is PlayerControllerB;

            CollectPrimaryLodRenderers(root);
            root.GetComponentsInChildren(false, RendererBuffer);
            for (int i = 0; i < RendererBuffer.Count; i++)
            {
                Renderer renderer = RendererBuffer[i];
                if (!IsEligible(renderer, cullingMask, targetItem, targetIsPlayer))
                    continue;

                ObjectiveOutline outline = renderer.gameObject.AddComponent<ObjectiveOutline>();
                outline.color = 0;
                outline.eraseRenderer = false;
                Created.Add(outline);
            }

            RendererBuffer.Clear();
            LodManaged.Clear();
            LodPrimary.Clear();
        }

        private static bool IsEligible(Renderer renderer, int cullingMask, GrabbableObject targetItem, bool targetIsPlayer)
        {
            if (renderer == null)
                return false;
            if (renderer is ParticleSystemRenderer || renderer is LineRenderer || renderer is TrailRenderer)
                return false;
            // A shadows-only renderer draws nothing a player sees but the mask still traces it.
            if (renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                return false;
            if ((cullingMask & (1 << renderer.gameObject.layer)) == 0)
                return false;
            if (LodManaged.Contains(renderer) && !LodPrimary.Contains(renderer))
                return false;
            if (renderer.GetComponentInParent<ScanNodeProperties>() != null)
                return false;
            if (!targetIsPlayer && renderer.GetComponentInParent<PlayerControllerB>() != null)
                return false;

            GrabbableObject item = renderer.GetComponentInParent<GrabbableObject>();
            return item == null || item == targetItem;
        }

        /// <summary>Every renderer an LOD group manages, and the ones in its first populated level.</summary>
        private static void CollectPrimaryLodRenderers(Transform root)
        {
            LodManaged.Clear();
            LodPrimary.Clear();
            root.GetComponentsInChildren(false, LodGroupBuffer);
            for (int g = 0; g < LodGroupBuffer.Count; g++)
            {
                LODGroup group = LodGroupBuffer[g];
                if (group == null || !group.enabled)
                    continue;

                LOD[] lods = group.GetLODs();
                bool primaryTaken = false;
                for (int l = 0; l < lods.Length; l++)
                {
                    Renderer[] lodRenderers = lods[l].renderers;
                    if (lodRenderers == null)
                        continue;

                    bool populated = false;
                    for (int r = 0; r < lodRenderers.Length; r++)
                    {
                        Renderer lodRenderer = lodRenderers[r];
                        if (lodRenderer == null)
                            continue;
                        LodManaged.Add(lodRenderer);
                        populated = true;
                        if (!primaryTaken)
                            LodPrimary.Add(lodRenderer);
                    }

                    primaryTaken |= populated;
                }
            }

            LodGroupBuffer.Clear();
        }
    }
}
