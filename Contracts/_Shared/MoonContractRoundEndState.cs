using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.WasteDisposal;

namespace Y4NGZCompany.Contracts._Shared
{
    internal static class MoonContractRoundEndState
    {
        private struct Outcome
        {
            internal bool HasContract;
            internal MoonContractType Type;
            internal string DisplayName;
            internal bool Completed;
            internal bool RewardApplied;
            internal int RewardAmount;
            internal bool UsesExactQuotaCredit;
            internal int SuccessfulBurnValue;
            internal bool PenaltyApplied;
            internal int PenaltyAmount;
            internal int QuotaBeforeModifiers;
            internal int NewQuota;
            internal int DirectivePercent;
            internal int RevisedQuota;
            internal bool CrewLossCaptured;
            internal bool CrewLossApplied;
            internal int CrewLossPlayersDead;
            internal int CrewLossBodiesInsured;
            internal int CrewLossPlayerCount;
            internal float CrewLossPenaltyFraction;
            internal int CrewLossPenaltyAmount;
            internal bool HazardPayChecked;
            internal bool HazardPayApplied;
            internal string HazardPayWeather;
            internal int HazardPayPercent;
            internal int HazardPayAmount;
            // #748: the host's Quota "Enabled" value at capture time. False means the whole
            // performance-report extension stands down and vanilla's report is left alone.
            // Mirrored to clients through QuotaRoundEndReport so a client's own config never
            // decides what the host has switched off.
            internal bool QuotaOverhaulEnabled;
        }

        private static Outcome _pending;
        private static bool _hasPending;
        private static bool _notificationQueued;
        /// <summary>#944 S11 U6: frame the "no pending notification" debug line was last printed on,
        /// so the host's double ClientRpc invocation prints it once. -1 means never.</summary>
        private static int _noPendingLoggedFrame = -1;
        // #424: the host's authoritative round-end report. Kept separate from _pending
        // because the named message can land before this machine has captured an outcome.
        private static QuotaRoundEndReport _serverReport;
        private static bool _hasServerReport;
        private static bool _roundEndReportBroadcast;

        /// <summary>
        /// One staging per outcome (#611 task 1.3). ApplyEndOfRoundEffects runs twice per round
        /// and the second pass still sees the same amounts on _pending, so without this the
        /// objective delta handed to the rollover publisher would be counted twice. Reset
        /// wherever <see cref="_roundEndReportBroadcast"/> is, because both latch the same
        /// thing: this outcome has already been handed on.
        ///
        /// <para><b>Clearing this is not enough on its own</b> — every site that resets it also
        /// calls <c>QuotaRolloverOutcomeStaging.Discard()</c>. Most rounds do not end in a
        /// rollover, so nothing consumes the Core staging slot; resetting only this latch would
        /// let the next round stage a second delta on top of the first, and the slot accumulates
        /// by design. The delta at the eventual rollover would then be the sum over every round
        /// since the last one, with an OR'd "counted toward quota" flag that is true if any of
        /// them happened to land before a check. The two must be cleared together.</para>
        /// </summary>
        private static bool _outcomeStaged;
        // #438: the host's built-but-unsent report. The broadcast can fail (no messaging manager
        // yet, write error), and the two call sites of ApplyEndOfRoundEffects are one-shot, so
        // the payload is parked here and retried from the per-frame contract runtime pass until
        // it lands or the next round starts.
        private static QuotaRoundEndReport _roundEndReportPayload;
        private static bool _hasRoundEndReportPayload;
        private static float _nextRoundEndBroadcastRetryAt;
        private static float _roundEndBroadcastGiveUpAt;
        private const float RoundEndBroadcastRetryIntervalSeconds = 0.5f;
        private const float RoundEndBroadcastRetryWindowSeconds = 30f;
        private static Coroutine _performanceReportApplyCoroutine;
        private static HUDManager _performanceReportApplyHost;
        private static bool _hasRoundStartContractSnapshot;
        private static MoonContractType _roundStartContractType;
        private static string _roundStartContractDisplayName;

        private struct PenaltyReport
        {
            internal int PlayersDead;
            internal int BodiesInsured;
            internal int CreditsBefore;
            internal int CreditsAfter;
            internal string PenaltyAddition;
            internal string PenaltyTotal;
        }

        internal static void CaptureCurrentOutcomeIfMissing(string reason)
        {
            if (_hasPending || _notificationQueued)
                return;

            CaptureCurrentOutcome(reason);
        }

        internal static void CaptureRoundStartContractSnapshot(string reason)
        {
            DiscardStaleRoundEndState(reason);

            MoonContractType type = MoonContractState.GetCurrentContract();
            if (type == MoonContractType.None)
            {
                ClearRoundStartContractSnapshot($"{reason}: no contract");
                return;
            }

            ClearServerRoundEndReport("round-start snapshot captured");
            _hasRoundStartContractSnapshot = true;
            _roundStartContractType = type;
            _roundStartContractDisplayName = MoonContractCatalog.Get(type).DisplayName;

            ContractsBootstrap.Log?.LogDebug(
                "[MoonContracts.ReportDiag] Captured round-start contract snapshot: " +
                $"type={type}, display='{_roundStartContractDisplayName}' ({reason}).");
        }

        internal static void ClearRoundStartContractSnapshot(string reason)
        {
            if (_hasRoundStartContractSnapshot)
            {
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.ReportDiag] Cleared round-start contract snapshot: " +
                    $"type={_roundStartContractType}, display='{_roundStartContractDisplayName}' ({reason}).");
            }

            _hasRoundStartContractSnapshot = false;
            _roundStartContractType = MoonContractType.None;
            _roundStartContractDisplayName = string.Empty;
            ClearServerRoundEndReport(reason);
        }

        /// <summary>
        /// A new round is starting, so anything left over from the previous one is dead weight
        /// (#438). The important half is <see cref="_hasPending"/>: it gates
        /// <see cref="CaptureCurrentOutcomeIfMissing"/>, so an outcome that was never rendered -
        /// a client whose host report never arrived leaves exactly that - would otherwise block
        /// every future capture for the rest of the session.
        /// </summary>
        private static void DiscardStaleRoundEndState(string reason)
        {
            if (_hasPending || _notificationQueued)
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.ReportDiag] Discarding an unrendered round-end outcome from the previous round " +
                    $"({reason}): pending={DescribeOutcome(_pending)}, notificationQueued={_notificationQueued}.");
            }

            _pending = default;
            _hasPending = false;
            _notificationQueued = false;

            if (_hasRoundEndReportPayload)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Quota] Dropping an unsent round-end quota report from the previous round ({reason}).");
            }

            _roundEndReportPayload = default;
            _hasRoundEndReportPayload = false;
            _nextRoundEndBroadcastRetryAt = 0f;
            _roundEndBroadcastGiveUpAt = 0f;

            // #451: an outcome discarded before it rendered leaves nobody to
            // release the HUD chrome the presentation suppressed, and the
            // _reportSequenceActive / _performanceReportDelayArmed gates stay set —
            // pinning GameplayUiVisibility false for the rest of the session. This
            // is the one place that provably runs after every such discard.
            ContractPerformanceReportUi.ForceRestoreRoundEndHudSuppression(
                $"stale round-end state discarded ({reason})");
        }

        private static void ClearServerRoundEndReport(string reason)
        {
            if (_hasServerReport)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Quota] Cleared host round-end quota report ({reason}).");
            }

            _serverReport = default;
            _hasServerReport = false;
            _roundEndReportBroadcast = false;
            _outcomeStaged = false;
            Y4NGZCore.Modules.Quota.QuotaRolloverOutcomeStaging.Discard();
        }

        internal static void CaptureCurrentOutcome(string reason)
        {
            if (!TryResolveReportContract(out var type, out string displayName, out bool fromSnapshot))
            {
                int quota = TimeOfDay.Instance != null ? Mathf.Max(0, TimeOfDay.Instance.profitQuota) : 0;
                string salvageDisplayName = MoonContractCatalog.Get(MoonContractType.None).DisplayName;
                _pending = new Outcome
                {
                    HasContract = false,
                    DisplayName = salvageDisplayName,
                    Completed = false,
                    QuotaBeforeModifiers = quota,
                    NewQuota = quota,
                    RevisedQuota = quota,
                    QuotaOverhaulEnabled = QuotaOverhaulRules.IsEnabled,
                };
                _hasPending = true;
                _notificationQueued = false;
                _roundEndReportBroadcast = false;
                _outcomeStaged = false;
                Y4NGZCore.Modules.Quota.QuotaRolloverOutcomeStaging.Discard();
                MergeServerRoundEndReportIntoPending("no-contract outcome captured");
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.ReportDiag] Captured no-contract round-end outcome: " +
                    $"quota={quota}, quotaOverhaulEnabled={QuotaOverhaulRules.IsEnabled}, " +
                    $"current={MoonContractState.GetCurrentContractDebugContext()}, " +
                    $"snapshot={DescribeRoundStartContractSnapshot()} ({reason}).");
                return;
            }

            _pending = new Outcome
            {
                HasContract = true,
                Type = type,
                DisplayName = displayName,
                Completed = IsContractComplete(type),
                QuotaBeforeModifiers = TimeOfDay.Instance != null ? Mathf.Max(0, TimeOfDay.Instance.profitQuota) : 0,
                NewQuota = TimeOfDay.Instance != null ? Mathf.Max(0, TimeOfDay.Instance.profitQuota) : 0,
                RevisedQuota = TimeOfDay.Instance != null ? Mathf.Max(0, TimeOfDay.Instance.profitQuota) : 0,
                QuotaOverhaulEnabled = QuotaOverhaulRules.IsEnabled,
            };
            _pending.UsesExactQuotaCredit = type == MoonContractType.WasteDisposal && _pending.Completed;
            _pending.SuccessfulBurnValue = _pending.UsesExactQuotaCredit ? WasteDisposalContract.GetSuccessfulBurnValue() : 0;
            _hasPending = true;
            _notificationQueued = false;
            _roundEndReportBroadcast = false;
            _outcomeStaged = false;
            Y4NGZCore.Modules.Quota.QuotaRolloverOutcomeStaging.Discard();
            MergeServerRoundEndReportIntoPending("contract outcome captured");
            ContractProgressionEvents.PublishContractResolved(type, _pending.Completed, reason);

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Captured round-end outcome for {_pending.DisplayName}: " +
                $"completed={_pending.Completed} ({reason}, source={(fromSnapshot ? "round-start snapshot" : "current contract")}).");
        }

        private static bool TryResolveReportContract(out MoonContractType type, out string displayName, out bool fromSnapshot)
        {
            if (_hasRoundStartContractSnapshot && _roundStartContractType != MoonContractType.None)
            {
                type = _roundStartContractType;
                displayName = !string.IsNullOrWhiteSpace(_roundStartContractDisplayName)
                    ? _roundStartContractDisplayName
                    : MoonContractCatalog.Get(type).DisplayName;
                fromSnapshot = true;
                MoonContractType current = MoonContractState.GetCurrentContract();
                if (current != type)
                {
                    ContractsBootstrap.Log?.LogDebug(
                        "[MoonContracts.ReportDiag] Using round-start contract snapshot for report capture: " +
                        $"type={type}, display='{displayName}', current={MoonContractState.GetCurrentContractDebugContext()}.");
                }

                return true;
            }

            type = MoonContractState.GetCurrentContract();
            fromSnapshot = false;
            if (type != MoonContractType.None)
            {
                displayName = MoonContractCatalog.Get(type).DisplayName;
                return true;
            }

            displayName = string.Empty;
            return false;
        }

        private static bool IsContractComplete(MoonContractType type)
        {
            var objectives = MoonContractState.GetObjectives(type);
            if (objectives.Count == 0)
                return true;

            foreach (var objective in objectives)
            {
                if (!objective.IsComplete)
                    return false;
            }

            return true;
        }

        private static string DescribeRoundStartContractSnapshot()
        {
            return _hasRoundStartContractSnapshot
                ? $"type={_roundStartContractType}, display='{_roundStartContractDisplayName}'"
                : "<none>";
        }

        internal static void CaptureCrewLosses(int playersDead, int bodiesInsured, int playerCount, string reason)
        {
            if (!_hasPending)
                CaptureCurrentOutcome(reason);

            if (!_hasPending)
                return;

            _pending.CrewLossCaptured = true;
            _pending.CrewLossPlayersDead = Mathf.Max(0, playersDead);
            _pending.CrewLossBodiesInsured = Mathf.Clamp(bodiesInsured, 0, _pending.CrewLossPlayersDead);
            _pending.CrewLossPlayerCount = Mathf.Max(1, playerCount);
            _roundEndReportBroadcast = false;
            _outcomeStaged = false;
            Y4NGZCore.Modules.Quota.QuotaRolloverOutcomeStaging.Discard();
            // Server-gated internally; a client's recorded moon player count is never used.
            QuotaOverhaulRules.RecordCurrentPlayerCountForMoon(_pending.CrewLossPlayerCount);
            // These counts are derived from local livingPlayers, so on a client the host's
            // report wins whenever it has already arrived.
            MergeServerRoundEndReportIntoPending("crew losses captured");
        }

        /// <summary>
        /// Client-side entry point for the host's authoritative round-end quota report (#424).
        /// Applies the quota immediately - the performance report may never render, but the
        /// quota must still be right - and folds the presentational fields into the pending
        /// outcome as soon as one exists.
        /// </summary>
        internal static void ApplyServerRoundEndReport(QuotaRoundEndReport report)
        {
            _serverReport = report;
            _hasServerReport = true;

            QuotaOverhaulRules.ApplyReplicatedQuota(report.RevisedQuota, "round-end report");
            ContractsBootstrap.Log?.LogInfo(
                "[MoonContracts.Quota] Received host round-end quota report: " +
                $"revisedQuota={report.RevisedQuota}, before={report.QuotaBeforeModifiers}, " +
                $"hasContract={report.HasContract}, completed={report.Completed}, " +
                $"reward={report.RewardAmount}, penalty={report.PenaltyAmount}, " +
                $"hazard={report.HazardPayAmount}, crewPenalty={report.CrewLossPenaltyAmount}, " +
                $"pendingCaptured={_hasPending}.");

            MergeServerRoundEndReportIntoPending("host round-end report received");
        }

        /// <summary>
        /// Idempotent: re-running it writes the same host-authored values over the same fields,
        /// so it is safe to call from every point where either half of the pair (the report or
        /// the pending outcome) can appear or be locally overwritten.
        /// </summary>
        private static void MergeServerRoundEndReportIntoPending(string reason)
        {
            if (!_hasServerReport || !_hasPending)
                return;

            QuotaRoundEndReport report = _serverReport;
            MoonContractType type = MoonContractType.None;
            if (report.HasContract && Enum.IsDefined(typeof(MoonContractType), report.ContractType))
                type = (MoonContractType)report.ContractType;

            _pending.HasContract = report.HasContract && type != MoonContractType.None;
            _pending.Type = _pending.HasContract ? type : MoonContractType.None;
            _pending.DisplayName = MoonContractCatalog.Get(_pending.Type).DisplayName;
            _pending.Completed = report.Completed;
            _pending.RewardApplied = report.RewardApplied;
            _pending.RewardAmount = report.RewardAmount;
            _pending.UsesExactQuotaCredit = report.UsesExactQuotaCredit;
            _pending.SuccessfulBurnValue = report.SuccessfulBurnValue;
            _pending.PenaltyApplied = report.PenaltyApplied;
            _pending.PenaltyAmount = report.PenaltyAmount;
            _pending.QuotaBeforeModifiers = report.QuotaBeforeModifiers;
            _pending.NewQuota = report.NewQuota;
            _pending.DirectivePercent = report.DirectivePercent;
            _pending.RevisedQuota = report.RevisedQuota;
            _pending.HazardPayChecked = true;
            _pending.HazardPayApplied = report.HazardPayApplied;
            _pending.HazardPayWeather = string.IsNullOrWhiteSpace(report.HazardPayWeather)
                ? "Eclipse"
                : report.HazardPayWeather.Trim();
            _pending.HazardPayPercent = report.HazardPayPercent;
            _pending.HazardPayAmount = report.HazardPayAmount;
            _pending.CrewLossCaptured = report.CrewLossCaptured;
            _pending.CrewLossApplied = true;
            _pending.CrewLossPlayersDead = report.CrewLossPlayersDead;
            _pending.CrewLossBodiesInsured = report.CrewLossBodiesInsured;
            _pending.CrewLossPlayerCount = report.CrewLossPlayerCount;
            _pending.CrewLossPenaltyFraction = report.CrewLossPenaltyFraction;
            _pending.CrewLossPenaltyAmount = report.CrewLossPenaltyAmount;
            _pending.QuotaOverhaulEnabled = report.QuotaOverhaulEnabled;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Quota] Merged host round-end report into pending outcome ({reason}): " +
                DescribeOutcome(_pending) + ".");
        }

        private static QuotaRoundEndReport BuildRoundEndReport(Outcome outcome)
        {
            return new QuotaRoundEndReport
            {
                HasContract = outcome.HasContract,
                ContractType = (int)outcome.Type,
                Completed = outcome.Completed,
                RewardApplied = outcome.RewardApplied,
                RewardAmount = outcome.RewardAmount,
                UsesExactQuotaCredit = outcome.UsesExactQuotaCredit,
                SuccessfulBurnValue = outcome.SuccessfulBurnValue,
                PenaltyApplied = outcome.PenaltyApplied,
                PenaltyAmount = outcome.PenaltyAmount,
                QuotaBeforeModifiers = outcome.QuotaBeforeModifiers,
                NewQuota = outcome.NewQuota,
                DirectivePercent = outcome.DirectivePercent,
                RevisedQuota = outcome.RevisedQuota,
                HazardPayApplied = outcome.HazardPayApplied,
                HazardPayWeather = outcome.HazardPayWeather,
                HazardPayPercent = outcome.HazardPayPercent,
                HazardPayAmount = outcome.HazardPayAmount,
                CrewLossCaptured = outcome.CrewLossCaptured,
                CrewLossPlayersDead = outcome.CrewLossPlayersDead,
                CrewLossBodiesInsured = outcome.CrewLossBodiesInsured,
                CrewLossPlayerCount = outcome.CrewLossPlayerCount,
                CrewLossPenaltyFraction = outcome.CrewLossPenaltyFraction,
                CrewLossPenaltyAmount = outcome.CrewLossPenaltyAmount,
                QuotaOverhaulEnabled = outcome.QuotaOverhaulEnabled,
            };
        }

        /// <summary>
        /// Finalizes this round's contract outcome against the quota.
        ///
        /// <para><paramref name="beforeQuotaRolloverCheck"/> reports which of the two call
        /// sites is running. The <c>EndOfGameClientRpc</c> prefix executes before the game
        /// compares <c>quotaFulfilled</c> against <c>profitQuota</c>, so a payout applied there
        /// could be what carried the crew over the line; the <c>FillEndGameStats</c> prefix
        /// runs after that comparison, so an outcome finalized there is banked against the
        /// <em>next</em> quota instead. Nothing downstream can work out which happened after
        /// the fact, which is why it is reported rather than inferred -- see
        /// <c>QuotaRolloverFinalized.OutcomeCountedTowardQuota</c>.</para>
        /// </summary>
        internal static void ApplyEndOfRoundEffects(bool beforeQuotaRolloverCheck = false)
        {
            if (!_hasPending)
                return;

            var time = TimeOfDay.Instance;
            if (time == null || StartOfRound.Instance == null || StartOfRound.Instance.isChallengeFile)
                return;

            // #424: every input below diverges per machine (config values, locally evaluated
            // objective completion, the Waste Disposal burn value, livingPlayers-derived crew
            // losses, per-machine recorded player counts, companion-mod penalty modifiers).
            // Only the host computes; clients wait for the broadcast rather than computing a
            // local answer and correcting it later, which would race and flicker the HUD.
            if (!QuotaOverhaulRules.IsServerRuntime())
            {
                MergeServerRoundEndReportIntoPending("client round end");
                return;
            }

            if (_pending.QuotaBeforeModifiers <= 0)
                _pending.QuotaBeforeModifiers = Mathf.Max(0, time.profitQuota);

            if (_pending.HasContract && _pending.Completed && !_pending.RewardApplied)
            {
                int currentQuota = Mathf.Max(0, time.profitQuota);
                float fraction = _pending.UsesExactQuotaCredit
                    ? 0f
                    : MoonContractState.GetContractRewardFractionForCurrentMoon();
                int reward = _pending.UsesExactQuotaCredit
                    ? WasteDisposalIncineratorRules.CalculateQuotaCredit(currentQuota, _pending.SuccessfulBurnValue)
                    : currentQuota <= 0 || fraction <= 0f ? 0 : Mathf.Max(1, Mathf.CeilToInt(currentQuota * fraction));
                int newQuota = QuotaOverhaulRules.ClampQuotaTarget(currentQuota - reward, "contract reward");

                time.profitQuota = newQuota;
                time.UpdateProfitQuotaCurrentTime();

                _pending.RewardApplied = true;
                _pending.RewardAmount = reward;
                _pending.NewQuota = newQuota;
                _pending.RevisedQuota = newQuota;
                _pending.DirectivePercent = _pending.UsesExactQuotaCredit
                    ? 0
                    : Mathf.RoundToInt(Mathf.Clamp01(fraction) * 100f);

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] {_pending.DisplayName} completed - quota reduced by ${reward} to ${newQuota}.");
            }

            if (_pending.HasContract && !_pending.Completed && !_pending.PenaltyApplied)
            {
                int quotaBeforePenalty = Mathf.Max(0, time.profitQuota);
                float penaltyFraction = MoonContractState.GetContractPenaltyFractionForCurrentMoon();
                int penalty = quotaBeforePenalty <= 0 || penaltyFraction <= 0f ? 0 : Mathf.Max(1, Mathf.CeilToInt(quotaBeforePenalty * penaltyFraction));
                int quotaAfterPenalty = QuotaOverhaulRules.ClampQuotaTarget(quotaBeforePenalty + penalty, "contract penalty");

                time.profitQuota = quotaAfterPenalty;
                time.UpdateProfitQuotaCurrentTime();

                _pending.PenaltyApplied = true;
                _pending.PenaltyAmount = penalty;
                _pending.NewQuota = quotaAfterPenalty;
                _pending.RevisedQuota = quotaAfterPenalty;
                _pending.DirectivePercent = Mathf.RoundToInt(Mathf.Clamp01(penaltyFraction) * 100f);

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] {_pending.DisplayName} failed - quota increased by ${penalty} to ${quotaAfterPenalty}.");
            }

            ApplyHazardPayModifier(time);
            ApplyCrewLossQuotaPenalty(time);
            _pending.RevisedQuota = Mathf.Max(0, time.profitQuota);

            // #611 task 1.3: hand the net contract outcome to the rollover publisher.
            //
            // Reward and penalty are expressed as movements of the quota *target*: completing a
            // contract lowers it, failing raises it. In "credit toward the quota" terms -- which
            // is what ObjectiveOutcomeDelta means -- lowering the target by N is worth +N, so
            // the delta is reward minus penalty. Hazard pay and the crew-loss penalty are
            // deliberately excluded: neither is an objective outcome, and both are already in
            // the report the performance screen renders.
            //
            // Staged, not published: the publisher runs later, in the quota patch layer, after
            // the game has applied the rollover, and it is the only thing that may decide a
            // rollover happened at all. Host-only, matching the guard above.
            //
            // Latched for the same reason the broadcast below is. This method runs TWICE per
            // round - the EndOfGameClientRpc prefix and the FillEndGameStats prefix - and the
            // applied-flags above make the second pass recompute nothing, so it would arrive
            // here with the same RewardAmount still on _pending. Staging accumulates by design
            // (a payout and a penalty are two legs of one outcome), so an unlatched second call
            // would double the delta the reward screen attributes to this quota.
            if (!_outcomeStaged)
            {
                _outcomeStaged = true;
                Y4NGZCore.Modules.Quota.QuotaRolloverOutcomeStaging.ReportObjectiveOutcome(
                    _pending.RewardAmount - _pending.PenaltyAmount,
                    beforeQuotaRolloverCheck);
            }

            // One packet per outcome. The second call (HUDManager.FillEndGameStats prefix)
            // recomputes nothing thanks to the applied-flags above, so it must not re-send.
            if (!_roundEndReportBroadcast)
            {
                // #438: the latch closes on a confirmed send, never before it. Latching first
                // meant a broadcast that silently failed - no messaging manager, a swallowed
                // write error - left every client without the authoritative quota forever.
                _roundEndReportPayload = BuildRoundEndReport(_pending);
                _hasRoundEndReportPayload = true;
                _nextRoundEndBroadcastRetryAt = 0f;
                _roundEndBroadcastGiveUpAt = Time.realtimeSinceStartup + RoundEndBroadcastRetryWindowSeconds;
                TryBroadcastRoundEndReport("round end");
            }
        }

        /// <summary>
        /// Per-frame retry for a round-end report whose first send did not land (#438). Driven
        /// from <c>MoonContractState.UpdateActiveContractMechanics</c>, which is the only
        /// always-running update path this state has; it gives up after
        /// <see cref="RoundEndBroadcastRetryWindowSeconds"/> so a permanently unsendable payload
        /// cannot retry for the rest of the session.
        /// </summary>
        internal static void TickPendingRoundEndBroadcast()
        {
            if (!_hasRoundEndReportPayload || _roundEndReportBroadcast)
                return;

            float now = Time.realtimeSinceStartup;
            if (now < _nextRoundEndBroadcastRetryAt)
                return;

            _nextRoundEndBroadcastRetryAt = now + RoundEndBroadcastRetryIntervalSeconds;
            if (TryBroadcastRoundEndReport("retry"))
                return;

            if (now >= _roundEndBroadcastGiveUpAt)
            {
                _hasRoundEndReportPayload = false;
                _roundEndReportPayload = default;
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.Quota] Gave up mirroring the round-end quota report; clients keep their last synced quota.");
            }
        }

        private static bool TryBroadcastRoundEndReport(string reason)
        {
            if (!_hasRoundEndReportPayload)
                return true;

            if (!QuotaSyncNetwork.BroadcastRoundEndReport(_roundEndReportPayload))
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Quota] Round-end quota report not mirrored yet ({reason}); will retry.");
                return false;
            }

            _roundEndReportBroadcast = true;
            _hasRoundEndReportPayload = false;
            _roundEndReportPayload = default;
            return true;
        }

        private static void ApplyHazardPayModifier(TimeOfDay time)
        {
            if (_pending.HazardPayChecked
                || !_pending.HasContract
                || !_pending.Completed
                || !QuotaOverhaulRules.IsEnabled)
            {
                return;
            }

            _pending.HazardPayChecked = true;
            if (!QuotaOverhaulRules.TryGetHazardPayPercentForCurrentMoon(out int percent, out string weatherName))
                return;

            int currentQuota = Mathf.Max(0, time.profitQuota);
            int reward = currentQuota <= 0 || percent <= 0 ? 0 : Mathf.Max(1, Mathf.CeilToInt(currentQuota * (percent / 100f)));
            int newQuota = QuotaOverhaulRules.ClampQuotaTarget(currentQuota - reward, "hazard pay");

            time.profitQuota = newQuota;
            time.UpdateProfitQuotaCurrentTime();

            _pending.HazardPayApplied = true;
            _pending.HazardPayWeather = string.IsNullOrWhiteSpace(weatherName) ? "Eclipse" : weatherName.Trim();
            _pending.HazardPayPercent = percent;
            _pending.HazardPayAmount = reward;
            _pending.RevisedQuota = newQuota;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Hazard Pay ({_pending.HazardPayWeather}) reduced quota by ${reward} to ${newQuota}.");
        }

        private static void ApplyCrewLossQuotaPenalty(TimeOfDay time)
        {
            if (_pending.CrewLossApplied || !_pending.CrewLossCaptured || !QuotaOverhaulRules.IsEnabled)
                return;

            _pending.CrewLossApplied = true;
            float fraction = QuotaOverhaulRules.CalculateDeathPenaltyFraction(
                _pending.CrewLossPlayersDead,
                _pending.CrewLossBodiesInsured,
                _pending.CrewLossPlayerCount);
            // Companion seam (Y4NGZUpgrades' Veteran): may reduce the fraction,
            // never increase it. Null-safe and exception-safe inside the helper.
            fraction = QuotaOverhaulRules.ApplyDeathPenaltyFractionModifier(fraction);
            _pending.CrewLossPenaltyFraction = fraction;

            int currentQuota = Mathf.Max(0, time.profitQuota);
            int penalty = currentQuota <= 0 || fraction <= 0f ? 0 : Mathf.Max(1, Mathf.CeilToInt(currentQuota * fraction));
            if (penalty <= 0)
            {
                _pending.RevisedQuota = currentQuota;
                return;
            }

            int newQuota = QuotaOverhaulRules.ClampQuotaTarget(currentQuota + penalty, "crew loss penalty");
            time.profitQuota = newQuota;
            time.UpdateProfitQuotaCurrentTime();

            _pending.CrewLossPenaltyAmount = penalty;
            _pending.RevisedQuota = newQuota;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Crew losses increased quota by ${penalty} to ${newQuota}.");
        }

        internal static void QueueShipNotification(MonoBehaviour host, float initialDelay = 1.25f, bool waitForQuota = true)
        {
            if (host == null || !_hasPending || !_pending.HasContract || _notificationQueued)
            {
                // #944 S11 U6: the only call site is the StartOfRound.AllPlayersHaveRevivedClientRpc
                // postfix (MoonContractPatches.cs:1503), and on the host a ClientRpc method body runs
                // twice - the send and the local invoke - so this printed twice on the same
                // millisecond (run 1 lines 39848-39849, run 2 lines 46429-46430, identical
                // timestamps). Same frame, so the frame counter is the exact discriminator: two
                // genuinely separate revive events cannot share one.
                if ((!_hasPending || !_pending.HasContract) && _noPendingLoggedFrame != Time.frameCount)
                {
                    _noPendingLoggedFrame = Time.frameCount;
                    ContractsBootstrap.Log?.LogDebug("[MoonContracts] No pending round-end contract notification to show.");
                }
                return;
            }

            if (ShouldMergeWithPenaltyPopup())
            {
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Holding contract notification for merged penalty report.");
                return;
            }

            var outcome = _pending;
            _hasPending = false;
            _notificationQueued = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Queued round-end notification for {outcome.DisplayName}.");
            host.StartCoroutine(ShowShipNotification(outcome, Mathf.Max(0f, initialDelay), waitForQuota));
        }

        internal static void ApplyToPerformanceReport(HUDManager hud)
        {
            ContractsBootstrap.Log?.LogDebug("[MoonContracts.ReportDiag] Immediate performance report apply requested.");
            BeginPerformanceReportDelayIfNeeded(hud, "immediate apply");
            if (!TryApplyToPerformanceReport(hud, logMissing: true))
                ContractPerformanceReportUi.CancelPerformanceReportDelay(hud, "immediate apply failed");
        }

        internal static void BeginPerformanceReportDelayIfNeeded(HUDManager hud, string reason)
        {
            if (hud == null || !_hasPending || !ShouldRenderPerformanceReport(_pending))
                return;

            ContractPerformanceReportUi.BeginPerformanceReportDelay(
                hud,
                reason,
                DescribeOutcome(_pending));
        }

        internal static void QueuePerformanceReportApply(HUDManager hud)
        {
            if (hud == null)
            {
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.ReportDiag] QueuePerformanceReportApply skipped: hud=null.");
                return;
            }

            if (!_hasPending)
            {
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.ReportDiag] QueuePerformanceReportApply has no pending report; restoring vanilla report UI.");
                ContractPerformanceReportUi.Restore(hud);
                ContractPerformanceReportUi.CancelPerformanceReportDelay(hud, "queue requested with no pending report");
                return;
            }

            if (_performanceReportApplyCoroutine != null && _performanceReportApplyHost == hud)
            {
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.ReportDiag] QueuePerformanceReportApply skipped: retry coroutine already running.");
                return;
            }

            if (_performanceReportApplyCoroutine != null && _performanceReportApplyHost != null)
            {
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.ReportDiag] QueuePerformanceReportApply stopping previous retry coroutine on old HUD host.");
                _performanceReportApplyHost.StopCoroutine(_performanceReportApplyCoroutine);
            }

            ContractsBootstrap.Log?.LogDebug(
                "[MoonContracts.ReportDiag] QueuePerformanceReportApply queued: " +
                $"pending={DescribeOutcome(_pending)}.");
            BeginPerformanceReportDelayIfNeeded(hud, "performance report apply queued");
            _performanceReportApplyHost = hud;
            _performanceReportApplyCoroutine = hud.StartCoroutine(ApplyToPerformanceReportWhenReady(hud));
        }

        private static IEnumerator ApplyToPerformanceReportWhenReady(HUDManager hud)
        {
            yield return new WaitForEndOfFrame();

            const int MaxAttempts = 90;
            bool applied = false;
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (hud == null)
                {
                    ContractsBootstrap.Log?.LogDebug($"[MoonContracts.ReportDiag] Retry coroutine stopped at attempt {attempt}: hud=null.");
                    break;
                }

                bool lastAttempt = attempt == MaxAttempts - 1;
                if (TryApplyToPerformanceReport(hud, logMissing: lastAttempt))
                {
                    applied = true;
                    ContractsBootstrap.Log?.LogDebug($"[MoonContracts.ReportDiag] Retry coroutine applied report at attempt {attempt + 1}/{MaxAttempts}.");
                    break;
                }

                if (attempt == 0 || lastAttempt)
                    ContractsBootstrap.Log?.LogDebug($"[MoonContracts.ReportDiag] Retry coroutine attempt {attempt + 1}/{MaxAttempts} did not apply report.");

                yield return null;
            }

            if (hud != null && _hasPending)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Performance report hierarchy never became ready; leaving vanilla report unchanged.");
                ContractPerformanceReportUi.CancelPerformanceReportDelay(hud, "performance report hierarchy never ready");
            }
            else if (!applied)
            {
                ContractPerformanceReportUi.CancelPerformanceReportDelay(hud, "performance report retry stopped before apply");
            }

            _performanceReportApplyCoroutine = null;
            _performanceReportApplyHost = null;
        }

        private static bool TryApplyToPerformanceReport(HUDManager hud, bool logMissing)
        {
            if (hud == null)
            {
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.ReportDiag] TryApplyToPerformanceReport skipped: hud=null.");
                return false;
            }

            if (!_hasPending)
            {
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.ReportDiag] TryApplyToPerformanceReport has no pending report; restoring vanilla report UI.");
                ContractPerformanceReportUi.Restore(hud);
                ContractPerformanceReportUi.CancelPerformanceReportDelay(hud, "try apply with no pending report");
                return true;
            }

            // #424: on a client the pending outcome is not final until the host's report
            // lands. Returning false keeps the caller's 90-frame retry loop polling instead
            // of rendering locally-computed quota numbers that would then have to change.
            // If the report never arrives the loop gives up and vanilla's report is left
            // alone, which is preferable to showing a wrong revised quota.
            if (!_hasServerReport && !QuotaOverhaulRules.IsServerRuntime())
            {
                if (logMissing)
                {
                    ContractsBootstrap.Log?.LogWarning(
                        "[MoonContracts.Quota] Host round-end quota report never arrived; leaving the vanilla performance report unchanged.");
                }

                return false;
            }

            MergeServerRoundEndReportIntoPending("performance report apply");

            var outcome = _pending;
            if (!ShouldRenderPerformanceReport(outcome))
            {
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts.ReportDiag] Performance report injection skipped: " +
                    $"no visible contract/quota modifier. pending={DescribeOutcome(outcome)}.");
                ContractPerformanceReportUi.Restore(hud);
                ContractPerformanceReportUi.CancelPerformanceReportDelay(hud, "pending report skipped");
                _pending = default;
                _hasPending = false;
                _notificationQueued = false;
                ClearRoundStartContractSnapshot("performance report skipped");
                return true;
            }

            var report = BuildPerformanceReportData(outcome);
            ContractsBootstrap.Log?.LogDebug(
                "[MoonContracts.ReportDiag] TryApplyToPerformanceReport attempting: " +
                $"pending={DescribeOutcome(outcome)}, reportTitle='{report.Title}', " +
                $"directive={DescribeBox(report.Directive)}, hazard={DescribeBox(report.HazardPay)}, " +
                $"crew={DescribeBox(report.CrewLosses)}, quota={DescribeBox(report.RevisedQuota)}.");
            if (!ContractPerformanceReportUi.TryApply(hud, report, logMissing))
            {
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.ReportDiag] TryApplyToPerformanceReport UI apply returned false.");
                return false;
            }

            _pending = default;
            _hasPending = false;
            _notificationQueued = false;
            ClearRoundStartContractSnapshot("performance report applied");
            // #716 (audit F5): Debug, because ContractPerformanceReportUi.TryApply now emits
            // the one Info summary the round end is entitled to and that line already carries
            // the contract name (it is in the report title) plus the cell count this one lacks.
            ContractsBootstrap.Log?.LogDebug(outcome.HasContract
                ? $"[MoonContracts] Injected {outcome.DisplayName} result into performance report."
                : "[MoonContracts] Injected quota overhaul details into performance report.");
            return true;
        }

        private static bool ShouldRenderPerformanceReport(Outcome outcome)
        {
            // #748: Quota "Enabled" = false switches the entire extension off - title, ledger
            // row, notes rewrite, XP and days-left holds, HUD chrome suppression. Every
            // presentation entry point routes through this predicate, so one check covers
            // them all, and the callers' existing "skipped" branch restores vanilla and clears
            // the pending outcome so Y4NGZUpgrades stops waiting on our writer.
            if (!outcome.QuotaOverhaulEnabled)
                return false;

            return outcome.HasContract
                || ShouldRenderSalvagePerformanceReport(outcome)
                || outcome.HazardPayApplied
                || outcome.CrewLossPenaltyAmount > 0;
        }

        private static bool ShouldRenderSalvagePerformanceReport(Outcome outcome)
        {
            return !outcome.HasContract
                && string.Equals(
                    outcome.DisplayName,
                    MoonContractCatalog.Get(MoonContractType.None).DisplayName,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string DescribeOutcome(Outcome outcome)
        {
            return $"hasContract={outcome.HasContract}, display='{outcome.DisplayName}', completed={outcome.Completed}, " +
                $"rewardApplied={outcome.RewardApplied}, penaltyApplied={outcome.PenaltyApplied}, " +
                $"reward={outcome.RewardAmount}, exactCredit={outcome.UsesExactQuotaCredit}, burnedValue={outcome.SuccessfulBurnValue}, " +
                $"penalty={outcome.PenaltyAmount}, quotaBefore={outcome.QuotaBeforeModifiers}, revisedQuota={outcome.RevisedQuota}, " +
                $"hazardApplied={outcome.HazardPayApplied}, crewPenalty={outcome.CrewLossPenaltyAmount}, " +
                $"quotaOverhaulEnabled={outcome.QuotaOverhaulEnabled}";
        }

        private static string DescribeBox(ContractPerformanceReportBox box)
        {
            if (box == null)
                return "<null>";

            string sign = box.HasSign ? box.IsIncrease ? "+" : "-" : string.Empty;
            string value = !string.IsNullOrWhiteSpace(box.ValueOverride)
                ? box.ValueOverride
                : $"{sign}${Mathf.Max(0, box.Amount)}";
            return $"[{box.Heading}|{box.Detail}|{value}|animate={box.Animate}]";
        }

        internal static bool ShouldMergeWithPenaltyPopup()
        {
            return _hasPending && _pending.HasContract && HasVanillaPenaltyPending();
        }

        internal static bool ShouldMergeWithPenaltyPopup(int playersDead)
        {
            var start = StartOfRound.Instance;
            return _hasPending
                && _pending.HasContract
                && playersDead > 0
                && start != null
                && !start.isChallengeFile
                && start.currentLevel != null
                && start.currentLevel.planetHasTime;
        }

        internal static void QueueMergedPenaltyNotification(
            MonoBehaviour host,
            int playersDead,
            int bodiesInsured,
            int creditsBefore,
            int creditsAfter,
            string penaltyAddition,
            string penaltyTotal)
        {
            if (host == null || !_hasPending || !_pending.HasContract || _notificationQueued)
                return;

            var outcome = _pending;
            var report = new PenaltyReport
            {
                PlayersDead = playersDead,
                BodiesInsured = bodiesInsured,
                CreditsBefore = Mathf.Max(0, creditsBefore),
                CreditsAfter = Mathf.Max(0, creditsAfter),
                PenaltyAddition = string.IsNullOrWhiteSpace(penaltyAddition)
                    ? $"{playersDead} casualties"
                    : penaltyAddition.Trim(),
                PenaltyTotal = string.IsNullOrWhiteSpace(penaltyTotal)
                    ? $"DUE: ${Mathf.Max(0, creditsBefore - creditsAfter)}"
                    : penaltyTotal.Trim(),
            };

            _pending = default;
            _hasPending = false;
            _notificationQueued = true;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Queued merged contract/penalty report for {outcome.DisplayName}.");
            host.StartCoroutine(ShowMergedPenaltyNotification(host, outcome, report));
        }

        private static bool HasVanillaPenaltyPending()
        {
            var start = StartOfRound.Instance;
            if (start == null || start.isChallengeFile || start.currentLevel == null || !start.currentLevel.planetHasTime)
                return false;

            int playersDead = Mathf.Max(0, start.connectedPlayersAmount + 1 - start.livingPlayers);
            return playersDead > 0;
        }

        private static IEnumerator ShowMergedPenaltyNotification(MonoBehaviour host, Outcome outcome, PenaltyReport report)
        {
            yield return new WaitForSeconds(0.15f);

            float hudWaitUntil = Time.time + 2f;
            while (HUDManager.Instance == null && Time.time < hudWaitUntil)
                yield return null;

            if (HUDManager.Instance == null)
            {
                _notificationQueued = false;
                yield break;
            }

            string title = BuildMergedTitle(outcome);
            string body = BuildMergedBody(outcome, report);
            bool warning = !outcome.Completed;
            ContractResultNotification.ShowCompanyReport(
                host,
                title,
                body,
                warning);

            _notificationQueued = false;
        }

        private static string BuildMergedTitle(Outcome outcome)
        {
            if (outcome.Completed)
                return $"{outcome.DisplayName.ToUpperInvariant()} \u00B7 SUCCESSFUL";

            return $"{outcome.DisplayName.ToUpperInvariant()} \u00B7 FAILURE";
        }

        private static string BuildPerformanceReportTitle(Outcome outcome)
        {
            if (!outcome.HasContract)
                return "PERFORMANCE REPORT";

            // #716 (audit E12): the vanilla title slot is one 22pt ellipsized line, and
            // "CONTRACT COMPLETE: CONTAINMENT BREACH" (37 chars) ran off the end of it, so the
            // title is subject, middle dot, status. #749: the status words are SUCCESSFUL and
            // FAILURE, the vocabulary the DIRECTIVE cell in the row below already uses;
            // FULFILLED/VOIDED named the same outcome twice on one screen.
            string contract = outcome.DisplayName.ToUpperInvariant();
            return outcome.Completed
                ? $"{contract} \u00B7 SUCCESSFUL"
                : $"{contract} \u00B7 FAILURE";
        }

        private static ContractPerformanceReportData BuildPerformanceReportData(Outcome outcome)
        {
            return new ContractPerformanceReportData
            {
                Title = BuildPerformanceReportTitle(outcome),
                HasContract = outcome.HasContract,
                Completed = outcome.Completed,
                Directive = BuildDirectiveBox(outcome),
                HazardPay = BuildHazardPayBox(outcome),
                CrewLosses = BuildCrewLossBox(outcome),
                PlayerNotes = MoonContractRoundStats.BuildPlayerNotes(
                    outcome.Type,
                    outcome.Completed,
                    outcome.CrewLossPlayersDead,
                    outcome.RevisedQuota),
                GradeOverride = MoonContractRoundStats.CalculateGrade(
                    outcome.Type,
                    outcome.HasContract,
                    outcome.Completed,
                    outcome.HazardPayApplied,
                    outcome.CrewLossPlayersDead,
                    outcome.QuotaBeforeModifiers,
                    outcome.RevisedQuota,
                    outcome.DirectivePercent),
                RevisedQuota = BuildRevisedQuotaBox(outcome),
            };
        }

        private static ContractPerformanceReportBox BuildRevisedQuotaBox(Outcome outcome)
        {
            return new ContractPerformanceReportBox
            {
                Heading = "REVISED QUOTA",
                Detail = string.Empty,
                StartAmount = Mathf.Max(0, outcome.QuotaBeforeModifiers),
                HasStartAmount = true,
                Amount = Mathf.Max(0, outcome.RevisedQuota),
                Animate = Mathf.Max(0, outcome.QuotaBeforeModifiers) != Mathf.Max(0, outcome.RevisedQuota),
                PlayConfirmWhenStatic = true,
                IsFinalQuota = true,
                Kind = ReportPerformanceBoxKind.RevisedQuota,
            };
        }

        /// <summary>
        /// True while a real round-end report is captured but not yet rendered, or its apply
        /// retry is still running. The debug preview (#1203) refuses to open over either.
        /// </summary>
        internal static bool IsPerformanceReportPendingOrApplying =>
            _hasPending || _performanceReportApplyCoroutine != null;

        /// <summary>
        /// #1203: the debug preview's report, built from sample figures by the same cell
        /// builders a real outcome goes through, so the preview's cells are the real cells.
        /// Reads and writes no round-end state: the outcome is local and <c>_pending</c> is
        /// never touched. Notes, the grade and the XP note candidates come from the sample
        /// rather than from this round's stats.
        /// </summary>
        internal static ContractPerformanceReportData BuildPreviewReportData(PerformanceReportPreviewSample sample, int slotCount)
        {
            int dead = Mathf.Max(0, sample.PlayersDead);
            var outcome = new Outcome
            {
                HasContract = true,
                DisplayName = sample.ContractName,
                Completed = true,
                DirectivePercent = sample.DirectivePercent,
                QuotaBeforeModifiers = sample.QuotaBeforeModifiers,
                RevisedQuota = sample.RevisedQuota,
                CrewLossCaptured = true,
                CrewLossApplied = dead > 0,
                CrewLossPlayersDead = dead,
                CrewLossPenaltyFraction = sample.CrewLossPenaltyFraction,
                CrewLossPenaltyAmount = dead > 0
                    ? Mathf.Max(1, Mathf.RoundToInt(sample.QuotaBeforeModifiers * sample.CrewLossPenaltyFraction))
                    : 0,
                HazardPayChecked = true,
                HazardPayApplied = sample.HazardPayApplied,
                HazardPayWeather = sample.HazardPayWeather ?? string.Empty,
                HazardPayPercent = sample.HazardPayPercent,
                QuotaOverhaulEnabled = true,
            };

            var candidates = new IReadOnlyList<PerformanceReportNoteEntry>[Mathf.Max(0, slotCount)];
            foreach (PerformanceReportPreviewRow row in sample.Rows)
            {
                if (row.Slot >= 0 && row.Slot < candidates.Length)
                    candidates[row.Slot] = row.XpNoteCandidates;
            }

            return new ContractPerformanceReportData
            {
                Title = BuildPerformanceReportTitle(outcome),
                HasContract = outcome.HasContract,
                Completed = outcome.Completed,
                Directive = BuildDirectiveBox(outcome),
                HazardPay = BuildHazardPayBox(outcome),
                CrewLosses = BuildCrewLossBox(outcome),
                RevisedQuota = BuildRevisedQuotaBox(outcome),
                PlayerNotes = sample.BuildPlayerNotes(slotCount),
                GradeOverride = sample.Grade,
                IsPreview = true,
                PreviewXpNoteCandidates = candidates,
                PreviewLocalSlot = sample.XpSlot,
            };
        }

        private static ContractPerformanceReportBox BuildDirectiveBox(Outcome outcome)
        {
            if (!outcome.HasContract)
            {
                return new ContractPerformanceReportBox
                {
                    Heading = "CONTRACT",
                    MiddleText = "SALVAGE",
                    // No directive was taken, so nothing here moved the quota either way.
                    Valence = ReportValence.Neutral,
                    ImpactPercent = 0f,
                    PlayConfirmWhenStatic = true,
                    Kind = ReportPerformanceBoxKind.Impact,
                };
            }

            if (outcome.Completed && outcome.UsesExactQuotaCredit)
            {
                return new ContractPerformanceReportBox
                {
                    Heading = "CONTRACT",
                    // #554: every directive cell qualifies its value with the outcome, so
                    // the exact-credit variant reads the same way as the percent ones. The
                    // credit attribution lives in the qualifier too - the band that used to
                    // carry it was deleted with the Detail row, and a credit is only ever
                    // burned on a success, so the one phrase states both.
                    Qualifier = "BURN CREDIT",
                    Valence = ReportValence.Benefit,
                    Amount = Mathf.Max(0, outcome.RewardAmount),
                    HasSign = true,
                    IsIncrease = false,
                    Animate = outcome.RewardAmount > 0,
                    PlayConfirmWhenStatic = true,
                    Kind = ReportPerformanceBoxKind.Amount,
                };
            }

            if (outcome.Completed)
            {
                return new ContractPerformanceReportBox
                {
                    Heading = "CONTRACT",
                    MiddleText = "SUCCESS",
                    Valence = ReportValence.Benefit,
                    ImpactPercent = Mathf.Max(0, outcome.DirectivePercent),
                    HasSign = true,
                    IsIncrease = false,
                    Animate = outcome.DirectivePercent > 0,
                    PlayConfirmWhenStatic = true,
                    Kind = ReportPerformanceBoxKind.Impact,
                };
            }

            return new ContractPerformanceReportBox
            {
                Heading = "CONTRACT",
                MiddleText = "QUOTA PENALTY",
                // A failed directive reads as a penalty whether or not a percent was
                // actually applied - the derivation would call an unapplied penalty neutral.
                Valence = ReportValence.Penalty,
                ImpactPercent = outcome.PenaltyApplied ? Mathf.Max(0, outcome.DirectivePercent) : 0f,
                HasSign = true,
                IsIncrease = true,
                Animate = outcome.PenaltyApplied && outcome.DirectivePercent > 0,
                PlayConfirmWhenStatic = true,
                Kind = ReportPerformanceBoxKind.Impact,
            };
        }

        private static ContractPerformanceReportBox BuildHazardPayBox(Outcome outcome)
        {
            if (!outcome.HazardPayApplied)
                return null;

            return new ContractPerformanceReportBox
            {
                Heading = "HAZARD PAY",
                MiddleText = outcome.HazardPayWeather.ToUpperInvariant(),
                Valence = ReportValence.Benefit,
                ImpactPercent = Mathf.Max(0, outcome.HazardPayPercent),
                HasSign = true,
                IsIncrease = false,
                Animate = outcome.HazardPayPercent > 0,
                PlayConfirmWhenStatic = true,
                Kind = ReportPerformanceBoxKind.Impact,
            };
        }

        // #556: every ledger cell now reads the same way - the effect on the quota in the
        // middle, the fact behind it along the bottom at the same weight. So the casualties
        // cell leads with the percent, like DIRECTIVE and HAZARD PAY do, and states the body
        // count underneath instead of putting it where the quota math belongs.
        private static ContractPerformanceReportBox BuildCrewLossBox(Outcome outcome)
        {
            bool penalized = outcome.CrewLossPenaltyAmount > 0;
            int dead = Mathf.Max(0, outcome.CrewLossPlayersDead);
            return new ContractPerformanceReportBox
            {
                Heading = "CASUALTIES",
                // #747: the heading already says what is counted; the band is the count alone.
                // With nobody dead there is no percent worth stating either: the cell is the
                // heading over a bare "0" and nothing else.
                Qualifier = dead > 0 ? dead.ToString(CultureInfo.InvariantCulture) : null,
                ValueOverride = dead > 0 ? null : "0",
                // The body count is the fact the danger hue belongs to, and the count now
                // lives in the qualifier band, so the band carries the valence. Deriving the
                // hue from ImpactPercent alone painted a crew that died without an applied
                // penalty in the muted "nothing happened" grey; see
                // docs/UI_PRESENTATION_GUIDELINES.md - deaths keep the semantic danger color.
                QualifierCarriesValence = true,
                Valence = dead > 0 ? ReportValence.Penalty : ReportValence.Neutral,
                ImpactPercent = penalized ? Mathf.Max(0f, outcome.CrewLossPenaltyFraction * 100f) : 0f,
                ShowSignAtZero = true,
                HasSign = true,
                IsIncrease = true,
                Animate = penalized && outcome.CrewLossPenaltyFraction > 0f,
                PlayConfirmWhenStatic = true,
                Kind = ReportPerformanceBoxKind.Impact,
            };
        }

        private static string FormatPercent(float fraction)
        {
            float percent = Mathf.Max(0f, fraction * 100f);
            return Mathf.Approximately(percent, Mathf.Round(percent))
                ? $"{Mathf.RoundToInt(percent).ToString(CultureInfo.InvariantCulture)}%"
                : $"{percent.ToString("0.#", CultureInfo.InvariantCulture)}%";
        }

        private static string BuildPerformanceReportQuotaLine(Outcome outcome)
        {
            if (outcome.Completed)
            {
                return outcome.RewardAmount > 0
                    ? $"CONTRACT QUOTA CREDIT \u00B7 -${outcome.RewardAmount} | NEW QUOTA \u00B7 ${outcome.NewQuota}"
                    : "CONTRACT QUOTA CREDIT \u00B7 CLEARED BY THE COMPANY";
            }

            if (outcome.PenaltyApplied)
                return $"CONTRACT QUOTA PENALTY \u00B7 +${outcome.PenaltyAmount} | NEW QUOTA \u00B7 ${outcome.NewQuota}";

            return "CONTRACT QUOTA \u00B7 NO ADJUSTMENT | RECOVER SCRAP AND EXTRACT";
        }


        private static string BuildMergedBody(Outcome outcome, PenaltyReport report)
        {
            string contractLine;
            if (outcome.Completed)
            {
                contractLine = outcome.RewardAmount > 0
                    ? $"QUOTA REDUCED \u00B7 ${outcome.RewardAmount}\nNEW QUOTA \u00B7 ${outcome.NewQuota}"
                    : "CONTRACT SETTLED \u00B7 NO QUOTA CHANGE";
            }
            else if (outcome.PenaltyApplied)
            {
                contractLine = $"QUOTA INCREASED \u00B7 ${outcome.PenaltyAmount}\nNEW QUOTA \u00B7 ${outcome.NewQuota}";
            }
            else
            {
                // #716 (audit E6): the one prose sentence in a card whose other seven lines
                // are LABEL \u00B7 VALUE telemetry. Same wording the report's own quota line uses.
                contractLine = "CONTRACT FAILURE \u00B7 RECOVER SCRAP AND EXTRACT";
            }

            string casualties = NormalizePenaltyText(report.PenaltyAddition);
            string total = NormalizePenaltyText(report.PenaltyTotal);
            int creditDelta = Mathf.Max(0, report.CreditsBefore - report.CreditsAfter);

            return $"{contractLine}\n" +
                $"CASUALTIES \u00B7 {casualties}\n" +
                $"FINE TOTAL \u00B7 {total}\n" +
                $"CREDITS \u00B7 ${report.CreditsBefore} -> ${report.CreditsAfter} (-${creditDelta})";
        }

        private static string NormalizePenaltyText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return text.Replace("\r\n", " ")
                .Replace('\n', ' ')
                .Replace('\r', ' ')
                .Trim();
        }

        private static IEnumerator ShowShipNotification(Outcome outcome, float initialDelay, bool waitForQuota)
        {
            if (initialDelay > 0f)
                yield return new WaitForSeconds(initialDelay);

            while (waitForQuota && HUDManager.Instance != null && HUDManager.Instance.displayingNewQuota)
                yield return null;

            if (waitForQuota)
                yield return new WaitForSeconds(0.5f);

            float hudWaitUntil = Time.time + 2f;
            while (HUDManager.Instance == null && Time.time < hudWaitUntil)
                yield return null;

            if (HUDManager.Instance == null)
            {
                _notificationQueued = false;
                yield break;
            }

            if (outcome.Completed)
            {
                ContractResultNotification.ShowCompleted(
                    StartOfRound.Instance,
                    outcome.DisplayName,
                    outcome.RewardAmount,
                    outcome.NewQuota);
            }
            else
            {
                ContractResultNotification.ShowFailed(
                    StartOfRound.Instance,
                    outcome.DisplayName);
            }

            _notificationQueued = false;
        }
    }
}
