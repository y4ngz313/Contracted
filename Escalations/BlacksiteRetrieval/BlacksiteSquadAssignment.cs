// #1018 S4: what one member has been told to do, as a value.
//
// Unity-bound ONLY through Vector3 and through the four order types whose constructor arities five
// native fixtures pin (BlacksiteSquadOrder, BlacksiteSquadOrderKind, BlacksiteFormationKind,
// BlacksiteApproachProfile) - so the record can project the legacy BlacksiteMemberOrder wire shape
// without allocating and without one line of executor churn (design note B.1).
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1018 S4 / #872 D8: what a destination MEANS, so no reader has to infer a hold from the
    /// coincidence that the goal equals the member's own feet.
    ///
    /// <c>SupportPoint</c>'s <c>HoldWhereItStands</c> terminal and <c>PickupApproach</c>'s refusal
    /// both answer with <c>soldier.transform.position</c> today, which the duty board measures as
    /// <c>remaining=Infinity</c> through <see cref="BlacksiteBlockedCause.NoReachableCandidate"/> -
    /// a distance the progress budget's closure epsilon can never satisfy. That is the 25 s Reassign
    /// cycle of the 2026-09-12 run. A hold now says it is a hold in its own field (design note C.5).
    /// </summary>
    internal enum BlacksiteAssignmentGoalKind : byte
    {
        /// <summary>Walk there; progress is measured against the leg.</summary>
        Travel = 0,
        /// <summary>Walk there and act; the <see cref="BlacksiteApproachProfile"/> is the contract.</summary>
        Interact = 1,
        /// <summary>Stand HERE, on purpose: the idle action's Hold phase
        /// (<see cref="BlacksiteIdleActionPolicy.HoldSeconds"/>) is the dwell.</summary>
        Hold = 2,
        /// <summary>Stand THERE, on purpose - a guard post, for the post's own life.</summary>
        Post = 3
    }

    /// <summary>
    /// #1018 S4: the four successor candidates the planner has PROVED for this member, each a point,
    /// a facing and its own life. Every field is a value, so reading the set allocates nothing and a
    /// <c>foreach</c> over it never exists to box (design note G10).
    ///
    /// The planner fills it on its own tick with its own path budget; the idle action only reads it.
    /// That is what makes <c>ExecuteIdleAction</c> safe to enter every frame (C.3).
    /// </summary>
    internal readonly struct BlacksiteSuccessorSet
    {
        internal readonly bool HasCover, HasFrontier, HasRegroup, HasPost;
        /// <summary>
        /// #1018 S4 (fix batch 2, S6): whether the proved cover point is the OPERATOR LANE case -
        /// the member is standing on the active operator's approach and clearing it is a
        /// correctness move - rather than an ordinary covering choice. It is the ranking's first
        /// precondition, only the planner can know it, and putting it here is what makes the
        /// planner's preview and the idle action's own rank agree, so <c>successor=</c> in the log
        /// is what the body actually takes.
        /// </summary>
        internal readonly bool CoverIsLane;
        internal readonly Vector3 Cover, Frontier, Regroup, Post;
        internal readonly Vector3 CoverFacing, FrontierFacing, RegroupFacing, PostFacing;
        internal readonly float CoverSeconds, FrontierSeconds, RegroupSeconds, PostSeconds;

        internal BlacksiteSuccessorSet(
            bool hasCover, Vector3 cover, Vector3 coverFacing, float coverSeconds,
            bool hasFrontier, Vector3 frontier, Vector3 frontierFacing, float frontierSeconds,
            bool hasRegroup, Vector3 regroup, Vector3 regroupFacing, float regroupSeconds,
            bool hasPost, Vector3 post, Vector3 postFacing, float postSeconds,
            bool coverIsLane = false)
        {
            CoverIsLane = coverIsLane;
            HasCover = hasCover; Cover = cover; CoverFacing = coverFacing; CoverSeconds = coverSeconds;
            HasFrontier = hasFrontier; Frontier = frontier; FrontierFacing = frontierFacing; FrontierSeconds = frontierSeconds;
            HasRegroup = hasRegroup; Regroup = regroup; RegroupFacing = regroupFacing; RegroupSeconds = regroupSeconds;
            HasPost = hasPost; Post = post; PostFacing = postFacing; PostSeconds = postSeconds;
        }

        internal bool Any => HasCover || HasFrontier || HasRegroup || HasPost;

        /// <summary>Whether the planner proved this kind. The ranking's four <c>has*</c> terms.</summary>
        internal bool Has(BlacksiteIdleSuccessor successor)
        {
            switch (successor)
            {
                case BlacksiteIdleSuccessor.CoverStep: return HasCover;
                case BlacksiteIdleSuccessor.FrontierTile: return HasFrontier;
                case BlacksiteIdleSuccessor.Regroup: return HasRegroup;
                case BlacksiteIdleSuccessor.GuardPost: return HasPost;
                default: return false;
            }
        }

        /// <summary>
        /// The point, facing and life of one proved successor. False for a kind the planner did not
        /// prove and for the terminal, whose point is the rally ladder's rather than the set's.
        /// </summary>
        internal bool TryGet(BlacksiteIdleSuccessor successor, out Vector3 goal, out Vector3 facing, out float seconds)
        {
            switch (successor)
            {
                case BlacksiteIdleSuccessor.CoverStep:
                    goal = Cover; facing = CoverFacing; seconds = CoverSeconds; return HasCover;
                case BlacksiteIdleSuccessor.FrontierTile:
                    goal = Frontier; facing = FrontierFacing; seconds = FrontierSeconds; return HasFrontier;
                case BlacksiteIdleSuccessor.Regroup:
                    goal = Regroup; facing = RegroupFacing; seconds = RegroupSeconds; return HasRegroup;
                case BlacksiteIdleSuccessor.GuardPost:
                    goal = Post; facing = PostFacing; seconds = PostSeconds; return HasPost;
                default:
                    goal = Vector3.zero; facing = Vector3.zero; seconds = 0f; return false;
            }
        }

        // The four composers. Each returns a new value, so the planner builds a set field by field
        // inside its own per-member pass without a builder object and without touching the heap.
        internal BlacksiteSuccessorSet WithCover(Vector3 point, Vector3 facing, float seconds,
            bool coverIsLane = false)
            => new BlacksiteSuccessorSet(true, point, facing, seconds,
                HasFrontier, Frontier, FrontierFacing, FrontierSeconds,
                HasRegroup, Regroup, RegroupFacing, RegroupSeconds,
                HasPost, Post, PostFacing, PostSeconds, coverIsLane);

        internal BlacksiteSuccessorSet WithFrontier(Vector3 point, Vector3 facing, float seconds)
            => new BlacksiteSuccessorSet(HasCover, Cover, CoverFacing, CoverSeconds,
                true, point, facing, seconds,
                HasRegroup, Regroup, RegroupFacing, RegroupSeconds,
                HasPost, Post, PostFacing, PostSeconds, CoverIsLane);

        internal BlacksiteSuccessorSet WithRegroup(Vector3 point, Vector3 facing, float seconds)
            => new BlacksiteSuccessorSet(HasCover, Cover, CoverFacing, CoverSeconds,
                HasFrontier, Frontier, FrontierFacing, FrontierSeconds,
                true, point, facing, seconds,
                HasPost, Post, PostFacing, PostSeconds, CoverIsLane);

        internal BlacksiteSuccessorSet WithPost(Vector3 point, Vector3 facing, float seconds)
            => new BlacksiteSuccessorSet(HasCover, Cover, CoverFacing, CoverSeconds,
                HasFrontier, Frontier, FrontierFacing, FrontierSeconds,
                HasRegroup, Regroup, RegroupFacing, RegroupSeconds,
                true, point, facing, seconds, CoverIsLane);
    }

    /// <summary>
    /// #1018 S4: what one member has been told to do. The ONE writer is
    /// <c>BlacksiteSquadPlanner</c>; every reader takes a copy.
    ///
    /// It replaces the seven writers of <c>duty.Order</c> (design note A.2) with one. #1293 R2: it
    /// has no expiry. It lives until the planner replaces it (a re-issue), drops it (a world
    /// transfer) or releases it (membership, unregister, a new mission) - the planner refreshes
    /// every live member on every pass, so nothing it stops refreshing is left standing.
    /// </summary>
    internal readonly struct BlacksiteSquadAssignment
    {
        // ---- identity and liveness ----
        internal readonly ulong Member;

        /// <summary>
        /// Monotonic per member per mission. A reader that remembers the last value it saw can tell
        /// "a new assignment" from "the same assignment re-read" without comparing destinations,
        /// which is what the 70-73 % byte-identical re-issue rate in #952's forensics could not be
        /// measured without. A byte-identical refresh does NOT move it (design note C.2).
        /// </summary>
        internal readonly int Sequence;
        internal readonly float IssuedAt;

        /// <summary>
        /// The rule that produced it, verbatim into diagnostics. The existing <c>[UAT][Job]</c>
        /// issue-line vocabulary (<c>duty | retry | roomAdvance | next goal</c>) plus <c>regroup</c>
        /// and <c>idle</c>.
        /// </summary>
        internal readonly string Source;

        // ---- the work ----
        internal readonly BlacksiteJobKind Kind;
        /// <summary>The record it serves, or the manifest's <c>NoCarrier</c>.</summary>
        internal readonly ulong ObjectId;
        internal readonly BlacksiteSquadOrderKind OrderKind;
        internal readonly BlacksiteFormationKind Formation;
        /// <summary>
        /// #1018 S4 (fix batch 1, SF11): the five <see cref="BlacksiteSquadOrder"/> fields the
        /// projection must NOT drop, carried explicitly rather than derived.
        ///
        /// <c>PlayerOrder</c> (<c>…Players.cs:206-211</c>) sets all of them to non-defaults - the
        /// contact's revision as the sequence, the contact's position as the anchor, its confidence,
        /// and an uncertainty radius that grows with staleness - and <c>TryRouteSlotOrder</c>
        /// (<c>…Raid.cs:279</c>) sets the anchor and the forward from the formation's own geometry.
        /// A first draft rebuilt the squad order with <c>0</c>, the destination and
        /// <c>Facing - Destination</c>, which silently zeroed a player response's confidence and
        /// moved the formation's anchor onto each member's own goal.
        ///
        /// <c>Forward</c> is stored raw and re-passed, never re-derived: the order's own constructor
        /// normalises it and substitutes <c>Vector3.forward</c> for a zero vector, so handing the
        /// same raw value back through the same constructor reproduces the original exactly.
        /// <c>OrderSequence</c> is named apart from this record's own <see cref="Sequence"/>, which
        /// is the planner's monotonic counter and deliberately never reaches the wire.
        /// </summary>
        internal readonly int OrderSequence;
        internal readonly Vector3 Anchor, Forward;
        internal readonly float Confidence, UncertaintyRadius;
        internal readonly bool ExactContact;
        internal readonly BlacksiteAssignmentGoalKind GoalKind;
        internal readonly Vector3 Destination, Facing;
        internal readonly float ArrivalTolerance, SeparationRadius;
        internal readonly bool IsInteractionOperator, IsPlayerResponse, TargetOutside;
        internal readonly bool HasAssignedSweepPoint;
        internal readonly Vector3 AssignedSweepPoint;
        internal readonly BlacksiteApproachProfile Approach;
        internal readonly BlacksiteBlockedCause Blocked;
        /// <summary>#1219 E2: the pace the issuing duty declared on the order.</summary>
        internal readonly BlacksiteOrderPace Pace;

        // ---- what the idle action's successor table ranks over (design note C.4) ----
        internal readonly BlacksiteSuccessorSet Successors;

        internal BlacksiteSquadAssignment(
            ulong member, int sequence, float issuedAt, string source,
            BlacksiteJobKind kind, ulong objectId,
            BlacksiteSquadOrderKind orderKind, BlacksiteFormationKind formation,
            int orderSequence, Vector3 anchor, Vector3 forward,
            float confidence, float uncertaintyRadius, bool exactContact,
            BlacksiteAssignmentGoalKind goalKind,
            Vector3 destination, Vector3 facing,
            float arrivalTolerance, float separationRadius,
            bool isInteractionOperator, bool isPlayerResponse, bool targetOutside,
            bool hasAssignedSweepPoint, Vector3 assignedSweepPoint,
            BlacksiteApproachProfile approach = default,
            BlacksiteBlockedCause blocked = BlacksiteBlockedCause.None,
            BlacksiteSuccessorSet successors = default,
            BlacksiteOrderPace pace = BlacksiteOrderPace.Normal)
        {
            Member = member; Sequence = sequence; IssuedAt = issuedAt;
            Source = source; Kind = kind; ObjectId = objectId;
            OrderKind = orderKind; Formation = formation;
            OrderSequence = orderSequence; Anchor = anchor; Forward = forward;
            Confidence = confidence; UncertaintyRadius = uncertaintyRadius; ExactContact = exactContact;
            GoalKind = goalKind;
            Destination = destination; Facing = facing;
            ArrivalTolerance = arrivalTolerance; SeparationRadius = separationRadius;
            IsInteractionOperator = isInteractionOperator; IsPlayerResponse = isPlayerResponse;
            TargetOutside = targetOutside;
            HasAssignedSweepPoint = hasAssignedSweepPoint; AssignedSweepPoint = assignedSweepPoint;
            Approach = approach; Blocked = blocked;
            Successors = successors;
            Pace = pace;
        }

        /// <summary>A <c>default</c> assignment is not one: the planner's sequence starts at 1.</summary>
        internal bool IsValid => Sequence > 0;

        /// <summary>A hold or a post is not a travel leg, so the budget observes it as
        /// <see cref="BlacksiteJobPhase.Holding"/> with a real distance (design note C.5).</summary>
        internal bool IsHold => GoalKind == BlacksiteAssignmentGoalKind.Hold
                                || GoalKind == BlacksiteAssignmentGoalKind.Post;

        /// <summary>
        /// #1018 S4 (design note C.2): the same assignment with the SAME <c>Sequence</c>. This is
        /// what a byte-identical refresh does, and it is what makes the re-issue rate readable
        /// straight off the log - <c>seq</c> stays put.
        /// #1233 S3 (rule 2): the goal is <paramref name="goal"/>'s - the order the refresh was made
        /// against - so a held Follow's goal is the one re-derived from its live anchor, and a plain
        /// order's is the one it was issued with.
        /// </summary>
        internal BlacksiteSquadAssignment Refreshed(BlacksiteBlockedCause blocked,
            BlacksiteAssignmentGoalKind goalKind, in BlacksiteMemberOrder goal)
            => new BlacksiteSquadAssignment(Member, Sequence, IssuedAt, Source, Kind, ObjectId,
                OrderKind, Formation, OrderSequence, goal.SquadOrder.Anchor, goal.SquadOrder.Forward,
                Confidence, UncertaintyRadius, ExactContact, goalKind, goal.Destination, goal.PreferredFacing,
                ArrivalTolerance, SeparationRadius, IsInteractionOperator, IsPlayerResponse, TargetOutside,
                HasAssignedSweepPoint, AssignedSweepPoint, Approach, blocked,
                Successors, Pace);

        /// <summary>
        /// The same assignment with the successors the planner proved this tick. They are not part
        /// of the work, so they do not move <c>Sequence</c>: a successor is not an assignment
        /// (design note F.5).
        /// </summary>
        internal BlacksiteSquadAssignment WithSuccessors(in BlacksiteSuccessorSet successors)
            => new BlacksiteSquadAssignment(Member, Sequence, IssuedAt, Source, Kind, ObjectId,
                OrderKind, Formation, OrderSequence, Anchor, Forward, Confidence, UncertaintyRadius,
                ExactContact, GoalKind, Destination, Facing, ArrivalTolerance, SeparationRadius,
                IsInteractionOperator, IsPlayerResponse, TargetOutside,
                HasAssignedSweepPoint, AssignedSweepPoint, Approach, Blocked,
                successors, Pace);

        /// <summary>
        /// The legacy wire shape, built without allocating: a struct in, two structs out, both
        /// <c>readonly struct</c>, so nothing reaches the heap.
        ///
        /// Every need in design note A.8 is a field of <see cref="BlacksiteMemberOrder"/> - which is
        /// why the struct survives S4 and <c>ExecuteSquadOrderCore</c>'s 125 lines are untouched.
        /// <c>Sequence</c> is deliberately NOT passed through to <c>BlacksiteSquadOrder.Sequence</c>:
        /// every producer in the tree issues 0 there and <c>MakeOrder</c> hard-codes it, so putting
        /// the planner's own counter on the wire would change a field five fixtures read. The wire
        /// sequence is <see cref="OrderSequence"/> - whatever the producer set (SF11).
        /// </summary>
        internal BlacksiteMemberOrder ToMemberOrder()
        {
            var squadOrder = new BlacksiteSquadOrder(OrderSequence, OrderKind, Formation, Anchor,
                Forward, Confidence, UncertaintyRadius, ExactContact,
                HasAssignedSweepPoint, AssignedSweepPoint);
            return new BlacksiteMemberOrder(squadOrder, Destination, Facing, SeparationRadius,
                ArrivalTolerance, IsInteractionOperator, IsPlayerResponse, TargetOutside, Approach,
                true, Kind, Pace);
        }
    }

    /// <summary>
    /// #1018 S4: the Vector3 half of the idle policy - the overload that reads a proved set instead
    /// of four loose floats. The Unity-free half, and the reason for the split, are in
    /// BlacksiteIdleAction.cs.
    /// </summary>
    internal static partial class BlacksiteIdleActionPolicy
    {
        /// <summary>The expiry one successor carries, from the set's own seconds. Never zero.</summary>
        internal static float SecondsFor(BlacksiteIdleSuccessor successor, in BlacksiteSuccessorSet set)
            => SecondsFor(successor, set.CoverSeconds, set.FrontierSeconds, set.RegroupSeconds, set.PostSeconds);

        /// <summary>
        /// The ranking against a proved set, so a caller cannot pass four <c>has*</c> terms that
        /// disagree with the points it is about to read.
        /// </summary>
        internal static BlacksiteIdleSuccessor Rank(
            bool onOperatorLane, bool separatedFromLead,
            bool hasCommittedRoute, bool queuedForResource,
            in BlacksiteSuccessorSet set, byte rejected)
            => Rank(onOperatorLane, separatedFromLead, hasCommittedRoute,
                queuedForResource, set.HasCover, set.HasFrontier, set.HasRegroup, set.HasPost, rejected);
    }
}
