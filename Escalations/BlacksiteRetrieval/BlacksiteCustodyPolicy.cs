namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #963 S14 U1. Where a witness stands with respect to a Blacksite escort.
    /// </summary>
    internal enum BlacksiteCustodyStage
    {
        /// <summary>Nobody holds him and anybody may take him.</summary>
        Free = 0,
        /// <summary>An escort holds him.</summary>
        Held = 1,
        /// <summary>He was taken out of an escort's hands and the re-take backoff is still running.</summary>
        Broken = 2,
    }

    /// <summary>
    /// #963 S14 U1. Why custody ended, in the order the causes resolve when more than one is
    /// live on the same tick. The order is the design's precedence: what happened to the witness
    /// outranks what happened to the escort. The values are internal diagnostics, not wire
    /// format. #1293 R2: every cause is a real event; the distance between the two bodies and
    /// the escort's combat are not causes, because the witness is attached to his escort.
    /// </summary>
    internal enum BlacksiteCustodyBreak
    {
        /// <summary>Custody holds.</summary>
        None = 0,
        /// <summary>The crew killed him. This is the contract completing, so it outranks everything.</summary>
        WitnessDead = 1,
        /// <summary>A heavy hit put him on the floor; he cannot walk, so he cannot be marched.</summary>
        Knockdown = 2,
        /// <summary>The escort died.</summary>
        EscortDead = 3,
        /// <summary>The escort was stunned.</summary>
        EscortStunned = 4,

        /// <summary>
        /// #964 S14 U2. The BOARD ended custody, not the witness and not the escort: a mid-round
        /// contract change through CleanupServer, a mission cleanup, or a manifest Drop whose
        /// reason is none of the causes above.
        ///
        /// It is never returned by <see cref="BlacksiteCustodyPolicy.Decide"/> and takes no part
        /// in the precedence: it is only ever PASSED to
        /// WhistleblowerEnemyAI.ReleaseCustodyServer by a caller that already knows why it is
        /// letting go. Without it those sites would have to borrow another cause's token and the
        /// [UAT][Custody] event=broken line would report a knockdown or a dead escort that never
        /// happened.
        /// </summary>
        Cleanup = 7,
    }

    /// <summary>
    /// #1158. What the witness's own body does the moment custody ends - a separate question from
    /// <see cref="BlacksiteCustodyBreak"/>, which only says why it ended. #1293 R2: these are his
    /// own states; custody adds no behaviour of its own after a release.
    /// </summary>
    internal enum BlacksiteCustodyRelease
    {
        /// <summary>His death owns the body; the release leaves his state to the path that killed him.</summary>
        DeathOwnsBody = 0,
        /// <summary>Inside: he is back in his own hiding state (on the floor, a knockdown owns him until he stands).</summary>
        Hiding = 1,
        /// <summary>Outside, he cowers where he stands (Lawson, 2026-09-20).</summary>
        Cower = 3,
    }

    /// <summary>
    /// #963 S14 U1. The custody numbers in one place, as plain static fields so the host keys
    /// ("Blacksite Escort Enabled", "Custody Broken By Knockdown") bind straight onto them without
    /// touching custody logic. The two fixed values never become keys at all
    /// (review-whistleblower-escort.md 3.11, the FixedSetting convention).
    /// </summary>
    internal static class BlacksiteCustodySettings
    {
        /// <summary>
        /// Master switch. False restores today's behaviour on a Whistleblower round: the squad
        /// raids scrap and nobody takes the witness. Host only.
        /// </summary>
        internal static bool EscortEnabled = true;

        /// <summary>
        /// Whether a heavy hit (WhistleblowerEnemyAI force >= 3, the existing knockdown gate)
        /// frees him. This is the cheap repeatable stall the design gives the crew. Host only.
        /// </summary>
        internal static bool CustodyBrokenByKnockdown = true;

        /// <summary>
        /// Fixed. How close an escort must be to take him, in metres, body to body: the reviewed
        /// take reach (review-whistleblower-escort.md 3.11). The board's dwell gate measures the
        /// same thing (BlacksiteObjectiveManifestController.CustodyReachDistance).
        /// </summary>
        internal const float CustodyTakeDistance = 1.2f;

        /// <summary>
        /// Fixed. How long after a break no escort may take him again, in seconds. Derivation:
        /// the reviewed number (review-whistleblower-escort.md 3.11). It is what makes a shovel
        /// knockdown or a killed escort buy the crew time rather than a single lost tick.
        /// </summary>
        internal const float CustodyRetakeBackoffSeconds = 15f;
    }

    /// <summary>
    /// #963 S14 U1. The custody decision with no Unity types in it: who holds the witness, why he
    /// stopped being held, and when he may be taken again. The host calls it from
    /// WhistleblowerEnemyAI.UpdateCustodyServer once per server tick; tests/WhistleblowerCustodyChecks
    /// compiles this file on its own and drives it directly.
    /// </summary>
    internal static class BlacksiteCustodyPolicy
    {
        /// <summary>
        /// The first break that applies, in the declared precedence, or
        /// <see cref="BlacksiteCustodyBreak.None"/> while custody holds. Every argument is a
        /// fact the caller already has: nothing is sampled in here. #1293 R2: no distance and no
        /// combat input - he is attached to his escort, so neither is an event.
        /// </summary>
        internal static BlacksiteCustodyBreak Decide(
            bool escortAlive,
            bool escortStunned,
            bool knockedDown,
            bool witnessDead,
            bool breakOnKnockdown)
        {
            if (witnessDead)
                return BlacksiteCustodyBreak.WitnessDead;
            if (knockedDown && breakOnKnockdown)
                return BlacksiteCustodyBreak.Knockdown;
            if (!escortAlive)
                return BlacksiteCustodyBreak.EscortDead;
            if (escortStunned)
                return BlacksiteCustodyBreak.EscortStunned;
            return BlacksiteCustodyBreak.None;
        }

        /// <summary>
        /// #1158. Outside, every release is a cower in place, because out there the escape run's
        /// settle test counts only players INSIDE the facility (round 2 line 45301, "flee
        /// distance: 5 tiles", one line after an outside cleanup break); a knockdown outside is
        /// included, and the stand-up then finds him already cowering. #1293 R2: inside, every
        /// release hands him back to his own hiding state - a knockdown leaves him on the floor
        /// until he stands up - and custody starts no run of its own.
        /// </summary>
        internal static BlacksiteCustodyRelease Release(BlacksiteCustodyBreak cause, bool insideFacility)
        {
            if (cause == BlacksiteCustodyBreak.WitnessDead)
                return BlacksiteCustodyRelease.DeathOwnsBody;
            return insideFacility ? BlacksiteCustodyRelease.Hiding : BlacksiteCustodyRelease.Cower;
        }

        /// <summary>
        /// Where the witness stands. The assignment decides Held; with nobody assigned the last
        /// break's backoff decides Broken against Free.
        /// </summary>
        internal static BlacksiteCustodyStage Stage(
            bool escortAssigned,
            BlacksiteCustodyBreak lastBreak,
            float secondsSinceBreak)
        {
            if (escortAssigned)
                return BlacksiteCustodyStage.Held;
            if (lastBreak != BlacksiteCustodyBreak.None
                && secondsSinceBreak < BlacksiteCustodySettings.CustodyRetakeBackoffSeconds)
                return BlacksiteCustodyStage.Broken;
            return BlacksiteCustodyStage.Free;
        }

        /// <summary>
        /// Whether an escort may take him right now. The distance is the escort's body to the
        /// witness's body.
        /// </summary>
        internal static bool CanBegin(
            BlacksiteCustodyStage stage,
            bool witnessAlive,
            bool escortAlive,
            float takeDistanceMetres)
        {
            return stage == BlacksiteCustodyStage.Free
                && witnessAlive
                && escortAlive
                && takeDistanceMetres <= BlacksiteCustodySettings.CustodyTakeDistance;
        }

        /// <summary>
        /// #874 F02. Whether a free witness may walk to a Blacksite soldier he can see.
        ///
        /// This is the recognition half of the escort: before it the squad had to corner an
        /// indifferent man and hold itself inside 1.2 m of him for a continuous 1.1 s while he
        /// walked his own roam, which is the chase the 2026-09-20 and 2026-09-21 runs never
        /// completed. It changes no radius and no dwell: the take gate, the line of sight and the
        /// custody stage machine are the ones that already shipped. What it changes is who closes
        /// the last two metres.
        ///
        /// <paramref name="stage"/> is the whole cooldown. A break stamps
        /// <see cref="BlacksiteCustodySettings.CustodyRetakeBackoffSeconds"/> of
        /// <see cref="BlacksiteCustodyStage.Broken"/>, during which no escort may take him AND he
        /// will not walk to one, so a shovel that frees him buys the crew the whole window rather
        /// than a man who turns round and hands himself back on the next tick.
        ///
        /// <paramref name="playerAtHand"/> is the danger term: a crew member close enough to have
        /// discovered him outranks the squad, because the state he takes from that - cornered,
        /// assessing, a run, or a cower outside - is the player-facing behaviour this must not
        /// touch.
        /// </summary>
        internal static bool CanApproachEscort(
            BlacksiteCustodyStage stage,
            bool escortEnabled,
            bool ableToWalk,
            bool playerAtHand)
        {
            return escortEnabled
                && ableToWalk
                && !playerAtHand
                && stage == BlacksiteCustodyStage.Free;
        }

        /// <summary>
        /// The closed vocabulary the [UAT][Custody] "event=broken" line reports as its cause.
        /// </summary>
        internal static string BreakToken(BlacksiteCustodyBreak cause)
        {
            switch (cause)
            {
                case BlacksiteCustodyBreak.WitnessDead:
                    return "witnessDead";
                case BlacksiteCustodyBreak.Knockdown:
                    return "knockdown";
                case BlacksiteCustodyBreak.EscortDead:
                    return "escortDead";
                case BlacksiteCustodyBreak.EscortStunned:
                    return "escortStunned";
                // #964 S14 U2: the manifest-originated release.
                case BlacksiteCustodyBreak.Cleanup:
                    return "cleanup";
                default:
                    return "none";
            }
        }
    }
}
