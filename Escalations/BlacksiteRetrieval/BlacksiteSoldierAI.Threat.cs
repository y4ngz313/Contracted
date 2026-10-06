using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        /// <summary>
        /// #1019 S5: the per-soldier description cache the threat registry keys on the contact
        /// reference. It lives HERE, on the member, and not in a static dictionary keyed on
        /// <c>EnemyAI</c>: a static map keyed on a Unity object boxes its key under Mono and holds
        /// a destroyed enemy alive until something sweeps it, while this dies with the soldier
        /// (design note G10.7).
        /// </summary>
        private BlacksiteThreatDescriptionCache _threatDescriptionCache;

        /// <summary>
        /// #1019 S5: what <see cref="ResolveThreatIntent"/> last decided, and what it last measured
        /// the contact as. The diagnostic REPORTS these; it does not describe the contact again.
        /// An earlier form had <c>TraceDecision</c> ask the table itself against a neutral squad
        /// context, which could only ever answer Engage or Ignore - so <c>intent=Rescue</c> was
        /// unreachable on the line and score.ps1's rescue pairing was dead on arrival.
        /// </summary>
        internal BlacksiteThreatIntent LastThreatIntent { get; private set; } = BlacksiteThreatIntent.Engage;
        internal string LastThreatProfile { get; private set; } = BlacksiteDiagnosticFormat.None;

        /// <summary>
        /// #1233 S6 (rule 5): the ONE engagement gate. A monster contact is something this member
        /// may FIGHT - close on, swing at, shoot at, drop its cargo for, break custody for - only
        /// when both halves hold on this frame:
        /// <list type="bullet">
        /// <item>F1's resolver names a body this member's weapon can hit NOW
        /// (<see cref="ThreatContactCollider"/>: an enabled collider on a shovel-mask layer that
        /// vanilla's own hit contract maps back to this contact). A camouflaged Dreadweaver with its
        /// colliders switched off, or a modded creature with no <c>EnemyAICollisionDetect</c> at
        /// all, has none - every swing and every round would pass through it.</item>
        /// <item>there is evidence of threat: it hurt or touched this member
        /// (<see cref="BlacksiteCombatController.IsImmediateAttacker"/>), it is hunting a squadmate
        /// (a pursuit lease or capture in <see cref="BlacksiteVanillaMonsters"/>, or a target in
        /// <see cref="BlacksiteMonsterCombatAdapterRegistry"/>), or its native attack can reach this
        /// member from where it stands (<see cref="BlacksiteVanillaMonsters.NativeAttackReaches"/>).</item>
        /// </list>
        /// Everything short of that is KNOWLEDGE: the sighting is still reported, tracked, barked and
        /// shared exactly as before, and the member goes on with its work. Round 3 of the
        /// 2026-09-27 run is the case: member 1 saw a camouflaged Dreadweaver at 6.8 m, the escort's
        /// cargo latch armed on that sighting alone and broke custody (<c>cause=escortCombat</c>,
        /// t=1158.72) in the same tick the baton said <c>NoTarget ... blocked=not hittable</c>.
        ///
        /// Unknown modded enemies are neither safe nor engaged by class: the class row answers
        /// nothing here. One that stands in reach, or that the pursuit bridge has leased onto a
        /// member, is fought if it has a body; one seen across a room is remembered.
        ///
        /// Players are not gated here: the crew is the faction the contract is fought against, and
        /// a visible player has always been a target on sight. Host state throughout - every input
        /// is written only by the authority. Evaluated in cost order and short-circuited: the body
        /// walk runs once per ask, and the reach linecast only for a body nothing cheaper admits.
        /// </summary>
        internal bool ContactEngageable(EnemyAI enemy)
        {
            if (enemy == null || enemy.isEnemyDead || !enemy.isActiveAndEnabled) return false;
            if (ThreatContactCollider(enemy) == null) return false;
            return (_combatController != null && _combatController.IsImmediateAttacker(enemy))
                || BlacksiteVanillaMonsters.Hunts(enemy)
                || BlacksiteMonsterCombatAdapterRegistry.Hunts(enemy)
                || BlacksiteVanillaMonsters.NativeAttackReaches(enemy, this);
        }

        /// <summary>
        /// #1019 S5: the ONE intent producer. #1020 S6: and the one decision core reads it.
        ///
        /// It replaces the <c>bool rescue</c> that the deleted legacy perception block produced.
        /// #1305 H (C2): the brain samples it once per decision, whatever the traversal state, and
        /// reads it as <c>inBand</c> (Engage or Rescue at a contact in band) and <c>attacker</c>.
        /// A target that died on this think, a butler that has left state 2 and a withdrawing
        /// member's contact outside its band all answer Ignore or Withdraw through the table's own
        /// rows (#1019 S5, design note F.4), so none of them enters Combat.
        /// #1233 S6: the bounded blocked-approach hold (the old arm 2, <c>HoldPosition</c>) is
        /// deleted. A contact the weapon cannot reach this think is refused by the Combat executor
        /// itself (<see cref="TryExecuteSelectedCombat"/>, an OutOfBand readiness yields the think),
        /// and a contact that fails <see cref="ContactEngageable"/> is no
        /// contact at all here: the table is never asked about a body the weapon cannot hit.
        /// </summary>
        internal BlacksiteThreatIntent ResolveThreatIntent()
        {
            // The profile is the one measured on THIS think, or none. Arms (1) and (2) return
            // before the contact is described, and a stale profile from a contact that has since
            // died would otherwise be printed beside intent=Rescue.
            LastThreatProfile = BlacksiteDiagnosticFormat.None;

            // (1) #1011 A3: this member is the committed rescuer of a held squadmate (#874 Plan F).
            // #1305 H (C2): no hazard outranks it any more - a live disc constrains the rescuer's
            // route, it does not take the decision.
            if (TryGetRescueContact(out _, out _))
                return Record(BlacksiteThreatIntent.Rescue);

            // (2) #874 Plan F: one committed rescuer per held squadmate. A baton member whose
            // contact is the captor of a squadmate ANOTHER member is committed to rescuing leaves
            // that captor to the rescuer: Ignore, so it covers or goes back to its work.
            // The contact is the one the controller holds or, with none, the one
            // TryConvergeOnOrderedContact would walk to - an order on it or a fresh hit from it -
            // which arm (4) would otherwise answer as Engage. A capture with no rescuer yet is left
            // to the table, so a member outside RescueRadius still closes on it and becomes
            // eligible. The election has just run in arm (1). Riflemen are untouched: the
            // controller already holds their fire on this captor.
            EnemyAI contact = _combatController?.CurrentEnemyTarget;
            if (UsesBaton)
            {
                EnemyAI engaged = contact != null ? contact
                    : HasMonsterAlertOrder || HasRecentDamage ? CombatEnemyTarget : null;
                if (engaged != null && BlacksiteVanillaMonsters.RescuedByAnother(engaged, this))
                    return Record(BlacksiteThreatIntent.Ignore);
            }

            // (3) The table, over the contact the combat controller has actually acquired - and
            // only while that contact still passes the gate. The contact is described with its
            // MEASURED body - the collider the melee path has already resolved through
            // BatonContactBody's own one-slot cache, on this member's own floor - so the profile
            // the diagnostic prints is the measured one and MeasureBody has a production caller.
            // The measurement is cached with the class, keyed on the contact reference plus the
            // three class-changing facts, so it costs one collider read and one ground ray per
            // contact rather than per think.
            if (contact != null
                && ContactEngageable(contact)
                && BlacksiteThreatAdapterRegistry.Cached(ref _threatDescriptionCache, contact, this,
                    ThreatContactCollider(contact), transform.position.y,
                    out var threatClass, out var state))
            {
                // Arm (1) answered every rescue, so no captured-ally row is offered here.
                var context = new BlacksiteSquadContext(
                    allyCaptured: false,
                    contactIsCaptor: false,
                    // Vision for the reaction rows is the monster reaction's, never this path's:
                    // rows 4 and 5 belong to TickMonsterReaction and are unreachable from here (a class
                    // the weapon cannot stop is answered by the reaction's own sighting, not this path's).
                    isObserverOfContact: false,
                    contactInReactionRange: false,
                    withdrawing: _transportLost || IsRaidWithdrawing || Time.time < _raidRegroupUntil,
                    // #874 N8: a rifle's open warning fire at an Old Bird is what lets that one
                    // contact be engaged; spent, it is Ignore and the member goes back to work.
                    warningFireOpen: BlacksiteThreatAdapterRegistry.WarningFireOpen(contact, this));
                LastThreatProfile = threatClass.Profile.ToString();
                return Record(BlacksiteThreatResponsePolicy.Decide(
                    in threatClass, state,
                    UsesBaton ? BlacksiteEngagementRole.Melee : BlacksiteEngagementRole.Rifle,
                    ThreatProximity(), in context).Intent);
            }

            // (4) No engageable contact acquired: the PROBE answer, which is what `rescue = false`
            // has always produced. It is not an engagement - it is what lets the Combat executor
            // run its acquisition on this think, and that acquisition admits only a contact that
            // passes the gate; with none it refuses the think and the duty board takes it. Nothing
            // was described, so the profile is named as absent.
            LastThreatProfile = BlacksiteDiagnosticFormat.None;
            return Record(BlacksiteThreatIntent.Engage);
        }

        private BlacksiteThreatIntent Record(BlacksiteThreatIntent intent)
        {
            LastThreatIntent = intent;
            return intent;
        }
    }
}
