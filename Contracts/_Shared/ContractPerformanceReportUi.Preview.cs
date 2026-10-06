using System;
using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1203: the parts of the debug report preview that need the report's own private state -
    /// the preview's sequence handle, and opening and closing the vanilla endgame panel. The
    /// orchestration (sample, refusals, save and restore) is <see cref="PerformanceReportPreview"/>.
    /// </summary>
    internal static partial class ContractPerformanceReportUi
    {
        private static Coroutine _previewSequence;
        private static HUDManager _previewSequenceHost;

        private static IEnumerable<PerformanceReportNoteEntry> GetPreviewXpNoteCandidates(
            ContractPerformanceReportData report,
            int playerIndex)
        {
            IReadOnlyList<PerformanceReportNoteEntry>[] candidates = report?.PreviewXpNoteCandidates;
            // Never null for a preview slot: null would fall through to this round's stats.
            return candidates != null && playerIndex >= 0 && playerIndex < candidates.Length && candidates[playerIndex] != null
                ? candidates[playerIndex]
                : Array.Empty<PerformanceReportNoteEntry>();
        }

        // A retried apply or a replaced preview must not leave an earlier sequence animating
        // cells the new pass has already destroyed.
        private static void StartPreviewSequence(HUDManager hud, List<AnimatedAmount> animated, int boxCount)
        {
            StopPreviewSequence();
            _previewSequenceHost = hud;
            _previewSequence = hud.StartCoroutine(AnimateQuotaNumbers(hud, animated, boxCount, preview: true));
        }

        /// <summary>
        /// Stops the preview's sequence wherever it is. A stopped coroutine never runs its
        /// finally block, so the countdown clip is stopped here instead.
        /// </summary>
        internal static void StopPreviewSequence()
        {
            if (_previewSequence != null && _previewSequenceHost != null)
            {
                _previewSequenceHost.StopCoroutine(_previewSequence);
                StopCountdown(GetOrCreatePerformanceReportAudioSource(_previewSequenceHost));
            }

            _previewSequence = null;
            _previewSequenceHost = null;
        }

        /// <summary>Starts the vanilla endgame panel the way the round end does.</summary>
        internal static void OpenPreviewPanel(HUDManager hud)
        {
            Animator animator = hud != null ? hud.endgameStatsAnimator : null;
            if (animator == null)
                return;

            animator.SetTrigger(IsChallengeFile()
                ? VanillaEndgameStatsDisplayChallengeTriggerName
                : VanillaEndgameStatsDisplayTriggerName);
        }

        /// <summary>
        /// Hands the endgame animator back: the speed the preview found, an unconsumed display
        /// trigger dropped, and a panel still open seeked to the point the report treats as
        /// closed, from where the clip finishes on its own. Never seeks backwards.
        /// </summary>
        internal static void ClosePreviewPanel(HUDManager hud, float animatorSpeed)
        {
            Animator animator = hud != null ? hud.endgameStatsAnimator : null;
            if (animator == null)
                return;

            animator.ResetTrigger(VanillaEndgameStatsDisplayTriggerName);
            animator.ResetTrigger(VanillaEndgameStatsDisplayChallengeTriggerName);
            animator.speed = animatorSpeed > 0f ? animatorSpeed : 1f;
            if (!animator.isActiveAndEnabled)
                return;

            AnimatorStateInfo state = GetEndgameStatsState(animator);
            if (!IsEndgameStatsState(state) || state.normalizedTime >= ReportClosedAnimatorNormalizedTime)
                return;

            animator.Play(state.fullPathHash, 0, ReportClosedAnimatorNormalizedTime);
            animator.Update(0f);
        }

        /// <summary>True while the vanilla endgame panel is open, whoever opened it.</summary>
        internal static bool IsEndgameStatsPanelOpen(HUDManager hud)
        {
            Animator animator = hud != null ? hud.endgameStatsAnimator : null;
            if (animator == null || !animator.isActiveAndEnabled)
                return false;

            AnimatorStateInfo state = GetEndgameStatsState(animator);
            return IsEndgameStatsState(state) && state.normalizedTime < ReportClosedAnimatorNormalizedTime;
        }

        // Mid-transition the current state is still the idle one, so the state being entered
        // is the one that decides.
        private static AnimatorStateInfo GetEndgameStatsState(Animator animator)
        {
            return animator.IsInTransition(0)
                ? animator.GetNextAnimatorStateInfo(0)
                : animator.GetCurrentAnimatorStateInfo(0);
        }

        private static bool IsEndgameStatsState(AnimatorStateInfo state)
        {
            return state.shortNameHash == Animator.StringToHash(VanillaEndgameStatsStateName)
                || state.shortNameHash == Animator.StringToHash(VanillaEndgameStatsChallengeStateName);
        }

        private static bool IsChallengeFile()
        {
            return StartOfRound.Instance != null && StartOfRound.Instance.isChallengeFile;
        }
    }
}
