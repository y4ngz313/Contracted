namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S6.4b: what the squad is organised around when no member is currently walking the
    // objective. The 2026-09-09 run answered "the frontier": the anchor was the MEMBER holding a
    // Reserved/Carried record, and between attempts (the 1/3/6/12/24 s objective backoff plus the
    // 30 s per-member bench) there was no such member, so all four raiders reverted to
    // SearchGoal and walked the -219 storey for the whole mission while the drive sat known and
    // static on -205. The anchor is a POINT with an optional owner, and the frontier is what runs
    // only when nothing is known.
    internal enum BlacksiteSquadFocusKind : byte { None = 0, Objective = 1, Carrier = 2, Search = 3 }

    internal readonly struct BlacksiteSquadFocus
    {
        internal BlacksiteSquadFocusKind Kind { get; }
        internal string Reason { get; }
        internal BlacksiteSquadFocus(BlacksiteSquadFocusKind kind, string reason) { Kind = kind; Reason = reason; }
        // The exact vocabulary the [UAT][Objective] event=focus line prints.
        internal string Anchor
        {
            get
            {
                switch (Kind)
                {
                    case BlacksiteSquadFocusKind.Objective: return "objective";
                    case BlacksiteSquadFocusKind.Carrier: return "carrier";
                    case BlacksiteSquadFocusKind.Search: return "search";
                    default: return "none";
                }
            }
        }
    }

    internal static class BlacksiteSquadFocusPolicy
    {
        // A record is attainable while the squad can still act on it, and that is a PHASE test and
        // never a failed-attempt count. S6.5a re-arms an exhausted mission objective to Available
        // instead of setting it Unavailable; this rule is correct under both behaviours, because
        // "the squad is anchored" follows the phase the record actually carries. Loaded, Extracted
        // and Staged are finished work; Unavailable is the objective the squad cannot act on.
        internal static bool Attainable(BlacksiteObjectivePhase phase)
            => phase == BlacksiteObjectivePhase.Available
            || phase == BlacksiteObjectivePhase.Reserved
            || phase == BlacksiteObjectivePhase.Carried;

        // Carried outranks Reserved outranks Available, so the anchor follows the most advanced
        // attempt on the board rather than the record's position in the list.
        internal static int Rank(BlacksiteObjectivePhase phase)
            => phase == BlacksiteObjectivePhase.Carried ? 3
            : phase == BlacksiteObjectivePhase.Reserved ? 2
            : phase == BlacksiteObjectivePhase.Available ? 1 : 0;

        /// <summary>
        /// The squad's anchor for one member. Pure: every input is measured by the caller.
        /// #1233 S3 (rule 2): a helper with a known objective never searches, unless it stands in the
        /// holder's world and the operator's stand on it is refused from afar (#1283 S6): there is
        /// nothing to stand beside, and the squad route searches until a measurement publishes a stand.
        /// #1293 R2: a refusal is route or floor evidence (no stand has a body-fit veto), so the
        /// Search is honest. A member in the other world keeps the
        /// objective, whose crossing takes it to the mission world. A live carrier is
        /// followed into any world, because the objective moves with it; a reserved operator is the
        /// anchor while it stands in the member's world, and until then the record it is walking to
        /// is (it becomes the operator the tick it enters the member's world).
        /// </summary>
        /// <param name="standingDown">The raid is returning or this member is withdrawing.</param>
        /// <param name="anyKnownObjective">Any non-Loot record exists at all.</param>
        /// <param name="objectiveAttainable">The best such record is Available, Reserved or Carried.</param>
        /// <param name="standRefused">The reserved operator's stand on it is refused from afar (BlacksiteObjectivePolicy.RefusalIsLocal); read only in the holder's world.</param>
        /// <param name="liveCarrier">That record is Carried by a living member.</param>
        /// <param name="liveReservation">That record is Reserved by a living member.</param>
        /// <param name="holderInMemberWorld">That living holder stands in this member's world.</param>
        /// <param name="sweeps">A mission objective is held and this member is not its one escort (#1305 G5 D17).</param>
        internal static BlacksiteSquadFocus Resolve(bool standingDown, bool anyKnownObjective, bool objectiveAttainable,
            bool standRefused, bool liveCarrier, bool liveReservation, bool holderInMemberWorld, bool sweeps)
        {
            if (standingDown)
                return new BlacksiteSquadFocus(BlacksiteSquadFocusKind.None, "the raid is returning or this member is withdrawing");
            if (!anyKnownObjective)
                return new BlacksiteSquadFocus(BlacksiteSquadFocusKind.Search, "no objective record exists yet");
            if (!objectiveAttainable)
                return new BlacksiteSquadFocus(BlacksiteSquadFocusKind.Search, "every known objective is finished or unavailable");
            if (sweeps)
                return new BlacksiteSquadFocus(BlacksiteSquadFocusKind.Search, "sweeping while the operator works");
            if (liveCarrier)
                return new BlacksiteSquadFocus(BlacksiteSquadFocusKind.Carrier, "escorting the carrier");
            if (standRefused && holderInMemberWorld)
                return new BlacksiteSquadFocus(BlacksiteSquadFocusKind.Search, "the known objective's standing point is refused from afar");
            if (liveReservation && holderInMemberWorld)
                return new BlacksiteSquadFocus(BlacksiteSquadFocusKind.Carrier, "supporting the reserved operator");
            return new BlacksiteSquadFocus(BlacksiteSquadFocusKind.Objective, "staging on the known objective between attempts");
        }

        /// <summary>
        /// The frontier score S6.4b gives a candidate node. With an objective known the objective
        /// term has weight one, so the score is the whole leader -> node -> objective path length
        /// and the winning node is the one that costs the least detour on the way to the
        /// objective. With nothing known the term is zero and the score is the plain distance the
        /// frontier has always used.
        /// </summary>
        internal static float SearchScore(float leaderToNode, float nodeToObjective, bool objectiveKnown)
            => leaderToNode + (objectiveKnown ? nodeToObjective : 0f);
    }
}
