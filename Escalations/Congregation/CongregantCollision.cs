using UnityEngine;
using Y4NGZCompany.Escalations._Shared;

namespace Y4NGZCompany.Escalations.Congregation
{
    internal static class CongregantCollision
    {
        internal const string BodyName = "CongregantBodyCollider";
        internal const float BodyRadius = .437f;
        internal const float BodyHeight = 2.39f;
        internal const float BodyCenterY = 1.31f;
        private const string HitName = "CultistCollision";
        private const float HitRadius = .598f;
        private const float HitHeight = 2.70f;
        private const float HitChildY = 1.34f;

        // Used before prefab registration and once per runtime actor as a fallback. Removing the
        // old root receiver immediately prevents duplicate hits even in the setup frame.
        internal static CapsuleCollider Ensure(CongregantAI ai)
        {
            if (ai == null)
                return null;

            foreach (EnemyAICollisionDetect detect in ai.GetComponents<EnemyAICollisionDetect>())
                UnityEngine.Object.DestroyImmediate(detect, allowDestroyingAssets: true);
            foreach (CapsuleCollider capsule in ai.GetComponents<CapsuleCollider>())
                UnityEngine.Object.DestroyImmediate(capsule, allowDestroyingAssets: true);

            Transform collision = ai.transform.Find(HitName);
            if (collision == null)
            {
                collision = new GameObject(HitName).transform;
                collision.SetParent(ai.transform, false);
            }
            collision.localPosition = new Vector3(0f, HitChildY, 0f);
            collision.localRotation = Quaternion.identity;
            collision.localScale = Vector3.one;
            int layer = LayerMask.NameToLayer("Enemies");
            collision.gameObject.layer = layer >= 0 ? layer : 19;
            try
            {
                collision.gameObject.tag = "Enemy";
            }
            catch (UnityException)
            {
                // The native fixture may not have the game's tag table.
            }

            CapsuleCollider hit = collision.GetComponent<CapsuleCollider>();
            if (hit == null)
                hit = collision.gameObject.AddComponent<CapsuleCollider>();
            hit.enabled = true;
            hit.isTrigger = true;
            hit.direction = 1;
            hit.radius = HitRadius;
            hit.height = HitHeight;
            hit.center = Vector3.zero;

            Rigidbody rigidbody = collision.GetComponent<Rigidbody>();
            if (rigidbody == null)
                rigidbody = collision.gameObject.AddComponent<Rigidbody>();
            rigidbody.isKinematic = true;
            rigidbody.useGravity = false;
            EnemyAICollisionDetect receiver = collision.GetComponent<EnemyAICollisionDetect>();
            if (receiver == null)
                receiver = collision.gameObject.AddComponent<EnemyAICollisionDetect>();
            receiver.mainScript = ai;
            receiver.onlyCollideWhenGrounded = false;

            return EnemyBodyCollision.Ensure(ai.gameObject, BodyName, BodyRadius, BodyHeight,
                BodyCenterY, kinematic: true);
        }
    }
}
