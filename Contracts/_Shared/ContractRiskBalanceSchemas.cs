using BepInEx.Configuration;
using UnityEngine;
using Y4NGZCompany.Core;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;

namespace Y4NGZCompany.Contracts._Shared
{
    // #694: every contract owns one plain config section; per-risk variation lives in
    // tier-table entries ("C=1, B=2, A=3, S=4") instead of one section per tier. The
    // nested RiskConfig view classes keep the historical ForRisk(risk).Key.Value shape
    // so balance call sites are unchanged. Values whose defaults players never had a
    // reason to touch (placement geometry, spawn scoring) are FixedSettings now.
    internal sealed class BlackoutBalanceConfig
    {
        private readonly RiskTierTable<int> _breakers;
        private readonly RiskTierTable<int> _flicker;

        internal BlackoutBalanceConfig(ConfigFile config)
        {
            _breakers = RiskTierTable.BindInt(config, ContractedConfigSections.BlackoutAudit,
                "Breakers Required By Risk", "C=1, B=2, A=3, S=3", 1, 3,
                "Host only. Breaker boxes that must be switched to complete the audit.");
            _flicker = RiskTierTable.BindInt(config, ContractedConfigSections.BlackoutAudit,
                "Ambient Flicker Interval Seconds By Risk", "C=30, B=45, A=60, S=60", 20, 180,
                "Host only. Seconds between ambient blackout flickers.");
        }

        internal RiskConfig ForRisk(char risk) => new RiskConfig(this, risk);

        internal sealed class RiskConfig
        {
            internal readonly DerivedSetting<int> BreakerCount;
            internal readonly DerivedSetting<int> AmbientFlickerIntervalSeconds;

            internal RiskConfig(BlackoutBalanceConfig owner, char risk)
            {
                BreakerCount = owner._breakers.View(risk);
                AmbientFlickerIntervalSeconds = owner._flicker.View(risk);
            }
        }
    }

    internal sealed class SurveyBalanceConfig
    {
        private readonly RiskTierTable<int> _drones;

        internal SurveyBalanceConfig(ConfigFile config)
        {
            _drones = RiskTierTable.BindInt(config, ContractedConfigSections.Survey,
                "Drones Required By Risk", "C=1, B=2, A=3, S=4", 1, 8,
                "Host only. Survey beacons that must be deployed to complete the contract.");
        }

        internal int DroneCount(char risk) => _drones.For(risk);
    }

    internal sealed class ShadowRaidBalanceConfig
    {
        private readonly RiskTierTable<float> _drillDuration;
        private readonly RiskTierTable<int> _containers;

        internal ShadowRaidBalanceConfig(ConfigFile config)
        {
            _drillDuration = RiskTierTable.BindFloat(config, ContractedConfigSections.ShadowRaid,
                "Drill Duration Seconds By Risk", "C=60, B=90, A=120, S=180", 5f, 300f,
                "Host only. Seconds the breach drill runs on a container.");
            _containers = RiskTierTable.BindInt(config, ContractedConfigSections.ShadowRaid,
                "Containers By Risk", "C=1, B=2, A=2, S=3", 1, 8,
                "Host only. Shipping containers placed for the raid.");
        }

        internal RiskConfig ForRisk(char risk) => new RiskConfig(this, risk);

        internal sealed class RiskConfig
        {
            internal readonly DerivedSetting<float> DrillDurationSeconds;
            internal readonly DerivedSetting<int> ContainerCount;

            internal RiskConfig(ShadowRaidBalanceConfig owner, char risk)
            {
                DrillDurationSeconds = owner._drillDuration.View(risk);
                ContainerCount = owner._containers.View(risk);
            }
        }
    }

    internal sealed class PayloadBalanceConfig
    {
        internal readonly ConfigEntry<bool> RunawayEnabled;
        private readonly ConfigEntry<int> _cargoValue;
        private readonly ConfigEntry<float> _pushSpeedMultiplier;
        private readonly ConfigEntry<float> _reverseInputMultiplier;
        private readonly ConfigEntry<float> _uphillMinimumSpeedMultiplier;
        private RiskConfig _riskView;

        internal PayloadBalanceConfig(ConfigFile config)
        {
            // Every default was identical across the four risk tiers, so these are
            // plain single values now rather than tier tables.
            RunawayEnabled = config.Bind(ContractedConfigSections.Payload, "Runaway Cart Enabled", true,
                "Host only. Allow released carts to roll downhill and break loose on steep slopes. Falling gravity remains enabled.");
            _cargoValue = config.Bind(ContractedConfigSections.Payload, "Cargo Value", 400,
                new ConfigDescription(
                    "Host only. Scrap value awarded after successful delivery.",
                    new AcceptableValueRange<int>(0, 5000)));
            _pushSpeedMultiplier = config.Bind(ContractedConfigSections.Payload, "Push Speed Multiplier", 0.45f,
                new ConfigDescription(
                    "Host cart speed multiplier relative to a 4.6 m/s walking reference.",
                    new AcceptableValueRange<float>(0.1f, 1f)));
            _reverseInputMultiplier = config.Bind(ContractedConfigSections.Payload, "Reverse Input Multiplier", 0.6f,
                new ConfigDescription(
                    "Additional speed multiplier while backing the cart up.",
                    new AcceptableValueRange<float>(0.1f, 1f)));
            _uphillMinimumSpeedMultiplier = config.Bind(ContractedConfigSections.Payload, "Uphill Minimum Speed Multiplier", 0.55f,
                new ConfigDescription(
                    "Lowest speed multiplier reached on steep climbs.",
                    new AcceptableValueRange<float>(0.1f, 1f)));
        }

        // The Payload view ignores the risk tier (every entry is a single plain value), and the push
        // runtime reads it three to four times per frame, so hand out one cached view instead of
        // allocating a fresh wrapper per read (#716 B7). The view holds the live ConfigEntry objects,
        // so a config change still lands on the next `.Value` read.
        internal RiskConfig ForRisk(char risk) => _riskView ?? (_riskView = new RiskConfig(this));

        internal sealed class RiskConfig
        {
            internal readonly ConfigEntry<int> CargoValue;
            internal readonly ConfigEntry<float> PushSpeedMultiplier;
            internal readonly ConfigEntry<float> ReverseInputMultiplier;
            internal readonly ConfigEntry<float> UphillMinimumSpeedMultiplier;

            internal RiskConfig(PayloadBalanceConfig owner)
            {
                CargoValue = owner._cargoValue;
                PushSpeedMultiplier = owner._pushSpeedMultiplier;
                ReverseInputMultiplier = owner._reverseInputMultiplier;
                UphillMinimumSpeedMultiplier = owner._uphillMinimumSpeedMultiplier;
            }
        }
    }

    internal sealed class WasteDisposalBalanceConfig
    {
        // Placement geometry and the capture volume are authored numbers fitted to the
        // incinerator prefab, not gameplay tuning; retired from the config surface (#694).
        internal readonly FixedSetting<float> FixtureWidthMeters = FixedSetting.Of(2.4f);
        internal readonly FixedSetting<float> FixtureHeightMeters = FixedSetting.Of(2.85f);
        internal readonly FixedSetting<float> FixtureDepthMeters = FixedSetting.Of(1.75f);
        internal readonly FixedSetting<float> FrontClearanceMeters = FixedSetting.Of(1.35f);
        internal readonly FixedSetting<float> MinEntranceDistanceMeters = FixedSetting.Of(24f);
        internal readonly FixedSetting<float> CaptureCenterX = FixedSetting.Of(0f);
        internal readonly FixedSetting<float> CaptureCenterY = FixedSetting.Of(1.05f);
        internal readonly FixedSetting<float> CaptureCenterZ = FixedSetting.Of(0.25f);
        internal readonly FixedSetting<float> CaptureWidthMeters = FixedSetting.Of(1.55f);
        internal readonly FixedSetting<float> CaptureHeightMeters = FixedSetting.Of(1f);
        internal readonly FixedSetting<float> CaptureDepthMeters = FixedSetting.Of(1.1f);
        internal readonly FixedSetting<float> EnemyStrikeRangeMeters = FixedSetting.Of(3.2f);
        internal readonly FixedSetting<float> EnemyStrikeCooldownSeconds = FixedSetting.Of(8f);

        internal readonly ConfigEntry<float> RepairHoldSeconds;
        internal readonly ConfigEntry<float> MalfunctionIntervalMinSeconds;
        internal readonly ConfigEntry<float> MalfunctionIntervalMaxSeconds;
        private readonly RiskTierTable<float> _requiredScrapPercent;

        internal WasteDisposalBalanceConfig(ConfigFile config)
        {
            RepairHoldSeconds = config.Bind(ContractedConfigSections.WasteDisposal, "Repair Hold Seconds", 4f,
                new ConfigDescription(
                    "Seconds a player must hold interact to repair a malfunctioned burn cycle.",
                    new AcceptableValueRange<float>(0.5f, 15f)));
            MalfunctionIntervalMinSeconds = config.Bind(ContractedConfigSections.WasteDisposal, "Malfunction Interval Min Seconds", 24f,
                new ConfigDescription(
                    "Host only. Shortest running interval before a periodic malfunction.",
                    new AcceptableValueRange<float>(5f, 180f)));
            MalfunctionIntervalMaxSeconds = config.Bind(ContractedConfigSections.WasteDisposal, "Malfunction Interval Max Seconds", 48f,
                new ConfigDescription(
                    "Host only. Longest running interval before a periodic malfunction.",
                    new AcceptableValueRange<float>(5f, 240f)));
            _requiredScrapPercent = RiskTierTable.BindFloat(config, ContractedConfigSections.WasteDisposal,
                "Required Moon Scrap Percent By Risk", "C=10, B=15, A=20, S=30", 1f, 100f,
                "Host only. Percentage of the generated moon scrap value that must be burned.");
        }

        internal Vector3 CaptureCenterLocal => new Vector3(CaptureCenterX.Value, CaptureCenterY.Value, CaptureCenterZ.Value);
        internal Vector3 CaptureSizeMeters => new Vector3(CaptureWidthMeters.Value, CaptureHeightMeters.Value, CaptureDepthMeters.Value);

        internal RiskConfig ForRisk(char risk) => new RiskConfig(this, risk);

        internal sealed class RiskConfig
        {
            internal readonly DerivedSetting<float> RequiredMoonScrapPercent;

            internal RiskConfig(WasteDisposalBalanceConfig owner, char risk)
            {
                RequiredMoonScrapPercent = owner._requiredScrapPercent.View(risk);
            }
        }
    }

    internal sealed class WhistleblowerBalanceConfig
    {
        // Diagnostics-only primitive placeholder, retired from the config surface (#694).
        internal readonly FixedSetting<bool> UseFallbackVisual = FixedSetting.Of(false);
        internal readonly ConfigEntry<bool> UseVanillaFriendlyModel;
        internal readonly ConfigEntry<bool> RoamWhileHiding;
        internal readonly ConfigEntry<int> FleeDistanceTiles;
        private readonly RiskTierTable<int> _health;

        // #966 S14 U4: the host keys the Blacksite witness escort owns. They are bound here,
        // beside the contract's other NPC behaviour keys, and copied onto
        // BlacksiteCustodySettings by ApplyCustodySettings at bootstrap. #1293 R2: the leash,
        // its grace and the pace multiplier are gone with the follow they tuned; the witness is
        // attached to his escort.
        internal readonly ConfigEntry<bool> BlacksiteEscortEnabled;
        internal readonly ConfigEntry<bool> CustodyBrokenByKnockdown;

        // Fixed, per the FixedSetting convention above: a reach and a backoff fitted to the two
        // bodies of an escort, not balance a player would reach for. The derivations are #963
        // S14 U1's and are restated in docs/CONTRACT_BALANCE_CONFIGURATION.md.
        //
        // Take distance 1.2 m: the reviewed take reach, body to body.
        internal readonly FixedSetting<float> CustodyTakeDistance =
            FixedSetting.Of(BlacksiteCustodySettings.CustodyTakeDistance);
        // Re-take backoff 15 s: the reviewed number (review-whistleblower-escort.md 3.11). It is
        // what makes a knockdown or a killed escort buy the crew time rather than one lost tick.
        internal readonly FixedSetting<float> CustodyRetakeBackoffSeconds =
            FixedSetting.Of(BlacksiteCustodySettings.CustodyRetakeBackoffSeconds);

        // Spawn-candidate scoring internals; identical across tiers and never gameplay
        // tuning a player would reach for. Retired from the config surface (#694).
        private readonly FixedSetting<float> _minSpawnPathDistance = FixedSetting.Of(80f);
        private readonly FixedSetting<float> _minSpawnHorizontalDistance = FixedSetting.Of(45f);
        private readonly FixedSetting<float> _minSpawnTileDepth = FixedSetting.Of(0.65f);
        private readonly FixedSetting<float> _topSpawnScorePercent = FixedSetting.Of(0.75f);

        internal WhistleblowerBalanceConfig(ConfigFile config)
        {
            UseVanillaFriendlyModel = config.Bind(ContractedConfigSections.Whistleblower, "Use Vanilla-Friendly Model", true,
                "Swap the Whistleblower to a Lethal Company scavenger-based model so it reads as a " +
                "vanilla asset. Requires a lgucontracts.bundle version that contains the " +
                "WhistleblowerRuntimeVisualVanilla prefab; older bundles fall back to the default model.");
            RoamWhileHiding = config.Bind(ContractedConfigSections.Whistleblower, "Roam While Hiding", true,
                "Host only. Before he is discovered the Whistleblower wanders the facility like a " +
                "vanilla enemy instead of standing where he spawned. He still stops and fights when " +
                "a player gets line of sight on him.");
            FleeDistanceTiles = config.Bind(ContractedConfigSections.Whistleblower, "Flee Distance Tiles",
                5,
                new ConfigDescription(
                    "Host only. How many facility tiles (rooms and corridors) the Whistleblower runs " +
                    "through before he hides again after an escape. Counted over the generated " +
                    "dungeon's tile graph; when that graph is unavailable an equivalent path " +
                    "distance is used instead.",
                    new AcceptableValueRange<int>(2, 12)));
            _health = RiskTierTable.BindInt(config, ContractedConfigSections.Whistleblower,
                "Health By Risk", "C=20, B=28, A=36, S=45", 1, 200,
                "Host only. Whistleblower hit points. Vanilla weapon force is applied directly.");

            // #966 S14 U4.
            BlacksiteEscortEnabled = config.Bind(ContractedConfigSections.Whistleblower,
                "Blacksite Escort Enabled", BlacksiteCustodySettings.EscortEnabled,
                "Host only. On a Whistleblower contract with a Blacksite raid, the recovery team " +
                "takes the witness into custody and marches him to their ship; if it leaves with " +
                "him aboard the contract voids. Off, the squad raids scrap on this contract as it " +
                "did before. The switch is read when an escort takes him, so turning it off " +
                "mid-round does not free a witness already in custody.");
            CustodyBrokenByKnockdown = config.Bind(ContractedConfigSections.Whistleblower,
                "Custody Broken By Knockdown", BlacksiteCustodySettings.CustodyBrokenByKnockdown,
                "Host only. Whether a heavy hit that puts the witness on the floor frees him from " +
                "his escort. This is the crew's cheap, repeatable stall; off, only killing him or " +
                "killing or stunning the escort ends custody.");
        }

        /// <summary>
        /// #966 S14 U4. Copies the custody keys onto <see cref="BlacksiteCustodySettings"/>
        /// and keeps them there when a value is edited in a live session. Called from the
        /// bootstrap immediately after construction, the same way
        /// <c>SurveyHeldPoseTuning.Bind</c> is, rather than from the constructor: a config schema
        /// in this file binds keys and nothing else, and the runtime statics it feeds live in a
        /// different subsystem.
        /// </summary>
        internal void ApplyCustodySettings()
        {
            CopyCustodySettings();
            BlacksiteEscortEnabled.SettingChanged += (_, __) => CopyCustodySettings();
            CustodyBrokenByKnockdown.SettingChanged += (_, __) => CopyCustodySettings();
        }

        private void CopyCustodySettings()
        {
            BlacksiteCustodySettings.EscortEnabled = BlacksiteEscortEnabled.Value;
            BlacksiteCustodySettings.CustodyBrokenByKnockdown = CustodyBrokenByKnockdown.Value;
        }

        internal RiskConfig ForRisk(char risk) => new RiskConfig(this, risk);

        internal sealed class RiskConfig
        {
            internal readonly DerivedSetting<int> Health;
            internal readonly FixedSetting<float> MinSpawnPathDistance;
            internal readonly FixedSetting<float> MinSpawnHorizontalDistance;
            internal readonly FixedSetting<float> MinSpawnTileDepth;
            internal readonly FixedSetting<float> TopSpawnScorePercent;

            internal RiskConfig(WhistleblowerBalanceConfig owner, char risk)
            {
                Health = owner._health.View(risk);
                MinSpawnPathDistance = owner._minSpawnPathDistance;
                MinSpawnHorizontalDistance = owner._minSpawnHorizontalDistance;
                MinSpawnTileDepth = owner._minSpawnTileDepth;
                TopSpawnScorePercent = owner._topSpawnScorePercent;
            }
        }
    }
}
