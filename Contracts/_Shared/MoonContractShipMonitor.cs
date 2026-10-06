using System;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Y4NGZCompany.Contracts._Shared
{
    internal static class MoonContractShipMonitor
    {
        private const string LabelColor = "#00D7E6";
        private const string ValueColor = "#00FF0D";
        private const float RefreshIntervalSeconds = 0.15f;
        private static float _nextRefreshAt;

        // #716/B14: the five lines only change when one of the raw inputs behind them
        // changes, so the 0.15s tick compares a cheap key built from those inputs and
        // skips the Split/Join/Replace formatting and the ten TMP property writes
        // entirely on every other tick (including the whole time the ship is in orbit).
        private static TextMeshProUGUI _styledText;
        private static string _lastPlanetName;
        private static MoonContractType _lastContract = (MoonContractType)(-1);
        private static char _lastRiskTier;
        private static LevelWeatherType _lastWeather = (LevelWeatherType)(-1);
        private static string _lastText;

        internal static void Tick()
        {
            if (Time.unscaledTime < _nextRefreshAt)
                return;

            _nextRefreshAt = Time.unscaledTime + RefreshIntervalSeconds;
            RefreshCurrent();
        }

        internal static void RefreshCurrent()
        {
            StartOfRound start = StartOfRound.Instance;
            TextMeshProUGUI text = start != null ? start.screenLevelDescription : null;
            SelectableLevel level = start != null ? start.currentLevel : null;
            if (text == null || level == null)
                return;

            MoonContractType contract = MoonContractState.GetCurrentContract();
            char riskTier = MoonContractState.GetCurrentRiskTier();
            LevelWeatherType weatherType = level.currentWeather;

            // Allocation-free change gate over the raw inputs, checked before any
            // formatting. The vanilla route display can still overwrite the label, so the
            // gate also confirms our last rendered string is still the one on screen -
            // one ordinal string compare instead of the whole formatting pass.
            if (ReferenceEquals(text, _styledText)
                && contract == _lastContract
                && riskTier == _lastRiskTier
                && weatherType == _lastWeather
                && string.Equals(level.PlanetName, _lastPlanetName, StringComparison.Ordinal)
                && string.Equals(text.text, _lastText, StringComparison.Ordinal))
                return;

            string orbiting = CleanValue(level.PlanetName, "Unknown Moon");
            string contractName = CleanValue(MoonContractCatalog.Get(contract).DisplayName, "Salvage");
            // #716/E12: an empty riskLevel makes GetCurrentRiskTier return '\0', which
            // rendered as a stray control character. Route it through CleanValue so it
            // falls back to a dash instead.
            string risk = CleanValue(riskTier == '\0' ? string.Empty : riskTier.ToString(), "-");
            string weather = FormatIdentifier(weatherType.ToString());
            string objective = GetObjectiveSummary(contract);
            string desiredText =
                Line("ORBITING", orbiting) + "\n" +
                Line("CONTRACT", contractName) + "\n" +
                Line("RISK", risk) + "\n" +
                Line("WEATHER", weather) + "\n" +
                Line("OBJECTIVE", objective);

            _styledText = text;
            _lastPlanetName = level.PlanetName;
            _lastContract = contract;
            _lastRiskTier = riskTier;
            _lastWeather = weatherType;
            _lastText = desiredText;

            text.richText = true;
            text.enableWordWrapping = true;
            text.enableAutoSizing = false;
            text.fontSize = 26f;
            text.fontStyle = FontStyles.Normal;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.overflowMode = TextOverflowModes.Overflow;
            text.lineSpacing = 1f;
            text.margin = new Vector4(12f, 10f, 12f, 10f);
            text.color = Color.white;
            if (!string.Equals(text.text, desiredText, StringComparison.Ordinal))
                text.text = desiredText;
        }

        private static string Line(string label, string value)
        {
            return $"<color={LabelColor}>{label}:</color> <color={ValueColor}>{EscapeRichText(value)}</color>";
        }

        private static string GetObjectiveSummary(MoonContractType contract)
        {
            return CleanValue(MoonContractCatalog.Get(contract).Objective, "Recover valuable scrap");
        }

        private static string CleanValue(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            string[] words = value.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return words.Length > 0 ? string.Join(" ", words) : fallback;
        }

        private static string FormatIdentifier(string value)
        {
            string cleaned = CleanValue(value, "None");
            if (string.Equals(cleaned, "None", StringComparison.OrdinalIgnoreCase))
                return "None";

            StringBuilder result = new StringBuilder(cleaned.Length + 4);
            for (int i = 0; i < cleaned.Length; i++)
            {
                char current = cleaned[i];
                if (i > 0 && char.IsUpper(current) && char.IsLower(cleaned[i - 1]))
                    result.Append(' ');
                result.Append(current);
            }
            return result.ToString();
        }

        private static string EscapeRichText(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }
    }

    [HarmonyPatch]
    internal static class MoonContractShipMonitorPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StartOfRound), "SetMapScreenInfoToCurrentLevel")]
        private static void RefreshAfterRouteDisplayUpdate()
        {
            MoonContractShipMonitor.RefreshCurrent();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StartOfRound), "SetPlanetsWeather")]
        private static void RefreshAfterWeatherUpdate()
        {
            MoonContractShipMonitor.RefreshCurrent();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StartOfRound), "LateUpdate")]
        private static void KeepContractDisplayAuthoritative()
        {
            MoonContractShipMonitor.Tick();
        }
    }
}
