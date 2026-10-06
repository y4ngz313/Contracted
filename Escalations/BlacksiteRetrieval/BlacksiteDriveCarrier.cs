using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Contracts.ShadowRaid;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed class BlacksiteDriveCarrier
    {
        internal const float QualifyingHeavyStunSeconds = 2.5f;

        private readonly BlacksiteSoldierAI _soldier;

        internal BlacksiteDriveCarrier(BlacksiteSoldierAI soldier)
        {
            _soldier = soldier;
        }

        internal void DropServer(string reason)
        {
            if (_soldier == null || !_soldier.IsServerAuthorityForSquad)
                return;
            if (!BlacksiteHardDriveObjectiveService.TryGetDriveForCarrier(_soldier, out BreachHardDriveItem drive))
                return;
            drive.DropFromSoldierServer(ResolveReachableDropPoint(_soldier.transform.position), reason);
        }

        internal static void ApplyHeldVisual(
            BreachHardDriveItem drive,
            BlacksiteSoldierAI carrier)
        {
            if (drive == null || carrier == null || drive.PhysicsProp == null)
                return;

            PhysicsProp prop = drive.PhysicsProp;
            prop.playerHeldBy = null;
            prop.heldByPlayerOnServer = false;
            prop.isPocketed = false;
            prop.isHeld = false;
            prop.isHeldByEnemy = true;
            prop.grabbable = false;
            prop.hasHitGround = false;
            prop.isInShipRoom = false;
            prop.isInElevator = false;
            prop.isInFactory = !BlacksiteWorld.Outside(carrier);
            prop.parentObject = carrier.ItemCarryHolder;
            prop.EnablePhysics(enable: false);
            prop.EnableItemMeshes(enable: true);
        }

        // #1219 B1: the loose drive's WORLD is not written here. The y < -80 heuristic that used
        // to be is gone: a manifest-owned drive (every drive this reaches) takes its world from the
        // replicated record, which LateUpdateReplica's release arm writes on every peer in the same
        // frame (prop.isInFactory = !record.Outside, the former carrier's world); until then the
        // prop keeps the carried world the replica's Carried arm has been writing.
        internal static void ApplyLooseVisual(
            BreachHardDriveItem drive,
            Vector3 worldPosition)
        {
            if (drive == null || drive.PhysicsProp == null)
                return;

            PhysicsProp prop = drive.PhysicsProp;
            Transform props = StartOfRound.Instance?.propsContainer;
            prop.parentObject = null;
            prop.transform.SetParent(props, worldPositionStays: true);
            prop.transform.position = worldPosition;
            prop.playerHeldBy = null;
            prop.heldByPlayerOnServer = false;
            prop.isPocketed = false;
            prop.isHeld = false;
            prop.isHeldByEnemy = false;
            prop.grabbable = true;
            prop.hasHitGround = false;
            bool inShip = StartOfRound.Instance?.shipInnerRoomBounds != null
                && StartOfRound.Instance.shipInnerRoomBounds.bounds.ClosestPoint(worldPosition) == worldPosition;
            prop.isInShipRoom = inShip;
            prop.isInElevator = inShip;
            prop.fallTime = 0f;
            prop.EnablePhysics(enable: true);
            prop.EnableItemMeshes(enable: true);
            prop.startFallingPosition = props != null
                ? props.InverseTransformPoint(worldPosition)
                : prop.transform.localPosition;
            prop.targetFloorPosition = prop.startFallingPosition;
            prop.DiscardItemFromEnemy();
        }

        internal static void ApplyExtractedVisual(
            BreachHardDriveItem drive,
            Transform extractionAnchor)
        {
            if (drive == null || drive.PhysicsProp == null || extractionAnchor == null)
                return;

            PhysicsProp prop = drive.PhysicsProp;
            prop.playerHeldBy = null;
            prop.heldByPlayerOnServer = false;
            prop.isHeld = false;
            prop.isHeldByEnemy = true;
            prop.grabbable = false;
            prop.parentObject = extractionAnchor;
            prop.EnablePhysics(enable: false);
            prop.EnableItemMeshes(enable: true);
        }

        // Shared with the hosted-weapon death drop (#316), which wants a soldier's corpse to leave
        // its rifle on the same reachable floor the hard drive would have landed on.
        internal static Vector3 ResolveReachableDropPoint(Vector3 origin)
        {
            Vector3[] offsets =
            {
                Vector3.zero,
                Vector3.forward * 1.1f,
                Vector3.right * 1.1f,
                Vector3.left * 1.1f,
                Vector3.back * 1.1f
            };
            for (int i = 0; i < offsets.Length; i++)
            {
                if (BlacksiteLandingGeometry.TryFloorNav(origin + offsets[i],
                    StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0, out var floor, .6f)
                    && Mathf.Abs(floor.y-origin.y)<.6f)
                    return floor + Vector3.up * 0.12f;
            }
            return origin + Vector3.up * 0.12f;
        }
    }
}
