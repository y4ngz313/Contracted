namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteRaidPolicy
    {
        /// <summary>
        /// #1072: the casualty term's arm, one full squad-motion sample.
        ///
        /// The casualty half of WithdrawSquad is a statement about raiders that DEPLOYED and then
        /// became unfit, and at elapsed 0 no raider has had time to become anything. On 2026-09-15
        /// Dine (30280) and March (36829) both committed at `fit=2/4, elapsed=0s` one frame after
        /// the SECOND soldier registered, because the fit count was read off a roster the deployment
        /// drain was still filling. The arm is BlacksiteObjectivePolicy.SquadMoveSampleSeconds -
        /// HolderImmobileWindowSeconds / SquadMoveSamples = 15 / 3 = 5 s, the interval the squad's
        /// own motion is sampled over - rather than a new number: it is the shortest interval this
        /// raid already treats as evidence about a member. It is an ARM, not a grace: it bounds
        /// nothing that was previously unbounded and the 480 s deadline below is untouched by it.
        /// </summary>
        internal const float WithdrawSquadArmSeconds = BlacksiteObjectivePolicy.SquadMoveSampleSeconds;
        /// <summary>The raid's hard deadline: past it the squad withdraws whatever else holds.</summary>
        internal const float RaidDeadlineSeconds = 480f;
        internal static bool WithdrawSquad(int deployedRaiders, int fitRaiders, float elapsed)
            => elapsed >= RaidDeadlineSeconds
                || (elapsed >= WithdrawSquadArmSeconds && deployedRaiders >= 4 && fitRaiders <= deployedRaiders / 2);
        /// <summary>
        /// #1072: the same rule, refused outright while the squad is still being spawned.
        ///
        /// <paramref name="deploymentPending"/> is the deployment drain's own pending signal
        /// (MoonEscalationState.BlacksiteSoldierDeploymentPending), never a count: the drain clears
        /// it on success, on a failed spawn and on an abort alike, so a squad that never finishes
        /// spawning cannot hang the rule off.
        /// </summary>
        internal static bool WithdrawSquad(int deployedRaiders, int fitRaiders, float elapsed, bool deploymentPending)
            => !deploymentPending && WithdrawSquad(deployedRaiders, fitRaiders, elapsed);
        /// <summary>
        /// #874 N4 (Lawson, 2026-09-23, decision 1): a withdrawal waits while a raider inside is
        /// making progress on the primary objective.
        ///
        /// Round 1 of the 2026-09-23 run committed the casualty withdrawal (fit=1/4) on the tick the
        /// backup hold ended, with the lone survivor inside and walking to the Payload (log 6212).
        /// "Making progress" is the board's own state, not a new clock: the raider HOLDS the primary
        /// (its reservation or its carry), stands inside, and is not leaving (withdrawing on its own,
        /// boarded, or cut off from its transport). #874 S4 / #1293 R2: a holder the progress
        /// ledger misses is released (or drops its carry) by the stalled answer, so "holds the
        /// primary" is false from the tick that answer fires or the holder dies. The hard deadline
        /// is never deferred.
        /// </summary>
        internal static bool DeferWithdrawalForObjective(float elapsed, bool holdsPrimary, bool inside, bool leaving)
            => elapsed < RaidDeadlineSeconds && holdsPrimary && inside && !leaving;
        internal static bool WithdrawMember(int health, bool ammunitionExhausted, bool guard)
            => !guard && (health <= 1 || ammunitionExhausted);
        internal static bool RegroupOutnumbered(int threats, int nearbyAllies, bool contact)
            => contact && threats >= 2 && threats > nearbyAllies;
        // #874 S11 U2 (I8): what "outnumbered" counts. RegroupOutnumbered is unchanged and still
        // unit-tested at its own three rows; what was broken is upstream of it, in
        // BlacksiteSoldierAI.UpdateRaidWithdrawal, whose tally iterated RoundManager.SpawnedEnemies
        // only. Players - the one threat the policy was written for - were never counted, so the
        // rule was dead in the situation it exists for. The tally is a rule, so it lives here where
        // BlacksiteBehaviourRegression can pin it, and the seen radius stays the single 18 m the
        // enemy half already used: no second radius is introduced.
        internal const float RegroupThreatRange = 18f;
        internal const float RegroupAllyRange = 14f;
        internal static int RegroupThreats(int seenEnemies, int seenPlayers) => seenEnemies + seenPlayers;
    }
}


