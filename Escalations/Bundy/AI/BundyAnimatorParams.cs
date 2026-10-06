using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.AI
{
    internal static class BundyAnimatorParams
    {
        internal const string MotionState = "MotionState";
        internal const string DoAttack = "DoAttack";
        internal const string AttackIndex = "AttackIndex";
        internal const string DoFinisher = "DoFinisher";
        internal const string FinisherEntry = "FinisherEntry";
        internal const string FinisherBody = "FinisherBody";
        internal const string DoEmote = "DoEmote";
        internal const string EmoteIndex = "EmoteIndex";
        internal const string DoContextual = "DoContextual";
        internal const string ContextType = "ContextType";
        internal const string PullStart = "PullStart";
        internal const string DoStagger = "DoStagger";
        internal const string StaggerType = "StaggerType";
        internal const string DoTurn = "DoTurn";
        internal const string TurnIndex = "TurnIndex";
        // Locomotion playback-rate multiplier (Stage 1: speed-synced locomotion). Drives the
        // Walk/Run states' Speed Multiplier so foot speed tracks the agent's real velocity
        // (kills the float/slide during acceleration and cornering). Synced via NetworkAnimator.
        internal const string Speed = "Speed";
        // Stage 4: directional locomotion blend tree. (MoveX, MoveY) = Bundy's planar velocity in his
        // own local space (right, forward) in m/s, so he plays the correct 8-way walk/run clip for any
        // movement direction relative to his facing. Synced via NetworkAnimator.
        internal const string MoveX = "MoveX";
        internal const string MoveY = "MoveY";
        // #276: pre-hashed ids for the three params written EVERY frame while moving (Speed,
        // MoveX, MoveY), so the per-frame SetFloat calls skip the string hashing. Event-driven
        // setters keep using the string names.
        internal static readonly int SpeedHash = Animator.StringToHash(Speed);
        internal static readonly int MoveXHash = Animator.StringToHash(MoveX);
        internal static readonly int MoveYHash = Animator.StringToHash(MoveY);
        // Job 5a: drop-down follow. True for the airborne window of a scripted ledge drop; the Unity-side
        // BundyJumpStatePatch adds the matching bool and the AnyState -> Jump_Fall transition to the
        // shipping controller's Base Layer. Absent on any bundle built before that patch, in which case
        // BundyAIBase.DropJump warns once and drops without the fall pose. Synced via NetworkAnimator.
        internal const string Falling = "Falling";
        // Issue #205: the overhead sprint, Bundy's third gait. ONE bool drives all three authored
        // clips — the Unity-side BundySprintStatePatch wires Sprint_Intro -> Sprint_Loop ->
        // Sprint_Outro behind it, so MotionState keeps its Idle/Walk/Run = 0/1/2 contract untouched
        // and every consumer that switches on it (audio loops, directional blend, idle demotion,
        // diagnostics) is unaffected. Absent on any bundle built before that patch, in which case
        // BundyAIBase.Sprint warns once and he keeps running. Synced via NetworkAnimator.
        internal const string Sprinting = "Sprinting";

        internal const int MotionIdle = 0;
        internal const int MotionWalk = 1;
        internal const int MotionRun = 2;
        internal const int TurnLeft90 = 0;
        internal const int TurnRight90 = 1;
        internal const int Turn180 = 2;
    }
}
