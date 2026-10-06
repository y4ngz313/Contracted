using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #874 S4 / #1293 R2: the ledger's host half. One instance lives on
    /// <see cref="BlacksiteSquadDirector"/> (a host-only object) and is ticked from its
    /// <c>UpdateServer</c>; nothing here runs on a client, nothing replicates and nothing moves a
    /// body. Every miss goes to the planner: <c>NoteStalled</c>, or <c>NoteStranded</c> on entry
    /// into the stranded fact.
    /// </summary>
    internal sealed partial class BlacksiteMotionMonitor
    {
        private readonly BlacksiteRetrievalMissionController _mission;
        private readonly Dictionary<ulong, Commitment> _commitments = new Dictionary<ulong, Commitment>();

        internal BlacksiteMotionMonitor(BlacksiteRetrievalMissionController mission)
        {
            _mission = mission;
        }

        internal void Tick(float now, IReadOnlyList<BlacksiteSoldierAI> members)
        {
            for (int i = 0; i < members.Count; i++) TickMember(now, members[i]);
        }

        /// <summary>
        /// #1293 R2: a device's progress event (the cart moved its pair forward) for the member's
        /// commitment in force. A member with no commitment yet opens one on the next tick anyway.
        /// </summary>
        internal void NoteProgress(BlacksiteSoldierAI member)
        {
            if (member == null || !_commitments.TryGetValue(member.NetworkObjectId, out Commitment commitment)) return;
            Progress(ref commitment, Time.time);
            _commitments[member.NetworkObjectId] = commitment;
        }

        /// <summary>A member left the squad: its commitment goes with it.</summary>
        internal void Forget(BlacksiteSoldierAI member)
        {
            if (member != null) _commitments.Remove(member.NetworkObjectId);
        }

        /// <summary>Round reset: every member's commitment.</summary>
        internal void Reset() => _commitments.Clear();

        private void TickMember(float now, BlacksiteSoldierAI member)
        {
            if (member == null || member.isEnemyDead) return;
            var navigator = member.Navigator;
            if (navigator == null) return;
            ulong id = member.NetworkObjectId;
            _commitments.TryGetValue(id, out Commitment commitment);
            Reading reading = Read(member, navigator, commitment);
            Verdict verdict = Observe(ref commitment, now, reading);
            _commitments[id] = commitment;
            if (verdict == Verdict.None) return;
            Vector3 goal = verdict == Verdict.Refused ? reading.RefusedGoal : reading.Goal;
            BlacksitePathState path = navigator.PathState;
            // #1305 H (C3): a refusal names its own reason - floodedGoal or hazardDisc - read off the
            // verdict it was read from on this same tick.
            BlacksiteDiagnostics.Log("Monitor",
                $"stalled member={member.MemberIndex}, goal={BlacksiteDiagnosticFormat.V(goal)}, path={path}"
                + (verdict == Verdict.Refused ? ", cause=" + navigator.Motion.LastVerdict.Reason : verdict == Verdict.Stranded ? ", cause=stranded" : string.Empty));
            // #1293 R3: the miss is the member's own Outcome decision event too, besides the
            // planner's answer below.
            member.RequestDecision(BlacksiteDecisionCause.Outcome);
            var planner = _mission.Objectives?.Planner;
            if (planner == null) return;
            // #1293 R2 (rule 3): entering the stranded fact is a miss with the stall answer's
            // releases; the planner then holds him, and the fact itself is never judged.
            if (verdict == Verdict.Stranded) planner.NoteStranded(member);
            else planner.NoteStalled(member, goal, path);
        }

        // The kind from state its owners already hold; the route is read only for Travel. A refused
        // route is a goal asked and not reached, so until the ledger has answered that refusal it
        // travels even with no destination issued (#1293 R2 A1e: a member fresh outside - a world
        // change forgets the issued destination - whose first leg is refused). Once answered, the
        // standing refusal alone is not travel, so a member who then holds without asking again is
        // never judged by the window on it.
        private static Reading Read(BlacksiteSoldierAI member, BlacksiteSoldierAI.BlacksiteNavigator navigator, in Commitment commitment)
        {
            Vector3 goal = navigator.CommitmentGoal;
            bool refused = navigator.Motion.LastRefusedRoute(out Vector3 refusedGoal);
            bool refusalUnanswered = refused && !(commitment.Open && commitment.RefusalAnswered
                && Vector3.Distance(refusedGoal, commitment.RefusedGoal) <= BlacksiteArrivalPolicy.MinDestinationChange);
            // #1305 G2 (R16): a transition its owner holds (TraversalForbidsStop) is Crossing, declared;
            // an entrance Approach stays judged Travel, a queued one Queue.
            var interactions = navigator.Interactions;
            bool bodyIsAgent = navigator.BodyWriter == BlacksiteBodyWriter.Agent
                && (interactions == null || !interactions.TraversalForbidsStop);
            var kind = KindOf(navigator.IsStranded, member.IsSeated, navigator.InDwell, member.InCombat,
                navigator.InTraversalQueue, navigator.IsPayloadCoupled, bodyIsAgent,
                (navigator.HasIssuedDestination || refusalUnanswered) && !navigator.HasArrived(goal));
            var reading = new Reading { Goal = goal, Kind = kind, Refused = refused, RefusedGoal = refusedGoal };
            if (kind == BlacksiteCommitmentKind.Travel)
            {
                // A pending path still carries the old one's distance, so it is not measured.
                reading.Remaining = navigator.PathState == BlacksitePathState.Pending ? -1f : navigator.RemainingDistance;
                reading.RouteEnd = navigator.RouteEnd;
                reading.Leg = navigator.IssuedDestination;
                reading.LegReach = navigator.LegReach;
                reading.Body = member.transform.position;
            }
            return reading;
        }
    }
}
