using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.Congregation
{
    /// <summary>What a congregant holds, and so what it drops (#845 C).</summary>
    internal enum CongregationPropKind
    {
        None,
        Knife,
        Drumstick
    }

    /// <summary>
    /// #845 C: the knife or drumstick a congregant holds in <c>CC_Base_R_Hand</c>.
    ///
    /// <b>Every peer builds this itself</b> from the optional props sidecar; nothing replicates. The
    /// congregant derives the kind from its synced role, drumming flag and death, and each prop is
    /// instantiated once, on first use, under the hand bone, so it is destroyed with the body. An
    /// absent prop shows nothing (the sidecar reports the absence once).
    /// </summary>
    internal sealed class CongregantHandProp
    {
        private const string HandBoneName = "CC_Base_R_Hand";

        // Grip pose under the hand socket. The socket cancels the bone's world scale, so positions
        // are metres and scales are relative to the prop as authored, both in the hand bone's axes.
        // Judged against the built props on the v12 rig at the knife contact frames and the drum beat frame (#845 C).
        internal static readonly Vector3 KnifeGripLocalPosition = new Vector3(0f, 0f, -0.06f);
        internal static readonly Vector3 KnifeGripLocalEuler = new Vector3(0f, 225f, 0f);
        internal static readonly Vector3 KnifeGripLocalScale = Vector3.one;
        internal static readonly Vector3 DrumstickGripLocalPosition = new Vector3(0f, 0f, -0.06f);
        internal static readonly Vector3 DrumstickGripLocalEuler = new Vector3(0f, 225f, 0f);
        internal static readonly Vector3 DrumstickGripLocalScale = Vector3.one;

        private static bool _missingHandLogged;

        private readonly Transform _rig;
        private readonly int _layer;
        private Transform _socket;
        private bool _socketResolved;
        private GameObject _knife;
        private bool _knifeBuilt;
        private GameObject _drumstick;
        private bool _drumstickBuilt;
        private CongregationPropKind _shown = CongregationPropKind.None;

        internal CongregantHandProp(Transform rig, int layer)
        {
            _rig = rig;
            _layer = layer;
        }

        /// <summary>Shows <paramref name="kind"/> and hides the other prop. Idempotent and cheap
        /// when the kind is unchanged.</summary>
        internal void Show(CongregationPropKind kind)
        {
            if (kind == _shown)
                return;
            _shown = kind;

            if (kind == CongregationPropKind.Knife && !_knifeBuilt)
            {
                _knifeBuilt = true;
                _knife = Build(CongregationPropsAssets.Knife, "CongregantKnife", KnifeGripLocalPosition, KnifeGripLocalEuler, KnifeGripLocalScale);
            }
            else if (kind == CongregationPropKind.Drumstick && !_drumstickBuilt)
            {
                _drumstickBuilt = true;
                _drumstick = Build(CongregationPropsAssets.Drumstick, "CongregantDrumstick", DrumstickGripLocalPosition, DrumstickGripLocalEuler, DrumstickGripLocalScale);
            }

            if (_knife != null)
                _knife.SetActive(kind == CongregationPropKind.Knife);
            if (_drumstick != null)
                _drumstick.SetActive(kind == CongregationPropKind.Drumstick);
        }

        private GameObject Build(GameObject prefab, string name, Vector3 position, Vector3 euler, Vector3 scale)
        {
            if (prefab == null)
                return null;
            Transform socket = ResolveSocket();
            if (socket == null)
                return null;

            GameObject instance = Object.Instantiate(prefab, socket, false);
            instance.name = name;
            foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(collider);
            foreach (Transform child in instance.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = _layer;
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
                renderer.shadowCastingMode = ShadowCastingMode.On;
            Transform transform = instance.transform;
            transform.localPosition = position;
            transform.localRotation = Quaternion.Euler(euler);
            transform.localScale = scale;
            return instance;
        }

        private Transform ResolveSocket()
        {
            if (_socketResolved)
                return _socket;
            _socketResolved = true;

            Transform hand = FindChildRecursive(_rig, HandBoneName);
            if (hand == null)
            {
                if (!_missingHandLogged)
                {
                    _missingHandLogged = true;
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Cult] Congregant rig has no {HandBoneName}; hand props are skipped.");
                }
                return null;
            }

            var socket = new GameObject("CongregantHandSocket") { layer = _layer };
            socket.transform.SetParent(hand, false);
            Vector3 boneScale = hand.lossyScale;
            socket.transform.localScale = new Vector3(Inverse(boneScale.x), Inverse(boneScale.y), Inverse(boneScale.z));
            _socket = socket.transform;
            return _socket;
        }

        private static float Inverse(float value)
        {
            return Mathf.Abs(value) > 1e-6f ? 1f / value : 1f;
        }

        private static Transform FindChildRecursive(Transform root, string name)
        {
            if (root == null)
                return null;
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildRecursive(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
