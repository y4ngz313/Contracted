using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace Y4NGZCompany.Core
{
    internal static class Y4NGZCompanyConfigMigration
    {
        // v17 (#1020 S6): subtractive. `Escalations / Blacksite Decision Core` is retired, because
        // the soldier has one decision core and the key selects nothing. The bump is what forces
        // the one migration that drops it: MigrateConfig returns early at
        // `previousSchemaVersion >= CurrentSchemaVersion`, so a v16 profile - which is every profile
        // that has ever seen the key, since it was added additively inside v16 - would otherwise
        // carry the orphaned line forever.
        internal const int CurrentSchemaVersion = 17;
        private const string MigrationSection = "00 - Migration";
        private const string MigrationVersionKey = "ConfigSchemaVersion";
        private const string MigrationFingerprintKey = "LegacySourceFingerprint";
        private const string MigrationStampFileName = "com.y4ngz.company.migration.complete";
        private const string LegacyMigrationStampFileName = "com.y4ngz.company.migration-v2.complete";

        /// <summary>
        /// The pre-fold standalone plugin config files this migration will read once, in this
        /// order, on a profile that has never been migrated.
        ///
        /// <para><b>Order is load-bearing:</b> <see cref="MergeLegacySourceValues"/> keys on
        /// (section, key) across ALL of these, and the first file to supply a pair wins. Two of
        /// these files can legitimately carry the same generic section name — <c>[Settings]</c>
        /// and <c>[Diagnostics]</c> are not unique to any one plugin — so reordering or removing
        /// an entry silently changes which file's value a colliding key takes.</para>
        ///
        /// <para><b>#613 task 2.1 and #614 task 3.1 did NOT remove the ship-side and takeover
        /// entries, and that is deliberate.</b> Both features left this assembly and neither's
        /// sections may be imported here any more, but the refusal is enforced by
        /// <see cref="ResolvesToShipSystemsSection"/> and
        /// <see cref="ResolvesToMonitorTakeoverSection"/> at the point of import rather than by
        /// deleting the source. Two reasons. First, the predicates guard values arriving from
        /// <em>any</em> of these files, not just the feature's own — <c>[Terminal]</c> in
        /// <c>lguterminal.cfg</c> and <c>[Settings]</c> in any of them both resolve to another
        /// plugin's sections — so deleting a file would close one door and leave the others open.
        /// Second, and specifically for <c>com.y4ngz.lgumonitortakeover.cfg</c>: not every mapping
        /// its sections can produce lands on a Takeover-owned destination.
        /// <see cref="GetDefinitionMigration"/> maps <c>[Diagnostics]</c> to
        /// <c>79 - Monitor Takeover - Diagnostics</c> for exactly two keys
        /// (<c>Verbose Logging</c>, <c>Dump Monitor Hierarchy On Orbit</c>) and returns
        /// <see cref="DefinitionMigrationAction.None"/> for every other key in that section, which
        /// means those keys still import into Contracted's own <c>[Diagnostics]</c>. The key gate
        /// exists precisely because that section name is shared. Dropping this file from the list
        /// would hand any such colliding key to <c>com.lawson.lethalcctv.cfg</c> or
        /// <c>com.lawsonwww.bundy.cfg</c> instead, because both sit after it here — a behaviour
        /// change on an import path Contracted still owns, in exchange for nothing the predicate
        /// does not already do.</para>
        /// </summary>
        private static readonly string[] LegacyConfigFiles =
        {
            "com.lguhud.contracthud.cfg",
            "com.y4ngz.lgushipsystems.cfg",
            "com.y4ngz.lgushop.cfg",
            "com.y4ngz.lguterminal.cfg",
            "com.y4ngz.lgumonitortakeover.cfg",
            "com.lawson.lethalcctv.cfg",
            "com.lawsonwww.bundy.cfg"
        };

        internal static void MigrateConfigSafely(ConfigFile config, ManualLogSource log)
        {
            try
            {
                MigrateConfig(config, log);
            }
            catch (Exception ex)
            {
                log.LogError($"Config migration failed; continuing startup with the current config: {ex}");
                try { config.Reload(); }
                catch (Exception reloadEx) { log.LogWarning($"Config reload after migration failure also failed: {reloadEx.Message}"); }
            }

            try
            {
                MigrateLocalDataDirectories(log);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Local data directory migration failed; continuing startup: {ex}");
            }
        }

        // #155: local placement/authoring data used to be written into
        // directories named after plugins that no longer exist. The writers
        // now target Y4NGZCompanyPaths.LocalDataDir; this moves existing user
        // data (camera placements, station poses, battery placements — real
        // authoring work) to the unified directory. Runs every startup and is
        // idempotent: files only move when the target does not already have
        // them, so an old directory reappearing from a backup is re-migrated
        // instead of silently coming back to life.
        private static readonly string[] LegacyLocalDataDirNames =
        {
            "LethalCCTV",
            "LGUContractHUD",
            "LGUShipSystems"
        };

        // AuthoredInteriorPlacementStore imports this file itself with kind
        // filters and one-shot import stamps, and its name collides with the
        // store's canonical profile in the target directory. Moving it
        // wholesale would bypass those filters — leave it to the store.
        private const string StoreOwnedLegacyFileName = "interior-authored-placements.local.json";

        private static void MigrateLocalDataDirectories(ManualLogSource log)
        {
            foreach (string dirName in LegacyLocalDataDirNames)
            {
                string sourceDir = Path.Combine(Paths.ConfigPath, dirName);
                if (!Directory.Exists(sourceDir))
                    continue;

                int moved = 0;
                int left = 0;
                foreach (string sourceFile in Directory.GetFiles(sourceDir))
                {
                    string fileName = Path.GetFileName(sourceFile);
                    if (string.Equals(fileName, StoreOwnedLegacyFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        left++;
                        continue;
                    }

                    string targetFile = Path.Combine(Y4NGZCompanyPaths.LocalDataDir, fileName);
                    if (File.Exists(targetFile))
                    {
                        left++;
                        continue;
                    }

                    Directory.CreateDirectory(Y4NGZCompanyPaths.LocalDataDir);
                    File.Move(sourceFile, targetFile);
                    moved++;
                }

                if (Directory.GetFileSystemEntries(sourceDir).Length == 0)
                    Directory.Delete(sourceDir);

                if (moved > 0 || left > 0)
                {
                    log.LogInfo(
                        $"Local data migration '{dirName}' -> '{Y4NGZCompanyPaths.LocalDataDirName}': " +
                        $"moved={moved}, left={left}.");
                }
            }
        }

        private static void MigrateConfig(ConfigFile config, ManualLogSource log)
        {
            string path = Path.Combine(Paths.ConfigPath, Plugin.Guid + ".cfg");
            Dictionary<ConfigDefinition, string> values = ReadConfigValues(path);
            string stampPath = Path.Combine(Paths.ConfigPath, MigrationStampFileName);
            string legacyStampPath = Path.Combine(Paths.ConfigPath, LegacyMigrationStampFileName);
            string fingerprint = ComputeLegacySourceFingerprint();
            int previousSchemaVersion = GetSchemaVersion(values);

            if (previousSchemaVersion >= CurrentSchemaVersion)
            {
                EnsureMigrationStamp(stampPath, fingerprint);
                return;
            }

            bool importExternalLegacy = previousSchemaVersion == 0
                && !HasMigrationStamp(stampPath, 1)
                && !HasMigrationStamp(legacyStampPath, 2);

            Dictionary<string, string> sectionMoves = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Phase 0 Diagnostics"] = "89 - CCTV - Diagnostics",
                ["Phase 1 Camera Exclusion"] = "81 - CCTV - Camera Selection",
                ["Phase 1 Camera Placement"] = "82 - CCTV - Camera Placement",
                ["Phase 1 Monitor Transform"] = "83 - CCTV - Ship Monitor",
                ["Phase 1.7 Display"] = "85 - CCTV - Display and Lighting",
                ["Phase 1.8 Room-Aware Placement"] = "81 - CCTV - Camera Selection",
                ["Phase 2 Ship-Side Hijack"] = "83 - CCTV - Ship Monitor",
                ["Phase 2 Render Performance"] = "84 - CCTV - Rendering",
                ["Phase 2.0 Legibility Probe"] = "89 - CCTV - Diagnostics",
                ["Phase 2.1 Camera Placement Review"] = "89 - CCTV - Diagnostics",
                ["Phase 2.2 Physical Camera Visuals"] = "86 - CCTV - Physical Cameras",
                ["Phase 2.3 Mainframe Security"] = "86 - CCTV - Physical Cameras",
                ["Phase X Focus Mouselook"] = "87 - CCTV - Operator Controls",
                ["Phase 2 CCTV Exposure"] = "88 - CCTV - Exposure",
                ["CCTV Operator Animation"] = "87 - CCTV - Operator Controls",
                ["Main"] = "75 - Shop - Shared",
                ["Display"] = "75 - Shop - Display",
                ["Categories"] = "76 - Shop - Categories",
                ["Notifications"] = "74 - Terminal - Notifications",
                ["General"] = "78 - Ship Systems - Shared",
                ["Requisition"] = "77 - Ship Upgrades - Requisition",
                ["Power"] = "78 - Ship Systems - Power",
                ["Fuel"] = "78 - Ship Systems - Fuel",
                ["Fuel Sources"] = "78 - Ship Systems - Fuel Sources",
                ["Fuel Loot"] = "78 - Ship Systems - Fuel Loot",
                ["Battery"] = "78 - Ship Systems - Battery",
                ["Quota Overhaul"] = "04 - Quota Overhaul",
                ["Escalations.Runtime"] = "90 - Escalations - Shared",
                ["Escalations.The Stranger"] = "91 - Escalations - Stranger",
                ["Escalations.Blacksite Retrieval"] = "92 - Escalations - Blacksite Retrieval",
                ["Escalations.The Congregation"] = "93 - Escalations - Congregation",
                ["Escalations.The Congregation.Seraphim"] = "94 - Escalations - Congregation Seraphim",
                ["Interior Injection"] = "99 - Diagnostics",
                ["CCTV Support"] = "95 - Interior Support",
                ["CCTV Security"] = "96 - CCTV Security - Shared",
                ["CCTV Security.Alarms"] = "97 - CCTV Security - Alarms",
                ["CCTV Security.Protocols"] = "98 - CCTV Security - Protocols",
                // Schema v7: reactive stings and interior ambience became one module
                // sharing one mixer bus and one duck, so they share one section.
                ["05 - Reactive Audio"] = "05 - Audio",
                ["07 - Interior Ambience"] = "05 - Audio"
            };

            // #613 task 2.1 (V1). The table above is built before the legacy merge, not after,
            // because the merge now has to know where each legacy value WOULD land in order to
            // refuse the ones bound for a Ship Systems section. See MergeLegacySourceValues.
            int externalSkippedShipSystems;
            int externalSkippedMonitorTakeover = 0;
            int externalImported = importExternalLegacy
                ? MergeLegacySourceValues(
                    values, sectionMoves, out externalSkippedShipSystems, out externalSkippedMonitorTakeover)
                : (externalSkippedShipSystems = 0);

            Dictionary<ConfigDefinition, string> normalized = new Dictionary<ConfigDefinition, string>();
            foreach (KeyValuePair<ConfigDefinition, string> pair in values)
            {
                if (!IsLegacyDefinition(pair.Key, sectionMoves))
                    normalized[pair.Key] = pair.Value;
            }

            int migrated = MigrateContractSelectionValues(values, normalized);
            migrated += MigrateContainmentValues(values, normalized);
            migrated += MigrateCoreContractRiskValues(values, normalized);
            migrated += MigrateWhistleblowerValues(values, normalized);
            migrated += MigrateDefuseValues(values, normalized);
            migrated += MigrateUiThemeValues(normalized);
            int removed = 0;
            foreach (KeyValuePair<ConfigDefinition, string> pair in values)
            {
                DefinitionMigrationAction action = GetDefinitionMigration(pair.Key, sectionMoves, out ConfigDefinition target);
                if (action == DefinitionMigrationAction.Remove)
                {
                    removed++;
                    continue;
                }
                if (action != DefinitionMigrationAction.Map)
                    continue;

                if (!normalized.ContainsKey(target))
                    normalized[target] = pair.Value;
                migrated++;
            }

            // Runs AFTER the section mapping, not before it. This seeds "05 - Audio"
            // from the pre-#152 com.y4ngz.upgrades.cfg, and since v7 made
            // "05 - Reactive Audio" a moved section its live values no longer survive
            // the first pass -- they arrive through the loop above. Seeding first
            // would let a stale upgrades.cfg value claim the key and silently beat
            // the setting the player is actually running.
            migrated += MigrateReactiveAudioValues(normalized);
            if (previousSchemaVersion < 8)
                migrated += MigrateCctvSecurityDefaults(normalized);
            if (previousSchemaVersion < 10)
                migrated += MigrateChainsawWalkSpeedDefault(normalized);
            if (previousSchemaVersion < 11)
                migrated += MigrateEladsHudValues(normalized);
            if (previousSchemaVersion < 13)
                migrated += MigrateGroundAtmosValues(normalized);
            if (previousSchemaVersion < 14)
                migrated += MigrateMonitorRowContentValues(normalized);

            // #694 (schema v16): the numbered-section schema itself is retired. This pass
            // runs LAST, on the fully normalized numbered-schema dictionary the passes
            // above produce, so every older profile funnels through one door: first to the
            // numbered schema, then to the plain-section schema. It moves the surviving
            // keys to their plain sections, assembles the per-risk sections into tier-table
            // values, and then drops every numbered section (00 - Migration excepted) plus
            // the retired Bundy and orphaned split-plugin sections outright.
            if (previousSchemaVersion < 16)
                migrated += MigrateV16PlainSections(normalized, ref removed);

            normalized[new ConfigDefinition(MigrationSection, MigrationVersionKey)] = CurrentSchemaVersion.ToString();
            normalized[new ConfigDefinition(MigrationSection, MigrationFingerprintKey)] = fingerprint;

            WriteConfigValuesAtomic(path, normalized);
            config.Reload();
            EnsureMigrationStamp(stampPath, fingerprint);
            log.LogInfo(
                $"Completed config schema v{CurrentSchemaVersion} migration: " +
                $"external={externalImported}, mapped={migrated}, removed={removed}, " +
                $"shipSystemsSkipped={externalSkippedShipSystems}, " +
                $"monitorTakeoverSkipped={externalSkippedMonitorTakeover}. " +
                "New-format values were preserved." +
                (externalSkippedShipSystems > 0
                    // #613 task 2.1 (V1): say so out loud. A user who had pre-fold ship settings
                    // is being told, once, that they were neither imported nor destroyed - which
                    // is the difference between "my settings vanished" and "my settings are
                    // waiting". No path is printed: the file names are fixed and public, and the
                    // config directory is machine-local.
                    ? $" {externalSkippedShipSystems} pre-fold ship systems / shop / terminal value(s)"
                      + " were left in their original config files for the Y4NGZShipSystems plugin"
                      + " to import; nothing was deleted."
                    : string.Empty)
                + (externalSkippedMonitorTakeover > 0
                    // #614 task 3.1: the same sentence for the same reason. Its own conditional,
                    // not a shared one: a profile can easily refuse Takeover values and no ship
                    // ones (or the reverse), and folding the two into one message gated on either
                    // count would name a plugin that skipped nothing. Without this, a user in that
                    // position got a bare "monitorTakeoverSkipped=12" and no way to tell a refusal
                    // from a loss - which is exactly the confusion the ship-systems line exists to
                    // prevent.
                    ? $" {externalSkippedMonitorTakeover} pre-fold monitor takeover / quota"
                      + " progression value(s) were left in their original config files for the"
                      + " Y4NGZMonitorTakeover plugin to import; nothing was deleted."
                    : string.Empty));
        }

        /// <summary>
        /// #694 (schema v16): numbered sections to plain sections. Consumes the fully
        /// normalized numbered-schema dictionary, emits the plain-section schema, and
        /// purges everything the plain schema does not carry: the orphaned split-plugin
        /// sections, the retired Bundy tuning surface, and the retired diagnostic keys.
        /// </summary>
        private static int MigrateV16PlainSections(Dictionary<ConfigDefinition, string> normalized, ref int removed)
        {
            int migrated = 0;

            // Per-contract selection sections: enum name in the old dotted key -> plain section.
            var contractSections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Whistleblower"] = "Whistleblower",
                ["BlackoutAudit"] = "Blackout Audit",
                ["ShadowRaid"] = "Shadow Raid",
                ["ContainmentBreach"] = "Containment Breach",
                ["Survey"] = "Survey",
                ["Defuse"] = "Defuse",
                ["Payload"] = "Payload",
                ["PestControl"] = "Pest Control",
                ["WasteDisposal"] = "Waste Disposal"
            };
            var selectionKeyRenames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Weight"] = "Selection Weight",
                ["UnlockAtCompletedQuota"] = "Unlock After Completed Quotas",
                ["MoonAllowList"] = "Moon Allow List",
                ["MoonDenyList"] = "Moon Deny List",
                ["ConstellationAllowList"] = "Constellation Allow List",
                ["ConstellationDenyList"] = "Constellation Deny List",
                ["InteriorAllowList"] = "Interior Allow List",
                ["InteriorDenyList"] = "Interior Deny List"
            };

            ConfigDefinition Def(string section, string key) => new ConfigDefinition(section, key);

            // Exact key moves (old definition -> new definition).
            var moves = new Dictionary<ConfigDefinition, ConfigDefinition>
            {
                [Def("02 - Contract Selection - Scoped Weights", "MoonContractWeights")] = Def("Contracts", "Moon Contract Weights"),
                [Def("02 - Contract Selection - Scoped Weights", "ConstellationContractWeights")] = Def("Contracts", "Constellation Contract Weights"),
                [Def("03 - Contract Rewards", "RewardPenaltyPercentByTier")] = Def("Contracts", "Reward And Penalty Percent By Risk"),
                [Def("04 - Quota Overhaul", "QuotaHazardPayPercentByWeather")] = Def("Contracts", "Hazard Pay Percent By Weather"),
                [Def("01 - Contract Selection", "None.Weight")] = Def("Contracts", "No Contract Weight"),

                [Def("04 - Quota Overhaul", "Enabled")] = Def("Quota", "Enabled"),
                [Def("04 - Quota Overhaul", "StartingQuota")] = Def("Quota", "Starting Quota"),
                [Def("04 - Quota Overhaul", "PlayerMultiplierEnabled")] = Def("Quota", "First Quota Player Scaling"),
                [Def("04 - Quota Overhaul", "PlayerMultiplierPerPlayer")] = Def("Quota", "First Quota Multiplier Per Player"),
                [Def("04 - Quota Overhaul", "PlayerMultiplierThreshold")] = Def("Quota", "First Quota Player Threshold"),
                [Def("04 - Quota Overhaul", "PlayerMultiplierCap")] = Def("Quota", "First Quota Player Cap"),
                [Def("04 - Quota Overhaul", "QuotaBaseIncreasePercent")] = Def("Quota", "Quota Increase Percent"),
                [Def("04 - Quota Overhaul", "DeathPenaltyPercentPerPlayer")] = Def("Quota", "Death Penalty Percent Per Player"),
                [Def("04 - Quota Overhaul", "DeathPenaltyDynamicCap")] = Def("Quota", "Death Penalty Dynamic Cap"),
                [Def("04 - Quota Overhaul", "DeathPenaltyRecoveryBonus")] = Def("Quota", "Death Penalty Recovery Bonus"),

                [Def("StashLoot", "Extra Ammo Rolls")] = Def("Company Stash Loot", "Extra Loot Rolls"),
                [Def("StashLoot", "Ammo Items")] = Def("Company Stash Loot", "Loot Items"),

                [Def("30 - Defuse - Shared", "InteractDistanceMeters")] = Def("Defuse", "Interact Distance Meters"),
                [Def("30 - Defuse - Shared", "ElevatedBombPlacement")] = Def("Defuse", "Elevated Bomb Placement"),
                // The four Defuse and Payload per-risk sections shipped identical defaults, so
                // the C tier is the representative source for the new single values.
                [Def("31 - Defuse - Risk C", "DeadlineNormalizedTime")] = Def("Defuse", "Deadline Normalized Time"),
                [Def("31 - Defuse - Risk C", "ExplosionRadiusMeters")] = Def("Defuse", "Explosion Radius Meters"),
                [Def("31 - Defuse - Risk C", "MinCorrectWireInputs")] = Def("Defuse", "Min Correct Wire Inputs"),
                [Def("31 - Defuse - Risk C", "MaxCorrectWireInputs")] = Def("Defuse", "Max Correct Wire Inputs"),
                [Def("41 - Containment Breach - Risk C", "EnemiesPerWave")] = Def("Containment Breach", "Enemies Per Wave"),
                [Def("60 - Payload - Shared", "RunawayEnabled")] = Def("Payload", "Runaway Cart Enabled"),
                [Def("61 - Payload - Risk C", "CargoValue")] = Def("Payload", "Cargo Value"),
                [Def("61 - Payload - Risk C", "PushSpeedMultiplier")] = Def("Payload", "Push Speed Multiplier"),
                [Def("61 - Payload - Risk C", "ReverseInputMultiplier")] = Def("Payload", "Reverse Input Multiplier"),
                [Def("61 - Payload - Risk C", "UphillMinimumSpeedMultiplier")] = Def("Payload", "Uphill Minimum Speed Multiplier"),
                [Def("65 - Whistleblower - Shared", "UseVanillaFriendlyModel")] = Def("Whistleblower", "Use Vanilla-Friendly Model"),

                [Def("70 - Waste Disposal - Shared", "RepairHoldSeconds")] = Def("Waste Disposal", "Repair Hold Seconds"),
                [Def("70 - Waste Disposal - Shared", "MalfunctionIntervalMinSeconds")] = Def("Waste Disposal", "Malfunction Interval Min Seconds"),
                [Def("70 - Waste Disposal - Shared", "MalfunctionIntervalMaxSeconds")] = Def("Waste Disposal", "Malfunction Interval Max Seconds"),

                [Def("90 - Escalations - Shared", "NoneChance")] = Def("Escalations", "No Escalation Weight"),
                [Def("91 - Escalations - Stranger", "Enabled")] = Def("Escalations", "Stranger Enabled"),
                [Def("91 - Escalations - Stranger", "Chance")] = Def("Escalations", "Stranger Chance"),
                [Def("91 - Escalations - Stranger", "ForceEveryRound")] = Def("Escalations", "Stranger Force Every Round"),
                [Def("91 - Escalations - Stranger", "MinDelaySeconds")] = Def("Escalations", "Stranger Min Delay Seconds"),
                [Def("91 - Escalations - Stranger", "MaxDelaySeconds")] = Def("Escalations", "Stranger Max Delay Seconds"),
                [Def("92 - Escalations - Blacksite Retrieval", "Enabled")] = Def("Escalations", "Blacksite Retrieval Enabled"),
                [Def("92 - Escalations - Blacksite Retrieval", "Chance")] = Def("Escalations", "Blacksite Retrieval Chance"),
                [Def("92 - Escalations - Blacksite Retrieval", "ForceEveryRound")] = Def("Escalations", "Blacksite Retrieval Force Every Round"),
                [Def("92 - Escalations - Blacksite Retrieval", "MinDelaySeconds")] = Def("Escalations", "Blacksite Retrieval Min Delay Seconds"),
                [Def("92 - Escalations - Blacksite Retrieval", "MaxDelaySeconds")] = Def("Escalations", "Blacksite Retrieval Max Delay Seconds"),
                [Def("92 - Escalations - Blacksite Retrieval", "SoldierCount")] = Def("Escalations", "Blacksite Retrieval Soldier Count"),
                [Def("93 - Escalations - Congregation", "Enabled")] = Def("Escalations", "Congregation Enabled"),
                [Def("93 - Escalations - Congregation", "Chance")] = Def("Escalations", "Congregation Chance"),
                [Def("93 - Escalations - Congregation", "ForceEveryRound")] = Def("Escalations", "Congregation Force Every Round"),
                [Def("93 - Escalations - Congregation", "MinDelaySeconds")] = Def("Escalations", "Congregation Min Delay Seconds"),
                [Def("93 - Escalations - Congregation", "MaxDelaySeconds")] = Def("Escalations", "Congregation Max Delay Seconds"),
                [Def("93 - Escalations - Congregation", "RedCultists")] = Def("Escalations", "Congregation Red Cultists"),
                [Def("93 - Escalations - Congregation", "BlackCultists")] = Def("Escalations", "Congregation Black Cultists"),
                [Def("93 - Escalations - Congregation", "WhiteCultists")] = Def("Escalations", "Congregation White Cultists"),
                [Def("93 - Escalations - Congregation", "CultistHealth")] = Def("Escalations", "Congregation Cultist Health"),
                [Def("93 - Escalations - Congregation", "AttackDamage")] = Def("Escalations", "Congregation Attack Damage"),

                [Def("95 - Interior Support", "DetailedPlacementRejectLogging")] = Def("Diagnostics", "Detailed Placement Reject Logging"),
                [Def("99 - Diagnostics", "DungeonSizeSoftCap")] = Def("Diagnostics", "Dungeon Size Soft Cap"),
                [Def("99 - Diagnostics", "ShadowRaidDebugLogging")] = Def("Diagnostics", "Shadow Raid Debug Logging"),
                [Def("99 - Diagnostics", "DefuseDebugLogging")] = Def("Diagnostics", "Defuse Debug Logging"),

                // The curated Stranger surface; every other Bundy key is dropped below.
                [Def("Gameplay", "ChainsawWalkSpeed")] = Def("The Stranger", "Walk Speed"),
                [Def("Gameplay", "ChainsawRunSpeed")] = Def("The Stranger", "Run Speed"),
                [Def("Gameplay", "ChainsawSprintSpeed")] = Def("The Stranger", "Sprint Speed"),
                [Def("Gameplay", "ChainsawSprintChance")] = Def("The Stranger", "Sprint Chance"),
                [Def("Combat", "StaggerBarMax")] = Def("The Stranger", "Stagger Bar Max"),
                [Def("Combat", "StaggerBarDamagePerForce")] = Def("The Stranger", "Stagger Damage Per Force"),
                [Def("Combat", "StaggerBarStunThresholdPercent")] = Def("The Stranger", "Stagger Stun Threshold Percent"),
                [Def("Combat", "KnockdownImmunitySeconds")] = Def("The Stranger", "Knockdown Immunity Seconds"),
                [Def("Combat", "ChainsawFinisherChancePercent")] = Def("The Stranger", "Finisher Chance Percent"),
                [Def("Combat", "ChainsawFinisherUsesBloodFX")] = Def("The Stranger", "Finisher Uses BloodFX"),
                [Def("Audio", "ChainsawLoopBaseVolume")] = Def("The Stranger", "Chainsaw Volume"),
                [Def("Audio", "BundyVoiceVolume")] = Def("The Stranger", "Voice Volume"),
                [Def("Audio", "BundyFootstepVolume")] = Def("The Stranger", "Footstep Volume")
            };

            // Old Enabled/Dynamic boolean pair -> one Death Penalty Mode value.
            bool hasPenaltyEnabled = TryGetBool(normalized, Def("04 - Quota Overhaul", "DeathPenaltyEnabled"), out bool penaltyEnabled);
            bool hasPenaltyDynamic = TryGetBool(normalized, Def("04 - Quota Overhaul", "DeathPenaltyDynamic"), out bool penaltyDynamic);
            if (hasPenaltyEnabled || hasPenaltyDynamic)
            {
                string mode = hasPenaltyEnabled && !penaltyEnabled
                    ? "Off"
                    : hasPenaltyDynamic && !penaltyDynamic ? "Static" : "Dynamic";
                ConfigDefinition modeDef = Def("Quota", "Death Penalty Mode");
                if (!normalized.ContainsKey(modeDef))
                {
                    normalized[modeDef] = mode;
                    migrated++;
                }
            }

            // Per-risk sections -> one tier-table value per key, built from whatever the
            // old sections carried (customized or default alike).
            migrated += AssembleTierTable(normalized, "{0} - Blackout Audit - Risk {1}", new[] { "11", "12", "13", "14" }, "BreakerCount", Def("Blackout Audit", "Breakers Required By Risk"));
            migrated += AssembleTierTable(normalized, "{0} - Blackout Audit - Risk {1}", new[] { "11", "12", "13", "14" }, "AmbientFlickerIntervalSeconds", Def("Blackout Audit", "Ambient Flicker Interval Seconds By Risk"));
            migrated += AssembleTierTable(normalized, "{0} - Survey - Risk {1}", new[] { "21", "22", "23", "24" }, "DroneCount", Def("Survey", "Drones Required By Risk"));
            migrated += AssembleTierTable(normalized, "{0} - Containment Breach - Risk {1}", new[] { "41", "42", "43", "44" }, "WaveCount", Def("Containment Breach", "Waves By Risk"));
            migrated += AssembleTierTable(normalized, "{0} - Containment Breach - Risk {1}", new[] { "41", "42", "43", "44" }, "WaveDurationSeconds", Def("Containment Breach", "Wave Duration Seconds By Risk"));
            migrated += AssembleTierTable(normalized, "{0} - Shadow Raid - Risk {1}", new[] { "51", "52", "53", "54" }, "DrillDurationSeconds", Def("Shadow Raid", "Drill Duration Seconds By Risk"));
            migrated += AssembleTierTable(normalized, "{0} - Shadow Raid - Risk {1}", new[] { "51", "52", "53", "54" }, "ContainerCount", Def("Shadow Raid", "Containers By Risk"));
            migrated += AssembleTierTable(normalized, "{0} - Whistleblower - Risk {1}", new[] { "66", "67", "68", "69" }, "Health", Def("Whistleblower", "Health By Risk"));
            migrated += AssembleTierTable(normalized, "{0} - Waste Disposal - Risk {1}", new[] { "71", "72", "73", "74" }, "RequiredMoonScrapPercent", Def("Waste Disposal", "Required Moon Scrap Percent By Risk"));

            // Apply the exact moves plus the per-contract selection-key transform.
            foreach (KeyValuePair<ConfigDefinition, string> pair in new List<KeyValuePair<ConfigDefinition, string>>(normalized))
            {
                ConfigDefinition target = null;
                if (moves.TryGetValue(pair.Key, out ConfigDefinition mapped))
                {
                    target = mapped;
                }
                else if (string.Equals(pair.Key.Section, "01 - Contract Selection", StringComparison.Ordinal))
                {
                    int dot = pair.Key.Key.IndexOf('.');
                    if (dot > 0
                        && contractSections.TryGetValue(pair.Key.Key.Substring(0, dot), out string section)
                        && selectionKeyRenames.TryGetValue(pair.Key.Key.Substring(dot + 1), out string newKey))
                    {
                        target = Def(section, newKey);
                    }
                }

                if (target == null)
                    continue;

                normalized.Remove(pair.Key);
                if (!normalized.ContainsKey(target))
                {
                    normalized[target] = pair.Value;
                    migrated++;
                }
            }

            // Purge ONLY the sections Contracted itself owned in the numbered schema plus
            // the retired Bundy surface. The other-plugin orphans (05 - Audio,
            // 06 - UI Theme, 08 - Blood FX, Monitor Takeover, Terminal/Shop, Ship
            // Systems, CCTV, 95 - Interior Support, CCTV Radar, Quota Progression)
            // deliberately stay in the file: they are the recovery inputs the sibling
            // plugins' one-time importers read when a module is installed later
            // (ARCHITECTURE_DLL_SPLIT.md section 4), and mod managers do not display
            // orphaned entries, so leaving them costs the player nothing.
            var deadSections = new HashSet<string>(StringComparer.Ordinal)
            {
                "01 - Contract Selection",
                "02 - Contract Selection - Scoped Weights",
                "03 - Contract Rewards",
                "04 - Quota Overhaul",
                "30 - Defuse - Shared",
                "40 - Containment Breach - Shared",
                "50 - Shadow Raid - Shared",
                "60 - Payload - Shared",
                "65 - Whistleblower - Shared",
                "70 - Waste Disposal - Shared",
                "90 - Escalations - Shared",
                "91 - Escalations - Stranger",
                "92 - Escalations - Blacksite Retrieval",
                "93 - Escalations - Congregation",
                "94 - Escalations - Congregation Seraphim",
                "96 - CCTV Security - Shared",
                "97 - CCTV Security - Alarms",
                "98 - CCTV Security - Protocols",
                "99 - Diagnostics",
                "StashLoot", "AI", "Audio", "Combat", "Debug", "Gameplay",
                "PairedAnimations", "Experimental", "Spawning",
                "Performance Diagnostics"
            };
            var riskFamilies = new (string Family, int FirstNumber)[]
            {
                ("Blackout Audit", 11), ("Survey", 21), ("Defuse", 31),
                ("Containment Breach", 41), ("Shadow Raid", 51), ("Payload", 61),
                ("Whistleblower", 66), ("Waste Disposal", 71)
            };
            string[] riskTiers = { "C", "B", "A", "S" };
            foreach ((string family, int firstNumber) in riskFamilies)
                for (int i = 0; i < riskTiers.Length; i++)
                    deadSections.Add((firstNumber + i).ToString(CultureInfo.InvariantCulture) + " - " + family + " - Risk " + riskTiers[i]);
            foreach (KeyValuePair<ConfigDefinition, string> pair in new List<KeyValuePair<ConfigDefinition, string>>(normalized))
            {
                if (deadSections.Contains(pair.Key.Section ?? string.Empty))
                {
                    normalized.Remove(pair.Key);
                    removed++;
                }
            }

            return migrated;
        }

        private static bool TryGetBool(Dictionary<ConfigDefinition, string> values, ConfigDefinition definition, out bool parsed)
        {
            parsed = false;
            return values.TryGetValue(definition, out string raw) && bool.TryParse(raw, out parsed);
        }

        /// <summary>
        /// Builds one "C=x, B=y, A=z, S=w" tier-table value from a key spread across the
        /// four old per-risk sections. Emits only when at least one source key exists and
        /// the target is not already present; missing tiers are simply omitted (the
        /// runtime falls back to the C column, matching the old ForRisk default).
        /// </summary>
        private static int AssembleTierTable(
            Dictionary<ConfigDefinition, string> values,
            string sectionFormat,
            string[] sectionNumbers,
            string key,
            ConfigDefinition target)
        {
            if (values.ContainsKey(target))
                return 0;

            char[] tiers = { 'C', 'B', 'A', 'S' };
            var parts = new List<string>(4);
            for (int i = 0; i < tiers.Length; i++)
            {
                var source = new ConfigDefinition(string.Format(sectionFormat, sectionNumbers[i], tiers[i]), key);
                if (values.TryGetValue(source, out string raw) && !string.IsNullOrWhiteSpace(raw))
                    parts.Add(tiers[i] + "=" + raw.Trim());
            }

            if (parts.Count == 0)
                return 0;

            values[target] = string.Join(", ", parts);
            return 1;
        }

        /// <summary>
        /// Imports the pre-fold standalone plugins' config files into this one.
        ///
        /// <para><b>Read-only against those files, and that is load-bearing.</b> Every source is
        /// opened through <see cref="ReadLegacyValues"/>, which is a
        /// <c>File.ReadAllLines</c> and nothing else. No legacy source in
        /// <see cref="LegacyConfigFiles"/> is ever deleted, moved or rewritten by this class -
        /// the only config file it retires is the standalone <c>Y4NGZ_UITheme.cfg</c>, in
        /// <see cref="RetireStandaloneUiThemeConfig"/>, and that name is deliberately not in the
        /// list. So a user's <c>com.y4ngz.lgushipsystems.cfg</c>, <c>lgushop.cfg</c> and
        /// <c>lguterminal.cfg</c> survive this migration untouched.</para>
        ///
        /// <para><b>#613 task 2.1 (V1): Ship-Systems-bound values are refused.</b> Ship Systems
        /// left this assembly and binds its own <c>com.y4ngz.company.shipsystems.cfg</c>.
        /// Importing a legacy fuel or store value into <c>com.y4ngz.company.cfg</c> would now
        /// write it to a destination nothing reads, and - worse for the task that has to clean
        /// this up - would give that one value two provenances: the original legacy file, still
        /// on disk, and a dead copy here. The Ship Systems split importer would then have to decide which of
        /// two identical-looking sources is authoritative, having been handed the ambiguity by
        /// us. Refusing the import leaves each such value in exactly one place, which is the
        /// pristine input the new owner reads.</para>
        ///
        /// <para><b>What is NOT skipped, deliberately:</b> the in-file section normalisation
        /// further down <see cref="MigrateConfig"/> (<see cref="IsLegacyDefinition"/> plus the
        /// mapping loop that follows it). That renames Ship Systems entries a user already has
        /// <em>inside</em> <c>com.y4ngz.company.cfg</c> (a monolith-era <c>[Battery]</c> becoming
        /// <c>[78 - Ship Systems - Battery]</c>). Those values are already single-provenance and
        /// already in this file. Normalising keeps the monolith readable when Contracted runs
        /// alone or awakens first. The standalone importer also accepts these settled pre-fold
        /// spellings because Ship Systems deliberately awakens before Contracted and Monitor
        /// Takeover has no Contracted dependency; migration correctness cannot depend on plugin
        /// load order.</para>
        ///
        /// <para><b>#614 task 3.1: the same is true of Monitor Takeover, and for the same
        /// reason.</b> A monolith-era <c>[Settings]</c> or <c>[Ending]</c> block already inside
        /// <c>com.y4ngz.company.cfg</c> is still normalised to
        /// <c>[71/72 - Monitor Takeover - …]</c> and <c>[73 - Monitor Takeover - Ending]</c> by
        /// that loop, and still lands in this file rather than in the takeover's. That is not an
        /// oversight and is not the ambiguity the predicates exist to prevent: nothing is being
        /// copied, so the value keeps its single provenance either way, and the choice is only
        /// between one canonical section name and one dead one. The two refusal predicates guard
        /// the <em>import</em> boundary — a value crossing from another file into this one — which
        /// is the only place a second copy can be created. The #616 importer recognizes either
        /// spelling and moves the owned blocks into
        /// <c>com.y4ngz.company.monitortakeover.cfg</c>; the sibling Ship Systems importer applies
        /// the same rule to its sections.</para>
        ///
        /// <para><paramref name="skippedShipSystems"/> and
        /// <paramref name="skippedMonitorTakeover"/> report how many were refused, for the startup
        /// log line - a silent skip and a silent import look identical in a bug report.</para>
        /// </summary>
        private static int MergeLegacySourceValues(
            Dictionary<ConfigDefinition, string> values,
            Dictionary<string, string> sectionMoves,
            out int skippedShipSystems,
            out int skippedMonitorTakeover)
        {
            int imported = 0;
            skippedShipSystems = 0;
            skippedMonitorTakeover = 0;
            foreach (string fileName in LegacyConfigFiles)
            {
                string sourcePath = Path.Combine(Paths.ConfigPath, fileName);
                foreach (LegacyValue value in ReadLegacyValues(sourcePath))
                {
                    ConfigDefinition definition = new ConfigDefinition(value.Section, value.Key);
                    if (values.ContainsKey(definition)) continue;
                    if (ResolvesToShipSystemsSection(definition, sectionMoves))
                    {
                        skippedShipSystems++;
                        continue;
                    }
                    // #614 task 3.1: the same refusal for Monitor Takeover, which left this
                    // assembly in the same way Ship Systems did. Checked second only because the
                    // two destination sets are disjoint - order cannot change the outcome.
                    if (ResolvesToMonitorTakeoverSection(definition, sectionMoves))
                    {
                        skippedMonitorTakeover++;
                        continue;
                    }
                    values[definition] = value.Value;
                    imported++;
                }
            }
            return imported;
        }

        /// <summary>
        /// True when <paramref name="section"/> is one of the config sections Ship Systems binds
        /// in its own file since #613 task 2.1.
        ///
        /// <para>Matched on the numeric prefix rather than an enumerated list because
        /// <c>76 - Shop - Category &lt;Name&gt;</c> is generated per configured store category
        /// (<c>ShopConfig.cs:90</c>) and cannot be enumerated here. Verified against the Ship
        /// Systems sources: every section it binds begins with one of these five prefixes, and no
        /// section Contracted, Bundy or Monitor Takeover binds does. The prefixes include the
        /// trailing space, so a future <c>"780 - ..."</c> could not be swept up by accident.</para>
        /// </summary>
        private static bool IsShipSystemsOwnedSection(string section)
        {
            if (string.IsNullOrEmpty(section))
                return false;

            return section.StartsWith("74 - Terminal ", StringComparison.Ordinal)
                || section.StartsWith("75 - Shop ", StringComparison.Ordinal)
                || section.StartsWith("76 - Shop ", StringComparison.Ordinal)
                || section.StartsWith("77 - Ship Upgrades ", StringComparison.Ordinal)
                || section.StartsWith("78 - Ship Systems ", StringComparison.Ordinal);
        }

        /// <summary>
        /// True when <paramref name="definition"/> is already in a Ship-Systems-owned section, or
        /// would be moved into one by this migration.
        ///
        /// <para>The destination is resolved through <see cref="GetDefinitionMigration"/> - the
        /// same function that performs the move - rather than by re-deriving it. A second copy of
        /// the mapping rules would drift from the real one, and the rules are not simple: besides
        /// the <paramref name="sectionMoves"/> table they include the <c>Category.*</c> prefix
        /// and the <c>[Terminal]</c> section, which splits by key between
        /// <c>74 - Terminal - Shared</c> and <c>77 - Ship Upgrades - Prices</c>.</para>
        /// </summary>
        private static bool ResolvesToShipSystemsSection(
            ConfigDefinition definition, Dictionary<string, string> sectionMoves)
        {
            if (IsShipSystemsOwnedSection(definition.Section))
                return true;

            return GetDefinitionMigration(definition, sectionMoves, out ConfigDefinition target)
                    == DefinitionMigrationAction.Map
                && target != null
                && IsShipSystemsOwnedSection(target.Section);
        }

        /// <summary>
        /// True when <paramref name="section"/> is one of the config sections Monitor Takeover
        /// binds in its own file since #614 task 3.1.
        ///
        /// <para><b>Keyed on the full section name, not the numeric prefix, and that is not a
        /// style choice.</b> Contracted binds <c>70 - Waste Disposal - Shared</c> and
        /// <c>71/72/73 - Waste Disposal - Risk C/B/A</c>
        /// (<c>WasteDisposalConfig</c>), so the numbers 70 through 73 are double-booked between a
        /// Contracted feature and a Monitor Takeover one. A prefix match on <c>"71 - "</c> would
        /// refuse to import the player's Waste Disposal risk tuning and there would be nothing in
        /// the log to connect the loss to this predicate. Every match below carries the feature
        /// name, so the two families cannot collide. The sibling
        /// <see cref="IsShipSystemsOwnedSection"/> already has this property for the same reason
        /// (<c>74 - Terminal </c> versus <c>74 - Waste Disposal - Risk S</c>); it is stated here
        /// because Monitor Takeover collides with four of Waste Disposal's five sections rather
        /// than one.</para>
        ///
        /// <para>The four <c>Monitor Takeover</c> sections are enumerated exactly, because
        /// <c>TakeoverBootstrap.BindConfig</c> binds exactly those four and no more. The quota
        /// family is a prefix because it cannot be enumerated:
        /// <c>QuotaProgressionRegistry.cs:148</c> generates
        /// <c>70 - Quota Progression - Quota NN</c> per milestone, alongside the fixed
        /// <c>70 - Quota Progression - Shared</c> at <c>:256</c>. The prefix keeps the feature
        /// name and a trailing space, so neither Waste Disposal nor a future
        /// <c>"700 - ..."</c> can be swept up by accident.</para>
        /// </summary>
        private static bool IsMonitorTakeoverOwnedSection(string section)
        {
            if (string.IsNullOrEmpty(section))
                return false;

            switch (section)
            {
                case "71 - Monitor Takeover - Shared":
                case "72 - Monitor Takeover - Presentation":
                case "73 - Monitor Takeover - Ending":
                case "79 - Monitor Takeover - Diagnostics":
                    return true;
            }

            return section.StartsWith("70 - Quota Progression ", StringComparison.Ordinal);
        }

        /// <summary>
        /// True when <paramref name="definition"/> is already in a Monitor-Takeover-owned section,
        /// or would be moved into one by this migration.
        ///
        /// <para>Resolved through <see cref="GetDefinitionMigration"/> for the reason spelled out
        /// on <see cref="ResolvesToShipSystemsSection"/>: a second copy of the mapping rules would
        /// drift from the real one. It matters more here, because two of the three Takeover
        /// mappings are conditional rather than table-driven — <c>[Settings]</c> splits by key
        /// between <c>71 - Monitor Takeover - Shared</c> and
        /// <c>72 - Monitor Takeover - Presentation</c>, and <c>[Diagnostics]</c> maps to
        /// <c>79 - Monitor Takeover - Diagnostics</c> for exactly two keys and to nothing at all
        /// for every other key in that shared section. Asking the mapping function is what makes
        /// the refusal exactly as narrow as the move.</para>
        /// </summary>
        private static bool ResolvesToMonitorTakeoverSection(
            ConfigDefinition definition, Dictionary<string, string> sectionMoves)
        {
            if (IsMonitorTakeoverOwnedSection(definition.Section))
                return true;

            return GetDefinitionMigration(definition, sectionMoves, out ConfigDefinition target)
                    == DefinitionMigrationAction.Map
                && target != null
                && IsMonitorTakeoverOwnedSection(target.Section);
        }

        private static int GetSchemaVersion(Dictionary<ConfigDefinition, string> values)
        {
            return values.TryGetValue(new ConfigDefinition(MigrationSection, MigrationVersionKey), out string raw)
                && int.TryParse(raw, out int version)
                    ? version
                    : 0;
        }

        private static bool IsLegacyDefinition(ConfigDefinition definition, Dictionary<string, string> sectionMoves)
        {
            return sectionMoves.ContainsKey(definition.Section)
                || definition.Section == "Settings"
                || definition.Section == "Ending"
                || definition.Section == "Containment Breach"
                || definition.Section == "Shadow Raid"
                || definition.Section == "Blacksite"
                || definition.Section == "Whistleblower"
                || definition.Section == "Defuse"
                || definition.Section == "Dead Drop"
                || definition.Section == "Contracts"
                || definition.Section == "Contract Balance"
                || definition.Section == "Terminal"
                || definition.Section.StartsWith("Category.", StringComparison.Ordinal)
                || IsRetiredDefinition(definition)
                || (definition.Section == "Diagnostics" &&
                    (definition.Key == "Verbose Logging" || definition.Key == "Dump Monitor Hierarchy On Orbit"));
        }

        private static DefinitionMigrationAction GetDefinitionMigration(
            ConfigDefinition source,
            Dictionary<string, string> sectionMoves,
            out ConfigDefinition target)
        {
            if (IsRetiredDefinition(source))
            {
                target = null;
                return DefinitionMigrationAction.Remove;
            }

            if (source.Section == "Phase 1.8 Room-Aware Placement" && source.Key == "Gate A Diagnostic Only")
            {
                target = null;
                return DefinitionMigrationAction.Remove;
            }

            if (source.Section == "Containment Breach" ||
                (source.Section == "Contract Balance" && source.Key.StartsWith("Containment", StringComparison.Ordinal)))
            {
                target = null;
                return DefinitionMigrationAction.Remove;
            }

            if (source.Section == "Shadow Raid" || source.Section == "Blacksite" ||
                (source.Section == "Contract Balance" && IsCoreContractTierKey(source.Key)))
            {
                target = null;
                return DefinitionMigrationAction.Remove;
            }

            if (source.Section == "Whistleblower")
            {
                target = null;
                return DefinitionMigrationAction.Remove;
            }

            if (source.Section == "Defuse" || source.Section == "Dead Drop")
            {
                target = null;
                return DefinitionMigrationAction.Remove;
            }

            if (source.Section == "Contracts" || source.Section == "Contract Balance")
            {
                target = null;
                return DefinitionMigrationAction.Remove;
            }

            if (sectionMoves.TryGetValue(source.Section, out string section))
            {
                string key = source.Key;
                if (source.Section == "Phase 1.8 Room-Aware Placement" && key == "Placement Diagnostic Only")
                    key = "Dry Run Camera Placement";
                else if (source.Section == "Phase 2.0 Legibility Probe" && key == "P20 Probe Enabled")
                    key = "Enable Placement Probe";
                else
                    key = RenameMergedAudioKey(source.Section, key);
                target = new ConfigDefinition(section, key);
                return DefinitionMigrationAction.Map;
            }

            if (source.Section.StartsWith("Category.", StringComparison.Ordinal))
            {
                string categoryName = source.Section.Substring("Category.".Length).Trim();
                target = new ConfigDefinition($"76 - Shop - Category {categoryName}", source.Key);
                return DefinitionMigrationAction.Map;
            }

            if (source.Section == "Terminal")
            {
                string terminalSection = source.Key == "Menu Title" || source.Key == "Visible Rows"
                    ? "74 - Terminal - Shared"
                    : "77 - Ship Upgrades - Prices";
                target = new ConfigDefinition(terminalSection, source.Key);
                return DefinitionMigrationAction.Map;
            }

            if (source.Section == "Settings")
            {
                string sectionName = source.Key == "Enabled" || source.Key == "Takeover Duration" || source.Key == "Orbit Delay"
                    ? "71 - Monitor Takeover - Shared"
                    : "72 - Monitor Takeover - Presentation";
                string key = source.Key == "Override Fifth Monitor" ? "Override Additional Monitors" : source.Key;
                target = new ConfigDefinition(sectionName, key);
                return DefinitionMigrationAction.Map;
            }

            if (source.Section == "Ending")
            {
                target = new ConfigDefinition("73 - Monitor Takeover - Ending", source.Key);
                return DefinitionMigrationAction.Map;
            }

            if (source.Section == "Diagnostics" &&
                (source.Key == "Verbose Logging" || source.Key == "Dump Monitor Hierarchy On Orbit"))
            {
                target = new ConfigDefinition("79 - Monitor Takeover - Diagnostics", source.Key);
                return DefinitionMigrationAction.Map;
            }

            target = null;
            return DefinitionMigrationAction.None;
        }

        private enum DefinitionMigrationAction
        {
            None,
            Map,
            Remove
        }

        private static bool IsRetiredDefinition(ConfigDefinition definition)
        {
            switch (definition.Section)
            {
                case "Abilities":
                case "Evidence Run":
                case "Player Animation API":
                case "Player Animation API Debug":
                case "State":
                case "UI":
                    return true;
            }

            if (definition.Section == "87 - CCTV - Operator Controls")
                return definition.Key == "UseCandidateDroneTabletAnimation";
            if (definition.Section == "01 - Contract Selection")
                return definition.Key.StartsWith("None.", StringComparison.Ordinal)
                    && definition.Key != "None.Weight";
            if (definition.Section == "70 - Quota Progression - Shared")
                return definition.Key == "ConfiguredMilestones";
            if (definition.Section == "Terminal")
                return definition.Key == "Ship Upgrades Keyword";
            if (definition.Section == "Battery")
                return definition.Key == "Restore Hold Seconds"
                    || definition.Key == "Sabotage Minimum Fulfilled Quota"
                    || definition.Key == "Sabotage Seconds"
                    || definition.Key == "Sabotage Seconds With Plating";
            if (definition.Section == "Combat")
                return definition.Key == "StandardBasicAttackDamage"
                    || definition.Key == "StandardFinisherChancePercent"
                    // Schema v12 (#288): the rolling stagger-damage accumulator was replaced by the
                    // 100-point stagger bar. Both keys describe a mechanism that no longer exists,
                    // so they are RETIRED (dropped from the profile) rather than re-defaulted onto
                    // the new StaggerBar* keys — a threshold of "6 accumulated force" has no honest
                    // translation into bar points.
                    || definition.Key == "StaggerKnockdownDamageThreshold"
                    || definition.Key == "StaggerDamageAccumWindowSeconds";
            if (definition.Section == "Experimental")
                return definition.Key == "ExperimentalStandardFinishers"
                    || definition.Key == "ExperimentalStandardMovementContexts";
            if (definition.Section == "Gameplay")
                return definition.Key == "StandardRunSpeed"
                    || definition.Key == "StandardWalkSpeed";
            if (definition.Section == "Power")
                return definition.Key == "Remote Operation Cost"
                    || definition.Key == "Signal Beacon Cost"
                    || definition.Key == "Speaker Hijack Cost";
            // Schema v15 (#338): burn time is now an exact gameplay rule
            // (committed scrap value * 0.6 seconds), not a configurable
            // base/rate/cap curve. Retire all three old controls so an existing
            // 0.12 rate cannot silently override the new contract behavior.
            if (definition.Section == "70 - Waste Disposal - Shared")
                return definition.Key == "BaseBurnDurationSeconds"
                    || definition.Key == "BurnSecondsPerScrapValue"
                    || definition.Key == "MaxBurnDurationSeconds";
            if (definition.Section == "Spawning")
                return definition.Key == "ChainsawRigLabSpawnWeight"
                    || definition.Key == "ChainsawV4ZWrapSpawnWeight"
                    || definition.Key == "ChainsawV5CleanSpawnWeight"
                    || definition.Key == "ChainsawV5GenericTestRigSpawnWeight"
                    || definition.Key == "StandardSpawnWeight";
            // The fire alarm test and camera calibration sweep protocol events
            // were removed on 2026-07-28, taking their cooldowns with them.
            // This rides the unreleased schema v7 rather than claiming a v8:
            // no profile has ever seen v7, so folding the retirement into it
            // costs a version number and nothing else.
            if (definition.Section == "98 - CCTV Security - Protocols")
                return definition.Key == "FireAlarmTestCooldownSeconds"
                    || definition.Key == "CalibrationSweepCooldownSeconds";
            // Schema v17 (#1020 S6): the Blacksite decision core key. A soldier has ONE decision
            // core now, so the key selects nothing; it is RETIRED rather than re-defaulted, because
            // "Legacy" no longer names a path that exists. Every other Escalations value is kept.
            //
            // The section is the literal, not ContractedConfigSections.Escalations, for the same
            // reason every other arm in this method is a literal: this file is compiled on its own
            // by ConfigMigrationRegression, which links no section table, and a retirement rule has
            // to keep naming a section long after the constant for it is gone (see "Abilities",
            // "Evidence Run" and "State" above, which have no constant at all any more).
            if (definition.Section == "Escalations")
                return definition.Key == "Blacksite Decision Core";
            if (definition.Section == "40 - Containment Breach - Shared")
                return definition.Key == "EnhancedTracking";
            if (definition.Section == "41 - Containment Breach - Risk C"
                || definition.Section == "42 - Containment Breach - Risk B"
                || definition.Section == "43 - Containment Breach - Risk A"
                || definition.Section == "44 - Containment Breach - Risk S")
                return definition.Key == "EnemyNames";
            return false;
        }

        private static int MigrateContractSelectionValues(
            Dictionary<ConfigDefinition, string> source,
            Dictionary<ConfigDefinition, string> target)
        {
            int imported = 0;
            if (source.TryGetValue(new ConfigDefinition("Contracts", "DefaultContractWeights"), out string weights))
                imported += ImportContractWeightMap(target, weights);
            imported += ImportContractListMap(source, target, "MoonWhitelistByContract", "MoonAllowList");
            imported += ImportContractListMap(source, target, "MoonBlacklistByContract", "MoonDenyList");
            imported += ImportContractListMap(source, target, "InteriorWhitelistByContract", "InteriorAllowList");
            imported += ImportContractListMap(source, target, "InteriorBlacklistByContract", "InteriorDenyList");
            imported += CopyIfPresent(source, target,
                new ConfigDefinition("Contracts", "MoonContractWeights"),
                new ConfigDefinition("02 - Contract Selection - Scoped Weights", "MoonContractWeights"));
            imported += CopyIfPresent(source, target,
                new ConfigDefinition("Contracts", "ConstellationContractWeights"),
                new ConfigDefinition("02 - Contract Selection - Scoped Weights", "ConstellationContractWeights"));
            imported += CopyIfPresent(source, target,
                new ConfigDefinition("Contract Balance", "RewardPenaltyPercentByTier"),
                new ConfigDefinition("03 - Contract Rewards", "RewardPenaltyPercentByTier"));
            return imported;
        }

        private static int ImportContractWeightMap(
            Dictionary<ConfigDefinition, string> target,
            string raw)
        {
            int imported = 0;
            foreach (string entry in (raw ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = entry.IndexOf('=');
                if (equals <= 0) continue;
                string contract = CanonicalContractName(entry.Substring(0, equals));
                if (contract == null) continue;
                string value = entry.Substring(equals + 1).Trim();
                if (value.Length == 0) continue;
                imported += SetIfMissing(target,
                    new ConfigDefinition("01 - Contract Selection", contract + ".Weight"),
                    value);
            }
            return imported;
        }

        private static int ImportContractListMap(
            Dictionary<ConfigDefinition, string> source,
            Dictionary<ConfigDefinition, string> target,
            string oldKey,
            string newSuffix)
        {
            if (!source.TryGetValue(new ConfigDefinition("Contracts", oldKey), out string raw))
                return 0;
            int imported = 0;
            foreach (string entry in (raw ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = entry.IndexOf('=');
                if (equals <= 0) continue;
                string contract = CanonicalContractName(entry.Substring(0, equals));
                if (contract == null) continue;
                if (contract == "None") continue;
                imported += SetIfMissing(target,
                    new ConfigDefinition("01 - Contract Selection", contract + "." + newSuffix),
                    entry.Substring(equals + 1).Trim());
            }
            return imported;
        }

        private static string CanonicalContractName(string raw)
        {
            var normalized = new StringBuilder();
            foreach (char c in raw ?? string.Empty)
                if (char.IsLetterOrDigit(c)) normalized.Append(char.ToLowerInvariant(c));
            switch (normalized.ToString())
            {
                case "none":
                case "salvage": return "None";
                case "whistleblower": return "Whistleblower";
                case "blackout":
                case "blackoutaudit": return "BlackoutAudit";
                case "blacksite":
                case "shadowraid": return "ShadowRaid";
                case "containment":
                case "containmentbreach": return "ContainmentBreach";
                case "evidencerun":
                case "survey": return "Survey";
                case "deaddrop":
                case "defuse": return "Defuse";
                case "payload":
                case "payloadhaul": return "Payload";
                default: return null;
            }
        }

        private static int MigrateContainmentValues(
            Dictionary<ConfigDefinition, string> source,
            Dictionary<ConfigDefinition, string> target)
        {
            int imported = 0;
            if (source.TryGetValue(new ConfigDefinition("Containment Breach", "EnemiesPerWave"), out string enemiesPerWave))
            {
                foreach (string section in ContainmentRiskSections())
                    imported += SetIfMissing(target, new ConfigDefinition(section, "EnemiesPerWave"), enemiesPerWave);
            }

            imported += ImportTierValues(source, target, "ContainmentWaveCountByTier", "WaveCount", ',');
            imported += ImportTierValues(source, target, "ContainmentWaveDurationSecondsByTier", "WaveDurationSeconds", ',');
            return imported;
        }

        private static int MigrateCoreContractRiskValues(
            Dictionary<ConfigDefinition, string> source,
            Dictionary<ConfigDefinition, string> target)
        {
            int imported = 0;
            imported += ImportSimpleTierMap(source, target, "BlackoutBreakerCountByTier", "BreakerCount", 11, 12, 13, 14);
            imported += ImportSimpleTierMap(source, target, "BlackoutAmbientFlickerIntervalSecondsByTier", "AmbientFlickerIntervalSeconds", 11, 12, 13, 14);
            imported += ImportSimpleTierMap(source, target, "SurveyDroneCountByTier", "DroneCount", 21, 22, 23, 24);
            imported += ImportSimpleTierMap(source, target, "ShadowRaidDrillDurationSecondsByTier", "DrillDurationSeconds", 51, 52, 53, 54);
            imported += ImportSimpleTierMap(source, target, "ShadowRaidContainerCountByTier", "ContainerCount", 51, 52, 53, 54);

            imported += CopyFirstIfPresent(source, target,
                new ConfigDefinition("99 - Diagnostics", "ShadowRaidDebugLogging"),
                new ConfigDefinition("Shadow Raid", "Enabled"),
                new ConfigDefinition("Blacksite", "Enabled"));
            imported += CopyFirstIfPresent(source, target,
                new ConfigDefinition("99 - Diagnostics", "ShadowRaidForceAllMoons"),
                new ConfigDefinition("Shadow Raid", "ForceAllMoonsContract"),
                new ConfigDefinition("Blacksite", "ForceAllMoonsContract"));
            foreach (string key in new[] { "DrillPrefabName", "DrillAttachDistance", "ContainerPrefabName", "RewardPrefabName" })
            {
                imported += CopyFirstIfPresent(source, target,
                    new ConfigDefinition("50 - Shadow Raid - Shared", key),
                    new ConfigDefinition("Shadow Raid", key),
                    new ConfigDefinition("Blacksite", key));
            }

            if (TryGetFirstValue(source, out string duration,
                new ConfigDefinition("Shadow Raid", "DrillDurationSeconds"),
                new ConfigDefinition("Blacksite", "DrillDurationSeconds")))
            {
                foreach (int section in new[] { 51, 52, 53, 54 })
                    imported += SetIfMissing(target, new ConfigDefinition(ShadowRiskSection(section), "DrillDurationSeconds"), duration);
            }
            return imported;
        }

        private static int MigrateDefuseValues(
            Dictionary<ConfigDefinition, string> source,
            Dictionary<ConfigDefinition, string> target)
        {
            int imported = 0;
            imported += CopyFirstIfPresent(source, target,
                new ConfigDefinition("30 - Defuse - Shared", "InteractDistanceMeters"),
                new ConfigDefinition("Defuse", "InteractDistance"),
                new ConfigDefinition("Dead Drop", "InteractDistance"));
            imported += CopyFirstIfPresent(source, target,
                new ConfigDefinition("30 - Defuse - Shared", "UseFacilityMeltdownOnFailure"),
                new ConfigDefinition("Defuse", "UseFacilityMeltdownOnFailure"),
                new ConfigDefinition("Dead Drop", "UseFacilityMeltdownOnFailure"));
            imported += CopyFirstIfPresent(source, target,
                new ConfigDefinition("99 - Diagnostics", "DefuseForceAllMoons"),
                new ConfigDefinition("Defuse", "ForceAllMoonsContract"),
                new ConfigDefinition("Dead Drop", "ForceAllMoonsContract"));

            if (TryGetFirstValue(source, out string radius,
                new ConfigDefinition("Defuse", "ExplosionRadius"),
                new ConfigDefinition("Dead Drop", "ExplosionRadius")))
            {
                foreach (int section in new[] { 31, 32, 33, 34 })
                {
                    string risk = section == 31 ? "C" : section == 32 ? "B" : section == 33 ? "A" : "S";
                    imported += SetIfMissing(target,
                        new ConfigDefinition($"{section:00} - Defuse - Risk {risk}", "ExplosionRadiusMeters"),
                        radius);
                }
            }
            return imported;
        }

        // Scoped v5 import: the reactive spawn/aggro audio module moved here from
        // Y4NGZUpgrades (#152). Only its [Reactive Audio] section may be read from
        // com.y4ngz.upgrades.cfg — that file otherwise belongs to a live plugin,
        // so it must never be added to LegacyConfigFiles (which merges whole files
        // and only fires on first-run installs anyway).
        /// <summary>
        /// Schema v7 folded "05 - Reactive Audio" and "07 - Interior Ambience" into
        /// "05 - Audio". Both had an "Enabled" key and the merged section has its own
        /// master "Enabled", so three keys would collide on a plain section move --
        /// silently resetting whichever lost. These renames keep all three distinct.
        /// </summary>
        private static string RenameMergedAudioKey(string section, string key)
        {
            if (section == "05 - Reactive Audio")
            {
                switch (key)
                {
                    case "Enabled": return "Sting Enabled";
                    case "Volume": return "Sting Volume";
                    case "Set Preference": return "Sting Set Preference";
                    case "Spawn Sound Max Seconds": return "Sting Spawn Max Seconds";
                    case "Spawn Sound Fade Seconds": return "Sting Spawn Fade Seconds";
                }
            }
            else if (section == "07 - Interior Ambience" && key == "Enabled")
            {
                return "Ambience Enabled";
            }

            return key;
        }

        private static int MigrateReactiveAudioValues(Dictionary<ConfigDefinition, string> target)
        {
            int imported = 0;
            string sourcePath = Path.Combine(Paths.ConfigPath, "com.y4ngz.upgrades.cfg");
            foreach (LegacyValue value in ReadLegacyValues(sourcePath))
            {
                if (!string.Equals(value.Section, "Reactive Audio", StringComparison.Ordinal))
                    continue;
                // Lands directly in the merged v7 section, under the same renamed
                // keys a "05 - Reactive Audio" value would have been mapped to.
                string key = RenameMergedAudioKey("05 - Reactive Audio", value.Key);
                imported += SetIfMissing(target, new ConfigDefinition("05 - Audio", key), value.Value);
            }
            return imported;
        }

        // Schema v8 raises the two CCTV defaults without clobbering profiles
        // that deliberately customized either value. Exact old defaults are
        // the only values migrated.
        private static int MigrateCctvSecurityDefaults(Dictionary<ConfigDefinition, string> target)
        {
            int migrated = 0;
            migrated += ReplaceFloatDefault(
                target,
                new ConfigDefinition("96 - CCTV Security - Shared", "RotationSeconds"),
                22f,
                90f);
            migrated += ReplaceFloatDefault(
                target,
                new ConfigDefinition("96 - CCTV Security - Shared", "CameraHealth"),
                2f,
                6f);
            return migrated;
        }

        // Schema v10 (Job 4.2) raises the chainsaw walk speed 0.98 -> 1.7 to match the clip cadence
        // reference moving to 1.4. BepInEx writes every default to disk on first run, so without
        // this every existing profile keeps 0.98 and the new gait never reaches the player -- the
        // stride change would look like it silently did nothing. Only the exact old default is
        // migrated, so a deliberately customized walk speed survives untouched.
        /// <summary>
        /// Schema v13 (#361). The single crossfading bed became a constant ground plus
        /// a modulated atmos layer, so "Bed Volume" carries forward as "Ground Volume"
        /// -- same role, same default, so a player who tuned it keeps their setting.
        ///
        /// The keys that went away need no work here: "Bed Crossfade Seconds" (nothing
        /// crossfades now) and "Sting Spawn Max/Fade Seconds" (they only ever governed
        /// the deleted drone layer). BepInEx leaves an unbound key in the file as a
        /// harmless orphan, exactly as it did for "Sting Set Preference" at v7.
        /// </summary>
        private static int MigrateGroundAtmosValues(Dictionary<ConfigDefinition, string> target)
        {
            ConfigDefinition legacy = new ConfigDefinition("05 - Audio", "Bed Volume");
            ConfigDefinition current = new ConfigDefinition("05 - Audio", "Ground Volume");

            if (!target.TryGetValue(legacy, out string raw))
                return 0;

            target.Remove(legacy);
            // A value already under the new key wins: it is the one the player is
            // actually running, and the legacy key can only be staler than it.
            if (target.ContainsKey(current))
                return 0;

            target[current] = raw;
            return 1;
        }

        /// <summary>
        /// Schema v14 (#370). The orbit and surface content flags still named
        /// pages that stopped existing in #365 and #367, and every one of those
        /// names selected nothing.
        ///
        /// That was not cosmetic. <c>OrbitContent</c> defaulted to
        /// <c>Clearance, Route, Crew</c>, so on every install — fresh as much as
        /// migrated — the orbit row could show only the crew page, and the save
        /// page #365 added was unreachable. Migrating saved values alone would
        /// not have fixed it; the bound default had to move too.
        ///
        /// <c>Clearance</c> and <c>Route</c> carry forward as <c>Save</c>, the
        /// page that replaced them. The three retired surface names are dropped
        /// instead of mapped: what they used to say is now said by the alert
        /// belts, and those are chosen with the new <c>Belts</c> key rather than
        /// by a page flag. The belts are default-on, so an install that had all
        /// three surface pages keeps being told all three things.
        ///
        /// A list left empty becomes <c>None</c> rather than the new default: a
        /// player whose value had no live flag in it had deselected the crew
        /// page deliberately, and silently switching it back on would overrule
        /// them.
        /// </summary>
        private static int MigrateMonitorRowContentValues(Dictionary<ConfigDefinition, string> target)
        {
            const string section = "78 - Ship Systems - Monitor Row Content";

            Dictionary<string, string> orbit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Clearance"] = "Save",
                ["Route"] = "Save"
            };
            Dictionary<string, string> landed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Operations"] = null,
                ["BatteryAlert"] = null,
                ["Escalation"] = null
            };

            return RewriteFlagList(target, new ConfigDefinition(section, "OrbitContent"), orbit)
                + RewriteFlagList(target, new ConfigDefinition(section, "LandedContent"), landed);
        }

        /// <summary>
        /// Rewrites one comma-separated flags value in place. A token maps to
        /// its replacement, drops when that replacement is null, and is left
        /// exactly as it was when the table does not mention it — so a live flag
        /// and a flag from some other version both survive. Order is kept and
        /// repeats are collapsed, which is what stops <c>Clearance, Route</c>
        /// becoming <c>Save, Save</c>.
        /// </summary>
        private static int RewriteFlagList(
            Dictionary<ConfigDefinition, string> target,
            ConfigDefinition definition,
            Dictionary<string, string> table)
        {
            if (!target.TryGetValue(definition, out string raw) || raw == null)
                return 0;

            List<string> kept = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string token in raw.Split(','))
            {
                string trimmed = token.Trim();
                if (trimmed.Length == 0)
                    continue;

                string mapped = trimmed;
                if (table.TryGetValue(trimmed, out string replacement))
                {
                    if (replacement == null)
                        continue;
                    mapped = replacement;
                }

                if (seen.Add(mapped))
                    kept.Add(mapped);
            }

            string rewritten = kept.Count > 0 ? string.Join(", ", kept) : "None";
            if (string.Equals(rewritten, raw.Trim(), StringComparison.Ordinal))
                return 0;

            target[definition] = rewritten;
            return 1;
        }

        private static int MigrateChainsawWalkSpeedDefault(Dictionary<ConfigDefinition, string> target)
        {
            return ReplaceFloatDefault(
                target,
                new ConfigDefinition("Gameplay", "ChainsawWalkSpeed"),
                0.98f,
                1.7f);
        }

        private static int ReplaceFloatDefault(
            Dictionary<ConfigDefinition, string> target,
            ConfigDefinition definition,
            float oldDefault,
            float newDefault)
        {
            if (!target.TryGetValue(definition, out string raw)
                || !float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                || Math.Abs(value - oldDefault) > 0.0001f)
            {
                return 0;
            }

            target[definition] = newDefault.ToString("0.###", CultureInfo.InvariantCulture);
            return 1;
        }

        // Scoped v6 import: UITheme ownership moved to Company (#153). Reads ONLY
        // the four theme keys from [UI] in com.y4ngz.upgrades.cfg (live plugin
        // file — same never-in-LegacyConfigFiles rule as the v5 import above) and
        // from the Company-legacy standalone Y4NGZ_UITheme.cfg. The Upgrades file
        // is read first: its copy was the live vanilla-recoloring surface, so its
        // values win under SetIfMissing.
        private static int MigrateUiThemeValues(Dictionary<ConfigDefinition, string> target)
        {
            string[] themeKeys =
            {
                "Color Theme",
                "Vanilla UI Recoloring Enabled",
                "Runtime Theme Sweeps Enabled",
                "Runtime Theme Sweep Interval Seconds",
            };
            int imported = 0;
            string standalonePath = Path.Combine(Paths.ConfigPath, "Y4NGZ_UITheme.cfg");
            string[] sourcePaths =
            {
                Path.Combine(Paths.ConfigPath, "com.y4ngz.upgrades.cfg"),
                standalonePath,
            };
            foreach (string sourcePath in sourcePaths)
            {
                foreach (LegacyValue value in ReadLegacyValues(sourcePath))
                {
                    if (!string.Equals(value.Section, "UI", StringComparison.Ordinal))
                        continue;
                    if (Array.IndexOf(themeKeys, value.Key) < 0)
                        continue;
                    imported += SetIfMissing(target, new ConfigDefinition("06 - UI Theme", value.Key), value.Value);
                }
            }

            RetireStandaloneUiThemeConfig(standalonePath);
            return imported;
        }


        // Schema v11 imports only the seven settings exposed by EladsHUD. The
        // source file remains untouched so uninstalling either package is safe,
        // and existing Company values always win through SetIfMissing.
        private static int MigrateEladsHudValues(Dictionary<ConfigDefinition, string> target)
        {
            string sourcePath = Path.Combine(Paths.ConfigPath, "me.eladnlg.customhud.cfg");
            if (!File.Exists(sourcePath))
                return 0;

            int imported = 0;
            foreach (LegacyValue value in ReadLegacyValues(sourcePath))
            {
                ConfigDefinition definition;
                string migratedValue = value.Value;
                switch (value.Key)
                {
                    case "HUDScale":
                        definition = new ConfigDefinition("06 - UI Theme", "Player HUD Scale");
                        break;
                    case "HideHealthbarAutomatically":
                        definition = new ConfigDefinition("06 - UI Theme", "Player HUD Auto Hide Health");
                        break;
                    case "HealthbarHideDelay":
                        definition = new ConfigDefinition("06 - UI Theme", "Player HUD Health Hide Delay");
                        break;
                    case "FlashlightBattery":
                        definition = new ConfigDefinition("06 - UI Theme", "Player HUD Pocket Flashlight Mode");
                        break;
                    case "DetailedStamina":
                        definition = new ConfigDefinition("06 - UI Theme", "Player HUD Stamina Detail");
                        break;
                    case "DisplayTimeLeft":
                        definition = new ConfigDefinition("06 - UI Theme", "Player HUD Display Battery Time");
                        break;
                    case "HidePlanetInfo":
                        definition = new ConfigDefinition("06 - UI Theme", "Player HUD Show Planet Information");
                        if (bool.TryParse(value.Value, out bool hidePlanetInfo))
                            migratedValue = (!hidePlanetInfo).ToString();
                        break;
                    default:
                        continue;
                }

                imported += SetIfMissing(target, definition, migratedValue);
            }

            return imported;
        }
        // Nothing binds Y4NGZ_UITheme.cfg after v6; retire it to a .bak so users
        // don't keep editing a dead file. Never allowed to fail the migration.
        private static void RetireStandaloneUiThemeConfig(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                string retired = path + ".pre-schema-v6.bak";
                if (File.Exists(retired))
                    File.Delete(path);
                else
                    File.Move(path, retired);
            }
            catch
            {
            }
        }

        private static int MigrateWhistleblowerValues(
            Dictionary<ConfigDefinition, string> source,
            Dictionary<ConfigDefinition, string> target)
        {
            int imported = CopyIfPresent(source, target,
                new ConfigDefinition("Whistleblower", "UseFallbackPrimitiveVisual"),
                new ConfigDefinition("65 - Whistleblower - Shared", "UseFallbackPrimitiveVisual"));
            foreach (string key in new[] { "MinSpawnPathDistance", "MinSpawnHorizontalDistance", "MinSpawnTileDepth", "TopSpawnScorePercent" })
            {
                if (!source.TryGetValue(new ConfigDefinition("Whistleblower", key), out string value)) continue;
                foreach (int number in new[] { 66, 67, 68, 69 })
                {
                    string risk = number == 66 ? "C" : number == 67 ? "B" : number == 68 ? "A" : "S";
                    imported += SetIfMissing(target,
                        new ConfigDefinition($"{number:00} - Whistleblower - Risk {risk}", key), value);
                }
            }
            return imported;
        }

        private static int ImportSimpleTierMap(
            Dictionary<ConfigDefinition, string> source,
            Dictionary<ConfigDefinition, string> target,
            string oldKey,
            string newKey,
            int c,
            int b,
            int a,
            int s)
        {
            if (!source.TryGetValue(new ConfigDefinition("Contract Balance", oldKey), out string raw))
                return 0;
            int imported = 0;
            bool hasExplicitC = HasTierEntry(raw, ',', "C");
            foreach (string entry in (raw ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = entry.IndexOf('=');
                if (equals <= 0) continue;
                string risk = entry.Substring(0, equals).Trim().ToUpperInvariant();
                if (risk == "D" && hasExplicitC) continue;
                int number;
                switch (risk) { case "D": case "C": number = c; break; case "B": number = b; break; case "A": number = a; break; case "S": number = s; break; default: continue; }
                string value = entry.Substring(equals + 1).Trim();
                ConfigDefinition definition = new ConfigDefinition(ContractRiskSection(oldKey, number), newKey);
                imported += SetIfMissing(target, definition, value);
            }
            return imported;
        }

        private static string ContractRiskSection(string oldKey, int number)
        {
            string contract = oldKey.StartsWith("Blackout", StringComparison.Ordinal) ? "Blackout Audit"
                : oldKey.StartsWith("Survey", StringComparison.Ordinal) ? "Survey"
                : "Shadow Raid";
            string risk = number % 10 == 1 ? "C" : number % 10 == 2 ? "B" : number % 10 == 3 ? "A" : "S";
            return $"{number:00} - {contract} - Risk {risk}";
        }

        private static string ShadowRiskSection(int number)
        {
            string risk = number == 51 ? "C" : number == 52 ? "B" : number == 53 ? "A" : "S";
            return $"{number:00} - Shadow Raid - Risk {risk}";
        }

        private static bool IsCoreContractTierKey(string key)
        {
            return key == "BlackoutBreakerCountByTier"
                || key == "BlackoutAmbientFlickerIntervalSecondsByTier"
                || key == "SurveyDroneCountByTier"
                || key == "ShadowRaidDrillDurationSecondsByTier"
                || key == "ShadowRaidContainerCountByTier";
        }

        private static int ImportTierValues(
            Dictionary<ConfigDefinition, string> source,
            Dictionary<ConfigDefinition, string> target,
            string legacyKey,
            string newKey,
            char separator)
        {
            if (!source.TryGetValue(new ConfigDefinition("Contract Balance", legacyKey), out string raw))
                return 0;

            int imported = 0;
            bool hasExplicitC = HasTierEntry(raw, separator, "C");
            foreach (string entry in (raw ?? string.Empty).Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = entry.IndexOf('=');
                if (equals <= 0) continue;
                string risk = entry.Substring(0, equals).Trim();
                if (risk.Equals("D", StringComparison.OrdinalIgnoreCase) && hasExplicitC) continue;
                string section = ContainmentRiskSection(risk);
                if (section == null) continue;
                string value = entry.Substring(equals + 1).Trim();
                if (value.Length == 0) continue;
                ConfigDefinition definition = new ConfigDefinition(section, newKey);
                imported += SetIfMissing(target, definition, value);
            }
            return imported;
        }

        private static bool HasTierEntry(string raw, char separator, string wantedRisk)
        {
            foreach (string entry in (raw ?? string.Empty).Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = entry.IndexOf('=');
                if (equals > 0 && entry.Substring(0, equals).Trim().Equals(wantedRisk, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static int CopyIfPresent(
            Dictionary<ConfigDefinition, string> source,
            Dictionary<ConfigDefinition, string> target,
            ConfigDefinition oldDefinition,
            ConfigDefinition newDefinition)
        {
            return source.TryGetValue(oldDefinition, out string value)
                ? SetIfMissing(target, newDefinition, value)
                : 0;
        }

        private static int CopyFirstIfPresent(
            Dictionary<ConfigDefinition, string> source,
            Dictionary<ConfigDefinition, string> target,
            ConfigDefinition newDefinition,
            params ConfigDefinition[] oldDefinitions)
        {
            return TryGetFirstValue(source, out string value, oldDefinitions)
                ? SetIfMissing(target, newDefinition, value)
                : 0;
        }

        private static bool TryGetFirstValue(
            Dictionary<ConfigDefinition, string> source,
            out string value,
            params ConfigDefinition[] definitions)
        {
            foreach (ConfigDefinition definition in definitions)
            {
                if (source.TryGetValue(definition, out value))
                    return true;
            }
            value = null;
            return false;
        }

        private static int SetIfMissing(
            Dictionary<ConfigDefinition, string> target,
            ConfigDefinition definition,
            string value)
        {
            if (target.ContainsKey(definition)) return 0;
            target[definition] = value;
            return 1;
        }

        private static IEnumerable<string> ContainmentRiskSections()
        {
            yield return "41 - Containment Breach - Risk C";
            yield return "42 - Containment Breach - Risk B";
            yield return "43 - Containment Breach - Risk A";
            yield return "44 - Containment Breach - Risk S";
        }

        private static string ContainmentRiskSection(string risk)
        {
            switch ((risk ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "D":
                case "C": return "41 - Containment Breach - Risk C";
                case "B": return "42 - Containment Breach - Risk B";
                case "A": return "43 - Containment Breach - Risk A";
                case "S": return "44 - Containment Breach - Risk S";
                default: return null;
            }
        }

        private static string ComputeLegacySourceFingerprint()
        {
            var material = new StringBuilder();
            foreach (string fileName in LegacyConfigFiles)
            {
                string path = Path.Combine(Paths.ConfigPath, fileName);
                if (!File.Exists(path)) continue;
                material.Append(fileName).Append(':').Append(ComputeFileSha256(path)).Append(';');
            }

            byte[] bytes = Encoding.UTF8.GetBytes(material.ToString());
            using (SHA256 sha = SHA256.Create())
                return ToHex(sha.ComputeHash(bytes));
        }

        private static string ComputeFileSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return ToHex(sha.ComputeHash(stream));
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                builder.Append(bytes[i].ToString("x2"));
            return builder.ToString();
        }

        private static bool HasCompletedMigrationStamp(string path)
        {
            return HasMigrationStamp(path, CurrentSchemaVersion);
        }

        private static bool HasMigrationStamp(string path, int minimumSchemaVersion)
        {
            if (!File.Exists(path)) return false;
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("schema=", StringComparison.Ordinal)) continue;
                    if (int.TryParse(trimmed.Substring("schema=".Length), out int schemaVersion)
                        && schemaVersion >= minimumSchemaVersion)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static void EnsureMigrationStamp(string path, string fingerprint)
        {
            if (HasCompletedMigrationStamp(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temp, new[]
                {
                    $"schema={CurrentSchemaVersion}",
                    $"legacyFingerprint={fingerprint}",
                    $"completedUtc={DateTime.UtcNow:O}"
                });
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static void WriteConfigValuesAtomic(string path, Dictionary<ConfigDefinition, string> values)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string backupPath = path + $".pre-schema-v{CurrentSchemaVersion}-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".bak";
            try
            {
                WriteConfigValues(tempPath, values);
                if (!File.Exists(path))
                {
                    File.Move(tempPath, path);
                    return;
                }

                try
                {
                    File.Replace(tempPath, path, backupPath, true);
                }
                catch (PlatformNotSupportedException)
                {
                    ReplaceWithRollback(tempPath, path, backupPath);
                }
                catch (IOException)
                {
                    ReplaceWithRollback(tempPath, path, backupPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static void ReplaceWithRollback(string tempPath, string path, string backupPath)
        {
            File.Copy(path, backupPath, false);
            string rollbackPath = path + ".rollback-" + Guid.NewGuid().ToString("N");
            File.Move(path, rollbackPath);
            try
            {
                File.Move(tempPath, path);
                File.Delete(rollbackPath);
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(rollbackPath, path);
                throw;
            }
        }

        private static void WriteConfigValues(string path, Dictionary<ConfigDefinition, string> values)
        {
            SortedDictionary<string, SortedDictionary<string, string>> sections =
                new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<ConfigDefinition, string> pair in values)
            {
                if (!sections.TryGetValue(pair.Key.Section, out SortedDictionary<string, string> entries))
                {
                    entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    sections[pair.Key.Section] = entries;
                }
                entries[pair.Key.Key] = pair.Value;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (StreamWriter writer = new StreamWriter(path, false))
            {
                writer.WriteLine("## Y4NGZCompany configuration. Legacy section names were normalized automatically.");
                foreach (KeyValuePair<string, SortedDictionary<string, string>> section in sections)
                {
                    writer.WriteLine();
                    writer.Write('[');
                    writer.Write(section.Key);
                    writer.WriteLine(']');
                    foreach (KeyValuePair<string, string> entry in section.Value)
                    {
                        writer.Write(entry.Key);
                        writer.Write(" = ");
                        writer.WriteLine(entry.Value);
                    }
                }
            }
        }

        private static Dictionary<ConfigDefinition, string> ReadConfigValues(string path)
        {
            Dictionary<ConfigDefinition, string> values = new Dictionary<ConfigDefinition, string>();
            foreach (LegacyValue value in ReadLegacyValues(path))
                values[new ConfigDefinition(value.Section, value.Key)] = value.Value;
            return values;
        }

        private static IEnumerable<LegacyValue> ReadLegacyValues(string path)
        {
            if (!File.Exists(path))
                yield break;

            string section = null;
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("##", StringComparison.Ordinal))
                    continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                if (section == null)
                    continue;

                int equalsIndex = line.IndexOf('=');
                if (equalsIndex <= 0)
                    continue;

                string key = line.Substring(0, equalsIndex).Trim();
                string value = line.Substring(equalsIndex + 1).Trim();
                if (key.Length == 0)
                    continue;

                yield return new LegacyValue(section, key, value);
            }
        }

        private readonly struct LegacyValue
        {
            internal LegacyValue(string section, string key, string value)
            {
                Section = section;
                Key = key;
                Value = value;
            }

            internal string Section { get; }
            internal string Key { get; }
            internal string Value { get; }
        }
    }
}
