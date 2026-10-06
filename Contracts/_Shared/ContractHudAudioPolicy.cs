using Y4NGZCompany.Escalations._Shared;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>#1343. Client-local HUD one-shots, separate from the card's looping typewriter.</summary>
    internal enum ContractHudAudioKind
    {
        Arrival,
        ObjectiveAdvance,
        ObjectiveComplete,
        EscalationWarning,
        EscalationLethal,
        EscalationCalm,
        OptionalReveal,
        PityBlink
    }

    /// <summary>Vanilla HUDManager clip slot; the Unity adapter resolves its field and guarded array index.</summary>
    internal enum ContractHudAudioClip
    {
        None,
        Scan,
        ScrapTally,
        ScrapFinish,
        WarningFirst,
        TipFirst
    }

    /// <summary>Unity-free cue routing, mix and cooldown policy. The runtime resolves actual clips.</summary>
    internal static class ContractHudAudioPolicy
    {
        internal const double PerKindCooldownSeconds = .5;
        internal const double GlobalCooldownSeconds = .15;
        internal const int KindCount = 8;

        internal static ContractHudAudioKind ForEscalation(EscalationAnnouncementTone tone) => tone switch
        {
            EscalationAnnouncementTone.Lethal => ContractHudAudioKind.EscalationLethal,
            EscalationAnnouncementTone.Calm => ContractHudAudioKind.EscalationCalm,
            _ => ContractHudAudioKind.EscalationWarning
        };

        internal static ContractHudAudioClip Clip(ContractHudAudioKind kind) => kind switch
        {
            ContractHudAudioKind.Arrival => ContractHudAudioClip.Scan,
            ContractHudAudioKind.ObjectiveAdvance or ContractHudAudioKind.OptionalReveal => ContractHudAudioClip.ScrapTally,
            ContractHudAudioKind.ObjectiveComplete => ContractHudAudioClip.ScrapFinish,
            ContractHudAudioKind.EscalationWarning or ContractHudAudioKind.EscalationLethal => ContractHudAudioClip.WarningFirst,
            ContractHudAudioKind.EscalationCalm or ContractHudAudioKind.PityBlink => ContractHudAudioClip.TipFirst,
            _ => ContractHudAudioClip.None
        };

        internal static float Gain(ContractHudAudioKind kind) => kind switch
        {
            ContractHudAudioKind.Arrival => .30f,
            ContractHudAudioKind.ObjectiveAdvance => .25f,
            ContractHudAudioKind.ObjectiveComplete => .40f,
            ContractHudAudioKind.EscalationWarning => .55f,
            ContractHudAudioKind.EscalationLethal => .55f,
            ContractHudAudioKind.EscalationCalm => .30f,
            ContractHudAudioKind.OptionalReveal => .20f,
            ContractHudAudioKind.PityBlink => .18f,
            _ => 0f
        };

        internal static float EffectiveVolume(ContractHudAudioKind kind, float master)
        {
            if (!(master > 0f))
                return 0f;
            return Gain(kind) * (master < 1f ? master : 1f);
        }

        /// <summary>One-shot events belong to the visible transition, never to hidden history.</summary>
        internal static bool TransitionAudible(bool visibleOnPoll, bool hiddenSinceLastPoll)
            => visibleOnPoll && !hiddenSinceLastPoll;
    }

    /// <summary>Per-local-peer one-shot timing; rejected/inaudible cues never move either window.</summary>
    internal sealed class ContractHudAudioCooldown
    {
        private readonly double[] _lastByKind = new double[ContractHudAudioPolicy.KindCount];
        private double _lastGlobal = double.NegativeInfinity;

        internal ContractHudAudioCooldown()
        {
            for (int i = 0; i < _lastByKind.Length; i++)
                _lastByKind[i] = double.NegativeInfinity;
        }

        /// <summary>Consumes the cue only when visible, enabled, nonzero and equipped with a live
        /// source and clip. Callers decide eligibility before this; no audio is queued for later.</summary>
        internal bool TryBegin(ContractHudAudioKind kind, double now, bool audible)
        {
            int index = (int)kind;
            if (!audible || (uint)index >= (uint)_lastByKind.Length || !(now >= 0d)
                || now - _lastGlobal < ContractHudAudioPolicy.GlobalCooldownSeconds
                || now - _lastByKind[index] < ContractHudAudioPolicy.PerKindCooldownSeconds)
                return false;

            _lastGlobal = now;
            _lastByKind[index] = now;
            return true;
        }
    }
}
