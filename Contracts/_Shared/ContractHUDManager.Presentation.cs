using System.Collections;
using TMPro;
using UnityEngine;
using Y4NGZCompany.Experience.UITheme;

namespace Y4NGZCompany.Contracts._Shared
{
    internal partial class ContractHUDManager
    {
        private ContractHudLineArt _contractIcon;
        private ContractHudLineArt _objectiveMarks;
        private RectTransform _iconRect;
        private MoonContractObjective? _activeObjective;
        // #1233 D6: the live title is struck and dimmed for the departed Shadow Raid drive.
        // Set with the title text (ApplyLiveTitle), cleared with the presentation; ApplyTheme tints by it.
        private bool _titleStruck;
        private bool _presentationBusy;
        private float _headerExpansion;

        private void BuildPresentationArt(Transform parent)
        {
            var icon = new GameObject("ContractIcon", typeof(RectTransform));
            icon.transform.SetParent(parent, false);
            _contractIcon = icon.AddComponent<ContractHudLineArt>();
            _contractIcon.raycastTarget = false;
            _contractIcon.color = ContractUiTheme.HudAccent;
            _iconRect = _contractIcon.rectTransform;
            var marks = new GameObject("ObjectiveCheckboxes", typeof(RectTransform));
            marks.transform.SetParent(_objectiveRect, false);
            _objectiveMarks = marks.AddComponent<ContractHudLineArt>();
            _objectiveMarks.raycastTarget = false;
            _objectiveMarks.color = ContractUiTheme.HudAccent;
            _objectiveMarks.ObjectiveText = _objectiveText;
            var rect = _objectiveMarks.rectTransform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
        }

        private void ClearArrivalReveal()
        {
            if (_arrivalMask != null)
            {
                _arrivalMask.enabled = false;
                _arrivalMask.padding = Vector4.zero;
            }
            if (_arrivalFlashing)
            {
                _arrivalFlashing = false;
                ApplyTheme();
            }
        }

        private IEnumerator FlyToDocked(Vector2 targetSize)
        {
            var start = new ContractHudArrivalGeometry(_cardRect.anchoredPosition.x, _cardRect.anchoredPosition.y,
                _cardRect.localScale.x, _cardRect.sizeDelta.x, _cardRect.sizeDelta.y, 1f);
            float elapsed = 0f;
            while (elapsed < ContractHudArrival.FlightSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                Vector2 targetPosition = GetMotionAdjustedSettledPosition();
                _arrivalDockedHeight = ContractHudArrival.ArrivalDestinationHeight(
                    _arrivalBaseHeight, PowerFaultRowHeight, SideSectionHeight);
                var end = new ContractHudArrivalGeometry(targetPosition.x, targetPosition.y,
                    SettledScale, targetSize.x, _arrivalDockedHeight, 0f);
                ContractHudArrivalGeometry frame = ContractHudArrival.Flight(in start, in end, elapsed);
                _cardRect.anchoredPosition = new Vector2(frame.X, frame.Y);
                _cardRect.localScale = Vector3.one * frame.Scale;
                _headerExpansion = frame.HeaderPose;
                ApplyCardLayout(new Vector2(frame.Width, frame.Height));
                // Land in this same coroutine step as the final layout: Update must not
                // change a section between the sampled destination and the settled handoff.
                if (elapsed < ContractHudArrival.FlightSeconds) yield return null;
            }
            // Docking is the sole body-visibility handoff. Refresh the fault from its real
            // source now (not its 0.1 s poll cache), then relayout at the measured destination.
            _arrivalState = _arrivalState.Dock();
            TickPowerFaultRow(force: true);
            _arrivalDockedHeight = ContractHudArrival.ArrivalDestinationHeight(
                _arrivalBaseHeight, PowerFaultRowHeight, SideSectionHeight);
            ApplyCardLayout(new Vector2(targetSize.x, _arrivalDockedHeight));
            _objectiveText.alpha = ContractHudArrival.ContentVisible(_isSettled) ? 1f : 0f;
            _objectiveText.maxVisibleCharacters = 0;
            UpdateAlertBelt();
            _arrivalBeltReserved = false;
            _cardRect.anchoredPosition = GetMotionAdjustedSettledPosition();
            UpdateReservedPromptTop();
        }

        private void ResetPresentation()
        {
            _activeObjective = null;
            _titleStruck = false;
            _presentationBusy = false;
            _headerExpansion = 0f;
            if (_objectiveMarks != null) _objectiveMarks.CheckProgress = 1f;
            ResetRowCues();
        }

        private void SelectContractIcon()
        {
            if (_contractIcon == null) return;
            _contractIcon.gameObject.SetActive(true);
            _contractIcon.Icon = _displayedContract switch
            {
                MoonContractType.Survey => ContractHudLineArt.Shape.Beacon,
                MoonContractType.BlackoutAudit => ContractHudLineArt.Shape.Bolt,
                MoonContractType.Defuse or MoonContractType.LguDefusal => ContractHudLineArt.Shape.Warning,
                MoonContractType.PestControl or MoonContractType.LguExtermination => ContractHudLineArt.Shape.Target,
                MoonContractType.Whistleblower or MoonContractType.LguDataRetrieval => ContractHudLineArt.Shape.Document,
                MoonContractType.ContainmentBreach or MoonContractType.LguExorcism => ContractHudLineArt.Shape.Shield,
                _ => ContractHudLineArt.Shape.Case
            };
            _contractIcon.SetVerticesDirty();
        }

        private void ApplyHeaderPose(float expansion)
        {
            if (_titleText == null || _cardRect == null) return;
            _headerExpansion = expansion;
            float width = _cardRect.sizeDelta.x;
            _titleText.fontSize = Mathf.Lerp(32f, 46f, expansion);
            float titleWidth = _titleText.GetPreferredValues(_titleText.text, 2000f, 100f).x;
            // Long contract names fit the intro header without leaking outside the frame.
            if (titleWidth > width - 110f)
            {
                _titleText.fontSize *= (width - 110f) / titleWidth;
                titleWidth = width - 110f;
            }
            float iconSize = Mathf.Lerp(36f, 58f, expansion);
            float groupLeft = Mathf.Max(20f, (width - titleWidth - iconSize - 20f) * .5f);
            float left = Mathf.Lerp(20f, groupLeft, expansion);
            float top = Mathf.Lerp(16f, (_cardRect.sizeDelta.y - iconSize) * .5f, expansion);
            if (_iconRect != null)
            {
                _iconRect.anchorMin = _iconRect.anchorMax = new Vector2(0f, 1f);
                _iconRect.pivot = new Vector2(0f, 1f);
                _iconRect.anchoredPosition = new Vector2(left, -top);
                _iconRect.sizeDelta = Vector2.one * iconSize;
            }
            _titleRect.anchorMin = _titleRect.anchorMax = new Vector2(0f, 1f);
            _titleRect.pivot = new Vector2(0f, 1f);
            bool showIcon = !_showingResult;
            float titleLeft = showIcon ? left + iconSize + 14f : 20f;
            _titleRect.anchoredPosition = new Vector2(titleLeft, -(top + 2f));
            _titleRect.sizeDelta = new Vector2(width - titleLeft - 18f, Mathf.Max(60f, iconSize));
        }

        private IEnumerator WaitForVanillaTimeArrival()
        {
            // QuotaAppear (3.383 seconds in vanilla) drives the time/quota introduction.
            // The landing hook already runs after it in vanilla; this also respects modded
            // duration and delayed animator evaluation without adding another fixed delay.
            yield return null;
            float waited = 0f;
            while (waited < 12f)
            {
                var hud = HUDManager.Instance;
                var animator = hud != null ? hud.quotaAnimator : null;
                if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null)
                    yield break;
                var current = animator.GetCurrentAnimatorStateInfo(0);
                bool arriving = current.IsName("QuotaAppear") && current.normalizedTime < 1f;
                if (animator.IsInTransition(0))
                    arriving |= animator.GetNextAnimatorStateInfo(0).IsName("QuotaAppear");
                if (!arriving) yield break;
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        private IEnumerator AnimateApprovedArrival()
        {
            ResetPresentation();
            _isShowing = true; _arrivalState = _arrivalState.Begin(); _isCompact = false; _showingResult = false;
            _arrivalDockedHeight = 0f;
            _arrivalBaseHeight = 0f;
            _arrivalBeltReserved = false;
            _canvasGroup.alpha = 0f;
            _objectiveText.text = string.Empty;
            _objectiveText.alpha = ContractHudArrival.ContentVisible(_isSettled) ? 1f : 0f;
            _objectiveText.maxVisibleCharacters = int.MaxValue;
            _alertBelt.Hide();
            _titleText.text = BuildLiveTitle();
            SelectContractIcon();
            ApplyTheme();
            _headerExpansion = 1f;
            _cardRect.localScale = Vector3.one * ContractHudArrival.IntroScale;
            ApplyCardLayout(new Vector2(ContractHudArrival.IntroWidth, ContractHudArrival.IntroHeight));
            _cardRect.anchoredPosition = GetCenteredPosition();
            yield return WaitForVanillaTimeArrival();

            // Measure the FIRST pending objective at its final docked width before revealing
            // the intro. The same measurement serves later live/objective-advance layouts.
            var arrivalRows = GetPresentedObjectives();
            _activeObjective = ContractHudObjectivePresentation.Select(arrivalRows);
            string objective = BuildLiveObjectiveText(compact: true);
            _lastSemanticSignature = BuildLiveSemanticSignature();
            Vector2 dockedSize = MeasureLiveCardSize(objective, compact: true);
            _arrivalBaseHeight = dockedSize.y - PowerFaultRowHeight - SideSectionHeight;
            _arrivalDockedHeight = ContractHudArrival.ArrivalDestinationHeight(
                _arrivalBaseHeight, PowerFaultRowHeight, SideSectionHeight);
            _sideAppliedHeight = SideSectionHeight;
            // Collect the destination segments without activating the strip before docking.
            UpdateAlertBelt();
            _arrivalBeltReserved = _beltSegments.Count > 0;

            _arrivalMask.padding = new Vector4(0f, _arrivalMask.canvasRect.height, 0f, 0f);
            _arrivalMask.enabled = true;
            _canvasGroup.alpha = 1f;
            float elapsed = 0f;
            int flashFrames = 0;
            bool arrivalCueAttempted = false;
            while (elapsed < ContractHudArrival.WipeSeconds || flashFrames < 2)
            {
                elapsed += Time.unscaledDeltaTime;
                float revealed = Mathf.Clamp01(elapsed / ContractHudArrival.WipeSeconds);
                _arrivalMask.padding = new Vector4(0f, _arrivalMask.canvasRect.height * (1f - revealed), 0f, 0f);
                if (!arrivalCueAttempted && revealed > 0f)
                {
                    arrivalCueAttempted = true;
                    ContractHudAudio.Play(ContractHudAudioKind.Arrival);
                }
                if (flashFrames < 2)
                {
                    _cardFrame.SetGlow(ContractUiTheme.White, .55f);
                    _arrivalFlashing = true;
                    flashFrames++;
                }
                else if (_arrivalFlashing)
                {
                    _arrivalFlashing = false;
                    ApplyTheme();
                }
                UpdateReservedPromptTop();
                yield return null;
            }
            ClearArrivalReveal();
            yield return new WaitForSecondsRealtime(ContractHudArrival.HoldSeconds);
            _isCompact = true;
            yield return FlyToDocked(dockedSize);

            ConsumeChangeDetection(arrivalRows, seedOnly: true);
            _cardRect.anchoredPosition = GetMotionAdjustedSettledPosition();
            UpdateReservedPromptTop();
            yield return TypewriterText(_objectiveText, objective, playAudio: true);
            yield return new WaitForSecondsRealtime(.55f);
            _lastLiveText = objective;
            _lastLivePlainText = ToPlainObjectiveText(objective);
            _arrivalBaseHeight = 0f;
            _arrivalDockedHeight = 0f;
            _animCoroutine = null;
            _liveCoroutine = StartCoroutine(LiveUpdateLoop());
        }

        private IEnumerator AnimateObjectiveAdvance(MoonContractObjective completed, bool audioEligible)
        {
            _presentationBusy = true;
            _isCompact = false;
            string previous = FormatObjectiveLine(completed);
            _objectiveText.text = previous;
            _objectiveText.maxVisibleCharacters = int.MaxValue;
            _objectiveText.alpha = 1f;
            // The row's semantic completion may have sounded this same poll; land the
            // checkbox tick midway through its fill, outside the global audio window.
            double advanceCueAt = Time.realtimeSinceStartupAsDouble + ContractHudAudioPolicy.GlobalCooldownSeconds;
            bool advanceCueAttempted = false;
            float elapsed = 0f;
            while (elapsed < .3f)
            {
                elapsed += Time.unscaledDeltaTime;
                if (!advanceCueAttempted && Time.realtimeSinceStartupAsDouble >= advanceCueAt)
                {
                    advanceCueAttempted = true;
                    if (audioEligible)
                        ContractHudAudio.Play(ContractHudAudioKind.ObjectiveAdvance);
                }
                _objectiveMarks.CheckProgress = Mathf.Clamp01(elapsed / .3f);
                yield return null;
            }
            _objectiveMarks.CheckProgress = 1f;
            yield return new WaitForSecondsRealtime(.25f);
            string next = BuildLiveObjectiveText(compact: true);
            string expanded = previous + "\n" + next;
            ResizeLiveCard(expanded, animate: true);
            while (_layoutTweenCoroutine != null) yield return null;
            _objectiveText.ForceMeshUpdate();
            int prefix = _objectiveText.textInfo.characterCount + 1;
            yield return TypewriterText(_objectiveText, expanded, playAudio: true, startVisibleCharacters: prefix);
            yield return new WaitForSecondsRealtime(.85f);
            // Re-read after the hold. Rapid progress cannot leave an obsolete step at rest.
            next = BuildLiveObjectiveText(compact: true);
            yield return FadeObjectiveSwap(next);
            _isCompact = true;
            ResizeLiveCard(next, animate: true);
            while (_layoutTweenCoroutine != null) yield return null;
            _activeObjective = ContractHudObjectivePresentation.Select(GetPresentedObjectives());
            _lastLiveText = next; _lastLivePlainText = ToPlainObjectiveText(next);
            _presentationBusy = false;
            _objectiveTypeCoroutine = null;
        }
    }
}
