namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Compatibility facade retained for the existing round and soldier call sites. The lease
    // (BlacksiteVanillaMonsters) drives every monster motor; the registry keeps the passive-acquire
    // ledger of the native IVisibleThreat consumers.
    internal static class BlacksiteMonsterTargeting
    {
        internal static void Reset()
        {
            BlacksiteVanillaMonsters.Reset();
            BlacksiteMonsterCombatAdapterRegistry.Reset();
        }

        internal static void ForceAggro(EnemyAI enemy, BlacksiteSoldierAI soldier, float seconds)
        {
            if (BlacksiteMonsterCapabilities.For(enemy).LeasedMotor) BlacksiteVanillaMonsters.ForceAggro(enemy, soldier);
            else BlacksiteMonsterCombatAdapterRegistry.ForceAggro(enemy, soldier, seconds);
        }

        internal static void SignalSoldierThreat(BlacksiteSoldierAI soldier, float radius, float seconds)
        {
            // Native listeners (including Eyeless Dog) receive the gunshot at its audible origin.
            if (BlacksiteVanillaMonsters.Authority() && BlacksiteVanillaMonsters.Living(soldier))
                RoundManager.Instance?.PlayAudibleNoise(soldier.transform.position, radius, 1f);
            BlacksiteMonsterCombatAdapterRegistry.SignalSoldierThreat(soldier, radius, seconds);
        }

        internal static void UpdateRuntime()
        {
            BlacksiteVanillaMonsters.UpdateRuntime();
            BlacksiteMonsterCombatAdapterRegistry.UpdateRuntime();
        }

        /// <summary>A member died (KillEnemy, host): a leased squad hunter passes to a teammate, the
        /// passive ledger and the sapsucker let go of him.</summary>
        internal static void NotifySoldierUnavailable(BlacksiteSoldierAI soldier)
        {
            BlacksiteVanillaMonsters.NotifySoldierUnavailable(soldier);
            BlacksiteMonsterCombatAdapterRegistry.NotifySoldierUnavailable(soldier);
        }
    }
}
