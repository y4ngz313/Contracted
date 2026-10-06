namespace Y4NGZCompany.Contracts.Payload
{
    /// <summary>
    /// Engine-independent bookkeeping for the Payload write-off watchdog (#1070), shared by the
    /// runtime poll in <see cref="PayloadCartController"/> and the headless regression suite.
    ///
    /// Everything here is a per-MOON fact: how many consecutive polls have confirmed a loss, how long
    /// a cart that touched a kill volume stays under scrutiny, and the fall threshold resolved from
    /// that moon's lowest known Y. On 2026-09-15 a cart outlived its round (see the despawn in
    /// <c>PayloadContract.ResetRunState</c>), carried one Experimentation strike and the
    /// Experimentation threshold (-237.57) onto Titan, and completed the three-poll count there
    /// against a cart sitting at Y -1566 - failing a contract Titan did not have. The old
    /// <c>RoundIsOver()</c> guard only returned early from the poll, which FROZE the counter instead
    /// of clearing it, so the round boundary is handled here as an edge.
    /// </summary>
    internal struct PayloadKillZoneWatchdogState
    {
        /// <summary>Consecutive polls that have confirmed a loss this round.</summary>
        internal int Strikes;

        /// <summary>Time (seconds) until which a cart that touched a kill volume stays under scrutiny.</summary>
        internal float ArmedUntil;

        /// <summary>True once this round's fall threshold has been resolved.</summary>
        internal bool FallThresholdResolved;

        /// <summary>This round's resolved fall threshold, in world Y.</summary>
        internal float FallThresholdY;

        /// <summary>Edge latch, so the round-over clear runs once per boundary and not once per poll.</summary>
        private bool _roundOverLatched;

        /// <summary>Drop the per-moon bookkeeping; the round-over latch is left where it is.</summary>
        internal void ClearRoundBookkeeping()
        {
            Strikes = 0;
            ArmedUntil = 0f;
            FallThresholdResolved = false;
            FallThresholdY = 0f;
        }

        /// <summary>
        /// Full reset, including the round-over edge latch. Used when a cart is configured for a new
        /// round, so the next transition into "round over" counts as an edge again.
        /// </summary>
        internal void ResetForNewRound()
        {
            ClearRoundBookkeeping();
            _roundOverLatched = false;
        }

        /// <summary>
        /// Feed the current round-over reading. Returns true exactly once per transition into
        /// "round over" - the moment this round's watchdog bookkeeping is dropped.
        /// </summary>
        internal bool NoteRoundOver(bool roundOver)
        {
            if (!roundOver)
            {
                _roundOverLatched = false;
                return false;
            }

            if (_roundOverLatched)
                return false;

            _roundOverLatched = true;
            ClearRoundBookkeeping();
            return true;
        }

        /// <summary>Resolve and latch this round's fall threshold from its lowest known Y.</summary>
        internal float ResolveFallThreshold(float lowestKnownY, float margin)
        {
            FallThresholdY = lowestKnownY - margin;
            FallThresholdResolved = true;
            return FallThresholdY;
        }
    }
}
