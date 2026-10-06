using System;

namespace Y4NGZCompany.Contracts.Lgu
{
    /// <summary>
    /// #804. The trust rules the LGU objective runtime turns its raw reads into progress with.
    ///
    /// Lategame Upgrades writes its per-peer contract state - <c>PentagramScript.currentRitual</c>
    /// and <c>ContractManager.bombOrder</c>/<c>SerialNumber</c> - only inside the execute stage of
    /// a <c>ClientRpc</c>, and it clears none of it between rounds. Two consequences drive
    /// everything here: a peer that never received the RPC holds an empty list, which reads as
    /// "finished" rather than "not started", and a peer that played the same contract last round
    /// holds that round's leftovers. So a polled count of zero is <em>unknown</em> until an RPC
    /// has actually executed on this peer and shown a populated list, and a polled serial that
    /// disagrees with the one latched this round is last round's.
    ///
    /// Deliberately pure BCL - no Unity, no Harmony, no LGU - so
    /// <c>LategameUpgradesCompatRegression</c> can link this file and exercise the real rules
    /// rather than a restatement of them.
    /// </summary>
    internal static class LguProgressRules
    {
        /// <summary>
        /// True when a count read off one of LGU's own lists may be folded into the latches.
        /// A negative count is an unreadable member; a zero count only means "done" once this
        /// peer has seen the list populated for itself.
        /// </summary>
        internal static bool IsCountTrustworthy(int count, bool observedLocally)
        {
            if (count < 0)
                return false;

            return count > 0 || observedLocally;
        }

        /// <summary>True when a count proves this peer is looking at a list LGU has actually
        /// filled in - the one observation that licenses trusting a later zero.</summary>
        internal static bool ObservesPopulatedList(int count)
        {
            return count > 0;
        }

        /// <summary>
        /// True when a serial read off <c>ContractManager</c> is demonstrably not this round's.
        /// An unreadable serial (null or empty) is not evidence either way and must not veto the
        /// read: the optional member may simply not have resolved, and the local-observation
        /// latch already scopes the count to this round on its own.
        /// </summary>
        internal static bool IsStaleSerial(string latchedThisRound, string polled)
        {
            if (string.IsNullOrEmpty(polled))
                return false;

            return !string.Equals(latchedThisRound, polled, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when a progress trailer belongs to the round the reader is standing in. The
        /// assignment snapshot rebroadcasts every five seconds and the payload only ever raises
        /// progress, so an unstamped trailer from the tail of one round would fold into the next
        /// one and complete it before the crew landed.
        /// </summary>
        internal static bool IsTrailerForCurrentRound(int trailerSeed, int currentSeed, int trailerLevelId, int currentLevelId)
        {
            return trailerSeed == currentSeed && trailerLevelId == currentLevelId;
        }
    }
}
