using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Y4NGZCompany.Facility.Interior.Placement.Authored;

using Y4NGZCompany.Contracts.ContainmentBreach;
using Y4NGZCompany.Contracts.Defuse;
using Y4NGZCompany.Contracts.Payload;
using Y4NGZCompany.Contracts.PestControl;
using Y4NGZCompany.Contracts.WasteDisposal;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Contracts.Survey;
using Y4NGZCompany.Contracts.Whistleblower;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
using Y4NGZCompany.Escalations.Congregation;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.Core;
namespace Y4NGZCompany.Bootstrap
{
    /// <summary>How crew deaths raise the quota (#694, replaces the old Enabled/Dynamic pair).</summary>
    public enum QuotaDeathPenaltyMode
    {
        Off,
        Static,
        Dynamic
    }

    internal static class ContractsBootstrap
    {
        // #612 task 1.4: contracts stay in Contracted, which keeps the historical id. Named
        // through the Core table rather than through Plugin.Guid so every bootstrap's owner id
        // reads from one place and the split's three ids are visible side by side.
        public const string PLUGIN_GUID = Y4NGZCore.Lifecycle.ModuleHarmonyIds.Contracted;
        public const string PLUGIN_NAME = "Y4NGZCompany Contracts";
        public const string PLUGIN_VERSION = "1.0.5";

        internal static ManualLogSource Log;
        internal static ConfigEntry<string> ContractMoonWeights;
        internal static ConfigEntry<string> ContractConstellationWeights;
        internal static ConfigEntry<string> ContractRewardPenaltyPercentByTier;
        internal static ConfigEntry<int> StashExtraAmmoRolls;
        internal static ConfigEntry<string> StashAmmoItems;
        internal static ConfigEntry<bool> QuotaOverhaulEnabled;
        internal static ConfigEntry<int> QuotaStartingQuota;
        internal static ConfigEntry<bool> QuotaPlayerMultiplierEnabled;
        internal static ConfigEntry<float> QuotaPlayerMultiplierPerPlayer;
        internal static ConfigEntry<int> QuotaPlayerMultiplierThreshold;
        internal static ConfigEntry<int> QuotaPlayerMultiplierCap;
        internal static ConfigEntry<int> QuotaBaseIncreasePercent;
        // #694: the old Enabled + Dynamic boolean pair collapsed into one mode entry;
        // these derived views keep the QuotaOverhaulRules call sites unchanged.
        internal static ConfigEntry<QuotaDeathPenaltyMode> QuotaDeathPenaltyModeEntry;
        internal static DerivedSetting<bool> QuotaDeathPenaltyEnabled;
        internal static DerivedSetting<bool> QuotaDeathPenaltyDynamic;
        internal static ConfigEntry<float> QuotaDeathPenaltyPercentPerPlayer;
        internal static ConfigEntry<float> QuotaDeathPenaltyDynamicCap;
        internal static readonly FixedSetting<float> QuotaDeathPenaltyThreshold = FixedSetting.Of(0.25f);
        internal static ConfigEntry<float> QuotaDeathPenaltyRecoveryBonus;
        internal static ConfigEntry<string> QuotaHazardPayPercentByWeather;
        // #671: client-local. Read every frame by VanillaControlTipMirror, so a live flip
        // takes effect without a restart.
        internal static ConfigEntry<bool> FlattenVanillaControlTips;
        // #1197/#1343: client-local objective hints. Read live on the RoundManager.Update tick.
        internal static ConfigEntry<bool> ObjectiveHintsEnabled;
        // #1343: a separate client-local one-shot accent; the master gate also applies.
        internal static ConfigEntry<bool> ObjectiveFirstSightGlintEnabled;
        internal static ConfigEntry<float> ObjectiveHintDelaySeconds;
        internal static ConfigEntry<float> ObjectiveHintBlinkSeconds;
        internal static ConfigEntry<float> ObjectiveHintRepeatSeconds;
        // #1343: local HUD one-shots, never sent over the network.
        internal static ConfigEntry<bool> ContractHudAudioEnabled;
        internal static ConfigEntry<float> ContractHudAudioVolume;
        internal static BlackoutBalanceConfig BlackoutBalance;
        internal static SurveyBalanceConfig SurveyBalance;
        internal static ShadowRaidBalanceConfig ShadowRaidBalance;
        internal static PayloadBalanceConfig PayloadBalance;
        internal static WasteDisposalBalanceConfig WasteDisposalBalance;
        internal static ContainmentBalanceConfig ContainmentBalance;
        internal static WhistleblowerBalanceConfig WhistleblowerBalance;
        // #694: one Escalations section with a master switch; each escalation's
        // Enabled view is master && its own entry so consumers stay unchanged.
        internal static ConfigEntry<bool> EscalationsEnabled;
        internal static ConfigEntry<float> EscalationNoneChance;
        private static ConfigEntry<bool> _strangerEnabledEntry;
        private static ConfigEntry<bool> _blacksiteEnabledEntry;
        private static ConfigEntry<bool> _congregationEnabledEntry;
        internal static DerivedSetting<bool> StrangerEscalationEnabled;
        internal static DerivedSetting<bool> BlacksiteRetrievalEnabled;
        internal static DerivedSetting<bool> CultEscalationEnabled;
        internal static ConfigEntry<float> StrangerEscalationChance;
        internal static ConfigEntry<bool> StrangerEscalationForce;
        internal static ConfigEntry<float> StrangerEscalationMinDelaySeconds;
        internal static ConfigEntry<float> StrangerEscalationMaxDelaySeconds;
        internal static ConfigEntry<float> BlacksiteRetrievalChance;
        internal static ConfigEntry<bool> BlacksiteRetrievalForce;
        internal static ConfigEntry<float> BlacksiteRetrievalMinDelaySeconds;
        internal static ConfigEntry<float> BlacksiteRetrievalMaxDelaySeconds;
        internal static ConfigEntry<int> BlacksiteRetrievalSoldierCount;
        internal static ConfigEntry<bool> BlacksiteRetrievalUseVanillaFriendlyModel;
        internal static ConfigEntry<float> CultEscalationChance;
        internal static ConfigEntry<bool> CultEscalationForce;
        internal static ConfigEntry<float> CultEscalationMinDelaySeconds;
        internal static ConfigEntry<float> CultEscalationMaxDelaySeconds;
        internal static ConfigEntry<int> CultEscalationRedCount;
        internal static ConfigEntry<int> CultEscalationBlackCount;
        internal static ConfigEntry<int> CultEscalationWhiteCount;
        internal static ConfigEntry<int> CultistHealth;
        internal static ConfigEntry<int> CultistAttackDamage;
        // Congregation and Seraphim encounter micro-tuning retired from the config
        // surface (#694); these hold the shipped defaults.
        // #845 D: the ceremony runs on progress, not phase timers. Defaults live beside the
        // Unity-free model (CongregationProgress) so the regression suite drives the same numbers.
        internal static readonly FixedSetting<float> CultCeremonyRedPrayProgress = FixedSetting.Of(CongregationProgress.DefaultRedPrayThreshold);
        internal static readonly FixedSetting<float> CultCeremonyAllPrayProgress = FixedSetting.Of(CongregationProgress.DefaultAllPrayThreshold);
        internal static readonly FixedSetting<float> CultCeremonyBeatProgress = FixedSetting.Of(CongregationProgress.DefaultBeatProgress);
        internal static readonly FixedSetting<float> CultCeremonyPrayLoopProgress = FixedSetting.Of(CongregationProgress.DefaultPrayLoopProgress);
        internal static readonly FixedSetting<float> CultCeremonyAllPresentProgressPerSecond = FixedSetting.Of(CongregationProgress.DefaultAllPresentProgressPerSecond);
        internal static readonly FixedSetting<float> CultCeremonyStallRuinSeconds = FixedSetting.Of(CongregationProgress.DefaultStallRuinSeconds);
        internal static readonly FixedSetting<float> CultCeremonySpeedUpEtaSeconds = FixedSetting.Of(CongregationProgress.DefaultSpeedUpEtaSeconds);
        internal static readonly FixedSetting<float> CultCeremonySpeedUpProgress = FixedSetting.Of(CongregationProgress.DefaultSpeedUpProgress);
        internal static readonly FixedSetting<float> CultCeremonyDrumSpeedUpTempo = FixedSetting.Of(CongregationProgress.DefaultDrumSpeedUpTempo);
        internal static readonly FixedSetting<float> CultCeremonyDrumSpeedUpRampSeconds = FixedSetting.Of(CongregationProgress.DefaultDrumSpeedUpRampSeconds);
        internal static readonly FixedSetting<float> CultEscalationCloseDetectionRadius = FixedSetting.Of(5.5f);
        internal static readonly FixedSetting<float> CultEscalationSoundDetectionRadius = FixedSetting.Of(18f);
        internal static readonly FixedSetting<float> CultEscalationStareSeconds = FixedSetting.Of(2f);
        internal static readonly FixedSetting<float> CultistAggroMoveSpeed = FixedSetting.Of(2.15f);
        internal static readonly FixedSetting<float> CultistAttackRange = FixedSetting.Of(1.65f);
        internal static readonly FixedSetting<float> CultistAttackCooldownSeconds = FixedSetting.Of(1.35f);
        internal static readonly FixedSetting<float> CultSeraphimScale = FixedSetting.Of(3f);
        internal static readonly FixedSetting<float> CultSeraphimHoverHeight = FixedSetting.Of(20f);
        internal static readonly FixedSetting<float> CultSeraphimRiseDurationSeconds = FixedSetting.Of(4.5f);
        internal static readonly FixedSetting<float> CultSeraphimBeamRange = FixedSetting.Of(170f);
        internal static readonly FixedSetting<float> CultSeraphimAttackRange = FixedSetting.Of(42f);
        internal static readonly FixedSetting<float> CultSeraphimBeamHoldSeconds = FixedSetting.Of(3f);
        internal static readonly FixedSetting<float> CultSeraphimAttackCooldownSeconds = FixedSetting.Of(7.5f);
        internal static readonly FixedSetting<float> CultSeraphimPeriodicVoiceSeconds = FixedSetting.Of(20f);
        internal static readonly FixedSetting<bool> CultSeraphimCullCultistsOnSpawn = FixedSetting.Of(true);
        // Developer-only debug spawns and per-contract force/trace toggles retired
        // from the config surface (#694); flip them in code when diagnosing.
        internal static readonly FixedSetting<bool> InteriorInjectionDebugEnabled = FixedSetting.Of(false);
        internal static readonly FixedSetting<string> InteriorInjectionDebugPrefabName = FixedSetting.Of("WhistleblowerRuntimeVisual");
        internal static ConfigEntry<bool> InteriorPlacementDetailedRejectLogging;
        internal static ConfigEntry<float> DungeonSizeSoftCap;
        internal static readonly FixedSetting<bool> BlackoutLightAuditVerboseLogging = FixedSetting.Of(false);
        internal static readonly FixedSetting<bool> PestLeverFirstPersonDiagnostics = FixedSetting.Of(false);
        internal static ConfigEntry<bool> ShadowRaidDebugEnabled;
        internal static ConfigEntry<bool> BlacksiteUatOutputMuted;
        internal static ConfigEntry<bool> BlacksiteHelmetLightShadows;
        internal static ConfigEntry<bool> PerformanceTimingLogging;
        internal static ConfigEntry<bool> PerformanceReportPreviewEnabled;
        internal static ConfigEntry<float> BlacksiteDetailCaptureSeconds;
        internal static readonly FixedSetting<bool> ShadowRaidForceAllMoonsContract = FixedSetting.Of(false);
        // Bundled prefab wiring, not gameplay tuning; retired from the config surface (#694).
        internal static readonly FixedSetting<string> ShadowRaidDrillPrefabName = FixedSetting.Of("BreachDrill");
        internal static readonly FixedSetting<float> ShadowRaidDrillAttachDistance = FixedSetting.Of(1.4f);
        internal static readonly FixedSetting<string> ShadowRaidContainerPrefabName = FixedSetting.Of("Shipping Container 20 Foot");
        internal static readonly FixedSetting<string> ShadowRaidRewardPrefabName = FixedSetting.Of("BlacksiteCargoCrate");
        internal static ConfigEntry<bool> SuppressLguContractShop;
        internal static DefuseBalanceConfig DefuseBalance;
        internal static ContractSelectionConfig ContractSelection;
        private static Harmony _harmony;

        internal static void Initialize(ConfigFile Config, ManualLogSource Logger, Harmony harmony)
        {
            Log = Logger;
            Y4NGZCore.Diagnostics.ModuleLog.Contracts = Logger;
            // Shared facility code moved to Y4NGZCore; keep its output on this
            // plugin's log source so existing log lines read unchanged.
            Y4NGZCore.CoreDiagnostics.Log = Logger;

            // #453 / #612 task 1.4: Contracted's own known writer of the local player's
            // interact-gating flags, registered rather than listed inside the watchdog.
            Y4NGZCore.Diagnostics.InputWatchdogWriterRegistry.Register(
                typeof(Y4NGZCompany.Contracts.PestControl.PestControlLeverPlayerAnimation),
                "Contracts",
                "Release");

            FlattenVanillaControlTips = Config.Bind(
                ContractedConfigSections.Contracts,
                "Flatten Vanilla Control Tips",
                true,
                "Client only. Without Y4NGZUI installed, draws the vanilla control tips (Drop item : [G] and the like) flat and top-right beneath the contract card instead of at the tilted vanilla placement that overlaps it. False leaves the vanilla tips untouched. Ignored when Y4NGZUI is installed; its prompt overlay owns the tips.");

            ObjectiveHintsEnabled = Config.Bind(
                ContractedConfigSections.ObjectiveHints,
                "Enabled",
                true,
                "Client only. Master switch for objective hints: both the locate pity blink and the local first-sight glint. Nothing is sent over the network; every player has their own cues.");

            ObjectiveFirstSightGlintEnabled = Config.Bind(
                ContractedConfigSections.ObjectiveHints,
                "First Sight Glint",
                true,
                "Client only. Give the actual first-sighted Wesley side objective or completed Locate target one brief, subtle material-emission accent per target per round. Never shows through walls or during a pity blink, and does not change its timer. Requires Objective Hints / Enabled.");

            ObjectiveHintDelaySeconds = Config.Bind(
                ContractedConfigSections.ObjectiveHints,
                "Delay Seconds",
                180f,
                new ConfigDescription(
                    "Client only. Seconds you must spend alive inside the facility, with the objective still unfound, before the hint plays. Time outside the facility pauses the count; finding the objective resets it.",
                    new AcceptableValueRange<float>(0f, 3600f)));

            ObjectiveHintBlinkSeconds = Config.Bind(
                ContractedConfigSections.ObjectiveHints,
                "Blink Seconds",
                6f,
                new ConfigDescription(
                    "Client only. How long the outline blinks and the card word pulses each time the hint plays.",
                    new AcceptableValueRange<float>(ContractPityTimerSettings.MinBlinkSeconds, ContractPityTimerSettings.MaxBlinkSeconds)));

            ObjectiveHintRepeatSeconds = Config.Bind(
                ContractedConfigSections.ObjectiveHints,
                "Repeat Seconds",
                0f,
                new ConfigDescription(
                    "Client only. Seconds inside the facility after a hint before it plays again for the same objective. 0 plays it once per objective.",
                    new AcceptableValueRange<float>(0f, 3600f)));

            ContractHudAudioEnabled = Config.Bind(
                ContractedConfigSections.ContractHudAudio,
                "Enabled",
                true,
                "Client only. Play contract card, escalation band and objective hint one-shots through the vanilla HUD audio source.");

            ContractHudAudioVolume = Config.Bind(
                ContractedConfigSections.ContractHudAudio,
                "Volume",
                1f,
                new ConfigDescription(
                    "Client only. Master volume (0 to 1) for contract HUD one-shots; each cue has its own quieter mix level.",
                    new AcceptableValueRange<float>(0f, 1f)));

            ContractMoonWeights = Config.Bind(
                ContractedConfigSections.Contracts,
                "Moon Contract Weights",
                string.Empty,
                "Host only. Per-moon contract weight overrides. Format: MoonName:Contract=Weight,Contract=Weight;OtherMoon:Contract=Weight.");

            ContractConstellationWeights = Config.Bind(
                ContractedConfigSections.Contracts,
                "Constellation Contract Weights",
                string.Empty,
                "Host only. Per-constellation contract weight overrides when constellation data is available. Format: ConstellationName:Contract=Weight,Contract=Weight.");

            ContractRewardPenaltyPercentByTier = Config.Bind(
                ContractedConfigSections.Contracts,
                "Reward And Penalty Percent By Risk",
                ContractConfigRules.DefaultRewardPenaltyPercentByTier,
                "Host only. Quota reward on success and penalty on failure by moon risk tier. Each contract " +
                "section can override its own reward or penalty. Waste Disposal success instead credits the " +
                "exact burned scrap value. Format: D=15,C=15,B=20,A=25,S=30.");

            QuotaHazardPayPercentByWeather = Config.Bind(
                ContractedConfigSections.Contracts,
                "Hazard Pay Percent By Weather",
                QuotaOverhaulRules.DefaultHazardPayPercentByWeather,
                "Host only. Extra quota reduction on contract success by weather. Format: Eclipse=10,Stormy=5.");

            SuppressLguContractShop = Config.Bind(
                ContractedConfigSections.Contracts,
                "Suppress LGU Contract Shop",
                true,
                "Host only. When true, Lategame Upgrades' own contract purchases (its shop's random and specified moon entries, and the 'contract' terminal command) are refused on the host, because Contracted assigns the contract for every moon itself. Clients are always refused whatever their own value says: LGU's purchase asks the host to overwrite the assignment for everyone, so only the host's value decides. Ignored when Lategame Upgrades is not installed.");

            ContractSelection = new ContractSelectionConfig(Config);

            StashExtraAmmoRolls = Config.Bind(
                ContractedConfigSections.CompanyStashLoot,
                "Extra Loot Rolls",
                2,
                "Host only. Weighted loot rolls after the Company Stash Gold bar spawns. Only used when " +
                "LethalCCTV's Company Stashes are installed. Set to 0 to disable.");

            StashAmmoItems = Config.Bind(
                ContractedConfigSections.CompanyStashLoot,
                "Loot Items",
                // Refreshed 2026-08-06 (Y4NGZUpgrades release prep). The previous default still named
                // ".357 Rounds" and "Crossbow Bolt", two display names Y4NGZUpgrades retired in #148
                // and #150 — both rolls resolved to nothing. This set is the live Upgrades item names,
                // plus the three renamed firearms as rarer stash finds. Names are resolved by
                // Item.itemName at round time; a missing item is logged and skipped, so the stash
                // still works with Y4NGZUpgrades absent.
                "Shotgun Shells|30|1;Small Caliber Rounds|30|1;Heavy Caliber Rounds|30|1;Fuel Tank|15|1;Pistol|10|1;Bolt-Action Rifle|8|1;Assault Rifle|6|1",
                "Host only. Weighted Company Stash loot. Any network-spawnable grabbable item name works. " +
                "Format: name|weight|count; entries are separated by semicolons.");

            QuotaOverhaulEnabled = Config.Bind(
                ContractedConfigSections.Quota,
                "Enabled",
                true,
                "Host only. When true, Contracted manages base quota scaling, crew-loss quota penalties, and report-row quota modifiers. " +
                "When false, the whole Contracted performance-report extension (title, ledger row, notes, XP hold) also stands down and the vanilla report is left untouched on every machine.");

            QuotaStartingQuota = Config.Bind(
                ContractedConfigSections.Quota,
                "Starting Quota",
                200,
                new ConfigDescription(
                    "Host only. Base quota before first-quota player scaling.",
                    new AcceptableValueRange<int>(1, 1000000)));

            QuotaPlayerMultiplierEnabled = Config.Bind(
                ContractedConfigSections.Quota,
                "First Quota Player Scaling",
                true,
                "Host only. When true, the very first quota scales by player count.");

            QuotaPlayerMultiplierPerPlayer = Config.Bind(
                ContractedConfigSections.Quota,
                "First Quota Multiplier Per Player",
                0.25f,
                new ConfigDescription(
                    "Host only. Quota multiplier added for each counted player above the threshold on the first quota.",
                    new AcceptableValueRange<float>(0f, 10f)));

            QuotaPlayerMultiplierThreshold = Config.Bind(
                ContractedConfigSections.Quota,
                "First Quota Player Threshold",
                1,
                new ConfigDescription(
                    "Host only. Players up to and including this count do not increase the first quota.",
                    new AcceptableValueRange<int>(0, 32)));

            QuotaPlayerMultiplierCap = Config.Bind(
                ContractedConfigSections.Quota,
                "First Quota Player Cap",
                8,
                new ConfigDescription(
                    "Host only. Maximum player count used by first-quota scaling.",
                    new AcceptableValueRange<int>(1, 64)));

            QuotaBaseIncreasePercent = Config.Bind(
                ContractedConfigSections.Quota,
                "Quota Increase Percent",
                25,
                new ConfigDescription(
                    "Host only. Percent increase applied to the stored base quota whenever a quota is completed.",
                    new AcceptableValueRange<int>(0, 1000)));

            QuotaDeathPenaltyModeEntry = Config.Bind(
                ContractedConfigSections.Quota,
                "Death Penalty Mode",
                QuotaDeathPenaltyMode.Dynamic,
                "Host only. Off: crew deaths never raise the quota. Static: each death adds Death Penalty " +
                "Percent Per Player. Dynamic: the penalty scales from crew size up to Death Penalty Dynamic Cap.");
            QuotaDeathPenaltyEnabled = new DerivedSetting<bool>(
                () => QuotaDeathPenaltyModeEntry.Value != QuotaDeathPenaltyMode.Off);
            QuotaDeathPenaltyDynamic = new DerivedSetting<bool>(
                () => QuotaDeathPenaltyModeEntry.Value == QuotaDeathPenaltyMode.Dynamic);

            QuotaDeathPenaltyPercentPerPlayer = Config.Bind(
                ContractedConfigSections.Quota,
                "Death Penalty Percent Per Player",
                0.12f,
                new ConfigDescription(
                    "Host only. Static crew-loss quota penalty per dead player when Death Penalty Mode is Static.",
                    new AcceptableValueRange<float>(0f, 1f)));

            QuotaDeathPenaltyDynamicCap = Config.Bind(
                ContractedConfigSections.Quota,
                "Death Penalty Dynamic Cap",
                0.5f,
                new ConfigDescription(
                    "Host only. Maximum dynamic quota penalty fraction for a full crew wipe.",
                    new AcceptableValueRange<float>(0f, 1f)));

            QuotaDeathPenaltyRecoveryBonus = Config.Bind(
                ContractedConfigSections.Quota,
                "Death Penalty Recovery Bonus",
                0.5f,
                new ConfigDescription(
                    "Host only. Fraction of one player's crew-loss quota penalty removed for each recovered body.",
                    new AcceptableValueRange<float>(0f, 1f)));

            BlackoutBalance = new BlackoutBalanceConfig(Config);
            SurveyBalance = new SurveyBalanceConfig(Config);
            SurveyHeldPoseTuning.Bind(Config, Y4NGZCompany.Core.ContractedConfigSections.Survey);
            ShadowRaidBalance = new ShadowRaidBalanceConfig(Config);
            PayloadBalance = new PayloadBalanceConfig(Config);
            WasteDisposalBalance = new WasteDisposalBalanceConfig(Config);

            ContainmentBalance = new ContainmentBalanceConfig(Config);
            WhistleblowerBalance = new WhistleblowerBalanceConfig(Config);
            // #966 S14 U4: the witness-escort keys feed BlacksiteCustodySettings, which custody
            // reads every server tick. Applied here for the same reason SurveyHeldPoseTuning.Bind
            // is: the schema binds the keys, the bootstrap wires them to the runtime.
            WhistleblowerBalance.ApplyCustodySettings();

            EscalationsEnabled = Config.Bind(
                ContractedConfigSections.Escalations,
                "Enabled",
                true,
                "Host only. Master switch: when false, no escalation of any kind can occur.");

            EscalationNoneChance = Config.Bind(
                ContractedConfigSections.Escalations,
                "No Escalation Weight",
                0.55f,
                new ConfigDescription(
                    "Host only. Selection weight for landing with no escalation, weighed against each escalation's Chance.",
                    new AcceptableValueRange<float>(0f, 1f)));

            _strangerEnabledEntry = Config.Bind(
                ContractedConfigSections.Escalations,
                "Stranger Enabled",
                true,
                "Host only. If true, The Stranger can be selected as the one runtime escalation on valid moons.");
            StrangerEscalationEnabled = new DerivedSetting<bool>(
                () => EscalationsEnabled.Value && _strangerEnabledEntry.Value);

            StrangerEscalationChance = Config.Bind(
                ContractedConfigSections.Escalations,
                "Stranger Chance",
                0.15f,
                new ConfigDescription(
                    "Host only. Selection weight for The Stranger.",
                    new AcceptableValueRange<float>(0f, 1f)));

            StrangerEscalationForce = Config.Bind(
                ContractedConfigSections.Escalations,
                "Stranger Force Every Round",
                false,
                "Debug/testing toggle. When true, The Stranger is selected on every valid moon.");

            StrangerEscalationMinDelaySeconds = Config.Bind(
                ContractedConfigSections.Escalations,
                "Stranger Min Delay Seconds",
                120f,
                new ConfigDescription(
                    "Host only. Minimum seconds after landing before The Stranger can start.",
                    new AcceptableValueRange<float>(10f, 900f)));

            StrangerEscalationMaxDelaySeconds = Config.Bind(
                ContractedConfigSections.Escalations,
                "Stranger Max Delay Seconds",
                180f,
                new ConfigDescription(
                    "Host only. Maximum seconds after landing before The Stranger can start.",
                    new AcceptableValueRange<float>(10f, 1200f)));

            _blacksiteEnabledEntry = Config.Bind(
                ContractedConfigSections.Escalations,
                "Blacksite Retrieval Enabled",
                true,
                "Host only. If true, Blacksite Retrieval can occur on valid moons. The team retrieves supported contract objectives; on other contracts it raids facility scrap or the apparatus.");
            BlacksiteRetrievalEnabled = new DerivedSetting<bool>(
                () => EscalationsEnabled.Value && _blacksiteEnabledEntry.Value);

            BlacksiteRetrievalChance = Config.Bind(
                ContractedConfigSections.Escalations,
                "Blacksite Retrieval Chance",
                0.15f,
                new ConfigDescription(
                    "Host only. Selection weight for Blacksite Retrieval during an eligible contract.",
                    new AcceptableValueRange<float>(0f, 1f)));

            BlacksiteRetrievalForce = Config.Bind(
                ContractedConfigSections.Escalations,
                "Blacksite Retrieval Force Every Round",
                false,
                "Debug/testing toggle. Bypasses chance and delay, but never the contract mode table (Containment Breach is never raided) and never an unresolved contract reading.");

            BlacksiteRetrievalMinDelaySeconds = Config.Bind(
                ContractedConfigSections.Escalations,
                "Blacksite Retrieval Min Delay Seconds",
                150f,
                new ConfigDescription(
                    "Host only. Minimum seconds after landing before Blacksite Retrieval can start.",
                    new AcceptableValueRange<float>(10f, 900f)));

            BlacksiteRetrievalMaxDelaySeconds = Config.Bind(
                ContractedConfigSections.Escalations,
                "Blacksite Retrieval Max Delay Seconds",
                360f,
                new ConfigDescription(
                    "Host only. Maximum seconds after landing before Blacksite Retrieval can start.",
                    new AcceptableValueRange<float>(10f, 1200f)));

            BlacksiteRetrievalSoldierCount = Config.Bind(
                ContractedConfigSections.Escalations,
                "Blacksite Retrieval Soldier Count",
                6,
                new ConfigDescription(
                    "Host only. Number of military hostiles spawned by Blacksite Retrieval.",
                    new AcceptableValueRange<int>(1, 10)));

            BlacksiteRetrievalUseVanillaFriendlyModel = Config.Bind(
                ContractedConfigSections.Escalations,
                "Blacksite Retrieval Use Vanilla-Friendly Model",
                true,
                "Local visual choice. Use the tactical scavenger-style Blacksite soldier. False restores the previous model. " +
                "Older bundles fall back automatically. Requires a game restart.");

            _congregationEnabledEntry = Config.Bind(
                ContractedConfigSections.Escalations,
                "Congregation Enabled",
                true,
                "Host only. If true, The Congregation cult escalation can occur on valid moons.");
            CultEscalationEnabled = new DerivedSetting<bool>(
                () => EscalationsEnabled.Value && _congregationEnabledEntry.Value);

            CultEscalationChance = Config.Bind(
                ContractedConfigSections.Escalations,
                "Congregation Chance",
                0.15f,
                new ConfigDescription(
                    "Host only. Selection weight for The Congregation.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CultEscalationForce = Config.Bind(
                ContractedConfigSections.Escalations,
                "Congregation Force Every Round",
                false,
                "Debug/testing toggle. When true, The Congregation starts shortly after landing on every valid moon.");

            CultEscalationMinDelaySeconds = Config.Bind(
                ContractedConfigSections.Escalations,
                "Congregation Min Delay Seconds",
                150f,
                new ConfigDescription(
                    "Host only. Minimum seconds after landing before The Congregation can start.",
                    new AcceptableValueRange<float>(10f, 900f)));

            CultEscalationMaxDelaySeconds = Config.Bind(
                ContractedConfigSections.Escalations,
                "Congregation Max Delay Seconds",
                360f,
                new ConfigDescription(
                    "Host only. Maximum seconds after landing before The Congregation can start.",
                    new AcceptableValueRange<float>(10f, 1200f)));

            CultEscalationRedCount = Config.Bind(
                ContractedConfigSections.Escalations,
                "Congregation Red Cultists",
                2,
                new ConfigDescription(
                    "Host only. Number of red cultists spawned by The Congregation.",
                    new AcceptableValueRange<int>(0, 8)));

            CultEscalationBlackCount = Config.Bind(
                ContractedConfigSections.Escalations,
                "Congregation Black Cultists",
                3,
                new ConfigDescription(
                    "Host only. Number of black cultists spawned by The Congregation.",
                    new AcceptableValueRange<int>(0, 8)));

            CultEscalationWhiteCount = Config.Bind(
                ContractedConfigSections.Escalations,
                "Congregation White Cultists",
                1,
                new ConfigDescription(
                    "Host only. Number of white ritual cultists spawned by The Congregation.",
                    new AcceptableValueRange<int>(0, 3)));

            CultistHealth = Config.Bind(
                ContractedConfigSections.Escalations,
                "Congregation Cultist Health",
                3,
                new ConfigDescription(
                    "Host only. Hit points for ritual cultists.",
                    new AcceptableValueRange<int>(1, 12)));

            CultistAttackDamage = Config.Bind(
                ContractedConfigSections.Escalations,
                "Congregation Attack Damage",
                35,
                new ConfigDescription(
                    "Host only. Damage dealt by a cultist stab.",
                    new AcceptableValueRange<int>(5, 100)));

            // #694: the CCTV Security surface (former sections 96-98) is owned by
            // LethalCCTV's own config file; Contracted no longer binds any of it.

            InteriorPlacementDetailedRejectLogging = Config.Bind(
                ContractedConfigSections.Diagnostics,
                "Detailed Placement Reject Logging",
                false,
                "When true, logs every rejected interior placement candidate. Leave false during normal play; failed plans still log compact reject summaries.");

            // The placement layer lives in Y4NGZCore now and has no config of its
            // own, so hand it this toggle rather than have it reach back here.
            Y4NGZCore.CoreDiagnostics.DetailedPlacementRejectLoggingProvider =
                () => InteriorPlacementDetailedRejectLogging?.Value == true;

            DungeonSizeSoftCap = Config.Bind(
                ContractedConfigSections.Diagnostics,
                "Dungeon Size Soft Cap",
                0f,
                new ConfigDescription(
                    "Mitigation for slower clients hard-hanging inside DunGen on oversized interiors "
                    + "(y4ngz313/Y4NGZCompany#446: a 125-tile Alcatras Bunker at DungeonSize 2.4 wedged both clients "
                    + "mid-generation while the host waited forever on floor-generation acks). "
                    + "0 disables the cap and leaves the scalar exactly as the game and LethalLevelLoader set it - "
                    + "that value is NOT chosen by this mod. When set above 0, the effective DungeonSize "
                    + "(DunGen's LengthMultiplier) is clamped to this value immediately before generation starts, "
                    + "on whichever peer is generating, and the clamp is logged loudly. "
                    + "Around 1.8 is a reasonable starting ceiling; lower it further only if clients still stall.",
                    new AcceptableValueRange<float>(0f, 5f)));

            ShadowRaidDebugEnabled = Config.Bind(
                ContractedConfigSections.Diagnostics,
                "Shadow Raid Debug Logging",
                false,
                "Enables rate-limited Shadow Raid and Blacksite Retrieval UAT diagnostics.");

            // #1305: frame-cost A/B switches.
            BlacksiteUatOutputMuted = Config.Bind(
                ContractedConfigSections.Diagnostics,
                "Blacksite UAT Output Muted",
                false,
                "Drops every Blacksite [UAT] line at the log sink after it is built; the [UAT][Cost] "
                + "line still prints. For log I/O A/B. Needs Shadow Raid Debug Logging on.");

            BlacksiteHelmetLightShadows = Config.Bind(
                ContractedConfigSections.Diagnostics,
                "Blacksite Helmet Light Shadows",
                true,
                "When off, Blacksite soldier helmet lights cast no shadows. Performance A/B.");

            PerformanceTimingLogging = Config.Bind(
                ContractedConfigSections.Diagnostics,
                "Performance Timing Logging",
                false,
                "Developer profiling switch for the per-frame timing reports. Off, a runtime pass "
                + "only reports once it costs more than a whole frame at 60 fps, and it reports at "
                + "Info; the one-time Harmony patch census drops to Debug. On, the investigation "
                + "thresholds and Warning levels come back, so a pass over 5ms is reported loudly. "
                + "The once-per-round landing and HUD-reveal timing lines are unaffected either way.");

            // #1203: local debug-only report preview. Off binds no input and does nothing.
            PerformanceReportPreviewEnabled = Config.Bind(
                ContractedConfigSections.Diagnostics,
                "Performance Report Preview",
                false,
                "Developer layout aid. On, Ctrl+Alt+F11 steps the performance report through a "
                + "sample with six bottom-row cells, then five, then closed, in orbit or on a moon, "
                + "and Y4NGZDebugTools can open it too. The sample never reaches XP, tokens or the "
                + "save, and it will not open over a real round-end report. Local only; leave off "
                + "for normal play.");
            PerformanceReportPreview.Initialize(PerformanceReportPreviewEnabled);

            BlacksiteDetailCaptureSeconds = Config.Bind(
                ContractedConfigSections.Diagnostics,
                "Blacksite Detail Capture Seconds",
                8f,
                new ConfigDescription(
                    "Seconds of high-rate gait and motion detail per soldier after a failure "
                    + "transition (a stall or a refused motion request). 0 disables all high-rate "
                    + "detail: no [UAT][Gait] lines, no [UAT][Motion] heartbeat, no "
                    + "[UAT][MotionRequest] repeats. Transition lines are always emitted. "
                    + "A window that keeps re-opening is capped at three times this value.",
                    new AcceptableValueRange<float>(0f, 60f)));

            // #874 G5: initialise the capture window with the configured seconds.
            // A zero or negative value disables capture; IsDetailOpen always returns false.
            BlacksiteDiagnostics.DetailCapture.Configure(BlacksiteDetailCaptureSeconds.Value);

            // #874 G1: initialise the opt-in LOS caller capture probe.
            // The probe binds its own config entry and hooks SettingChanged internally.
            Y4NGZCompany.Experience.Diagnostics.EnemyPathLosCallerCapture.Initialize(Config, Logger);

            DefuseBalance = new DefuseBalanceConfig(Config);

            ContractAssetLoader.Load();
            AuthoredInteriorPlacementStore.Load();
            // #545: LethalCCTV owns the real mainframe/vault preview factory and assigns this
            // hook at its own bootstrap. Without LethalCCTV nothing ever claims it, the debug
            // GUI receives null for those kinds, and the capture UI aborts the edit session —
            // so claim the slot with proportioned primitive ghosts only while it is still
            // empty. Either load order stays correct: a factory LethalCCTV registered first is
            // kept, and one it registers later overwrites this fallback with the real prefabs.
            if (InteriorFixtureHooks.FixturePreviewFactory == null)
                InteriorFixtureHooks.FixturePreviewFactory = AuthoredInteriorPlacementDebugApi.CreateFixtureFallbackPreview;
            AuthoredPlacementCoverageReport.WriteReport("plugin startup");
            BreachDrillItemRegistry.Register();
            BreachHardDriveItemRegistry.Register();
            BlacksiteEquipmentAssets.Register();
            CongregationLoot.Register();
            SurveyBeaconItemRegistry.Register();
            WhistleblowerEnemyRegistry.Register();
            PestControlTrapRegistry.Register();
            WasteDisposalIncineratorRegistry.Register();
            BreachContainerController.PrepareNetworkPrefab();
            DefuseContract.PrepareNetworkPrefab();

            _harmony = harmony ?? new Harmony(PLUGIN_GUID);
            _harmony.PatchAll(typeof(StartOfRoundPatches));
            _harmony.PatchAll(typeof(MoonContractStartOfRoundPatches));
            _harmony.PatchAll(typeof(MoonContractShipMonitorPatches));
            _harmony.PatchAll(typeof(BlackoutDungeonFlowPatch));
            _harmony.PatchAll(typeof(BreachAuthoredTileInjectionCleanupPatch));
            // #446: generation heartbeat on every peer, host-side ack watchdog, DungeonSize soft cap.
            _harmony.PatchAll(typeof(DungeonGenerationWatchdogGeneratePatch));
            _harmony.PatchAll(typeof(DungeonGenerationWatchdogRuntimePatch));
            _harmony.PatchAll(typeof(DungeonGenerationWatchdogLifecyclePatches));
            _harmony.PatchAll(typeof(DungeonGenerationWatchdogDisconnectPatch));
            _harmony.PatchAll(typeof(BlackoutBreakerSpawnMapObjectPatch));
            _harmony.PatchAll(typeof(BlackoutRefreshLightsPatch));
            _harmony.PatchAll(typeof(BlackoutBreakerSwitchPatch));
            _harmony.PatchAll(typeof(VanillaDarkStartDiagnosticPatch));
            // #845 C: borrows the vanilla knife parts for CongregationLoot once Items are loaded.
            _harmony.PatchAll(typeof(CongregationLootPatches));
            // #1336 S3: a living Seraphim keeps its NavMeshAgent off instead of vanilla re-enabling it every frame.
            _harmony.PatchAll(typeof(SeraphimAgentEnablePatch));
            _harmony.PatchAll(typeof(MoonContractTerminalTextPatch));
            _harmony.PatchAll(typeof(DefuseTerminalStartPatch));
            _harmony.PatchAll(typeof(DefuseTerminalParsePatch));
            _harmony.PatchAll(typeof(DefuseTerminalQuitPatch));
            _harmony.PatchAll(typeof(DefuseTerminalHelpPatch));
            _harmony.PatchAll(typeof(MoonContractOptionalTerminalCompatibilityStartPatch));
            // #1219 H3: the renderer-independent contract list command.
            _harmony.PatchAll(typeof(MoonContractsTerminalCommandStartPatch));
            _harmony.PatchAll(typeof(MoonContractsTerminalCommandLoadPatch));
            _harmony.PatchAll(typeof(MoonContractsTerminalCommandHelpPatch));
            // #653: applied manually - PatchAll throws on its zero-target TargetMethods
            // when darmuh's TerminalStuff is absent, killing the rest of this list.
            MoonsPlusContractLabelPatch.Apply(_harmony);
            MoonContractOptionalTerminalCompatibility.Initialize(_harmony);
            // #802: applied by hand and only when LGU is loaded - the targets live in a foreign
            // assembly, so they cannot be named in a [HarmonyPatch] attribute.
            Y4NGZCompany.Core.Compatibility.LategameUpgradesShopSuppression.Apply(_harmony);
            // #1219 H2: 'contracts' prints our list while the LGU shop is suppressed. A prefix on
            // Interactive Terminal API's parser postfix, resolved by name like the patch above.
            MoonContractLguTerminalTakeover.Apply(_harmony);
            // #804: the objective-progress postfixes, same reasoning - LGU's contract objects
            // live in a foreign assembly, so their ClientRpcs are patched by hand and only when
            // LGU is loaded.
            Y4NGZCompany.Core.Compatibility.LategameUpgradesContractObjects.ApplyProgressPatches(_harmony);
            _harmony.PatchAll(typeof(MoonContractEscalationDepartureSweepPatch));
            _harmony.PatchAll(typeof(MoonContractEndOfRoundPatch));
            _harmony.PatchAll(typeof(QuotaOverhaulResetShipPatch));
            _harmony.PatchAll(typeof(QuotaOverhaulPlayerCountPatch));
            _harmony.PatchAll(typeof(QuotaOverhaulSetNewProfitQuotaPatch));
            _harmony.PatchAll(typeof(QuotaOverhaulSyncNewProfitQuotaPatch));
            _harmony.PatchAll(typeof(MoonContractPrePerformanceReportPatch));
            _harmony.PatchAll(typeof(MoonContractDaysLeftDelayPatch));
            _harmony.PatchAll(typeof(MoonContractPlayerLevelDelayPatch));
            MoonContractUpgradesXpUiDelayPatch.Apply(_harmony);
            _harmony.PatchAll(typeof(MoonContractPenaltyAnimationPatch));
            _harmony.PatchAll(typeof(MoonContractMergedPenaltyPatch));
            _harmony.PatchAll(typeof(MoonContractShipNotificationPatch));
            _harmony.PatchAll(typeof(SurveyDropshipOpenPatch));
            _harmony.PatchAll(typeof(SurveyBeaconCarryPatch));
            _harmony.PatchAll(typeof(ContainmentExitBlockPatch));
            // #1305 C2: a player pushing the Payload cart through an entrance takes it with him.
            _harmony.PatchAll(typeof(PayloadCartEntranceTeleportPatch));
            // Y4NGZ terminal renders contract labels directly through MoonContractApi.
            // #716/B3: WhistleblowerEnemySpawnPatch is gone — the contract mechanics branch
            // in MoonContractRuntimePatch already drives EnsureWhistleblowerTarget.
            _harmony.PatchAll(typeof(WhistleblowerEnemySpawnGuardPatch));
            PatchWhistleblowerIndexSpawnGuard();
            _harmony.PatchAll(typeof(MoonContractRuntimePatch));
            _harmony.PatchAll(typeof(BlacksiteRadMechAttackEventPatches));
            _harmony.PatchAll(typeof(BlacksiteNativeShotgunPatch));
            _harmony.PatchAll(typeof(BlacksiteSlimeRayPatch));
            _harmony.PatchAll(typeof(BlacksiteVanillaStrategyPatches));
            _harmony.PatchAll(typeof(BlacksiteVanillaUpdatePatches));
            _harmony.PatchAll(typeof(BlacksiteCoilObservationPatch));
            _harmony.PatchAll(typeof(BlacksiteBarberBeatPatch));
            _harmony.PatchAll(typeof(BlacksiteJesterCrankPatch));
            _harmony.PatchAll(typeof(BlacksiteCapturedPlayerCollisionPatches));
            _harmony.PatchAll(typeof(BlacksiteNativePlayerCapturePatches));
            _harmony.PatchAll(typeof(BlacksiteDogCollisionPatch));
            _harmony.PatchAll(typeof(BlacksiteFleaRescuePatch));
            _harmony.PatchAll(typeof(BlacksiteFleaPosePatch));
            _harmony.PatchAll(typeof(BlacksiteSpiderWebPatch));
            _harmony.PatchAll(typeof(BlacksiteVanillaHitClockPatches));
            // #947 S12 U1: applied by hand, not PatchAll, so a GiantKiwiAI member this build cannot
            // resolve logs once and disables the release instead of throwing out of plugin init.
            BlacksiteKiwiThreatPatches.Apply(_harmony);

            _harmony.PatchAll(typeof(MoonContractScrapGrabStatsPatch));
            _harmony.PatchAll(typeof(MoonContractEnemyHitStatsPatch));
            _harmony.PatchAll(typeof(MoonContractEnemyKillStatsPatch));
            // Payload push patches used to be applied on the Payload activation frame, where the
            // PlayerControllerB.Update transpiler dominated a 266 ms hitch (y4ngz313/Y4NGZCompany#299).
            // Both patch bodies early-out unless the local player is actively pushing a payload cart,
            // so applying them at plugin init is behaviour-neutral and moves the cost to load time.
            PayloadPushPatches.EnsureApplied();
            ContainmentBreachDirector.EnsureInstance();
            // #984: warm the Whistleblower foley here rather than on his Start. The pools are
            // eleven .ogg files streamed over UnityWebRequest, so loading them from Start left
            // his first swing, first hit and first steps silent. Plugin.CoroutineHost is the
            // plugin's own DontDestroyOnLoad host, so the load survives every scene change;
            // WhistleblowerEnemyAI.Start still calls EnsureLoaded as the fallback.
            WhistleblowerSoundLibrary.EnsureLoaded(Plugin.CoroutineHost);
            // #1233 S8: the Payload cart's ten clips, on the same host for the same reason. The
            // cart used to start this staged load on its own spawn, one clip per frame, and the
            // 27 September log put its 80.78 ms of main-thread decode across the nineteen frames
            // right after touchdown. PayloadCartController.OnNetworkSpawn still calls EnsureAudioLoads,
            // which is a no-op once the load has started.
            PayloadCartAudio.EnsureAudioLoads(Plugin.CoroutineHost);
            // #611 task 1.3: everything another feature reads out of Contracted goes through
            // Y4NGZCore from here on. Registered last, after the patches and the director, so a
            // consumer that resolves a capability the moment it appears finds the state behind
            // it already initialised.
            ContractedModuleProviders.EnsureRegistered();
            Log.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loaded.");
            Log.LogInfo($"[MoonContracts.Build] module={typeof(ContractsBootstrap).Assembly.ManifestModule.ModuleVersionId}, location={typeof(ContractsBootstrap).Assembly.Location}, blacksite=landing-gear-v1.");
        }

        private static void PatchWhistleblowerIndexSpawnGuard()
        {
            var legacySpawnMethod = AccessTools.Method(
                typeof(RoundManager),
                "SpawnEnemyGameObject",
                new[] { typeof(UnityEngine.Vector3), typeof(float), typeof(int) });

            if (legacySpawnMethod == null)
            {
                Log?.LogInfo("[MoonContracts] RoundManager.SpawnEnemyGameObject(Vector3,float,int) not present; skipping legacy Whistleblower index spawn guard.");
                return;
            }

            _harmony.PatchAll(typeof(WhistleblowerEnemyIndexSpawnGuardPatch));
        }
    }
}


