using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #1019 S5: THIS registry answers the SOLDIER's question - "what is this contact, and what
    // should I do about it" - and it produces a BlacksiteThreatClass and a BlacksiteThreatState for
    // BlacksiteThreatResponsePolicy.Decide.
    //
    // BlacksiteMonsterCombatAdapterRegistry answers the MONSTER's question - "who is this monster
    // chasing, and when does it swing" - and it drives the monster's own motor. The two are
    // deliberately NOT merged (design note G8): they have different clocks, different lifetimes and
    // different owners, and one of them writes to vanilla AI state while this one writes nothing at
    // all. Nothing here reads or touches that registry's adapters, its clocks or its targets.

    /// <summary>
    /// #1019 S5: the production description cache, as a per-soldier VALUE rather than a static
    /// dictionary keyed on <c>EnemyAI</c>. A static dictionary keyed on a Unity object boxes the
    /// key under Mono and keeps a destroyed enemy alive until something sweeps it; a struct the
    /// soldier owns dies with the soldier (design note G10.7).
    ///
    /// It holds the contact reference only to compare it - never to dereference it - and it is
    /// invalidated by a change of contact or by a change of the three facts that can change a CLASS
    /// mid-round. It is deliberately NOT keyed on the nine-value threat state: that flips several
    /// times a second on <c>moveTowardsDestination</c> alone, and none of Idle, Searching, Chasing,
    /// Stunned, AttachedToPlayer or Dead changes anything the class carries.
    /// </summary>
    internal struct BlacksiteThreatDescriptionCache
    {
        private object _contact;
        private BlacksiteThreatClass _class;
        private byte _facts;
        private bool _valid;

        internal bool TryGet(EnemyAI enemy, byte facts, out BlacksiteThreatClass threatClass)
        {
            threatClass = _class;
            return _valid && ReferenceEquals(_contact, enemy) && _facts == facts;
        }

        internal void Store(EnemyAI enemy, in BlacksiteThreatClass threatClass, byte facts)
        {
            _contact = enemy;
            _class = threatClass;
            _facts = facts;
            _valid = true;
        }

        internal void Invalidate()
        {
            _contact = null;
            _class = default;
            _facts = 0;
            _valid = false;
        }
    }

    /// <summary>
    /// #1019 S5: turns a live <c>EnemyAI</c> into the (class, state) pair the table decides on, in
    /// two halves with very different costs. #1293 R5: the per-class facts are rows of
    /// <see cref="BlacksiteMonsterCapabilities"/>; this registry reads them and adds the live state.
    ///
    /// <see cref="DescribeClass"/> is the CHEAP half - the capability row, state flags, the
    /// weapons gate and the support level - and it is what the 0.2 s roster walk and the
    /// perception block call, once per contact. It walks no colliders, casts no rays and
    /// allocates nothing.
    ///
    /// <see cref="MeasureBody"/> is the EXPENSIVE half - the measured profile arms - and it runs
    /// only for the ONE committed melee contact, against the collider that path has already
    /// resolved through <c>BatonContactBody</c>'s own one-slot cache. That cache is right for
    /// exactly one contact, which is what the melee path has and what a roster walk does not: a
    /// walk that alternated contacts through it would re-run
    /// <c>GetComponentsInChildren&lt;Collider&gt;(false)</c> - and its array allocation - per
    /// contact per think.
    ///
    /// <see cref="TryDescribe"/> is the pure test seam over both, and neither half has a clock of
    /// its own (design note F.3.4's seam).
    /// </summary>
    internal static class BlacksiteThreatAdapterRegistry
    {
        /// <summary>
        /// A measured body at least this tall is a Giant whatever its type is, so a modded five
        /// metre enemy gets the giant ring with no table row (design note C.7, G1).
        /// </summary>
        internal const float GiantBodyHeight = 3.5f;

        // The facts that can change a CLASS while the contact reference stays the same. Any
        // other state change leaves the class identical, which is what makes the cache below sound.
        private const byte FactButlerHostile = 1;
        private const byte FactKiwiHostile = 2;
        private const byte FactRiding = 4;
        // #1083 (WP I): the give-up clock is the FOURTH such fact, and it has to be one. The cache
        // stores WeaponsExcluded on the class, so without a bit here a member that has just given
        // up on a contact would keep being served the description it cached a frame earlier.
        private const byte FactGaveUp = 8;

        /// <summary>
        /// #1019 S5: the cheap half. Everything about the contact that does not need its body
        /// measured, which is everything the table's rows actually read.
        ///
        /// <paramref name="observer"/> is taken for signature symmetry with
        /// <see cref="TryDescribe"/> and <see cref="Cached"/> and is deliberately NOT read: nothing
        /// about what a contact IS depends on which member is asking - the observer-specific half
        /// is <c>BlacksiteSquadContext.IsObserverOfContact</c> - and that is exactly what makes one
        /// class cacheable per contact rather than per (contact, member) pair.
        ///
        /// False only for a contact that is null or destroyed, and even then the state is answered
        /// (<see cref="BlacksiteThreatState.Dead"/>) rather than left undefined.
        /// </summary>
        internal static bool DescribeClass(EnemyAI enemy, BlacksiteSoldierAI observer,
            out BlacksiteThreatClass threatClass, out BlacksiteThreatState state)
        {
            threatClass = default;
            state = BlacksiteThreatState.Dead;
            // The UNITY null check, deliberately: SpawnedEnemies can hold destroyed objects, and
            // this is the opposite direction to #1009 A2 (F6)'s rule about `??` bypassing the
            // lifetime check (BlacksiteSoldierAI.Equipment.cs:443-445, Behaviour.cs:332). Both
            // rules are about the SAME lifetime; they only point at different operators.
            if (enemy == null) return false;

            state = ObserveState(enemy);
            threatClass = Describe(enemy);
            return true;
        }

        /// <summary>
        /// #1019 S5: the expensive half, for the ONE contact a collider has already been resolved
        /// for. It refines <paramref name="threatClass"/> in place: the measured radius, height and
        /// grounded Y always, and the PROFILE only when the class arms left it Humanoid - a flea,
        /// a Forest Keeper or a slime has already been named by what it is, not by how big it is.
        ///
        /// <paramref name="collider"/> is the one <c>BatonContactBody</c>
        /// (BlacksiteHitResolver.ResolveBody) resolved, so nothing here walks a hierarchy.
        /// <paramref name="observerFloorY"/> is the asking member's own feet, and it is what makes
        /// the ground cast below reach a contact a storey above or below him.
        /// </summary>
        internal static void MeasureBody(EnemyAI enemy, Collider collider, float observerFloorY,
            ref BlacksiteThreatClass threatClass)
        {
            if (enemy == null || collider == null) return;
            Bounds bounds = collider.bounds;
            // The same two early-outs MeasureBatonContact takes (Equipment.cs:660-662): a contact
            // with no usable bounds keeps the profile's declared defaults.
            if (bounds.size.sqrMagnitude <= 1e-6f) return;

            float height = bounds.size.y;
            float radius = Mathf.Max(bounds.size.x, bounds.size.z) * .5f;
            float groundedY = GroundUnder(bounds, observerFloorY);
            BlacksiteBodyProfile profile = threatClass.Profile;
            if (profile == BlacksiteBodyProfile.Humanoid)
            {
                // Giant, then Flyer, then GroundCrawler: only a Humanoid row (every unknown type
                // included) is refined by its measured body.
                if (height >= GiantBodyHeight) profile = BlacksiteBodyProfile.Giant;
                // Nothing in the tree reads a flight flag at 7d5f3107, so this ground gap - and not
                // a state - is the whole of the Flyer profile (H.check: Airborne is dropped).
                else if (bounds.center.y - groundedY > BlacksiteEngagementGeometry.VerticalTolerance)
                    profile = BlacksiteBodyProfile.Flyer;
                // BlacksiteCombatPolicy.BatonLowContactHeight (:315): a body whose collider is
                // below it is one the authored arc passes over, which is the #1010 A2 shape.
                else if (height <= BlacksiteCombatPolicy.BatonLowContactHeight)
                    profile = BlacksiteBodyProfile.GroundCrawler;
            }

            threatClass = new BlacksiteThreatClass(
                profile,
                radius,
                height,
                groundedY,
                threatClass.WeaponsExcluded,
                threatClass.ReactionRange,
                threatClass.ReactionConeDegrees,
                threatClass.ReactionIsWatch,
                threatClass.Support,
                threatClass.EngagementRefused,
                threatClass.Stunnable);
        }

        /// <summary>
        /// #1019 S5: the pure test seam. Always computes, has no clock, mutates nothing. The
        /// four-argument form is the class half alone - what a fixture drives when it is asking
        /// what a contact IS.
        /// </summary>
        internal static bool TryDescribe(EnemyAI enemy, BlacksiteSoldierAI observer,
            out BlacksiteThreatClass threatClass, out BlacksiteThreatState state)
            => TryDescribe(enemy, observer, null, 0f, out threatClass, out state);

        /// <summary>
        /// The whole description: the class half, then the measured half when a collider is
        /// supplied. The melee path is the only caller that has one.
        /// </summary>
        internal static bool TryDescribe(EnemyAI enemy, BlacksiteSoldierAI observer,
            Collider collider, float observerFloorY,
            out BlacksiteThreatClass threatClass, out BlacksiteThreatState state)
        {
            if (!DescribeClass(enemy, observer, out threatClass, out state)) return false;
            if (collider != null) MeasureBody(enemy, collider, observerFloorY, ref threatClass);
            return true;
        }

        /// <summary>
        /// #1019 S5: the production wrapper over the class half. Same body, same answer, no clock.
        /// The state is recomputed on every call because it is cheap and it is what the table
        /// decides on; the class is returned from the cache unless the contact or one of the three
        /// class-changing facts has moved.
        /// </summary>
        internal static bool Cached(ref BlacksiteThreatDescriptionCache cache, EnemyAI enemy,
            BlacksiteSoldierAI observer, Collider collider, float observerFloorY,
            out BlacksiteThreatClass threatClass, out BlacksiteThreatState state)
        {
            threatClass = default;
            state = BlacksiteThreatState.Dead;
            if (enemy == null) { cache.Invalidate(); return false; }

            state = ObserveState(enemy);
            byte facts = ClassFacts(enemy);
            if (cache.TryGet(enemy, facts, out threatClass)) return true;

            threatClass = Describe(enemy);
            // The MEASURED half, behind the same cache: one collider read and one downward ray per
            // contact rather than per think. The collider is the one BatonContactBody has
            // already resolved for this contact, so nothing walks a hierarchy here either.
            if (collider != null) MeasureBody(enemy, collider, observerFloorY, ref threatClass);
            cache.Store(enemy, in threatClass, facts);
            return true;
        }

        /// <summary>
        /// #1019 S5: never shoot at, swing at or alert on this contact: the class half
        /// (<see cref="WeaponsExcludedByClassFor"/>, the capability row with its two STATEFUL
        /// exclusions), then the host-only give-up memo, then the escort objective.
        /// </summary>
        internal static bool WeaponsExcludedFor(EnemyAI enemy)
            => WeaponsExcludedByClassFor(enemy)
               // #1083 (WP I): the same answer, MEASURED rather than claimed. This is the single
               // forwarder every weapon path already asks - the rifle target filter
               // (BlacksiteHazardAwareness.ExcludeFromRifleTargets), acquisition
               // (BlacksiteSoldierAI.CanConsiderEnemy and with it the sweep, the remembered target
               // and the rescue captor), the alert receipt (ReceiveMonsterAlertOrder), the baton
               // engagement and its sweep and Describe's own WeaponsExcluded - so an instance the
               // memo has given up on stops being alerted on, closed in on, swung at and shot at in
               // one place instead of seven. Every one of those readers is HOST-side, which is what
               // makes a host-only memo a sound term of this predicate.
               //
               // The ONE reader that is not host-side - BlacksiteBatonStun.TryApply, which every
               // peer runs from its HitEnemyOnLocalClient/HitEnemyClientRpc postfixes for a PLAYER's
               // baton - asks WeaponsExcludedByClassFor below instead, for the reason stated there.
               || GaveUpOn(enemy)
               // 2026-09-16 playtest (WP1, #963): the witness is the contract OBJECTIVE the squad
               // escorts out, not a contact it fights. The run logged weaponsExcluded=False, 23
               // baton hits at 2 damage against his 45 HP, and extraction 0.16 s after the kill.
               // The arm is HERE rather than in the class half below on purpose: the class half is
               // the one predicate a PLAYER's baton stun reads on every peer, and the crew must
               // keep stunning and killing him - that is how the crew wins the contract.
               || IsEscortObjective(enemy);

        /// <summary>
        /// #1126: the one contact the squad must SEE while never fighting it. The arm above makes the
        /// witness weapons-excluded; this names him to the SIGHT gate
        /// (<c>BlacksiteSoldierAI.CanSightEnemy</c>, through
        /// <c>BlacksiteSightPolicy.BlindedByWeaponExclusion</c>), so #1091's exclusion costs the squad
        /// its weapons and not its eyes. It is the same row fact as the arm above, so the two facts
        /// about the witness cannot drift.
        ///
        /// It is deliberately NOT a term of any weapons predicate. Being seen buys him nothing on the
        /// rifle, the baton, the sweep, the alert receipt, the acquisition latch, the remembered
        /// target or the rescue captor - every one of those still reads
        /// <see cref="WeaponsExcludedFor"/> through <c>BlacksiteSoldierAI.CanConsiderEnemy</c> and
        /// <c>BlacksiteCombatController.IsValidEnemy</c>, which are untouched.
        ///
        /// The give-up memo is deliberately absent too. Sight is restored for the escort objective
        /// alone, so a memo'd instance stays as unseen as it is today and the memo's behaviour is
        /// unchanged for every enemy; the memo can in any case only ever name a contact the squad has
        /// already shot at, which the witness never is.
        /// </summary>
        internal static bool IsEscortObjective(EnemyAI enemy)
            => BlacksiteMonsterCapabilities.For(enemy).EscortObjective;

        /// <summary>
        /// #874 WP F: the classes the squad's weapons cannot hurt, which stay seen, reported,
        /// alerted on and withdrawn from. It is deliberately NOT a term of
        /// <see cref="WeaponsExcludedFor"/>: that predicate is also the first gate of the sight test
        /// (BlacksiteSightPolicy.BlindedByWeaponExclusion), and #1126 measured what folding a class
        /// into it costs - the squad went blind to the witness. The weapons gates ask
        /// <see cref="IsAttackable"/> or <see cref="IsAttackableBy"/>; the sight gate keeps asking
        /// the exclusion alone.
        ///
        /// The Old Bird (RadMechAI) is the one class. Its HitEnemy is a no-op, so before WP F the
        /// squad drew, closed in, swung and fired at it until BlacksiteFutileDamageMemo had counted
        /// three futile hits - a fight with a known-immune class, and then an instance the memo
        /// also made invisible.
        ///
        /// #874 N8 (decision 7) replaced WP F's "never targeted or fought; soldiers neither fight
        /// nor back away". A baton member still never draws, closes in or swings at it, and now
        /// EVADES it on sight inside its reaction range (<see cref="Reaction"/>). A rifle member may
        /// fire a warning first - <see cref="WarningFireOpen"/> - and evades it once that is spent
        /// or the Old Bird has attacked it or a squadmate. The Old Bird's own stomp is what closes
        /// the warning (<see cref="NoteRefusedContactAttack"/>); nothing here writes its attacks.
        /// </summary>
        internal static bool EngagementRefusedFor(EnemyAI enemy) => BlacksiteMonsterCapabilities.For(enemy).EngagementRefused;

        /// <summary>#1262 P4: a body with no EnemyType makes no claim of stun immunity.</summary>
        internal static bool Stunnable(EnemyAI enemy) => enemy != null && (enemy.enemyType == null || enemy.enemyType.canBeStunned);

        /// <summary>
        /// #874 WP F: THE attackable-contact definition for the WHOLE squad - a live contact that is
        /// neither weapons-excluded (class, state or give-up memo) nor a class the squad's weapons
        /// cannot hurt. It is what every gate that is not one rifle's own asks - the baton
        /// engagement, its sweep, the draw, the alert order and the squad's handed-out track - and
        /// <see cref="BlacksiteThreatClass.Attackable"/> is the same fact for the threat table.
        /// #874 N8: a member's own acquisition and its rifle's target gate ask
        /// <see cref="IsAttackableBy"/>, which adds that member's warning fire. Host-side, like the
        /// memo inside it.
        /// </summary>
        internal static bool IsAttackable(EnemyAI enemy)
            => enemy != null && !enemy.isEnemyDead && !WeaponsExcludedFor(enemy) && !EngagementRefusedFor(enemy);

        /// <summary>#1262 P4 (D4): a baton member's own acquisition, its rescue-captor gate (RescueRefusal ->
        /// CanConsiderRescueCaptor) and its combat target gate refuse a class its baton cannot stun, so it never
        /// closes on or is elected to rescue from a keeper; a rifle member takes it as an ordinary target.</summary>
        internal static bool IsAttackableBy(EnemyAI enemy, BlacksiteSoldierAI soldier)
            => (IsAttackable(enemy) && (soldier == null || !soldier.UsesBaton
                   || (BlacksiteMonsterCapabilities.HittableBy(enemy) & BlacksiteWeaponReach.Baton) != 0))
               || WarningFireOpen(enemy, soldier);

        /// <summary>
        /// #874 N8 (decision 7): this RIFLE member may still fire at this refused contact. Never a
        /// baton member, and never a member carrying or pushing cargo: its hands cannot fire, and
        /// setting the cargo down to shoot at something the rounds cannot hurt is the wrong trade -
        /// it flees with the cargo instead. Otherwise open while the squad has not been attacked by
        /// the contact and the member has committed fewer than
        /// <see cref="BlacksiteWarningFireMemo.WarningBurstsPerEncounter"/> bursts at it, and ALSO
        /// while the burst that spent the budget is still in flight, so the evade that follows
        /// does not cut a burst already committed. The attack latch outranks that burst: a stomped
        /// squad runs now. Host state; the table reads it through
        /// <see cref="BlacksiteSquadContext.WarningFireOpen"/>.
        /// </summary>
        internal static bool WarningFireOpen(EnemyAI enemy, BlacksiteSoldierAI soldier)
        {
            if (!EngagementRefusedFor(enemy) || soldier == null || soldier.UsesBaton
                || !TryContactKey(enemy, out ulong contact) || enemy.isEnemyDead || WeaponsExcludedFor(enemy)
                || soldier.IsObjectiveCarrier || soldier.HasOccupiedCargoHands)
                return false;
            if (WarningFire.MayFire(contact, soldier.NetworkObjectId, soldier.SquadId)) return true;
            return !WarningFire.WasAttacked(contact, soldier.SquadId) && soldier.IsFiringBurstAt(enemy);
        }

        /// <summary>
        /// #874 N8: one burst committed at this contact - BlacksiteCombatController.BeginFiring, the
        /// one place a burst (or an aimed single) is committed at a target. Only a refused class is
        /// counted. Host-only, like the fire path that calls it.
        /// </summary>
        internal static void NoteWarningBurst(EnemyAI enemy, BlacksiteSoldierAI soldier)
        {
            if (!EngagementRefusedFor(enemy) || soldier == null || !TryContactKey(enemy, out ulong contact)) return;
            int bursts = WarningFire.RecordBurst(contact, soldier.NetworkObjectId);
            if (!BlacksiteDiagnostics.Enabled) return;
            BlacksiteDiagnostics.Log("MonsterAdapter",
                "warning burst " + bursts + "/" + BlacksiteWarningFireMemo.WarningBurstsPerEncounter
                + " member=" + soldier.MemberIndex + " at " + ContactName(enemy) + "#" + contact
                + (bursts >= BlacksiteWarningFireMemo.WarningBurstsPerEncounter
                    ? "; warning fire spent, this member evades it from here." : "."));
        }

        /// <summary>
        /// #874 N8: a refused contact attacked a squad member - BlacksiteSoldierAI
        /// .NotifyAttackedByEnemy, which every monster hit on a soldier reaches (the Old Bird's
        /// stomp through BlacksiteMonsterCombatAdapterRegistry.NotifyRadMechStomp). The warning is
        /// closed for every rifle of that squad, so they evade it as the batons already do.
        /// </summary>
        internal static void NoteRefusedContactAttack(EnemyAI enemy, BlacksiteSoldierAI victim)
        {
            if (!EngagementRefusedFor(enemy) || victim == null || !TryContactKey(enemy, out ulong contact)) return;
            if (!WarningFire.RecordAttack(contact, victim.SquadId) || !BlacksiteDiagnostics.Enabled) return;
            BlacksiteDiagnostics.Log("MonsterAdapter",
                ContactName(enemy) + "#" + contact + " attacked member=" + victim.MemberIndex
                + "; the squad's warning fire is closed and its rifles evade it.");
        }

        /// <summary>#874 N8: round reset. The warning budget is round state; nothing else clears it.</summary>
        internal static void ResetWarningFire() => WarningFire.Reset();

        private static readonly BlacksiteWarningFireMemo WarningFire = new BlacksiteWarningFireMemo();

        private static string ContactName(EnemyAI enemy)
            => enemy.enemyType != null ? enemy.enemyType.enemyName : enemy.GetType().Name;

        /// <summary>
        /// #1083 (WP I): the CLASS half of weapon exclusion - every arm above the memo, and nothing
        /// that only the host knows. It is what a decision REPLICATED ACROSS PEERS has to ask: a
        /// row answers identically on every machine (type, plus two vanilla fields that are
        /// themselves replicated state), while the give-up memo is written only where the
        /// squad's damage is applied and therefore reads empty on a client.
        ///
        /// The one such decision today is a PLAYER's baton stun (BlacksiteBatonStun.TryApply, run
        /// per peer). Folding the memo into it would let the host refuse a stun that a remote client
        /// applies, leaving a 1 s stun state alive on one peer only. The squad's own stun cannot
        /// reach it for a given-up instance at all - a soldier never swings at one, because
        /// SweepBatonSegment asks the memo-inclusive predicate before the swing lands - so nothing
        /// is lost by keeping the memo out of this arm.
        /// </summary>
        internal static bool WeaponsExcludedByClassFor(EnemyAI enemy)
            => BlacksiteMonsterCapabilities.HittableBy(enemy) == BlacksiteWeaponReach.None;

        /// <summary>
        /// #1083 (WP I): the per-instance half of weapon exclusion. HOST state - the memo is only
        /// ever written by <see cref="ObserveDamageAttempt"/>, which runs where the damage is
        /// applied and where the soldier decides - so a client reads an empty memo and answers
        /// exactly what it answers today.
        /// </summary>
        internal static bool GaveUpOn(EnemyAI enemy)
            => FutileDamage.TrackedCount != 0 && TryContactKey(enemy, out ulong contact) && FutileDamage.IsExcluded(contact);

        /// <summary>
        /// #1083 (WP I): one CONFIRMED hit - our code invoked the vanilla damage entry point on
        /// this instance, and <paramref name="healthBefore"/>/<paramref name="healthAfter"/> are
        /// that instance's own <c>enemyHP</c> read on both sides of the call. A miss, a blocked
        /// swing and a stun that did not apply never reach here, because the two call sites are the
        /// two lines that actually call <c>HitEnemyOnLocalClient</c>.
        ///
        /// A DEAD contact is not a sample on either side: both call sites refuse a dead candidate
        /// before they swing or fire (IsValidEnemy, and the sweep's own isEnemyDead gate), and the
        /// killing round is dropped here - a corpse's HP does not move for anyone, so counting the
        /// hit that killed something as futile is the one false positive worth spending a branch on.
        ///
        /// #874 N8: a refused class is not a sample either. Its immunity is a CLASS fact the warning
        /// budget already bounds, and a rifle's two warning bursts are up to six futile rounds - a
        /// give-up would fold the Old Bird into WeaponsExcludedFor, which blinds the sight gate, and
        /// the squad would stop seeing the thing it is running from.
        /// </summary>
        internal static void ObserveDamageAttempt(EnemyAI enemy, int healthBefore, int healthAfter, string weapon)
        {
            if (enemy == null || enemy.isEnemyDead || EngagementRefusedFor(enemy) || !TryContactKey(enemy, out ulong contact)) return;
            if (!FutileDamage.RecordHit(contact, healthBefore, healthAfter)) return;
            BlacksiteDiagnostics.LogRateLimited(
                "monster.giveup." + contact,
                30f,
                "MonsterAdapter",
                "giving up on " + (enemy.enemyType != null ? enemy.enemyType.enemyName : enemy.GetType().Name)
                + "#" + contact + ": " + BlacksiteFutileDamageMemo.FutileHitsBeforeGivingUp
                + " hits, hp unchanged at " + healthAfter + " (weapon=" + weapon
                + "); this instance is weapons-excluded for the rest of the round.");
        }

        /// <summary>#1083 (WP I): round reset. The memo is round state; nothing else clears it.</summary>
        internal static void ResetFutileDamage() => FutileDamage.Reset();

        /// <summary>
        /// #1083 (WP I): the memo itself. Keyed by NETWORK OBJECT ID rather than by an EnemyAI
        /// reference, for the reason BlacksiteThreatDescriptionCache gives above: a static
        /// dictionary keyed on a Unity object keeps a destroyed enemy alive until something sweeps
        /// it. The id is the same handle BatonStunClientRpc and BlacksiteVanillaMonsters already
        /// use for a contact, it is stable for the life of the spawn, and it makes the decision
        /// logic Unity-free.
        /// </summary>
        private static readonly BlacksiteFutileDamageMemo FutileDamage = new BlacksiteFutileDamageMemo();

        private static bool TryContactKey(EnemyAI enemy, out ulong contact)
        {
            contact = 0UL;
            // The spawned test is BlacksiteVanillaMonsters.cs:205's, verbatim: NetworkObjectId on an
            // unspawned body is not a key, it is an error log.
            if (enemy == null || enemy.NetworkObject == null || !enemy.NetworkObject.IsSpawned) return false;
            contact = enemy.NetworkObject.NetworkObjectId;
            return true;
        }

        /// <summary>
        /// #1019 S5: what the contact is DOING, in the order the facts override one another. One
        /// implementation for all six profiles: the states are properties of the contact, not of
        /// the profile.
        /// </summary>
        internal static BlacksiteThreatState ObserveState(EnemyAI enemy)
            => enemy == null
                ? BlacksiteThreatState.Dead
                : ObserveState(enemy, BlacksiteVanillaMonsters.CaptureVictim(enemy));

        private static BlacksiteThreatState ObserveState(EnemyAI enemy, BlacksiteSoldierAI captive)
        {
            if (enemy == null || enemy.isEnemyDead) return BlacksiteThreatState.Dead;
            // A PLAYER in its hands outranks everything: it is neither ours to rescue nor ours to
            // interrupt. BlacksiteVanillaMonsters.HasNativePlayerCapture (VanillaMonsters.cs:123).
            if (BlacksiteVanillaMonsters.HasNativePlayerCapture(enemy)) return BlacksiteThreatState.AttachedToPlayer;
            // #1011 A3: a squadmate in its hands. BlacksiteVanillaMonsters.CaptureVictim (:135-136),
            // resolved once by the caller.
            if (captive != null) return BlacksiteThreatState.AttachedToAlly;
            // BlacksiteVanillaMonsters.Pursuit.cs:82 reads the cling as the reason the flea is not
            // walking anywhere.
            if (enemy is CentipedeAI flea && flea.clingingToCeiling) return BlacksiteThreatState.CeilingHeld;
            // Both halves mean "the native motor owns this body and it is not attacking", which is
            // one decision for the soldier: Pursuit.cs:49 and :82 read them in exactly that sense.
            if (enemy.stunNormalizedTimer >= 0f || enemy.inSpecialAnimation) return BlacksiteThreatState.Stunned;
            // The two facts in the whole tree that flip weapon exclusion mid-round
            // (BlacksiteMonsterCapabilities.HittableBy reads the same two).
            if (enemy is ButlerEnemyAI && enemy.currentBehaviourStateIndex == 2) return BlacksiteThreatState.Hostile;
            if (enemy is GiantKiwiAI && !BlacksiteKiwiThreatState.IsAvoided(enemy)) return BlacksiteThreatState.Hostile;
            // Reported, not decided on. The soldier-side pursuit record is private to
            // BlacksiteVanillaMonsters; these three read the vanilla fields that record WRITES -
            // Destination (Pursuit.cs:44) sets moveTowardsDestination, and an idle monster is
            // behaviour state 0 (Pursuit.cs:65) - so the answer follows the same facts without
            // reaching into the dictionary or re-walking it.
            if (enemy.movingTowardsTargetPlayer || enemy.moveTowardsDestination) return BlacksiteThreatState.Chasing;
            if (enemy.currentBehaviourStateIndex > 0) return BlacksiteThreatState.Searching;
            return BlacksiteThreatState.Idle;
        }

        // The facts that can change a class while the contact reference does not: the butler
        // turning hostile, the sapsucker latch going hot, a flea taking or leaving the ceiling, and
        // (#1083) this member's squad giving up on damaging it. Everything else a class carries is
        // fixed for the life of the contact.
        private static byte ClassFacts(EnemyAI enemy)
        {
            byte facts = 0;
            if (enemy is ButlerEnemyAI && enemy.currentBehaviourStateIndex == 2) facts |= FactButlerHostile;
            if (enemy is GiantKiwiAI && !BlacksiteKiwiThreatState.IsAvoided(enemy)) facts |= FactKiwiHostile;
            if (enemy is CentipedeAI flea && flea.clingingToCeiling) facts |= FactRiding;
            // #1083 (WP I): the give-up clock flips WeaponsExcluded mid-round exactly as the butler
            // and the sapsucker latch do, so it is a cache key for exactly the same reason.
            if (GaveUpOn(enemy)) facts |= FactGaveUp;
            return facts;
        }

        // The same downward mask BlacksiteSoldierAI.ResolveBatonLaneFloor uses
        // (Equipment.cs:754-756) and the same soldier-relative cast length it bounds with
        // (:769-771), so "the ground under this contact" is one question with one answer and a
        // contact a storey above the asking member is still reached.
        //
        // The origin is just UNDER the contact's own bounds and not above them: a downward ray that
        // starts above a solid collider hits that collider first, which would make every measured
        // ground gap negative and the Flyer arm dead. Starting below the body cannot hit it at all.
        // Nothing is hit for a body already resting on the floor - the ray starts inside or under
        // the floor collider - and the fallback is the body's own underside, which for a grounded
        // contact IS the ground.
        private static float GroundUnder(Bounds bounds, float observerFloorY)
        {
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            Vector3 origin = new Vector3(bounds.center.x, bounds.min.y - .01f, bounds.center.z);
            float length = Mathf.Max(4f, bounds.min.y - observerFloorY + 4f);
            return Physics.Raycast(origin, Vector3.down, out var floor, length, mask, QueryTriggerInteraction.Ignore)
                ? floor.point.y
                : bounds.min.y;
        }

        // #1262 P4 (D4, rule 4): every class is reacted to at its row's band (one band by default); a
        // Gaze row (the coil-head) is WATCHED rather than evaded, and the cold sapsucker's band is its
        // nest footprint plus the egg disc.
        private static void Reaction(EnemyAI enemy, out float range, out float cone, out bool watch)
        {
            var row = BlacksiteMonsterCapabilities.For(enemy);
            range = row.ReactionRange; cone = row.ReactionConeDegrees; watch = row.Gaze;
            if (enemy is GiantKiwiAI bird && BlacksiteKiwiThreatState.IsAvoided(enemy))
                range = BlacksiteHazardPolicy.NestFootprintRadius(bird.protectNestRadius) + BlacksiteHazardPolicy.EggFootprintRadius;
        }

        // The one class-half Describe. The profile is the capability row's (with its live overrides); the
        // radius and height are that profile's declared row until MeasureBody supplies the
        // contact's own, which is what makes the five declared radii FALLBACKS rather than the live
        // path for a committed melee contact (design note C.2). GroundedY is the contact's own root
        // until then, for the same reason.
        private static BlacksiteThreatClass Describe(EnemyAI enemy)
        {
            BlacksiteBodyProfile profile = BlacksiteMonsterCapabilities.ProfileOf(enemy);
            Reaction(enemy, out float reactionRange, out float reactionCone, out bool watch);
            return new BlacksiteThreatClass(
                profile,
                BlacksiteEngagementGeometry.RadiusOf(profile),
                DefaultHeight(profile),
                enemy.transform.position.y,
                WeaponsExcludedFor(enemy),
                reactionRange,
                reactionCone,
                watch,
                BlacksiteMonsterCapabilities.GetSupportLevel(enemy),
                EngagementRefusedFor(enemy),
                stunnable: Stunnable(enemy));
        }

        // Fallbacks only, for a contact nothing has measured. The humanoid figure is the 2.5 m
        // player capsule BlacksiteMeleeReachChecks measures against (BlacksiteCombatPolicy.cs:247-248);
        // the crawler figure is BatonLowContactHeight (:315), the height below which the authored
        // arc passes over a body; the giant figure is the threshold a measured body is called a
        // Giant at.
        private static float DefaultHeight(BlacksiteBodyProfile profile)
        {
            switch (profile)
            {
                case BlacksiteBodyProfile.GroundCrawler:
                case BlacksiteBodyProfile.Ceiling:
                    return BlacksiteCombatPolicy.BatonLowContactHeight;
                case BlacksiteBodyProfile.Giant: return GiantBodyHeight;
                case BlacksiteBodyProfile.Hazard: return 0f;
                default: return 2.5f;
            }
        }
    }
}
