using System;
using System.Collections.Generic;

using Unity.Collections;
using Unity.Netcode;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Host-authoritative fan-out for contract progression events. Defuse and Payload own
    /// NetworkBehaviours and publish through their own ClientRpcs; every other sub-objective is
    /// resolved in static contract state, so it rides this named-message channel instead. Either
    /// way the outcome is identical: the host decides once, and every machine raises the same
    /// <see cref="ContractProgressionEvent"/> locally so a subscriber can filter to its own player.
    /// </summary>
    internal static class ContractProgressionNetwork
    {
        private const string ProgressionMessage = "Y4NGZCompany.ContractProgression.v1";
        // #406: a client asks for this round's ledger the moment its handler exists. Named
        // messages sent before that registration were dropped on the floor with no replay, so a
        // client that finished loading late simply never saw the sub-objectives it had earned.
        private const string ReplayRequestMessage = "Y4NGZCompany.ContractProgression.Replay.v1";
        private const int MaxTextLength = 96;
        private const int WriterCapacity = 1024;
        /// <summary>Bound on the host's per-round replay ledger; a round cannot plausibly exceed it.</summary>
        private const int MaxReplayEvents = 256;
        // #435: the messaging manager, not the NetworkManager, is what handlers live on. NGO
        // builds a new one per StartHost/StartClient while the singleton persists, so a
        // NetworkManager-keyed latch silently skips registration in a session's second lobby.
        private static CustomMessagingManager _registeredMessaging;

        /// <summary>
        /// Host-side: every event published this round, in publish order, capped.
        ///
        /// The ledger is keyed by round id rather than cleared from a round-lifecycle callback.
        /// The obvious hook - <c>MoonContractState.ResetRunState</c> - runs inside
        /// <c>ShipLeave_Postfix</c> in the same call stack that has just published the round's
        /// ContractCompleted/ContractFailed events, so clearing there would delete precisely the
        /// events a client is most likely to still be missing, and make their bookkeeping dead
        /// work. Keying instead lets the ledger survive the whole end-of-round window and expire
        /// when the next round's first event arrives.
        /// </summary>
        private static readonly List<ContractProgressionEvent> ReplayLedger = new List<ContractProgressionEvent>(MaxReplayEvents);
        private static bool _replayLedgerOverflowed;
        private static string _ledgerRoundId = string.Empty;

        /// <summary>Client-side: event ids already raised locally, so a replay never double-fires.</summary>
        private static readonly HashSet<string> ReceivedEventIds = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>
        /// Client-side replay request bookkeeping. The first attempt can land before the host has
        /// registered its own handler, which would drop it exactly the way this whole mechanism
        /// exists to prevent, so it is retried - but only until the host answers. The host
        /// acknowledges every request it serves, including an empty one, so a satisfied client
        /// stops asking and the host never re-serializes a ledger it has already sent.
        /// </summary>
        private const int MaxReplayRequests = 4;
        private const float ReplayRequestRetrySeconds = 4f;
        private static int _replayRequests;
        private static float _nextReplayRequestAt;
        private static bool _replaySatisfied;
        private static string _clientRoundId = string.Empty;

        internal static void EnsureNetworkHandlers()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || network.CustomMessagingManager == null || !network.IsListening)
                return;

            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (ReferenceEquals(messaging, _registeredMessaging))
            {
                RequestReplay(network, messaging);
                return;
            }

            if (_registeredMessaging != null)
            {
                try { _registeredMessaging.UnregisterNamedMessageHandler(ProgressionMessage); }
                catch { }
                try { _registeredMessaging.UnregisterNamedMessageHandler(ReplayRequestMessage); }
                catch { }
            }

            try
            {
                messaging.RegisterNamedMessageHandler(ProgressionMessage, OnProgressionMessage);
                messaging.RegisterNamedMessageHandler(ReplayRequestMessage, OnReplayMessage);
                _registeredMessaging = messaging;
                // A new messaging manager is a new lobby: neither side's bookkeeping carries over.
                ResetLedger();
                ResetClientReplayState(ContractProgressionEvents.CurrentRoundId());
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.Progression] Progression message handlers registered.");
                RequestReplay(network, messaging);
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Progression] Handler registration skipped: {ex.Message}");
            }
        }

        private static void ResetLedger()
        {
            ReplayLedger.Clear();
            _replayLedgerOverflowed = false;
            _ledgerRoundId = string.Empty;
        }

        /// <summary>A new round is a new ledger to ask for, and a fresh set of ids to dedupe.</summary>
        private static void ResetClientReplayState(string roundId)
        {
            _clientRoundId = roundId ?? string.Empty;
            ReceivedEventIds.Clear();
            _replayRequests = 0;
            _nextReplayRequestAt = 0f;
            _replaySatisfied = false;
        }

        private static void RequestReplay(NetworkManager network, CustomMessagingManager messaging)
        {
            if (network.IsServer || messaging == null || !network.IsConnectedClient)
                return;

            // #716/B13: this runs every frame on every client. The round-change check cannot
            // move below the _replaySatisfied early-out — a satisfied client would then never
            // notice the next round and never ask for its ledger — so the fix is on the other
            // side: CurrentRoundId() now compares the seed and level ints and only rebuilds
            // the string when they actually move, and the comparison below short-circuits on
            // reference equality for the cached instance.
            string roundId = ContractProgressionEvents.CurrentRoundId();
            if (!string.Equals(roundId, _clientRoundId, StringComparison.Ordinal))
                ResetClientReplayState(roundId);

            if (_replaySatisfied
                || _replayRequests >= MaxReplayRequests
                || UnityEngine.Time.realtimeSinceStartup < _nextReplayRequestAt)
            {
                return;
            }

            _replayRequests++;
            _nextReplayRequestAt = UnityEngine.Time.realtimeSinceStartup + ReplayRequestRetrySeconds;

            var writer = new FastBufferWriter(WriterCapacity, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(ContractProgressionEvents.CurrentSchemaVersion);
                WriteText(ref writer, roundId);
                messaging.SendNamedMessage(ReplayRequestMessage, NetworkManager.ServerClientId, writer);
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.Progression] Requested round '{roundId}' progression replay from the host (attempt {_replayRequests}/{MaxReplayRequests}).");
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Progression] Replay request deferred: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>
        /// One message, both directions. Client to host it is a request; host to client it is the
        /// acknowledgement that follows the replayed events on the same reliable channel, which is
        /// what stops the client retrying.
        /// </summary>
        private static void OnReplayMessage(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null)
                return;

            string roundId;
            try
            {
                reader.ReadValueSafe(out int schemaVersion);
                if (schemaVersion != ContractProgressionEvents.CurrentSchemaVersion)
                    return;
                ReadText(ref reader, out roundId);
            }
            catch
            {
                return;
            }

            if (!network.IsServer)
            {
                if (senderClientId != NetworkManager.ServerClientId)
                    return;

                _replaySatisfied = true;
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.Progression] Host acknowledged the replay for round '{roundId}'; no further requests.");
                return;
            }

            if (senderClientId == NetworkManager.ServerClientId)
                return;

            CustomMessagingManager manager = network.CustomMessagingManager;
            if (manager == null)
                return;

            // A request for a round this ledger no longer holds is answered with an empty replay
            // rather than the wrong round's events. The acknowledgement still goes out, so the
            // client stops asking instead of burning its retries on a ledger that cannot help it.
            bool roundMatches = string.Equals(roundId, _ledgerRoundId, StringComparison.Ordinal);
            int sent = 0;
            if (roundMatches)
            {
                for (int i = 0; i < ReplayLedger.Count; i++)
                {
                    if (SendToClient(manager, senderClientId, ReplayLedger[i]))
                        sent++;
                }
            }

            SendReplayAcknowledgement(manager, senderClientId, _ledgerRoundId);
            ContractsBootstrap.Log?.LogDebug(
                $"[MoonContracts.Progression] Replayed {sent}/{ReplayLedger.Count} progression event(s) to client {senderClientId} "
                + (roundMatches
                    ? "for the current round"
                    : $"(client asked for round '{roundId}', ledger holds '{_ledgerRoundId}')")
                + (_replayLedgerOverflowed ? "; ledger overflowed this round, the newest events were not retained." : "."));
        }

        private static void SendReplayAcknowledgement(CustomMessagingManager manager, ulong clientId, string roundId)
        {
            var writer = new FastBufferWriter(WriterCapacity, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(ContractProgressionEvents.CurrentSchemaVersion);
                WriteText(ref writer, roundId);
                manager.SendNamedMessage(ReplayRequestMessage, clientId, writer);
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Progression] Replay acknowledgement failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>
        /// Publishes locally and mirrors to every client. Silently does nothing on a client:
        /// only the host may author a progression event.
        /// </summary>
        internal static void BroadcastServer(ContractProgressionEvent progressionEvent)
        {
            if (progressionEvent == null)
                return;

            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsListening)
            {
                // No live session (offline harness): there is nothing to mirror to.
                ContractProgressionEvents.Publish(progressionEvent);
                return;
            }

            if (!network.IsServer)
                return;

            ContractProgressionEvents.Publish(progressionEvent);
            RecordForReplay(progressionEvent);
            SendToClients(network, progressionEvent);
        }

        /// <summary>
        /// Keeps the host's copy of this round's events so a client that registered its handler
        /// after the send can ask for them. Bounded: past the cap the ledger stops growing and
        /// says so once, rather than holding a round's worth of allocations open indefinitely.
        /// </summary>
        private static void RecordForReplay(ContractProgressionEvent progressionEvent)
        {
            // Round-keyed expiry: the previous round's ledger is dropped by the first event of the
            // next one, so it stays available right through the end-of-round window.
            string roundId = ContractProgressionEvents.CurrentRoundId();
            if (!string.Equals(roundId, _ledgerRoundId, StringComparison.Ordinal))
            {
                ReplayLedger.Clear();
                _replayLedgerOverflowed = false;
                _ledgerRoundId = roundId;
            }

            if (ReplayLedger.Count >= MaxReplayEvents)
            {
                if (!_replayLedgerOverflowed)
                {
                    _replayLedgerOverflowed = true;
                    ContractsBootstrap.Log?.LogDebug(
                        $"[MoonContracts.Progression] Replay ledger hit its {MaxReplayEvents}-event cap; later events are mirrored live but not replayed.");
                }

                return;
            }

            ReplayLedger.Add(progressionEvent);
        }

        private static void SendToClients(NetworkManager network, ContractProgressionEvent progressionEvent)
        {
            CustomMessagingManager manager = network.CustomMessagingManager;
            if (manager == null || network.ConnectedClientsIds == null || network.ConnectedClientsIds.Count <= 1)
                return;

            var writer = new FastBufferWriter(WriterCapacity, Allocator.Temp);
            try
            {
                Write(ref writer, progressionEvent);
                manager.SendNamedMessageToAll(ProgressionMessage, writer);
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Progression] Failed to mirror event '{progressionEvent.EventId}': {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static bool SendToClient(CustomMessagingManager manager, ulong clientId, ContractProgressionEvent progressionEvent)
        {
            if (manager == null || progressionEvent == null)
                return false;

            var writer = new FastBufferWriter(WriterCapacity, Allocator.Temp);
            try
            {
                Write(ref writer, progressionEvent);
                manager.SendNamedMessage(ProgressionMessage, clientId, writer);
                return true;
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Progression] Failed to replay event '{progressionEvent.EventId}': {ex.Message}");
                return false;
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void Write(ref FastBufferWriter writer, ContractProgressionEvent progressionEvent)
        {
            writer.WriteValueSafe((int)progressionEvent.Kind);
            writer.WriteValueSafe(progressionEvent.PlayerClientId);
            writer.WriteValueSafe(progressionEvent.Count);
            writer.WriteValueSafe(progressionEvent.Seconds);
            writer.WriteValueSafe(progressionEvent.ContractSucceeded ? (byte)1 : (byte)0);
            WriteText(ref writer, progressionEvent.EventId);
            WriteText(ref writer, progressionEvent.ContractId);
            WriteText(ref writer, progressionEvent.ContractType);
            WriteText(ref writer, progressionEvent.SourceLabel);
            WriteText(ref writer, progressionEvent.RiskLevel);
            // #1201: appended after the last string, so the message name stays .v1. An older
            // client stops reading before it and ignores it; an older host never sends it and the
            // reader below defaults it to false. Replay re-writes the stored event, so a late
            // joiner gets the flag too.
            writer.WriteValueSafe(progressionEvent.CrewWide ? (byte)1 : (byte)0);
        }

        private static void OnProgressionMessage(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId)
                return;
            // The host already published its own copy before sending.
            if (NetworkManager.Singleton?.IsServer == true)
                return;

            try
            {
                reader.ReadValueSafe(out int kind);
                reader.ReadValueSafe(out ulong playerClientId);
                reader.ReadValueSafe(out int count);
                reader.ReadValueSafe(out float seconds);
                reader.ReadValueSafe(out byte succeeded);
                ReadText(ref reader, out string eventId);
                ReadText(ref reader, out string contractId);
                ReadText(ref reader, out string contractType);
                ReadText(ref reader, out string sourceLabel);
                ReadText(ref reader, out string riskLevel);
                bool crewWide = false;
                if (reader.TryBeginRead(1))
                {
                    reader.ReadByte(out byte crewWideByte);
                    crewWide = crewWideByte != 0;
                }

                if (!Enum.IsDefined(typeof(ContractProgressionEventKind), kind))
                    return;

                // Every id on this channel is unique per (round, occurrence, credited player), so
                // an id already seen here can only be the replay overlapping the live mirror.
                if (!string.IsNullOrEmpty(eventId) && !ReceivedEventIds.Add(eventId))
                    return;

                ContractProgressionEvents.Publish(new ContractProgressionEvent
                {
                    SchemaVersion = ContractProgressionEvents.CurrentSchemaVersion,
                    EventId = eventId,
                    Kind = (ContractProgressionEventKind)kind,
                    PlayerClientId = playerClientId,
                    ContractId = contractId,
                    ContractType = contractType,
                    SourceLabel = sourceLabel,
                    RiskLevel = riskLevel,
                    Count = count,
                    Seconds = seconds,
                    ContractSucceeded = succeeded != 0,
                    CrewWide = crewWide
                });
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Progression] Malformed progression message: {ex.Message}");
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
