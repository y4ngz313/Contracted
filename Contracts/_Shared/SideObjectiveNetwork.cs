using System;

using Unity.Collections;
using Unity.Netcode;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1195. Host-authoritative carriage of the side-objective snapshot, on the
    /// <see cref="ContractProgressionNetwork"/> pattern.
    ///
    /// The host is the only author: it folds each source reading into the published snapshot
    /// (<see cref="PublishHost"/>) and mirrors every change to all clients. Clients never read a
    /// source's host-only state; they apply what arrives and send the host their sightings. The
    /// state message is a whole snapshot, so it is idempotent and a late joiner needs only the latest
    /// one: a client asks for it (<see cref="ReplayMessage"/>) until the host answers, exactly as the
    /// progression ledger replay does.
    ///
    /// Wire layout, all little-endian through <see cref="FastBufferWriter"/>:
    /// <list type="bullet">
    /// <item><c>State</c> (host to client): int schema, text roundId, byte source, text scope,
    /// text entry, int count, int required, byte flags (1 completed, 2 reward claimed, 4 revealed).</item>
    /// <item><c>Replay</c> (client to host): int schema, text roundId. The host answers with State.</item>
    /// <item><c>Sighting</c> (client to host): int schema, text roundId, byte source, text scope, text entry.</item>
    /// </list>
    /// Text is an int length then UTF-16 code units, capped at <see cref="MaxTextLength"/>.
    /// </summary>
    internal static class SideObjectiveNetwork
    {
        internal const string StateMessage = "Y4NGZCompany.SideObjective.State.v1";
        internal const string ReplayMessage = "Y4NGZCompany.SideObjective.Replay.v1";
        internal const string SightingMessage = "Y4NGZCompany.SideObjective.Sighting.v1";
        private const int SchemaVersion = 1;
        private const int MaxTextLength = 64;
        private const int WriterCapacity = 512;

        private const byte FlagCompleted = 1;
        private const byte FlagRewardClaimed = 2;
        private const byte FlagRevealed = 4;

        private const int MaxReplayRequests = 6;
        private const float ReplayRequestRetrySeconds = 3f;

        /// <summary>The messaging manager, not the NetworkManager, carries the handlers (#435).</summary>
        private static CustomMessagingManager _registeredMessaging;

        private static readonly SideObjectivePublication Published = new SideObjectivePublication();
        private static SideObjectiveSnapshot _current;

        // Client-side replay bookkeeping: asked until any state for this round has been applied.
        private static string _clientRoundId = string.Empty;
        private static bool _stateAppliedThisRound;
        private static int _replayRequests;
        private static float _nextReplayRequestAt;

        // Client-side sighting throttle.
        private static float _nextSightingSendAt;

        /// <summary>The snapshot this peer last applied: its own on the host, the host's on a client.</summary>
        internal static SideObjectiveSnapshot Current => _current;

        internal static SideObjectivePublication Publication => Published;

        internal static bool IsServer
        {
            get
            {
                NetworkManager network = NetworkManager.Singleton;
                return network != null && network.IsListening && network.IsServer;
            }
        }

        /// <summary>Clears every published value and all bookkeeping. Round reset and lobby change.</summary>
        internal static void ResetRunState()
        {
            _current = SideObjectiveSnapshot.None;
            Published.Reset();
            _clientRoundId = string.Empty;
            _stateAppliedThisRound = false;
            _replayRequests = 0;
            _nextReplayRequestAt = 0f;
            _nextSightingSendAt = 0f;
        }

        /// <summary>Called every tick. A reference compare once the handlers are in place.</summary>
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
                try { _registeredMessaging.UnregisterNamedMessageHandler(StateMessage); }
                catch { }
                try { _registeredMessaging.UnregisterNamedMessageHandler(ReplayMessage); }
                catch { }
                try { _registeredMessaging.UnregisterNamedMessageHandler(SightingMessage); }
                catch { }
            }

            try
            {
                messaging.RegisterNamedMessageHandler(StateMessage, OnStateMessage);
                messaging.RegisterNamedMessageHandler(ReplayMessage, OnReplayMessage);
                messaging.RegisterNamedMessageHandler(SightingMessage, OnSightingMessage);
                _registeredMessaging = messaging;
                // A new messaging manager is a new lobby: nothing published in the last one applies.
                SideObjectiveState.ResetRunState();
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.Side] Side objective message handlers registered.");
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Handler registration skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Host-only. Folds a source reading into the published snapshot and mirrors any change to
        /// every client. Does nothing on a client. <paramref name="reading"/> never carries a reveal
        /// of its own; the reveal latch lives in
        /// <see cref="SideObjectivePolicy.MergeHostReading"/>.
        /// </summary>
        internal static void PublishHost(in SideObjectiveSnapshot reading)
        {
            if (!IsServer)
                return;

            Commit(SideObjectivePolicy.MergeHostReading(_current, reading));
        }

        /// <summary>
        /// A local sighting on this peer. On the host it reveals for the crew at once; a client sends
        /// it to the host and re-sends at most every <see cref="SideObjectivePolicy.SightingResendSeconds"/>
        /// until the revealed state comes back.
        /// </summary>
        internal static void ReportLocalSighting(float now)
        {
            SideObjectiveSnapshot current = _current;
            if (!SideObjectivePolicy.NeedsLocalSighting(current))
                return;

            if (IsServer)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Host sighted '{current.Entry}' ({current.Scope}); revealing for the crew.");
                Commit(current.WithRevealed(true));
                return;
            }

            if (now < _nextSightingSendAt)
                return;

            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network != null && network.IsListening && network.IsConnectedClient
                ? network.CustomMessagingManager
                : null;
            if (messaging == null)
                return;

            // Armed only once there is a channel to send on, so a sighting made before the
            // connection settles is retried on the next check rather than two seconds later.
            _nextSightingSendAt = now + SideObjectivePolicy.SightingResendSeconds;

            var writer = new FastBufferWriter(WriterCapacity, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(SchemaVersion);
                WriteText(ref writer, ContractProgressionEvents.CurrentRoundId());
                writer.WriteValueSafe((byte)current.Source);
                WriteText(ref writer, current.Scope);
                WriteText(ref writer, current.Entry);
                messaging.SendNamedMessage(SightingMessage, NetworkManager.ServerClientId, writer);
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Sighted '{current.Entry}' ({current.Scope}); told the host.");
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Sighting send deferred: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>
        /// Client-only, called while the round is live. Asks the host for the current snapshot until
        /// one has been applied this round: covers a late joiner and a client whose handlers came up
        /// after the host's last change.
        /// </summary>
        internal static void TickClientReplay(float now)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || network.IsServer || !network.IsListening || !network.IsConnectedClient)
                return;

            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null || !ReferenceEquals(messaging, _registeredMessaging))
                return;

            string roundId = ContractProgressionEvents.CurrentRoundId();
            if (!string.Equals(roundId, _clientRoundId, StringComparison.Ordinal))
            {
                _clientRoundId = roundId;
                _stateAppliedThisRound = false;
                _replayRequests = 0;
                _nextReplayRequestAt = now + 1f;
            }

            if (_stateAppliedThisRound || _replayRequests >= MaxReplayRequests || now < _nextReplayRequestAt)
                return;

            _replayRequests++;
            _nextReplayRequestAt = now + ReplayRequestRetrySeconds;
            var writer = new FastBufferWriter(WriterCapacity, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(SchemaVersion);
                WriteText(ref writer, roundId);
                messaging.SendNamedMessage(ReplayMessage, NetworkManager.ServerClientId, writer);
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.Side] Requested round '{roundId}' side objective state from the host (attempt {_replayRequests}/{MaxReplayRequests}).");
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Replay request deferred: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void Commit(in SideObjectiveSnapshot snapshot)
        {
            if (snapshot.Equals(_current))
                return;

            Apply(snapshot);
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager manager = network != null ? network.CustomMessagingManager : null;
            if (manager == null || network.ConnectedClientsIds == null || network.ConnectedClientsIds.Count <= 1)
                return;

            var writer = new FastBufferWriter(WriterCapacity, Allocator.Temp);
            try
            {
                WriteState(ref writer, snapshot);
                manager.SendNamedMessageToAll(StateMessage, writer);
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Side] Failed to mirror the side objective state: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void Apply(in SideObjectiveSnapshot snapshot)
        {
            bool newObjective = !snapshot.SameObjective(_current);
            _current = snapshot;
            if (Published.Apply(snapshot) || newObjective)
            {
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.Side] {snapshot.Source} '{snapshot.Entry}' ({snapshot.Scope}) {snapshot.Count}/{snapshot.Required}"
                    + $" completed={snapshot.Completed} claimed={snapshot.RewardClaimed} revealed={snapshot.Revealed}"
                    + $" -> {Published.Phase} r{Published.Revision}.");
            }
        }

        private static void OnStateMessage(ulong senderClientId, FastBufferReader reader)
        {
            if (senderClientId != NetworkManager.ServerClientId)
                return;
            // The host applied its own copy before sending.
            if (IsServer)
                return;

            SideObjectiveSnapshot snapshot;
            string roundId;
            try
            {
                reader.ReadValueSafe(out int schema);
                if (schema != SchemaVersion)
                    return;
                ReadText(ref reader, out roundId);
                reader.ReadValueSafe(out byte source);
                ReadText(ref reader, out string scope);
                ReadText(ref reader, out string entry);
                reader.ReadValueSafe(out int count);
                reader.ReadValueSafe(out int required);
                reader.ReadValueSafe(out byte flags);
                SideObjectiveSource kind = Enum.IsDefined(typeof(SideObjectiveSource), source)
                    ? (SideObjectiveSource)source
                    : SideObjectiveSource.None;
                snapshot = new SideObjectiveSnapshot(
                    kind,
                    scope,
                    entry,
                    count,
                    required,
                    (flags & FlagCompleted) != 0,
                    (flags & FlagRewardClaimed) != 0,
                    (flags & FlagRevealed) != 0);
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Malformed side objective state: {ex.Message}");
                return;
            }

            // A message for another round, or one still in flight after this peer's round ended,
            // would resurrect a finished objective; the replay request covers anything dropped here.
            if (!string.Equals(roundId, ContractProgressionEvents.CurrentRoundId(), StringComparison.Ordinal)
                || !SideObjectiveRuntime.AcceptsState())
            {
                return;
            }

            // Recorded here as well as in TickClientReplay: a spontaneous broadcast that lands before
            // the replay tick has seen this round must not be mistaken for last round's and trigger
            // a redundant request.
            if (!string.Equals(_clientRoundId, roundId, StringComparison.Ordinal))
            {
                _clientRoundId = roundId;
                _replayRequests = 0;
            }

            _stateAppliedThisRound = true;
            Apply(snapshot);
        }

        private static void OnReplayMessage(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServer || senderClientId == NetworkManager.ServerClientId)
                return;

            try
            {
                reader.ReadValueSafe(out int schema);
                if (schema != SchemaVersion)
                    return;
                ReadText(ref reader, out _);
            }
            catch
            {
                return;
            }

            CustomMessagingManager manager = NetworkManager.Singleton.CustomMessagingManager;
            if (manager == null)
                return;

            // Always answered, with an empty snapshot when there is nothing, so the client stops asking.
            // The client checks the round id itself.
            var writer = new FastBufferWriter(WriterCapacity, Allocator.Temp);
            try
            {
                WriteState(ref writer, _current);
                manager.SendNamedMessage(StateMessage, senderClientId, writer);
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Replayed side objective state to client {senderClientId}.");
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Replay to client {senderClientId} failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnSightingMessage(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServer || senderClientId == NetworkManager.ServerClientId)
                return;

            string roundId;
            byte source;
            string scope;
            string entry;
            try
            {
                reader.ReadValueSafe(out int schema);
                if (schema != SchemaVersion)
                    return;
                ReadText(ref reader, out roundId);
                reader.ReadValueSafe(out source);
                ReadText(ref reader, out scope);
                ReadText(ref reader, out entry);
            }
            catch
            {
                return;
            }

            if (!string.Equals(roundId, ContractProgressionEvents.CurrentRoundId(), StringComparison.Ordinal)
                || !SideObjectivePolicy.AcceptSighting(_current, (SideObjectiveSource)source, scope, entry)
                || _current.Revealed)
            {
                return;
            }

            ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Client {senderClientId} sighted '{entry}' ({scope}); revealing for the crew.");
            Commit(_current.WithRevealed(true));
        }

        private static void WriteState(ref FastBufferWriter writer, in SideObjectiveSnapshot snapshot)
        {
            byte flags = 0;
            if (snapshot.Completed)
                flags |= FlagCompleted;
            if (snapshot.RewardClaimed)
                flags |= FlagRewardClaimed;
            if (snapshot.Revealed)
                flags |= FlagRevealed;

            writer.WriteValueSafe(SchemaVersion);
            WriteText(ref writer, ContractProgressionEvents.CurrentRoundId());
            writer.WriteValueSafe((byte)snapshot.Source);
            WriteText(ref writer, snapshot.Scope);
            WriteText(ref writer, snapshot.Entry);
            writer.WriteValueSafe(snapshot.Count);
            writer.WriteValueSafe(snapshot.Required);
            writer.WriteValueSafe(flags);
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
                throw new FormatException($"text length {length} out of range");

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
