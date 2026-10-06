// VENDORED from the Procedural Hit Reactions System (IronSamiStudio), asset path
// Assets/IronSamiStudio/ProceduralHitReactionSystem/Scripts/BodyPartReactor.cs.
// Kept as close to verbatim as possible so upstream can be diffed against it. Changes:
//   1. namespace (IronSamiStudio.ProceduralHitReactions -> Y4NGZCompany.Escalations.Bundy.HitReactions)
//   2. CRITICAL: Update() no longer calls animator.SetLayerWeight(controller.reactionLayer, weight).
//      Upstream defaults reactionLayer to 1, and on Bundy's controller layer 1 is the LUNGE
//      OVERRIDE layer — driving its weight from a flinch would silently take over (or wipe)
//      the lower-body lunge pose on every hit. The additive LateUpdate bone offset below IS
//      the whole mechanism; the layer weight was never needed for it.
//   3. The #if UNITY_EDITOR legacy/obsolete field block was dropped (it only existed to keep
//      old serialized inspector data alive; this build has no serialized instances).
//   4. The cached controller/animator/animatorLayer fields (and the Start() lookup + LogError
//      that populated them) are gone — they existed only to feed the removed SetLayerWeight.
//   5. profile-null guards in Update() (upstream assumed an inspector-assigned profile).
//   6. CaptureBaseline() extracted from Start(). Upstream only ever sampled origRot/origPos in
//      Start, which is fine for an inspector-authored prefab but NOT for a reactor built at
//      runtime and reacted to on the same frame: Start has not run yet, so origRot is still the
//      default all-zero quaternion and every offset derived from it comes out NaN. The runtime
//      adapter calls CaptureBaseline() at build time; Start still calls it if nobody did.
using UnityEngine;
using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.Bundy.HitReactions
{

    /// <summary>
    /// The class for FollowObjects, the follow trough system.
    /// </summary>
     [System.Serializable]
    public class FollowObject
    {
        public Transform transform;
        [Range(0f, 1f)] public float rotWeight = 0.2f;
        [Range(0f, 1f)] public float posWeight = 0.1f;
    }



    /// <summary>
    /// Drives one bone’s reaction pose, using data from a ReactionProfile.
    /// </summary>
    public class BodyPartReactor : MonoBehaviour
    {
        /* ─────────────────────────── Inspector ─────────────────────────── */

        [Header("References")]
        public Transform bone;

        [Header("Reaction Profile")]
        public ReactionProfile profile;

        [Header("Follow-Through (optional)")]
        public List<FollowObject> followObjects = new List<FollowObject>();

        [Header("Per-Bone Multipliers")]
        [Tooltip("0.5 = half strength / speed,  2 = double,  0 = mute")]
        [Range(0f, 3f)] public float intensityMultiplier = 1f;

        [Tooltip("Scales reactionDuration from the profile")]
        [Range(0.1f, 3f)] public float durationMultiplier = 1f;

        /* ─────────────────────────── Internals ─────────────────────────── */

        // Upstream cached `controller`, `animator` and `animatorLayer` here purely to feed the
        // SetLayerWeight call that change (2) removes. Nothing else read them, so they are gone.
        float      timer;
        float      weight;
        bool       baselineCaptured;
        Quaternion origRot;
        Vector3    origPos;
        Quaternion targetRot;
        Vector3    targetPos;
        Quaternion deltaRot;
        Vector3    deltaPos;

        /* ─────────────────────────── Runtime ─────────────────────────── */

        void Start()
        {
            if (!baselineCaptured)
                CaptureBaseline();
        }

        /// <summary>
        /// Samples the bone's rest pose. Must run before the first React() — see change (6). In the
        /// additive + RandomOnly configuration the baseline cancels out of deltaRot entirely, so
        /// WHICH animated frame it samples does not matter; that it is a valid quaternion does.
        /// </summary>
        public void CaptureBaseline()
        {
            if (bone == null)
                return;

            origRot = bone.localRotation;
            origPos = bone.localPosition;
            baselineCaptured = true;
        }

        void Update()
        {
            if (profile == null)
                return;

            float intensity = Mathf.Max(0f, intensityMultiplier);

            float blendIn = profile.blendInSpeed * intensity *
                (profile.slowBlendFactor <= 0f ? 1f : profile.slowBlendFactor);


            // blend weight in / out
            if (timer > 0f)
            {
                timer -= Time.deltaTime;
                weight = Mathf.MoveTowards(weight, 1f, blendIn * Time.deltaTime);
            }
            else
            {
                weight = Mathf.MoveTowards(weight, 0f, profile.blendOutSpeed * Time.deltaTime);
            }

            // (upstream drove controller.reactionLayer's weight here — removed, see the header note)
        }

        /* ─────────────────────────── Public API ─────────────────────────── */

        public void React(Vector3 hitDir, Vector3 hitPoint)
        {
            if (bone == null || profile == null) return;
            if (!baselineCaptured) CaptureBaseline();

            float intensity = Mathf.Max(0f, intensityMultiplier);

            // –– figure out direction ––
            Vector3 effDir = profile.useContactPointDirection
                           ? (hitPoint - bone.position)
                           : hitDir;
            if (effDir.sqrMagnitude < 1e-6f) effDir = hitDir;
            effDir.Normalize();

            if (profile.useLocalHitDirection)
                effDir = bone.InverseTransformDirection(effDir);

            if (profile.autoInvertBackHits && Vector3.Dot(effDir, Vector3.forward) < 0f)
                effDir = -effDir;

            // New "Override on hit" method enum, replaced the old "OverrideOnHit" bool.
            if (profile.hitAcceptance == ReactionProfile.HitAcceptance.IgnoreWhileActive &&
              timer > 0f)
              return;

            timer = profile.reactionDuration * durationMultiplier;
            switch (profile.blendResetMode)
            {
              case ReactionProfile.BlendResetMode.HardReset:
                weight = 0f;
                break;

              case ReactionProfile.BlendResetMode.ScaledCarryOver:
                weight = Mathf.Clamp01(weight * profile.carryOverScale);
                break;
            }

            /* ───────────────────── Rotation ───────────────────── */
            if (profile.useRotation)
            {
                // random swing
                Vector3 r = new Vector3(
                    Random.Range(-profile.rotationStrength.x * intensity, profile.rotationStrength.x * intensity),
                    Random.Range(-profile.rotationStrength.y * intensity, profile.rotationStrength.y * intensity),
                    Random.Range(-profile.rotationStrength.z * intensity, profile.rotationStrength.z * intensity)
                );
                Quaternion qRand = Quaternion.Euler(r) * origRot;

                // direction-based swing
                Quaternion forwardQ = Quaternion.LookRotation(Vector3.forward);
                Quaternion qDir = Quaternion.LookRotation(effDir)
                                * Quaternion.Inverse(forwardQ)
                                * origRot;

                // blend
                switch (profile.rotationMode)
                {
                    case ReactionMode.RandomOnly:
                        targetRot = qRand; break;
                    case ReactionMode.DirectionOnly:
                        targetRot = qDir;  break;
                    default:
                        targetRot = Quaternion.Slerp(qRand, qDir, profile.directionRotationMultiplier * intensity);
                        break;
                }

                /* ★ NEW: axis-lock */
                Vector3 eul = targetRot.eulerAngles;
                eul.x *= profile.rotationAxisMask.x;
                eul.y *= profile.rotationAxisMask.y;
                eul.z *= profile.rotationAxisMask.z;
                targetRot = Quaternion.Euler(eul);

                // choose baseline (bind pose or current pose)
                Quaternion fromRot = profile.offsetsFromCurrentPose ? bone.localRotation : origRot;
                deltaRot = targetRot * Quaternion.Inverse(fromRot);
            }

            /* ───────────────────── Translation ───────────────────── */
            if (profile.useTranslation)
            {
                // random offset
                Vector3 offs = new Vector3(
                    Random.Range(-profile.translationStrength.x * intensity, profile.translationStrength.x * intensity),
                    Random.Range(-profile.translationStrength.y * intensity, profile.translationStrength.y * intensity),
                    Random.Range(-profile.translationStrength.z * intensity, profile.translationStrength.z * intensity)
                );
                Vector3 pRand = origPos + offs;

                // direction-based offset
                Vector3 pDir = origPos + effDir * profile.directionTranslationMultiplier * intensity;

                // blend
                switch (profile.translationMode)
                {
                    case ReactionMode.RandomOnly:
                        targetPos = pRand; break;
                    case ReactionMode.DirectionOnly:
                        targetPos = pDir;  break;
                    default:
                        targetPos = Vector3.Lerp(pRand, pDir, profile.directionTranslationMultiplier);
                        break;
                }

                /* ★ NEW: axis-lock */
                Vector3 deltaT = targetPos - origPos;
                deltaT.Scale(profile.translationAxisMask);  // zero locked axes
                targetPos = origPos + deltaT;

                deltaPos = targetPos - origPos;
            }
        }

        void LateUpdate()
        {
            if (bone == null || profile == null) return;
            float w = profile.blendCurve.Evaluate(weight);
            if (w <= 0f) return;

            // –– main bone ––
            if (!profile.additive)
            {
                if (profile.useRotation)
                    bone.localRotation = Quaternion.Slerp(origRot, targetRot, w);
                if (profile.useTranslation)
                    bone.localPosition = Vector3.Lerp(origPos, targetPos, w);
            }
            else
            {
                if (profile.useRotation)
                {
                    Quaternion rOff = Quaternion.Slerp(Quaternion.identity, deltaRot, w);
                    bone.localRotation *= rOff;
                }
                if (profile.useTranslation)
                {
                    Vector3 pOff = deltaPos * w;
                    bone.localPosition += pOff;
                }
            }

           // –– follow-through ––
        if (followObjects != null && followObjects.Count > 0)
        {
            foreach (var fo in followObjects)
            {
                if (fo == null || fo.transform == null) continue;
                var t   = fo.transform;
                var rW  = fo.rotWeight;
                var pW  = fo.posWeight;

                Quaternion baseRot = t.localRotation;
                Vector3    basePos = t.localPosition;
                if (profile.useRotation && rW > 0f)
                {
                    Quaternion offR = Quaternion.Slerp(
                        Quaternion.identity,
                        deltaRot,
                        w * rW);
                        t.localRotation = baseRot * offR;
                }
                if (profile.useTranslation && pW > 0f)
                {
                     Vector3 offP = deltaPos * (w * pW);
                    t.localPosition = basePos + offP;
                    }
                }
            }

        }
    }
}
