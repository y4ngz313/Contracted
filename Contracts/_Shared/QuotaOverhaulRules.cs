using System;
using System.Globalization;

using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Contracts._Shared
{
    internal static class QuotaOverhaulRules
    {
        internal const string DefaultHazardPayPercentByWeather = "Eclipse=10";

        private const string BaseQuotaKey = "Y4NGZ_QuotaOverhaulBaseQuota";
        private const string FirstQuotaPlayerCountKey = "Y4NGZ_QuotaOverhaulFirstQuotaPlayerCount";
        private const string MoonPlayerCountKey = "Y4NGZ_QuotaOverhaulMoonPlayerCount";

        private static int _baseQuota;
        private static bool _hasBaseQuota;
        private static int _recordPlayersThisQuota;
        private static int _recordPlayersThisMoon;
        private static int _pendingRolloverQuota;
        private static bool _deathPenaltyModifierWarned;

        /// <summary>
        /// Optional death-penalty fraction modifier supplied by companion mods
        /// (Y4NGZUpgrades' Veteran). Receives the computed per-round-end penalty
        /// fraction and returns a possibly-reduced fraction; the result is clamped
        /// to [0, original] so a modifier can only reduce the penalty, never
        /// increase it. Applied on the server/host path when the crew-loss quota
        /// penalty is charged. Same seam pattern as
        /// CctvSupportApi.DetectionTimeMultiplierProvider.
        /// </summary>
        public static System.Func<float, float> DeathPenaltyFractionModifier = null;

        internal static bool IsEnabled
        {
            get { return ContractsBootstrap.QuotaOverhaulEnabled == null || ContractsBootstrap.QuotaOverhaulEnabled.Value; }
        }

        /// <summary>
        /// True when this machine owns quota decisions: the host of a live session, or any
        /// machine with no live session at all (solo save load before the session is listening,
        /// and the offline regression harness, both of which must behave exactly as before).
        /// Mirrors <c>ContractProgressionNetwork.BroadcastServer</c>'s "no live session" branch.
        /// </summary>
        internal static bool IsServerRuntime()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening)
                return true;

            return manager.IsServer;
        }

        /// <summary>
        /// Single choke point for every quota-target write (#517). A quota of $0 reads as
        /// already fulfilled to vanilla (<c>timeUntilDeadline</c> checks against $0 sold), so
        /// the target is floored at $1; a lower attempt is logged as a warning.
        /// </summary>
        internal static int ClampQuotaTarget(int quota, string context)
        {
            int clamped = Mathf.Clamp(quota, 1, 1000000000);
            if (quota < 1)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Quota] Refused quota target ${quota} ({context}); floored to ${clamped} - a $0 quota reads as already fulfilled (#517).");
            }

            return clamped;
        }

        /// <summary>
        /// Client-side apply for a quota the host authored. Never touches the save file:
        /// a client's <c>currentSaveFileName</c> is its own solo slot (#424).
        /// </summary>
        internal static void ApplyReplicatedQuota(int quota, string reason)
        {
            var time = TimeOfDay.Instance;
            if (time == null || quota < 0)
                return;

            time.profitQuota = ClampQuotaTarget(quota, string.IsNullOrWhiteSpace(reason) ? "host sync" : reason);
            time.UpdateProfitQuotaCurrentTime();
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Applied host quota ${time.profitQuota} ({(string.IsNullOrWhiteSpace(reason) ? "host sync" : reason)}).");
        }

        internal static int CalculateFirstQuotaPlayerScaledBase(int playerCount)
        {
            int startingQuota = Mathf.Max(1, ContractsBootstrap.QuotaStartingQuota?.Value ?? 200);
            if (ContractsBootstrap.QuotaPlayerMultiplierEnabled != null && !ContractsBootstrap.QuotaPlayerMultiplierEnabled.Value)
                return startingQuota;

            int cap = Mathf.Max(1, ContractsBootstrap.QuotaPlayerMultiplierCap?.Value ?? 8);
            int threshold = Mathf.Max(0, ContractsBootstrap.QuotaPlayerMultiplierThreshold?.Value ?? 1);
            int countedPlayers = Mathf.Clamp(Mathf.Max(1, playerCount), 1, cap) - threshold;
            if (countedPlayers < 0)
                countedPlayers = 0;

            float perPlayer = Mathf.Max(0f, ContractsBootstrap.QuotaPlayerMultiplierPerPlayer?.Value ?? 0.25f);
            float quota = startingQuota * (1f + perPlayer * countedPlayers);
            return Mathf.Max(1, Mathf.FloorToInt(quota));
        }

        internal static void EnsureSaveStateLoaded(StartOfRound start)
        {
            if (!IsEnabled || start == null || start.isChallengeFile)
                return;

            var time = TimeOfDay.Instance;
            if (time == null)
                return;

            if (!IsServerRuntime())
            {
                // #424: a client's currentSaveFileName is its own solo save slot, so neither
                // reading run state from it nor writing into it means anything here. The host's
                // quota reaches this machine through vanilla's OnPlayerConnectedClientRpc and
                // through QuotaSyncNetwork. Drop any state left over from a previous session
                // this process hosted so nothing stale can leak into a client's report.
                ResetLocalState();
                ContractsBootstrap.Log?.LogDebug(
                    "[MoonContracts] Quota overhaul save state skipped on client; host quota is authoritative.");
                return;
            }

            LoadSavedState(time);

            if (time.timesFulfilledQuota <= 0 && !_hasBaseQuota)
            {
                if (time.quotaFulfilled <= 0 && start.gameStats.daysSpent <= 0)
                {
                    InitializeNewRun(start, "save load");
                    return;
                }

                _baseQuota = Mathf.Max(1, time.profitQuota);
                _hasBaseQuota = true;
                _recordPlayersThisQuota = Mathf.Max(1, GetCurrentTotalPlayers(start));
                _recordPlayersThisMoon = _recordPlayersThisQuota;
                SaveAllQuotaState(time);
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Inferred quota overhaul base quota ${_baseQuota} from existing first-quota save.");
                return;
            }

            if (!_hasBaseQuota)
            {
                _baseQuota = Mathf.Max(1, time.profitQuota);
                _hasBaseQuota = true;
                SaveBaseQuota(_baseQuota);
            }
        }

        private static void ResetLocalState()
        {
            _baseQuota = 0;
            _hasBaseQuota = false;
            _recordPlayersThisQuota = 0;
            _recordPlayersThisMoon = 0;
            _pendingRolloverQuota = 0;
        }

        internal static void InitializeNewRun(StartOfRound start, string reason)
        {
            if (!IsEnabled || start == null || start.isChallengeFile)
                return;

            // Host-authoritative (#424). StartOfRound.ResetShip runs on every machine, so
            // without this gate a client would rewrite its own quota and solo save file.
            if (!IsServerRuntime())
            {
                ResetLocalState();
                return;
            }

            var time = TimeOfDay.Instance;
            if (time == null)
                return;

            int playerCount = GetCurrentTotalPlayers(start);
            _recordPlayersThisQuota = Mathf.Max(1, playerCount);
            _recordPlayersThisMoon = _recordPlayersThisQuota;
            _baseQuota = CalculateFirstQuotaPlayerScaledBase(_recordPlayersThisQuota);
            _hasBaseQuota = true;
            _pendingRolloverQuota = 0;

            ApplyQuotaToTime(time, _baseQuota);
            SaveAllQuotaState(time);

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Initialized quota overhaul base quota ${_baseQuota} for {_recordPlayersThisQuota} player(s) ({reason}).");
        }

        internal static void RecordCurrentPlayerCountForFirstQuota()
        {
            RecordCurrentPlayerCountForFirstQuota(GetCurrentTotalPlayers(StartOfRound.Instance), "current player count");
        }

        internal static void RecordCurrentPlayerCountForFirstQuota(int playerCount, string reason)
        {
            if (!IsEnabled || !IsServerRuntime())
                return;

            var start = StartOfRound.Instance;
            var time = TimeOfDay.Instance;
            if (start == null || time == null || start.isChallengeFile || time.timesFulfilledQuota > 0)
                return;

            int totalPlayers = Mathf.Max(1, playerCount);
            if (totalPlayers <= _recordPlayersThisQuota && _hasBaseQuota)
                return;

            _recordPlayersThisQuota = totalPlayers;
            _baseQuota = CalculateFirstQuotaPlayerScaledBase(_recordPlayersThisQuota);
            _hasBaseQuota = true;

            ApplyQuotaToTime(time, _baseQuota);
            SaveAllQuotaState(time);

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Updated first quota to ${_baseQuota} for {_recordPlayersThisQuota} player(s) ({reason}).");
        }

        internal static void RecordCurrentPlayerCountForMoon()
        {
            RecordCurrentPlayerCountForMoon(GetCurrentTotalPlayers(StartOfRound.Instance));
        }

        internal static void RecordCurrentPlayerCountForMoon(int playerCount)
        {
            if (!IsEnabled || !IsServerRuntime())
                return;

            int totalPlayers = Mathf.Max(1, playerCount);
            if (totalPlayers <= _recordPlayersThisMoon)
                return;

            _recordPlayersThisMoon = totalPlayers;
            SaveInt(MoonPlayerCountKey, _recordPlayersThisMoon);
        }

        internal static int ApplyNextBaseQuotaAfterCompletion(TimeOfDay time)
        {
            if (!IsEnabled || time == null || !IsServerRuntime())
                return time != null ? Mathf.Max(0, time.profitQuota) : 0;

            EnsureBaseQuota(time);

            int percent = Mathf.Max(0, ContractsBootstrap.QuotaBaseIncreasePercent?.Value ?? 25);
            float multiplier = 1f + percent / 100f;
            _baseQuota = Mathf.Max(1, Mathf.CeilToInt(_baseQuota * multiplier));
            _hasBaseQuota = true;
            _recordPlayersThisQuota = Mathf.Max(1, GetCurrentTotalPlayers(StartOfRound.Instance));
            _recordPlayersThisMoon = _recordPlayersThisQuota;

            SaveAllQuotaState(time);
            return _baseQuota;
        }

        internal static void PrepareNextBaseQuotaAfterCompletion(TimeOfDay time)
        {
            // A client must never stage a rollover quota: the host stages it, rewrites the
            // wire value in SyncNewProfitQuotaClientRpc, and the client applies what arrives.
            // Gating here keeps _pendingRolloverQuota provably 0 on clients rather than
            // accidentally 0, so OverrideSyncedRolloverQuota can never fire client-side.
            if (!IsEnabled || time == null || !IsServerRuntime())
            {
                _pendingRolloverQuota = 0;
                return;
            }

            _pendingRolloverQuota = ApplyNextBaseQuotaAfterCompletion(time);
        }

        internal static void OverrideSyncedRolloverQuota(ref int newProfitQuota)
        {
            if (!IsEnabled || _pendingRolloverQuota <= 0)
                return;

            newProfitQuota = _pendingRolloverQuota;
        }

        internal static void ApplyPreparedRolloverQuota(TimeOfDay time)
        {
            if (!IsEnabled || time == null || _pendingRolloverQuota <= 0 || !IsServerRuntime())
                return;

            ApplyQuotaToTime(time, _pendingRolloverQuota);
            SaveAllQuotaState(time);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Quota overhaul rolled base quota to ${_pendingRolloverQuota}.");
            _pendingRolloverQuota = 0;
        }

        internal static float CalculateDeathPenaltyFraction(int playersDead, int bodiesInsured)
        {
            return CalculateDeathPenaltyFraction(playersDead, bodiesInsured, _recordPlayersThisMoon);
        }

        internal static float CalculateDeathPenaltyFraction(int playersDead, int bodiesInsured, int recordedPlayers)
        {
            if (!IsEnabled || ContractsBootstrap.QuotaDeathPenaltyEnabled != null && !ContractsBootstrap.QuotaDeathPenaltyEnabled.Value)
                return 0f;

            int dead = Mathf.Max(0, playersDead);
            if (dead <= 0)
                return 0f;

            int recovered = Mathf.Clamp(bodiesInsured, 0, dead);
            int playerCount = Mathf.Max(1, recordedPlayers);
            bool dynamicPenalty = ContractsBootstrap.QuotaDeathPenaltyDynamic == null || ContractsBootstrap.QuotaDeathPenaltyDynamic.Value;
            float cap = Mathf.Clamp01(ContractsBootstrap.QuotaDeathPenaltyDynamicCap?.Value ?? 0.5f);
            float recoveryBonus = Mathf.Clamp01(ContractsBootstrap.QuotaDeathPenaltyRecoveryBonus?.Value ?? 0.5f);
            float penaltyPerDead = dynamicPenalty
                ? cap / playerCount
                : Mathf.Clamp01(ContractsBootstrap.QuotaDeathPenaltyPercentPerPlayer?.Value ?? 0.12f);

            float penalty = penaltyPerDead * dead;
            penalty -= penaltyPerDead * recoveryBonus * recovered;
            float threshold = Mathf.Clamp01(ContractsBootstrap.QuotaDeathPenaltyThreshold?.Value ?? 0.25f);
            if (penalty < threshold)
                return 0f;

            return Mathf.Clamp01(penalty);
        }

        internal static float ApplyDeathPenaltyFractionModifier(float fraction)
        {
            System.Func<float, float> modifier = DeathPenaltyFractionModifier;
            if (modifier == null || fraction <= 0f)
                return fraction;

            try
            {
                // Reduce-only: a companion modifier can soften the crew-loss
                // penalty but never worsen it past the computed fraction.
                return Mathf.Clamp(modifier(fraction), 0f, fraction);
            }
            catch (Exception ex)
            {
                if (!_deathPenaltyModifierWarned)
                {
                    _deathPenaltyModifierWarned = true;
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts] DeathPenaltyFractionModifier threw; using unmodified fraction. {ex.Message}");
                }
                return fraction;
            }
        }

        internal static bool TryGetHazardPayPercentForCurrentMoon(out int percent, out string weatherName)
        {
            percent = 0;
            weatherName = string.Empty;

            var start = StartOfRound.Instance;
            if (!IsEnabled || start == null || start.currentLevel == null)
                return false;

            LevelWeatherType weather = start.currentLevel.currentWeather;
            if (weather == LevelWeatherType.None)
                return false;

            string actualName = weather.ToString();
            string config = ContractsBootstrap.QuotaHazardPayPercentByWeather?.Value ?? DefaultHazardPayPercentByWeather;
            foreach (string entry in config.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = entry.IndexOf('=');
                if (separator <= 0 || separator >= entry.Length - 1)
                    continue;

                string key = entry.Substring(0, separator).Trim();
                string rawValue = entry.Substring(separator + 1).Trim();
                if (!WeatherMatches(actualName, key))
                    continue;

                if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                    continue;

                percent = Mathf.Clamp(parsed, 0, 100);
                weatherName = key;
                return percent > 0;
            }

            return false;
        }

        private static void EnsureBaseQuota(TimeOfDay time)
        {
            if (_hasBaseQuota)
                return;

            LoadSavedState(time);
            if (_hasBaseQuota)
                return;

            _baseQuota = Mathf.Max(1, time != null ? time.profitQuota : ContractsBootstrap.QuotaStartingQuota?.Value ?? 200);
            _hasBaseQuota = true;
            SaveBaseQuota(_baseQuota);
        }

        private static void LoadSavedState(TimeOfDay time)
        {
            string saveName = GetSaveName();
            if (string.IsNullOrWhiteSpace(saveName))
                return;

            if (ES3.KeyExists(BaseQuotaKey, saveName))
            {
                _baseQuota = Mathf.Max(1, ES3.Load(BaseQuotaKey, saveName, time != null ? time.profitQuota : 200));
                _hasBaseQuota = true;
            }

            _recordPlayersThisQuota = Mathf.Max(
                0,
                ES3.Load(FirstQuotaPlayerCountKey, saveName, _recordPlayersThisQuota));
            _recordPlayersThisMoon = Mathf.Max(
                _recordPlayersThisQuota,
                ES3.Load(MoonPlayerCountKey, saveName, _recordPlayersThisMoon));
        }

        private static void ApplyQuotaToTime(TimeOfDay time, int quota)
        {
            // Backstop: every caller is already server-gated, so a client reaching here
            // would be a regression. Refuse rather than diverge.
            if (time == null || !IsServerRuntime())
                return;

            time.profitQuota = ClampQuotaTarget(quota, "quota overhaul rules");
            time.UpdateProfitQuotaCurrentTime();

            // Vanilla only ships the quota to the player who is joining
            // (OnPlayerConnectedClientRpc), so in-lobby clients would keep a stale first
            // quota when the host rescales it for a later join, and would never see a
            // post-wipe reset. Mirror every host-side rewrite instead.
            QuotaSyncNetwork.BroadcastQuotaValue(time.profitQuota, "quota overhaul rules");
        }

        private static void SaveAllQuotaState(TimeOfDay time)
        {
            SaveBaseQuota(_baseQuota);
            SaveInt(FirstQuotaPlayerCountKey, _recordPlayersThisQuota);
            SaveInt(MoonPlayerCountKey, _recordPlayersThisMoon);
            if (time != null)
                SaveInt("ProfitQuota", time.profitQuota);
        }

        private static void SaveBaseQuota(int quota)
        {
            SaveInt(BaseQuotaKey, quota);
        }

        private static void SaveInt(string key, int value)
        {
            // The single cheapest stop to all save pollution (#424): on a client
            // GetSaveName() resolves to that player's own solo save slot, and the vanilla
            // "ProfitQuota" key written below is read straight back by StartOfRound when
            // they next load that solo game.
            if (!IsServerRuntime())
                return;

            string saveName = GetSaveName();
            if (string.IsNullOrWhiteSpace(saveName))
                return;

            ES3.Save(key, value, saveName);
        }

        private static string GetSaveName()
        {
            return GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.currentSaveFileName
                : string.Empty;
        }

        private static int GetCurrentTotalPlayers(StartOfRound start)
        {
            return start != null ? Mathf.Max(1, start.connectedPlayersAmount + 1) : 1;
        }

        private static bool WeatherMatches(string actualName, string configuredName)
        {
            string actual = NormalizeWeather(actualName);
            string configured = NormalizeWeather(configuredName);
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(configured))
                return false;

            return actual == configured
                || actual.Contains(configured)
                || configured.Contains(actual)
                || actual == "eclipsed" && configured == "eclipse";
        }

        private static string NormalizeWeather(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value.Trim()
                .ToLowerInvariant()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);
        }
    }
}
