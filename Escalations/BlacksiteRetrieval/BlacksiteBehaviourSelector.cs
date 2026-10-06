namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteBehaviour
    {
        None, IdleWithGoal, SquadOrder, ObjectiveJob, Combat,
        Evade, Withdrawal, EntranceTraversal, CommittedTraversal, Hold
    }

    // #874 S7.2/S7.3: how close the member's OWN exact track is. Unity-free on purpose, and the
    // single perception input the decision receives - it is sampled once per decision and the
    // rule never re-runs vision.
    // MeleeReach means inside BlacksiteCombatPolicy.BatonAttackRange, ThreatRadius inside
    // BlacksiteCombatPolicy.ThreatRadius, None everything else including an alert-only track.
    internal enum BlacksiteThreatProximity { None, ThreatRadius, MeleeReach }

    // #1020 S6: Unity-free RETAINED-BEHAVIOUR STATE, and since #1305 pass 8 H (C2, D5) the brain's
    // one decision rule. The utility table, its continuity bonus, the 1.5 s Combat floor and the
    // damage lock are deleted (R1: the job owns the member; a threat is a constraint on its route
    // and fire). BlacksiteSoldierBrain writes Current through CommitBehaviour on every committed
    // decision; death and despawn clear it through Reset.
    internal sealed class BlacksiteBehaviourSelector
    {
        /// <summary>The brain's attempt bound: every attempt rejects one kind or resolves the duty once.</summary>
        internal const int CandidateCount = (int)BlacksiteBehaviour.Hold;
        internal BlacksiteBehaviour Current { get; private set; }
        internal static uint Bit(BlacksiteBehaviour behavior) => 1U << (int)behavior;

        /// <summary>
        /// #1305 H (C2) <c>inBand</c>: the threat resolution answered a fighting intent at a contact
        /// close enough to take the member from its job - MeleeReach, or ThreatRadius for a member
        /// that is not the protected carrier (a carrier keeps travelling until the contact is on it).
        /// A rescue is in band wherever its captor is (TryGetRescueContact already bounds it by
        /// RescueRadius), exactly where the deleted table always ranked it.
        /// </summary>
        internal static bool InBand(BlacksiteThreatIntent intent, BlacksiteThreatProximity threat, bool protectedCarrier)
            => intent == BlacksiteThreatIntent.Rescue
            || (intent == BlacksiteThreatIntent.Engage
                && (threat == BlacksiteThreatProximity.MeleeReach
                    || (threat == BlacksiteThreatProximity.ThreatRadius && !protectedCarrier)));

        /// <summary>
        /// #1305 H (VerifyLive B1) <c>fighting</c>: the resolution answered a fighting intent. The
        /// deleted table scored Combat 0 for Ignore, Evade, Withdraw and Watch, so the job always beat
        /// it; combat memory is offered only to a fighting intent for the same reason.
        /// </summary>
        internal static bool Fighting(BlacksiteThreatIntent intent)
            => intent == BlacksiteThreatIntent.Engage || intent == BlacksiteThreatIntent.Rescue;

        /// <summary>
        /// #1305 H (C2, D2) <c>attacker</c>: a captor this member rescues, in band, or the player who
        /// just hit this member (BlacksiteCombatController.ImmediatePlayerAttackerProximity) in band
        /// himself - never another contact that happens to be in band inside the attacker window.
        /// Only this preempts an entrance use or a brain-level committed crossing.
        /// </summary>
        internal static bool Attacker(bool inBand, BlacksiteThreatIntent intent,
            BlacksiteThreatProximity playerAttacker, bool protectedCarrier)
            => (inBand && intent == BlacksiteThreatIntent.Rescue)
            || InBand(BlacksiteThreatIntent.Engage, playerAttacker, protectedCarrier);

        /// <summary>
        /// #1305 H (C2): the fixed decision rule over <paramref name="available"/> (the brain's mask
        /// with this decision's executor refusals removed). First match wins; the brain runs its
        /// executor and, on a refusal, masks the kind out and asks again. No numbers, no
        /// continuity, no floor, no damage lock. Combat is a mode of the job: an in-band contact
        /// enters it and the controller dropping its engagement (Brain.Continue) ends it.
        /// IdleWithGoal is the total last answer - the brain resolves the duty board when it first
        /// gets it and asks again with the job's kind.
        /// </summary>
        internal static BlacksiteBehaviour Decide(uint available, bool inBand, bool attacker, bool protectedCarrier, bool fighting)
        {
            bool combat = Has(available, BlacksiteBehaviour.Combat);
            if (attacker && combat) return BlacksiteBehaviour.Combat;
            if (Has(available, BlacksiteBehaviour.CommittedTraversal)) return BlacksiteBehaviour.CommittedTraversal;
            if (Has(available, BlacksiteBehaviour.EntranceTraversal)) return BlacksiteBehaviour.EntranceTraversal;
            if (inBand && combat) return BlacksiteBehaviour.Combat;
            if (Has(available, BlacksiteBehaviour.Withdrawal)) return BlacksiteBehaviour.Withdrawal;
            if (protectedCarrier && Has(available, BlacksiteBehaviour.ObjectiveJob)) return BlacksiteBehaviour.ObjectiveJob;
            // Combat without an in-band contact: memory or an ordered convergence, for a fighting
            // intent only; its executor declines when nothing remains.
            if (combat && fighting) return BlacksiteBehaviour.Combat;
            if (Has(available, BlacksiteBehaviour.ObjectiveJob)) return BlacksiteBehaviour.ObjectiveJob;
            if (Has(available, BlacksiteBehaviour.SquadOrder)) return BlacksiteBehaviour.SquadOrder;
            return BlacksiteBehaviour.IdleWithGoal;
        }

        private static bool Has(uint available, BlacksiteBehaviour behaviour) => (available & Bit(behaviour)) != 0;

        internal void Commit(BlacksiteBehaviour behavior) => Current = behavior;

        // Death and despawn: a corpse holds no decision.
        internal void Reset() => Current = BlacksiteBehaviour.None;
    }
}
