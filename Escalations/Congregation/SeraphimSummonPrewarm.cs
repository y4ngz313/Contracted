using System;

namespace Y4NGZCompany.Escalations.Congregation
{
    /// <summary>The host's Phase 3 Seraphim pre-warm stages (#1336), in the order they run.</summary>
    internal enum SeraphimPrewarmStage
    {
        None,
        Instantiate,
        Build,
        Effects,
        Ready,
    }

    /// <summary>Host stage machine for the Seraphim pre-warm (#1336). The summon frame cost 81 ms:
    /// instantiate, component build and rise effects all at once. Phase 3 lasts 15-30 s first, so
    /// that work runs there, one stage per frame. No Unity types, so the regression suite drives it;
    /// the escalation runs each stage's work.</summary>
    internal sealed class SeraphimSummonPrewarm
    {
        /// <summary>The last stage handed out by <see cref="NextStage"/>: None until the first one,
        /// Ready once every stage ran.</summary>
        internal SeraphimPrewarmStage Stage { get; private set; }

        internal bool IsBegun { get; private set; }

        /// <summary>Begun and not yet Ready: <see cref="NextStage"/> still has work to hand out.</summary>
        internal bool IsPending => IsBegun && Stage != SeraphimPrewarmStage.Ready;

        /// <summary>Starts the stages. Already begun: a no-op, so re-entering Phase 3 never restarts
        /// the pre-warm or builds a second body. True when this call started it.</summary>
        internal bool Begin()
        {
            if (IsBegun)
                return false;

            IsBegun = true;
            Stage = SeraphimPrewarmStage.None;
            return true;
        }

        /// <summary>Advances exactly one stage and returns it: Instantiate, Build, Effects, then Ready,
        /// which is terminal. None when not begun.</summary>
        internal SeraphimPrewarmStage NextStage()
        {
            if (!IsBegun)
                return SeraphimPrewarmStage.None;

            if (Stage != SeraphimPrewarmStage.Ready)
                Stage++;
            return Stage;
        }

        /// <summary>Drops the pre-warm; a later <see cref="Begin"/> starts over.</summary>
        internal void Abort()
        {
            IsBegun = false;
            Stage = SeraphimPrewarmStage.None;
        }

        /// <summary>The summon takes what the pre-warm reached, ready or not, and resets it.</summary>
        internal SeraphimPrewarmStage Consume()
        {
            SeraphimPrewarmStage reached = Stage;
            Abort();
            return reached;
        }
    }

    /// <summary>The rise effects one peer creates when the Seraphim breaks the ground (#1336).</summary>
    internal enum SeraphimRiseEffectStage
    {
        None,
        HolyEffect,
        RiseDust,
        BurstLight,
    }

    /// <summary>The rise effects a peer still owes, handed out one per frame (#1336) so the
    /// frame that starts the rise - the host's summon, a client's spawn - does not also build them.
    /// Nothing is handed out in the frame that queued them.</summary>
    internal sealed class RiseEffectSchedule
    {
        internal const int StageCount = 3;

        private int _next = StageCount;
        private int _lastFrame;

        /// <summary>Stages queued and not yet handed out.</summary>
        internal int Remaining => StageCount - _next;

        /// <summary>Queues every rise effect in order, replacing whatever was still queued.</summary>
        internal void Queue(int frame)
        {
            _next = 0;
            _lastFrame = frame;
        }

        /// <summary>The next stage, at most one per frame: None when empty, in the queuing frame or
        /// when this frame already took one.</summary>
        internal SeraphimRiseEffectStage TakeNext(int frame)
        {
            if (_next >= StageCount || frame == _lastFrame)
                return SeraphimRiseEffectStage.None;

            _lastFrame = frame;
            return StageAt(_next++);
        }

        /// <summary>Hands out every remaining stage at once, in order, into <paramref name="into"/>
        /// (sized <see cref="StageCount"/>; caller-owned so nothing allocates) and empties the
        /// schedule. Returns how many were written.</summary>
        internal int FlushRemaining(SeraphimRiseEffectStage[] into)
        {
            int count = 0;
            while (_next < StageCount && into != null && count < into.Length)
                into[count++] = StageAt(_next++);
            _next = StageCount;
            return count;
        }

        private static SeraphimRiseEffectStage StageAt(int index)
        {
            return (SeraphimRiseEffectStage)(index + 1);
        }
    }
}
