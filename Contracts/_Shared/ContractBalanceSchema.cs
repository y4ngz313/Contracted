using BepInEx.Configuration;
using UnityEngine;

using Y4NGZCompany.Contracts.Defuse;
using Y4NGZCompany.Core;
using Y4NGZCompany.Core.Compatibility;
namespace Y4NGZCompany.Contracts._Shared
{
    internal enum ContractRiskLevel
    {
        C,
        B,
        A,
        S
    }

    internal static class ContractRisk
    {
        internal static ContractRiskLevel Current
        {
            get
            {
                string value = StartOfRound.Instance?.currentLevel?.riskLevel;
                return Parse(value);
            }
        }

        internal static ContractRiskLevel Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return ContractRiskLevel.C;

            switch (char.ToUpperInvariant(value.Trim()[0]))
            {
                case 'S': return ContractRiskLevel.S;
                case 'A': return ContractRiskLevel.A;
                case 'B': return ContractRiskLevel.B;
                default: return ContractRiskLevel.C;
            }
        }
    }

    internal readonly struct DefuseEffectiveSettings
    {
        internal readonly ContractRiskLevel Risk;
        internal readonly float DeadlineNormalizedTime;
        internal readonly float ExplosionRadiusMeters;
        internal readonly int MinCorrectWireInputs;
        internal readonly int MaxCorrectWireInputs;
        internal readonly float InteractDistanceMeters;
        internal readonly bool UseFacilityMeltdownOnFailure;

        internal DefuseEffectiveSettings(
            ContractRiskLevel risk,
            float deadlineNormalizedTime,
            float explosionRadiusMeters,
            int minCorrectWireInputs,
            int maxCorrectWireInputs,
            float interactDistanceMeters,
            bool useFacilityMeltdownOnFailure)
        {
            Risk = risk;
            DeadlineNormalizedTime = Mathf.Clamp(deadlineNormalizedTime, 0.1f, 0.95f);
            ExplosionRadiusMeters = Mathf.Clamp(explosionRadiusMeters, 1f, 30f);
            MinCorrectWireInputs = Mathf.Clamp(minCorrectWireInputs, 1, DefuseRules.MaxWireSlots);
            MaxCorrectWireInputs = Mathf.Clamp(
                maxCorrectWireInputs,
                MinCorrectWireInputs,
                DefuseRules.MaxWireSlots);
            InteractDistanceMeters = Mathf.Clamp(interactDistanceMeters, 1f, 6f);
            UseFacilityMeltdownOnFailure = useFacilityMeltdownOnFailure;
        }
    }

    // #694: one plain "Defuse" section. The per-risk sections collapsed to single
    // values because every tier shipped identical defaults. The inert
    // UseFacilityMeltdownOnFailure key (#220) and the pre-schema legacy importer
    // were retired with the config overhaul; the migration owns value carry-over.
    internal sealed class DefuseBalanceConfig
    {
        internal const string BombPrefabName = "P_DeadDrop_BombPreview";
        internal const float BombScale = 1f;
        internal const float BombVerticalOffsetMeters = 0.05f;

        // Elevated procedural tier (#548): the bomb may rest on a stable prop top whose
        // surface sits inside this band above the room's floor plane.
        internal const float ElevatedMinHeightMeters = 0.65f;
        internal const float ElevatedMaxHeightMeters = 1.30f;

        private readonly ConfigEntry<float> _interactDistance;
        private readonly ConfigEntry<bool> _elevatedPlacement;
        private readonly ConfigEntry<float> _deadlineNormalizedTime;
        private readonly ConfigEntry<float> _explosionRadiusMeters;
        private readonly ConfigEntry<int> _minCorrectWireInputs;
        private readonly ConfigEntry<int> _maxCorrectWireInputs;
        // #220: a failed Defuse always starts FacilityMeltdown when that mod is
        // installed; nothing is left for this to gate, so it is a fixed value now.
        private readonly FixedSetting<bool> _useFacilityMeltdown = FixedSetting.Of(false);
        private readonly ConfigEntry<bool> _debugLogging;
        private readonly FixedSetting<bool> _forceAllMoons = FixedSetting.Of(false);

        internal DefuseBalanceConfig(ConfigFile config)
        {
            _interactDistance = config.Bind(
                ContractedConfigSections.Defuse,
                "Interact Distance Meters",
                2.8f,
                new ConfigDescription(
                    "Maximum distance in meters for wire and keypad interaction.",
                    new AcceptableValueRange<float>(1f, 6f)));

            _elevatedPlacement = config.Bind(
                ContractedConfigSections.Defuse,
                "Elevated Bomb Placement",
                true,
                "Host only. Allow the procedural bomb placement to rest the bomb on stable prop tops " +
                $"({ElevatedMinHeightMeters:0.00}-{ElevatedMaxHeightMeters:0.00} m above the floor) before falling back to the floor.");

            // #992: retain the setting on disk for restoration; it is inactive during suspension.
            config.Bind(
                ContractedConfigSections.Defuse,
                "Prefer LGU Defuse",
                true,
                "Temporarily inactive: Contracted's Defuse is suspended. LGU Defusal is used " +
                "when available, regardless of this saved value. Without LGU Defusal, neither " +
                "bomb-disarm contract rolls. Native Defuse tuning is preserved for restoration.");

            _deadlineNormalizedTime = config.Bind(
                ContractedConfigSections.Defuse,
                "Deadline Normalized Time",
                0.5625f,
                new ConfigDescription(
                    "Host only. Normalized day time when Defuse fails (0.5625 matches 5:00 PM).",
                    new AcceptableValueRange<float>(0.1f, 0.95f)));

            _explosionRadiusMeters = config.Bind(
                ContractedConfigSections.Defuse,
                "Explosion Radius Meters",
                8f,
                new ConfigDescription(
                    "Host only. Local failure blast radius in meters when FacilityMeltdown is not installed.",
                    new AcceptableValueRange<float>(1f, 30f)));

            _minCorrectWireInputs = config.Bind(
                ContractedConfigSections.Defuse,
                "Min Correct Wire Inputs",
                1,
                new ConfigDescription(
                    "Host only. Minimum correct wire inputs allowed in a generated Defuse procedure.",
                    new AcceptableValueRange<int>(1, DefuseRules.MaxWireSlots)));

            _maxCorrectWireInputs = config.Bind(
                ContractedConfigSections.Defuse,
                "Max Correct Wire Inputs",
                DefuseRules.MaxWireSlots,
                new ConfigDescription(
                    "Host only. Maximum correct wire inputs allowed in a generated Defuse procedure.",
                    new AcceptableValueRange<int>(1, DefuseRules.MaxWireSlots)));

            _debugLogging = config.Bind(
                ContractedConfigSections.Diagnostics,
                "Defuse Debug Logging",
                false,
                "Enable detailed Defuse diagnostic logging.");
        }

        internal bool ForceAllMoons => _forceAllMoons.Value;
        internal bool DebugLogging => _debugLogging.Value;
        internal bool ElevatedBombPlacement => _elevatedPlacement.Value;

        internal DefuseEffectiveSettings GetEffective()
        {
            ContractRiskLevel risk = ContractRisk.Current;
            return new DefuseEffectiveSettings(
                risk,
                _deadlineNormalizedTime.Value,
                _explosionRadiusMeters.Value,
                _minCorrectWireInputs.Value,
                _maxCorrectWireInputs.Value,
                _interactDistance.Value,
                _useFacilityMeltdown.Value);
        }

        internal string BuildAuditLine()
        {
            DefuseEffectiveSettings value = GetEffective();
            return $"risk={value.Risk} deadline={value.DeadlineNormalizedTime:0.###} " +
                   $"blast={value.ExplosionRadiusMeters:0.##}m wires={value.MinCorrectWireInputs}-{value.MaxCorrectWireInputs} " +
                   $"interact={value.InteractDistanceMeters:0.##}m meltdown={value.UseFacilityMeltdownOnFailure} " +
                   // #992: no native fallback while Defuse is suspended.
                   $"active={(MoonContractLguBridge.IsContractCapabilityAvailable(MoonContractType.LguDefusal) ? "LguDefusal" : "None")}";
        }
    }
}
