using GameNetcodeStuff;
using UnityEngine;

using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Contracts.BlackoutAudit
{
    /// <summary>
    /// Remembers who last flipped a breaker switch, so the Blackout Audit completion can be
    /// credited to a person instead of fanned out across the whole crew.
    ///
    /// Vanilla's <c>BreakerBox.SwitchBreaker(bool)</c> carries no actor, but the switch it hangs
    /// off is an <c>AnimatedObjectTrigger</c> whose replicated
    /// <c>UpdateAnimClientRpc(bool, bool, int playerWhoTriggered)</c> does - and it carries it to
    /// every machine, the host included. Latching the actor in a prefix of that RPC (and of the
    /// local <c>TriggerAnimation</c>) puts the id in place before the switch's UnityEvent reaches
    /// <c>SwitchBreaker</c>, which is where the audit decides it is complete.
    ///
    /// The latch is deliberately short-lived: a stale id is worse than no id, because no id falls
    /// back to the crew-wide fan-out that this system used before.
    /// </summary>
    internal static class BlackoutBreakerActorLatch
    {
        /// <summary>How long a latched actor stays valid. One switch flip resolves in well under this.</summary>
        private const float ActorValiditySeconds = 3f;

        private static ulong _actorClientId = ContractProgressionEventIds.UnknownPlayerClientId;
        private static float _latchedAt = float.NegativeInfinity;

        internal static void Clear()
        {
            _actorClientId = ContractProgressionEventIds.UnknownPlayerClientId;
            _latchedAt = float.NegativeInfinity;
        }

        internal static void Latch(PlayerControllerB player)
        {
            Latch(ContractProgressionEvents.ResolveClientId(player));
        }

        internal static void Latch(ulong actorClientId)
        {
            if (!ContractProgressionEventIds.IsAttributable(actorClientId))
                return;

            _actorClientId = actorClientId;
            _latchedAt = Time.realtimeSinceStartup;
        }

        /// <summary>Latches from an <c>AnimatedObjectTrigger</c> player index (-1 means "not a player").</summary>
        internal static void LatchByPlayerIndex(int playerIndex)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || playerIndex < 0 || playerIndex >= players.Length)
                return;

            Latch(players[playerIndex]);
        }

        /// <summary>
        /// The actor to credit right now, or <see cref="ContractProgressionEventIds.UnknownPlayerClientId"/>
        /// when nobody flipped a switch recently enough to be responsible.
        ///
        /// #406: a latched id is also surrendered when its owner has left the session by the time
        /// the audit resolves - it names a client that no longer exists, and crediting it would
        /// land the reward on nobody. Returning the sentinel selects the crew-wide fan-out instead.
        /// The connectivity test is <see cref="ContractProgressionEvents.IsCreditableActor"/>, the
        /// same one the publisher choke point applies, so a player who died after the flip still
        /// keeps the credit and only a disconnect reroutes it.
        /// </summary>
        internal static ulong ResolveActorClientId()
        {
            if (!ContractProgressionEvents.IsCreditableActor(_actorClientId))
                return ContractProgressionEventIds.UnknownPlayerClientId;

            return Time.realtimeSinceStartup - _latchedAt <= ActorValiditySeconds
                ? _actorClientId
                : ContractProgressionEventIds.UnknownPlayerClientId;
        }
    }
}
