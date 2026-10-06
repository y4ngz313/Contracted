namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1233 S2 (rule 1): the squad's world is the objective's world. These are the pure rules the
    /// host reads it with: which record is the objective, which world that objective is in, which
    /// world a mission member therefore works in, and whether a doorway may take the member there.
    /// No epoch, no crossing stamp, no window: the answer is read from live facts every time it is
    /// asked. Unity-free, so tests/BlacksiteChurnChecks drives the rules the runtime applies.
    /// </summary>
    internal static class BlacksiteMissionWorld
    {
        /// <summary>
        /// <c>TryFocusRecord</c>'s precedence, one record at a time: a non-Loot record takes the
        /// focus when its rank outranks the best so far (Carried over Reserved over Available,
        /// <see cref="BlacksiteSquadFocusPolicy.Rank"/>; #1305 G5 (D17): an attainable mission objective
        /// outranks every salvage record); on a tie the earlier record keeps it. When two live records
        /// sit in opposite worlds, this record, the primary focus, decides the mission world.
        /// </summary>
        internal static bool TakesFocus(BlacksiteObjectiveKind kind, BlacksiteObjectivePhase phase, ref int bestRank)
        {
            if (kind == BlacksiteObjectiveKind.Loot) return false;
            int rank = BlacksiteSquadFocusPolicy.Rank(phase);
            if (rank > 0 && kind != BlacksiteObjectiveKind.Salvage) rank += BlacksiteSquadFocusPolicy.Rank(BlacksiteObjectivePhase.Carried);
            if (rank <= bestRank) return false;
            bestRank = rank;
            return true;
        }

        /// <summary>
        /// Which world the objective behind a record is in (true = outside): a Carried record with a
        /// live carrier is where that carrier stands, every other record where it last published.
        /// </summary>
        internal static bool ObjectiveOutside(BlacksiteObjectivePhase phase, bool liveCarrier, bool carrierOutside, bool recordOutside)
            => phase == BlacksiteObjectivePhase.Carried && liveCarrier ? carrierOutside : recordOutside;

        /// <summary>
        /// Whether a mission member (no record of its own, no duty world) works inside the facility:
        /// the focus objective's world while there is one, otherwise the mission's inside-clear
        /// default - the baton raiders clear the facility, every other spawn stays outside.
        /// </summary>
        internal static bool Interior(bool hasFocus, bool focusOutside, bool usesBaton)
            => hasFocus ? !focusOutside : usesBaton;

        /// <summary>
        /// The entrance gate: a doorway that changes the member's world is admitted iff its far side
        /// is the member's own world by <c>RequiresInterior</c>, or the passage keeps its own
        /// semantics (<paramref name="exemptPassage"/>: extraction, egress, a transport-lost
        /// survivor, a popped-Jester flight, a player response).
        /// </summary>
        internal static bool AdmitsCrossing(bool toOutside, bool intendsInterior, bool exemptPassage)
            => exemptPassage || toOutside != intendsInterior;

        /// <summary>The <c>[UAT][Entrance] event=refused</c> line for a crossing the gate refused.</summary>
        internal static string RefusedLine(int member, bool toOutside, bool intendsInterior)
            => $"event=refused, member={member}, world={Token(toOutside)}, missionWorld={Token(!intendsInterior)}, reason=notMissionWorld";

        private static string Token(bool outside) => outside ? "outside" : "inside";
    }
}
