using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Contracts.Survey
{
    /// <summary>Positions the beacon between vanilla HoldForward's animated hands.</summary>
    internal sealed class SurveyBeaconCarryPose
    {
        private PlayerControllerB _holder;
        private SkinnedMeshRenderer _model;
        private Transform _left;
        private Transform _right;

        internal bool Apply(GrabbableObject item)
        {
            var holder = item != null && item.isHeld ? item.playerHeldBy : null;
            if (holder == null || holder.isPlayerDead || holder.inSpecialInteractAnimation || item.parentObject == null)
            {
                _holder = null; _model = null; _left = null; _right = null;
                return false;
            }
            // Vanilla uses different animated skeletons for the local hands and other players.
            bool firstPerson = item.parentObject == holder.localItemHolder;
            var model = firstPerson ? holder.thisPlayerModelArms : holder.thisPlayerModel;
            if (_holder != holder || _model != model)
            {
                _holder = holder; _model = model; _left = null; _right = null;
                if (model != null)
                    foreach (var bone in model.bones)
                    {
                        if (bone == null) continue;
                        if (bone.name == "hand.L") _left = bone;
                        else if (bone.name == "hand.R") _right = bone;
                    }
            }
            if (_left == null || _right == null) return false;
            Solve(_left.position, _right.position, holder.transform.forward, firstPerson, out Vector3 position, out Quaternion rotation);
            // Optional tuning is a delta in the solved beacon frame, not a replacement hand pose.
            position += rotation * item.itemProperties.positionOffset;
            rotation *= Quaternion.Euler(item.itemProperties.rotationOffset);
            item.transform.SetPositionAndRotation(position, rotation);
            return true;
        }

        internal static void Solve(Vector3 left, Vector3 right, Vector3 playerForward, bool firstPerson, out Vector3 position, out Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(playerForward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .001f) forward = Vector3.forward;
            // Item +Z is the screen normal. Local view tips the far edge up for
            // readability; the body view stays level with the palms at its lower edge.
            rotation = Quaternion.LookRotation(Vector3.up, forward);
            if (firstPerson)
                rotation = Quaternion.AngleAxis(-12f, Vector3.Cross(Vector3.up, forward)) * rotation;
            float heightAboveWrists = firstPerson ? .035f : .125f;
            position = (left + right) * .5f + Vector3.up * heightAboveWrists + forward * .02f;
        }
    }
}
