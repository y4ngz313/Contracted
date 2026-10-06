using System.Collections.Generic;
using UnityEngine;
using Y4NGZCompany.Experience.UITheme;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1196. Row feedback on the card body: the live poll feeds each snapshot to the row-cue
    /// renderer, the renderer draws the sweep, counter roll and key-word pulse on the body text,
    /// and the completion or failure glow is applied to the card frame here. All of it rides the
    /// card's own transforms, so it follows the GameplayHudMotion offset and the theme with the
    /// rest of the card, and it is idle (one compare per frame) whenever no cue runs.
    /// </summary>
    internal partial class ContractHUDManager
    {
        private ContractHudRowCueRenderer _rowCues;
        private ContractHudFrame _cardFrame;
        private float _cardGlow;
        private bool _cardGlowFailure;
        // Polls pause while vanilla hides the HUD. Keep observing visual transitions on return,
        // but never play a completion that happened while the card was hidden.
        private bool _audioHiddenSinceLastPoll;
        private bool _audioEligibleForLastPoll;

        /// <summary>
        /// Hands the displayed contract's rows to the row-cue tracker, which compares them with the
        /// previous poll by stable key (#520) and starts the progress, completion or failure cue. A
        /// seed pass (the reveal) records the rows without cueing, so a late joiner and a fresh card
        /// never replay history.
        /// </summary>
        private void ConsumeChangeDetection(IReadOnlyList<MoonContractObjective> objectives, bool seedOnly)
        {
            // Capture eligibility before clearing the hidden-history latch: the same poll may
            // start an advance whose audio is attempted halfway through its visual animation.
            _audioEligibleForLastPoll = !seedOnly
                && ContractHudAudioPolicy.TransitionAudible(HudAudioVisible(), _audioHiddenSinceLastPoll);
            _audioHiddenSinceLastPoll = false;
            if (_rowCues == null)
                return;
            _rowCues.Observe(objectives, seedOnly);
            if (_rowCues.CompletedThisObserve && _audioEligibleForLastPoll)
                ContractHudAudio.Play(ContractHudAudioKind.ObjectiveComplete);
        }

        /// <summary>Only local HUD cues currently visible to the player can consume an audio window.</summary>
        internal static bool HudAudioVisible()
            => GameplayUiVisibility.IsVisibleUnderPauseMenu && !IsVanillaHudHidden();

        private void TickRowCues()
        {
            if (!HudAudioVisible())
                _audioHiddenSinceLastPoll = true;
            if (_rowCues != null && _rowCues.Tick(Time.unscaledTime))
                ApplyCardGlow(_rowCues.Glow, _rowCues.GlowIsFailure);
        }

        /// <summary>Clears every row cue and returns the frame to its resting tint.</summary>
        private void ResetRowCues()
        {
            _rowCues?.Clear();
            ApplyCardGlow(0f, false);
        }

        private void ApplyCardGlow(float amount, bool failure)
        {
            if (amount <= 0f && _cardGlow <= 0f)
                return;
            _cardGlow = amount;
            _cardGlowFailure = failure;
            ReapplyCardGlow(force: true);
        }

        /// <summary>
        /// Writes the current glow onto the frame. ApplyTheme calls this after resetting the frame
        /// tint, so a theme or text refresh in the middle of a glow does not drop a frame of it.
        /// </summary>
        private void ReapplyCardGlow(bool force = false)
        {
            if (_cardFrame == null || (!force && _cardGlow <= 0f))
                return;
            Color toward = _cardGlowFailure ? ContractUiTheme.Danger : ContractUiTheme.White;
            _cardFrame.SetGlow(toward, _cardGlow * ContractHudRowFeedback.GlowStrength);
        }
    }
}
