namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteCombatLifetimeAction { Preserve, Tick, Reset }
    internal sealed class BlacksiteCombatLifetime
    {
        private bool _active;
        internal BlacksiteCombatLifetimeAction Advance(bool server, bool missionAlive, bool soldiersAlive)
        {
            if (!server) return BlacksiteCombatLifetimeAction.Preserve;
            if (missionAlive || soldiersAlive) { _active = true; return BlacksiteCombatLifetimeAction.Tick; }
            if (!_active) return BlacksiteCombatLifetimeAction.Preserve;
            _active = false; return BlacksiteCombatLifetimeAction.Reset;
        }
        internal void Clear() => _active = false;
    }
}
