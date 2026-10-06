using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteRetrievalRules
    {
        // #941 S11 U3: eligibility is the contract mode table, not "is this a defined enum value".
        // System.Enum.IsDefined admitted None, ContainmentBreach, PestControl, WasteDisposal and
        // every Lgu* contract. The name and signature are unchanged so all eight call sites read
        // the table without knowing about it.
        // #1075: this asks the table one question only - may this CONTRACT be raided. It is no
        // longer also "has this peer been told the contract", because None is now a raidable
        // contract; callers that need that second answer use IsContractReadyToSchedule below.
        internal static bool IsContractEligible(MoonContractType contract)
        {
            return BlacksiteContractModes.Mode(contract) != BlacksiteContractMode.Excluded;
        }

        /// <summary>
        /// #1075: the question a scheduler actually has, which eligibility alone cannot answer.
        /// <see cref="MoonContractType.None"/> now means two different things. As a CONTRACT it is
        /// the salvage round the squad may raid (mode table: Default). As a READING it is also what
        /// a peer reports when it has not been told the round's contract yet -
        /// MoonContractState.GetContractForLevel returns None for a client whose host assignment
        /// snapshot has not arrived, and for anyone asked before StartOfRound has a current level.
        /// Only the pair (value, resolved) separates "this round has no contract" from "nobody has
        /// said yet", so a gate that may commit a raid asks for both.
        ///
        /// Every contract other than None is unaffected: a non-None reading is by construction one
        /// this peer was given, so the resolved flag only ever adds a requirement to None.
        /// </summary>
        internal static bool IsContractReadyToSchedule(MoonContractType contract, bool contractResolved)
        {
            return IsContractEligible(contract)
                   && (contractResolved || contract != MoonContractType.None);
        }

        internal static bool UsesEmbeddedHardDrive(MoonContractType contract)
        {
            return contract == MoonContractType.ShadowRaid;
        }

        internal static bool CreatesEventHardDrive(MoonContractType contract)
        {
            return false; // All supported adapters now use existing objective identities.
        }

        internal static bool CanSchedule(MoonContractType contract, bool moonEligible, bool enabled)
        {
            return enabled && moonEligible && IsContractEligible(contract);
        }

        /// <summary>#1305 C1 (D6): a Payload raid is scheduled only once its cart has network-spawned; it never skips.</summary>
        internal static bool WaitsForPayloadCart(MoonContractType contract, bool cartSpawned)
            => contract == MoonContractType.Payload && !cartSpawned;

        internal static bool ShouldCleanupUntouchedEventDrive(
            bool eventCreated,
            bool isHeld,
            bool isPocketed,
            bool hasBeenHeld,
            bool heldByPlayerOnServer,
            bool isInShipRoom,
            bool isInElevator,
            bool soldierHeld)
        {
            return eventCreated
                   && !isHeld
                   && !isPocketed
                   && !hasBeenHeld
                   && !heldByPlayerOnServer
                   && !isInShipRoom
                   && !isInElevator
                   && !soldierHeld;
        }
    }
}
