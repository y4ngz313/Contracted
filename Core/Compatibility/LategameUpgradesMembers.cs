namespace Y4NGZCompany.Core.Compatibility
{
    /// <summary>What kind of metadata row a <see cref="LguMemberRequirement"/> names.</summary>
    internal enum LguMemberKind
    {
        Type,
        Field,
        Property,
        Method,
    }

    /// <summary>
    /// One member of Lategame Upgrades that <see cref="LategameUpgradesCompat"/> resolves by
    /// reflection. The shim uses the names to build its one warning; the
    /// LategameUpgradesCompatRegression suite reads the same table straight out of this file
    /// and checks every row against the shipped MoreShipUpgrades.dll, so the two can never
    /// drift apart.
    /// </summary>
    internal sealed class LguMemberRequirement
    {
        internal LguMemberRequirement(
            string declaringType,
            string member,
            LguMemberKind kind,
            bool isStatic,
            string accessibility,
            int parameterCount,
            bool required,
            string usedFor)
        {
            DeclaringType = declaringType;
            Member = member;
            Kind = kind;
            IsStatic = isStatic;
            Accessibility = accessibility;
            ParameterCount = parameterCount;
            Required = required;
            UsedFor = usedFor;
        }

        /// <summary>Full name of the declaring type, or of the type itself when <see cref="Kind"/> is
        /// <see cref="LguMemberKind.Type"/>.</summary>
        internal string DeclaringType { get; }

        /// <summary>Member name; null for a type row.</summary>
        internal string Member { get; }

        internal LguMemberKind Kind { get; }

        internal bool IsStatic { get; }

        /// <summary>Expected CLR accessibility: "Public", "Assembly" (C# internal) or "Private".
        /// For a property this is the accessibility of its getter.</summary>
        internal string Accessibility { get; }

        /// <summary>Expected parameter count; -1 when the row is not a method.</summary>
        internal int ParameterCount { get; }

        /// <summary>False for members the shim can live without: their absence disables one
        /// feature (the shop suppression) instead of the whole bridge.</summary>
        internal bool Required { get; }

        internal string UsedFor { get; }

        internal string Describe()
        {
            return Kind == LguMemberKind.Type
                ? DeclaringType
                : DeclaringType + "." + Member;
        }
    }

    /// <summary>
    /// The single declaration of everything Contracted needs out of Lategame Upgrades
    /// (#802). Pure BCL on purpose so the regression suite can compile this exact file
    /// without dragging Unity or BepInEx into its closure.
    /// </summary>
    internal static class LategameUpgradesMembers
    {
        internal const string PluginGuid = "com.malco.lethalcompany.moreshipupgrades";
        internal const string AssemblyName = "MoreShipUpgrades";

        /// <summary>Version the member table was verified against.</summary>
        internal const string VerifiedAgainstVersion = "3.14.1";

        internal const string ContractManagerType = "MoreShipUpgrades.Managers.ContractManager";
        internal const string CommandParserType = "MoreShipUpgrades.Misc.CommandParser";
        internal const string ContractApplicationType = "MoreShipUpgrades.UI.Application.ContractApplication";

        // #804. The five contract objects LGU spawns on the moon, plus the configuration chain
        // that says how many bugs an extermination contract releases. Every one of these is
        // optional: losing one costs that contract's objective detail, never the assignment
        // bridge, which is why none of them takes part in LategameUpgradesCompat.IsUsable.
        internal const string DataPcType = "MoreShipUpgrades.UpgradeComponents.Items.Contracts.DataRetrieval.DataPCScript";
        internal const string BugNestType = "MoreShipUpgrades.UpgradeComponents.Items.Contracts.Exterminator.BugNestScript";
        internal const string ExtractPlayerType = "MoreShipUpgrades.UpgradeComponents.Items.Contracts.Extraction.ExtractPlayerScript";
        internal const string PentagramType = "MoreShipUpgrades.UpgradeComponents.Items.Contracts.Exorcism.PentagramScript";
        internal const string BombDefusalType = "MoreShipUpgrades.UpgradeComponents.Items.Contracts.BombDefusal.BombDefusalScript";

        internal const string UpgradeBusType = "MoreShipUpgrades.Managers.UpgradeBus";
        internal const string LategameConfigurationType = "MoreShipUpgrades.Configuration.LategameConfiguration";
        internal const string ContractsConfigurationType = "MoreShipUpgrades.Configuration.Contracts.ContractsConfiguration";
        internal const string ExterminationConfigurationType = "MoreShipUpgrades.Configuration.Contracts.ExterminationContractConfiguration";

        internal const string InstanceProperty = "Instance";
        internal const string ContractLevelField = "contractLevel";
        internal const string ContractTypeField = "contractType";
        internal const string LastContractIndexField = "lastContractIndex";
        internal const string SyncContractDetailsClientRpcMethod = "SyncContractDetailsClientRpc";
        internal const string ContractsField = "contracts";
        internal const string TryGetContractMethod = "TryGetContract";
        internal const string PurchaseRandomMoonContractMethod = "PurchaseRandomMoonContract";
        internal const string PurchaseSpecifiedMoonContractMethod = "PurchaseSpecifiedMoonContract";

        // #804. Objective-progress members. Every ClientRpc below executes on every peer, so a
        // Harmony postfix on it latches the same progress on every machine with no Contracted
        // networking of its own.
        internal const string BombOrderField = "bombOrder";
        internal const string SerialNumberField = "SerialNumber";

        internal const string InitiateGameClientRpcMethod = "InitiateGameClientRpc";
        internal const string ExitGameClientRpcMethod = "ExitGameClientRpc";
        internal const string DisableNestClientRpcMethod = "DisableNestClientRpc";
        internal const string HealScavClientRpcMethod = "HealScavClientRpc";
        internal const string SyncAltarClientRpcMethod = "SyncAltarClientRpc";
        internal const string SyncCurrentRitualClientRpcMethod = "SyncCurrentRitualClientRpc";
        internal const string FailRitualClientRpcMethod = "FailRitualClientRpc";
        internal const string SyncBombDetailsClientRpcMethod = "SyncBombDetailsClientRpc";
        internal const string BombStateChangeClientRpcMethod = "BombStateChangeClientRpc";

        internal const string CurrentRitualField = "currentRitual";
        internal const string DemonInstructionsField = "DemonInstructions";
        internal const string BombTimerField = "BombTimer";

        internal const string UpgradeBusInstanceProperty = "Instance";
        internal const string PluginConfigurationProperty = "PluginConfiguration";
        internal const string ContractsConfigurationProperty = "ContractsConfiguration";
        internal const string ExterminationConfigurationProperty = "ExterminationConfiguration";
        internal const string AmountSpawnsProperty = "AmountSpawns";

        /// <summary>Bugs an extermination contract releases when LGU's own count is unreadable.
        /// Matches the shipped default of <c>ExterminationContractConfiguration.AmountSpawns</c>.</summary>
        internal const int DefaultExterminationSpawnCount = 20;

        /// <summary>Wires an LGU bomb carries. Fixed at red/green/blue by
        /// <c>BombDefusalScript.Start</c>, which only shuffles the order.</summary>
        internal const int BombWireCount = 3;

        /// <summary>Items an exorcism ritual takes when the demon's own list is unreadable.
        /// Every entry in <c>PentagramScript.DemonInstructions</c> is three items long.</summary>
        internal const int DefaultRitualItemCount = 3;

        /// <summary>Value LGU stores in <see cref="ContractTypeField"/> and
        /// <see cref="ContractLevelField"/> when there is no contract.</summary>
        internal const string NoneValue = "None";

        /// <summary>Index LGU's sync RPC reads as "clear the contract".</summary>
        internal const int NoneIndex = -1;

        internal static readonly LguMemberRequirement[] All =
        {
            new LguMemberRequirement(ContractManagerType, null, LguMemberKind.Type, false, "Assembly", -1, true,
                "the contract singleton Contracted reads and writes"),
            new LguMemberRequirement(ContractManagerType, InstanceProperty, LguMemberKind.Property, true, "Assembly", -1, true,
                "reaching the live manager"),
            new LguMemberRequirement(ContractManagerType, ContractLevelField, LguMemberKind.Field, false, "Assembly", -1, true,
                "the moon the contract is on"),
            new LguMemberRequirement(ContractManagerType, ContractTypeField, LguMemberKind.Field, false, "Assembly", -1, true,
                "the contract type string"),
            new LguMemberRequirement(ContractManagerType, LastContractIndexField, LguMemberKind.Field, false, "Assembly", -1, true,
                "LGU's own repeat-avoidance bookkeeping"),
            new LguMemberRequirement(ContractManagerType, SyncContractDetailsClientRpcMethod, LguMemberKind.Method, false, "Assembly", 2, true,
                "replicating the assignment to every client"),
            new LguMemberRequirement(CommandParserType, null, LguMemberKind.Type, false, "Assembly", -1, true,
                "the contract type table"),
            new LguMemberRequirement(CommandParserType, ContractsField, LguMemberKind.Field, true, "Public", -1, true,
                "which contract types LGU has enabled, and the index the RPC takes"),
            new LguMemberRequirement(CommandParserType, TryGetContractMethod, LguMemberKind.Method, true, "Private", 1, false,
                "suppressing the 'contract' terminal command"),
            new LguMemberRequirement(ContractApplicationType, null, LguMemberKind.Type, false, "Assembly", -1, false,
                "LGU's interactive contract shop"),
            new LguMemberRequirement(ContractApplicationType, PurchaseRandomMoonContractMethod, LguMemberKind.Method, false, "Private", 1, false,
                "suppressing the random-moon purchase"),
            new LguMemberRequirement(ContractApplicationType, PurchaseSpecifiedMoonContractMethod, LguMemberKind.Method, false, "Private", 2, false,
                "suppressing the specified-moon purchase"),

            // #804. Objective progress. All optional: a missing row costs one objective's
            // detail, never the assignment bridge.
            new LguMemberRequirement(ContractManagerType, BombOrderField, LguMemberKind.Field, false, "Assembly", -1, false,
                "wires still to cut on the defusal device"),
            new LguMemberRequirement(ContractManagerType, SerialNumberField, LguMemberKind.Field, false, "Assembly", -1, false,
                "the defusal device's serial number status line"),

            new LguMemberRequirement(DataPcType, null, LguMemberKind.Type, false, "Assembly", -1, false,
                "the data-retrieval laptop"),
            new LguMemberRequirement(DataPcType, InitiateGameClientRpcMethod, LguMemberKind.Method, false, "Private", 1, false,
                "latching that the laptop's login screen was opened"),
            new LguMemberRequirement(DataPcType, ExitGameClientRpcMethod, LguMemberKind.Method, false, "Private", 2, false,
                "latching that survey.db was recovered"),

            new LguMemberRequirement(BugNestType, null, LguMemberKind.Type, false, "Assembly", -1, false,
                "the hoarding-bug nest"),
            new LguMemberRequirement(BugNestType, DisableNestClientRpcMethod, LguMemberKind.Method, false, "Private", 1, false,
                "latching that the nest was destroyed"),

            new LguMemberRequirement(ExtractPlayerType, null, LguMemberKind.Type, false, "Assembly", -1, false,
                "the downed scavenger"),
            new LguMemberRequirement(ExtractPlayerType, HealScavClientRpcMethod, LguMemberKind.Method, false, "Private", 0, false,
                "latching that the scavenger was treated"),

            new LguMemberRequirement(PentagramType, null, LguMemberKind.Type, false, "Assembly", -1, false,
                "the exorcism pentagram"),
            new LguMemberRequirement(PentagramType, SyncAltarClientRpcMethod, LguMemberKind.Method, false, "Private", 1, false,
                "learning which demon the altar named"),
            new LguMemberRequirement(PentagramType, SyncCurrentRitualClientRpcMethod, LguMemberKind.Method, false, "Private", 1, false,
                "counting ritual items as they are placed"),
            new LguMemberRequirement(PentagramType, FailRitualClientRpcMethod, LguMemberKind.Method, false, "Private", 0, false,
                "failing the ritual objective on a wrong placement"),
            new LguMemberRequirement(PentagramType, CurrentRitualField, LguMemberKind.Field, false, "Private", -1, false,
                "the ritual items still outstanding"),
            new LguMemberRequirement(PentagramType, DemonInstructionsField, LguMemberKind.Field, true, "Public", -1, false,
                "the demon names and their ritual item lists"),

            new LguMemberRequirement(BombDefusalType, null, LguMemberKind.Type, false, "Assembly", -1, false,
                "the defusal device"),
            new LguMemberRequirement(BombDefusalType, SyncBombDetailsClientRpcMethod, LguMemberKind.Method, false, "Private", 2, false,
                "learning the device's serial number and wire order"),
            new LguMemberRequirement(BombDefusalType, BombStateChangeClientRpcMethod, LguMemberKind.Method, false, "Private", 1, false,
                "counting wires as they are cut, and detecting a detonation"),
            new LguMemberRequirement(BombDefusalType, BombTimerField, LguMemberKind.Field, false, "Private", -1, false,
                "the device's own countdown, for the DEADLINE status line"),

            new LguMemberRequirement(UpgradeBusType, null, LguMemberKind.Type, false, "Public", -1, false,
                "the configuration root"),
            new LguMemberRequirement(UpgradeBusType, UpgradeBusInstanceProperty, LguMemberKind.Property, true, "Assembly", -1, false,
                "reaching the live configuration"),
            new LguMemberRequirement(UpgradeBusType, PluginConfigurationProperty, LguMemberKind.Property, false, "Assembly", -1, false,
                "reaching the plugin configuration"),
            new LguMemberRequirement(LategameConfigurationType, null, LguMemberKind.Type, false, "Public", -1, false,
                "the plugin configuration"),
            new LguMemberRequirement(LategameConfigurationType, ContractsConfigurationProperty, LguMemberKind.Property, false, "Public", -1, false,
                "the contracts configuration section"),
            new LguMemberRequirement(ContractsConfigurationType, null, LguMemberKind.Type, false, "Public", -1, false,
                "the contracts configuration section"),
            new LguMemberRequirement(ContractsConfigurationType, ExterminationConfigurationProperty, LguMemberKind.Property, false, "Public", -1, false,
                "the extermination configuration section"),
            new LguMemberRequirement(ExterminationConfigurationType, null, LguMemberKind.Type, false, "Public", -1, false,
                "the extermination configuration section"),
            new LguMemberRequirement(ExterminationConfigurationType, AmountSpawnsProperty, LguMemberKind.Property, false, "Public", -1, false,
                "how many hoarding bugs an extermination contract releases"),
        };
    }
}
