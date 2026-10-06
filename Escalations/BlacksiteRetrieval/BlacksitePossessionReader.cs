using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1076: the thin live adapter over <see cref="BlacksitePossessionPolicy"/>.
    ///
    /// This answers exactly one question: does the Blacksite squad have the contract object right
    /// now. It comes from <c>BlacksiteRetrievalMissionController.ObjectiveSnapshots</c>, the
    /// NetworkList every peer reads (BlacksiteSquadBlackboard has read it on every peer since
    /// design note C.5), so it costs no new RPC.
    ///
    /// It deliberately does NOT answer "did they leave with it". That fact cannot be read off any
    /// live object: departure is one synchronous chain that despawns the ship - and the mission is
    /// a component of the ship - in the same call as <c>ConfirmDepartureServer</c>, so on the host
    /// <see cref="BlacksiteHardDriveObjectiveService.ActiveMission"/> is already null before the
    /// next HUD poll, and on a client the record's Extracted write never reaches the wire at all.
    /// Departure rides the progression channel and is latched by
    /// <c>MoonContractState.NotifyBlacksiteObjectiveDeparted</c>; the contract cards check that
    /// latch FIRST and never ask this reader once it is up.
    ///
    /// Null-safe: no mission (no escalation this round, or a client before the mission has spawned)
    /// reads as None, which is exactly the shipped card.
    /// </summary>
    internal static class BlacksitePossessionReader
    {
        /// <summary>
        /// Reused across calls: this runs from the HUD's quarter-second objective poll on every
        /// peer, and a fresh list per poll per contract is garbage for nothing.
        /// </summary>
        private static readonly List<BlacksitePossessionRecord> Scratch = new List<BlacksitePossessionRecord>(8);

        /// <summary>How far the squad has got with <paramref name="kind"/>, on any peer.</summary>
        internal static BlacksitePossessionReading Read(BlacksiteObjectiveKind kind)
        {
            BlacksiteRetrievalMissionController mission = BlacksiteHardDriveObjectiveService.ActiveMission;
            if (mission == null || mission.ObjectiveSnapshots == null)
                return default;

            Scratch.Clear();
            foreach (BlacksiteObjectiveSnapshot snapshot in mission.ObjectiveSnapshots)
                Scratch.Add(new BlacksitePossessionRecord((int)snapshot.Kind, (int)snapshot.Phase, snapshot.CarrierId));

            return BlacksitePossessionPolicy.Resolve(Scratch, (int)kind);
        }
    }
}
