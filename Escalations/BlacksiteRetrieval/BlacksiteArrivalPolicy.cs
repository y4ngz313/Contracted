using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S4.2: one ordered arrival contract.
    //
    // The 2026-09-09 Offense / Shadow Raid run stacked three arrival authorities in the wrong
    // order: the agent stopped at stoppingDistance = 1.1 m, the navigation watchdog (deleted in
    // S4) treated anything inside ArrivalFloor = max(0.9, stop + 0.35) = 1.45 m as close enough
    // to stop supervising, and the
    // duty board treated anything inside max(1.8, order.ArrivalTolerance) as arrived. A goal that
    // had to be reached closer than 1.45 m therefore produced a member standing still at 1.08 m
    // with a valid path, not held, and invisible to every detector in the build.
    //
    // The repair is not a new number. It is an ordering: StopDistance <= ArrivalFloor <=
    // ArrivalTolerance, computed at one point, from the owner that is actually in force. Where an
    // owner's own derivation would break the ordering the value is clamped UPWARD, never downward,
    // so no authority is ever tighter than the one below it can deliver.
    //
    // Deliberately free of UnityEngine so tests/BlacksiteMotionStallRegression can assert the
    // ordering across every owner combination without an editor.
    internal readonly struct BlacksiteArrivalContract
    {
        /// <summary>Where the agent is told to stop. The NavMeshAgent's own stoppingDistance.</summary>
        internal float StopDistance { get; }

        /// <summary>
        /// The innermost boundary of "arrived": the stall classifier reports <c>arrived</c> (and the
        /// host motion monitor exempts the member) inside <see cref="ArrivalTolerance"/>, which is
        /// clamped up to this floor. Never below <see cref="StopDistance"/>: a member standing
        /// exactly where it was told to stop is not a stall.
        /// </summary>
        internal float ArrivalFloor { get; }

        /// <summary>
        /// The distance at which the duty board and every "have I arrived" test declare arrival.
        /// Never below <see cref="ArrivalFloor"/>: a board that declared arrival tighter than the
        /// arrival floor would keep re-issuing a goal that movement has already finished.
        /// </summary>
        internal float ArrivalTolerance { get; }

        /// <summary>Who set <see cref="StopDistance"/>, for the [UAT][Motion] stopOwner field.</summary>
        internal string StopOwner { get; }

        /// <summary>The live action's own gate, or 0 when no reach contract is in force.</summary>
        internal float ReachRadius { get; }

        internal bool HasReach => ReachRadius > 0f;

        internal BlacksiteArrivalContract(float stopDistance, float arrivalFloor, float arrivalTolerance,
            string stopOwner, float reachRadius)
        {
            StopDistance = stopDistance;
            ArrivalFloor = arrivalFloor;
            ArrivalTolerance = arrivalTolerance;
            StopOwner = string.IsNullOrWhiteSpace(stopOwner) ? "default" : stopOwner;
            ReachRadius = reachRadius;
        }

        /// <summary>Acceptance A5. True when the three values are in their required order.</summary>
        internal bool Ordered => StopDistance <= ArrivalFloor && ArrivalFloor <= ArrivalTolerance;

        /// <summary>
        /// #874 S8.5: how close a corridor follower must be to a waypoint before it hands out the
        /// next one. The fourth arrival authority, and the one S4.2 missed.
        ///
        /// Derived, not chosen. A member brakes at <see cref="ArrivalFloor"/> and every "have I
        /// arrived" test declares arrival at <see cref="ArrivalTolerance"/>, so a follower that
        /// only advances inside a SMALLER radius can never advance at all: run 1's member 1 stood
        /// 0.90 m short of its last ground-route waypoint for 243 s with a complete path, because
        /// arrival was 0.90 m and the follower's advance was a bare 0.60 m. The radius is
        /// therefore the floor plus the settle the agent is allowed anyway
        /// (<see cref="BlacksiteArrivalPolicy.SettleSlack"/>), and never under the tolerance, which
        /// extends S4.2's ordering to StopDistance &lt;= ArrivalFloor &lt;= ArrivalTolerance &lt;=
        /// AdvanceRadius. On the ground route that is 0.90 + 0.20 = 1.10 m.
        ///
        /// It is also never under the historical 0.60 m look-ahead
        /// (<see cref="BlacksiteArrivalPolicy.LegacyAdvanceRadius"/>), so a tighter contract can
        /// only make the follower advance EARLIER than the pacing the v13 live run proved
        /// (run 3 median issued/walked ratio 0.98), never later.
        /// </summary>
        internal float AdvanceRadius
        {
            get
            {
                float advance = ArrivalFloor + BlacksiteArrivalPolicy.SettleSlack;
                if (advance < ArrivalTolerance) advance = ArrivalTolerance;
                return advance < BlacksiteArrivalPolicy.LegacyAdvanceRadius
                    ? BlacksiteArrivalPolicy.LegacyAdvanceRadius : advance;
            }
        }

        /// <summary>
        /// Acceptance A5 extended to the follower: a route point may not be handed out later than
        /// the member declares arrival, or it brakes before it advances.
        /// </summary>
        internal bool OrderedForFollowing => Ordered && ArrivalTolerance <= AdvanceRadius;
    }

    internal static class BlacksiteArrivalPolicy
    {
        /// <summary>
        /// The movement default, unchanged. A generic walking goal has no reach requirement, so
        /// lowering this would only make every member brake later for no gain; an order that must
        /// be reached closely carries a BlacksiteApproachProfile instead.
        /// </summary>
        internal const float DefaultStoppingDistance = 1.1f;

        /// <summary>A ground-route waypoint is a corridor point and is walked over, not stopped at.</summary>
        internal const float GroundRouteStoppingDistance = .25f;

        /// <summary>
        /// A NavMeshAgent with autoBraking settles anywhere inside stoppingDistance, and the issued
        /// destination is itself a sampled navigation point. Both are budgeted before any gate.
        /// This is BlacksiteApproachProfile.SettleSlack; the profile reads it from here so the two
        /// cannot drift apart.
        /// </summary>
        internal const float SettleSlack = .2f;

        /// <summary>
        /// A member is inside an action's gate only with a margin: the gate is measured to the
        /// action's own anchor, and the issued destination is a sampled point near it.
        /// </summary>
        internal const float ProfileReachMargin = .1f;

        /// <summary>
        /// #874 S8.5: the corridor look-ahead the ground-route follower was written with
        /// (BlacksiteLandingApproach.TryWaypoint's three 0.6 literals at a2b9e9b). Retained only as
        /// the LOWER bound of <see cref="BlacksiteArrivalContract.AdvanceRadius"/>, so a contract
        /// tighter than the one the v13 run walked cannot make the follower advance later than the
        /// pacing that run proved. It is never an upper bound: 0.60 m is what stalled member 1.
        /// </summary>
        internal const float LegacyAdvanceRadius = .6f;

        /// <summary>
        /// The historical no-profile arrival floor, retained exactly: max(0.9, stop + 0.35). It is
        /// deliberately unchanged because loosening the arrived boundary is not this unit's repair
        /// and tightening it would report members that are correctly settled at a sampled goal as
        /// stalled.
        /// </summary>
        internal const float LegacyArrivalFloorMinimum = .9f;
        internal const float LegacyArrivalSlack = .35f;

        /// <summary>
        /// The arrival test with no order tolerance to follow: acceptance A1's own margin,
        /// remaining &gt; stop + 0.3 m. Shared with BlacksiteMotionPolicy so the stall classifier
        /// and the duty board cannot disagree about what "arrived" means.
        /// </summary>
        internal const float ArrivalMargin = BlacksiteMotionPolicy.ArrivalMargin;

        /// <summary>
        /// The NavMeshAgent radius that BlacksiteNavigator.ConfigureAgent writes. Two standing
        /// points closer together than this are the same standing point.
        /// </summary>
        internal const float AgentRadius = .45f;

        /// <summary>
        /// BlacksiteNavigator.SetDestinationThrottled compares the squared displacement of a new
        /// destination against 1.0 for an ordinary goal, so any re-issue under one metre is
        /// silently dropped by the navigator before the agent ever sees it.
        /// </summary>
        internal const float ReissueHysteresis = 1f;

        /// <summary>
        /// The smallest goal move a planner may hand out. Derived, not chosen: the navigator
        /// silently ignores anything under its own 1 m re-issue hysteresis, and a new post that is
        /// inside one agent radius of the old one is not a different place to stand, so
        /// 1.0 + 0.45 = 1.45 m is the first distance that is both issued and physically distinct.
        /// A board that hands out less than this produces episode 9's signature: the reported goal
        /// alternates while the issued destination, and therefore the remaining distance, never moves.
        /// </summary>
        internal const float MinDestinationChange = ReissueHysteresis + AgentRadius;

        // -------------------------------------------------------------------------------------
        // #874 Q1: the ONE arrival table. Stop distance (DefaultStoppingDistance), claim offset,
        // arrival margin (ArrivalMargin) and the entrance hand-off are read from here and nowhere
        // else, and the proof below fails the BUILD if they stop fitting inside each other.
        //
        // 2026-09-23 round 2 (log 40594-53808): the squad director displaced a member's standing
        // point 1.35 m, the agent stopped 1.1 m short of it, and the member stood 3.76 m from the
        // door it had to be within 3.0 m of to cross - for 165 s, until combat moved it.
        // -------------------------------------------------------------------------------------

        /// <summary>#874 S8.1/#1305: the ONE entrance hand-off - the threshold hold, the approach search bound and a door order's arrival band (two numbers once left 3.0-4.5 m unowned).</summary>
        internal const float EntranceHandoffDistance = 3f;

        /// <summary>
        /// #874 S4.3: the first crowd ring. Two standing bodies just clear each other at twice the
        /// agent radius.
        /// </summary>
        internal const float ClaimSeparationBase = 2f * AgentRadius;

        /// <summary>#874 S4.3: every further claimant on the point widens the ring by one body radius.</summary>
        internal const float ClaimSeparationStep = AgentRadius;

        /// <summary>
        /// #874 Q1: the widest displacement a crowd claim may hand out, whatever the crowd: two rings.
        /// The squad director's ring stops growing here, and the proof below holds it to the hand-off.
        /// </summary>
        internal const float MaxClaimOffset = ClaimSeparationBase + ClaimSeparationStep;

        /// <summary>
        /// #874 Q1: stop + claim + tolerance &lt;= hand-off, as a const-expression division that
        /// fails the build (the BlacksiteEngagementGeometry pattern). A member sent to the widest
        /// claim beside a door stops the agent's stop distance short of it and settles inside the
        /// arrival margin, and must still be inside the hand-off: 1.10 + 1.35 + 0.30 = 2.75 &lt;= 3.00.
        /// </summary>
        private const int ClaimFitsHandoff =
            1 / ((DefaultStoppingDistance + MaxClaimOffset + ArrivalMargin <= EntranceHandoffDistance) ? 1 : 0);

        /// <summary>Referenced so the proof above is not an unused constant.</summary>
        internal static int TableProofs => ClaimFitsHandoff;

        /// <summary>
        /// #874 Q1: how far from a leg a member stands once it has reached that leg - the agent's
        /// stop distance plus A1's margin, the classifier's own "reached" band.
        /// </summary>
        internal static float LegSettle(float stopDistance) => Math.Max(.1f, stopDistance) + ArrivalMargin;

        /// <summary>
        /// #874 Q1: whether a substituted standing point - a claim, a follow leg - satisfies the
        /// goal's own test: a member that reaches it settles <see cref="LegSettle"/> from it, and
        /// from there must still be inside <paramref name="goalRadius"/> of the goal.
        /// </summary>
        internal static bool SubstituteSatisfies(float substituteToGoal, float goalRadius, float stopDistance)
            => substituteToGoal + LegSettle(stopDistance) <= goalRadius + SubstituteEpsilon;

        /// <summary>
        /// #874 Q1: the displacement a claim may add to <paramref name="legToGoal"/> and still pass
        /// <see cref="SubstituteSatisfies"/>; never more than <see cref="MaxClaimOffset"/>, and zero
        /// or less when the leg itself leaves no room (a corner on the way, not the place to stand).
        /// </summary>
        internal static float ClaimAllowance(float goalRadius, float stopDistance, float legToGoal)
            => Math.Min(MaxClaimOffset, goalRadius - LegSettle(stopDistance) - legToGoal);

        /// <summary>Float slack on the substitution test, so a claim sampled on the boundary is not refused by rounding.</summary>
        internal const float SubstituteEpsilon = .01f;

        /// <summary>
        /// #874 S8.5: the advance radius of a ground-route issue with no reach profile and no order
        /// tolerance, which is the contract every corridor walk is made under: stop 0.25, floor
        /// max(0.9, 0.25 + 0.35) = 0.90, tolerance clamped up to 0.90, so 0.90 + 0.20 = 1.10 m.
        /// Callers that are not themselves issuing under a live contract read the number here
        /// rather than writing a literal.
        /// </summary>
        internal static float GroundRouteAdvanceRadius
            => Resolve(GroundRouteStoppingDistance, "ground-route", false, 0f, 0f, 0f).AdvanceRadius;

        /// <summary>#1305: a corridor walks under its own contract; an order's tolerance as its advance would skip proven corners.</summary>
        internal static float OrderToleranceFor(bool corridor, float orderTolerance) => corridor ? 0f : orderTolerance;

        /// <summary>#1305: arrival is horizontal, but a door goal (the goal in force under the hand-off band) arrives only where the hand-off can begin, height included.</summary>
        internal static bool WithinArrival(float horizontal, float vertical, float tolerance, bool goalInForce, float orderTolerance, float handoffHeight)
            => horizontal <= tolerance && (!goalInForce || orderTolerance != EntranceHandoffDistance || Math.Abs(vertical) <= handoffHeight);

        /// <summary>
        /// The one point the three values are computed. Every caller passes the owner state it
        /// already has; nothing here reads live navigation.
        /// </summary>
        /// <param name="stopDistance">BlacksiteNavigator.EffectiveStoppingDistance.</param>
        /// <param name="stopOwner">BlacksiteNavigator.StoppingDistanceOwner.</param>
        /// <param name="hasReach">Whether a combat or order approach profile is live.</param>
        /// <param name="reachStopDistance">That profile's StopDistance.</param>
        /// <param name="reachRadius">That profile's ReachRadius, i.e. the action's own gate.</param>
        /// <param name="orderTolerance">
        /// The order's ArrivalTolerance, or 0 when no order is in force. Clamped upward: an order
        /// that asks for 45 cm without carrying a reach profile is asking for a precision the
        /// movement layer was never told to deliver, and pretending otherwise is what produced a
        /// duty board that replanned every two seconds against a stop it could not reach.
        /// </param>
        internal static BlacksiteArrivalContract Resolve(
            float stopDistance,
            string stopOwner,
            bool hasReach,
            float reachStopDistance,
            float reachRadius,
            float orderTolerance)
        {
            float stop = Math.Max(.1f, stopDistance);
            float floor = hasReach
                ? Math.Min(reachStopDistance + SettleSlack, reachRadius - ProfileReachMargin)
                : Math.Max(LegacyArrivalFloorMinimum, stop + LegacyArrivalSlack);
            // Clamp upward. A melee profile with a 0.15 m stop and a 0.2 m reach derives a 0.1 m
            // floor, which would declare a correctly settled attacker stalled forever.
            if (floor < stop) floor = stop;
            float tolerance = orderTolerance > 0f ? orderTolerance : stop + ArrivalMargin;
            // Clamp upward. Below the floor the stall classifier already reports arrived, so a
            // tighter board tolerance can only re-issue a goal nothing will act on.
            if (tolerance < floor) tolerance = floor;
            return new BlacksiteArrivalContract(stop, floor, tolerance, stopOwner, hasReach ? Math.Max(0f, reachRadius) : 0f);
        }
    }
}
