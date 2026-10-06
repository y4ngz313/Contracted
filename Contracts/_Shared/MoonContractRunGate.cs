namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #951 S11H U3 (c): whether the contract runtime pass may run this frame.
    ///
    /// Test 3 ended with a 133.84 ms frame (log 80181-80322). GameNetworkManager.Disconnect's
    /// postfix calls MoonContractState.ResetRunState() while NetworkManager.IsListening is still
    /// true, so the very next RoundManager.Update postfix passed the #944 S11 U6 listening guard,
    /// re-rolled Blacksite ("Runtime escalation selected") and re-ran interior injection on a level
    /// that was already being torn down. IsListening cannot see that case, because at that instant
    /// it is still true: the disconnect is in flight, not finished.
    ///
    /// The rule is here, pure and free of UnityEngine and Harmony, because a Harmony postfix cannot
    /// be pinned without an editor and the ordering is the part worth pinning.
    /// BlacksiteTierTwoThreeRegression links this file and asserts it.
    ///
    /// The latch is process-wide and single-threaded: every writer is a Harmony patch on the Unity
    /// main thread, and every reader is the RoundManager.Update postfix on the same thread.
    /// </summary>
    internal static class MoonContractRunGate
    {
        private static bool _disconnecting;

        /// <summary>True between a disconnect and the next round start.</summary>
        internal static bool Disconnecting => _disconnecting;

        /// <summary>Latched by the GameNetworkManager.Disconnect postfix BEFORE it resets the run
        /// state, so no pass between the reset and netcode actually stopping can act on it.</summary>
        internal static void NoteDisconnect() => _disconnecting = true;

        /// <summary>Released by the StartOfRound.StartGame prefix (the host's round start) and by
        /// the StartOfRound.SetShipReadyToLand postfix, which every peer runs. A latch that is
        /// never released would disable the contract runtime for the life of the process, so it is
        /// released at both of the points a new round is known to be beginning.</summary>
        internal static void NoteRunStarted() => _disconnecting = false;

        /// <summary>
        /// The whole gate: netcode is listening AND no disconnect is in flight. Both halves are
        /// passed in rather than read, so the rule can be asserted without a session.
        /// </summary>
        internal static bool ShouldRunRuntimePass(bool networkListening, bool disconnecting)
            => networkListening && !disconnecting;
    }
}
