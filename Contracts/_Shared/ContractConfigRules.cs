using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Y4NGZCompany.Contracts._Shared
{
    public static class ContractConfigRules
    {
        internal const string DefaultContractWeights =
            "None=2,Whistleblower=1,BlackoutAudit=1,ShadowRaid=1,ContainmentBreach=1,Survey=1,Defuse=1,Payload=1,PestControl=1,WasteDisposal=1," +
            // #803: the LGU-backed directives roll at the same base weight; the LGU capability
            // gate, not the weight, is what keeps them out of the pool without that mod.
            "LguDataRetrieval=1,LguExtermination=1,LguExtraction=1,LguExorcism=1,LguDefusal=1";
        internal const string DefaultRewardPenaltyPercentByTier = "D=15,C=15,B=20,A=25,S=30";
        internal const string DefaultBlackoutBreakerCountByTier = "D=1,C=1,B=2,A=3,S=3";
        internal const string DefaultBlackoutAmbientFlickerIntervalSecondsByTier = "D=30,C=30,B=45,A=60,S=60";
        internal const string DefaultSurveyDroneCountByTier = "D=1,C=1,B=2,A=3,S=4";
        internal const string DefaultShadowRaidDrillDurationSecondsByTier = "D=60,C=60,B=90,A=120,S=180";
        internal const string DefaultShadowRaidContainerCountByTier = "D=1,C=1,B=2,A=2,S=3";
        internal const string DefaultContainmentWaveCountByTier = "D=1,C=1,B=2,A=3,S=4";
        internal const string DefaultContainmentWaveDurationSecondsByTier = "D=45,C=45,B=60,A=75,S=90";

        internal static Dictionary<MoonContractType, int> ParseContractWeights(string value)
        {
            var result = new Dictionary<MoonContractType, int>();
            foreach (string entry in SplitEntries(value, ','))
            {
                string key;
                string rawValue;
                if (!TrySplitPair(entry, out key, out rawValue))
                    continue;
                MoonContractType contract;
                if (!TryParseContract(key, out contract))
                    continue;
                int weight;
                if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out weight))
                    continue;

                result[contract] = Math.Max(0, weight);
            }

            return result;
        }

        internal static Dictionary<string, Dictionary<MoonContractType, int>> ParseScopedContractWeights(string value)
        {
            var result = new Dictionary<string, Dictionary<MoonContractType, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (string scopeEntry in SplitEntries(value, ';'))
            {
                int separator = scopeEntry.IndexOf(':');
                if (separator <= 0 || separator >= scopeEntry.Length - 1)
                    continue;

                string scope = NormalizeLookupKey(scopeEntry.Substring(0, separator));
                if (string.IsNullOrWhiteSpace(scope))
                    continue;

                var weights = ParseContractWeights(scopeEntry.Substring(separator + 1));
                if (weights.Count == 0)
                    continue;

                result[scope] = weights;
            }

            return result;
        }

        internal static Dictionary<MoonContractType, List<string>> ParseContractStringLists(string value)
        {
            var result = new Dictionary<MoonContractType, List<string>>();
            foreach (string entry in SplitEntries(value, ';'))
            {
                string key;
                string rawValue;
                if (!TrySplitPair(entry, out key, out rawValue))
                    continue;
                MoonContractType contract;
                if (!TryParseContract(key, out contract))
                    continue;

                var values = SplitEntries(rawValue, ',')
                    .Select(item => item.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList();
                if (values.Count == 0)
                    continue;

                result[contract] = values;
            }

            return result;
        }

        internal static Dictionary<char, int> ParseTierIntMap(string value)
        {
            var result = new Dictionary<char, int>();
            foreach (string entry in SplitEntries(value, ','))
            {
                string key;
                string rawValue;
                if (!TrySplitPair(entry, out key, out rawValue))
                    continue;
                char tier;
                if (!TryParseTier(key, out tier))
                    continue;
                int parsed;
                if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    continue;

                result[tier] = parsed;
            }

            return result;
        }

        internal static Dictionary<char, float> ParseTierFloatMap(string value)
        {
            var result = new Dictionary<char, float>();
            foreach (string entry in SplitEntries(value, ','))
            {
                string key;
                string rawValue;
                if (!TrySplitPair(entry, out key, out rawValue))
                    continue;
                char tier;
                if (!TryParseTier(key, out tier))
                    continue;
                float parsed;
                if (!float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    continue;

                result[tier] = parsed;
            }

            return result;
        }

        internal static Dictionary<char, List<string>> ParseTierStringLists(string value)
        {
            var result = new Dictionary<char, List<string>>();
            foreach (string entry in SplitEntries(value, ';'))
            {
                string key;
                string rawValue;
                if (!TrySplitPair(entry, out key, out rawValue))
                    continue;
                char tier;
                if (!TryParseTier(key, out tier))
                    continue;

                var values = SplitEntries(rawValue, ',')
                    .Select(item => item.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList();
                if (values.Count == 0)
                    continue;

                result[tier] = values;
            }

            return result;
        }

        internal static int GetTierInt(Dictionary<char, int> values, char tier, int fallback)
        {
            tier = NormalizeTier(tier);
            int value;
            if (tier != '\0' && values != null && values.TryGetValue(tier, out value))
                return value;
            return fallback;
        }

        internal static float GetTierFloat(Dictionary<char, float> values, char tier, float fallback)
        {
            tier = NormalizeTier(tier);
            float value;
            if (tier != '\0' && values != null && values.TryGetValue(tier, out value))
                return value;
            return fallback;
        }

        internal static List<string> GetTierStringList(Dictionary<char, List<string>> values, char tier, IEnumerable<string> fallback)
        {
            tier = NormalizeTier(tier);
            List<string> list;
            if (tier != '\0' && values != null && values.TryGetValue(tier, out list) && list.Count > 0)
                return list;
            return fallback == null ? new List<string>() : fallback.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        }

        internal static bool IsNameListed(
            Dictionary<MoonContractType, List<string>> lists,
            MoonContractType contract,
            string name)
        {
            List<string> configured;
            if (lists == null || !lists.TryGetValue(contract, out configured) || configured.Count == 0)
                return false;

            return configured.Any(entry => NameMatches(name, entry));
        }

        internal static bool NameMatches(string actualName, string configuredName)
        {
            if (string.IsNullOrWhiteSpace(actualName) || string.IsNullOrWhiteSpace(configuredName))
                return false;

            string actual = actualName.Trim();
            string configured = configuredName.Trim();
            if (actual.Equals(configured, StringComparison.OrdinalIgnoreCase))
                return true;
            if (actual.IndexOf(configured, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (configured.IndexOf(actual, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string normalizedActual = NormalizeLookupKey(actual);
            string normalizedConfigured = NormalizeLookupKey(configured);
            if (string.IsNullOrWhiteSpace(normalizedActual) || string.IsNullOrWhiteSpace(normalizedConfigured))
                return false;

            return normalizedActual == normalizedConfigured
                   || normalizedActual.Contains(normalizedConfigured)
                   || normalizedConfigured.Contains(normalizedActual);
        }

        internal static char ResolveRiskTier(string risk)
        {
            if (string.IsNullOrWhiteSpace(risk))
                return '\0';

            string normalized = risk.Trim().ToUpperInvariant()
                .Replace("RISK", string.Empty)
                .Replace("LEVEL", string.Empty)
                .Replace(":", string.Empty)
                .Trim();

            for (int i = 0; i < normalized.Length; i++)
            {
                char c = NormalizeTier(normalized[i]);
                if (c != '\0')
                    return c;
            }

            return '\0';
        }

        internal static bool TryParseContract(string value, out MoonContractType contract)
        {
            contract = MoonContractType.None;
            string normalized = NormalizeLookupKey(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            switch (normalized)
            {
                case "none":
                case "salvage":
                    contract = MoonContractType.None;
                    return true;
                case "whistleblower":
                    contract = MoonContractType.Whistleblower;
                    return true;
                case "blackoutaudit":
                case "blackout":
                    contract = MoonContractType.BlackoutAudit;
                    return true;
                case "shadowraid":
                case "blacksite":
                    contract = MoonContractType.ShadowRaid;
                    return true;
                case "containmentbreach":
                case "containment":
                    contract = MoonContractType.ContainmentBreach;
                    return true;
                case "survey":
                    contract = MoonContractType.Survey;
                    return true;
                case "defuse":
                case "deadrop":
                case "deaddrop":
                case "deaddropcontract":
                    contract = MoonContractType.Defuse;
                    return true;
                case "wastedisposal":
                case "incinerator":
                case "scrapincineration":
                    contract = MoonContractType.WasteDisposal;
                    return true;
                case "pestcontrol":
                case "pest":
                    contract = MoonContractType.PestControl;
                    return true;
                // #803: the LGU-backed directives. Each accepts its enum name, its display
                // name, its short name and the bare noun a player is most likely to type.
                // "defusal" is deliberately the LGU one and "defuse" stays Contracted's own -
                // the two are mutually exclusive at selection time, but they are separate
                // config sections and a weight string has to be able to name either.
                case "lgudataretrieval":
                case "dataretrieval":
                case "lgudata":
                case "data":
                    contract = MoonContractType.LguDataRetrieval;
                    return true;
                case "lguextermination":
                case "extermination":
                case "lguexterm":
                case "exterm":
                case "exterminator":
                    contract = MoonContractType.LguExtermination;
                    return true;
                case "lguextraction":
                case "extraction":
                case "lguextract":
                case "extract":
                    contract = MoonContractType.LguExtraction;
                    return true;
                case "lguexorcism":
                case "exorcism":
                    contract = MoonContractType.LguExorcism;
                    return true;
                // #804: the longer spellings are here so this parser and
                // MoonContractState.NormalizeContractName accept exactly the same set of words
                // for the same contract. They disagreed on "defusal" until then.
                case "lgudefusal":
                case "defusal":
                case "defusalcontract":
                case "bombdefusal":
                    contract = MoonContractType.LguDefusal;
                    return true;
                default:
                    return Enum.TryParse(value, true, out contract);
            }
        }

        /// <summary>
        /// #804. The save-file and terminal spellings of a contract, resolved to an enum member
        /// name for <c>Enum.TryParse</c>. It lived in <c>MoonContractState</c>, where nothing
        /// could hold it to <see cref="TryParseContract"/> - and the two had drifted: "defusal"
        /// meant Contracted's own Defuse here and Lategame Upgrades' Defusal there. Both tables
        /// live in this file now, and ContractCatalogRegression asserts they agree.
        ///
        /// Unrecognised input is returned trimmed, so a plain enum name still parses.
        /// </summary>
        internal static string NormalizeContractName(string contractName)
        {
            if (string.IsNullOrWhiteSpace(contractName))
                return string.Empty;

            string trimmed = contractName.Trim();
            string key = trimmed
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();

            switch (key)
            {
                case "blacksite":
                case "shippingcontainer":
                case "shippingcontainercontract":
                case "shadowraid":
                    return nameof(MoonContractType.ShadowRaid);
                // "defuse" and "dead drop" are Contracted's own case; "defusal" and its longer
                // spellings are LGU's. The two are separate contracts with separate config
                // sections, so the same word must not reach both.
                case "deaddrop":
                case "deaddropcontract":
                case "defuse":
                    return nameof(MoonContractType.Defuse);
                case "defusal":
                case "defusalcontract":
                case "bombdefusal":
                    return nameof(MoonContractType.LguDefusal);
                case "wastedisposal":
                case "wastedisposalcontract":
                case "incinerator":
                case "scrapincineration":
                    return nameof(MoonContractType.WasteDisposal);
                default:
                    return trimmed;
            }
        }

        internal static string NormalizeLookupKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var builder = new StringBuilder(value.Length);
            foreach (char c in value.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                    builder.Append(c);
            }

            return builder.ToString();
        }

        private static IEnumerable<string> SplitEntries(string value, char separator)
        {
            if (string.IsNullOrWhiteSpace(value))
                yield break;

            string[] entries = value.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                string entry = entries[i].Trim();
                if (!string.IsNullOrWhiteSpace(entry))
                    yield return entry;
            }
        }

        private static bool TrySplitPair(string entry, out string key, out string value)
        {
            key = string.Empty;
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(entry))
                return false;

            int separator = entry.IndexOf('=');
            if (separator <= 0 || separator >= entry.Length - 1)
                return false;

            key = entry.Substring(0, separator).Trim();
            value = entry.Substring(separator + 1).Trim();
            return !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryParseTier(string value, out char tier)
        {
            tier = '\0';
            if (string.IsNullOrWhiteSpace(value))
                return false;

            for (int i = 0; i < value.Length; i++)
            {
                tier = NormalizeTier(value[i]);
                if (tier != '\0')
                    return true;
            }

            return false;
        }

        private static char NormalizeTier(char tier)
        {
            switch (char.ToUpperInvariant(tier))
            {
                case 'S':
                    return 'S';
                case 'A':
                    return 'A';
                case 'B':
                    return 'B';
                case 'C':
                    return 'C';
                case 'D':
                    return 'D';
                default:
                    return '\0';
            }
        }
    }
}
