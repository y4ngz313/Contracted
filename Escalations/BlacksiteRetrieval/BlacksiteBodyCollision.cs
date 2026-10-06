using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S2.2: marks the solid torso capsule so a query can recognise a soldier body without a
    // name compare. It carries no EnemyAICollisionDetect and no IHittable on purpose.
    [DisallowMultipleComponent]
    internal sealed class BlacksiteBodyCollider : MonoBehaviour
    {
        internal CapsuleCollider Capsule { get; private set; }

        internal void Bind(CapsuleCollider capsule)
        {
            Capsule = capsule;
        }
    }

    // #874 S2.2: the soldier's physical presence to a player. Before this the only capsule was a
    // trigger on Enemies with pushPlayerForce/Distance at zero, so a player walked through a
    // standing soldier. This is separation only: damage stays on the authored trigger hit capsule,
    // and it is not a navigation obstacle - NavMeshAgents ignore physics colliders and two static
    // colliders never push each other, so a six-member column still fits through a doorway.
    internal static class BlacksiteBodyCollision
    {
        internal const string ObjectName = "BlacksiteBodyCollider";
        internal const float Radius = .38f;
        internal const float Height = 1.8f;
        internal const float CenterY = 1f;

        internal static void AttachMarker(CapsuleCollider capsule)
        {
            if (capsule == null)
                return;

            BlacksiteBodyCollider marker = capsule.GetComponent<BlacksiteBodyCollider>();
            if (marker == null)
                marker = capsule.gameObject.AddComponent<BlacksiteBodyCollider>();
            marker.Bind(capsule);
        }

        internal static CapsuleCollider Find(GameObject root)
        {
            if (root == null)
                return null;
            BlacksiteBodyCollider marker = root.GetComponentInChildren<BlacksiteBodyCollider>(true);
            return marker != null ? marker.Capsule : null;
        }

        internal static bool IsBody(Collider collider)
        {
            return collider != null && collider.GetComponent<BlacksiteBodyCollider>() != null;
        }
    }
}
