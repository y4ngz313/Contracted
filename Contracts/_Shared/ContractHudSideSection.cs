using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>#1195. Where the card's OPTIONAL section is in its life.</summary>
    internal enum ContractHudSideStage
    {
        /// <summary>No revealed side objective: nothing drawn, no height.</summary>
        Hidden = 0,
        /// <summary>Progress or the claim step is on the card.</summary>
        Shown = 1,
        /// <summary>"Reward recovered" holds, dimmed, for <see cref="ContractHudSideSection.RecoveredHoldSeconds"/>.</summary>
        Recovered = 2,
        /// <summary>The section fades out in place for <see cref="ContractHudSideSection.FadeSeconds"/>.</summary>
        Fading = 3,
        /// <summary>Done for this objective: no height until a different phase is published.</summary>
        Collapsed = 4,
    }

    /// <summary>
    /// #1195. The presentation policy of the card's OPTIONAL section, fed from Contract S
    /// (<c>SideObjectiveState</c>) each time its revision moves. Unity-free and Netcode-free so
    /// ContractHudPresentationRegression links it; ContractHUDManager.SideObjective.cs draws it.
    ///
    /// <para>Progress and the claim step show as one card row. Recovered shows only as a transition
    /// from a phase this card drew: it holds, dims out and collapses. A section first observed
    /// already recovered (a late join, a card that was hidden and reset) never shows, so nobody is
    /// shown a finished objective they never saw. The published row reaches the card and the #1196
    /// cue tracker as it is: side rows never pass through the #880 completion pins, because the
    /// count can fall.</para>
    /// </summary>
    internal sealed class ContractHudSideSection
    {
        /// <summary>How long "Reward recovered" stays on the card before it dims out.</summary>
        internal const float RecoveredHoldSeconds = 3f;
        /// <summary>How long the recovered section takes to fade before its height is removed.</summary>
        internal const float FadeSeconds = .35f;
        internal const string HeaderPrefix = "OPTIONAL";

        private static readonly MoonContractObjective[] NoRows = new MoonContractObjective[0];
        private readonly MoonContractObjective[] _row = new MoonContractObjective[1];
        private float _recoveredAt;

        internal ContractHudSideStage Stage { get; private set; }
        internal SideObjectivePhase Phase { get; private set; }
        internal string Title { get; private set; } = string.Empty;
        internal MoonContractObjective Row => _row[0];

        /// <summary>Needs a per-frame look: anything but Hidden or Collapsed.</summary>
        internal bool Active => Stage != ContractHudSideStage.Hidden && Stage != ContractHudSideStage.Collapsed;

        /// <summary>Occupies height on the card.</summary>
        internal bool Reserved => Stage == ContractHudSideStage.Shown
            || Stage == ContractHudSideStage.Recovered
            || Stage == ContractHudSideStage.Fading;

        /// <summary>Opens a card with no contract, and keeps one open. A fading section does not:
        /// a card it holds alone closes as a whole instead.</summary>
        internal bool HoldsCard => Stage == ContractHudSideStage.Shown || Stage == ContractHudSideStage.Recovered;

        /// <summary>The recovered section is drawn in the dim tier.</summary>
        internal bool Dimmed => Stage == ContractHudSideStage.Recovered || Stage == ContractHudSideStage.Fading;

        /// <summary>What the section's row-cue tracker observes: the one published row while the
        /// section takes height, otherwise nothing.</summary>
        internal IReadOnlyList<MoonContractObjective> CueRows => Reserved ? _row : NoRows;

        /// <summary>Takes a new Contract S reading.</summary>
        internal void Observe(SideObjectivePhase phase, string title, in MoonContractObjective row, float now)
        {
            Phase = phase;
            bool hidden = phase == SideObjectivePhase.Hidden;
            Title = hidden ? string.Empty : title ?? string.Empty;
            _row[0] = hidden ? default : row;
            switch (phase)
            {
                case SideObjectivePhase.Progress:
                case SideObjectivePhase.ClaimReward:
                    Stage = ContractHudSideStage.Shown;
                    break;
                case SideObjectivePhase.Recovered:
                    if (Stage == ContractHudSideStage.Shown)
                    {
                        Stage = ContractHudSideStage.Recovered;
                        _recoveredAt = now;
                    }
                    else if (Stage == ContractHudSideStage.Hidden)
                    {
                        Stage = ContractHudSideStage.Collapsed;
                    }
                    break;
                default:
                    Stage = ContractHudSideStage.Hidden;
                    break;
            }
        }

        /// <summary>Advances the recovered hold, fade and collapse. True when the stage changed.</summary>
        internal bool Tick(float now)
        {
            if (Stage == ContractHudSideStage.Recovered && now - _recoveredAt >= RecoveredHoldSeconds)
            {
                Stage = ContractHudSideStage.Fading;
                return true;
            }
            if (Stage == ContractHudSideStage.Fading && now - _recoveredAt >= RecoveredHoldSeconds + FadeSeconds)
            {
                Stage = ContractHudSideStage.Collapsed;
                return true;
            }
            return false;
        }

        /// <summary>The section's opacity: 1 while it takes height, falling to 0 across the fade.</summary>
        internal float Alpha(float now)
        {
            if (Stage == ContractHudSideStage.Fading)
            {
                float t = (now - _recoveredAt - RecoveredHoldSeconds) / FadeSeconds;
                return t <= 0f ? 1f : t >= 1f ? 0f : 1f - t;
            }
            return Reserved ? 1f : 0f;
        }

        /// <summary>Forgets everything; the next reading is a first sighting.</summary>
        internal void Reset()
        {
            Stage = ContractHudSideStage.Hidden;
            Phase = SideObjectivePhase.Hidden;
            Title = string.Empty;
            _row[0] = default;
        }

        /// <summary>The optional section label and, when present, the uppercase source payload.</summary>
        internal static string Header(string title) => string.IsNullOrEmpty(title)
            ? HeaderPrefix : HeaderPrefix + " \u00B7 " + title.ToUpperInvariant();

        /// <summary>
        /// The one rule both the power-fault tick and the OPTIONAL tick apply to a card opened with no
        /// contract, so whichever runs first on a frame decides the same way: kept while either
        /// section needs it, faded out whole while the section is fading, closed at once otherwise.
        /// </summary>
        internal static ContractHudSectionsOnlyClose CloseSectionsOnlyCard(bool powerFaultVisible, ContractHudSideStage stage)
        {
            if (powerFaultVisible || stage == ContractHudSideStage.Shown || stage == ContractHudSideStage.Recovered)
                return ContractHudSectionsOnlyClose.Keep;
            return stage == ContractHudSideStage.Fading ? ContractHudSectionsOnlyClose.Fade : ContractHudSectionsOnlyClose.Immediate;
        }
    }

    /// <summary>
    /// Tracks an actual host reveal separately from a first already-revealed reading. Card hides
    /// do not reset this per-round history; a network reset (no objective) does.
    /// </summary>
    internal sealed class ContractHudSideRevealPolicy
    {
        private readonly List<SideObjectiveSnapshot> _seen = new List<SideObjectiveSnapshot>(2);
        private SideObjectiveSnapshot _armed;

        /// <summary>True only for the first hidden-to-visible transition of this source key.</summary>
        internal bool Observe(in SideObjectiveSnapshot snapshot, SideObjectivePhase phase)
        {
            if (!snapshot.HasObjective)
            {
                _armed = SideObjectiveSnapshot.None;
                _seen.Clear();
                return false;
            }

            if (phase == SideObjectivePhase.Hidden)
            {
                _armed = snapshot.Revealed ? SideObjectiveSnapshot.None : snapshot;
                return false;
            }

            bool seen = Seen(snapshot);
            bool firstReveal = (phase == SideObjectivePhase.Progress || phase == SideObjectivePhase.ClaimReward)
                && _armed.HasObjective && _armed.SameObjective(snapshot) && !seen;
            _armed = SideObjectiveSnapshot.None;
            if (!seen)
                _seen.Add(snapshot);
            return firstReveal;
        }

        private bool Seen(in SideObjectiveSnapshot snapshot)
        {
            for (int i = 0; i < _seen.Count; i++)
                if (_seen[i].SameObjective(snapshot))
                    return true;
            return false;
        }
    }

    /// <summary>#1195. What happens to a card that only its in-card sections opened.</summary>
    internal enum ContractHudSectionsOnlyClose
    {
        Keep = 0,
        /// <summary>Close with the card's fade-out.</summary>
        Fade = 1,
        /// <summary>Close at once: nothing on it is on its way out.</summary>
        Immediate = 2,
    }

    /// <summary>
    /// #1195. Stacking of the card's in-card sections, bottom up: the #883 power-fault row keeps
    /// the card's bottom slot, the OPTIONAL section sits directly above it, and the main objective body
    /// sits above both. Each section draws its top divider only when something is above it.
    /// </summary>
    internal static class ContractHudSideSectionLayout
    {
        /// <summary>Bottom edge of the OPTIONAL section, measured up from the card's bottom edge.</summary>
        internal static float SideBottom(float bottomPadding, float powerFaultHeight)
            => bottomPadding + Math.Max(0f, powerFaultHeight);

        /// <summary>Bottom edge of the main objective body.</summary>
        internal static float BodyBottom(float bottomPadding, float powerFaultHeight, float sideHeight)
            => SideBottom(bottomPadding, powerFaultHeight) + Math.Max(0f, sideHeight);

        /// <summary>Height of a card opened by its sections alone, with no contract.</summary>
        internal static float SectionsOnlyHeight(float bottomPadding, float powerFaultHeight, float sideHeight)
            => BodyBottom(bottomPadding, powerFaultHeight, sideHeight);

        internal static bool SideDivider(bool mainObjectiveShown) => mainObjectiveShown;

        internal static bool PowerFaultDivider(bool mainObjectiveShown, bool sideShown) => mainObjectiveShown || sideShown;
    }
}
