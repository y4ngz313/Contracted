using System;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S6.4a: what a holder release decided, and why. One value instead of a bool plus an out
    // parameter, so a caller cannot read a cause that was never decided. Keep is not a cause.
    // #874 S4: the immobile-holder release is gone - a holder that stopped moving is the monitor's stall.
    internal enum BlacksiteHolderRelease { Keep, ProgressBudget }
    // #874 S6.5a: what a released record's phase becomes. Unity-free on purpose - the manifest's
    // BlacksiteObjectivePhase lives beside a NetworkSerializable struct that the headless fixtures
    // cannot compile, and the only two answers this rule ever gives are these.
    internal enum BlacksiteObjectiveAvailability { Available, Unavailable }
    // #1078: the named cause behind an Unavailable. Unity-free for the same reason the availability
    // above is, and appended-only: None is the "kept" answer and is first, so a caller that only
    // wants the phase reads the same two answers it always did.
    // #874 S2: NoStandingPoint keeps its name but is the unreachable rule's arm: #1305 G1 (R19), the
    // releasing holder's own route to the standing point is Unsupported at the second failure or later
    // (a PartialFrontier route never withdraws).
    // #874 S4: #1153's Blocked arm is gone with the only close that produced it.
    // #1219 A4: MissionAttemptsExhausted is a mission objective whose six-failure budget is spent
    // (it used to re-arm at the capped 24 s backoff without limit); CartImmovable is a stuck payload
    // cart no ship device can lift (#1219 D3, BlacksiteObjectivePolicy.CartStuckAnswer). These two
    // and #1305 C4's NoStandingPoint write the objective off as lost. Appended, so no existing value
    // is renumbered.
    internal enum BlacksiteObjectiveWithdrawal { None, LocalProbes, AttemptsExhausted, NoStandingPoint, MissionAttemptsExhausted, CartImmovable }
    // #1219 D3: the mission's one answer to a cart whose stuck verdict latched. None: no verdict,
    // the record takes its ordinary release. ShipLift: the cart stands outside within the ship's
    // lift reach, so it stays owed and the ship's own device loads it (ground magnet, hover cable);
    // #1293 R2: Refresh's lift-reach measurement stages it. WriteOff: inside the facility, or
    // outside where no ship device reaches - withdrawn as CartImmovable and lost.
    internal enum BlacksiteCartStuckAnswer : byte { None, ShipLift, WriteOff }
    // #874 S6.5a: which rule ordered the extraction. The 2026-09-09 log said only
    // "outstanding=False" one line after the sixth failure, which is a symptom, not a rule.
    // #1271 S1: Stalled (the motion monitor's squad verdict) is deleted. Nothing serialises this
    // enum, so the declaration order is the test order.
    // #1066 U2: ObjectivesLost is appended last so no existing value is renumbered. It is the same
    // TEST position as ObjectivesDone - since #1305 C4 (R12) and E2 it is the answer that branch
    // gives whenever nothing the squad hauled is aboard: lost is never done.
    // #874 (2026-09-20): PrimarySecured is appended after it, same rule. It is the answer for the
    // round 3 shape the log holds: the witness went aboard at t=1257.72 and the recall only fired
    // because `outstanding` happened to go false with it. A Payload still on its magnet or a
    // ShadowDrive still in the container leaves loot and salvage records outstanding, so that
    // coincidence does not repeat for them, and the squad would have kept searching with the
    // round's whole objective already on the ship.
    // #874 Q4 (2026-09-24): PrimaryStaged is appended after it, same rule. The round's objective
    // standing at the rendezvous is the pickup request itself (BlacksiteExtractionSequence): the
    // squad has finished the part it owns, and only the ship can take it further.
    internal enum BlacksiteReturnRule { None, RaidersGone, Clock, ObjectivesDone, Withdrawal, ObjectivesLost, PrimarySecured, PrimaryStaged }
    // Decisions are evaluated by the host against current object and carrier state.
    internal static class BlacksiteObjectivePolicy
    {
        internal static bool CanReserve(bool available, bool exists, bool playerHeld, bool crewStored, bool playerPushing, bool retryReady)
            => available && exists && !playerHeld && !crewStored && !playerPushing && retryReady;
        internal static bool CarrierInterrupted(bool exists, bool dead, bool stunned, bool fighting, bool playerHeld)
            => !exists || dead || stunned || fighting || playerHeld;
        /// <summary>
        /// #874 G2: the cargo-threat latch's arming rule. A carrier drops its cargo and holds
        /// <c>HasImmediateCargoThreat</c> for <see cref="CargoThreatLatchSeconds"/> when a threat it
        /// holds is inside <see cref="ImmediateCargoThreatMetres"/>. The test runs on every server
        /// frame against RETAINED facts only - no vision, no scan, no squad report - so every fact
        /// says until when it counts: the body is measured where it stands now and counts until
        /// its retention lapses. #1233 S6 (rule 5): the body is one the threat-response gate
        /// admitted (<c>BlacksiteSoldierAI.ContactEngageable</c>) - a remembered identity or an
        /// exact anchor no longer arms it, and the anchor rule that stood here is deleted.
        /// </summary>
        internal const float ImmediateCargoThreatMetres = 5f;
        internal const float CargoThreatLatchSeconds = 2f;
        internal static bool RetainedThreatArmsCargo(float distance, float retainedUntil, float now)
            => now <= retainedUntil && distance <= ImmediateCargoThreatMetres;
        internal static bool CanExtract(bool loaded, bool exists, bool playerHeld, bool cartPlayerAttached, bool departureConfirmed)
            => departureConfirmed && loaded && exists && !playerHeld && !cartPlayerAttached;
        /// <summary>
        /// #874 (2026-09-22, Rend): whether ONE record still owes the raid work. This is the
        /// <c>outstanding</c> term <see cref="ReturnRule"/> reads.
        ///
        /// #874 Q4 (2026-09-24): a STAGED record is no longer outstanding, primary or not. The
        /// 22 September rule counted a staged primary so the recall could not report the raid
        /// done with its objective on the ground - and on 23 September round 1 that same term was
        /// what kept a hover ship away while a 60 s delivery window waited for its magnet (log
        /// 15521-15523). A staged primary is now the pickup request itself
        /// (<see cref="BlacksiteReturnRule.PrimaryStaged"/>, tested ahead of every clock), and
        /// staged cargo of any kind is lifted by the ship during the extraction sequence's
        /// CargoLift step, which #1293 R2 ends when nothing is owed rather than on a clock.
        /// </summary>
        internal static bool RecordOutstanding(bool mission, bool available, bool reserved, bool carried)
            => mission && (available || reserved || carried);

        /// <summary>The raid's own clock. Moved and named, unchanged at 480 s.</summary>
        internal const float MissionWindowSeconds = 480f;

        /// <summary>
        /// #874 S8.4: 180 s, the existing undiscovered-objective deadline: nothing about the mission
        /// may run past three minutes on evidence of nothing having been found.
        /// </summary>
        internal const float UndiscoveredObjectiveSeconds = 180f;

        /// <summary>
        /// #874 S8.4: the cadence the ship's rendezvous re-measures its raiders on - three samples
        /// across one HolderImmobileWindowSeconds, 5 s.
        /// </summary>
        internal const int SquadMoveSamples = 3;
        internal const float SquadMoveSampleSeconds = HolderImmobileWindowSeconds / SquadMoveSamples;

        /// <summary>
        /// #874 S6.5a: which of the return rules fired, in the order they are tested. The
        /// squad's casualty rule (WithdrawSquad) reaches this as withdrawalCommitted and is named
        /// first because it overrides everything else at the call site.
        /// </summary>
        /// <remarks>
        /// #1066 U2: ObjectivesLost splits the ObjectivesDone branch in two without moving it: the
        /// branch still fires on exactly the same arithmetic, and only its name changes. On
        /// 2026-09-15 the round-end ShadowRaid purge destroyed the drive at t=720.86 and the line
        /// read rule=objectivesDone - a success - for a mission that then recalled the ship, boarded
        /// nobody and finished Aborted. A purge must never print the token a completed raid prints.
        /// </remarks>
        /// <remarks>
        /// #874 (2026-09-20): primarySecured is appended and defaults to false, so every existing
        /// caller and every existing pin keeps its answer. It is tested immediately after the
        /// squad's own existence, ahead of every clock, because it is not a deadline at all: the
        /// round's primary objective is physically on the ship and nothing optional outranks
        /// taking it home. Loot, salvage, survey sweeps and the guard ring are exactly what it
        /// overrides, and the call site latches the pickup, so the commitment cannot be undone by
        /// a record going contestable one tick later.
        /// </remarks>
        /// <remarks>
        /// #874 Q4 (2026-09-24): primaryStaged is appended and defaults to false, same rule. It is
        /// tested right after primarySecured and ahead of every clock: the round's objective is
        /// standing at the rendezvous with nobody holding it, which is the squad's whole part of the
        /// delivery, so the pickup is requested at once and the ship does the rest.
        /// </remarks>
        /// <remarks>
        /// #874 Q11: deploymentPending is appended and defaults to false. While the deploy slice
        /// is still handing out the squad (MoonEscalationState.BlacksiteSoldierDeploymentPending),
        /// an empty or guard-only roster is a squad not yet spawned, not a squad that is gone, so
        /// RaidersGone waits for the drain to finish (or abort, which ends the mission itself).
        /// </remarks>
        /// <remarks>
        /// #1305 C4 (R12) / #1305 E2: cargoAboard alone names the branch - objectivesDone with the
        /// manifest's loaded or delivered mission cargo (the line's loaded= fact), objectivesLost with
        /// nothing aboard: lost is never done. A caller that does not say what is aboard is told
        /// objectivesLost. On 2026-10-03 Burrow (LogOutput.log 21626-21639) a drive withdrawn
        /// noStandingPoint boarded `outstanding=False, loaded=0, rule=objectivesDone`; the #1066 U2
        /// lost latch that #1305 C4 also read is gone, because on Divide (14000, 16546) it would have
        /// named salvage hauled after the written-off apparatus objectivesLost.
        /// </remarks>
        internal static BlacksiteReturnRule ReturnRule(int raiders, bool outstanding, float elapsed,
            bool discoveredObjective, bool withdrawalCommitted,
            bool primarySecured = false, bool primaryStaged = false, bool deploymentPending = false,
            bool cargoAboard = false)
        {
            if (withdrawalCommitted) return BlacksiteReturnRule.Withdrawal;
            if (raiders == 0) return deploymentPending ? BlacksiteReturnRule.None : BlacksiteReturnRule.RaidersGone;
            if (primarySecured) return BlacksiteReturnRule.PrimarySecured;
            if (primaryStaged) return BlacksiteReturnRule.PrimaryStaged;
            if (elapsed >= MissionWindowSeconds) return BlacksiteReturnRule.Clock;
            if (!outstanding && elapsed >= (discoveredObjective ? 30f : UndiscoveredObjectiveSeconds))
                return cargoAboard ? BlacksiteReturnRule.ObjectivesDone : BlacksiteReturnRule.ObjectivesLost;
            return BlacksiteReturnRule.None;
        }

        /// <summary>The rule's printed token, in the S4.0 line contract's spelling.</summary>
        internal static string ReturnRuleToken(BlacksiteReturnRule rule)
        {
            switch (rule)
            {
                case BlacksiteReturnRule.RaidersGone: return "raidersGone";
                case BlacksiteReturnRule.Clock: return "clock";
                case BlacksiteReturnRule.ObjectivesDone: return "objectivesDone";
                // #1066 U2: its own token, so a log grep can tell a finished raid from a purged one.
                case BlacksiteReturnRule.ObjectivesLost: return "objectivesLost";
                // #874 (2026-09-20): the recall the primary objective itself ordered.
                case BlacksiteReturnRule.PrimarySecured: return "primarySecured";
                // #874 Q4: the pickup request the staged objective itself raised.
                case BlacksiteReturnRule.PrimaryStaged: return "primaryStaged";
                case BlacksiteReturnRule.Withdrawal: return "withdrawal";
                default: return "none";
            }
        }

        /// <summary>
        /// #874 S6.5a: the phase a released record takes is <c>AvailabilityAfter(WithdrawalCause(...))</c>.
        /// #1219 A4 reverses the S6.5a answer for an exhausted mission objective: it used to stay
        /// available with its retry at the capped 24 s backoff, re-arming without limit, and that is
        /// an unbounded wait (2026-09-26 Test 3 round 1: 19 attempts and 8 acquisitions on a stuck
        /// cart). The six-failure budget is now the bound: a mission objective that spends it is
        /// withdrawn as <see cref="BlacksiteObjectiveWithdrawal.MissionAttemptsExhausted"/> and
        /// written off as lost (event=lost); with nothing aboard the return rule reports objectivesLost.
        /// Only haul progress clears the budget (<c>BlacksiteObjectiveAttempts.CreditHaulProgress</c>):
        /// new ground on the carry goals, never an acquisition and never ground regained after a
        /// player pushed the cart back (that round's cart moved 9.3 m in attempt 2 and was re-hauled
        /// 34.7 m in attempt 14). The ground is kept per world (inside, outside) on that world's
        /// carry goal, so a cart taken back through the door and re-hauled regains ground in both
        /// worlds and credits nothing, and the uncredited partial carries across the door: a flip
        /// moves the measured partial and loses none of it; the unsampled approach to the door and
        /// the first metres past it are not counted (<c>BlacksiteObjectiveAttempts.ObserveCarry</c>). The
        /// metric is route distance: a navmesh change that shortens a route without the cart moving
        /// can credit once per <see cref="HaulProgressMetres"/>, bounded by that goal's route length.
        /// #1078 (2026-09-15): the local pickup probe withdrew a mission objective after three
        /// bounded probes, ahead of the mission-objective test. It still does not: the probe arm is
        /// deferred on its own backoff (<see cref="DefersLocalProbeClose"/>), and the holder's own
        /// route at each close is the unreachable verdict (#1305 C4). A loot/salvage record still
        /// withdraws on both arms because nothing re-arms for it.
        /// </summary>
        /// <remarks>
        /// #1305 G1 (R19): holderRoute and failures replace hadStandingPoint and default to a
        /// Reachable route, so every other caller keeps its answer. The objective's own unreachable
        /// rule withdraws only on the releasing holder's own Unsupported verdict (no route can even
        /// start) once the objective has spent <see cref="UnsupportedWriteOffFailures"/> failures; a
        /// PartialFrontier route is a temporary physical gap and never a write-off (2026-10-04 round 3,
        /// Player.log 39262: a Partial walk-car-walk route wrote the drive off at failures=1/6). A
        /// probe from the tile's entrance is never the verdict; the traveller is.
        /// </remarks>
        /// <remarks>
        /// #1078 (2026-09-15): the LOCAL PROBE arm is limited to non-mission records. On 2026-09-15
        /// the Payload cart's handle could not be resolved from outside - the standing point was
        /// inside a navmesh carve, see TryHandleApproach's own budget - and at t=520 the third probe
        /// printed "localAttempts=3, objectiveFailures=1/6, result=Unavailable", `outstanding` went
        /// false one line later, Boarding was ordered with rule=objectivesDone and the ship departed
        /// carrying nothing, with the cart still on the ground and one failure of six spent.
        /// #1219 A4: WithdrawalCause's cartImmovable is appended and defaults to false; slice D
        /// raises it for a stuck cart no ship device can lift (<see cref="CartStuckAnswer"/>).
        /// </remarks>
        internal static BlacksiteObjectiveAvailability AvailabilityAfter(BlacksiteObjectiveWithdrawal cause)
            => cause == BlacksiteObjectiveWithdrawal.None
                ? BlacksiteObjectiveAvailability.Available
                : BlacksiteObjectiveAvailability.Unavailable;

        /// <summary>
        /// #1078: WHICH withdrawal rule withdrew the record, or None. The rule
        /// used to be one boolean expression, so a withdrawal could only ever be reported as the
        /// phase it produced and the 2026-09-15 forensics had to infer the arm from localAttempts= on
        /// a neighbouring line. Tested in the order the arms bind: the unreachable rule outranks the
        /// record's kind because it is a fact about the world rather than about the squad's budget.
        /// #1219 A4: an immovable cart is the next world fact and outranks both budget arms; a
        /// mission objective is then withdrawn only by its spent six-failure budget.
        /// </summary>
        internal const int UnsupportedWriteOffFailures = 2;
        internal static BlacksiteObjectiveWithdrawal WithdrawalCause(bool missionObjective, bool exhausted, int localAttempts,
            BlacksiteRouteReach holderRoute = BlacksiteRouteReach.Reachable, int failures = 0, bool cartImmovable = false)
        {
            if (holderRoute == BlacksiteRouteReach.Unsupported && failures >= UnsupportedWriteOffFailures) return BlacksiteObjectiveWithdrawal.NoStandingPoint;
            if (cartImmovable) return BlacksiteObjectiveWithdrawal.CartImmovable;
            if (missionObjective) return exhausted ? BlacksiteObjectiveWithdrawal.MissionAttemptsExhausted : BlacksiteObjectiveWithdrawal.None;
            if (localAttempts >= LocalProbeBudget) return BlacksiteObjectiveWithdrawal.LocalProbes;
            return exhausted ? BlacksiteObjectiveWithdrawal.AttemptsExhausted : BlacksiteObjectiveWithdrawal.None;
        }

        /// <summary>
        /// #1219 D3: the mission's one answer to a stuck cart (<see cref="BlacksiteCartStuckAnswer"/>).
        /// Inside the facility no ship device reaches it, whatever the distance, so the squad
        /// leaves without it (Lawson, 2026-09-27). Outside, the ship lifts it if its device
        /// reaches the cart's pose, and it is written off only if none does.
        /// </summary>
        internal static BlacksiteCartStuckAnswer CartStuckAnswer(bool verdict, bool outside, bool shipCanLift)
            => !verdict ? BlacksiteCartStuckAnswer.None
                : outside && shipCanLift ? BlacksiteCartStuckAnswer.ShipLift
                : BlacksiteCartStuckAnswer.WriteOff;

        /// <summary>
        /// #1219 D3: the withdrawal a dropped record takes once its stuck-cart answer is known.
        /// A ship lift is no withdrawal at all - the record stays owed and nothing is written off,
        /// even on the release that spent the six-failure budget: it is the lift's to load. A
        /// write-off is <see cref="BlacksiteObjectiveWithdrawal.CartImmovable"/>, ahead of the
        /// budget. No verdict is <see cref="WithdrawalCause"/>'s own answer, so A4's budget stays
        /// the backstop for every cart without one.
        /// </summary>
        internal static BlacksiteObjectiveWithdrawal DropWithdrawal(bool missionObjective, bool exhausted, BlacksiteCartStuckAnswer stuck)
            => stuck == BlacksiteCartStuckAnswer.ShipLift ? BlacksiteObjectiveWithdrawal.None
                : WithdrawalCause(missionObjective, exhausted, 0, cartImmovable: stuck == BlacksiteCartStuckAnswer.WriteOff);

        /// <summary>#1219 D3: the answer= token on the [UAT][Objective] event=cartStuck line.</summary>
        internal static string CartStuckAnswerToken(BlacksiteCartStuckAnswer answer)
            => answer == BlacksiteCartStuckAnswer.ShipLift ? "shipLift"
                : answer == BlacksiteCartStuckAnswer.WriteOff ? "writeOff" : "none";

        /// <summary>
        /// #1219 A4: a withdrawal that writes a mission objective off as lost (the #1066 U2
        /// NoteObjectiveLost event=lost line); the return rule then reads only what is aboard.
        /// #1305 C4 (R12): every mission withdrawal does, the unreachable rule's NoStandingPoint
        /// included - on 2026-10-03 Burrow (LogOutput.log 21626-21639) its exclusion let a drive
        /// nobody reached board as objectivesDone. LocalProbes and AttemptsExhausted withdraw only
        /// loot and salvage, which are never mission objectives.
        /// </summary>
        internal static bool WritesOffObjective(BlacksiteObjectiveWithdrawal cause)
            => cause == BlacksiteObjectiveWithdrawal.NoStandingPoint
                || cause == BlacksiteObjectiveWithdrawal.MissionAttemptsExhausted
                || cause == BlacksiteObjectiveWithdrawal.CartImmovable;

        /// <summary>#1219 A4: the withdrawal= token on the [UAT][Job] and [UAT][Objective] lines.</summary>
        internal static string WithdrawalToken(BlacksiteObjectiveWithdrawal cause)
        {
            switch (cause)
            {
                case BlacksiteObjectiveWithdrawal.LocalProbes: return "localProbes";
                case BlacksiteObjectiveWithdrawal.AttemptsExhausted: return "attemptsExhausted";
                case BlacksiteObjectiveWithdrawal.NoStandingPoint: return "noStandingPoint";
                case BlacksiteObjectiveWithdrawal.MissionAttemptsExhausted: return "missionAttemptsExhausted";
                case BlacksiteObjectiveWithdrawal.CartImmovable: return "cartImmovable";
                default: return "none";
            }
        }

        /// <summary>
        /// #1219 A4: how much NEW ground a carry must gain toward its carry goal before the haul
        /// counts as progress and clears the objective's failure history
        /// (<c>BlacksiteObjectiveAttempts.ObserveCarry</c>). The 5 m
        /// <see cref="HolderNearApproachMetres"/> figure, reused: well past the 0.5 m progress
        /// epsilon and any jitter at the handle, and short of any real haul.
        /// </summary>
        internal const float HaulProgressMetres = HolderNearApproachMetres;

        /// <summary>
        /// #1153: whether the board may spend WORK on this reservation yet - resolve its approach
        /// ring, and hold the one <c>ObjectiveBoard</c> - or whether its operator is still crossing
        /// into the target's world.
        ///
        /// On 2026-09-20 the ring was resolved from the ship deck (52682 `ringExhausted
        /// operator=(-113.02, 20.76, -3.00) result=unroutedFallback`, every bearing
        /// `unrouted(PathPartial)`), because a route from the deck to a spot inside a facility is
        /// partial whatever the spot is. Nothing consumes that answer - ObserveJob's goal for a
        /// cross-world operator is already the entrance and HasPhysicalAccess already refuses a
        /// different world space - and it left behind an unrouted standing point the reachability
        /// rule reads as "nobody can stand here", plus a board held by a member who had not
        /// started.
        ///
        /// It is a rule rather than an inline test because both chokepoints ask it: the approach
        /// resolver (<c>TryPickupApproach</c>, which every pickup point chains through) and the
        /// per-tick board renewal (<c>ReconcileObjectiveClaims</c>). <paramref name="carried"/>
        /// keeps a CARRY out of it: a carried record keeps the world it was reserved in by design,
        /// so the leg to the ship must not drop his goal or his board.
        /// </summary>
        internal static bool MayWorkRecord(bool operatorInTargetWorld, bool carried)
            => operatorInTargetWorld || carried;

        /// <summary>The bounded local pickup probe, unchanged at three probes at the object.</summary>
        internal const int LocalProbeBudget = 3;

        /// <summary>
        /// #1078 rework: whether a release on the spent local probe budget must be deferred on the
        /// objective's own capped attempt backoff instead of the flat one-second retry.
        ///
        /// The three-probe close is reported as Interrupted with cause ApproachUnreachable, and
        /// Interrupted spends no failure history (BlacksiteObjectiveAttempts.Close only fails on
        /// Failed), does not bench the operator (only a Failed close does) and used to set the retry
        /// one second out. Once the record also re-arms instead of withdrawing - which is the rest of
        /// #1078 - that is a loop: reserve, three probes over about three seconds, Available, the
        /// same operator, forever, with nothing but the 480 s clock to end it. Re-arming without a
        /// backoff is not re-arming, it is spinning.
        ///
        /// The backoff is the one the exhaustion arm already uses - 1, 3, 6, 12, 24, 24 s - applied
        /// to a count of probe closes rather than of squad failures, so a handle that cannot be
        /// resolved costs one attempt per backoff step and the whole schedule spans 70 s. No new
        /// schedule and no new constant: a longer deadline would hide the failure, and this one
        /// bounds it instead.
        /// </summary>
        internal static bool DefersLocalProbeClose(bool missionObjective, int localAttempts)
            => missionObjective && localAttempts >= LocalProbeBudget;

        /// <summary>
        /// #1271 S2 (rule 4): reachability is decided where a standing point is chosen. A point is
        /// published only on a Reachable route - from the operator for the witness's stand, from the
        /// tile's entrance for a ring - and refused, by name, on a PartialFrontier or Unsupported one. Null
        /// accepts. On 2026-09-29 round 2 member 1 reserved the witness at t=411.57 (Player.log
        /// 21707) and every witnessPoint line from 21784 (t=411.82) on read navStatus=PathPartial; the
        /// point was published anyway and the operator stood at the end of the partial route.
        /// </summary>
        internal static string StandRefusal(BlacksiteRouteReach reach)
            => reach == BlacksiteRouteReach.Reachable ? null : "the route to it is " + reach;

        /// <summary>
        /// #1283 S6: where a refused stand was measured from decides what the refusal is about. Same
        /// world and within six metres, it is about the SPOT: RefuseApproach charges a probe for it and
        /// the three-probe close owns it. From farther, or from the other world, it is about the ROUTE -
        /// a refusal measured from 190 m away is a statement about the route, not about the spot - so
        /// no probe is charged and the squad searches until a measurement publishes a stand.
        /// </summary>
        internal const float LocalRefusalMetres = 6f;
        internal static bool RefusalIsLocal(bool sameWorld, float operatorDistance)
            => sameWorld && operatorDistance <= LocalRefusalMetres;

        /// <summary>
        /// #874 S6.4a AC4: 5 m. The acceptance criterion's own figure, well outside every gate the
        /// last few metres use - PickupReachDistance 0.80, the container hand-off window
        /// ObjectiveApproachDistance 3.40. #1293 R2: no longer a holder veto (the ledger's miss is
        /// the one judge of a holder's progress); <see cref="HaulProgressMetres"/> reuses it.
        /// </summary>
        internal const float HolderNearApproachMetres = 5f;
        /// <summary>
        /// #874 S6.4a AC4: 0.5 m over 15 s. The holder-track window and distance; the closing
        /// window and the ship's rendezvous sample are measured in the same terms.
        /// </summary>
        internal const float HolderImmobileMetres = .5f;
        internal const float HolderImmobileWindowSeconds = 15f;
        /// <summary>
        /// #874 S6.4a AC4: the closing window, 10 s. Its epsilon is the job's own progress epsilon
        /// (BlacksiteRaidJob credits progress at remaining &lt; best - 0.5), so "the route closed"
        /// and "the watermark moved" are the same event.
        /// </summary>
        internal const float HolderClosingWindowSeconds = 10f;
        internal const float HolderClosingEpsilonMetres = .5f;

        /// <summary>
        /// #874 S6.4a / S4: the veto on the ledger's stalled answer for a record holder. Pure:
        /// every input is measured by the manifest. Kept only while waiting on a traversal, in
        /// combat or committed to a crossing - facts that end on their own events, after which the
        /// ledger's next window answers again. #1293 R2: the near-approach and still-closing arms
        /// are gone with the 52 s claim ceiling that bounded them; they judged progress a second
        /// time, so a holder stuck short of its point was kept for ever.
        /// </summary>
        internal static BlacksiteHolderRelease ShouldReleaseHolder(bool waiting, bool combat, bool committed)
            => waiting || combat || committed
                ? BlacksiteHolderRelease.Keep
                : BlacksiteHolderRelease.ProgressBudget;

        /// <summary>A candidate in the other world still has to cross it. Unchanged at 250.</summary>
        internal const float WrongWorldPenalty = 250f;
        /// <summary>
        /// The longest route the score carries. Longer routes are clamped to it, which changes no
        /// ordering that matters (nothing on an authored moon comes close) and makes the
        /// complete-route bias below exact rather than a guessed magic number.
        /// </summary>
        internal const float RouteBudgetMetres = 10000f;
        /// <summary>
        /// #874 S6.4a item 7: one metre more than the worst score a COMPLETE route can produce,
        /// so a complete route always beats an incomplete one and no arithmetic can invert that.
        /// The old rule was RouteDistance + 30 for an incomplete path, under which the 2026-09-09
        /// board would have preferred an incomplete 60 m route to a complete 120 m one.
        /// </summary>
        internal const float RouteIncompleteBias = RouteBudgetMetres + WrongWorldPenalty + 1f;

        /// <summary>
        /// #874 S6.4a item 7: how the board ranks operators. Complete routes first, then the
        /// shortest, with the same-world preference the board already had.
        /// </summary>
        internal static float OperatorScore(bool routeComplete, float routeLength, bool sameWorld)
        {
            float length = float.IsNaN(routeLength)
                ? RouteBudgetMetres
                : Math.Min(Math.Max(routeLength, 0f), RouteBudgetMetres);
            return length + (sameWorld ? 0f : WrongWorldPenalty) + (routeComplete ? 0f : RouteIncompleteBias);
        }
    }
}
