using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #947 S12 U2: NoEffect is APPENDED, so no existing ordinal moves and the four existing reasons
    // keep their exact meaning. It is the residual verdict: the band is right, the contact is seen,
    // the lane is clear, and a whole magazine still produced nothing.
    // #1219 F2: Strafe is appended the same way. It is the stand-up fight's own movement: in the
    // band, seen, lane clear, and the member has held one spot for a whole StrafeIntervalSeconds.
    internal enum BlacksiteRepositionReason { None, TooClose, OutOfRange, LostSight, BlockedLane, NoEffect, Strafe }
    internal enum BlacksiteNoContactAction { ResumeDuty, FinishAction, ContinueReposition, Hold, Search }
    internal enum BlacksiteFireReadiness
    {
        Ready, Disabled, Reloading, Recovering, GrenadeCommitted, Firing, Repositioning,
        NoTarget, Ineligible, OutOfRange, LostSight, Acquiring, Settling, FriendlyLane,
        PoseMissing, PoseNotEngaged, PoseBlending, HandsBusy, GripMisaligned,
        AnimatorMissing, AnimatorTransition, AnimationState, BodyMisaligned, RifleMisaligned, ApproachBlocked,
        // #874 S2.1: inside the melee reach but outside its facing cone. The per-tick facing step
        // owns that frame; issuing another destination there is what froze the v11 raiders.
        Turning,
        // #874 S7.2: appended, never inserted, so no existing ordinal moves. HoldingLane is the
        // melee stance for a lane ring that is occupied now while the contact itself is still
        // reachable: hold, face, retry the ring from a rotated bearing on the next engagement.
        // ApproachBlocked keeps its old meaning, which is now the narrower one - no route to the
        // contact at all, where handing the planner an alternative approach is still correct.
        // #1271 S3a: ApproachBlocked is also the rifle's verdict when its search finds neither cover
        // nor an approach. Nothing persists these ordinals, so a removed value moves no stored state.
        HoldingLane,
        // #874 S10.3: appended, never inserted, so no existing ordinal moves. The suppression point
        // is older than BlacksiteCombatController.SuppressionStaleSeconds, so it describes the
        // geometry the contact left rather than the contact. A stance, not a failure to act: the
        // member keeps its target, its facing and its memory and simply does not fire at a wall.
        SuppressionStale
    }

    /// <summary>
    /// #1019 S5: what the engagement DID this decision, in eight values, beside the twenty-eight
    /// above rather than instead of them.
    ///
    /// Every DECISION reads this; every REPORT keeps <see cref="BlacksiteFireReadiness"/> as its
    /// detail, so <c>reason=</c> on [UAT][CombatDecision] and [UAT][Melee] keeps its 28-value
    /// vocabulary and the thirteen native string comparisons against
    /// <c>LastBatonReadiness.ToString()</c> and <c>FireReadiness.ToString()</c> are unchanged.
    /// score.ps1 parses no readiness value at all (it has no CombatDecision, Shot, Melee or Baton
    /// parser; design note A.10), so this collapse costs the scorer nothing.
    ///
    /// #1020 S6 asked which of the readiness values no longer occur once the legacy decision
    /// path is deleted, and the answer is NONE: every value's producers live in
    /// BlacksiteCombatController, …Equipment.cs, BlacksiteSoldierAimPose and
    /// BlacksiteSoldierAI.GetCombatAimReadiness, all of them reached through
    /// TryExecuteSelectedCombat, which is what the one decision core calls. So S6 deletes no value
    /// and PROVES the set instead: BlacksiteTierOneRegression asserts this switch's mapping value
    /// by value and asserts that all eight outcomes are produced (design note C.4, ruling G4).
    /// </summary>
    internal enum BlacksiteEngagementOutcome : byte
    {
        /// <summary>A shot or a swing released, or one already in flight.</summary>
        Firing = 0,
        /// <summary>In the band and committing shortly.</summary>
        Aiming = 1,
        /// <summary>Walking a lane or a reposition.</summary>
        Closing = 2,
        /// <summary>In position, deliberately not acting yet.</summary>
        Holding = 3,
        /// <summary>The contact is known and the line is refused.</summary>
        NoLine = 4,
        /// <summary>The contact is known and the geometry is wrong.</summary>
        OutOfBand = 5,
        /// <summary>The weapon or the body cannot act.</summary>
        NotArmed = 6,
        /// <summary>There is nothing to act on.</summary>
        NoContact = 7
    }

    // Pure decisions shared by the runtime and the encounter regression harness.
    internal static class BlacksiteCombatPolicy
    {
        /// <summary>
        /// #1019 S5: the 28 readiness values collapsed to the eight outcomes a DECISION is taken
        /// on. Total over the enum and exhaustive by construction - every value has an explicit
        /// arm and the default is <see cref="BlacksiteEngagementOutcome.NotArmed"/>, which is the
        /// safe answer for a value this switch has not been taught (a member that does not act is
        /// recoverable; one that fires on an unknown readiness is not).
        ///
        /// Callers that need a value rather than an outcome keep asking for the value: the
        /// <c>LostSight</c> arm of EngageBaton's release switch releases on that one NoLine value
        /// and retains the others, which no outcome can express, and stays a value test.
        /// </summary>
        internal static BlacksiteEngagementOutcome Outcome(BlacksiteFireReadiness readiness)
        {
            switch (readiness)
            {
                // Acting on the contact this decision.
                case BlacksiteFireReadiness.Ready: return BlacksiteEngagementOutcome.Firing;
                case BlacksiteFireReadiness.Firing: return BlacksiteEngagementOutcome.Firing;
                case BlacksiteFireReadiness.GrenadeCommitted: return BlacksiteEngagementOutcome.Firing;

                // In the band, not yet committed.
                case BlacksiteFireReadiness.Acquiring: return BlacksiteEngagementOutcome.Aiming;
                case BlacksiteFireReadiness.Settling: return BlacksiteEngagementOutcome.Aiming;
                case BlacksiteFireReadiness.Turning: return BlacksiteEngagementOutcome.Aiming;

                // Moving.
                case BlacksiteFireReadiness.Repositioning: return BlacksiteEngagementOutcome.Closing;

                // In position and deliberately not acting.
                case BlacksiteFireReadiness.HoldingLane: return BlacksiteEngagementOutcome.Holding;
                case BlacksiteFireReadiness.Reloading: return BlacksiteEngagementOutcome.Holding;
                case BlacksiteFireReadiness.Recovering: return BlacksiteEngagementOutcome.Holding;

                // The contact is known and the line is refused.
                case BlacksiteFireReadiness.LostSight: return BlacksiteEngagementOutcome.NoLine;
                case BlacksiteFireReadiness.FriendlyLane: return BlacksiteEngagementOutcome.NoLine;
                case BlacksiteFireReadiness.SuppressionStale: return BlacksiteEngagementOutcome.NoLine;

                // The contact is known and the geometry is wrong.
                case BlacksiteFireReadiness.OutOfRange: return BlacksiteEngagementOutcome.OutOfBand;
                case BlacksiteFireReadiness.ApproachBlocked: return BlacksiteEngagementOutcome.OutOfBand;

                // There is nothing to act on.
                case BlacksiteFireReadiness.NoTarget: return BlacksiteEngagementOutcome.NoContact;
                case BlacksiteFireReadiness.Ineligible: return BlacksiteEngagementOutcome.NoContact;

                // The weapon or the body cannot act: Disabled and the eleven pose, grip, animator
                // and alignment refusals.
                case BlacksiteFireReadiness.Disabled: return BlacksiteEngagementOutcome.NotArmed;
                case BlacksiteFireReadiness.PoseMissing: return BlacksiteEngagementOutcome.NotArmed;
                case BlacksiteFireReadiness.PoseNotEngaged: return BlacksiteEngagementOutcome.NotArmed;
                case BlacksiteFireReadiness.PoseBlending: return BlacksiteEngagementOutcome.NotArmed;
                case BlacksiteFireReadiness.HandsBusy: return BlacksiteEngagementOutcome.NotArmed;
                case BlacksiteFireReadiness.GripMisaligned: return BlacksiteEngagementOutcome.NotArmed;
                case BlacksiteFireReadiness.AnimatorMissing: return BlacksiteEngagementOutcome.NotArmed;
                case BlacksiteFireReadiness.AnimatorTransition: return BlacksiteEngagementOutcome.NotArmed;
                case BlacksiteFireReadiness.AnimationState: return BlacksiteEngagementOutcome.NotArmed;
                case BlacksiteFireReadiness.BodyMisaligned: return BlacksiteEngagementOutcome.NotArmed;
                case BlacksiteFireReadiness.RifleMisaligned: return BlacksiteEngagementOutcome.NotArmed;
                default: return BlacksiteEngagementOutcome.NotArmed;
            }
        }

        internal const float CrouchEnterSpeedThreshold = 1.2f, CrouchExitSpeedThreshold = 2f;
        internal const float CrouchMinimumHoldSeconds = 4.5f, CrouchFastMoveExitDelaySeconds = .8f, CrouchReentryCooldownSeconds = 2f;
        internal const float CombatHitPoseInterval = 2.5f;
        internal static bool AllowHitPose(bool hardStun, bool engaged, float now, float lastPoseAt)
            => hardStun || !engaged || now - lastPoseAt >= CombatHitPoseInterval;
        internal const float BlockedLaneGraceSeconds = 0.7f;
        internal const float BodyAimToleranceDegrees = 18f;
        internal const float RifleAimToleranceDegrees = 10f;

        // #1064 WP3: the hands arbitration, as three pure rules so the headless suites pin the
        // production decision rather than restating it.
        //
        // ArmedDwellSeconds is a GUARD, not the fix for the 2026-09-15 D3 flip. That flip - a 20 ms
        // `from=Disabled, to=Ready` immediately followed by `from=Ready, to=Disabled`, once per
        // member on a roughly one-second round robin - is closed by narrowing the traversal arm of
        // CurrentHandOwner to Navigator.RequiresBothHands, and by that alone. Against the CURRENT
        // disable set this dwell never fires, and that is stated rather than discovered: every
        // source in that set is reported immediate by the caller, and the one non-immediate claim
        // the arbitration can see - the replicated _climbingPose - is derived on the host from the
        // same facts as RequiresBothHands one frame earlier, so in the single trailing frame where
        // it is true on its own the weapon is already Disabled and there is no dwell to hold.
        //
        // What it buys is that a FUTURE non-immediate claim cannot reintroduce the twitch: once a
        // weapon is armed it stays armed for this long unless something that PHYSICALLY owns the
        // hands or the body says otherwise (a two-hand traversal leg, cargo, the carry handback, an
        // elevator ride, a teleport, stun or death - all of which the caller reports as immediate).
        // 1.5 s is two orders above the flip it is sized against. BlacksiteReactiveRegression drives the claim
        // synthetically so the rule is pinned even though production no longer reaches it.
        internal const float ArmedDwellSeconds = 1.5f;

        /// <summary>
        /// #1064 WP3: true while an armed weapon is inside its dwell and a non-immediate stow
        /// request must be refused. <paramref name="armedAt"/> is the time the weapon last left
        /// <c>Disabled</c>; a weapon that has never armed passes <c>float.NegativeInfinity</c> and
        /// is never held.
        /// </summary>
        internal static bool HoldsArmedDwell(float now, float armedAt)
            => !float.IsNegativeInfinity(armedAt) && !float.IsNaN(armedAt) && now - armedAt < ArmedDwellSeconds;

        /// <summary>
        /// #1064 WP3: an <see cref="BlacksiteThreatIntent.Engage"/> decision over a LIVE EXACT
        /// contact beats an interruptible claim on the hands. <paramref name="handsPhysicallyTaken"/>
        /// is a leg that needs both hands, cargo, the carry handback or an incapacitated actor -
        /// none of which an intent can argue with - and <paramref name="bodyAvailable"/> is false
        /// while a motor owns the body (an elevator ride, an entrance teleport, stun, an
        /// uninitialised or non-combat lifecycle). What is left is a claim the member could drop,
        /// and a member that has decided to fight drops it.
        ///
        /// Like <see cref="ArmedDwellSeconds"/> this is a GUARD rather than a live path: against
        /// the current disable set there is no arbitrable claim for it to win, because the one
        /// non-immediate source - the replicated <c>_climbingPose</c> - only trails a leg that has
        /// already stowed the weapon. It is the rule a future soft claim is answered by, pinned
        /// value by value in BlacksiteReactiveRegression.
        /// </summary>
        internal static bool EngagementWinsHands(
            BlacksiteThreatIntent intent, bool hasExactContact, bool handsPhysicallyTaken, bool bodyAvailable)
            => intent == BlacksiteThreatIntent.Engage && hasExactContact && !handsPhysicallyTaken && bodyAvailable;

        // #1064 WP3: the per-round alignment gate is read at the band a MOVING body can actually
        // hold when the member reaching it is moving. #1219 F2: that is now the ordinary case - the
        // readiness steps no longer stop the body, a committed combat step keeps walking through
        // the ramp, the settle and the burst (at FiringWalkSpeed while a burst fires), and only the
        // movement decision itself holds a member that has nowhere to go. The aim
        // overlay and the look/spine IK solve toward the point every frame, but the base locomotion
        // clip swings the torso and the muzzle with each stride, and the 2026-09-15 D1 capture spent
        // nine consecutive thinks (2.45 s) between two bursts refused by
        // BodyMisaligned/RifleMisaligned while the member was repositioning on a visible contact.
        // The band is WIDENED while moving rather than removed: a stationary shot is still held to
        // 18/10 degrees exactly as before.
        internal const float MovingShotSpeedThreshold = .5f;
        internal const float MovingAimToleranceBonusDegrees = 6f;

        /// <summary>#1064 WP3: is this member's own planar speed a walking shot rather than a standing one.</summary>
        internal static bool IsMovingShot(float planarSpeed)
            => !float.IsNaN(planarSpeed) && planarSpeed > MovingShotSpeedThreshold;

        // #874 S7.2/S7.3: the two numbers combat arbitration is decided on. Both live here, beside
        // the S2.1b envelope and not inside it, because neither is a reach measurement and neither
        // may be read as one: the envelope below is spent exactly and does not move.
        //
        // MaximumEngagementDistance is the rifle's own engagement ceiling. It was a private const
        // in BlacksiteCombatController; it moved here unchanged so ThreatRadius can be derived from
        // it in one Unity-free place rather than transcribed, and the controller now reads it back.
        internal const float MaximumEngagementDistance = 11f;
        // The navigator's ordinary goal hysteresis: a destination less than one metre from the last
        // one is not re-issued (BlacksiteNavigator.cs:161, reissueThreshold 1f on a squared
        // magnitude). One agent step, in other words.
        internal const float NavigatorGoalHysteresisMetres = 1f;
        // A contact a rifle would already be shooting at, or one that is a single goal re-issue
        // outside that, is a threat for EVERY role - cover, assist search, guard or carrier. 12 m.
        // Above every stand-off the 2026-09-09 run produced (8.25 to 9.07 m), so the members that
        // stood and watched at those ranges are inside it, and well outside BatonAttackRange, which
        // is the inner threat band rather than a competitor to this one.
        internal const float ThreatRadius = MaximumEngagementDistance + NavigatorGoalHysteresisMetres;

        /// <summary>
        /// #1011 A3 (observed failure O3): how far a squadmate may be held by a monster and still
        /// be this member's business.
        ///
        /// It is <see cref="ThreatRadius"/> exactly, and deliberately so rather than by accident: a
        /// captor holding a squadmate twelve metres away is a contact a rifle would already be
        /// engaging, and the whole point of the rescue rule is that the baton member walks to it
        /// instead. Nothing widens - a captor outside this radius is an ordinary contact answered the
        /// ordinary way, and a member outside it keeps whatever it was doing.
        /// </summary>
        internal const float RescueRadius = ThreatRadius;

        /// <summary>
        /// #1011 A3: whether a captured squadmate at <paramref name="allyDistance"/> metres is
        /// close enough to make its captor this member's preferred target. The pure half of the
        /// acquisition rule, so <c>BlacksiteBehaviourRegression</c> pins the runtime's own test
        /// rather than a transcription of it.
        ///
        /// A negative distance is refused rather than treated as zero: the only way to produce one
        /// is a caller that has not measured anything, and "I have not looked" is not "he is on top
        /// of me". The bound is inclusive, so a victim standing exactly on the radius is rescued -
        /// the radius names the last metre that counts, not the first that does not.
        /// </summary>
        internal static bool RescueTakesPriority(float allyDistance, float radius = RescueRadius)
            => allyDistance >= 0f && allyDistance <= radius;

        // #988 B3: an alert about a monster is an ORDER, not a notification. The 2026-09-12 run
        // told members 1, 2 and 3 about a crawler at 18 to 20 m and members 2 and 3 answered with
        // `respondedAt=none`; they engaged only when it walked up to them 15 s later, so a 4v1
        // was fought as four sequential 1v1s.
        //
        // Two radii, and they are different questions. The REPORTER has to be in the fight for the
        // report to be an order at all, which is ThreatRadius - the same 12 m every other threat
        // consumer uses, so nothing widens what counts as a threat. The RECIPIENT radius is how far
        // a raider may be told to come, and it is the lane geometry's own arithmetic rather than a
        // chosen number: a member ordered in walks a lane on the stand-off circle at the role speed
        // the navigator reports (4.80 m/s), and 18 m is what that member covers in 3.75 s. Told
        // from further out than its own squadmate can live, an order arrives after the fight it was
        // meant for.
        //
        // #1065 WP4: SoldierHealth doubled to 20, so the victim this radius was measured against -
        // one taking a 2-force bite every 0.77 s - now lives 7.7 s rather than 3.85 s. The radius
        // is deliberately NOT widened with it. 18 m was derived as the furthest an order can still
        // be useful from, the doubling only turns that equality into margin, and a wider order
        // radius is a squad-formation change nobody has asked for.
        internal const float MonsterAlertOrderRadius = 18f;
        // How long one report keeps ordering. Every fresh report refreshes it, so a live contact
        // holds the order for as long as somebody can see it; 6 s past the last report is one
        // 18 m walk-in plus one baton draw (1.233 s) and no more, so a contact that died or left
        // stops ordering without needing its own release message.
        internal const float MonsterAlertOrderSeconds = 6f;
        // #988 B3: how long a hit stays a fact (HasRecentDamage): a baton member that has just been
        // bitten converges on its live contact (TryConvergeOnOrderedContact) instead of leaving it,
        // and ResolveThreatIntent's one-rescuer arm reads the same window. 3 s is just under the
        // 3.85 s a soldier survived a bite cadence, so it never outlives the fight. #1305 H (C2):
        // it is a fact for convergence only; the decision has no damage lock.
        internal const float DamageCombatLockSeconds = 3f;
        // #988 round 2, SHOULD-FIX 3: how much closer a rival contact has to be before a held order
        // is replaced. It is the melee lane separation, LaneSeparation(BatonLaneSpacingDegrees) =
        // 2 x 1.72 x sin(30) = 1.72 m: one whole lane step, the smallest distance at which two
        // contacts are somewhere different for a member that is walking to a lane on one of them.
        // Below it the two orders are the same errand and the held one keeps the member.
        internal static readonly float MonsterAlertOrderSwapMargin = LaneSeparation(BatonLaneSpacingDegrees);
        // How often one recipient may be re-ordered at all. #1293 R1: the director issues orders
        // from the perception pass for every member whose sight bit holds a Monster row, so a
        // contact seen every pass by four members must not reconsider a third member's order four
        // times a pass.
        internal const float MonsterAlertOrderCooldownSeconds = 1f;
        /// <summary>
        /// #988 round 2, SHOULD-FIX 3: does a rival contact replace the order this member is
        /// already holding? Only if it is at least one lane step nearer. Last-writer-wins made a
        /// raider inside two reporters' radii walk X, Y, X on consecutive thinks; this is the same
        /// shape as the S10.1 target commitment, and it is Unity-free so the runtime and
        /// BlacksiteBehaviourRegression ask one question.
        /// </summary>
        internal static bool MonsterAlertOrderReplaces(float heldRange, float rivalRange)
            => rivalRange <= heldRange - MonsterAlertOrderSwapMargin;
        // The order test itself, so the squad director and the regression ask one question.
        internal static bool MonsterAlertOrders(float contactToReporter, float contactToMember)
            => contactToReporter <= ThreatRadius && contactToMember <= MonsterAlertOrderRadius;
        // #988 B5: when a live monster contact stops standing in the withdrawal's way. Dead, or
        // ThreatRadius out - the same 12 m that made it an order in the first place, so the
        // withdrawal is deferred by exactly the contacts combat is fighting and by no others.
        internal const float WithdrawalContactClearRange = ThreatRadius;

        // #1293 R1: inOwnSight is the pass's own-sight bit; its 0.35 s grace is the occlusion hold.
        internal static BlacksiteNoContactAction NoContactAction(bool memoryAlive, bool committedAction,
            bool repositioning, bool reloading, bool recovering, bool defensiveOnly, bool inOwnSight)
        {
            if (committedAction) return BlacksiteNoContactAction.FinishAction;
            // A lost sightline cannot erase a path committed by combat, including a reload move.
            if (repositioning) return BlacksiteNoContactAction.ContinueReposition;
            if (!memoryAlive && !reloading && !recovering) return BlacksiteNoContactAction.ResumeDuty;
            if (!memoryAlive || reloading || recovering || defensiveOnly || inOwnSight)
                return BlacksiteNoContactAction.Hold;
            return BlacksiteNoContactAction.Search;
        }

        // #947 S12 U2: the rifle's own magazine, moved here from BlacksiteCombatController's private
        // const block (unchanged value, the controller reads it back) for the same reason
        // MaximumEngagementDistance moved in S7.2: the rule below is derived from it in one
        // Unity-free place rather than transcribed, and BlacksiteBehaviourRegression can pin it.
        internal const int MagazineCapacity = 18;

        // #1065 WP4 (owner decision 2026-09-15): the four lethality facts of a squad-versus-monster
        // fight, named and gathered here because each of them used to be a bare literal at its own
        // call site and no test could see any of them. They are pinned by BlacksiteTierOneRegression.
        //
        // RifleDamagePerRound was the literal 1 in BlacksiteCombatController.ApplyShotDamage. The
        // 2026-09-15 log is the argument: eight rounds fired, five landed, and an eyeless dog at
        // enemyHP 12 went 12 -> 7 while it killed six soldiers. At 2 the same dog is six landed
        // rounds, which one member alone can produce in about 5 s of aimed single fire
        // (SingleRecoverySeconds .55 + AimSettleSeconds .3 = .85 s a round) or two bursts in about
        // 2.7 s. That is what MonsterCaptureRearmSeconds below is measured against.
        internal const int RifleDamagePerRound = 2;

        // Was 10 (#937, 2026-09-10). Doubled with the rifle so the squad's own exchange rate is
        // unchanged against a PLAYER - the shotgun and shovel numbers did not move - while a
        // monster's 25-raw-to-1-force conversion (BlacksiteSoldierAI.ApplyVanillaMonsterDamage)
        // needs twice as many contacts to take a member off the board. BlacksiteSoldierAI.cs keeps
        // its own private forwarder of this name so its spawn-state call site is untouched.
        internal const int SoldierHealth = 20;

        // The raw damage a capture applies when its grace expires. It is far above
        // SoldierHealth * 25 on purpose: a capture that runs its clock out is lethal, and the grace
        // - not a survivable damage number - is what makes a rescue possible.
        internal const int CaptureKillRawDamage = 1000;

        // How long a captured member is held before the capture kills. #1065 WP4: every capture
        // kind now has one. DogGrab used to be special-cased to kill on the CONTACT FRAME in
        // BlacksiteVanillaMonsters.BeginCapture, so all six 2026-09-15 dog kills went
        // Captured -> Dead at the same timestamp and no swing, burst or stun could ever have landed
        // inside the window. A dog is now exactly the Bracken/Flea case: .65 s in which a baton stun
        // or a killing burst ends the capture and the member walks away.
        internal const float CaptureKillGraceSeconds = .65f;

        // A Forest Keeper is the one exception and keeps its own, much longer window (4.7 s), which
        // is the length of the native EatPlayer animation it is synchronised to. It was already the
        // only branch in the runtime's killAt expression; naming it here is what lets the test
        // state that DogGrab is NOT a branch.
        internal const float ConsumingCaptureKillGraceSeconds = 4.7f;

        /// <summary>
        /// The grace a capture gets before it kills, as a function of the one thing that varies:
        /// whether the captor consumes its victim on a native animation clock.
        /// </summary>
        internal static float CaptureKillGraceSecondsFor(bool consuming)
            => consuming ? ConsumingCaptureKillGraceSeconds : CaptureKillGraceSeconds;

        // How long a captor must wait after ANY capture of its own ends before it may open another
        // one, shared across the whole squad because the cooldown is keyed on the captor. Without
        // it the dog's kill cadence was travel time alone - it re-armed 1 s after the grab and took
        // six members in 21.5 s, about 3.6 s apiece. 5 s is the time one surviving member needs to
        // finish a 12 HP dog unaided at RifleDamagePerRound (six landed rounds, ~5.1 s of aimed
        // single fire), so a squad that loses one member to a capture always gets a whole kill's
        // worth of shooting back before it can lose another.
        internal const float MonsterCaptureRearmSeconds = 5f;

        // How many rounds may miss before the member has to change something. It is the magazine,
        // not a chosen number: "he emptied a magazine into a rock" is the observation, and one
        // magazine is the smallest unit of fire that cannot be explained by spread, recoil or a
        // contact that stepped behind cover for a burst.
        //
        // The fire cadence is what makes that unit the right size. A full magazine is 18 rounds; at
        // the fastest cadence the controller can produce it is six three-round bursts of
        // 2 x BurstCadenceSeconds 0.12 + BurstRecoverySeconds 0.8 = 1.04 s, plus six
        // AimSettleSeconds 0.3 re-settles = 8.0 s, and at the slowest it is 18 single rounds of
        // SingleRecoverySeconds 0.55 + 0.3 = 15.3 s. So NoEffect can never fire sooner than 8 s of
        // continuous shooting: it is an order of magnitude above the 0.35 s sight grace and
        // BlockedLaneGraceSeconds 0.7, which own the short transient causes of a miss. Half a
        // magazine (9 rounds, 4.0 to 7.7 s) was rejected for sitting too close to them.
        internal const int NoEffectRounds = MagazineCapacity;

        // roundsWithoutEffect defaults to 0, "nothing has missed", so the eight existing
        // BlacksiteTierOneRegression cases - every one of them about the four older reasons - keep
        // asking the question they already asked, in a file this unit does not own. The runtime
        // caller, BlacksiteCombatController.UpdateTacticalMovement, always passes its real count.
        // #1219 F2: strafeDue is the caller's cadence clock (StrafeIntervalSeconds since his last
        // move ended), false by default for the same reason.
        // #1293 R1: visible is the pass's own-sight bit; its 0.35 s grace is the corner-peek grace.
        internal static BlacksiteRepositionReason RepositionReason(
            float distance, bool visible, float blockedLaneSeconds,
            int roundsWithoutEffect = 0, bool strafeDue = false)
        {
            if (distance < 3.5f) return BlacksiteRepositionReason.TooClose;
            if (distance > 14f) return BlacksiteRepositionReason.OutOfRange;
            if (!visible) return BlacksiteRepositionReason.LostSight;
            if (blockedLaneSeconds >= BlockedLaneGraceSeconds) return BlacksiteRepositionReason.BlockedLane;
            // Last in the ladder on purpose: every more specific cause of a miss - out of the band,
            // out of sight, a squadmate in the lane - names itself first and carries its own remedy.
            // What is left is the case run 2 had no answer for: 3.5 < d <= 14, visible, lane clear,
            // and 70 rounds into a rock because the only line to the contact this member has does
            // not reach the part of it he is aiming at. The remedy is a different vantage.
            if (roundsWithoutEffect >= NoEffectRounds) return BlacksiteRepositionReason.NoEffect;
            // #1219 F2: below every remedy. A seen contact in the band with nothing wrong is where
            // round 2's riflemen stood for whole fights (members 4 and 5, reason=None at 12.9 m); a
            // due cadence moves him one lateral step inside the same band.
            if (strafeDue) return BlacksiteRepositionReason.Strafe;
            return BlacksiteRepositionReason.None;
        }

        // #1219 F2: how long a rifleman in a stand-up fight holds one spot before the next lateral
        // step. One whole burst cycle - 2 x BurstCadenceSeconds 0.12 + BurstRecoverySeconds 0.8 =
        // 1.04 s - plus the 0.14 s combat decision cadence it was sized against is 1.18 s, rounded
        // to 1.2 s.
        // This clock runs independently of the weapon phase, including an active burst.
        internal const float StrafeIntervalSeconds = 1.2f;

        // A combat step owns a reach contract, just like a melee approach. Its agent stop plus
        // navigation settle slack stays inside the controller's arrival gate; the default travel
        // stop (1.1 m) cannot complete this short step's existing 0.8 m gate.
        internal const float RepositionStopDistance = .2f;
        internal const float RepositionArrivalRadius = .8f;

        // #1219 F2: the agent speed while a burst is actually firing, and only then. Nonzero, so a
        // committed step keeps going through the burst instead of freezing, and read as a walking
        // shot (above MovingShotSpeedThreshold 0.5 m/s, so the moving alignment band applies). It
        // sits above CrouchEnterSpeedThreshold 1.2 m/s so the slow-down cannot drop the member into
        // a crouch mid-burst, and below CrouchExitSpeedThreshold 2 m/s so it cannot stand a crouched
        // one up either.
        internal const float FiringWalkSpeed = 1.4f;

        /// <summary>
        /// #1064 WP3: <paramref name="moving"/> defaults to false, so every existing caller and
        /// every existing pin reads the unchanged standing band. A walking shooter adds
        /// <see cref="MovingAimToleranceBonusDegrees"/> to both tolerances and nothing else.
        /// </summary>
        internal static bool CanReleaseShot(bool eligible, bool lineClear, bool friendlyBlocked,
            float bodyAngle, float rifleAngle, bool moving = false)
        {
            float bonus = moving ? MovingAimToleranceBonusDegrees : 0f;
            return eligible && lineClear && !friendlyBlocked
                && !float.IsNaN(bodyAngle) && !float.IsNaN(rifleAngle)
                && Math.Abs(bodyAngle) <= BodyAimToleranceDegrees + bonus
                && Math.Abs(rifleAngle) <= RifleAimToleranceDegrees + bonus;
        }

        internal static bool CanReleaseScheduledShot(float now, float due, bool firstRound, bool animationEvent)
        {
            return animationEvent ? firstRound && now >= due - .025f : now >= due + (firstRound ? .12f : 0f);
        }

        // #874 Q1: the hand-off distance is BlacksiteArrivalPolicy.EntranceHandoffDistance, the one
        // arrival table, which proves the stop distance, the widest claim and the arrival margin fit
        // inside it.
        // Unchanged: one storey of the moon's authored entrance geometry.
        internal const float EntranceHandoffHeight = 2.5f;

        internal static bool CanBeginEntrance(float horizontalDistance, float verticalDistance)
        {
            return horizontalDistance <= BlacksiteArrivalPolicy.EntranceHandoffDistance
                && Math.Abs(verticalDistance) <= EntranceHandoffHeight;
        }

        // BlacksiteEngagementGeometry owns the measured reach and translated body-profile rows.
        // BlacksiteMeleeReachChecks exercises the actual bundled weapon and production sweep;
        // its frame-rate/phase intersection bounds the gate, not one convenient animation sample.
        // The gate clears both measured edges by BatonReachMargin. The full inward/outward settle
        // band clears the gate by that margin again. These forwarders keep every humanoid consumer
        // on the same derivation as the profile-aware close-in, commit, abort and confirmed hit.
        internal const float BatonReachMargin = BlacksiteEngagementGeometry.ReachMargin;
        internal const float BatonAttackMinRange = BlacksiteEngagementGeometry.HumanoidMinReach;
        internal const float BatonAttackRange = BlacksiteEngagementGeometry.HumanoidMaxReach;
        // #1305 H (C4): the reach a protected carrier's contact must leave before his Combat hands
        // the decision back to his job - the gate plus the stop-distance band, ArrivalTolerance
        // (ApproachStop + SettleSlack, .35 m: the worst a settled body sits from the point it was
        // issued). Entry stays at the gate (BlacksiteSoldierAI.ThreatProximity).
        internal const float CarrierHoldReach = BatonAttackRange + BlacksiteEngagementGeometry.ArrivalTolerance;
        internal const float BatonAttackVerticalRange = BlacksiteEngagementGeometry.VerticalTolerance;
        // At the 1.25 attack clock the earliest hit window opens .640 s after the swing starts.
        // Turning at 300 deg/s squares a 35-degree windup with the contact before that window.
        // Off-axis native columns describe a contact side-stepping during the windup.
        internal const float BatonFacingDegrees = BlacksiteEngagementGeometry.FacingDegrees;
        // Lane clearance, not the physical minimum. The physical minimum measured in S2.2 is .78 m:
        // the player CharacterController radius .4 plus the solid soldier body capsule radius .38
        // (BlacksiteBodyCollision). This .95 is what the lane spacing must clear, giving .17 m over
        // two bodies actually touching; the stand-off now clears it by far more than that.
        internal const float BatonBodySeparation = BlacksiteEngagementGeometry.BodySeparation;
        internal const float BatonStandOff = BlacksiteEngagementGeometry.HumanoidStandOff;
        // Shrunk from .25 m by the S2.1b measurement, not by preference: the envelope was .75 m
        // wide then and .40 m of that is the four margins, so the stop plus the shared slack could
        // not exceed .35 m and the shared slack is .2 m. The margins were not touched. .15 m is
        // also the floor BlacksiteApproachProfile clamps every stop distance to, so this is as
        // small as the shared approach contract can be asked to hold. Remeasuring the weapon
        // changes the gate and stand-off together, not this movement tolerance.
        internal const float BatonApproachStop = BlacksiteEngagementGeometry.ApproachStop;
        // Must equal BlacksiteApproachProfile.SettleSlack; the native fixture asserts they agree.
        internal const float BatonSettleSlack = BlacksiteEngagementGeometry.SettleSlack;
        internal const float BatonLaneSpacingDegrees = 60f;
        // #1010 A2: the three geometries above all describe a 2.5 m humanoid standing on the
        // soldier's own NavMesh, because that is the only shape BlacksiteMeleeReachChecks has ever
        // measured. The constants below name the two shapes the 2026-09-12 run produced that it
        // does not cover, and the bound on holding a contact neither of them can be reached at.
        //
        // A contact whose collider top is below this height above the soldier's own FEET is a body
        // the authored arc passes over: TickBatonSweep lays its capsules along the animated hand,
        // which S21B_SHAFT measures between waist and shoulder height, so nothing it sweeps reaches
        // the floor plane at all. .7 m is the height the reach fixture's own crouched capsule
        // (1.5 m tall, centre .7 m) has its CENTRE at - the lowest point the existing measurement
        // proves the arc can still strike a body at - so a contact whose whole collider is below it
        // is outside everything that has been measured and owes the extra downward capsule.
        internal const float BatonLowContactHeight = .7f;
        // The body-height step this policy treats as a storey change: above the .3 m floor steps the
        // reach fixture sweeps and below the 1.65 m head a clung flea rides at, and the same step
        // GetCombatAimPoint and the threat samplers assume when they use root + 1.1 as "the chest of
        // a contact on this floor".
        // It no longer decides the lane floor. ResolveBatonLaneFloor used it as an early-out on the
        // contact's height above the soldier's FEET, which answers a different question: a contact
        // standing on a 2.5 m ledge is 0.93 m above a soldier 1.57 m up the ramp to it and so read
        // as "on his floor", and the lane ring went 0.93 m under the ledge, where no lane can be
        // sampled. That floor is now resolved geometrically - same plane, ground under the contact,
        // or the carrier's feet - so this constant is a pinned relation and nothing else.
        internal const float BatonOverheadContactHeight = 1f;
        // The invariants are exact to half a millimetre; float arithmetic on the derived constants
        // lands on their boundaries rather than inside them.
        private const float ReachTolerance = .0005f;
        internal static float BatonSettledRange(float standOff, float stop)
            => standOff + stop + BlacksiteEngagementGeometry.SettleSlack;
        // #1019 S5: the INWARD arm is the addition, and it is the half the shipped invariant
        // never had. The third clause budgets the settle OUTWARD from the ring; nothing budgeted
        // it inward, which is how a stand-off of 1.72 could sit legally above a gate whose inner
        // wall was 1.62 while the agent settled at 1.37. Both walls are now spent exactly:
        //     1.72 - 0.35 = 1.37 >= 1.27 + 0.10 = 1.37   and   2.07 <= 2.17 - 0.10 = 2.07.
        internal static bool BatonSettlesInsideGate(float standOff, float stop)
            => standOff >= BatonBodySeparation
               && standOff >= BatonAttackMinRange + BatonReachMargin - ReachTolerance
               && standOff - BlacksiteEngagementGeometry.ArrivalTolerance
                  >= BatonAttackMinRange + BatonReachMargin - ReachTolerance
               && BatonSettledRange(standOff, stop) <= BatonAttackRange - BatonReachMargin + ReachTolerance;
        internal static bool BatonInReach(float range, float verticalGap)
            => BlacksiteEngagementGeometry.InReach(BlacksiteBodyProfile.Humanoid, range, verticalGap);
        // Inside the arc, not out of it, and since S2.1r that is a spacing rule rather than a
        // geometry one: the swept shaft does reach a contact this close, but the swing lands with
        // the hand already through it. So this stays a reason to step back out to the stand-off and
        // strike from where the weapon reads, never a reason to swing from contact range.
        internal static bool BatonInsideMinimumReach(float range, float verticalGap)
            => BlacksiteEngagementGeometry.InsideMinimumReach(BlacksiteBodyProfile.Humanoid, range, verticalGap);
        internal static bool BatonCanAttack(float range, float verticalGap, float facingAngle)
            => BlacksiteEngagementGeometry.CanAttack(BlacksiteBodyProfile.Humanoid, range, verticalGap, facingAngle);
        // Where on the contact's own circle a close-in lane is placed. Normally the stand-off. For a
        // contact that has walked inside the minimum reach the back-out would be shorter than the
        // agent's own arrival tolerance - a destination it already considers reached, which is the
        // v11 stall - so the lane is pushed out far enough to be a step the agent can execute, and
        // capped at the far edge of the settle band so it stays inside the gate.
        internal static float BatonLaneRadius(float range)
            => BlacksiteEngagementGeometry.LaneRadius(BlacksiteBodyProfile.Humanoid, range);
        // Distance between two lanes that far apart on the stand-off circle. Attackers converging
        // on one target must not be sent to points they cannot both physically stand on.
        internal static float LaneSeparation(float degrees)
            => (float)(2d * BatonStandOff * Math.Sin(Math.Abs(degrees) * Math.PI / 360d));

        // #874 S10.1: the predictive commit. Everything above is unchanged - the reach envelope is
        // spent exactly and nothing here widens it. What changes is WHEN the reach test is applied:
        // the strike is tested ActiveFrom(swing) seconds after the commit, so testing the range at
        // the commit tests a geometry the swing will never meet. The gate below is the same
        // BatonInReach annulus, applied to the range the swing will actually be taken at.
        //
        // The lead is the authored hit window's own opening frame, not a chosen number: 27, 24 and
        // 29 clip frames at 30 fps played at BlacksiteBatonPolicy.BatonAnimatorSpeed 1.25 (#1219 F2),
        // that is 0.720, 0.640 and 0.773 s.
        internal static float ImpactLeadSeconds(int swing) => BlacksiteBatonPolicy.ActiveFrom(swing);
        // Conservative per-swing bands measured by BlacksiteMeleeReachChecks across frame grids.
        // The narrower reach gate decides new commits; these bands decide whether an in-flight
        // swing can still land. Both also respect the confirmed-hit root guard.
        internal static float BatonConnectInner(int swing)
            => BlacksiteEngagementGeometry.ConnectInner(BlacksiteBodyProfile.Humanoid, swing);
        internal static float BatonConnectOuter(int swing)
            => BlacksiteEngagementGeometry.ConnectOuter(BlacksiteBodyProfile.Humanoid, swing);
        // The commit test. Same bounds, same cone, applied to the predicted range at impact. Nothing
        // widens: TickBatonSweep still decides the hit, so a mispredicted swing whiffs as today.
        internal static bool BatonWillConnect(float predictedRange, float verticalGap, float facingAngle)
            => BatonInReach(predictedRange, verticalGap) && facingAngle < BatonFacingDegrees;
        // SwingAbortLeadSeconds = 0.1: three frames of a 30 fps server, the frame the sweep's
        // MaxShaftSweepSteps treats as one step of head travel (about 0.6 m). With the hit window
        // (4 clip frames at BatonAnimatorSpeed 1.25 = 0.107 s, #1219 F2) the abort window is
        // 0.207 s, which the per-frame swing continuation samples several times.
        internal const float SwingAbortLeadSeconds = .1f;
        // The margin the reach envelope already spends at each wall. A swing whose range is inside
        // the measured band plus this is not aborted, because that is the same tolerance every other
        // melee wall is measured with.
        internal const float SwingAbortMargin = BatonReachMargin;
        /// <summary>
        /// True inside the window where a committed swing can still be called off: from one abort
        /// lead before the hit window opens until it closes. After it closes the strike is decided
        /// and the actor owes the S2.3 recovery frame instead.
        /// </summary>
        internal static bool SwingAbortWindow(int swing, float elapsedSeconds)
            => elapsedSeconds >= ImpactLeadSeconds(swing) - SwingAbortLeadSeconds
               && elapsedSeconds < BlacksiteBatonPolicy.ActiveUntil(swing);
        /// <summary>
        /// A swing that will land outside the band the weapon actually connects over is a whiff the
        /// actor can still refuse. <paramref name="predictedRange"/> is the range at impact, so an
        /// actor that is still closing is not called off for a range it is about to leave.
        /// </summary>
        /// #988 B2 note: this rule is DELIBERATELY unchanged. An exemption for swings committed at
        /// close quarters was written and withdrawn: skipping the inner wall for them let a
        /// committed swing survive a contact sprinting out of reach, and
        /// BlacksiteMeleeApproachChecks measured the 6.6 m/s sprint break moving from 3.34 m to
        /// 2.41 m - the homing attack S10.1 exists to refuse. Both walls stand for every swing; a
        /// close-quarters swing is called off by a contact that leaves the band in either
        /// direction, exactly like any other.
        internal static bool ShouldAbortSwing(int swing, float elapsedSeconds, float predictedRange)
            => ShouldAbortSwingFor(BlacksiteBodyProfile.Humanoid, swing, elapsedSeconds, predictedRange);

        /// <summary>
        /// #1063 WP2: the same rule against the band of the contact's OWN profile. It is a
        /// separately NAMED method rather than an overload deliberately: the external
        /// BlacksiteLandingCombatChecks fixture binds several of this type's melee members through
        /// <c>AccessTools.Method(policyType, name)</c> with no argument types, and a second method
        /// of the same name would make that lookup ambiguous at run time.
        ///
        /// The forwarder above keeps the Humanoid row for every existing caller and every pin.
        /// </summary>
        internal static bool ShouldAbortSwingFor(BlacksiteBodyProfile profile, int swing,
            float elapsedSeconds, float predictedRange)
            => SwingAbortWindow(swing, elapsedSeconds)
               && (predictedRange < BlacksiteEngagementGeometry.ConnectInner(profile, swing) - SwingAbortMargin
                   || predictedRange > BlacksiteEngagementGeometry.ConnectOuter(profile, swing) + SwingAbortMargin);

        /// <summary>
        /// #988 B2: the hole the reach gate leaves at its inner wall. The gate is
        /// [BatonAttackMinRange, BatonAttackRange] and it is NARROWER than the weapon: the measured
        /// connect band of swings 1 and 2 starts at 0.73 m (BatonConnectInner), nearly a metre
        /// inside the 1.62 m the gate refuses at. So a contact standing on the actor, or closing
        /// into it fast enough that the predicted range at impact lands under the gate, produced
        /// `holding the gate: predicted=0.00` five times in the 2026-09-12 run (L11507, L11704,
        /// L11738, L11829, L11875) while a Thumper stood at 1.66 to 1.77 m and bit.
        ///
        /// Nothing here widens the envelope. The band is the RECORD of the S2.1r sweep, the same
        /// one ShouldAbortSwing has always been measured against, and TickBatonSweep still decides
        /// every hit, so a swing taken here whiffs exactly as one taken through the ordinary gate
        /// does. What changes is only that the actor takes it: swing 0 is skipped because its own
        /// band starts at 0.98 m, and the choice between 1 and 2 keeps the actor's own cycle.
        /// </summary>
        internal static bool BatonCloseQuartersSwing(int nextSwing, float range,
            float verticalGap, float facingAngle, out int swing)
            => BatonCloseQuartersSwingFor(BlacksiteBodyProfile.Humanoid, nextSwing, range,
                verticalGap, facingAngle, out swing);

        /// <summary>
        /// #1063 WP2: the same rule against the gate and the band of the contact's OWN profile,
        /// under a separate NAME for the same reason <see cref="ShouldAbortSwingFor"/> has one -
        /// BlacksiteLandingCombatChecks binds <c>BatonCloseQuartersSwing</c> by name with no
        /// argument types (MeleeApproachChecks.cs:162) and an overload would make that ambiguous.
        /// </summary>
        internal static bool BatonCloseQuartersSwingFor(BlacksiteBodyProfile profile, int nextSwing,
            float range, float verticalGap, float facingAngle, out int swing)
        {
            swing = nextSwing % 3 == 2 ? 2 : 1;
            if (profile == BlacksiteBodyProfile.Hazard) return false;
            // #1063 WP2: the vertical half is one-sided now, so a contact BELOW the member is not
            // refused here either - it is the shape this branch exists for.
            if (!BlacksiteEngagementGeometry.WithinVerticalReach(verticalGap)
                || facingAngle >= BatonFacingDegrees) return false;
            // Exactly one shape, and it is the INSTANTANEOUS range: the contact is inside the
            // minimum reach now. The prediction is deliberately not consulted. A predicted-into-the-
            // gate arm was tried and withdrawn: BlacksiteContactVelocity is a RELATIVE estimator, so
            // an actor sprinting at a contact that is strafing reads the same "predicted inside the
            // minimum reach" as a contact walking onto a standing actor, and committing there turned
            // BlacksiteMeleeApproachChecks' 6.6 m/s sprint case from a clean break into a connect -
            // the homing attack S10.1 exists to refuse. Inside the minimum reach there is no such
            // ambiguity: the contact is on the actor whatever either of them is doing, and a
            // contact closing through the gate reaches this branch on the next think anyway, which
            // is what ends the `holding the gate` loop - the old answer BACKED OUT to the stand-off
            // and arrived back in the gate, so the member could cycle there indefinitely.
            if (range >= BlacksiteEngagementGeometry.MinReach(profile)) return false;
            return range >= BlacksiteEngagementGeometry.ConnectInner(profile, swing) - SwingAbortMargin
                && range <= BlacksiteEngagementGeometry.ConnectOuter(profile, swing) + SwingAbortMargin;
        }

        // #874 S10.4: where the mission layer's cover ring places a helper, so the ring and the
        // melee lane are one placement rather than two. A melee slot sits on the stand-off circle
        // itself: BatonSettledRange(BatonStandOff, BatonApproachStop) is 2.07 m, exactly
        // BatonAttackRange - BatonReachMargin, so the stand-off is the ONLY radius whose worst
        // settle stays inside the gate and there is no room for a second, wider melee ring. The
        // twelve slots therefore differ by bearing alone, which is what the lane claims arbitrate
        // anyway. The rifle radii are unchanged: 6 m, with the outer six slots 3 m further out.
        internal const float RifleCoverRadius = 6f;
        internal const float RifleCoverRingStep = 3f;
        internal const int CoverRingSlots = 12;
        internal static float CoverRingRadius(bool melee, int step)
            => melee ? BatonStandOff
                : step < CoverRingSlots / 2 ? RifleCoverRadius : RifleCoverRadius + RifleCoverRingStep;

        // #874 S11 U2 (W1): the target commitment, as a rule rather than as two statements either
        // side of an early return. Both values are unchanged and neither is authored here: the
        // commitment itself is BlacksiteCombatController.TargetStickSeconds (3 s) and arrives as an
        // argument, and the swap margin is the +80 the controller has always compared against,
        // named here because it is half of the same rule. What moved is only WHEN the deadline is
        // written: SelectTarget wrote it before its `if (same) return;`, so re-selecting the same
        // contact on every 0.14 s think re-armed the commitment for as long as the contact was
        // held and the margin never lapsed. A commitment is to a CHOICE, so only a change of
        // choice may arm one.
        internal const int TargetSwapMargin = 80;
        internal static float NextTargetStickyDeadline(
            bool sameTarget, float now, float currentDeadline, float stickSeconds)
            => sameTarget ? currentDeadline : now + stickSeconds;
        internal static bool RefusesTargetSwap(
            bool sameTarget, bool hasValidTarget, bool forcePriority,
            float now, float stickyDeadline, int incomingPriority, int currentPriority)
            => !sameTarget
               && hasValidTarget
               && !forcePriority
               && now < stickyDeadline
               && incomingPriority < currentPriority + TargetSwapMargin;

        // #1293 R1: moved unchanged from the deleted awareness store; both target selections read it.
        internal const float MonsterPriorityRange = 6f;

        // #874 S11 U2 (I6): the peek-cover score, lifted verbatim out of
        // BlacksiteCombatController.TryFindBoundedCombatPosition's candidate scoring so the mission
        // cover ring and the combat reposition search score cover with ONE implementation. Every
        // number is the one that search has always used; none is new and none is tuned here. The
        // ring previously scored a slot with a single sightline linecast and a +12 penalty for a
        // blocked one, which is a firing-lane test: it preferred standing in the open.
        internal const float CoverBodyOccludedBonus = 2.5f;
        internal const float CoverPeekClearBonus = 3.5f;
        internal const float CoverPeekBlockedPenalty = 4f;
        // Half a Lethal Company player capsule (0.4 m radius) plus a shoulder: the lateral step a
        // body takes to look past the cover it is standing behind. Unchanged from the search.
        internal const float CoverPeekOffset = .72f;
        // The height the search samples a candidate's own body at, unchanged from the search.
        internal const float CoverBodySampleHeight = 1.25f;
        internal static float PeekCoverScore(bool bodyOccluded, bool peekClear)
            => (bodyOccluded ? CoverBodyOccludedBonus : 0f)
               + (peekClear ? CoverPeekClearBonus : -CoverPeekBlockedPenalty);

        // #1262 P4: the one band every class the squad cannot stop is evaded inside; a Gaze row (the
        // coil-head) is watched in its own row's band and the cold sapsucker keeps its nest footprint.
        // 30 m is a full Jester crank's walk at the 3.6 m/s role speed.
        internal const float MonsterReactionRange = 30f;
        internal const float MonsterReactionConeDegrees = 100f;

        // #1086 (verifier finding 1): "back off toward the entrance" is only a retreat while the
        // entrance is not on the far side of the Jester. A goal inside this cone of the direction
        // to the contact, and FURTHER AWAY than the contact is, is walked to THROUGH it - the
        // member closes instead of opening, and the stand-off band can never arm because the
        // distance is falling. 60 degrees is the same wide forward arc the reaction cone uses,
        // halved: outside it the walk has a real sideways component and passes the contact rather
        // than at it.
        internal const float JesterBlockedGoalConeDegrees = 60f;

        /// <summary>
        /// The rule above, as arithmetic, so it is provable without a scene: a retreat goal is
        /// refused when it lies inside the cone AND the contact is nearer than the goal. A goal
        /// nearer than the contact is kept however narrow the angle - walking two metres to a door
        /// the contact is standing beyond is still moving away from nothing, but it is also not
        /// walking past it.
        /// </summary>
        internal static bool RetreatGoalBlockedByContact(float goalDistance, float contactDistance, float angleDegrees)
            => angleDegrees < JesterBlockedGoalConeDegrees && contactDistance < goalDistance;

        /// <summary>
        /// #1209: the damage the victim's own client applies for one soldier hit the host has
        /// already resolved. Only a gunshot passes through the victim's Bullet Resistance, once,
        /// and the answer is held to [0, <paramref name="raw"/>]: resistance never adds damage and
        /// a negative answer must not heal. The host keeps choosing the hit and the raw damage; the
        /// rank read belongs to whoever this client's player is, so the caller runs this only after
        /// the hit is established as new and owned. A null <paramref name="incomingFirearmResistance"/>
        /// (no Better Armory, an older one, or no hosting bridge) leaves the hit as it was.
        /// </summary>
        internal static int VictimOwnedPlayerDamage(int raw, bool gunshot, Func<int, int> incomingFirearmResistance)
        {
            if (raw <= 0 || !gunshot || incomingFirearmResistance == null)
                return raw;

            int resolved = incomingFirearmResistance(raw);
            return resolved < 0 ? 0 : resolved > raw ? raw : resolved;
        }
    }
}


