using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>#1293 R3: why a member decides - a fact that CHANGED for it; <c>[UAT][Brain]</c> prints it.</summary>
    internal enum BlacksiteDecisionCause : byte
    {
        None,
        /// <summary>The member's first initialisation.</summary>
        Spawned,
        /// <summary>The planner issued, changed or released this member's order.</summary>
        Order,
        /// <summary>A commitment ended: a motion verdict, arrival, a dwell, an interaction task, the weapon becoming ready.</summary>
        Outcome,
        /// <summary>The committed action's own deadline passed.</summary>
        Wake,
        /// <summary>The squad table changed (new, reacquired, expired, reclassified or lost-sight threat row).</summary>
        World,
        /// <summary>A crossing or seat began, ended or handed back.</summary>
        Crossing,
        /// <summary>A squadmate died.</summary>
        Casualty,
        /// <summary>Witness custody moved.</summary>
        Custody,
        /// <summary>A manifest record this member holds changed phase.</summary>
        Phase,
        /// <summary>The member was attacked, alerted or stimulated.</summary>
        Stimulus,
        /// <summary>The member changed worlds through an entrance.</summary>
        Transfer,
        /// <summary>A motor or an exclusive branch owned the body; the first frame back decides.</summary>
        Exclusive
    }

    /// <summary>#1293 R3: the member's published intent - its last committed decision, why and since when. Host only.</summary>
    internal readonly struct BlacksiteMemberIntent
    {
        internal readonly BlacksiteBehaviour Intent;
        internal readonly string Reason;
        internal readonly float Since;

        internal BlacksiteMemberIntent(BlacksiteBehaviour intent, string reason, float since)
        {
            Intent = intent;
            Reason = reason;
            Since = since;
        }
    }

    /// <summary>
    /// #1293 R3: which dirty members decide on this frame. One queue per mission, host only, cleared
    /// with the mission; Unity-free. Each frame reserves up to
    /// <see cref="BlacksiteDeploySlice.MembersPerFrame"/> slots for the oldest members refused on the
    /// previous frame, then admits others while capacity remains, each at most once per frame. A
    /// refused member runs its continuation and asks again.
    /// </summary>
    internal sealed class BlacksiteDecisionQueue
    {
        // Marking order; a member appears once and keeps its place when marked again.
        private readonly List<ulong> _pending = new List<ulong>();
        private readonly List<ulong> _reserved = new List<ulong>();
        private readonly List<ulong> _refused = new List<ulong>();
        private readonly List<ulong> _admitted = new List<ulong>();
        private int _frame = int.MinValue;
        private int _free;

        internal int Pending => _pending.Count;

        internal void Mark(ulong member)
        {
            if (!_pending.Contains(member)) _pending.Add(member);
        }

        internal bool Admit(ulong member, int frame)
        {
            if (frame != _frame) BeginFrame(frame);
            if (_admitted.Contains(member)) return false;
            int reserved = _reserved.IndexOf(member);
            if (reserved >= 0) _reserved.RemoveAt(reserved);
            else if (_free > 0) _free--;
            else
            {
                Mark(member);
                if (!_refused.Contains(member)) _refused.Add(member);
                return false;
            }
            _admitted.Add(member);
            _pending.Remove(member);
            return true;
        }

        internal void Remove(ulong member)
        {
            _pending.Remove(member);
            _reserved.Remove(member);
            _refused.Remove(member);
        }

        internal void Clear()
        {
            _pending.Clear();
            _reserved.Clear();
            _refused.Clear();
            _admitted.Clear();
            _frame = int.MinValue;
            _free = 0;
        }

        // The previous frame's refusals become this frame's reservations, oldest mark first.
        private void BeginFrame(int frame)
        {
            _frame = frame;
            _reserved.Clear();
            for (int i = 0; i < _pending.Count && _reserved.Count < BlacksiteDeploySlice.MembersPerFrame; i++)
                if (_refused.Contains(_pending[i])) _reserved.Add(_pending[i]);
            _refused.Clear();
            _admitted.Clear();
            _free = BlacksiteDeploySlice.MembersPerFrame - _reserved.Count;
        }
    }
}
