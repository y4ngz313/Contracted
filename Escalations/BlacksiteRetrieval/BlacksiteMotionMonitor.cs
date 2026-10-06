using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1293 R2: what a member is committed to doing right now, derived on every ledger tick from
    /// state its owners already hold. <see cref="Travel"/> and <see cref="Push"/> are judged; every
    /// other kind is progress by declaration and never judged. <see cref="Stranded"/> is answered
    /// once, on entry. <see cref="None"/>: no goal, or arrived.
    /// </summary>
    internal enum BlacksiteCommitmentKind : byte
    {
        None,
        Travel,
        Push,
        Dwell,
        Seat,
        Crossing,
        Queue,
        Combat,
        Stranded
    }

    /// <summary>
    /// #1293 R2: the ONE progress ledger, host only. Per member one <see cref="Commitment"/>: the
    /// planner goal it serves, its kind, when it last progressed and the best path advance on the
    /// route in force. A Travel commitment progresses when the route's remaining distance reaches a
    /// new best <see cref="DropMetres"/> below the last one, or when it passes a leg's end onto the
    /// next leg (<see cref="PassedCorner"/>); a Push commitment only by a device event
    /// (<c>NoteProgress</c>: the cart moving its pair); a declared
    /// kind on every tick it holds. A judged commitment with no
    /// progress for <see cref="WindowSeconds"/> is a miss, answered once, and again only after
    /// another whole window. A route the motion owner refused for a routed commitment (Travel,
    /// Push) is a miss on its first observation, once per refused goal. Entering Stranded is a miss with the same
    /// answer, once per entry. Nothing here moves a body.
    ///
    /// This file is the pure half - the rules the headless suite pins against its Vector3 stub. The
    /// host's instance half (<c>BlacksiteMotionMonitor.Server.cs</c>) reads them off the member and
    /// its navigator and hands every miss to the planner.
    /// </summary>
    internal sealed partial class BlacksiteMotionMonitor
    {
        // #1293 R2: the one remaining clock - it replaces the warp, the second-stall span and the
        // devices' own progress clocks - and the one advance that counts as progress.
        internal const float WindowSeconds = 8f, DropMetres = 1f;

        internal enum Verdict { None, Stalled, Refused, Stranded }

        /// <summary>One member's commitment. <c>default</c> is no commitment yet.</summary>
        internal struct Commitment
        {
            internal bool Open;
            // The planner goal this commitment serves: a goal re-asked within MinDestinationChange
            // of it is the same commitment.
            internal Vector3 Goal;
            internal BlacksiteCommitmentKind Kind;
            internal float LastProgressAt;
            // The best remaining distance on the route in force, where the route ends and the leg
            // point it was issued for; Routed once measured on a route.
            internal bool Routed;
            internal float Best;
            internal Vector3 RouteEnd;
            internal Vector3 RouteLeg;
            // The refused goal this commitment's goal has already been answered for.
            internal bool RefusalAnswered;
            internal Vector3 RefusedGoal;
        }

        /// <summary>What the ledger reads of one member on one tick.</summary>
        internal struct Reading
        {
            // The planner goal the member serves - never a raw step or a leg of it.
            internal Vector3 Goal;
            internal BlacksiteCommitmentKind Kind;
            // Travel only: the navigator's remaining distance (negative: no path, or one still
            // pending), where the route in force ends, the leg point it was issued for, how near
            // that point the leg counts as walked, and where the body stands.
            internal float Remaining;
            internal Vector3 RouteEnd;
            internal Vector3 Leg;
            internal float LegReach;
            internal Vector3 Body;
            // The motion owner's last verdict refused the route to this goal.
            internal bool Refused;
            internal Vector3 RefusedGoal;
        }

        // Judged kinds walk a route, so a standing refusal is answered only for them.
        internal static bool Judged(BlacksiteCommitmentKind kind)
            => kind == BlacksiteCommitmentKind.Travel || kind == BlacksiteCommitmentKind.Push;

        /// <summary>
        /// The kind, first match wins. Stranded outranks everything: the body has no navmesh to be
        /// bound to. A seat outranks a dwell, combat intent, a wait, the crossing body writer and
        /// travel. A declared wait (a door, the ship ladder, a traversal queue) outranks the cart, so
        /// a coupled pair queued at a door is a Queue; the coupled pair outranks the crossing body
        /// writer it is.
        /// </summary>
        internal static BlacksiteCommitmentKind KindOf(bool stranded, bool seated, bool inDwell, bool inCombat,
            bool queued, bool coupledPush, bool bodyIsAgent, bool travelling)
        {
            if (stranded) return BlacksiteCommitmentKind.Stranded;
            if (seated) return BlacksiteCommitmentKind.Seat;
            if (inDwell) return BlacksiteCommitmentKind.Dwell;
            if (inCombat) return BlacksiteCommitmentKind.Combat;
            if (queued) return BlacksiteCommitmentKind.Queue;
            if (coupledPush) return BlacksiteCommitmentKind.Push;
            if (!bodyIsAgent) return BlacksiteCommitmentKind.Crossing;
            return travelling ? BlacksiteCommitmentKind.Travel : BlacksiteCommitmentKind.None;
        }

        /// <summary>
        /// One tick of one member. A new kind, or a goal moved more than MinDestinationChange from
        /// the commitment's own, opens a new commitment; entering Stranded is answered then, once.
        /// A refused route of a routed commitment (Travel, Push) is answered at once, once per refused goal. A
        /// declared kind (or none) progresses by declaration. A judged commitment that has not
        /// progressed for <see cref="WindowSeconds"/> is <see cref="Verdict.Stalled"/>, and the
        /// window restarts.
        /// </summary>
        internal static Verdict Observe(ref Commitment c, float now, in Reading r)
        {
            if (!c.Open || r.Kind != c.Kind
                || Vector3.Distance(r.Goal, c.Goal) > BlacksiteArrivalPolicy.MinDestinationChange)
            {
                bool enteredStranded = r.Kind == BlacksiteCommitmentKind.Stranded
                    && !(c.Open && c.Kind == BlacksiteCommitmentKind.Stranded);
                bool sameGoal = c.Open && Vector3.Distance(r.Goal, c.Goal) <= BlacksiteArrivalPolicy.MinDestinationChange;
                c = new Commitment
                {
                    Open = true, Goal = r.Goal, Kind = r.Kind, LastProgressAt = now, Best = float.NaN,
                    RefusalAnswered = sameGoal && c.RefusalAnswered, RefusedGoal = c.RefusedGoal
                };
                if (enteredStranded) return Verdict.Stranded;
            }
            if (Judged(c.Kind) && r.Refused && (!c.RefusalAnswered
                    || Vector3.Distance(r.RefusedGoal, c.RefusedGoal) > BlacksiteArrivalPolicy.MinDestinationChange))
            {
                c.RefusalAnswered = true;
                c.RefusedGoal = r.RefusedGoal;
                c.LastProgressAt = now;
                return Verdict.Refused;
            }
            if (!Judged(c.Kind))
            {
                c.LastProgressAt = now;
                return Verdict.None;
            }
            if (c.Kind == BlacksiteCommitmentKind.Travel) Advance(ref c, now, r);
            if (now - c.LastProgressAt < WindowSeconds) return Verdict.None;
            c.LastProgressAt = now;
            return Verdict.Stalled;
        }

        /// <summary>A device's progress event for the member's commitment in force.</summary>
        internal static void Progress(ref Commitment c, float now)
        {
            if (c.Open) c.LastProgressAt = now;
        }

        // Travel progress is path advance. With no path, or one still pending, nothing is measured
        // and nothing re-seeds. A re-issue to the same point is the same route; a route whose end
        // moved more than MinDestinationChange re-seeds Best without progress, and a passed corner
        // re-seeds it as progress.
        private static void Advance(ref Commitment c, float now, in Reading r)
        {
            if (r.Remaining < 0f) return;
            bool corner = c.Routed && PassedCorner(r.Body, c.RouteLeg, r.Leg, r.LegReach);
            if (!c.Routed || corner
                || Vector3.Distance(r.RouteEnd, c.RouteEnd) > BlacksiteArrivalPolicy.MinDestinationChange)
            {
                if (corner) c.LastProgressAt = now;
                c.Routed = true;
                c.Best = r.Remaining;
                c.RouteEnd = r.RouteEnd;
                c.RouteLeg = r.Leg;
                return;
            }
            if (r.Remaining > c.Best - DropMetres) return;
            c.Best = r.Remaining;
            c.LastProgressAt = now;
        }

        /// <summary>
        /// #1293 R2: the body stands within the measured leg's reach of its point (the leg's own
        /// advance radius, at least <see cref="DropMetres"/>) and the leg in force lies beyond that
        /// reach - the measured leg's end is a passed corner, so a chain of short legs (the
        /// ground-route follower, a smart-path step, a claim leg) progresses. A re-issue that
        /// abandons a leg mid-way, re-asks the same point, or flips back to a leg already left only
        /// re-seeds or measures. Horizontal, as the follower measures.
        /// </summary>
        internal static bool PassedCorner(Vector3 body, Vector3 oldLeg, Vector3 newLeg, float legReach)
        {
            float reach = Mathf.Max(DropMetres, legReach);
            return Flat(body, oldLeg) <= reach && Flat(body, newLeg) > reach;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = b.y;
            return Vector3.Distance(a, b);
        }
    }
}
