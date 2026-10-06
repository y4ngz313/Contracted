using System;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Core.Compatibility
{
    /// <summary>
    /// #727. Mid-round weather change alerts for the contract card's alert belt.
    ///
    /// Detection is mod-agnostic on purpose: every weather mod that changes conditions
    /// mid-round (WeatherRegistry's own controller, WeatherTweaks on top of it) writes the
    /// new value into vanilla's <c>SelectableLevel.currentWeather</c> and
    /// <c>TimeOfDay.currentLevelWeather</c>, because that is what drives vanilla's weather
    /// effects. Sampling those two fields on the card's existing quarter-second poll sees the
    /// change without binding to any mod's event, so nothing here can fail to load the way
    /// DawnLib's WeatherRegistry compat does when MrovLib is missing.
    ///
    /// WeatherRegistry is used only to name the weather: vanilla's enum covers the base
    /// eight, and a registered custom weather is just an integer past them. The lookup is
    /// reflection by name, inside try/catch, so a missing or reshaped API degrades to the
    /// enum name and never throws.
    /// </summary>
    internal static class WeatherChangeAlerts
    {
        internal const string WeatherRegistryGuid = "mrov.WeatherRegistry";
        private const float AlertSeconds = 18f;

        private static readonly bool _registryLoaded = Chainloader.PluginInfos.ContainsKey(WeatherRegistryGuid);
        private static bool _registryResolved;
        private static MethodInfo _getWeatherMethod;
        private static PropertyInfo _weatherNameProperty;

        private static bool _baselineValid;
        private static LevelWeatherType _baselineLevelWeather;
        private static LevelWeatherType _baselineTimeOfDayWeather;

        internal static bool IsWeatherRegistryLoaded => _registryLoaded;

        /// <summary>
        /// Called from the contract card's live poll. Establishes a baseline on the first
        /// landed sample and posts one belt segment per change after that. Nothing is posted
        /// for the landing itself: the moon's forecast is already on the terminal and the
        /// ship monitor, and the belt is for what changes under the crew's feet.
        /// </summary>
        internal static void Sample()
        {
            StartOfRound round = StartOfRound.Instance;
            TimeOfDay day = TimeOfDay.Instance;
            if (round == null || round.currentLevel == null || !round.shipHasLanded || round.inShipPhase)
            {
                _baselineValid = false;
                return;
            }

            LevelWeatherType levelWeather = round.currentLevel.currentWeather;
            LevelWeatherType dayWeather = day != null ? day.currentLevelWeather : levelWeather;
            if (!_baselineValid)
            {
                _baselineValid = true;
                _baselineLevelWeather = levelWeather;
                _baselineTimeOfDayWeather = dayWeather;
                return;
            }

            if (levelWeather == _baselineLevelWeather && dayWeather == _baselineTimeOfDayWeather)
                return;

            // Prefer the level field: WeatherRegistry writes it first and it is what the
            // terminal reads. The TimeOfDay copy is the fallback signal for mods that only
            // touch the live effect.
            LevelWeatherType changed = levelWeather != _baselineLevelWeather ? levelWeather : dayWeather;
            _baselineLevelWeather = levelWeather;
            _baselineTimeOfDayWeather = dayWeather;

            string name = ResolveWeatherName(changed);
            ContractHudAlerts.Post($"WEATHER · {name}", AlertSeconds);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Weather] Mid-round weather change: {name} ({(int)changed}).");
        }

        internal static void Reset()
        {
            _baselineValid = false;
        }

        private static string ResolveWeatherName(LevelWeatherType weather)
        {
            string registryName = TryGetRegistryName(weather);
            if (!string.IsNullOrWhiteSpace(registryName))
                return registryName.Trim().ToUpperInvariant();

            if (Enum.IsDefined(typeof(LevelWeatherType), weather))
                return weather == LevelWeatherType.None ? "CLEAR" : weather.ToString().ToUpperInvariant();

            return $"CODE {(int)weather}";
        }

        private static string TryGetRegistryName(LevelWeatherType weather)
        {
            if (!_registryLoaded)
                return null;

            ResolveRegistry();
            if (_getWeatherMethod == null)
                return null;

            try
            {
                object registered = _getWeatherMethod.Invoke(null, new object[] { weather });
                if (registered == null)
                    return null;

                if (_weatherNameProperty == null)
                    _weatherNameProperty = registered.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                return _weatherNameProperty?.GetValue(registered) as string;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Weather] WeatherRegistry name lookup failed, using enum names: {e.Message}");
                _getWeatherMethod = null;
                return null;
            }
        }

        private static void ResolveRegistry()
        {
            if (_registryResolved)
                return;

            _registryResolved = true;
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(WeatherRegistryGuid, out var info) || info?.Instance == null)
                    return;

                Assembly assembly = info.Instance.GetType().Assembly;
                Type manager = assembly.GetType("WeatherRegistry.WeatherManager", throwOnError: false);
                if (manager == null)
                    return;

                foreach (MethodInfo method in manager.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "GetWeather")
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(LevelWeatherType))
                    {
                        _getWeatherMethod = method;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Weather] WeatherRegistry resolve failed, using enum names: {e.Message}");
                _getWeatherMethod = null;
            }
        }
    }
}
