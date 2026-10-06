using UnityEngine;

namespace Y4NGZCompany.Escalations._Shared
{
    internal static class EnemyBodyCollision
    {
        // EnemiesNotRendered collides with Player but stays out of the enemy damage/push scans
        // and Blacksite's world probes. Physical presence is separate from a damage receiver.
        private const string LayerName = "EnemiesNotRendered";
        private const int LayerFallback = 23;

        internal static CapsuleCollider Ensure(GameObject root, string name, float radius, float height,
            float centerY, bool kinematic)
        {
            if (root == null)
                return null;

            Transform body = root.transform.Find(name);
            if (body == null)
            {
                body = new GameObject(name).transform;
                body.SetParent(root.transform, false);
            }
            // Root child, never the animated rig: strides must not sweep the blocker into players.
            body.localPosition = Vector3.zero;
            body.localRotation = Quaternion.identity;
            body.localScale = Vector3.one;
            int layer = LayerMask.NameToLayer(LayerName);
            body.gameObject.layer = layer >= 0 ? layer : LayerFallback;
            body.gameObject.tag = "Untagged";

            CapsuleCollider capsule = body.GetComponent<CapsuleCollider>();
            if (capsule == null)
                capsule = body.gameObject.AddComponent<CapsuleCollider>();
            capsule.isTrigger = false;
            capsule.direction = 1;
            capsule.radius = radius;
            capsule.height = height;
            capsule.center = new Vector3(0f, centerY, 0f);

            if (kinematic)
            {
                Rigidbody rigidbody = body.GetComponent<Rigidbody>();
                if (rigidbody == null)
                    rigidbody = body.gameObject.AddComponent<Rigidbody>();
                rigidbody.isKinematic = true;
                rigidbody.useGravity = false;
            }
            return capsule;
        }
    }
}
