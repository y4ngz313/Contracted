namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// The contract facts the host publishes to the report. Kept in its own engine-free file so
    /// the wording that reads them (<see cref="PerformanceReportWording"/>) can be compiled and
    /// run outside Unity by tests/PerformanceReportRegression; the publisher beside it in
    /// <see cref="ContractProgressionEvents"/> needs UnityEngine and cannot be (#1202).
    /// </summary>
    public enum ContractProgressionEventKind
    {
        DefuseCorrectWire = 1,
        DefuseCorrectCode = 2,
        PayloadPilotSeconds = 3,
        WhistleblowerSighted = 4,
        WhistleblowerNeutralized = 5,
        SurveyDronePlaced = 6,
        BlackoutAuditRestored = 7,
        ShadowRaidDrillAttached = 8,
        ShadowRaidDrillCompleted = 9,
        PestControlCompleted = 10,
        WasteDisposalCompleted = 11,
        ContractCompleted = 12,
        ContractFailed = 13,
        // #406: additive kinds. SchemaVersion stays 1 - the wire format did not change and
        // the consumer's switch has a tolerant default, so an older consumer simply ignores
        // a kind it does not know rather than mis-reading the payload.
        ContainmentBreachWaveSurvived = 14,
        ShadowRaidHardDriveRecovered = 15,
        WasteDisposalRepaired = 16,
        PestControlCapture = 17,
        // #794: one credit per restored Blackout Audit breaker zone, to the crew member who
        // flipped it, so a partial restore on a failed round still reads back on the report
        // and can be paid. The full-restore BlackoutAuditRestored still follows the final
        // zone. Additive like the #406 kinds: SchemaVersion stays 1 and an older consumer's
        // switch ignores it.
        BlackoutBreakerRestored = 18,
        // #805: the ten steps of the five Lategame Upgrades-backed directives. Additive like
        // every kind since #406 - SchemaVersion stays 1, the wire layout is unchanged, and an
        // older consumer's tolerant switch ignores a kind it does not know.
        //
        // All ten are published by the host only, off the same LGU ClientRpc postfixes that
        // latch the objective card (#804). Those bodies carry the object, never the player who
        // acted on it, so nine of the ten are crew-wide payouts and say so in their ids; only
        // the extermination cull has an attributable actor, through the enemy-hit ledger the
        // report already keeps.
        LguLaptopLogin = 19,
        LguDataRecovered = 20,
        LguHoardingBugExterminated = 21,
        LguNestDestroyed = 22,
        LguScavengerTreated = 23,
        LguScavengerRecovered = 24,
        LguRitualItemPlaced = 25,
        LguDemonIdentified = 26,
        LguWireCut = 27,
        LguDeviceDefused = 28,
        // #1076: a Blacksite ship left the moon with a contract objective aboard. Additive like
        // every kind since #406 - SchemaVersion stays 1, the wire layout is unchanged, and an
        // older consumer's tolerant switch ignores it.
        //
        // This is NOT a sub-objective credit and deliberately has no case in
        // PerformanceReportWording.DescribeAct, so the report ledger drops it on the empty
        // label: the round's failure already reaches the report as ContractFailed from
        // PublishContractResolved. It rides this channel purely because this channel is the only
        // host->client contract fact that does not need a live NetworkObject - see the publisher.
        BlacksiteObjectiveDeparted = 29,
        // #1195: a side objective (Wesley's Interiors secondaries; #1198's trials later) - one per
        // step reached, one when it completes, one when its reward is claimed. Additive like every
        // kind since #406: SchemaVersion stays 1 and an older consumer's tolerant switch ignores
        // them. They are not contract events: ContractId and ContractType are empty, SourceLabel
        // is the objective's player-facing name and Count is the step. A step is published only
        // the first time the count reaches it in a round, so a count that falls and climbs again
        // (Toy Store's mask statues) cannot credit the same step twice.
        SideObjectiveStepCompleted = 30,
        SideObjectiveCompleted = 31,
        SideObjectiveRewardClaimed = 32
    }
}
