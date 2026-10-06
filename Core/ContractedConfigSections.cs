namespace Y4NGZCompany.Core
{
    /// <summary>
    /// The one table of Contracted's player-facing config section names (#694).
    /// Sections are plain nouns with no numeric ordering prefixes; risk-tier
    /// variation lives inside tier-table values, not in per-tier sections.
    /// </summary>
    public static class ContractedConfigSections
    {
        public const string Contracts = "Contracts";
        public const string Quota = "Quota";
        // #1197/#1343: client-local Locate pity timer and first-sight accent.
        public const string ObjectiveHints = "Objective Hints";
        public const string ContractHudAudio = "Contract HUD Audio";

        public const string BlackoutAudit = "Blackout Audit";
        public const string Survey = "Survey";
        public const string Defuse = "Defuse";
        public const string ContainmentBreach = "Containment Breach";
        public const string ShadowRaid = "Shadow Raid";
        public const string Payload = "Payload";
        public const string Whistleblower = "Whistleblower";
        public const string PestControl = "Pest Control";
        public const string WasteDisposal = "Waste Disposal";

        // #803: the Lategame Upgrades-backed directives. Each gets its own plain section so its
        // selection controls sit where every other contract's do.
        public const string DataRetrieval = "Data Retrieval";
        public const string Extermination = "Extermination";
        public const string Extraction = "Extraction";
        public const string Exorcism = "Exorcism";
        public const string Defusal = "Defusal";

        public const string Escalations = "Escalations";
        public const string TheStranger = "The Stranger";

        public const string CompanyStashLoot = "Company Stash Loot";
        public const string Diagnostics = "Diagnostics";
    }
}
