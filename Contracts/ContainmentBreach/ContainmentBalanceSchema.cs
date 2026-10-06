using BepInEx.Configuration;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core;

namespace Y4NGZCompany.Contracts.ContainmentBreach
{
    // #694: one plain "Containment Breach" section; per-risk variation lives in
    // tier-table entries. EnemiesPerWave was identical across tiers and is a single
    // value now. ForRisk keeps the historical view shape so call sites are unchanged.
    internal sealed class ContainmentBalanceConfig
    {
        private readonly RiskTierTable<int> _waveCount;
        private readonly RiskTierTable<int> _waveDurationSeconds;
        private readonly ConfigEntry<int> _enemiesPerWave;

        internal ContainmentBalanceConfig(ConfigFile config)
        {
            _waveCount = RiskTierTable.BindInt(config, ContractedConfigSections.ContainmentBreach,
                "Waves By Risk", "C=1, B=2, A=3, S=4", 1, 8,
                "Host only. Enemy waves that must be survived.");
            _waveDurationSeconds = RiskTierTable.BindInt(config, ContractedConfigSections.ContainmentBreach,
                "Wave Duration Seconds By Risk", "C=45, B=60, A=75, S=90", 15, 300,
                "Host only. Seconds players must survive each wave.");
            _enemiesPerWave = config.Bind(ContractedConfigSections.ContainmentBreach, "Enemies Per Wave", 2,
                new ConfigDescription(
                    "Host only. Enemies spawned at the start of each wave.",
                    new AcceptableValueRange<int>(1, 8)));
        }

        internal RiskConfig ForRisk(char risk) => new RiskConfig(this, risk);

        internal sealed class RiskConfig
        {
            internal readonly DerivedSetting<int> WaveCount;
            internal readonly DerivedSetting<int> WaveDurationSeconds;
            internal readonly ConfigEntry<int> EnemiesPerWave;

            internal RiskConfig(ContainmentBalanceConfig owner, char risk)
            {
                WaveCount = owner._waveCount.View(risk);
                WaveDurationSeconds = owner._waveDurationSeconds.View(risk);
                EnemiesPerWave = owner._enemiesPerWave;
            }
        }
    }
}
