// #1018 S4: the idle Action's RULES - the phases one idle episode passes through, the closed
// successor vocabulary, and the pure ranking that hands a bounded hold exactly one thing to do
// next.
//
// Strictly Unity-free: no UnityEngine using, no Vector3, no Time. tests/BlacksiteBehaviourRegression
// compiles Vector3-free sources only, and splitting the rules from the points they rank is what lets
// that suite drive the WHOLE table headlessly - all 32 rejection masks against every precondition
// combination - and prove, rather than assert, that a bounded hold always terminates.
//
// The Vector3 half of the same policy (the BlacksiteSuccessorSet overload of SecondsFor) lives in
// BlacksiteSquadAssignment.cs beside the record whose points it reads; this class is declared
// partial for exactly that split.
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1018 S4: what an idle member is doing right now. Every value but <c>None</c> carries an
    /// expiry, which is the whole of the exit criterion "no idle hold without an expiry".
    /// </summary>
    internal enum BlacksiteIdlePhase : byte
    {
        /// <summary>The member has an actionable assignment; the idle action holds no state.</summary>
        None = 0,
        /// <summary>Standing at the assignment's hold point with the facing sweep armed, until
        /// <see cref="BlacksiteIdleActionPolicy.HoldSeconds"/>.</summary>
        Hold = 1,
        /// <summary>Walking to exactly one ranked successor, under that successor's own expiry.</summary>
        Successor = 2,
        /// <summary>Every successor was refused. The rally ladder's point, with the Idle lease's
        /// own ceiling, and the planner told to replan now.</summary>
        Rally = 3
    }

    /// <summary>
    /// #1018 S4: the purposeful things a bounded idle hold can end in. Closed and ordered; the
    /// ranking in <see cref="BlacksiteIdleActionPolicy.Rank"/> is the only producer.
    /// </summary>
    internal enum BlacksiteIdleSuccessor : byte
    {
        None = 0,
        /// <summary>A covering position off the active operator's approach lane, with sight of it.</summary>
        CoverStep = 1,
        /// <summary>An unexplored frontier node or a doorway of the room the member stands in.</summary>
        FrontierTile = 2,
        /// <summary>Close on the squad lead this member has drifted away from.</summary>
        Regroup = 3,
        /// <summary>A guard patrol post.</summary>
        GuardPost = 4,
        /// <summary>The terminal. A rally point with an expiry, never a refusal without one.</summary>
        RallyHold = 5
    }

    /// <summary>
    /// #1018 S4: the bounds and the ranking. Pure, total and closed - for any set of proved
    /// candidates and any rejection mask this class returns exactly one successor and a positive
    /// number of seconds for it, which is how "every idle hold ends in a purposeful successor"
    /// survives without touching a score curve (design note C.7).
    /// </summary>
    internal static partial class BlacksiteIdleActionPolicy
    {
        /// <summary>
        /// #1018 S4: the whole hold phase, not one look.
        ///
        /// The derivation and the Human call it carries are design note A.4 and G6: today the
        /// per-target sweep hold is <c>BlacksiteMotionPolicy.SweepHoldMinSeconds</c> 3 to
        /// <c>SweepHoldMaxSeconds</c> 6 over as many targets as the room has doorways and corners,
        /// commonly five to seven, so an idle episode is 15 to 40 s of continuous purposeful
        /// standing re-armed every 4 s. Six seconds is #1018's own number and it is a REDUCTION of
        /// the per-episode look count, not a tightening of an existing 6 s rule. The sweep stays
        /// armed through a <see cref="BlacksiteIdleSuccessor.GuardPost"/> successor (G6's adopted
        /// recommendation), so an episode still produces several looks while no hold is unbounded.
        /// </summary>
        internal const float HoldSeconds = 6f;

        /// <summary>
        /// The default life each successor kind carries when the proved point did not name one: the
        /// idle step's own declared dwell - the walk to the point and the stand on it - after which
        /// the action re-ranks. #1293 R2: these are the idle action's numbers, no longer quoted from
        /// the deleted <c>BlacksiteRaidJob</c> budgets (values unchanged: a cover step 15 s, a
        /// frontier tile and a regroup 22 s). A stalled walk is the progress ledger's miss. A post
        /// lives 17 s: the longest patrol dwell (<see cref="BlacksitePatrolPolicy.MaxDwellSeconds"/>)
        /// plus two seconds for the next waypoint's leg, so it never expires first (#874 Q6).
        /// </summary>
        internal const float CoverSeconds = 15f;
        internal const float FrontierSeconds = 22f;
        internal const float RegroupSeconds = 22f;
        internal const float PostSeconds = BlacksitePatrolPolicy.MaxDwellSeconds + 2f;

        /// <summary>
        /// The terminal's own declared dwell at the rally point, 15 s (the value the Idle lease
        /// ceiling had before #874 S4 deleted the lease book).
        /// </summary>
        internal const float RallySeconds = 15f;

        /// <summary>The successor kinds a rejection mask can address, and therefore its width.</summary>
        internal const int SuccessorKinds = 5;
        /// <summary>Nothing refused yet.</summary>
        internal const byte NoneRejected = 0;
        /// <summary>Every kind refused. There are exactly <c>AllRejected + 1</c> = 32 masks.</summary>
        internal const byte AllRejected = (1 << SuccessorKinds) - 1;

        /// <summary>This successor's place in the rejection mask. <c>None</c> occupies no bit.</summary>
        internal static byte Bit(BlacksiteIdleSuccessor successor)
            => successor == BlacksiteIdleSuccessor.None ? (byte)0 : (byte)(1 << ((int)successor - 1));

        /// <summary>Whether this mask has already refused this successor.</summary>
        internal static bool Refused(byte rejected, BlacksiteIdleSuccessor successor)
            => (rejected & Bit(successor)) != 0;

        /// <summary>The same mask with one more kind refused.</summary>
        internal static byte Refuse(byte rejected, BlacksiteIdleSuccessor successor)
            => (byte)(rejected | Bit(successor));

        /// <summary>
        /// #1018 S4: the successor ranking (design note C.4). Ranked, first applicable wins.
        ///
        /// <list type="number">
        /// <item><see cref="BlacksiteIdleSuccessor.CoverStep"/> - the member is standing on the
        /// operator's approach lane, a covering point is
        /// proved, and it is not queued for a resource (a member waiting its turn does not leave
        /// the queue to take cover).</item>
        /// <item><see cref="BlacksiteIdleSuccessor.FrontierTile"/> - the squad has NO committed
        /// route and a frontier is proved. That precondition is exactly
        /// <see cref="BlacksiteIdlePolicy.IssuesRoutelessDestination"/>'s rule: with a route in
        /// force nothing else is walking anywhere either, so a held observation is the right
        /// answer and #990 S16 D3's routeless case is the one that walks.</item>
        /// <item><see cref="BlacksiteIdleSuccessor.Regroup"/> - the member is separated from the
        /// lead and a regroup point is proved. The lead's own storey test
        /// (<see cref="BlacksiteResponsePolicy.OffStorey"/>, 4 m) is the planner's: it only proves
        /// a regroup point for a lead on this member's storey, so it is folded into
        /// <paramref name="hasRegroup"/> rather than re-asked here.</item>
        /// <item><see cref="BlacksiteIdleSuccessor.GuardPost"/> - a post is proved. A post is a
        /// hold with a purpose and needs no other precondition.</item>
        /// <item><see cref="BlacksiteIdleSuccessor.RallyHold"/> - everything above refused or
        /// rejected.</item>
        /// </list>
        ///
        /// The terminal is deliberately NOT refusable: its own bit exists so <see cref="Bit"/> is
        /// total over the enum, but a mask that refuses it still gets it, because a hold with
        /// nowhere to go is the failure S4 exists to delete. That is what makes this function total
        /// (see <see cref="AlwaysTerminates"/>) and why it can never answer
        /// <see cref="BlacksiteIdleSuccessor.None"/>.
        /// </summary>
        internal static BlacksiteIdleSuccessor Rank(
            bool onOperatorLane, bool separatedFromLead,
            bool hasCommittedRoute, bool queuedForResource,
            bool hasCover, bool hasFrontier, bool hasRegroup, bool hasPost,
            byte rejected)
        {
            if (onOperatorLane && hasCover && !queuedForResource
                && !Refused(rejected, BlacksiteIdleSuccessor.CoverStep))
                return BlacksiteIdleSuccessor.CoverStep;
            if (!hasCommittedRoute && hasFrontier
                && !Refused(rejected, BlacksiteIdleSuccessor.FrontierTile))
                return BlacksiteIdleSuccessor.FrontierTile;
            if (separatedFromLead && hasRegroup
                && !Refused(rejected, BlacksiteIdleSuccessor.Regroup))
                return BlacksiteIdleSuccessor.Regroup;
            if (hasPost && !Refused(rejected, BlacksiteIdleSuccessor.GuardPost))
                return BlacksiteIdleSuccessor.GuardPost;
            return BlacksiteIdleSuccessor.RallyHold;
        }

        /// <summary>
        /// The expiry one successor carries, from the seconds the planner proved with the point.
        /// Never zero: a proved point that named no life falls back to its kind's own dwell above,
        /// and the terminal is always the rally dwell. A zero here would be an idle hold
        /// with no expiry, which is the thing #1018 deletes.
        /// </summary>
        internal static float SecondsFor(BlacksiteIdleSuccessor successor,
            float coverSeconds, float frontierSeconds, float regroupSeconds, float postSeconds)
        {
            switch (successor)
            {
                case BlacksiteIdleSuccessor.CoverStep: return Positive(coverSeconds, CoverSeconds);
                case BlacksiteIdleSuccessor.FrontierTile: return Positive(frontierSeconds, FrontierSeconds);
                case BlacksiteIdleSuccessor.Regroup: return Positive(regroupSeconds, RegroupSeconds);
                case BlacksiteIdleSuccessor.GuardPost: return Positive(postSeconds, PostSeconds);
                default: return RallySeconds;
            }
        }

        private static float Positive(float value, float fallback) => value > 0f ? value : fallback;

        /// <summary>
        /// The invariant, stated as a function: a bounded hold always has somewhere to go next.
        ///
        /// #1018 S4 (fix batch 1, SF13): this is a TAUTOLOGY, deliberately and by construction, and
        /// saying so is the point. <see cref="Rank"/>'s last statement is an unconditional
        /// <c>return RallyHold</c> and the terminal is not refusable, so there is no input for
        /// which the ranking can answer <see cref="BlacksiteIdleSuccessor.None"/> - which is exactly
        /// the property "every idle hold ends in a purposeful successor" needs. It is written as a
        /// call rather than as <c>=&gt; true</c> so that a future edit which makes the last return
        /// conditional breaks it here instead of silently reintroducing an unbounded hold; the
        /// evaluation point is the worst corner of the domain (nothing proved, a route committed,
        /// queued for a resource). Totality itself is PROVED by the suite, which walks all 32 masks
        /// against every precondition combination and asserts one non-None answer each, not by this
        /// single-corner check.
        /// </summary>
        internal static bool AlwaysTerminates(byte rejected)
            => Rank(false, false, true, true, false, false, false, false, rejected)
               != BlacksiteIdleSuccessor.None;
    }
}
