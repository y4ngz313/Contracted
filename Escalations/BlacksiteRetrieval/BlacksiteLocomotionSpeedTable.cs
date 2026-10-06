using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #874 Q7 (E3): the one locomotion speed table. The soldier's role speeds, the reference speed
    /// every standing and crouched blend-tree child is calibrated against, and the measured ground
    /// speed of each child, so the animator clock, the blend normalisation and the controller's
    /// authored child rates are read from the same numbers.
    ///
    /// The contract, end to end: the body moves at <c>v</c>; the blend is <c>v / role</c> in the
    /// body's own frame (<see cref="Blend"/>), the same for the rest and the aimed tree; a child
    /// authored at blend point <c>p</c> with measured ground speed <c>g</c> carries the rate
    /// <c>Reference * |p| / g</c> (<see cref="CalibratedRate"/>); the animator clock on a locomotion
    /// state is <c>role / Reference</c> (<see cref="AnimatorSpeed"/>). The child's feet then travel at
    /// <c>role * |p|</c>, which is <c>|v|</c> exactly when the blend landed on it, in either tree and at
    /// either role speed, so a stance change never re-normalises the legs.
    ///
    /// Unity-free so the headless suite pins the rates against the controller calibration record
    /// (<c>q7-isolated-rebuild/q7-review/controller-calibration.txt</c>, 2026-09-25).
    /// </summary>
    internal static class BlacksiteLocomotionSpeedTable
    {
        /// <summary>The role speed of an ordinary move, and the one the blend is normalised by.</summary>
        internal const float NormalRoleSpeed = 3.6f;

        /// <summary>The role speed while a move is urgent (objective combat, withdrawal).</summary>
        internal const float UrgentRoleSpeed = 4.8f;

        /// <summary>
        /// #874 S5: the patrol stroll. The StandingPatrol tree's forward walk child sits at (0, .55)
        /// (<see cref="Children"/>[0]), so a stroll at this speed over the 3.6 role blends to that child alone (1.98 m/s).
        /// </summary>
        internal const float StrollSpeed = NormalRoleSpeed * .55f;

        /// <summary>
        /// The speed the controller's locomotion children are calibrated to: at an animator speed of
        /// 1 every child's feet travel at <c>ReferenceSpeed * |p|</c>.
        /// </summary>
        internal const float ReferenceSpeed = 3f;

        // Below this normalised planar speed the controller treats the member as standing: the rest
        // turns are authored on `Speed < .05`, and the gestures use the same line.
        internal const float StationaryBlend = .05f;

        // A role speed of zero would divide the blend by nothing.
        private const float MinimumRoleSpeed = .1f;

        internal static float RoleSpeed(bool urgent) => urgent ? UrgentRoleSpeed : NormalRoleSpeed;

        /// <summary>The animator clock on a calibrated locomotion state.</summary>
        internal static float AnimatorSpeed(float roleSpeed) => Math.Max(MinimumRoleSpeed, roleSpeed) / ReferenceSpeed;

        /// <summary>
        /// The baton controller's locomotion trees are not rate-calibrated: they play their authored
        /// clips at the ordinary role speed, so that is their reference.
        /// </summary>
        internal static float BatonAnimatorSpeed(float roleSpeed) => Math.Max(MinimumRoleSpeed, roleSpeed) / NormalRoleSpeed;

        /// <summary>
        /// The one normalisation. <paramref name="localX"/> and <paramref name="localZ"/> are the
        /// body's planar velocity in its own frame (m/s); the outputs are MoveX, MoveZ and Speed.
        /// </summary>
        internal static void Blend(float localX, float localZ, float roleSpeed, out float moveX, out float moveZ, out float speed)
        {
            float role = Math.Max(MinimumRoleSpeed, roleSpeed);
            moveX = Clamp(localX / role, -1f, 1f);
            moveZ = Clamp(localZ / role, -1f, 1f);
            speed = Clamp((float)Math.Sqrt(localX * localX + localZ * localZ) / role, 0f, 1f);
        }

        /// <summary>The rate a child at (<paramref name="x"/>, <paramref name="z"/>) is authored with.</summary>
        internal static float CalibratedRate(float groundSpeed, float x, float z)
            => groundSpeed <= 0f ? 0f : ReferenceSpeed * (float)Math.Sqrt(x * x + z * z) / groundSpeed;

        /// <summary>How fast a child's feet travel at this role speed, in m/s.</summary>
        internal static float StrideSpeed(in BlacksiteLocomotionChild child, float roleSpeed)
            => CalibratedRate(child.GroundSpeed, child.X, child.Z) * AnimatorSpeed(roleSpeed) * child.GroundSpeed;

        /// <summary>
        /// Every calibrated child, keyed on its tree, clip and blend point, with the ground speed the
        /// calibration measured for its clip. Mirrored children share their clip's speed.
        /// </summary>
        internal static readonly BlacksiteLocomotionChild[] Children =
        {
            new BlacksiteLocomotionChild("StandingPatrol", "RestWalkForward", 0f, .55f, 1.949f),
            new BlacksiteLocomotionChild("StandingPatrol", "RestRunForward", 0f, 1f, 6.486f),
            new BlacksiteLocomotionChild("StandingPatrol", "RestWalkBackward", 0f, -.58f, 1.618f),
            new BlacksiteLocomotionChild("StandingPatrol", "RestWalkBackward", 0f, -1f, 1.618f),
            new BlacksiteLocomotionChild("StandingAimed", "RunForward", 0f, 1f, 7.507f),
            new BlacksiteLocomotionChild("StandingAimed", "WalkForwardLeft", -.41f, .41f, 2.898f),
            new BlacksiteLocomotionChild("StandingAimed", "WalkForwardRight", .41f, .41f, 3.000f),
            new BlacksiteLocomotionChild("StandingAimed", "AimStrafeLeft", -.58f, 0f, 2.373f),
            new BlacksiteLocomotionChild("StandingAimed", "AimStrafeLeft", .58f, 0f, 2.373f),
            new BlacksiteLocomotionChild("StandingAimed", "WalkBackward", 0f, -.58f, 1.519f),
            new BlacksiteLocomotionChild("StandingAimed", "AimStrafeLeft", -1f, 0f, 2.373f),
            new BlacksiteLocomotionChild("StandingAimed", "AimStrafeLeft", 1f, 0f, 2.373f),
            new BlacksiteLocomotionChild("StandingAimed", "WalkBackward", 0f, -1f, 1.519f),
            new BlacksiteLocomotionChild("StandingAimed", "WalkForwardLeft", -.7f, .7f, 2.898f),
            new BlacksiteLocomotionChild("StandingAimed", "WalkForwardRight", .7f, .7f, 3.000f),
            new BlacksiteLocomotionChild("CrouchedLocomotion", "CrouchWalkForward", 0f, .55f, 1.752f),
            new BlacksiteLocomotionChild("CrouchedLocomotion", "CrouchWalkForward", 0f, 1f, 1.752f),
            new BlacksiteLocomotionChild("CrouchedLocomotion", "CrouchWalkRight", .55f, 0f, 3.357f),
            new BlacksiteLocomotionChild("CrouchedLocomotion", "CrouchWalkRight", -.55f, 0f, 3.357f),
            new BlacksiteLocomotionChild("CrouchedLocomotion", "CrouchWalkBackward", 0f, -.55f, 3.330f),
        };

        // #1219 E4: alternatives, not claims that every controller carries every child.
        // Walk indices refer to Children above; runtime and stage use those same names and points.
        internal static readonly BlacksiteStrollProfile[] StrollProfiles =
        {
            new BlacksiteStrollProfile("RestWalkForward", 0, 0, "RestRunForward"),
            new BlacksiteStrollProfile("RestWalkForwardLegacyRun", 0, 0, "RunForward"),
        };

        internal const float RunBlendZ = 1f;
        internal const float MaxStrollRunWeight = .15f;
        internal const float StrollRunSelectWeight = .10f;
        internal const float MinStrollWalkWeight = .5f;
        internal const float MembershipWeight = .9f;
        internal const float StrollBlendRadius = StrollSpeed / NormalRoleSpeed;

        /// <summary>Prove membership in StandingPatrol, then measure the straight-ahead stroll on each walk child.</summary>
        internal static BlacksiteStrollCapability SelectStroll(IBlacksiteBlendSampler sampler)
        {
            for (int i = 0; i < StrollProfiles.Length; i++)
            {
                BlacksiteStrollProfile profile = StrollProfiles[i];
                sampler.Sample(0f, RunBlendZ);
                if (sampler.Weight(profile.RunClip) < MembershipWeight) continue;
                if (!TryStroll(sampler, profile.WalkClip, profile.WalkX, profile.WalkZ, profile.RunClip,
                    out float walkWeight, out float runWeight)) continue;
                float otherWalk = walkWeight, otherRun = runWeight;
                if (profile.OtherWalkClip != profile.WalkClip
                    && !TryStroll(sampler, profile.OtherWalkClip, profile.OtherWalkX, profile.OtherWalkZ, profile.RunClip,
                        out otherWalk, out otherRun)) continue;
                return new BlacksiteStrollCapability(i, Math.Min(walkWeight, otherWalk), Math.Max(runWeight, otherRun));
            }
            return BlacksiteStrollCapability.None;
        }

        private static bool TryStroll(IBlacksiteBlendSampler sampler, string walk, float x, float z, string run,
            out float walkWeight, out float runWeight)
        {
            walkWeight = runWeight = 0f;
            sampler.Sample(x, z);
            if (sampler.Weight(walk) < MembershipWeight) return false;
            sampler.Sample(0f, StrollBlendRadius);
            walkWeight = sampler.Weight(walk); runWeight = sampler.Weight(run);
            if (runWeight > StrollRunSelectWeight || walkWeight < MinStrollWalkWeight) return false;
            // Starting and stopping must also avoid the run; a single full-speed sample is not enough.
            for (int radial = 1; radial < 10; radial++)
            {
                sampler.Sample(0f, StrollBlendRadius * radial / 10f);
                if (sampler.Weight(run) > StrollRunSelectWeight) return false;
            }
            return true;
        }

        /// <summary>
        /// Map an already-damped non-aiming velocity onto the stroll axis. #1283 S3a: the heading is
        /// REPLACED, not shaped: the body does not face its path at walk starts and turns, and #1283 S3b's
        /// StandingPatrol carries only its centre-axis rest children. The planar radius is kept, scaled by
        /// clamp(2cos, -1, 1) of the body-frame heading (full within 60 deg of facing or its reverse,
        /// zero at 90 deg, so a sideways drift never flips fore/aft), and written straight fore/aft.
        /// Damping AFTER this map would interpolate through the run child.
        /// </summary>
        internal static void StrollBlend(in BlacksiteStrollCapability stroll, ref float moveX, ref float moveZ)
        {
            if (!stroll.IsValid) return;
            double radius = Math.Sqrt(moveX * moveX + moveZ * moveZ);
            // #1283 S3a SF2: a continuous fore/aft factor instead of a sign flip at 90 deg.
            double along = radius < 1e-4d ? 1d : Math.Max(-1d, Math.Min(1d, 2d * moveZ / radius));
            moveX = 0f;
            moveZ = (float)(radius * along);
        }

        private static float Clamp(float value, float min, float max)
            => float.IsNaN(value) ? 0f : Math.Max(min, Math.Min(max, value));
    }

    /// <summary>Alternative StandingPatrol membership, using the calibration table's walk children.</summary>
    internal readonly struct BlacksiteStrollProfile
    {
        internal readonly string Name, WalkClip, OtherWalkClip, RunClip;
        internal readonly float WalkX, WalkZ, OtherWalkX, OtherWalkZ;

        internal BlacksiteStrollProfile(string name, int walk, int otherWalk, string runClip)
        {
            Name = name; RunClip = runClip;
            BlacksiteLocomotionChild primary = BlacksiteLocomotionSpeedTable.Children[walk];
            BlacksiteLocomotionChild other = BlacksiteLocomotionSpeedTable.Children[otherWalk];
            WalkClip = primary.Clip; WalkX = primary.X; WalkZ = primary.Z;
            OtherWalkClip = other.Clip; OtherWalkX = other.X; OtherWalkZ = other.Z;
        }
    }

    /// <summary>The selected stroll row and the weights its straight-ahead stroll measured.</summary>
    internal readonly struct BlacksiteStrollCapability
    {
        internal static readonly BlacksiteStrollCapability None = new BlacksiteStrollCapability(-1, 0f, 0f);
        internal readonly int Profile;
        internal readonly float WalkWeight, RunWeight;

        internal BlacksiteStrollCapability(int profile, float walkWeight, float runWeight)
        { Profile = profile; WalkWeight = walkWeight; RunWeight = runWeight; }

        internal bool IsValid => Profile >= 0;
        internal string Name => IsValid ? BlacksiteLocomotionSpeedTable.StrollProfiles[Profile].Name : "none";
    }

    internal interface IBlacksiteBlendSampler
    {
        void Sample(float moveX, float moveZ);
        float Weight(string clip);
    }

    /// <summary>One calibrated blend-tree child: tree state, clip, blend point, measured ground speed (m/s).</summary>
    internal readonly struct BlacksiteLocomotionChild
    {
        internal readonly string Tree;
        internal readonly string Clip;
        internal readonly float X;
        internal readonly float Z;
        internal readonly float GroundSpeed;

        internal BlacksiteLocomotionChild(string tree, string clip, float x, float z, float groundSpeed)
        { Tree = tree; Clip = clip; X = x; Z = z; GroundSpeed = groundSpeed; }
    }
}
