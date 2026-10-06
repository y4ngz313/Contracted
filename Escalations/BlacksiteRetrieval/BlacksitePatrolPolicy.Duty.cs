// #1219 E1-E3: the ship guard's duty as a state machine, and the pace an order declares.
//
// Patrol (Walk -> Dwell -> Look) -> Alert (scan in place for at most AlertScanSeconds, the dwell
// paused rather than reset) -> Respond (exact contact; a backup dispatch leaves the Guard duty and
// never reaches this machine). Stun and hazard avoidance own the body without being a response:
// the duty Yields, with the dwell paused. #1293 R2: a dwell ends by its own declared duration and a
// scan by AlertScanSeconds; no clock watches either. A leg is a travel commitment: the ledger
// judges it and the planner's stall answer (TryAnswerStalledPatrol) marks its waypoint dead.
//
// This half of BlacksitePatrolPolicy is Unity-free.
using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1219 E2: the pace an order is walked at, declared by the duty that issued it. Normal is the
    /// default so an order nobody paced walks the role speed. Only the named escalations (combat,
    /// withdrawal, a Jester) are Urgent; an alert without contact never is.
    /// </summary>
    internal enum BlacksiteOrderPace : byte
    {
        Normal = 0,
        Stroll = 1,
        Urgent = 2
    }

    /// <summary>#1219 E1: where a ship guard's duty stands after one <see cref="BlacksitePatrolPolicy.Step"/>.</summary>
    internal enum BlacksiteGuardPhase : byte
    {
        /// <summary>Walking to the current waypoint (a new leg, or back onto a paused dwell).</summary>
        Walk,
        /// <summary>On the waypoint, looking outward, the dwell counting down.</summary>
        Dwell,
        /// <summary>The dwell ended on this tick and the guard looked on to the next live, unheld waypoint.</summary>
        Look,
        /// <summary>Alerted without contact: scanning in place, the dwell paused, for at most one scan.</summary>
        Alert,
        /// <summary>Exact contact (combat) owns the body; the visit is kept and the next leg is Normal.</summary>
        Respond,
        /// <summary>A stun or a hazard owns the body; the visit is kept, nothing counts down.</summary>
        Yield
    }

    /// <summary>
    /// #1219 E1: one guard's duty state, the whole of it. A VISIT is one waypoint's leg and dwell;
    /// <see cref="BlacksitePatrolPolicy.Step"/>'s Look ends it and starts the next.
    /// </summary>
    internal struct BlacksiteGuardDuty
    {
        /// <summary>The waypoint the guard is walking to or dwelling on.</summary>
        internal int Index;
        internal BlacksiteGuardPhase Phase;
        /// <summary>Seconds of this visit's dwell still to stand; negative while no dwell has begun.</summary>
        internal float DwellLeft;
        /// <summary>When the current scan ends (Alert only).</summary>
        internal float AlertUntil;
        /// <summary>This visit's one scan is used: a sustained or repeated alert does not scan again.</summary>
        internal bool AlertSpent;
        /// <summary>An alert or a contact was seen this visit, so the next leg is Normal.</summary>
        internal bool Alerted;
        /// <summary>The pace of this visit's leg.</summary>
        internal BlacksiteOrderPace LegPace;
        /// <summary>The physical source of a new leg, retained until the body clears it.</summary>
        internal int SourceIndex;
        /// <summary>A paired exchange walks a staging point before the occupied destination is admitted.</summary>
        internal bool Exchanging;
        /// <summary>Diagnostic visit stamp.</summary>
        internal float DwellStartedAt;
        internal float LastTick;
        /// <summary>The spent dwell is waiting for a free destination or a paired exchange.</summary>
        internal bool HeldOut;

        internal bool Dwelling => DwellLeft >= 0f;

        internal static BlacksiteGuardDuty Begin(int index, float now) => new BlacksiteGuardDuty
        {
            Index = index, Phase = BlacksiteGuardPhase.Walk, DwellLeft = -1f,
            LegPace = BlacksiteOrderPace.Stroll, SourceIndex = -1, DwellStartedAt = -1f, LastTick = now
        };
    }

    internal static partial class BlacksitePatrolPolicy
    {
        /// <summary>The dwell at each waypoint, seeded per member and waypoint inside this band.</summary>
        internal const float MinDwellSeconds = 10f;
        internal const float MaxDwellSeconds = 15f;

        /// <summary>#1219 E1: the longest an alert without contact holds a guard scanning in place, once per visit.</summary>
        internal const float AlertScanSeconds = 6f;

        /// <summary>
        /// One planner pass: the mission's refresh cadence
        /// (<c>BlacksiteRetrievalMissionController.RefreshInterval</c> reads this value), which is the
        /// cadence <see cref="Step"/> runs on.
        /// </summary>
        internal const float PlannerTickSeconds = .25f;

        /// <summary>
        /// The dwell at waypoint <paramref name="waypoint"/> for a member seeded by
        /// <paramref name="seed"/>: deterministic, inside [<see cref="MinDwellSeconds"/>,
        /// <see cref="MaxDwellSeconds"/>], and different from one waypoint to the next so two guards
        /// never settle into the same rhythm.
        /// </summary>
        internal static float DwellSeconds(int seed, int waypoint)
        {
            unchecked
            {
                uint hash = (uint)seed * 2654435761u ^ (uint)(waypoint + 1) * 2246822519u;
                hash ^= hash >> 15; hash *= 2654435761u; hash ^= hash >> 13;
                float unit = (hash % 1001u) / 1000f;
                return MinDwellSeconds + unit * (MaxDwellSeconds - MinDwellSeconds);
            }
        }

        internal static bool IsLive(int index, int deadMask) => index >= 0 && index < 31 && (deadMask & (1 << index)) == 0;

        internal static bool IsHeld(int index, int heldMask) => index >= 0 && index < 31 && (heldMask & (1 << index)) != 0;

        internal static int LiveCount(int count, int deadMask)
        {
            int live = 0;
            for (int i = 0; i < count; i++) if (IsLive(i, deadMask)) live++;
            return live;
        }

        /// <summary>
        /// #1219 E3: the next waypoint after <paramref name="index"/> that is live and that no other
        /// guard holds or is walking to (<paramref name="heldMask"/>), or <paramref name="index"/>
        /// itself when there is none. A held source is never treated as free; a full loop needs
        /// <see cref="TryExchange"/> and physical departure before admitting the swapped goals.
        /// </summary>
        internal static int NextLive(int index, int count, int deadMask, int heldMask = 0)
        {
            for (int step = 1; step < count; step++)
            {
                int at = (index + step) % count;
                if (IsLive(at, deadMask) && !IsHeld(at, heldMask)) return at;
            }
            return index;
        }

        /// <summary>
        /// #1219 E3: where a new guard starts. With no other guard on the loop, the nearest live
        /// waypoint; otherwise half a loop on from the lowest waypoint another guard holds, walking
        /// forward past dead and held ones. Never a held or dead waypoint: -1 when none is left.
        /// </summary>
        internal static int ChooseStart(System.Collections.Generic.IReadOnlyList<float> ranges, int heldMask, int deadMask)
        {
            int count = ranges.Count;
            int held = -1;
            for (int i = 0; i < count && i < 31; i++) if (IsHeld(i, heldMask)) { held = i; break; }
            if (held >= 0)
            {
                int preferred = (held + count / 2) % count;
                for (int step = 0; step < count; step++)
                {
                    int at = (preferred + step) % count;
                    if (IsLive(at, deadMask) && !IsHeld(at, heldMask)) return at;
                }
                return -1;
            }
            int best = -1; float bestRange = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
                if (IsLive(i, deadMask) && ranges[i] < bestRange) { bestRange = ranges[i]; best = i; }
            return best;
        }

        /// <summary>
        /// #1219 E2: the pace the Guard duty declares on its order. A leg walks its visit's pace;
        /// an Alert hold, a response and a yield declare Normal (the body is either standing or
        /// owned by another behaviour, and only a named escalation is ever Urgent).
        /// </summary>
        internal static BlacksiteOrderPace OrderPace(in BlacksiteGuardDuty duty)
            => duty.Phase == BlacksiteGuardPhase.Walk || duty.Phase == BlacksiteGuardPhase.Look
               || duty.Phase == BlacksiteGuardPhase.Dwell
                ? duty.LegPace : BlacksiteOrderPace.Normal;

        /// <summary>
        /// #1219 E2: the pace a member walks this frame. A named escalation (combat, withdrawal, a
        /// Jester) is Urgent whatever the order says; otherwise the order's own pace while an order
        /// owns the body, and Normal under every other owner.
        /// </summary>
        internal static BlacksiteOrderPace ResolvePace(BlacksiteOrderPace orderPace, bool orderOwnsBody, bool escalation)
            => escalation ? BlacksiteOrderPace.Urgent : orderOwnsBody ? orderPace : BlacksiteOrderPace.Normal;

        /// <summary>
        /// One planner pass. A scan, response or yield pauses the dwell; a re-arrival resumes the
        /// same remainder.
        /// </summary>
        internal static BlacksiteGuardPhase Step(ref BlacksiteGuardDuty duty, int count, float now, int seed,
            bool arrived, bool alerted, bool contact, bool yielded, int deadMask, int heldMask)
        {
            float elapsed = Math.Max(0f, now - duty.LastTick);
            duty.LastTick = now;
            if (count <= 0) { duty = BlacksiteGuardDuty.Begin(0, now); return duty.Phase; }
            if (duty.Index < 0 || duty.Index >= count)
            {
                duty.Index = ((duty.Index % count) + count) % count;
                duty.DwellLeft = -1f;
            }
            if (arrived && !duty.Exchanging) duty.SourceIndex = -1;
            // The time since the last pass is dwell only if the guard spent it dwelling.
            if (duty.Phase == BlacksiteGuardPhase.Dwell && duty.Dwelling)
                duty.DwellLeft = Math.Max(0f, duty.DwellLeft - elapsed);

            if (contact)
            {
                duty.Phase = BlacksiteGuardPhase.Respond;
                duty.Alerted = true; duty.LegPace = BlacksiteOrderPace.Normal;
                return duty.Phase;
            }
            if (yielded)
            {
                duty.Phase = BlacksiteGuardPhase.Yield;
                return duty.Phase;
            }

            if (duty.Phase == BlacksiteGuardPhase.Alert)
            {
                // The scan runs to its end - the navigator's dwell stands the body that long - and a
                // sustained or repeated alert neither extends it nor opens a second one this visit.
                if (now < duty.AlertUntil) return duty.Phase;
                // The scan is over. Time past its end was spent standing on the waypoint, so a
                // dwelling guard counts it as dwell.
                if (duty.Dwelling && arrived && now > duty.AlertUntil)
                    duty.DwellLeft = Math.Max(0f, duty.DwellLeft - (now - duty.AlertUntil));
                duty.Phase = BlacksiteGuardPhase.Walk;
            }
            else if (alerted && !duty.AlertSpent && duty.SourceIndex < 0 && !duty.Exchanging)
            {
                duty.AlertSpent = true; duty.Alerted = true;
                duty.AlertUntil = now + AlertScanSeconds;
                // A scan mid-leg resumes the leg at Normal; a scan mid-dwell leaves the next leg to Look.
                if (!duty.Dwelling) duty.LegPace = BlacksiteOrderPace.Normal;
                duty.Phase = BlacksiteGuardPhase.Alert;
                return duty.Phase;
            }

            if (!arrived || duty.Exchanging)
            {
                // A new leg, or the walk back onto a dwell something carried the body off.
                duty.Phase = BlacksiteGuardPhase.Walk;
                return duty.Phase;
            }
            if (!duty.Dwelling)
            {
                duty.DwellLeft = DwellSeconds(seed, duty.Index);
                duty.DwellStartedAt = now;
            }
            if (duty.DwellLeft > 0f)
            {
                duty.Phase = BlacksiteGuardPhase.Dwell;
                return duty.Phase;
            }
            return Look(ref duty, count, deadMask, heldMask);
        }

        private static BlacksiteGuardPhase Look(ref BlacksiteGuardDuty duty, int count, int deadMask, int heldMask)
        {
            int next = NextLive(duty.Index, count, deadMask, heldMask);
            if (next == duty.Index)
            {
                // Keep the spent dwell spent: retry next pass, or exchange with the other ready guard.
                duty.HeldOut = true;
                duty.Phase = BlacksiteGuardPhase.Dwell;
                return duty.Phase;
            }
            BeginLeg(ref duty, next);
            return duty.Phase;
        }

        private static void BeginLeg(ref BlacksiteGuardDuty duty, int next)
        {
            duty.SourceIndex = duty.Index;
            duty.HeldOut = false;
            duty.Index = next;
            duty.DwellLeft = -1f;
            duty.AlertSpent = false;
            duty.LegPace = duty.Alerted ? BlacksiteOrderPace.Normal : BlacksiteOrderPace.Stroll;
            duty.Alerted = false;
            duty.Phase = BlacksiteGuardPhase.Look;
            duty.DwellStartedAt = -1f;
        }

        /// <summary>
        /// Reserve both incoming destinations atomically, retaining both physical sources. The
        /// planner first walks proved staging points; neither occupied destination is yet issued.
        /// </summary>
        internal static bool TryExchange(ref BlacksiteGuardDuty first, ref BlacksiteGuardDuty second, int deadMask)
        {
            if ((!first.HeldOut && !second.HeldOut) || first.Phase != BlacksiteGuardPhase.Dwell
                || second.Phase != BlacksiteGuardPhase.Dwell || first.Exchanging || second.Exchanging
                || first.Index == second.Index || !IsLive(first.Index, deadMask) || !IsLive(second.Index, deadMask))
                return false;
            int source = first.Index;
            BeginLeg(ref first, second.Index);
            BeginLeg(ref second, source);
            first.Exchanging = second.Exchanging = true;
            return true;
        }

        /// <summary>Only a measured body departure releases a physical source.</summary>
        internal static void ObserveDeparture(ref BlacksiteGuardDuty duty, bool sourceVacated, int occupiedMask)
        {
            if (sourceVacated) duty.SourceIndex = -1;
            if (duty.SourceIndex < 0 && !IsHeld(duty.Index, occupiedMask)) duty.Exchanging = false;
        }
    }
}
