using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        private readonly BlacksiteBehaviourSelector _behaviourSelector = new BlacksiteBehaviourSelector();
        private Vector3? _behaviourGoal;
        internal BlacksiteBehaviour SelectedBehaviour => _behaviourSelector.Current;
        internal Vector3? BehaviourGoal => _behaviourGoal;

        // Frame-rate motors retain control, but still participate in every selection.
        // This does not tick a cabin, passage or recovery a second time.
        private void SelectExclusiveBehaviour(BlacksiteBehaviour behavior, string reason)
        {
            // #1017 S3 / #1020 S6: every caller is a mandatory single-bit commit followed
            // immediately by a return - the motors, the capture, the stun rungs and the monster
            // evade branch - so the behaviour and the reason are the caller's own and the brain
            // commits them on every such frame.
            Brain.CommitExclusive(behavior, reason);
        }

        private void CommitBehaviour(BlacksiteBehaviour behavior, string reason, Vector3? goal = null)
        {
            // #874 S1.2: an approach profile belongs to the order that issued it. Every decision
            // that resolves without executing an order - combat, an evade, a traversal, a hold -
            // releases it here, so no action can leave a reach contract on the agent.
            if (_approachOrderFrame != Time.frameCount) Navigator.ReleaseApproachProfile();
            bool changed = _behaviourSelector.Current != behavior;
            _behaviourSelector.Commit(behavior);
            // #874 S4.0b: the contact-response pair an accepted replacement closes.
            if (changed) BlacksitePerceptionDiagnostics.NoteResponse(MemberIndex, NetworkObjectId, behavior);
            // #1233 S6 (rule 2): a motor or an interaction owns the navigator's intended goal, so
            // that is the goal it reports. Combat reports the body it is fighting (CombatGoal),
            // never the destination the interaction it interrupted had issued. #1305 R2: none is none.
            _behaviourGoal = goal ?? Navigator.IntendedGoal;
            if (changed && BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.Log("Behaviour", $"member={MemberIndex}, selected={behavior}, goal={_behaviourGoal?.ToString() ?? "none"}, reason={reason}.");
            PublishIntent(behavior, reason);
        }

        private bool TryResolveBehaviourOrder(out BlacksiteSquadDirector director, out BlacksiteMemberOrder order)
        {
            order = default;
            return TryGetSquadDirector(out director) && director.TryGetMemberOrder(this, out order);
        }

        // Pass 8 H (C1): a sweep or a search-last-known order is a squad order like any other and
        // runs the same executor (ExecuteSquadOrder); AutonomousSearch is folded into SquadOrder.
        private static BlacksiteBehaviour ClassifyOrder(BlacksiteMemberOrder order, bool protectedCarrier)
            => protectedCarrier || order.IsInteractionOperator
                ? BlacksiteBehaviour.ObjectiveJob
                : BlacksiteBehaviour.SquadOrder;

        private bool TryExecuteSelectedCombat(out string reason)
        {
            reason = "combat-contact";
            // #1305 H (coordinator Q2, C1): the job's interaction is interrupted once, by the FIRST
            // ACCEPTED Combat decision of a stretch (the selection was not Combat). An attempt refused
            // below interrupts nothing, so an unreachable contact cannot end the job's task on every
            // decision (VerifyLive2), and an ongoing fight never interrupts its own route's steps.
            bool stretchBegins = _behaviourSelector.Current != BlacksiteBehaviour.Combat;
            // #1233 S6 (rule 5): a target found here has passed ContactEngageable inside the
            // acquisition - a body this weapon can hit now, and evidence of threat - so the
            // interrupt below is the threat-response decision authorising Combat, not a sighting.
            if (TryFindCombatTarget(out var playerTarget, out var enemyTarget))
            {
                PublishCombatFormationSlot();
                if (playerTarget != null) EngagePlayerTarget(playerTarget);
                else if (enemyTarget != null) EngageEnemyTarget(enemyTarget);
                // #874 S7.2, M3 of the 2026-09-09 run: a HELD lane - the ring is merely occupied by
                // squadmates this think - keeps the Combat bit under a named stance rather than
                // handing the member to its squad order for 0.1 to 0.3 s at a time. A crowd is a
                // predicate this member can wait out; the bodies resolve it.
                // #1019 S5: the stances are read as OUTCOMES. Holding is HoldingLane, Reloading and
                // Recovering; OutOfBand is ApproachBlocked and OutOfRange.
                // #1233 S6 (rule 5): an OutOfBand readiness - no ground for the ring, or no route to
                // the contact at all - is a body the weapon cannot reach THIS think, and Combat
                // yields the think at once: the duty board takes it and the next think asks the
                // geometry again. The three-think counter and its 6 s HoldPosition window that
                // stood here (#1010 A2, #1019 S5) were a budget on that question, and a scored
                // stance for a contact the member had already been told it could not reach.
                if (UsesBaton)
                {
                    switch (BlacksiteCombatPolicy.Outcome(LastBatonReadiness))
                    {
                        case BlacksiteEngagementOutcome.Holding:
                            reason = "combat-holding-lane";
                            break;
                        case BlacksiteEngagementOutcome.OutOfBand:
                            reason = "combat-blocked-approach";
                            _combatController.ClearFormationSlot();
                            _combatController.ContinueWithoutContact();
                            return false;
                    }
                }
                // #1271 S3a: a rifle yields the same way on ApproachBlocked alone - no cover, no
                // approach, and the contact unseen or out of range. OutOfRange and LostSight stay.
                else if (_combatController.LastFireReadiness == BlacksiteFireReadiness.ApproachBlocked)
                {
                    reason = "combat-blocked-approach";
                    _combatController.ClearFormationSlot();
                    return false;
                }
                InterruptJobInteraction(stretchBegins);
                return true;
            }
            // #988 B3: Combat stays an available candidate while a monster contact is live inside
            // the radius, even when nobody can currently see it.
            //
            // `accepted=False, reason=combat-memory` is the flip that killed member 0 of the
            // 2026-09-12 run: it entered Combat at t=381.21 on a crawler 11.9 m away and was handed
            // back to AutonomousSearch 1.5 s later at t=382.69 (L10781-10783), because for a baton
            // member ContinueWithoutContact clears the target and reports the swing lock and
            // nothing else. It then stood still, facing away (`visible=False, dot=-0.83`,
            // `movedMetresLast5s=0.00`) and was bitten to death from t=384.00.
            //
            // A live contact this member has been ordered onto - or one it is holding and has been
            // hit by inside DamageCombatLockSeconds - is work, and the answer is the lane sweep B3
            // already owns rather than a hold: the member converges, draws on the way in, and the
            // ordinary engagement takes it back the instant the contact is visible again.
            if (TryConvergeOnOrderedContact(out reason))
            {
                PublishCombatFormationSlot();
                InterruptJobInteraction(stretchBegins);
                return true;
            }
            _combatController.ClearFormationSlot();
            reason = "combat-memory";
            if (!_combatController.ContinueWithoutContact()) return false;
            InterruptJobInteraction(stretchBegins);
            return true;
        }

        // #1305 H (C1): after the engagement, which has already asked for combat's goal. A live task
        // answers that ask with its own step (InteractionCoordinator.TryHandleDestination), so the
        // held request is forgotten and combat's goal asked for again - the baton close-in or the
        // rifle's committed move (VerifyLive5). With neither, the task's path goes: a combat hold only
        // stops the agent, which would walk it when the hold ends (VerifyLive6).
        private void InterruptJobInteraction(bool stretchBegins)
        {
            if (!stretchBegins || _interactionCoordinator == null) return;
            _interactionCoordinator.Interrupt("combat contact");
            Navigator.RequestImmediateDestinationRefresh();
            if (_hasBatonApproachGoal) Navigator.SetMeleeApproachDestination(_batonApproachGoal);
            else if (_combatController?.ReissueCommittedMove() != true) Navigator.ClearPath();
        }

        // #1233 S6 (rule 5): the live goal the Combat behaviour reports - the body it is fighting,
        // resolved from the controller's own aim point on this frame - and nothing when there is
        // none. CommitBehaviour used to default Combat to Navigator.IntendedGoal, which at 1158.72
        // of round 3 (2026-09-27) was the interrupted elevator's destination 38 m from the monster.
        private Vector3? CombatGoal
        {
            get
            {
                if (_combatController == null) return null;
                Vector3 aim = _combatController.GetCurrentAimPoint();
                return aim != Vector3.zero ? aim : (Vector3?)null;
            }
        }

        // #988 B3: the unseen-contact branch of Combat. It answers only for a baton raider with a
        // live monster contact that is either an active squad order or one this member has just
        // been hurt by; everything else falls through to the unchanged ContinueWithoutContact.
        private bool TryConvergeOnOrderedContact(out string reason)
        {
            reason = "combat-alert-order";
            if (!UsesBaton || !AllowsCombatLifecycle) return false;
            if (!HasMonsterAlertOrder && !HasRecentDamage) return false;
            if (!TryGetLiveMonsterContact(out EnemyAI enemy, out float distance)) return false;
            if (distance > BlacksiteCombatPolicy.MonsterAlertOrderRadius) return false;
            // #1233 S6 (rule 5): an order is knowledge plus an instruction to close, and closing is
            // Combat. The same gate the acquisition applies: no body the baton can hit, or no
            // evidence the thing is a threat, and the member keeps the order as a track and stays
            // at its work.
            if (!ContactEngageable(enemy)) return false;
            var readiness = ConvergeOnOrderedContact(enemy);
            LastBatonReadiness = readiness;
            // #1019 S5: Closing is Repositioning alone and Holding is the three stance values, of
            // which ConvergeOnOrderedContact returns only HoldingLane. Same two arms, same answers.
            switch (BlacksiteCombatPolicy.Outcome(readiness))
            {
                case BlacksiteEngagementOutcome.Closing:
                    return true;
                case BlacksiteEngagementOutcome.Holding:
                    reason = "combat-order-holding-lane";
                    return true;
                default:
                    // Hands busy, no pose, or no route at all: the order cannot be executed, so the
                    // decision goes back to the loop exactly as it did before this branch existed.
                    ReleaseBatonApproach();
                    return false;
            }
        }
    }
}
