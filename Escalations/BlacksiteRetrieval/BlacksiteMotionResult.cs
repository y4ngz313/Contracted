namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #874 S4: who writes a soldier's transform this frame. The agent walks the body; a crossing
    /// motor (a jump link, a ship rope/ladder/lip, a world-ladder climb, an elevator ride, the
    /// payload coupling) hand-walks it; a seat (a ship passenger seat, a corpse, a monster capture)
    /// holds it where something else puts it. Everything else that stands a member still is a
    /// dwell: the agent stays the writer and is stopped until a deadline.
    /// </summary>
    internal enum BlacksiteBodyWriter : byte { Agent, Crossing, Seated }

    /// <summary>
    /// #1013 A6: what a movement request actually did. #874 S4: every value names a path
    /// <c>BlacksiteMotionOwner.Request</c> can still take.
    /// </summary>
    internal enum BlacksiteMotionResult
    {
        /// <summary>A destination was handed to the agent on this call.</summary>
        Issued,

        /// <summary>
        /// The request asked for the goal the member is already walking to, so nothing was
        /// re-issued. The legs are moving under this request's own goal.
        /// </summary>
        Unchanged,

        /// <summary>
        /// A crossing or a seat owns the body, or the interaction coordinator took the
        /// destination, and the agent destination is not this caller's to set.
        /// </summary>
        OwnedByMotor,

        /// <summary>
        /// #955 S12A U2: the issue gate dropped a byte-identical re-issue, or there was no agent to
        /// command at all, or the agent threw while being commanded, or (#874 Q12) the goal is
        /// under the flood plane.
        /// </summary>
        Dropped,
    }

    internal static class BlacksiteMotionResultExtensions
    {
        /// <summary>
        /// #1013 A6: the one definition of "this request left the member under way". True for a
        /// destination that was issued, for a goal the member is already walking to, and for a
        /// motor that took the body; false for every refusal or drop.
        /// </summary>
        internal static bool MotionUnderway(this BlacksiteMotionResult result)
            => result == BlacksiteMotionResult.Issued
               || result == BlacksiteMotionResult.Unchanged
               || result == BlacksiteMotionResult.OwnedByMotor;
    }

    /// <summary>
    /// #1015 S1: the coarse motion contract. <see cref="BlacksiteMotionResult"/> survives as the
    /// fine-grained reason inside <see cref="BlacksiteMotionVerdict"/>.
    /// </summary>
    internal enum BlacksiteMotionOutcome
    {
        /// <summary>The legs are under way toward this request's own goal.</summary>
        Moving,
        /// <summary>The request was refused for a named reason; the goal is kept.</summary>
        Blocked,
        /// <summary>The member is inside the arrival tolerance of the point it was given.</summary>
        Arrived,
        /// <summary>A crossing, a seat or the interaction coordinator owns the body's destination.</summary>
        OwnedByMotor
    }

    /// <summary>
    /// #1015 S1: what one <c>Request</c> did. <see cref="Result"/> and <see cref="Reason"/> are the
    /// #1013 tokens; <see cref="Outcome"/> is the coarse contract the brain reads.
    /// </summary>
    internal readonly struct BlacksiteMotionVerdict
    {
        internal readonly BlacksiteMotionOutcome Outcome;
        internal readonly BlacksiteMotionResult Result;
        internal readonly string Reason;

        internal BlacksiteMotionVerdict(BlacksiteMotionOutcome outcome, BlacksiteMotionResult result, string reason)
        { Outcome = outcome; Result = result; Reason = reason; }

        /// <summary>#1015 S1 decision 1: defined on the COARSE outcome. #1305 H (C3, R1): a route a
        /// live hazard disc refuses is the job's own wait - the member stands facing the disc until
        /// the disc's exit event - so the job that asked keeps the member through it.</summary>
        internal bool MotionUnderway()
            => Outcome == BlacksiteMotionOutcome.Moving
               || Outcome == BlacksiteMotionOutcome.Arrived
               || Outcome == BlacksiteMotionOutcome.OwnedByMotor
               || (Outcome == BlacksiteMotionOutcome.Blocked && Reason == HazardDiscReason);

        /// <summary>#1305 H (C3): the reason a route refused by a live hazard disc is reported with.</summary>
        internal const string HazardDiscReason = "hazardDisc";

        internal static string Token(BlacksiteMotionOutcome outcome) => outcome.ToString();
    }
}
