namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1019 S5: what the contact is DOING. Nine values, closed and total, every one with a named
    /// live producer (design note C.4, as ruled in H.check).
    ///
    /// There is no <c>Airborne</c>: nothing in the tree reads a flight flag at 7d5f3107, so the
    /// Flyer profile is decided by the MEASURED ground gap alone and the state set carries no row
    /// nothing can produce. <c>inSpecialAnimation</c> is folded into <see cref="Stunned"/>: both
    /// mean "the native motor owns this body and it is not attacking", which is one decision for
    /// the soldier (BlacksiteVanillaMonsters.Pursuit.cs:82 and :130 read them together in exactly
    /// that sense). <see cref="Hostile"/> is the addition, and it is the reason the table is a
    /// table: the butler's <c>currentBehaviourStateIndex == 2</c> and the sapsucker's hot latch are
    /// the only two facts in the tree that flip weapon exclusion MID-ROUND, so a class row without
    /// them would lie for half the round.
    /// </summary>
    internal enum BlacksiteThreatState : byte
    {
        Idle = 0,
        Searching = 1,
        Chasing = 2,
        Hostile = 3,
        CeilingHeld = 4,
        AttachedToAlly = 5,
        AttachedToPlayer = 6,
        Stunned = 7,
        Dead = 8
    }

    /// <summary>#1019 S5: which job the member is doing when it asks.</summary>
    internal enum BlacksiteEngagementRole : byte { Melee = 0, Rifle = 1, Carrier = 2, Guard = 3 }

    /// <summary>
    /// #1019 S5: what the member is to DO about this contact. Six values, and exactly one of them
    /// (<see cref="Ignore"/> through rows 1 and 6) can end an engagement - there is no silent
    /// fall-through, because the last adapter matches everything and the last row is total.
    /// #1233 S6 (rule 5): <c>HoldPosition</c> is deleted with the blocked-approach counter that was
    /// its only producer. A body the weapon cannot reach this think is the weapon's own
    /// OutOfBand verdict, which yields the think; it is not a scored stance.
    /// </summary>
    internal enum BlacksiteThreatIntent : byte
    {
        Ignore = 0,
        Engage = 1,
        Rescue = 2,
        Evade = 3,
        Withdraw = 4,
        // #1019 S5, appended: the coil-head's squad-elected watcher must keep watching it. #1271 S3a:
        // it is no behaviour - the watcher faces its Coil-Head (WatchFaces) while its job and
        // Combat keep running.
        Watch = 5
    }

    /// <summary>
    /// #1019 S5: WHICH point the Unity side resolves to aim and strike at. Expressed as a choice
    /// rather than as a Vector3 so this whole file stays Unity-free: the geometry query belongs
    /// where the colliders are and the decision belongs here.
    /// </summary>
    internal enum BlacksiteAimAnchor : byte
    {
        /// <summary>Nothing is aimed at: Ignore, Evade and Withdraw.</summary>
        None = 0,
        /// <summary>
        /// The contact's own measured body - BlacksiteSoldierAI.MeasureBatonContact's bounds
        /// centre with Y clamped into the span (Equipment.cs:657-665).
        /// </summary>
        ContactBody = 1,
        /// <summary>
        /// #1011 A3: a captor that RIDES its victim is struck where it is riding -
        /// BlacksiteVanillaMonsters.TryGetCaptureAnchor (Attacks.cs:294-298), which answers false
        /// for a captor whose own body is already the right thing to aim at, so the Unity side
        /// falls back to <see cref="ContactBody"/> exactly as BlacksiteCombatController.cs:1539-1541
        /// does today.
        /// </summary>
        CaptureAnchor = 2
    }

    /// <summary>
    /// #1019 S5: what the contact IS. A value type, built once per contact by the adapter that
    /// matched it, never re-derived inside a decision.
    ///
    /// <see cref="WeaponsExcluded"/> is the whole of <c>BlacksiteThreatAdapterRegistry.WeaponsExcludedFor</c>
    /// as a CLASS FACT, including its two stateful arms, which is what
    /// <see cref="BlacksiteThreatState.Hostile"/> carries. This struct never asks Unity anything.
    /// </summary>
    internal readonly struct BlacksiteThreatClass
    {
        internal readonly BlacksiteBodyProfile Profile;
        /// <summary>The measured body radius, or the profile default when the contact has no
        /// non-trigger collider at all.</summary>
        internal readonly float Radius;
        /// <summary>The measured collider height, or the profile default.</summary>
        internal readonly float Height;
        /// <summary>The ring plane the adapter resolved, in world Y.</summary>
        internal readonly float GroundedY;
        /// <summary>BlacksiteThreatAdapterRegistry.WeaponsExcludedFor, verbatim.</summary>
        internal readonly bool WeaponsExcluded;
        /// <summary>The band a member reacts inside (default BlacksiteCombatPolicy.MonsterReactionRange).</summary>
        internal readonly float ReactionRange;
        /// <summary>The cone that goes with <see cref="ReactionRange"/>.</summary>
        internal readonly float ReactionConeDegrees;
        /// <summary>A Gaze row (the coil-head): its squad-elected watcher faces it instead of evading it.</summary>
        internal readonly bool ReactionIsWatch;
        internal readonly BlacksiteMonsterSupportLevel Support;
        /// <summary>
        /// #874 WP F: the squad's weapons cannot hurt this class, so the squad as a whole never
        /// ENGAGES it - no draw, no close-in, no swing - while it stays SEEN, reported and withdrawn
        /// from. That is the whole difference from <see cref="WeaponsExcluded"/>, which also blinds
        /// the sight gate (BlacksiteSightPolicy.BlindedByWeaponExclusion). The Old Bird is the
        /// class: its HitEnemy is a no-op. #874 N8 (decision 7): a rifle member with warning fire
        /// left (<see cref="BlacksiteSquadContext.WarningFireOpen"/>) is the one exception, and the
        /// class's reaction range makes everybody else evade it on sight.
        /// BlacksiteThreatAdapterRegistry.EngagementRefusedFor.
        /// </summary>
        internal readonly bool EngagementRefused;
        /// <summary>#1262 P4: <c>enemyType.canBeStunned</c>. A stun is the only way a baton ENDS an
        /// attack - TickCaptures releases a capture on <c>stunNormalizedTimer &gt; 0</c>.</summary>
        internal readonly bool Stunnable;

        internal BlacksiteThreatClass(
            BlacksiteBodyProfile profile,
            float radius,
            float height,
            float groundedY,
            bool weaponsExcluded,
            float reactionRange,
            float reactionConeDegrees,
            bool reactionIsWatch,
            BlacksiteMonsterSupportLevel support,
            bool engagementRefused = false,
            bool stunnable = true)
        {
            Profile = profile;
            Radius = radius;
            Height = height;
            GroundedY = groundedY;
            WeaponsExcluded = weaponsExcluded;
            ReactionRange = reactionRange;
            ReactionConeDegrees = reactionConeDegrees;
            ReactionIsWatch = reactionIsWatch;
            Support = support;
            EngagementRefused = engagementRefused;
            Stunnable = stunnable;
        }

        /// <summary>
        /// #874 WP F: the squad may fight this contact at all - neither weapons-excluded nor a class
        /// its weapons cannot hurt. BlacksiteThreatAdapterRegistry.IsAttackable is the same fact
        /// asked of a live enemy. #874 N8: a refused class may still take a rifle's warning fire;
        /// that is a member fact, carried by the context, not by the class.
        /// </summary>
        internal bool Attackable => !WeaponsExcluded && !EngagementRefused;

        /// <summary>
        /// #948 S12 U3 / #1019 S5: "an adapter may drive it". Unsupported is NOT Ignore - an
        /// unknown or modded enemy is engaged with its measured envelope (design note C.7).
        /// </summary>
        internal bool IsKnown => Support != BlacksiteMonsterSupportLevel.Unsupported;
    }

    /// <summary>
    /// #1019 S5: everything the decision needs about the SQUAD rather than about the contact.
    /// Scalars, bools and enums only - no Unity type reaches this file - and every field is filled
    /// by the caller from the site that already computes it, on that site's own clock.
    /// </summary>
    internal readonly struct BlacksiteSquadContext
    {
        /// <summary>
        /// #1011 A3: a living squadmate is held by SOME monster inside
        /// BlacksiteCombatPolicy.RescueRadius and, since #874 Plan F, THIS member is its committed
        /// rescuer - BlacksiteVanillaMonsters.TryGetRescueAssignment through
        /// BlacksiteSoldierAI.TryGetRescueContact.
        /// </summary>
        internal readonly bool AllyCaptured;
        /// <summary>...and THIS contact is the monster holding it.</summary>
        internal readonly bool ContactIsCaptor;
        /// <summary>
        /// This member is the Gaze row's squad-elected watcher (#1305 E4:
        /// BlacksiteVanillaMonsters.ElectWatchers) - BlacksiteVanillaMonsters.SoldierReaction's watch rule.
        /// </summary>
        internal readonly bool IsObserverOfContact;
        /// <summary>
        /// The caller's own <c>SoldierSees(soldier, enemy, class.ReactionRange,
        /// class.ReactionConeDegrees)</c> verdict (vision is Unity's, so it is a field), OR'd with
        /// <see cref="IsObserverOfContact"/> (#1305 E4) so an elected watcher not yet facing it turns.
        ///
        /// CADENCE: it is filled only by the monster reaction's own sighting
        /// (<c>BlacksiteVanillaMonsters.SoldierReaction</c>) and NEVER per frame - vision is the
        /// one thing in this struct that costs a linecast. Every other caller passes false, which
        /// makes rows 4 and 5 unreachable for it, which is correct: only the monster reaction
        /// has ever produced an evade or a watch.
        /// </summary>
        internal readonly bool ContactInReactionRange;
        /// <summary>The member is withdrawing (BlacksiteBehaviour.Withdrawal is committed).</summary>
        internal readonly bool Withdrawing;
        /// <summary>
        /// #874 N8 (decision 7): this member is a rifle that may still put a warning burst into
        /// THIS refused contact - BlacksiteThreatAdapterRegistry.WarningFireOpen, which is host
        /// state (the burst count and the squad's attack latch). Read only for a class whose
        /// <see cref="BlacksiteThreatClass.EngagementRefused"/> is set; false for every other
        /// caller, which leaves every other row exactly as it was.
        /// </summary>
        internal readonly bool WarningFireOpen;

        internal BlacksiteSquadContext(
            bool allyCaptured,
            bool contactIsCaptor,
            bool isObserverOfContact,
            bool contactInReactionRange,
            bool withdrawing,
            bool warningFireOpen = false)
        {
            AllyCaptured = allyCaptured;
            ContactIsCaptor = contactIsCaptor;
            IsObserverOfContact = isObserverOfContact;
            ContactInReactionRange = contactInReactionRange;
            Withdrawing = withdrawing;
            WarningFireOpen = warningFireOpen;
        }
    }

    /// <summary>
    /// #1019 S5: where the member is sent to stand, in the geometry's own terms. Radius 0 with
    /// plane <see cref="BlacksiteRingPlane.None"/> means there is no ring: the member does not
    /// approach this contact at all.
    /// </summary>
    internal readonly struct BlacksiteApproachRing
    {
        internal readonly float Radius;
        internal readonly BlacksiteRingPlane Plane;
        internal readonly float Stop;

        internal BlacksiteApproachRing(float radius, BlacksiteRingPlane plane, float stop)
        {
            Radius = radius;
            Plane = plane;
            Stop = stop;
        }

        internal bool HasRing => Plane != BlacksiteRingPlane.None && Radius > 0f;
        internal static BlacksiteApproachRing NoRing => new BlacksiteApproachRing(0f, BlacksiteRingPlane.None, 0f);
    }

    /// <summary>
    /// #1019 S5: the gate a strike is tested against, for THIS contact's profile. All four numbers
    /// come from BlacksiteEngagementGeometry; none is authored here.
    /// </summary>
    internal readonly struct BlacksiteReachGate
    {
        internal readonly float Min;
        internal readonly float Max;
        internal readonly float VerticalTolerance;
        internal readonly float ConeDegrees;

        internal BlacksiteReachGate(float min, float max, float verticalTolerance, float coneDegrees)
        {
            Min = min;
            Max = max;
            VerticalTolerance = verticalTolerance;
            ConeDegrees = coneDegrees;
        }

        internal bool HasGate => Max > 0f;
        internal static BlacksiteReachGate NoGate => new BlacksiteReachGate(0f, 0f, 0f, 0f);
    }

    /// <summary>
    /// #1019 S5: the ONE answer. What to do, where to aim, where to stand and what to test a
    /// strike against. #1305 H (D5): the Combat score it used to carry is deleted with the table.
    /// </summary>
    internal readonly struct BlacksiteThreatResponse
    {
        internal readonly BlacksiteThreatIntent Intent;
        internal readonly BlacksiteAimAnchor AimAnchor;
        internal readonly BlacksiteApproachRing ApproachRing;
        internal readonly BlacksiteReachGate ReachGate;

        internal BlacksiteThreatResponse(
            BlacksiteThreatIntent intent,
            BlacksiteAimAnchor aimAnchor,
            BlacksiteApproachRing approachRing,
            BlacksiteReachGate reachGate)
        {
            Intent = intent;
            AimAnchor = aimAnchor;
            ApproachRing = approachRing;
            ReachGate = reachGate;
        }

        internal bool Engages => Intent == BlacksiteThreatIntent.Engage
                                 || Intent == BlacksiteThreatIntent.Rescue;
    }

    /// <summary>
    /// #1019 S5: the ONE decision about a contact.
    ///
    /// Pure, total, allocation-free and Unity-free. It has NO clock of its own: every cadence in
    /// the tree stays on the field that owns it today (design note F.3.4's seam), so a fixture
    /// that forces a clock open still gets exactly one decision per call.
    /// </summary>
    internal static class BlacksiteThreatResponsePolicy
    {
        /// <summary>#1262 P4 (D4, rule 4): a rifle stops anything it may shoot; a baton stops what it may
        /// hit AND can stun. The keeper (<c>canBeStunned</c> false) is a rifle target and a baton evades it.
        /// </summary>
        internal static bool WeaponStops(in BlacksiteThreatClass threatClass, BlacksiteEngagementRole role)
            => threatClass.Attackable && (role != BlacksiteEngagementRole.Melee || threatClass.Stunnable);

        /// <summary>
        /// #1019 S5: the table. First match wins; every (class, state, role, band, context)
        /// produces exactly one response, and <see cref="BlacksiteThreatIntent.Ignore"/> is reached
        /// only through an explicit row.
        ///
        /// PRECONDITION, and it is the caller's: this table does not re-test acquisition. The rest
        /// of <c>BlacksiteSoldierAI.CanConsiderEnemy</c> (:4469-4479) - the
        /// <c>BlacksiteHazardAwareness.ExcludeFromRifleTargets</c> forwarder, the inside/outside
        /// test and the 6 m close-acquire test - has
        /// already run before a contact is offered here, exactly as it runs before
        /// <c>TryGetRescueContact</c> names a captor today. The Rescue row in
        /// particular assumes it: it asks only whether THIS contact is the captor of a held
        /// squadmate, because the captor was already filtered.
        /// The ship guard's 12 m leash is not part of that filter: it is only his post
        /// (<c>AllowsPlayerResponse</c> / <c>BlacksiteDeploymentPolicy.CanGuardPursue</c>, where he
        /// may walk to answer a player), never a target gate.
        /// </summary>
        internal static BlacksiteThreatResponse Decide(
            in BlacksiteThreatClass threatClass,
            BlacksiteThreatState threatState,
            BlacksiteEngagementRole role,
            BlacksiteThreatProximity proximityBand,
            in BlacksiteSquadContext squadContext)
        {
            // Row 1. A dead contact is not a contact. BlacksiteSoldierAI.CanConsiderEnemy (:4470)
            // and BlacksiteCombatController.IsValidEnemy (:1654) both refuse it today.
            if (threatState == BlacksiteThreatState.Dead)
                return Ignored();

            // Row 2 (the note's row 10), and it has to be HERE rather than last. It is NOT disjoint
            // from the rows below at band == None: a withdrawing member whose contact is stunned
            // twenty metres out, or whose blocked counter is up, or who is a coil-head's elected watcher
            // fifteen metres from a contact inside its own 22 m reaction range, would be handed a
            // fighting intent by one of those rows and never reach the withdrawal at all. A member
            // that is leaving with nothing in its threat band leaves. Behaviour.cs:104-105 and
            // BlacksiteCombatPolicy.WithdrawalContactClearRange (:157) are what produce it.
            if (squadContext.Withdrawing && proximityBand == BlacksiteThreatProximity.None)
                return Withdrawing();

            // Row 3. #1011 A3: the captor of a held squadmate is the preferred baton target. The
            // gate mirrors today's exactly - BlacksiteSoldierAI.TryGetRescueContact is UsesBaton +
            // TryGetRescueAssignment(RescueRadius), whose #874 Plan F election includes
            // CanConsiderEnemy(captor) and answers for the one committed rescuer only.
            // #1305 H (C2): the hazard warning-band input is deleted with the Hazard behaviour.
            // #1262 P4 (D4): the election refuses a captor the baton cannot stun
            // (BlacksiteThreatAdapterRegistry.IsAttackableBy), so ContactIsCaptor is never true for a keeper here.
            if (role == BlacksiteEngagementRole.Melee
                && squadContext.AllyCaptured
                && squadContext.ContactIsCaptor)
                return Rescuing(threatClass);

            // #1011 A3, and the row that is NOT here: a rifle whose contact is holding a squadmate.
            // It was written and removed. For a contact the squad may shoot it is exactly row 8
            // (Engage, same ring, same gate), and for one it may not - a Barber holding
            // a squadmate - it would have jumped the weapons gate below and ordered a rifleman onto
            // a target ExcludesWeapons refuses. The hold-fire that H.check ruling 2 protects lives
            // in the controller and is untouched: BlacksiteCombatController.cs:626-632,
            // IsAllyAttachedTarget :649-650, holdFire=ally-attached :414.

            // #874 N8 (decision 7): a rifle's WARNING FIRE at a class the weapons cannot hurt (the
            // Old Bird). While it is open the rifle is the one member that is not sent away by row 4
            // and not refused by row 6 - it falls through to the fighting rows like an attackable
            // contact. The moment the budget is spent or the squad is attacked it is false, and the
            // same contact is Evade inside its reaction range and Ignore outside it, as for a baton.
            // A weapons-excluded class is never reopened by it: exclusion outranks the warning.
            bool warningFire = role == BlacksiteEngagementRole.Rifle
                && squadContext.WarningFireOpen
                && threatClass.EngagementRefused
                && !threatClass.WeaponsExcluded;

            bool weaponStops = WeaponStops(in threatClass, role);
            // Row 4 (#1262 P4, D4 / rule 4): the DEFAULT. A class an authority names as a threat (IsKnown -
            // the witness, a manticoil or a modded unknown is not one) that this member's weapon cannot
            // stop is EVADED inside its reaction range, and faced while backing off (TickMonsterReaction).
            if (threatClass.IsKnown && !weaponStops && squadContext.ContactInReactionRange
                && !threatClass.ReactionIsWatch && !warningFire)
                return Evading();
            // Row 5. The squad-elected watcher of a Gaze row watches it; the rest ignore it.
            if (squadContext.ContactInReactionRange && threatClass.ReactionIsWatch && squadContext.IsObserverOfContact)
                return Watching();
            // Row 6. Outside the band, or a class no authority names: a contact the weapon cannot stop is
            // left alone. Above rows 7 and 8 so a stunned excluded class is never engaged.
            if (!weaponStops && !warningFire)
                return Ignored();

            // Row 7. A stunned contact is the easiest kill there is - and after row 6 it is by
            // construction a contact the squad is allowed to hit.
            if (threatState == BlacksiteThreatState.Stunned)
                return Engaging(threatClass);

            // Row 8, and it is total: everything left is a contact the squad may hit, INCLUDING
            // every class no authority in the tree names. An unknown or modded enemy is
            // Unsupported, not excluded, and it is engaged with its own measured envelope
            // (design note C.7) - the capability table's default row, unchanged.
            // #1233 S6 (rule 5): whether the member may ACT on that answer is decided before the
            // table is asked - BlacksiteSoldierAI.ContactEngageable admits only a contact with a
            // hittable body and evidence of threat - so an attackable class seen at a distance is
            // never described here at all.
            return Engaging(threatClass);
        }

        /// <summary>
        /// #874 P-S4: what a member does about a monster that has ACQUIRED it - taken its pursuit
        /// lease in <c>BlacksiteVanillaMonsters.Acquire</c> - whether or not the member's own
        /// perception has the contact. <see cref="Decide"/> is otherwise only ever asked about a
        /// contact the member has seen, so in the 2026-09-23 run a Forest Keeper acquired rifle
        /// guard member 5 at t=1859.70 and grabbed him at t=1865.02 while every combat line in
        /// between read NoTarget: nothing downstream of the acquisition asked the table at all.
        ///
        /// A contact that has chosen this member is inside its reaction band by definition, so the
        /// context is re-built with <see cref="BlacksiteSquadContext.ContactInReactionRange"/> true
        /// and the table decides from there. The one answer the table does not give: a class no
        /// authority names (<see cref="BlacksiteThreatClass.IsKnown"/> false) is EVADED rather than
        /// engaged by row 9 - an unclassified thing that is already hunting a member is not one to
        /// walk at. <paramref name="reason"/> is always a literal, printed as reason= on the
        /// acquire line, so an Ignore is never silent.
        /// </summary>
        internal static BlacksiteThreatResponse DecideAcquired(
            in BlacksiteThreatClass threatClass,
            BlacksiteThreatState threatState,
            BlacksiteEngagementRole role,
            BlacksiteThreatProximity proximityBand,
            in BlacksiteSquadContext squadContext,
            out string reason)
        {
            if (threatState != BlacksiteThreatState.Dead && !threatClass.IsKnown)
            {
                reason = "unclassified";
                return Evading();
            }
            var acquired = new BlacksiteSquadContext(
                squadContext.AllyCaptured,
                squadContext.ContactIsCaptor,
                squadContext.IsObserverOfContact,
                true,
                squadContext.Withdrawing,
                squadContext.WarningFireOpen);
            var response = Decide(in threatClass, threatState, role, proximityBand, in acquired);
            reason = AcquisitionReason(response.Intent, in threatClass, threatState);
            return response;
        }

        // #874 P-S4: which row answered, in the words a log reader needs. Ignore has exactly the
        // two producers the table gives it - Dead (row 1) and the refusal row 6 - and row 6 is
        // split by why the contact is refused: a coil-head is weapons-excluded for all but its elected watcher.
        private static string AcquisitionReason(
            BlacksiteThreatIntent intent, in BlacksiteThreatClass threatClass, BlacksiteThreatState threatState)
        {
            switch (intent)
            {
                case BlacksiteThreatIntent.Engage: return threatState == BlacksiteThreatState.Stunned ? "stunned" : "attackable";
                case BlacksiteThreatIntent.Rescue: return "captor";
                case BlacksiteThreatIntent.Evade:
                    return threatClass.WeaponsExcluded ? "weapons-excluded" : threatClass.EngagementRefused ? "engagement-refused" : "unstunnable";
                case BlacksiteThreatIntent.Withdraw: return "withdrawing";
                case BlacksiteThreatIntent.Watch: return "observer";
                default:
                    if (threatState == BlacksiteThreatState.Dead) return "dead";
                    return threatClass.WeaponsExcluded ? "weapons-excluded" : "engagement-refused";
            }
        }

        private static BlacksiteThreatResponse Engaging(in BlacksiteThreatClass threatClass)
            => new BlacksiteThreatResponse(
                BlacksiteThreatIntent.Engage,
                BlacksiteAimAnchor.ContactBody,
                RingFor(threatClass.Profile, BlacksiteEngagementGeometry.PlaneFor(threatClass.Profile)),
                GateFor(threatClass.Profile));

        private static BlacksiteThreatResponse Rescuing(in BlacksiteThreatClass threatClass)
            => new BlacksiteThreatResponse(
                BlacksiteThreatIntent.Rescue,
                BlacksiteAimAnchor.CaptureAnchor,
                // #1011 A3 / #1010 A2: a rescue ring is always the CARRIER's feet. The victim is
                // the one thing that names the floor exactly; the geometric fallback cannot promise
                // one (BlacksiteSoldierAI.Equipment.cs:718-727).
                RingFor(threatClass.Profile, BlacksiteRingPlane.CarrierFeet),
                GateFor(threatClass.Profile));

        private static BlacksiteThreatResponse Watching()
            => new BlacksiteThreatResponse(
                BlacksiteThreatIntent.Watch,
                // The elected watcher keeps its aim on the coil head; nothing is committed. #1271 S3a:
                // TickMonsterReaction faces it (WatchFaces) and the brain keeps the member's duty or Combat.
                BlacksiteAimAnchor.ContactBody,
                BlacksiteApproachRing.NoRing,
                BlacksiteReachGate.NoGate);

        private static BlacksiteThreatResponse Evading()
            => new BlacksiteThreatResponse(
                BlacksiteThreatIntent.Evade,
                BlacksiteAimAnchor.None,
                BlacksiteApproachRing.NoRing,
                BlacksiteReachGate.NoGate);

        private static BlacksiteThreatResponse Withdrawing()
            => new BlacksiteThreatResponse(
                BlacksiteThreatIntent.Withdraw,
                BlacksiteAimAnchor.None,
                BlacksiteApproachRing.NoRing,
                BlacksiteReachGate.NoGate);

        private static BlacksiteThreatResponse Ignored()
            => new BlacksiteThreatResponse(
                BlacksiteThreatIntent.Ignore,
                BlacksiteAimAnchor.None,
                BlacksiteApproachRing.NoRing,
                BlacksiteReachGate.NoGate);

        private static BlacksiteApproachRing RingFor(BlacksiteBodyProfile profile, BlacksiteRingPlane plane)
            => plane == BlacksiteRingPlane.None
                ? BlacksiteApproachRing.NoRing
                : new BlacksiteApproachRing(
                    BlacksiteEngagementGeometry.StandOff(profile),
                    plane,
                    BlacksiteEngagementGeometry.ApproachStop);

        private static BlacksiteReachGate GateFor(BlacksiteBodyProfile profile)
            => profile == BlacksiteBodyProfile.Hazard
                ? BlacksiteReachGate.NoGate
                : new BlacksiteReachGate(
                    BlacksiteEngagementGeometry.MinReach(profile),
                    BlacksiteEngagementGeometry.MaxReach(profile),
                    BlacksiteEngagementGeometry.VerticalTolerance,
                    BlacksiteEngagementGeometry.FacingDegrees);
    }
}
