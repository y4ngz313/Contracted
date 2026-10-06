using System;
using System.IO;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Escalations.Bundy
{
    /// <summary>
    /// #761. The vanilla-friendly Stranger: one client-local config switch that mutes every
    /// vocal cue (gated in <c>BundyAIBase.PlayBundyAudioLocal</c> through
    /// <see cref="VoicelinesMuted"/>) and parents a burlap sack to his head bone.
    ///
    /// The sack mesh is authored in Blender and ships as an OPTIONAL sidecar AssetBundle,
    /// <c>y4ngz-bundy-sack.bundle</c>, next to the plugin. Absent-safe in every direction: no
    /// bundle, no prefab or a rig without a head bone never blocks the spawn — the runtime
    /// falls back to a procedural placeholder so the toggle stays testable before the real
    /// asset exists, and a missing head bone simply logs and leaves him bare-headed.
    ///
    /// Attachment contract (see docs/assets/13-vanilla-friendly-stranger-sack.md): the
    /// prefab is authored in the BODY MESH'S OWN SPACE — the sack sits on the head in the rig's
    /// rest pose, exactly where it would sit if it were part of the body FBX. At runtime the
    /// attachment root is parented to the head bone with its local matrix set to the body
    /// mesh's bind pose for that bone, which maps mesh space onto bone space; the prefab's own
    /// transform is kept inside that root untouched. Nothing about bone axes has to be known in
    /// Blender. Only when no skinned mesh carrying the head bone can be found does the root
    /// degrade to identity (bone space), which the log names explicitly.
    /// </summary>
    internal static class BundyStrangerSack
    {
        internal const string SidecarBundleFileName = "y4ngz-bundy-sack.bundle";
        internal const string PrefabName = "Y4NGZ_StrangerSack";
        internal const string AttachmentRootName = "Y4NGZ_StrangerSackRoot";
        private const string ArmatureName = "Armature";
        private const string HeadBoneName = "head";

        // Placeholder proportions in metres at model scale 1. A capsule primitive is 2 units tall
        // and 1 wide at scale 1, so the scale below yields a 0.34 m wide, 0.46 m tall sack. The CC
        // rig's head bone sits at the top of the neck, at the BACK of the skull (measured R35b:
        // bone origin z 1.655, head mesh 1.612..1.938 tall and centred ~5 cm forward of it), so
        // the capsule is lifted and pushed forward to wrap the whole head. A capsule centred on
        // the bone origin only pokes out of the back of the skull (Lawson, 2026-09-03).
        private static readonly Vector3 PlaceholderScale = new Vector3(0.34f, 0.23f, 0.34f);
        private const float PlaceholderCentreLift = 0.13f;
        private const float PlaceholderCentreForward = 0.05f;
        private static readonly Color PlaceholderBurlap = new Color(0.52f, 0.40f, 0.25f, 1f);

        private static bool s_prefabResolved;
        private static GameObject s_prefab;
        private static AssetBundle s_sidecarBundle;

        /// <summary>The config switch. Default on: the vanilla-friendly Stranger is the shipped look.</summary>
        internal static bool Enabled => BundyPlugin.ModConfig?.VanillaFriendlyStranger?.Value ?? true;

        /// <summary>Read by the audio path on every vocal cue. Same switch, named for what it does there.</summary>
        internal static bool VoicelinesMuted => Enabled;

        /// <summary>
        /// Called from <c>BundyAIBase.Start</c> on every client after the cosmetic tints. Idempotent:
        /// a second call on an instance that already wears the sack returns immediately.
        /// </summary>
        internal static void ApplyToInstance(BundyAIBase instance)
        {
            if (instance == null || !Enabled)
                return;

            // Prefer the approved employee body. Older/missing assets keep this sack fallback.
            if (BundyStrangerEmployee.TryApplyToInstance(instance))
                return;

            try
            {
                if (instance.GetComponentInChildren<BundyStrangerSackMarker>(true) != null)
                    return;

                Transform head = ResolveHeadBone(instance);
                if (head == null)
                {
                    BundyPlugin.Log?.LogWarning(
                        "[Bundy] Vanilla-friendly sack skipped: no 'head' bone was found on this instance, " +
                        "so he stays bare-headed. Voicelines are still muted.");
                    return;
                }

                var root = new GameObject(AttachmentRootName);
                BundyStrangerSackMarker marker = root.AddComponent<BundyStrangerSackMarker>();
                root.transform.SetParent(head, false);
                root.transform.localPosition = Vector3.zero;
                root.transform.localRotation = Quaternion.identity;
                root.transform.localScale = Vector3.one;

                int layer = ResolveBodyLayer(instance);
                root.layer = layer;

                GameObject prefab = ResolvePrefab();
                string placement;
                if (prefab != null)
                {
                    placement = ApplyBindPoseToRoot(instance, head, root.transform)
                        ? "bind pose (mesh space)"
                        : "identity (bone space; no skinned mesh carried the head bone)";
                    GameObject sack = UnityEngine.Object.Instantiate(prefab, root.transform, false);
                    sack.name = PrefabName;
                    marker.Placeholder = false;
                }
                else
                {
                    BuildPlaceholder(instance, head, root.transform);
                    placement = "placeholder (world-fit capsule)";
                    marker.Placeholder = true;
                }

                PrepareRenderers(root, layer);
                marker.HiddenHair = HideHair(instance);

                BundyPlugin.Log?.LogInfo(
                    $"[Bundy] Vanilla-friendly sack attached to '{head.name}': " +
                    $"{(marker.Placeholder ? "procedural placeholder" : "prefab '" + prefab.name + "'")}, " +
                    $"placement={placement}, hairRenderersHidden={marker.HiddenHair?.Length ?? 0}, layer={layer}.");
            }
            catch (Exception e)
            {
                BundyPlugin.Log?.LogWarning($"[Bundy] Vanilla-friendly sack failed to attach; he stays bare-headed. {e.GetType().Name}: {e.Message}");
            }
        }

        private static Transform ResolveHeadBone(BundyAIBase instance)
        {
            Animator animator = instance.creatureAnimator ?? instance.GetComponentInChildren<Animator>(true);
            Transform searchRoot = instance.transform;
            if (animator != null)
                searchRoot = FindChildRecursive(animator.transform, ArmatureName) ?? animator.transform;

            Transform head = FindChildRecursive(searchRoot, HeadBoneName)
                ?? FindChildRecursive(instance.transform, HeadBoneName);
            if (head == null && animator != null && animator.avatar != null && animator.avatar.isHuman)
                head = animator.GetBoneTransform(HumanBodyBones.Head);
            return head;
        }

        private static GameObject ResolvePrefab()
        {
            if (s_prefabResolved)
                return s_prefab;
            s_prefabResolved = true;

            try
            {
                // A future bundy.bundle rebuild may carry the sack itself; prefer that so the sidecar
                // becomes redundant rather than a second copy.
                if (BundyPlugin.ModAssets != null)
                {
                    s_prefab = BundyPlugin.ModAssets.LoadAsset<GameObject>(PrefabName);
                    if (s_prefab != null)
                    {
                        BundyPlugin.Log?.LogInfo($"[Bundy] Vanilla-friendly sack prefab '{PrefabName}' loaded from {BundyPlugin.BundleFilename}.");
                        return s_prefab;
                    }
                }

                string assemblyDir = Path.GetDirectoryName(typeof(BundyStrangerSack).Assembly.Location);
                string sidecarPath = string.IsNullOrEmpty(assemblyDir) ? null : Path.Combine(assemblyDir, SidecarBundleFileName);
                if (sidecarPath == null || !File.Exists(sidecarPath))
                {
                    BundyPlugin.Log?.LogInfo($"[Bundy] No '{SidecarBundleFileName}' next to the plugin; the vanilla-friendly sack uses the procedural placeholder.");
                    return null;
                }

                s_sidecarBundle = AssetBundle.LoadFromFile(sidecarPath);
                if (s_sidecarBundle == null)
                {
                    BundyPlugin.Log?.LogWarning($"[Bundy] AssetBundle.LoadFromFile returned null for '{sidecarPath}'; using the placeholder sack.");
                    return null;
                }

                s_prefab = s_sidecarBundle.LoadAsset<GameObject>(PrefabName);
                if (s_prefab == null)
                {
                    GameObject[] all = s_sidecarBundle.LoadAllAssets<GameObject>();
                    if (all != null && all.Length > 0)
                    {
                        s_prefab = all[0];
                        BundyPlugin.Log?.LogWarning($"[Bundy] '{SidecarBundleFileName}' has no prefab named '{PrefabName}'; using its first prefab '{s_prefab.name}'.");
                    }
                }

                if (s_prefab == null)
                    BundyPlugin.Log?.LogWarning($"[Bundy] '{SidecarBundleFileName}' contains no GameObject prefab; using the placeholder sack.");
                else
                    BundyPlugin.Log?.LogInfo($"[Bundy] Vanilla-friendly sack prefab '{s_prefab.name}' loaded from '{sidecarPath}'.");
            }
            catch (Exception e)
            {
                BundyPlugin.Log?.LogWarning($"[Bundy] Vanilla-friendly sack prefab load failed; using the placeholder. {e.GetType().Name}: {e.Message}");
                s_prefab = null;
            }

            return s_prefab;
        }

        /// <summary>
        /// Sets <paramref name="root"/>'s local transform to the body mesh's bind pose for the head
        /// bone. bindposes[i] maps mesh-local space into bone i's local space, so a child of the
        /// bone carrying that matrix places mesh-space authored geometry exactly where it sat on the
        /// rest pose and animates with the bone from there.
        /// </summary>
        private static bool ApplyBindPoseToRoot(BundyAIBase instance, Transform head, Transform root)
        {
            SkinnedMeshRenderer[] skinned = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRenderer best = null;
            int bestIndex = -1;
            foreach (SkinnedMeshRenderer smr in skinned)
            {
                if (smr == null || smr.sharedMesh == null)
                    continue;
                Transform[] bones = smr.bones;
                Matrix4x4[] bindposes = smr.sharedMesh.bindposes;
                if (bones == null || bindposes == null || bones.Length != bindposes.Length)
                    continue;
                int index = Array.IndexOf(bones, head);
                if (index < 0)
                    continue;
                // Prefer the richest skin: the body carries the whole rig, eyes/teeth carry a few bones.
                if (best == null || bones.Length > best.bones.Length)
                {
                    best = smr;
                    bestIndex = index;
                }
            }

            if (best == null)
                return false;

            Matrix4x4 bind = best.sharedMesh.bindposes[bestIndex];
            root.localPosition = bind.GetColumn(3);
            root.localRotation = bind.rotation;
            root.localScale = bind.lossyScale;
            return true;
        }

        private static void BuildPlaceholder(BundyAIBase instance, Transform head, Transform root)
        {
            // World-fit rather than bone-fit: the CC rig's bone axes are not something a placeholder
            // should depend on. Size and lift from the instance's own up vector and scale, then
            // parent with world position kept so the capsule rides the bone from there.
            GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = PrefabName + "_Placeholder";
            Collider collider = capsule.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.Destroy(collider);

            float modelScale = Mathf.Max(0.01f, instance.transform.lossyScale.y);
            Vector3 up = instance.transform.up;
            Vector3 forward = instance.transform.forward;
            capsule.transform.position = head.position
                + up * (PlaceholderCentreLift * modelScale)
                + forward * (PlaceholderCentreForward * modelScale);
            capsule.transform.rotation = Quaternion.LookRotation(forward, up);
            capsule.transform.localScale = PlaceholderScale * modelScale;
            capsule.transform.SetParent(root, true);

            Renderer renderer = capsule.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("HDRP/Unlit");
                if (shader != null)
                {
                    var material = new Material(shader) { name = "Y4NGZ_StrangerSack_Placeholder" };
                    if (material.HasProperty("_BaseColor"))
                        material.SetColor("_BaseColor", PlaceholderBurlap);
                    if (material.HasProperty("_Smoothness"))
                        material.SetFloat("_Smoothness", 0.12f);
                    renderer.sharedMaterial = material;
                }
            }
        }

        private static void PrepareRenderers(GameObject root, int layer)
        {
            // The sack must never be a collider: the chainsaw blade hitbox, the body hitbox and the
            // gate probes all raycast around the head and a stray collider there would block them.
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.Destroy(collider);

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = layer;

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }
        }

        private static int ResolveBodyLayer(BundyAIBase instance)
        {
            SkinnedMeshRenderer body = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
            return body != null ? body.gameObject.layer : instance.gameObject.layer;
        }

        /// <summary>
        /// Hair and scalp are alpha-blended cards that would poke through any sack; hide those
        /// renderers wholesale. Same material-name test as the cosmetic tint pass in
        /// <c>BundyAIBase.Setup.cs</c> so the two agree on what "hair" is.
        /// </summary>
        private static Renderer[] HideHair(BundyAIBase instance)
        {
            var hidden = new System.Collections.Generic.List<Renderer>();
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer.GetComponentInParent<BundyStrangerSackMarker>() != null)
                    continue;
                bool isHair = false;
                foreach (Material shared in renderer.sharedMaterials)
                {
                    if (shared == null)
                        continue;
                    if (shared.name.IndexOf("Hair_Transparency", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        shared.name.IndexOf("Scalp_Transparency", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        isHair = true;
                        break;
                    }
                }
                if (!isHair || !renderer.enabled)
                    continue;
                renderer.enabled = false;
                hidden.Add(renderer);
            }
            return hidden.ToArray();
        }

        private static Transform FindChildRecursive(Transform parent, string childName)
        {
            if (parent == null)
                return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (string.Equals(child.name, childName, StringComparison.Ordinal))
                    return child;
                Transform nested = FindChildRecursive(child, childName);
                if (nested != null)
                    return nested;
            }
            return null;
        }
    }

    /// <summary>
    /// Marks the sack attachment root on an instance so <see cref="BundyStrangerSack.ApplyToInstance"/>
    /// is idempotent, and remembers what it hid so a debug path can restore it.
    /// </summary>
    internal sealed class BundyStrangerSackMarker : MonoBehaviour
    {
        internal bool Placeholder;
        internal Renderer[] HiddenHair;
    }
}
