namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1195 Contract S. The side objective as the contract card reads it, identical on every peer:
    /// the host publishes it over <see cref="SideObjectiveNetwork"/> and every peer applies the same
    /// snapshot through <see cref="SideObjectivePublication"/>.
    ///
    /// Hidden until a crewmate first sights or interacts with the objective, and whenever
    /// there is none: Wesley's tools absent, neither a Wesley interior secondary nor a Wesley's Moons
    /// trial this round (#1198), or Wesley's own gates ("Enable secondaries", "Disable with trials",
    /// "Enable trials", "Only on trial moons") leaving nothing chosen. Row keys are stable per
    /// phase (<see cref="SideObjectivePolicy.ProgressKey"/>, <see cref="SideObjectivePolicy.ClaimKey"/>,
    /// <see cref="SideObjectivePolicy.RecoveredKey"/>), and the Progress count can fall. Poll
    /// <see cref="Revision"/> for a visible change or a change of objective identity (even hidden).
    /// </summary>
    internal static class SideObjectiveState
    {
        internal static SideObjectivePhase Phase => SideObjectiveNetwork.Publication.Phase;

        /// <summary>OPTIONAL header payload: an interior display name, TRIAL, or empty; hidden is empty.</summary>
        internal static string Title => SideObjectiveNetwork.Publication.Title;

        /// <summary>The one row for the current phase; default while hidden.</summary>
        internal static MoonContractObjective Row => SideObjectiveNetwork.Publication.Row;

        internal static int Revision => SideObjectiveNetwork.Publication.Revision;

        /// <summary>Round reset and lobby change: clears the published state and every cache.</summary>
        internal static void ResetRunState()
        {
            SideObjectiveNetwork.ResetRunState();
            SideObjectiveRuntime.ResetRunState();
        }
    }
}
