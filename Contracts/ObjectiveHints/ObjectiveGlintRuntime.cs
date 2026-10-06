using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Contracts.ObjectiveHints
{
    /// <summary>
    /// Local first-sight surface accent. Only clones ordinary opaque HDRP/Lit slots; their original
    /// depth, geometry, textures and render settings still decide which pixels the camera can see.
    /// No outline pass or screen-space composite is involved.
    /// </summary>
    internal static class ObjectiveGlintRuntime
    {
        private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
        private static readonly int EmissiveColorMapId = Shader.PropertyToID("_EmissiveColorMap");
        private static readonly int SurfaceTypeId = Shader.PropertyToID("_SurfaceType");
        private static readonly int AlphaCutoffId = Shader.PropertyToID("_AlphaCutoffEnable");
        private static readonly int AlphaClipId = Shader.PropertyToID("_AlphaClip");
        private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");
        private static readonly int ZTestId = Shader.PropertyToID("_ZTest");
        private static readonly int OpaqueZTestId = Shader.PropertyToID("_ZTestDepthEqualForOpaque");
        private static readonly int GBufferZTestId = Shader.PropertyToID("_ZTestGBuffer");
        private static readonly int UseEmissiveIntensityId = Shader.PropertyToID("_UseEmissiveIntensity");
        private const string CctvOutlineTypeName = "Y4NGZCompany.ShipSystems.Surveillance.OutlineEffect.Outline";

        private static readonly ObjectiveGlintPolicy Policy = new ObjectiveGlintPolicy();
        private static readonly List<Renderer> RendererBuffer = new List<Renderer>();
        private static readonly List<MonoBehaviour> ComponentBuffer = new List<MonoBehaviour>();
        private static readonly List<Material> MaterialBuffer = new List<Material>();
        private static readonly List<ActiveCue> Active = new List<ActiveCue>();

        private sealed class ActiveCue
        {
            internal Component Target;
            internal Camera Camera;
            internal float StartedAt;
            internal Color Accent;
            internal readonly List<OwnedRenderer> Renderers = new List<OwnedRenderer>();
        }

        private sealed class OwnedSlot
        {
            internal int Index;
            internal Material Source;
            internal Material Clone;
            internal Color Emission;
        }

        private sealed class OwnedRenderer
        {
            internal Renderer Renderer;
            internal readonly List<OwnedSlot> Slots = new List<OwnedSlot>();
        }

        internal static void TryShow(Component target, Camera camera, bool pityBlinkActive)
        {
            if (target == null || !target.gameObject.activeInHierarchy)
                return;
            GrabbableObject targetItem = target.GetComponentInParent<GrabbableObject>();
            int targetId = (targetItem != null ? targetItem.gameObject : target.gameObject).GetInstanceID();

            // Discovery is consumed even when presentation is suppressed (including a disabled switch).
            bool enabled = ObjectiveGlintPolicy.Enabled(
                ContractsBootstrap.ObjectiveHintsEnabled != null && ContractsBootstrap.ObjectiveHintsEnabled.Value,
                ContractsBootstrap.ObjectiveFirstSightGlintEnabled != null
                    && ContractsBootstrap.ObjectiveFirstSightGlintEnabled.Value);
            bool outlined = HasOutline(target);
            if (!Policy.TryConsume(targetId, enabled && camera != null && camera.enabled, pityBlinkActive, outlined))
                return;

            var cue = new ActiveCue
            {
                Target = target,
                Camera = camera,
                StartedAt = Time.unscaledTime,
                Accent = ContractUiTheme.HudAccent * 0.65f
            };
            cue.Accent.a = 0f;

            Transform root = target.transform;
            bool targetIsPlayer = target is PlayerControllerB;
            root.GetComponentsInChildren(false, RendererBuffer);
            for (int i = 0; i < RendererBuffer.Count; i++)
            {
                Renderer renderer = RendererBuffer[i];
                if (renderer == null)
                    continue;
                if (!IsEligible(renderer, camera.cullingMask, targetItem, targetIsPlayer)
                    || HasOutline(renderer) || renderer.HasPropertyBlock() || IsOwned(renderer))
                    continue;

                OwnEligibleSlots(renderer, cue);
            }

            RendererBuffer.Clear();
            if (cue.Renderers.Count != 0)
                Active.Add(cue);
        }

        internal static void Tick(Camera camera, bool enabled)
        {
            if (Active.Count == 0)
                return;
            if (!enabled || ContractsBootstrap.ObjectiveHintsEnabled == null
                || !ContractsBootstrap.ObjectiveHintsEnabled.Value
                || ContractsBootstrap.ObjectiveFirstSightGlintEnabled == null
                || !ContractsBootstrap.ObjectiveFirstSightGlintEnabled.Value
                || camera == null || !camera.enabled)
            {
                ClearActive();
                return;
            }

            for (int i = Active.Count - 1; i >= 0; i--)
            {
                ActiveCue cue = Active[i];
                float elapsed = Time.unscaledTime - cue.StartedAt;
                if (cue.Camera != camera || cue.Target == null || !cue.Target.gameObject.activeInHierarchy
                    || elapsed < 0f || elapsed >= ObjectiveGlintPolicy.DurationSeconds
                    || HasOutline(cue.Target) || !OwnsUnchangedSlots(cue))
                {
                    RemoveCueAt(i);
                    continue;
                }

                Color boost = cue.Accent * ObjectiveGlintPolicy.Envelope(elapsed);
                for (int r = 0; r < cue.Renderers.Count; r++)
                {
                    List<OwnedSlot> slots = cue.Renderers[r].Slots;
                    for (int s = 0; s < slots.Count; s++)
                    {
                        OwnedSlot slot = slots[s];
                        slot.Clone.SetColor(EmissiveColorId, slot.Emission + boost);
                    }
                }
            }
        }

        internal static void CancelTargets(List<Component> targets)
        {
            if (targets == null || targets.Count == 0)
                return;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Component target = Active[i].Target;
                if (target == null)
                {
                    RemoveCueAt(i);
                    continue;
                }
                for (int t = 0; t < targets.Count; t++)
                {
                    Component candidate = targets[t];
                    if (candidate == null || !(ReferenceEquals(candidate, target)
                        || candidate.transform.IsChildOf(target.transform)
                        || target.transform.IsChildOf(candidate.transform)))
                        continue;
                    RemoveCueAt(i);
                    break;
                }
            }
        }

        internal static void ClearActive()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
                RemoveCueAt(i);
        }

        internal static void ResetRunState()
        {
            ClearActive();
            Policy.Reset();
        }

        private static bool IsOwned(Renderer renderer)
        {
            for (int i = 0; i < Active.Count; i++)
            {
                List<OwnedRenderer> owned = Active[i].Renderers;
                for (int r = 0; r < owned.Count; r++)
                {
                    if (owned[r].Renderer == renderer)
                        return true;
                }
            }
            return false;
        }

        private static bool OwnsUnchangedSlots(ActiveCue cue)
        {
            for (int r = 0; r < cue.Renderers.Count; r++)
            {
                OwnedRenderer owner = cue.Renderers[r];
                Renderer renderer = owner.Renderer;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy
                    || renderer.HasPropertyBlock())
                    return false;

                renderer.GetSharedMaterials(MaterialBuffer);
                bool unchanged = true;
                for (int s = 0; s < owner.Slots.Count; s++)
                {
                    OwnedSlot slot = owner.Slots[s];
                    if (slot.Index >= MaterialBuffer.Count || MaterialBuffer[slot.Index] != slot.Clone)
                    {
                        unchanged = false;
                        break;
                    }
                }
                MaterialBuffer.Clear();
                if (!unchanged)
                    return false;
            }
            return true;
        }

        private static void RemoveCueAt(int index)
        {
            ActiveCue cue = Active[index];
            for (int r = 0; r < cue.Renderers.Count; r++)
            {
                OwnedRenderer owner = cue.Renderers[r];
                Renderer renderer = owner.Renderer;
                if (renderer != null)
                {
                    renderer.GetSharedMaterials(MaterialBuffer);
                    bool restore = false;
                    for (int s = 0; s < owner.Slots.Count; s++)
                    {
                        OwnedSlot slot = owner.Slots[s];
                        if (slot.Index >= MaterialBuffer.Count || MaterialBuffer[slot.Index] != slot.Clone)
                            continue;
                        MaterialBuffer[slot.Index] = slot.Source;
                        restore = true;
                    }
                    if (restore)
                        renderer.SetSharedMaterials(MaterialBuffer);
                    MaterialBuffer.Clear();
                }
                for (int s = 0; s < owner.Slots.Count; s++)
                {
                    Material clone = owner.Slots[s].Clone;
                    if (clone != null)
                        UnityEngine.Object.Destroy(clone);
                }
            }
            Active.RemoveAt(index);
        }

        private static void OwnEligibleSlots(Renderer renderer, ActiveCue cue)
        {
            renderer.GetSharedMaterials(MaterialBuffer);
            OwnedRenderer owner = null;
            for (int i = 0; i < MaterialBuffer.Count; i++)
            {
                Material source = MaterialBuffer[i];
                if (!CanClone(source))
                    continue;

                if (owner == null)
                    owner = new OwnedRenderer { Renderer = renderer };
                Material clone = new Material(source)
                {
                    name = source.name + " (Objective first sight)",
                    hideFlags = HideFlags.HideAndDontSave
                };
                Color emission = source.GetColor(EmissiveColorId);
                if (clone.HasProperty(UseEmissiveIntensityId))
                    clone.SetFloat(UseEmissiveIntensityId, 0f);
                clone.EnableKeyword("_EMISSION");
                clone.SetColor(EmissiveColorId, emission);
                owner.Slots.Add(new OwnedSlot { Index = i, Source = source, Clone = clone, Emission = emission });
                MaterialBuffer[i] = clone;
            }

            if (owner != null)
            {
                cue.Renderers.Add(owner);
                renderer.SetSharedMaterials(MaterialBuffer);
            }
            MaterialBuffer.Clear();
        }

        private static bool CanClone(Material source)
        {
            if (source == null || source.shader == null || !source.shader.isSupported
                || !string.Equals(source.shader.name, "HDRP/Lit", StringComparison.Ordinal)
                || !source.HasProperty(EmissiveColorId)
                || !source.HasProperty(SurfaceTypeId) || source.GetFloat(SurfaceTypeId) != 0f
                || source.renderQueue < (int)RenderQueue.Geometry || source.renderQueue >= (int)RenderQueue.AlphaTest
                || source.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")
                || source.IsKeywordEnabled("_ALPHATEST_ON")
                || (source.HasProperty(AlphaCutoffId) && source.GetFloat(AlphaCutoffId) != 0f)
                || (source.HasProperty(AlphaClipId) && source.GetFloat(AlphaClipId) != 0f)
                || (source.HasProperty(EmissiveColorMapId) && source.GetTexture(EmissiveColorMapId) != null)
                || (source.HasProperty(ZWriteId) && source.GetFloat(ZWriteId) != 1f)
                || (source.HasProperty(ZTestId) && !DepthTestIsNormal(source.GetFloat(ZTestId)))
                || (source.HasProperty(OpaqueZTestId) && !DepthTestIsNormal(source.GetFloat(OpaqueZTestId)))
                || (source.HasProperty(GBufferZTestId) && !DepthTestIsNormal(source.GetFloat(GBufferZTestId))))
                return false;

            string renderType = source.GetTag("RenderType", false, string.Empty);
            return string.IsNullOrEmpty(renderType) || string.Equals(renderType, "Opaque", StringComparison.Ordinal);
        }

        private static bool DepthTestIsNormal(float value)
        {
            return value == (float)CompareFunction.Equal || value == (float)CompareFunction.LessEqual;
        }

        private static bool IsEligible(Renderer renderer, int cullingMask, GrabbableObject targetItem, bool targetIsPlayer)
        {
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy
                || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
                || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly
                || (cullingMask & (1 << renderer.gameObject.layer)) == 0
                || renderer.GetComponentInParent<ScanNodeProperties>() != null
                || (!targetIsPlayer && renderer.GetComponentInParent<PlayerControllerB>() != null))
                return false;

            GrabbableObject item = renderer.GetComponentInParent<GrabbableObject>();
            return item == null || item == targetItem;
        }

        private static bool HasOutline(Component target)
        {
            Renderer ancestor = target.GetComponentInParent<Renderer>();
            if (ancestor != null && HasOutline(ancestor))
                return true;
            target.transform.GetComponentsInChildren(false, RendererBuffer);
            bool found = false;
            for (int i = 0; i < RendererBuffer.Count; i++)
            {
                Renderer renderer = RendererBuffer[i];
                if (renderer != null && HasOutline(renderer))
                {
                    found = true;
                    break;
                }
            }
            RendererBuffer.Clear();
            return found;
        }

        private static bool HasOutline(Renderer renderer)
        {
            renderer.GetComponents(ComponentBuffer);
            for (int i = 0; i < ComponentBuffer.Count; i++)
            {
                MonoBehaviour component = ComponentBuffer[i];
                if (component == null || !component.isActiveAndEnabled)
                    continue;
                Type type = component.GetType();
                if (type == typeof(ObjectiveOutline)
                    || string.Equals(type.FullName, CctvOutlineTypeName, StringComparison.Ordinal))
                {
                    ComponentBuffer.Clear();
                    return true;
                }
            }
            ComponentBuffer.Clear();
            return false;
        }

    }
}
