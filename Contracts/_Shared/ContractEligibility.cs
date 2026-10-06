using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Y4NGZCompany.Core;

namespace Y4NGZCompany.Contracts._Shared
{
    internal enum ContractEligibilityReason
    {
        Allowed,
        InvalidContract,
        Disabled,
        QuotaLocked,
        WeightZero,
        MoonDenied,
        MoonNotAllowed,
        ConstellationDenied,
        ConstellationNotAllowed,
        ConstellationDataUnavailable,
        InteriorDenied,
        InteriorNotAllowed,
        CapabilityUnavailable,
        DebugOverride
    }

    internal readonly struct ContractEligibilityDecision
    {
        internal readonly bool Allowed;
        internal readonly ContractEligibilityReason Reason;
        internal readonly string Detail;

        internal ContractEligibilityDecision(bool allowed, ContractEligibilityReason reason, string detail = "")
        {
            Allowed = allowed;
            Reason = reason;
            Detail = detail ?? string.Empty;
        }
    }

    // #694: every contract's selection controls live in that contract's own plain
    // config section, next to its balance values, instead of a shared numbered
    // "01 - Contract Selection" section with dotted key prefixes.
    internal sealed class ContractSelectionProfile
    {
        internal readonly MoonContractType Contract;
        internal readonly ConfigEntry<bool> Enabled;
        internal readonly ConfigEntry<int> Weight;
        internal readonly ConfigEntry<int> UnlockAtCompletedQuota;
        internal readonly ConfigEntry<int> RewardPercentOverride;
        internal readonly ConfigEntry<int> FailurePenaltyPercentOverride;
        internal readonly ConfigEntry<string> MoonAllowList;
        internal readonly ConfigEntry<string> MoonDenyList;
        internal readonly ConfigEntry<string> ConstellationAllowList;
        internal readonly ConfigEntry<string> ConstellationDenyList;
        internal readonly ConfigEntry<string> InteriorAllowList;
        internal readonly ConfigEntry<string> InteriorDenyList;

        internal ContractSelectionProfile(
            ConfigFile config,
            MoonContractType contract,
            int defaultWeight,
            string moonAllow,
            string moonDeny,
            string interiorAllow,
            string interiorDeny)
        {
            Contract = contract;

            // "None" is the weighted vanilla-Salvage outcome. Eligibility returns before
            // consulting gates or route filters, so it gets only its weight, bound in the
            // shared Contracts section under a name that says what it actually does.
            if (contract == MoonContractType.None)
            {
                Weight = config.Bind(ContractedConfigSections.Contracts, "No Contract Weight", Math.Max(0, defaultWeight),
                    "Host only. Random-selection weight for landing with no directive (vanilla Salvage), " +
                    "weighed against every enabled contract's Selection Weight. Zero means a directive " +
                    "rolls whenever any contract is eligible.");
                return;
            }

            string section = SectionFor(contract);
            Enabled = config.Bind(section, "Enabled", true,
                "Host only. Master switch: when false this contract never rolls, regardless of weights.");
            Weight = config.Bind(section, "Selection Weight", Math.Max(0, defaultWeight),
                "Host only. Random-selection weight against the other contracts and No Contract Weight. " +
                "Zero disables random selection.");
            UnlockAtCompletedQuota = config.Bind(section, "Unlock After Completed Quotas", 0,
                new ConfigDescription(
                    "Host only. Completed quota count required before this contract can roll. Zero means immediately available.",
                    new AcceptableValueRange<int>(0, 99)));
            RewardPercentOverride = config.Bind(section, "Reward Percent Override", -1,
                new ConfigDescription(
                    "Host only. Quota reduction percent when this contract succeeds. -1 uses the shared " +
                    "Reward And Penalty Percent By Risk table in the Contracts section.",
                    new AcceptableValueRange<int>(-1, 100)));
            FailurePenaltyPercentOverride = config.Bind(section, "Failure Penalty Percent Override", -1,
                new ConfigDescription(
                    "Host only. Quota increase percent when this contract fails. -1 uses the shared " +
                    "Reward And Penalty Percent By Risk table in the Contracts section.",
                    new AcceptableValueRange<int>(-1, 100)));
            MoonAllowList = config.Bind(section, "Moon Allow List", moonAllow ?? string.Empty,
                "Comma-separated moon names. Empty allows every otherwise eligible moon.");
            MoonDenyList = config.Bind(section, "Moon Deny List", moonDeny ?? string.Empty,
                "Comma-separated moon names that always reject this contract.");
            ConstellationAllowList = config.Bind(section, "Constellation Allow List", string.Empty,
                "Comma-separated constellation names. Empty allows every constellation.");
            ConstellationDenyList = config.Bind(section, "Constellation Deny List", string.Empty,
                "Comma-separated constellation names that always reject this contract.");
            InteriorAllowList = config.Bind(section, "Interior Allow List", interiorAllow ?? string.Empty,
                "Comma-separated DungeonFlow names. Empty allows every otherwise eligible interior.");
            InteriorDenyList = config.Bind(section, "Interior Deny List", interiorDeny ?? string.Empty,
                "Comma-separated DungeonFlow names that always reject this contract.");
        }

        internal static string SectionFor(MoonContractType contract)
        {
            switch (contract)
            {
                case MoonContractType.Whistleblower: return ContractedConfigSections.Whistleblower;
                case MoonContractType.BlackoutAudit: return ContractedConfigSections.BlackoutAudit;
                case MoonContractType.ShadowRaid: return ContractedConfigSections.ShadowRaid;
                case MoonContractType.ContainmentBreach: return ContractedConfigSections.ContainmentBreach;
                case MoonContractType.Survey: return ContractedConfigSections.Survey;
                case MoonContractType.Defuse: return ContractedConfigSections.Defuse;
                case MoonContractType.Payload: return ContractedConfigSections.Payload;
                case MoonContractType.PestControl: return ContractedConfigSections.PestControl;
                case MoonContractType.WasteDisposal: return ContractedConfigSections.WasteDisposal;
                // #803: LGU-backed directives.
                case MoonContractType.LguDataRetrieval: return ContractedConfigSections.DataRetrieval;
                case MoonContractType.LguExtermination: return ContractedConfigSections.Extermination;
                case MoonContractType.LguExtraction: return ContractedConfigSections.Extraction;
                case MoonContractType.LguExorcism: return ContractedConfigSections.Exorcism;
                case MoonContractType.LguDefusal: return ContractedConfigSections.Defusal;
                default: return ContractedConfigSections.Contracts;
            }
        }

        internal bool IsEnabled => Enabled == null || Enabled.Value;

        internal IReadOnlyList<string> MoonAllows => Split(MoonAllowList?.Value);
        internal IReadOnlyList<string> MoonDenies => Split(MoonDenyList?.Value);
        internal IReadOnlyList<string> ConstellationAllows => Split(ConstellationAllowList?.Value);
        internal IReadOnlyList<string> ConstellationDenies => Split(ConstellationDenyList?.Value);
        internal IReadOnlyList<string> InteriorAllows => Split(InteriorAllowList?.Value);
        internal IReadOnlyList<string> InteriorDenies => Split(InteriorDenyList?.Value);

        private static IReadOnlyList<string> Split(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    internal sealed class ContractSelectionConfig
    {
        private static readonly MoonContractType[] SelectableContracts =
        {
            MoonContractType.None,
            MoonContractType.Whistleblower,
            MoonContractType.BlackoutAudit,
            MoonContractType.ShadowRaid,
            MoonContractType.ContainmentBreach,
            MoonContractType.Survey,
            MoonContractType.Defuse,
            MoonContractType.Payload,
            MoonContractType.PestControl,
            MoonContractType.WasteDisposal,
            // #803: LGU-backed directives. They bind the same selection profile as every other
            // contract; LGU's presence and per-type availability are a runtime capability gate
            // applied at eligibility time, not a reason to skip binding the config.
            MoonContractType.LguDataRetrieval,
            MoonContractType.LguExtermination,
            MoonContractType.LguExtraction,
            MoonContractType.LguExorcism,
            MoonContractType.LguDefusal
        };

        private readonly Dictionary<MoonContractType, ContractSelectionProfile> _profiles =
            new Dictionary<MoonContractType, ContractSelectionProfile>();

        internal ContractSelectionConfig(ConfigFile config)
        {
            for (int i = 0; i < SelectableContracts.Length; i++)
            {
                MoonContractType contract = SelectableContracts[i];
                int defaultWeight = contract == MoonContractType.None ? 2 : 1;
                _profiles[contract] = new ContractSelectionProfile(
                    config,
                    contract,
                    defaultWeight,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty);
            }
        }

        internal ContractSelectionProfile Get(MoonContractType contract)
        {
            return _profiles.TryGetValue(contract, out ContractSelectionProfile profile) ? profile : null;
        }

        internal Dictionary<MoonContractType, int> BuildBaseWeights()
        {
            return _profiles.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.IsEnabled ? Math.Max(0, pair.Value.Weight.Value) : 0);
        }

    }

    internal static class ContractEligibilityService
    {
        internal static ContractEligibilityDecision Evaluate(
            ContractSelectionProfile profile,
            int completedQuotas,
            string moonName,
            IReadOnlyList<string> constellations,
            bool constellationDataAvailable,
            string interiorName = null,
            bool debugOverride = false,
            bool capabilityAvailable = true,
            bool includeWeight = true)
        {
            if (profile == null)
                return Deny(ContractEligibilityReason.InvalidContract, "No selection profile exists.");
            if (profile.Contract == MoonContractType.None)
                return Allow();
            if (!profile.IsEnabled)
                return Deny(ContractEligibilityReason.Disabled, "disabled in config");
            if (debugOverride)
                return new ContractEligibilityDecision(true, ContractEligibilityReason.DebugOverride, "Diagnostics override bypassed normal eligibility.");
            if (completedQuotas < Math.Max(0, profile.UnlockAtCompletedQuota.Value))
                return Deny(ContractEligibilityReason.QuotaLocked, $"requires quota {profile.UnlockAtCompletedQuota.Value}");
            if (includeWeight && profile.Weight.Value <= 0)
                return Deny(ContractEligibilityReason.WeightZero, "ordinary selection weight is zero");
            if (MatchesAny(moonName, profile.MoonDenies))
                return Deny(ContractEligibilityReason.MoonDenied, moonName);
            if (profile.MoonAllows.Count > 0 && !MatchesAny(moonName, profile.MoonAllows))
                return Deny(ContractEligibilityReason.MoonNotAllowed, moonName);

            if (profile.ConstellationAllows.Count > 0 && !constellationDataAvailable)
                return Deny(ContractEligibilityReason.ConstellationDataUnavailable, "an allow list is configured but membership data is unavailable");
            if (MatchesAny(constellations, profile.ConstellationDenies))
                return Deny(ContractEligibilityReason.ConstellationDenied, string.Join(",", constellations ?? Array.Empty<string>()));
            if (profile.ConstellationAllows.Count > 0 && !MatchesAny(constellations, profile.ConstellationAllows))
                return Deny(ContractEligibilityReason.ConstellationNotAllowed, string.Join(",", constellations ?? Array.Empty<string>()));

            if (!string.IsNullOrWhiteSpace(interiorName))
            {
                if (MatchesAny(interiorName, profile.InteriorDenies))
                    return Deny(ContractEligibilityReason.InteriorDenied, interiorName);
                if (profile.InteriorAllows.Count > 0 && !MatchesAny(interiorName, profile.InteriorAllows))
                    return Deny(ContractEligibilityReason.InteriorNotAllowed, interiorName);
            }

            if (!capabilityAvailable)
                return Deny(ContractEligibilityReason.CapabilityUnavailable, "required runtime capability is unavailable");
            return Allow();
        }

        private static bool MatchesAny(string actual, IReadOnlyList<string> configured)
        {
            if (string.IsNullOrWhiteSpace(actual) || configured == null)
                return false;
            for (int i = 0; i < configured.Count; i++)
                if (ContractConfigRules.NameMatches(actual, configured[i])) return true;
            return false;
        }

        private static bool MatchesAny(IReadOnlyList<string> actual, IReadOnlyList<string> configured)
        {
            if (actual == null || configured == null)
                return false;
            for (int i = 0; i < actual.Count; i++)
                if (MatchesAny(actual[i], configured)) return true;
            return false;
        }

        private static ContractEligibilityDecision Allow() =>
            new ContractEligibilityDecision(true, ContractEligibilityReason.Allowed);

        private static ContractEligibilityDecision Deny(ContractEligibilityReason reason, string detail) =>
            new ContractEligibilityDecision(false, reason, detail);
    }
}
