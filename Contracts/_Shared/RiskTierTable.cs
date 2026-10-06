using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using Y4NGZCompany.Core;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// One config entry holding a per-risk tier table such as <c>C=1, B=2, A=3, S=4</c>.
    /// A tier missing from the player's value falls back to the C entry, then to the
    /// authored default for that tier; parsed values are clamped to the given range.
    /// Parsing is cached per raw string so per-frame reads stay cheap.
    /// </summary>
    internal sealed class RiskTierTable<T> where T : struct
    {
        private readonly ConfigEntry<string> _entry;
        private readonly Dictionary<char, T> _defaults;
        private readonly T _min, _max;
        private readonly Func<string, Dictionary<char, T>> _parse;
        private readonly Func<T, T, T, T> _clamp;
        private string _cachedRaw;
        private Dictionary<char, T> _cached;

        internal RiskTierTable(
            ConfigFile config,
            string section,
            string key,
            string defaultTable,
            T min,
            T max,
            string description,
            Func<string, Dictionary<char, T>> parse,
            Func<T, T, T, T> clamp)
        {
            _parse = parse;
            _clamp = clamp;
            _min = min;
            _max = max;
            _defaults = parse(defaultTable);
            _entry = config.Bind(section, key, defaultTable,
                description + " Moon risk tiers, format: " + defaultTable + ".");
        }

        internal T For(char risk)
        {
            string raw = _entry.Value ?? string.Empty;
            if (!ReferenceEquals(raw, _cachedRaw))
            {
                _cached = _parse(raw);
                _cachedRaw = raw;
            }

            return _clamp(Resolve(_cached, risk, () => Resolve(_defaults, risk, () => _min)), _min, _max);
        }

        internal DerivedSetting<T> View(char risk) => new DerivedSetting<T>(() => For(risk));

        private static T Resolve(Dictionary<char, T> map, char risk, Func<T> fallback)
        {
            char tier = char.ToUpperInvariant(risk);
            if (map != null)
            {
                if (map.TryGetValue(tier, out T value))
                    return value;
                // Historic behaviour: unknown or unlisted tiers (D included) used the
                // Risk C section, so an absent tier keeps reading the C column.
                if (map.TryGetValue('C', out T cValue))
                    return cValue;
            }

            return fallback();
        }
    }

    internal static class RiskTierTable
    {
        internal static RiskTierTable<int> BindInt(
            ConfigFile config, string section, string key, string defaultTable,
            int min, int max, string description)
        {
            return new RiskTierTable<int>(config, section, key, defaultTable, min, max, description,
                ContractConfigRules.ParseTierIntMap,
                (v, lo, hi) => Mathf.Clamp(v, lo, hi));
        }

        internal static RiskTierTable<float> BindFloat(
            ConfigFile config, string section, string key, string defaultTable,
            float min, float max, string description)
        {
            return new RiskTierTable<float>(config, section, key, defaultTable, min, max, description,
                ContractConfigRules.ParseTierFloatMap,
                (v, lo, hi) => Mathf.Clamp(v, lo, hi));
        }
    }
}
