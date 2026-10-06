namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteLifecycleState : byte { Active, Traversing, Boarded, Captured, Dead, Consumed }

    // #874 S2.4: one effective lifecycle state for a soldier. Body, weapon, lights, scan node,
    // colliders and combat eligibility all read this, so a one-time renderer toggle followed by a
    // per-frame equipment writer can no longer produce a gun-only actor, and a boarded soldier can
    // no longer advertise a Ready weapon over a retained contact the aim layer is forbidden to face.
    internal static class BlacksiteLifecycle
    {
        internal static BlacksiteLifecycleState Resolve(bool consumed, bool dead, bool captured, bool boarded, bool traversing)
            => consumed ? BlacksiteLifecycleState.Consumed
            : dead ? BlacksiteLifecycleState.Dead
            : captured ? BlacksiteLifecycleState.Captured
            : boarded ? BlacksiteLifecycleState.Boarded
            : traversing ? BlacksiteLifecycleState.Traversing
            : BlacksiteLifecycleState.Active;

        // #874 (2026-09-20): only a CONSUMED actor is out of the world. A corpse stays visible, a
        // captured body is carried by its monster, and a BOARDED one is a passenger: he walked up
        // the ladder, he is standing in the hold, and the player watching the transport lift off
        // should see the men it is taking home. Hiding him was also how the round 3 departure lost
        // the witness - a hidden escort is one step from a despawned escort, and custody broke
        // against exactly that. What a passenger does NOT get back is his job: AllowsCombat and
        // HoldsWeapon below still refuse Boarded, so he rides with his weapon stowed and no
        // contact, and BlacksiteShipController's passenger registry owns his transform and tells
        // the departure probes that his colliders are cargo rather than an obstruction.
        internal static bool Hides(BlacksiteLifecycleState state)
            => state == BlacksiteLifecycleState.Consumed;

        // Nothing brings a dead or consumed actor's presentation back.
        internal static bool Restores(BlacksiteLifecycleState state)
            => state != BlacksiteLifecycleState.Dead && state != BlacksiteLifecycleState.Consumed;

        internal static bool HasBodyPresence(BlacksiteLifecycleState state)
            => state == BlacksiteLifecycleState.Active || state == BlacksiteLifecycleState.Traversing;

        internal static bool HasHitPresence(BlacksiteLifecycleState state)
            => !Hides(state) && state != BlacksiteLifecycleState.Dead;

        internal static bool HasLight(BlacksiteLifecycleState state) => HasHitPresence(state);

        // Traversal is ordinary life: the existing hand-ownership, passage and interaction gates
        // already decide what a climbing or rappelling member may do.
        internal static bool AllowsCombat(BlacksiteLifecycleState state)
            => state == BlacksiteLifecycleState.Active || state == BlacksiteLifecycleState.Traversing;

        internal static bool HoldsWeapon(BlacksiteLifecycleState state) => AllowsCombat(state);
    }
}
