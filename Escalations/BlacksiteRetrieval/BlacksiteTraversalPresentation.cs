using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S5.2 (#873): what a traversing member is actually doing, replicated.
    //
    // ClimbingPoseFlag is one bit and it is set for three unrelated motions: the ship rope
    // (BlacksiteShipCrossingKind.Rope*), the ship ladder (BlacksiteShipCrossingKind.LadderUp) and the
    // world ladder (BlacksiteNavigator.IsWorldClimbing). A presentation writer holding only that
    // bit cannot tell a rung from a rope, which is why the baton writer asked _ropeDirection - a
    // value that is 0 whenever the member is not on the cable at all - and got the rope-ascend clip
    // for every ladder in the game. This enum is the kind that bit was standing in for.
    internal enum BlacksiteTraversalKind : byte
    {
        None = 0,
        RopeDescend = 1,
        RopeAscend = 2,
        LadderUp = 3,
        LadderDown = 4,
        // #991: the off-mesh link crossing. Two kinds rather than one boolean beside the kind,
        // because the two supplied clips are two states and the selection must survive replication
        // as a single byte the way every kind above it does.
        Jump = 6,
        JumpRunning = 7,
    }

    internal enum BlacksiteLadderPhase : byte
    {
        None,
        Approaching,
        Mounting,
        Climbing,
        Dismounting,
    }

    /// <summary>
    /// #874 F07: a resolved ladder pose AND whether the bound controller can actually play the
    /// climb the member is doing.
    ///
    /// The substitute for a controller with no ladder clip is the member's own locomotion, and
    /// returning it as a bare string made the absence indistinguishable from a climb: every caller
    /// got back <c>"StandingPatrol"</c> and had to re-derive, from a second probe of its own,
    /// whether that was the correct rest pose of a member walking to a ladder foot or a MISSING
    /// climb clip being presented as a completed climb. BlacksiteSoldierAI.DriveRifleLadderState
    /// re-derived it (<c>state != Fallback</c>) and UpdateAnimation re-probed the controller for
    /// the same fact a third time; the baton writer re-derived nothing at all and simply played
    /// the substitute.
    ///
    /// The two facts travel together from here on. <see cref="ClimbAuthored"/> is the answer to
    /// "does this controller carry the rise state for this traversal", so a false there is a
    /// missing AUTHORED STATE and is reported as one - it is never a completed climb. It says
    /// nothing about the pose being wrong: an approach is an ordinary walk and is authored by
    /// every controller, which is what <see cref="PresentsLadder"/> separates.
    /// </summary>
    internal readonly struct BlacksiteLadderPose
    {
        /// <summary>The state to play. Never null; never a rope state.</summary>
        internal readonly string State;

        /// <summary>
        /// The bound controller authors the rise state this traversal needs. False means the climb
        /// itself has no clip and <see cref="State"/> is the locomotion substitute.
        /// </summary>
        internal readonly bool ClimbAuthored;

        /// <summary>This pose IS a ladder state (a mount, a rise or a top-out), not locomotion.</summary>
        internal readonly bool PresentsLadder;

        internal BlacksiteLadderPose(string state, bool climbAuthored, bool presentsLadder)
        { State = state; ClimbAuthored = climbAuthored; PresentsLadder = presentsLadder; }
    }

    // The state names the two shipped controllers are asked for, and the two derived rates the
    // fallback runs at. No Unity type is touched beyond Mathf, so a fixture can drive this directly.
    internal static class BlacksiteTraversalPresentation
    {
        // Cable: both directions play BatonRope (the supplied Rope Climb FBX) at the crossing's
        // own rate (CrossingAnimatorSpeed); _ropeDirection only aligns the hands.
        internal const string BatonRope = "BatonRope";
        // #991: the two jump states, baked from Lawson's supplied Unarmed Jump and Unarmed Jump
        // Running FBX by asset-pipeline/BlacksiteEquipmentBuild.cs. Plain layer-0 states with no
        // transitions and no parameters, picked by name like the ladder states, and deliberately
        // absent from BlacksiteAimPosePolicy.OverlayStateNames: a soldier in the air is not aiming.
        internal const string BatonJump = "BatonJump";
        internal const string BatonJumpRunning = "BatonJumpRunning";
        // Ladder, rifle controller (lgucontracts.bundle), authored by asset-pipeline/BlacksiteVanillaMotionBake.cs and
        // played by both loadouts (#1283 S3b: a baton member borrows the rifle pair); stale bundles retain the fallback.
        internal const string RifleLadderMount = "LadderMount";
        internal const float LadderClimbRate = 1f;
        internal const string RifleLadderClimb = "LadderClimb";
        internal const string RifleLadderDescend = "LadderDescend";
        // #1283 S4: the step over the ladder top (Dismounting). One name for both loadouts; a
        // controller without it holds the rise state at ClimbRate 0 instead.
        internal const string RifleLadderTopOut = "LadderTopOut";
        // The one state both shipped controllers do have. It is the substitute, never a rope.
        internal const string Fallback = "StandingPatrol";

        internal static bool IsRope(BlacksiteTraversalKind kind)
            => kind == BlacksiteTraversalKind.RopeDescend || kind == BlacksiteTraversalKind.RopeAscend;

        // #874 S5 (F07): the stand-in for a rope crossing whose sidecar has no BatonRope (#1283 S3b: a ladder has none;
        // a controller without the ladder states shows plain StandingPatrol). It is the member's own walk at the climb
        // rate, BlacksiteLandingGearPolicy.ClimbSpeed = 1.2 m/s over the GetRoleMoveSpeed() UpdateAnimation normalises
        // by (3.6 m/s, 4.8 m/s urgent), so the walk plays at 1.2 / 3.6 = 0.333 of real time or the feet skate.
        internal static float LadderFallbackBlend(float roleMoveSpeed)
            => Mathf.Clamp01(BlacksiteLandingGearPolicy.ClimbSpeed / Mathf.Max(.1f, roleMoveSpeed));

        internal static float LadderFallbackSpeed(float roleMoveSpeed)
            => BlacksiteLandingGearPolicy.ClimbSpeed / Mathf.Max(.1f, roleMoveSpeed);

        // A real ladder state carries its own playback rate, authored where the clip is measured:
        // asset-pipeline/BlacksiteVanillaMotionBake.cs bakes the vanilla ClimbLadder clip, measures
        // the hand rise it produces on the rig and keeps the authored in-place cycle at rate 1.
        // ClimbRate controls the state multiplier; the runtime leaves _animator.speed at 1
        // for every ladder state and the direction is a state of its own.
        //
        // The direction cannot be a runtime negative speed. Animator.speed clamps to zero: the S5.2
        // fixture measured a requested -1 arriving as 0.000, which froze the member on the mount
        // clip. AnimatorState.speed does take a negative, so a descent is the same clip authored
        // once more at the negated rate under the *Descend name. Where that state is absent the
        // ascent state is played forwards, which still reads as a ladder and never as a rope.
        internal const float LadderPlaybackSpeed = 1f;

        internal static string LadderRiseState(BlacksiteTraversalKind kind, string climbState, string descendState)
            => kind == BlacksiteTraversalKind.LadderDown ? descendState : climbState;

        // #1283 S4: only an ascent tops out; a descent's step-off at the foot (also Dismounting) holds the descend state.
        internal static bool TopsOut(BlacksiteTraversalKind kind, bool topOutAuthored)
            => kind != BlacksiteTraversalKind.LadderDown && topOutAuthored;

        // The phases on the ladder itself; the approach and a ship ladder's deck leg are ordinary locomotion.
        internal static bool OnLadder(BlacksiteLadderPhase phase)
            => phase == BlacksiteLadderPhase.Mounting || phase == BlacksiteLadderPhase.Climbing || phase == BlacksiteLadderPhase.Dismounting;

        /// <summary>
        /// The phase chooses the clip: mount, climb and dismount are ladder states, the approach keeps
        /// ordinary locomotion. #1283 S4: the dismount plays <paramref name="topOutState"/>, which the
        /// resolver hands the rise state when the controller authors no top-out (UpdateAnimation then
        /// holds it at ClimbRate 0), so the step over the top never snaps to locomotion. #874 F07: the
        /// state travels with its capability, and the function is total on purpose, so "an unauthored
        /// climb (<paramref name="climbAuthored"/> false) is never presented as a ladder" is one pure
        /// rule a fixture can pin.
        /// </summary>
        internal static BlacksiteLadderPose LadderPoseForPhase(BlacksiteLadderPhase phase, BlacksiteTraversalKind kind,
            string mountState, string climbState, string descendState, string topOutState, bool climbAuthored)
        {
            if (!OnLadder(phase) || !climbAuthored) return new BlacksiteLadderPose(Fallback, climbAuthored, false);
            string state = phase == BlacksiteLadderPhase.Mounting ? mountState
                : phase == BlacksiteLadderPhase.Dismounting ? topOutState
                : LadderRiseState(kind, climbState, descendState);
            return new BlacksiteLadderPose(state, true, true);
        }

        // #991 ---------------------------------------------------------------------------------

        internal static bool IsJump(BlacksiteTraversalKind kind)
            => kind == BlacksiteTraversalKind.Jump || kind == BlacksiteTraversalKind.JumpRunning;

        /// <summary>The state each jump kind names. Direction is a state, exactly as it is for the
        /// ladder, because Animator.speed cannot express one.</summary>
        internal static string JumpState(BlacksiteTraversalKind kind, string standingState, string runningState)
            => kind == BlacksiteTraversalKind.JumpRunning ? runningState : standingState;

        // The rope's rate: its clip length over the crossing, bounded because a short rope (6 m, 0.78 s)
        // would otherwise blur the 2.6 s loop at 3.3x.
        internal const float CrossingSpeedMin = .7f, CrossingSpeedMax = 1.6f;
        internal static float CrossingAnimatorSpeed(float clipSeconds, float crossingSeconds)
            => Mathf.Clamp(clipSeconds / Mathf.Max(.01f, crossingSeconds), CrossingSpeedMin, CrossingSpeedMax);
        // #1271: a jump plays its airtime window (from JumpTakeoffNormalizedTime) over exactly the crossing,
        // unclamped. A crossing too short to show it plays no clip; peers read that off this replicated rate.
        internal static float JumpAnimatorSpeed(float crossingSeconds)
            => BlacksiteLandingGearPolicy.JumpStandingAirtime / Mathf.Max(.01f, crossingSeconds);
        internal static bool ShowsJumpClip(float jumpRate)
            => jumpRate * BlacksiteLandingGearPolicy.JumpClipMinimumCrossingSeconds <= BlacksiteLandingGearPolicy.JumpStandingAirtime;
    }
}
