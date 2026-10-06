using UnityEngine;
using Y4NGZCompany.Contracts.Survey;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // The manifest (order 300) owns identity; this component owns only the final carried pose.
    // Run after vanilla GrabbableObject, animation/IK, rope presentation and manifest replication.
    [DefaultExecutionOrder(32000)]
    internal sealed class BlacksiteCarryPresentation : MonoBehaviour
    {
        private GrabbableObject _item;
        private Transform _holder;
        private int _boundFrame = -1;
        private BlacksiteSoldierAI _soldier;
        private bool _releasing, _hasLastPose;
        private Vector3 _lastPosition, _releaseTarget;
        private Quaternion _lastRotation;
        internal static void BeginRelease(GrabbableObject item, Vector3 target)
        {
            var pose = item != null ? item.GetComponent<BlacksiteCarryPresentation>() : null;
            if (pose == null || !pose._hasLastPose || pose._soldier == null || pose._soldier.isEnemyDead || pose._soldier.ReplicatedStunned)
            { Release(item); return; }
            pose._holder = null; pose._boundFrame = -1;
            pose._releaseTarget = target; pose._releasing = true;
            item.transform.SetPositionAndRotation(pose._lastPosition, pose._lastRotation);
            // The former grip owns a short release; the weapon becomes available only afterward.
            float duration = Vector3.Distance(pose._lastPosition, target) / 6f + .04f;
            pose._soldier.BeginCarryRelease(duration);
        }

        internal static bool IsReleasing(GrabbableObject item) => item != null && item.GetComponent<BlacksiteCarryPresentation>()?._releasing == true;

        internal static void Bind(GrabbableObject item, Transform holder)
        {
            if (item == null || holder == null) return;
            var pose = item.GetComponent<BlacksiteCarryPresentation>();
            if (pose == null) pose = item.gameObject.AddComponent<BlacksiteCarryPresentation>();
            pose._item = item; pose._holder = holder; pose._soldier = holder.GetComponentInParent<BlacksiteSoldierAI>(); pose._releasing = false; pose._boundFrame = Time.frameCount;
        }

        internal static void Release(GrabbableObject item)
        {
            var pose = item != null ? item.GetComponent<BlacksiteCarryPresentation>() : null;
            if (pose != null) { pose._boundFrame = -1; pose._holder = null; pose._releasing = false; }
        }

        internal void AdvanceRelease(float deltaSeconds)
        {
            if (_soldier == null || _soldier.isEnemyDead || _soldier.ReplicatedStunned || _item == null || _item.isHeld || _item.playerHeldBy != null || _item.heldByPlayerOnServer || _item.isPocketed)
            { _releasing = false; return; }
            _soldier.BeginCarryRelease(.1f);
            _lastPosition = Vector3.MoveTowards(_lastPosition, _releaseTarget, Mathf.Min(.25f, 6f * deltaSeconds));
            _item.transform.SetPositionAndRotation(_lastPosition, _lastRotation);
            if (Vector3.Distance(_lastPosition, _releaseTarget) < .01f) _releasing = false;
            return;
        }

        private void LateUpdate()
        {
            if (_releasing) { AdvanceRelease(Time.deltaTime); return; }
            if (_boundFrame != Time.frameCount || _item == null || _holder == null
                || !_item.isHeldByEnemy || _item.playerHeldBy != null || _item.heldByPlayerOnServer || _item.isPocketed) return;
            // Holder position is already evaluated on the scaled soldier skeleton. Item offsets
            // are world-size deltas in that solved frame, matching native third-person holding.
            var rotation = _holder.rotation;
            var position = _holder.position;
            if (_soldier != null && _item.GetComponent<SurveyDroneItem>() != null
                && _soldier.TryCarryWrists(out var left, out var right))
                SurveyBeaconCarryPose.Solve(left, right, _soldier.transform.forward, false, out position, out rotation);
            if (_item.itemProperties != null)
            {
                position += rotation * _item.itemProperties.positionOffset;
                rotation *= Quaternion.Euler(_item.itemProperties.rotationOffset);
            }
            _item.transform.SetPositionAndRotation(position, rotation);
            _lastPosition = position; _lastRotation = rotation; _hasLastPose = true;
        }
    }
}
