namespace Y4NGZCompany.Contracts._Shared
{
    internal readonly struct MoonContractDefinition
    {
        internal readonly string DisplayName;
        // Lawson's short name (#874): at most one word, up to 11 characters. The moons page
        // always shows it in the contract tag (#874 S6); DisplayName is for every other surface.
        internal readonly string ShortName;
        internal readonly string Title;
        internal readonly string Briefing;
        // What the crew actually has to do, sized for two wrapped terminal box lines.
        internal readonly string GameplayDescription;
        // One-line objective for the ship monitor.
        internal readonly string Objective;
        internal readonly string Art;

        internal MoonContractDefinition(
            string displayName,
            string shortName,
            string title,
            string briefing,
            string gameplayDescription,
            string objective,
            string art = "")
        {
            DisplayName = displayName;
            ShortName = shortName;
            Title = title;
            Briefing = briefing;
            GameplayDescription = gameplayDescription;
            Objective = objective;
            Art = art ?? string.Empty;
        }
    }
}
