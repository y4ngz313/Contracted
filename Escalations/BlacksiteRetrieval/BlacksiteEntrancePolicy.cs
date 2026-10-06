namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteEntrancePolicy
    {
        // Authored entrance points may be inset into the building collision shell. Leave
        // room for the soldier capsule and the eroded moon navmesh to stand outside
        // while using the door (Adamance stops about 1.7m from the authored point).
        internal const float UseRange = 2f;
        internal const float HeightTolerance = 1.25f;
        internal const float RouteArrivalRange = UseRange - .1f;

        // #874 plan B: how far from the door BlacksiteLandingGeometry.ResolveEntranceApproach may
        // bind navigation when the door's own anchor is off the navmesh. On Vertigo the anchor sits
        // in the door recess and the nearest baked navigation is 1.57 m outward on the same flat
        // terrain, 7 cm past the old 1.5 m bind, so every landing route was refused by a constant.
        //
        // Derivation: it is the soldiers' entrance hand-off, BlacksiteArrivalPolicy
        // .EntranceHandoffDistance (3.0 m), by construction. A route
        // or a member that reaches the bound navigation point is then already inside the distance at
        // which the leg hands the doorway to the paired entrance interaction, which walks the rest, so the
        // metres off the navmesh always have an owner, and they are exactly the metres the resolver
        // proves with the physical tail (RouteCheck's 0.15 m swept steps and 0.32 m step limit,
        // arriving within RouteArrivalRange and HeightTolerance). A wider bound would bind navigation
        // a member reaches and then stands on outside the hand-off with nobody owning the rest. It is
        // inside vanilla's RoundManager.GetNavMeshPosition default of 5 m. The radius only bounds the
        // search; the physical tail proof is what accepts a point.
        internal const float ApproachSearchRadius = BlacksiteArrivalPolicy.EntranceHandoffDistance;

        /// <summary>
        /// #874 N6: the up-front door choice weighs the objective. On Vow (seed 86393907, log
        /// 10428/10542) the Payload cart sat 10.9 m from the fire exit, in a NavMesh pocket 7 m
        /// below the caves the main door and the mineshaft car lead to. The squad kept the main
        /// door and never reached it. The main door is kept whenever it can walk (or ride) to the
        /// objective: it is the door the landing proved. A door that reaches the objective replaces
        /// the main door only when the main door cannot reach it or its endpoint refuses. A door
        /// that does not reach it never replaces a usable main door, because swapping one dead end
        /// for another only costs the landing's own route proof.
        /// </summary>
        internal static bool PrefersObjectiveDoor(bool mainUsable, bool mainReachesObjective, bool alternateReachesObjective)
            => alternateReachesObjective && (!mainUsable || !mainReachesObjective);
    }
}
