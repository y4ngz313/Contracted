namespace Y4NGZCompany.Contracts._Shared
{
    internal enum MoonContractType
    {
        None = 0,
        Whistleblower = 1,
        BlackoutAudit = 4,
        ShadowRaid = 5,
        ContainmentBreach = 6,
        Survey = 7,
        Defuse = 8,
        Payload = 9,
        PestControl = 12,
        WasteDisposal = 13,

        // #803: Lategame Upgrades-backed contracts. Contracted rolls them through its own
        // rotation and hands the assignment to LGU, which spawns and plays them. Ids 2, 3,
        // 10 and 11 are retired and must never be reused.
        LguDataRetrieval = 14,
        LguExtermination = 15,
        LguExtraction = 16,
        LguExorcism = 17,
        LguDefusal = 18
    }
}
