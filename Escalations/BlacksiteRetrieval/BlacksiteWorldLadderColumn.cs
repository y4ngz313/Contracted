using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S5.1 (#873): how many bodies one authored world ladder carries at once, and which of
    // them may back down when a climb is refused.
    //
    // The 2026-09-09 run put one member on an 8.51 m ladder at a time because
    // BlacksiteNavigator.HandleWorldLadder called BlacksitePassageQueue.Request and Release but
    // never Advance, so the admitted head held from mount to dismount. Four raiders spent 33.85 s
    // of squad wall time and about 40 idle member-seconds frozen at the foot for 28 s of climbing.
    //
    // The queue still orders the waiting members (FIFO, with its eviction budget); this type owns
    // occupancy. It is
    // deliberately free of UnityEngine so the headless regression can pin the admission table, and
    // it holds no reference to a soldier, a collider or a transform.
    internal sealed class BlacksiteWorldLadderColumn
    {
        /// <summary>
        /// Minimum vertical separation between two bodies on one column, in metres.
        ///
        /// The collision probe is a capsule of radius 0.32 m and height 1.96 m centred 1.14 m above
        /// the foot (<c>BlacksitePhysicalStep.cs:32</c>), and <c>CanOccupy</c> overlaps spheres at
        /// foot +0.48 and +1.80 (<c>:51</c>), so the occupied column runs from foot+0.16 to
        /// foot+2.12: 1.96 m tall. The minimum non-overlapping vertical separation is therefore
        /// 1.96 m. 2.4 m leaves 0.44 m of clearance and costs a 2.0 s mount interval at
        /// <c>BlacksiteLandingGearPolicy.ClimbSpeed</c> 1.2 m/s.
        /// </summary>
        internal const float HeadwayMetres = 2.4f;

        /// <summary>
        /// The most bodies any column carries, whatever its rise. A squad is six members and the
        /// deployment allocates at most four raiders to one route
        /// (<c>BlacksiteDeploymentPolicy</c>), so a fifth simultaneous climber cannot occur
        /// on a squad ladder; the ceiling keeps a very tall shaft from queueing the whole roster
        /// onto one column where nothing can pass a stuck body.
        /// </summary>
        internal const int OccupantCeiling = 4;

        /// <summary>
        /// The occupied body column itself, in metres: foot+0.16 to foot+2.12 from the capsule at
        /// <c>BlacksitePhysicalStep.cs:32</c> and <c>:51</c>. Admission spaces bodies at
        /// <see cref="HeadwayMetres"/>; a follower closing on a stalled climber is governed by
        /// the motor's <c>canEnter</c> predicate, which is this column's <see cref="SpaceFree"/>.
        /// </summary>
        internal const float BodyColumnMetres = 1.96f;

        /// <summary>
        /// An occupant that stops reporting for this long has died, been captured, been stunned out
        /// of the traversal or simply left. Same value as <c>BlacksitePassageQueue</c>'s own stale
        /// window, so the queue and the column forget an absent member on the same schedule.
        /// </summary>
        private const float StaleSeconds = 2f;

        /// <summary>
        /// How close to the far end of the column counts as having left it. The motor advances a
        /// waypoint within 0.015 m (<c>BlacksiteWorldLadderMotor.cs:35</c>), so 0.05 m is above
        /// that tolerance and far below any separation the headway cares about.
        /// </summary>
        private const float EndMetres = .05f;

        /// <summary>
        /// Slack on the headway comparison, in metres. World-ladder points are world coordinates
        /// hundreds of metres from the origin, so a separation authored as exactly one headway
        /// lands a few ten-thousandths either side of it in single precision and a strict compare
        /// refuses a body that is legally spaced. One millimetre is 0.04 % of the headway and the
        /// same slack <c>BlacksitePassageQueue</c> already applies to its own headway compare.
        /// </summary>
        private const float HeadwaySlack = .001f;

        private struct Occupant
        {
            internal ulong Member;
            internal bool Up;
            internal float Y;
            internal float SeenAt;
        }

        private readonly List<Occupant> _occupants = new List<Occupant>();
        private bool _hasSpan;
        private float _bottomY, _topY;

        internal float RequiredHeadway => HeadwayMetres;

        /// <summary>Vertical metres between the two authored ends, 0 until the ladder validates.</summary>
        internal float Rise => _hasSpan ? Math.Abs(_topY - _bottomY) : 0f;

        /// <summary>
        /// Never more bodies than clear spans: <c>clamp(floor(rise / headway), 1, 4)</c>. A 2.5 m
        /// interior ladder therefore stays single occupancy, which is the answer to open question 3
        /// of <c>findings-ladders.md</c>: below one headway single occupancy is correct and the
        /// headway is not shrunk to manufacture a second slot.
        /// </summary>
        internal int MaxOccupants
        {
            get
            {
                int spans = (int)Math.Floor(Rise / HeadwayMetres);
                return spans < 1 ? 1 : spans > OccupantCeiling ? OccupantCeiling : spans;
            }
        }

        /// <summary>Every body this column owns, including one standing on the dismount.</summary>
        internal int Occupants => _occupants.Count;

        /// <summary>
        /// The bodies still between the two ends. A member that has reached the far end of the rise
        /// is on the dismount, not on a span, so it no longer consumes a slot: without this the
        /// last of four raiders waits for the whole lateral dismount leg and the mount interval
        /// exceeds acceptance criterion 6 by about 0.1 s.
        /// </summary>
        internal int SpanOccupants
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _occupants.Count; i++) if (!LeftTheSpans(_occupants[i])) count++;
                return count;
            }
        }

        /// <summary>The authored ends of the vertical run, bottom first. Order-independent.</summary>
        internal void SetSpan(float bottomY, float topY)
        {
            _hasSpan = true;
            _bottomY = Math.Min(bottomY, topY);
            _topY = Math.Max(bottomY, topY);
        }

        /// <summary>
        /// May this member mount now? All of these must hold: no occupant travels the opposite
        /// direction; the occupant nearest the mount along this direction is at least
        /// <see cref="HeadwayMetres"/> beyond the mount; there is a free span. The vanilla
        /// <c>Occupied</c> (a player on the trigger) is tested by the caller and is unchanged.
        /// Re-admitting a member already on the column refreshes it and succeeds, so the caller may
        /// call this every frame of its approach.
        /// </summary>
        internal bool TryAdmit(ulong member, bool up, float footY, float now, out string refusal)
        {
            Prune(now);
            refusal = null;
            int index = IndexOf(member);
            if (index >= 0)
            {
                Occupant held = _occupants[index];
                held.Up = up; held.SeenAt = now;
                _occupants[index] = held;
                return true;
            }
            for (int i = 0; i < _occupants.Count; i++)
                if (_occupants[i].Up != up)
                {
                    refusal = "column held by " + _occupants.Count + " climber(s) travelling "
                        + (_occupants[i].Up ? "up" : "down");
                    return false;
                }
            int spans = SpanOccupants;
            if (spans >= MaxOccupants)
            {
                refusal = "column full at " + spans + " of " + MaxOccupants + " spans over "
                    + Rise.ToString("0.00") + "m of rise";
                return false;
            }
            if (TryLead(up, out _, out float leadY))
            {
                float headway = up ? leadY - footY : footY - leadY;
                if (headway < HeadwayMetres - HeadwaySlack)
                {
                    refusal = "lead climber is " + headway.ToString("0.00") + "m from the mount and "
                        + HeadwayMetres.ToString("0.00") + "m is required";
                    return false;
                }
            }
            _occupants.Add(new Occupant { Member = member, Up = up, Y = footY, SeenAt = now });
            return true;
        }

        /// <summary>Per-frame position of a mounted occupant. Ignored for anyone else.</summary>
        internal void Report(ulong member, float y, float now)
        {
            int index = IndexOf(member);
            if (index < 0) return;
            Occupant held = _occupants[index];
            held.Y = y; held.SeenAt = now;
            _occupants[index] = held;
        }

        /// <summary>
        /// Death, stun, capture and every ordinary dismount arrive here. Removal keeps the order of
        /// the survivors, so an unwinding stack is not reshuffled by a casualty in the middle of it.
        /// </summary>
        internal void Release(ulong member)
        {
            int index = IndexOf(member);
            if (index >= 0) _occupants.RemoveAt(index);
        }

        /// <summary>The occupant nearest the mount along this direction: lowest up, highest down.</summary>
        internal bool TryLead(bool up, out ulong member, out float y)
        {
            member = 0UL; y = 0f;
            bool found = false;
            for (int i = 0; i < _occupants.Count; i++)
            {
                Occupant candidate = _occupants[i];
                if (found && (up ? candidate.Y >= y : candidate.Y <= y)) continue;
                member = candidate.Member; y = candidate.Y; found = true;
            }
            return found;
        }

        /// <summary>
        /// The nearest body ahead of this member along the column, which is the separation the
        /// <c>[UAT][WorldLadder]</c> lines report as <c>headwayToLead</c>.
        /// </summary>
        internal bool TryLeadAhead(ulong member, bool up, float footY, out ulong lead, out float headway)
        {
            lead = 0UL; headway = 0f;
            bool found = false;
            for (int i = 0; i < _occupants.Count; i++)
            {
                Occupant candidate = _occupants[i];
                if (candidate.Member == member) continue;
                float gap = up ? candidate.Y - footY : footY - candidate.Y;
                if (gap <= 0f) continue;
                if (found && gap >= headway) continue;
                lead = candidate.Member; headway = gap; found = true;
            }
            return found;
        }

        /// <summary>
        /// Only the occupant nearest the mount may back down: the lowest of an ascending stack, the
        /// highest of a descending one. A higher blocked occupant holds in place and retreats once
        /// it becomes the lowest, so the stack unwinds bottom-first instead of driving a retreating
        /// body through the climber below it. A member this column does not own is unconstrained.
        /// </summary>
        internal bool CanRetreat(ulong member)
        {
            int index = IndexOf(member);
            if (index < 0) return true;
            return TryLead(_occupants[index].Up, out ulong lead, out _) && lead == member;
        }

        internal bool IsOccupant(ulong member) => IndexOf(member) >= 0;

        /// <summary>
        /// Would this member's next height come inside another occupant's body column? Admission
        /// spaces bodies at the headway, but a follower closing on a climber that has stalled needs
        /// a per-step answer at the capsule's own height. A member this column does not own is
        /// unconstrained.
        /// </summary>
        internal bool SpaceFree(ulong member, float y)
        {
            for (int i = 0; i < _occupants.Count; i++)
            {
                if (_occupants[i].Member == member) continue;
                if (Math.Abs(_occupants[i].Y - y) < BodyColumnMetres) return false;
            }
            return true;
        }

        private bool LeftTheSpans(Occupant occupant)
            => _hasSpan && (occupant.Up ? occupant.Y >= _topY - EndMetres : occupant.Y <= _bottomY + EndMetres);

        private int IndexOf(ulong member)
        {
            for (int i = 0; i < _occupants.Count; i++) if (_occupants[i].Member == member) return i;
            return -1;
        }

        private void Prune(float now)
        {
            for (int i = _occupants.Count - 1; i >= 0; i--)
                if (now - _occupants[i].SeenAt > StaleSeconds) _occupants.RemoveAt(i);
        }
    }
}
