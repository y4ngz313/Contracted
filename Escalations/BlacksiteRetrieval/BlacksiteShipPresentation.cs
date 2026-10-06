using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteShipPresentation
    {
        internal static void FaceFlame(Transform flame, Vector3 viewer, Vector3 fallbackUp)
        {
            // Vanilla facePlayerOnAxis uses this authored axis, not world-up.
            var axis = flame.parent.Find("flameAxis");
            flame.LookAt(viewer, axis != null ? axis.up : fallbackUp);
        }
        internal static BoxCollider CreateControlVolume(Transform target, Vector3 size)
        {
            var go = new GameObject("BlacksiteDoorControl");
            go.transform.SetParent(target, false);
            int layer = LayerMask.NameToLayer("InteractableObject");
            go.layer = layer < 0 ? 9 : layer;
            go.tag = "InteractTrigger";
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = size;
            return box;
        }
    }
}
