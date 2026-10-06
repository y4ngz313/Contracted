using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #315: the numbers behind the soldier's upper-body aim overlay and look IK. The animator
    /// carries an "Aim" override layer masked from the spine up; the runtime blends that layer
    /// in while the soldier is aiming and in a standing locomotion state, aims the head at the
    /// target through Animator look-at, and pitches the spine chain toward the target by a
    /// clamped angle so the torso can follow a target on a catwalk without folding into the
    /// hunch the 2026-09-04 footage showed. Unity-free on purpose so the regression harness can
    /// pin the clamps and the blend directions.
    /// </summary>
    internal static class BlacksiteAimPosePolicy
    {
        internal const string AimLayerName = "Aim";

        // #874 S3.2: the controller's topmost override layer, masked to the arms, shoulders and
        // fingers. Both soldier controllers carry one and both author its default weight at 1, so
        // it is resolved by name rather than by index - it is last on either controller - and -1
        // is tolerated for a bundle without it.
        internal const string CarryLayerName = "Carry";

        // The carry layer's own Any-State transitions are authored at 0.12 s
        // (asset-pipeline/BlacksiteCarryLayerBuild.cs), so its weight rides in and out over the
        // same window and the pose never arrives before the layer that carries it.
        internal const float CarryBlendSeconds = 0.12f;

        // How long the support hand takes to leave the weapon and to come back to it. Unchanged
        // from the value BlacksiteRiflePose has always blended over; named here so the solver, the
        // fixture and the Unity-free regression all read the same number.
        internal const float SupportBlendSeconds = 0.16f;

        // #874 S3.2 R3: what the support wrist is allowed to be, in degrees away from the avatar's
        // bind pose, after the forearm has absorbed what it can. Pronation/supination is the
        // tightest because the forearm owns it. Flexion is the loosest, and its number is measured
        // rather than assumed: the authored rifle clips put up to 39 deg of flexion, 14 deg of
        // deviation and 75 deg of twist on this rig's own support wrist, and seating the palm on the
        // handguard costs 15 to 25 deg of flexion more than the clip's own pose, so 70 deg still
        // leaves a margin under the roughly 80 deg a loaded human wrist reaches. The wrap limit is
        // the free rotation of the hand about the contact normal, the one degree of freedom a palm
        // flat on the handguard keeps, and it is the only lever used to bring flexion and deviation
        // inside their bounds; 45 deg is the thumb-forward angle a support hand takes anyway.
        internal const float SupportTwistLimitDegrees = 25f;
        internal const float SupportFlexLimitDegrees = 70f;
        internal const float SupportDeviationLimitDegrees = 30f;
        internal const float SupportWrapLimitDegrees = 45f;

        // The support elbow's swivel about the shoulder-to-wrist chord, away from the clip's own
        // bend plane. It moves no joint but the elbow, and it is the lever that actually reaches
        // the wrist: measured on the real rig, the grip's contact normal lies close to the
        // forearm's own long axis, so rotating the hand about it is almost pure pronation and the
        // forearm absorption already covers it, while swivelling the elbow rolls the forearm under
        // the fixed hand and is what brings flexion and deviation down. Bounded so the elbow still
        // follows the clip's bend plane rather than replacing it.
        internal const float SupportSwivelLimitDegrees = 35f;

        // The base-layer states the aim overlay may sit on while engaged. Action states (fire,
        // reload, grenade, hit, radio, signal), the crouch family - whose clips already aim - the
        // traversal states and death own the arms outright, and the overlay leaves them alone.
        internal static readonly string[] OverlayStateNames = { "StandingPatrol", "StandingAimed" };

        // #874 Q7: the base-layer states a shot may be released from, and how. The aimed trees and
        // the aimed actions always; RestToAim only at the end of the raise and never while it is
        // blending (normalised time at least RaiseReadyNormalizedTime, not in transition), so the
        // fire decision waits for the pose instead of firing from a half-raised rifle; the patrol
        // tree only under a fully raised aim overlay. Every other state - AimToRest, RestToSlung,
        // SlungToRest, the rest turns and idle, reloads, hits, gestures, death - is refused.
        internal static readonly (string Name, BlacksiteFirePoseKind Kind)[] FirePoseStates =
        {
            ("StandingAimed", BlacksiteFirePoseKind.Aimed),
            ("StandingFire", BlacksiteFirePoseKind.Aimed),
            ("WalkingFire", BlacksiteFirePoseKind.Aimed),
            ("RunningFire", BlacksiteFirePoseKind.Aimed),
            ("CrouchedLocomotion", BlacksiteFirePoseKind.Aimed),
            ("CrouchFire", BlacksiteFirePoseKind.Aimed),
            ("RestToAim", BlacksiteFirePoseKind.Raise),
            ("StandingPatrol", BlacksiteFirePoseKind.PatrolOverlay),
        };

        // The raise is complete from here: 0.9 of the 0.867 s clip, the last 0.075 s at its 1.15 rate.
        internal const float RaiseReadyNormalizedTime = .9f;

        internal static BlacksiteFirePoseKind ClassifyFirePose(string stateName)
        {
            for (int i = 0; i < FirePoseStates.Length; i++)
                if (FirePoseStates[i].Name == stateName) return FirePoseStates[i].Kind;
            return BlacksiteFirePoseKind.Unsupported;
        }

        /// <param name="inTransition">The base layer is blending (this state as source or destination).</param>
        /// <param name="aimOverlayRaised">The Aim override layer exists and is at full weight.</param>
        internal static bool AcceptsFirePose(BlacksiteFirePoseKind kind, float normalizedTime, bool inTransition, bool aimOverlayRaised)
        {
            switch (kind)
            {
                case BlacksiteFirePoseKind.Aimed: return true;
                case BlacksiteFirePoseKind.Raise: return !inTransition && normalizedTime >= RaiseReadyNormalizedTime;
                case BlacksiteFirePoseKind.PatrolOverlay: return aimOverlayRaised;
                default: return false;
            }
        }

        // Pitch is positive upwards. Down is clamped tighter than up: the rifle-aim clips
        // already carry the head low, and the footage hunch came from adding to that.
        internal const float MaxPitchUpDegrees = 30f;
        internal const float MaxPitchDownDegrees = 22f;

        internal const float LayerBlendInSeconds = 0.18f;
        internal const float LayerBlendOutSeconds = 0.28f;
        internal const float PitchSmoothingSeconds = 0.12f;

        // How the pitch is shared down the spine chain (renormalised over the bones the
        // avatar actually maps). Weighting the chest keeps the pelvis still.
        internal const float SpinePitchShare = 0.3f;
        internal const float ChestPitchShare = 0.45f;
        internal const float UpperChestPitchShare = 0.25f;

        // Look-at: head only. Body weight stays at zero because the spine pitch below owns the
        // torso, and the head is counter-rotated after that pitch so the look-at solve holds.
        internal const float LookHeadWeight = 0.8f;
        internal const float LookBodyWeight = 0f;
        internal const float LookEyesWeight = 0f;
        internal const float LookClampWeight = 0.55f;

        internal static float ClampPitch(float pitchDegrees)
        {
            if (float.IsNaN(pitchDegrees))
                return 0f;
            return Math.Max(-MaxPitchDownDegrees, Math.Min(MaxPitchUpDegrees, pitchDegrees));
        }

        /// <summary>
        /// Elevation from the eye to the aim point, clamped. <paramref name="horizontalDistance"/>
        /// is floored so a target standing on the soldier's toes cannot produce a vertical pitch.
        /// </summary>
        internal static float ComputePitchDegrees(float verticalOffset, float horizontalDistance)
        {
            float distance = Math.Max(0.35f, horizontalDistance);
            double radians = Math.Atan2(verticalOffset, distance);
            return ClampPitch((float)(radians * 180.0 / Math.PI));
        }

        /// <summary>Linear blend toward 1 while engaged and toward 0 otherwise.</summary>
        internal static float StepWeight(float current, bool engaged, float deltaSeconds)
        {
            float target = engaged ? 1f : 0f;
            float seconds = engaged ? LayerBlendInSeconds : LayerBlendOutSeconds;
            float step = seconds <= 0f ? 1f : Math.Max(0f, deltaSeconds) / seconds;
            if (current < target)
                return Math.Min(target, current + step);
            return Math.Max(target, current - step);
        }

        /// <summary>
        /// Symmetric blend of the carry layer's weight. Unlike the aim overlay the carry layer is
        /// as urgent leaving as arriving: the arms it overrides have to be handed back to the base
        /// clips on the same schedule they were taken.
        /// </summary>
        internal static float StepCarryWeight(float current, bool carrying, float deltaSeconds)
            => StepSymmetric(current, carrying, CarryBlendSeconds, deltaSeconds);

        private static float StepSymmetric(float current, bool raised, float seconds, float deltaSeconds)
        {
            float target = raised ? 1f : 0f;
            float step = seconds <= 0f ? 1f : Math.Max(0f, deltaSeconds) / seconds;
            if (current < target)
                return Math.Min(target, current + step);
            return Math.Max(target, current - step);
        }

        /// <summary>Exponential approach used for the pitch so a target crossing a catwalk does not snap the torso.</summary>
        internal static float SmoothPitch(float current, float target, float deltaSeconds)
        {
            if (PitchSmoothingSeconds <= 0f || deltaSeconds <= 0f)
                return target;
            float blend = 1f - (float)Math.Exp(-deltaSeconds / PitchSmoothingSeconds);
            return current + (target - current) * blend;
        }
    }

    /// <summary>#874 Q7: how a base-layer state stands toward releasing a shot.</summary>
    internal enum BlacksiteFirePoseKind { Unsupported, Aimed, Raise, PatrolOverlay }
}
