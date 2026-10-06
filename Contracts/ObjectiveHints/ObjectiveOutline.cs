using UnityEngine;

namespace Y4NGZCompany.Contracts.ObjectiveHints
{
    /// <summary>
    /// #1197. Contracted's copy of LethalCCTV's per-renderer <c>Outline</c>. It registers with
    /// <see cref="ObjectiveOutlineEffect"/> only, so a CCTV or squad-ping outline and an objective
    /// hint never draw each other's renderers. Added by <see cref="ObjectiveHintOutlines"/> for one
    /// blink window and destroyed after it.
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    internal sealed class ObjectiveOutline : MonoBehaviour
    {
        private static readonly Material[] NoMaterials = new Material[0];

        private Material[] _sharedMaterials;

        internal Renderer Renderer { get; private set; }
        internal SpriteRenderer SpriteRenderer { get; private set; }
        internal SkinnedMeshRenderer SkinnedMeshRenderer { get; private set; }
        internal MeshFilter MeshFilter { get; private set; }

        /// <summary>Colour slot 0-2 on the effect.</summary>
        internal int color;
        internal bool eraseRenderer;

        internal Material[] SharedMaterials
        {
            get
            {
                if (_sharedMaterials == null && Renderer != null)
                    _sharedMaterials = Renderer.sharedMaterials;

                return _sharedMaterials ?? NoMaterials;
            }
        }

        private void Awake()
        {
            Renderer = GetComponent<Renderer>();
            SkinnedMeshRenderer = GetComponent<SkinnedMeshRenderer>();
            SpriteRenderer = GetComponent<SpriteRenderer>();
            MeshFilter = GetComponent<MeshFilter>();
        }

        private void OnEnable()
        {
            ObjectiveOutlineEffect.RegisterOutline(this);
        }

        private void OnDisable()
        {
            ObjectiveOutlineEffect.UnregisterOutline(this);
        }
    }
}
