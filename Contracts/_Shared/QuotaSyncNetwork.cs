using System;

using Unity.Collections;
using Unity.Netcode;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Every field of the round-end quota report the host computes once and every
    /// machine renders. Purely presentational on the receiving side except
    /// <see cref="RevisedQuota"/>, which is the authoritative post-modifier quota.
    /// </summary>
    internal struct QuotaRoundEndReport
    {
        internal bool HasContract;
        internal int ContractType;
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
        internal bool HazardPayApplied;
        internal string HazardPayWeather;
        internal int HazardPayPercent;
        internal int HazardPayAmount;
        internal bool CrewLossCaptured;
        internal int CrewLossPlayersDead;
        internal int CrewLossBodiesInsured;
        internal int CrewLossPlayerCount;
        internal float CrewLossPenaltyFraction;
        internal int CrewLossPenaltyAmount;
        // #748: appended to the wire format as a trailing byte. A report from a host that
        // predates it simply ends early and the reader defaults this to true, so a mixed
        // session degrades to the old behaviour rather than dropping the whole report.
        internal bool QuotaOverhaulEnabled;
    }

    /// <summary>
    /// Host-authoritative quota replication (#424). The quota overhaul used to recompute
    /// the end-of-round quota independently on every machine from inputs that are not the
    /// same everywhere (per-machine config, local objective completion, local livingPlayers,
    /// per-machine recorded player counts, companion-mod penalty modifiers), and clients
    /// additionally persisted the result into their own solo save slot. Now the host decides
    /// once and mirrors the decision here; clients only render and apply what they receive.
    ///
    /// Two messages ride this channel:
    /// <list type="bullet">
    /// <item><description><c>QuotaRoundEnd.v1</c> - the full round-end report (quota plus every
    /// presentational field the performance report consumes).</description></item>
    /// <item><description><c>QuotaValue.v1</c> - a bare authoritative quota, sent whenever the
    /// host's overhaul rules rewrite the quota outside the round-end path (new run, first-quota
    /// player rescaling on join/disconnect, rollover). Vanilla only replicates the quota to the
    /// player who is joining, so in-lobby clients would otherwise keep a stale first quota.</description></item>
    /// </list>
    /// Registration and validation mirror <see cref="ContractProgressionNetwork"/>.
    /// </summary>
    internal static class QuotaSyncNetwork
    {
        private const string RoundEndMessage = "Y4NGZCompany.QuotaRoundEnd.v1";
        private const string QuotaValueMessage = "Y4NGZCompany.QuotaValue.v1";
        private const int MaxTextLength = 64;
        private const int WriterCapacity = 512;

        // #435: NGO replaces CustomMessagingManager on every StartHost/StartClient while
        // NetworkManager.Singleton lives for the whole process, so a NetworkManager-keyed latch
        // never re-registers in the second lobby of a session. Latch the messaging manager.
        private static CustomMessagingManager _registeredMessaging;

        internal static void EnsureNetworkHandlers()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || network.CustomMessagingManager == null || !network.IsListening)
                return;

            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (ReferenceEquals(messaging, _registeredMessaging))
                return;

            if (_registeredMessaging != null)
            {
                try { _registeredMessaging.UnregisterNamedMessageHandler(RoundEndMessage); }
                catch { }
                try { _registeredMessaging.UnregisterNamedMessageHandler(QuotaValueMessage); }
                catch { }
            }

            try
            {
                messaging.RegisterNamedMessageHandler(RoundEndMessage, OnRoundEndMessage);
                messaging.RegisterNamedMessageHandler(QuotaValueMessage, OnQuotaValueMessage);
                _registeredMessaging = messaging;
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.Quota] Quota sync message handlers registered.");
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Quota] Handler registration skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Mirrors the host's round-end report to every client. Does nothing without a live
        /// session (offline harness/solo: the host already holds the only copy) and nothing
        /// at all on a client.
        ///
        /// Returns true when the report reached the wire, or when there was nobody to mirror it
        /// to. Returns false only when a real audience existed and the send did not happen, so
        /// the caller (#438) knows to keep its "broadcast" latch open and retry.
        /// </summary>
        internal static bool BroadcastRoundEndReport(QuotaRoundEndReport report)
        {
            if (!TryGetSendManager(out CustomMessagingManager manager, out bool noAudience))
                return noAudience;

            var writer = new FastBufferWriter(WriterCapacity, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(report.HasContract ? (byte)1 : (byte)0);
                writer.WriteValueSafe(report.ContractType);
                writer.WriteValueSafe(report.Completed ? (byte)1 : (byte)0);
                writer.WriteValueSafe(report.RewardApplied ? (byte)1 : (byte)0);
                writer.WriteValueSafe(report.RewardAmount);
                writer.WriteValueSafe(report.UsesExactQuotaCredit ? (byte)1 : (byte)0);
                writer.WriteValueSafe(report.SuccessfulBurnValue);
                writer.WriteValueSafe(report.PenaltyApplied ? (byte)1 : (byte)0);
                writer.WriteValueSafe(report.PenaltyAmount);
                writer.WriteValueSafe(report.QuotaBeforeModifiers);
                writer.WriteValueSafe(report.NewQuota);
                writer.WriteValueSafe(report.DirectivePercent);
                writer.WriteValueSafe(report.RevisedQuota);
                writer.WriteValueSafe(report.HazardPayApplied ? (byte)1 : (byte)0);
                writer.WriteValueSafe(report.HazardPayPercent);
                writer.WriteValueSafe(report.HazardPayAmount);
                WriteText(ref writer, report.HazardPayWeather);
                writer.WriteValueSafe(report.CrewLossCaptured ? (byte)1 : (byte)0);
                writer.WriteValueSafe(report.CrewLossPlayersDead);
                writer.WriteValueSafe(report.CrewLossBodiesInsured);
                writer.WriteValueSafe(report.CrewLossPlayerCount);
                writer.WriteValueSafe(report.CrewLossPenaltyFraction);
                writer.WriteValueSafe(report.CrewLossPenaltyAmount);
                writer.WriteValueSafe(report.QuotaOverhaulEnabled ? (byte)1 : (byte)0);
                manager.SendNamedMessageToAll(RoundEndMessage, writer);
                ContractsBootstrap.Log?.LogInfo(
                    "[MoonContracts.Quota] Broadcast host round-end quota report: " +
                    $"revisedQuota={report.RevisedQuota}, before={report.QuotaBeforeModifiers}, " +
                    $"completed={report.Completed}, reward={report.RewardAmount}, penalty={report.PenaltyAmount}, " +
                    $"hazard={report.HazardPayAmount}, crew={report.CrewLossPenaltyAmount}.");
                return true;
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Quota] Failed to mirror round-end quota report: {ex.Message}");
                return false;
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>Mirrors an authoritative quota value the host just applied outside the round-end path.</summary>
        internal static void BroadcastQuotaValue(int quota, string reason)
        {
            if (!TryGetSendManager(out CustomMessagingManager manager))
                return;

            var writer = new FastBufferWriter(WriterCapacity, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(quota);
                WriteText(ref writer, reason);
                manager.SendNamedMessageToAll(QuotaValueMessage, writer);
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Quota] Broadcast host quota ${quota} ({reason}).");
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Quota] Failed to mirror quota value: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static bool TryGetSendManager(out CustomMessagingManager manager)
        {
            return TryGetSendManager(out manager, out _);
        }

        /// <summary>
        /// <paramref name="noAudience"/> distinguishes the two reasons a send is skipped: there
        /// is genuinely nobody to mirror to (offline harness, solo, or this machine is not the
        /// host - retrying would never help), versus a live multi-client session whose messaging
        /// manager is momentarily missing, which is worth retrying.
        /// </summary>
        private static bool TryGetSendManager(out CustomMessagingManager manager, out bool noAudience)
        {
            manager = null;
            noAudience = true;
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsListening || !network.IsServer)
                return false;

            if (network.ConnectedClientsIds == null || network.ConnectedClientsIds.Count <= 1)
                return false;

            noAudience = false;
            manager = network.CustomMessagingManager;
            return manager != null;
        }

        private static void OnRoundEndMessage(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId)
                return;
            // The host authored this report locally before sending it.
            if (NetworkManager.Singleton?.IsServer == true)
                return;

            try
            {
                var report = new QuotaRoundEndReport();
                reader.ReadValueSafe(out byte hasContract);
                report.HasContract = hasContract != 0;
                reader.ReadValueSafe(out int contractType);
                report.ContractType = contractType;
                reader.ReadValueSafe(out byte completed);
                report.Completed = completed != 0;
                reader.ReadValueSafe(out byte rewardApplied);
                report.RewardApplied = rewardApplied != 0;
                reader.ReadValueSafe(out int rewardAmount);
                report.RewardAmount = rewardAmount;
                reader.ReadValueSafe(out byte exactCredit);
                report.UsesExactQuotaCredit = exactCredit != 0;
                reader.ReadValueSafe(out int burnValue);
                report.SuccessfulBurnValue = burnValue;
                reader.ReadValueSafe(out byte penaltyApplied);
                report.PenaltyApplied = penaltyApplied != 0;
                reader.ReadValueSafe(out int penaltyAmount);
                report.PenaltyAmount = penaltyAmount;
                reader.ReadValueSafe(out int quotaBefore);
                report.QuotaBeforeModifiers = quotaBefore;
                reader.ReadValueSafe(out int newQuota);
                report.NewQuota = newQuota;
                reader.ReadValueSafe(out int directivePercent);
                report.DirectivePercent = directivePercent;
                reader.ReadValueSafe(out int revisedQuota);
                report.RevisedQuota = revisedQuota;
                reader.ReadValueSafe(out byte hazardApplied);
                report.HazardPayApplied = hazardApplied != 0;
                reader.ReadValueSafe(out int hazardPercent);
                report.HazardPayPercent = hazardPercent;
                reader.ReadValueSafe(out int hazardAmount);
                report.HazardPayAmount = hazardAmount;
                ReadText(ref reader, out string hazardWeather);
                report.HazardPayWeather = hazardWeather;
                reader.ReadValueSafe(out byte crewCaptured);
                report.CrewLossCaptured = crewCaptured != 0;
                reader.ReadValueSafe(out int crewDead);
                report.CrewLossPlayersDead = crewDead;
                reader.ReadValueSafe(out int crewInsured);
                report.CrewLossBodiesInsured = crewInsured;
                reader.ReadValueSafe(out int crewPlayers);
                report.CrewLossPlayerCount = crewPlayers;
                reader.ReadValueSafe(out float crewFraction);
                report.CrewLossPenaltyFraction = crewFraction;
                reader.ReadValueSafe(out int crewPenalty);
                report.CrewLossPenaltyAmount = crewPenalty;
                // #748: trailing field; absent from a pre-#748 host's report.
                report.QuotaOverhaulEnabled = true;
                if (reader.TryBeginRead(1))
                {
                    reader.ReadByte(out byte quotaOverhaulEnabled);
                    report.QuotaOverhaulEnabled = quotaOverhaulEnabled != 0;
                }

                MoonContractRoundEndState.ApplyServerRoundEndReport(report);
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Quota] Malformed round-end quota report: {ex.Message}");
            }
        }

        private static void OnQuotaValueMessage(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId)
                return;
            if (NetworkManager.Singleton?.IsServer == true)
                return;

            try
            {
                reader.ReadValueSafe(out int quota);
                ReadText(ref reader, out string reason);
                QuotaOverhaulRules.ApplyReplicatedQuota(quota, reason);
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Quota] Malformed quota value message: {ex.Message}");
            }
        }

        private static void WriteText(ref FastBufferWriter writer, string value)
        {
            string text = value ?? string.Empty;
            if (text.Length > MaxTextLength)
                text = text.Substring(0, MaxTextLength);

            writer.WriteValueSafe(text.Length);
            for (int i = 0; i < text.Length; i++)
                writer.WriteValueSafe((ushort)text[i]);
        }

        private static void ReadText(ref FastBufferReader reader, out string value)
        {
            reader.ReadValueSafe(out int length);
            if (length < 0 || length > MaxTextLength)
            {
                value = string.Empty;
                return;
            }

            var characters = new char[length];
            for (int i = 0; i < length; i++)
            {
                reader.ReadValueSafe(out ushort character);
                characters[i] = (char)character;
            }

            value = new string(characters);
        }
    }
}
