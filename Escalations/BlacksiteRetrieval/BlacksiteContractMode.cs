using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #941 S11 U3: how a Blacksite raid behaves under a given moon contract. One table, one
    /// source. It answers both questions that used to be answered separately: may Blacksite
    /// schedule against this contract at all (<see cref="Excluded"/>), and which objective
    /// adapter <c>BlacksiteObjectiveManifestController.Discover()</c> runs.
    /// </summary>
    internal enum BlacksiteContractMode : byte
    {
        /// <summary>Blacksite never schedules against this contract.</summary>
        Excluded = 0,

        /// <summary>
        /// The bounded fallback raid: the apparatus when an eligible one is in the facility, else
        /// facility scrap. #1075: this is also the mode of a no-contract (Salvage) round - the
        /// contract the crew reads as "Salvage" gets the same salvage raid every unadapted contract
        /// gets, so it needs no arm of its own in <c>BlacksiteObjectiveManifestController.Discover()</c>.
        /// </summary>
        Default = 1,

        /// <summary>Payload: the active cart is the objective.</summary>
        PayloadCart = 2,

        /// <summary>Shadow Raid: the embedded hard drive is the objective.</summary>
        HardDrive = 3,

        /// <summary>Whistleblower: the witness is the objective while the escort is switched on; off, the default raid.</summary>
        WitnessEscort = 4
    }

    /// <summary>#874 Q10: the substitute work a raid with no contract objective invents for itself.</summary>
    internal enum BlacksiteFallbackMission : byte
    {
        /// <summary>The raid has a contract objective on this moon and takes nothing else.</summary>
        None = 0,
        /// <summary>An eligible apparatus is in the facility.</summary>
        Apparatus = 1,
        /// <summary>Bounded facility scrap.</summary>
        Salvage = 2
    }

    internal static class BlacksiteContractModes
    {
        /// <summary>
        /// #941 S11 U3: Lawson's matrix of 2026-09-10. Payload is the cart, Shadow Raid is the
        /// drive, Whistleblower is the escort seam, everything else is the default raid, and
        /// Containment Breach is never Blacksite (a facility already full of a contained thing
        /// does not also get a corporate retrieval squad).
        ///
        /// #1075: "no contract" is the one arm that changed. S11 U3 excluded it because run 2 day 2
        /// of the 2026-09-10 test scheduled a raid with contract=None (#945) that the old
        /// System.Enum.IsDefined gate admitted; the 2026-09-15 Titan landing then showed what that
        /// blanket exclusion costs - a salvage day on which Blacksite can never be the escalation
        /// ("Selection gate rejected contract None; force=True, weight=0"). Lawson's decision of
        /// 2026-09-15 is that a salvage round is a round: the squad raids it under the same bounded
        /// fallback every unadapted contract gets, so None maps to Default here.
        ///
        /// What #945 actually needed is not an exclusion but a distinction, because None is ALSO
        /// what any peer reports before the round's contract is resolved. That distinction lives in
        /// <see cref="BlacksiteRetrievalRules.IsContractReadyToSchedule"/>, which pairs this table
        /// with a readiness signal, and never in this table - a mode is a property of a contract,
        /// not of how much a peer has been told.
        ///
        /// Every defined <see cref="MoonContractType"/> member has its own arm on purpose. Adding
        /// a contract without deciding its mode is caught by BlacksiteTierTwoThreeRegression,
        /// which asserts that its expected-mode table covers Enum.GetValues exactly. The trailing
        /// discard arm is not a mode assignment: it fails closed for a value that is not a defined
        /// member at all (a malformed or future network value), so an unknown contract can never
        /// schedule a raid.
        /// </summary>
        internal static BlacksiteContractMode Mode(MoonContractType contract) => contract switch
        {
            // #1075: the no-contract (Salvage) round runs the bounded facility-salvage raid.
            MoonContractType.None => BlacksiteContractMode.Default,
            MoonContractType.ContainmentBreach => BlacksiteContractMode.Excluded,

            MoonContractType.Payload => BlacksiteContractMode.PayloadCart,
            MoonContractType.ShadowRaid => BlacksiteContractMode.HardDrive,
            MoonContractType.Whistleblower => BlacksiteContractMode.WitnessEscort,

            MoonContractType.BlackoutAudit => BlacksiteContractMode.Default,
            MoonContractType.Survey => BlacksiteContractMode.Default,
            MoonContractType.Defuse => BlacksiteContractMode.Default,
            MoonContractType.PestControl => BlacksiteContractMode.Default,
            MoonContractType.WasteDisposal => BlacksiteContractMode.Default,
            MoonContractType.LguDataRetrieval => BlacksiteContractMode.Default,
            MoonContractType.LguExtermination => BlacksiteContractMode.Default,
            MoonContractType.LguExtraction => BlacksiteContractMode.Default,
            MoonContractType.LguExorcism => BlacksiteContractMode.Default,
            MoonContractType.LguDefusal => BlacksiteContractMode.Default,

            _ => BlacksiteContractMode.Excluded
        };

        /// <summary>
        /// #874 Q10 (Lawson, 2026-09-24, decision 3): whether a raid under this mode has a contract
        /// objective of its own on the moon. The Whistleblower's witness is the objective only
        /// while the escort setting is on; off, the setting's own promise is that the squad raids
        /// scrap, so that mode then has none.
        /// </summary>
        internal static bool HasPrimaryObjective(BlacksiteContractMode mode, bool witnessEscortEnabled) => mode switch
        {
            BlacksiteContractMode.PayloadCart => true,
            BlacksiteContractMode.HardDrive => true,
            BlacksiteContractMode.WitnessEscort => witnessEscortEnabled,
            _ => false
        };

        /// <summary>
        /// #874 Q10: a raid with a primary never takes substitute work - the apparatus above all,
        /// which on 2026-09-23 round 3 was carried off in place of the witness. Without one, an
        /// eligible apparatus is the fallback every time (the seeded one-in-four coin is gone),
        /// else salvage.
        /// </summary>
        internal static BlacksiteFallbackMission Fallback(bool hasPrimary, bool apparatusAvailable)
            => hasPrimary ? BlacksiteFallbackMission.None
                : apparatusAvailable ? BlacksiteFallbackMission.Apparatus
                : BlacksiteFallbackMission.Salvage;
    }
}
