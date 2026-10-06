// VENDORED from the Procedural Hit Reactions System (IronSamiStudio), asset path
// Assets/IronSamiStudio/ProceduralHitReactionSystem/Scripts/ReactionProfiles.cs.
// Kept as close to verbatim as possible so upstream can be diffed against it. The ONLY
// change is the namespace (IronSamiStudio.ProceduralHitReactions ->
// Y4NGZCompany.Escalations.Bundy.HitReactions). ReactionProfile stays a ScriptableObject;
// the mod builds instances with ScriptableObject.CreateInstance at runtime, so no asset
// bundle carries one.
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.HitReactions
{
    public enum ReactionMode
    {
        RandomOnly,
        DirectionOnly,
        RandomAndDirection
    }

    [CreateAssetMenu(menuName = "HitReactions/ReactionProfile")]
    public class ReactionProfile : ScriptableObject
    {
        /* ────────────── supporting enums ────────────── */
        public enum BlendResetMode { HardReset, ScaledCarryOver }
        public enum HitAcceptance  { IgnoreWhileActive, RestartTimer }

        /* ─────────────────── Timing ─────────────────── */
        [Header("Timing")]
        [Tooltip("How long the reaction pose is held (seconds).")]
        public float reactionDuration = 0.3f;

        [Tooltip("Blend-in speed (units/second).")]
        public float blendInSpeed = 5f;

        [Tooltip("Blend-out speed (units/second).")]
        public float blendOutSpeed = 5f;

        /* ──────────────── Blend Curve ──────────────── */
        [Header("Blend Curve")]
        public AnimationCurve blendCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        /* ───────────────── Rotation ───────────────── */
        [Header("Rotation")]
        public bool   useRotation  = true;
        public Vector3 rotationStrength = new Vector3(10, 10, 5);
        public ReactionMode rotationMode = ReactionMode.RandomAndDirection;

        [Tooltip("0 = only random, 1 = only direction-based.")]
        public float directionRotationMultiplier = 1f;

        /* ──────────────── Translation ──────────────── */
        [Header("Translation")]
        public bool   useTranslation = false;
        public Vector3 translationStrength = new Vector3(0.1f, 0.1f, 0.1f);
        public ReactionMode translationMode = ReactionMode.RandomOnly;

        [Tooltip("Units translated along hit direction when TranslationMode uses direction.")]
        public float directionTranslationMultiplier = 0.1f;

        /* ──────────── Hit-Direction Source ─────────── */
        [Header("Hit-Direction Source")]
        [Tooltip("True: (hitPoint − bone).normalized  |  False: incoming projectile vector.")]
        public bool useContactPointDirection = false;

        /* ─────────────── Hit Behaviour ─────────────── */
        [Header("Hit Behaviour")]
        [Tooltip("Convert hit direction into the local space of the bone.")]
        public bool useLocalHitDirection = true;

        [Tooltip("Automatically invert direction for hits coming from behind.")]
        public bool autoInvertBackHits = true;

        /* ─────────────── Blend Behaviour ────────────── */
        [Header("Blend Behaviour")]
        [Tooltip("If true, layer the reaction additively on top of the current pose.")]
        public bool additive = true;

        public BlendResetMode blendResetMode = BlendResetMode.HardReset;

        [Tooltip("When using ScaledCarryOver, current weight = weight × carryOverScale.")]
        [Range(0f, 1f)] public float carryOverScale = 0.3f;

        [Tooltip("Factor applied to Blend-In speed for a slow-blend effect (0 = off).")]
        [Range(0f, 1f)] public float slowBlendFactor = 0f;   // 0 = disabled

        [Tooltip("Treat the bone’s *current* pose as the zero-point for offsets.")]
        public bool offsetsFromCurrentPose = false;

        /* ─────────────── Hit Acceptance ─────────────── */
        [Header("Hit Acceptance")]
        public HitAcceptance hitAcceptance = HitAcceptance.IgnoreWhileActive;

        /* ──────────────── Axis Locks ──────────────── */
        [Header("Axis Locks (1 = allowed, 0 = blocked)")]
        [Tooltip("Rotation axis mask; 1 enables, 0 locks.")]
        public Vector3 rotationAxisMask = Vector3.one;      // X,Y,Z ∈ [0,1]

        [Tooltip("Translation axis mask; 1 enables, 0 locks.")]
        public Vector3 translationAxisMask = Vector3.one;   // X,Y,Z ∈ [0,1]
    }
}
