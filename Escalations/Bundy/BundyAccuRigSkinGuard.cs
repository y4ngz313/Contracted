using System;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.Bundy
{
    internal sealed class BundyAccuRigSkinGuard : MonoBehaviour
    {
        private const string BasisRootName = "root";
        private const string MeshRootName = "unamedmesh";
        private const float TransformEpsilon = 0.0005f;
        private const float BoundsWarnSize = 8f;

        private Transform _basisRoot;
        private Transform _meshRoot;
        private Transform _hipRoot;
        private Vector3 _basisLocalPosition;
        private Quaternion _basisLocalRotation;
        private Vector3 _basisLocalScale;
        private Vector3 _meshLocalPosition;
        private Quaternion _meshLocalRotation;
        private Vector3 _meshLocalScale;
        private Vector3 _hipLocalPosition;
        private Vector3 _hipLocalScale;
        private SkinnedMeshRenderer[] _skinnedRenderers;
        private bool _captured;
        private bool _loggedReady;
        private bool _loggedCorrection;
        private bool _loggedHipCorrection;
        private bool _loggedBoundsWarning;
        private float _nextBoundsProbeAt;

        internal bool PinHipTranslation { get; set; } = true;

        internal void ConfigurePrefabVisual()
        {
            CaptureBindPose("prefab");
            PrepareRenderers();
        }

        private void Awake()
        {
            CaptureBindPose("awake");
            PrepareRenderers();
            PinBasisTransforms("awake");
        }

        private void OnEnable()
        {
            CaptureBindPose("enable");
            PrepareRenderers();
            PinBasisTransforms("enable");
        }

        private void LateUpdate()
        {
            if (!_captured)
                CaptureBindPose("late");

            PinBasisTransforms("late");

            if (Time.time >= _nextBoundsProbeAt)
            {
                _nextBoundsProbeAt = Time.time + 0.5f;
                ProbeRendererBounds();
            }
        }

        private void CaptureBindPose(string context)
        {
            if (_captured)
                return;

            _basisRoot = FindDirectChild(transform, BasisRootName);
            _meshRoot = FindDirectChild(transform, MeshRootName);
            _skinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _hipRoot = ResolveHipRoot(_skinnedRenderers);

            if (_basisRoot == null || _meshRoot == null || _hipRoot == null)
            {
                LogWarning(
                    $"AccuRigSkinGuard could not arm on '{HierarchyPath(transform)}' during {context}: " +
                    $"basisRoot={NameOrMissing(_basisRoot)}, meshRoot={NameOrMissing(_meshRoot)}, " +
                    $"hipRoot={NameOrMissing(_hipRoot)}, skinned={_skinnedRenderers?.Length ?? 0}.");
                return;
            }

            _basisLocalPosition = _basisRoot.localPosition;
            _basisLocalRotation = _basisRoot.localRotation;
            _basisLocalScale = _basisRoot.localScale;
            _meshLocalPosition = _meshRoot.localPosition;
            _meshLocalRotation = _meshRoot.localRotation;
            _meshLocalScale = _meshRoot.localScale;
            _hipLocalPosition = _hipRoot.localPosition;
            _hipLocalScale = _hipRoot.localScale;
            _captured = true;

            if (!_loggedReady)
            {
                _loggedReady = true;
                LogInfo(
                    $"AccuRigSkinGuard armed on '{HierarchyPath(transform)}': " +
                    $"basis='{_basisRoot.name}' pos={Format(_basisLocalPosition)} rot={Format(_basisLocalRotation)} " +
                    $"mesh='{_meshRoot.name}' pos={Format(_meshLocalPosition)} rot={Format(_meshLocalRotation)} " +
                    $"hip='{_hipRoot.name}' pos={Format(_hipLocalPosition)} " +
                    $"skinned={_skinnedRenderers.Length}.");
            }
        }

        private void PrepareRenderers()
        {
            if (_skinnedRenderers == null || _skinnedRenderers.Length == 0)
                _skinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);

            for (int i = 0; i < _skinnedRenderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = _skinnedRenderers[i];
                if (renderer == null)
                    continue;

                renderer.enabled = true;
                renderer.forceRenderingOff = false;
                renderer.updateWhenOffscreen = false;
                renderer.allowOcclusionWhenDynamic = true;
            }
        }

        private void PinBasisTransforms(string context)
        {
            if (!_captured || _basisRoot == null || _meshRoot == null)
                return;

            bool corrected = false;
            corrected |= PinTransform(_basisRoot, _basisLocalPosition, _basisLocalRotation, _basisLocalScale);
            corrected |= PinTransform(_meshRoot, _meshLocalPosition, _meshLocalRotation, _meshLocalScale);

            if (corrected && !_loggedCorrection)
            {
                _loggedCorrection = true;
                LogWarning(
                    $"AccuRigSkinGuard corrected animated FBX basis on '{HierarchyPath(transform)}' during {context}. " +
                    "This prevents source-pack root curves from pulling the skinned mesh away from its bind pose.");
            }

            if (PinHipTranslation)
                PinHipTransform(context);
        }

        private static bool PinTransform(Transform target, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            bool changed = Vector3.Distance(target.localPosition, position) > TransformEpsilon
                || Quaternion.Angle(target.localRotation, rotation) > 0.05f
                || Vector3.Distance(target.localScale, scale) > TransformEpsilon;

            if (!changed)
                return false;

            target.localPosition = position;
            target.localRotation = rotation;
            target.localScale = scale;
            return true;
        }

        private void ProbeRendererBounds()
        {
            if (_skinnedRenderers == null)
                return;

            for (int i = 0; i < _skinnedRenderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = _skinnedRenderers[i];
                if (renderer == null)
                    continue;

                Vector3 size = renderer.bounds.size;
                float max = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                if (!float.IsNaN(max) && !float.IsInfinity(max) && max <= BoundsWarnSize)
                    continue;

                PinBasisTransforms("bounds-probe");
                if (!_loggedBoundsWarning)
                {
                    _loggedBoundsWarning = true;
                    LogWarning(
                        $"AccuRigSkinGuard saw exploded renderer bounds on '{HierarchyPath(renderer.transform)}': " +
                        $"size={Format(size)}, rootBone={NameOrMissing(renderer.rootBone)}, mesh={NameOrMissing(renderer.sharedMesh)}. " +
                        "The guard pinned the AccuRig basis again.");
                }
            }
        }

        private void PinHipTransform(string context)
        {
            if (_hipRoot == null)
                return;

            bool corrected = Vector3.Distance(_hipRoot.localPosition, _hipLocalPosition) > TransformEpsilon
                || Vector3.Distance(_hipRoot.localScale, _hipLocalScale) > TransformEpsilon;
            if (!corrected)
                return;

            Vector3 animatedPosition = _hipRoot.localPosition;
            _hipRoot.localPosition = _hipLocalPosition;
            _hipRoot.localScale = _hipLocalScale;

            if (!_loggedHipCorrection)
            {
                _loggedHipCorrection = true;
                LogWarning(
                    $"AccuRigSkinGuard pinned AccuRig hip translation on '{HierarchyPath(transform)}' during {context}: " +
                    $"animatedLocal={Format(animatedPosition)}, bindLocal={Format(_hipLocalPosition)}. " +
                    "This keeps the chainsaw controller's rotations but removes the bad 100x humanoid body translation.");
            }
        }

        private static Transform ResolveHipRoot(SkinnedMeshRenderer[] renderers)
        {
            if (renderers == null)
                return null;

            for (int i = 0; i < renderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                if (renderer != null && renderer.rootBone != null)
                    return renderer.rootBone;
            }

            return null;
        }

        private static Transform FindDirectChild(Transform parent, string name)
        {
            if (parent == null)
                return null;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child != null && child.name == name)
                    return child;
            }

            return null;
        }

        private static string NameOrMissing(UnityEngine.Object obj)
        {
            return obj != null ? obj.name : "<missing>";
        }

        private static string Format(Vector3 value)
        {
            return $"({value.x:F3}, {value.y:F3}, {value.z:F3})";
        }

        private static string Format(Quaternion value)
        {
            return $"({value.x:F3}, {value.y:F3}, {value.z:F3}, {value.w:F3})";
        }

        private static string HierarchyPath(Transform target)
        {
            if (target == null)
                return "<null>";

            string path = target.name;
            Transform current = target.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private static void LogInfo(string message)
        {
            if (!BundyPlugin.DebugLogsEnabled)
                return;

            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogInfo("[Bundy] " + message);
        }

        private static void LogWarning(string message)
        {
            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogWarning("[Bundy] " + message);
            else
                Debug.LogWarning("[Bundy] " + message);
        }
    }
}
