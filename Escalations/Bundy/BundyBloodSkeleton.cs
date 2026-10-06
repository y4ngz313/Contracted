using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy
{
    /// <summary>
    /// Cached, curated blood-placement skeleton for Bundy's generic rig. A contact is matched
    /// against bone-to-child segments instead of joint positions, so a forearm hit stays on the
    /// forearm rather than snapping to whichever endpoint happens to be closer.
    /// </summary>
    internal sealed class BundyBloodSkeleton
    {
        internal readonly struct Match
        {
            internal readonly Transform Anchor;
            internal readonly BundyBloodProfile Profile;
            internal readonly string BoneName;
            internal readonly Vector3 SegmentPoint;

            internal Match(
                Transform anchor,
                in BundyBloodProfile profile,
                string boneName,
                Vector3 segmentPoint)
            {
                Anchor = anchor;
                Profile = profile;
                BoneName = boneName;
                SegmentPoint = segmentPoint;
            }
        }

        private readonly struct Definition
        {
            internal readonly string BoneName;
            internal readonly string SegmentEndName;
            internal readonly BundyBloodProfile Profile;

            internal Definition(
                string boneName,
                string segmentEndName,
                in BundyBloodProfile profile)
            {
                BoneName = boneName;
                SegmentEndName = segmentEndName;
                Profile = profile;
            }
        }

        // The approved plan calls this a 19-bone table, while its explicit left/right list contains
        // 20 names. The list is authoritative: omitting either side would make located limb blood
        // asymmetric, so every named entry is represented here.
        private static readonly Definition[] Definitions =
        {
            Define("head", null, 0.30f, 0.26f, 0.34f, 0.64f),
            Define("neck_01", "head", 0.28f, 0.22f, 0.30f, 0.56f),
            Define("spine_01", "spine_02", 0.50f, 0.42f, 0.58f, 1.00f),
            Define("spine_02", "spine_03", 0.52f, 0.44f, 0.60f, 1.04f),
            Define("spine_03", "neck_01", 0.55f, 0.46f, 0.62f, 1.10f),
            Define("pelvis", "spine_01", 0.50f, 0.42f, 0.58f, 1.00f),
            Define("clavicle_l", "upperarm_l", 0.34f, 0.24f, 0.34f, 0.68f),
            Define("clavicle_r", "upperarm_r", 0.34f, 0.24f, 0.34f, 0.68f),
            Define("upperarm_l", "lowerarm_l", 0.34f, 0.25f, 0.36f, 0.68f),
            Define("upperarm_r", "lowerarm_r", 0.34f, 0.25f, 0.36f, 0.68f),
            Define("lowerarm_l", "hand_l", 0.30f, 0.22f, 0.30f, 0.60f),
            Define("lowerarm_r", "hand_r", 0.30f, 0.22f, 0.30f, 0.60f),
            Define("hand_l", null, 0.22f, 0.18f, 0.24f, 0.44f),
            Define("hand_r", null, 0.22f, 0.18f, 0.24f, 0.44f),
            Define("thigh_l", "calf_l", 0.42f, 0.32f, 0.44f, 0.84f),
            Define("thigh_r", "calf_r", 0.42f, 0.32f, 0.44f, 0.84f),
            Define("calf_l", "foot_l", 0.34f, 0.25f, 0.34f, 0.68f),
            Define("calf_r", "foot_r", 0.34f, 0.25f, 0.34f, 0.68f),
            Define("foot_l", null, 0.28f, 0.22f, 0.30f, 0.56f),
            Define("foot_r", null, 0.28f, 0.22f, 0.30f, 0.56f)
        };

        private Animator _animator;
        private Transform _armature;
        private Transform[] _bones;
        private Transform[] _segmentEnds;
        private int _resolvedCount;

        internal int ResolvedCount => _resolvedCount;

        internal bool TryResolve(Animator animator, Vector3 contactPoint, out Match match)
        {
            match = default;
            if (!EnsureResolved(animator))
                return false;

            int bestIndex = -1;
            float bestDistanceSquared = float.PositiveInfinity;
            Vector3 bestSegmentPoint = Vector3.zero;
            bool bestIsPoint = false;

            for (int i = 0; i < _bones.Length; i++)
            {
                Transform bone = _bones[i];
                if (bone == null)
                    continue;

                Transform segmentEnd = _segmentEnds[i];
                bool candidateIsPoint = segmentEnd == null;
                Vector3 candidatePoint = candidateIsPoint
                    ? bone.position
                    : ClosestPointOnSegment(contactPoint, bone.position, segmentEnd.position);
                float distanceSquared = (contactPoint - candidatePoint).sqrMagnitude;

                bool strictlyCloser = distanceSquared < bestDistanceSquared - 0.000001f;
                bool pointWinsTie =
                    Mathf.Abs(distanceSquared - bestDistanceSquared) <= 0.000001f &&
                    candidateIsPoint &&
                    !bestIsPoint;
                if (!strictlyCloser && !pointWinsTie)
                    continue;

                bestIndex = i;
                bestDistanceSquared = distanceSquared;
                bestSegmentPoint = candidatePoint;
                bestIsPoint = candidateIsPoint;
            }

            if (bestIndex < 0)
                return false;

            Definition definition = Definitions[bestIndex];
            match = new Match(
                _bones[bestIndex],
                in definition.Profile,
                definition.BoneName,
                bestSegmentPoint);
            return true;
        }

        /// <summary>Resolve one curated bone explicitly for authored finisher marks.</summary>
        internal bool TryResolveNamed(Animator animator, string boneName, out Match match)
        {
            match = default;
            if (string.IsNullOrEmpty(boneName) || !EnsureResolved(animator))
                return false;

            for (int i = 0; i < Definitions.Length; i++)
            {
                Definition definition = Definitions[i];
                if (definition.BoneName != boneName)
                    continue;

                Transform bone = _bones[i];
                if (bone == null)
                    return false;

                match = new Match(
                    bone,
                    in definition.Profile,
                    definition.BoneName,
                    bone.position);
                return true;
            }

            return false;
        }

        internal void Invalidate()
        {
            _animator = null;
            _armature = null;
            _bones = null;
            _segmentEnds = null;
            _resolvedCount = 0;
        }

        private bool EnsureResolved(Animator animator)
        {
            if (animator == null)
                return false;

            if (_animator == animator && _armature != null && _bones != null)
                return _resolvedCount > 0;

            Invalidate();
            Transform armature = FindChildRecursive(animator.transform, "Armature");
            if (armature == null)
                return false;

            _animator = animator;
            _armature = armature;
            _bones = new Transform[Definitions.Length];
            _segmentEnds = new Transform[Definitions.Length];

            for (int i = 0; i < Definitions.Length; i++)
            {
                Definition definition = Definitions[i];
                Transform bone = FindChildRecursive(armature, definition.BoneName);
                _bones[i] = bone;
                if (bone != null)
                    _resolvedCount++;

                if (!string.IsNullOrEmpty(definition.SegmentEndName))
                    _segmentEnds[i] = FindChildRecursive(armature, definition.SegmentEndName);
            }

            return _resolvedCount > 0;
        }

        private static Definition Define(
            string boneName,
            string segmentEndName,
            float radius,
            float minWidth,
            float maxWidth,
            float depth)
        {
            var profile = new BundyBloodProfile(
                radius,
                minWidth,
                maxWidth,
                depth,
                0.04f,
                1.15f);
            return new Definition(boneName, segmentEndName, in profile);
        }

        private static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector3 segment = end - start;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared <= 0.000001f)
                return start;

            float t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / lengthSquared);
            return start + segment * t;
        }

        private static Transform FindChildRecursive(Transform parent, string childName)
        {
            if (parent == null)
                return null;

            if (parent.name == childName)
                return parent;

            foreach (Transform child in parent)
            {
                Transform nested = FindChildRecursive(child, childName);
                if (nested != null)
                    return nested;
            }

            return null;
        }
    }
}
