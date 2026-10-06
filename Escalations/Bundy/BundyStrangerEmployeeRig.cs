using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Y4NGZCompany.Escalations.Bundy
{
    /// <summary>Shared by runtime and the Unity authoring proof; never changes the live skeleton.</summary>
    internal static class BundyStrangerEmployeeRig
    {
        internal const string PrefabName = "Y4NGZ_StrangerEmployee";
        internal const string RootName = "Y4NGZ_StrangerEmployeeRoot";

        internal static bool TryAttach(GameObject host, Animator animator, GameObject prefab,
            out BundyStrangerEmployeeMarker marker, out string failure)
        {
            marker = host != null ? host.GetComponent<BundyStrangerEmployeeMarker>() : null;
            failure = null;
            if (marker != null && marker.Root != null)
                return true;

            GameObject replacement = null;
            Renderer[] originals = Array.Empty<Renderer>();
            bool[] originalEnabled = Array.Empty<bool>();
            try
            {
                Require(host != null && animator != null && prefab != null, "host, Animator or employee prefab is missing");
                Transform liveArmature = animator.transform.Find("Armature");
                Require(liveArmature != null, "the live canonical Armature is missing");
                Transform sourceArmature = prefab.transform.Find("Armature");
                Require(sourceArmature != null, "the employee donor Armature is missing");
                Transform[] sourceBones = sourceArmature.GetComponentsInChildren<Transform>(true)
                    .Where(t => t != sourceArmature).ToArray();
                Require(sourceBones.Length == 68, "the employee donor must contain exactly 68 bones");
                var liveBones = new Dictionary<string, Transform>(StringComparer.Ordinal);
                foreach (Transform bone in sourceBones)
                {
                    string path = RelativePath(sourceArmature, bone);
                    Transform live = liveArmature.Find(path);
                    Require(live != null, "missing canonical bone path: " + path);
                    Require(!liveBones.ContainsKey(bone.name), "duplicate donor bone: " + bone.name);
                    liveBones.Add(bone.name, live);
                }
                Require(liveBones.ContainsKey("pelvis") && liveBones.ContainsKey("head") &&
                    liveBones.ContainsKey("hand_l") && liveBones.ContainsKey("hand_r"), "required canonical bones are missing");
                foreach (Component component in prefab.GetComponentsInChildren<Component>(true))
                    Require(component is Transform || component is SkinnedMeshRenderer,
                        "employee prefab contains an unexpected component: " + (component != null ? component.GetType().Name : "missing script"));

                var canonical = new HashSet<Transform>(liveBones.Values);
                originals = host.GetComponentsInChildren<Renderer>(true).Where(renderer =>
                {
                    if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) return false;
                    // Keep the animated saw and any other hand attachments, regardless of mesh type.
                    if (renderer.transform.IsChildOf(liveBones["hand_l"]) || renderer.transform.IsChildOf(liveBones["hand_r"])) return false;
                    var skin = renderer as SkinnedMeshRenderer;
                    return renderer.transform.IsChildOf(liveBones["head"]) ||
                        (skin != null && skin.bones.Any(bone => bone != null && canonical.Contains(bone)));
                }).ToArray();
                Require(originals.Length > 0, "no compatible original body renderers were found");
                originalEnabled = originals.Select(r => r.enabled).ToArray();

                replacement = UnityEngine.Object.Instantiate(prefab, host.transform, false);
                replacement.name = RootName;
                replacement.SetActive(false);
                var renderers = replacement.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                Require(renderers.Length == 2 && renderers.Any(r => r.name == "Stranger_v10_Body") &&
                    renderers.Any(r => r.name == "Stranger_v10_Head"), "expected the two approved employee meshes");
                Transform donorArmature = replacement.transform.Find("Armature");
                int layer = originals[0].gameObject.layer;
                uint renderingLayerMask = originals[0].renderingLayerMask;
                foreach (var renderer in renderers)
                {
                    var mesh = renderer.sharedMesh;
                    Require(mesh != null && mesh.vertexCount > 0 && renderer.bones.Length == 68 &&
                        mesh.bindposes.Length == renderer.bones.Length, "invalid mesh or bindposes: " + renderer.name);
                    Require(renderer.sharedMaterials.Length == mesh.subMeshCount && renderer.sharedMaterials.All(m => m != null && m.shader != null),
                        "missing material or shader: " + renderer.name);
                    Require(!renderer.transform.IsChildOf(donorArmature), "renderer is nested inside the disposable donor skeleton");
                    Transform[] mapped = renderer.bones.Select(bone =>
                    {
                        Require(bone != null && liveBones.ContainsKey(bone.name), "unmapped renderer bone");
                        return liveBones[bone.name];
                    }).ToArray();
                    // Retain imported bindposes: recalculating from the live animated pose would distort the mesh.
                    renderer.bones = mapped;
                    renderer.rootBone = liveBones["pelvis"];
                    renderer.updateWhenOffscreen = true;
                    renderer.shadowCastingMode = ShadowCastingMode.On;
                    renderer.receiveShadows = true;
                    renderer.renderingLayerMask = renderingLayerMask;
                    renderer.enabled = true;
                }
                foreach (Transform child in replacement.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = layer;
                donorArmature.gameObject.SetActive(false);
                DestroyOwned(donorArmature.gameObject);
                // Commit visibility only after both meshes are fully validated and rebound.
                marker = marker != null ? marker : host.AddComponent<BundyStrangerEmployeeMarker>();
                marker.Root = replacement;
                marker.Renderers = renderers;
                marker.ReplacedRenderers = originals;
                foreach (Renderer original in originals) original.enabled = false;
                replacement.SetActive(true);
                return true;
            }
            catch (Exception exception)
            {
                for (int i = 0; i < originalEnabled.Length; i++) if (originals[i] != null) originals[i].enabled = originalEnabled[i];
                if (replacement != null) { replacement.SetActive(false); DestroyOwned(replacement); }
                if (marker != null && marker.Root == replacement) { marker.Root = null; DestroyOwned(marker); }
                marker = null;
                failure = exception.Message;
                return false;
            }
        }

        internal static T[] FilterVisibilityCache<T>(T[] cached, T[] additions, Renderer[] replaced) where T : Renderer
        {
            var hidden = new HashSet<Renderer>(replaced ?? Array.Empty<Renderer>());
            return (cached ?? Array.Empty<T>()).Concat(additions ?? Array.Empty<T>())
                .Where(renderer => renderer != null && !hidden.Contains(renderer)).Distinct().ToArray();
        }

        private static string RelativePath(Transform root, Transform node)
        {
            string path = node.name;
            while (node.parent != root)
            {
                node = node.parent;
                Require(node != null, "donor bone is outside its Armature");
                path = node.name + "/" + path;
            }
            return path;
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }

    internal sealed class BundyStrangerEmployeeMarker : MonoBehaviour
    {
        internal GameObject Root;
        internal SkinnedMeshRenderer[] Renderers = Array.Empty<SkinnedMeshRenderer>();
        internal Renderer[] ReplacedRenderers = Array.Empty<Renderer>();
    }
}
