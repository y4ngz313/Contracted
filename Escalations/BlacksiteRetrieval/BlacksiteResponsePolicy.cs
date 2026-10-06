using System;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // A called guard's response has explicit stages. Each stage is a distinct piece of work on the
    // member's own duty board entry, so it opens its own attempt and owns its own deadline; one
    // guard's arrival can never renew the other's clock, because each member owns its own duty.
    internal enum BlacksiteBackupPhase : byte
    {
        Dispatched = 0, EntranceTransition = 1, TravelToReportedFloor = 2,
        LocalSupportArrival = 3, CombatOrSearch = 4, Released = 5
    }
    // How a response goal was resolved, and why an idle member chose its next goal. Both appear
    // verbatim in the diagnostic line, so a live log names the decision instead of a coordinate.
    internal enum BlacksiteResponseGoal : byte { SupportSlot, FloorNode, EntranceHold, FloorAnchor, Blocked }
    internal enum BlacksiteIdleGoal : byte { Patrol, Lane, Coverage, Frontier, Observe, Blocked }
    // What a support pass publishes. Ring is one of the twelve bearings; HoldWhereItStands is the
    // member's own stance, answered when none of them stood or the frame budget deferred the pass.
    // #1283 S6: there is no tether and no terminal. The escort caller never walks HoldWhereItStands
    // (it takes the squad route); the backup floor response reads its NoReachableCandidate as its trigger.
    internal enum BlacksiteSupportFallback : byte { Ring, HoldWhereItStands }
    internal static class BlacksiteResponsePolicy
    {
        // A support candidate belongs to the storey it is judged against, not to the actor's own
        // height. For a response that storey is the REPORTED point's supporting floor: an upstairs
        // fight six metres above the entrance is reachable by stairs, and rejecting candidates
        // against the guard's current height would refuse every one of them before any path exists.
        internal const float FloorTolerance = 1.25f;
        // A NavMesh sample this far from the reported point is a different storey, so the raw
        // reported height is kept instead of snapping the response onto the floor below.
        internal const float FloorAnchorTolerance = 2.5f;
        // #874 S9.5: the radius the reported point is sampled against, named rather than repeated.
        // It is also the horizontal window in which the navigator's last good navmesh position is
        // a better sample of the SAME place: further than this it is a different place, not a
        // cleaner reading of this one.
        internal const float ReportSampleRadius = 3f;
        // #874 S9.5: how far under a report the supporting ground is looked for when the NavMesh
        // carries no sample there. One body height: the swept capsule (BlacksitePhysicalStep
        // .CapsuleHeight, 1.96 m, read here through its Unity-free home
        // BlacksiteWorldLadderColumn.BodyColumnMetres), and a reporter knocked up to one body
        // height off its floor by melee is still on that floor. It is deliberately SMALLER than the
        // storey tests around it, so this fallback can never reach down onto another storey.
        internal const float ReportDropMetres = BlacksiteWorldLadderColumn.BodyColumnMetres;
        // A new report retargets the response; only a materially different storey restarts travel.
        internal const float FloorChangeMetres = 3f;
        internal const float ArrivalMetres = 8f;
        internal const float SupportArrivalMetres = 2.5f;
        // #1271 S3b: a post is released after this long with no report and no fight near it. It
        // outlasts round 2's widest report gap (27.2 s, Player.log 33549 -> 35540), and matches the
        // 35 s the withdrawal rule grants a response.
        internal const float PostQuietSeconds = 35f;
        // #874 S6.4b/S7.6: the one definition of "off the anchor's storey". Off it, the order IS
        // the anchor and the elevator, the stairs or a world ladder owns the route; a support
        // candidate placed round the anchor would be judged against the anchor's own storey and
        // leave the member with nothing to walk to. Four metres is the threshold S6.4b already
        // uses in SupportOrSearchOrder and the same gap the native mineshaft task requires before
        // it will offer a ride at all (BlacksiteElevatorInteraction.TryCreateNativeMineshaftTask).
        // It sits above both storey tests below it: FloorTolerance 1.25 (same storey) and
        // FloorChangeMetres 3 (a report worth restarting travel for). A mineshaft storey in the
        // repaired run is 13 to 37 metres, so nothing real sits between 3 and 4.
        internal const float OffStoreyMetres = 4f;
        internal static bool OffStorey(float actorY, float anchorY) => Math.Abs(anchorY - actorY) > OffStoreyMetres;
        internal static bool OnFloor(float candidateY, float floorY) => Math.Abs(candidateY - floorY) <= FloorTolerance;
        internal static bool MaterialFloorChange(float wasY, float nowY) => Math.Abs(nowY - wasY) > FloorChangeMetres;
        // Phases only advance inside one response: jitter at a distance or floor boundary cannot
        // reopen an earlier stage and hand the guard a renewed deadline. A report on a materially
        // different floor restarts travel explicitly, not through this rule.
        internal static BlacksiteBackupPhase NextPhase(BlacksiteBackupPhase phase, bool sameWorld,
            bool onReportedFloor, bool atSupportPoint, bool combat, bool areaChecked)
        {
            if (phase == BlacksiteBackupPhase.Released) return phase;
            if (areaChecked && !combat) return BlacksiteBackupPhase.Released;
            var stage = combat ? BlacksiteBackupPhase.CombatOrSearch
                : !sameWorld ? BlacksiteBackupPhase.EntranceTransition
                : !onReportedFloor ? BlacksiteBackupPhase.TravelToReportedFloor
                : !atSupportPoint ? BlacksiteBackupPhase.LocalSupportArrival
                : BlacksiteBackupPhase.CombatOrSearch;
            return stage > phase ? stage : phase;
        }
    }
    // An idle discretionary member picks purposeful work, never a step around itself. A member
    // already standing on a useful covering position holds and observes: deliberate observation
    // is a legitimate outcome, and a hold is what stops corner-to-corner hopping.
    internal static class BlacksiteIdlePolicy
    {
        // Never stand within this distance of the active operator's approach lane.
        internal const float LaneClearance = 1.5f;
        // Bounded blocked idle choices before the member reports blockage and holds.
        internal const int MaxBlockedChoices = 3;
        internal static BlacksiteIdleGoal Choose(bool guard, bool patrol, bool holdingUsefulCover,
            bool onOperatorLane, bool cover, bool frontier)
            => guard ? (patrol ? BlacksiteIdleGoal.Patrol : BlacksiteIdleGoal.Observe)
             : holdingUsefulCover ? BlacksiteIdleGoal.Observe
             : cover ? (onOperatorLane ? BlacksiteIdleGoal.Lane : BlacksiteIdleGoal.Coverage)
             : frontier ? BlacksiteIdleGoal.Frontier
             : onOperatorLane ? BlacksiteIdleGoal.Blocked
             : BlacksiteIdleGoal.Observe;

        /// <summary>
        /// #990 (Stage D3): when a held sweep is not good enough, and the member walks instead.
        ///
        /// #957 made every idle choice but Lane and Patrol a hold plus a facing sweep, which is
        /// right while the squad has somewhere to be: a member standing on a useful covering
        /// position should watch a doorway, not hop between corners. It is wrong when the squad has
        /// no committed route at all, because then nothing else is moving either. The 2026-09-12
        /// Whistleblower run is the case: the room-graph planner failed its first and only plan at
        /// t=105.67 and stayed routeless for the remaining five minutes, so all four raiders idled
        /// on <c>Coverage</c>, <c>Observe</c>, <c>Frontier</c> and <c>Blocked</c> and issued no
        /// destination between them for a 35 s stretch in which nobody moved a metre.
        ///
        /// Only the two choices that already mean "I have nothing here" walk: <c>Frontier</c> (an
        /// unexplored node exists and the member wanted it) and <c>Blocked</c> (no covering
        /// position and no frontier proved). <c>Coverage</c> and <c>Observe</c> are deliberate
        /// observation and stay held whatever the route says, because a member that has found a
        /// useful place to watch from has somewhere to be by definition.
        /// </summary>
        internal static bool IssuesRoutelessDestination(BlacksiteIdleGoal choice, bool hasCommittedRoute)
            => !hasCommittedRoute
               && (choice == BlacksiteIdleGoal.Frontier || choice == BlacksiteIdleGoal.Blocked);
    }
}
