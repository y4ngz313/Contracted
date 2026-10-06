namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S4.0a: why an objective attempt closed.
    //
    // The 2026-09-09 run released the Shadow Drive six times and the log said only
    // `reason=job Reassign` - a restatement of the result, not a cause. This is
    // the closed vocabulary the [UAT][Objective] event=closed line prints, by name. Unknown is a
    // defect marker: no release path that can be named may print it. #874 S4: the enum is
    // log-only (nothing serializes it), and the causes only the deleted job clocks produced are gone.
    internal enum BlacksiteObjectiveCloseCause
    {
        /// <summary>The attempt achieved its goal (acquired, staged, loaded).</summary>
        Completed,
        /// <summary>Something outside the squad's control ended it (casualty, theft, stun, cleanup).</summary>
        Interrupted,
        /// <summary>A deliberate handoff to a better-placed operator, or a dispatch elsewhere.</summary>
        Reassigned,
        /// <summary>The attempt did not progress: the monitor's stalled answer released the holder, a carrier's drop, or (#1305 G4) a cart lease with no new best.</summary>
        ProgressBudget,
        /// <summary>Abandoned after a physical block: the cargo lift ran out, or the cart's stuck verdict answered a drop (#1233 S1).</summary>
        Abandoned,
        /// <summary>The attempt was terminated without a result (record or object no longer valid).</summary>
        Invalidated,
        /// <summary>
        /// #874 S6.2: the approach resolver refused every ring around the goal - the object is
        /// sealed, walled in or has no standing point. A placement fact about the room, not a squad
        /// failure, so it closes as Interrupted and does not escalate the objective's backoff.
        /// </summary>
        ApproachUnreachable,
        /// <summary>Never printed on a path that can be named. Its presence in a log is the bug.</summary>
        Unknown,
        /// <summary>
        /// #950 S11H U2 / #874 S2 / #1305 C4 (rule 4, R11): the objective itself cannot be reached -
        /// the holder whose own miss closed the attempt, standing in the objective's world, has no
        /// complete navmesh path from where he stands to the standing point. One report decides; a
        /// probe from the tile's entrance is never a verdict, and a close by a holder in the other
        /// world (or a handoff, or a stranded body) makes none. Not one refused ring like
        /// ApproachUnreachable. Written only by ReleaseJob (HolderReach).
        /// </summary>
        Unreachable,
        /// <summary>
        /// #1066 U2: the round itself ended under the objective. On 2026-09-15 the round-end
        /// ShadowRaid purge destroyed the drive's NetworkObject at t=720.86; Refresh resolved null,
        /// wrote the record Unavailable with no [Objective] line at all (the attempt was not open,
        /// so CloseAttempt logged nothing), and one tick later "nothing outstanding" was read as
        /// rule=objectivesDone - a success. The ship was recalled, nobody boarded and the mission
        /// finished Aborted. This cause separates "the world took the objective away" from
        /// Invalidated's "the record or object stopped being valid", and it is what
        /// BlacksiteReturnRule.ObjectivesLost is derived from.
        /// </summary>
        RoundEnded,
        /// <summary>#1305 G4: a cart lease ended without progress and no ring candidate around the handle passed the cart's pose gates.</summary>
        NoDeliverablePose,
        /// <summary>#1305 G4: a cart whose stuck verdict latched (no cart route) where no ship device lifts it, written off by AbandonCargo; #1305 H3: also a Carried attempt dropped because the coupled cart reports no plan, which returns the record to Available.</summary>
        NoCartRoute
    }
}
