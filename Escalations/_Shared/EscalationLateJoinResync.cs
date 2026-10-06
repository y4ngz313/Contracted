using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations._Shared
{
    /// <summary>
    /// #716/G2. Escalation state is replicated by a named message that the server sends only when
    /// something changes, so a player who joins after the escalation started never learned it was
    /// running: no HUD tip, no ritual fire, no crate counter, until the next state change (which on
    /// a finished ceremony never comes). Netcode replays nothing for a custom named message — only
    /// <c>NetworkObject</c> spawns are replayed to a late joiner — so the server has to resend.
    ///
    /// <para>The resend is <b>targeted</b> (<c>SendNamedMessage(name, clientId, writer)</c>), not a
    /// broadcast. A broadcast would in fact be harmless — every receiver applies the same fields and
    /// the escalation band fires on rising edges only, so an unchanged state re-applied on an
    /// already-current client is a no-op — but a targeted send keeps a join from touching the other
    /// clients at all.</para>
    ///
    /// <para>#1343 S2: the payload also carries the terminal events (ceremony complete or ruined,
    /// Blacksite aborted) with the host's server-time stamp. A resend never re-announces anything
    /// (rising edge, <see cref="EscalationAnnouncementEdges"/>); a late joiner gets the current
    /// ongoing state once, and never a terminal event older than
    /// <see cref="EscalationAnnouncementEdges.TerminalFreshSeconds"/>.</para>
    ///
    /// <para><b>Why the send is retried rather than sent once.</b> The server raises
    /// <c>OnClientConnectedCallback</c> as soon as approval completes, which is before the joining
    /// client has loaded the scene where <c>RoundManager.Update</c> — and therefore the escalation
    /// tick that registers the client's named-message handler — first runs. A named message that
    /// arrives before its handler is registered is dropped by NGO with no error. So each late joiner
    /// gets <see cref="ResendAttempts"/> sends spread over <see cref="ResendIntervalSeconds"/>,
    /// which costs a handful of small (under 1 KB) messages per join and covers a slow load. Repeats are
    /// idempotent for the same reason a broadcast would be.</para>
    ///
    /// <para>Subscription is keyed on the <see cref="NetworkManager"/> instance and re-evaluated
    /// every tick from <c>EnsureNetworkHandlers</c>, so it attaches when the escalation net layer
    /// comes up on a server, detaches on a client or when the singleton goes away, and cannot double
    /// subscribe. <c>NetworkManager.Singleton</c> outlives a lobby, so leaving a subscription
    /// attached would fire in the next one.</para>
    /// </summary>
    internal sealed class EscalationLateJoinResync
    {
        /// <summary>Sends per joining client. Three covers a client whose scene load runs long
        /// without making a join measurably chattier.</summary>
        private const int ResendAttempts = 3;
        /// <summary>Delay before the first send. The joiner is still loading at approval time.</summary>
        private const float FirstResendDelaySeconds = 0.75f;
        /// <summary>Spacing between the remaining sends.</summary>
        private const float ResendIntervalSeconds = 1.5f;

        private readonly string _logTag;
        private readonly Action<ulong> _sendStateToClient;
        private readonly List<PendingResend> _pending = new List<PendingResend>();
        private NetworkManager _subscribed;

        internal EscalationLateJoinResync(string logTag, Action<ulong> sendStateToClient)
        {
            _logTag = logTag;
            _sendStateToClient = sendStateToClient;
        }

        /// <summary>Call once per tick from the escalation's <c>EnsureNetworkHandlers</c>, with
        /// whatever <c>NetworkManager.Singleton</c> currently is (null included). Attaches or
        /// detaches the connection callbacks as the role and the singleton change, then flushes any
        /// resend that has come due.</summary>
        internal void Sync(NetworkManager manager)
        {
            bool wantAttached = manager != null && manager.IsListening && manager.IsServer;

            if (!wantAttached)
            {
                Detach();
                return;
            }

            if (!ReferenceEquals(_subscribed, manager))
            {
                Detach();
                manager.OnClientConnectedCallback += OnClientConnected;
                manager.OnClientDisconnectCallback += OnClientDisconnected;
                _subscribed = manager;
            }

            Flush(manager);
        }

        /// <summary>Unsubscribes and drops any queued resend. Safe to call when never attached.</summary>
        internal void Detach()
        {
            NetworkManager subscribed = _subscribed;
            _subscribed = null;
            _pending.Clear();

            if (ReferenceEquals(subscribed, null) || subscribed == null)
                return;

            try
            {
                subscribed.OnClientConnectedCallback -= OnClientConnected;
                subscribed.OnClientDisconnectCallback -= OnClientDisconnected;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"{_logTag} Late-join resync detach skipped: {e.Message}");
            }
        }

        private void OnClientConnected(ulong clientId)
        {
            // The host's own client already has the authoritative fields: they are the same statics.
            if (clientId == NetworkManager.ServerClientId)
                return;

            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].ClientId != clientId)
                    continue;

                // A reconnect on the same id restarts the schedule rather than queueing twice.
                _pending[i] = new PendingResend(clientId);
                return;
            }

            _pending.Add(new PendingResend(clientId));
        }

        private void OnClientDisconnected(ulong clientId)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].ClientId == clientId)
                    _pending.RemoveAt(i);
            }

            // The server's own disconnect ends the session; the singleton survives it, so drop the
            // subscription here rather than waiting for the next tick to notice.
            NetworkManager subscribed = _subscribed;
            if (!ReferenceEquals(subscribed, null) && subscribed != null && clientId == subscribed.LocalClientId)
                Detach();
        }

        private void Flush(NetworkManager manager)
        {
            if (_pending.Count == 0)
                return;

            float now = Time.realtimeSinceStartup;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingResend pending = _pending[i];

                if (!manager.ConnectedClients.ContainsKey(pending.ClientId))
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                if (now < pending.NextAttemptAt)
                    continue;

                try
                {
                    _sendStateToClient(pending.ClientId);
                }
                catch (Exception e)
                {
                    ContractsBootstrap.Log?.LogDebug(
                        $"{_logTag} Late-join state resend to client {pending.ClientId} failed: {e.Message}");
                }

                pending.AttemptsRemaining--;
                if (pending.AttemptsRemaining <= 0)
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                pending.NextAttemptAt = now + ResendIntervalSeconds;
                _pending[i] = pending;
            }
        }

        private struct PendingResend
        {
            internal readonly ulong ClientId;
            internal float NextAttemptAt;
            internal int AttemptsRemaining;

            internal PendingResend(ulong clientId)
            {
                ClientId = clientId;
                NextAttemptAt = Time.realtimeSinceStartup + FirstResendDelaySeconds;
                AttemptsRemaining = ResendAttempts;
            }
        }
    }
}
