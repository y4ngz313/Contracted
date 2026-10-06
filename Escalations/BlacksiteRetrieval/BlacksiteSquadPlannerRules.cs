// #1018 S4: the planner's PURE rules - the replan gate and the two intent classifications.
//
// Design note D says the duty board's twelve-boolean replan gate becomes "the planner's ShouldReplan
// - the same twelve terms, unchanged, now pure and testable (F.5)". This is that type. The rules
// here decide whether to re-issue, whether an intent is discretionary work and whether its goal
// moves under the member; none of them touches the world, so tests/BlacksiteJobLifecycleRegression
// drives them directly rather than restating them. #1293 R2/R3: an assignment lives until the
// planner replaces or releases it; the gate takes edges and BlacksitePlannerDirtySet picks a pass's members.
//
// Unity-free but for the stubbed Vector3, so the headless suite pins the one copy of each rule the runtime applies.
using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #874 P-S2: what a Follow assignment walks to. <see cref="Anchor"/> is the anchor's slot on
    /// its route, re-derived every tick; in the other world the executor's leg is the link the anchor
    /// crossed (#1233 S3) and the goal is still the anchor's. <see cref="None"/> is no follow at all,
    /// and the planner's ordinary order stands.
    /// </summary>
    internal enum BlacksiteFollowTarget : byte { None, Anchor }

    /// <summary>
    /// #874 Q1: the DIFFERENT goal an unreachable one is answered with, in the same answer:
    /// <see cref="Crossing"/> is the member's own-world crossing, <see cref="Replanned"/> the goal
    /// the planner's ordinary producers give the duty now (another post, another slot), and
    /// <see cref="None"/> means neither differs from the goal just given up - the member holds
    /// where it stands under the closed attempt and the next plan starts fresh.
    /// </summary>
    internal enum BlacksiteUnreachableRetarget : byte { None, Crossing, Replanned }

    internal static class BlacksiteSquadPlannerRules
    {
        /// <summary>#1018 S4, #1293 R3: the replan gate - a change of work, else an EDGE (arrival, revised goal, failed
        /// attempt). #1305 S: a room advance waits while the member walks a Complete path.</summary>
        internal static bool ShouldReplan(bool changed, bool roomAdvance, bool travelling, bool beatReady,
            bool interrupted, bool arrivalRose, bool goalRevised, bool retry)
            => changed || (roomAdvance && !travelling) || beatReady
               || (!interrupted && (arrivalRose || goalRevised || retry));

        /// <summary>#1305 S: a source=roomAdvance issue's cause= - his own arrival, a stall, else the lead's new goal.</summary>
        internal static string RoomAdvanceCause(bool arrivalRose, bool stall) => arrivalRose ? "arrival" : stall ? "stall" : "leadChanged";

        /// <summary>#1305 S: followers re-slot only for a lead goal move of one column spacing, or his stall.</summary>
        internal static bool ArmsFollowers(bool stall, float leadMove) => stall || leadMove >= BlacksiteEntranceColumn.SpacingMetres;

        /// <summary>#1305 S: a member at or above MovingSpeed across the floor is walking, so he has a behind.</summary>
        internal static bool Moving(Vector3 velocity) => Vector3.ProjectOnPlane(velocity, Vector3.up).magnitude >= BlacksiteMotionPolicy.MovingSpeed;

        /// <summary>#1305 S: an unwalkable slot's stand-in, never a search goal nor a new point at his feet (R2): the usable
        /// walkable point toward it if past his feet and not behind his travel, else his own goal, else the lead's.</summary>
        internal static Vector3 SlotFallback(Vector3 position, Vector3 velocity, Vector3? towardSlot, Vector3? currentGoal, Vector3 leadGoal)
            => towardSlot is Vector3 toward && Vector3.ProjectOnPlane(toward - position, Vector3.up).magnitude > BlacksiteArrivalPolicy.MinDestinationChange
               && (!Moving(velocity) || Vector3.Dot(toward - position, Vector3.ProjectOnPlane(velocity, Vector3.up)) >= 0f) ? toward : currentGoal ?? leadGoal;

        /// <summary>#1305 S: a moving follower never searches: the goal he walks, else the lead's; null (search on his own) only once he stands.</summary>
        internal static Vector3? MovingFollowerGoal(Vector3 velocity, Vector3? walking, Vector3 leadGoal) => Moving(velocity) ? walking ?? leadGoal : (Vector3?)null;

        /// <summary>
        /// #874 A4 (C.2): whether a RESPONSE evaluation is a refresh (goal within
        /// <see cref="BlacksiteArrivalPolicy.MinDestinationChange"/>, <c>Sequence</c> kept) rather than
        /// a re-issue. A first sight, a new attempt, a changed intent or no assignment re-issues.
        /// </summary>
        internal static bool RefreshesResponseInPlace(bool fresh, bool newDuty, bool changed,
            bool hasAssignment, float goalMove)
            => !fresh && !newDuty && !changed && hasAssignment
               && goalMove <= BlacksiteArrivalPolicy.MinDestinationChange;

        /// <summary>
        /// Whether this intent is discretionary work, which is what the 1.45 m minimum destination
        /// change gates and what the 4 s idle review runs for. <c>…Duties.cs:146-147</c> verbatim: a
        /// guard, an escort, a sweep and a rally can each yield without losing their job.
        /// </summary>
        internal static bool Discretionary(BlacksiteJobKind kind)
            => kind == BlacksiteJobKind.Guard || kind == BlacksiteJobKind.Escort
               || kind == BlacksiteJobKind.Sweep || kind == BlacksiteJobKind.Rally;

        /// <summary>
        /// A goal that moves under the member (<c>…Duties.cs:162-163</c>). #1293 R3: it is re-planned
        /// when the world row it is planned against moves (the row's <c>Revision</c>), never on a dwell.
        /// </summary>
        internal static bool MovingGoal(BlacksiteJobKind kind)
            => kind == BlacksiteJobKind.Escort || kind == BlacksiteJobKind.Objective
               || kind == BlacksiteJobKind.Rally;

        /// <summary>
        /// #874 P-S2 / #1233 S3 (rule 2) / #1271 S2: whether a member follows an anchor. A follow is
        /// the anchor's attempt: the anchor is live (BlacksiteMotionPolicy.FollowEndReason, the world
        /// left to the link leg), its attempt is open, and it is under way on a route
        /// (BlacksiteMotionPolicy.RouteInForce) - in any world and on any storey, a car ride included.
        /// An anchor with no route leads nobody: on 2026-09-29 round 2 (Player.log 21707-41084) the
        /// escorts of an operator standing where its Partial path ended were held "arrived" beside it
        /// for two minutes. A LEASHED member (an outside ship guard on Guard duty,
        /// BlacksiteDeploymentPolicy.LeashesDuty) follows nothing: its goal is its leash's, and a
        /// follow is walked unleashed.
        /// </summary>
        internal static BlacksiteFollowTarget FollowTarget(bool anchorLive, bool anchorAttemptOpen, bool anchorRouted,
            bool memberLeashed)
            => anchorLive && anchorAttemptOpen && anchorRouted && !memberLeashed
                ? BlacksiteFollowTarget.Anchor : BlacksiteFollowTarget.None;

        /// <summary>#1271 S2: an anchor's route leads only while its duty is not blocked; an operator
        /// the stand veto refused holds at its own feet on a path consumed there, which is no route.</summary>
        internal static bool AnchorRouted(bool hasRoute, BlacksiteBlockedCause blocked)
            => hasRoute && blocked == BlacksiteBlockedCause.None;

        /// <summary>
        /// #874 P-S2: the link a member follows its anchor through is the one the anchor itself
        /// cleared, when the anchor's last crossing left from the world this member stands in. With
        /// no such record the member's own world offers the director's door instead.
        /// </summary>
        internal static bool UsesAnchorCrossing(bool anchorCrossed, bool crossedFromMemberWorld)
            => anchorCrossed && crossedFromMemberWorld;

        /// <summary>
        /// #874 P-S2: a plan that produces the Follow the member already holds - the same anchor and
        /// the same target - is a REFRESH: the motion owner re-derives the goal from the live anchor,
        /// so nothing is re-issued. This is what deletes the 2 s escort resample (R3: 33/30/34 issues
        /// per member in 72 s, log 70356-72346). A changed intent, a new attempt and a retry still issue.
        /// </summary>
        internal static bool RefreshesFollowInPlace(bool followHeld, bool sameFollow, bool changed,
            bool newDuty, bool retry)
            => followHeld && sameFollow && !changed && !newDuty && !retry;

        /// <summary>
        /// #874 Q1: the source= and reason= an unreachable goal's replacement is issued under. It is
        /// never "retry": a retry re-issues the goal that failed, which is the 2026-09-23 round-2
        /// loop - NoReachableCandidate, remaining=Infinity, Invalidated, retry the same Rally
        /// (54332, 53526) for 165 s.
        /// </summary>
        internal const string UnreachableReason = "unreachable";

        /// <summary>
        /// #874 Q1 / S4: which goal replaces one this member stalled on. A candidate is a different
        /// goal only if it is further than <see cref="BlacksiteArrivalPolicy.MinDestinationChange"/>
        /// from the stalled one (the planner's one "same goal" threshold) and it applies. The
        /// crossing comes first; the planner passes <paramref name="crossingGivenUp"/> true for a
        /// goal in the member's own world, so only a cross-world goal is re-sited to the crossing.
        /// </summary>
        internal static BlacksiteUnreachableRetarget RetargetUnreachable(
            float crossingFromGoal, bool crossingGivenUp, float replannedFromGoal, bool replannedGivenUp)
        {
            if (crossingFromGoal > BlacksiteArrivalPolicy.MinDestinationChange && !crossingGivenUp)
                return BlacksiteUnreachableRetarget.Crossing;
            if (replannedFromGoal > BlacksiteArrivalPolicy.MinDestinationChange && !replannedGivenUp)
                return BlacksiteUnreachableRetarget.Replanned;
            return BlacksiteUnreachableRetarget.None;
        }
    }

    /// <summary>
    /// #1293 R3: which members one planner pass evaluates: marked, or at or past their
    /// <c>NextPlan</c>. The selection is made before the pass's first evaluation, so a mark taken
    /// during the pass counts on the next one.
    /// </summary>
    internal sealed class BlacksitePlannerDirtySet
    {
        private readonly HashSet<ulong> _marked = new HashSet<ulong>();
        private bool _all;

        internal void Mark(ulong member) => _marked.Add(member);
        internal void MarkAll() => _all = true;
        /// <summary>A released member's mark goes with it: NGO recycles network ids.</summary>
        internal void Forget(ulong member) => _marked.Remove(member);

        /// <summary>
        /// The ONE member selection of a pass (<c>BlacksiteSquadPlanner.Tick</c> only iterates it):
        /// fills <paramref name="due"/> with the positions in <paramref name="candidates"/> this pass
        /// evaluates - marked (the mark is consumed), a squad-wide mark taken before the pass, or at
        /// or past its <c>NextPlan</c>. A member with no plan state passes NegativeInfinity.
        /// </summary>
        internal void SelectPass(List<(ulong Member, float NextPlan)> candidates, float now, List<int> due)
        {
            bool all = _all;
            _all = false;
            due.Clear();
            for (int i = 0; i < candidates.Count; i++)
                if (_marked.Remove(candidates[i].Member) || all || now >= candidates[i].NextPlan) due.Add(i);
        }

        internal void Clear() { _marked.Clear(); _all = false; }
    }
}
