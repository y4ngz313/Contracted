using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Contracts.Payload
{
    public partial class PayloadCartController
    {
        /// <summary>Shared buffer for the pusher capsule, mirroring <c>SpawnOverlaps</c>.</summary>
        private static readonly Collider[] PusherOverlaps = new Collider[32];
        private static readonly RaycastHit[] PusherFloorHits = new RaycastHit[8];

        /// <summary>Radius and vertical span of the player volume the gate tests behind the handle.</summary>
        private const float PusherCapsuleRadius = 0.3f;
        private const float PusherCapsuleBottom = 0.35f;
        private const float PusherCapsuleTop = 1.6f;

        /// <summary>
        /// Spawn-time gate (#877) for the floor the player ends up on. Grabbing the cart snaps the
        /// pusher to <see cref="FollowDistance"/> behind the handle (cart local +Z), so a pose whose
        /// body box is clear can still be unusable: the standing spot may be inside a railing, off a
        /// walkway lip, or off the NavMesh entirely, which is the "wedged, hard to grab" half of the
        /// report. Players and enemies are ignored the same way the motion box ignores them.
        /// <paramref name="ignore"/> excludes an already-spawned cart's own colliders.
        /// </summary>
        internal static bool IsPusherStandClear(Vector3 cartPosition, Quaternion rotation, out string blocker, Transform ignore = null)
        {
            blocker = null;
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : Physics.DefaultRaycastLayers;
            Vector3 stand = cartPosition + Quaternion.Euler(0f, rotation.eulerAngles.y, 0f) * Vector3.forward * FollowDistance;

            int count = Physics.OverlapCapsuleNonAlloc(
                stand + Vector3.up * PusherCapsuleBottom,
                stand + Vector3.up * PusherCapsuleTop,
                PusherCapsuleRadius,
                PusherOverlaps,
                mask,
                QueryTriggerInteraction.Ignore);
            if (count == PusherOverlaps.Length) { blocker = "<too many colliders>"; return false; }
            for (int i = 0; i < count; i++)
            {
                Collider hit = PusherOverlaps[i];
                PusherOverlaps[i] = null;
                if (hit == null) continue;
                if (ignore != null && (hit.transform == ignore || hit.transform.IsChildOf(ignore))) continue;
                if (hit.GetComponentInParent<PlayerControllerB>() != null || hit.GetComponentInParent<EnemyAI>() != null) continue;
                if (blocker == null) blocker = hit.name;
            }

            if (blocker != null)
                return false;

            // Floor, not merely absence of obstruction: the strip behind the handle can be the
            // last half metre of a walkway with a pit past it.
            if (!TryFindPusherFloor(stand, mask, ignore))
            {
                blocker = "<no floor under pusher>";
                return false;
            }

            // A spot the player can stand on but cannot be moved to is no better than a wall.
            if (!NavMesh.SamplePosition(stand, out _, 0.5f, NavMesh.AllAreas))
            {
                blocker = "<no navmesh under pusher>";
                return false;
            }

            return true;
        }

        /// <summary>Nearly flat ground within a metre under the standing point, ignoring the cart itself.</summary>
        private static bool TryFindPusherFloor(Vector3 stand, int mask, Transform ignore)
        {
            if (ignore == null)
            {
                return Physics.Raycast(stand + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 1.0f, mask, QueryTriggerInteraction.Ignore)
                       && hit.normal.y >= 0.5f;
            }

            // Re-checking a live cart: its own low colliders can sit under the standing point, and
            // hitting one must not be read as either floor or its absence.
            int count = Physics.RaycastNonAlloc(
                new Ray(stand + Vector3.up * 0.5f, Vector3.down), PusherFloorHits, 1.0f, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit candidate = PusherFloorHits[i];
                if (candidate.collider == null)
                    continue;
                if (candidate.collider.transform == ignore || candidate.collider.transform.IsChildOf(ignore))
                    continue;
                if (candidate.normal.y >= 0.5f)
                    return true;
            }

            return false;
        }
    }
}
