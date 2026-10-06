using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts._Shared
{
    internal static partial class ContractAssetLoader
    {
        private static AssetBundle _seraphimBundle;
        private static bool _seraphimSidecarAttempted;
        private static readonly string[] SeraphimSidecarClipNames =
        {
            "Ophanim|awakening_2", "Ophanim|idle_rotation_2", "Ophanim|idle_fast_rotation_2",
            "Ophanim|atack_wheel_start_2", "Ophanim|atack_wheel_2", "Ophanim|atack_wheel_end_2",
            "Ophanim|idle_no_rotation_2",
        };
        private static readonly string[] SeraphimSidecarMaterialNames = { "SeraphimBody", "SeraphimCards", "SeraphimEye" };
        private const int SeraphimSidecarTriangleBudget = 25000;

        // Publish the visual and controller together only after every load-time check succeeds.
        // Keeping the bundle alive also keeps shared meshes, textures and clips alive for all spawns.
        private static bool TryLoadSeraphimVisualBundle()
        {
            if (_seraphimSidecarAttempted)
                return _seraphimBundle != null;
            _seraphimSidecarAttempted = true;
            AssetBundle candidate = null;
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(ContractAssetLoader).Assembly.Location) ?? string.Empty,
                    "y4ngz-seraphim.bundle");
                if (!File.Exists(path))
                    throw new FileNotFoundException("Sidecar is missing", path);
                candidate = AssetBundle.LoadFromFile(path);
                if (candidate == null)
                    throw new InvalidDataException("Sidecar could not be loaded");
                var prefab = candidate.LoadAsset<GameObject>("seraphim.prefab");
                var controller = candidate.LoadAsset<RuntimeAnimatorController>("seraphim.controller");
                ValidateSeraphimSidecar(candidate, prefab, controller);
                PrepareAuthoredSeraphimMaterials(prefab);
                SeraphimPrefab = prefab;
                SeraphimController = controller;
                _seraphimBundle = candidate;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Loaded validated Seraphim sidecar visual and controller.");
                return true;
            }
            catch (Exception e)
            {
                if (candidate != null)
                    candidate.Unload(true);
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Seraphim sidecar rejected; retaining the legacy visual and controller: " + e.Message);
                return false;
            }
        }

        private static void ValidateSeraphimSidecar(AssetBundle bundle, GameObject prefab, RuntimeAnimatorController controller)
        {
            if (prefab == null || controller == null)
                throw new InvalidDataException("Missing Seraphim prefab or controller");
            foreach (Component component in prefab.GetComponentsInChildren<Component>(true))
            {
                if (!(component is Transform) && !(component is SkinnedMeshRenderer) && !(component is Animator))
                    throw new InvalidDataException("Seraphim prefab is not visual-only");
            }
            var animators = prefab.GetComponentsInChildren<Animator>(true);
            if (animators.Length != 1 || animators[0].runtimeAnimatorController != controller
                || animators[0].avatar == null || !animators[0].avatar.isValid
                || animators[0].transform.Find("Ophanim/Root") == null)
                throw new InvalidDataException("Invalid Seraphim controller/avatar/root pairing");
            // The clips ship as the controller's dependencies, keeping the exact state names that
            // SeraphimEnemyAI's clip-length lookup matches against clip.name.
            AnimationClip[] clips = controller.animationClips;
            if (clips.Length != SeraphimSidecarClipNames.Length)
                throw new InvalidDataException("Unexpected Seraphim clip count");
            for (int i = 0; i < SeraphimSidecarClipNames.Length; i++)
            {
                string name = SeraphimSidecarClipNames[i];
                AnimationClip clip = null;
                foreach (AnimationClip candidate in clips)
                {
                    if (candidate == null || candidate.name != name)
                        continue;
                    if (clip != null && clip != candidate)
                        throw new InvalidDataException("Duplicate Seraphim clip " + name);
                    clip = candidate;
                }
                bool shouldLoop = i == 1 || i == 2 || i == 4 || i == 6;
                if (clip == null || clip.length <= 0f || clip.isLooping != shouldLoop)
                    throw new InvalidDataException("Missing or invalid Seraphim clip " + name);
            }
            ValidateSeraphimStates(prefab);
            var baseColor = bundle.LoadAsset<Texture2D>("seraphim_basecolor");
            ValidateSeraphimTexture(baseColor, 2048);
            var emission = bundle.LoadAsset<Texture2D>("seraphim_emission");
            ValidateSeraphimTexture(emission, 1024);
            var renderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length != 1)
                throw new InvalidDataException("Seraphim requires one skinned renderer");
            var renderer = renderers[0];
            Mesh mesh = renderer.sharedMesh;
            if (mesh == null || mesh.subMeshCount != SeraphimSidecarMaterialNames.Length || renderer.bones.Length != 60)
                throw new InvalidDataException("Invalid Seraphim mesh or bone palette");
            ulong triangles = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                triangles += mesh.GetIndexCount(i) / 3;
            if (triangles == 0 || triangles > SeraphimSidecarTriangleBudget)
                throw new InvalidDataException("Seraphim triangle budget exceeded");
            var bones = new HashSet<Transform>();
            foreach (Transform bone in renderer.bones)
            {
                if (bone == null || !bones.Add(bone))
                    throw new InvalidDataException("Invalid Seraphim bone binding");
            }
            Transform animatorRoot = animators[0].transform;
            RequireSeraphimBone(animatorRoot, bones, "Ophanim/Root");
            RequireSeraphimBone(animatorRoot, bones, "Ophanim/Root/Ophaim_main_eye");
            RequireSeraphimBone(animatorRoot, bones, "Ophanim/Root/Ophaim_main_eye/Main_eye");
            string[] rings = { "First_ring", "Second_ring", "Third_ring" };
            string[] eyes = { "Eye_first_ring", "Eye_second_ring", "Eye_Third_ring" };
            for (int ring = 0; ring < rings.Length; ring++)
            {
                string path = "Ophanim/Root/" + rings[ring];
                RequireSeraphimBone(animatorRoot, bones, path);
                for (int eye = 0; eye < 18; eye++)
                {
                    string name = eye == 0 ? eyes[ring] : eyes[ring] + "." + eye.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                    RequireSeraphimBone(animatorRoot, bones, path + "/" + name);
                }
            }
            // Body and cards are opaque so HDRP fog reaches them like terrain; only the central
            // eye is transparent with fog off, the one Lit path fog skips, so it stays a beacon.
            Material[] materials = renderer.sharedMaterials;
            if (materials.Length != SeraphimSidecarMaterialNames.Length)
                throw new InvalidDataException("Seraphim requires three materials");
            Color bodyGlow = Color.black, eyeGlow = Color.black;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                string name = SeraphimSidecarMaterialNames[i];
                bool body = i == 0;
                bool cards = i == 1;
                bool eye = i == 2;
                // Ring eyes glow on the body through the derived map, the central eye through the atlas.
                Texture emissiveMap = body ? emission : eye ? baseColor : null;
                if (material == null || material.name != name || material.shader == null
                    || material.shader.name != "HDRP/Lit" || !material.shader.isSupported
                    || material.GetTexture("_BaseColorMap") != baseColor || material.GetTexture("_MaskMap") != null
                    || material.GetTexture("_NormalMap") != null || material.GetFloat("_Metallic") != 0f
                    || !(material.GetFloat("_Smoothness") <= 0.5f)
                    || material.GetFloat("_SurfaceType") != (eye ? 1f : 0f)
                    || material.GetFloat("_AlphaCutoffEnable") != (cards ? 1f : 0f)
                    || material.GetTexture("_EmissiveColorMap") != emissiveMap
                    || (eye && material.GetFloat("_EnableFogOnTransparent") != 0f)
                    || (!cards && (!material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") || material.GetFloat("_EmissiveExposureWeight") != 0f))
                    || (cards && (material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") || material.GetColor("_EmissiveColor").maxColorComponent != 0f)))
                    throw new InvalidDataException("Invalid Seraphim material " + name);
                if (body)
                    bodyGlow = material.GetColor("_EmissiveColor");
                else if (eye)
                    eyeGlow = material.GetColor("_EmissiveColor");
            }
            // The central eye must saturate at night; the ring eyes stay a dim glow beside it.
            float centralNits = eyeGlow.maxColorComponent, ringNits = bodyGlow.maxColorComponent;
            if (!(centralNits > 1f) || !(ringNits > 0f) || !(ringNits <= 0.15f * centralNits + 1e-4f))
                throw new InvalidDataException("Invalid Seraphim eye emission");
        }

        private static void RequireSeraphimBone(Transform root, HashSet<Transform> palette, string path)
        {
            Transform bone = root.Find(path);
            if (bone == null || !palette.Remove(bone))
                throw new InvalidDataException("Missing or misbound Seraphim bone " + path);
        }

        private static void ValidateSeraphimStates(GameObject prefab)
        {
            // HasState needs an initialized Animator. This script-free, hidden probe exists only
            // during asset load, before the prefab gains any gameplay or network components.
            GameObject probe = UnityEngine.Object.Instantiate(prefab);
            try
            {
                probe.hideFlags = HideFlags.HideAndDontSave;
                foreach (Renderer renderer in probe.GetComponentsInChildren<Renderer>(true))
                    renderer.forceRenderingOff = true;
                probe.SetActive(true);
                Animator animator = probe.GetComponentInChildren<Animator>(true);
                animator.enabled = true;
                animator.Rebind();
                animator.Update(0f);
                foreach (string state in SeraphimSidecarClipNames)
                {
                    if (!animator.HasState(0, Animator.StringToHash(state)))
                        throw new InvalidDataException("Missing Seraphim layer-zero state " + state);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }
        }

        private static void ValidateSeraphimTexture(Texture2D texture, int maximum)
        {
            if (texture == null || texture.width > maximum || texture.height > maximum || texture.format != TextureFormat.BC7)
                throw new InvalidDataException("Seraphim texture exceeds its BC7 size budget");
        }
    }
}
