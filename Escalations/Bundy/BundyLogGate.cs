using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.Bundy
{
    // Job 1: which workstream a Bundy diagnostic belongs to. Categories exist so one subsystem can be
    // reopened at LogVerbosity 0 without the other five coming back with it — the run-5 log was 22,610
    // lines and the question being asked of it was usually about a single system.
    public enum BundyLogCategory
    {
        // State transitions, damage events, patch receipts, spawn/registration. Never demote anything
        // here above verbosity 0: every wrong read this project has made came from a log that did not
        // say plainly whether something fired.
        Core,
        Perception,
        Movement,
        Combat,
        Anim,
        Audio,
        Siege,
    }

    // Job 1: the single gate every Bundy diagnostic passes through.
    //
    // Two independent axes, both under the DebugLogs master:
    //   - LogVerbosity 0..3 is a global level. A line prints when its own verbosity is <= the level.
    //   - The Log* category flags are additive reopeners. A flag forces its whole workstream on
    //     regardless of level, so investigating one subsystem does not mean restoring all of them.
    //
    // Verbosity is a property of the *call site*, not of the category — a category-0 Combat line
    // (BundyBasicAttackDamage) and a category-2 Combat line (BundyBladeHitbox per-tick) coexist.
    internal static class BundyLogGate
    {
        internal const int DefaultVerbosity = 1;

        // #716 F3: the master alone, for writers that have no category/verbosity of their own -
        // BundyAIBase.WriteInfoLine is the one that mattered, because the direct callers of it were
        // bypassing this gate completely and logging through every finisher.
        internal static bool MasterEnabled
        {
            get
            {
                BundyConfig config = BundyPlugin.ModConfig;
                return config != null && (config.DebugLogs?.Value ?? false);
            }
        }

        internal static bool Allows(BundyLogCategory category, int verbosity)
        {
            BundyConfig config = BundyPlugin.ModConfig;
            if (config == null)
                return false;

            if (!(config.DebugLogs?.Value ?? false))
                return false;

            if (verbosity <= (config.LogVerbosity?.Value ?? DefaultVerbosity))
                return true;

            return IsCategoryReopened(config, category);
        }

        private static bool IsCategoryReopened(BundyConfig config, BundyLogCategory category)
        {
            switch (category)
            {
                // Core is already unconditional at verbosity 0, so it has no reopener of its own —
                // there is no level it is hidden at that a flag could bring it back from.
                case BundyLogCategory.Perception:
                    return config.LogPerception?.Value ?? false;
                case BundyLogCategory.Movement:
                    return config.LogMovement?.Value ?? false;
                case BundyLogCategory.Combat:
                    return config.LogCombat?.Value ?? false;
                case BundyLogCategory.Anim:
                    return config.LogAnim?.Value ?? false;
                case BundyLogCategory.Audio:
                    return config.LogAudio?.Value ?? false;
                case BundyLogCategory.Siege:
                    return config.LogSiege?.Value ?? false;
                default:
                    return false;
            }
        }
    }
}
