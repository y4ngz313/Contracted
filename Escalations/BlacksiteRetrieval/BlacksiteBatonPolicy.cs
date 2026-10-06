namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteBatonPhase : byte { Stowed, Drawing, Ready, Swing, Stowing }
    internal static class BlacksiteBatonPolicy
    {
        internal const float TurnDegreesPerSecond = 300f;
        internal static bool DamageRequiresStow(bool hardStun, bool handsUnavailable) => hardStun || handsUnavailable;
        internal const float DrawSeconds = 1.233333f, StowSeconds = 1.7f;
        // #1219 F2: the one attack playback factor. The three attack clips are authored at 30 fps
        // and used to play at 1.0 while the locomotion around them runs on the calibrated stride
        // clock (animSpeed 1.333 at the urgent role speed, 4.8 / 3.6), so a swing read about a
        // quarter slower than the walk it came out of. 1.25 is the bottom of that clock's band,
        // taken rather than the full 1.333 so the wind-up keeps its authored weight: the clips carry
        // their own slow wind-up and fast strike in their frames, and nothing here adds a second
        // factor to either. EVERY swing time below is the clip's own frame divided by this, and the
        // presentation plays the attack state at exactly this rate (BlacksiteSoldierAI.Equipment
        // ResolveBatonAnimatorSpeed), so the hit window and the recovery frame stay on the clip:
        // clip time = elapsed x BatonAnimatorSpeed. The draw and the stow are not attacks and keep
        // their authored rate.
        internal const float BatonAnimatorSpeed = 1.25f;
        // The authored length of each attack clip in CLIP seconds. The only raw value here; the
        // presentation reads it to place the animator inside the clip.
        internal static float ClipSeconds(int swing) => swing == 0 ? 2.4f : swing == 1 ? 2.266667f : 3.166667f;
        // Wall seconds on the replicated swing clock, like every other time below.
        internal static float Duration(int swing) => ClipSeconds(swing) / BatonAnimatorSpeed;
        // #874 S2.3: a swing releases the actor at its own reviewed recovery frame, not at the end of
        // its clip. Measured on the bundled attack clips (BlacksiteMeleeCadenceChecks): the earliest
        // 30 fps frame at or after ActiveUntil plus the .1 s blend where a .15 s crossfade into
        // BatonReady's first frame moves the weapon head under 2.5 m/s, every sampled joint under
        // 2 m/s and every sampled joint under the actor's own 300 deg/s turn rate. Earlier than this
        // the follow-through is still mid-arc and the blend reads as a snap. #1219 F2: the frame is
        // the clip's; the time is that frame at BatonAnimatorSpeed.
        internal static float ReadyAt(int swing) => ClipFrame(swing == 0 ? 62 : swing == 1 ? 58 : 73);
        private static float ClipFrame(int frame) => frame / 30f / BatonAnimatorSpeed;
        // #874 S2.1r: the swept weapon is the whole baton, not its head alone. Measured on the
        // shipped equipment bundle by BlacksiteMeleeReachChecks (S21B_BATONMESH), which reads every
        // vertex of the bundled baton mesh in the model's own local frame - the frame the sweep
        // builds its points in: the mesh runs from y = -0.105 to y = 0.617 m with a maximum radial
        // extent of 0.027 m. HeadOffset and HeadRadius are unchanged, and the head sphere is not
        // inflated: above y = HeadOffset - HeadRadius = 0.39 m the mesh reaches only 0.106 m from
        // the head point, comfortably inside 0.18. The shaft is the rest of the weapon, and its
        // measured half-thickness there is 0.0240 m, rounded up to the next centimetre. Nothing
        // here is chosen; the fixture re-measures the mesh and asserts these against it.
        internal const float HeadOffset = .57f;
        internal const float HeadRadius = .18f;
        internal const float ShaftRadius = .03f;
        // The shaft is swept by laying one capsule along the whole weapon at each of a few poses
        // interpolated between the previous frame and this one, so the step between consecutive
        // poses must stay inside the shaft's own diameter or a thin contact can pass between two of
        // them. The head is the fast end, so its travel inside the window sets the count:
        // ceil(travel / 2 * ShaftRadius) intervals, that is one capsule more than that. The cap is
        // what the fastest measured swing needs on a 30 fps server, where the head covers about
        // 0.6 m in a frame: 0.6 / 0.06 = 10 intervals, so 16 leaves headroom and still bounds the
        // per-frame query count. At 60 fps the measured travel is about 0.3 m, that is 5. #1219 F2:
        // those travels were measured at the authored rate; at BatonAnimatorSpeed 1.25 the head
        // covers about 0.75 m in a 30 fps frame, 13 intervals, still inside the cap.
        internal const int MaxShaftSweepSteps = 16;
        internal static int ShaftSweepSteps(float headTravel)
            => headTravel <= 0f ? 1
                : System.Math.Min(MaxShaftSweepSteps, System.Math.Max(1, (int)System.Math.Ceiling(headTravel / (2d * ShaftRadius))));
        internal static float ActiveFrom(int swing) => ClipFrame(swing == 0 ? 27 : swing == 1 ? 24 : 29);
        internal static float ActiveUntil(int swing) => ClipFrame(swing == 0 ? 31 : swing == 1 ? 28 : 33);
        internal static bool CrossesHitWindow(int swing, float previous, float now) => now >= ActiveFrom(swing) && previous <= ActiveUntil(swing) && now >= previous;
        internal static bool InHand(BlacksiteBatonPhase phase, float elapsed) => phase == BlacksiteBatonPhase.Ready || phase == BlacksiteBatonPhase.Swing
            || (phase == BlacksiteBatonPhase.Drawing && elapsed >= .5f) || (phase == BlacksiteBatonPhase.Stowing && elapsed < .6f);
        internal static bool Powered(BlacksiteBatonPhase phase, float elapsed) => phase == BlacksiteBatonPhase.Ready || phase == BlacksiteBatonPhase.Swing
            || (phase == BlacksiteBatonPhase.Drawing && elapsed >= .9f) || (phase == BlacksiteBatonPhase.Stowing && elapsed < .266667f);
    }
}
