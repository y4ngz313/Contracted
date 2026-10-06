namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // The navigator's bind numbers (#872, #874 S1), free of Unity types AND of every other
    // Blacksite type, so the headless gates can compile this file on its own and prove it. There is
    // no ladder left: #1293 R2, the agent is bound where the body stands or the member is stranded,
    // and what a member that stays blocked should do instead - a different goal, a regroup, the
    // fallback - is the squad planner's answer to the progress ledger's miss.
    internal static class BlacksiteRecoveryLadder
    {
        /// <summary>
        /// The navigator's one definition of "moved": half a metre, measured horizontally,
        /// because a member walks the floor it is standing on. The stillness clock resets on it.
        /// </summary>
        internal const float MinimumGoalDistance = .5f;

        /// <summary>
        /// #978: the one ground-navigation tolerance. It is how far a baked moon mesh is allowed to
        /// sit from the physics floor a body actually stands on: outdoor navigation is a height
        /// approximation and Vow's mesh moves about 15.3 cm after carving, so every place that
        /// publishes or accepts a ground navigation point has to answer with the same figure.
        /// </summary>
        internal const float GroundNavTolerance = .25f;
    }
}
