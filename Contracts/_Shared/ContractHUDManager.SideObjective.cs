using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1195/#1343. The OPTIONAL section: the host-published side objective under the main
    /// objective. Its source payload labels the header, while the action remains a separate row.
    /// A first observed host reveal types the section silently, then sweeps the row once.
    /// Later seeds, phase changes and card reopens show the full published row without replay.
    /// </summary>
    internal partial class ContractHUDManager
    {
        private const int NoSideRevision = int.MinValue;
        private const float SideSectionTopPadding = 10f;
        // The header takes the power-fault row's 20-unit title type; the row keeps the body's 22.
        private const string SideHeaderSizeTag = "<size=20>";

        private readonly ContractHudSideSection _side = new ContractHudSideSection();
        private RectTransform _sideRow;
        private CanvasGroup _sideGroup;
        private TextMeshProUGUI _sideText;
        private Image _sideDivider;
        private ContractHudLineArt _sideMarks;
        private ContractHudRowCueRenderer _sideCues;
        private readonly ContractHudSideRevealPolicy _sideReveal = new ContractHudSideRevealPolicy();
        private SideObjectiveSnapshot _sideRevealSnapshot;
        private SideObjectivePhase _sideRevealPhase;
        private Coroutine _sideRevealCoroutine;
        private Coroutine _sideOpeningTween;
        private bool _sideRevealPending;
        private bool _sideRevealOpening;
        private int _sideRevision = NoSideRevision;
        private bool _sideTextDirty = true;
        private float _sideContentHeight;
        // The side height the last ResizeLiveCard laid out, so a change is resized exactly once;
        // negative after a reset, so the next card to show the section always lays it out.
        private float _sideAppliedHeight;

        /// <summary>The section is drawn: it takes height, and the card is neither a result nor mid-arrival.</summary>
        private bool SideSectionShown => ContractHudArrival.ContentVisible(_isSettled, _side.Reserved && !_showingResult)
            && _sideRow != null && (!_sideRevealPending || _animCoroutine == null);
        // A previously revealed section reserves its destination height during flight even
        // though its pixels are gated. A new S3 reveal still opens later via its own tween.
        private float SideSectionHeight => (SideSectionShown
            || (!_isSettled && _side.Reserved && !_sideRevealPending && !_showingResult && _sideRow != null))
            ? _sideContentHeight : 0f;

        private void BuildSideSection(Transform parent)
        {
            StopSideReveal(keepPending: true);
            if (_sideRevealPending)
                _sideAppliedHeight = -1f;
            _sideRow = new GameObject("SideObjectiveSection", typeof(RectTransform), typeof(CanvasGroup)).GetComponent<RectTransform>();
            _sideRow.SetParent(parent, false);
            _sideGroup = _sideRow.GetComponent<CanvasGroup>();
            _sideGroup.interactable = false;
            _sideGroup.blocksRaycasts = false;

            _sideText = new GameObject("SideObjectiveText", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            _sideText.transform.SetParent(_sideRow, false);
            // The body row size, so the checkbox art and the row cues measure it like the body.
            _sideText.fontSize = 22f;
            _sideText.fontStyle = FontStyles.Normal;
            _sideText.alignment = TextAlignmentOptions.TopLeft;
            _sideText.enableWordWrapping = true;
            _sideText.overflowMode = TextOverflowModes.Overflow;
            _sideText.richText = true;
            _sideText.raycastTarget = false;
            _sideText.text = string.Empty;
            AssignVanillaFont(_sideText);
            var rect = _sideText.rectTransform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero; rect.offsetMax = new Vector2(0f, -SideSectionTopPadding);

            var marks = new GameObject("SideObjectiveCheckbox", typeof(RectTransform));
            marks.transform.SetParent(_sideText.transform, false);
            _sideMarks = marks.AddComponent<ContractHudLineArt>();
            _sideMarks.raycastTarget = false;
            _sideMarks.ObjectiveText = _sideText;
            rect = _sideMarks.rectTransform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;

            _sideDivider = new GameObject("SideObjectiveDivider", typeof(RectTransform)).AddComponent<Image>();
            _sideDivider.transform.SetParent(_sideRow, false);
            _sideDivider.raycastTarget = false;
            rect = _sideDivider.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f); rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(0f, -1f); rect.offsetMax = Vector2.zero;

            _sideCues = new ContractHudRowCueRenderer(_sideText);
            // A rebuilt card starts from the section's current rows without replaying a cue.
            _sideCues.Observe(_side.CueRows, seedOnly: true);
            _sideTextDirty = true;
            ApplySideSectionTheme();
            _sideRow.gameObject.SetActive(false);
        }

        private void LayoutSideSection()
        {
            if (_sideRow == null)
                return;
            bool shown = SideSectionShown;
            if (_sideRow.gameObject.activeSelf != shown)
                _sideRow.gameObject.SetActive(shown);
            if (!shown)
                return;
            float bottom = ContractHudSideSectionLayout.SideBottom(LiveCardBottomPadding, PowerFaultRowHeight);
            _sideRow.anchorMin = Vector2.zero; _sideRow.anchorMax = new Vector2(1f, 0f); _sideRow.pivot = new Vector2(.5f, 0f);
            _sideRow.offsetMin = new Vector2(LiveCardInnerSidePadding, bottom);
            _sideRow.offsetMax = new Vector2(-LiveCardInnerSidePadding, bottom + _sideContentHeight);
            _sideDivider.enabled = ContractHudSideSectionLayout.SideDivider(!_sectionsOnly);
        }

        /// <summary>
        /// Update. One revision compare and one stage check while the section is hidden or collapsed;
        /// otherwise it follows Contract S, runs the recovered timeline, opens or closes a
        /// sections-only card, resizes the card when the section's height changes, and ticks the
        /// section's row cues.
        /// </summary>
        private void TickSideSection()
        {
            int revision = SideObjectiveState.Revision;
            if (revision == _sideRevision && !_side.Active)
                return;

            float now = Time.unscaledTime;
            if (revision != _sideRevision)
            {
                SideObjectiveSnapshot snapshot = SideObjectiveNetwork.Current;
                SideObjectivePhase phase = SideObjectiveState.Phase;
                bool continuingReveal = (_sideRevealPending || _sideRevealCoroutine != null)
                    && phase == _sideRevealPhase && snapshot.SameObjective(_sideRevealSnapshot);
                StopSideReveal(keepPending: continuingReveal);
                _sideRevision = revision;
                bool newlyRevealed = _sideReveal.Observe(snapshot, phase);
                _sideRevealPending = _sideRevealPending || newlyRevealed;
                if (newlyRevealed)
                {
                    _sideRevealSnapshot = snapshot;
                    _sideRevealPhase = phase;
                }
                _side.Observe(phase, SideObjectiveState.Title, SideObjectiveState.Row, now);
                _sideTextDirty = true;
                // The published row as it is: never through the #880 pins, since the count can fall.
                if (_sideCues != null)
                    _sideCues.Observe(_side.CueRows, seedOnly: continuingReveal);
            }
            if (_showingResult && (_sideRevealPending || _sideRevealCoroutine != null))
                StopSideReveal();
            if (_sideRevealCoroutine != null && (_animCoroutine != null || !_isSettled))
            {
                // Main arrival owns the shared card layout; resume this reveal after it settles.
                StopSideReveal(keepPending: true);
                LayoutSideSection();
            }
            _side.Tick(now);
            if (!_side.Active)
                ReleaseSideCues();

            // HoldsCard is false until the objective is revealed (an unrevealed one publishes the
            // Hidden phase), so an unseen objective never opens a card.
            if (_side.HoldsCard && !_isShowing && RoundAllowsSideCard())
            {
                if (!OpenSectionsOnlyCard())
                    return;
                // Start a side-only reveal at the sectionless size, not a stale previous card size.
                if (_sideRevealPending)
                    ApplyCardLayout(new Vector2(LiveCardDefaultWidth,
                        ContractHudSideSectionLayout.SectionsOnlyHeight(LiveCardBottomPadding, PowerFaultRowHeight, 0f)));
            }
            if (!_isShowing || _cardRect == null)
                return;

            if (_sectionsOnly && CloseSectionsOnlyCardIfUnneeded())
                return;

            if (_sideTextDirty)
                RebuildSideText();
            float height = SideSectionHeight;
            if (height != _sideAppliedHeight && _isSettled && !_showingResult && _animCoroutine == null)
            {
                // The existing smoothstep layout tween opens a first reveal and closes a collapse.
                bool revealOpening = _sideRevealPending;
                Coroutine previousTween = _layoutTweenCoroutine;
                ResizeLiveCard(_objectiveText.text, animate: revealOpening || (!_sectionsOnly && height < _sideAppliedHeight));
                if (revealOpening && !object.ReferenceEquals(previousTween, _layoutTweenCoroutine))
                    _sideOpeningTween = _layoutTweenCoroutine;
            }
            if (_sideRevealPending && SideSectionShown && _animCoroutine == null && _sideText != null)
            {
                _sideRevealPending = false;
                _sideRevealOpening = true;
                _sideGroup.alpha = 0f;
                _sideRevealCoroutine = StartCoroutine(RevealSideSection());
            }
            if (_sideGroup != null && SideSectionShown && !_sideRevealOpening)
            {
                float alpha = _side.Alpha(now);
                if (_sideGroup.alpha != alpha)
                    _sideGroup.alpha = alpha;
            }
            TickSideCues(now);
        }

        /// <summary>
        /// The single owner of closing a card with no contract. Both the fault tick and the OPTIONAL
        /// tick call it, so a card the section is still fading out is never snapped shut by the
        /// other tick. True when it hid the card.
        /// </summary>
        private bool CloseSectionsOnlyCardIfUnneeded()
        {
            switch (ContractHudSideSection.CloseSectionsOnlyCard(_powerFaultVisible, _side.Stage))
            {
                case ContractHudSectionsOnlyClose.Fade:
                    Hide(immediate: false);
                    return true;
                case ContractHudSectionsOnlyClose.Immediate:
                    Hide(immediate: true);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The round is live and there is a local player to show a no-contract card to.</summary>
        private static bool RoundAllowsSideCard()
        {
            var start = StartOfRound.Instance;
            var network = GameNetworkManager.Instance;
            return start != null && !start.inShipPhase && !start.shipIsLeaving
                && network != null && network.localPlayerController != null;
        }

        private IEnumerator RevealSideSection()
        {
            // Main arrival owns the shared resize; the side starts only after it finishes.
            while (_layoutTweenCoroutine != null)
                yield return null;
            if (_animCoroutine != null || !_isSettled)
            {
                _sideRevealPending = true;
                _sideRevealOpening = false;
                _sideAppliedHeight = -1f;
                _sideRevealCoroutine = null;
                LayoutSideSection();
                yield break;
            }
            if (_sideText == null || !SideSectionShown)
            {
                _sideRevealOpening = false;
                _sideRevealCoroutine = null;
                yield break;
            }

            _sideText.maxVisibleCharacters = 0;
            _sideRevealOpening = false;
            _sideGroup.alpha = _side.Alpha(Time.unscaledTime);
            yield return TypewriterText(_sideText, _sideText.text, playAudio: false);
            if (_animCoroutine != null || !_isSettled || !SideSectionShown)
            {
                // The main card may have taken over during typing; never sweep an invisible row.
                if (_animCoroutine != null && !_showingResult && _side.Reserved)
                {
                    _sideRevealPending = true;
                    _sideAppliedHeight = -1f;
                }
                _sideRevealCoroutine = null;
                LayoutSideSection();
                yield break;
            }
            if (_sideCues != null)
                _sideCues.PlayRevealSweep(_side.Row.SignatureKey, Time.unscaledTime);
            OnSideObjectiveRevealed();
            _sideRevealCoroutine = null;
        }

        private void StopSideReveal(bool keepPending = false)
        {
            bool pending = keepPending && (_sideRevealPending || _sideRevealCoroutine != null);
            // ResizeLiveCard shares one tween with the main card. Cancel only the one this
            // reveal opened; never stop a later main-owned layout animation.
            if (_sideOpeningTween != null)
            {
                if (object.ReferenceEquals(_sideOpeningTween, _layoutTweenCoroutine))
                {
                    StopLayoutTween();
                    _sideAppliedHeight = -1f;
                }
                _sideOpeningTween = null;
            }
            if (_sideRevealCoroutine != null)
            {
                StopCoroutine(_sideRevealCoroutine);
                _sideRevealCoroutine = null;
            }
            if (_sideText != null)
                _sideText.maxVisibleCharacters = int.MaxValue;
            _sideRevealPending = pending;
            _sideRevealOpening = false;
            if (_sideGroup != null && _side.Reserved)
                _sideGroup.alpha = _side.Alpha(Time.unscaledTime);
        }

        private void OnSideObjectiveRevealed()
        {
            ContractHudAudio.Play(ContractHudAudioKind.OptionalReveal);
        }

        private void RebuildSideText()
        {
            if (_sideRevealCoroutine != null)
            {
                // Font verification and theme changes rebuild TMP without consuming the reveal.
                StopSideReveal(keepPending: true);
            }
            _sideTextDirty = false;
            if (_sideText == null || !_side.Reserved)
                return;
            string tint = _side.Dimmed ? ContractUiTheme.DimHex : ContractUiTheme.HudAccentHex;
            string header = ContractHudObjectivePresentation.Escape(ContractHudSideSection.Header(_side.Title));
            string text = SideHeaderSizeTag + "<color=" + tint + ">" + header + "</color></size>\n"
                + FormatObjectiveLine(_side.Row);
            _sideText.text = text;
            _sideContentHeight = SideSectionTopPadding
                + _sideText.GetPreferredValues(text, LiveCardDefaultWidth - LiveCardInnerSidePadding * 2f, 4000f).y;
        }

        private void TickSideCues(float now)
        {
            if (_sideCues == null || !_sideCues.Tick(now) || !SideSectionShown)
                return;
            // One frame edge for both texts: the body's own glow wins while it is the brighter one.
            float bodyGlow = _rowCues != null ? _rowCues.Glow : 0f;
            if (_sideCues.Glow >= bodyGlow)
                ApplyCardGlow(_sideCues.Glow, _sideCues.GlowIsFailure);
        }

        private void ReleaseSideCues()
        {
            if (_sideCues == null)
                return;
            _sideCues.Clear();
            if (_rowCues == null || _rowCues.Glow <= 0f)
                ApplyCardGlow(0f, false);
        }

        private void ApplySideSectionTheme()
        {
            if (_sideText != null) _sideText.color = ContractUiTheme.HudAccent;
            if (_sideDivider != null) _sideDivider.color = ContractUiTheme.HudAccent;
            if (_sideMarks != null) _sideMarks.color = ContractUiTheme.HudAccent;
        }

        /// <summary>
        /// Hide: forgets the section, so the next frame reads Contract S as a first sighting. A
        /// section still in progress returns (on a sections-only card if no contract is up); one
        /// already recovered stays gone. The row keeps its last pixels for the card's fade-out.
        /// </summary>
        private void ResetSideSection()
        {
            StopSideReveal();
            _side.Reset();
            _sideRevision = NoSideRevision;
            _sideTextDirty = true;
            _sideAppliedHeight = -1f;
            if (_sideCues != null)
                _sideCues.Clear();
        }
    }
}
